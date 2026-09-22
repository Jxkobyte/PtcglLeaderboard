using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RainierClientSDK.source.SeasonRank;
using UnityEngine;

namespace PrizeTracker.Core
{
    internal class BoardRow
    {
        public int Rank;
        public string PlayerId = "";
        public string DisplayName = "";
        public int Exp, Wins, Losses, SeasonMatches, ConsecutiveWins, Snapshots;
        public long FirstSeen, LastSeen;
        public List<string> Flags = new List<string>();

        public string Record { get { return Wins + "-" + Losses; } }
    }

    internal class BoardState
    {
        public int SeasonId;
        public string EndDate = "";
        public int EndDateVotes, EndDateTotal;
        public int Total;
        public long UpdatedTs;
        public List<BoardRow> Players = new List<BoardRow>();
        public BoardRow Me;
        public DateTime FetchedUtc;
    }

    /// <summary>
    /// The community leaderboard client: submits the game's OWN season record after each match,
    /// and fetches the board for the screen.
    ///
    /// What is submitted is SeasonRank - exp, wins, losses, seasonMatches - which the client
    /// fetches from TPCi's servers. It is the official record, it resets with the season by
    /// itself, and it is what the board ranks on. Our own match log rides along only as a
    /// cross-check (the service flags a player whose local count and official count disagree).
    ///
    /// Opt-in. Nothing leaves the machine until Enabled is true, and the only identity sent is a
    /// random id generated once plus the display name. Opponents' names are never sent.
    ///
    /// All network work runs on a background thread through HttpWebRequest (System.dll, so no
    /// extra assembly reference) and results are handed back to the main thread through a queue
    /// drained in Update - Unity objects must not be touched from other threads.
    /// </summary>
    internal class Leaderboard : MonoBehaviour
    {
        public bool Enabled;
        public string PlayerId = "";
        public string DisplayName = "";        // user override; empty means the in-game name
        public string ApiBase = "";
        public Season Season;
        public MatchHistory History;

        /// <summary>Raised when a learned in-game name should be persisted.</summary>
        public Action<string> OnLearnedName;

        public BoardState State { get; private set; }
        public bool Busy { get; private set; }
        public string LastError { get; private set; }
        public string LastSubmit { get; private set; }
        public string LearnedName { get; private set; }

        public string EffectiveName
        {
            get
            {
                if (!string.IsNullOrEmpty(DisplayName)) return DisplayName.Trim();
                if (!string.IsNullOrEmpty(LearnedName)) return LearnedName;
                return "";
            }
        }

        public bool Configured
        {
            get { return Enabled && !string.IsNullOrEmpty(ApiBase) && !string.IsNullOrEmpty(PlayerId); }
        }

        private readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();
        private float _nextRefreshAllowed;
        private float _nextNameCheck;

        private void Update()
        {
            Action a;
            while (_mainThread.TryDequeue(out a)) { try { a(); } catch (Exception e) { Plugin.Log.LogWarning("leaderboard: " + e.Message); } }

            // The in-game name is only readable while a match is running (it comes from the
            // match's player list), so it is learned then and remembered.
            if (Time.unscaledTime >= _nextNameCheck)
            {
                _nextNameCheck = Time.unscaledTime + 5f;
                try
                {
                    var name = Game.MyName();
                    if (!string.IsNullOrEmpty(name) && name != LearnedName)
                    {
                        LearnedName = name;
                        if (OnLearnedName != null) OnLearnedName(name);
                    }
                }
                catch { }
            }
        }

        // -----------------------------------------------------------------------------------
        // submit

        /// <summary>A name remembered from an earlier session.</summary>
        public void SetLearnedName(string name)
        {
            if (!string.IsNullOrEmpty(name)) LearnedName = name;
        }

        /// <summary>
        /// Submit once the client has had time to refresh SeasonRank from the server - it does
        /// that in its own end-of-match flow, after the result is known, so submitting the instant
        /// the match ends would send the PREVIOUS record.
        /// </summary>
        public void SubmitAfterMatchDelayed(float seconds)
        {
            if (!Configured) { LastSubmit = Enabled ? "not configured" : "opted out"; return; }
            StartCoroutine(SubmitLater(seconds));
        }

