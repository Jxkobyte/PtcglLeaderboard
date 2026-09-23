using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PtcglLeaderboard.Core
{
    /// <summary>One finished match.</summary>
    internal class MatchRecord
    {
        public DateTime WhenUtc;
        public int DurationSeconds;
        public string MyDeck = "";
        public string DeckId = "";    // stable inventory id; survives a rename
        public string Opponent = "";
        public string OppArchetype = "";   // derived from the Pokemon they actually showed
        public bool Won;
        public int MyPrizesLeft;
        public int OppPrizesLeft;
        public int Turns;
        public string DeckBox = "";    // deck box art id (a 3D UV unwrap - see CoverCard)
        public string CoverCard = "";  // a card that stands for the deck, for the row thumbnail
        public string Sleeve = "";     // the deck's card sleeve - what the client's own deck tile shows
        public string Log = "";       // the client's own battle log text, when captured

        /// <summary>
        /// The opponent's cards we actually saw, as "cardSourceId*count" pairs separated by spaces.
        ///
        /// Counts come from the tracker, which keys on ENTITY id - so two physical copies of a card
        /// are two entities and count 2, while one card moving hand-to-discard-to-hand stays 1.
        /// Only source ids are stored: names and art are both resolved from the card cache at
        /// display time, and the client's own deck exporter wants ids anyway.
        /// </summary>
        public string OppCards = "";

        /// <summary>
        /// Names for OppCards, in the same order, separated by '|'.
        ///
        /// Resolved at match time on purpose: CardCache is only populated while a match is live, so
        /// looking a name up later from the menus returns nothing and the card shows as a raw id
        /// like "sve_10_ph". Same reason FillerCard is captured here rather than computed on use.
        /// </summary>
        public string OppNames = "";

        /// <summary>
        /// One digit per card in OppCards order: 0 Pokemon, 1 Trainer, 2 Energy, 3 unknown.
        ///
        /// Captured at match time for the same reason names are - CardCache only holds cards while
        /// a match is live, so the type cannot be looked up later from the menus.
        /// </summary>
        public string OppTypes = "";


        public string Result { get { return Won ? "W" : "L"; } }

        /// <summary>Parsed <see cref="OppCards"/>: source id and how many distinct copies we saw.</summary>
        public List<KeyValuePair<string, int>> OppCardList()
        {
            var list = new List<KeyValuePair<string, int>>();
            if (string.IsNullOrEmpty(OppCards)) return list;
            foreach (var part in OppCards.Split(' '))
            {
                if (part.Length == 0) continue;
                int star = part.LastIndexOf('*');
                if (star <= 0) continue;
                int n;
                if (!int.TryParse(part.Substring(star + 1), out n) || n <= 0) continue;
                list.Add(new KeyValuePair<string, int>(part.Substring(0, star), n));
            }
            return list;
        }

        /// <summary>Display name for one of OppCards, by index. Falls back to the id.</summary>
        public string OppNameAt(int index, string sourceId)
        {
            if (!string.IsNullOrEmpty(OppNames))
            {
                var names = OppNames.Split('|');
                if (index >= 0 && index < names.Length && names[index].Length > 0) return names[index];
            }
            return sourceId;
        }

        /// <summary>Sort rank for one card: Pokemon, then Trainer, then Energy.</summary>
        public int OppTypeAt(int index)
        {
            if (index >= 0 && index < OppTypes.Length)
            {
                int n = OppTypes[index] - '0';
                if (n >= 0 && n <= 3) return n;
            }
            return 3;
        }

        /// <summary>Total distinct cards of theirs we identified - the "N of 60" numerator.</summary>
        public int OppCardsSeen()
        {
            int n = 0;
            foreach (var kv in OppCardList()) n += kv.Value;
            return n;
        }

        /// <summary>
        /// Hand-rolled JSON, deliberately.
        ///
        /// The record is flat (strings, ints, a date), and the alternative - Newtonsoft - is a trap
        /// here: the copy shipped with the game is a Unity-patched strong-named build that fails
        /// signature validation outside Unity, so anything in Core that used it would work in game
        /// and throw in the tests and previewer. Forty lines of serializer avoids that entirely.
        /// </summary>
        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append('{');
            Str(sb, "when", WhenUtc.ToString("o", CultureInfo.InvariantCulture)); sb.Append(',');
            Num(sb, "seconds", DurationSeconds); sb.Append(',');
            Str(sb, "deck", MyDeck); sb.Append(',');
            Str(sb, "deckId", DeckId); sb.Append(',');
            Str(sb, "opponent", Opponent); sb.Append(',');
            Str(sb, "oppArchetype", OppArchetype); sb.Append(',');
            Num(sb, "won", Won ? 1 : 0); sb.Append(',');
            Num(sb, "myPrizesLeft", MyPrizesLeft); sb.Append(',');
            Num(sb, "oppPrizesLeft", OppPrizesLeft); sb.Append(',');
            Num(sb, "turns", Turns); sb.Append(',');
            Str(sb, "deckBox", DeckBox); sb.Append(',');
            Str(sb, "cover", CoverCard); sb.Append(',');
            Str(sb, "sleeve", Sleeve); sb.Append(',');
            Str(sb, "oppCards", OppCards); sb.Append(',');
            Str(sb, "oppNames", OppNames); sb.Append(',');
            Str(sb, "oppTypes", OppTypes); sb.Append(',');
            Str(sb, "log", Log);
            sb.Append('}');
            return sb.ToString();
        }

        private static void Str(StringBuilder sb, string k, string v)
        {
            sb.Append('"').Append(k).Append("\":\"").Append(Escape(v ?? "")).Append('"');
        }

        private static void Num(StringBuilder sb, string k, int v)
        {
            sb.Append('"').Append(k).Append("\":").Append(v.ToString(CultureInfo.InvariantCulture));
        }

        private static string Escape(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                // A battle log is MULTI-LINE, and a record is one line of a .jsonl file. Flattening
                // newlines to spaces - which is what this used to do - would quietly destroy the
                // log's structure, so they are escaped properly and decoded symmetrically below.
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 32) sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static MatchRecord FromJson(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            var f = Fields(line);
            if (f.Count == 0) return null;
            var r = new MatchRecord();
            DateTime when;
            if (DateTime.TryParse(Get(f, "when"), CultureInfo.InvariantCulture,
                                  DateTimeStyles.RoundtripKind, out when)) r.WhenUtc = when;
            r.DurationSeconds = Int(f, "seconds");
            r.MyDeck = Get(f, "deck");
            r.DeckId = Get(f, "deckId");
            r.Opponent = Get(f, "opponent");
            r.OppArchetype = Get(f, "oppArchetype");
            r.Won = Int(f, "won") != 0;
            r.MyPrizesLeft = Int(f, "myPrizesLeft");
            r.OppPrizesLeft = Int(f, "oppPrizesLeft");
            r.Turns = Int(f, "turns");
            r.DeckBox = Get(f, "deckBox");
            r.CoverCard = Get(f, "cover");
            r.Sleeve = Get(f, "sleeve");
            r.Log = Get(f, "log");
            r.OppCards = Get(f, "oppCards");
            r.OppNames = Get(f, "oppNames");
            r.OppTypes = Get(f, "oppTypes");
            return r;
        }

        private static string Get(Dictionary<string, string> f, string k)
        {
            string v;
            return f.TryGetValue(k, out v) ? v : "";
        }

        private static int Int(Dictionary<string, string> f, string k)
        {
            int v;
            return int.TryParse(Get(f, k), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        /// <summary>Tokenises a flat one-level JSON object. Not general purpose; it does not need to be.</summary>
        private static Dictionary<string, string> Fields(string s)
        {
            var d = new Dictionary<string, string>();
            int i = 0;
            while (i < s.Length)
            {
                int ks = s.IndexOf('"', i);
                if (ks < 0) break;
                int ke = EndOfString(s, ks + 1);
                if (ke < 0) break;
                string key = Unescape(s.Substring(ks + 1, ke - ks - 1));

                int colon = s.IndexOf(':', ke);
                if (colon < 0) break;
                int p = colon + 1;
                while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
                if (p >= s.Length) break;

                if (s[p] == '"')
                {
                    int ve = EndOfString(s, p + 1);
                    if (ve < 0) break;
                    d[key] = Unescape(s.Substring(p + 1, ve - p - 1));
                    i = ve + 1;
                }
                else
                {
                    int ve = p;
                    while (ve < s.Length && s[ve] != ',' && s[ve] != '}') ve++;
                    d[key] = s.Substring(p, ve - p).Trim();
                    i = ve;
                }
            }
            return d;
        }

        private static int EndOfString(string s, int from)
        {
            for (int i = from; i < s.Length; i++)
            {
                if (s[i] == '\\') { i++; continue; }
                if (s[i] == '"') return i;
            }
            return -1;
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    if (n == 'n') sb.Append('\n');
                    else if (n == 'r') sb.Append('\r');
                    else if (n == 't') sb.Append('\t');
                    else sb.Append(n);          // \" and \\ decode to themselves, as before
                    continue;
                }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }
    }

    internal class Winrate
    {
        public string Label = "";
        public int Wins, Losses;
        public int Played { get { return Wins + Losses; } }
        public double Rate { get { return Played == 0 ? 0 : (double)Wins / Played; } }
    }

    /// <summary>
    /// Append-only match log plus the aggregates worth looking at between games.
    ///
    /// JSONL rather than one big JSON array: appending a line is atomic enough to survive the game
    /// being killed mid-write, and a corrupt line costs you that one match rather than the file.
    /// Unparseable lines are skipped rather than throwing.
    /// </summary>
    internal class MatchHistory
    {
        private readonly List<MatchRecord> _records = new List<MatchRecord>();
        public string Path { get; private set; }

        public IList<MatchRecord> Records { get { return _records; } }
        public int Count { get { return _records.Count; } }

        public MatchHistory(string path) { Path = path; }

        public void Load()
        {
            _records.Clear();
            try
            {
                if (!File.Exists(Path)) return;
                foreach (var line in File.ReadAllLines(Path))
                {
                    var r = MatchRecord.FromJson(line.Trim());
                    if (r != null) _records.Add(r);
                }
            }
            catch { /* a broken history file must never stop the overlay */ }
        }

        public void Add(MatchRecord r)
        {
            if (r == null) return;
            _records.Add(r);
            try
            {
                var dir = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                // UTF8Encoding(false), not Encoding.UTF8: the latter writes a byte-order mark when
                // it CREATES the file. Our own reader strips it, but a .jsonl file is meant to be
                // readable by anything, and a leading BOM breaks a naive parser on line one.
                File.AppendAllText(Path, r.ToJson() + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }

        public Winrate Overall()
        {
            var w = new Winrate { Label = "Overall" };
            foreach (var r in _records) { if (r.Won) w.Wins++; else w.Losses++; }
            return w;
        }

        public List<Winrate> ByDeck() { return Group(r => string.IsNullOrEmpty(r.MyDeck) ? "(unknown)" : r.MyDeck); }

        public List<Winrate> ByMatchup()
        {
            return Group(r => string.IsNullOrEmpty(r.OppArchetype) ? "(unknown)" : r.OppArchetype);
        }

        private List<Winrate> Group(Func<MatchRecord, string> key)
        {
            var map = new Dictionary<string, Winrate>();
            foreach (var r in _records)
            {
                var k = key(r);
                Winrate w;
                if (!map.TryGetValue(k, out w)) { w = new Winrate { Label = k }; map[k] = w; }
                if (r.Won) w.Wins++; else w.Losses++;
            }
            return map.Values.OrderByDescending(w => w.Played).ThenBy(w => w.Label).ToList();
        }

        /// <summary>Most recent first.</summary>
        public List<MatchRecord> Recent(int n)
        {
            var list = new List<MatchRecord>(_records);
            list.Reverse();
            return list.Take(n).ToList();
        }

        /// <summary>A slice of the history, newest first - one page of the match list.</summary>
        public List<MatchRecord> Page(int start, int count)
        {
            var list = new List<MatchRecord>(_records);
            list.Reverse();
            if (start < 0) start = 0;
            if (start >= list.Count || count <= 0) return new List<MatchRecord>();
            return list.Skip(start).Take(count).ToList();
        }

        /// <summary>
        /// Win/loss for one specific deck, by name.
        ///
        /// Matched on the deck NAME, which is what gets recorded. Renaming a deck therefore starts
        /// its record over - the client's deck ids are not stable enough across edits to key on
        /// instead, and a name is what you actually recognise in the list.
        /// </summary>
        public Winrate ForDeck(string deckName) { return ForDeck(null, deckName); }

        /// <summary>
        /// Win/loss for one deck.
        ///
        /// Matched on the deck's stable inventory id where both sides have one, so renaming a deck
        /// keeps its record. Falls back to the name, which also covers matches recorded before ids
        /// were stored. Note this deliberately tracks the DECK, not its contents - edit the list
        /// freely and the record follows it.
        /// </summary>
        public Winrate ForDeck(string deckId, string deckName)
        {
            var w = new Winrate { Label = deckName ?? "" };
            bool haveId = !string.IsNullOrEmpty(deckId);
            if (!haveId && string.IsNullOrEmpty(deckName)) return w;

            foreach (var r in _records)
            {
                bool match;
                if (haveId && !string.IsNullOrEmpty(r.DeckId))
                    match = string.Equals(r.DeckId, deckId, StringComparison.Ordinal);
                else
                    match = !string.IsNullOrEmpty(deckName) &&
                            string.Equals(r.MyDeck, deckName, StringComparison.OrdinalIgnoreCase);
                if (!match) continue;
                if (r.Won) w.Wins++; else w.Losses++;
            }
            return w;
        }

        /// <summary>Current streak, e.g. "+3" for three wins, "-2" for two losses.</summary>
        public string Streak()
        {
            if (_records.Count == 0) return "-";
            bool won = _records[_records.Count - 1].Won;
            int n = 0;
            for (int i = _records.Count - 1; i >= 0 && _records[i].Won == won; i--) n++;
            return (won ? "+" : "-") + n;
        }
    }
}
