using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// Schedule screen dropdowns with separate schedules: the schedule list already shows only the local colony's
    /// schedules, but the per-duplicant "move to schedule" dropdown listed every colony's schedules and the blank
    /// "add duplicant" dropdown listed every colony's duplicants. Filter both to what the local player may use.
    /// </summary>
    public static class ColonyScheduleDropdowns
    {
        private static bool On => ColonySchedules.SeparateActive;

        private static void FilterSchedules(ScheduleMinionWidget w)
        {
            if (!On || w == null || w.dropDown == null || ScheduleManager.Instance == null) return;
            var list = ScheduleManager.Instance.GetSchedules().Where(ColonySchedules.LocalCanEdit).Cast<IListableOption>().ToList();
            w.dropDown.ChangeContent(list);
        }

        private static void FilterMinions(ScheduleMinionWidget w)
        {
            if (!On || w == null || w.dropDown == null) return;
            var list = global::Components.LiveMinionIdentities.Items
                .Where(m => m != null && ColonyGuard.LocalCanControl(m.gameObject))
                .Cast<IListableOption>().ToList();
            w.dropDown.ChangeContent(list);
        }

        [HarmonyPatch(typeof(ScheduleMinionWidget), nameof(ScheduleMinionWidget.Setup))]
        public static class ScheduleMinionWidget_Setup
        {
            public static void Postfix(ScheduleMinionWidget __instance)
            {
                try { FilterSchedules(__instance); }
                catch (System.Exception ex) { Debug.LogWarning($"[ONIT-Colonies] schedule dropdown: {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(ScheduleMinionWidget), nameof(ScheduleMinionWidget.SetupBlank))]
        public static class ScheduleMinionWidget_SetupBlank
        {
            public static void Postfix(ScheduleMinionWidget __instance)
            {
                try { FilterMinions(__instance); }
                catch (System.Exception ex) { Debug.LogWarning($"[ONIT-Colonies] schedule dropdown: {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(ScheduleMinionWidget), "OnLivingMinionsChanged")]
        public static class ScheduleMinionWidget_OnLivingMinionsChanged
        {
            public static void Postfix(ScheduleMinionWidget __instance)
            {
                try { FilterMinions(__instance); }
                catch (System.Exception ex) { Debug.LogWarning($"[ONIT-Colonies] schedule dropdown: {ex.Message}"); }
            }
        }

        /// <summary>Safety: never move a duplicant to another colony's schedule from the local UI.</summary>
        [HarmonyPatch(typeof(ScheduleMinionWidget), nameof(ScheduleMinionWidget.ChangeAssignment))]
        public static class ScheduleMinionWidget_ChangeAssignment
        {
            public static bool Prefix(Schedule targetSchedule, Schedulable schedulable)
            {
                if (!On) return true;
                if (!ColonySchedules.LocalCanEdit(targetSchedule)) return false;
                if (schedulable != null && !ColonyGuard.LocalCanControl(schedulable.gameObject)) return false;
                return true;
            }
        }
    }
}
