using System;
using System.Collections.Generic;
using System.Linq;
using PrizeTracker.Core;

namespace TrackerTests
{
    /// <summary>
    /// Exercises the REAL Tracker/BoardSnapshot source (linked into this assembly) against
    /// hand-built board snapshots. Nothing here stubs the logic under test.
    /// </summary>
    internal static class Program
    {
        private static int _fail;

        private static void Check(string what, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what + (detail.Length > 0 ? "   [" + detail + "]" : ""));
            if (!ok) _fail++;
        }

        private static void CheckNear(string what, double got, double want, double tol = 0.0005)
        {
            bool ok = Math.Abs(got - want) <= tol;
            Check(what, ok, string.Format("got {0:F4} want {1:F4}", got, want));
        }

        private static CardRef Known(string name, string id = null)
        {
            return new CardRef { EntityId = Guid.NewGuid().ToString(), SourceId = id ?? name, Name = name };
        }

        private static CardRef Hidden()
        {
            return new CardRef { EntityId = "PRIVATE", SourceId = null, Name = null };
        }

        private static List<CardRef> HiddenMany(int n)
        {
            var l = new List<CardRef>();
            for (int i = 0; i < n; i++) l.Add(Hidden());
            return l;
        }

        /// <summary>
        /// Builds a snapshot from an explicit description of where our 60 cards are.
        /// 'All' is filled the way MatchBoard.GetAllPlayerXCards would: every zone concatenated.
        /// </summary>
        private static BoardSnapshot Make(
            List<CardRef> hand, int deckHidden, int prizeHidden,
            List<CardRef> discard = null, List<CardRef> deckRevealed = null,
            List<CardRef> prizeRevealed = null, List<CardRef> bench = null,
            CardRef active = null, SideSnapshot opp = null)
        {
            var me = new SideSnapshot();
            me.Hand = hand ?? new List<CardRef>();
            me.Discard = discard ?? new List<CardRef>();
            me.Bench = bench ?? new List<CardRef>();
            me.Active = active;
            me.Deck = new List<CardRef>();
            me.Deck.AddRange(deckRevealed ?? new List<CardRef>());
            me.Deck.AddRange(HiddenMany(deckHidden));
            me.Prize = new List<CardRef>();
            me.Prize.AddRange(prizeRevealed ?? new List<CardRef>());
            me.Prize.AddRange(HiddenMany(prizeHidden));

            me.All = new List<CardRef>();
            me.All.AddRange(me.Hand);
            me.All.AddRange(me.Discard);
            me.All.AddRange(me.Bench);
            if (me.Active != null) me.All.Add(me.Active);
            me.All.AddRange(me.Deck);
            me.All.AddRange(me.Prize);

            return new BoardSnapshot
            {
                Valid = true,
                IAmPlayer1 = true,
                Me = me,
                Opp = opp ?? new SideSnapshot(),
            };
        }

        private static Dictionary<string, int> Deck60(params object[] pairs)
        {
            var d = new Dictionary<string, int>();
            for (int i = 0; i < pairs.Length; i += 2) d[(string)pairs[i]] = (int)pairs[i + 1];
            return d;
        }

        private static PrizeRow Row(Tracker t, string name)
        {
            return t.Prizes.FirstOrDefault(r => r.Name == name);
        }

        private static void Main()
        {
            Console.WriteLine("=== Tracker logic tests (running the real source) ===\n");

            ProbabilityMaths();
            FreshGame();
            AfterSeeingCopies();
            ProvablyPrized();
            DeckFullyRevealed();
            RevealedPrizes();
            MismatchedDecklist();
            OpponentTracking();
            OpponentNoDoubleCountOnZoneMove();
            UnnamedCardStillCounts();
            PrizeKnowledgeSurvivesSearchClosing();
            TakenPrizeIsRemoved();
            PrizeSlotsAlwaysMatchPrizesLeft();
            MatchHistoryRoundTrips();
            BattleLogSurvivesRoundTrip();
            BottomStackCountsDown();
            BottomStackHandlesMultipleUses();
            BottomStackUnknownOrderAndShuffle();

            Console.WriteLine();
            Console.WriteLine(_fail == 0 ? "ALL TESTS PASSED" : _fail + " TEST(S) FAILED");
            Environment.Exit(_fail == 0 ? 0 : 1);
        }

