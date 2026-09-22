using System.Collections.Generic;
using System.Linq;

namespace PrizeTracker.Core
{
    /// <summary>A card known to be near the bottom of your deck.</summary>
    internal class BottomCard
    {
        public string EntityId;
        public string Name;
        public string SourceId;
        public int DrawsAway;      // cards to draw before this one comes off the top
        public int GroupFirst;     // earliest arrival within its group
        public int GroupLast;      // latest arrival within its group
        public bool OrderKnown;    // false when the group's internal order was randomised
    }

    /// <summary>
    /// Tracks cards placed on the BOTTOM of your deck and counts them down as you draw, so you can
    /// see what is coming back around and when.
    ///
    /// Written for Metang's "Metal Maker" (TEF 114), whose sub-actions are, in order: move the top
    /// FOUR cards of your deck to HiddenPending revealed to you, attach any Basic Metal Energy
    /// among them, SHUFFLE what is left, then move all of it to DeckBottom. Nothing here is
    /// Metang-specific though - it keys off the board, so any effect that puts cards on the bottom
    /// shows up.
    ///
    /// Three consequences of those mechanics drive the design:
    ///
    ///  - USES STACK. A second Metang takes the next four and puts them BELOW the first group, so
    ///    groups accumulate rather than replace. Cards are tracked by entityID (revealed cards
    ///    carry a real one; only hidden cards are stamped "PRIVATE") so a group already being
    ///    tracked is never double-counted.
    ///
    ///  - THE DECK NEVER GROWS from this. Cards come off the top and go back to the bottom, minus
    ///    any energy attached. So a deck that GREW means something else shuffled cards in, and
    ///    whatever order we remembered is worthless - reset.
    ///
    ///  - ORDER WITHIN A GROUP MAY BE UNKNOWN. Metal Maker shuffles the group before placing it.
    ///    If the cards stay revealed in the deck we can just read the real order; if they do not,
    ///    we know the SET and its arrival window but not which comes first, and the display says so
    ///    rather than inventing an order.
    ///
    /// Deck order is confirmed from the engine, not assumed: MatchBoard.InsertCardEntity does
    /// p1Deck.Add(card) for the bottom and p1Deck.Insert(0, card) for the top, so index 0 is the
    /// top and the last index is the bottom.
    /// </summary>
    internal class BottomStack
    {
        private class Group
        {
            public List<BottomCard> Cards = new List<BottomCard>();
            public bool OrderKnown;
        }

        private readonly List<Group> _groups = new List<Group>();
        private readonly HashSet<string> _tracked = new HashSet<string>();
        private List<CardRef> _prevPending = new List<CardRef>();
        private int _lastDeckCount = -1;

        public bool Active { get { return _groups.Sum(g => g.Cards.Count) > 0; } }

        /// <summary>All tracked cards, nearest to the top first.</summary>
        public List<BottomCard> Cards
        {
            get
            {
                var all = new List<BottomCard>();
                foreach (var g in _groups) all.AddRange(g.Cards);
                return all;
            }
        }

        public int GroupCount { get { return _groups.Count; } }

        public void Reset()
        {
            _groups.Clear();
            _tracked.Clear();
            _prevPending = new List<CardRef>();
            _lastDeckCount = -1;
        }

        public void Update(SideSnapshot me)
        {
            if (me == null) return;
            int count = me.Deck != null ? me.Deck.Count : 0;

            // The deck grew, so cards were put back in from somewhere. Anything we remembered
            // about the bottom is no longer reliable.
            if (_lastDeckCount >= 0 && count > _lastDeckCount) Reset();

            var run = FindBottomRun(me.Deck);
            var newInRun = run.Where(c => !string.IsNullOrEmpty(c.EntityId) && !_tracked.Contains(c.EntityId)).ToList();
            if (newInRun.Count > 0)
            {
                // Visible at the bottom: we can read the real order straight off the board.
                AddGroup(newInRun, orderKnown: true);
            }
            else
            {
                // Not visible in the deck. Fall back to watching the staging zone: cards revealed
                // in HiddenPending that then vanish have been placed on the bottom, and this is
                // the only chance to learn them if the deck itself keeps them face down.
                var pendingNow = (me.Pending ?? new List<CardRef>()).Where(c => c.Known).ToList();
                var gone = _prevPending
                    .Where(p => !string.IsNullOrEmpty(p.EntityId)
                                && !_tracked.Contains(p.EntityId)
                                && !pendingNow.Any(n => n.EntityId == p.EntityId))
                    .ToList();
                if (gone.Count > 0) AddGroup(gone, orderKnown: false);
                _prevPending = pendingNow;
            }

            if (run.Count > 0)
                _prevPending = (me.Pending ?? new List<CardRef>()).Where(c => c.Known).ToList();

            _lastDeckCount = count;
            Recount(count);
        }

        private void AddGroup(List<CardRef> cards, bool orderKnown)
        {
            var g = new Group { OrderKnown = orderKnown };
            foreach (var c in cards)
            {
                g.Cards.Add(new BottomCard
                {
                    EntityId = c.EntityId,
                    Name = c.Key,
                    SourceId = c.SourceId,
                    OrderKnown = orderKnown,
                });
                _tracked.Add(c.EntityId);
            }
            _groups.Add(g);
        }

        /// <summary>
        /// The run of revealed cards at the END of the deck, bounded above by a face-down card.
        ///
        /// That upper bound matters: while you search, the WHOLE deck is revealed, and without the
        /// guard a search reads as a 47-card bottom stack. Requiring a hidden card above the run
        /// separates "a few were placed on the bottom" from "I am looking through my deck".
        /// </summary>
        private static List<CardRef> FindBottomRun(List<CardRef> deck)
        {
            var run = new List<CardRef>();
            if (deck == null || deck.Count == 0) return run;

            int i = deck.Count - 1;
            while (i >= 0 && deck[i].Known) { run.Add(deck[i]); i--; }
            if (i < 0) run.Clear();      // whole deck visible: a search, not a bottom stack
            run.Reverse();               // nearest to the top first
            return run;
        }

        /// <summary>
        /// If K cards sit at the bottom of a deck of M they occupy the last K slots, so the first
        /// has M-K above it. Drawing shrinks M and every distance falls with it.
        /// </summary>
        private void Recount(int deckCount)
        {
            // Drop anything that has already been drawn off the top of the tracked block.
            int total = _groups.Sum(g => g.Cards.Count);
            while (total > deckCount && _groups.Count > 0)
            {
                var first = _groups[0];
                first.Cards.RemoveAt(0);
                total--;
                if (first.Cards.Count == 0) _groups.RemoveAt(0);
            }
            if (_groups.Count == 0) { _tracked.Clear(); return; }

            int above = deckCount - total;
            int idx = 0;
            foreach (var g in _groups)
            {
                int first = above + idx;
                int last = first + g.Cards.Count - 1;
                foreach (var c in g.Cards)
                {
                    c.DrawsAway = above + idx;
                    c.GroupFirst = first;
                    c.GroupLast = last;
                    c.OrderKnown = g.OrderKnown;
                    idx++;
                }
            }
        }
    }
}
