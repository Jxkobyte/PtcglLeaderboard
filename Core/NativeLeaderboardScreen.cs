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
        public MatchHistory History;

        private static readonly Color Ink = new Color(0.16f, 0.17f, 0.20f, 1f);
        private static readonly Color InkDim = new Color(0.55f, 0.57f, 0.61f, 1f);
        private static readonly Color Accent = new Color(0.176f, 0.408f, 0.847f, 1f);
        private static readonly Color MeTint = new Color(0.176f, 0.408f, 0.847f, 0.13f);
        private static readonly Color RowA = new Color(0.965f, 0.968f, 0.975f, 1f);
        private static readonly Color RowB = new Color(0.985f, 0.987f, 0.990f, 1f);
        private static readonly Color Hairline = new Color(0f, 0f, 0f, 0.10f);

        // Medal colours for the podium, and a darker shade of each for the plinth's face so the
        // numeral on it stays legible against the lighter top.
        private static readonly Color[] Medal =
        {
            new Color(0.839f, 0.678f, 0.220f, 1f),   // gold
            new Color(0.686f, 0.718f, 0.753f, 1f),   // silver
            new Color(0.729f, 0.478f, 0.259f, 1f),   // bronze
        };
        private static readonly Color[] MedalDim =
        {
            new Color(0.949f, 0.906f, 0.784f, 1f),
            new Color(0.925f, 0.933f, 0.945f, 1f),
            new Color(0.941f, 0.882f, 0.827f, 1f),
        };
        private static readonly Color FlagBg = new Color(0.984f, 0.941f, 0.824f, 1f);
        private static readonly Color FlagInk = new Color(0.54f, 0.43f, 0.12f, 1f);

        // The panel fills what the screen actually has. Measured off a real 1920x1080 client:
        // the panel's own 2260 units drew 1696px, so a unit is 0.75px and the full canvas is
        // about 2560x1440 - of which the client's nav bar owns the top ~95. PanelDrop pushes the
        // centred panel below it, because Center() is symmetrical and would otherwise put the
        // title under the nav.
        // The panel takes the whole screen below the nav bar. Measured on a real 1920x1080
        // client: a unit draws at 0.75px, so the canvas is about 2560x1440 and the client's nav
        // owns the top ~95. Center() is symmetrical, so PanelDrop pushes it clear of the nav
        // rather than the panel being sized down to avoid it.
        private const float PanelW = 2480f, PanelH = 1310f, PanelDrop = -40f;
        private const float RowW = 2320f, RowH = 74f, RowGap = 6f;

        // Every column below was placed for a 2100-wide row. Rather than retype them, the
        // right-hand group moves by exactly the extra width and the middle takes a share, so a
        // wider row widens the gaps evenly instead of stranding the numbers mid-row.
        private const float Shift = RowW - 2100f;

        // The podium takes the top three out of the list and shows them properly, so the page
        // still holds seven players - three on the podium and four in the list beneath it.
        // The podium starts at the very top. There is no title strip above it any more: the
        // heading moved into the gutter beside the columns, which is dead space the podium was
        // never going to use, and that bought the list its seventh row.
        private const float PodiumTop = 20f, PodiumH = 480f;

        // The tallest place's picture, and the shape they are all drawn at. Everything else on the
        // podium is measured from these two. NameStrip is what the name and rating take above it.
        private const float NameStrip = 86f;
        private const float ImgHMax = PodiumH - NameStrip, ImgAspect = 452f / 516f;
        private const int PodiumPlaces = 3;

        // The headings are placed by their TOP edge, so the rows have to clear the whole 40 of
        // them plus a gap - measuring from the heading's top put the first row through the
        // middle of them.
        private const float HeadH = 40f;
        private const float HeadTop = PodiumTop + PodiumH + 20f;
        private const float BodyTop = HeadTop + HeadH + 12f;

        // Derived BOTTOM-UP from the footer rule, so the row count is whatever actually fits
        // rather than a number counted once by hand. The strip used to be drawn through the
        // pinned row when the count and the arithmetic disagreed; now they cannot.
        // One strip along the bottom carries paging in the middle and the footer notes at
        // either end, rather than stacking a rule, a footer line and a paging line. Three strips
        // cost over 100 units of height for text that fits beside the buttons.
        private const float PagingH = 52f;
        private const float PagingTop = PanelH - 34f - PagingH;
        private const float PinnedTop = PagingTop - 16f - RowH;
        private const float RowsSpace = PinnedTop - 8f - BodyTop;
        private const int VisibleRows = (int)((RowsSpace + RowGap) / (RowH + RowGap));

        private bool _built;
        private Transform _body, _podium;
        private readonly List<GameObject> _podiumArt = new List<GameObject>();
        private RectTransform _pinned;
        private TextMeshProUGUI _subtitle, _foot, _status, _empty;
        private readonly List<GameObject> _rows = new List<GameObject>();
        private BoardState _rendered;
        private float _nextTick;
        private readonly Pager _pager = new Pager(VisibleRows);
        private TextMeshProUGUI _pagePrev, _pageNext, _pageLabel;

        private class PendingArt { public string Bundle, Asset; public RawImage Target; }
        private readonly List<PendingArt> _pending = new List<PendingArt>();

        public override bool DeferReconnectRequest { get { return true; } }
        public override ScreenType screenType { get { return ScreenType.None; } }

        /// <summary>
        /// Give the shared avatar groups back when the screen closes.
        ///
        /// The podium borrows the client's own player/opponent/professor avatars, so leaving
        /// without restoring would show whoever topped the leaderboard on the profile screen.
        /// </summary>
        public override void OnDeactivate(HUBGroupController nextGroup)
        {
            try { PodiumAvatars.Release(this); }
            catch (Exception e) { Plugin.Log.LogWarning("leaderboard: " + e.Message); }
            base.OnDeactivate(nextGroup);
        }

        public override void OnActivate(HUBGroupController prevGroup)
        {
            base.OnActivate(prevGroup);
            try
            {
                Build();
                _pager.Reset();
                // Reads and reports our own outfit once, so it is obvious from the log whether we
                // can put a figure of ourselves on other people's podiums - a silent nothing is
                // indistinguishable from a broken read.
                OutfitCode.Mine();
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
            Center(panel, PanelW, PanelH, 0, PanelDrop);

            // The heading sits BESIDE the podium, in the gutter its columns leave empty, and
            // wraps onto two lines to fit there. A full-width title strip across the top cost
            // about 140 units of height for one line of text, and that was the difference
            // between six rows in the list and seven.
            var title = GameArt.Label("Text_Bold", panel, "COMMUNITY\nLEADERBOARD", 42, Ink,
                                      TextAlignmentOptions.TopLeft);
            title.enableWordWrapping = true;
            title.lineSpacing = -14f;
            NativeHistoryScreen.Place(title.rectTransform, 0, 1, 0, 1, 76, -34, 420, 130);

            _subtitle = GameArt.Label("Text_Regular", panel, "", 28, InkDim, TextAlignmentOptions.TopRight);
            _subtitle.enableWordWrapping = true;
            NativeHistoryScreen.Place(_subtitle.rectTransform, 1, 1, 1, 1, -76, -36, 420, 120);

            var podium = new GameObject("Podium", typeof(RectTransform));
            podium.transform.SetParent(panel, false);
            NativeHistoryScreen.Place((RectTransform)podium.transform, 0.5f, 1, 0.5f, 1, 0,
                                      -PodiumTop, RowW, PodiumH);
            _podium = podium.transform;

            // Column headings, in the row's own coordinates.
            var head = new GameObject("Head", typeof(RectTransform));
            head.transform.SetParent(panel, false);
            NativeHistoryScreen.Place((RectTransform)head.transform, 0.5f, 1, 0.5f, 1, 0, -HeadTop, RowW, HeadH);
            Col(head.transform, "#", 30, 70, TextAlignmentOptions.MidlineLeft);
            Col(head.transform, "PLAYER", 215, 600 + Shift * 0.3f, TextAlignmentOptions.MidlineLeft);
            Col(head.transform, "LEAGUE", 820 + Shift * 0.3f, 300, TextAlignmentOptions.MidlineLeft);
            Col(head.transform, "ELO", 1150 + Shift, 200, TextAlignmentOptions.MidlineRight);
            Col(head.transform, "RECORD", 1390 + Shift, 220, TextAlignmentOptions.MidlineRight);
            Col(head.transform, "WIN %", 1630 + Shift, 130, TextAlignmentOptions.MidlineRight);

            var body = new GameObject("Body", typeof(RectTransform));
            body.transform.SetParent(panel, false);
            NativeHistoryScreen.Place((RectTransform)body.transform, 0.5f, 1, 0.5f, 1, 0, -BodyTop,
                                      RowW, VisibleRows * (RowH + RowGap));
            _body = body.transform;

            _empty = GameArt.Label("Text_Regular", panel, "", 34, InkDim, TextAlignmentOptions.Top);
            NativeHistoryScreen.Place(_empty.rectTransform, 0.5f, 1, 0.5f, 1, 0, -420, 1400, 200);
            _empty.enableWordWrapping = true;

            // The footer notes share the paging line, at either end of it. PREV and NEXT reach
            // 325 either side of centre, so 340 is where text can start without running into
            // them.
            _foot = GameArt.Label("Text_Regular", panel, "", 26, InkDim, TextAlignmentOptions.MidlineLeft);
            NativeHistoryScreen.Place(_foot.rectTransform, 0, 0, 0, 0, 80, 60, 780, 40);

            _status = GameArt.Label("Text_Regular", panel, "", 26, InkDim, TextAlignmentOptions.MidlineRight);
            NativeHistoryScreen.Place(_status.rectTransform, 1, 0, 1, 0, -80, 60, 780, 40);


            _pageLabel = GameArt.Label("Text_Regular", panel, "", 26, InkDim, TextAlignmentOptions.Center);
            NativeHistoryScreen.Place(_pageLabel.rectTransform, 0.5f, 1, 0.5f, 1, 0, -PagingTop, 360, 40);
            _pagePrev = PageButton(panel, "PREV", -230, () => { if (_pager.Move(-1, Total())) Populate(); });
            _pageNext = PageButton(panel, "NEXT", 230, () => { if (_pager.Move(+1, Total())) Populate(); });
        }

        /// <summary>How many players the LIST pages over - the board minus the podium.</summary>
        private int Total()
        {
            var st = Board != null ? Board.State : null;
            if (st == null) return 0;
            return Mathf.Max(0, st.Players.Count - Mathf.Min(PodiumPlaces, st.Players.Count));
        }

        private TextMeshProUGUI PageButton(Transform panel, string text, float x, Action onClick)
        {
            var btn = Img("Page" + text, panel, GameArt.Sprite("btn_Oct_16"),
                          new Color(0.93f, 0.94f, 0.95f, 1f));
            NativeHistoryScreen.Place(btn, 0.5f, 1, 0.5f, 1, x, -PagingTop, 190, 52);
            var label = GameArt.Label("Text_MediumItalic", btn, text, InkDim,
                                      TextAlignmentOptions.Center, 26);
            NativeHistoryScreen.Stretch(label.rectTransform);
            // Img() turns raycasting off, which is right for decoration and fatal for a button.
            var img = btn.GetComponent<Image>();
            img.raycastTarget = true;
            var b = btn.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            b.onClick.AddListener(() => onClick());
            return label;
        }

        /// <summary>A page button that cannot go anywhere is dimmed, not hidden.</summary>
        private static void SetPageButton(TextMeshProUGUI label, bool enabled)
        {
            if (label == null) return;
            label.color = enabled ? InkDim : new Color(0.80f, 0.81f, 0.83f, 1f);
            var btn = label.transform.parent != null
                ? label.transform.parent.GetComponent<Button>() : null;
            if (btn != null) btn.interactable = enabled;
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
            foreach (var a in _podiumArt) if (a != null) UnityEngine.Object.Destroy(a);
            _podiumArt.Clear();
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
                SetPageButton(_pagePrev, false);
                SetPageButton(_pageNext, false);
                if (_pageLabel != null) _pageLabel.text = "";
                UpdateStatus();
                return;
            }
            _empty.text = "";

            string myId = Board != null ? Board.PlayerId : "";
            float y = 0f;
            bool meShown = false;

            // The top three come out of the list and onto the podium, so nobody is shown twice.
            int places = Mathf.Min(PodiumPlaces, st.Players.Count);
            for (int i = 0; i < places; i++)
            {
                var p = st.Players[i];
                bool me = !string.IsNullOrEmpty(myId) && p.PlayerId == myId;
                meShown |= me;
                Plinth(p, i, me);
            }

            int total = Mathf.Max(0, st.Players.Count - places);
            int start = _pager.Start(total), shown = _pager.Count(total);
            for (int i = start; i < start + shown; i++)
            {
                var p = st.Players[places + i];
                bool me = !string.IsNullOrEmpty(myId) && p.PlayerId == myId;
                meShown |= me;
                Row(_body, p, y, me, i % 2 == 0 ? RowA : RowB);
                y += RowH + RowGap;
            }

            SetPageButton(_pagePrev, _pager.CanPrev(total));
            SetPageButton(_pageNext, _pager.CanNext(total));
            if (_pageLabel != null) _pageLabel.text = _pager.Label(total);

            // Your own row, pinned below the list when you are not in the visible part of it.
            if (!meShown && st.Me != null)
            {
                var host = new GameObject("Pinned", typeof(RectTransform));
                host.transform.SetParent(_body.parent, false);
                _pinned = (RectTransform)host.transform;
                NativeHistoryScreen.Place(_pinned, 0.5f, 1, 0.5f, 1, 0, -PinnedTop, RowW, RowH);
                Row(_pinned, st.Me, 0f, true, RowA);
            }

            UpdateStatus();
        }

        /// <summary>
        /// One place on the podium: a plinth carrying the position and the rating, the player
        /// standing on it, and their name above.
        ///
        /// Laid out bottom-up from the plinth's base, because that is the one edge all three
        /// columns share - first place is simply a taller block, and everything above it rides up
        /// with it rather than being positioned separately per place.
        ///
        /// The likeness is whatever we captured the last time we watched that player's game, so a
        /// stranger has none: an avatar is a 3D model the client renders live, not a picture, and
        /// there is nothing to draw for someone whose match we never saw. That case gets their
        /// initial on the medal colour, which reads as deliberate rather than broken.
        /// </summary>
        private void Plinth(BoardRow p, int place, bool me)
        {
            if (_podium == null || place < 0 || place >= PodiumPlaces) return;

            // Second, first, third - left to right. First place stands in the middle and highest,
            // which is the whole reason a podium reads as a ranking without being labelled.
            const float ColW = 520f;
            float[] xs = { 0f, -ColW, ColW };

            // Block heights in WORLD units, not pixels: the block is a real cube in front of the
            // real camera, and the camera frames block plus figure. A taller block therefore
            // pushes its figure higher inside an image the same size as its neighbours', and
            // aligning the three images along their bottoms is what makes the steps.
            // Shorter than they were. The camera has to frame block plus figure plus enough
            // room above for raised arms, so every unit of block is a unit the figure does not
            // get - and the figure is the thing worth looking at.
            float[] blocks = { 0.62f, 0.44f, 0.30f };

            float x = xs[place], blockH = blocks[place];
            Color medal = Medal[place], dim = MedalDim[place];

            // Each place's picture is sized in PROPORTION to what its camera frames, so every
            // figure comes out at the same scale on screen and only the blocks differ. Sizing all
            // three pictures the same instead made third place's figure the LARGEST - its block
            // is the shortest, so the same picture height was spread over less world - which
            // quietly argued against the ranking the podium exists to show.
            float pxPerUnit = ImgHMax / (PodiumAvatars.FramedHeight + blocks[0]);
            float imgH = pxPerUnit * (PodiumAvatars.FramedHeight + blockH);
            float imgW = imgH * ImgAspect;

            var col = new GameObject("Place" + (place + 1), typeof(RectTransform));
            col.transform.SetParent(_podium, false);
            NativeHistoryScreen.Place((RectTransform)col.transform, 0.5f, 0, 0.5f, 0, x, 0, ColW, PodiumH);
            _podiumArt.Add(col);
            Transform t = col.transform;

            // Best first: a live 3D figure standing on a real block, built from the outfit the
            // board carries. Then the still we photographed the last time we watched them play.
            // Then their initial.
            var outfit = OutfitCode.Parse(p.Outfit);
            var tex = outfit != null ? null : Avatars.ForPlayer(History, p.DisplayName, me);

            if (outfit != null)
            {
                var stage = new GameObject("Figure", typeof(RectTransform), typeof(RawImage));
                stage.transform.SetParent(t, false);
                var raw = stage.GetComponent<RawImage>();
                raw.raycastTarget = false;
                raw.color = new Color(1f, 1f, 1f, 0f);       // until its camera has something to show
                NativeHistoryScreen.Place((RectTransform)stage.transform, 0.5f, 0, 0.5f, 0,
                                          0, 0, imgW, imgH);
                PodiumAvatars.Show(this, place, raw, outfit, OutfitCode.IsMale(p.Outfit),
                                   blockH, medal);

                // How much of the image the block occupies, from the same arithmetic the camera
                // was framed with - the block's share is its own height over the whole framed
                // height. That is what puts the position numeral on the block rather than across
                // the figure's shins, and it follows the headroom automatically.
                float blockPx = pxPerUnit * blockH;
                var num = GameArt.Label("Text_Bold", t, (p.Rank > 0 ? p.Rank : place + 1).ToString(),
                                        Mathf.RoundToInt(Mathf.Clamp(blockPx * 0.46f, 30f, 60f)),
                                        Color.white, TextAlignmentOptions.Center);
                // Low on the block, not centred on it: a block seen from slightly above shows
                // its TOP face across the upper third, so a numeral centred on the block's full
                // height sits on the top face rather than on the face you are reading.
                NativeHistoryScreen.Place(num.rectTransform, 0.5f, 0, 0.5f, 0, 0,
                                          blockPx * 0.30f, 240, blockPx * 0.56f);
            }
            else
            {
                // No outfit to build from, so a flat plate in the medal colour carrying whatever
                // likeness we do have. Same footprint, so the row of three still lines up.
                float plateH = imgH * 0.55f;
                var ring = Img("Ring", t, GameArt.Sprite("btn_Oct_16"), medal);
                NativeHistoryScreen.Place(ring, 0.5f, 0, 0.5f, 0, 0, imgH * 0.2f,
                                          plateH * 0.92f + 12f, plateH + 12f);

                var plate = Img("Plate", ring, GameArt.Sprite("btn_Oct_16"), dim);
                NativeHistoryScreen.Place(plate, 0.5f, 0.5f, 0.5f, 0.5f, 0, 0, plateH * 0.92f, plateH);

                if (tex != null)
                {
                    var face = new GameObject("Face", typeof(RectTransform), typeof(RawImage));
                    face.transform.SetParent(plate, false);
                    var raw = face.GetComponent<RawImage>();
                    raw.texture = tex;
                    raw.raycastTarget = false;
                    NativeHistoryScreen.Place((RectTransform)face.transform, 0.5f, 0.5f, 0.5f, 0.5f,
                                              0, 0, plateH * 0.92f - 8f, plateH - 8f);
                }
                else
                {
                    string initial = string.IsNullOrEmpty(p.DisplayName)
                        ? "?" : p.DisplayName.Substring(0, 1).ToUpperInvariant();
                    var ini = GameArt.Label("Text_Bold", plate, initial, 84, medal,
                                            TextAlignmentOptions.Center);
                    NativeHistoryScreen.Stretch(ini.rectTransform);
                }

                var block = Img("Block", t, GameArt.Sprite("btn_Oct_16"), medal);
                NativeHistoryScreen.Place(block, 0.5f, 0, 0.5f, 0, 0, 0, imgW, imgH * 0.22f);
                var num = GameArt.Label("Text_Bold", block, (p.Rank > 0 ? p.Rank : place + 1).ToString(),
                                        48, Color.white, TextAlignmentOptions.Center);
                NativeHistoryScreen.Stretch(num.rectTransform);
            }

            var elo = GameArt.Label("Text_Medium", t, p.Elo > 0 ? p.Elo.ToString("N0") : "-",
                                    30, InkDim, TextAlignmentOptions.Center);
            NativeHistoryScreen.Place(elo.rectTransform, 0.5f, 0, 0.5f, 0, 0, ImgHMax + 6, ColW, 32);

            var name = GameArt.Label("Text_Medium", t, "", 36, me ? Accent : Ink,
                                     TextAlignmentOptions.Center);
            name.richText = true;
            name.text = Esc(p.DisplayName) +
                        (me ? "  <size=20><color=#" + ColorUtility.ToHtmlStringRGB(Accent) + ">YOU</color></size>" : "");
            NativeHistoryScreen.Place(name.rectTransform, 0.5f, 0, 0.5f, 0, 0, ImgHMax + 40, ColW, 46);
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

            // Rank 0 means unranked - below Master - so show a dash rather than a position that
            // does not exist. "0" read as a real standing, and a very bad one.
            var rank = GameArt.Label("Text_Medium", row, p.Rank > 0 ? p.Rank.ToString() : "–",
                                     32, me ? Accent : InkDim, TextAlignmentOptions.MidlineLeft);
            NativeHistoryScreen.Place(rank.rectTransform, 0, 0.5f, 0, 0.5f, 30, 0, 80, 44);

            Badge(row, p.Exp, 120);

            var name = GameArt.Label("Text_Medium", row, "", 34, Ink, TextAlignmentOptions.MidlineLeft);
            name.richText = true;
            name.text = Esc(p.DisplayName) +
                        (me ? "  <size=22><color=#" + ColorUtility.ToHtmlStringRGB(Accent) + ">YOU</color></size>" : "");
            NativeHistoryScreen.Place(name.rectTransform, 0, 0.5f, 0, 0.5f, 215, 0, 600 + Shift * 0.3f, 44);

            var league = GameArt.Label("Text_Regular", row, LeagueTitle(p.Exp), 28, InkDim,
                                       TextAlignmentOptions.MidlineLeft);
            NativeHistoryScreen.Place(league.rectTransform, 0, 0.5f, 0, 0.5f, 820 + Shift * 0.3f, 0, 300, 44);

            // ELO alone. Everyone on this board is in Master, where exp has stopped separating
            // players - one rank spans 550 to 15000 - so it is the only number left that ranks.
            var rating = GameArt.Label("Text_Medium", row, p.Elo > 0 ? p.Elo.ToString("N0") : "-",
                                       32, Ink, TextAlignmentOptions.MidlineRight);
            NativeHistoryScreen.Place(rating.rectTransform, 0, 0.5f, 0, 0.5f, 1150 + Shift, 0, 200, 44);

            // "120-60": 120 wins, 60 losses - the matches played, as a record.
            var rec = GameArt.Label("Text_Medium", row, p.Record, 32, Ink, TextAlignmentOptions.MidlineRight);
            NativeHistoryScreen.Place(rec.rectTransform, 0, 0.5f, 0, 0.5f, 1390 + Shift, 0, 220, 44);

            int played = p.Wins + p.Losses;
            string pct = played > 0 ? Mathf.RoundToInt(100f * p.Wins / played) + "%" : "-";
            var wr = GameArt.Label("Text_Regular", row, pct, 30, InkDim, TextAlignmentOptions.MidlineRight);
            NativeHistoryScreen.Place(wr.rectTransform, 0, 0.5f, 0, 0.5f, 1630 + Shift, 0, 130, 44);

            // Flags are the board's honesty: they are shown, never hidden, and never block.
            float fx = 1790f + Shift;
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
                               LeagueTitle(st.Me.Exp) + " - Master only on the board";
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
