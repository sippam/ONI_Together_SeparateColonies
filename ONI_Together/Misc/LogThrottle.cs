using System.Collections.Generic;
using UnityEngine;

namespace ONI_Together.Misc
{
    /// <summary>
    /// "Once per key per minute" for the hottest sync warnings (unknown NetIds): one session produced 100k+ of these
    /// lines; formatting + writing them is real CPU on every client.
    /// </summary>
    public static class LogThrottle
    {
        private static readonly Dictionary<string, Dictionary<long, float>> _seen = new Dictionary<string, Dictionary<long, float>>();

        public static bool Allow(string category, long key, float interval = 60f)
        {
            if (!_seen.TryGetValue(category, out var map)) { map = new Dictionary<long, float>(); _seen[category] = map; }
            float now = Time.unscaledTime;
            if (map.TryGetValue(key, out float t) && now - t < interval) return false;
            if (map.Count > 4096) map.Clear();
            map[key] = now;
            return true;
        }
    }
}
