using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace PrizeTracker.Core
{
    /// <summary>
    /// Finds the client's OWN prize viewer - the panel that opens when you click your prize cards.
    ///
    /// Showing solved prizes inside that panel, in place of the card backs it already draws, is a
    /// far better answer than floating a second panel beside it: it is the client's own layout,
    /// its own art, its own open/close behaviour, and there is nothing extra on screen.
    ///
    /// This does not change anything. It reports what appears while a match is running so the
    /// swap can target the real objects instead of a guessed GameObject path - the same discipline
    /// that found the avatar render targets, after several rounds lost to guessing paths blind.
    ///
    /// Reports only when the set of matching objects CHANGES, so opening the viewer shows up as a
    /// distinct event rather than being buried in a repeated dump.
    /// </summary>
    internal class PrizeViewProbe : MonoBehaviour
    {
        public bool InMatch;

        private float _next;
        private string _signature;
        private int _reports;

        private void Update()
        {
            if (!InMatch || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;

            // The one-shot dumps run regardless of the report cap. Capping them together meant the
            // drawer subtree was never captured: the cap was spent on the *prize* name scan long
            // before the drawer was ever opened.
            try { DumpDrawerSlot(); DumpSlottedPrizes(); } catch { }
            if (_reports >= 6) return;

            try { Scan(); }
            catch (Exception e)
            {
                _reports = 99;
                Plugin.Log.LogWarning("prize view probe failed: " + e.Message);
            }
        }

        private void Scan()
        {
            var hits = new List<RectTransform>();
            foreach (var t in Resources.FindObjectsOfTypeAll<RectTransform>())
            {
                if (t == null || !t.gameObject.activeInHierarchy) continue;
                var n = t.name;
                if (n.IndexOf("prize", StringComparison.OrdinalIgnoreCase) < 0) continue;
                hits.Add(t);
            }
            DumpBoardPrizes();
            if (hits.Count == 0) return;

            var sig = string.Join(";", hits.Select(h => h.name).OrderBy(x => x).ToArray());
            if (sig == _signature) return;
            _signature = sig;
            _reports++;

            Plugin.Log.LogWarning("prize view: " + hits.Count + " active object(s) named *prize*:");
            foreach (var h in hits.Take(24))
            {
                var img = h.GetComponent<Image>();
                var raw = h.GetComponent<RawImage>();
                var comps = string.Join(",", h.GetComponents<Component>()
                    .Where(c => c != null && !(c is RectTransform) && !(c is CanvasRenderer))
                    .Select(c => c.GetType().Name).ToArray());

                Plugin.Log.LogWarning("  " + Path(h)
                    + "  size=" + Mathf.RoundToInt(h.rect.width) + "x" + Mathf.RoundToInt(h.rect.height)
                    + (img != null && img.sprite != null ? "  sprite=" + img.sprite.name : "")
                    + (raw != null && raw.texture != null ? "  tex=" + raw.texture.name : "")
                    + "  {" + comps + "}");
            }
        }

        /// <summary>
        /// What the PRIZE PILE ON THE BOARD is made of.
        ///
        /// The drawer is Unity UI and easy to draw into; the board's prize stack is not - the probe
        /// found it as Board_Landscape/.../Offset:Prize/AnimOffset:Prize/Prize carrying
        /// PrizeController and PrizeCardMover, which is 3D board furniture, not a RectTransform.
        /// Showing those face up means finding how a card's face is rendered there, so this lists
        /// the stack's children with their components and renderers once.
        /// </summary>
        private bool _dumpedBoard;

        private void DumpBoardPrizes()
        {
            if (_dumpedBoard) return;
            DumpDrawerSlot();
            DumpSlottedPrizes();

            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t == null || t.name != "Prize" || !t.gameObject.activeInHierarchy) continue;
                if (t.GetComponent("PrizeController") == null) continue;
                if (Path(t).IndexOf("PlayerBoardElements", StringComparison.Ordinal) < 0) continue;

                _dumpedBoard = true;
                Plugin.Log.LogWarning("board prize stack: " + Path(t));
                int n = 0;
                foreach (var c in t.GetComponentsInChildren<Transform>(true))
                {
                    if (n++ > 24) break;
                    var comps = string.Join(",", c.GetComponents<Component>()
                        .Where(x => x != null && !(x is Transform))
                        .Select(x => x.GetType().Name).ToArray());
                    var rend = c.GetComponent<Renderer>();
                    Plugin.Log.LogWarning("  " + new string(' ', 2) + c.name
                        + (c.gameObject.activeSelf ? "" : " [INACTIVE]")
                        + (rend != null && rend.sharedMaterial != null
                            ? "  mat=" + rend.sharedMaterial.name : "")
                        + "  {" + comps + "}");
                }
                return;
            }
        }

        private bool _dumpedSlot, _dumpedCards;

        /// <summary>One drawer slot in full - looking for the card renderer the client uses.</summary>
        private void DumpDrawerSlot()
        {
            if (_dumpedSlot) return;
            foreach (var t in Resources.FindObjectsOfTypeAll<RectTransform>())
            {
                if (t == null || !t.gameObject.activeInHierarchy) continue;
                if (!t.name.StartsWith("PrizeSlot_Drawer", StringComparison.Ordinal)) continue;

                _dumpedSlot = true;
                Plugin.Log.LogWarning("drawer slot subtree: " + Path(t));
                foreach (var c in t.GetComponentsInChildren<Transform>(true))
                {
                    var comps = string.Join(",", c.GetComponents<Component>()
                        .Where(x => x != null && !(x is Transform) && !(x is CanvasRenderer))
                        .Select(x => x.GetType().Name).ToArray());
                    Plugin.Log.LogWarning("    " + c.name +
                        (c.gameObject.activeSelf ? "" : " [INACTIVE]") + "  {" + comps + "}");
                }
                return;
            }
        }

        /// <summary>
        /// The board's six prize cards, via PrizeController.SlottedPrizes.
        ///
        /// They are not children of the board's Prize object - that has none - so they are pooled
        /// Card3D objects the controller holds. Whatever loads their art is what has to be driven
        /// to show a solved prize face up.
        /// </summary>
        private void DumpSlottedPrizes()
        {
            if (_dumpedCards) return;
            foreach (var pc in Resources.FindObjectsOfTypeAll<PrizeController>())
            {
                if (pc == null || !pc.gameObject.activeInHierarchy) continue;
                if (Path(pc.transform).IndexOf("PlayerBoardElements", StringComparison.Ordinal) < 0) continue;

                var cards = pc.SlottedPrizes;
                if (cards == null || cards.Count == 0) continue;

                _dumpedCards = true;
                Plugin.Log.LogWarning("slotted prizes: " + cards.Count + " Card3D under " + Path(pc.transform));
                int n = 0;
                foreach (var card in cards)
                {
                    if (card == null || n++ >= 2) continue;     // two is enough to see the shape
                    Plugin.Log.LogWarning("  card: " + Path(card.transform));
                    foreach (var c in card.GetComponentsInChildren<Transform>(true))
                    {
                        var comps = string.Join(",", c.GetComponents<Component>()
                            .Where(x => x != null && !(x is Transform))
                            .Select(x => x.GetType().Name).ToArray());
                        if (comps.Length == 0) continue;
                        Plugin.Log.LogWarning("      " + c.name + "  {" + comps + "}");
                    }
                }
                return;
            }
        }

        private static string Path(Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null; x = x.parent) parts.Insert(0, x.name);
            return string.Join("/", parts.ToArray());
        }
    }
}
