using System.Linq;
using ONI_Together.Networking;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// In-game panel for separate colonies (IMGUI, toggle with Ctrl+Shift+C or the "Colonies" button):
    /// colony list, "Found my colony" placement, territory overlay, host tools.
    /// </summary>
    public class ColonyPanel : MonoBehaviour
    {
        // F1-F11 are overlay hotkeys in ONI, so use a chord nobody binds
        private static bool TogglePressed =>
            (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) &&
            (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) &&
            Input.GetKeyDown(KeyCode.C);

        private bool _visible;
        private global::Game _seenGame;
        private bool _autoOpened;
        private bool _showTerritory = true;
        private bool _showHoverOwner = true;
        private Rect _window = new Rect(20, 120, 380, 460);
        private Vector2 _scroll;

        // placement mode
        private bool _placing;
        private string _placeReason;
        private int _hoverCell = -1;
        private string _colonyName = "";
        private string _status = "";

        private void Awake() => ColonyMessages.OnMessage += OnMessage;
        private void OnDestroy() => ColonyMessages.OnMessage -= OnMessage;

        private void OnMessage(string m)
        {
            _status = m;
            if (ColonyRegistry.Enabled) _visible = true;
        }

        private static bool InGame => global::Game.Instance != null && MultiplayerSession.InSession;

        private void Update()
        {
            if (!InGame) { _placing = false; _autoOpened = false; return; }
            if (global::Game.Instance != _seenGame) { _seenGame = global::Game.Instance; _autoOpened = false; }
            if (TogglePressed) _visible = !_visible;

            // auto-open once per world for a player who still has to found a colony
            if (!_autoOpened && ColonyRegistry.Active && ColonyRegistry.LocalColonyId == ColonyRegistry.NoColony)
            {
                _autoOpened = true;
                _visible = true;
            }

            if (_placing) UpdatePlacement();
        }

        private void UpdatePlacement()
        {
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1))
            {
                _placing = false;
                _status = "Placement cancelled";
                return;
            }
            Vector3 pos = PlayerController.GetCursorPos(KInputManager.GetMousePos());
            _hoverCell = Grid.PosToCell(pos);
            ColonyFounder.ValidateHomeCell(_hoverCell, out _placeReason);

            Vector2 mouse = Input.mousePosition;
            mouse.y = Screen.height - mouse.y;
            bool overWindow = _visible && _window.Contains(mouse);
            if (Input.GetMouseButtonDown(0) && !overWindow)
            {
                if (ColonyFounder.RequestFound(_hoverCell, _colonyName, out string reason))
                {
                    _status = MultiplayerSession.IsHost ? "Founding..." : "Request sent — waiting for host...";
                    _placing = false;
                }
                else _status = reason;
            }
        }

        private void OnGUI()
        {
            if (!InGame) return;

            if (ColonyRegistry.Active)
            {
                if (_showTerritory && Event.current.type == EventType.Repaint) DrawTerritories();
                if (_showHoverOwner && !_placing) DrawHoverOwner();
            }

            // spectators can't dig/build: make that impossible to miss
            if (ColonyRegistry.Active && ColonyRegistry.LocalColonyId == ColonyRegistry.NoColony && !_placing)
            {
                var bs = new GUIStyle(GUI.skin.box) { fontSize = 15, alignment = TextAnchor.MiddleCenter, wordWrap = true };
                bs.normal.textColor = new Color(1f, 0.85f, 0.3f);
                var r = new Rect(Screen.width / 2f - 260, 70, 520, 46);
                GUI.Box(r, "You are SPECTATING (no colony yet) — orders are ignored.\nOpen Colonies (Ctrl+Shift+C) → \"Found my colony\" to start your own base.", bs);
                if (Event.current.type == EventType.MouseDown && r.Contains(Event.current.mousePosition)) { _visible = true; Event.current.Use(); }
            }

            if (_placing)
            {
                Vector2 m = Input.mousePosition;
                string txt = _placeReason == null
                    ? "Click to place your Printing Pod here (Esc/right-click cancels)"
                    : $"Can't place here: {_placeReason}";
                var style = new GUIStyle(GUI.skin.box) { wordWrap = true };
                style.normal.textColor = _placeReason == null ? Color.green : new Color(1f, 0.5f, 0.4f);
                GUI.Box(new Rect(m.x + 18, Screen.height - m.y + 10, 300, 40), txt, style);
            }

            if (!_visible)
            {
                if (GUI.Button(new Rect(Screen.width - 110, 60, 90, 24), "Colonies"))
                    _visible = true;
                return;
            }
            _window = GUI.Window(0x4A6C01, _window, DrawWindow, "Separate colonies (Ctrl+Shift+C)");
        }

        private void DrawWindow(int id)
        {
            if (GUI.Button(new Rect(_window.width - 24, 2, 20, 16), "x")) _visible = false;
            _scroll = GUILayout.BeginScrollView(_scroll);

            _showTerritory = GUILayout.Toggle(_showTerritory, " Show territory borders");
            _showHoverOwner = GUILayout.Toggle(_showHoverOwner, " Show owner under cursor");

            GUILayout.Label($"Separate colonies: <b>{(ColonyRegistry.Enabled ? "ON" : "OFF")}</b>", Rich());
            if (MultiplayerSession.IsHost)
            {
                if (GUILayout.Button(ColonyRegistry.Enabled ? "Disable separate colonies (shared colony)" : "Enable separate colonies for this save"))
                    ColonyFounder.HostSetMode(!ColonyRegistry.Enabled);
                if (GUILayout.Button("Hard sync all clients now"))
                    GameServerHardSync.PerformHardSync(false);
            }
            if (!ColonyRegistry.Enabled)
            {
                GUILayout.Label(MultiplayerSession.IsHost ? "Shared colony mode (vanilla ONI Together)." : "The host has to enable separate colonies.");
                Footer();
                return;
            }

            GUILayout.Space(4);
            GUILayout.Label("<b>Colonies</b>", Rich());
            int local = ColonyRegistry.LocalColonyId;
            foreach (var c in ColonyRegistry.Colonies)
            {
                var col = new Color(c.ColorR, c.ColorG, c.ColorB);
                string hex = ColorUtility.ToHtmlStringRGB(col);
                GUILayout.Label($"<color=#{hex}>■</color> #{c.Id} <b>{c.Name}</b>{(c.Id == local ? " (you)" : "")}\n" +
                                $"    {c.OwnerName} · {CountDupes(c.Id)} dupes", Rich());
            }

            if (local == ColonyRegistry.NoColony)
            {
                GUILayout.Space(6);
                GUILayout.Label("<b>You don't have a colony yet.</b>", Rich());
                GUILayout.BeginHorizontal();
                GUILayout.Label("Name", GUILayout.Width(45));
                _colonyName = GUILayout.TextField(_colonyName);
                GUILayout.EndHorizontal();
                if (GUILayout.Button(_placing ? "Placing... (click the map)" : "Found my colony — pick a spot"))
                    _placing = !_placing;
                GUILayout.Label($"Must be at least {System.Math.Max(10, ColonyRegistry.TerritoryRadius * 2 + 4)} cells from other colonies, on a floor with a free 4x4 area.");
            }
            else
            {
                GUILayout.Space(4);
                GUILayout.Label("Research and the Printing Pod timer are shared between colonies in this version; prints arrive at the pod you accept them on.");
            }
            Footer();
        }

        private void Footer()
        {
            if (!string.IsNullOrEmpty(_status))
            {
                GUILayout.Space(4);
                GUILayout.Label($"<i>{_status}</i>", Rich());
            }
            foreach (var l in ColonyMessages.Recent.Skip(System.Math.Max(0, ColonyMessages.Recent.Count - 3)))
                GUILayout.Label("• " + l);
            GUILayout.EndScrollView();
            GUI.DragWindow();
        }

        #region Territory overlay

        private const int CirclePoints = 96;
        private const float DotSize = 4f;

        /// <summary>Dotted circle (TerritoryRadius) around every colony's home cell, in the colony's color.</summary>
        private static void DrawTerritories()
        {
            var cam = Camera.main;
            int radius = ColonyRegistry.TerritoryRadius;
            if (cam == null || radius <= 0) return;
            var prev = GUI.color;
            foreach (var c in ColonyRegistry.Colonies)
            {
                if (!Grid.IsValidCell(c.HomeCell)) continue;
                Vector3 center = Grid.CellToPosCCC(c.HomeCell, Grid.SceneLayer.Front);
                GUI.color = new Color(c.ColorR, c.ColorG, c.ColorB, 0.85f);
                for (int i = 0; i < CirclePoints; i++)
                {
                    float a = i * Mathf.PI * 2f / CirclePoints;
                    Vector3 world = center + new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * radius;
                    Vector3 sp = cam.WorldToScreenPoint(world);
                    if (sp.z < 0f || sp.x < -10 || sp.y < -10 || sp.x > Screen.width + 10 || sp.y > Screen.height + 10) continue;
                    GUI.DrawTexture(new Rect(sp.x - DotSize / 2, Screen.height - sp.y - DotSize / 2, DotSize, DotSize), Texture2D.whiteTexture);
                }
            }
            GUI.color = prev;
        }

        private static GUIStyle _hoverStyle;

        /// <summary>Small label at the bottom-left: whose territory / building is under the cursor.</summary>
        private static void DrawHoverOwner()
        {
            int cell;
            try { cell = Grid.PosToCell(PlayerController.GetCursorPos(KInputManager.GetMousePos())); }
            catch { return; }
            if (!Grid.IsValidCell(cell)) return;

            string text = null;
            var territory = ColonyRegistry.Get(ColonyRegistry.GetTerritoryOwner(cell));
            if (territory != null) text = $"Territory: {territory.Name}";

            for (int layer = 0; layer < (int)ObjectLayer.NumLayers; layer++)
            {
                var go = Grid.Objects[cell, layer];
                if (go == null || go.GetComponent<Building>() == null) continue;
                var owner = ColonyRegistry.Get(ColonyRegistry.GetOwner(go));
                string line = owner != null ? $"{go.GetProperName()}: {owner.Name}" : $"{go.GetProperName()}: unowned";
                text = text == null ? line : text + "\n" + line;
                break;
            }
            if (text == null) return;
            if (_hoverStyle == null)
            {
                _hoverStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, wordWrap = true };
                _hoverStyle.normal.textColor = Color.white;
            }
            GUI.Box(new Rect(10, Screen.height - 70, 280, 46), text, _hoverStyle);
        }

        #endregion

        private static int CountDupes(int colonyId)
        {
            int n = 0;
            foreach (MinionIdentity mi in global::Components.LiveMinionIdentities.Items)
                if (mi != null && ColonyRegistry.GetOwner(mi.gameObject) == colonyId) n++;
            return n;
        }

        private static GUIStyle _rich;
        private static GUIStyle Rich()
        {
            if (_rich == null) _rich = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
            return _rich;
        }
    }
}
