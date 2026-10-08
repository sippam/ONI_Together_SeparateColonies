using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ONI_Together.Colonies.Logistics
{
    /// <summary>
    /// New loose items (dug material, harvests, drops, deconstruction leftovers, machine and kitchen products) go straight
    /// into the nearest storage that accepts them. Items are queued when they spawn and handled on the next frames, so
    /// anything the game stores by itself in the meantime (a duplicant's hands, a machine) is left alone.
    /// Items that existed when the save was loaded are never touched.
    /// </summary>
    public static class AutoStore
    {
        private static readonly Queue<Pickupable> _queue = new Queue<Pickupable>();
        private static float _readyAt = -1f;
        private const int PerFrame = 150;

        public static void OnGameStart()
        {
            _queue.Clear();
            _readyAt = Time.unscaledTime + 5f; // skip everything spawned while the save loads
        }

        internal static void Enqueue(Pickupable p)
        {
            if (p == null || _readyAt < 0f || Time.unscaledTime < _readyAt || !LogisticsCfg.Simulates) return;
            if (!LogisticsCfg.AutoStore) return;
            if (_queue.Count > 20000) _queue.Clear(); // safety valve
            _queue.Enqueue(p);
        }

        public static void Tick()
        {
            if (_queue.Count == 0) return;
            if (!LogisticsCfg.AutoStore) { _queue.Clear(); return; }
            int n = Math.Min(PerFrame, _queue.Count);
            for (int i = 0; i < n; i++)
            {
                var p = _queue.Dequeue();
                try { if (Eligible(p)) StoreInNearest(p); }
                catch (Exception ex) { Debug.LogWarning($"[ONIT-Logistics] auto-store: {ex.Message}"); }
            }
        }

        private static bool Eligible(Pickupable p)
        {
            if (p == null || p.gameObject == null || !p.gameObject.activeInHierarchy) return false;
            if (p.storage != null) return false; // already in a storage / being carried
            var kpid = p.KPrefabID;
            if (kpid == null) return false;
            if (kpid.HasTag(GameTags.Stored) || kpid.HasTag(GameTags.StoredPrivate) || kpid.HasTag(GameTags.Creature)
                || kpid.HasTag(GameTags.BaseMinion) || kpid.HasTag(GameTags.Robot) || kpid.HasTag(GameTags.Equipped)) return false;
            if (p.GetComponent<MinionIdentity>() != null) return false;
            return p.FetchTotalAmount > 0f;
        }

        /// <summary>Store (splitting if needed) into the nearest accepting storages. True if anything was stored.</summary>
        public static bool StoreInNearest(Pickupable p)
        {
            int cell = Grid.PosToCell(p);
            if (!Grid.IsValidCell(cell)) return false;
            int world = p.GetMyWorldId();
            int owner = 0;
            if (LogisticsCfg.ColoniesSeparate)
            {
                owner = LogisticsCfg.OwnerOf(p.gameObject);
                if (owner == 0) owner = LogisticsCfg.OwnerForCell(cell);
            }

            var candidates = new List<(Storage s, long d)>();
            foreach (var s in StorageIndex.All)
            {
                if (!StorageIndex.Usable(s) || s.GetMyWorldId() != world) continue;
                if (!StorageIndex.Accepts(s, p.KPrefabID)) continue;
                if (s.RemainingCapacity() <= 0.01f) continue;
                if (!StorageIndex.OwnerOk(s, owner)) continue;
                int sc = Grid.PosToCell(s);
                if (!StorageIndex.InRange(cell, sc)) continue;
                candidates.Add((s, StorageIndex.DistSq(cell, sc)));
            }
            if (candidates.Count == 0) return false;
            candidates.Sort((a, b) => a.d.CompareTo(b.d));

            bool stored = false;
            foreach (var (s, _) in candidates)
            {
                if (p == null || p.gameObject == null) break;
                float rem = s.RemainingCapacity();
                if (rem <= 0.01f) continue;
                float amount = p.FetchTotalAmount;
                if (amount <= rem)
                {
                    s.Store(p.gameObject, hide_popups: true);
                    return true;
                }
                var part = p.Take(rem);
                if (part != null && part != p)
                {
                    s.Store(part.gameObject, hide_popups: true);
                    stored = true;
                }
            }
            return stored;
        }
    }

    [HarmonyPatch(typeof(Pickupable), "OnSpawn")]
    public static class Pickupable_OnSpawn
    {
        public static void Postfix(Pickupable __instance) => AutoStore.Enqueue(__instance);
    }

    /// <summary>Fabricators that keep their product inside (storeProduced) hand it over to storage too.</summary>
    [HarmonyPatch(typeof(ComplexFabricator), "SpawnOrderProduct")]
    public static class ComplexFabricator_SpawnOrderProduct
    {
        public static void Postfix(ComplexFabricator __instance, List<GameObject> __result)
        {
            if (__instance == null || __result == null || !LogisticsCfg.Simulates) return;
            if (!LogisticsCfg.AutoStore) return;
            var outStorage = __instance.outStorage;
            foreach (var go in __result)
            {
                if (go == null) continue;
                var p = go.GetComponent<Pickupable>();
                if (p == null) continue;
                try
                {
                    if (outStorage != null && outStorage.items.Contains(go))
                    {
                        // only move it if some storage can actually take it, otherwise leave it where the game put it
                        outStorage.Remove(go);
                        if (!AutoStore.StoreInNearest(p) && go != null && p.storage == null)
                            outStorage.Store(go, hide_popups: true);
                    }
                    // loose products are picked up by the spawn queue
                }
                catch (Exception ex) { Debug.LogWarning($"[ONIT-Logistics] fabricator product: {ex.Message}"); }
            }
        }
    }
}
