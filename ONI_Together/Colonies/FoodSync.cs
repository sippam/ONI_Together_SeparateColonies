using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// Host-authoritative colony food calories.
    /// Clients don't run dupe AI, so eating, partial meals, cooking and spoilage leave their copy of the food
    /// items out of step and the top-left kcal meter / Food diagnostic / kcal graph showed nonsense.
    /// The host sends the per-world totals that RationTracker.CountAmount computes every few seconds and
    /// clients answer CountAmount from that (every food UI and the KCal tracker go through it).
    /// Dupe stomach calories are already synced by ONI Together's VitalStatsPacket.
    /// </summary>
    public static class FoodSync
    {
        private const float SendInterval = 3f;
        private const float StaleAfter = 20f;

        private class WorldFood
        {
            public float Kcal;
            public float Units;
            public Dictionary<string, float> ByFood = new Dictionary<string, float>();
            public float ReceivedAt;
        }

        private static readonly Dictionary<int, WorldFood> _client = new Dictionary<int, WorldFood>();
        private static float _timer;

        public static void Reset() { _client.Clear(); _timer = 0f; }

        public static void HostTick()
        {
            if (!MultiplayerSession.IsHost || !MultiplayerSession.InSession) return;
            if (!ColonyNet.HostHasClients) return;
            _timer += Time.unscaledDeltaTime;
            if (_timer < SendInterval) return;
            _timer = 0f;
            try
            {
                var tracker = WorldResourceAmountTracker<RationTracker>.Get();
                var cm = ClusterManager.Instance;
                if (tracker == null || cm == null) return;
                var pkt = new FoodTotalsPacket();
                foreach (var w in cm.WorldContainers)
                {
                    if (w == null || !w.IsDiscovered || w.worldInventory == null) continue;
                    var by = new Dictionary<string, float>();
                    float kcal = tracker.CountAmount(by, out float units, w.worldInventory, true);
                    if (kcal <= 0f && units <= 0f && w.id != cm.activeWorldId && global::Components.LiveMinionIdentities.GetWorldItems(w.id).Count == 0) continue;
                    pkt.Worlds.Add(new FoodTotalsPacket.Entry { WorldId = w.id, Kcal = kcal, Units = units, ByFood = by });
                }
                if (pkt.Worlds.Count > 0) PacketSender.SendToAllClients(pkt, PacketSendMode.Unreliable);
            }
            catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] food sync send: {ex.Message}"); }
        }

        internal static void OnTotals(FoodTotalsPacket p)
        {
            if (MultiplayerSession.IsHost) return;
            float now = Time.unscaledTime;
            foreach (var e in p.Worlds)
                _client[e.WorldId] = new WorldFood { Kcal = e.Kcal, Units = e.Units, ByFood = e.ByFood, ReceivedAt = now };
        }

        private static bool TryGet(WorldInventory inv, out WorldFood f)
        {
            f = null;
            if (inv == null || _client.Count == 0) return false;
            var wc = inv.GetComponent<WorldContainer>();
            if (wc == null || !_client.TryGetValue(wc.id, out f)) return false;
            return Time.unscaledTime - f.ReceivedAt < StaleAfter;
        }

        [HarmonyPatch(typeof(WorldResourceAmountTracker<RationTracker>), nameof(WorldResourceAmountTracker<RationTracker>.CountAmount),
            new[] { typeof(Dictionary<string, float>), typeof(float), typeof(WorldInventory), typeof(bool) },
            new[] { ArgumentType.Normal, ArgumentType.Out, ArgumentType.Normal, ArgumentType.Normal })]
        public static class RationTracker_CountAmount
        {
            public static bool Prefix(object __instance, Dictionary<string, float> unitCountByID, ref float totalUnitsFound, WorldInventory inventory, ref float __result)
            {
                // the generic body is shared with other trackers (e.g. electrobanks): only touch food
                if (!(__instance is RationTracker)) return true;
                if (!MultiplayerSession.IsClient || !MultiplayerSession.InSession) return true;
                if (!TryGet(inventory, out var f)) return true;
                if (unitCountByID != null)
                    foreach (var kv in f.ByFood)
                        unitCountByID[kv.Key] = (unitCountByID.TryGetValue(kv.Key, out float v) ? v : 0f) + kv.Value;
                totalUnitsFound = f.Units;
                __result = f.Kcal;
                return false;
            }
        }
    }

    /// <summary>Host -> clients: food calories per world (as RationTracker counts them on the host).</summary>
    public class FoodTotalsPacket : IPacket
    {
        public struct Entry
        {
            public int WorldId;
            public float Kcal, Units;
            public Dictionary<string, float> ByFood;
        }

        public List<Entry> Worlds = new List<Entry>();

        public void Serialize(BinaryWriter w)
        {
            w.Write(Worlds.Count);
            foreach (var e in Worlds)
            {
                w.Write(e.WorldId); w.Write(e.Kcal); w.Write(e.Units);
                var by = e.ByFood ?? new Dictionary<string, float>();
                w.Write(by.Count);
                foreach (var kv in by) { w.Write(kv.Key ?? ""); w.Write(kv.Value); }
            }
        }

        public void Deserialize(BinaryReader r)
        {
            int n = Math.Min(r.ReadInt32(), 256);
            Worlds = new List<Entry>(Math.Max(0, n));
            for (int i = 0; i < n; i++)
            {
                var e = new Entry { WorldId = r.ReadInt32(), Kcal = r.ReadSingle(), Units = r.ReadSingle(), ByFood = new Dictionary<string, float>() };
                int m = Math.Min(r.ReadInt32(), 512);
                for (int j = 0; j < m; j++) { string k = r.ReadString(); e.ByFood[k] = r.ReadSingle(); }
                Worlds.Add(e);
            }
        }

        public void OnDispatched() => FoodSync.OnTotals(this);
    }
}
