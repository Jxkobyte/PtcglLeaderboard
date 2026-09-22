using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using PrizeTracker.Core;

namespace Preview
{
    /// <summary>
    /// Offline previewer for the overlay.
    ///
    /// PTCGL takes about a minute to launch, so iterating on the overlay inside the game is
    /// painfully slow. This drives the REAL <see cref="Tracker"/> with hand-built match states and
    /// renders the exact rows it produces to an HTML page, so layout and content can be iterated in
    /// seconds.
    ///
    /// What this does and does not prove:
    ///   - the DATA is real: same Tracker, same accounting, same ordering, same probabilities
    ///   - the PIXELS are a mock: the game draws with Unity IMGUI, this draws with HTML/CSS
    /// So it is a design and content tool, not a pixel-accurate simulation. The styling here is
    /// deliberately limited to things IMGUI can also do (solid fills, rectangles, plain text) so a
    /// design that looks right here is portable to the overlay.
    ///
    /// Card art comes from TCGdex URLs here because Unity asset bundles cannot be read outside the
    /// game; in game the overlay loads art from the client's own bundles instead.
    /// </summary>
    internal static class Program
    {
        // ---- fixture ---------------------------------------------------
        // A realistic Dragapult-shaped list. Ids are TCGdex "set/number" pairs verified to resolve,
        // so the preview shows genuine card art rather than placeholders.
        private class Card
        {
            public string Name;
            public string Id;
            public int Count;
            public Card(string name, string id, int count) { Name = name; Id = id; Count = count; }
        }

        private static readonly List<Card> Deck = new List<Card>
        {
            new Card("Dreepy",                "sv06/128", 4),
            new Card("Drakloak",              "sv06/129", 4),
            new Card("Dragapult ex",          "sv06/130", 3),
            new Card("Dudunsparce",           "sv05/129", 2),
            new Card("Dunsparce",             "sv09/120", 2),
            new Card("Unfair Stamp",          "sv06/165", 2),
            new Card("Buddy-Buddy Poffin",    "sv05/144", 4),
            new Card("Crispin",               "sv07/133", 2),
            new Card("Ultra Ball",            "sv03/196", 4),
            new Card("Boss's Orders",         "sv06/182", 3),
            new Card("Night Stretcher",       "sv06/157", 3),
            new Card("Iono",                  "sv05/162", 4),
            new Card("Professor's Research",  "sv07/154", 4),
            new Card("Rare Candy",            "sv08/240", 4),
            new Card("Earthen Vessel",        "sv09/161", 3),
            new Card("Basic Psychic Energy",  "sv06/182", 12),
        };

        private static Dictionary<string, int> DeckCounts()
        {
            var d = new Dictionary<string, int>();
            foreach (var c in Deck) d[c.Name] = d.ContainsKey(c.Name) ? d[c.Name] + c.Count : c.Count;
            return d;
        }

        private static Dictionary<string, string> DeckIds()
        {
            var d = new Dictionary<string, string>();
            foreach (var c in Deck) if (!d.ContainsKey(c.Name)) d[c.Name] = c.Id;
            return d;
        }

        private static int DeckSize() { return Deck.Sum(c => c.Count); }

