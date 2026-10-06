using System;
using System.Collections.Generic;
using UnityEngine;

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
        public static readonly string[] WaterGeysers = { "GeyserGeneric_hot_water", "GeyserGeneric_slush_water", "GeyserGeneric_steam", "GeyserGeneric_salt_water", "GeyserGeneric_filthy_water" };

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
                if (area.Contains(mc) || area.Contains(Grid.CellAbove(mc))) { reason = "A duplicant is standing there"; return false; }
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
            if (!InStartWorld(tc, 2)) return "Too close to the edge of the asteroid";
            if (Grid.Element[tc].id == SimHashes.Unobtanium) return "Neutronium in the way";
            for (int layer = 0; layer < (int)ObjectLayer.NumLayers; layer++)
            {
                var go = Grid.Objects[tc, layer];
                if (go == null) continue;
                if (go.GetComponent<Telepad>() != null) return "Another Printing Pod is in the way";
                if (go.GetComponent<Geyser>() != null) return "A geyser is in the way";
                int owner = ColonyRegistry.GetOwner(go);
                if (owner != ColonyRegistry.NoColony && go.GetComponent<Building>() != null)
                    return $"Would overwrite {ColonyRegistry.Get(owner)?.Name ?? "another colony"}'s buildings";
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

        /// <summary>Place one water geyser. near &gt;= 0: 20-40 cells from that cell, otherwise anywhere away from colonies.</summary>
        public static bool PlaceWaterGeyser(int near, System.Random rng)
        {
            var world = ClusterManager.Instance?.GetStartWorld();
            if (world == null) return false;
            var min = world.minimumBounds; var max = world.maximumBounds;
            int nx = 0, ny = 0;
            if (near >= 0) Grid.CellToXY(near, out nx, out ny);

            for (int attempt = 0; attempt < 400; attempt++)
            {
                int x, y;
                if (near >= 0)
                {
                    double a = rng.NextDouble() * Math.PI * 2;
                    double d = 20 + rng.NextDouble() * 20;
                    x = nx + (int)Math.Round(Math.Cos(a) * d);
                    y = ny + (int)Math.Round(Math.Sin(a) * d);
                }
                else
                {
                    x = rng.Next((int)min.x + 10, (int)max.x - 10);
                    y = rng.Next((int)min.y + 10, (int)max.y - 10);
                }
                int cell = Grid.XYToCell(x, y);
                if (!AreaOk(x, y, near)) continue;

                string id = WaterGeysers[rng.Next(WaterGeysers.Length)];
                var prefab = Assets.GetPrefab(new Tag(id));
                if (prefab == null) continue;

                // pocket of breathable air with a sandstone floor, like a worldgen geyser cave
                for (int dx = -3; dx <= 4; dx++)
                {
                    for (int dy = 0; dy <= 4; dy++)
                    {
                        int c = Grid.XYToCell(x + dx, y + dy);
                        SimMessages.ReplaceElement(c, SimHashes.Oxygen, CellEventLogger.Instance.TemplateLoader, 1f, 300f);
                    }
                    int floor = Grid.XYToCell(x + dx, y - 1);
                    if (!Grid.Solid[floor])
                        SimMessages.ReplaceElement(floor, SimHashes.SandStone, CellEventLogger.Instance.TemplateLoader, 1000f, 300f);
                }
                var go = GameUtil.KInstantiate(prefab, Grid.CellToPosCBC(cell, Grid.SceneLayer.BuildingBack), Grid.SceneLayer.BuildingBack);
                go.SetActive(true);
                Debug.Log($"[ONIT-Colonies] Placed {id} at {x},{y}");
                return true;
            }
            Debug.LogWarning("[ONIT-Colonies] No spot found for a water geyser");
            return false;
        }

        private static bool AreaOk(int x, int y, int allowNearCell)
        {
            int ownHome = allowNearCell;
            for (int dx = -4; dx <= 5; dx++)
            {
                for (int dy = -1; dy <= 5; dy++)
                {
                    int c = Grid.XYToCell(x + dx, y + dy);
                    if (!InStartWorld(c, 4)) return false;
                    if (Grid.Element[c].id == SimHashes.Unobtanium) return false;
                    if (Grid.Objects[c, (int)ObjectLayer.Building] != null || Grid.Objects[c, (int)ObjectLayer.FoundationTile] != null) return false;
                }
            }
            int here = Grid.XYToCell(x, y);
            Grid.CellToXY(here, out int hx, out int hy);
            foreach (var col in ColonyRegistry.Colonies)
            {
                if (!Grid.IsValidCell(col.HomeCell) || col.HomeCell == ownHome) continue;
                Grid.CellToXY(col.HomeCell, out int cx, out int cy);
                int r = Math.Max(16, ColonyRegistry.TerritoryRadius + 6);
                if ((cx - hx) * (cx - hx) + (cy - hy) * (cy - hy) < r * r) return false;
            }
            var startWorld = ClusterManager.Instance?.GetStartWorld();
            var geysers = startWorld != null ? global::Components.Geysers.GetItems(startWorld.id) : null;
            if (geysers != null) foreach (Geyser g in geysers)
            {
                if (g == null) continue;
                Grid.CellToXY(Grid.PosToCell(g), out int gx, out int gy);
                if (Math.Abs(gx - hx) < 12 && Math.Abs(gy - hy) < 10) return false;
            }
            return true;
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

        #endregion
    }
}
