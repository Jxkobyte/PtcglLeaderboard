using System;
using System.Linq;
using HarmonyLib;
using TMPro;
using TPCI.Rainier.Features.DeckManager;
using UnityEngine;

namespace PrizeTracker.Core
{
    /// <summary>
    /// Draws each deck's win/loss record onto the game's own deck tiles, in the deck screen.
    ///
    /// Hooked on DeckSelectEntry.Setup, which the client calls whenever a tile binds to a deck.
    /// That matters because the list recycles its entries as you scroll - patching Setup means the
    /// badge follows the deck rather than the tile, with no polling and nothing to keep in sync.
    /// DeckSelectEntry.storedDeck is a public getter, so the tile tells us which deck it is showing.
    ///
    /// The label is a CLONE of a TextMeshPro label already inside the tile rather than a new object.
    /// Cloning inherits the font asset, material, canvas scaling and sharp-text settings that this
    /// UI uses; building one from scratch reliably produces the wrong font or invisible text.
    ///
    /// Everything is wrapped and fails silent: a mod must never be able to break the deck screen.
    /// </summary>
    internal static class DeckBadge
    {
        private const string BadgeName = "PrizeTrackerWinrate";

        public static MatchHistory History;
        public static bool Enabled = true;
        private static bool _loggedFailure;
        // The deck tile is LIGHT, so these have to be dark to be read at all. The first set was
        // picked for a dark background - an even record came out at (0.92, 0.93, 0.96), which is
        // very nearly white, so "2-2" was invisible against the tile it sits on.
        //
        // Colour still carries the result, but only as a tint on something already legible:
        // black by default, darkened green or red when the record leans one way.
        private static readonly Color Even = new Color(0.10f, 0.11f, 0.13f, 1f);
        private static readonly Color Winning = new Color(0.09f, 0.42f, 0.20f, 1f);
        private static readonly Color Losing = new Color(0.62f, 0.12f, 0.12f, 1f);

        // A deck with no matches is still shown, just quieter - dark enough to read, light enough
        // to say "nothing here yet" rather than "a real record of nought and nought".
        private static readonly Color Unplayed = new Color(0.42f, 0.44f, 0.48f, 1f);

        private static bool _loggedApplied;
        private static bool _loggedNoAnchor;
        private static int _applied;

        /// <summary>How many deck tiles have been badged this session - surfaced in the SET tab.</summary>
        public static int Applied { get { return _applied; } }

        [HarmonyPatch(typeof(DeckSelectEntry), "Setup")]
        private static class SetupPatch
        {
            private static void Postfix(DeckSelectEntry __instance)
            {
                Apply(__instance);
            }
        }

        public static void Apply(DeckSelectEntry entry)
        {
            if (!Enabled || entry == null || History == null) return;
            try
            {
                var deck = entry.storedDeck;
                if (deck == null || string.IsNullOrEmpty(deck.deckName)) return;

                var label = FindOrCreate(entry, deck.deckName);
                if (label == null)
                {
                    if (!_loggedNoAnchor)
                    {
                        _loggedNoAnchor = true;
                        Plugin.Log.LogWarning("deck badge: no TextMeshPro label found inside a deck tile to clone, "
                                            + "so the record cannot be drawn on \"" + deck.deckName + "\".");
                    }
                    return;
                }

                // Proves the hook fires. Without it a working badge and a broken one look identical
                // whenever the history is empty, because unplayed decks draw nothing.
                _applied++;
                if (!_loggedApplied)
                {
                    _loggedApplied = true;
                    Plugin.Log.LogInfo("deck badge attached on the deck screen (first: \"" + deck.deckName
                                     + "\", history has " + History.Count + " matches).");
                }

                var w = History.ForDeck(deck.id, deck.deckName);
                if (w.Played == 0)
                {
                    // Every deck carries a record, including an empty one. Drawing nothing here
                    // was worse than useless: a deck you have not played and a badge that is not
                    // working look exactly the same.
                    label.text = "0-0";
                    label.color = Unplayed;
                    return;
                }

                // Record only. The percentage was redundant beside it and made the badge read as
                // a statistic rather than as part of the tile.
                label.text = w.Wins + "-" + w.Losses;
                label.color = w.Rate >= 0.55 ? Winning
                            : w.Rate < 0.45 ? Losing
                            : Even;
            }
            catch (Exception e)
            {
                if (!_loggedFailure)
                {
                    _loggedFailure = true;
                    Plugin.Log.LogWarning("deck win-rate badge disabled: " + e.Message);
                }
                Enabled = false;
            }
        }