        // ---- snapshot helpers ------------------------------------------
        private static CardRef Known(string name)
        {
            var id = DeckIds().ContainsKey(name) ? DeckIds()[name] : name;
            return new CardRef { EntityId = Guid.NewGuid().ToString(), SourceId = id, Name = name };
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
        /// Builds a board from what is VISIBLE plus how many prizes remain, deriving the hidden
        /// deck count so the 60-card invariant holds by construction. Getting this wrong by hand is
        /// exactly what the tracker's mismatch guard is there to catch - and it caught it.
        /// </summary>
        private static BoardSnapshot Snap(List<CardRef> hand, List<CardRef> discard,
                                          int prizesLeft,
                                          List<CardRef> prizeRevealed = null,
                                          SideSnapshot opp = null,
                                          List<CardRef> deckRevealed = null)
        {
            hand = hand ?? new List<CardRef>();
            discard = discard ?? new List<CardRef>();
            prizeRevealed = prizeRevealed ?? new List<CardRef>();
            deckRevealed = deckRevealed ?? new List<CardRef>();
            int deckHidden = DeckSize() - hand.Count - discard.Count - prizesLeft - deckRevealed.Count;
            int prizeHidden = prizesLeft - prizeRevealed.Count;
            if (deckHidden < 0 || prizeHidden < 0)
                throw new InvalidOperationException("fixture does not add up to " + DeckSize());

            var me = new SideSnapshot();
            me.Hand = hand;
            me.Discard = discard;
            me.Deck = new List<CardRef>();
            me.Deck.AddRange(deckRevealed);
            me.Deck.AddRange(HiddenMany(deckHidden));
            me.Prize = new List<CardRef>();
            me.Prize.AddRange(prizeRevealed);
            me.Prize.AddRange(HiddenMany(prizeHidden));

            me.All = new List<CardRef>();
            me.All.AddRange(me.Hand);
            me.All.AddRange(me.Discard);
            me.All.AddRange(me.Deck);
            me.All.AddRange(me.Prize);

            return new BoardSnapshot { Valid = true, IAmPlayer1 = true, Me = me, Opp = opp ?? new SideSnapshot() };
        }

        private static SideSnapshot Opponent()
        {
            var o = new SideSnapshot();
            o.Active = Known("Dragapult ex");
            o.Bench = new List<CardRef> { Known("Dreepy"), Known("Dreepy"), Known("Drakloak") };
            o.Discard = new List<CardRef> { Known("Ultra Ball"), Known("Ultra Ball"), Known("Iono"), Known("Rare Candy") };
            o.Hand = HiddenMany(4);
            o.Deck = HiddenMany(38);
            o.Prize = HiddenMany(5);
            o.All = new List<CardRef>();
            o.All.Add(o.Active);
            o.All.AddRange(o.Bench);
            o.All.AddRange(o.Discard);
            o.All.AddRange(o.Hand);
            o.All.AddRange(o.Deck);
            o.All.AddRange(o.Prize);
            return o;
        }

        // ---- scenarios --------------------------------------------------
        private class Scenario
        {
            public string Title;
            public string Note;
            public Tracker Tracker;
        }

        private static Tracker NewTracker()
        {
            var t = new Tracker();
            t.SetDeck(new Dictionary<string, int>(), "unused");   // clears state
            t.SetDeckByName(DeckCounts(), "Dragapult ex", DeckIds());
            return t;
        }

        private static List<Scenario> BuildScenarios()
        {
            var list = new List<Scenario>();

            // 1. Opening hand.
            {
                var t = NewTracker();
                var hand = new List<CardRef> {
                    Known("Dreepy"), Known("Dreepy"), Known("Rare Candy"), Known("Iono"),
                    Known("Buddy-Buddy Poffin"), Known("Basic Psychic Energy"), Known("Ultra Ball") };
                t.Update(Snap(hand, new List<CardRef>(), 6, null, Opponent()));
                list.Add(new Scenario { Title = "Turn 1", Note = "Nothing seen yet - every unaccounted card carries its base prize odds.", Tracker = t });
            }

            // 2. Mid game: a chunk of the deck seen, odds have sharpened.
            {
                var t = NewTracker();
                var hand = new List<CardRef> { Known("Dragapult ex"), Known("Boss's Orders"), Known("Basic Psychic Energy") };
                var discard = new List<CardRef>();
                foreach (var n in new[] { "Ultra Ball", "Ultra Ball", "Ultra Ball", "Iono", "Iono",
                                          "Professor's Research", "Rare Candy", "Rare Candy",
                                          "Buddy-Buddy Poffin", "Buddy-Buddy Poffin", "Dreepy",
                                          "Drakloak", "Basic Psychic Energy", "Basic Psychic Energy",
                                          "Earthen Vessel", "Night Stretcher" })
                    discard.Add(Known(n));
                t.Update(Snap(hand, discard, 4,
                              new List<CardRef> { Known("Dunsparce"), Known("Crispin") }, Opponent()));
                list.Add(new Scenario { Title = "Mid game", Note = "Two prizes already flipped face up; the rest are odds over a smaller pool.", Tracker = t });
            }

            // 3. Late game: deck nearly gone, several cards provably prized.
            {
                var t = NewTracker();
                var hand = new List<CardRef> { Known("Dragapult ex") };
                var discard = new List<CardRef>();
                var counts = DeckCounts();
                // Put almost everything in the discard, leaving a few unaccounted.
                // 6 cards unaccounted for but only 1 deck slot left, so anything with 2+ copies
                // outstanding provably has the overflow sitting in the prizes.
                var leaveOut = new Dictionary<string, int> {
                    { "Rare Candy", 3 }, { "Boss's Orders", 2 }, { "Iono", 1 } };
                foreach (var kvp in counts)
                {
                    int hold = leaveOut.ContainsKey(kvp.Key) ? leaveOut[kvp.Key] : 0;
                    int inHand = kvp.Key == "Dragapult ex" ? 1 : 0;
                    for (int i = 0; i < kvp.Value - hold - inHand; i++) discard.Add(Known(kvp.Key));
                }
                t.Update(Snap(hand, discard, 5, null, Opponent()));
                list.Add(new Scenario { Title = "Late game", Note = "Only 1 card left in deck - anything that cannot fit there is provably prized.", Tracker = t });
            }

            // 4. After the first deck search: the deck was seen in full, so the prizes are
            //    SOLVED and stay solved once the search closes and the deck goes face down again.
            {
                var t = NewTracker();
                var hand = new List<CardRef> { Known("Dragapult ex"), Known("Boss's Orders") };
                var discard = new List<CardRef>();
                foreach (var n in new[] { "Ultra Ball", "Ultra Ball", "Iono", "Rare Candy",
                                          "Buddy-Buddy Poffin", "Dreepy", "Basic Psychic Energy",
                                          "Professor's Research" })
                    discard.Add(Known(n));

                // Mid-search: every remaining card of the deck is face up.
                var counts = DeckCounts();
                var seen = new Dictionary<string, int>();
                foreach (var c in hand) seen[c.Name] = seen.ContainsKey(c.Name) ? seen[c.Name] + 1 : 1;
                foreach (var c in discard) seen[c.Name] = seen.ContainsKey(c.Name) ? seen[c.Name] + 1 : 1;

                // Choose the 6 prizes, then everything else is in the deck and visible.
                var prized = new Dictionary<string, int> {
                    { "Dragapult ex", 1 }, { "Rare Candy", 2 }, { "Iono", 1 },
                    { "Boss's Orders", 1 }, { "Earthen Vessel", 1 } };
                var deckSeen = new List<CardRef>();
                foreach (var kvp in counts)
                {
                    int already = seen.ContainsKey(kvp.Key) ? seen[kvp.Key] : 0;
                    int inPrize = prized.ContainsKey(kvp.Key) ? prized[kvp.Key] : 0;
                    for (int i = 0; i < kvp.Value - already - inPrize; i++) deckSeen.Add(Known(kvp.Key));
                }
                t.Update(Snap(hand, discard, 6, null, Opponent(), deckRevealed: deckSeen));

                // Search closes: deck face down again, nothing else changed.
                t.Update(Snap(hand, discard, 6, null, Opponent()));

                list.Add(new Scenario {
                    Title = "After the first deck search",
                    Note = "The deck was seen in full, so prizes are solved exactly - and stay solved "
                         + "after the search closes and the deck goes face down again. Deck contents "
                         + "become exact too: decklist minus prizes minus everything visible.",
                    Tracker = t });
            }

            return list;
        }

        // ---- render -----------------------------------------------------
        private static string Art(string id)
        {
            if (string.IsNullOrEmpty(id) || !id.Contains("/")) return null;
            return "https://assets.tcgdex.net/en/sv/" + id + "/low.png";
        }

        private static string Esc(string s)
        {
            // Attributes here are single-quoted, so the apostrophe MUST be escaped too - otherwise
            // a card like "Boss's Orders" truncates its title and, worse, breaks the data-* art
            // fallback chain.
            return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                            .Replace("\"", "&quot;").Replace("'", "&#39;");
        }

        private static string Tile(string name, string id, string countBadge, string footer,
                                   string cls, double fill)
        {
            var art = Art(id);
            var sb = new StringBuilder();
            sb.Append("<div class='tile " + cls + "' title='" + Esc(name) + "'>");
            sb.Append("<div class='art'>");
            if (art != null) sb.Append("<img src='" + art + "' alt='" + Esc(name) + "' loading='lazy'>");
            else sb.Append("<div class='noart'>" + Esc(name) + "</div>");
            if (!string.IsNullOrEmpty(countBadge)) sb.Append("<div class='count'>" + Esc(countBadge) + "</div>");
            sb.Append("</div>");
            if (fill >= 0)
            {
                sb.Append("<div class='bar'><i style='width:" + (int)Math.Round(fill * 100) + "%'></i></div>");
            }
            sb.Append("<div class='foot'>" + Esc(footer) + "</div>");
            sb.Append("</div>");
            return sb.ToString();
        }

        private static string Pct(double p)
        {
            if (p <= 0) return "-";
            if (p >= 0.995) return "100%";
            return (int)Math.Round(p * 100) + "%";
        }

        private static string RenderPanel(Scenario sc, string tab)
        {
            var t = sc.Tracker;
            var sb = new StringBuilder();
            sb.Append("<div class='panel'>");
            sb.Append("<div class='title'>Prize Tracker</div>");

            sb.Append("<div class='tabs'>");
            foreach (var name in new[] { "PRIZES", "DECK", "OPP", "SET" })
                sb.Append("<span class='tab" + (name == tab ? " on" : "") + "'>" + name + "</span>");
            sb.Append("</div>");

            sb.Append("<div class='score'><b>You " + t.MyPrizes + "</b><b>Opp " + t.OppPrizes + "</b>" +
                      "<span>deck " + t.MyDeckCount + " &middot; hand " + t.MyHand + "</span></div>");
            sb.Append("<div class='status'>" + Esc(t.Status) + "</div>");

            if (tab == "PRIZES")
            {
                sb.Append(BoardMock.PrizeGrid(t));
            }
            else
            {
            sb.Append("<div class='grid'>");
            if (tab == "PRIZES")
            {
                foreach (var r in t.Prizes)
                {
                    bool certain = r.GuaranteedPrized > 0;
                    sb.Append(Tile(r.Name, r.SourceId,
                        r.Unaccounted > 1 ? "x" + r.Unaccounted : null,
                        certain ? r.GuaranteedPrized + " PRIZED" : Pct(r.ProbAnyPrized),
                        certain ? "certain" : "", certain ? -1 : r.ProbAnyPrized));
                }
            }
            else if (tab == "DECK")
            {
                foreach (var r in t.DeckRows)
                    sb.Append(Tile(r.Name, r.SourceId,
                        r.Count > 1 ? "x" + r.Count : null,
                        r.Confirmed ? "IN DECK" : Pct(r.ProbTopDeck),
                        r.Confirmed ? "certain" : "", -1));
            }
            else if (tab == "OPP")
            {
                foreach (var r in t.OppSeen)
                    sb.Append(Tile(r.Name, r.SourceId, r.Count > 1 ? "x" + r.Count : null, "", "", -1));
            }
            sb.Append("</div>");
            }

            sb.Append("<div class='foot-bar'>F1 hide &nbsp;|&nbsp; FPS cap: 60 (match)</div>");
            sb.Append("</div>");
            return sb.ToString();
        }

        private static void Main(string[] args)
        {
            int port = 8080;
            bool serve = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--serve" || args[i] == "-s") serve = true;
                else { int p; if (int.TryParse(args[i], out p)) port = p; }
            }

            Generate();
            if (serve) Server.Run(port, Generate);
        }

