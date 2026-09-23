using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PtcglLeaderboard.Core
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

        /// <summary>Called when sharing is switched on or off, so the choice is written to disk.</summary>

        private static readonly Color Ink = new Color(0.16f, 0.17f, 0.20f, 1f);
        private static readonly Color InkDim = new Color(0.55f, 0.57f, 0.61f, 1f);
        private static readonly Color Accent = new Color(0.176f, 0.408f, 0.847f, 1f);
        // Strong enough to find at a glance. At 0.13 your own row was very nearly the same
        // white as every other row, which defeats the point of marking it.
        private static readonly Color MeTint = new Color(0.176f, 0.408f, 0.847f, 0.30f);
        private static readonly Color RowA = new Color(0.965f, 0.968f, 0.975f, 1f);
        private static readonly Color RowB = new Color(0.985f, 0.987f, 0.990f, 1f);
        private static readonly Color Hairline = new Color(0f, 0f, 0f, 0.10f);

        // A backdrop for the podium, and a floor for the blocks to stand on, so the three figures
        // read as being somewhere rather than cut out and pasted onto the panel.
        private static readonly Color StageBg = new Color(0.937f, 0.949f, 0.969f, 1f);
        private static readonly Color StageFloor = new Color(0.882f, 0.898f, 0.929f, 1f);

        // The heading row gets a solid fill rather than a hairline, so the table visibly starts
        // there instead of the first row appearing to float under the podium.
        private static readonly Color HeadBg = new Color(0.886f, 0.898f, 0.918f, 1f);

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

        // The panel fills what the screen actually has. Measured off a real 1920x1080 client:
        // the panel's own 2260 units drew 1696px, so a unit is 0.75px and the full canvas is
        // about 2560x1440 - of which the client's nav bar owns the top ~95. PanelDrop pushes the
        // centred panel below it, because Center() is symmetrical and would otherwise put the
        // title under the nav.
        // The panel takes the whole screen below the nav bar. Measured on a real 1920x1080
        // client: a unit draws at 0.75px, so the canvas is about 2560x1440 and the client's nav
        // owns the top ~95. Center() is symmetrical, so PanelDrop pushes it clear of the nav
        // rather than the panel being sized down to avoid it.
        private const float PanelW = 2480f, PanelH = 1334f, PanelDrop = -52f;
        private const float RowW = 2320f, RowH = 74f, RowGap = 6f;

        // The list runs in TWO columns of half-width rows, so a page holds twice as many
        // players in the same height - which is what paid for the podium taking a row. Dropping
        // the league name made room: everyone on this board is in Master by definition, so that
        // column repeated the same words down the whole page.
        private const float ListGap = 44f;
        private const float HalfW = (RowW - ListGap) / 2f;          // 1138

        // Column positions inside a half-width row. Written out rather than derived from the old
        // full-width ones, because halving the row is not a scale - the avatar and the rank do not
        // get smaller, so the text columns absorb all of the loss.
        // The flag pills are gone from the row, so their width goes back to the columns. They
        // were also the one thing here wide enough to overrun a half-width row - "unverified
        // matches" is 200 units on its own - and an overrun in a two-column list lands in the
        // NEXT column rather than in the margin.
        //
        // The flags themselves are untouched: still raised at write time, still stored, still
        // served by the board. This is the list not showing them, not the board forgetting them.
        private const float CRank = 18f, CBadge = 100f, CName = 180f, CNameW = 360f;
        private const float CElo = 560f, CEloW = 180f;
        private const float CRec = 760f, CRecW = 190f;
        private const float CWin = 970f, CWinW = 120f;

        // The podium takes the top three out of the list and shows them properly, so the page
        // still holds seven players - three on the podium and four in the list beneath it.
        // The podium starts at the very top. There is no title strip above it any more: the
        // heading moved into the gutter beside the columns, which is dead space the podium was
        // never going to use, and that bought the list its seventh row.
        // One row's worth of height moved from the list to the podium, which is most of what
        // makes the figures bigger: they are framed by height, so the band is the only thing that
        // really sets their size. Six rows now rather than seven - still derived, so the count
        // follows the arithmetic rather than being typed in.
        // 664 rather than 584: five rows a column instead of six. The list was leaving a band
        // of empty panel above the paging strip, and the figures are framed by height, so the
        // band is the only thing that really sets how big they are. Ten players a page now.
        private const float PodiumTop = 20f, PodiumH = 650f;

        // The tallest place's picture, and the shape they are all drawn at. Everything else on
        // the podium is measured from these two.
        //
        // There is no separate strip for the names any more - they are lettered onto the fronts
        // of the blocks, as part of the podium. That hands its height back to the picture, which
        // is roughly what the taller blocks cost, so the figures did not shrink to pay for it.
        private const float NameStrip = 0f;

        // How far up the front of a block the CENTRES of its three lines sit, measured from the
        // ground all three blocks share - so they line up across the podium however tall the block
        // behind them is. Fixed by the SHORTEST front face, which is third place's: everything has
        // to fit on that one, and the extra room on the taller blocks reads as height rather than
        // as space to fill.
        private const float EloUp = 26f, NameUp = 54f, NumeralUp = 88f;
        private const float LineBox = 38f;
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
        // The paging line is the bottom row of the panel: buttons, page label and the footer
        // note all sit on ONE line, centred on the same height, just above the panel's edge.
        private const float PagingTop = PanelH - 16f - PagingH;
        private const float PagingMid = PagingTop + PagingH * 0.5f;   // from the top
        private const float PagingUp = PanelH - PagingMid;              // the same line, from the bottom
        // No strip is reserved for your own row any more. Reserving one cost a row's height
        // on every page and sat empty whenever you were on the page - which is most of the
        // time - as a band of blank panel above the footer. When you are NOT on the page your
        // row takes the last slot of the right-hand column instead.
        private const float RowsSpace = PagingTop - 16f - BodyTop;
        private const int VisibleRows = (int)((RowsSpace + RowGap) / (RowH + RowGap));

        // The plaque is 1024x288 in the client's own art, and hangs under the season line on
        // the right. Its height is derived from that shape, so a differently-proportioned plaque
        // next set still sits where it should rather than being squashed to fit.
        private const float PlaqueW = 420f;
        private const float PlaqueH = PlaqueW * 288f / 1024f;
        // The two corners share one inset and one top line, so the heading on the left and the
        // plaque on the right sit level and equally far in from the backdrop's edges.
        private const float HeadInset = 110f, HeadTopY = 36f;

        private bool _built;
        private RectTransform _plaque;
        private Sprite _plaqueArt;
        private bool _plaqueLogged;
        private Transform _body, _podium;
        private readonly List<GameObject> _podiumArt = new List<GameObject>();
        private TextMeshProUGUI _subtitle, _foot, _empty;
        private readonly List<GameObject> _rows = new List<GameObject>();
        private BoardState _rendered;
        private float _nextTick;
        private readonly Pager _pager = new Pager(VisibleRows * 2);
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
            // The current season's expansion plaque, in the gutter above the heading. The name
            // comes from the season document's own banner asset - its prefix is the expansion -
            // so this follows the game from set to set instead of being pinned to one.
            //
            // It is only drawn if the client already has that sprite loaded. Nothing here pulls
            // an asset bundle for decoration, so on a session that has not been near the Shop
            // there is simply no plaque, and the heading sits where it always did.
            _plaque = Img("Plaque", panel, null, new Color(1f, 1f, 1f, 0f));
            NativeHistoryScreen.Place(_plaque, 1, 1, 1, 1, -HeadInset, -HeadTopY, PlaqueW, PlaqueH);
            // Kept above the season line by construction: the line is placed from the plaque's
            // own bottom edge, so a differently-shaped plaque next set pushes the text down
            // rather than landing on it.

            var title = GameArt.Label("Text_Bold", panel, "LEADERBOARD", 44, Ink,
                                      TextAlignmentOptions.TopLeft);
            // Inset from the backdrop's edge, not flush with it: the backdrop starts where the
            // rows do, so anything placed at the row margin sits right on its rounded corner.
            NativeHistoryScreen.Place(title.rectTransform, 0, 1, 0, 1, HeadInset, -HeadTopY, 560, 70);

            _subtitle = GameArt.Label("Text_Medium", panel, "", 28, Ink, TextAlignmentOptions.TopRight);
            _subtitle.enableWordWrapping = true;
            // Inset a little further than the plaque: right-aligned text reads as crowding the
            // edge at the same margin a solid image sits comfortably at.
            NativeHistoryScreen.Place(_subtitle.rectTransform, 1, 1, 1, 1, -(HeadInset + 24f),
                                      -(HeadTopY + PlaqueH + 14f), PlaqueW, 80);

            // Built BEFORE the podium so it sits behind it: this canvas draws in sibling order.
            var stage = Img("Stage", panel, GameArt.Sprite("btn_Oct_20"), StageBg);
            NativeHistoryScreen.Place(stage, 0.5f, 1, 0.5f, 1, 0, -PodiumTop + 12f, RowW, PodiumH + 4f);

            var floor = Img("Floor", panel, GameArt.Sprite("btn_Oct_16"), StageFloor);
            NativeHistoryScreen.Place(floor, 0.5f, 1, 0.5f, 1, 0, -(PodiumTop + PodiumH - 34f), RowW, 42f);

            // The heading was built before the backdrop, and this canvas draws in sibling order,
            // so without this the backdrop covers it.
            if (_plaque != null) _plaque.SetAsLastSibling();
            title.transform.SetAsLastSibling();
            _subtitle.transform.SetAsLastSibling();

            var podium = new GameObject("Podium", typeof(RectTransform));
            podium.transform.SetParent(panel, false);
            NativeHistoryScreen.Place((RectTransform)podium.transform, 0.5f, 1, 0.5f, 1, 0,
                                      -PodiumTop, RowW, PodiumH);
            _podium = podium.transform;

            var headBg = Img("HeadBg", panel, GameArt.Sprite("btn_Oct_16"), HeadBg);
            NativeHistoryScreen.Place(headBg, 0.5f, 1, 0.5f, 1, 0, -(HeadTop - 6f), RowW, HeadH + 12f);

            // Column headings, in the row's own coordinates.
            var head = new GameObject("Head", typeof(RectTransform));
            head.transform.SetParent(panel, false);
            NativeHistoryScreen.Place((RectTransform)head.transform, 0.5f, 1, 0.5f, 1, 0, -HeadTop, RowW, HeadH);
            for (int c = 0; c < 2; c++)
            {
                float x = c * (HalfW + ListGap);
                Col(head.transform, "#", x + CRank, 70, TextAlignmentOptions.MidlineLeft);
                Col(head.transform, "PLAYER", x + CName, CNameW, TextAlignmentOptions.MidlineLeft);
                Col(head.transform, "ELO", x + CElo, CEloW, TextAlignmentOptions.MidlineRight);
                Col(head.transform, "RECORD", x + CRec, CRecW, TextAlignmentOptions.MidlineRight);
                Col(head.transform, "WIN %", x + CWin, CWinW, TextAlignmentOptions.MidlineRight);
            }

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
            NativeHistoryScreen.Place(_foot.rectTransform, 0, 0, 0, 0.5f, 80, PagingUp, 780, 40);

            _pageLabel = GameArt.Label("Text_Regular", panel, "", 26, InkDim, TextAlignmentOptions.Center);
            NativeHistoryScreen.Place(_pageLabel.rectTransform, 0.5f, 1, 0.5f, 0.5f, 0, -PagingMid, 360, 40);
            _pagePrev = PageButton(panel, "PREV", -190, () => { if (_pager.Move(-1, Total())) Populate(); });
            _pageNext = PageButton(panel, "NEXT", 190, () => { if (_pager.Move(+1, Total())) Populate(); });
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

            // First place last, so it draws in FRONT of the two beside it. The blocks overlap
            // slightly to close the seam between them, and this canvas draws in sibling order -
            // so built in rank order, second and third were laid over the gold block's edges and
            // over the winner's outstretched arms.
            if (places > 0 && _podiumArt.Count > 0 && _podiumArt[0] != null)
                _podiumArt[0].transform.SetAsLastSibling();

            int total = Mathf.Max(0, st.Players.Count - places);
            int start = _pager.Start(total), shown = _pager.Count(total);
            for (int i = 0; i < shown; i++)
            {
                var p = st.Players[places + start + i];
                bool me = !string.IsNullOrEmpty(myId) && p.PlayerId == myId;
                meShown |= me;

                // Column-major: the left column runs 4..9 and the right 10..15, so reading down a
                // column follows the ranking. Filling left-to-right instead would put consecutive
                // ranks side by side and make the eye zigzag.
                int col = i / VisibleRows, slot = i % VisibleRows;
                Row(_body, p, col * (HalfW + ListGap), slot * (RowH + RowGap), me,
                    slot % 2 == 0 ? RowA : RowB);
            }
            y = 0f;

            SetPageButton(_pagePrev, _pager.CanPrev(total));
            SetPageButton(_pageNext, _pager.CanNext(total));
            if (_pageLabel != null) _pageLabel.text = _pager.Label(total);
            ShowPlaque();

            // Your own row, when you are not on this page: it takes the last slot of the
            // right-hand column, drawn after the list so it sits over whatever was there. On a
            // full page that costs one entry - the one you are least likely to be looking for,
            // given that you are looking for yourself.
            if (!meShown && st.Me != null)
                Row(_body, st.Me, HalfW + ListGap, (VisibleRows - 1) * (RowH + RowGap), true, RowA);

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
            // How far apart the three stand is DERIVED from how wide a block actually comes out
            // on screen - its world width times the scale this band is drawn at - with two units
            // of overlap so the join has no seam of panel showing through.
            //
            // Fixing that step at a number is what went wrong last time: growing the podium band
            // widens the blocks, because everything here is scaled to fit the band, and the step
            // stayed put. The three ended up overlapping by 56 units, far enough for first place
            // to cover the end of second place's name.

            // Block heights in WORLD units, not pixels: the block is a real cube in front of the
            // real camera, and the camera frames block plus figure. A taller block therefore
            // pushes its figure higher inside an image the same size as its neighbours', and
            // aligning the three images along their bottoms is what makes the steps.
            // Shorter than they were. The camera has to frame block plus figure plus enough
            // room above for raised arms, so every unit of block is a unit the figure does not
            // get - and the figure is the thing worth looking at.
            // All three the same height and the same shape. Stepping them made first place
            // biggest, but it also meant three different front faces to letter on, sized by the
            // shortest - so second and third carried more block than their text needed while the
            // figures paid for it. Level blocks give every figure the same, larger frame.
            const float BlockH = 0.65f;
            float[] blocks = { BlockH, BlockH, BlockH };

            float blockH = blocks[place];
            Color medal = Medal[place], dim = MedalDim[place];

            // Each place's picture is sized in PROPORTION to what its camera frames, so every
            // figure comes out at the same scale on screen and only the blocks differ. Sizing all
            // three pictures the same instead made third place's figure the LARGEST - its block
            // is the shortest, so the same picture height was spread over less world - which
            // quietly argued against the ranking the podium exists to show.
            float pxPerUnit = ImgHMax / (PodiumAvatars.FramedHeight + blocks[0]);
            float colStep = pxPerUnit * PodiumAvatars.BlockWidth - 2f;
            float colW = colStep - 30f;                  // the name is lettered ON the block
            float[] xs = { 0f, -colStep, colStep };
            float imgH = pxPerUnit * (PodiumAvatars.FramedHeight + blockH);
            float imgW = imgH * ImgAspect;

            float x = xs[place];
            var col = new GameObject("Place" + (place + 1), typeof(RectTransform));
            col.transform.SetParent(_podium, false);
            NativeHistoryScreen.Place((RectTransform)col.transform, 0.5f, 0, 0.5f, 0, x, 0, colStep, PodiumH);
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

                // The numerals are placed from the GROUND, at one height and one size for all
                // three, so they line up across the podium. Sizing and centring each one on its
                // own block instead put them at three different heights in three different
                // sizes, and on the shortest block the numeral rode up onto the top face - a
                // block seen from above shows that face across its upper third, so "centred on
                // the block" is not on the face you are reading.
                var num = GameArt.Label("Text_Bold", t, (p.Rank > 0 ? p.Rank : place + 1).ToString(),
                                        36, Color.white, TextAlignmentOptions.Center);
                // Place() positions by the PIVOT, and a bottom pivot means y is the box's bottom
                // edge - so a 40-tall box placed at 16 put the glyph's centre at 36, which on
                // third place's short front face was right at its top edge. Half the box height
                // comes off so that NumeralUp means what it says.
                NativeHistoryScreen.Place(num.rectTransform, 0.5f, 0, 0.5f, 0, 0,
                                          NumeralUp - LineBox * 0.5f, 240, LineBox);
            }
            else
            {
                // No outfit to build from, so a flat plate in the medal colour carrying whatever
                // likeness we do have. Same footprint, so the row of three still lines up.
                float plateH = imgH * 0.55f;
                var ring = Img("Ring", t, GameArt.Sprite("btn_Oct_16"), medal);
                NativeHistoryScreen.Place(ring, 0.5f, 0, 0.5f, 0, 0, imgH * 0.30f,
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
                // Matches the real blocks' proportions, so a place with no outfit still lines up
                // with the two beside it.
                NativeHistoryScreen.Place(block, 0.5f, 0, 0.5f, 0, 0, 0, colW + 16f, NumeralUp + 24f);
                var num = GameArt.Label("Text_Bold", block, (p.Rank > 0 ? p.Rank : place + 1).ToString(),
                                        34, Color.white, TextAlignmentOptions.Center);
                NativeHistoryScreen.Place(num.rectTransform, 0.5f, 0, 0.5f, 0, 0,
                                          NumeralUp - LineBox * 0.5f, 240, LineBox);
            }

            // White on the block, not grey under it: these are lettered onto the podium now, so
            // they take the block's own ink rather than the panel's.
            var elo = GameArt.Label("Text_Medium", t, p.Elo > 0 ? p.Elo.ToString("N0") : "-",
                                    24, new Color(1f, 1f, 1f, 0.85f), TextAlignmentOptions.Center);
            NativeHistoryScreen.Place(elo.rectTransform, 0.5f, 0, 0.5f, 0, 0,
                                      EloUp - LineBox * 0.5f, colW, LineBox);

            var name = GameArt.Label("Text_Medium", t, "", 30, Color.white,
                                     TextAlignmentOptions.Center);
            name.richText = true;
            name.text = Esc(p.DisplayName) +
                        (me ? "  <size=18>(YOU)</size>" : "");
            NativeHistoryScreen.Place(name.rectTransform, 0.5f, 0, 0.5f, 0, 0,
                                      NameUp - LineBox * 0.5f, colW, LineBox);
        }

        private void Row(Transform parent, BoardRow p, float x, float y, bool me, Color bg)
        {
            var row = Img("Row", parent, GameArt.Sprite("btn_Oct_16"), me ? MeTint : bg);
            NativeHistoryScreen.Place(row, 0, 1, 0, 1, x, -y, HalfW, RowH);
            _rows.Add(row.gameObject);

            // No accent bar down the edge of your own row: the row's blue tint and the YOU tag
            // already say which one is yours, and the bar sat right in front of the name.

            // Rank 0 means unranked - below Master - so show a dash rather than a position that
            // does not exist. "0" read as a real standing, and a very bad one.
            var rank = GameArt.Label("Text_Medium", row, p.Rank > 0 ? p.Rank.ToString() : "–",
                                     28, me ? Accent : InkDim, TextAlignmentOptions.MidlineLeft);
            NativeHistoryScreen.Place(rank.rectTransform, 0, 0.5f, 0, 0.5f, CRank, 0, 70, 44);

            Badge(row, p.Exp, CBadge);

            var name = GameArt.Label("Text_Medium", row, "", 30, Ink, TextAlignmentOptions.MidlineLeft);
            name.richText = true;
            name.text = Esc(p.DisplayName) +
                        (me ? "  <size=20><color=#" + ColorUtility.ToHtmlStringRGB(Accent) + ">YOU</color></size>" : "");
            NativeHistoryScreen.Place(name.rectTransform, 0, 0.5f, 0, 0.5f, CName, 0, CNameW, 44);

            // ELO alone. Everyone on this board is in Master, where exp has stopped separating
            // players - one rank spans 550 to 15000 - so it is the only number left that ranks.
            var rating = GameArt.Label("Text_Medium", row, p.Elo > 0 ? p.Elo.ToString("N0") : "-",
                                       28, Ink, TextAlignmentOptions.MidlineRight);
            NativeHistoryScreen.Place(rating.rectTransform, 0, 0.5f, 0, 0.5f, CElo, 0, CEloW, 44);

            // "120-60": 120 wins, 60 losses - the matches played, as a record.
            var rec = GameArt.Label("Text_Medium", row, p.Record, 28, Ink, TextAlignmentOptions.MidlineRight);
            NativeHistoryScreen.Place(rec.rectTransform, 0, 0.5f, 0, 0.5f, CRec, 0, CRecW, 44);

            int played = p.Wins + p.Losses;
            string pct = played > 0 ? Mathf.RoundToInt(100f * p.Wins / played) + "%" : "-";
            var wr = GameArt.Label("Text_Regular", row, pct, 26, InkDim, TextAlignmentOptions.MidlineRight);
            NativeHistoryScreen.Place(wr.rectTransform, 0, 0.5f, 0, 0.5f, CWin, 0, CWinW, 44);

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

        private static string Ago(TimeSpan t)
        {
            if (t.TotalSeconds < 90) return "just now";
            if (t.TotalMinutes < 90) return Mathf.RoundToInt((float)t.TotalMinutes) + " min ago";
            if (t.TotalHours < 36) return Mathf.RoundToInt((float)t.TotalHours) + " h ago";
            return Mathf.RoundToInt((float)t.TotalDays) + " d ago";
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


        /// <summary>Show the season's plaque, if the client happens to have that art loaded.</summary>
        private void ShowPlaque()
        {
            if (_plaque == null) return;
            var img = _plaque.GetComponent<Image>();
            if (img == null) return;
            var name = Season != null ? Season.PlaqueAsset : "";
            // Looked up fresh, NOT through GameArt.Sprite: that builds its index once and keeps
            // it, so a sprite the client loads later - and the expansion art only loads when
            // something has shown it - stays invisible to it forever.
            var sprite = _plaqueArt;
            if (sprite == null && !string.IsNullOrEmpty(name))
            {
                foreach (var sp in Resources.FindObjectsOfTypeAll<Sprite>())
                    if (sp != null && sp.name == name) { sprite = _plaqueArt = sp; break; }
            }
            img.sprite = sprite;
            if (!_plaqueLogged)
            {
                _plaqueLogged = true;
                Plugin.Log.LogWarning("plaque: banner=" +
                                      (Season != null ? Season.BannerAsset : "(no season)") +
                                      " asset=" + name + " found=" + (sprite != null));
            }
            img.type = Image.Type.Simple;
            img.color = sprite == null ? new Color(1f, 1f, 1f, 0f) : Color.white;
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
                left = left == "ended" ? "season ended" : left + " left";
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
        }


        private string LeagueTitle(int exp)
        {
            Season.League league; Season.Rank rank; int idx;
            if (Season == null || !Season.RankFor((uint)Math.Max(0, exp), out league, out rank, out idx)) return "";
            return league.Title + " " + (idx + 1);
        }

    }
}
