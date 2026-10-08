using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ONI_Together.Colonies.Logistics
{
    /// <summary>
    /// Fetch errands whose material is already in storage are fulfilled on the spot: the material is taken out of the
    /// nearest storages and put straight into the destination (construction site, machine, farm plot, kitchen...), then
    /// the errand completes exactly like a duplicant delivery would (same completion callbacks). Only when the storages
    /// together hold the full requested amount; otherwise duplicants fetch as usual. Errands into storage buildings
    /// (storage-to-storage hauling) are left to the game.
    /// </summary>
    public static class InstantDelivery
    {
        private static float _timer;
        private const float Interval = 0.5f;
        private const int MaxPerTick = 80;
        private static readonly MethodInfo Succeed = AccessTools.Method(typeof(StandardChoreBase), "Succeed", new[] { typeof(string) });
        private static readonly List<FetchChore> _chores = new List<FetchChore>();

        private struct Stock { public Pickupable P; public int Cell; }

        public static void Tick()
        {
            if (!LogisticsCfg.InstantDelivery) return;
            _timer += Time.unscaledDeltaTime;
            if (_timer < Interval) return;
            _timer = 0f;
            var gcp = GlobalChoreProvider.Instance;
            if (gcp == null || Succeed == null) return;

            _chores.Clear();
            foreach (var kv in gcp.fetchMap)
                if (kv.Value != null) _chores.AddRange(kv.Value);
            if (_chores.Count == 0) return;

            // what is in storage right now, by prefab, per world
            var stock = new Dictionary<int, Dictionary<Tag, List<Stock>>>();
            foreach (var s in StorageIndex.All)
            {
                if (!StorageIndex.Usable(s)) continue;
                int world = s.GetMyWorldId();
                if (!stock.TryGetValue(world, out var byTag)) stock[world] = byTag = new Dictionary<Tag, List<Stock>>();
                int sc = Grid.PosToCell(s);
                foreach (var go in s.items)
                {
                    if (go == null) continue;
                    var p = go.GetComponent<Pickupable>();
                    if (p == null || p.KPrefabID == null || p.UnreservedFetchAmount <= 0f) continue;
                    var tag = p.KPrefabID.PrefabTag;
                    if (!byTag.TryGetValue(tag, out var list)) byTag[tag] = list = new List<Stock>();
                    list.Add(new Stock { P = p, Cell = sc });
                }
            }
            if (stock.Count == 0) return;

            int done = 0;
            foreach (var chore in _chores)
            {
                if (done >= MaxPerTick) break;
                try { if (TryFulfil(chore, stock)) done++; }
                catch (Exception ex) { Debug.LogWarning($"[ONIT-Logistics] delivery: {ex.Message}"); }
            }
        }

        private static bool Valid(FetchChore chore)
        {
            if (chore == null || chore.isComplete || chore.driver != null) return false;
            var dest = chore.destination;
            if (dest == null || dest.gameObject == null || !dest.gameObject.activeInHierarchy) return false;
            if (StorageIndex.IsStorageBuilding(dest.gameObject)) return false;          // hauling into storage: not ours
            // conveyor loaders, automatic dispensers, rocket ports... would just cycle items back and forth;
            // critter feeders are the one filtered receiver we do fill
            if (dest.GetComponent<TreeFilterable>() != null && dest.GetComponent<CreatureFeeder>() == null) return false;
            if (dest.GetComponent<MinionIdentity>() != null) return false;
            if (chore.requiredTag == GameTags.Garbage) return false;                    // sweep errands
            var dec = dest.GetComponent<Deconstructable>();
            if (dec != null && dec.IsMarkedForDeconstruction()) return false;
            var en = dest.GetComponent<BuildingEnabledButton>();
            if (en != null && !en.IsEnabled) return false;
            return chore.originalAmount > 0f;
        }

        private static bool TryFulfil(FetchChore chore, Dictionary<int, Dictionary<Tag, List<Stock>>> stock)
        {
            if (!Valid(chore)) return false;
            var dest = chore.destination;
            int world = dest.GetMyWorldId();
            if (!stock.TryGetValue(world, out var byTag)) return false;
            int destCell = Grid.PosToCell(dest);
            float need = chore.originalAmount;

            // candidate stock for this errand
            var cands = new List<Stock>();
            if (chore.criteria == FetchChore.MatchCriteria.MatchID)
            {
                foreach (var t in chore.tags)
                    if (byTag.TryGetValue(t, out var list)) cands.AddRange(list);
            }
            else
            {
                foreach (var list in byTag.Values) cands.AddRange(list);
            }
            float available = 0f;
            for (int i = cands.Count - 1; i >= 0; i--)
            {
                var c = cands[i];
                // also applies the colony rules of the Separate Colonies fork (it patches IsFetchablePickup)
                if (c.P == null || c.P.storage == null || !StorageIndex.InRange(destCell, c.Cell)
                    || !FetchManager.IsFetchablePickup(c.P, chore, dest)) { cands.RemoveAt(i); continue; }
                available += c.P.UnreservedFetchAmount;
            }
            if (available + 0.0001f < need) return false;
            cands.Sort((a, b) => StorageIndex.DistSq(destCell, a.Cell).CompareTo(StorageIndex.DistSq(destCell, b.Cell)));

            float left = need;
            Pickupable last = null;
            foreach (var c in cands)
            {
                if (left <= 0.0001f) break;
                if (c.P == null || c.P.storage == null) continue;
                float take = Mathf.Min(left, c.P.UnreservedFetchAmount);
                if (take <= 0f) continue;
                var part = c.P.Take(take);
                if (part == null) continue;
                left -= part.FetchTotalAmount;
                dest.Store(part.gameObject, hide_popups: true);
                last = part;
            }
            if (last == null) return false;
            chore.fetchTarget = last;
            Succeed.Invoke(chore, new object[] { "ONIT-Logistics" });
            return true;
        }
    }
}