        /// <summary>
        /// A battle log is multi-line and a record is ONE line of a .jsonl file.
        ///
        /// The escaper used to replace every newline with a space, which round-tripped without
        /// error while silently destroying the log's structure - the worst kind of failure, since
        /// nothing throws and the data just quietly becomes wrong. This asserts the text survives
        /// byte for byte, and that the record still occupies exactly one physical line.
        /// </summary>
        private static void BattleLogSurvivesRoundTrip()
        {
            Console.WriteLine("-- battle log round trip --");
            var log = "Turn 1 - Jacob\r\nPlayed \"Ultra Ball\"\n- discarded 2 cards\n\tindented\\slashed\n";
            var rec = new MatchRecord
            {
                WhenUtc = DateTime.UtcNow, MyDeck = "The tang", Opponent = "Gallata13",
                Won = true, Log = log, DeckBox = "deckbox_metal_01",
            };

            var line = rec.ToJson();
            Check("record stays on one line", !line.Contains("\n") && !line.Contains("\r"),
                  "len " + line.Length);

            var back = MatchRecord.FromJson(line);
            Check("battle log survives byte for byte", back != null && back.Log == log,
                  back == null ? "null" : "got " + back.Log.Length + " of " + log.Length + " chars");
            Check("deck box art id survives", back != null && back.DeckBox == "deckbox_metal_01");
            Check("quotes and backslashes survive", back != null && back.Log.Contains("\"Ultra Ball\"")
                                                  && back.Log.Contains("indented\\slashed"));
        }

        // ---------------------------------------------------------------
        private static void ProbabilityMaths()
        {
            Console.WriteLine("-- hypergeometric core --");
            // Known Pokemon TCG reference values for a fresh 60-card deck with 6 prizes.
            CheckNear("1-of prized", Tracker.ProbAtLeastOne(60, 6, 1), 0.100000);
            CheckNear("2-of prized", Tracker.ProbAtLeastOne(60, 6, 2), 0.191525);
            CheckNear("3-of prized", Tracker.ProbAtLeastOne(60, 6, 3), 0.275161);
            CheckNear("4-of prized", Tracker.ProbAtLeastOne(60, 6, 4), 0.351460);
            CheckNear("degenerate: no copies", Tracker.ProbAtLeastOne(60, 6, 0), 0.0);
            CheckNear("degenerate: no prize slots", Tracker.ProbAtLeastOne(60, 0, 4), 0.0);
            CheckNear("all slots are prizes", Tracker.ProbAtLeastOne(6, 6, 2), 1.0);
            CheckNear("more copies than non-prize room", Tracker.ProbAtLeastOne(10, 6, 5), 1.0);
            Console.WriteLine();
        }

        private static void FreshGame()
        {
            Console.WriteLine("-- fresh game: 7 in hand, 47 deck, 6 prizes --");
            var t = new Tracker();
            // 4 Ultra Ball, 4 Rare Candy, 52 filler
            t.SetDeckByName(Deck60("Ultra Ball", 4, "Rare Candy", 4, "Filler", 52), "test");

            var hand = new List<CardRef>();
            for (int i = 0; i < 7; i++) hand.Add(Known("Filler"));
            var snap = Make(hand, deckHidden: 47, prizeHidden: 6);

            t.Update(snap);

            Check("60 cards accounted", t.TotalUnknown == 53, "hidden=" + t.TotalUnknown);
            Check("status is not an error", !t.Status.Contains("does not match"), t.Status);
            var ub = Row(t, "Ultra Ball");
            Check("Ultra Ball row exists", ub != null);
            if (ub != null)
            {
                Check("4 copies unaccounted", ub.Unaccounted == 4, "got " + ub.Unaccounted);
                Check("none provably prized", ub.GuaranteedPrized == 0);
                // 4 copies among 53 hidden slots, 6 of which are prizes.
                CheckNear("Ultra Ball prize odds", ub.ProbAnyPrized, 1 - Hyper(53, 6, 4));
                CheckNear("expected prized", ub.ExpectedPrized, 4.0 * 6 / 53);
            }
            Console.WriteLine();
        }

        private static void AfterSeeingCopies()
        {
            Console.WriteLine("-- odds sharpen as copies are seen --");
            var t = new Tracker();
            t.SetDeckByName(Deck60("Ultra Ball", 4, "Filler", 56), "test");

            // 3 of the 4 Ultra Ball already in the discard; 1 unaccounted.
            var discard = new List<CardRef> { Known("Ultra Ball"), Known("Ultra Ball"), Known("Ultra Ball") };
            var hand = new List<CardRef>();
            for (int i = 0; i < 7; i++) hand.Add(Known("Filler"));
            var snap = Make(hand, deckHidden: 44, prizeHidden: 6, discard: discard);

            t.Update(snap);
            var ub = Row(t, "Ultra Ball");
            Check("1 copy left unaccounted", ub != null && ub.Unaccounted == 1);
            CheckNear("single copy odds = prizes/hidden", ub.ProbAnyPrized, 6.0 / 50);
            Console.WriteLine();
        }

