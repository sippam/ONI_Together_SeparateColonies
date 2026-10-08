using System.Collections.Generic;
using UnityEngine;

namespace ONI_Together.Colonies.Logistics
{
    /// <summary>Player storages we store into and deliver from: storage bins (incl. smart / modded lockers), fridges, ration boxes.</summary>
    public static class StorageIndex
    {
        private static readonly List<Storage> _all = new List<Storage>();
        private static float _nextRefresh;

        public static void Invalidate() => _nextRefresh = 0f;

        public static bool IsStorageBuilding(GameObject go) =>
            go != null && (go.GetComponent<StorageLocker>() != null || go.GetComponent<Refrigerator>() != null || go.GetComponent<RationBox>() != null);

        public static List<Storage> All
        {
            get
            {
                if (Time.unscaledTime >= _nextRefresh)
                {
                    _nextRefresh = Time.unscaledTime + 3f;
                    _all.Clear();
                    foreach (var b in global::Components.BuildingCompletes.Items)
                    {
                        if (b == null) continue;
                        var go = b.gameObject;
                        if (!IsStorageBuilding(go)) continue;
                        var s = go.GetComponent<Storage>();
                        if (s != null && go.GetComponent<TreeFilterable>() != null) _all.Add(s);
                    }
                }
                return _all;
            }
        }

        /// <summary>Can items go in / come out right now?</summary>
        public static bool Usable(Storage s)
        {
            if (s == null || s.gameObject == null || !s.gameObject.activeInHierarchy) return false;
            if (!s.allowItemRemoval || s.GetOnlyFetchMarkedItems()) return false;
            var dec = s.GetComponent<Deconstructable>();
            if (dec != null && dec.IsMarkedForDeconstruction()) return false;
            var en = s.GetComponent<BuildingEnabledButton>();
            if (en != null && !en.IsEnabled) return false;
            return true;
        }

        public static bool Accepts(Storage s, KPrefabID item)
        {
            var tf = s.GetComponent<TreeFilterable>();
            return tf != null && item != null && tf.AcceptedTags.Contains(item.PrefabTag);
        }

        public static bool InRange(int a, int b)
        {
            int max = LogisticsCfg.MaxDistance;
            if (max <= 0) return true;
            return DistSq(a, b) <= (long)max * max;
        }

        public static long DistSq(int a, int b)
        {
            Grid.CellToXY(a, out int ax, out int ay);
            Grid.CellToXY(b, out int bx, out int by);
            return (long)(ax - bx) * (ax - bx) + (long)(ay - by) * (ay - by);
        }

        /// <summary>Colony rule (separate colonies only): an unowned storage or one of that colony.</summary>
        public static bool OwnerOk(Storage s, int owner)
        {
            if (owner == 0 || !LogisticsCfg.ColoniesSeparate) return true;
            int so = LogisticsCfg.OwnerOf(s.gameObject);
            return so == 0 || so == owner;
        }
    }
}
