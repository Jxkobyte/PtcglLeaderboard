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

        private void Update()
        {
            if (_failed) return;

            if (!InMatch)
            {
                if (_dressed.Count > 0) { _dressed.Clear(); _signature = null; }
                return;
            }

            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.3f;

            try { Apply(); }
            catch (Exception e)
            {
                _failed = true;
                Plugin.Log.LogWarning("prize reveal disabled: " + e.Message);
            }
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
            if (owner == null) return;

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

            ShowDrawerSlotsFaceUp(n);
        }

        /// <summary>Load the face and flip the card, once per card.</summary>
        private void Dress(PrizeController owner, Card3D card, string sourceId)
        {
            int id = card.GetInstanceID();
            if (!_dressed.Add(id)) return;

            var view = card.view;
            if (view == null || view.graphic == null) return;

            // The client's own loader: it looks the card up in the card database and works out the
            // asset bundle itself.
            view.graphic.Init(sourceId, -1, null);

            // The client's own reveal animation, used when an effect reveals one of your prizes.
            // It is a no-op on a card that is already face up, so calling it is safe.
            owner.QueuePrizeRevealFlipAnim(card);
        }

        /// <summary>
        /// Turn the first <paramref name="count"/> drawer slots around so their cards face out.
        ///
        /// The client sets these rotations itself every time the drawer opens, and only for cards
        /// it knows are revealed - which ours are not, as far as it is concerned - so this has to
        /// be re-applied rather than set once. It also means the client corrects us for free: a
        /// slot holding a card we did not solve gets set back to face-down by its own code.
        /// </summary>
        private static void ShowDrawerSlotsFaceUp(int count)
        {
            if (count <= 0) return;

            int i = 0;
            foreach (var slot in Resources.FindObjectsOfTypeAll<global::PrizeSlot>())
            {
                if (slot == null || !slot.gameObject.activeInHierarchy) continue;
                if (slot.SlotMover == null) continue;
                if (i++ >= count) continue;
                slot.SlotMover.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
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
