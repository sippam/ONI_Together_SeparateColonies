using System;
using System.Collections.Generic;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// Preview drawn while a player is choosing where to found their colony:
    ///  - every cell the start-base template will cover, coloured by what happens to it
    ///    (green free, yellow cleared, magenta unowned building left in place, red blocked)
    ///  - outlines of the template's buildings (Printing Pod highlighted)
    ///  - the new colony's territory circle and the keep-out ring around every existing colony
    /// Client-side only (reads local grid state), recomputed when the hovered cell changes or every 0.5 s.
    /// </summary>
    public static class ColonyPlacementOverlay
    {
        private enum CellState : byte { Free, Cleared, KeptBuilding, Blocked }

        private struct CellInfo { public int Dx, Dy; public CellState State; }
        private struct Footprint { public int MinX, MinY, MaxX, MaxY; public string Name; public bool IsPod; }

        private static readonly List<CellInfo> _cells = new List<CellInfo>();
        private static readonly List<Footprint> _buildings = new List<Footprint>();
        private static int _origin = -1;
        private static float _nextRefresh;
        private static int _nFree, _nCleared, _nKept, _nBlocked;
        private static string _firstBlock;
        private static bool _usingTemplate;

        private static readonly Color ColFree = new Color(0.2f, 0.95f, 0.3f, 0.28f);
        private static readonly Color ColCleared = new Color(1f, 0.85f, 0.2f, 0.30f);
        private static readonly Color ColKept = new Color(0.95f, 0.3f, 0.95f, 0.40f);
        private static readonly Color ColBlocked = new Color(1f, 0.15f, 0.1f, 0.55f);
        private static readonly Color ColOutline = new Color(1f, 1f, 1f, 0.85f);
        private static readonly Color ColPod = new Color(0.3f, 0.9f, 1f, 1f);
        private static readonly Color ColKeepOut = new Color(1f, 0.3f, 0.25f, 0.8f);

        private static GUIStyle _label;

        public static void Reset() { _origin = -1; _cells.Clear(); _buildings.Clear(); }

        /// <summary>Call every frame while placing.</summary>
        public static void Update(int origin)
        {
            if (origin == _origin && Time.unscaledTime < _nextRefresh) return;
            _origin = origin;
            _nextRefresh = Time.unscaledTime + 0.5f;
            try { Recompute(origin); }
            catch (Exception ex)
            {
                _cells.Clear(); _buildings.Clear();
                Debug.LogWarning($"[ONIT-Colonies] placement overlay: {ex.Message}");
            }
        }

        private static void Recompute(int origin)
        {
            _cells.Clear(); _buildings.Clear();
            _nFree = _nCleared = _nKept = _nBlocked = 0;
            _firstBlock = null;
            if (!Grid.IsValidCell(origin)) return;
            Grid.CellToXY(origin, out int ox, out int oy);

            var tpl = ColonyWorldTools.GetStartTemplate();
            _usingTemplate = tpl != null;
            if (tpl == null)
            {
                // no start template: only the Printing Pod gets placed
                var hq = Assets.GetBuildingDef(ColonyFounder.HeadquartersId);
                if (hq != null) AddFootprint(hq, 0, 0, true);
                return;
            }

            var minionCells = new HashSet<int>();
            foreach (MinionIdentity mi in global::Components.LiveMinionIdentities.Items)
            {
                if (mi == null) continue;
                int mc = Grid.PosToCell(mi);
                minionCells.Add(mc);
                minionCells.Add(Grid.CellAbove(mc));
            }

            foreach (var c in tpl.cells)
            {
                int x = ox + c.location_x, y = oy + c.location_y;
                var info = new CellInfo { Dx = c.location_x, Dy = c.location_y };
                if (x < 0 || y < 0 || x >= Grid.WidthInCells || y >= Grid.HeightInCells)
                {
                    info.State = CellState.Blocked;
                    _firstBlock = _firstBlock ?? "Too close to the edge of the asteroid";
                }
                else
                {
                    int tc = Grid.XYToCell(x, y);
                    string block = ColonyWorldTools.CellBlockReason(tc);
                    if (block == null && minionCells.Contains(tc)) block = "A duplicant is standing there";
                    if (block != null) { info.State = CellState.Blocked; _firstBlock = _firstBlock ?? block; }
                    else info.State = Classify(tc);
                }
                switch (info.State)
                {
                    case CellState.Free: _nFree++; break;
                    case CellState.Cleared: _nCleared++; break;
                    case CellState.KeptBuilding: _nKept++; break;
                    default: _nBlocked++; break;
                }
                _cells.Add(info);
            }

            if (tpl.buildings != null)
            {
                foreach (var b in tpl.buildings)
                {
                    if (b == null || string.IsNullOrEmpty(b.id)) continue;
                    var def = Assets.GetBuildingDef(b.id);
                    if (def == null) continue;
                    AddFootprint(def, b.location_x, b.location_y, b.id == ColonyFounder.HeadquartersId);
                }
            }
        }

        private static CellState Classify(int tc)
        {
            for (int layer = 0; layer < (int)ObjectLayer.NumLayers; layer++)
            {
                var go = Grid.Objects[tc, layer];
                if (go == null) continue;
                // the stamp does not remove buildings (only plants, critters, geysers, debris)
                if (go.GetComponent<Building>() != null && go.GetComponent<Crop>() == null) return CellState.KeptBuilding;
            }
            var e = Grid.Element[tc];
            if (Grid.Solid[tc] || (e != null && e.IsLiquid)) return CellState.Cleared;
            for (int layer = 0; layer < (int)ObjectLayer.NumLayers; layer++)
            {
                var go = Grid.Objects[tc, layer];
                if (go != null && (go.GetComponent<Crop>() != null || go.GetComponent<Uprootable>() != null)) return CellState.Cleared;
            }
            return CellState.Free;
        }

        private static void AddFootprint(BuildingDef def, int bx, int by, bool pod)
        {
            var fp = new Footprint { MinX = int.MaxValue, MinY = int.MaxValue, MaxX = int.MinValue, MaxY = int.MinValue, IsPod = pod };
            var offs = def.PlacementOffsets;
            if (offs == null || offs.Length == 0) offs = new[] { new CellOffset(0, 0) };
            foreach (var o in offs)
            {
                fp.MinX = Math.Min(fp.MinX, bx + o.x); fp.MaxX = Math.Max(fp.MaxX, bx + o.x);
                fp.MinY = Math.Min(fp.MinY, by + o.y); fp.MaxY = Math.Max(fp.MaxY, by + o.y);
            }
            try { fp.Name = global::STRINGS.UI.StripLinkFormatting(def.Name); } catch { fp.Name = def.PrefabID; }
            _buildings.Add(fp);
        }

        /// <summary>Lines for the cursor tooltip.</summary>
        public static string Summary
        {
            get
            {
                if (!_usingTemplate) return null;
                string s = $"Base area: {_cells.Count} cells — free {_nFree}, cleared {_nCleared}";
                if (_nKept > 0) s += $", buildings kept {_nKept}";
                if (_nBlocked > 0) s += $", BLOCKED {_nBlocked}";
                return s;
            }
        }

        public static string Legend => "Green free · Yellow dug out / plants removed · Magenta existing building stays · Red blocked · Cyan Printing Pod";

        /// <summary>Call from OnGUI (Repaint only).</summary>
        public static void Draw(int origin, bool valid)
        {
            var cam = Camera.main;
            if (cam == null || !Grid.IsValidCell(origin)) return;
            Vector3 p0 = cam.WorldToScreenPoint(Grid.CellToPos(origin));
            Vector3 p1 = cam.WorldToScreenPoint(Grid.CellToPos(origin) + new Vector3(Grid.CellSizeInMeters, 0f, 0f));
            float px = p1.x - p0.x;
            if (px <= 0.3f) return;
            float baseX = p0.x, baseY = Screen.height - p0.y; // screen-space top-left of the origin cell's bottom edge
            Rect CellRect(int dx, int dy) => new Rect(baseX + dx * px, baseY - (dy + 1) * px, px, px);

            var prev = GUI.color;
            var tex = Texture2D.whiteTexture;

            // keep-out rings around existing colonies + the new colony's territory
            int minDist = ColonyFounder.MinColonyDistance;
            foreach (var c in ColonyRegistry.Colonies)
                if (Grid.IsValidCell(c.HomeCell)) DottedCircle(cam, c.HomeCell, minDist, ColKeepOut);
            if (ColonyRules.TerritoryProtection && ColonyRegistry.TerritoryRadius > 0)
                DottedCircle(cam, origin, ColonyRegistry.TerritoryRadius, valid ? new Color(1f, 1f, 1f, 0.7f) : new Color(1f, 0.5f, 0.4f, 0.7f));

            if (_usingTemplate)
            {
                bool gap = px >= 6f; // leave a 1px grid line between cells when zoomed in
                foreach (var ci in _cells)
                {
                    switch (ci.State)
                    {
                        case CellState.Free: GUI.color = ColFree; break;
                        case CellState.Cleared: GUI.color = ColCleared; break;
                        case CellState.KeptBuilding: GUI.color = ColKept; break;
                        default: GUI.color = ColBlocked; break;
                    }
                    var r = CellRect(ci.Dx, ci.Dy);
                    if (gap) { r.xMin += 0.5f; r.yMin += 0.5f; r.xMax -= 0.5f; r.yMax -= 0.5f; }
                    GUI.DrawTexture(r, tex);
                }
            }

            if (_label == null)
            {
                _label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 11, wordWrap = true };
                _label.normal.textColor = Color.white;
            }
            foreach (var b in _buildings)
            {
                var r = Rect.MinMaxRect(baseX + b.MinX * px, baseY - (b.MaxY + 1) * px, baseX + (b.MaxX + 1) * px, baseY - b.MinY * px);
                Color col = b.IsPod ? (valid ? ColPod : ColBlocked) : ColOutline;
                if (b.IsPod && !_usingTemplate) { GUI.color = new Color(col.r, col.g, col.b, 0.3f); GUI.DrawTexture(r, tex); }
                Outline(r, col, b.IsPod ? 2f : 1f);
                if (b.IsPod || (px >= 12f && r.width >= 40f))
                {
                    GUI.color = Color.white;
                    var lr = b.IsPod ? new Rect(r.x - 40, r.y - 18, r.width + 80, 16) : r;
                    GUI.Label(lr, b.IsPod ? "Printing Pod" : b.Name, _label);
                }
            }
            GUI.color = prev;
        }

        private static void Outline(Rect r, Color c, float w)
        {
            GUI.color = c;
            var t = Texture2D.whiteTexture;
            GUI.DrawTexture(new Rect(r.xMin, r.yMin, r.width, w), t);
            GUI.DrawTexture(new Rect(r.xMin, r.yMax - w, r.width, w), t);
            GUI.DrawTexture(new Rect(r.xMin, r.yMin, w, r.height), t);
            GUI.DrawTexture(new Rect(r.xMax - w, r.yMin, w, r.height), t);
        }

        private static void DottedCircle(Camera cam, int centerCell, int radius, Color color)
        {
            if (radius <= 0) return;
            GUI.color = color;
            Vector3 center = Grid.CellToPosCCC(centerCell, Grid.SceneLayer.Front);
            int points = Mathf.Clamp(radius * 4, 48, 160);
            for (int i = 0; i < points; i++)
            {
                float a = i * Mathf.PI * 2f / points;
                Vector3 sp = cam.WorldToScreenPoint(center + new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * radius);
                if (sp.z < 0f || sp.x < -10 || sp.y < -10 || sp.x > Screen.width + 10 || sp.y > Screen.height + 10) continue;
                GUI.DrawTexture(new Rect(sp.x - 2f, Screen.height - sp.y - 2f, 4f, 4f), Texture2D.whiteTexture);
            }
        }
    }
}