        private System.Collections.IEnumerator SubmitLater(float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
            SubmitAfterMatch();
        }

        /// <summary>
        /// Send the season record. Called after a match is recorded, when SeasonRank has been
        /// refreshed by the client's own end-of-match flow.
        /// </summary>
        public void SubmitAfterMatch()
        {
            if (!Configured) { LastSubmit = Enabled ? "not configured" : "opted out"; return; }
            if (Season == null) Season = Season.Load(Season.DefaultCacheDir());

            uint exp, wins, losses, matches, seasonIdx; int streak;
            if (!ReadSeasonRank(out exp, out wins, out losses, out matches, out streak, out seasonIdx))
            {
                LastSubmit = "season record unavailable";
                Plugin.Log.LogWarning("leaderboard: SeasonRank not readable; nothing submitted.");
                return;
            }

            int seasonId = seasonIdx > 0 ? (int)seasonIdx : (Season != null ? Season.Id : 0);
            if (seasonId <= 0) { LastSubmit = "no season"; return; }

            var body = new JObject
            {
                ["playerId"] = PlayerId,
                ["displayName"] = string.IsNullOrEmpty(EffectiveName) ? "Player" : EffectiveName,
                ["seasonId"] = seasonId,
                ["exp"] = exp,
                ["wins"] = wins,
                ["losses"] = losses,
                ["seasonMatches"] = matches,
                ["consecutiveWins"] = streak,
                ["localMatches"] = LocalMatchesThisSeason(),
                ["clientTs"] = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds,
            };
            if (Season != null && Season.EndUtc.HasValue)
                body["endDate"] = Season.EndUtc.Value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

            Post("/v1/snapshot", body.ToString(Formatting.None), (ok, text) =>
            {
                if (!ok) { LastSubmit = "failed: " + text; Plugin.Log.LogWarning("leaderboard submit failed: " + text); return; }
                try
                {
                    var r = JObject.Parse(text);
                    var flags = r["flags"] as JArray;
                    LastSubmit = "rank " + r.Value<int?>("rank") +
                                 (flags != null && flags.Count > 0 ? " flags " + string.Join(",", flags.ToObject<string[]>()) : "");
                }
                catch { LastSubmit = "submitted"; }
                Plugin.Log.LogInfo("leaderboard: " + LastSubmit);
                Refresh(true);
            });
        }

        private static bool ReadSeasonRank(out uint exp, out uint wins, out uint losses, out uint matches,
                                           out int streak, out uint seasonIdx)
        {
            exp = wins = losses = matches = seasonIdx = 0; streak = 0;
            try
            {
                exp = SeasonRank.exp;
                wins = SeasonRank.wins;
                losses = SeasonRank.losses;
                matches = SeasonRank.seasonMatches;
                streak = SeasonRank.consecutiveWins;
                seasonIdx = SeasonRank.seasonIndex;
                return true;
            }
            catch { return false; }
        }

        private int LocalMatchesThisSeason()
        {
            if (History == null || Season == null || !Season.StartUtc.HasValue) return 0;
            int n = 0;
            foreach (var r in History.Records)
                if (r != null && r.WhenUtc >= Season.StartUtc.Value) n++;
            return n;
        }

        // -----------------------------------------------------------------------------------
        // fetch

        /// <summary>Fetch the board. Throttled unless forced; the screen calls this on open.</summary>
        public void Refresh(bool force)
        {
            if (Busy) return;
            if (!force && Time.unscaledTime < _nextRefreshAllowed) return;
            _nextRefreshAllowed = Time.unscaledTime + 30f;
            if (string.IsNullOrEmpty(ApiBase)) { LastError = "no server configured"; return; }

            var path = "/v1/leaderboard?limit=100" +
                       (string.IsNullOrEmpty(PlayerId) ? "" : "&player=" + Uri.EscapeDataString(PlayerId));
            Busy = true;
            Get(path, (ok, text) =>
            {
                Busy = false;
                if (!ok) { LastError = text; return; }
                try
                {
                    State = Parse(text);
                    State.FetchedUtc = DateTime.UtcNow;
                    LastError = null;
                }
                catch (Exception e) { LastError = "bad response: " + e.Message; }
            });
        }

