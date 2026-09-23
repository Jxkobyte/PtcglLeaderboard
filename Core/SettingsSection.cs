using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PrizeTracker.Core
{
    /// <summary>
    /// Adds a PRIZE TRACKER card to the game's own Settings screen, alongside SOUND / VIDEO
    /// OPTIONS / LANGUAGE, carrying exactly one control: whether the mod is on.
    ///
    /// That is the whole of the mod's settings. Sharing the season record is simply part of
    /// what the mod does, so it has no switch of its own - turning the mod off is what stops it,
    /// along with everything else. Only this card survives being switched off, so it can be
    /// switched back on.
    ///
    /// The card is a CLONE of one of the game's existing settings cards, so it inherits the
    /// card background, padding, header styling and font; only the contents are replaced. It
    /// is recreated whenever it goes missing, because the settings screen is rebuilt each time
    /// it opens.
    ///
    /// NEVER while a match is running. The in-match settings menu is the same screen, and a card
    /// injected there pushed the client's own controls down - it hid the CONCEDE button. So the
    /// card exists in the lobby's settings only; in a match the screen is left exactly as the
    /// client built it.
    /// </summary>
    internal class SettingsSection : MonoBehaviour
    {
        private const string SectionName = "PrizeTrackerSettings";

        /// <summary>Read and write the one setting. Wired by Plugin, which owns the config.</summary>
        public Func<bool> IsEnabled;
        public Action<bool> SetEnabled;

        private float _next;
        private bool _logged;
        private bool _failed;

        private Image _box, _tick;

        // The Settings screen's own checkbox design: a rounded square that is light grey when
        // off and fills solid red with a white tick when on.
        private static readonly Color ClientRed = new Color(0.890f, 0.035f, 0.110f, 1f);
        private static readonly Color BoxOff = new Color(0.905f, 0.905f, 0.905f, 1f);

        private void Update()
        {
            if (_failed || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;

            // Hands off the in-match settings menu, see the class comment.
            if (Game.InMatch()) return;

            try { Ensure(); }
            catch (Exception e)
            {
                _failed = true;
                Plugin.Log.LogWarning("settings section disabled: " + e.Message);
            }
        }

        /// <summary>True when any node we must preserve lives underneath <paramref name="t"/>.</summary>
        private static bool HoldsAny(Transform t, HashSet<Transform> keep)
        {
            foreach (var k in keep) if (k == t || k.IsChildOf(t)) return true;
            return false;
        }

        private void Ensure()
        {
            var labels = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>()
                .Where(l => l != null && l.canvas != null && l.gameObject.activeInHierarchy)
                .ToArray();

            // The settings screen is identified by a heading only it has.
            var anchorLabel = labels.FirstOrDefault(l =>
            {
                var t = (l.text ?? "").Trim();
                return string.Equals(t, "VIDEO OPTIONS", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t, "SOUND", StringComparison.OrdinalIgnoreCase);
            });
            if (anchorLabel == null) return;

            // Walk up to the whole card: the child of the list that contains this heading.
            var card = CardRoot(anchorLabel.transform);
            if (card == null || card.parent == null) return;

            foreach (Transform sib in card.parent)
                if (sib.name == SectionName) { Refresh(); return; }

            var copy = UnityEngine.Object.Instantiate(card.gameObject, card.parent);
            copy.name = SectionName;
            copy.SetActive(true);
            copy.transform.SetSiblingIndex(card.GetSiblingIndex() + 1);

            // Keep the card's own components intact - the Image, and the client's
            // CenterPivotDimensionMatch sizer that gives it its height. Stripping those is what
            // collapsed it onto its neighbours last time. Only the CONTENTS are replaced.
            var allLabels = copy.GetComponentsInChildren<TextMeshProUGUI>(true).ToList();
            var title = allLabels.FirstOrDefault();
            if (title == null) { UnityEngine.Object.Destroy(copy); return; }

            // Remove each control the original card carried - CONTAINER AND ALL, not just the
            // control itself. Destroying every Selectable took out a TMP_Dropdown but left its
            // sibling "Shadow" plate behind as a grey bar across our rows. Decorative pieces are
            // left alone, so the card keeps its background, mask and accent.
            var keep = new HashSet<Transform>();
            for (var t = title.transform; t != null && t != copy.transform; t = t.parent) keep.Add(t);

            var doomed = new HashSet<GameObject>();
            var junk = new List<Component>();
            junk.AddRange(copy.GetComponentsInChildren<Selectable>(true).Cast<Component>());
            for (int i = 1; i < allLabels.Count; i++) junk.Add(allLabels[i]);

            foreach (var c in junk)
            {
                if (c == null) continue;
                // Climb to the outermost wrapper that exists only to hold this control.
                var node = c.transform;
                while (node.parent != null && node.parent != copy.transform &&
                       !keep.Contains(node.parent) && !HoldsAny(node.parent, keep))
                    node = node.parent;
                if (node == copy.transform || keep.Contains(node) || HoldsAny(node, keep)) continue;
                doomed.Add(node.gameObject);
            }
            foreach (var go in doomed) if (go != null) UnityEngine.Object.Destroy(go);

            // LocText would rewrite our heading back to the original on the next refresh.
            foreach (var c in title.GetComponents<MonoBehaviour>())
                if (c != null && !(c is TextMeshProUGUI)) UnityEngine.Object.Destroy(c);
            title.text = "PRIZE TRACKER";

            var panel = title.transform.parent != null ? title.transform.parent : copy.transform;
            BuildRow(copy.transform as RectTransform, panel, title);

            if (!_logged)
            {
                _logged = true;
                Plugin.Log.LogInfo("settings section added: cloned \"" + card.name + "\" into \"" +
                                   card.parent.name + "\".");
            }
        }

        /// <summary>
        /// The card is the ancestor whose PARENT owns the list layout.
        ///
        /// Confirmed from a live dump of the screen: the settings list is a "Content" object with
        /// a VerticalLayoutGroup and a ContentSizeFitter, and each card is a direct child of it.
        /// </summary>
        private static Transform CardRoot(Transform heading)
        {
            for (var t = heading; t != null && t.parent != null; t = t.parent)
                if (t.parent.GetComponent<VerticalLayoutGroup>() != null)
                    return t;
            return null;
        }

        /// <summary>The one row: a caption on the left, the client-style checkbox on the right.</summary>
        private void BuildRow(RectTransform card, Transform panel, TextMeshProUGUI template)
        {
            const float y = 96f;   // clear of the card's title
            const float h = 44f;

            // Inset to where the client puts its own captions and controls (SCREEN RESOLUTION,
            // WINDOWED) rather than flush with the card edge, and in the smaller caption size.
            const float inset = 125f;
            var label = CloneLabel(template, panel, "ENABLED", TextAlignmentOptions.MidlineLeft);
            label.fontSize = template.fontSize * 0.85f;
            Place(label.rectTransform, 0, 1, 0, 1, inset, -y, 420, 34);

            _box = Img("Box", panel, GameArt.Sprite("checkboxBG"), BoxOff);
            Place(_box.rectTransform, 1, 1, 1, 0.5f, -inset, -(y + 17), 40, 40);
            _tick = Img("Tick", _box.transform, GameArt.Sprite("Checkmark"), Color.white);
            Place(_tick.rectTransform, 0.5f, 0.5f, 0.5f, 0.5f, 0, 0, 26, 26);

            // The whole row is the hit target, not just the box, like the client's own rows.
            var hitGo = new GameObject("Hit", typeof(RectTransform));
            hitGo.transform.SetParent(panel, false);
            var hitRt = (RectTransform)hitGo.transform;
            hitRt.anchorMin = new Vector2(0, 1);
            hitRt.anchorMax = new Vector2(1, 1);
            hitRt.pivot = new Vector2(0.5f, 1);
            hitRt.anchoredPosition = new Vector2(0, -(y - 5f));
            hitRt.sizeDelta = new Vector2(0, h);
            hitRt.localScale = Vector3.one;
            var hit = hitGo.AddComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, 0f);
            hit.raycastTarget = true;
            var btn = hitGo.AddComponent<Button>();
            btn.targetGraphic = hit;
            btn.onClick.AddListener(() =>
            {
                if (IsEnabled == null || SetEnabled == null) return;
                SetEnabled(!IsEnabled());
                Refresh();
            });

            // Grow the card to fit: its height came from the card we cloned, sized for that
            // card's contents.
            SetCardHeight(card, y + h + 24f);
            Refresh();
        }

        private static Image Img(string name, Transform parent, Sprite sprite, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            img.raycastTarget = false;
            img.type = Image.Type.Simple;
            img.preserveAspect = true;
            return img;
        }

        private void Refresh()
        {
            bool on = IsEnabled != null && IsEnabled();
            if (_box != null) _box.color = on ? ClientRed : BoxOff;
            if (_tick != null) _tick.color = on ? Color.white : new Color(0f, 0f, 0f, 0f);
        }

        private static TextMeshProUGUI CloneLabel(TextMeshProUGUI template, Transform parent,
                                                  string text, TextAlignmentOptions align)
        {
            var go = UnityEngine.Object.Instantiate(template.gameObject, parent);
            go.SetActive(true);
            foreach (var c in go.GetComponents<MonoBehaviour>())
                if (c != null && !(c is TextMeshProUGUI)) UnityEngine.Object.Destroy(c);
            foreach (var child in go.GetComponentsInChildren<Transform>(true))
                if (child != go.transform) UnityEngine.Object.Destroy(child.gameObject);

            var lbl = go.GetComponent<TextMeshProUGUI>();
            lbl.text = text;
            lbl.alignment = align;
            lbl.enableWordWrapping = false;
            lbl.overflowMode = TextOverflowModes.Overflow;
            lbl.raycastTarget = false;
            go.GetComponent<RectTransform>().localScale = Vector3.one;
            return lbl;
        }

        /// <summary>
        /// Drive the height through whichever mechanism this list actually honours: a vertical
        /// layout group reads LayoutElement, without one the rect's own size counts. A
        /// ContentSizeFitter would override us, so it is switched off rather than destroyed -
        /// removing components from this card is what collapsed it onto its neighbours before.
        /// </summary>
        private static void SetCardHeight(RectTransform card, float h)
        {
            if (card == null) return;

            foreach (var fit in card.GetComponents<ContentSizeFitter>())
                fit.verticalFit = ContentSizeFitter.FitMode.Unconstrained;

            var le = card.GetComponent<LayoutElement>();
            if (le == null) le = card.gameObject.AddComponent<LayoutElement>();
            le.minHeight = h;
            le.preferredHeight = h;

            card.sizeDelta = new Vector2(card.sizeDelta.x, h);
        }

        private static void Place(RectTransform rt, float ax, float ay, float px, float py,
                                  float x, float y, float w, float h)
        {
            rt.anchorMin = new Vector2(ax, ay);
            rt.anchorMax = new Vector2(ax, ay);
            rt.pivot = new Vector2(px, py);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);
            rt.localScale = Vector3.one;
        }
    }
}
