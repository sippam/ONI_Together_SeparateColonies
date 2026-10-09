using System;
using System.Collections.Generic;
using System.Linq;
using ONI_Together.Networking;
using UnityEngine;
using static ONI_Together.Colonies.ColonyLoc;

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
        private bool _minimized;
        private bool _prefsLoaded;
        private bool _winMoved;

        // "Colonies" toggle button: drag it anywhere (position remembered), click to open
        private const float BtnW = 112f, BtnH = 28f;
        private Vector2 _btnPos = new Vector2(-1f, -1f);
        private bool _btnDown, _btnDragging;
        private Vector2 _btnGrab, _btnStart;
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
        private bool _startersChosen;

        private void OnStartersChosen(List<MinionStartingStats> picks)
        {
            for (int i = 0; i < _starters.Length; i++) _starters[i] = i < picks.Count ? picks[i] : null;
            _startersChosen = true;
            _placing = true;
            _visible = true;
            _status = T("Now click where your Printing Pod should go (Esc / right-click cancels)");
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
                _status = T("Placement cancelled");
                return;
            }
            Vector3 pos = PlayerController.GetCursorPos(KInputManager.GetMousePos());
            _hoverCell = Grid.PosToCell(pos);
            ColonyFounder.ValidateHomeCell(_hoverCell, out _placeReason);
            ColonyPlacementOverlay.Update(_hoverCell);

            if (Input.GetMouseButtonDown(0))
            {
                if (ColonyFounder.RequestFound(_hoverCell, _colonyName, CurrentStarters(), out string reason))
                {
                    _status = MultiplayerSession.IsHost ? T("Founding...") : T("Request sent — waiting for host...");
                    _placing = false;
                }
                else _status = reason;
            }
        }

        private enum PanelTab { Colony, Colonies, Rules, View }
        private PanelTab _tab = PanelTab.Colony;

        private void OnGUI()
        {
            if (!InGame) return;
            // the game's duplicant cards are open: keep IMGUI out of the way
            if (StarterPicker.Active) return;
            ColonyTheme.Ensure();

            if (ColonyRegistry.Active)
            {
                if (_showTerritory && ColonyRules.TerritoryProtection && Event.current.type == EventType.Repaint) DrawTerritories();
                if (_showHoverOwner && !_placing) DrawHoverOwner();
            }

            // spectators can't dig/build: make that impossible to miss
            if (ColonyRegistry.Active && ColonyRegistry.LocalColonyId == ColonyRegistry.NoColony && !_placing)
            {
                string spec = $"{ColonyTheme.Colored("<b>" + T("SPECTATING") + "</b>", ColonyTheme.Accent)}" + T(" — you don't have a colony yet, your orders are ignored.") + "\n" +
                              $"{ColonyTheme.Colored(T("Click here"), ColonyTheme.Accent)}" + T(" to start your own base.");
                float sh = ColonyTheme.Banner.CalcHeight(new GUIContent(spec), 560f);
                var r = new Rect(Screen.width / 2f - 280, 64, 560, sh);
                GUI.Box(r, spec, ColonyTheme.Banner);
                if (Event.current.type == EventType.MouseDown && r.Contains(Event.current.mousePosition)) { _visible = true; _tab = PanelTab.Colony; Event.current.Use(); }
            }

            if (_placing)
            {
                if (Event.current.type == EventType.Repaint)
                    ColonyPlacementOverlay.Draw(_hoverCell, _placeReason == null);
                bool ok = _placeReason == null;
                string txt = ok
                    ? ColonyTheme.Colored("<b>" + T("Click to place your Printing Pod here") + "</b>", ColonyTheme.Good)
                    : ColonyTheme.Colored("<b>" + T("Can't place here:") + "</b> " + _placeReason, ColonyTheme.Bad);
                txt += ColonyTheme.Colored(T("   (Esc / right-click cancels)"), ColonyTheme.Muted);
                string sum = ColonyPlacementOverlay.Summary;
                if (sum != null)
                    txt += "\n" + sum + "\n" +
                           $"{ColonyTheme.Colored("■", new Color(0.2f, 0.95f, 0.3f))} {T("free")}   {ColonyTheme.Colored("■", new Color(1f, 0.85f, 0.2f))} {T("cleared")}   " +
                           $"{ColonyTheme.Colored("■", new Color(0.95f, 0.3f, 0.95f))} {T("building stays")}   {ColonyTheme.Colored("■", new Color(1f, 0.25f, 0.2f))} {T("blocked")}   " +
                           $"{ColonyTheme.Colored("□", new Color(0.3f, 0.9f, 1f))} Printing Pod   {ColonyTheme.Colored("○", new Color(1f, 0.3f, 0.25f))} {T("keep-out ring")}";
                var style = ColonyTheme.Banner;
                float w = 600f, h = style.CalcHeight(new GUIContent(txt), w);
                GUI.Box(new Rect(Screen.width / 2f - w / 2f, 64, w, h), txt, style);
            }

            if (_placing) return; // window hidden while placing so the whole base preview is visible
            LoadPrefs();
            if (!_visible)
            {
                DrawToggleButton();
                return;
            }
            _window.width = 500;
            _window.height = _minimized ? 64f : Mathf.Min(720, Screen.height - 120);
            var before = _window.position;
            _window = GUI.Window(0x4A6C01, _window, DrawWindow, GUIContent.none, ColonyTheme.Window);
            _window.x = Mathf.Clamp(_window.x, 40f - _window.width, Screen.width - 40f);
            _window.y = Mathf.Clamp(_window.y, 0f, Screen.height - 40f);
            if (_window.position != before) _winMoved = true;
            if (_winMoved && Event.current.rawType == EventType.MouseUp) { _winMoved = false; SavePrefs(); }
        }

        private void LoadPrefs()
        {
            if (_prefsLoaded) return;
            _prefsLoaded = true;
            try
            {
                _btnPos = new Vector2(PlayerPrefs.GetFloat("ONIT_Colonies_BtnX", -1f), PlayerPrefs.GetFloat("ONIT_Colonies_BtnY", -1f));
                float wx = PlayerPrefs.GetFloat("ONIT_Colonies_WinX", _window.x), wy = PlayerPrefs.GetFloat("ONIT_Colonies_WinY", _window.y);
                _window.x = wx; _window.y = wy;
                _minimized = PlayerPrefs.GetInt("ONIT_Colonies_Min", 0) == 1;
            }
            catch (Exception) { }
        }

        private void SavePrefs()
        {
            try
            {
                PlayerPrefs.SetFloat("ONIT_Colonies_BtnX", _btnPos.x);
                PlayerPrefs.SetFloat("ONIT_Colonies_BtnY", _btnPos.y);
                PlayerPrefs.SetFloat("ONIT_Colonies_WinX", _window.x);
                PlayerPrefs.SetFloat("ONIT_Colonies_WinY", _window.y);
                PlayerPrefs.SetInt("ONIT_Colonies_Min", _minimized ? 1 : 0);
                PlayerPrefs.Save();
            }
            catch (Exception) { }
        }

        private Vector2 ClampButton(Vector2 p)
            => new Vector2(Mathf.Clamp(p.x, 0f, Screen.width - BtnW), Mathf.Clamp(p.y, 0f, Screen.height - BtnH));

        /// <summary>The closed-panel button. Drag to move it (remembered), click to open the panel.</summary>
        private void DrawToggleButton()
        {
            if (_btnPos.x < 0f || _btnPos.y < 0f) _btnPos = new Vector2(Screen.width - BtnW - 20f, 58f);
            _btnPos = ClampButton(_btnPos);
            var r = new Rect(_btnPos.x, _btnPos.y, BtnW, BtnH);
            var e = Event.current;
            switch (e.type)
            {
                case EventType.MouseDown:
                    if (e.button == 0 && r.Contains(e.mousePosition))
                    {
                        _btnDown = true; _btnDragging = false;
                        _btnStart = e.mousePosition; _btnGrab = e.mousePosition - _btnPos;
                        e.Use();
                    }
                    else if (e.button == 1 && r.Contains(e.mousePosition))
                    {
                        _btnPos = new Vector2(-1f, -1f); SavePrefs(); e.Use(); // right-click: back to the default spot
                        return;
                    }
                    break;
                case EventType.MouseDrag:
                    if (_btnDown)
                    {
                        if (!_btnDragging && (e.mousePosition - _btnStart).sqrMagnitude > 36f) _btnDragging = true;
                        if (_btnDragging) _btnPos = ClampButton(e.mousePosition - _btnGrab);
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (_btnDown && e.button == 0)
                    {
                        _btnDown = false;
                        if (_btnDragging) SavePrefs(); else _visible = true;
                        _btnDragging = false;
                        e.Use();
                    }
                    break;
            }
            r = new Rect(_btnPos.x, _btnPos.y, BtnW, BtnH);
            GUI.Box(r, "≡  " + T("Colonies"), ColonyTheme.ButtonPrimary);
        }

        private void DrawWindow(int id)
        {
            ColonyTheme.Ensure();
            int local = ColonyRegistry.LocalColonyId;
            var mine = ColonyRegistry.Get(local);

            // ---- header ----
            GUILayout.BeginVertical(ColonyTheme.HeaderBar);
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical();
            GUILayout.Label(T("Separate Colonies"), ColonyTheme.Title);
            string sub;
            if (!ColonyRegistry.Enabled) sub = T("Shared colony mode");
            else
            {
                sub = F("{0} colonies on this asteroid", ColonyRegistry.Colonies.Count);
                sub += mine != null
                    ? F("  ·  you: {0}", ColonyTheme.Colored(mine.Name, new Color(mine.ColorR, mine.ColorG, mine.ColorB)))
                    : $"  ·  {ColonyTheme.Colored(T("you are spectating"), ColonyTheme.Accent)}";
            }
            GUILayout.Label(sub + ColonyTheme.Colored("   ·   Ctrl+Shift+C", ColonyTheme.Muted), ColonyTheme.Subtitle);
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(_minimized ? "□" : "—", ColonyTheme.CloseButton, GUILayout.Width(26), GUILayout.Height(24)))
            {
                _minimized = !_minimized;
                SavePrefs();
            }
            if (GUILayout.Button("X", ColonyTheme.CloseButton, GUILayout.Width(26), GUILayout.Height(24))) _visible = false;
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            if (_minimized)
            {
                GUI.DragWindow(new Rect(0, 0, _window.width - 70, 64));
                return;
            }

            // ---- tabs ----
            GUILayout.BeginHorizontal();
            TabButton(PanelTab.Colony, mine != null ? T("My colony") : T("Start"));
            TabButton(PanelTab.Colonies, F("Colonies ({0})", ColonyRegistry.Colonies.Count));
            TabButton(PanelTab.Rules, T("Rules"));
            TabButton(PanelTab.View, T("View"));
            GUILayout.EndHorizontal();
            GUILayout.Space(4);

            _scroll = GUILayout.BeginScrollView(_scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUIStyle.none);
            switch (_tab)
            {
                case PanelTab.Colony: DrawColonyTab(local, mine); break;
                case PanelTab.Colonies: DrawColoniesTab(local); break;
                case PanelTab.Rules: DrawRulesTab(); break;
                case PanelTab.View: DrawViewTab(); break;
            }
            GUILayout.Space(6);
            GUILayout.EndScrollView();

            // ---- status bar ----
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(_status)) lines.Add(ColonyTheme.Colored(_status, ColonyTheme.Text));
            foreach (var l in ColonyMessages.Recent.Skip(System.Math.Max(0, ColonyMessages.Recent.Count - 2)))
                if (l != _status) lines.Add("• " + l);
            if (lines.Count > 0) GUILayout.Label(string.Join("\n", lines), ColonyTheme.StatusBar);

            GUI.DragWindow(new Rect(0, 0, _window.width - 70, 52)); // drag by the header only
        }

        private void TabButton(PanelTab tab, string label)
        {
            if (GUILayout.Button(label, _tab == tab ? ColonyTheme.TabActive : ColonyTheme.Tab)) { _tab = tab; _scroll = Vector2.zero; }
        }

        private static void BeginCard(bool highlight = false) => GUILayout.BeginVertical(highlight ? ColonyTheme.CardHighlight : ColonyTheme.CardStyle);
        private static void EndCard() => GUILayout.EndVertical();

        // ---------------------------------------------------------------- tab: my colony / start

        private void DrawColonyTab(int local, ColonyInfo mine)
        {
            if (!ColonyRegistry.Enabled)
            {
                BeginCard();
                GUILayout.Label(T("Separate colonies are off"), ColonyTheme.SectionTitle);
                GUILayout.Label(MultiplayerSession.IsHost
                    ? T("Everyone shares one colony (vanilla ONI Together). Turn it on to let every player run their own base on this asteroid.")
                    : T("Everyone shares one colony. The host can turn on separate colonies."), ColonyTheme.MutedLabel);
                if (MultiplayerSession.IsHost && GUILayout.Button(T("Enable separate colonies for this save"), ColonyTheme.ButtonPrimary))
                    ColonyFounder.HostSetMode(true);
                EndCard();
                return;
            }

            if (mine == null) { DrawStartCard(); return; }

            var col = new Color(mine.ColorR, mine.ColorG, mine.ColorB);
            BeginCard(true);
            GUILayout.BeginHorizontal();
            ColonyTheme.Swatch(col, 18);
            GUILayout.Space(6);
            GUILayout.Label($"<size=17><b>{mine.Name}</b></size>", ColonyTheme.Label);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(F("{0} duplicants", CountDupes(local)), ColonyTheme.Badge);
            if (ColonyRules.SeparateResearch) GUILayout.Label(F("Research: {0}", ColonyResearch.ActiveName(local)), ColonyTheme.Badge);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (ColonyImmigration.SeparateActive) GUILayout.Label(F("Printing Pod: {0}", ColonyImmigration.Describe(local)), ColonyTheme.MutedLabel);
            GUILayout.Space(4);
            ColonyTheme.Divider();
            GUILayout.BeginHorizontal();
            GUILayout.Label(T("Name"), ColonyTheme.MutedLabel, GUILayout.Width(44));
            if (_renameText == null) _renameText = mine.Name ?? "";
            _renameText = GUILayout.TextField(_renameText, 24, ColonyTheme.TextField);
            GUI.enabled = !string.IsNullOrEmpty(_renameText) && _renameText.Trim() != mine.Name;
            if (GUILayout.Button(T("Rename"), ColonyTheme.ButtonSmall, GUILayout.Width(74))) ColonyFounder.RequestRename(_renameText);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (ColonyRules.PrefixDupeNames) GUILayout.Label(T("New duplicants get the colony name as a prefix."), ColonyTheme.SmallLabel);
            EndCard();

            BeginCard();
            GUILayout.Label(T("What is yours alone"), ColonyTheme.SectionTitle);
            Feature(T("Research"), ColonyRules.SeparateResearch, T("your own tech tree"), T("shared tech tree"));
            Feature(T("Printing Pod"), ColonyRules.SeparatePrintingPod, T("your own timer and choices"), T("shared — first to accept gets it"));
            Feature(T("Schedules"), ColonyRules.SeparateSchedules, T("your own"), T("shared"));
            Feature(T("Errands"), ColonyRules.SeparateErrands, ColonyRules.HelpOtherColonies ? T("your dupes, your orders first (see Work priority)") : T("your dupes only work for you"), T("anyone's dupes do anyone's work"));
            Feature(T("Buildings & food"), !ColonyRules.SharedResources, T("only your colony uses them"), T("shared with everyone"));
            EndCard();

            DrawWorkOrder(local);
        }

        private static void Feature(string name, bool own, string ownText, string sharedText)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(name, ColonyTheme.Label, GUILayout.Width(120));
            GUILayout.Label(own ? ColonyTheme.Colored("● ", ColonyTheme.Good) + ownText : ColonyTheme.Colored("● ", ColonyTheme.Muted) + sharedText, ColonyTheme.MutedLabel);
            GUILayout.EndHorizontal();
        }

        private void DrawStartCard()
        {
            BeginCard(true);
            GUILayout.Label("<size=16><b>" + T("Start your own colony") + "</b></size>", ColonyTheme.Label);
            GUILayout.Label(T("You are spectating. Found a colony to get a Printing Pod, a starting base and 3 duplicants of your own."), ColonyTheme.MutedLabel);
            GUILayout.Space(6);

            Step(1, T("Name your colony"), !string.IsNullOrEmpty(_colonyName?.Trim()));
            GUILayout.BeginHorizontal();
            GUILayout.Space(34);
            _colonyName = GUILayout.TextField(_colonyName ?? "", 24, ColonyTheme.TextField);
            GUILayout.EndHorizontal();
            GUILayout.Space(4);

            bool cards = StarterPicker.Available;
            Step(2, T("Choose 3 starting duplicants"), _startersChosen || (!cards && _starters.All(x => x != null)));
            if (_startersChosen)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(34);
                GUILayout.Label(string.Join(", ", _starters.Where(x => x != null).Select(x => x.Name)), ColonyTheme.MutedLabel);
                GUILayout.EndHorizontal();
            }
            if (!cards) DrawStarterPicker();
            GUILayout.Space(4);

            Step(3, T("Click the map to place your Printing Pod"), false);
            GUILayout.BeginHorizontal();
            GUILayout.Space(34);
            GUILayout.Label(F("At least {0} cells from other colonies. ", ColonyFounder.MinColonyDistance) +
                            (ColonyWorldTools.GetStartTemplate() != null ? T("A preview shows the whole base; the area is cleared and you get the same starting room as the host.")
                                                                         : T("Needs a floor with a free 4x4 area.")), ColonyTheme.SmallLabel);
            GUILayout.EndHorizontal();
            GUILayout.Space(8);

            string btn = _placing ? T("Placing… click the map (Esc cancels)")
                       : cards ? (_startersChosen ? T("Place my colony") : T("Choose duplicants"))
                       : T("Place my colony");
            if (GUILayout.Button(btn, ColonyTheme.ButtonPrimary))
            {
                if (_placing) _placing = false;
                else if (cards && !_startersChosen) { if (!StarterPicker.Open(OnStartersChosen)) _placing = true; }
                else _placing = true;
            }
            if (cards && _startersChosen && !_placing && GUILayout.Button(T("Choose duplicants again"), ColonyTheme.ButtonSmall))
                StarterPicker.Open(OnStartersChosen);
            EndCard();
        }

        private static void Step(int n, string text, bool done)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(done ? "✓" : n.ToString(), ColonyTheme.RankBadge);
            GUILayout.Label(done ? ColonyTheme.Colored($"<b>{text}</b>", ColonyTheme.Good) : $"<b>{text}</b>", ColonyTheme.Label);
            GUILayout.EndHorizontal();
        }

        // ---------------------------------------------------------------- tab: colonies

        private void DrawColoniesTab(int local)
        {
            if (!ColonyRegistry.Enabled || ColonyRegistry.Colonies.Count == 0)
            {
                BeginCard();
                GUILayout.Label(T("No colonies yet."), ColonyTheme.MutedLabel);
                EndCard();
                return;
            }
            foreach (var c in ColonyRegistry.Colonies)
            {
                var col = new Color(c.ColorR, c.ColorG, c.ColorB);
                bool me = c.Id == local;
                BeginCard(me);
                GUILayout.BeginHorizontal();
                ColonyTheme.ColorBar(col, 40);
                GUILayout.BeginVertical();
                GUILayout.Label($"<b>{c.Name}</b>" + (me ? ColonyTheme.Colored(T("  (you)"), ColonyTheme.Accent) : ""), ColonyTheme.Label);
                GUILayout.Label(F("{0}  ·  {1} duplicants  ·  colony #{2}", c.OwnerName, CountDupes(c.Id), c.Id), ColonyTheme.MutedLabel);
                bool details = me || MultiplayerSession.IsHost;
                if (details && ColonyRules.SeparateResearch) GUILayout.Label(F("Researching: {0}", ColonyResearch.ActiveName(c.Id)), ColonyTheme.SmallLabel);
                if (details && ColonyImmigration.SeparateActive) GUILayout.Label(F("Printing Pod: {0}", ColonyImmigration.Describe(c.Id)), ColonyTheme.SmallLabel);
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
                EndCard();
            }
        }

        // ---------------------------------------------------------------- tab: rules

        private void DrawRulesTab()
        {
            bool host = MultiplayerSession.IsHost;
            BeginCard();
            GUILayout.Label(host ? T("You are the host — changes apply to everyone right away and are saved in your mod options.")
                                 : T("Set by the host. Shown here so you know how this save plays."), ColonyTheme.MutedLabel);
            EndCard();
            if (!ColonyRegistry.Enabled)
            {
                DrawHostTools();
                return;
            }

            var cur = ColonyRules.Data;
            var next = new ColonyRulesData();

            BeginCard();
            GUILayout.Label(T("Protection"), ColonyTheme.SectionTitle);
            next.TerritoryProtection = ColonyTheme.CheckToggle(cur.TerritoryProtection, T("Territory protection"), T("No building or digging inside other colonies' circles."), host);
            next.BuildingProtection = ColonyTheme.CheckToggle(cur.BuildingProtection, T("Building protection"), T("No orders on other colonies' buildings and duplicants."), host);
            next.CancelOnlyOwnOrders = ColonyTheme.CheckToggle(cur.CancelOnlyOwnOrders, T("Cancel only your own orders"), T("The cancel tool leaves other colonies' orders alone."), host);
            EndCard();

            BeginCard();
            GUILayout.Label(T("Work"), ColonyTheme.SectionTitle);
            next.SeparateErrands = ColonyTheme.CheckToggle(cur.SeparateErrands, T("Separate errands"), T("Duplicants work for their own colony."), host);
            next.HelpOtherColonies = ColonyTheme.CheckToggle(cur.HelpOtherColonies, T("Help other colonies"), T("Duplicants also do other colonies' errands, in each player's Work priority order."), host);
            next.SharedResources = ColonyTheme.CheckToggle(cur.SharedResources, T("Share buildings, machines & food"), T("Only duplicant control stays separate."), host);
            next.SharedDupeManagement = ColonyTheme.CheckToggle(cur.SharedDupeManagement, T("Shared duplicant management"), T("Anyone can change any dupe's priorities, food and skills."), host);
            EndCard();

            BeginCard();
            GUILayout.Label(T("Colony systems"), ColonyTheme.SectionTitle);
            next.SeparateResearch = ColonyTheme.CheckToggle(cur.SeparateResearch, T("Separate research"), T("Every colony has its own tech tree."), host);
            next.SeparatePrintingPod = ColonyTheme.CheckToggle(cur.SeparatePrintingPod, T("Separate Printing Pods"), T("Own timer and own choices for every colony."), host);
            next.SeparateSchedules = ColonyTheme.CheckToggle(cur.SeparateSchedules, T("Separate schedules"), T("Every colony manages its own schedules."), host);
            next.PrefixDupeNames = ColonyTheme.CheckToggle(cur.PrefixDupeNames, T("Colony name as dupe prefix"), T("New duplicants are named \"Colony Name\"."), host);
            EndCard();

            bool changed = next.TerritoryProtection != cur.TerritoryProtection || next.BuildingProtection != cur.BuildingProtection
                           || next.SeparateErrands != cur.SeparateErrands || next.SeparateResearch != cur.SeparateResearch
                           || next.SharedDupeManagement != cur.SharedDupeManagement
                           || next.SeparatePrintingPod != cur.SeparatePrintingPod || next.SeparateSchedules != cur.SeparateSchedules
                           || next.HelpOtherColonies != cur.HelpOtherColonies || next.CancelOnlyOwnOrders != cur.CancelOnlyOwnOrders
                           || next.SharedResources != cur.SharedResources || next.PrefixDupeNames != cur.PrefixDupeNames;
            if (changed && host)
            {
                ColonyRules.Apply(next);
                ColonyRules.SaveToConfig();
                ColonyNet.BroadcastRegistry();
                _status = T("Rules updated");
            }

            DrawHostTools();
        }

        private void DrawHostTools()
        {
            if (!MultiplayerSession.IsHost) return;
            BeginCard();
            GUILayout.Label(T("Host tools"), ColonyTheme.SectionTitle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(ColonyRegistry.Enabled ? T("Turn separate colonies off") : T("Turn separate colonies on"), ColonyTheme.ButtonSecondary))
                ColonyFounder.HostSetMode(!ColonyRegistry.Enabled);
            if (GUILayout.Button(T("Hard sync all clients"), ColonyTheme.ButtonSecondary))
                GameServerHardSync.PerformHardSync(false);
            GUILayout.EndHorizontal();
            GUILayout.Label(T("Hard sync sends your exact world to everyone (short pause). Background sync fixes small differences on its own."), ColonyTheme.SmallLabel);
            EndCard();
        }

        // ---------------------------------------------------------------- tab: view

        private void DrawViewTab()
        {
            BeginCard();
            GUILayout.Label(T("Overlays"), ColonyTheme.SectionTitle);
            _showTerritory = ColonyTheme.CheckToggle(_showTerritory, T("Territory borders"), T("Dotted circle in each colony's colour (when territory protection is on)."));
            _showHoverOwner = ColonyTheme.CheckToggle(_showHoverOwner, T("Owner under the cursor"), T("Small box at the bottom-left: whose territory and building you are pointing at."));
            EndCard();
            BeginCard();
            GUILayout.Label(T("Keys"), ColonyTheme.SectionTitle);
            GUILayout.Label("<b>Ctrl+Shift+C</b>  " + T("open / close this window"), ColonyTheme.MutedLabel);
            GUILayout.Label("<b>Esc / right-click</b>  " + T("cancel placing a colony"), ColonyTheme.MutedLabel);
            EndCard();
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

        /// <summary>"Whose work first" list for the local colony, with up/down buttons.</summary>
        private void DrawWorkOrder(int local)
        {
            if (ColonyRegistry.Colonies.Count < 2) return;
            BeginCard();
            GUILayout.BeginHorizontal();
            GUILayout.Label(T("Work priority"), ColonyTheme.SectionTitle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(T("Reset"), ColonyTheme.ButtonSmall, GUILayout.Width(64))) ColonyWorkOrder.LocalReset();
            GUILayout.EndHorizontal();
            GUILayout.Label(T("Your duplicants finish the errands of the colony at the top first, then move down the list."), ColonyTheme.MutedLabel);
            if (!ColonyRules.SeparateErrands || !ColonyRules.HelpOtherColonies)
                GUILayout.Label(ColonyTheme.Colored(T("Not active: the host has to turn on \"Separate errands\" and \"Help other colonies\"."), ColonyTheme.Accent), ColonyTheme.SmallLabel);
            GUILayout.Space(4);
            var order = ColonyWorkOrder.Get(local);
            for (int i = 0; i < order.Count; i++)
            {
                var c = ColonyRegistry.Get(order[i]);
                if (c == null) continue;
                bool me = c.Id == local;
                GUILayout.BeginHorizontal();
                GUILayout.Label((i + 1).ToString(), ColonyTheme.RankBadge);
                ColonyTheme.ColorBar(new Color(c.ColorR, c.ColorG, c.ColorB), 24);
                GUILayout.BeginVertical();
                GUILayout.Label($"<b>{c.Name}</b>" + (me ? ColonyTheme.Colored(T("  you"), ColonyTheme.Accent) : ""), ColonyTheme.Label);
                GUILayout.Label(me ? T("your orders + unowned work") : c.OwnerName, ColonyTheme.SmallLabel);
                GUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                GUI.enabled = i > 0;
                if (GUILayout.Button("▲", ColonyTheme.ButtonIcon)) ColonyWorkOrder.LocalMove(i, -1);
                GUI.enabled = i < order.Count - 1;
                if (GUILayout.Button("▼", ColonyTheme.ButtonIcon)) ColonyWorkOrder.LocalMove(i, +1);
                GUI.enabled = true;
                GUILayout.EndHorizontal();
                if (i < order.Count - 1) ColonyTheme.Divider();
            }
            GUILayout.Space(2);
            GUILayout.Label(T("Eating, sleeping and other personal needs always come first. A duplicant finishes its current errand before switching."), ColonyTheme.SmallLabel);
            EndCard();
        }

        /// <summary>Fallback picker when the game's duplicant cards can't be used.</summary>
        private void DrawStarterPicker()
        {
            for (int i = 0; i < _starters.Length; i++)
            {
                if (_starters[i] == null) _starters[i] = Roll(i);
                var st = _starters[i];
                GUILayout.BeginVertical(ColonyTheme.CardStyle);
                GUILayout.BeginHorizontal();
                GUILayout.Label($"<b>{st?.Name ?? "?"}</b>" + (st?.personality != null && st.personality.model == GameTags.Minions.Models.Bionic ? ColonyTheme.Colored(T("  Bionic"), ColonyTheme.Accent) : ""), ColonyTheme.Label);
                GUILayout.FlexibleSpace();
                if (BionicAvailable && GUILayout.Button(T(ModelNames[_starterModel[i]]), ColonyTheme.ButtonSmall, GUILayout.Width(80)))
                {
                    _starterModel[i] = (_starterModel[i] + 1) % 3;
                    _starters[i] = Roll(i);
                }
                if (GUILayout.Button(T("Reroll"), ColonyTheme.ButtonSmall, GUILayout.Width(64))) _starters[i] = Roll(i);
                GUILayout.EndHorizontal();
                if (st != null) GUILayout.Label(DescribeStarter(st), ColonyTheme.SmallLabel);
                GUILayout.EndVertical();
            }
            if (GUILayout.Button(T("Reroll all"), ColonyTheme.ButtonSmall))
                for (int i = 0; i < _starters.Length; i++) _starters[i] = Roll(i);
        }

        private static string DescribeStarter(MinionStartingStats st)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                var interests = st.skillAptitudes.Where(kv => kv.Key != null && kv.Value > 0f).Select(kv => kv.Key.Name).ToList();
                if (interests.Count > 0) sb.Append(T("Interests: ")).Append(string.Join(", ", interests)).Append('\n');

                var traits = st.Traits.Where(t => t != null && t.ValidStarterTrait).ToList();
                if (traits.Count > 0)
                    sb.Append(T("Traits: ")).Append(string.Join(", ", traits.Select(t => t.PositiveTrait ? $"<color=#7CFC00>{t.Name}</color>" : $"<color=#FF8C69>{t.Name}</color>"))).Append('\n');

                var attrs = st.StartingLevels.Where(kv => kv.Value != 0).OrderByDescending(kv => kv.Value).Take(6)
                    .Select(kv => $"{Db.Get().Attributes.TryGet(kv.Key)?.Name ?? kv.Key} {kv.Value:+#;-#;0}").ToList();
                if (attrs.Count > 0) sb.Append(T("Attributes: ")).Append(string.Join(", ", attrs)).Append('\n');

                if (st.stressTrait != null) sb.Append(T("Stress: ")).Append(st.stressTrait.Name);
                if (st.joyTrait != null) sb.Append(T(" · Overjoyed: ")).Append(st.joyTrait.Name);
            }
            catch (System.Exception ex) { sb.Append($"(details unavailable: {ex.Message})"); }
            return STRINGS_Strip(sb.ToString());
        }

        /// <summary>Game strings carry &lt;link&gt; tags IMGUI can't render.</summary>
        private static string STRINGS_Strip(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s ?? "", "</?link[^>]*>|</?style[^>]*>", "");

        #endregion

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
                if (!Grid.IsValidCell(c.HomeCell) || !ColonyWorldTools.OnActiveWorld(c.HomeCell)) continue;
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


        /// <summary>Small label at the bottom-left: whose territory / building is under the cursor.</summary>
        private static void DrawHoverOwner()
        {
            int cell;
            try { cell = Grid.PosToCell(PlayerController.GetCursorPos(KInputManager.GetMousePos())); }
            catch { return; }
            if (!Grid.IsValidCell(cell)) return;

            string text = null;
            var territory = ColonyRegistry.Get(ColonyRegistry.GetTerritoryOwner(cell));
            if (territory != null) text = F("Territory: {0}", territory.Name);

            for (int layer = 0; layer < (int)ObjectLayer.NumLayers; layer++)
            {
                var go = Grid.Objects[cell, layer];
                if (go == null || go.GetComponent<Building>() == null) continue;
                var owner = ColonyRegistry.Get(ColonyRegistry.GetOwner(go));
                string line = owner != null ? $"{go.GetProperName()}: {owner.Name}" : F("{0}: unowned", go.GetProperName());
                text = text == null ? line : text + "\n" + line;
                break;
            }
            if (text == null) return;
            var hs = ColonyTheme.Tooltip;
            float h = hs.CalcHeight(new GUIContent(text), 300f);
            GUI.Box(new Rect(12, Screen.height - h - 24, 300, h), text, hs);
        }

        #endregion

        private static int CountDupes(int colonyId)
        {
            int n = 0;
            foreach (MinionIdentity mi in global::Components.LiveMinionIdentities.Items)
                if (mi != null && ColonyRegistry.GetOwner(mi.gameObject) == colonyId) n++;
            return n;
        }
    }
}