        private static void ProvablyPrized()
        {
            Console.WriteLine("-- provably prized (deck smaller than unaccounted copies) --");
            var t = new Tracker();
            t.SetDeckByName(Deck60("Ultra Ball", 4, "Filler", 56), "test");

            // Deck is down to 2 hidden cards, 6 hidden prizes, and all 4 Ultra Ball unaccounted:
            // at most 2 can be in the deck, so at least 2 MUST be prized.
            var seen = new List<CardRef>();
            for (int i = 0; i < 52; i++) seen.Add(Known("Filler"));   // 52 of 56 filler seen
            var snap = Make(new List<CardRef>(), deckHidden: 2, prizeHidden: 6, discard: seen);

            t.Update(snap);
            var ub = Row(t, "Ultra Ball");
            Check("status ok", !t.Status.Contains("does not match"), t.Status);
            Check("2 provably prized", ub != null && ub.GuaranteedPrized == 2,
                  ub == null ? "no row" : "got " + ub.GuaranteedPrized);
            CheckNear("odds are certainty", ub.ProbAnyPrized, 1.0);
            // Note both Filler and Ultra Ball are provably 2-prized here (only 2 deck slots exist
            // for 4 copies of each), so the real invariant is ordering, not which name leads.
            int firstUncertain = t.Prizes.FindIndex(r => r.GuaranteedPrized == 0);
            int lastCertain = t.Prizes.FindLastIndex(r => r.GuaranteedPrized > 0);
            Check("certain rows sort above uncertain ones",
                  firstUncertain < 0 || lastCertain < firstUncertain,
                  "order=" + string.Join(",", t.Prizes.Select(r => r.Name + ":" + r.GuaranteedPrized).ToArray()));
            Console.WriteLine();
        }

        private static void DeckFullyRevealed()
        {
            Console.WriteLine("-- deck fully revealed => prizes exact --");
            var t = new Tracker();
            t.SetDeckByName(Deck60("Ultra Ball", 4, "Filler", 56), "test");

            // Every deck card visible (mid-search), 6 prizes still face down.
            var deckRevealed = new List<CardRef>();
            for (int i = 0; i < 46; i++) deckRevealed.Add(Known("Filler"));
            deckRevealed.Add(Known("Ultra Ball"));
            deckRevealed.Add(Known("Ultra Ball"));
            var discard = new List<CardRef>();
            for (int i = 0; i < 6; i++) discard.Add(Known("Filler"));

            var snap = Make(new List<CardRef>(), deckHidden: 0, prizeHidden: 6,
                            discard: discard, deckRevealed: deckRevealed);

            t.Update(snap);
            Check("status says exact", t.Status.Contains("exact"), t.Status);
            var ub = Row(t, "Ultra Ball");
            Check("remaining 2 Ultra Ball are prized", ub != null && ub.GuaranteedPrized == 2,
                  ub == null ? "no row" : "got " + ub.GuaranteedPrized);
            var confirmed = t.DeckRows.Where(r => r.Confirmed).ToList();
            Check("confirmed deck rows present", confirmed.Count == 2, "rows=" + confirmed.Count);
            Console.WriteLine();
        }

        private static void RevealedPrizes()
        {
            Console.WriteLine("-- revealed prize cards are shown as facts --");
            var t = new Tracker();
            t.SetDeckByName(Deck60("Ultra Ball", 4, "Filler", 56), "test");

            var hand = new List<CardRef>();
            for (int i = 0; i < 7; i++) hand.Add(Known("Filler"));
            var prizeRevealed = new List<CardRef> { Known("Ultra Ball") };
            var snap = Make(hand, deckHidden: 47, prizeHidden: 5, prizeRevealed: prizeRevealed);

            t.Update(snap);
            var ub = Row(t, "Ultra Ball");
            Check("revealed prize listed at 100%", ub != null && ub.ProbAnyPrized >= 1.0);
            Check("revealed prize marked certain", ub != null && ub.GuaranteedPrized >= 1);

            // And with ALL prizes revealed, the tab must not fall back to listing the whole deck.
            var t2 = new Tracker();
            t2.SetDeckByName(Deck60("Ultra Ball", 4, "Filler", 56), "test");
            var allPrizes = new List<CardRef>();
            for (int i = 0; i < 6; i++) allPrizes.Add(Known("Filler"));
            var snap2 = Make(new List<CardRef>(), deckHidden: 54, prizeHidden: 0, prizeRevealed: allPrizes);
            t2.Update(snap2);
            Check("no deck cards leak into the prize list",
                  t2.Prizes.All(r => r.GuaranteedPrized > 0),
                  "rows=" + string.Join(",", t2.Prizes.Select(r => r.Name + ":" + r.GuaranteedPrized).ToArray()));
            Console.WriteLine();
        }

