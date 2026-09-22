using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PrizeTracker.Core
{
    /// <summary>
    /// Adds a PRIZE TRACKER section to the game's own Settings screen, under GENERAL, alongside
    /// SOUND / VIDEO OPTIONS / LANGUAGE.
    ///
    /// The section is a CLONE of one of the game's existing settings cards, so it inherits the
    /// card background, padding, header styling and font. Only the contents are replaced. That is
    /// what makes it read as part of the screen rather than something pasted on top.
    ///
    /// It is recreated whenever it goes missing, because the settings screen is rebuilt each time
    /// it opens. Everything is wrapped: a failure must never break Settings.
    /// </summary>
    internal class SettingsSection : MonoBehaviour
    {
        private const string SectionName = "PrizeTrackerSettings";

        public PerformanceTuner Perf;
        public Action OnChanged;

        private float _next;
        private bool _logged;
        private bool _failed;

        private TextMeshProUGUI _fpsValue, _unfocusedValue, _badgeValue;

        private Transform _dumpRoot;

        private void Update()
        {
            if (_failed || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            if (_dumpRoot != null) { var d = _dumpRoot; _dumpRoot = null; DumpCard(d); }
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
            // control itself.
            //
            // That distinction is the whole bug. Destroying every Selectable took out the
            // TMP_Dropdown but left its sibling "Shadow" (a 359x62 black-40% image) behind, and
            // that is the grey bar sitting across our rows. Decorative pieces are left alone, so
            // the card keeps its background, mask and accent and still looks like a card.
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
            BuildRows(copy.transform as RectTransform, panel, title);

            if (!_logged)
            {
                _logged = true;
                Plugin.Log.LogInfo("settings section added: cloned \"" + card.name + "\" into \"" +
                                   card.parent.name + "\".");
                // Dump on the NEXT tick, not here. Object.Destroy is deferred to the end of the
                // frame, so a dump taken now still lists everything we just removed - which is
                // exactly how the last read of this log pointed at the wrong culprit.
                _dumpRoot = copy.transform;
            }
        }

        /// <summary>What is actually left inside our card, read after the deletes have landed.</summary>
        private static void DumpCard(Transform root)
        {
            if (root == null) { Plugin.Log.LogInfo("settings card: gone before dump."); return; }
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var comps = string.Join(",", t.GetComponents<Component>()
                    .Where(c => c != null && !(c is RectTransform))
                    .Select(c => c.GetType().Name).ToArray());
                var img = t.GetComponent<Image>();
                var rt = t as RectTransform;
                Plugin.Log.LogInfo("  card child \"" + t.name + "\" active=" + t.gameObject.activeSelf +
                    (rt != null ? " size=" + rt.rect.width.ToString("F0") + "x" + rt.rect.height.ToString("F0") : "") +
                    (img != null ? " imgColor=" + img.color : "") + " [" + comps + "]");
            }
        }

        /// <summary>
        /// Dump the settings screen's structure on demand (F6).
        ///
        /// Guessing how this list is built has failed twice, and each guess costs a full game
        /// restart to test. Capturing the real hierarchy while the screen is open - including what
        /// is scrolled into view - is far cheaper than another blind attempt.
        /// </summary>
        public void DumpNow()
        {
            try
            {
                var labels = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>()
                    .Where(l => l != null && l.canvas != null && l.gameObject.activeInHierarchy)
                    .ToArray();

                var anchorLabel = labels.FirstOrDefault(l =>
                {
                    var t = (l.text ?? "").Trim();
                    return string.Equals(t, "VIDEO OPTIONS", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(t, "SOUND", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(t, "LANGUAGE", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(t, "BATTLE OPTIONS", StringComparison.OrdinalIgnoreCase);
                });

                if (anchorLabel == null)
                {
                    Plugin.Log.LogInfo("[dump] settings screen not open. visible labels: " +
                        string.Join(" | ", labels.Select(l => (l.text ?? "").Trim())
                                                 .Where(t => t.Length > 0 && t.Length < 24)
                                                 .Distinct().Take(25).ToArray()));
                    return;
                }

                Plugin.Log.LogInfo("[dump] ---- settings hierarchy from \"" + anchorLabel.text.Trim() + "\" ----");

                // Walk up so we can see which ancestor actually owns the list layout.
                int level = 0;
                for (var t = anchorLabel.transform; t != null && level < 7; t = t.parent, level++)
                    Plugin.Log.LogInfo("[dump] up" + level + ": " + Describe(t));

                // Then the list itself: every sibling card, in order.
                var card = CardRoot(anchorLabel.transform);
                if (card != null && card.parent != null)
                {
                    Plugin.Log.LogInfo("[dump] list container: " + Describe(card.parent));
                    int i = 0;
                    foreach (Transform sib in card.parent)
                    {
                        var head = sib.GetComponentInChildren<TextMeshProUGUI>(true);
                        Plugin.Log.LogInfo("[dump]   [" + (i++) + "] " + Describe(sib) +
                                           " label=\"" + (head != null ? (head.text ?? "").Trim() : "") + "\"");
                    }
                }
                // Also dump the top nav bar, so the real tabs' structure and their selected
                // styling can be compared against the clone.
                var home = labels.FirstOrDefault(l =>
                    string.Equals((l.text ?? "").Trim(), "HOME", StringComparison.OrdinalIgnoreCase));
                if (home != null)
                {
                    for (var t = home.transform; t != null && t.parent != null; t = t.parent)
                    {
                        if (t.parent.name != "Selections") continue;
                        Plugin.Log.LogInfo("[dump] ---- nav bar tabs ----");
                        foreach (Transform tab in t.parent)
                        {
                            var lbl = tab.GetComponentInChildren<TextMeshProUGUI>(true);
                            var active = string.Join(",", tab.GetComponentsInChildren<Transform>(true)
                                .Where(x => x != tab && x.gameObject.activeSelf)
                                .Select(x => x.name).Take(8).ToArray());
                            Plugin.Log.LogInfo("[dump]   tab " + Describe(tab) +
                                " text=\"" + (lbl != null ? (lbl.text ?? "").Trim() : "") +
                                "\" color=" + (lbl != null ? lbl.color.ToString() : "?") +
                                " activeChildren=[" + active + "]");
                        }
                        break;
                    }
                }

                Plugin.Log.LogInfo("[dump] ---- end ----");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[dump] failed: " + e.Message); }
        }

        private static string Describe(Transform t)
        {
            var rt = t as RectTransform;
            var comps = string.Join(",", t.GetComponents<Component>()
                .Where(c => c != null && !(c is RectTransform))
                .Select(c => c.GetType().Name).ToArray());
            string geo = rt == null ? "" :
                " pos=" + rt.anchoredPosition.x.ToString("F0") + "," + rt.anchoredPosition.y.ToString("F0") +
                " size=" + rt.rect.width.ToString("F0") + "x" + rt.rect.height.ToString("F0") +
                " anchors=" + rt.anchorMin.x.ToString("F2") + "," + rt.anchorMin.y.ToString("F2") +
                "-" + rt.anchorMax.x.ToString("F2") + "," + rt.anchorMax.y.ToString("F2");
            return "\"" + t.name + "\" children=" + t.childCount + geo + " [" + comps + "]";
        }

        /// <summary>
        /// The card is the ancestor whose PARENT owns the list layout.
        ///
        /// Confirmed from a live dump of the screen rather than guessed: the settings list is a
        /// "Content" object with a VerticalLayoutGroup and a ContentSizeFitter, and each card
        /// (BattleGroup, and so on) is a direct child of it. Earlier versions walked to the wrong
        /// ancestor entirely, which is why the card overlapped instead of taking a slot.
        /// </summary>
        private static Transform CardRoot(Transform heading)
        {
            for (var t = heading; t != null && t.parent != null; t = t.parent)
                if (t.parent.GetComponent<VerticalLayoutGroup>() != null)
                    return t;
            return null;
        }

        private void BuildRows(RectTransform card, Transform panel, TextMeshProUGUI template)
        {
            float y = 96f;   // clear of the card's title
            _fpsValue = AddRow(panel, template, "FRAME RATE CAP", ref y,
                               () => Step(-1), () => Step(+1));
            _unfocusedValue = AddRow(panel, template, "WHEN NOT FOCUSED", ref y,
                               () => StepUnfocused(-1), () => StepUnfocused(+1));
            _badgeValue = AddToggle(panel, template, "DECK WIN RATES", ref y,
                               () => DeckBadge.Enabled = !DeckBadge.Enabled);

            // Grow the card to fit. Its height came from the card we cloned, sized for that card's
            // contents - our rows are absolutely positioned, so nothing else tells the layout how
            // tall this needs to be and the last rows spilled onto the card below.
            SetCardHeight(card, y + 24f);
            Refresh();
        }

        /// <summary>One caption + value row, with optional -/+ controls on the right.</summary>
        private TextMeshProUGUI AddRow(Transform panel, TextMeshProUGUI template, string caption,
                                       ref float y, Action minus, Action plus)
        {
            var label = CloneLabel(template, panel, caption, TextAlignmentOptions.MidlineLeft);
            Place(label.rectTransform, 0, 1, 0, 1, 34, -y, 420, 34);

            var value = CloneLabel(template, panel, "", TextAlignmentOptions.MidlineRight);

            if (minus != null)
            {
                MakeButton(panel, template, "-", -168, y, minus);
                MakeButton(panel, template, "+", -34, y, plus);
                Place(value.rectTransform, 1, 1, 1, 1, -92, -y, 70, 34);
            }
            else
            {
                Place(value.rectTransform, 1, 1, 1, 1, -34, -y, 240, 34);
            }

            y += 44f;
            return value;
        }

        private void MakeButton(Transform card, TextMeshProUGUI template, string text,
                                float x, float y, Action onClick)
        {
            var go = new GameObject("Btn" + text, typeof(RectTransform));
            go.transform.SetParent(card, false);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.82f, 0.13f, 0.15f, 1f);   // the client's red control colour
            Place(go.GetComponent<RectTransform>(), 1, 1, 0.5f, 0.5f, x, -(y + 17), 44, 34);

            var lbl = CloneLabel(template, go.transform, text, TextAlignmentOptions.Center);
            Stretch(lbl.rectTransform);
            lbl.color = Color.white;   // the title's dark grey is unreadable on the red

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() => { onClick(); Refresh(); if (OnChanged != null) OnChanged(); });
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

        private static readonly int[] Steps = { 0, 30, 45, 60, 75, 90, 120, 144, 165, 240 };

        private void Step(int dir)
        {
            if (Perf == null) return;
            int i = Array.IndexOf(Steps, Perf.MatchFps);
            if (i < 0) i = Array.FindLastIndex(Steps, v => v < Perf.MatchFps);
            if (i < 0) i = 0;
            Perf.MatchFps = Steps[Mathf.Clamp(i + dir, 0, Steps.Length - 1)];
            Perf.MenuFps = Perf.MatchFps;
            Perf.Apply();
        }

        /// <summary>
        /// Drive the height through whichever mechanism this list actually honours.
        ///
        /// A vertical layout group reads LayoutElement; without one the rect's own size is what
        /// counts. Setting both is harmless and avoids another restart spent finding out which.
        /// A ContentSizeFitter would override us, so it is switched off rather than destroyed -
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

        private static readonly int[] UnfocusedSteps = { 0, 5, 10, 15, 20, 30, 60 };

        private void StepUnfocused(int dir)
        {
            if (Perf == null) return;
            int i = Array.IndexOf(UnfocusedSteps, Perf.UnfocusedFps);
            if (i < 0) i = Array.FindLastIndex(UnfocusedSteps, v => v < Perf.UnfocusedFps);
            if (i < 0) i = 0;
            Perf.UnfocusedFps = UnfocusedSteps[Mathf.Clamp(i + dir, 0, UnfocusedSteps.Length - 1)];
            Perf.Apply();
        }

        /// <summary>A caption with an ON/OFF button, matching the steppers' proportions.</summary>
        private TextMeshProUGUI AddToggle(Transform panel, TextMeshProUGUI template, string caption,
                                          ref float y, Action toggle)
        {
            var label = CloneLabel(template, panel, caption, TextAlignmentOptions.MidlineLeft);
            Place(label.rectTransform, 0, 1, 0, 1, 34, -y, 420, 34);

            var go = new GameObject("Toggle", typeof(RectTransform));
            go.transform.SetParent(panel, false);
            var img = go.AddComponent<Image>();
            Place(go.GetComponent<RectTransform>(), 1, 1, 1f, 0.5f, -34, -(y + 17), 92, 34);

            var value = CloneLabel(template, go.transform, "", TextAlignmentOptions.Center);
            Stretch(value.rectTransform);
            value.color = Color.white;

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() => { toggle(); Refresh(); if (OnChanged != null) OnChanged(); });

            y += 44f;
            return value;
        }

        /// <summary>Colour lives on the button behind the label, so drive both from one place.</summary>
        private static void SetToggle(TextMeshProUGUI value, bool on)
        {
            if (value == null) return;
            value.text = on ? "ON" : "OFF";
            var img = value.transform.parent != null
                ? value.transform.parent.GetComponent<Image>() : null;
            if (img != null)
                img.color = on ? new Color(0.82f, 0.13f, 0.15f, 1f)
                               : new Color(0.62f, 0.64f, 0.68f, 1f);
        }

        private void Refresh()
        {
            if (Perf != null)
            {
                if (_fpsValue != null)
                    _fpsValue.text = Perf.MatchFps <= 0 ? "UNCAPPED" : Perf.MatchFps.ToString();
                if (_unfocusedValue != null)
                    _unfocusedValue.text = Perf.UnfocusedFps <= 0 ? "UNCAPPED" : Perf.UnfocusedFps.ToString();
            }
            SetToggle(_badgeValue, DeckBadge.Enabled);
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

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;
        }
    }
}
