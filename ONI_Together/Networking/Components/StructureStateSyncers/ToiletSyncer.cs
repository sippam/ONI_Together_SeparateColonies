using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using ONI_Together.Misc;
using ONI_Together.Networking.Packets.World;
using UnityEngine;
using static STRINGS.UI.METERS;

namespace ONI_Together.Networking.Components.StructureStateSyncers
{
    public class ToiletSyncer : StructureSyncerBase
    {
        private FlushToilet flushToilet;
        private Toilet outhouseToilet;
        private Storage storage;
        private ConduitConsumer conduitConsumer;
        private KPrefabID prefabID;

        // Outhouse, client side: the host's view of the state machine. The client's own storage is rebuilt from
        // every state packet, which used to fire OnStorageChange transitions (needsdirt -> ready -> full -> ...)
        // and made the "needs emptying" status blink. Toilet.StatesInstance predicates answer from these instead.
        public bool HasHostState;
        public bool HostNeedsDirt;
        public bool HostSandRemoved = true;
        public string HostStateName;
        // Lavatory, client side
        public bool HostWaterFull;
        public bool HostContaminated;

        private StateMachine.Instance Smi()
        {
            if (outhouseToilet != null) return outhouseToilet.smi;
            if (flushToilet != null) return flushToilet.GetSMI<FlushToilet.SMInstance>();
            return null;
        }

        protected override void Initialize()
        {
            flushToilet = GetComponent<FlushToilet>();
            outhouseToilet = GetComponent<Toilet>();
            storage = GetComponent<Storage>();
            conduitConsumer = GetComponent<ConduitConsumer>();
            prefabID = GetComponent<KPrefabID>();
        }

        protected override void SampleState(out Variant value, out bool active, out Dictionary<string, Variant> optionalValues)
        {
            active = false;
            optionalValues = new Dictionary<string, Variant>();
            BuildingUtils.EncodeStorageContents(storage, optionalValues);
            optionalValues["is_operational"] = operational?.IsOperational ?? true;
            if (flushToilet != null)
            {
                value = storage?.MassStored() ?? 0f;
                GetTotalWater(out float totalWater, out float totalWaste, out float totalGunk);
                optionalValues["total_water"] = totalWater;
                optionalValues["total_waste"] = totalWaste;
                optionalValues["total_gunk"] = totalGunk;
                var fsmi = flushToilet.GetSMI<FlushToilet.SMInstance>();
                if (fsmi != null)
                {
                    optionalValues["sm_state"] = fsmi.GetCurrentState()?.name ?? "";
                    optionalValues["contaminated"] = fsmi.HasContaminatedMass();
                }
            }
            else if (outhouseToilet != null)
            {
                value = outhouseToilet.FlushesUsed;
                var smi = outhouseToilet.smi;
                if (smi != null)
                {
                    optionalValues["sm_state"] = smi.GetCurrentState()?.name ?? "";
                    optionalValues["needs_dirt"] = smi.RequiresDirtDelivery();
                    optionalValues["sand_removed"] = smi.IsToxicSandRemoved();
                }
            }
            else
            {
                value = 0f;
            }
        }

        protected override void ApplyState(StructureStatePacket packet)
        {
            if (storage == null) return;
            if (flushToilet != null)
            {
                var opt = packet.OptionalValues;
                if (opt.TryGetValue("total_water", out var tw)) { HostWaterFull = tw.Float >= flushToilet.massConsumedPerUse; HasHostState = true; }
                if (opt.TryGetValue("contaminated", out var ct)) HostContaminated = ct.Boolean;
                if (opt.TryGetValue("sm_state", out var st)) HostStateName = st.String;
            }
            if (outhouseToilet != null)
            {
                // set the host's answers first so the storage rebuild below can't flip the state machine
                var opt = packet.OptionalValues;
                if (opt.TryGetValue("needs_dirt", out var nd)) { HostNeedsDirt = nd.Boolean; HasHostState = true; }
                if (opt.TryGetValue("sand_removed", out var sr)) HostSandRemoved = sr.Boolean;
                if (opt.TryGetValue("sm_state", out var st)) HostStateName = st.String;
            }
            BuildingUtils.RebuildStorageFromData(storage, packet.OptionalValues);
            SyncToilet(packet);
            MatchHostState();

            if (packet.OptionalValues.TryGetValue("is_operational", out var isOp))
            {
                if (isOp.Boolean)
                    prefabID.AddTag(GameTags.Operational);
                else
                    prefabID.RemoveTag(GameTags.Operational);
            }
        }

        private void SyncToilet(StructureStatePacket packet)
        {
            if (flushToilet != null)
                SyncFlushToilet(packet);
            else if (outhouseToilet != null)
                SyncOuthouse(packet);
        }

        private void GetTotalWater(out float totalWater, out float totalWaste, out float totalGunk)
        {
            totalWater = 0f;
            totalWaste = 0f; 
            totalGunk = 0f;

            foreach (var item in storage.items)
            {
                if (item == null) continue;
                var pe = item.GetComponent<PrimaryElement>();
                if (pe == null) continue;
                if (pe.ElementID == SimHashes.Water) totalWater += pe.Mass;
                else if (pe.ElementID == SimHashes.DirtyWater) totalWaste += pe.Mass;
                else if (pe.ElementID == GunkMonitor.GunkElement) totalGunk += pe.Mass;
            }
        }