        /// <summary>The badge may live a level or two down now, so search the whole tile.</summary>
        private static TextMeshProUGUI FindBadge(Transform entryRoot)
        {
            foreach (var t in entryRoot.GetComponentsInChildren<Transform>(true))
                if (t != null && t.name == BadgeName)
                    return t.GetComponent<TextMeshProUGUI>();
            return null;
        }


        private static TextMeshProUGUI FindOrCreate(DeckSelectEntry entry, string deckName)
        {
            var entryRoot = entry.transform as RectTransform;
            if (entryRoot == null) return null;

            var existing = FindBadge(entryRoot);
            if (existing != null) return existing;

            // Clone the tile's DECK NAME label specifically, identified by its text matching the
            // deck. Any label would inherit a font, but the tile also holds small counters and
            // badges in other faces and weights - matching the deck name is what makes the record
            // read as part of the same UI. Falls back to the largest label on the tile.
            var candidates = entry.GetComponentsInChildren<TextMeshProUGUI>(true)
                                  .Where(x => x != null && x.font != null)
                                  .ToList();
            if (candidates.Count == 0) return null;

            var source = candidates.FirstOrDefault(
                             x => string.Equals((x.text ?? "").Trim(), deckName,
                                                StringComparison.OrdinalIgnoreCase))
                      ?? candidates.OrderByDescending(x => x.fontSize).First();

            // Parent on the entry root, which is where the badge is reliably visible, and let a
            // follower copy the hover lift. Guessing which child animates did not work: the
            // deck-name container does not move, and "largest image" picked a masked Gradient
            // overlay, which made the badge disappear entirely.
            var copy = UnityEngine.Object.Instantiate(source.gameObject, entryRoot);
            copy.name = BadgeName;
            copy.SetActive(true);

            // Strip anything that would fight us for position. A cloned label often carries layout
            // components from its original slot, which a parent layout group would then honour and
            // move the badge somewhere unhelpful.
            foreach (var le in copy.GetComponents<UnityEngine.UI.LayoutElement>())
                UnityEngine.Object.Destroy(le);
            foreach (var fitter in copy.GetComponents<UnityEngine.UI.ContentSizeFitter>())
                UnityEngine.Object.Destroy(fitter);

            // The deck-name label is driven by a localiser that would rewrite our text back to the
            // deck name on the next refresh.
            foreach (var c in copy.GetComponents<MonoBehaviour>())
            {
                if (c == null || c is TextMeshProUGUI) continue;
                var n = c.GetType().Name;
                if (n.IndexOf("Localiz", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("DeckName", StringComparison.OrdinalIgnoreCase) >= 0)
                    UnityEngine.Object.Destroy(c);
            }
            foreach (var child in copy.GetComponentsInChildren<Transform>(true))
                if (child != copy.transform) UnityEngine.Object.Destroy(child.gameObject);

            var label = copy.GetComponent<TextMeshProUGUI>();
            if (label == null) return null;

            label.raycastTarget = false;          // never steal clicks from the tile
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Overflow;
            label.alignment = TextAlignmentOptions.Top;
            // Keep the source's own size and weight so the record matches the tile's typography
            // rather than imposing a style of its own.
            label.fontSize = source.fontSize;
            label.fontStyle = source.fontStyle;
            label.fontSharedMaterial = source.fontSharedMaterial;

            var rt = copy.GetComponent<RectTransform>();
            // Top CENTRE of the tile.
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -20f);
            rt.sizeDelta = new Vector2(140f, 28f);
            rt.localScale = Vector3.one;
            rt.SetAsLastSibling();                // draw above the tile art

            // Hover-follow is OFF. Tracking "whichever descendant moved furthest" pushed the
            // badges right out of their tiles, because plenty of children legitimately move for
            // reasons that have nothing to do with the hover. A correct badge in the right place
            // beats one that follows the card and lands in the wrong one.

            return label;
        }
    }
}
