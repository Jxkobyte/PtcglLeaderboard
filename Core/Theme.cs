using UnityEngine;

namespace PrizeTracker.Core
{
    /// <summary>
    /// IMGUI styling for the overlay.
    ///
    /// Unity's built-in GUI skin is a light-grey 2005-era look that glares over the game board, so
    /// everything here is drawn against explicit 1x1 textures instead of the default skin. There is
    /// deliberately ONE palette: this is a dark overlay sitting on top of a game, not an app with a
    /// theme setting, and a light variant only ever applied to this panel's own chrome.
    ///
    /// Styles and textures are created lazily on first OnGUI - constructing a Texture2D at
    /// plugin-load time, before Unity's graphics are up, is not safe.
    /// </summary>
    internal class Theme
    {
        private bool _built;

        // palette
        public Color Bg, Panel, Row, RowAlt, Text, TextDim, Accent, Good, Warn, Line;

        public GUIStyle Window, Header, Label, Dim, Small, RowStyle, RowAltStyle,
                        Tab, TabOn, Btn, Value, Title;

        private Texture2D _texBg, _texPanel, _texRow, _texRowAlt, _texTabOn, _texLine;

        public void EnsureBuilt()
        {
            if (_built) return;
            Build();
            _built = true;
        }

        private static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            t.wrapMode = TextureWrapMode.Clamp;
            return t;
        }

        private void Build()
        {
            Bg      = new Color(0.07f, 0.08f, 0.10f, 0.96f);
            Panel   = new Color(0.11f, 0.12f, 0.15f, 1f);
            Row     = new Color(0.13f, 0.14f, 0.17f, 1f);
            RowAlt  = new Color(0.16f, 0.17f, 0.21f, 1f);
            Text    = new Color(0.92f, 0.93f, 0.96f, 1f);
            TextDim = new Color(0.58f, 0.61f, 0.68f, 1f);
            Accent  = new Color(0.40f, 0.69f, 1.00f, 1f);
            Good    = new Color(0.40f, 0.85f, 0.55f, 1f);
            Warn    = new Color(1.00f, 0.72f, 0.30f, 1f);
            Line    = new Color(0.25f, 0.27f, 0.33f, 1f);

            _texBg = Solid(Bg);
            _texPanel = Solid(Panel);
            _texRow = Solid(Row);
            _texRowAlt = Solid(RowAlt);
            _texTabOn = Solid(Accent);
            _texLine = Solid(Line);

            Window = new GUIStyle(GUI.skin.window);
            Window.normal.background = _texBg;
            Window.onNormal.background = _texBg;
            Window.normal.textColor = Text;
            Window.onNormal.textColor = Text;
            Window.fontStyle = FontStyle.Bold;
            Window.border = new RectOffset(6, 6, 6, 6);
            Window.padding = new RectOffset(8, 8, 22, 8);

            Label = new GUIStyle(GUI.skin.label);
            Label.normal.textColor = Text;
            Label.wordWrap = false;
            Label.padding = new RectOffset(4, 4, 1, 1);
            Label.margin = new RectOffset(0, 0, 0, 0);

            Dim = new GUIStyle(Label);
            Dim.normal.textColor = TextDim;

            Small = new GUIStyle(Label);
            Small.normal.textColor = TextDim;
            Small.fontSize = 10;

            Title = new GUIStyle(Label);
            Title.fontStyle = FontStyle.Bold;
            Title.normal.textColor = Accent;

            Header = new GUIStyle(Label);
            Header.fontStyle = FontStyle.Bold;
            Header.normal.textColor = TextDim;
            Header.fontSize = 10;

            Value = new GUIStyle(Label);
            Value.alignment = TextAnchor.MiddleRight;

            RowStyle = new GUIStyle(GUI.skin.box);
            RowStyle.normal.background = _texRow;
            RowStyle.border = new RectOffset(2, 2, 2, 2);
            RowStyle.margin = new RectOffset(0, 0, 1, 0);
            RowStyle.padding = new RectOffset(4, 4, 2, 2);

            RowAltStyle = new GUIStyle(RowStyle);
            RowAltStyle.normal.background = _texRowAlt;

            Tab = new GUIStyle(GUI.skin.button);
            Tab.normal.background = _texPanel;
            Tab.hover.background = _texRowAlt;
            Tab.active.background = _texRowAlt;
            Tab.normal.textColor = TextDim;
            Tab.hover.textColor = Text;
            Tab.fontStyle = FontStyle.Bold;
            Tab.border = new RectOffset(2, 2, 2, 2);
            Tab.margin = new RectOffset(0, 2, 0, 4);
            Tab.padding = new RectOffset(6, 6, 4, 4);

            TabOn = new GUIStyle(Tab);
            TabOn.normal.background = _texTabOn;
            TabOn.hover.background = _texTabOn;
            TabOn.normal.textColor = new Color(0.05f, 0.06f, 0.08f);
            TabOn.hover.textColor = TabOn.normal.textColor;

            Btn = new GUIStyle(Tab);
            Btn.fontStyle = FontStyle.Normal;
        }

        public Texture2D LineTex { get { return _texLine; } }

        /// <summary>
        /// Filled horizontal bar, used for prize probability. Draws a WHITE texture tinted via
        /// GUI.color - tinting an already-coloured texture multiplies the two and comes out wrong.
        /// </summary>
        public void Bar(Rect r, float fill01, Color color)
        {
            var prev = GUI.color;
            var white = Texture2D.whiteTexture;
            GUI.color = new Color(Line.r, Line.g, Line.b, 0.6f);
            GUI.DrawTexture(r, white);
            GUI.color = color;
            var f = new Rect(r.x, r.y, r.width * Mathf.Clamp01(fill01), r.height);
            GUI.DrawTexture(f, white);
            GUI.color = prev;
        }

        public void HLine(float width)
        {
            var r = GUILayoutUtility.GetRect(width, 1f, GUILayout.ExpandWidth(true));
            var prev = GUI.color;
            GUI.color = Line;
            GUI.DrawTexture(r, _texLine);
            GUI.color = prev;
        }
    }
}
