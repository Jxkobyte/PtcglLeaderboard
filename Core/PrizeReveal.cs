using System;
using System.Collections.Generic;
using TPCI.Rainier.Match.Cards;
using UnityEngine;

namespace PrizeTracker.Core
{
    /// <summary>
    /// Shows solved prizes on the CLIENT'S OWN CARDS, rather than drawing a second set over them.
    ///
    /// The first attempt painted a RawImage onto each PrizeSlot in the prize drawer, and it never
    /// looked right - it came out mirrored, and the sizing could not be made to match. Decompiling
    /// the client explained why: a PrizeSlot holds no card image at all. It is a CardMover (a
    /// position), a highlight and an index label, and the card you see is a real Card3D that the
    /// client MOVES into the slot. So the overlay was being laid over something that never draws a
    /// card, and every attempt to size it was fitting a shape that was not there.
    ///
    /// PrizeCardSelectionMenu.SetMoversOverTime is the client's own recipe for a revealed prize:
    ///
    ///     if (prizes[i].cardInfo != null &amp;&amp; prizes[i].cardInfo.cardEntity.HasMetaData(CardIsRevealed))
    ///         prizeSlots[i].SlotMover.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
    ///     else
    ///         prizeSlots[i].SlotMover.transform.localRotation = Quaternion.identity;
    ///
    /// - turn the slot around, and the card's face points at you. That is what this does, plus
    /// loading the card's illustration through the client's own loader (Card.Init resolves the
    /// asset bundle from the card database itself, so none of our bundle-name derivation is needed
    /// here), plus the client's own flip animation for the cards on the board.
    ///
    /// The board and the drawer show THE SAME six Card3D objects (PrizeController.SlottedPrizes),
    /// so this covers both at once.
    ///
    /// What it deliberately does NOT do: set Card3D.cardInfo. The client uses cardInfo == null to
    /// mean "this prize is still face down to me" - GetFirstPrivateCard and RegisterPrivateCard
    /// both depend on it - so filling it in risks breaking the act of taking a prize. Rendering is
    /// safe to touch; the client's bookkeeping is not.
    ///
    /// Solving yields the SET of six cards, never which slot holds which, so the card shown in
    /// slot 3 is one of the six and not the one slot 3 will give you. That is inherent: the client
    /// identifies Deck, Hand, Active and Pending entities and never the prize entities. If the
    /// wrong art ends up on a card, taking it corrects itself - Card3D.UpdateData calls
    /// graphic.Init with the real cardSourceID, and Card.Init unloads first when the id differs.
    /// </summary>
    internal class PrizeReveal : MonoBehaviour
    {
        public Tracker Tracker;
        public bool InMatch;

        /// <summary>Instance IDs of cards we have already dressed, so art loads once per card.</summary>
        private readonly HashSet<int> _dressed = new HashSet<int>();
        private string _signature;
        private float _next;
        private bool _failed;
        private PrizeController _owner;

        private void Update()
        {
            if (_failed) return;

            if (!InMatch)
            {
                if (_dressed.Count > 0) { _dressed.Clear(); _signature = null; }
                _owner = null;
                return;
            }

            try
            {
                // EVERY FRAME, and deliberately.
                //
                // The client sets its slot rotations when the prize panel opens - identity, since
                // as far as it is concerned nothing here is revealed - so whatever we set before
                // that is undone at the moment of opening. Correcting on a 0.3s tick meant the
                // cards visibly turned face down and then back again each time the panel opened.
                //
                // This is not the client continuously fighting us: nothing of its own runs against
                // this frame to frame, it simply gets there first. Re-asserting every frame closes
                // the gap to one frame. It is cheap because the expensive part - finding the
                // controller by scanning every loaded object - stays on the throttle below, and
                // because rotations are only written when they are actually wrong.
                FaceFrame();

                if (Time.unscaledTime >= _next)
                {
                    _next = Time.unscaledTime + 0.3f;
                    Apply();
                }
            }
            catch (Exception e)
            {
                _failed = true;
                Plugin.Log.LogWarning("prize reveal disabled: " + e.Message);
            }
        }

        /// <summary>The cheap half: re-assert facing using the controller found on the last tick.</summary>
        private void FaceFrame()
        {
            if (_owner == null || Tracker == null) return;

            var menu = _owner.prizeDisplayMenu;
            if (menu != null && menu.IsAwaitingSelection) return;

            bool anyKnown = false;
            foreach (var p in Tracker.PrizeSlots) if (p.Known) { anyKnown = true; break; }
            if (!anyKnown) return;

            var cards = _owner.SlottedPrizes;
            if (cards != null) FaceSlotsOut(menu, cards);
        }

        private void Apply()
        {
            if (Tracker == null) return;

            var known = new List<PrizeSlot>();
            foreach (var p in Tracker.PrizeSlots) if (p.Known) known.Add(p);
            if (known.Count == 0) return;

            // Re-dress from scratch when the solved set changes - a partial solve can be added to.
            var sig = "";
            foreach (var p in known) sig += p.SourceId + ";";
            if (sig != _signature) { _signature = sig; _dressed.Clear(); }

            var owner = FindOwnPrizeController();
            _owner = owner;
            if (owner == null) return;

            // NEVER while the client is asking which prize to take.
            //
            // Solving yields the SET of six cards and never which slot holds which - the client
            // identifies Deck, Hand, Active and Pending entities and never the prize entities
            // individually. Painting a card onto a numbered slot therefore asserts something we do
            // not know, and on the "Choose 1 Prize card" prompt that is not merely cosmetic: it
            // invites picking a slot to get the card shown on it, which is a coin flip dressed up
            // as information.
            //
            // PrizeCardSelectionMenu runs a state machine over the same slots for both jobs, and
            // exposes it, so this is the client's own distinction rather than a guess about which
            // screen is open: VIEW is looking at your prizes, SELECT is choosing one.
            var menu = owner.prizeDisplayMenu;
            if (menu != null && menu.IsAwaitingSelection) { Undress(owner); return; }

            var cards = owner.SlottedPrizes;
            if (cards == null) return;

            int n = 0;
            for (int i = 0; i < cards.Count && n < known.Count; i++)
            {
                var card = cards[i];
                if (card == null) continue;

                // A prize the client has already identified needs nothing from us, and is not one
                // of the ones we solved - skip it rather than overwrite real data with a guess.
                if (!card.IsPrivate) continue;

                Dress(owner, card, known[n].SourceId);
                n++;
            }

            FaceSlotsOut(menu, cards);
        }

