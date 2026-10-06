using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// Second batch of separate-colonies patches:
    ///  - management screens list only your own dupes
    ///  - Copy Settings / Empty Pipe / Disconnect respect ownership
    ///  - robots / fabricator products belong to the producing colony
    ///  - asteroid size option
    /// Targets checked against build 744825.
    /// </summary>
    public static class ColonyPatchesExtra
    {
        private static bool On => ColonyRegistry.Active;

        /// <summary>Colony of a dupe identity as used by UI (MinionIdentity / proxy). Stored minions: unknown (0).</summary>
        public static int OwnerOfIdentity(IAssignableIdentity id)
        {
            if (id == null) return ColonyRegistry.NoColony;
            if (id is MinionIdentity mi) return mi != null ? ColonyRegistry.GetOwner(mi.gameObject) : ColonyRegistry.NoColony;
            if (id is MinionAssignablesProxy proxy)
            {
                var target = proxy != null ? proxy.GetTargetGameObject() : null;
                return target != null ? ColonyRegistry.GetOwner(target) : ColonyRegistry.NoColony;
            }
            return ColonyRegistry.NoColony;
        }

        public static bool LocalOwnsIdentity(IAssignableIdentity id)
        {
            if (!On || id == null) return true;
            int owner = OwnerOfIdentity(id);
            return owner == ColonyRegistry.NoColony || owner == ColonyRegistry.LocalColonyId;
        }

        // =====================================================================
        // Management screens
        // =====================================================================

        /// <summary>Jobs/Priorities, Consumables, Vitals (all TableScreen): skip rows for other colonies' dupes.</summary>
        [HarmonyPatch(typeof(TableScreen), "AddRow")]
        public static class TableScreen_AddRow
        {
            public static bool Prefix(IAssignableIdentity minion) => minion == null || LocalOwnsIdentity(minion);
        }

        /// <summary>Skills screen: hide other colonies' dupes from the list.</summary>
        [HarmonyPatch(typeof(SkillsScreen), "BuildMinions")]
        public static class SkillsScreen_BuildMinions
        {
            private static readonly FieldInfo F_rows = AccessTools.Field(typeof(SkillsScreen), "sortableRows");

            public static void Postfix(SkillsScreen __instance)
            {
                if (!On) return;
                if (F_rows?.GetValue(__instance) is List<SkillMinionWidget> rows)
                {
                    foreach (var w in rows)
                        if (w != null) w.gameObject.SetActive(LocalOwnsIdentity(w.assignableIdentity));
                }
                if (!LocalOwnsIdentity(__instance.CurrentlySelectedMinion))
                    __instance.CurrentlySelectedMinion = FirstOwnDupe();
            }
        }

        /// <summary>Never let the Skills screen select another colony's dupe (also covers clicks on portraits elsewhere).</summary>
        [HarmonyPatch(typeof(SkillsScreen), nameof(SkillsScreen.CurrentlySelectedMinion), MethodType.Setter)]
        public static class SkillsScreen_SetSelected
        {
            public static void Prefix(ref IAssignableIdentity value)
            {
                if (!On || LocalOwnsIdentity(value)) return;
                value = FirstOwnDupe();
            }
        }

        private static IAssignableIdentity FirstOwnDupe()
        {
            int local = ColonyRegistry.LocalColonyId;
            foreach (MinionIdentity mi in global::Components.LiveMinionIdentities.Items)
                if (mi != null && ColonyRegistry.GetOwner(mi.gameObject) == local) return mi;
            return null;
        }

        // =====================================================================
        // Remaining tools
        // =====================================================================

        [HarmonyPatch(typeof(EmptyConduitWorkable), nameof(EmptyConduitWorkable.MarkForEmptying))]
        public static class EmptyConduitWorkable_MarkForEmptying
        {
            public static bool Prefix(EmptyConduitWorkable __instance) => !On || ColonyGuard.CanActOn(__instance);
        }

        [HarmonyPatch(typeof(EmptySolidConduitWorkable), nameof(EmptySolidConduitWorkable.MarkForEmptying))]
        public static class EmptySolidConduitWorkable_MarkForEmptying
        {
            public static bool Prefix(EmptySolidConduitWorkable __instance) => !On || ColonyGuard.CanActOn(__instance);
        }

        [HarmonyPatch(typeof(DisconnectTool), "DisconnectCellsAction")]
        public static class DisconnectTool_DisconnectCellsAction
        {
            public static bool Prefix(int cell, GameObject objectOnCell)
            {
                if (!On) return true;
                return ColonyGuard.CanActOn(objectOnCell) && ColonyGuard.CanModifyCell(cell);
            }
        }

        /// <summary>Copy Settings: can't paste onto another colony's buildings.</summary>
        [HarmonyPatch(typeof(CopyBuildingSettings), nameof(CopyBuildingSettings.ApplyCopy))]
        public static class CopyBuildingSettings_ApplyCopy
        {
            public static bool Prefix(KPrefabID other_id, ref bool __result)
            {
                if (!On || other_id == null || ColonyGuard.CanActOn(other_id.gameObject)) return true;
                __result = false;
                return false;
            }
        }

        // =====================================================================
        // Robots: Sweepy / Remote Worker inherit their station's colony
        // =====================================================================

        [ThreadStatic] private static int _robotSpawnColony;

        private static void EnterRobotSpawn(Component station, ref int state)
        {
            state = _robotSpawnColony;
            _robotSpawnColony = On && station != null ? ColonyRegistry.GetOwner(station.gameObject) : ColonyRegistry.NoColony;
        }

        [HarmonyPatch(typeof(SweepBotStation), "MakeNewSweepBot")]
        public static class SweepBotStation_MakeNewSweepBot
        {
            public static void Prefix(SweepBotStation __instance, ref int __state) => EnterRobotSpawn(__instance, ref __state);
            public static Exception Finalizer(int __state, Exception __exception) { _robotSpawnColony = __state; return __exception; }
        }

        [HarmonyPatch(typeof(RemoteWorkerDock), "MakeNewWorker")]
        public static class RemoteWorkerDock_MakeNewWorker
        {
            public static void Prefix(RemoteWorkerDock __instance, ref int __state) => EnterRobotSpawn(__instance, ref __state);
            public static Exception Finalizer(int __state, Exception __exception) { _robotSpawnColony = __state; return __exception; }
        }

        /// <summary>Rocket modules that build their payload internally (Scout Module → Rover + Lander).</summary>
        [HarmonyPatch(typeof(BuildingInternalConstructor.Instance), nameof(BuildingInternalConstructor.Instance.ConstructionComplete))]
        public static class BuildingInternalConstructor_ConstructionComplete
        {
            public static void Prefix(BuildingInternalConstructor.Instance __instance, ref int __state)
            {
                __state = _robotSpawnColony;
                _robotSpawnColony = On && __instance?.gameObject != null ? ColonyRegistry.GetOwner(__instance.gameObject) : ColonyRegistry.NoColony;
            }
            public static Exception Finalizer(int __state, Exception __exception) { _robotSpawnColony = __state; return __exception; }
        }

        /// <summary>
        /// Everything a colony's fabricator produces belongs to that colony — includes Flydo (FetchDrone,
        /// made at the Advanced Crafting Table) and keeps other colonies' dupes from sweeping fresh products.
        /// </summary>
        [HarmonyPatch(typeof(ComplexFabricator), "SpawnOrderProduct")]
        public static class ComplexFabricator_SpawnOrderProduct
        {
            public static void Postfix(ComplexFabricator __instance, List<GameObject> __result)
            {
                if (!On || __result == null) return;
                int owner = ColonyRegistry.GetOwner(__instance.gameObject);
                if (owner == ColonyRegistry.NoColony) return;
                foreach (var go in __result)
                    if (go != null && ColonyRegistry.GetOwner(go) == ColonyRegistry.NoColony)
                        ColonyRegistry.SetOwner(go, owner);
            }
        }

        /// <summary>Both stations spawn their robot with this overload of GameUtil.KInstantiate.</summary>
        [HarmonyPatch(typeof(GameUtil), nameof(GameUtil.KInstantiate),
            new Type[] { typeof(GameObject), typeof(Vector3), typeof(Grid.SceneLayer), typeof(string), typeof(int) })]
        public static class GameUtil_KInstantiate
        {
            public static void Postfix(GameObject __result)
            {
                if (_robotSpawnColony == ColonyRegistry.NoColony || __result == null) return;
                if (ColonyRegistry.GetOwner(__result) == ColonyRegistry.NoColony)
                    ColonyRegistry.SetOwner(__result, _robotSpawnColony);
            }
        }
    
        /// <summary>
        /// Bigger asteroid for new games: scale every world's worldsize right before the cluster lays out its worlds.
        /// Only the host's worldgen matters (clients load the host's save). Originals are remembered per World object
        /// so repeated games don't compound the scale. Rounded to multiples of 32 cells.
        /// </summary>
        [HarmonyPatch(typeof(ProcGenGame.Cluster), nameof(ProcGenGame.Cluster.InitializeWorlds))]
        public static class Cluster_InitializeWorlds
        {
            private static readonly Dictionary<ProcGen.World, Vector2I> _original = new Dictionary<ProcGen.World, Vector2I>();
            private static readonly PropertyInfo _sizeProp = AccessTools.Property(typeof(ProcGen.World), nameof(ProcGen.World.worldsize));

            public static void Prefix()
            {
                try
                {
                    float scale = Mathf.Clamp(Configuration.Instance?.Colonies?.WorldScalePercent ?? 100, 100, 300) / 100f;
                    var cache = ProcGen.SettingsCache.worlds?.worldCache;
                    if (cache == null || _sizeProp == null) return;
                    if (scale <= 1.001f && _original.Count == 0) return;
                    foreach (var w in cache.Values)
                    {
                        if (w == null) continue;
                        if (!_original.TryGetValue(w, out var orig)) { orig = w.worldsize; _original[w] = orig; }
                        var target = scale <= 1.001f ? orig : new Vector2I(Round32(orig.x * scale), Round32(orig.y * scale));
                        if (w.worldsize.x != target.x || w.worldsize.y != target.y)
                            _sizeProp.SetValue(w, target, null);
                    }
                    if (scale > 1.001f)
                        Debug.Log($"[ONIT-Colonies][World] Asteroid size x{scale} applied to {cache.Count} world definitions");
                }
                catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies][World] World scaling failed: {ex.Message}"); }
            }

            private static int Round32(float v) => Math.Max(32, (int)Math.Round(v / 32f) * 32);
        }
}
}
