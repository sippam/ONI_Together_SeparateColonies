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

        #region Steam vents (worldgen POI templates)

        /// <summary>
        /// The game's own worldgen POIs that contain a Cool Steam Vent (GeyserGeneric_steam), with the biome worldgen
        /// is allowed to put them in (worldTemplateRules in worldgen/worlds/*.yaml: GuaranteeOne, zoneTypes filter).
        /// Stamping the template copies the whole structure: rock shell, cave, plants and the vent itself.
        /// </summary>
        private static readonly (string Template, ProcGen.SubWorld.ZoneType Zone)[] SteamVentPois =
        {
            ("poi/jungle/geyser_steam", ProcGen.SubWorld.ZoneType.ToxicJungle),
            ("poi/hotmarsh/geyser_steam", ProcGen.SubWorld.ZoneType.BoggyMarsh),
        };

        private const int VentMinDistFromColony = 30;

        private static ProcGen.SubWorld.ZoneType ZoneAt(int cell)
        {
            try { return global::World.Instance.zoneRenderData.GetSubWorldZoneType(cell); }
            catch { return ProcGen.SubWorld.ZoneType.Space; }
        }

        private static TemplateContainer LoadTemplate(string name)
        {
            try
            {
                if (!TemplateCache.Initted) TemplateCache.Init();
                return TemplateCache.TemplateExists(name) ? TemplateCache.GetTemplate(name) : null;
            }
            catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] template {name}: {ex.Message}"); return null; }
        }

        /// <summary>
        /// Stamp one steam-vent POI where worldgen would have put it (its biome), away from every colony.
        /// near &gt;= 0: of all valid spots pick the one closest to that cell (the host's base); otherwise a random one.
        /// onDone runs when the stamp has finished (needs the game unpaused).
        /// </summary>
        public static bool PlaceSteamVent(int near, System.Random rng, System.Action onDone)
        {
            var world = ClusterManager.Instance?.GetStartWorld();
            if (world == null) return false;
            var min = world.minimumBounds; var max = world.maximumBounds;

            var pois = new List<(TemplateContainer Tpl, ProcGen.SubWorld.ZoneType Zone, string Name)>();
            foreach (var p in SteamVentPois)
            {
                var t = LoadTemplate(p.Template);
                if (t?.cells != null && t.cells.Count > 0) pois.Add((t, p.Zone, p.Template));
            }
            if (pois.Count == 0) { Debug.LogWarning("[ONIT-Colonies] steam vent templates not found"); return false; }

            int nx = 0, ny = 0;
            if (near >= 0) Grid.CellToXY(near, out nx, out ny);
            var found = new List<(int X, int Y, int P)>();
            for (int attempt = 0; attempt < 4000 && found.Count < 40; attempt++)
            {
                int x = rng.Next((int)min.x + 8, (int)max.x - 8);
                int y = rng.Next((int)min.y + 8, (int)max.y - 8);
                int cell = Grid.XYToCell(x, y);
                if (!Grid.IsValidCell(cell)) continue;
                var zone = ZoneAt(cell);
                int pi = pois.FindIndex(p => p.Zone == zone);
                if (pi < 0) continue;
                if (!PoiFits(pois[pi].Tpl, x, y, pois[pi].Zone)) continue;
                found.Add((x, y, pi));
            }
            if (found.Count == 0)
            {
                Debug.LogWarning("[ONIT-Colonies] No jungle/marsh spot found for a steam vent (this asteroid may not have those biomes)");
                return false;
            }
            var pick = found[rng.Next(found.Count)];
            if (near >= 0)
            {
                long best = long.MaxValue;
                foreach (var f in found)
                {
                    long d = (long)(f.X - nx) * (f.X - nx) + (long)(f.Y - ny) * (f.Y - ny);
                    if (d < best) { best = d; pick = f; }
                }
            }
            var poi = pois[pick.P];
            Debug.Log($"[ONIT-Colonies] Stamping {poi.Name} at {pick.X},{pick.Y}");
            TemplateLoader.Stamp(poi.Tpl, new Vector2(pick.X, pick.Y), () =>
            {
                Debug.Log($"[ONIT-Colonies] Steam vent POI done at {pick.X},{pick.Y}");
                try { onDone?.Invoke(); } catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] steam vent callback: {ex.Message}"); }
            });
            return true;
        }

        /// <summary>Would the POI stamped at (x,y) stay in its biome and not wreck anything?</summary>
        private static bool PoiFits(TemplateContainer tpl, int x, int y, ProcGen.SubWorld.ZoneType zone)
        {
            int inZone = 0;
            foreach (var c in tpl.cells)
            {
                int tc = Grid.XYToCell(x + c.location_x, y + c.location_y);
                if (!InStartWorld(tc, 3)) return false;
                if (Grid.Element[tc].id == SimHashes.Unobtanium) return false;
                if (ZoneAt(tc) == zone) inZone++;
                for (int layer = 0; layer < (int)ObjectLayer.NumLayers; layer++)
                {
                    if (layer == (int)ObjectLayer.Pickupables) continue;
                    var go = Grid.Objects[tc, layer];
                    if (go == null) continue;
                    // anything built, any geyser / pod: keep away (wild plants are fine, the POI brings its own)
                    if (go.GetComponent<Building>() != null || go.GetComponent<Geyser>() != null || go.GetComponent<Telepad>() != null) return false;
                }
            }
            if (inZone < tpl.cells.Count * 0.7f) return false; // mostly inside the biome, like worldgen

            foreach (MinionIdentity mi in global::Components.LiveMinionIdentities.Items)
            {
                if (mi == null) continue;
                Grid.CellToXY(Grid.PosToCell(mi), out int mx, out int my);
                if (Math.Abs(mx - x) < 20 && Math.Abs(my - y) < 15) return false;
            }
            foreach (var col in ColonyRegistry.Colonies)
            {
                if (!Grid.IsValidCell(col.HomeCell)) continue;
                Grid.CellToXY(col.HomeCell, out int cx, out int cy);
                int r = Math.Max(VentMinDistFromColony, ColonyRegistry.TerritoryRadius + 12);
                if ((cx - x) * (cx - x) + (cy - y) * (cy - y) < r * r) return false;
            }
            var startWorld = ClusterManager.Instance?.GetStartWorld();
            if (startWorld != null && ColonyRegistry.Colonies.Count == 0)
            {
                // no colonies registered (mode off / single player): keep away from every Printing Pod
                foreach (Telepad pad in global::Components.Telepads.Items)
                {
                    if (pad == null) continue;
                    Grid.CellToXY(Grid.PosToCell(pad), out int px, out int py);
                    if ((px - x) * (px - x) + (py - y) * (py - y) < VentMinDistFromColony * VentMinDistFromColony) return false;
                }
            }
            var geysers = startWorld != null ? global::Components.Geysers.GetItems(startWorld.id) : null;
            if (geysers != null) foreach (Geyser g in geysers)
            {
                if (g == null) continue;
                Grid.CellToXY(Grid.PosToCell(g), out int gx, out int gy);
                if (Math.Abs(gx - x) < 22 && Math.Abs(gy - y) < 16) return false;
            }
            return true;
        }

        /// <summary>Host / single player, freshly generated world: add the configured number of steam vents once.</summary>
        public static void AddExtraWaterIfNew()
        {
            if (ColonySaveData.ExtraWaterDone) return;
            ColonySaveData.ExtraWaterDone = true;
            int n = Mathf.Clamp(Configuration.Instance?.Colonies?.ExtraWaterGeysers ?? 0, 0, 12);
            if (n <= 0 || GameClock.Instance == null || GameClock.Instance.GetCycle() > 0) return; // only brand-new worlds
            var rng = new System.Random(Grid.WidthInCells * 7919 + Grid.HeightInCells);
            int placed = 0;
            for (int i = 0; i < n; i++) if (PlaceSteamVent(-1, rng, null)) placed++;
            Debug.Log($"[ONIT-Colonies] Extra water: stamping {placed}/{n} steam vents");
        }

        /// <summary>
        /// Host / single player: make sure the configured number of steam vents exists for the host — in the nearest
        /// jungle / marsh area, where worldgen puts them. Works on existing saves; the count is stored in the save.
        /// </summary>
        public static void AddHostWaterIfNeeded()
        {
            int want = Mathf.Clamp(Configuration.Instance?.Colonies?.HostWaterGeysers ?? 0, 0, 4);
            if (ColonySaveData.HostWaterPlaced >= want) return;
            int home = HostHomeCell();
            if (!Grid.IsValidCell(home)) { Debug.LogWarning("[ONIT-Colonies] Host water: no host Printing Pod found"); return; }
            var rng = new System.Random(home * 131 + ColonySaveData.HostWaterPlaced * 17 + Grid.WidthInCells);
            int started = 0;
            while (ColonySaveData.HostWaterPlaced < want)
            {
                // count it right away so a reload can't start a second stamp for the same slot
                if (!PlaceSteamVent(home, rng, () =>
                    {
                        if (ColonyNet.InSession) ColonySession.ScheduleHardSync(3f, "steam vent for the host");
                    })) break;
                ColonySaveData.HostWaterPlaced++;
                started++;
            }
            Debug.Log($"[ONIT-Colonies] Host water: stamping {started}, total {ColonySaveData.HostWaterPlaced}/{want}");
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