        /// <summary>
        /// Load the face and turn the card around, once per card.
        ///
        /// Probing a real match showed a face-down prize and a face-up card in hand are IDENTICAL
        /// in every respect that was suspected of mattering - both viewMode BIG, both with cardFront
        /// and CardSideBack enabled on the same materials, both already carrying a loaded graphic.
        /// The single difference is which way the card faces: worldFwd (0,1,0) against (0,-1,0).
        ///
        /// That probe also overturned the original approach here. visuallyRevealed is ALREADY TRUE
        /// on a face-down prize, and QueuePrizeRevealFlipAnim is guarded by !visuallyRevealed, so
        /// the previous version of this method would have called it and had it do nothing at all -
        /// silently, and only in a real match. Clearing the flag first lets the client's own flip
        /// animation run, and its completion callback sets the flag back.
        /// </summary>
        private void Dress(PrizeController owner, Card3D card, string sourceId)
        {
            int id = card.GetInstanceID();
            if (!_dressed.Add(id)) return;

            var view = card.view;
            if (view == null || view.graphic == null)
            {
                Plugin.Log.LogWarning("prize reveal: " + card.name + " has no view/graphic");
                return;
            }

            var before = view.graphic.CardId;

            // The client's own loader - it looks the card up in the card database and works out
            // the asset bundle itself, so none of our bundle-name derivation applies here.
            view.graphic.Init(sourceId, -1, null);

            // The client's own reveal animation, as used when an effect reveals one of your prizes.
            card.SetVisuallyRevealed(false);
            owner.QueuePrizeRevealFlipAnim(card);

            Plugin.Log.LogWarning("prize reveal: " + card.name + " graphic " + before + " -> " + sourceId
                + "; fwd " + card.transform.forward + " revealed=" + card.visuallyRevealed);
        }

        /// <summary>
        /// Put everything back the way the client had it.
        ///
        /// Leaving the slots turned around would strand a face-up prize on the selection prompt,
        /// and re-dressing must be allowed to happen again afterwards, so the record of what has
        /// been dressed is cleared too.
        /// </summary>
        private void Undress(PrizeController owner)
        {
            if (_dressed.Count == 0) return;
            _dressed.Clear();
            _signature = null;

            var menu = owner != null ? owner.prizeDisplayMenu : null;
            if (menu == null) return;
            foreach (var mover in menu.PrizeSlots)
                if (mover != null) mover.transform.localRotation = Quaternion.identity;
        }

        /// <summary>
        /// Turn the first <paramref name="count"/> drawer slots around so their cards face out.
        ///
        /// The client sets these rotations itself every time the drawer opens, and only for cards
        /// it knows are revealed - which ours are not, as far as it is concerned - so this has to
        /// be re-applied rather than set once. It also means the client corrects us for free: a
        /// slot holding a card we did not solve gets set back to face-down by its own code.
        /// </summary>
        private static void FaceSlotsOut(PrizeCardSelectionMenu menu, IReadOnlyList<Card3D> prizes)
        {
            if (menu == null) return;

            // The menu's own ordered slot list, paired with the prize list the same way its own
            // SetMoversOverTime pairs them - prizes[i] sits in slot i.
            //
            // Turning the MOVER is what the client does, but it only takes effect through the
            // positioner as it places the card. Probing the open panel showed the movers rotated
            // exactly as intended and nothing on screen changing: every mover was inactive with no
            // children, and the cards were parented to the slot root instead. Rotating a mover a
            // card is not attached to, after the positioner has already finished placing it, moves
            // nothing.
            //
            // So the card's own rotation is set through its positioner as well. Both are set to
            // the same value, which means it does not matter which one ends up driving.
            var face = Quaternion.Euler(0f, 180f, 0f);
            int i = 0;
            foreach (var mover in menu.PrizeSlots)
            {
                bool show = i < prizes.Count && prizes[i] != null;
                var want = show ? face : Quaternion.identity;

                if (mover != null) mover.transform.localRotation = want;

                if (show)
                {
                    var card = prizes[i];
                    var pos = card.positioner;
                    if (pos != null && Quaternion.Angle(card.transform.localRotation, want) > 1f)
                        pos.SetLocalRotation(want);
                }
                i++;
            }
        }

        /// <summary>
        /// Our own prize pile, not the opponent's.
        ///
        /// PrizeController inherits playerID from PlayerCardOwner, so this is an exact test rather
        /// than a guess at a GameObject path.
        /// </summary>
        private static PrizeController FindOwnPrizeController()
        {
            foreach (var pc in Resources.FindObjectsOfTypeAll<PrizeController>())
            {
                if (pc == null || !pc.gameObject.activeInHierarchy) continue;
                if (pc.playerID != PlayerID.LOCAL) continue;
                return pc;
            }
            return null;
        }
    }
}