        private static void MismatchedDecklist()
        {
            Console.WriteLine("-- wrong decklist is reported, not silently wrong --");
            var t = new Tracker();
            t.SetDeckByName(Deck60("Filler", 60), "test");
            // Board says 53 hidden but hand holds cards that are not in the list at all.
            var hand = new List<CardRef>();
            for (int i = 0; i < 7; i++) hand.Add(Known("Some Other Card"));
            var snap = Make(hand, deckHidden: 47, prizeHidden: 6);

            t.Update(snap);
            Check("mismatch surfaced in status", t.Status.Contains("does not match"), t.Status);
            Check("no misleading prize rows", t.Prizes.Count == 0, "rows=" + t.Prizes.Count);
            Console.WriteLine();
        }

        private static void OpponentTracking()
        {
            Console.WriteLine("-- opponent reveals --");
            var t = new Tracker();
            t.SetDeckByName(Deck60("Filler", 60), "test");

            var opp = new SideSnapshot();
            opp.Active = Known("Dragapult ex");
            opp.Bench = new List<CardRef> { Known("Dreepy"), Known("Dreepy") };
            opp.Discard = new List<CardRef> { Known("Ultra Ball"), Known("Ultra Ball") };
            opp.Hand = HiddenMany(4);
            opp.Deck = HiddenMany(40);
            opp.Prize = HiddenMany(6);
            opp.All = new List<CardRef>();
            opp.All.Add(opp.Active);
            opp.All.AddRange(opp.Bench);
            opp.All.AddRange(opp.Discard);
            opp.All.AddRange(opp.Hand);
            opp.All.AddRange(opp.Deck);
            opp.All.AddRange(opp.Prize);

            var hand = new List<CardRef>();
            for (int i = 0; i < 7; i++) hand.Add(Known("Filler"));
            var snap = Make(hand, deckHidden: 47, prizeHidden: 6, opp: opp);

            t.Update(snap);
            Check("opp prize count", t.OppPrizes == 6, "got " + t.OppPrizes);
            Check("opp hand count", t.OppHand == 4, "got " + t.OppHand);
            Check("board groups benched duplicates",
                  t.OppBoard.Any(r => r.Name == "Dreepy" && r.Count == 2));
            Check("discard grouped", t.OppDiscard.Any(r => r.Name == "Ultra Ball" && r.Count == 2));
            Check("seen-this-match includes board and discard",
                  t.OppSeen.Any(r => r.Name == "Dragapult ex") &&
                  t.OppSeen.Any(r => r.Name == "Ultra Ball" && r.Count == 2));
            Check("hidden opp cards are not counted as seen",
                  t.OppSeen.Sum(r => r.Count) == 5,
                  "seen=" + t.OppSeen.Sum(r => r.Count));
            Console.WriteLine();
        }

        private static void OpponentNoDoubleCountOnZoneMove()
        {
            Console.WriteLine("-- a card moving zones is counted once --");
            var t = new Tracker();
            t.SetDeckByName(Deck60("Filler", 60), "test");

            var boss = new CardRef { EntityId = "e-boss-1", SourceId = "bossid", Name = "Boss's Orders" };

            // tick 1: it is in their (revealed) hand
            var opp1 = new SideSnapshot();
            opp1.Hand = new List<CardRef> { boss };
            opp1.All = new List<CardRef> { boss };
            var myHand = new List<CardRef>();
            for (int i = 0; i < 7; i++) myHand.Add(Known("Filler"));
            t.Update(Make(myHand, 47, 6, opp: opp1));

            // tick 2: same physical card, now in the discard
            var opp2 = new SideSnapshot();
            opp2.Discard = new List<CardRef> { boss };
            opp2.All = new List<CardRef> { boss };
            t.Update(Make(myHand, 47, 6, opp: opp2));

            var row = t.OppSeen.FirstOrDefault(r => r.Name == "Boss's Orders");
            Check("still counted exactly once", row != null && row.Count == 1,
                  row == null ? "missing" : "got " + row.Count);

            // A genuinely different second copy must count separately.
            var boss2 = new CardRef { EntityId = "e-boss-2", SourceId = "bossid", Name = "Boss's Orders" };
            var opp3 = new SideSnapshot();
            opp3.Discard = new List<CardRef> { boss, boss2 };
            opp3.All = new List<CardRef> { boss, boss2 };
            t.Update(Make(myHand, 47, 6, opp: opp3));
            row = t.OppSeen.FirstOrDefault(r => r.Name == "Boss's Orders");
            Check("a real second copy does count", row != null && row.Count == 2,
                  row == null ? "missing" : "got " + row.Count);

            // ResetMatch must wipe it.
            t.ResetMatch("next-match");
            Check("reset clears opponent memory", t.OppSeen.Count == 0);
            Console.WriteLine();
        }

