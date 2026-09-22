using System;
using System.Collections.Generic;
using System.Linq;

namespace PrizeTracker.Core
{
    internal class PrizeRow
    {
        public string Name;
        public string SourceId;   // representative printing, for card art
        public int Unaccounted;       // copies not yet seen anywhere
        public int GuaranteedPrized;  // copies that MUST be in the prizes
        public double ProbAnyPrized;  // P(at least one copy is prized)
        public double ExpectedPrized;
    }

    internal class DeckRow
    {
        public string Name;
        public string SourceId;   // representative printing, for card art
        public int Count;
        public bool Confirmed;    // physically seen sitting in the deck (e.g. mid-search)
        public double ProbTopDeck;
    }

    internal class OppRow
    {
        public string Name;
        public string SourceId;   // representative printing, for card art
        public int Count;
        public string Where;
    }

    /// <summary>
    /// One of the physical prize cards. There are exactly as many of these as prizes remaining,
    /// which is the point: the prize display should always be six cards (then five, then four...),
    /// filled in as you learn them, rather than a list of every card that might be prized.
    /// </summary>
    internal class PrizeSlot
    {
        public string Name;
        public string SourceId;
        public bool Known;
    }

    /// <summary>
    /// Turns a <see cref="BoardSnapshot"/> plus our own 60-card list into prize / deck / opponent
    /// readouts.
    ///
    /// The prize maths is the part the old tracker could not do. It used to show nothing at all
    /// until seen.Count + prizeCount == decklist.Count -- that is, until you had physically looked
    /// at your entire deck -- and then it dumped a flat list. In practice the overlay sat empty
    /// for most of most games.
    ///
    /// Instead: every card we cannot see sits in one of a set of interchangeable hidden slots
    /// (face-down deck + face-down prizes). Which slot a given copy occupies is uniformly random,
    /// so the prize question is plain hypergeometry over that pool. That yields a useful answer
    /// from turn one, and it sharpens every time a card is revealed.
    /// </summary>
    internal class Tracker
    {
        // Our decklist: accounting key -> copies. Null until we can read the active deck.
        private Dictionary<string, int> _deck;
        // Accounting is keyed on NAME, but art needs an id, so remember one printing per key.
        private readonly Dictionary<string, string> _idForKey = new Dictionary<string, string>();
        public string DeckName { get; private set; }
        public bool DeckKnown { get { return _deck != null && _deck.Count > 0; } }
        public int DeckSize { get { return _deck == null ? 0 : _deck.Values.Sum(); } }

        // Opponent cards revealed at any point this match, keyed by entityID so a card that moves
        // zones (hand -> discard -> shuffled back) is counted exactly once.
        private readonly Dictionary<string, CardRef> _oppSeen = new Dictionary<string, CardRef>();
        private string _matchKey;

        // --- outputs -------------------------------------------------------
        public List<PrizeRow> Prizes = new List<PrizeRow>();
        /// <summary>One entry per remaining prize card - this is what the PRIZES tab shows.</summary>
        public List<PrizeSlot> PrizeSlots = new List<PrizeSlot>();
        /// <summary>Cards placed on the bottom of the deck, counting down as you draw.</summary>
        public readonly BottomStack Bottom = new BottomStack();
        public List<DeckRow> DeckRows = new List<DeckRow>();
        public List<OppRow> OppBoard = new List<OppRow>();
        public List<OppRow> OppDiscard = new List<OppRow>();
        public List<OppRow> OppSeen = new List<OppRow>();
        public string Status = "Waiting for a match...";
        public int PrizeUnknown, DeckUnknown, TotalUnknown;