        private static BoardState Parse(string text)
        {
            var j = JObject.Parse(text);
            var st = new BoardState
            {
                SeasonId = j.Value<int?>("seasonId") ?? 0,
                EndDate = j.Value<string>("endDate") ?? "",
                Total = j.Value<int?>("total") ?? 0,
                UpdatedTs = j.Value<long?>("updatedTs") ?? 0,
            };
            var agree = j["endDateAgreement"] as JObject;
            if (agree != null)
            {
                st.EndDateVotes = agree.Value<int?>("votes") ?? 0;
                st.EndDateTotal = agree.Value<int?>("total") ?? 0;
            }
            var players = j["players"] as JArray;
            if (players != null) foreach (var p in players) st.Players.Add(Row((JObject)p));
            var me = j["me"] as JObject;
            if (me != null) st.Me = Row(me);
            return st;
        }

        private static BoardRow Row(JObject p)
        {
            var r = new BoardRow
            {
                Rank = p.Value<int?>("rank") ?? 0,
                PlayerId = p.Value<string>("playerId") ?? "",
                DisplayName = p.Value<string>("displayName") ?? "",
                Exp = p.Value<int?>("exp") ?? 0,
                Wins = p.Value<int?>("wins") ?? 0,
                Losses = p.Value<int?>("losses") ?? 0,
                SeasonMatches = p.Value<int?>("seasonMatches") ?? 0,
                ConsecutiveWins = p.Value<int?>("consecutiveWins") ?? 0,
                Snapshots = p.Value<int?>("snapshots") ?? 0,
                FirstSeen = p.Value<long?>("firstSeen") ?? 0,
                LastSeen = p.Value<long?>("lastSeen") ?? 0,
            };
            var flags = p["flags"] as JArray;
            if (flags != null) foreach (var f in flags) r.Flags.Add(f.ToString());
            return r;
        }

        // -----------------------------------------------------------------------------------
        // transport

        private void Get(string path, Action<bool, string> done) { Send("GET", path, null, done); }
        private void Post(string path, string body, Action<bool, string> done) { Send("POST", path, body, done); }

        private void Send(string method, string path, string body, Action<bool, string> done)
        {
            var url = ApiBase.TrimEnd('/') + path;
            var t = new Thread(() =>
            {
                bool ok; string text;
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method = method;
                    req.Timeout = 10000;
                    req.ReadWriteTimeout = 10000;
                    req.UserAgent = "PrizeTracker/1.0";
                    req.Accept = "application/json";
                    if (body != null)
                    {
                        var bytes = Encoding.UTF8.GetBytes(body);
                        req.ContentType = "application/json";
                        req.ContentLength = bytes.Length;
                        using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                    }
                    using (var res = (HttpWebResponse)req.GetResponse())
                    using (var rd = new StreamReader(res.GetResponseStream()))
                    {
                        text = rd.ReadToEnd();
                        ok = (int)res.StatusCode >= 200 && (int)res.StatusCode < 300;
                    }
                }
                catch (WebException we)
                {
                    ok = false;
                    text = we.Message;
                    try
                    {
                        var res = we.Response as HttpWebResponse;
                        if (res != null)
                            using (var rd = new StreamReader(res.GetResponseStream()))
                                text = (int)res.StatusCode + " " + rd.ReadToEnd();
                    }
                    catch { }
                }
                catch (Exception e) { ok = false; text = e.Message; }
                _mainThread.Enqueue(() => done(ok, text));
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>A new random player id, for first run.</summary>
        public static string NewPlayerId()
        {
            return "p-" + Guid.NewGuid().ToString("N");
        }
    }
}
