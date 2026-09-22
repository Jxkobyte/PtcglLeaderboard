using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PrizeTracker.Core
{
    /// <summary>
    /// The community leaderboard, as a REAL screen in the client's own navigation - built the way
    /// NativeHistoryScreen is, for the same reasons (see its header): the client reparents it into
    /// the active screen holder, runs its own transition, and the top bar's Animator handles the
    /// selected look.
    ///
    /// Ranked by exp, the game's own season rank, with each player's league badge drawn from the
    /// same "leagueicons" bundle and the same GetRankByEXP rule the friends screen uses - so a
    /// badge here is the badge the client would show for that player.
    ///
    /// Anyone can look. Opting in only controls whether YOUR record is submitted.
    /// </summary>
    internal class NativeLeaderboardScreen : HUBScreenController, INativeScreen
    {
        public Leaderboard Board;
        public Season Season;

        private static readonly Color Ink = new Color(0.16f, 0.17f, 0.20f, 1f);
        private static readonly Color InkDim = new Color(0.55f, 0.57f, 0.61f, 1f);
        private static readonly Color Accent = new Color(0.176f, 0.408f, 0.847f, 1f);
        private static readonly Color MeTint = new Color(0.176f, 0.408f, 0.847f, 0.13f);
        private static readonly Color RowA = new Color(0.965f, 0.968f, 0.975f, 1f);
        private static readonly Color RowB = new Color(0.985f, 0.987f, 0.990f, 1f);
        private static readonly Color Hairline = new Color(0f, 0f, 0f, 0.10f);
        private static readonly Color FlagBg = new Color(0.984f, 0.941f, 0.824f, 1f);
        private static readonly Color FlagInk = new Color(0.54f, 0.43f, 0.12f, 1f);

        private const float PanelW = 2260f, PanelH = 1240f;
        private const float RowW = 2100f, RowH = 92f, RowGap = 6f;
        private const int VisibleRows = 9;
        private const float BodyTop = 212f;

        private bool _built;
        private Transform _body;
        private RectTransform _pinned;
        private TextMeshProUGUI _subtitle, _foot, _status, _empty;
        private readonly List<GameObject> _rows = new List<GameObject>();
        private BoardState _rendered;
        private float _nextTick;

        private class PendingArt { public string Bundle, Asset; public RawImage Target; }
        private readonly List<PendingArt> _pending = new List<PendingArt>();

        public override bool DeferReconnectRequest { get { return true; } }
        public override ScreenType screenType { get { return ScreenType.None; } }

        public override void OnActivate(HUBGroupController prevGroup)
        {
            base.OnActivate(prevGroup);
            try
            {
                Build();
                if (Board != null) Board.Refresh(true);
                Populate();
            }
            catch (Exception e) { Plugin.Log.LogWarning("leaderboard screen activate failed: " + e.Message); }
        }

        private void Update()
        {
            FillArt();
            if (Time.unscaledTime < _nextTick) return;
            _nextTick = Time.unscaledTime + 1f;
            if (!_built || !gameObject.activeInHierarchy) return;

            try
            {
                if (Board != null && Board.State != _rendered) Populate();
                else UpdateStatus();
            }
            catch (Exception e) { Plugin.Log.LogWarning("leaderboard screen update failed: " + e.Message); }
        }

        // -----------------------------------------------------------------------------------

        public void Build()
        {
            if (_built) return;
            _built = true;

            var root = (RectTransform)transform;
            var panel = Img("Panel", root, GameArt.Sprite("btn_Oct_20"), Color.white);
            Center(panel, PanelW, PanelH, 0, 0);

            var title = GameArt.Label("Text_Bold", panel, "COMMUNITY LEADERBOARD", 50, Ink,
                                      TextAlignmentOptions.MidlineLeft);
            NativeHistoryScreen.Place(title.rectTransform, 0, 1, 0, 1, 80, -44, 1200, 64);

            _subtitle = GameArt.Label("Text_Regular", panel, "", 30, InkDim, TextAlignmentOptions.MidlineRight);
            NativeHistoryScreen.Place(_subtitle.rectTransform, 1, 1, 1, 1, -80, -50, 1000, 44);

            var rule = Img("Rule", panel, null, Hairline);
            NativeHistoryScreen.Place(rule, 0.5f, 1, 0.5f, 1, 0, -122, RowW, 2);

            // Column headings, in the row's own coordinates.
            var head = new GameObject("Head", typeof(RectTransform));
            head.transform.SetParent(panel, false);
            NativeHistoryScreen.Place((RectTransform)head.transform, 0.5f, 1, 0.5f, 1, 0, -136, RowW, 40);
            Col(head.transform, "#", 30, 70, TextAlignmentOptions.MidlineLeft);
            Col(head.transform, "PLAYER", 215, 600, TextAlignmentOptions.MidlineLeft);
            Col(head.transform, "LEAGUE", 820, 300, TextAlignmentOptions.MidlineLeft);
            Col(head.transform, "ELO", 1150, 200, TextAlignmentOptions.MidlineRight);
            Col(head.transform, "RECORD", 1390, 220, TextAlignmentOptions.MidlineRight);
            Col(head.transform, "WIN %", 1630, 130, TextAlignmentOptions.MidlineRight);

            var body = new GameObject("Body", typeof(RectTransform));
            body.transform.SetParent(panel, false);
            NativeHistoryScreen.Place((RectTransform)body.transform, 0.5f, 1, 0.5f, 1, 0, -BodyTop,
                                      RowW, VisibleRows * (RowH + RowGap));
            _body = body.transform;

            _empty = GameArt.Label("Text_Regular", panel, "", 34, InkDim, TextAlignmentOptions.Top);
            NativeHistoryScreen.Place(_empty.rectTransform, 0.5f, 1, 0.5f, 1, 0, -420, 1400, 200);
            _empty.enableWordWrapping = true;

            var footRule = Img("FootRule", panel, null, Hairline);
            NativeHistoryScreen.Place(footRule, 0.5f, 0, 0.5f, 0, 0, 108, RowW, 2);

            _foot = GameArt.Label("Text_Regular", panel, "", 26, InkDim, TextAlignmentOptions.MidlineLeft);
            NativeHistoryScreen.Place(_foot.rectTransform, 0, 0, 0, 0, 80, 62, 1500, 40);

            _status = GameArt.Label("Text_Regular", panel, "", 26, InkDim, TextAlignmentOptions.MidlineRight);
            NativeHistoryScreen.Place(_status.rectTransform, 1, 0, 1, 0, -80, 62, 700, 40);
        }

        private static void Col(Transform parent, string text, float x, float w, TextAlignmentOptions align)
        {
            var l = GameArt.Label("Text_Regular", parent, text, 24, InkDim, align);
            l.characterSpacing = 6f;
            NativeHistoryScreen.Place(l.rectTransform, 0, 0.5f, 0, 0.5f, x, 0, w, 36);
        }

        // -----------------------------------------------------------------------------------

        private void Populate()
        {
            Build();
            foreach (var r in _rows) if (r != null) UnityEngine.Object.Destroy(r);
            _rows.Clear();
            if (_pinned != null) { UnityEngine.Object.Destroy(_pinned.gameObject); _pinned = null; }
            _pending.Clear();

            var st = Board != null ? Board.State : null;
            _rendered = st;

            if (st == null || st.Players.Count == 0)
            {
                _empty.text = Board == null || string.IsNullOrEmpty(Board.ApiBase)
                    ? "No leaderboard server is configured."
                    : (Board.Busy ? "Loading..."
                       : !string.IsNullOrEmpty(Board.LastError) ? "Could not reach the leaderboard.\n" + Board.LastError
                       : "No one has reached Master this season yet.");
                UpdateStatus();
                return;
            }
            _empty.text = "";

            string myId = Board != null ? Board.PlayerId : "";
            float y = 0f;
            bool meShown = false;
            for (int i = 0; i < st.Players.Count && i < VisibleRows; i++)
            {
                var p = st.Players[i];
                bool me = !string.IsNullOrEmpty(myId) && p.PlayerId == myId;
                meShown |= me;
                Row(_body, p, y, me, i % 2 == 0 ? RowA : RowB);
                y += RowH + RowGap;
            }

            // Your own row, pinned below the list when you are not in the visible part of it.
            if (!meShown && st.Me != null)
            {
                var host = new GameObject("Pinned", typeof(RectTransform));
                host.transform.SetParent(_body.parent, false);
                _pinned = (RectTransform)host.transform;
                NativeHistoryScreen.Place(_pinned, 0.5f, 1, 0.5f, 1, 0,
                                          -(BodyTop + VisibleRows * (RowH + RowGap) + 10f), RowW, RowH);
                Row(_pinned, st.Me, 0f, true, RowA);
            }

            UpdateStatus();
        }

        private void Row(Transform parent, BoardRow p, float y, bool me, Color bg)
        {
            var row = Img("Row", parent, GameArt.Sprite("btn_Oct_16"), me ? MeTint : bg);
            NativeHistoryScreen.Place(row, 0, 1, 0, 1, 0, -y, RowW, RowH);
            _rows.Add(row.gameObject);

            if (me)
            {
                var bar = Img("Accent", row, null, Accent);
                NativeHistoryScreen.Place(bar, 0, 0.5f, 0, 0.5f, 10, 0, 6, RowH - 24);
            }

            var rank = GameArt.Label("Text_Medium", row, p.Rank.ToString(), 32, me ? Accent : InkDim,
                                     TextAlignmentOptions.MidlineLeft);
            NativeHistoryScreen.Place(rank.rectTransform, 0, 0.5f, 0, 0.5f, 30, 0, 80, 44);

            Badge(row, p.Exp, 120);

            var name = GameArt.Label("Text_Medium", row, "", 34, Ink, TextAlignmentOptions.MidlineLeft);
            name.richText = true;
            name.text = Esc(p.DisplayName) +
                        (me ? "  <size=22><color=#" + ColorUtility.ToHtmlStringRGB(Accent) + ">YOU</color></size>" : "");
            NativeHistoryScreen.Place(name.rectTransform, 0, 0.5f, 0, 0.5f, 215, 0, 600, 44);

            var league = GameArt.Label("Text_Regular", row, LeagueTitle(p.Exp), 28, InkDim,
                                       TextAlignmentOptions.MidlineLeft);
            NativeHistoryScreen.Place(league.rectTransform, 0, 0.5f, 0, 0.5f, 820, 0, 300, 44);

            // ELO alone. Everyone on this board is in Master, where exp has stopped separating
            // players - one rank spans 550 to 15000 - so it is the only number left that ranks.
            var rating = GameArt.Label("Text_Medium", row, p.Elo > 0 ? p.Elo.ToString("N0") : "-",
                                       32, Ink, TextAlignmentOptions.MidlineRight);
            NativeHistoryScreen.Place(rating.rectTransform, 0, 0.5f, 0, 0.5f, 1150, 0, 200, 44);

            // "120-60": 120 wins, 60 losses - the matches played, as a record.
            var rec = GameArt.Label("Text_Medium", row, p.Record, 32, Ink, TextAlignmentOptions.MidlineRight);
            NativeHistoryScreen.Place(rec.rectTransform, 0, 0.5f, 0, 0.5f, 1390, 0, 220, 44);

            int played = p.Wins + p.Losses;
            string pct = played > 0 ? Mathf.RoundToInt(100f * p.Wins / played) + "%" : "-";
            var wr = GameArt.Label("Text_Regular", row, pct, 30, InkDim, TextAlignmentOptions.MidlineRight);
            NativeHistoryScreen.Place(wr.rectTransform, 0, 0.5f, 0, 0.5f, 1630, 0, 130, 44);

            // Flags are the board's honesty: they are shown, never hidden, and never block.
            float fx = 1790f;
            foreach (var f in p.Flags)
            {
                var label = FlagText(f);
                if (label == null) continue;
                var pill = Img("Flag", row, GameArt.Sprite("btn_Oct_16"), FlagBg);
                float w = 34f + label.Length * 12f;
                NativeHistoryScreen.Place(pill, 0, 0.5f, 0, 0.5f, fx, 0, w, 38);
                var t = GameArt.Label("Text_Regular", pill, label, 22, FlagInk, TextAlignmentOptions.Center);
                NativeHistoryScreen.Stretch(t.rectTransform);
                fx += w + 8f;
                if (fx > RowW - 60f) break;
            }
        }

        /// <summary>The league frame with the rank icon over it, both from the client's own bundle.</summary>
        private void Badge(Transform row, int exp, float x)
        {
            Season.League league; Season.Rank rank; int idx;
            if (Season == null || !Season.RankFor((uint)Math.Max(0, exp), out league, out rank, out idx)) return;

            var holder = new GameObject("Badge", typeof(RectTransform));
            holder.transform.SetParent(row, false);
            NativeHistoryScreen.Place((RectTransform)holder.transform, 0, 0.5f, 0.5f, 0.5f, x + 36f, 0, 76, 76);

            Layer(holder.transform, "Frame", league.Frame);
            Layer(holder.transform, "Icon", rank.Image);
        }

        private void Layer(Transform holder, string name, string asset)
        {
            if (string.IsNullOrEmpty(asset)) return;
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(holder, false);
            var raw = go.AddComponent<RawImage>();
            raw.raycastTarget = false;
            raw.color = new Color(1, 1, 1, 0);          // invisible until the art lands
            NativeHistoryScreen.Stretch((RectTransform)go.transform);

            var tex = ItemArt.Instance != null ? ItemArt.Instance.Get("leagueicons", asset) : null;
            if (tex != null) { raw.texture = tex; raw.color = Color.white; }
            else _pending.Add(new PendingArt { Bundle = "leagueicons", Asset = asset, Target = raw });
        }

        private void FillArt()
        {
            if (_pending.Count == 0 || ItemArt.Instance == null) return;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var p = _pending[i];
                if (p.Target == null) { _pending.RemoveAt(i); continue; }
                var tex = ItemArt.Instance.Get(p.Bundle, p.Asset);
                if (tex == null) continue;
                p.Target.texture = tex;
                p.Target.color = Color.white;
                _pending.RemoveAt(i);
            }
        }

        private string LeagueTitle(int exp)
        {
            Season.League league; Season.Rank rank; int idx;
            if (Season == null || !Season.RankFor((uint)Math.Max(0, exp), out league, out rank, out idx)) return "";
            return league.Title + " " + (idx + 1);
        }

        private void UpdateStatus()
        {
            var st = Board != null ? Board.State : null;
            var now = DateTime.UtcNow;

            // Season and countdown: the service's consensus end date if it has one, else our own
            // client's config - the same document, read locally.
            int seasonId = st != null && st.SeasonId > 0 ? st.SeasonId : (Season != null ? Season.Id : 0);
            DateTime? end = st != null ? Season.ParseUtc(st.EndDate) : null;
            if (!end.HasValue && Season != null) end = Season.EndUtc;
            string left = "";
            if (end.HasValue)
            {
                var tmp = new Season { EndUtc = end };
                left = tmp.TimeLeftText(now);
                left = left == "ended" ? "season ended" : "resets in " + left;
            }
            _subtitle.text = (seasonId > 0 ? "Season " + seasonId : "") +
                             (left.Length > 0 ? "   ·   " + left : "");

            if (st != null)
            {
                var age = st.FetchedUtc == default(DateTime) ? "" : Ago(now - st.FetchedUtc);
                _foot.text = "Master league, ranked by ELO   ·   " + st.Total +
                             (st.Total == 1 ? " player" : " players") +
                             (age.Length > 0 ? "   ·   updated " + age : "");
            }
            else _foot.text = "Master league, ranked by ELO";

            if (Board == null) { _status.text = ""; return; }
            if (!Board.Enabled) _status.text = "Not sharing your record - turn on Community Leaderboard in Settings";
            else if (string.IsNullOrEmpty(Board.EffectiveName)) _status.text = "Sharing as your in-game name once a match has been played";
            else if (!string.IsNullOrEmpty(Board.LastError)) _status.text = Board.LastError;
            else if (st != null && st.Me != null && !st.Me.Master)
                _status.text = "Sharing as " + Board.EffectiveName + "   ·   " +
                               LeagueTitle(st.Me.Exp) + " - the board ranks Master only";
            else _status.text = "Sharing as " + Board.EffectiveName +
                                (string.IsNullOrEmpty(Board.LastSubmit) ? "" : "   ·   last submit: " + Board.LastSubmit);
        }

        private static string Ago(TimeSpan t)
        {
            if (t.TotalSeconds < 90) return "just now";
            if (t.TotalMinutes < 90) return Mathf.RoundToInt((float)t.TotalMinutes) + " min ago";
            if (t.TotalHours < 36) return Mathf.RoundToInt((float)t.TotalHours) + " h ago";
            return Mathf.RoundToInt((float)t.TotalDays) + " d ago";
        }

        private static string FlagText(string f)
        {
            switch (f)
            {
                case "new": return "new";
                case "nonmonotonic": return "counters reset";
                case "impossible-rate": return "too fast";
                case "inconsistent": return "record mismatch";
                case "local-mismatch": return "unverified matches";
                default: return null;
            }
        }

        private static string Esc(string s)
        {
            return (s ?? "").Replace("<", "‹").Replace(">", "›");
        }

        // -----------------------------------------------------------------------------------

        private static RectTransform Img(string name, Transform parent, Sprite sprite, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            img.raycastTarget = false;
            if (sprite != null) img.type = Image.Type.Sliced;
            return (RectTransform)go.transform;
        }

        private static void Center(RectTransform rt, float w, float h, float x, float y)
        {
            NativeHistoryScreen.Place(rt, 0.5f, 0.5f, 0.5f, 0.5f, x, y, w, h);
        }
    }
}
