using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using PrizeTracker.Core;

namespace Preview
{
    /// <summary>
    /// Loads fixture.json - a REAL mid-game state lifted from a PokeAI self-play replay, together
    /// with the real 60-card decklist and card ids resolved from the client's card database - and
    /// turns it into a <see cref="BoardSnapshot"/> for the real Tracker.
    ///
    /// The same snapshot drives both the board recreation and the overlay next to it, so what the
    /// panel claims can be checked directly against the board it describes rather than against a
    /// second, independently invented mockup.
    /// </summary>
    internal static class Fixture
    {
        public static void RenderBoardMock()
        {
            var path = Find();
            if (path == null) { Console.WriteLine("fixture.json not found - skipping board mock"); return; }

            var fx = JObject.Parse(File.ReadAllText(path));
            var tracker = new Tracker();

            var deck = new Dictionary<string, int>();
            foreach (var kv in (JObject)fx["decklist"]) deck[kv.Key] = (int)kv.Value;
            var ids = Ids(fx);
            tracker.SetDeckByName(deck, "dragapult", ids);
            tracker.Update(Snapshot(fx));

            var outPath = Path.GetFullPath("board.html");
            File.WriteAllText(outPath, BoardMock.Render(fx, tracker), Encoding.UTF8);
            Console.WriteLine("wrote " + outPath);
            Console.WriteLine(string.Format(
                "  board: turn {0} | my prizes {1}, opp prizes {2}, deck {3}, hand {4}",
                fx["turn"], tracker.MyPrizes, tracker.OppPrizes, tracker.MyDeckCount, tracker.MyHand));
            Console.WriteLine("  tracker: " + tracker.Status);
            Console.WriteLine("  prize rows: " + tracker.Prizes.Count);
        }

        private static Dictionary<string, string> Ids(JObject fx)
        {
            var ids = new Dictionary<string, string>();
            foreach (var kv in (JObject)fx["srcIds"]) ids[Game.Pretty(kv.Key)] = (string)kv.Value;
            return ids;
        }

        private static string Find()
        {
            var dir = Directory.GetCurrentDirectory();
            for (int i = 0; i < 7 && !string.IsNullOrEmpty(dir); i++)
            {
                var a = Path.Combine(dir, "Preview", "fixture.json");
                if (File.Exists(a)) return a;
                var b = Path.Combine(dir, "fixture.json");
                if (File.Exists(b)) return b;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        private static CardRef Hidden()
        {
            return new CardRef { EntityId = "PRIVATE", SourceId = null, Name = null };
        }

        private static CardRef From(JObject o)
        {
            if (o == null) return null;
            return new CardRef
            {
                EntityId = (string)(o["entityId"] ?? Guid.NewGuid().ToString()),
                SourceId = (string)o["srcId"],
                Name = Game.Pretty((string)o["name"]),
            };
        }

        /// <summary>Attached energy and tools are part of the 60 and must be accounted for.</summary>
        private static void AddAttached(JObject poke, List<CardRef> into, Dictionary<string, string> ids)
        {
            foreach (var key in new[] { "energy", "tools" })
            {
                var arr = (JArray)poke[key];
                if (arr == null) continue;
                foreach (var e in arr)
                {
                    var nm = Game.Pretty((string)e);
                    string sid;
                    ids.TryGetValue(nm, out sid);
                    into.Add(new CardRef { EntityId = Guid.NewGuid().ToString(), SourceId = sid, Name = nm });
                }
            }
        }

        private static SideSnapshot Side(JObject side, Dictionary<string, string> ids, bool deriveDeck)
        {
            var s = new SideSnapshot();
            s.Active = From((JObject)side["active"]);
            foreach (var b in (JArray)side["bench"] ?? new JArray()) s.Bench.Add(From((JObject)b));
            foreach (var h in (JArray)side["hand"] ?? new JArray()) s.Hand.Add(From((JObject)h));
            foreach (var d in (JArray)side["discardList"] ?? new JArray()) s.Discard.Add(From((JObject)d));

            var attached = new List<CardRef>();
            if (side["active"] != null) AddAttached((JObject)side["active"], attached, ids);
            foreach (var b in (JArray)side["bench"] ?? new JArray()) AddAttached((JObject)b, attached, ids);

            int prizes = (int)side["prize"];
            for (int i = 0; i < prizes; i++) s.Prize.Add(Hidden());

            // The deck count is DERIVED for our own side rather than taken from the replay: that
            // format exports only the TOP card of an evolution stack, so the cards underneath are
            // missing and the raw count would leave the 60 not adding up.
            int visible = (s.Active != null ? 1 : 0) + s.Bench.Count + s.Hand.Count
                        + s.Discard.Count + attached.Count;
            int deckHidden = deriveDeck ? Math.Max(0, 60 - visible - prizes) : (int)side["deck"];
            for (int i = 0; i < deckHidden; i++) s.Deck.Add(Hidden());

            s.All = new List<CardRef>();
            if (s.Active != null) s.All.Add(s.Active);
            s.All.AddRange(s.Bench);
            s.All.AddRange(s.Hand);
            s.All.AddRange(s.Discard);
            s.All.AddRange(attached);
            s.All.AddRange(s.Deck);
            s.All.AddRange(s.Prize);
            return s;
        }

        private static BoardSnapshot Snapshot(JObject fx)
        {
            var ids = Ids(fx);
            return new BoardSnapshot
            {
                Valid = true,
                IAmPlayer1 = true,
                Turn = (int)fx["turn"],
                Me = Side((JObject)fx["me"], ids, true),
                Opp = Side((JObject)fx["opp"], ids, false),
            };
        }
    }
}