        /// <summary>
        /// The fewest unidentified deck cards seen at any point this match.
        ///
        /// Prizes can only be solved while the whole deck is momentarily revealed - a deck search -
        /// because that is what leaves the prizes as the only unaccounted cards. If this never
        /// reaches 0 then no full reveal was ever observed, which distinguishes "you have not
        /// searched yet" from "we searched and failed to see it" without guessing.
        /// </summary>
        public int MinDeckUnknown = int.MaxValue;
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
                if (!_idForKey.ContainsKey(key)) _idForKey[key] = kvp.Key;
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
                string id;
                if (idsByName != null && idsByName.TryGetValue(kvp.Key, out id) && !_idForKey.ContainsKey(key))
                    _idForKey[key] = id;
            }
            _deck = byKey;
        }

        /// <summary>Forget per-match state when a new match starts.</summary>
        public void ResetMatch(string matchKey)
        {
            _matchKey = matchKey;
            _oppSeen.Clear();
            PrizeSlots.Clear();
            Bottom.Reset();
            _solvedPrizes = null;
            MinDeckUnknown = int.MaxValue;
            _searchLogs = 0;
            _solveLogs = 0;
            _lastPrizeCount = -1;
            _lastVisible = null;
            Prizes.Clear(); DeckRows.Clear();
            OppBoard.Clear(); OppDiscard.Clear(); OppSeen.Clear();
        }

        public string MatchKey { get { return _matchKey; } }

        /// <summary>A representative printing id for an accounting key, for loading card art.</summary>
        private string IdFor(string key)
        {
            string id;
            return _idForKey.TryGetValue(key, out id) ? id : null;
        }

        /// <summary>Remember the printing we actually saw, so art matches what is on the board.</summary>
        private void RememberId(CardRef c)
        {
            if (c == null || !c.Known || string.IsNullOrEmpty(c.Key)) return;
            if (!_idForKey.ContainsKey(c.Key)) _idForKey[c.Key] = c.SourceId;
        }

        // Once the deck has been seen in full, the prizes are SOLVED and stay solved.
        //
        // This has to be LATCHED rather than recomputed every tick. Searching your deck reveals
        // every card in it, so at that instant "decklist minus everything visible" is exactly the
        // prize list -- but the search then ends and the deck goes face down again. Recomputing
        // from the current board would discard the answer the moment the search window closed and
        // drop back to probabilities, throwing away the very knowledge a player keeps in their
        // head for the rest of the game.
        //
        // Prizes only ever leave the prize zone, so the latched set is only ever reduced.
        private Dictionary<string, int> _solvedPrizes;
        private int _searchLogs;
        private int _solveLogs;
        private int _mismatchLogs;

        /// <summary>
        /// Optional diagnostic sink, supplied by the plugin.
        ///
        /// Core must not reference BepInEx: the test project compiles these same files and runs
        /// the real logic, so a direct Plugin.Log call here breaks the build for the tests rather
        /// than for the game - which is exactly what happened. An injected delegate keeps the
        /// diagnostics without dragging the host in.
        /// </summary>
        public static Action<string> Diagnostic;

        /// <summary>
        /// Record what the zones look like while a search is open.
        ///
        /// Solving prizes depends on the deck being fully identified at some instant, and a
        /// confirmed case exists where search cards were played and that never happened. The
        /// question is WHERE the revealed cards are: still in the deck zone but unidentified, or
        /// moved into the pending/staging zone while the picker is open. Counting both settles it
        /// instead of another round of guessing.
        /// </summary>
        private void LogSearchWindow(BoardSnapshot s)
        {
            if (_searchLogs >= 6) return;
            int pending = s.Me.Pending.Count;
            if (pending == 0 && DeckUnknown == s.Me.Deck.Count) return;   // nothing interesting

            _searchLogs++;
            if (Diagnostic != null) Diagnostic(string.Format(
                "search window: deck {0} cards ({1} identified) | pending {2} ({3} identified) | " +
                "hand {4} | prizes {5} unknown",
                s.Me.Deck.Count, s.Me.Deck.Count - DeckUnknown,
                pending, s.Me.Pending.Count(c => c.Known),
                s.Me.Hand.Count, PrizeUnknown));
        }
        private int _lastPrizeCount = -1;
        private Dictionary<string, int> _lastVisible;

        public bool PrizesSolved { get { return _solvedPrizes != null; } }

        /// <summary>
        /// A prize was taken: it moves to hand, so it appears as a newly visible card. Drop it from
        /// the latched set. Anything that cannot be attributed is retired by count as a fallback,
        /// so the list can never claim more prizes than are actually left.
        /// </summary>
        private void RetireTakenPrizes(int taken, Dictionary<string, int> visibleNow)
        {
            if (_solvedPrizes == null || taken <= 0) return;

            if (_lastVisible != null)
            {
                foreach (var kvp in visibleNow)
                {
                    if (taken <= 0) break;
                    int before;
                    _lastVisible.TryGetValue(kvp.Key, out before);
                    int appeared = kvp.Value - before;
                    int held;
                    if (appeared <= 0 || !_solvedPrizes.TryGetValue(kvp.Key, out held) || held <= 0) continue;
                    int drop = Math.Min(Math.Min(appeared, held), taken);
                    _solvedPrizes[kvp.Key] = held - drop;
                    if (_solvedPrizes[kvp.Key] <= 0) _solvedPrizes.Remove(kvp.Key);
                    taken -= drop;
                }
            }

            while (taken > 0 && _solvedPrizes.Count > 0)
            {
                var k = _solvedPrizes.Keys.First();
                _solvedPrizes[k]--;
                if (_solvedPrizes[k] <= 0) _solvedPrizes.Remove(k);
                taken--;
            }
        }

        public void Update(BoardSnapshot s)
        {
            if (s == null || !s.Valid) { Status = "Waiting for a match..."; return; }

            MyPrizes = s.Me.PrizesLeft; MyHand = s.Me.HandCount; MyDeckCount = s.Me.DeckCount;
            MyDiscardCount = s.Me.Discard.Count; MyLost = s.Me.LostZone.Count;
            OppPrizes = s.Opp.PrizesLeft; OppHand = s.Opp.HandCount; OppDeck = s.Opp.DeckCount;
            OppDiscardCount = s.Opp.Discard.Count; OppLost = s.Opp.LostZone.Count;

            Bottom.Update(s.Me);
            UpdateOpponent(s);
            UpdateMine(s);
            BuildPrizeSlots();
        }

        /// <summary>
        /// Turns whatever we know into the actual prize cards: one slot per remaining prize, each
        /// either a named card or still face down.
        ///
        /// Both paths converge here. A row is only allowed to fill a slot if it is CERTAIN - a
        /// revealed prize, or a copy that provably cannot fit anywhere else - so a slot is never
        /// filled by a guess. Everything else stays face down, exactly as it is on the table.
        /// </summary>
        private void BuildPrizeSlots()
        {
            PrizeSlots.Clear();
            foreach (var r in Prizes)
            {
                for (int i = 0; i < r.GuaranteedPrized && PrizeSlots.Count < MyPrizes; i++)
                    PrizeSlots.Add(new PrizeSlot { Name = r.Name, SourceId = r.SourceId, Known = true });
            }
            while (PrizeSlots.Count < MyPrizes)
                PrizeSlots.Add(new PrizeSlot { Known = false });
        }

        /// <summary>Card name for a key, for diagnostics. Falls back to the raw key.</summary>
        private static string NameOf(string key)
        {
            try { var n = Game.NameOf(key); if (!string.IsNullOrEmpty(n)) return n; } catch { }
            return key;
        }

        /// <summary>Cards that might be prized, most likely first - only meaningful while unsolved.</summary>
        public List<PrizeRow> Likely
        {
            get { return Prizes.Where(r => r.GuaranteedPrized == 0 && r.ProbAnyPrized > 0).ToList(); }
        }

        public int PrizeSlotsKnown { get { return PrizeSlots.Count(p => p.Known); } }

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

        // -------------------------------------------------------------------
        private void UpdateMine(BoardSnapshot s)
        {
            Prizes.Clear(); DeckRows.Clear();

            if (!DeckKnown)
            {
                Status = "Active deck not readable yet - visit the deck screen once.";
                return;
            }

            // Known = every card of ours whose identity the server has actually shown us, in any
            // zone: board, hand, discard, lost zone, attachments, and any deck or prize card that
            // got revealed. Unknown = still face down.
            var known = new Dictionary<string, int>();
            int unknownTotal = 0;
            foreach (var c in s.Me.All)
            {
                if (c.Known)
                {
                    int cur;
                    known[c.Key] = known.TryGetValue(c.Key, out cur) ? cur + 1 : 1;
                    RememberId(c);
                }
                else unknownTotal++;
            }

            PrizeUnknown = s.Me.Prize.Count(c => !c.Known);
            DeckUnknown = s.Me.Deck.Count(c => !c.Known);
            if (DeckUnknown < MinDeckUnknown) MinDeckUnknown = DeckUnknown;
            LogSearchWindow(s);
            TotalUnknown = unknownTotal;

            // Cards from our list that we have not accounted for yet.
            var unaccounted = new Dictionary<string, int>();
            foreach (var kvp in _deck)
            {
                int seen;
                known.TryGetValue(kvp.Key, out seen);
                int left = kvp.Value - seen;
                if (left > 0) unaccounted[kvp.Key] = left;
            }

            int unaccountedTotal = unaccounted.Values.Sum();

            // The solve turns on four numbers and nothing else. When the deck is fully identified
            // and prizes still are not solved, printing them says which one is wrong instead of
            // leaving "0 of 6 known" as the only visible symptom.
            if (DeckUnknown == 0 && _solvedPrizes == null && Diagnostic != null && _solveLogs < 4)
            {
                _solveLogs++;
                Diagnostic(string.Format(
                    "solve check: deckUnknown={0} unaccounted={1} totalUnknown={2} prizeUnknown={3}" +
                    " -> {4}",
                    DeckUnknown, unaccountedTotal, TotalUnknown, PrizeUnknown,
                    unaccountedTotal != TotalUnknown ? "BLOCKED by the decklist sanity gate"
                    : unaccountedTotal != PrizeUnknown ? "unaccounted does not equal the prize count"
                    : "should solve"));
            }

            // When the gate blocks, say WHICH cards disagree. "5 unaccounted vs 6 hidden" names a
            // discrepancy without naming its cause, and the cause is always a specific card:
            // either one on the board that our list does not contain, or one we have matched more
            // copies of than the list holds.
            if (unaccountedTotal != TotalUnknown && Diagnostic != null && _mismatchLogs < 2)
            {
                _mismatchLogs++;
                var over = new List<string>();
                foreach (var kvp in known)
                {
                    int inDeck;
                    _deck.TryGetValue(kvp.Key, out inDeck);
                    if (kvp.Value > inDeck)
                        over.Add(NameOf(kvp.Key) + " seen " + kvp.Value + " but list has " + inDeck);
                }
                var short_ = new List<string>();
                foreach (var kvp in unaccounted) short_.Add(NameOf(kvp.Key) + " x" + kvp.Value);

                Diagnostic("decklist mismatch: board has cards the list does not -> " +
                           (over.Count == 0 ? "(none)" : string.Join("; ", over.ToArray())));
                Diagnostic("decklist mismatch: still unaccounted -> " +
                           (short_.Count == 0 ? "(none)" : string.Join("; ", short_.ToArray())));
                // Where does the extra card actually live? All is the engine's ownership walk;
                // comparing it against the individual zones says whether a card is counted in two
                // places or the walk simply holds one more than the zones do.
                int zoneSum = (s.Me.Active != null ? 1 : 0) + s.Me.Bench.Count + s.Me.Hand.Count +
                              s.Me.Deck.Count + s.Me.Discard.Count + s.Me.LostZone.Count +
                              s.Me.Pending.Count + s.Me.Prize.Count(c => c != null);
                Diagnostic(string.Format(
                    "decklist mismatch: zones active={0} bench={1} hand={2} deck={3} discard={4} " +
                    "lost={5} pending={6} prize={7} => {8}; All={9} (hidden in All={10})",
                    s.Me.Active != null ? 1 : 0, s.Me.Bench.Count, s.Me.Hand.Count, s.Me.Deck.Count,
                    s.Me.Discard.Count, s.Me.LostZone.Count, s.Me.Pending.Count,
                    s.Me.Prize.Count(c => c != null), zoneSum, s.Me.All.Count,
                    s.Me.All.Count(c => !c.Known)));

                // The identified cards, by name, against what the list says we should have. With
                // zones and the ownership walk agreeing, the discrepancy has to be visible here.
                var lines = new List<string>();
                foreach (var kvp in known.OrderBy(k => k.Key))
                {
                    int inDeck;
                    _deck.TryGetValue(kvp.Key, out inDeck);
                    lines.Add(NameOf(kvp.Key) + " " + kvp.Value + "/" + inDeck);
                }
                Diagnostic("decklist mismatch: identified (seen/list) -> " +
                           string.Join(", ", lines.ToArray()));

                // What is actually in the prize slots, and in the deck the client reports? If the
                // client is not redacting (a locally simulated match may not), a prized card can
                // be listed in the deck AND occupy a prize slot - the same physical card counted
                // once as identified and once as hidden, which is exactly 55 + 6 = 61.
                var pz = new List<string>();
                foreach (var c in s.Me.Prize)
                    if (c != null)
                        pz.Add((string.IsNullOrEmpty(c.SourceId) ? "hidden" : c.Name ?? c.SourceId) +
                               "[" + (string.IsNullOrEmpty(c.EntityId) ? "-" : c.EntityId) + "]");
                Diagnostic("decklist mismatch: prize slots -> " + string.Join(", ", pz.ToArray()));

                int deckHidden = s.Me.Deck.Count(c => !c.Known);
                Diagnostic("decklist mismatch: deck zone " + s.Me.Deck.Count + " (" + deckHidden +
                           " hidden), hand " + s.Me.Hand.Count + " (" +
                           s.Me.Hand.Count(c => !c.Known) + " hidden)");

                Diagnostic("decklist mismatch: deck total " + _deck.Values.Sum() +
                           ", matched " + known.Values.Sum() + ", hidden " + TotalUnknown +
                           " (matched + hidden should equal the deck total)");
            }

            // Sanity gate: the unaccounted multiset should exactly fill the hidden slots. A
            // mismatch means the list we loaded is not the one being played (deck switched, or a
            // name we could not resolve), so say so rather than show confident wrong numbers.
            if (unaccountedTotal != TotalUnknown)
            {
                Status = string.Format(
                    "Decklist does not match the board ({0} unaccounted vs {1} hidden) - odds hidden.",
                    unaccountedTotal, TotalUnknown);
                BuildConfirmedDeckRows(s);
                return;
            }

            // --- solve, and latch ------------------------------------------
            // Retire any prize that has been taken since the last tick, before re-solving.
            int prizesNow = s.Me.PrizesLeft;
            if (_lastPrizeCount >= 0 && prizesNow < _lastPrizeCount)
                RetireTakenPrizes(_lastPrizeCount - prizesNow, known);
            _lastPrizeCount = prizesNow;
            _lastVisible = known;

            // The moment nothing in the deck is face down - i.e. we are looking through it - every
            // remaining unaccounted card must be a prize. That is the whole trick, and it is why
            // prize tracking only really begins at your first deck search.
            if (DeckUnknown == 0 && unaccountedTotal == PrizeUnknown && PrizeUnknown > 0)
                _solvedPrizes = new Dictionary<string, int>(unaccounted);

            if (_solvedPrizes != null && _solvedPrizes.Count > 0)
            {
                BuildSolvedView(s, known);
                return;
            }

            int nonPrizeUnknown = TotalUnknown - PrizeUnknown;

            // Prize cards whose identity we have actually been shown (some effects flip prizes
            // face up). They are facts, not estimates, so they lead the list at 100%.
            var revealedPrizes = new Dictionary<string, int>();
            foreach (var c in s.Me.Prize)
            {
                if (!c.Known) continue;
                int cur;
                revealedPrizes[c.Key] = revealedPrizes.TryGetValue(c.Key, out cur) ? cur + 1 : 1;
            }
            foreach (var kvp in revealedPrizes)
            {
                Prizes.Add(new PrizeRow
                {
                    Name = kvp.Key,
                    SourceId = IdFor(kvp.Key),
                    Unaccounted = kvp.Value,
                    GuaranteedPrized = kvp.Value,
                    ProbAnyPrized = 1.0,
                    ExpectedPrized = kvp.Value,
                });
            }

            // Only the still-hidden prize slots produce odds. Without this gate, once every prize
            // was revealed the tab would list the entire remaining deck at 0% under a "PRIZED"
            // heading, which reads as though those cards were prized.
            foreach (var kvp in PrizeUnknown > 0 ? unaccounted : new Dictionary<string, int>())
            {
                int k = kvp.Value;
                var row = new PrizeRow();
                row.Name = kvp.Key;
                row.SourceId = IdFor(kvp.Key);
                row.Unaccounted = k;
                // If there are not enough non-prize hidden slots to hold every copy, the overflow
                // is provably prized. This is what allows an exact call before the deck is fully
                // revealed, which the old all-or-nothing approach could never do.
                row.GuaranteedPrized = Math.Max(0, k - nonPrizeUnknown);
                row.ProbAnyPrized = ProbAtLeastOne(TotalUnknown, PrizeUnknown, k);
                row.ExpectedPrized = TotalUnknown > 0 ? (double)k * PrizeUnknown / TotalUnknown : 0.0;
                Prizes.Add(row);
            }

            Prizes = Prizes
                .OrderByDescending(r => r.GuaranteedPrized)
                .ThenByDescending(r => r.ProbAnyPrized)
                .ThenBy(r => r.Name)
                .ToList();

            // Deck view: the same hidden pool, read as "what can I still draw".
            // P(the very next card off the deck is X) = copies / hidden slots, because every
            // hidden slot is equally likely to hold any one unaccounted copy.
            foreach (var kvp in unaccounted)
            {
                DeckRows.Add(new DeckRow
                {
                    Name = kvp.Key,
                    SourceId = IdFor(kvp.Key),
                    Count = kvp.Value,
                    Confirmed = false,
                    ProbTopDeck = TotalUnknown > 0 ? (double)kvp.Value / TotalUnknown : 0.0,
                });
            }
            BuildConfirmedDeckRows(s);
            DeckRows = DeckRows.OrderByDescending(r => r.Confirmed).ThenByDescending(r => r.Count)
                               .ThenBy(r => r.Name).ToList();

            if (PrizeUnknown == 0)
                Status = "All prizes revealed.";
            else if (DeckUnknown == 0)
                Status = "Deck fully known - prizes are exact.";
            else
                Status = string.Format("{0} hidden: {1} in deck, {2} in prizes.",
                                       TotalUnknown, DeckUnknown, PrizeUnknown);
        }

        /// <summary>
        /// The view once prizes are solved: no probabilities anywhere, because there is nothing
        /// left to estimate.
        ///
        ///   prizes = the latched set
        ///   deck   = decklist - prizes - everything visible
        ///
        /// Both are exact, and they stay exact for the rest of the game even though the deck is
        /// face down again.
        /// </summary>
        private void BuildSolvedView(BoardSnapshot s, Dictionary<string, int> known)
        {
            foreach (var kvp in _solvedPrizes)
                Prizes.Add(new PrizeRow
                {
                    Name = kvp.Key,
                    SourceId = IdFor(kvp.Key),
                    Unaccounted = kvp.Value,
                    GuaranteedPrized = kvp.Value,
                    ProbAnyPrized = 1.0,
                    ExpectedPrized = kvp.Value,
                });
            Prizes = Prizes.OrderByDescending(r => r.GuaranteedPrized).ThenBy(r => r.Name).ToList();

            // "deck = decklist - prizes - everything visible" means everything visible OUTSIDE the
            // deck. While a search is open the deck's own cards are revealed too, and counting
            // those as visible would subtract the deck from itself and report it as empty.
            var outsideDeck = new Dictionary<string, int>(known);
            foreach (var c in s.Me.Deck)
            {
                int held;
                if (!c.Known || !outsideDeck.TryGetValue(c.Key, out held) || held <= 0) continue;
                outsideDeck[c.Key] = held - 1;
            }

            foreach (var kvp in _deck)
            {
                int seen, prized;
                outsideDeck.TryGetValue(kvp.Key, out seen);
                _solvedPrizes.TryGetValue(kvp.Key, out prized);
                int inDeck = kvp.Value - seen - prized;
                if (inDeck > 0)
                    DeckRows.Add(new DeckRow
                    {
                        Name = kvp.Key,
                        SourceId = IdFor(kvp.Key),
                        Count = inDeck,
                        Confirmed = true,
                        ProbTopDeck = MyDeckCount > 0 ? (double)inDeck / MyDeckCount : 0.0,
                    });
            }
            DeckRows = DeckRows.OrderByDescending(r => r.Count).ThenBy(r => r.Name).ToList();

            Status = string.Format("Prizes solved: {0} card{1} known exactly.",
                                   _solvedPrizes.Values.Sum(),
                                   _solvedPrizes.Values.Sum() == 1 ? "" : "s");
        }

        /// <summary>Cards we have literally seen sitting in the deck (a mid-search reveal).</summary>
        private void BuildConfirmedDeckRows(BoardSnapshot s)
        {
            var confirmed = new Dictionary<string, int>();
            foreach (var c in s.Me.Deck)
            {
                if (!c.Known) continue;
                int cur;
                confirmed[c.Key] = confirmed.TryGetValue(c.Key, out cur) ? cur + 1 : 1;
            }
            foreach (var kvp in confirmed)
                DeckRows.Add(new DeckRow { Name = kvp.Key, SourceId = IdFor(kvp.Key), Count = kvp.Value, Confirmed = true, ProbTopDeck = 0 });
        }

        /// <summary>
        /// P(at least one of k copies sits in the prize slots), drawing prizeSlots slots without
        /// replacement from total interchangeable hidden slots. Computed as a running product
        /// rather than via factorials, so it cannot overflow.
        /// </summary>
        internal static double ProbAtLeastOne(int total, int prizeSlots, int k)
        {
            if (total <= 0 || prizeSlots <= 0 || k <= 0) return 0.0;
            if (prizeSlots >= total) return 1.0;
            if (k > total - prizeSlots) return 1.0;   // cannot fit them all outside the prizes
            double pNone = 1.0;
            for (int i = 0; i < prizeSlots; i++)
            {
                pNone *= (double)(total - k - i) / (total - i);
                if (pNone <= 0) return 1.0;
            }
            return 1.0 - pNone;
        }

        // -------------------------------------------------------------------
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
                RememberId(c);
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
