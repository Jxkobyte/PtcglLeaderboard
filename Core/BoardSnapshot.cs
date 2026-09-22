using System.Collections.Generic;
using System.Linq;
using MatchLogic;

namespace PrizeTracker.Core
{
    /// <summary>One card as the client currently sees it. Unknown cards are server-redacted.</summary>
    internal class CardRef
    {
        public string EntityId;
        public string SourceId;
        public string Name;         // null when the card is hidden from us
        public int Damage;
        public int EnergyCount;
        public int ToolCount;

        /// <summary>
        /// Identified by the server. Deliberately keyed on SourceId alone: a card can be perfectly
        /// visible yet have no cached display name (a set the client has not cached), and treating
        /// that as hidden would corrupt the unaccounted-vs-hidden accounting the prize odds rest on.
        /// </summary>
        public bool Known { get { return !string.IsNullOrEmpty(SourceId); } }

        /// <summary>
        /// Key used for all accounting. Deliberately the NAME, not the cardSourceID: different
        /// printings of the same card are functionally identical for prize/deck purposes, and
        /// name-matching survives the deck listing and the board disagreeing about which printing
        /// a copy is. Falls back to the id when the name can't be resolved.
        /// </summary>
        public string Key { get { return !string.IsNullOrEmpty(Name) ? Name : SourceId; } }

        public static CardRef From(CardEntity e)
        {
            if (e == null) return null;
            var c = new CardRef();
            c.EntityId = e.entityID;
            try { c.SourceId = e.cardSourceID; } catch { }
            c.Name = Game.NameOf(c.SourceId);
            try { c.Damage = e.damageCounters; } catch { }
            try { c.EnergyCount = e.attachedEnergy != null ? e.attachedEnergy.Count : 0; } catch { }
            try { c.ToolCount = e.attachedTools != null ? e.attachedTools.Count : 0; } catch { }
            return c;
        }
    }

    /// <summary>Every zone for one seat, as the client is allowed to see it.</summary>
    internal class SideSnapshot
    {
        public CardRef Active;
        public List<CardRef> Bench = new List<CardRef>();
        public List<CardRef> Hand = new List<CardRef>();
        public List<CardRef> Deck = new List<CardRef>();
        public List<CardRef> Discard = new List<CardRef>();
        public List<CardRef> LostZone = new List<CardRef>();
        public List<CardRef> Prize = new List<CardRef>();   // taken prizes are removed
        public List<CardRef> Pending = new List<CardRef>(); // mid-effect limbo
        public List<CardRef> All = new List<CardRef>();     // every card this seat owns, all zones

        public int PrizesLeft { get { return Prize.Count; } }
        public int DeckCount { get { return Deck.Count; } }
        public int HandCount { get { return Hand.Count; } }
    }

    internal class BoardSnapshot
    {
        public bool Valid;
        public bool IAmPlayer1;
        public int Turn;
        public CardRef Stadium;
        public bool StadiumIsMine;
        public SideSnapshot Me = new SideSnapshot();
        public SideSnapshot Opp = new SideSnapshot();

        /// <summary>
        /// Reads the live MatchBoard. Cheap: GetBoardState() is a field getter and this only walks
        /// already-materialised lists, so polling a few times a second costs nothing measurable.
        /// </summary>
        public static BoardSnapshot Read()
        {
            return Read(Game.Info());
        }

        public static BoardSnapshot Read(global::RainierClientSDK.MatchInfo info)
        {
            var snap = new BoardSnapshot();
            var board = Game.BoardOf(info);
            if (board == null) return snap;

            snap.IAmPlayer1 = Game.IAmPlayer1Of(info);
            try { snap.Turn = board.GetTotalTurnCount(); } catch { }

            bool p1 = snap.IAmPlayer1;
            Fill(snap.Me, board, p1);
            Fill(snap.Opp, board, !p1);
            ApplyKnownDeck(snap.Me, board, info, p1);

            try
            {
                if (board.stadium != null)
                {
                    snap.Stadium = CardRef.From(board.stadium);
                    snap.StadiumIsMine = (board.stadium.isPlayer1 == p1);
                }
            }
            catch { }

            snap.Valid = true;
            return snap;
        }