        private static void UnnamedCardStillCounts()
        {
            Console.WriteLine("-- a visible card with no cached name still counts as known --");
            var c = new CardRef { EntityId = "e1", SourceId = "sv9_123", Name = null };
            Check("Known is true without a name", c.Known);
            Check("Key falls back to the source id", c.Key == "sv9_123", "key=" + c.Key);

            var hidden = new CardRef { EntityId = "PRIVATE", SourceId = null, Name = null };
            Check("a redacted card is not known", !hidden.Known);
            Console.WriteLine();
        }

        /// <summary>
        /// THE regression test for prize tracking.
        ///
        /// Searching your deck reveals every card in it, which solves the prizes exactly. The
        /// search then closes and the deck goes face down again. A tracker that recomputes from
        /// the current board loses the answer at that moment and falls back to probabilities --
        /// which is exactly the knowledge a real player keeps for the rest of the game.
        /// </summary>
        private static void PrizeKnowledgeSurvivesSearchClosing()
        {
            Console.WriteLine("-- prize knowledge survives the search closing --");
            var t = new Tracker();
            t.SetDeckByName(Deck60("Ultra Ball", 4, "Filler", 56), "test");

            var hand = new List<CardRef> { Known("Ultra Ball") };
            var discard = new List<CardRef>();
            for (int i = 0; i < 6; i++) discard.Add(Known("Filler"));

            // mid-search: the whole deck is face up
            var deckSeen = new List<CardRef> { Known("Ultra Ball"), Known("Ultra Ball") };
            for (int i = 0; i < 45; i++) deckSeen.Add(Known("Filler"));
            t.Update(Make(hand, deckHidden: 0, prizeHidden: 6, discard: discard, deckRevealed: deckSeen));

            Check("prizes solved during the search", t.PrizesSolved, t.Status);
            var ub = Row(t, "Ultra Ball");
            Check("1 Ultra Ball prized", ub != null && ub.GuaranteedPrized == 1,
                  ub == null ? "no row" : "got " + ub.GuaranteedPrized);

            // search closes: the deck is face down again, nothing else changed
            t.Update(Make(hand, deckHidden: 47, prizeHidden: 6, discard: discard));

            Check("still solved after the deck re-hides", t.PrizesSolved, t.Status);
            ub = Row(t, "Ultra Ball");
            Check("Ultra Ball STILL exactly prized", ub != null && ub.GuaranteedPrized == 1,
                  ub == null ? "LOST IT" : "got " + ub.GuaranteedPrized);
            Check("no probabilities left to show", t.Prizes.All(r => r.GuaranteedPrized > 0));
            Check("6 prize cards listed", t.Prizes.Sum(r => r.Unaccounted) == 6,
                  "got " + t.Prizes.Sum(r => r.Unaccounted));

            // deck = decklist - prizes - visible, exactly
            var deckUb = t.DeckRows.FirstOrDefault(r => r.Name == "Ultra Ball");
            Check("deck contents exact: 2 Ultra Ball", deckUb != null && deckUb.Count == 2,
                  deckUb == null ? "missing" : "got " + deckUb.Count);
            Check("all deck rows are confirmed", t.DeckRows.All(r => r.Confirmed));
            Check("deck totals 47", t.DeckRows.Sum(r => r.Count) == 47,
                  "got " + t.DeckRows.Sum(r => r.Count));
            Console.WriteLine();
        }

        private static void TakenPrizeIsRemoved()
        {
            Console.WriteLine("-- taking a prize removes that card from the list --");
            var t = new Tracker();
            t.SetDeckByName(Deck60("Ultra Ball", 4, "Filler", 56), "test");

            var hand = new List<CardRef> { Known("Ultra Ball") };
            var discard = new List<CardRef>();
            for (int i = 0; i < 6; i++) discard.Add(Known("Filler"));
            var deckSeen = new List<CardRef> { Known("Ultra Ball"), Known("Ultra Ball") };
            for (int i = 0; i < 45; i++) deckSeen.Add(Known("Filler"));
            t.Update(Make(hand, 0, 6, discard: discard, deckRevealed: deckSeen));
            Check("solved: 6 prizes", t.Prizes.Sum(r => r.Unaccounted) == 6);

            // A prize is taken and lands in hand - here it is the Ultra Ball.
            var hand2 = new List<CardRef> { Known("Ultra Ball"), Known("Ultra Ball") };
            t.Update(Make(hand2, deckHidden: 47, prizeHidden: 5, discard: discard));

            Check("5 prize cards left", t.Prizes.Sum(r => r.Unaccounted) == 5,
                  "got " + t.Prizes.Sum(r => r.Unaccounted));
            Check("the taken Ultra Ball is gone from prizes",
                  t.Prizes.All(r => r.Name != "Ultra Ball"),
                  string.Join(",", t.Prizes.Select(r => r.Name + ":" + r.Unaccounted).ToArray()));
            Console.WriteLine();
        }

