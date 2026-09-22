using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PrizeTracker.Core
{
    /// <summary>
    /// The match history screen, built as real Unity UI so it sits in the game's own canvas and
    /// uses the game's own font.
    ///
    /// Every text element is cloned from a TextMeshPro label already in the scene. That is the
    /// difference between looking native and looking bolted on: the client uses a specific TMP font
    /// asset and material, and a label created from scratch renders in the wrong typeface (or not at
    /// all). Cloning inherits all of it.
    ///
    /// This is not a HUBScreenController - it does not join the game's screen stack. It is a panel
    /// drawn over the menu and dismissed by its own close button, the nav tab, or leaving the menus.
    /// </summary>
    internal class HistoryScreen : MonoBehaviour
    {
        public MatchHistory History;

        private GameObject _root;
        private TextMeshProUGUI _fontSource;
        private readonly List<GameObject> _rows = new List<GameObject>();
        private bool _visible;
        private bool _failed;

        /// <summary>Height of the game's top navigation bar, left clickable.</summary>
        private const float NavBarHeight = 72f;

        private static readonly Color Bg = new Color(0.04f, 0.05f, 0.07f, 0.97f);
        private static readonly Color Panel = new Color(0.09f, 0.10f, 0.13f, 1f);
        private static readonly Color RowA = new Color(0.12f, 0.13f, 0.17f, 1f);
        private static readonly Color RowB = new Color(0.15f, 0.16f, 0.20f, 1f);
        private static readonly Color Text = new Color(0.93f, 0.94f, 0.97f, 1f);
        private static readonly Color Dim = new Color(0.60f, 0.64f, 0.71f, 1f);
        private static readonly Color Gold = new Color(1.00f, 0.80f, 0.30f, 1f);
        private static readonly Color Good = new Color(0.42f, 0.86f, 0.56f, 1f);
        private static readonly Color Bad = new Color(1.00f, 0.50f, 0.42f, 1f);

        public bool Visible { get { return _visible; } }

        public void Toggle() { if (_visible) Hide(); else Show(); }

        public void Hide()
        {
            _visible = false;
            if (_root != null) _root.SetActive(false);
        }

        public void Show()
        {
            if (_failed) return;
            try
            {
                Build();
                if (_root == null) return;
                _root.SetActive(true);
                _root.transform.SetAsLastSibling();
                _visible = true;
                Populate();
                var rt = _root.GetComponent<RectTransform>();
                Plugin.Log.LogInfo("match history screen shown: size=" + rt.rect.size +
                                   " rows=" + _rows.Count);
            }
            catch (Exception e)
            {
                _failed = true;
                Plugin.Log.LogWarning("match history screen failed: " + e.Message);
            }
        }

        // ---------------------------------------------------------------
        private void Build()
        {
            if (_root != null) return;

            _fontSource = FindFontSource();
            if (_fontSource == null) { Plugin.Log.LogWarning("match history screen: no TMP label to clone"); return; }

            // Stand the screen up on its OWN root canvas rather than borrowing the one that
            // happened to own the label we cloned. It did open last time - the log recorded it -
            // but it lived under the "ItemSelect" canvas, so navigating to another screen took our
            // panel away with it.
            var host = new GameObject("PrizeTrackerHistoryCanvas");
            UnityEngine.Object.DontDestroyOnLoad(host);

            var ownCanvas = host.AddComponent<Canvas>();
            ownCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            ownCanvas.sortingOrder = 30000;

            var scaler = host.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            host.AddComponent<GraphicRaycaster>();

            _root = NewRect("PrizeTrackerHistoryScreen", host.transform, Bg);

            // Leave the top bar uncovered. A full-screen backdrop with a raycaster swallowed every
            // click aimed at the nav bar, so you could open this screen but not navigate away from
            // it. Starting below the bar keeps the real tabs live.
            Stretch(_root, 0, NavBarHeight, 0, 0);

            // Clicking the dim area closes, the way the game's own overlays behave.
            var dismiss = _root.AddComponent<Button>();
            dismiss.targetGraphic = _root.GetComponent<Image>();
            dismiss.onClick.AddListener(Hide);

            Plugin.Log.LogInfo("match history screen built on its own overlay canvas.");

            var panel = NewRect("Panel", _root.transform, Panel);
            Stretch(panel, 140, 24, 140, 60);

            // The panel itself must not pass clicks through to the dismiss backdrop behind it.
            var block = panel.AddComponent<Button>();
            block.targetGraphic = panel.GetComponent<Image>();
            block.transition = Selectable.Transition.None;

            var title = NewLabel(panel.transform, "MATCH HISTORY", 34, Gold, TextAlignmentOptions.TopLeft);
            Place(title, 0, 1, 0, 1, 34, -26, 620, 46);

            var close = NewRect("Close", panel.transform, new Color(0.22f, 0.24f, 0.30f, 1f));
            Place(close, 1, 1, 1, 1, -34, -26, 46, 40);
            var closeLabel = NewLabel(close.transform, "X", 22, Text, TextAlignmentOptions.Center);
            Stretch(closeLabel, 0, 0, 0, 0);
            var btn = close.AddComponent<Button>();
            btn.targetGraphic = close.GetComponent<Image>();
            btn.onClick.AddListener(Hide);

            var body = NewRect("Body", panel.transform, new Color(0, 0, 0, 0));
            Stretch(body, 34, 92, 34, 30);
            body.name = "Body";
        }

        private void Populate()
        {
            foreach (var r in _rows) if (r != null) UnityEngine.Object.Destroy(r);
            _rows.Clear();

            var body = _root.transform.Find("Panel/Body");
            if (body == null || History == null) return;

            float y = 0f;

            var all = History.Overall();
            var header = History.Count == 0
                ? "No matches recorded yet - finish a game and it will appear here."
                : all.Wins + "W - " + all.Losses + "L      " +
                  Mathf.RoundToInt((float)(all.Rate * 100f)) + "%      streak " + History.Streak();
            y = AddLine(body, header, 22, History.Count == 0 ? Dim : Text, y, 34);

            y += 10;
            y = AddSection(body, "BY DECK", y);
            foreach (var d in Game.AllDecks().Take(10))
            {
                var w = History.ForDeck(d.Id, d.Name);
                y = AddRow(body, d.Name,
                           w.Played == 0 ? "0-0" : w.Wins + "-" + w.Losses,
                           w.Played == 0 ? "" : Mathf.RoundToInt((float)(w.Rate * 100f)) + "%",
                           w.Played == 0 ? Dim : (w.Rate >= 0.55 ? Good : w.Rate < 0.45 ? Bad : Text),
                           y);
            }

            if (History.Count > 0)
            {
                y += 10;
                y = AddSection(body, "RECENT MATCHES", y);
                foreach (var m in History.Recent(10))
                {
                    var opp = string.IsNullOrEmpty(m.OppArchetype) ? "(unknown deck)" : m.OppArchetype;
                    y = AddRow(body, (m.Won ? "WIN" : "LOSS") + "   vs " + opp,
                               m.MyDeck, m.MyPrizesLeft + "-" + m.OppPrizesLeft,
                               m.Won ? Good : Bad, y);
                }
            }
        }

        // ---------------------------------------------------------------
        private float AddSection(Transform parent, string text, float y)
        {
            var go = NewLabel(parent, text, 16, Dim, TextAlignmentOptions.MidlineLeft);
            Place(go, 0, 1, 0, 1, 2, -y, 900, 24);
            _rows.Add(go);
            return y + 28;
        }

        private float AddLine(Transform parent, string text, int size, Color color, float y, float h)
        {
            var go = NewLabel(parent, text, size, color, TextAlignmentOptions.MidlineLeft);
            Place(go, 0, 1, 0, 1, 2, -y, 1200, h);
            _rows.Add(go);
            return y + h + 4;
        }

        private float AddRow(Transform parent, string left, string mid, string right, Color color, float y)
        {
            var row = NewRect("Row", parent, (_rows.Count % 2 == 0) ? RowA : RowB);
            Place(row, 0, 1, 0, 1, 0, -y, 1240, 34);
            _rows.Add(row);

            var l = NewLabel(row.transform, left, 19, color, TextAlignmentOptions.MidlineLeft);
            Place(l, 0, 0.5f, 0, 0.5f, 14, 0, 700, 30);

            var m = NewLabel(row.transform, mid, 18, Dim, TextAlignmentOptions.MidlineRight);
            Place(m, 1, 0.5f, 1, 0.5f, -130, 0, 360, 30);

            var r = NewLabel(row.transform, right, 19, color, TextAlignmentOptions.MidlineRight);
            Place(r, 1, 0.5f, 1, 0.5f, -18, 0, 100, 30);

            return y + 38;
        }

        // ---------------------------------------------------------------
        /// <summary>A TMP label from the live scene, so clones inherit the game's font asset.</summary>
        private TextMeshProUGUI FindFontSource()
        {
            if (_fontSource != null && _fontSource.font != null) return _fontSource;
            return Resources.FindObjectsOfTypeAll<TextMeshProUGUI>()
                            .FirstOrDefault(t => t != null && t.font != null && t.canvas != null);
        }

        private GameObject NewRect(string name, Transform parent, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            if (color.a > 0f)
            {
                var img = go.AddComponent<Image>();
                img.color = color;
                img.raycastTarget = true;   // the backdrop swallows clicks meant for the menu behind
            }
            return go;
        }

        private GameObject NewLabel(Transform parent, string text, int size, Color color,
                                    TextAlignmentOptions align)
        {
            // Cloned, not constructed: a fresh TextMeshProUGUI has no font asset assigned and
            // renders as nothing.
            var src = FindFontSource();
            var go = UnityEngine.Object.Instantiate(src.gameObject, parent);
            go.name = "Label";
            go.SetActive(true);

            foreach (var child in go.GetComponentsInChildren<Transform>(true))
                if (child != go.transform) UnityEngine.Object.Destroy(child.gameObject);
            foreach (var c in go.GetComponents<MonoBehaviour>())
            {
                if (c == null || c is TextMeshProUGUI) continue;
                UnityEngine.Object.Destroy(c);
            }

            var label = go.GetComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = size;
            label.color = color;
            label.alignment = align;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.raycastTarget = false;
            go.GetComponent<RectTransform>().localScale = Vector3.one;
            return go;
        }

        private static void Stretch(GameObject go, float l, float t, float r, float b)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(l, b);
            rt.offsetMax = new Vector2(-r, -t);
            rt.localScale = Vector3.one;
        }

        private static void Place(GameObject go, float ax, float ay, float px, float py,
                                  float x, float y, float w, float h)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(ax, ay);
            rt.anchorMax = new Vector2(ax, ay);
            rt.pivot = new Vector2(px, py);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);
            rt.localScale = Vector3.one;
        }
    }
}