        /// <summary>
        /// Replace our own deck list with the identified cards the client is actually holding.
        ///
        /// This is the difference between prize solving working and not. The deck list reachable
        /// through GetBoardState() is OPAQUE PLACEHOLDERS - measured live, 46 cards and 0 of them
        /// identified, even with a search picker open. The real identities live in MatchInfo's
        /// private matchEntities list, which during that same search reported Player2Deck=46 fully
        /// identified. Hidden cards are simply absent from it, which is what makes it usable: once
        /// the deck is known, the only cards left unaccounted for are the six prizes, and that is
        /// the entire trick this tool rests on.
        ///
        /// Only OUR side is corrected - the opponent's deck is genuinely hidden and must stay so.
        /// If the list is unavailable or holds nothing for the deck, the board's own version is
        /// kept and everything behaves exactly as before.
        /// </summary>
        private static void ApplyKnownDeck(SideSnapshot side, MatchBoard board,
                                           global::RainierClientSDK.MatchInfo info, bool asPlayer1)
        {
            if (info == null) return;
            try
            {
                var f = typeof(global::RainierClientSDK.MatchInfo).GetField("matchEntities",
                            System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.NonPublic);
                if (f == null) return;
                var list = f.GetValue(info) as System.Collections.IEnumerable;
                if (list == null) return;

                var want = asPlayer1 ? BoardPos.Player1Deck : BoardPos.Player2Deck;
                var known = new List<CardEntity>();
                lock (list)
                {
                    foreach (var o in list)
                    {
                        var ce = o as CardEntity;
                        if (ce == null || string.IsNullOrEmpty(ce.cardSourceID)) continue;
                        if (ce.cardSourceID == Game.PrivateEntityID) continue;
                        if (ce.currentGamePos != want) continue;
                        known.Add(ce);
                    }
                }

                // Only trust it when it accounts for the whole deck. A partial list would make the
                // deck look smaller than it is and quietly corrupt every count downstream.
                if (known.Count == 0 || known.Count != side.Deck.Count) return;
                side.Deck = Conv(known);

                // ALSO rebuild the all-cards walk, which is what the accounting actually reads.
                //
                // Correcting only side.Deck left the tracker in a contradictory state: the deck
                // reported as fully known while the same 46 cards were still counted as
                // unaccounted, because Tracker totals over side.All - the engine's ownership walk
                // over the BOARD, which is still placeholders. The result was "Deck fully known"
                // next to "0 of 5 known", which is the sort of disagreement that should be
                // impossible rather than merely unlikely.
                var all = asPlayer1 ? board.GetAllPlayer1Cards() : board.GetAllPlayer2Cards();
                if (all == null) return;

                var rebuilt = new List<CardEntity>(all.Count);
                foreach (var ce in all)
                    if (ce != null && ce.currentGamePos != want) rebuilt.Add(ce);
                rebuilt.AddRange(known);
                side.All = Conv(rebuilt);
            }
            catch { /* the board's own deck list stays in place */ }
        }

        private static void Fill(SideSnapshot side, MatchBoard b, bool asPlayer1)
        {
            side.Active = CardRef.From(asPlayer1 ? b.p1Active : b.p2Active);
            side.Bench = Conv(asPlayer1 ? b.p1Bench : b.p2Bench);
            side.Hand = Conv(asPlayer1 ? b.p1Hand : b.p2Hand);
            side.Deck = Conv(asPlayer1 ? b.p1Deck : b.p2Deck);
            side.Discard = Conv(asPlayer1 ? b.p1Discard : b.p2Discard);
            side.LostZone = Conv(asPlayer1 ? b.p1LostZone : b.p2LostZone);

            // Taken prizes are nulled IN PLACE in a fixed-size list, so the raw .Count is always 6.
            // Counting non-null entries is the only correct way to read "prizes remaining".
            var prize = asPlayer1 ? b.p1Prize : b.p2Prize;
            side.Prize = Conv(prize);

            var pending = new List<CardEntity>();
            try
            {
                var hidden = asPlayer1 ? b.p1HiddenPending : b.p2HiddenPending;
                var visible = asPlayer1 ? b.p1VisiblePending : b.p2VisiblePending;
                if (hidden != null) pending.AddRange(hidden);
                if (visible != null) pending.AddRange(visible);
            }
            catch { }
            side.Pending = Conv(pending);

            // The engine's own full-ownership walk: active/bench/hand/discard/lost/pending/prizes/
            // stadium/deck PLUS everything attached (energy, tools, the evolution stack underneath
            // a Pokemon). Using it means card conservation is guaranteed rather than re-derived.
            try
            {
                var all = asPlayer1 ? b.GetAllPlayer1Cards() : b.GetAllPlayer2Cards();
                side.All = Conv(all);
            }
            catch { side.All = new List<CardRef>(); }
        }

        private static List<CardRef> Conv(List<CardEntity> src)
        {
            var list = new List<CardRef>();
            if (src == null) return list;
            for (int i = 0; i < src.Count; i++)
            {
                if (src[i] == null) continue;   // nulled prize slot
                var c = CardRef.From(src[i]);
                if (c != null) list.Add(c);
            }
            return list;
        }
    }
}