        private void SyncFlushToilet(StructureStatePacket packet)
        {
            var opt = packet.OptionalValues;
            float totalWater = 0f, totalWaste = 0f, totalGunk = 0f;
            if (opt.TryGetValue("total_water", out var w)) totalWater = w.Float;
            if (opt.TryGetValue("total_waste", out var wa)) totalWaste = wa.Float;
            if (opt.TryGetValue("total_gunk", out var g)) totalGunk = g.Float;
            bool full = totalWater >= flushToilet.massConsumedPerUse;

            if (conduitConsumer != null)
                conduitConsumer.enabled = !full;

            float fillPct = Mathf.Clamp01(totalWater / flushToilet.massConsumedPerUse);
            float wastePct = Mathf.Clamp01(totalWaste / flushToilet.massEmittedPerUse);
            float gunkPct = Mathf.Clamp01(totalGunk / flushToilet.massEmittedPerUse);

            flushToilet.fillMeter?.SetPositionPercent(fillPct);
            flushToilet.contaminationMeter?.SetPositionPercent(wastePct);
            flushToilet.gunkMeter?.SetPositionPercent(gunkPct);
        }

        private void SyncOuthouse(StructureStatePacket packet)
        {
            outhouseToilet.FlushesUsed = packet.Value.Int;
            outhouseToilet.meter?.SetPositionPercent(Mathf.Clamp01((float)outhouseToilet.FlushesUsed / outhouseToilet.maxFlushes));
        }

        /// <summary>Client: follow the host's outhouse state instead of re-deriving it from the rebuilt storage.</summary>
        private void MatchHostState()
        {
            try
            {
                var smi = Smi();
                if (smi == null || string.IsNullOrEmpty(HostStateName)) return;
                var cur = smi.GetCurrentState()?.name;
                if (cur == HostStateName) return;
                // only the top-level state matters (ready / filling / clogged / backedup...); sub-states like
                // ready.inuse follow the host's animations on their own and fighting over them causes blinking
                if (Top(cur) == Top(HostStateName)) return;
                // "full"/"earlyclean" only play the clog animation and then move on to the waiting state by themselves
                if ((HostStateName.EndsWith("full") && cur != null && cur.EndsWith("fullWaitingForClean")) ||
                    (HostStateName.EndsWith("earlyclean") && cur != null && cur.EndsWith("earlyWaitingForClean")))
                    return;
                smi.GoTo(HostStateName);
            }
            catch (Exception ex)
            {
                ONI_Together.DebugTools.DebugConsole.LogWarning($"[ToiletSyncer] state sync failed: {ex.Message}");
            }
        }

        private static string Top(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var parts = name.Split('.');
            return parts.Length > 1 ? parts[1] : parts[0];
        }

        protected override bool ShouldForceSync()
        {
            return false;
        }
    }

    /// <summary>Client: outhouse state predicates come from the host (see ToiletSyncer).</summary>
    [HarmonyPatch(typeof(Toilet.StatesInstance), nameof(Toilet.StatesInstance.RequiresDirtDelivery))]
    public static class Toilet_RequiresDirtDelivery_ClientPatch
    {
        public static bool Prefix(Toilet.StatesInstance __instance, ref bool __result)
        {
            if (!MultiplayerSession.IsClient || !MultiplayerSession.InSession) return true;
            var sync = __instance?.master?.GetComponent<ToiletSyncer>();
            if (sync == null || !sync.HasHostState) return true;
            __result = sync.HostNeedsDirt;
            return false;
        }
    }

    [HarmonyPatch(typeof(Toilet.StatesInstance), nameof(Toilet.StatesInstance.IsToxicSandRemoved))]
    public static class Toilet_IsToxicSandRemoved_ClientPatch
    {
        public static bool Prefix(Toilet.StatesInstance __instance, ref bool __result)
        {
            if (!MultiplayerSession.IsClient || !MultiplayerSession.InSession) return true;
            var sync = __instance?.master?.GetComponent<ToiletSyncer>();
            if (sync == null || !sync.HasHostState) return true;
            __result = sync.HostSandRemoved;
            return false;
        }
    }

    /// <summary>Client: lavatory fullness / contamination come from the host (see ToiletSyncer).</summary>
    [HarmonyPatch(typeof(FlushToilet.SMInstance), nameof(FlushToilet.SMInstance.UpdateFullnessState))]
    public static class FlushToilet_UpdateFullnessState_ClientPatch
    {
        public static bool Prefix(FlushToilet.SMInstance __instance, ref bool __result)
        {
            if (!MultiplayerSession.IsClient || !MultiplayerSession.InSession) return true;
            var sync = __instance?.master?.GetComponent<ToiletSyncer>();
            if (sync == null || !sync.HasHostState) return true;
            __result = sync.HostWaterFull;
            return false;
        }
    }

    [HarmonyPatch(typeof(FlushToilet.SMInstance), nameof(FlushToilet.SMInstance.HasContaminatedMass))]
    public static class FlushToilet_HasContaminatedMass_ClientPatch
    {
        public static bool Prefix(FlushToilet.SMInstance __instance, ref bool __result)
        {
            if (!MultiplayerSession.IsClient || !MultiplayerSession.InSession) return true;
            var sync = __instance?.master?.GetComponent<ToiletSyncer>();
            if (sync == null || !sync.HasHostState) return true;
            __result = sync.HostContaminated;
            return false;
        }
    }
}
