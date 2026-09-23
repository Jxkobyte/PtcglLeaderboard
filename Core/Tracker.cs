using System;
using System.Collections.Generic;
using System.Linq;
using SharedSDKUtils;

namespace PrizeTracker.Core
{
    /// <summary>One line of the opponent's cards - a card, and how many of it we have seen.</summary>
    internal class OppRow
    {
        public string Name;
        public string SourceId;   // representative printing, for card art
        public int Count;
        public string Where;
    }

    /// <summary>
    /// What we know about a match in progress, for the record kept of it afterwards.
    ///
    /// This used to work out WHICH cards were sitting in your prizes, and it could: a deck search
    /// briefly reveals the whole deck, which leaves the prizes as the only unaccounted cards, and
    /// from there it is elimination. That is gone, deliberately - when you take a prize the game
    /// lets you choose which card you take, so knowing what is in them is not a read on the game,
    /// it is playing a different game to everyone else.
    ///
    /// What remains is the bookkeeping match history and the leaderboard actually run on, all of
    /// it things either player can see at the table: which deck you brought, how many prizes each
    /// side has left, and which of the opponent's cards have been revealed in play.
    /// </summary>
    internal class Tracker
    {
        /// <summary>Optional sink for one-off notes worth seeing in the log.</summary>
        public static Action<string> Diagnostic;

        // Our decklist: accounting key -> copies. Null until we can read the active deck.
        private Dictionary<string, int> _deck;
        public string DeckName { get; private set; }
        public bool DeckKnown { get { return _deck != null && _deck.Count > 0; } }
        public int DeckSize { get { return _deck == null ? 0 : _deck.Values.Sum(); } }

        // Opponent cards revealed at any point this match, keyed by entityID so a card that moves
        // zones (hand -> discard -> shuffled back) is counted exactly once.
        private readonly Dictionary<string, CardRef> _oppSeen = new Dictionary<string, CardRef>();
        private string _matchKey;

        public List<OppRow> OppBoard = new List<OppRow>();
        public List<OppRow> OppDiscard = new List<OppRow>();
        public List<OppRow> OppSeen = new List<OppRow>();

        // Zone counts. Every one of these is public at the table: both players can see how many
        // prizes are left, how big a hand is, and how deep a deck is.
        public int OppPrizes, OppHand, OppDeck, OppDiscardCount, OppLost;
        public int MyPrizes, MyHand, MyDeckCount, MyDiscardCount, MyLost;

        public void SetDeck(Dictionary<string, int> cardIdToCount, string deckName)
        {
            DeckName = deckName;
            if (cardIdToCount == null) { _deck = null; return; }

            // Re-key from cardSourceID to display name so a board card that happens to be a
            // different printing still matches, and so two printings read as one line.
            var byKey = new Dictionary<string, int>();
            foreach (var kvp in cardIdToCount)
            {
                var key = Game.NameOf(kvp.Key) ?? kvp.Key;
                int cur;
                byKey[key] = byKey.TryGetValue(key, out cur) ? cur + kvp.Value : kvp.Value;
            }
            _deck = byKey;
        }

        /// <summary>
        /// Set the decklist from card NAMES that are already in accounting-key form (the clipboard
        /// import path, where there is no cardSourceID to resolve).
        /// </summary>
        public void SetDeckByName(Dictionary<string, int> nameToCount, string deckName,
                                  Dictionary<string, string> idsByName = null)
        {
            DeckName = deckName;
            if (nameToCount == null) { _deck = null; return; }
            var byKey = new Dictionary<string, int>();
            foreach (var kvp in nameToCount)
            {
                var key = Game.Pretty(kvp.Key);
                int cur;
                byKey[key] = byKey.TryGetValue(key, out cur) ? cur + kvp.Value : kvp.Value;
            }
            _deck = byKey;
        }

        /// <summary>Forget per-match state when a new match starts.</summary>
        public void ResetMatch(string matchKey)
        {
            _matchKey = matchKey;
            _oppSeen.Clear();
            OppBoard.Clear(); OppDiscard.Clear(); OppSeen.Clear();
            OppPrizes = OppHand = OppDeck = OppDiscardCount = OppLost = 0;
            MyPrizes = MyHand = MyDeckCount = MyDiscardCount = MyLost = 0;
        }

        public string MatchKey { get { return _matchKey; } }

        public void Update(BoardSnapshot s)
        {
            if (s == null || !s.Valid) return;

            MyPrizes = s.Me.PrizesLeft; MyHand = s.Me.HandCount; MyDeckCount = s.Me.DeckCount;
            MyDiscardCount = s.Me.Discard.Count; MyLost = s.Me.LostZone.Count;
            OppPrizes = s.Opp.PrizesLeft; OppHand = s.Opp.HandCount; OppDeck = s.Opp.DeckCount;
            OppDiscardCount = s.Opp.Discard.Count; OppLost = s.Opp.LostZone.Count;

            UpdateOpponent(s);
        }

        /// <summary>
        /// A short label for the opponent's deck, built from the Pokemon they actually put into
        /// play. Trainers are far too shared between decks to identify anything; the Pokemon line
        /// is what people name a deck after, so the two most-played ones are a decent label.
        /// Purely descriptive - it makes no claim to have identified a specific list.
        /// </summary>
        public string OpponentArchetype()
        {
            var mons = OppSeen
                .Where(r => Game.FormatOf(r.SourceId) == CardFormat.Pokemon)
                .OrderByDescending(r => r.Count).ThenBy(r => r.Name)
                .Select(r => r.Name)
                .Take(2)
                .ToList();
            if (mons.Count == 0) return "";
            return string.Join(" / ", mons.ToArray());
        }

        private void UpdateOpponent(BoardSnapshot s)
        {
            // Record anything of theirs we can currently identify. Keyed by entityID, so this
            // accumulates over the whole match and never double counts a card that merely moved
            // zones. Redacted cards all share the literal id "PRIVATE", so they are skipped.
            foreach (var c in s.Opp.All)
            {
                if (!c.Known) continue;
                if (string.IsNullOrEmpty(c.EntityId) || c.EntityId == Game.PrivateEntityID) continue;
                _oppSeen[c.EntityId] = c;
            }

            OppBoard = GroupBoard(s.Opp);
            OppDiscard = Group(s.Opp.Discard, "discard");
            OppSeen = _oppSeen.Values
                .GroupBy(c => c.Key)
                .Select(g => new OppRow { Name = g.Key, SourceId = g.First().SourceId, Count = g.Count(), Where = "" })
                .OrderByDescending(r => r.Count).ThenBy(r => r.Name)
                .ToList();
        }

        private static List<OppRow> GroupBoard(SideSnapshot side)
        {
            var list = new List<CardRef>();
            if (side.Active != null) list.Add(side.Active);
            list.AddRange(side.Bench);
            return Group(list, "board");
        }

        private static List<OppRow> Group(List<CardRef> cards, string where)
        {
            return cards.Where(c => c.Known)
                        .GroupBy(c => c.Key)
                        .Select(g => new OppRow { Name = g.Key, SourceId = g.First().SourceId, Count = g.Count(), Where = where })
                        .OrderByDescending(r => r.Count).ThenBy(r => r.Name)
                        .ToList();
        }
    }
}