        /// <summary>Regenerates both pages. Called on startup and again on every served request.</summary>
        internal static void Generate()
        {
            var scenarios = BuildScenarios();
            var sb = new StringBuilder();
            sb.Append(@"<!doctype html><meta charset='utf-8'><title>Prize Tracker preview</title>
<style>
 body{background:#1b1d22;color:#e9edf3;font:13px/1.4 'Segoe UI',sans-serif;margin:0;padding:24px}
 h1{font-size:15px;font-weight:600;margin:0 0 4px;color:#9aa3b2}
 .note{color:#79808f;font-size:12px;margin:0 0 14px}
 .row{display:flex;gap:18px;align-items:flex-start;flex-wrap:wrap;margin-bottom:34px}
 .col h2{font-size:11px;letter-spacing:.08em;color:#79808f;margin:0 0 6px;font-weight:700}
 .panel{width:340px;background:#12141a;border:1px solid #2b2e36;padding:8px;box-sizing:border-box}
 .title{font-weight:700;font-size:12px;margin:0 0 6px}
 .tabs{display:flex;gap:2px;margin-bottom:6px}
 .tab{flex:1;text-align:center;font-size:10px;font-weight:700;padding:4px 0;background:#1c1f26;color:#79808f}
 .tab.on{background:#66b0ff;color:#0d0f13}
 .score{display:flex;gap:10px;align-items:center;margin:2px 0}
 .score b{color:#66b0ff;font-size:12px}
 .score span{margin-left:auto;color:#79808f;font-size:11px}
 .status{color:#79808f;font-size:10px;margin-bottom:6px;border-bottom:1px solid #2b2e36;padding-bottom:5px}
 .grid{display:grid;grid-template-columns:repeat(4,1fr);gap:5px}
 .tile{background:#20242c;padding:3px}
 .tile.certain{background:#1d3326;outline:1px solid #4fd47f}
 .art{position:relative;aspect-ratio:5/7;background:#0c0e12;overflow:hidden}
 .art img{width:100%;height:100%;object-fit:cover;display:block}
 .noart{font-size:9px;color:#79808f;padding:4px;text-align:center}
 .count{position:absolute;right:2px;top:2px;background:rgba(8,10,14,.85);color:#fff;font-size:10px;font-weight:700;padding:1px 4px}
 .bar{height:3px;background:#2b2e36;margin-top:3px}
 .bar i{display:block;height:3px;background:#66b0ff}
 .foot{font-size:10px;text-align:center;padding-top:2px;color:#c7cedb}
 .tile.certain .foot{color:#4fd47f;font-weight:700}
 .art.facedown{display:flex;align-items:center;justify-content:center;
   background:linear-gradient(145deg,#28425f,#16283c);border:4px solid #3a5f88;
   box-sizing:border-box;color:#9fc4e4;font-weight:700;font-size:18px}
 .hint{font-size:10px;color:#79808f;margin-top:5px}
 .seclbl{font-size:10px;font-weight:700;color:#79808f;margin:8px 0 3px;letter-spacing:.06em}
 .lrow{display:flex;align-items:center;gap:6px;background:#20242c;padding:2px 4px;margin-bottom:1px}
 .lrow span{font-size:10px;color:#c7cedb;flex:1;overflow:hidden;white-space:nowrap}
 .lrow .lbar{width:40px;height:4px;background:#2b2e36;display:block}
 .lrow .lbar b{display:block;height:4px;background:#66b0ff}
 .lrow em{font-size:10px;color:#c7cedb;font-style:normal;width:30px;text-align:right}
 .foot-bar{margin-top:7px;border-top:1px solid #2b2e36;padding-top:5px;color:#79808f;font-size:10px}
</style>
<h1>Prize Tracker &mdash; overlay preview</h1>
<p class='note'>Rendered from the real Tracker. Data, ordering and probabilities are exactly what the overlay shows in game; only the pixels are a mock.</p>
");
            foreach (var sc in scenarios)
            {
                sb.Append("<h1>" + Esc(sc.Title) + "</h1><p class='note'>" + Esc(sc.Note) + "</p><div class='row'>");
                foreach (var tab in new[] { "PRIZES", "DECK", "OPP" })
                    sb.Append("<div class='col'><h2>" + tab + "</h2>" + RenderPanel(sc, tab) + "</div>");
                sb.Append("</div>");
            }

            Fixture.RenderBoardMock();

            var outPath = Path.GetFullPath("preview.html");
            File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
            Console.WriteLine("wrote " + outPath);

            foreach (var sc in scenarios)
                Console.WriteLine(string.Format("  {0,-10} prizes={1,-2} rows={2,-3} status={3}",
                    sc.Title, sc.Tracker.MyPrizes, sc.Tracker.Prizes.Count, sc.Tracker.Status));
        }
    }
}
