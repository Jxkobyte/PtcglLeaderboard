using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;

namespace PrizeTracker.Core
{
    /// <summary>
    /// The current ranked season: its id, when it ends, and the league/rank ladder - read from the
    /// SAME data the client uses, not derived.
    ///
    /// The client has no "time left in season" request. It fetches the season's config document
    /// (ConfigUtils.GetSeasonDocID_V_2_0(season) = "season_0054_0.2", key "season54") through its
    /// IConfigLoader and reads endDate out of it; the current season id comes from the document
    /// "season_current_0.0". Both fetches are cached on disk under
    /// AppData/LocalLow/pokemon/Pokemon TCG Live/config-cache/ with exactly those names, so this
    /// reads the cache: the response the client got, verbatim, with no second request and no game
    /// needed.
    ///
    /// Seasons are NOT a fixed length - 51, 56 and 61 days across three recent ones - which is why
    /// the end date is read and never computed. Every one seen so far ends at 17:00 UTC.
    ///
    /// The rank lookup is a port of LeagueConfigContent.GetRankByEXP, quirks included, so the
    /// badge shown for an exp value is the one the client would show.
    ///
    /// Core has no BepInEx dependency: callers pass the folder in.
    /// </summary>
    internal class Season
    {
        public class Rank
        {
            public string Name;       // e.g. great_league_rank2
            public string Image;      // asset in the "leagueicons" bundle, e.g. rankIcon_Image_GreatLeague_2
            public uint ExpLimit;
        }

        public class League
        {
            public string Name;       // e.g. great_league
            public string ColorHex;   // e.g. #006CB2
            public string Frame;      // asset in the "leagueicons" bundle, e.g. rankIcon_Frame_GreatLeague
            public List<Rank> Ranks = new List<Rank>();

            /// <summary>"Great League", from "great_league".</summary>
            public string Title
            {
                get
                {
                    if (string.IsNullOrEmpty(Name)) return "";
                    var parts = Name.Replace('_', ' ').Split(' ');
                    for (int i = 0; i < parts.Length; i++)
                        if (parts[i].Length > 0)
                            parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);
                    return string.Join(" ", parts);
                }
            }
        }

        public int Id;
        public DateTime? EndUtc;
        public DateTime? StartUtc;       // the previous season's end, when that document is cached
        public List<League> Leagues = new List<League>();

        /// <summary>Where the client caches its config documents.</summary>
        public static string DefaultCacheDir()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // LocalLow is a sibling of Local with no SpecialFolder of its own.
            var low = Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow");
            return Path.Combine(low, "pokemon", "Pokemon TCG Live", "config-cache");
        }

        /// <summary>Load the current season, or null if the cache has none.</summary>
        public static Season Load(string cacheDir)
        {
            try
            {
                var cur = Doc(Path.Combine(cacheDir, "season_current_0.0.json"), "season");
                if (cur == null) return null;
                int id = cur.Value<int?>("seasonID") ?? 0;
                if (id <= 0) return null;

                var s = LoadSeason(cacheDir, id);
                if (s == null) return null;

                var prev = LoadSeason(cacheDir, id - 1);
                if (prev != null && prev.EndUtc.HasValue) s.StartUtc = prev.EndUtc;
                return s;
            }
            catch { return null; }
        }

        /// <summary>One season's document, in the client's own naming.</summary>
        public static Season LoadSeason(string cacheDir, int id)
        {
            if (id <= 0) return null;
            // The client requests version 0.2 of the document; older caches only hold 0.1.
            foreach (var ver in new[] { "0.2", "0.1", "0.0" })
            {
                var file = Path.Combine(cacheDir, string.Format(CultureInfo.InvariantCulture,
                    "season_{0:0000}_{1}.json", id, ver));
                var content = Doc(file, "season" + id);
                if (content == null) continue;

                var s = new Season { Id = content.Value<int?>("seasonID") ?? id };
                s.EndUtc = ParseUtc(content.Value<string>("endDate"));

                var lc = content["leagueConfigContent"] as JObject;
                var leagues = lc != null ? lc["leagueData"] as JArray : null;
                if (leagues != null)
                {
                    foreach (var l in leagues)
                    {
                        var league = new League
                        {
                            Name = l.Value<string>("leagueName") ?? "",
                            ColorHex = l.Value<string>("leagueColorHex") ?? "",
                            Frame = l.Value<string>("leagueFrame") ?? "",
                        };
                        var ranks = l["rankData"] as JArray;
                        if (ranks != null)
                            foreach (var r in ranks)
                                league.Ranks.Add(new Rank
                                {
                                    Name = r.Value<string>("rankName") ?? "",
                                    Image = r.Value<string>("rankImage") ?? "",
                                    ExpLimit = r.Value<uint?>("expLimit") ?? 0,
                                });
                        s.Leagues.Add(league);
                    }
                }
                return s;
            }
            return null;
        }

        /// <summary>
        /// The league and rank for an exp value, exactly as LeagueConfigContent.GetRankByEXP
        /// decides it: walk every rank in order, keeping a running threshold, and take each rank
        /// whose predecessor's limit the exp has reached. Beyond the very last limit the client
        /// wraps to zero - odd, but reproduced so the two never disagree.
        /// </summary>
        public bool RankFor(uint exp, out League league, out Rank rank, out int rankIndex)
        {
            league = null; rank = null; rankIndex = 0;
            if (Leagues.Count == 0) return false;

            var last = Leagues[Leagues.Count - 1];
            if (last.Ranks.Count > 0 && exp > last.Ranks[last.Ranks.Count - 1].ExpLimit) exp = 0;

            uint threshold = 0;
            league = Leagues[0];
            rank = league.Ranks.Count > 0 ? league.Ranks[0] : null;
            foreach (var l in Leagues)
            {
                for (int i = 0; i < l.Ranks.Count; i++)
                {
                    var r = l.Ranks[i];
                    if (exp >= threshold && r.ExpLimit >= threshold)
                    {
                        league = l; rank = r; rankIndex = i;
                        threshold = r.ExpLimit;
                    }
                }
            }
            return rank != null;
        }

        /// <summary>"43 days" / "6 hours" / "ended" - for the header.</summary>
        public string TimeLeftText(DateTime nowUtc)
        {
            if (!EndUtc.HasValue) return "";
            var left = EndUtc.Value - nowUtc;
            if (left.TotalSeconds <= 0) return "ended";
            if (left.TotalDays >= 2) return ((int)Math.Floor(left.TotalDays)) + " days";
            if (left.TotalHours >= 2) return ((int)Math.Floor(left.TotalHours)) + " hours";
            return Math.Max(1, (int)Math.Floor(left.TotalMinutes)) + " min";
        }

        private static JObject Doc(string file, string key)
        {
            if (!File.Exists(file)) return null;
            var root = JObject.Parse(File.ReadAllText(file));
            var keys = root["keys"] as JObject;
            var entry = keys != null ? keys[key] as JObject : null;
            var blob = entry != null ? entry.Value<string>("contentString") : null;
            if (string.IsNullOrEmpty(blob)) return null;
            return JObject.Parse(blob);
        }

        public static DateTime? ParseUtc(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            DateTime d;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                                  DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d))
                return d;
            return null;
        }
    }
}