        /// <summary>
        /// The prize display must always be exactly the prize cards - six of them, then five as
        /// they are taken - never a list of every card that might be prized.
        /// </summary>
        private static void PrizeSlotsAlwaysMatchPrizesLeft()
        {
            Console.WriteLine("-- prize slots always equal prizes remaining --");

            // fresh game: nothing known, so six face-down slots
            // A realistic spread. Note a single 56-of would NOT work here, and correctly so: with
            // only 47 deck slots, 49 unaccounted copies proves 2 of them are prized by pigeonhole.
            var t = new Tracker();
            t.SetDeckByName(Deck60("Ultra Ball", 4, "Iono", 4, "Filler A", 30, "Filler B", 22), "test");
            var hand = new List<CardRef>();
            for (int i = 0; i < 7; i++) hand.Add(Known("Filler A"));
            t.Update(Make(hand, deckHidden: 47, prizeHidden: 6));
            Check("6 slots at the start", t.PrizeSlots.Count == 6, "got " + t.PrizeSlots.Count);
            Check("none are filled by a guess", t.PrizeSlotsKnown == 0, "got " + t.PrizeSlotsKnown);
            Check("candidates live in the separate 'likely' list", t.Likely.Count > 0);

            // solved, then a prize taken: five slots, all known
            var t2 = new Tracker();
            t2.SetDeckByName(Deck60("Ultra Ball", 4, "Filler", 56), "test");
            var h = new List<CardRef> { Known("Ultra Ball") };
            var discard = new List<CardRef>();
            for (int i = 0; i < 6; i++) discard.Add(Known("Filler"));
            var deckSeen = new List<CardRef> { Known("Ultra Ball"), Known("Ultra Ball") };
            for (int i = 0; i < 45; i++) deckSeen.Add(Known("Filler"));
            t2.Update(Make(h, 0, 6, discard: discard, deckRevealed: deckSeen));
            Check("6 slots, all known once solved",
                  t2.PrizeSlots.Count == 6 && t2.PrizeSlotsKnown == 6,
                  t2.PrizeSlots.Count + " slots / " + t2.PrizeSlotsKnown + " known");
            Check("no candidate list once solved", t2.Likely.Count == 0);

            var h2 = new List<CardRef> { Known("Ultra Ball"), Known("Ultra Ball") };
            t2.Update(Make(h2, deckHidden: 47, prizeHidden: 5, discard: discard));
            Check("5 slots after taking one",
                  t2.PrizeSlots.Count == 5 && t2.PrizeSlotsKnown == 5,
                  t2.PrizeSlots.Count + " slots / " + t2.PrizeSlotsKnown + " known");
            Console.WriteLine();
        }

        /// <summary>
        /// The match log is hand-rolled JSON, so round-tripping it is worth a test - especially
        /// names with quotes and apostrophes, which is exactly where a naive serializer breaks.
        /// </summary>
        private static void MatchHistoryRoundTrips()
        {
            Console.WriteLine("-- match history round-trip and win rates --");

            var rec = new MatchRecord
            {
                WhenUtc = new DateTime(2026, 9, 21, 14, 30, 0, DateTimeKind.Utc),
                DurationSeconds = 742,
                MyDeck = "Dragapult \"test\" deck",
                Opponent = "O'Brien",
                OppArchetype = "N's Zoroark ex / N's Zorua",
                Won = true,
                MyPrizesLeft = 0,
                OppPrizesLeft = 3,
                Turns = 14,
            };
            var back = MatchRecord.FromJson(rec.ToJson());
            Check("deck name with quotes survives", back.MyDeck == rec.MyDeck, back.MyDeck);
            Check("opponent with apostrophe survives", back.Opponent == rec.Opponent, back.Opponent);
            Check("archetype survives", back.OppArchetype == rec.OppArchetype, back.OppArchetype);
            Check("result survives", back.Won && back.Turns == 14 && back.OppPrizesLeft == 3);
            Check("timestamp survives", back.WhenUtc == rec.WhenUtc, back.WhenUtc.ToString("o"));

            var h = new MatchHistory(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "pt_test_" + Guid.NewGuid().ToString("N") + ".jsonl"));
            h.Add(Rec("A", "X", true));
            h.Add(Rec("A", "X", false));
            h.Add(Rec("A", "Y", true));
            h.Add(Rec("B", "X", true));

            var all = h.Overall();
            Check("overall 3-1", all.Wins == 3 && all.Losses == 1, all.Wins + "-" + all.Losses);
            CheckNear("overall rate", all.Rate, 0.75);

