using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// Harmony patches for "separate colonies on one asteroid".
    /// Every patch is a no-op unless ColonyRegistry.Active (mode on + at least one colony).
    ///
    /// Applied by ONI Together's UserMod2.PatchAll (every [HarmonyPatch] class in the assembly).
    /// Targets were verified by reflection against Assembly-CSharp build 744825 — a missing target would
    /// abort PatchAll, so re-verify after every game update (tools/Verify).
    /// </summary>
    public static class ColonyPatches
    {
        private static bool On => ColonyRegistry.Active;
        /// <summary>Errand/fetch separation (ColonyRules.SeparateErrands).</summary>
        private static bool Errands => ColonyRegistry.Active && ColonyRules.SeparateErrands && !ColonyRules.SharedResources;

        /// <summary>Shared resources: finished buildings (machines, storage, beds, tables...) belong to everyone's work.</summary>
        private static bool IsSharedTarget(GameObject go) => ColonyRules.SharedResources && go != null && go.GetComponent<BuildingComplete>() != null;

        // =====================================================================
        // Save data
        // =====================================================================

        [HarmonyPatch(typeof(SaveGame), "OnPrefabInit")]
        public static class SaveGame_OnPrefabInit
        {
            public static void Postfix(SaveGame __instance)
            {
                __instance.gameObject.AddOrGet<ColonySaveData>();
            }
        }

        /// <summary>Rebuild the Storage->colony index as objects spawn (after load, the map only has InstanceIDs).</summary>
        [HarmonyPatch(typeof(Storage), "OnSpawn")]
        public static class Storage_OnSpawn
        {
            public static void Postfix(Storage __instance)
            {
                if (!On) return;
                ColonyRegistry.RefreshStorages(__instance.gameObject);
            }
        }

        // =====================================================================
        // Chores: dupes only work for their own colony
        // =====================================================================

        public static readonly Chore.Precondition SameColony = new Chore.Precondition
        {
            id = "ONIT_Colonies_SameColony",
            description = "Belongs to another colony",
            canExecuteOnAnyThread = false, // uses GetComponent
            fn = (ref Chore.Precondition.Context context, object data) =>
            {
                if (!ColonyRegistry.Active || !ColonyRules.SeparateErrands) return true;
                // "help when idle": foreign errands stay available, ChoreConsumer_ChooseChore ranks them last
                if (ColonyRules.HelpOtherColonies) return true;
                int worker = ColonyRegistry.GetOwner(context.consumerState.prefabid);
                if (worker == ColonyRegistry.NoColony) return true;
                var chore = context.chore;
                if (chore == null) return true;
                GameObject target = chore.gameObject;
                if (target == null) return true;
                if (IsSharedTarget(target)) return true;
                return ColonyGuard.CanWork(worker, ColonyRegistry.GetOwner(target));
            }
        };

        [HarmonyPatch]
        public static class StandardChoreBase_Ctor
        {
            public static MethodBase TargetMethod()
            {
                foreach (var c in AccessTools.GetDeclaredConstructors(typeof(StandardChoreBase)))
                    if (c.GetParameters().Length >= 10) return c;
                throw new Exception("StandardChoreBase constructor not found");
            }

            public static void Postfix(StandardChoreBase __instance)
            {
                // Always added (cheap when mode is off) so chores created before the mode switch are covered.
                __instance.AddPrecondition(SameColony);
            }
        }

        /// <summary>
        /// Work order ("whose work first"): before the dupe picks from its sorted candidate list (best = last),
        /// regroup errands by colony following the dupe's colony WorkOrder (e.g. Host > P2 > P3), keeping vanilla
        /// order inside each group. Unowned/shared work counts as the dupe's own colony. Tiers are never mixed:
        /// idle &lt; normal errands &lt; personal needs &lt; emergency (!!) errands &lt; compulsory, so eating/sleeping
        /// always beats other colonies' errands. Host only (clients don't run dupe AI in ONI Together).
        /// </summary>
        [HarmonyPatch(typeof(ChoreConsumer), "ChooseChore")]
        public static class ChoreConsumer_ChooseChore
        {
            [ThreadStatic] private static List<Chore.Precondition.Context>[] _buckets;

            public static void Prefix(ChoreConsumer __instance, List<Chore.Precondition.Context> succeeded_contexts)
            {
                if (!ColonyRegistry.Active || !ColonyRules.SeparateErrands || !ColonyRules.HelpOtherColonies) return;
                if (succeeded_contexts == null || succeeded_contexts.Count < 2 || __instance == null) return;
                int worker = ColonyRegistry.GetOwner(__instance.gameObject);
                if (worker == ColonyRegistry.NoColony) return;
                var ranks = ColonyWorkOrder.Ranks(worker);
                if (ranks == null || !ranks.TryGetValue(worker, out int ownSlot)) return;

                int n = ranks.Count;            // colony slots; slot n = unknown colony (after all listed)
                int span = n + 1;
                // bucket layout (ascending preference):
                // 0 idle | 1..span normal errands (worst slot first) | span+1 personal needs | span+2.. emergency | last compulsory
                int total = 2 * span + 3;
                if (_buckets == null || _buckets.Length < total)
                {
                    _buckets = new List<Chore.Precondition.Context>[Math.Max(total, 16)];
                    for (int i = 0; i < _buckets.Length; i++) _buckets[i] = new List<Chore.Precondition.Context>();
                }
                var idleType = Db.Get().ChoreTypes.Idle;
                int firstBucket = -1;
                bool mixed = false;
                foreach (var ctx in succeeded_contexts)
                {
                    var chore = ctx.chore;
                    var pc = ctx.masterPriority.priority_class;
                    int b;
                    if (pc == PriorityScreen.PriorityClass.idle || (chore != null && chore.choreType == idleType)) b = 0;
                    else if (pc == PriorityScreen.PriorityClass.personalNeeds) b = span + 1;
                    else if (pc == PriorityScreen.PriorityClass.compulsory) b = total - 1;
                    else
                    {
                        int slot = ownSlot;
                        var go = chore?.gameObject;
                        int owner = go != null ? ColonyRegistry.GetOwner(go) : ColonyRegistry.NoColony;
                        if (owner != ColonyRegistry.NoColony && owner != worker && !IsSharedTarget(go))
                            slot = ranks.TryGetValue(owner, out int s) ? s : n;
                        int inTier = n - slot; // first slot -> highest
                        b = (pc == PriorityScreen.PriorityClass.topPriority ? span + 2 : 1) + inTier;
                    }
                    if (firstBucket < 0) firstBucket = b; else if (b != firstBucket) mixed = true;
                    _buckets[b].Add(ctx);
                }
                if (mixed)
                {
                    succeeded_contexts.Clear();
                    for (int i = 0; i < total; i++) succeeded_contexts.AddRange(_buckets[i]);
                }
                for (int i = 0; i < total; i++) _buckets[i].Clear();
            }
        }

        // ---- fetching: never take items out of another colony's storage ----

        private static int SourceOwner(Pickupable p)
        {
            int owner = ColonyRegistry.GetOwner(p.storage);
            if (owner != ColonyRegistry.NoColony) return owner;
            return p.KPrefabID != null ? ColonyRegistry.GetOwner(p.KPrefabID.InstanceID) : ColonyRegistry.NoColony;
        }

        /// <summary>Runs inside FetchManager worker threads — only thread-safe lookups here.</summary>
        [HarmonyPatch(typeof(Pickupable), nameof(Pickupable.CouldBePickedUpByMinion), new Type[] { typeof(int) })]
        public static class Pickupable_CouldBePickedUpByMinion
        {
            public static void Postfix(Pickupable __instance, int carrierID, ref bool __result)
            {
                if (!__result || !Errands) return;
                int carrier = ColonyRegistry.GetOwner(carrierID);
                if (carrier == ColonyRegistry.NoColony) return;
                int src = SourceOwner(__instance);
                if (src != ColonyRegistry.NoColony && src != carrier) __result = false;
            }
        }

        [HarmonyPatch(typeof(Pickupable), nameof(Pickupable.CouldBePickedUpByTransferArm), new Type[] { typeof(int) })]
        public static class Pickupable_CouldBePickedUpByTransferArm
        {
            public static void Postfix(Pickupable __instance, int carrierID, ref bool __result)
            {
                if (!__result || !Errands) return;
                int carrier = ColonyRegistry.GetOwner(carrierID);
                if (carrier == ColonyRegistry.NoColony) return;
                int src = SourceOwner(__instance);
                if (src != ColonyRegistry.NoColony && src != carrier) __result = false;
            }
        }

        [HarmonyPatch(typeof(FetchManager), nameof(FetchManager.IsFetchablePickup))]
        public static class FetchManager_IsFetchablePickup
        {
            public static void Postfix(Pickupable pickup, Storage destination, ref bool __result)
            {
                if (!__result || !Errands || pickup == null) return;
                int dest = ColonyRegistry.GetOwner(destination);
                if (dest == ColonyRegistry.NoColony) return;
                int src = SourceOwner(pickup);
                if (src != ColonyRegistry.NoColony && src != dest) __result = false;
            }
        }

        [HarmonyPatch(typeof(FetchManager), nameof(FetchManager.IsFetchablePickup_Exclude),
            new Type[] { typeof(KPrefabID), typeof(Storage), typeof(float), typeof(HashSet<Tag>), typeof(Tag[]), typeof(Storage) })]
        public static class FetchManager_IsFetchablePickup_Exclude
        {
            public static void Postfix(KPrefabID pickup_id, Storage source, Storage destination, ref bool __result)
            {
                if (!__result || !Errands) return;
                int dest = ColonyRegistry.GetOwner(destination);
                if (dest == ColonyRegistry.NoColony) return;
                int src = ColonyRegistry.GetOwner(source);
                if (src == ColonyRegistry.NoColony && pickup_id != null) src = ColonyRegistry.GetOwner(pickup_id.InstanceID);
                if (src != ColonyRegistry.NoColony && src != dest) __result = false;
            }
        }

        // =====================================================================
        // Local input scope: everything the local player does with tools acts "as" their colony
        // =====================================================================

        private static void PushLocal(ref bool __state)
        {
            __state = false;
            if (!On) return;
            int local = ColonyRegistry.LocalColonyId;
            ColonyContext.Push(local == ColonyRegistry.NoColony ? ColonyContext.Spectator : local);
            __state = true;
        }

        private static Exception PopLocal(bool __state, Exception __exception)
        {
            if (__state) ColonyContext.Pop();
            return __exception;
        }

        [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.OnKeyDown))]
        public static class PlayerController_OnKeyDown
        {
            public static void Prefix(ref bool __state) => PushLocal(ref __state);
            public static Exception Finalizer(bool __state, Exception __exception) => PopLocal(__state, __exception);
        }

        [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.OnKeyUp))]
        public static class PlayerController_OnKeyUp
        {
            public static void Prefix(ref bool __state) => PushLocal(ref __state);
            public static Exception Finalizer(bool __state, Exception __exception) => PopLocal(__state, __exception);
        }

        [HarmonyPatch(typeof(PlayerController), "Update")]
        public static class PlayerController_Update
        {
            public static void Prefix(ref bool __state) => PushLocal(ref __state);
            public static Exception Finalizer(bool __state, Exception __exception) => PopLocal(__state, __exception);
        }

        // =====================================================================
        // Tagging new objects with the acting colony
        // =====================================================================

        [HarmonyPatch(typeof(BuildingDef), nameof(BuildingDef.TryPlace),
            new Type[] { typeof(GameObject), typeof(Vector3), typeof(Orientation), typeof(IList<Tag>), typeof(string), typeof(bool), typeof(int) })]
        public static class BuildingDef_TryPlace
        {
            public static void Postfix(GameObject __result) => ColonyGuard.TagNew(__result);
        }

        [HarmonyPatch(typeof(BuildingDef), nameof(BuildingDef.TryReplaceTile),
            new Type[] { typeof(GameObject), typeof(Vector3), typeof(Orientation), typeof(IList<Tag>), typeof(string), typeof(int) })]
        public static class BuildingDef_TryReplaceTile
        {
            public static void Postfix(GameObject __result) => ColonyGuard.TagNew(__result);
        }

        [HarmonyPatch(typeof(BuildingDef), nameof(BuildingDef.Build),
            new Type[] { typeof(int), typeof(Orientation), typeof(Storage), typeof(IList<Tag>), typeof(float), typeof(bool), typeof(float) })]
        public static class BuildingDef_Build
        {
            public static void Postfix(GameObject __result) => ColonyGuard.TagNew(__result);
        }

        /// <summary>The finished building inherits the blueprint's colony.</summary>
        [HarmonyPatch(typeof(Constructable), "FinishConstruction")]
        public static class Constructable_FinishConstruction
        {
            public static void Prefix(Constructable __instance, ref bool __state)
            {
                __state = false;
                if (!On) return;
                int owner = ColonyRegistry.GetOwner(__instance.gameObject);
                if (owner == ColonyRegistry.NoColony) return;
                ColonyContext.Push(owner);
                __state = true;
            }

            public static Exception Finalizer(bool __state, Exception __exception)
            {
                if (__state) ColonyContext.Pop();
                return __exception;
            }
        }

        [HarmonyPatch(typeof(DigTool), nameof(DigTool.PlaceDig))]
        public static class DigTool_PlaceDig
        {
            public static bool Prefix(int cell, ref GameObject __result)
            {
                if (!On || ColonyGuard.CanModifyCell(cell)) return true;
                __result = null;
                return false;
            }

            public static void Postfix(GameObject __result) => ColonyGuard.TagNew(__result);
        }

        [HarmonyPatch(typeof(MopTool), "OnDragTool")]
        public static class MopTool_OnDragTool
        {
            public static bool Prefix(int cell) => !On || ColonyGuard.CanModifyCell(cell);

            public static void Postfix(int cell)
            {
                if (!On || !Grid.IsValidCell(cell) || !ColonyGuard.CanModifyCell(cell)) return;
                ColonyGuard.TagNew(Grid.Objects[cell, 8]); // MopTool stores its placer on layer 8
            }
        }

        [HarmonyPatch(typeof(MinionStartingStats), nameof(MinionStartingStats.Deliver))]
        public static class MinionStartingStats_Deliver
        {
            public static void Postfix(GameObject __result) => ColonyGuard.TagNew(__result);
        }

        [HarmonyPatch(typeof(CarePackageInfo), nameof(CarePackageInfo.Deliver))]
        public static class CarePackageInfo_Deliver
        {
            public static void Postfix(GameObject __result) => ColonyGuard.TagNew(__result);
        }

        // =====================================================================
        // Guards: a colony can't give orders on another colony's objects
        // =====================================================================

        [HarmonyPatch(typeof(Deconstructable), nameof(Deconstructable.QueueDeconstruction), new Type[] { typeof(bool) })]
        public static class Deconstructable_QueueDeconstruction
        {
            public static bool Prefix(Deconstructable __instance) => !On || ColonyGuard.CanActOn(__instance);
        }

        [HarmonyPatch(typeof(Prioritizable), nameof(Prioritizable.SetMasterPriority))]
        public static class Prioritizable_SetMasterPriority
        {
            public static bool Prefix(Prioritizable __instance) => !On || ColonyGuard.CanActOn(__instance);
        }

        [HarmonyPatch(typeof(Clearable), nameof(Clearable.MarkForClear))]
        public static class Clearable_MarkForClear
        {
            public static bool Prefix(Clearable __instance, bool restoringFromSave)
            {
                if (!On || restoringFromSave) return true;
                return ColonyGuard.CanActOn(__instance);
            }

            public static void Postfix(Clearable __instance, bool restoringFromSave)
            {
                if (On && !restoringFromSave) ColonyGuard.ClaimIfUnowned(__instance.gameObject);
            }
        }

        [HarmonyPatch(typeof(HarvestDesignatable), nameof(HarvestDesignatable.SetHarvestWhenReady))]
        public static class HarvestDesignatable_SetHarvestWhenReady
        {
            public static bool Prefix(HarvestDesignatable __instance) => !On || ColonyGuard.CanActOn(__instance);
            public static void Postfix(HarvestDesignatable __instance, bool state)
            {
                if (On && state) ColonyGuard.ClaimIfUnowned(__instance.gameObject);
            }
        }

        [HarmonyPatch(typeof(Capturable), nameof(Capturable.MarkForCapture), new Type[] { typeof(bool), typeof(PrioritySetting), typeof(bool) })]
        public static class Capturable_MarkForCapture
        {
            public static bool Prefix(Capturable __instance) => !On || ColonyGuard.CanActOn(__instance);
            public static void Postfix(Capturable __instance, bool mark)
            {
                if (On && mark) ColonyGuard.ClaimIfUnowned(__instance.gameObject);
            }
        }

        [HarmonyPatch(typeof(Disinfectable), nameof(Disinfectable.MarkForDisinfect))]
        public static class Disinfectable_MarkForDisinfect
        {
            public static bool Prefix(Disinfectable __instance) => !On || ColonyGuard.CanActOn(__instance);
        }

        /// <summary>CancelTool fires a cancel event on every object in the cell — filter out foreign ones.</summary>
        [HarmonyPatch(typeof(CancelTool), "OnDragTool")]
        public static class CancelTool_OnDragTool
        {
            public static bool Prefix(CancelTool __instance, int cell)
            {
                if (!On || !ColonyContext.IsPlayerAction || !Grid.IsValidCell(cell)) return true;
                for (int i = 0; i < 45; i++)
                {
                    GameObject go = Grid.Objects[cell, i];
                    if (go == null) continue;
                    if (!ColonyGuard.CanCancel(go)) continue;
                    string layer = __instance.GetFilterLayerFromGameObject(go);
                    if (__instance.IsActiveLayer(layer)) go.Trigger(2127324410);
                }
                return false;
            }
        }

        /// <summary>No building inside another colony's territory, and no replacing their tiles.</summary>
        [HarmonyPatch(typeof(BuildingDef), nameof(BuildingDef.IsValidPlaceLocation),
            new Type[] { typeof(GameObject), typeof(int), typeof(Orientation), typeof(bool), typeof(string), typeof(bool) },
            new ArgumentType[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Normal })]
        public static class BuildingDef_IsValidPlaceLocation
        {
            public static void Postfix(BuildingDef __instance, int cell, ref string fail_reason, ref bool __result)
            {
                if (!__result || !On || !ColonyContext.IsPlayerAction) return;
                if (ColonyContext.Current == ColonyContext.Spectator)
                {
                    __result = false;
                    fail_reason = "Found your colony first";
                    return;
                }
                if (!ColonyGuard.CanModifyCell(cell))
                {
                    __result = false;
                    var owner = ColonyRegistry.Get(ColonyRegistry.GetTerritoryOwner(cell));
                    fail_reason = $"Inside {owner?.Name ?? "another colony"}'s territory";
                    return;
                }
                foreach (var layer in new[] { __instance.ObjectLayer, __instance.ReplacementLayer, __instance.TileLayer })
                {
                    if (layer == ObjectLayer.NumLayers || (int)layer < 0) continue;
                    var existing = Grid.Objects[cell, (int)layer];
                    if (existing != null && !ColonyGuard.CanActOn(existing))
                    {
                        __result = false;
                        fail_reason = "Belongs to another colony";
                        return;
                    }
                }
            }
        }

        // =====================================================================
        // Local UI: no side screens / action buttons on another colony's objects
        // =====================================================================

        [HarmonyPatch(typeof(DetailsScreen), nameof(DetailsScreen.Refresh))]
        public static class DetailsScreen_Refresh
        {
            public static void Postfix(DetailsScreen __instance, GameObject go)
            {
                if (!On || go == null) return;
                if (!ColonyGuard.LocalCanControl(go)) __instance.DeactivateSideContent();
            }
        }

        [HarmonyPatch(typeof(UserMenu), nameof(UserMenu.AddButton))]
        public static class UserMenu_AddButton
        {
            public static bool Prefix(GameObject go) => !On || ColonyGuard.LocalCanControl(go);
        }

        [HarmonyPatch(typeof(UserMenu), nameof(UserMenu.AddSlider))]
        public static class UserMenu_AddSlider
        {
            public static bool Prefix(GameObject go) => !On || ColonyGuard.LocalCanControl(go);
        }

        // =====================================================================
        // Separate research: research buildings of another colony feed that colony's profile (host)
        // =====================================================================

        [ThreadStatic] private static int _researchRedirect;

        /// <summary>Colony whose research tree receives Research.AddResearchPoints right now (0 = local/vanilla).</summary>
        public static int ResearchRedirect
        {
            get => _researchRedirect;
            set => _researchRedirect = value;
        }

        private static int ForeignOwner(Component c) => ColonyResearch.ForeignOwner(c);

        [HarmonyPatch(typeof(ResearchCenter), "ConvertMassToResearchPoints")]
        public static class ResearchCenter_ConvertMassToResearchPoints
        {
            public static void Prefix(ResearchCenter __instance, ref int __state)
            {
                __state = _researchRedirect;
                _researchRedirect = ForeignOwner(__instance);
            }

            public static Exception Finalizer(int __state, Exception __exception)
            {
                _researchRedirect = __state;
                return __exception;
            }
        }

        [HarmonyPatch(typeof(Research), nameof(Research.AddResearchPoints))]
        public static class Research_AddResearchPoints
        {
            public static bool Prefix(string researchTypeID, float points)
            {
                if (_researchRedirect == ColonyRegistry.NoColony) return true;
                ColonyResearch.AddPoints(_researchRedirect, researchTypeID, points);
                return false;
            }
        }

        [HarmonyPatch(typeof(ResearchCenter), nameof(ResearchCenter.GetPercentComplete))]
        public static class ResearchCenter_GetPercentComplete
        {
            public static bool Prefix(ResearchCenter __instance, ref float __result)
            {
                int owner = ForeignOwner(__instance);
                if (owner == ColonyRegistry.NoColony) return true;
                __result = ColonyResearch.GetPercentComplete(owner, __instance.research_point_type_id);
                return false;
            }
        }

        [HarmonyPatch(typeof(ResearchCenter), "ResearchComponentCompleted")]
        public static class ResearchCenter_ResearchComponentCompleted
        {
            public static bool Prefix(ResearchCenter __instance, ref bool __result)
            {
                int owner = ForeignOwner(__instance);
                if (owner == ColonyRegistry.NoColony) return true;
                __result = ColonyResearch.ComponentCompleted(owner, __instance.research_point_type_id);
                return false;
            }
        }

        /// <summary>Re-implementation of ResearchCenter.UpdateWorkingState against the colony's profile.</summary>
        [HarmonyPatch(typeof(ResearchCenter), "UpdateWorkingState")]
        public static class ResearchCenter_UpdateWorkingState
        {
            private static readonly FieldInfo F_operational = AccessTools.Field(typeof(ResearchCenter), "operational");

            public static bool Prefix(ResearchCenter __instance)
            {
                int owner = ForeignOwner(__instance);
                if (owner == ColonyRegistry.NoColony) return true;
                var colony = ColonyRegistry.Get(owner);
                var op = F_operational?.GetValue(__instance) as Operational;
                if (colony == null || op == null) return true;

                string type = __instance.research_point_type_id;
                Tech active = ColonyResearch.GetActiveTech(colony.Research);
                bool hasActive = active != null;
                bool needsThisType = hasActive && active.costsByResearchTypeID.TryGetValue(type, out float cost)
                                     && ColonyResearch.GetPoints(colony.Research, active.Id, type) < cost;

                var sel = __instance.GetComponent<KSelectable>();
                var items = Db.Get().BuildingStatusItems;
                if (sel != null)
                {
                    if (op.GetFlag(EnergyConsumer.PoweredFlag) && !hasActive)
                    {
                        sel.AddStatusItem(items.NoResearchSelected);
                        sel.RemoveStatusItem(items.NoApplicableResearchSelected);
                    }
                    else if (op.GetFlag(EnergyConsumer.PoweredFlag) && !needsThisType)
                    {
                        sel.RemoveStatusItem(items.NoResearchSelected);
                        sel.AddStatusItem(items.NoApplicableResearchSelected);
                    }
                    else
                    {
                        sel.RemoveStatusItem(items.NoResearchSelected);
                        sel.RemoveStatusItem(items.NoApplicableResearchSelected);
                    }
                }
                op.SetFlag(ResearchCenter.ResearchSelectedFlag, hasActive && needsThisType);
                if ((!hasActive || !needsThisType) && __instance.worker != null)
                    __instance.StopWork(__instance.worker, aborted: true);
                return false;
            }
        }

        private const string Nuclear = "nuclear";

        [HarmonyPatch(typeof(NuclearResearchCenterWorkable), "OnWorkTick")]
        public static class NuclearResearchCenterWorkable_OnWorkTick
        {
            private static readonly FieldInfo F_storage = AccessTools.Field(typeof(NuclearResearchCenterWorkable), "radiationStorage");

            public static void Prefix(NuclearResearchCenterWorkable __instance, ref int __state)
            {
                __state = ResearchRedirect;
                ResearchRedirect = ColonyResearch.ForeignOwner(__instance);
            }

            public static void Postfix(NuclearResearchCenterWorkable __instance, ref bool __result)
            {
                int owner = ResearchRedirect;
                if (owner == ColonyRegistry.NoColony) return;
                // vanilla decided "done?" from the LOCAL tree; recompute from the owner's tree
                var storage = F_storage?.GetValue(__instance) as HighEnergyParticleStorage;
                var colony = ColonyRegistry.Get(owner);
                Tech active = colony != null ? ColonyResearch.GetActiveTech(colony.Research) : null;
                __result = (storage != null && storage.IsEmpty()) || active == null
                           || ColonyResearch.GetPercentComplete(owner, Nuclear) >= 1f;
            }

            public static Exception Finalizer(int __state, Exception __exception)
            {
                ResearchRedirect = __state;
                return __exception;
            }
        }

        [HarmonyPatch(typeof(NuclearResearchCenterWorkable), nameof(NuclearResearchCenterWorkable.GetPercentComplete))]
        public static class NuclearResearchCenterWorkable_GetPercentComplete
        {
            public static bool Prefix(NuclearResearchCenterWorkable __instance, ref float __result)
            {
                int owner = ColonyResearch.ForeignOwner(__instance);
                if (owner == ColonyRegistry.NoColony) return true;
                __result = ColonyResearch.GetPercentComplete(owner, Nuclear);
                return false;
            }
        }

        [HarmonyPatch(typeof(NuclearResearchCenter.States), "IsResearchSelected")]
        public static class NuclearResearchCenter_IsResearchSelected
        {
            public static bool Prefix(NuclearResearchCenter.StatesInstance smi, ref bool __result)
            {
                int owner = ColonyResearch.ForeignOwner(smi?.master);
                if (owner == ColonyRegistry.NoColony) return true;
                var colony = ColonyRegistry.Get(owner);
                __result = colony != null && ColonyResearch.GetActiveTech(colony.Research) != null;
                return false;
            }
        }

        [HarmonyPatch(typeof(NuclearResearchCenter.States), "IsResearchApplicable")]
        public static class NuclearResearchCenter_IsResearchApplicable
        {
            public static bool Prefix(NuclearResearchCenter.StatesInstance smi, ref bool __result)
            {
                int owner = ColonyResearch.ForeignOwner(smi?.master);
                if (owner == ColonyRegistry.NoColony) return true;
                var colony = ColonyRegistry.Get(owner);
                Tech active = colony != null ? ColonyResearch.GetActiveTech(colony.Research) : null;
                string type = smi.master.researchTypeID;
                __result = active != null && active.costsByResearchTypeID.TryGetValue(type, out float cost)
                           && ColonyResearch.GetPoints(colony.Research, active.Id, type) < cost;
                return false;
            }
        }

        // =====================================================================
        // Printing Pod: deliveries belong to the pod's colony (immigration timer itself is shared in this MVP)
        // =====================================================================

        public static Telepad FindTelepad(int colonyId)
        {
            if (colonyId <= 0) return null;
            foreach (Telepad pad in global::Components.Telepads.Items)
                if (pad != null && ColonyRegistry.GetOwner(pad.gameObject) == colonyId) return pad;
            return null;
        }

        // ---- per-colony Printing Pod timers (ColonyRules.SeparatePrintingPod) ----

        [HarmonyPatch(typeof(Immigration), nameof(Immigration.Sim200ms))]
        public static class Immigration_Sim200ms
        {
            public static void Postfix(float dt) => ColonyImmigration.TickForeign(dt);
        }

        /// <summary>The local timer only runs while the LOCAL colony's pod is operational (vanilla: any pod).</summary>
        [HarmonyPatch(typeof(Immigration), "IsHalted")]
        public static class Immigration_IsHalted
        {
            public static bool Prefix(ref bool __result)
            {
                if (!ColonyImmigration.SeparateActive || ColonyImmigration.HolderColony <= 0) return true;
                __result = ColonyImmigration.IsHalted(ColonyImmigration.HolderColony);
                return false;
            }
        }

        /// <summary>Printing/rejecting at another colony's pod ends THAT colony's timer.</summary>
        [HarmonyPatch(typeof(Immigration), nameof(Immigration.EndImmigration))]
        public static class Immigration_EndImmigration
        {
            public static bool Prefix(ref int __result)
            {
                if (!ColonyImmigration.SeparateActive || !ColonyContext.IsSet) return true;
                if (ColonyContext.Current == ColonyImmigration.HolderColony) return true;
                __result = ColonyImmigration.EndForeign(ColonyContext.Current);
                return false;
            }
        }

        [HarmonyPatch(typeof(Telepad), nameof(Telepad.OnAcceptDelivery))]
        public static class Telepad_OnAcceptDelivery
        {
            public static void Prefix(Telepad __instance, ITelepadDeliverable delivery, ref bool __state)
            {
                __state = false;
                if (!On) return;
                int owner = ColonyRegistry.GetOwner(__instance.gameObject);
                if (owner == ColonyRegistry.NoColony) return;
                if (delivery is MinionStartingStats stats && !ONI_Together.Networking.MultiplayerSession.IsClient)
                    stats.Name = ColonyRules.PrefixedName(owner, stats.Name);
                ColonyContext.Push(owner);
                __state = true;
            }

            public static Exception Finalizer(bool __state, Exception __exception)
            {
                if (__state) ColonyContext.Pop();
                return __exception;
            }
        }

        [HarmonyPatch(typeof(Telepad), nameof(Telepad.RejectAll))]
        public static class Telepad_RejectAll
        {
            public static void Prefix(Telepad __instance, ref bool __state)
            {
                __state = false;
                if (!On) return;
                int owner = ColonyRegistry.GetOwner(__instance.gameObject);
                if (owner == ColonyRegistry.NoColony) return;
                ColonyContext.Push(owner);
                __state = true;
            }

            public static Exception Finalizer(bool __state, Exception __exception)
            {
                if (__state) ColonyContext.Pop();
                return __exception;
            }
        }

        /// <summary>Camera "go home" (H) and similar UI helpers jump to the local colony's Printing Pod.</summary>
        [HarmonyPatch(typeof(GameUtil), nameof(GameUtil.GetActiveTelepad))]
        public static class GameUtil_GetActiveTelepad
        {
            public static void Postfix(ref GameObject __result)
            {
                if (!On) return;
                var mine = FindTelepad(ColonyRegistry.LocalColonyId);
                if (mine != null) __result = mine.gameObject;
            }
        }

        /// <summary>Whatever pod was clicked, the printing screen always opens for the local colony's pod.</summary>
        [HarmonyPatch(typeof(ImmigrantScreen), nameof(ImmigrantScreen.InitializeImmigrantScreen))]
        public static class ImmigrantScreen_Initialize
        {
            public static bool Prefix(ref Telepad telepad)
            {
                if (!On) return true;
                int local = ColonyRegistry.LocalColonyId;
                if (telepad != null && ColonyRegistry.GetOwner(telepad.gameObject) == local) return true;
                var mine = FindTelepad(local);
                if (mine == null) return false;
                telepad = mine;
                return true;
            }
        }
    }
}
