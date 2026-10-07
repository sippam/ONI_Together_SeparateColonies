using System;
using System.Collections.Generic;
using UnityEngine;
using static ONI_Together.Colonies.ColonyLoc;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// World edits for separate colonies (host / single player only; clients get the result via hard sync):
    ///  - the start-base template (same one the host's asteroid started with) for new colonies — clears the area,
    ///    places the Printing Pod and the starting room like a fresh game
    ///  - extra water geysers
    /// </summary>
    public static class ColonyWorldTools
    {
        public static readonly string[] WaterGeysers = { "GeyserGeneric_hot_water" }; // Water Geyser (clean water vent)

        private static TemplateContainer _cachedTemplate;
        private static string _cachedFor;

        /// <summary>The start world's starting-base template (e.g. bases/sandstoneBase), or null.</summary>
        public static TemplateContainer GetStartTemplate()
        {
            try
            {
                var world = ClusterManager.Instance?.GetStartWorld();
                string worldName = world?.worldName;
                if (string.IsNullOrEmpty(worldName)) return null;
                if (_cachedFor == worldName) return _cachedTemplate;
                _cachedFor = worldName;
                _cachedTemplate = null;
                var data = ProcGen.SettingsCache.worlds?.GetWorldData(worldName);
                string tpl = data?.startingBaseTemplate;
                if (string.IsNullOrEmpty(tpl)) return null;
                if (!TemplateCache.Initted) TemplateCache.Init();
                if (!TemplateCache.TemplateExists(tpl)) return null;
                _cachedTemplate = TemplateCache.GetTemplate(tpl);
                if (_cachedTemplate?.cells == null || _cachedTemplate.cells.Count == 0) _cachedTemplate = null;
                Debug.Log($"[ONIT-Colonies] Start template for new colonies: {tpl} ({_cachedTemplate?.cells?.Count ?? 0} cells)");
                return _cachedTemplate;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ONIT-Colonies] start template lookup failed: {ex.Message}");
                return null;
            }
        }

        public static bool InStartWorld(int cell, int margin)
        {
            if (!Grid.IsValidCell(cell)) return false;
            var world = ClusterManager.Instance?.GetStartWorld();
            if (world == null) return true;
            Grid.CellToXY(cell, out int x, out int y);
            var min = world.minimumBounds; var max = world.maximumBounds;
            return x >= min.x + margin && x <= max.x - margin && y >= min.y + margin && y <= max.y - margin;
        }

        /// <summary>Can the start template be stamped with its origin at `cell` without wrecking anything important?</summary>
        public static bool ValidateTemplateArea(TemplateContainer tpl, int cell, out string reason)
        {
            reason = null;
            Grid.CellToXY(cell, out int x, out int y);
            // stamping clears every entity with Health in the area — never do that to a duplicant
            var area = TemplateCells(tpl, cell);
            foreach (MinionIdentity mi in global::Components.LiveMinionIdentities.Items)
            {
                if (mi == null) continue;
                int mc = Grid.PosToCell(mi);
                if (area.Contains(mc) || area.Contains(Grid.CellAbove(mc))) { reason = T("A duplicant is standing there"); return false; }
            }
            foreach (var c in tpl.cells)
            {
                int tc = Grid.XYToCell(x + c.location_x, y + c.location_y);
                reason = CellBlockReason(tc);
                if (reason != null) return false;
            }
            return true;
        }

        /// <summary>Why the start template can't cover this cell (null = fine). Shared by validation and the placement overlay.</summary>
        public static string CellBlockReason(int tc)
        {
            if (!InStartWorld(tc, 2)) return T("Too close to the edge of the asteroid");
            if (Grid.Element[tc].id == SimHashes.Unobtanium) return T("Neutronium in the way");
            for (int layer = 0; layer < (int)ObjectLayer.NumLayers; layer++)
            {
                var go = Grid.Objects[tc, layer];
                if (go == null) continue;
                if (go.GetComponent<Telepad>() != null) return T("Another Printing Pod is in the way");
                if (go.GetComponent<Geyser>() != null) return T("A geyser is in the way");
                int owner = ColonyRegistry.GetOwner(go);
                if (owner != ColonyRegistry.NoColony && go.GetComponent<Building>() != null)
                    return F("Would overwrite {0}'s buildings", ColonyRegistry.Get(owner)?.Name ?? T("another colony"));
            }
            return null;
        }

        public static HashSet<int> TemplateCells(TemplateContainer tpl, int origin)
        {
            var set = new HashSet<int>();
            Grid.CellToXY(origin, out int x, out int y);
            foreach (var c in tpl.cells) set.Add(Grid.XYToCell(x + c.location_x, y + c.location_y));
            return set;
        }

        #region Water geysers

        /// <summary>
        /// Place one water geyser in a natural-looking cave. near &gt;= 0: minDist-maxDist cells from that cell,
        /// otherwise anywhere away from colonies.
        /// </summary>
        public static bool PlaceWaterGeyser(int near, System.Random rng, int minDist = 20, int maxDist = 40)
        {
            var world = ClusterManager.Instance?.GetStartWorld();
            if (world == null) return false;
            var min = world.minimumBounds; var max = world.maximumBounds;
            int nx = 0, ny = 0;
            if (near >= 0) Grid.CellToXY(near, out nx, out ny);
            var zones = WaterVentZones(world.id);

            for (int attempt = 0; attempt < 1500; attempt++)
            {
                int x, y;
                if (near >= 0)
                {
                    double a = rng.NextDouble() * Math.PI * 2;
                    double d = minDist + rng.NextDouble() * Math.Max(0, maxDist - minDist);
                    x = nx + (int)Math.Round(Math.Cos(a) * d);
                    y = ny + (int)Math.Round(Math.Sin(a) * d * 0.8); // asteroids are wider than tall: favour sideways
                }
                else
                {
                    x = rng.Next((int)min.x + 12, (int)max.x - 12);
                    y = rng.Next((int)min.y + 12, (int)max.y - 12);
                }
                if (!Grid.IsValidCell(Grid.XYToCell(x, y))) continue;
                // only in biomes where water vents normally form
                if (zones.Count > 0 && !zones.Contains(ZoneAt(Grid.XYToCell(x, y)))) continue;

                var cave = ShapeCave(x, y, rng, out var floor);
                if (!CaveOk(x, y, near, cave, floor)) continue;

                string id = WaterGeysers[rng.Next(WaterGeysers.Length)];
                var prefab = Assets.GetPrefab(new Tag(id));
                if (prefab == null) continue;

                CarveCave(cave, floor, rng);
                int cell = Grid.XYToCell(x, y);
                var go = GameUtil.KInstantiate(prefab, Grid.CellToPosCBC(cell, Grid.SceneLayer.BuildingBack), Grid.SceneLayer.BuildingBack);
                go.SetActive(true);
                Debug.Log($"[ONIT-Colonies] Placed {id} at {x},{y} (cave {cave.Count} cells)");
                return true;
            }
            Debug.LogWarning("[ONIT-Colonies] No spot found for a water geyser");
            return false;
        }

        /// <summary>Temperate / wet biomes where the Water Geyser shows up in worldgen.</summary>
        private static readonly ProcGen.SubWorld.ZoneType[] DefaultWaterZones =
        {
            ProcGen.SubWorld.ZoneType.Sandstone, ProcGen.SubWorld.ZoneType.BoggyMarsh, ProcGen.SubWorld.ZoneType.Ocean, ProcGen.SubWorld.ZoneType.Forest,
            ProcGen.SubWorld.ZoneType.Swamp, ProcGen.SubWorld.ZoneType.KelpForest, ProcGen.SubWorld.ZoneType.Reef, ProcGen.SubWorld.ZoneType.Beach,
        };

        private static ProcGen.SubWorld.ZoneType ZoneAt(int cell)
        {
            try { return global::World.Instance.zoneRenderData.GetSubWorldZoneType(cell); }
            catch { return ProcGen.SubWorld.ZoneType.Space; }
        }

        /// <summary>
        /// Biomes to place water vents in: the biomes this world already has water vents in (that's where its worldgen
        /// put them), plus the usual temperate/wet biomes. Empty set = no biome data (place anywhere).
        /// </summary>
        private static HashSet<ProcGen.SubWorld.ZoneType> WaterVentZones(int worldId)
        {
            var set = new HashSet<ProcGen.SubWorld.ZoneType>(DefaultWaterZones);
            try
            {
                var geysers = global::Components.Geysers.GetItems(worldId);
                if (geysers != null) foreach (Geyser g in geysers)
                {
                    if (g == null || g.configuration == null) continue;
                    if (g.configuration.GetElement() != SimHashes.Water) continue;
                    var z = ZoneAt(Grid.PosToCell(g));
                    if (z != ProcGen.SubWorld.ZoneType.Space) set.Add(z);
                }
                if (global::World.Instance?.zoneRenderData == null) set.Clear();
            }
            catch { }
            return set;
        }

        /// <summary>
        /// Irregular cave around a geyser whose origin (bottom-centre) is (gx, gy): a wobbly ellipse with a rough edge,
        /// a level floor under the geyser's 4x2 footprint and room above it. floor = cells that must be solid.
        /// </summary>
        private static HashSet<int> ShapeCave(int gx, int gy, System.Random rng, out HashSet<int> floor)
        {
            var cave = new HashSet<int>();
            floor = new HashSet<int>();
            double rx = 5.5 + rng.NextDouble() * 3.0;   // 5.5..8.5 half-width
            double ry = 3.2 + rng.NextDouble() * 1.6;   // 3.2..4.8 half-height
            double cx = gx + 0.5 + (rng.NextDouble() - 0.5) * 2.0;
            double cy = gy - 0.5 + ry * (0.75 + rng.NextDouble() * 0.2); // bottom of the ellipse sits around the geyser's feet
            double p1 = rng.NextDouble() * 6.283, p2 = rng.NextDouble() * 6.283, p3 = rng.NextDouble() * 6.283;
            double a1 = 0.12 + rng.NextDouble() * 0.1, a2 = 0.06 + rng.NextDouble() * 0.08, a3 = 0.04 + rng.NextDouble() * 0.05;
            int ix0 = (int)Math.Floor(cx - rx * 1.4), ix1 = (int)Math.Ceiling(cx + rx * 1.4);
            int iy0 = gy - 2, iy1 = (int)Math.Ceiling(cy + ry * 1.4);
            for (int y = iy0; y <= iy1; y++)
            {
                for (int x = ix0; x <= ix1; x++)
                {
                    double dx = (x + 0.5 - cx) / rx, dy = (y + 0.5 - cy) / ry;
                    double th = Math.Atan2(dy, dx);
                    double r = 1.0 + a1 * Math.Sin(2 * th + p1) + a2 * Math.Sin(3 * th + p2) + a3 * Math.Sin(5 * th + p3);
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist > r) continue;
                    if (dist > r * 0.82 && rng.NextDouble() < 0.3) continue; // ragged edge
                    if (y < gy) continue;                                    // nothing below the geyser's floor line
                    cave.Add(Grid.XYToCell(x, y));
                }
            }
            // geyser footprint (x-1..x+2, 2 tall) + one row of headroom must be open
            for (int x = gx - 1; x <= gx + 2; x++)
                for (int y = gy; y <= gy + 2; y++)
                    cave.Add(Grid.XYToCell(x, y));
            // level floor under the footprint; elsewhere the cave keeps its natural bowl shape
            for (int x = gx - 2; x <= gx + 3; x++)
                floor.Add(Grid.XYToCell(x, gy - 1));
            // cave cells right above the floor line that aren't over the footprint get a floor too (no hanging pits)
            foreach (int c in cave)
            {
                int below = Grid.CellBelow(c);
                if (!cave.Contains(below)) floor.Add(below);
            }
            floor.ExceptWith(cave);
            return cave;
        }

        private static bool CaveOk(int gx, int gy, int allowNearCell, HashSet<int> cave, HashSet<int> floor)
        {
            foreach (var set in new[] { cave, floor })
            {
                foreach (int c in set)
                {
                    if (!InStartWorld(c, 4)) return false;
                    if (Grid.Element[c].id == SimHashes.Unobtanium) return false;
                    for (int layer = 0; layer < (int)ObjectLayer.NumLayers; layer++)
                    {
                        if (layer == (int)ObjectLayer.Pickupables) continue; // loose debris is fine
                        if (Grid.Objects[c, layer] != null) return false;   // buildings, plants, pipes, tiles, other geysers
                    }
                }
            }
            // keep the open cave mostly underground: don't punch into big open areas or space
            int open = 0;
            foreach (int c in cave) if (!Grid.Solid[c] && !Grid.IsLiquid(c)) open++;
            if (open > cave.Count / 2) return false;

            foreach (var col in ColonyRegistry.Colonies)
            {
                if (!Grid.IsValidCell(col.HomeCell) || col.HomeCell == allowNearCell) continue;
                Grid.CellToXY(col.HomeCell, out int cx, out int cy);
                int r = Math.Max(16, ColonyRegistry.TerritoryRadius + 6);
                if ((cx - gx) * (cx - gx) + (cy - gy) * (cy - gy) < r * r) return false;
            }
            if (Grid.IsValidCell(allowNearCell))
            {
                // not inside the host's own base either
                Grid.CellToXY(allowNearCell, out int hx, out int hy);
                if ((hx - gx) * (hx - gx) + (hy - gy) * (hy - gy) < 16 * 16) return false;
            }
            var startWorld = ClusterManager.Instance?.GetStartWorld();
            var geysers = startWorld != null ? global::Components.Geysers.GetItems(startWorld.id) : null;
            if (geysers != null) foreach (Geyser g in geysers)
            {
                if (g == null) continue;
                Grid.CellToXY(Grid.PosToCell(g), out int ex, out int ey);
                if (Math.Abs(ex - gx) < 16 && Math.Abs(ey - gy) < 12) return false;
            }
            return true;
        }

        /// <summary>Replace the cave with the surrounding gas and give it a floor of the surrounding rock, at the local temperature.</summary>
        private static void CarveCave(HashSet<int> cave, HashSet<int> floor, System.Random rng)
        {
            // look at a ring around the cave to copy what nature put there
            var ring = new HashSet<int>();
            foreach (int c in cave)
                foreach (int n in new[] { Grid.CellLeft(c), Grid.CellRight(c), Grid.CellAbove(c), Grid.CellBelow(c),
                                          Grid.OffsetCell(c, 2, 0), Grid.OffsetCell(c, -2, 0), Grid.OffsetCell(c, 0, 2), Grid.OffsetCell(c, 0, -2) })
                    if (Grid.IsValidCell(n) && !cave.Contains(n)) ring.Add(n);

            var gasCount = new Dictionary<SimHashes, int>();
            var solidCount = new Dictionary<SimHashes, int>();
            var solidMass = new Dictionary<SimHashes, float>();
            float tSum = 0f; int tN = 0;
            foreach (int c in ring)
            {
                var e = Grid.Element[c];
                if (e == null) continue;
                if (Grid.Mass[c] > 0f) { tSum += Grid.Temperature[c]; tN++; }
                if (e.IsGas && e.id != SimHashes.Vacuum) gasCount[e.id] = (gasCount.TryGetValue(e.id, out int g) ? g : 0) + 1;
                else if (e.IsSolid && e.id != SimHashes.Unobtanium)
                {
                    solidCount[e.id] = (solidCount.TryGetValue(e.id, out int s) ? s : 0) + 1;
                    solidMass[e.id] = (solidMass.TryGetValue(e.id, out float m) ? m : 0f) + Grid.Mass[c];
                }
            }
            SimHashes gas = SimHashes.Oxygen, rock = SimHashes.SandStone;
            int best = 0;
            foreach (var kv in gasCount) if (kv.Value > best) { best = kv.Value; gas = kv.Key; }
            best = 0;
            foreach (var kv in solidCount) if (kv.Value > best) { best = kv.Value; rock = kv.Key; }
            float rockMass = solidCount.TryGetValue(rock, out int rc) && rc > 0 ? Mathf.Clamp(solidMass[rock] / rc, 200f, 2000f) : 1000f;
            float temp = tN > 0 ? Mathf.Clamp(tSum / tN, 250f, 330f) : 300f;

            foreach (int c in cave)
            {
                float mass = 0.8f + (float)rng.NextDouble() * 0.9f;
                SimMessages.ReplaceElement(c, gas, CellEventLogger.Instance.TemplateLoader, mass, temp);
            }
            foreach (int c in floor)
            {
                if (Grid.Solid[c]) continue; // keep the natural rock that is already there
                SimMessages.ReplaceElement(c, rock, CellEventLogger.Instance.TemplateLoader, rockMass * (0.85f + (float)rng.NextDouble() * 0.3f), temp);
            }
        }

        /// <summary>Host / single player, freshly generated world: add the configured number of water geysers once.</summary>
        public static void AddExtraWaterIfNew()
        {
            if (ColonySaveData.ExtraWaterDone) return;
            ColonySaveData.ExtraWaterDone = true;
            int n = Mathf.Clamp(Configuration.Instance?.Colonies?.ExtraWaterGeysers ?? 0, 0, 12);
            if (n <= 0 || GameClock.Instance == null || GameClock.Instance.GetCycle() > 0) return; // only brand-new worlds
            var rng = new System.Random(Grid.WidthInCells * 7919 + Grid.HeightInCells);
            int placed = 0;
            for (int i = 0; i < n; i++) if (PlaceWaterGeyser(-1, rng)) placed++;
            Debug.Log($"[ONIT-Colonies] Extra water: placed {placed}/{n} geysers");
        }

        /// <summary>
        /// Host / single player: make sure the configured number of water geysers sits near the host's base.
        /// Works on existing saves too; the count placed is stored in the save so it never stacks up on reload.
        /// </summary>
        public static void AddHostWaterIfNeeded()
        {
            int want = Mathf.Clamp(Configuration.Instance?.Colonies?.HostWaterGeysers ?? 0, 0, 4);
            if (ColonySaveData.HostWaterPlaced >= want) return;
            int home = HostHomeCell();
            if (!Grid.IsValidCell(home)) { Debug.LogWarning("[ONIT-Colonies] Host water: no host Printing Pod found"); return; }
            var rng = new System.Random(home * 131 + ColonySaveData.HostWaterPlaced * 17 + Grid.WidthInCells);
            int placed = 0;
            while (ColonySaveData.HostWaterPlaced < want)
            {
                int dmin = Mathf.Clamp(Configuration.Instance?.Colonies?.HostWaterDistance ?? 45, 20, 150);
                if (!PlaceWaterGeyser(home, rng, dmin, dmin + 25)) break;
                ColonySaveData.HostWaterPlaced++;
                placed++;
            }
            Debug.Log($"[ONIT-Colonies] Host water: placed {placed}, total {ColonySaveData.HostWaterPlaced}/{want}");
            if (placed > 0 && ColonyNet.InSession) ColonySession.ScheduleHardSync(3f, "water geyser near host base");
        }

        /// <summary>The host's colony home, else the first colony's, else the start world's Printing Pod.</summary>
        private static int HostHomeCell()
        {
            if (ColonyRegistry.Enabled)
            {
                var mine = ColonyRegistry.Get(ColonyRegistry.GetColonyOfPlayer(ColonyRegistry.LocalPlayerId));
                if (mine != null && Grid.IsValidCell(mine.HomeCell)) return mine.HomeCell;
                ColonyInfo first = null;
                foreach (var c in ColonyRegistry.Colonies)
                    if (Grid.IsValidCell(c.HomeCell) && (first == null || c.Id < first.Id)) first = c;
                if (first != null) return first.HomeCell;
            }
            var start = ClusterManager.Instance?.GetStartWorld();
            foreach (Telepad pad in global::Components.Telepads.Items)
            {
                if (pad == null) continue;
                if (start != null && pad.GetMyWorldId() != start.id) continue;
                return Grid.PosToCell(pad);
            }
            return -1;
        }

        #endregion
    }
}
