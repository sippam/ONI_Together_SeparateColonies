using System;
using UnityEngine;
using ONI_Together.Networking;

namespace ONI_Together.Colonies.Logistics
{
    /// <summary>Settings + multiplayer/colony glue for the built-in logistics (formerly the Instant Logistics mod).</summary>
    public static class LogisticsCfg
    {
        public static bool AutoStore => Configuration.Instance?.Colonies?.LogisticsAutoStore ?? false;
        public static bool InstantDelivery => Configuration.Instance?.Colonies?.LogisticsInstantDelivery ?? false;
        public static int MaxDistance => Configuration.Instance?.Colonies?.LogisticsMaxDistance ?? 0;

        /// <summary>Only the simulating side acts: single player or the host. Clients get the result through sync.</summary>
        public static bool Simulates => !MultiplayerSession.InSession || MultiplayerSession.IsHost;

        /// <summary>Separate colonies on and resources not shared.</summary>
        public static bool ColoniesSeparate => ColonyRegistry.Active && !ColonyRules.SharedResources;

        public static int OwnerOf(GameObject go) => go != null ? ColonyRegistry.GetOwner(go) : 0;

        /// <summary>Colony an item spawned at this cell belongs to: territory owner, else the colony whose base is nearest.</summary>
        public static int OwnerForCell(int cell)
        {
            int t = ColonyRegistry.GetTerritoryOwner(cell);
            if (t != ColonyRegistry.NoColony) return t;
            Grid.CellToXY(cell, out int x, out int y);
            int best = 0; long bestD = long.MaxValue;
            foreach (var c in ColonyRegistry.Colonies)
            {
                if (!Grid.IsValidCell(c.HomeCell)) continue;
                Grid.CellToXY(c.HomeCell, out int hx, out int hy);
                long d = (long)(hx - x) * (hx - x) + (long)(hy - y) * (hy - y);
                if (d < bestD) { bestD = d; best = c.Id; }
            }
            return best;
        }
    }
}
