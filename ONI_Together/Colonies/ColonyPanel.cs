using System.Collections.Generic;
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
        private Rect _window = new Rect(20, 100, 460, 680);
        private Vector2 _scroll;

        // placement mode
        private bool _placing;
        private string _placeReason;
        private int _hoverCell = -1;
        private string _colonyName = "";
        private string _status = "";
        private string _renameText;

        // starting dupe picker (like the new-game "choose your duplicants" screen)
        private readonly MinionStartingStats[] _starters = new MinionStartingStats[ColonyFounder.StartingDupes];
        private readonly int[] _starterModel = new int[ColonyFounder.StartingDupes]; // 0 any, 1 standard, 2 bionic
        private bool _showStarters = true;
        private bool _startersChosen;

        private void OnStartersChosen(List<MinionStartingStats> picks)
        {
            for (int i = 0; i < _starters.Length; i++) _starters[i] = i < picks.Count ? picks[i] : null;
            _startersChosen = true;
            _placing = true;
            _visible = true;
            _status = "Now click where your Printing Pod should go (Esc / right-click cancels)";
        }

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
            if (global::Game.Instance != _seenGame)
            {
                _seenGame = global::Game.Instance;
                _autoOpened = false;
                _renameText = null;
                _startersChosen = false;
                for (int i = 0; i < _starters.Length; i++) _starters[i] = null; // fresh picks per world
            }
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
            ColonyPlacementOverlay.Update(_hoverCell);

            Vector2 mouse = Input.mousePosition;
            mouse.y = Screen.height - mouse.y;
            bool overWindow = _visible && _window.Contains(mouse);
            if (Input.GetMouseButtonDown(0) && !overWindow)
            {
                if (ColonyFounder.RequestFound(_hoverCell, _colonyName, CurrentStarters(), out string reason))
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
                if (_showTerritory && ColonyRules.TerritoryProtection && Event.current.type == EventType.Repaint) DrawTerritories();
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
                if (Event.current.type == EventType.Repaint)
                    ColonyPlacementOverlay.Draw(_hoverCell, _placeReason == null);
                string txt = _placeReason == null
                    ? "Click to place your Printing Pod here (Esc/right-click cancels)"
                    : $"Can't place here: {_placeReason}";
                string sum = ColonyPlacementOverlay.Summary;
                if (sum != null) txt += "\n" + sum + "\n" + ColonyPlacementOverlay.Legend;
                var style = new GUIStyle(GUI.skin.box) { wordWrap = true, alignment = TextAnchor.UpperLeft };
                style.normal.textColor = _placeReason == null ? Color.green : new Color(1f, 0.5f, 0.4f);
                float w = 520f, h = style.CalcHeight(new GUIContent(txt), w);
                // pinned at the top so it never hides the base preview under the cursor
                GUI.Box(new Rect(Screen.width / 2f - w / 2f, 70, w, h), txt, style);
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

            // ---- rules (host edits, everyone sees) ----
            GUILayout.Space(4);
            GUILayout.Label("<b>Rules</b>" + (MultiplayerSession.IsHost ? " (click to change)" : " (set by host)"), Rich());
            var cur = ColonyRules.Data;
            var next = new ColonyRulesData
            {
                TerritoryProtection = RuleToggle(cur.TerritoryProtection, "Territory protection (no building/digging near other colonies)"),
                BuildingProtection = RuleToggle(cur.BuildingProtection, "Building protection (no orders on other colonies' buildings/dupes)"),
                SeparateErrands = RuleToggle(cur.SeparateErrands, "Separate errands (dupes only work for their own colony)"),
                SeparateResearch = RuleToggle(cur.SeparateResearch, "Separate research trees"),
                SharedDupeManagement = RuleToggle(cur.SharedDupeManagement, "Shared dupe priorities / management screens"),
                SeparatePrintingPod = RuleToggle(cur.SeparatePrintingPod, "Separate Printing Pod timers & choices"),
                SeparateSchedules = RuleToggle(cur.SeparateSchedules, "Separate schedules"),
                HelpOtherColonies = RuleToggle(cur.HelpOtherColonies, "Dupes help other colonies when they have nothing to do"),
                CancelOnlyOwnOrders = RuleToggle(cur.CancelOnlyOwnOrders, "Cancel tool only removes your own orders"),
                SharedResources = RuleToggle(cur.SharedResources, "Share buildings, machines & food (only dupe control separate)"),
                PrefixDupeNames = RuleToggle(cur.PrefixDupeNames, "Colony name as prefix for new dupes"),
            };
            bool changed = next.TerritoryProtection != cur.TerritoryProtection || next.BuildingProtection != cur.BuildingProtection
                           || next.SeparateErrands != cur.SeparateErrands || next.SeparateResearch != cur.SeparateResearch
                           || next.SharedDupeManagement != cur.SharedDupeManagement
                           || next.SeparatePrintingPod != cur.SeparatePrintingPod || next.SeparateSchedules != cur.SeparateSchedules
                           || next.HelpOtherColonies != cur.HelpOtherColonies || next.CancelOnlyOwnOrders != cur.CancelOnlyOwnOrders
                           || next.SharedResources != cur.SharedResources || next.PrefixDupeNames != cur.PrefixDupeNames;
            if (changed && MultiplayerSession.IsHost)
            {
                ColonyRules.Apply(next);
                ColonyRules.SaveToConfig();
                ColonyNet.BroadcastRegistry();
                _status = "Rules updated";
            }

            GUILayout.Space(4);
            GUILayout.Label("<b>Colonies</b>", Rich());
            int local = ColonyRegistry.LocalColonyId;
            foreach (var c in ColonyRegistry.Colonies)
            {
                var col = new Color(c.ColorR, c.ColorG, c.ColorB);
                string hex = ColorUtility.ToHtmlStringRGB(col);
                GUILayout.Label($"<color=#{hex}>■</color> #{c.Id} <b>{c.Name}</b>{(c.Id == local ? " (you)" : "")}\n" +
                                $"    {c.OwnerName} · {CountDupes(c.Id)} dupes" +
                                (ColonyRules.SeparateResearch && (c.Id == local || MultiplayerSession.IsHost) ? $" · researching: {ColonyResearch.ActiveName(c.Id)}" : "") +
                                ((c.Id == local || MultiplayerSession.IsHost) && ColonyImmigration.SeparateActive ? $" · {ColonyImmigration.Describe(c.Id)}" : ""), Rich());
            }

            if (local == ColonyRegistry.NoColony)
            {
                GUILayout.Space(6);
                GUILayout.Label("<b>You don't have a colony yet.</b>", Rich());
                GUILayout.BeginHorizontal();
                GUILayout.Label("Name", GUILayout.Width(45));
                _colonyName = GUILayout.TextField(_colonyName);
                GUILayout.EndHorizontal();
                if (StarterPicker.Available)
                {
                    // same duplicant cards as the host's new-game screen; Embark -> pick the spot
                    if (GUILayout.Button(_placing ? "Placing... (click the map)" : "Found my colony — choose duplicants"))
                    {
                        if (_placing) _placing = false;
                        else if (!StarterPicker.Open(OnStartersChosen)) _placing = true;
                    }
                    if (_startersChosen)
                        GUILayout.Label("Duplicants chosen: " + string.Join(", ", _starters.Where(x => x != null).Select(x => x.Name)) + " — click the map to place your base.");
                    _showStarters = false;
                }
                else
                {
                    DrawStarterPicker();
                    if (GUILayout.Button(_placing ? "Placing... (click the map)" : "Found my colony — pick a spot"))
                        _placing = !_placing;
                }
                GUILayout.Label($"Must be at least {(ColonyRules.TerritoryProtection ? System.Math.Max(10, ColonyRegistry.TerritoryRadius * 2 + 4) : 10)} cells from other colonies. " +
                                (ColonyWorldTools.GetStartTemplate() != null ? "The area is cleared and you get the same starting room as the host." : "Needs a floor with a free 4x4 area."));
            }
            else
            {
                GUILayout.Space(4);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Rename", GUILayout.Width(55));
                if (_renameText == null) _renameText = ColonyRegistry.Get(local)?.Name ?? "";
                _renameText = GUILayout.TextField(_renameText, 24);
                if (GUILayout.Button("OK", GUILayout.Width(40))) ColonyFounder.RequestRename(_renameText);
                GUILayout.EndHorizontal();
                if (ColonyRules.PrefixDupeNames) GUILayout.Label("New dupes get this name as a prefix.");
                GUILayout.Label((ColonyRules.SeparateResearch ? "Research: your own tree. " : "Research: shared tree. ") +
                                (ColonyRules.SeparatePrintingPod ? "Printing Pod: your own timer and choices. " : "Printing Pod: shared timer (first to accept gets it). ") +
                                (ColonyRules.SeparateSchedules ? "Schedules: your own." : "Schedules: shared."));
            }
            Footer();
        }

        #region Starting dupe picker

        private static bool BionicAvailable
        {
            get { try { return global::Game.IsDlcActiveForCurrentSave("DLC3_ID"); } catch { return false; } }
        }

        private static readonly string[] ModelNames = { "Any", "Standard", "Bionic" };

        private MinionStartingStats Roll(int slot)
        {
            try
            {
                if (BionicAvailable && _starterModel[slot] == 1) return new MinionStartingStats(GameTags.Minions.Models.Standard, is_starter_minion: true);
                if (BionicAvailable && _starterModel[slot] == 2) return new MinionStartingStats(GameTags.Minions.Models.Bionic, is_starter_minion: true);
                return new MinionStartingStats(is_starter_minion: true);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[ONIT-Colonies] rolling starter failed: {ex.Message}");
                return null;
            }
        }

        private List<MinionStartingStats> CurrentStarters()
        {
            for (int i = 0; i < _starters.Length; i++) if (_starters[i] == null) _starters[i] = Roll(i);
            return _starters.Where(x => x != null).ToList();
        }

        private void DrawStarterPicker()
        {
            GUILayout.Space(4);
            _showStarters = GUILayout.Toggle(_showStarters, " <b>Choose your 3 starting duplicants</b>", RichToggle());
            if (!_showStarters) return;
            for (int i = 0; i < _starters.Length; i++)
            {
                if (_starters[i] == null) _starters[i] = Roll(i);
                var st = _starters[i];
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();
                GUILayout.Label($"<b>{i + 1}. {st?.Name ?? "?"}</b>" + (st?.personality != null && st.personality.model == GameTags.Minions.Models.Bionic ? " (Bionic)" : ""), Rich());
                if (BionicAvailable && GUILayout.Button(ModelNames[_starterModel[i]], GUILayout.Width(70)))
                {
                    _starterModel[i] = (_starterModel[i] + 1) % 3;
                    _starters[i] = Roll(i);
                }
                if (GUILayout.Button("Reroll", GUILayout.Width(60))) _starters[i] = Roll(i);
                GUILayout.EndHorizontal();
                if (st != null) GUILayout.Label(DescribeStarter(st), Rich());
                GUILayout.EndVertical();
            }
            if (GUILayout.Button("Reroll all"))
                for (int i = 0; i < _starters.Length; i++) _starters[i] = Roll(i);
        }

        private static string DescribeStarter(MinionStartingStats st)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                var interests = st.skillAptitudes.Where(kv => kv.Key != null && kv.Value > 0f).Select(kv => kv.Key.Name).ToList();
                if (interests.Count > 0) sb.Append("Interests: ").Append(string.Join(", ", interests)).Append('\n');

                var traits = st.Traits.Where(t => t != null && t.ValidStarterTrait).ToList();
                if (traits.Count > 0)
                    sb.Append("Traits: ").Append(string.Join(", ", traits.Select(t => t.PositiveTrait ? $"<color=#7CFC00>{t.Name}</color>" : $"<color=#FF8C69>{t.Name}</color>"))).Append('\n');

                var attrs = st.StartingLevels.Where(kv => kv.Value != 0).OrderByDescending(kv => kv.Value).Take(6)
                    .Select(kv => $"{Db.Get().Attributes.TryGet(kv.Key)?.Name ?? kv.Key} {kv.Value:+#;-#;0}").ToList();
                if (attrs.Count > 0) sb.Append("Attributes: ").Append(string.Join(", ", attrs)).Append('\n');

                if (st.stressTrait != null) sb.Append("Stress: ").Append(st.stressTrait.Name);
                if (st.joyTrait != null) sb.Append(" · Overjoyed: ").Append(st.joyTrait.Name);
            }
            catch (System.Exception ex) { sb.Append($"(details unavailable: {ex.Message})"); }
            return STRINGS_Strip(sb.ToString());
        }

        /// <summary>Game strings carry &lt;link&gt; tags IMGUI can't render.</summary>
        private static string STRINGS_Strip(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s ?? "", "</?link[^>]*>|</?style[^>]*>", "");

        private static GUIStyle _richToggle;
        private static GUIStyle RichToggle()
        {
            if (_richToggle == null) _richToggle = new GUIStyle(GUI.skin.toggle) { richText = true };
            return _richToggle;
        }

        #endregion

        /// <summary>Host: clickable toggle. Clients: read-only label.</summary>
        private static bool RuleToggle(bool value, string label)
        {
            if (MultiplayerSession.IsHost) return GUILayout.Toggle(value, " " + label);
            GUILayout.Label((value ? "[x] " : "[ ] ") + label);
            return value;
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
