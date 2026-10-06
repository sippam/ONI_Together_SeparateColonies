using System;
using System.Collections.Generic;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// Per-colony schedules (ColonyRules.SeparateSchedules).
    /// ONI Together already keeps the schedule LIST identical on every machine (add/delete/edit packets by index);
    /// this class only tracks which colony owns which schedule (index-aligned with ScheduleManager.schedules).
    /// The host is authoritative: it re-broadcasts the owner list in the registry after every change.
    /// </summary>
    public static class ColonySchedules
    {
        /// <summary>Owner colony per schedule index (0 = unowned / shared).</summary>
        public static readonly List<int> Owners = new List<int>();

        /// <summary>Set around local UI clicks that create a schedule (add / duplicate button).</summary>
        public static bool LocalUiCreating;

        public static bool SeparateActive => ColonyRegistry.Active && ColonyRules.SeparateSchedules;

        private static List<Schedule> Schedules => ScheduleManager.Instance != null ? ScheduleManager.Instance.GetSchedules() : null;

        public static void Reset() => Owners.Clear();

        private static void Normalize()
        {
            var list = Schedules;
            if (list == null) return;
            while (Owners.Count < list.Count) Owners.Add(ColonyRegistry.NoColony);
            if (Owners.Count > list.Count) Owners.RemoveRange(list.Count, Owners.Count - list.Count);
        }

        public static int GetOwner(Schedule s)
        {
            var list = Schedules;
            if (s == null || list == null) return ColonyRegistry.NoColony;
            int idx = list.IndexOf(s);
            if (idx < 0 || idx >= Owners.Count) return ColonyRegistry.NoColony;
            return Owners[idx];
        }

        public static void SetOwner(Schedule s, int colonyId)
        {
            var list = Schedules;
            if (s == null || list == null) return;
            Normalize();
            int idx = list.IndexOf(s);
            if (idx < 0) return;
            if (Owners[idx] == colonyId) return;
            Owners[idx] = colonyId;
            ColonyNet.MarkRegistryDirty();
        }

        /// <summary>Local UI: may the local player see/edit this schedule?</summary>
        public static bool LocalCanEdit(Schedule s)
        {
            if (!SeparateActive || s == null) return true;
            int owner = GetOwner(s);
            return owner == ColonyRegistry.NoColony || owner == ColonyRegistry.LocalColonyId;
        }

        /// <summary>ScheduleManager.AddSchedule / DuplicateSchedule postfix (every machine).</summary>
        public static void OnScheduleAdded(Schedule s)
        {
            if (!ColonyRegistry.Enabled || s == null) return;
            int owner = ColonyRegistry.NoColony;
            if (LocalUiCreating) owner = ColonyRegistry.LocalColonyId;
            else if (ColonyContext.IsSet) owner = ColonyContext.Current;
            Normalize();
            if (owner != ColonyRegistry.NoColony) SetOwner(s, owner);
        }

        /// <summary>ScheduleManager.DeleteSchedule prefix (every machine): drop the owner entry before the list shrinks.</summary>
        public static void OnScheduleDeleting(Schedule s)
        {
            var list = Schedules;
            if (s == null || list == null || list.Count <= 1) return; // vanilla refuses to delete the last one
            Normalize();
            int idx = list.IndexOf(s);
            if (idx >= 0 && idx < Owners.Count) { Owners.RemoveAt(idx); ColonyNet.MarkRegistryDirty(); }
        }

        /// <summary>The colony's first schedule; creates "&lt;colony&gt; schedule" when it has none (host / single player).</summary>
        public static Schedule EnsureColonySchedule(int colonyId)
        {
            var list = Schedules;
            if (list == null || colonyId <= 0) return null;
            Normalize();
            for (int i = 0; i < list.Count; i++)
                if (Owners[i] == colonyId) return list[i];
            if (MultiplayerSessionIsClient()) return null; // only the host creates schedules
            var info = ColonyRegistry.Get(colonyId);
            var s = ScheduleManager.Instance.AddSchedule(Db.Get().ScheduleGroups.allGroups, $"{info?.Name ?? ("Colony " + colonyId)} schedule", false);
            Normalize();
            SetOwner(s, colonyId);
            Debug.Log($"[ONIT-Colonies] Created schedule for colony #{colonyId}");
            return s;
        }

        /// <summary>Host: a dupe that belongs to a colony must sit on one of that colony's schedules.</summary>
        public static void EnsureDupeOnOwnSchedule(GameObject go, int colonyId)
        {
            if (!SeparateActive || go == null || colonyId <= 0 || MultiplayerSessionIsClient()) return;
            var sched = go.GetComponent<Schedulable>();
            if (sched == null) return;
            var current = sched.GetSchedule();
            if (current != null && GetOwner(current) == colonyId) return;
            var target = EnsureColonySchedule(colonyId);
            if (target == null || target == current) return;
            current?.Unassign(sched);
            target.Assign(sched);
        }

        /// <summary>Host: give every unowned schedule to a colony (when the mode is switched on mid-game).</summary>
        public static void ClaimAllUnowned(int colonyId)
        {
            var list = Schedules;
            if (list == null) return;
            Normalize();
            bool changed = false;
            for (int i = 0; i < Owners.Count; i++)
                if (Owners[i] == ColonyRegistry.NoColony) { Owners[i] = colonyId; changed = true; }
            if (changed) ColonyNet.MarkRegistryDirty();
        }

        /// <summary>Client: owner list from the host.</summary>
        public static void ApplyRemote(List<int> owners)
        {
            if (owners == null) return;
            Owners.Clear();
            Owners.AddRange(owners);
            RefreshScreen();
        }

        public static void LoadFromSave(List<int> owners)
        {
            Owners.Clear();
            if (owners != null) Owners.AddRange(owners);
        }

        public static List<int> Snapshot()
        {
            Normalize();
            return new List<int>(Owners);
        }

        public static void OnModeChanged(bool separate)
        {
            if (MultiplayerSessionIsClient() || !ColonyRegistry.Enabled || !separate) return;
            // put every colony's dupes back on their own schedules
            foreach (MinionIdentity mi in global::Components.LiveMinionIdentities.Items)
            {
                if (mi == null) continue;
                int owner = ColonyRegistry.GetOwner(mi.gameObject);
                if (owner > 0) EnsureDupeOnOwnSchedule(mi.gameObject, owner);
            }
            ColonyNet.MarkRegistryDirty();
        }

        private static void RefreshScreen()
        {
            try
            {
                var list = Schedules;
                var f = HarmonyLib.AccessTools.Field(typeof(ScheduleManager), "onSchedulesChanged");
                if (list != null && f?.GetValue(ScheduleManager.Instance) is Action<List<Schedule>> cb) cb(list);
            }
            catch { }
        }

        private static bool MultiplayerSessionIsClient() => ONI_Together.Networking.MultiplayerSession.IsClient;
    }
}
