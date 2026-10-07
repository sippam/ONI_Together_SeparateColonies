using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// IMGUI look for the colonies UI: ONI-like dark navy panels, plum primary buttons, slate secondary buttons,
    /// custom checkboxes and tabs. Textures are generated at runtime (no asset files) and rebuilt if Unity unloads them.
    /// </summary>
    public static class ColonyTheme
    {
        // palette
        public static readonly Color Bg = Hex(0x1B1E2B);
        public static readonly Color BgBorder = Hex(0x4A5070);
        public static readonly Color Header = Hex(0x2A2E44);
        public static readonly Color Card = Hex(0x262A3D);
        public static readonly Color CardBorder = Hex(0x363B55);
        public static readonly Color Primary = Hex(0x9A4A84);
        public static readonly Color PrimaryHover = Hex(0xB45A9B);
        public static readonly Color PrimaryActive = Hex(0x7A3A68);
        public static readonly Color Secondary = Hex(0x3C4566);
        public static readonly Color SecondaryHover = Hex(0x4C577E);
        public static readonly Color SecondaryActive = Hex(0x30385A);
        public static readonly Color Text = Hex(0xECEAF4);
        public static readonly Color Muted = Hex(0xA4A2B8);
        public static readonly Color Accent = Hex(0xF2C14E);
        public static readonly Color Good = Hex(0x7FDB8A);
        public static readonly Color Bad = Hex(0xFF7A6B);
        public static readonly Color Field = Hex(0x141622);

        public static GUIStyle Window, HeaderBar, Title, Subtitle, CloseButton;
        public static GUIStyle Tab, TabActive;
        public static GUIStyle CardStyle, CardHighlight, SectionTitle, Label, MutedLabel, SmallLabel, Badge;
        public static GUIStyle ButtonPrimary, ButtonSecondary, ButtonSmall, ButtonIcon;
        public static GUIStyle CheckBox, ToggleLabel, TextField, Banner, Tooltip, StatusBar, RankBadge;

        private static Texture2D _probe;

        public static void Ensure()
        {
            if (Window != null && _probe != null) return;
            _probe = Solid(Color.white);

            Window = new GUIStyle
            {
                normal = { background = Framed(Bg, BgBorder, 1), textColor = Text },
                border = new RectOffset(2, 2, 2, 2),
                padding = new RectOffset(0, 0, 0, 0),
            };
            Window.onNormal.background = Window.normal.background;

            HeaderBar = new GUIStyle { normal = { background = Solid(Header) }, padding = new RectOffset(12, 8, 6, 6) };
            Title = new GUIStyle { fontSize = 16, fontStyle = FontStyle.Bold, normal = { textColor = Text }, richText = true };
            Subtitle = new GUIStyle { fontSize = 11, normal = { textColor = Muted }, richText = true };

            CloseButton = MakeButton(Hex(0x3A3F5C), Bad, Hex(0x8A3A3A), 14);
            CloseButton.fontStyle = FontStyle.Bold;
            CloseButton.padding = new RectOffset(0, 0, 0, 2);

            Tab = MakeButton(Header, Hex(0x353A55), Hex(0x30344C), 13);
            Tab.normal.textColor = Muted;
            Tab.margin = new RectOffset(0, 0, 0, 0);
            Tab.fixedHeight = 30;
            Tab.normal.background = Framed(Header, Header, 0, bottomLine: Hex(0x353A55));
            TabActive = new GUIStyle(Tab) { fontStyle = FontStyle.Bold };
            TabActive.normal.background = TabActive.hover.background = Framed(Bg, Bg, 0, bottomLine: Primary, lineHeight: 3);
            TabActive.normal.textColor = TabActive.hover.textColor = Text;

            CardStyle = new GUIStyle
            {
                normal = { background = Framed(Card, CardBorder, 1) },
                border = new RectOffset(2, 2, 2, 2),
                padding = new RectOffset(10, 10, 8, 8),
                margin = new RectOffset(8, 8, 4, 4),
            };
            CardHighlight = new GUIStyle(CardStyle) { normal = { background = Framed(Hex(0x2C2540), Primary, 1) } };

            SectionTitle = new GUIStyle { fontSize = 13, fontStyle = FontStyle.Bold, normal = { textColor = Accent }, richText = true, margin = new RectOffset(0, 0, 2, 4) };
            Label = new GUIStyle { fontSize = 13, normal = { textColor = Text }, richText = true, wordWrap = true, margin = new RectOffset(0, 0, 2, 2) };
            MutedLabel = new GUIStyle(Label) { normal = { textColor = Muted }, fontSize = 12 };
            SmallLabel = new GUIStyle(MutedLabel) { fontSize = 11 };
            Badge = new GUIStyle
            {
                fontSize = 11,
                normal = { background = Framed(Hex(0x323853), Hex(0x444B6B), 1), textColor = Text },
                border = new RectOffset(2, 2, 2, 2),
                padding = new RectOffset(6, 6, 2, 3),
                margin = new RectOffset(0, 4, 2, 2),
                richText = true,
                alignment = TextAnchor.MiddleCenter,
            };
            RankBadge = new GUIStyle(Badge)
            {
                fontSize = 13, fontStyle = FontStyle.Bold, fixedWidth = 26, fixedHeight = 24,
                normal = { background = Framed(Primary, PrimaryHover, 1), textColor = Color.white },
                margin = new RectOffset(0, 6, 2, 2),
            };

            ButtonPrimary = MakeButton(Primary, PrimaryHover, PrimaryActive, 14);
            ButtonPrimary.fontStyle = FontStyle.Bold;
            ButtonPrimary.fixedHeight = 34;
            ButtonSecondary = MakeButton(Secondary, SecondaryHover, SecondaryActive, 13);
            ButtonSecondary.fixedHeight = 28;
            ButtonSmall = MakeButton(Secondary, SecondaryHover, SecondaryActive, 12);
            ButtonSmall.fixedHeight = 24;
            ButtonIcon = MakeButton(Secondary, SecondaryHover, SecondaryActive, 12);
            ButtonIcon.fixedWidth = 28; ButtonIcon.fixedHeight = 24;
            ButtonIcon.padding = new RectOffset(0, 0, 0, 0);

            CheckBox = new GUIStyle
            {
                normal = { background = Check(false, false) },
                hover = { background = Check(false, true) },
                active = { background = Check(false, true) },
                onNormal = { background = Check(true, false) },
                onHover = { background = Check(true, true) },
                onActive = { background = Check(true, true) },
                fixedWidth = 18, fixedHeight = 18,
                margin = new RectOffset(0, 8, 3, 3),
            };
            ToggleLabel = new GUIStyle(Label) { hover = { textColor = Color.white }, active = { textColor = Accent } };

            TextField = new GUIStyle
            {
                fontSize = 13,
                normal = { background = Framed(Field, Hex(0x454B6A), 1), textColor = Text },
                focused = { background = Framed(Field, Primary, 1), textColor = Color.white },
                hover = { background = Framed(Field, Hex(0x5A6185), 1), textColor = Text },
                border = new RectOffset(2, 2, 2, 2),
                padding = new RectOffset(8, 8, 5, 5),
                margin = new RectOffset(0, 4, 2, 2),
                fixedHeight = 26,
                clipping = TextClipping.Clip,
            };

            Banner = new GUIStyle
            {
                fontSize = 14,
                richText = true,
                wordWrap = true,
                alignment = TextAnchor.MiddleCenter,
                normal = { background = Framed(new Color(0.11f, 0.12f, 0.18f, 0.94f), Accent, 2), textColor = Text },
                border = new RectOffset(3, 3, 3, 3),
                padding = new RectOffset(14, 14, 8, 8),
            };
            Tooltip = new GUIStyle(Banner)
            {
                fontSize = 13,
                alignment = TextAnchor.UpperLeft,
                normal = { background = Framed(new Color(0.11f, 0.12f, 0.18f, 0.94f), BgBorder, 1), textColor = Text },
                border = new RectOffset(2, 2, 2, 2),
                padding = new RectOffset(10, 10, 6, 6),
            };
            StatusBar = new GUIStyle
            {
                fontSize = 12,
                richText = true,
                wordWrap = true,
                normal = { background = Solid(Header), textColor = Muted },
                padding = new RectOffset(12, 12, 5, 5),
            };
        }

        // ---- helpers for the panel ----

        public static string HexOf(Color c) => ColorUtility.ToHtmlStringRGB(c);
        public static string Colored(string text, Color c) => $"<color=#{HexOf(c)}>{text}</color>";

        /// <summary>Small coloured square (colony colour).</summary>
        public static void Swatch(Color c, float size = 14f)
        {
            var r = GUILayoutUtility.GetRect(size, size, GUILayout.Width(size), GUILayout.Height(size));
            r.y += 3;
            var prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = prev;
        }

        /// <summary>Thin vertical colour bar inside a row.</summary>
        public static void ColorBar(Color c, float height = 24f)
        {
            var r = GUILayoutUtility.GetRect(4, height, GUILayout.Width(4), GUILayout.Height(height));
            var prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = prev;
            GUILayout.Space(6);
        }

        public static void Divider()
        {
            var r = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true), GUILayout.Height(1));
            var prev = GUI.color;
            GUI.color = CardBorder;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = prev;
            GUILayout.Space(4);
        }

        // ---- texture generation ----

        private static GUIStyle MakeButton(Color normal, Color hover, Color active, int fontSize)
        {
            var s = new GUIStyle
            {
                fontSize = fontSize,
                alignment = TextAnchor.MiddleCenter,
                richText = true,
                normal = { background = Framed(normal, Lighten(normal, 0.12f), 1), textColor = Text },
                hover = { background = Framed(hover, Lighten(hover, 0.12f), 1), textColor = Color.white },
                active = { background = Framed(active, Lighten(active, 0.1f), 1), textColor = Color.white },
                border = new RectOffset(2, 2, 2, 2),
                padding = new RectOffset(10, 10, 4, 5),
                margin = new RectOffset(0, 4, 3, 3),
            };
            s.onNormal = s.normal; s.onHover = s.hover; s.onActive = s.active;
            return s;
        }

        private static Color Lighten(Color c, float a) => Color.Lerp(c, Color.white, a);

        private static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        /// <summary>6x6 texture: fill + border (used 9-sliced with RectOffset(2,2,2,2)). Optional bottom line for tabs.</summary>
        private static Texture2D Framed(Color fill, Color border, int width, Color? bottomLine = null, int lineHeight = 1)
        {
            const int n = 6;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    bool edge = width > 0 && (x < width || y < width || x >= n - width || y >= n - width);
                    Color c = edge ? border : fill;
                    if (bottomLine.HasValue && y < lineHeight) c = bottomLine.Value; // texture y=0 is the bottom
                    t.SetPixel(x, y, c);
                }
            t.Apply();
            return t;
        }

        /// <summary>18x18 checkbox.</summary>
        private static Texture2D Check(bool on, bool hover)
        {
            const int n = 18;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            Color border = hover ? PrimaryHover : Hex(0x5A6185);
            Color fill = on ? Primary : Field;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    t.SetPixel(x, y, (x == 0 || y == 0 || x == n - 1 || y == n - 1) ? border : fill);
            if (on)
            {
                // check mark (texture y grows upwards)
                int[,] pts = { { 4, 9 }, { 5, 8 }, { 6, 7 }, { 7, 6 }, { 8, 7 }, { 9, 8 }, { 10, 9 }, { 11, 10 }, { 12, 11 }, { 13, 12 } };
                for (int i = 0; i < pts.GetLength(0); i++)
                    for (int d = 0; d < 2; d++)
                        t.SetPixel(pts[i, 0], pts[i, 1] + d, Color.white);
            }
            t.Apply();
            return t;
        }

        /// <summary>Checkbox + clickable label (+ optional hint line). Read-only when !interactive.</summary>
        public static bool CheckToggle(bool value, string label, string hint = null, bool interactive = true)
        {
            GUILayout.BeginHorizontal();
            bool prevEnabled = GUI.enabled;
            GUI.enabled = prevEnabled && interactive;
            bool v = GUILayout.Toggle(value, GUIContent.none, CheckBox);
            GUILayout.BeginVertical();
            if (GUILayout.Button(label, ToggleLabel) && interactive) v = !value;
            if (!string.IsNullOrEmpty(hint)) GUILayout.Label(hint, SmallLabel);
            GUILayout.EndVertical();
            GUI.enabled = prevEnabled;
            GUILayout.EndHorizontal();
            return interactive ? v : value;
        }

        private static Color Hex(int rgb) => new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
    }
}
