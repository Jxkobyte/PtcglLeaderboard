using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PtcglLeaderboard.Core
{
    /// <summary>
    /// Adds a "MATCH HISTORY" tab to the game's own top navigation bar, next to DECKS.
    ///
    /// The real tabs are HUBScreenControllers driven by MainMenuNavigation.Initialize, with their
    /// own prefabs and screen lifecycle. Registering a genuine one means shipping a prefab and
    /// hooking that lifecycle; instead this CLONES an existing nav button and drives its own panel.
    /// The tab therefore inherits the bar's real font, size, spacing and hover styling, which is
    /// what makes it look like it belongs, without touching the game's screen system.
    ///
    /// The button is rebuilt whenever it goes missing, because the bar is rebuilt on navigation.
    /// Everything is wrapped: a failure here must never break the menu.
    /// </summary>
    internal class NavTab : MonoBehaviour
    {
        // One instance per tab. Defaults are the original Match History tab; the leaderboard sets
        // its own name, caption and position.
        public string TabName = "PtcglLeaderboardHistoryTab";
        public string Caption = "MATCH HISTORY";

        /// <summary>
        /// The caption of the real tab this one goes AFTER. "DECKS" puts it second; "CARD DEX"
        /// puts it at the end of the text tabs, just before the level/currency widgets.
        /// </summary>
        public string InsertAfter = "DECKS";

        public MatchHistory History;
        public HistoryScreen Screen;    // legacy overlay fallback; may be null for a native-only tab

        private float _next;
        private int _reports;
        private bool _failed;

        private List<TextMeshProUGUI> _labels;

        private void ApplyCaption()
        {
            if (_labels == null) return;
            foreach (var lbl in _labels)
            {
                if (lbl == null) continue;
                if (lbl.text != Caption)
                {
                    lbl.text = Caption;
                    lbl.enableWordWrapping = false;
                    lbl.overflowMode = TextOverflowModes.Overflow;
                }
            }
            FitWidth();
        }

        /// <summary>
        /// Widen the tab to fit its caption.
        ///
        /// The clone inherited the DECKS tab's width, and "MATCH HISTORY" is far longer, so the
        /// text overflowed across the tabs either side of it. The bar does make room for the extra
        /// tab - it just needs to be told how much room this one wants.
        /// </summary>
        private void FitWidth()
        {
            if (_tab == null || _labels == null || _labels.Count == 0) return;

            float widest = 0f;
            foreach (var lbl in _labels)
                if (lbl != null) widest = Mathf.Max(widest, lbl.preferredWidth);
            if (widest <= 1f) return;

            float want = widest + 28f;          // breathing room, matching the other tabs' padding
            if (Mathf.Abs(want - _lastWidth) < 1f) return;
            _lastWidth = want;

            var le = _tab.GetComponent<LayoutElement>() ?? _tab.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = want;
            le.minWidth = want;
            le.flexibleWidth = 0f;

            var rt = _tab as RectTransform;
            if (rt != null) rt.sizeDelta = new Vector2(want, rt.sizeDelta.y);

            foreach (var lbl in _labels)
            {
                var lrt = lbl != null ? lbl.rectTransform : null;
                if (lrt != null) lrt.sizeDelta = new Vector2(want, lrt.sizeDelta.y);
            }
        }

        // The real tabs turn yellow and show an underline when active. Those visuals are driven by
        // components that had to be stripped from the clone (they also drive navigation), so the
        // styling is LEARNED from the live tabs instead of hardcoding a colour: whichever sibling
        // is styled differently from the rest is the selected one, and we copy exactly that.
        private struct TabStyle
        {
            public bool Known;
            public Color LabelColor;
            public HashSet<string> ActiveChildren;
        }

        private TabStyle _selected, _unselected;
        private bool _wasOpen;

        private void LearnStyles()
        {
            if (_tab == null || _tab.parent == null) return;

            var siblings = new List<Transform>();
            foreach (Transform sib in _tab.parent)
                if (sib != _tab && sib.GetComponentInChildren<TextMeshProUGUI>(true) != null)
                    siblings.Add(sib);
            if (siblings.Count < 2) return;

            // Group by label colour; the odd one out is the active tab.
            var byColor = new Dictionary<string, List<Transform>>();
            foreach (var sib in siblings)
            {
                var lbl = sib.GetComponentInChildren<TextMeshProUGUI>(true);
                if (lbl == null) continue;
                var key = ColorKey(lbl.color);
                if (!byColor.ContainsKey(key)) byColor[key] = new List<Transform>();
                byColor[key].Add(sib);
            }
            if (byColor.Count < 2) return;

            List<Transform> majority = null, minority = null;
            foreach (var kv in byColor)
            {
                if (majority == null || kv.Value.Count > majority.Count)
                { minority = majority; majority = kv.Value; }
                else if (minority == null || kv.Value.Count < minority.Count)
                    minority = kv.Value;
            }
            if (majority == null || minority == null || minority.Count == 0) return;

            _unselected = Capture(majority[0]);
            _selected = Capture(minority[0]);

            // Report what was learned, once, automatically. Nothing here should need a hotkey.
            if (!_loggedStyles)
            {
                _loggedStyles = true;
                Plugin.Log.LogInfo("nav tab styling learned: selected=" + Format(_selected, minority[0]) +
                                   "  unselected=" + Format(_unselected, majority[0]));
            }
        }

        private static string Format(TabStyle style, Transform sample)
        {
            var lbl = sample != null ? sample.GetComponentInChildren<TextMeshProUGUI>(true) : null;
            return "\"" + (lbl != null ? (lbl.text ?? "").Trim() : "?") + "\" color=" +
                   style.LabelColor + " active=[" +
                   string.Join(",", style.ActiveChildren.Take(8).ToArray()) + "]";
        }

        private bool _loggedStyles;

        private static string ColorKey(Color c)
        {
            return Mathf.RoundToInt(c.r * 20) + "," + Mathf.RoundToInt(c.g * 20) + "," +
                   Mathf.RoundToInt(c.b * 20);
        }

        private static TabStyle Capture(Transform tab)
        {
            var style = new TabStyle { Known = true, ActiveChildren = new HashSet<string>() };
            var lbl = tab.GetComponentInChildren<TextMeshProUGUI>(true);
            if (lbl != null) style.LabelColor = lbl.color;
            foreach (var t in tab.GetComponentsInChildren<Transform>(true))
                if (t != tab && t.gameObject.activeSelf) style.ActiveChildren.Add(t.name);
            return style;
        }

        private void ApplyStyle(bool selected)
        {
            var style = selected ? _selected : _unselected;
            if (!style.Known || _tab == null) return;

            foreach (var lbl in _tab.GetComponentsInChildren<TextMeshProUGUI>(true))
                if (lbl != null) lbl.color = style.LabelColor;

            // Underline, glow, highlight plate - whatever the real tab switches on, by name.
            foreach (var t in _tab.GetComponentsInChildren<Transform>(true))
            {
                if (t == _tab || t.name == "ClickCatcher") continue;
                bool shouldBeOn = style.ActiveChildren.Contains(t.name);
                if (t.gameObject.activeSelf != shouldBeOn) t.gameObject.SetActive(shouldBeOn);
            }
        }

        /// <summary>Highlight our tab while its screen is open, and clear it when it closes.</summary>
        private void SyncSelection()
        {
            if (_tab == null) return;
            LearnStyles();

            bool open = Screen != null && Screen.Visible;

            // Close when the user picks a REAL tab - but only when the selection actually CHANGES.
            //
            // The first version closed whenever any other tab looked selected, and HOME always
            // does: the game has no idea our tab exists, so it never deselects. The screen was
            // therefore hidden on the same frame it opened, which looked exactly like the click
            // doing nothing.
            var selectedNow = SelectedSiblingName();
            if (open)
            {
                if (_selectedWhenOpened == null) _selectedWhenOpened = selectedNow;
                else if (selectedNow != null && selectedNow != _selectedWhenOpened)
                {
                    Screen.Hide();
                    open = false;
                    _selectedWhenOpened = null;
                }
            }
            else _selectedWhenOpened = null;

            if (open != _wasOpen)
            {
                _wasOpen = open;
                RefreshStyle();
            }
        }

        private bool _hovering;

        /// <summary>Selected while the screen is open, highlighted on hover, plain otherwise.</summary>
        private void RefreshStyle()
        {
            bool open = Screen != null && Screen.Visible;
            ApplyStyle(open || _hovering);
        }

        private string _selectedWhenOpened;

        /// <summary>Which real tab is currently highlighted, by name.</summary>
        private string SelectedSiblingName()
        {
            if (!_selected.Known || _tab == null || _tab.parent == null) return null;
            foreach (Transform sib in _tab.parent)
            {
                if (sib == _tab) continue;
                var lbl = sib.GetComponentInChildren<TextMeshProUGUI>(true);
                if (lbl != null && ColorKey(lbl.color) == ColorKey(_selected.LabelColor)) return sib.name;
            }
            return null;
        }

        private Transform _tab;
        private float _lastWidth;
        private bool _native;

        /// <summary>The cloned tab, for NativeScreenInstaller to wire to a real screen.</summary>
        public Transform TabRoot { get { return _tab; } }

        /// <summary>
        /// Take the tab out of the top bar (the mod switched off) or put it back.
        ///
        /// Hidden means this component stops running as well: the bar is rebuilt on every
        /// navigation and Ensure() would re-clone the tab into it. When it is switched back on
        /// Ensure() re-clones as usual, and NativeScreenInstaller re-wires the new clone.
        /// </summary>
        public void SetHidden(bool hidden)
        {
            enabled = !hidden;
            if (_tab != null && _tab.gameObject.activeSelf == hidden) _tab.gameObject.SetActive(!hidden);
        }

        /// <summary>
        /// Hand the tab over to the client's own navigation.
        ///
        /// Two stand-ins go away once a real screen exists behind this tab. The ClickCatcher was an
        /// invisible Image laid over the whole tab because nothing was listening for clicks after
        /// the navigation components were stripped; it now only gets in the way of the tab's REAL
        /// Button, which sits underneath it. And LocText is the localiser that kept rewriting the
        /// caption back to "Decks" - the reason the caption had to be re-applied every frame. It
        /// was missed before because the strip filter looked for "Localiz", which does not match
        /// the name "LocText".
        /// </summary>
        public void GoNative()
        {
            _native = true;
            if (_tab == null) return;

            var catcher = _tab.Find("ClickCatcher");
            if (catcher != null) UnityEngine.Object.Destroy(catcher.gameObject);

            foreach (var c in _tab.GetComponentsInChildren<MonoBehaviour>(true))
                if (c != null && c.GetType().Name == "LocText") UnityEngine.Object.Destroy(c);

            ApplyCaption();
        }

        private void Update()
        {
            ApplyCaption();
            // Once the tab drives a real screen, the client's own Animator handles the selected
            // look - reproducing it here as well would fight it.
            if (!_native) { try { SyncSelection(); } catch { } }
            if (_failed || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            try { Ensure(); }
            catch (Exception e)
            {
                _failed = true;
                Plugin.Log.LogWarning(Caption + " tab disabled: " + e.Message);
            }
        }

        /// <summary>Every tab in the top bar. Used to recognise the bar itself.</summary>
        private static readonly string[] TopBarTabs =
            { "HOME", "DECKS", "PROFILE", "SHOP", "BATTLE PASS", "CARD DEX" };

        private void Ensure()
        {
            var labels = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>()
                .Where(l => l != null && l.canvas != null && l.gameObject.activeInHierarchy)
                .ToArray();

            Transform container, tabRoot;
            if (!FindTopBar(labels, out container, out tabRoot))
            {
                Report(labels, "top bar not found");
                return;
            }

            foreach (Transform child in container)
                if (child.name == TabName) return;

            var copy = UnityEngine.Object.Instantiate(tabRoot.gameObject, container);
            copy.name = TabName;
            copy.SetActive(true);
            copy.transform.SetSiblingIndex(InsertIndex(container, tabRoot));

            // Strip ONE thing: the localiser.
            //
            // The earlier filter removed anything named like navigation, and that took out
            // PersistentNavButtonGroup - the component that carries the tab's destination screen
            // and, through its Animator, the selected look. Keeping it is what lets this tab be
            // driven by the client's own code instead of an imitation of it. Keeping the rest also
            // keeps ButtonClickSfx, so the tab even sounds like the others.
            //
            // LocText has to go: it rewrites the caption back to the original tab's text. Note the
            // name - an earlier filter looked for "Localiz" and never matched "LocText", which is
            // why the caption had to be re-applied every single frame.
            foreach (var c in copy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (c != null && c.GetType().Name == "LocText") UnityEngine.Object.Destroy(c);
            }

            // Keep the labels and re-apply the caption every tick. Setting it once was not enough:
            // something still rewrites it back to DECKS after we clone, so the tab kept the
            // original text.
            _tab = copy.transform;
            _lastWidth = 0f;
            _labels = copy.GetComponentsInChildren<TextMeshProUGUI>(true).ToList();
            ApplyCaption();

            // Give the tab its own click catcher rather than reusing whatever the original used.
            // The real tab does not handle clicks through a plain Button, and stripping its
            // navigation components left nothing listening at all - so the clone did nothing.
            if (_native) return;   // a real screen is wired up; the tab's own Button handles clicks

            var catcher = new GameObject("ClickCatcher", typeof(RectTransform));
            catcher.transform.SetParent(copy.transform, false);
            var crt = catcher.GetComponent<RectTransform>();
            crt.anchorMin = Vector2.zero;
            crt.anchorMax = Vector2.one;
            crt.offsetMin = Vector2.zero;
            crt.offsetMax = Vector2.zero;
            crt.localScale = Vector3.one;

            var hit = catcher.AddComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, 0.004f);   // invisible, but raycastable
            hit.raycastTarget = true;

            // NOTE: the cloned InvisibleRaycast child is deliberately LEFT ALONE. It looked like
            // the culprit behind the tab doing nothing, but the click was landing all along - the
            // screen was simply rendering into a canvas that was not visible. Removing components
            // whose purpose is not understood has already caused two regressions today.

            var btn = catcher.AddComponent<Button>();
            btn.targetGraphic = hit;
            btn.interactable = true;
            btn.onClick.AddListener(() =>
            {
                Plugin.Log.LogInfo(Caption + " tab clicked.");
                if (Screen != null) Screen.Toggle();
            });
            catcher.transform.SetAsLastSibling();

            // The real tabs turn yellow on hover; that came from components we had to strip, so
            // reproduce it with the colours learned from the live bar.
            var trigger = catcher.AddComponent<EventTrigger>();
            var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            enter.callback.AddListener(_ => { _hovering = true; RefreshStyle(); });
            trigger.triggers.Add(enter);
            var exit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
            exit.callback.AddListener(_ => { _hovering = false; RefreshStyle(); });
            trigger.triggers.Add(exit);

            Plugin.Log.LogInfo(Caption + " tab added: cloned \"" + tabRoot.name + "\" into \"" +
                               container.name + "\" after \"" + InsertAfter + "\".");
#if DEVTOOLS
            foreach (Transform sib in container)
            {
                var lbl = sib.GetComponentInChildren<TextMeshProUGUI>(true);
                var active = string.Join(",", sib.GetComponentsInChildren<Transform>(true)
                    .Where(x => x != sib && x.gameObject.activeSelf).Select(x => x.name).Take(8).ToArray());
                Plugin.Log.LogInfo("  tab \"" + sib.name + "\" text=\"" +
                                   (lbl != null ? (lbl.text ?? "").Trim() : "") + "\" color=" +
                                   (lbl != null ? lbl.color.ToString() : "?") + " active=[" + active + "]");
            }
#endif
        }

        /// <summary>
        /// The sibling index for the clone: right after the tab whose caption is InsertAfter, or
        /// right after the cloned tab if that caption is not in the bar. Our OWN earlier tabs are
        /// skipped when matching, so two of ours never fight over the same slot.
        /// </summary>
        private int InsertIndex(Transform container, Transform tabRoot)
        {
            int after = -1;
            foreach (Transform sib in container)
            {
                if (sib.name.StartsWith("PtcglLeaderboard", StringComparison.Ordinal)) continue;
                var lbl = sib.GetComponentInChildren<TextMeshProUGUI>(true);
                if (lbl == null) continue;
                if (string.Equals((lbl.text ?? "").Trim(), InsertAfter, StringComparison.OrdinalIgnoreCase))
                    after = sib.GetSiblingIndex();
            }
            return (after >= 0 ? after : tabRoot.GetSiblingIndex()) + 1;
        }

        /// <summary>
        /// Find the TOP BAR and the DECKS tab inside it.
        ///
        /// Three earlier attempts all landed in the wrong container, each for a different reason:
        /// a single button's internals, then the HOME screen's shortcut row (which really does
        /// contain PROFILE, DECKS and CARD DEX), and then an ancestor holding the entire screen -
        /// which trivially contains every tab name somewhere below it.
        ///
        /// The test that actually describes a bar of tabs is structural, not a name count: FOUR OR
        /// MORE of its IMMEDIATE CHILDREN each contain a tab name. A screen-wide ancestor fails
        /// that (its tabs are buried inside one or two children), and the shortcut row fails it
        /// too. Requiring HOME among them settles the last ambiguity, since only the top bar has it.
        /// </summary>
        private static bool FindTopBar(TextMeshProUGUI[] labels, out Transform container, out Transform tabRoot)
        {
            container = null; tabRoot = null;

            foreach (var deckLabel in labels)
            {
                if (!string.Equals((deckLabel.text ?? "").Trim(), "DECKS", StringComparison.OrdinalIgnoreCase))
                    continue;

                for (var t = deckLabel.transform; t != null && t.parent != null; t = t.parent)
                {
                    var parent = t.parent;
                    int childrenWithTab = 0;
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (Transform child in parent)
                    {
                        bool hasTab = false;
                        foreach (var lbl in child.GetComponentsInChildren<TextMeshProUGUI>(true))
                        {
                            if (lbl == null) continue;
                            var v = (lbl.text ?? "").Trim();
                            if (v.Length == 0) continue;
                            if (Array.FindIndex(TopBarTabs,
                                    x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase)) < 0) continue;
                            hasTab = true;
                            names.Add(v.ToUpperInvariant());
                        }
                        if (hasTab) childrenWithTab++;
                    }

                    if (childrenWithTab >= 4 && names.Contains("HOME"))
                    {
                        container = parent;
                        tabRoot = t;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>Periodic, bounded diagnostics - only once a menu is actually on screen.</summary>
        private void Report(TextMeshProUGUI[] labels, string why)
        {
            var seen = labels.Select(l => (l.text ?? "").Trim())
                             .Where(t => t.Length > 0 && t.Length <= 18)
                             .Distinct().Take(28).ToArray();
            if (seen.Length == 0 || _reports >= 4) return;
            _reports++;
            Plugin.Log.LogInfo(Caption + " tab: " + why + ". visible labels: " +
                               string.Join(" | ", seen));
        }
    }
}
