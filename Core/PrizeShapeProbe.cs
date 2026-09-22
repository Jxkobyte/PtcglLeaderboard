using System;
using System.Collections.Generic;
using System.Linq;
using TPCI.Rainier.Match.Cards;
using UnityEngine;

namespace PrizeTracker.Core
{
    /// <summary>
    /// Reports what makes a card show its FACE, by diffing a face-down prize against a card that
    /// is already face up in the same match.
    ///
    /// Reasoning about this from the decompile has a poor record - the same approach produced two
    /// wrong answers about card texture aspect before the client was simply asked. The facts that
    /// matter here are all observable at runtime: whether a private prize's view is initialised at
    /// all, what view mode it is in, whether its graphic has a card loaded, which renderers are on,
    /// and which way it is turned. A face-up card answers the same questions, and the difference
    /// between the two columns is exactly what a reveal has to reproduce.
    ///
    /// One shot, read-only, and it changes nothing.
    /// </summary>
    internal class PrizeShapeProbe : MonoBehaviour
    {
        public bool InMatch;

        private float _next;
        private bool _done;
        private string _waiting;

        private void Waiting(string why)
        {
            if (_waiting == why) return;
            _waiting = why;
            Plugin.Log.LogInfo("prize shape probe waiting: " + why);
        }

        private bool _dumpedOpenView;