            var deckA = h.ByDeck().FirstOrDefault(w => w.Label == "A");
            Check("deck A is 2-1", deckA != null && deckA.Wins == 2 && deckA.Losses == 1);
            // Deck records follow the deck's stable id, so a rename keeps the history.
            var idH = new MatchHistory(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "pt_id_" + Guid.NewGuid().ToString("N") + ".jsonl"));
            idH.Add(new MatchRecord { MyDeck = "Old Name", DeckId = "deck-1", Won = true });
            idH.Add(new MatchRecord { MyDeck = "Old Name", DeckId = "deck-1", Won = false });
            var renamed = idH.ForDeck("deck-1", "Brand New Name");
            Check("record survives a rename", renamed.Wins == 1 && renamed.Losses == 1,
                  renamed.Wins + "-" + renamed.Losses);
            Check("a different deck id does not match", idH.ForDeck("deck-2", "Old Name").Played == 0,
                  "got " + idH.ForDeck("deck-2", "Old Name").Played);
            // Records written before ids existed still match by name.
            idH.Add(new MatchRecord { MyDeck = "Legacy", DeckId = "", Won = true });
            Check("legacy records still match by name", idH.ForDeck("whatever", "Legacy").Played == 1,
                  "got " + idH.ForDeck("whatever", "Legacy").Played);
            try { System.IO.File.Delete(idH.Path); } catch { }

            var forA = h.ForDeck("A");
            Check("ForDeck A is 2-1", forA.Wins == 2 && forA.Losses == 1, forA.Wins + "-" + forA.Losses);
            Check("ForDeck is case-insensitive", h.ForDeck("a").Played == 3, "got " + h.ForDeck("a").Played);
            Check("ForDeck for an unplayed deck is empty", h.ForDeck("Never Played").Played == 0);

            var vsX = h.ByMatchup().FirstOrDefault(w => w.Label == "X");
            Check("vs X is 2-1", vsX != null && vsX.Wins == 2 && vsX.Losses == 1);
            // last two records are both wins, so the streak is +2
            Check("streak counts consecutive wins", h.Streak() == "+2", h.Streak());
            Check("recent is newest first", h.Recent(2)[0].MyDeck == "B");

            // reload from disk
            var h2 = new MatchHistory(h.Path);
            h2.Load();
            Check("reloads from disk", h2.Count == 4, "got " + h2.Count);
            try { System.IO.File.Delete(h.Path); } catch { }
            Console.WriteLine();
        }

        private static MatchRecord Rec(string deck, string opp, bool won)
        {
            return new MatchRecord { WhenUtc = DateTime.UtcNow, MyDeck = deck, OppArchetype = opp, Won = won };
        }

        /// <summary>
        /// Cards put on the bottom of the deck, counted down as you draw.
        ///
        /// Deck order is confirmed from the engine: moving to the bottom is p1Deck.Add and moving
        /// to the top is p1Deck.Insert(0, ...), so index 0 is the top and the last index is the
        /// bottom.
        /// </summary>
        private static void BottomStackCountsDown()
        {
            Console.WriteLine("-- bottom-of-deck stack --");
            var b = new BottomStack();

            var g1 = new[] { Known("Beldum"), Known("Metang"), Known("Metagross ex") };
            b.Update(DeckSide(17, g1));
            Check("picks up all 3", b.Cards.Count == 3, "got " + b.Cards.Count);
            Check("nearest is 17 draws away", b.Cards[0].DrawsAway == 17, "got " + b.Cards[0].DrawsAway);
            Check("nearest listed first", b.Cards[0].Name == "Beldum", b.Cards[0].Name);
            Check("deepest is 19 away", b.Cards[2].DrawsAway == 19, "got " + b.Cards[2].DrawsAway);

            // The reveal ends: same cards, now face down. Must keep counting them down.
            b.Update(DeckSide(2, null, hiddenTail: 3));
            Check("still tracked after the reveal ends", b.Cards.Count == 3, "got " + b.Cards.Count);
            Check("now 2 draws away", b.Cards[0].DrawsAway == 2, "got " + b.Cards[0].DrawsAway);
            Console.WriteLine();
        }

        /// <summary>
        /// A second Metang puts its four BELOW the first group. Groups must stack, not replace -
        /// the first version of this cleared the earlier group and lost it.
        /// </summary>
        private static void BottomStackHandlesMultipleUses()
        {
            Console.WriteLine("-- multiple Metang uses stack --");
            var b = new BottomStack();

            // Use 1: three cards land on the bottom of a 20-card deck.
            var g1 = new[] { Known("Beldum"), Known("Metang"), Known("Metagross ex") };
            b.Update(DeckSide(17, g1));
            Check("group 1 tracked", b.Cards.Count == 3);

            // Use 2: the deck is now 20 still (cards came off the top), and a NEW trio sits below
            // the first, with group 1 face down again above them.
            var g2 = new[] { Known("Bravery Charm"), Known("Earthen Vessel") };
            b.Update(DeckSideMixed(15, 3, g2));
            Check("both groups tracked", b.Cards.Count == 5, "got " + b.Cards.Count);
            Check("two groups", b.GroupCount == 2, "got " + b.GroupCount);
            Check("group 1 still nearest", b.Cards[0].Name == "Beldum", b.Cards[0].Name);
            Check("group 1 at 15", b.Cards[0].DrawsAway == 15, "got " + b.Cards[0].DrawsAway);
            Check("group 2 is deeper", b.Cards[3].Name == "Bravery Charm" && b.Cards[3].DrawsAway == 18,
                  b.Cards[3].Name + " at " + b.Cards[3].DrawsAway);
            Check("deepest is 19", b.Cards[4].DrawsAway == 19, "got " + b.Cards[4].DrawsAway);

            // Re-seeing the same visible run must not duplicate it.
            b.Update(DeckSideMixed(15, 3, g2));
            Check("no double counting on re-read", b.Cards.Count == 5, "got " + b.Cards.Count);

            // Draw 16: the first group's nearest card has been drawn and leaves the list.
            b.Update(DeckSide(0, null, hiddenTail: 4));
            Check("drawn cards leave the list", b.Cards.Count == 4, "got " + b.Cards.Count);
            Check("next up is Metang", b.Cards[0].Name == "Metang" && b.Cards[0].DrawsAway == 0,
                  b.Cards[0].Name + " at " + b.Cards[0].DrawsAway);
            Console.WriteLine();
        }

        /// <summary>
        /// Metal Maker shuffles the group before placing it, so if we cannot read the real order
        /// off the deck we must report an arrival WINDOW rather than invent a sequence.
        /// </summary>
        private static void BottomStackUnknownOrderAndShuffle()
        {
            Console.WriteLine("-- unknown order, shuffle reset, full reveal --");

            // Cards seen in HiddenPending that then vanish went to the bottom.
            var b = new BottomStack();
            var staged = new List<CardRef> { Known("Beldum"), Known("Bravery Charm") };
            var s1 = DeckSide(20, null);
            s1.Pending = staged;
            b.Update(s1);
            Check("staging alone does not track yet", !b.Active);

            var s2 = DeckSide(20, null);
            s2.Pending = new List<CardRef>();
            b.Update(s2);
            Check("tracked once they leave staging", b.Cards.Count == 2, "got " + b.Cards.Count);
            Check("order marked unknown", b.Cards.All(c => !c.OrderKnown));
            Check("arrival window is 18-19",
                  b.Cards[0].GroupFirst == 18 && b.Cards[0].GroupLast == 19,
                  b.Cards[0].GroupFirst + "-" + b.Cards[0].GroupLast);

            // A shuffle: the deck grows.
            b.Update(DeckSide(40, null));
            Check("cleared by a shuffle", !b.Active, "still " + b.Cards.Count);

            // A deck SEARCH reveals everything and must not read as a giant bottom stack.
            var all = new BottomStack();
            all.Update(DeckSide(0, new[] { Known("A"), Known("B"), Known("C"), Known("D") }));
            Check("a full reveal is not a bottom stack", !all.Active, "got " + all.Cards.Count);
            Console.WriteLine();
        }

        /// <summary>Deck with hidden cards on top and optionally revealed ones at the bottom.</summary>
        private static SideSnapshot DeckSide(int hiddenOnTop, CardRef[] revealedBottom, int hiddenTail = 0)
        {
            var s = new SideSnapshot();
            s.Deck = new List<CardRef>();
            for (int i = 0; i < hiddenOnTop; i++) s.Deck.Add(Hidden());
            if (revealedBottom != null) s.Deck.AddRange(revealedBottom);
            for (int i = 0; i < hiddenTail; i++) s.Deck.Add(Hidden());
            s.All = new List<CardRef>(s.Deck);
            return s;
        }

        /// <summary>Hidden top, then a hidden block (an earlier group gone face down), then a revealed run.</summary>
        private static SideSnapshot DeckSideMixed(int hiddenOnTop, int hiddenMiddle, CardRef[] revealedBottom)
        {
            var s = new SideSnapshot();
            s.Deck = new List<CardRef>();
            for (int i = 0; i < hiddenOnTop; i++) s.Deck.Add(Hidden());
            for (int i = 0; i < hiddenMiddle; i++) s.Deck.Add(Hidden());
            s.Deck.AddRange(revealedBottom);
            s.All = new List<CardRef>(s.Deck);
            return s;
        }

        /// <summary>C(total-k, prize)/C(total, prize) - the "none prized" reference.</summary>
        private static double Hyper(int total, int prize, int k)
        {
            double p = 1.0;
            for (int i = 0; i < prize; i++) p *= (double)(total - k - i) / (total - i);
            return p;
        }
    }
}