        private void Update()
        {
            if (!InMatch) return;

            // The prize VIEW panel is its own question: the board can be showing dressed cards
            // while that panel shows sleeve backs, which means the slots it draws through are not
            // turned the way the board's are. Catch it while it is actually open.
            try { DumpWhileOpen(); } catch { }

            if (_done || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 2f;

            try { Run(); }
            catch (Exception e) { _done = true; Plugin.Log.LogWarning("prize shape probe failed: " + e.Message); }
        }

        private void DumpWhileOpen()
        {
            var owner = FindOwnPrizeController();
            var menu = owner != null ? owner.prizeDisplayMenu : null;
            if (menu == null || !menu.IsOpen) { _dumpedOpenView = false; return; }
            if (_dumpedOpenView) return;
            _dumpedOpenView = true;

            Plugin.Log.LogWarning("=== prize panel open: inspection=" + menu.IsInInspection
                + " awaitingSelection=" + menu.IsAwaitingSelection + " ===");

            int i = 0;
            foreach (var mover in menu.PrizeSlots)
            {
                Plugin.Log.LogWarning("  slot " + i + ": "
                    + (mover == null ? "(null mover)"
                       : mover.name + " rot=" + mover.transform.localRotation.eulerAngles
                         + " active=" + mover.gameObject.activeInHierarchy
                         + " children=" + mover.transform.childCount));
                i++;
            }

            var prizes = owner.SlottedPrizes;
            for (int k = 0; prizes != null && k < prizes.Count; k++)
            {
                var c = prizes[k];
                if (c == null) { Plugin.Log.LogWarning("  prize " + k + ": (taken)"); continue; }
                var g = c.view != null ? c.view.graphic : null;
                Plugin.Log.LogWarning("  prize " + k + ": " + c.name
                    + " graphic=" + (g == null ? "none" : g.CardId)
                    + " fwd=" + c.transform.forward
                    + " parent=" + (c.transform.parent != null ? c.transform.parent.name : "(none)"));
            }
        }

        private void Run()
        {
            var owner = FindOwnPrizeController();
            if (owner == null) { Waiting("no prize controller yet"); return; }

            var prizes = owner.SlottedPrizes;
            if (prizes == null || prizes.Count == 0) { Waiting("prize controller has no slotted cards"); return; }

            Card3D prize = null;
            foreach (var c in prizes) { if (c != null && c.IsPrivate) { prize = c; break; } }
            if (prize == null) { Waiting("no PRIVATE prize among " + prizes.Count + " slotted"); return; }

            // Something already face up, for comparison - anything identified will do.
            Card3D faceUp = null;
            foreach (var c in Resources.FindObjectsOfTypeAll<Card3D>())
            {
                if (c == null || !c.gameObject.activeInHierarchy) continue;
                if (c.IsPrivate || !c.visuallyRevealed) continue;
                faceUp = c; break;
            }

            _done = true;
            Plugin.Log.LogWarning("=== prize shape probe ===");

            // Every prize's currently-loaded graphic. A face-down prize turned out to already
            // carry one, which is either the real card (the client knowing something it does not
            // show) or whatever that pooled Card3D rendered last. Listing all six makes it
            // obvious which - real prizes would be six plausible deck cards, pooled leftovers
            // will not be.
            var ids = new List<string>();
            foreach (var c in prizes)
                ids.Add(c == null ? "(null)"
                        : (c.view != null && c.view.graphic != null
                           ? (string.IsNullOrEmpty(c.view.graphic.CardId) ? "(none)" : c.view.graphic.CardId)
                           : "(no graphic)"));
            Plugin.Log.LogWarning("  prize graphics currently loaded: " + string.Join(", ", ids.ToArray()));
            Describe("PRIVATE PRIZE", prize);
            if (faceUp != null) Describe("FACE-UP CARD", faceUp);
            else Plugin.Log.LogWarning("  (no face-up card found to compare against)");
        }

        private static void Describe(string label, Card3D card)
        {
            Plugin.Log.LogWarning(label + ": " + card.name);
            try
            {
                Plugin.Log.LogWarning("  isPrivate=" + card.IsPrivate
                    + " visuallyRevealed=" + card.visuallyRevealed
                    + " boardPos=" + card.CurrentOwnerBoardPos
                    + " playerID=" + card.playerID);

                var view = card.view;
                if (view == null) { Plugin.Log.LogWarning("  view: NULL"); }
                else
                {
                    var g = view.graphic;
                    Plugin.Log.LogWarning("  viewMode=" + view.cardViewMode
                        + " graphic=" + (g == null ? "NULL"
                            : ("CardId=" + (string.IsNullOrEmpty(g.CardId) ? "(none)" : g.CardId)
                               + " bundle=" + (string.IsNullOrEmpty(g.AssetBundleName) ? "(none)" : g.AssetBundleName))));
                }

                Plugin.Log.LogWarning("  localRot=" + card.transform.localRotation.eulerAngles
                    + " worldFwd=" + card.transform.forward);

                var rends = card.GetComponentsInChildren<Renderer>(true)
                    .Where(r => r != null).Take(10).ToArray();
                foreach (var r in rends)
                    Plugin.Log.LogWarning("    renderer " + r.name
                        + (r.enabled ? "" : " [DISABLED]")
                        + (r.gameObject.activeInHierarchy ? "" : " [INACTIVE]")
                        + "  mat=" + (r.sharedMaterial != null ? r.sharedMaterial.name : "none"));
            }
            catch (Exception e) { Plugin.Log.LogWarning("  describe failed: " + e.Message); }
        }

        /// <summary>
        /// Our own prize pile.
        ///
        /// playerID reads through PlayerCardOwner.controller and is UNKNOWN until that is wired up,
        /// so it cannot be the only test - filtering on it alone found nothing at all and the probe
        /// sat retrying in silence. The path check is what the existing probe uses and it works, so
        /// playerID is preferred and the path is the fallback.
        /// </summary>
        private static PrizeController FindOwnPrizeController()
        {
            PrizeController byPath = null;
            foreach (var pc in Resources.FindObjectsOfTypeAll<PrizeController>())
            {
                if (pc == null || !pc.gameObject.activeInHierarchy) continue;
                if (pc.playerID == PlayerID.LOCAL) return pc;
                if (byPath == null &&
                    Path(pc.transform).IndexOf("PlayerBoardElements", StringComparison.Ordinal) >= 0)
                    byPath = pc;
            }
            return byPath;
        }

        private static string Path(Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null; x = x.parent) parts.Insert(0, x.name);
            return string.Join("/", parts.ToArray());
        }
    }
}
