using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PrizeTracker.Core
{
    /// <summary>
    /// The Match History screen, built as a REAL screen in the client's own navigation system
    /// rather than an overlay drawn on top of it.
    ///
    /// Why this is not the overlay it replaces: the client's screens are HUBScreenControllers that
    /// live under MainCanvas/AllObjects/LetterboxObjects/InactiveScreens and get REPARENTED into
    /// ActiveScreens/CurrentScreenA|B by MainMenuNavigationScreenHolder.SetScreen when shown, with
    /// a DOTween fade/scale supplied by the navigation object. Anything drawn on a separate canvas
    /// is a different thing wearing the same colours: it misses the transition, renders above the
    /// HUD instead of below it, and has to reimplement tab highlighting by hand.
    ///
    /// HUBScreenController is concrete, so we can subclass it and the client drives the lifecycle.
    ///
    /// Everything is read from the live scene, never hardcoded: fonts by cloning a label that
    /// already uses the wanted font asset (which inherits its material too), and sprites by name
    /// from the client's own loaded set. See GameArt below.
    /// </summary>
    internal class NativeHistoryScreen : HUBScreenController, INativeScreen
    {
        public MatchHistory History;

        private readonly List<GameObject> _rows = new List<GameObject>();
        private Transform _body;
        private TextMeshProUGUI _summary, _empty;
        private readonly Pager _pager = new Pager(RowsPerPage);
        private TextMeshProUGUI _pagePrev, _pageNext, _pageLabel;
        private bool _built;

        // deck preview
        private GameObject _modal;
        private RectTransform _panel;
        private Transform _grid;
        private TextMeshProUGUI _modalTitle, _modalSub, _modalNote, _copyDeckLabel;
        private readonly List<GameObject> _tiles = new List<GameObject>();
        private MatchRecord _viewing;

        /// <summary>
        /// Art that has been ASKED FOR but has not arrived yet.
        ///
        /// CardArt.Get queues an asset-bundle load and returns null on the first call, so building
        /// a tile once at open time always renders the name-plate fallback and never goes back for
        /// the texture. Everything looked broken while the loads were in fact succeeding - the log
        /// showed a single failure out of a whole grid. These get filled in as they land.
        /// </summary>
        private class PendingArt
        {
            public string Key;
            public RectTransform Holder;
            public GameObject Fallback;
            public Rect Uv = FullUv;
            public bool IsItem;       // a sleeve/box/coin, loaded by ItemArt rather than CardArt
        }

        private static readonly Rect FullUv = new Rect(0, 0, 1, 1);

        /// <summary>
        /// The card within its square texture, taken from the CLIENT rather than worked out.
        ///
        /// Card textures are square (256x256 thumbnails, 1024x1024 full) and the card does not
        /// fill them: there is padding either side. The client's own card images read
        ///
        ///     rect 286x400  aspect 0.716  uv (x:0.14, y:0.00, w:0.72, h:1.00)  tex 256x256
        ///
        /// on every one sampled, at two different sizes - the middle 72% of the width at full
        /// height, drawn into a card-shaped rect. 0.72 x 1024 = 737, and 737/1024 = 0.720, which
        /// is a card.
        ///
        /// Both earlier attempts guessed instead of asking: fitting the square whole drew cards as
        /// squares, and squeezing the whole square into a card frame drew them narrow. The texture
        /// could not settle it either - it is fully opaque corner to corner, so the padding is not
        /// visible as padding, and measuring circles in the art kept picking up the card's
        /// background. The client had the answer available the whole time.
        /// </summary>
        private static readonly Rect CardUv = new Rect(0.14f, 0f, 0.72f, 1f);

        /// <summary>
        /// The front panel of a deck box within its texture.
        ///
        /// A deck box bundle holds the UV UNWRAP for the 3D box model, not a 2D picture: drawing it
        /// whole shows the front art squeezed into a strip with the wrap pattern and the top/bottom
        /// faces laid out around it. Every box is the same model with a different texture, so the
        /// layout is fixed and one crop serves all of them. Measured off a rendered box rather than
        /// derived from the model, so it is close rather than exact.
        /// </summary>
        private static readonly Rect DeckBoxFrontUv = new Rect(0.313f, 0.100f, 0.432f, 0.805f);

        private readonly List<PendingArt> _pendingArt = new List<PendingArt>();
        private float _nextArtPoll;

        private void Update()
        {
            if (_pendingArt.Count == 0 || Time.unscaledTime < _nextArtPoll) return;
            _nextArtPoll = Time.unscaledTime + 0.25f;

            for (int i = _pendingArt.Count - 1; i >= 0; i--)
            {
                var p = _pendingArt[i];
                if (p.Holder == null) { _pendingArt.RemoveAt(i); continue; }

                var tex = p.IsItem
                    ? (ItemArt.Instance != null ? ItemArt.Instance.Get(p.Key) : null)
                    : (CardArt.Instance != null ? CardArt.Instance.Get(p.Key) : null);
                if (tex == null) continue;

                AttachTexture(p.Holder, tex, p.Uv, preserveRatio: false);
                if (p.Fallback != null) p.Fallback.SetActive(false);
                _pendingArt.RemoveAt(i);
            }
        }

        private static void AttachTexture(RectTransform holder, Texture tex)
        {
            AttachTexture(holder, tex, FullUv);
        }

        /// <summary>
        /// Draw a texture to fill its holder, or keep its own proportions inside it.
        ///
        /// <paramref name="preserveRatio"/> is the whole question, and it is settled by looking at
        /// a real card texture rather than reasoning about it - two earlier rounds guessed, once
        /// each way. A card texture is 1024x1024, SQUARE, and the card fills it edge to edge with
        /// no padding: it is stored horizontally stretched. So drawing it across a card-shaped
        /// holder is what puts it back to card proportions, and "keep the image's own proportions"
        /// - which sounds like the careful choice - is precisely what made cards render as squares.
        ///
        /// Avatars are the opposite case. They are real photographs at their own shape, so they do
        /// need fitting, and they get it by asking for it.
        /// </summary>
        private static bool _loggedTexShape;

        private static void AttachTexture(RectTransform holder, Texture tex, Rect uv,
                                          bool preserveRatio = false)
        {
            var go = new GameObject("Tex", typeof(RectTransform));
            go.transform.SetParent(holder, false);
            var raw = go.AddComponent<RawImage>();
            raw.texture = tex;
            raw.uvRect = uv;
            raw.raycastTarget = false;
            Stretch((RectTransform)go.transform);

            if (preserveRatio)
            {
                float ratio = tex.height > 0 ? (float)tex.width / tex.height : 0.716f;
                if (uv.width > 0f && uv.height > 0f) ratio *= uv.width / uv.height;

                var fit = go.AddComponent<AspectRatioFitter>();
                fit.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
                fit.aspectRatio = ratio;
            }

            if (!_loggedTexShape)
            {
                _loggedTexShape = true;
                Plugin.Log.LogInfo("card texture shape: " + tex.width + "x" + tex.height +
                                   "; drawn " + (preserveRatio ? "fitted" : "filling the card frame") + ".");
            }
            // First, so a backing plate drawn earlier stays behind it.
            go.transform.SetSiblingIndex(holder.childCount > 1 ? 1 : 0);
        }

        public override bool DeferReconnectRequest { get { return true; } }
        public override ScreenType screenType { get { return ScreenType.None; } }

        public override void OnActivate(HUBGroupController prevGroup)
        {
            base.OnActivate(prevGroup);
            _pager.Reset();
            try { Populate(); }
            catch (Exception e) { Plugin.Log.LogWarning("history screen populate failed: " + e.Message); }
        }

        public override void OnDeactivate(HUBGroupController nextGroup)
        {
            base.OnDeactivate(nextGroup);
            CloseDeck();
        }

        // -----------------------------------------------------------------
        private static readonly Color Ink = new Color(0.16f, 0.17f, 0.20f, 1f);
        private static readonly Color InkDim = new Color(0.55f, 0.57f, 0.61f, 1f);
        private static readonly Color WinCol = new Color(0.176f, 0.408f, 0.847f, 1f);
        private static readonly Color LossCol = new Color(0.820f, 0.133f, 0.149f, 1f);
        private static readonly Color WinTint = new Color(0.176f, 0.408f, 0.847f, 0.17f);
        private static readonly Color LossTint = new Color(0.820f, 0.133f, 0.149f, 0.16f);
        private static readonly Color Slate = new Color(0.227f, 0.247f, 0.278f, 1f);
        private static readonly Color Hairline = new Color(0f, 0f, 0f, 0.10f);

        private const float PanelW = 2260f, PanelH = 1240f;
        private const float RowW = 2100f, RowH = 130f, RowGap = 8f;

        // The rows area is 1040 tall and a row occupies 138 with its gap, so seven fit. The eighth
        // would overhang the panel, which is what the paging strip below exists to avoid.
        private const int RowsPerPage = 7;

        public void Build()
        {
            if (_built) return;
            _built = true;

            var root = (RectTransform)transform;
            Stretch(root);

            // A dropped shadow behind an octagonal white panel is this client's panel idiom
            // everywhere it shows one - see the profile screen's stat panel.
            var shadow = Img("Shadow", root, GameArt.Sprite("shadow_Hex_12"), new Color(0, 0, 0, 0.30f));
            Center(shadow, PanelW + 60f, PanelH + 50f, 0, -14);

            var panel = Img("Panel", root, GameArt.Sprite("btn_Oct_20"), Color.white);
            Center(panel, PanelW, PanelH, 0, 0);

            var title = GameArt.Label("Text_Bold", panel.transform, "MATCH HISTORY", 64, Ink,
                                      TextAlignmentOptions.TopLeft);
            Place(title.rectTransform, 0, 1, 0, 1, 84, -58, 1200, 80);

            _summary = GameArt.Label("Text_Medium", panel.transform, "", 36, InkDim,
                                     TextAlignmentOptions.TopRight);
            Place(_summary.rectTransform, 1, 1, 1, 1, -84, -68, 1200, 56);

            var rule = Img("Rule", panel.transform, null, Hairline);
            Place(rule, 0.5f, 1, 0.5f, 1, 0, -150, RowW, 2);

            var body = new GameObject("Rows", typeof(RectTransform));
            body.transform.SetParent(panel.transform, false);
            Place((RectTransform)body.transform, 0.5f, 1, 0.5f, 1, 0, -172, RowW, 1040);
            _body = body.transform;

            _empty = GameArt.Label("Text_Regular", panel.transform,
                                   "No matches recorded yet - finish a game and it will appear here.",
                                   34, InkDim, TextAlignmentOptions.Top);
            Place(_empty.rectTransform, 0.5f, 1, 0.5f, 1, 0, -300, 1600, 56);

            _pageLabel = GameArt.Label("Text_Regular", panel.transform, "", 28, InkDim,
                                       TextAlignmentOptions.Center);
            Place(_pageLabel.rectTransform, 0.5f, 0, 0.5f, 0, 0, 40, 420, 44);
            _pagePrev = PageButton(panel.transform, "PREV", -260,
                                   () => { if (_pager.Move(-1, HistoryCount())) Populate(); });
            _pageNext = PageButton(panel.transform, "NEXT", 260,
                                   () => { if (_pager.Move(+1, HistoryCount())) Populate(); });

            BuildModal(root);
        }

        // -----------------------------------------------------------------
        private void Populate()
        {
            Build();
            foreach (var r in _rows) if (r != null) UnityEngine.Object.Destroy(r);
            _rows.Clear();
            if (History == null) return;

            var all = History.Overall();
            _summary.text = History.Count == 0
                ? ""
                : all.Wins + "W  -  " + all.Losses + "L      " +
                  Mathf.RoundToInt((float)(all.Rate * 100f)) + "%      streak " + History.Streak();

            _empty.gameObject.SetActive(History.Count == 0);

            int total = HistoryCount();
            float y = 0f;
            foreach (var m in History.Page(_pager.Start(total), _pager.Count(total))) Row(m, ref y);

            SetPageButton(_pagePrev, _pager.CanPrev(total));
            SetPageButton(_pageNext, _pager.CanNext(total));
            if (_pageLabel != null) _pageLabel.text = _pager.Label(total);
        }

        private int HistoryCount() { return History != null ? History.Count : 0; }

        private TextMeshProUGUI PageButton(Transform panel, string text, float x, Action onClick)
        {
            var btn = Img("Page" + text, panel, GameArt.Sprite("btn_Oct_16"),
                          new Color(0.93f, 0.94f, 0.95f, 1f));
            Place(btn, 0.5f, 0, 0.5f, 0, x, 40, 210, 58);
            var label = GameArt.Label("Text_MediumItalic", btn, text, InkDim,
                                      TextAlignmentOptions.Center, 28);
            Stretch(label.rectTransform);
            // Img() turns raycasting off - right for decoration, fatal for a button.
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

        /// <summary>
        /// One match, one row, tinted by result.
        ///
        /// The whole row carries the win/loss colour rather than a single cell, which is what makes
        /// a column of results readable at a glance without reading any of the text.
        /// </summary>
        private void Row(MatchRecord m, ref float y)
        {
            var accent = m.Won ? WinCol : LossCol;

            var row = Img("Row", _body, GameArt.Sprite("btn_Oct_16"), m.Won ? WinTint : LossTint);
            Place(row, 0, 1, 0, 1, 0, -y, RowW, RowH);
            _rows.Add(row.gameObject);

            var bar = Img("Accent", row, null, accent);
            Place(bar, 0, 0.5f, 0, 0.5f, 16, 0, 7, 92);

            // Our deck box, clear of the result bar. At x=38 with a centred pivot it started at
            // x=1 and covered the bar entirely, so the result colour vanished on any row that had
            // deck art - visible as rows 1 and 2 having no coloured edge at all.
            // The deck's own CARD SLEEVE, which is the card-shaped art the client's deck tile
            // shows and therefore what a player recognises the deck by. Records written before
            // sleeves were captured fall back to the guessed cover card, so old rows still draw
            // something rather than going blank.
            var thumb = !string.IsNullOrEmpty(m.Sleeve) ? m.Sleeve
                      : !string.IsNullOrEmpty(m.CoverCard) ? m.CoverCard : null;
            Art(row, thumb, 74, 0, 76, 106, false, isItem: !string.IsNullOrEmpty(m.Sleeve));

            var head = GameArt.Label("Text_Medium", row, "", 34, Ink, TextAlignmentOptions.MidlineLeft);
            head.richText = true;
            head.text = "<color=#" + ColorUtility.ToHtmlStringRGB(accent) + "><b>" +
                        (m.Won ? "WIN" : "LOSS") + "</b></color>   " + Esc(m.MyDeck);
            Place(head.rectTransform, 0, 0.5f, 0, 0.5f, 130, 18, 620, 44);

            var meta = GameArt.Label("Text_Regular", row, Meta(m), 26, InkDim,
                                     TextAlignmentOptions.MidlineLeft);
            Place(meta.rectTransform, 0, 0.5f, 0, 0.5f, 130, -22, 620, 36);

            // Opponent avatar: same circular framing as before, at full row height.
            Art(row, AvatarKey(m), 800, 0, RowH - 8f, RowH - 8f, false, true);

            var oppName = GameArt.Label("Text_Medium", row,
                                        string.IsNullOrEmpty(m.Opponent) ? "-" : m.Opponent, 34, Ink,
                                        TextAlignmentOptions.MidlineLeft);
            Place(oppName.rectTransform, 0, 0.5f, 0, 0.5f, 896, 18, 520, 44);

            var oppDeck = GameArt.Label("Text_Regular", row,
                                        string.IsNullOrEmpty(m.OppArchetype) ? "unknown deck" : m.OppArchetype,
                                        26, InkDim, TextAlignmentOptions.MidlineLeft);
            Place(oppDeck.rectTransform, 0, 0.5f, 0, 0.5f, 896, -22, 520, 36);

            var when = GameArt.Label("Text_Regular", row, Ago(m.WhenUtc), 26, InkDim,
                                     TextAlignmentOptions.MidlineRight);
            Place(when.rectTransform, 0, 0.5f, 1, 0.5f, 1700, 0, 250, 40);

            bool hasCards = m.OppCardsSeen() > 0;
            Button(row, "VIEW DECK", 1730, 210, true, hasCards, () => OpenDeck(m));

            bool hasLog = !string.IsNullOrEmpty(m.Log);
            TextMeshProUGUI logLabel = null;
            logLabel = Button(row, hasLog ? "COPY LOG" : "NO LOG", 1954, 190, false, hasLog, () =>
            {
                try
                {
                    GUIUtility.systemCopyBuffer = m.Log;
                    Plugin.Log.LogInfo("battle log copied (" + m.Log.Length + " chars).");
                    StartCoroutine(Confirm(logLabel, "COPIED!", "COPY LOG"));
                }
                catch (Exception e) { Plugin.Log.LogWarning("copy failed: " + e.Message); }
            });

            y += RowH + RowGap;
        }

        /// <summary>
        /// Say it worked, then go back to being a button.
        ///
        /// Copying to the clipboard is completely invisible - nothing on screen changes and there
        /// is no way to tell a successful copy from a dead button, which is exactly how the
        /// unclickable version looked.
        /// </summary>
        private static System.Collections.IEnumerator Confirm(TextMeshProUGUI label, string said,
                                                              string restore)
        {
            if (label == null) yield break;
            label.text = said;
            yield return new WaitForSeconds(1.6f);
            if (label != null) label.text = restore;
        }

        private static string Meta(MatchRecord m)
        {
            var bits = new List<string>();
            bits.Add(m.MyPrizesLeft + " - " + m.OppPrizesLeft + " prizes");
            if (m.Turns > 0) bits.Add(m.Turns + " turns");
            if (m.DurationSeconds > 0)
                bits.Add(m.DurationSeconds >= 60
                    ? (m.DurationSeconds / 60) + "m " + (m.DurationSeconds % 60).ToString("00") + "s"
                    : m.DurationSeconds + "s");
            return string.Join("  ·  ", bits.ToArray());
        }

        /// <summary>Rich text is on for the result colour, so a deck name with a bracket cannot break it.</summary>
        private static string Esc(string s)
        {
            return string.IsNullOrEmpty(s) ? "" : s.Replace("<", "<noparse><</noparse>");
        }

        // -----------------------------------------------------------------
        /// <summary>A texture tile: deck box, avatar, or card. Silently absent if art will not load.</summary>
        private RectTransform Art(Transform parent, string key, float x, float y, float w, float h,
                                  bool rounded, bool isAvatar = false, Rect? uv = null,
                                  bool isItem = false)
        {
            if (string.IsNullOrEmpty(key)) return null;

            // Avatar keys are OUR OWN file names, not card ids. Falling through to the card loader
            // with one would queue a pointless asset-bundle load for a card that cannot exist, and
            // its failure would land in the very diagnostics being used to chase real art problems.
            Texture tex = isAvatar ? Avatars.Get(key)
                         : isItem  ? (ItemArt.Instance != null ? ItemArt.Instance.Get(key) : null)
                                   : (CardArt.Instance != null ? CardArt.Instance.Get(key) : null);

            // An avatar is a file on disk - absent means absent. Card and deck-box art is loaded
            // asynchronously, so a null here only means "not yet" and the slot is kept for it.
            if (tex == null && isAvatar) return null;

            var holder = new GameObject("Art", typeof(RectTransform));
            holder.transform.SetParent(parent, false);
            var hrt = (RectTransform)holder.transform;
            Place(hrt, 0, 0.5f, 0.5f, 0.5f, x, y, w, h);

            // Avatars keep the circular framing the row was designed around. A Mask needs a
            // graphic to define its shape, and showMaskGraphic keeps that circle from being drawn
            // as a white disc over the art.
            if (isAvatar)
            {
                var shape = holder.AddComponent<Image>();
                shape.sprite = GameArt.Sprite("circle_0");
                shape.color = Color.white;
                holder.AddComponent<Mask>().showMaskGraphic = false;
            }

            var rect = uv ?? (!isAvatar && !isItem ? CardUv : FullUv);
            if (tex != null) AttachTexture(hrt, tex, rect, preserveRatio: isAvatar);
            else _pendingArt.Add(new PendingArt { Key = key, Holder = hrt, Uv = rect, IsItem = isItem });

            if (rounded)
            {
                // A backing plate BEHIND the art, not a sheet over it. The first version stretched
                // a 55%-white rectangle across the top of the image, which is simply a veil - it
                // washed the deck box out to a pale grey smudge.
                var plate = Img("Plate", holder.transform, GameArt.Sprite("btn_Oct_16"),
                                new Color(1f, 1f, 1f, 0.85f));
                Place(plate, 0.5f, 0.5f, 0.5f, 0.5f, 0, 0, w + 8, h + 8);
                // Behind everything, including art that arrives later and inserts itself first.
                plate.SetAsFirstSibling();
            }
            return hrt;
        }

        private TextMeshProUGUI Button(Transform row, string text, float x, float w, bool solid,
                                       bool enabled, Action onClick)
        {
            var col = !enabled ? new Color(0.78f, 0.79f, 0.82f, solid ? 1f : 0f)
                    : solid ? Slate : new Color(0, 0, 0, 0f);
            var btn = Img("Btn", row, GameArt.Sprite("btn_Oct_16"), col);
            Place(btn, 0, 0.5f, 1, 0.5f, x, 0, w, 58);

            if (!solid)
            {
                var edge = Img("Edge", btn, GameArt.Sprite("btn_Oct_16"),
                               new Color(0.60f, 0.62f, 0.66f, enabled ? 1f : 0.5f));
                Stretch(edge);
                var inner = Img("Fill", edge, GameArt.Sprite("btn_Oct_16"), Color.white);
                Place(inner, 0.5f, 0.5f, 0.5f, 0.5f, 0, 0, w - 4, 54);
            }

            var label = GameArt.Label("Text_MediumItalic", btn, text,
                                      solid ? Color.white : new Color(0.36f, 0.38f, 0.41f, enabled ? 1f : 0.5f),
                                      TextAlignmentOptions.Center, 26);
            Stretch(label.rectTransform);
            label.transform.SetAsLastSibling();

            if (!enabled || onClick == null) return label;
            var b = btn.gameObject.AddComponent<Button>();
            var g = btn.GetComponent<Image>();
            // Img() turns raycasting OFF for everything it builds, which is right for decoration
            // and fatal for a button: the graphic cannot be hit, so the click never lands and the
            // button looks enabled while doing nothing. This is why VIEW DECK did nothing at all.
            g.raycastTarget = true;
            b.targetGraphic = g;
            b.onClick.AddListener(() => onClick());
            return label;
        }

        // =================================================================
        // Deck preview
        // =================================================================
        // Geometry measured off the Limitless decklist the user gave as the reference, rather than
        // chosen by eye: 8 columns, cards at 136x189 (0.7196 wide-to-tall) with an 8px gap on both
        // axes, and the count badge CENTRED horizontally with its middle 85.2% of the way down the
        // card at 27.2% x 22.8% of the card's size. Those badge figures are the median of 23 cards
        // read out of the image, and they varied by under a percent across all of them.
        //
        // Absolute sizes here are what fits the client's 2560x1440 design canvas at four rows,
        // which is the binding constraint - eight columns is comfortable, height is not.
        private const int Cols = 8, MaxRows = 4, MaxTiles = Cols * MaxRows;
        private const float CardW = 185f, CardH = 257f, CardGap = 11f;
        private const float BadgeCx = 0.5f, BadgeCy = 0.852f, BadgeW = 0.272f, BadgeH = 0.228f;

        // Room above the grid for the title and subtitle, and below it for the rule, note and
        // button. The panel is sized from these plus however many rows the deck actually needs.
        private const float HeaderH = 168f, FooterH = 150f;

        private static float GridHeight(int rows)
        {
            return rows * CardH + (rows - 1) * CardGap;
        }

        /// <summary>
        /// Tall enough for exactly the rows this deck needs.
        ///
        /// A fixed height was wrong in both directions: three rows of cards overflowed it and ran
        /// over the footer rule and the copy button, while a small deck left a large empty panel.
        /// The reference page keeps its cards one size and lets the page grow, so this does too.
        /// </summary>
        private static float PanelHeight(int rows)
        {
            return HeaderH + GridHeight(rows) + FooterH;
        }

        private void BuildModal(Transform root)
        {
            _modal = new GameObject("DeckModal", typeof(RectTransform));
            _modal.transform.SetParent(root, false);
            Stretch((RectTransform)_modal.transform);

            var scrim = Img("Scrim", _modal.transform, null, new Color(0, 0, 0, 0.62f));
            Stretch(scrim);
            scrim.GetComponent<Image>().raycastTarget = true;
            var dismiss = scrim.gameObject.AddComponent<Button>();
            dismiss.targetGraphic = scrim.GetComponent<Image>();
            dismiss.transition = Selectable.Transition.None;
            dismiss.onClick.AddListener(CloseDeck);

            float w = Cols * CardW + (Cols - 1) * CardGap + 96f;
            var panel = Img("Panel", _modal.transform, GameArt.Sprite("btn_Oct_20"), Color.white);
            Center(panel, w, PanelHeight(MaxRows), 0, 0);
            _panel = panel;
            // Clicks inside the panel must not reach the dismiss scrim behind it.
            var block = panel.gameObject.AddComponent<Button>();
            block.targetGraphic = panel.GetComponent<Image>();
            block.transition = Selectable.Transition.None;
            panel.GetComponent<Image>().raycastTarget = true;

            _modalTitle = GameArt.Label("Text_Bold", panel.transform, "", 50, Ink,
                                        TextAlignmentOptions.TopLeft);
            Place(_modalTitle.rectTransform, 0, 1, 0, 1, 48, -44, w - 200, 64);

            _modalSub = GameArt.Label("Text_Regular", panel.transform, "", 30, InkDim,
                                      TextAlignmentOptions.TopLeft);
            Place(_modalSub.rectTransform, 0, 1, 0, 1, 48, -108, w - 200, 44);

            var close = Img("Close", panel.transform, GameArt.Sprite("btn_Oct_16"),
                            new Color(0.90f, 0.91f, 0.93f, 1f));
            Place(close, 1, 1, 1, 1, -44, -44, 62, 62);
            var cl = GameArt.Label("Text_Bold", close, "X", InkDim, TextAlignmentOptions.Center, 30);
            Stretch(cl.rectTransform);
            close.GetComponent<Image>().raycastTarget = true;
            var cb = close.gameObject.AddComponent<Button>();
            cb.targetGraphic = close.GetComponent<Image>();
            cb.onClick.AddListener(CloseDeck);

            var grid = new GameObject("Grid", typeof(RectTransform));
            grid.transform.SetParent(panel.transform, false);
            Place((RectTransform)grid.transform, 0.5f, 1, 0.5f, 1, 0, -HeaderH,
                  Cols * CardW + (Cols - 1) * CardGap, GridHeight(MaxRows));
            _grid = grid.transform;

            var footRule = Img("Rule", panel.transform, null, Hairline);
            Place(footRule, 0.5f, 0, 0.5f, 0, 0, 118, w - 96, 2);

            _modalNote = GameArt.Label("Text_Regular", panel.transform, "", 26, InkDim,
                                       TextAlignmentOptions.MidlineLeft);
            _modalNote.enableWordWrapping = true;
            Place(_modalNote.rectTransform, 0, 0, 0, 0, 48, 58, w - 560, 80);

            var copy = Img("CopyDeck", panel.transform, GameArt.Sprite("btn_Oct_16"), Slate);
            Place(copy, 1, 0, 1, 0, -48, 40, 380, 64);
            _copyDeckLabel = GameArt.Label("Text_MediumItalic", copy, "COPY AS DECKLIST", Color.white,
                                           TextAlignmentOptions.Center, 28);
            Stretch(_copyDeckLabel.rectTransform);
            copy.GetComponent<Image>().raycastTarget = true;
            var copyBtn = copy.gameObject.AddComponent<Button>();
            copyBtn.targetGraphic = copy.GetComponent<Image>();
            copyBtn.onClick.AddListener(CopyDeck);

            _modal.SetActive(false);
        }

        private void OpenDeck(MatchRecord m)
        {
            try
            {
                _viewing = m;
                Build();
                foreach (var t in _tiles) if (t != null) UnityEngine.Object.Destroy(t);
                _tiles.Clear();

                var cards = m.OppCardList();
                int seen = m.OppCardsSeen();

                _modalTitle.text = (string.IsNullOrEmpty(m.Opponent) ? "OPPONENT" : m.Opponent.ToUpperInvariant())
                                 + " - CARDS SEEN";
                _modalSub.text = seen + " of " + DeckExport.DeckSize + " cards revealed during the match"
                               + (string.IsNullOrEmpty(m.OppArchetype) ? "" : "   ·   " + m.OppArchetype);

                // Pokemon, then Trainers, then Energy - the order a decklist is read in. Indices
                // are carried along because the name and type live in parallel lists.
                var order = new List<int>();
                for (int i = 0; i < cards.Count; i++) order.Add(i);
                order.Sort((a, b) =>
                {
                    int ta = m.OppTypeAt(a), tb = m.OppTypeAt(b);
                    if (ta != tb) return ta.CompareTo(tb);
                    if (cards[a].Value != cards[b].Value) return cards[b].Value.CompareTo(cards[a].Value);
                    return string.Compare(m.OppNameAt(a, cards[a].Key),
                                          m.OppNameAt(b, cards[b].Key), StringComparison.OrdinalIgnoreCase);
                });

                int shown = 0;
                foreach (var i in order)
                {
                    if (shown >= MaxTiles) break;
                    Tile(cards[i].Key, cards[i].Value, shown++, m.OppNameAt(i, cards[i].Key));
                }

                int rows = Mathf.Clamp(Mathf.CeilToInt(shown / (float)Cols), 1, MaxRows);
                if (_panel != null)
                    _panel.sizeDelta = new Vector2(_panel.sizeDelta.x, PanelHeight(rows));

                int hidden = cards.Count - shown;
                _modalNote.text = hidden > 0 ? "+" + hidden + " more not shown." : "";

                // Says exactly what was drawn, so "it looks the same" can be told apart from
                // "the new code did not run" without guessing.
                Plugin.Log.LogInfo("deck view: " + shown + " of " + cards.Count + " cards, "
                    + rows + " row(s); card " + CardW + "x" + CardH + " gap " + CardGap
                    + "; panel " + (_panel != null ? _panel.sizeDelta.ToString() : "?")
                    + "; badge " + (CardW * BadgeW) + "x" + (CardH * BadgeH)
                    + " at " + BadgeCx + "," + BadgeCy);

                _copyDeckLabel.text = "COPY AS DECKLIST";
                _modal.SetActive(true);
                _modal.transform.SetAsLastSibling();
            }
            catch (Exception e) { Plugin.Log.LogWarning("deck preview failed: " + e.Message); }
        }

        private void CloseDeck()
        {
            if (_modal != null) _modal.SetActive(false);
        }

        /// <summary>One card: its art, and the client's own red hex count badge over the corner.</summary>
        private void Tile(string sourceId, int count, int index, string name)
        {
            float x = (index % Cols) * (CardW + CardGap);
            float y = (index / Cols) * (CardH + CardGap);

            var tile = new GameObject("Card", typeof(RectTransform));
            tile.transform.SetParent(_grid, false);
            Place((RectTransform)tile.transform, 0, 1, 0, 1, x, -y, CardW, CardH);
            _tiles.Add(tile);

            var tex = CardArt.Instance != null ? CardArt.Instance.Get(sourceId) : null;
            if (tex != null)
            {
                AttachTexture((RectTransform)tile.transform, tex, CardUv);
            }
            else
            {
                // No art is not a broken tile: show the card's name on a plain card-shaped plate so
                // the list is still complete and readable.
                var plate = Img("Plate", tile.transform, GameArt.Sprite("btn_Oct_16"),
                                new Color(0.93f, 0.93f, 0.95f, 1f));
                Stretch(plate);
                var label = GameArt.Label("Text_Regular", plate,
                                          string.IsNullOrEmpty(name) ? CardName(sourceId) : name,
                                          24, Ink, TextAlignmentOptions.Center);
                label.enableWordWrapping = true;
                Place(label.rectTransform, 0.5f, 0.5f, 0.5f, 0.5f, 0, 0, CardW - 24, CardH - 60);

                // The load is in flight; swap the plate out for the real art when it lands.
                _pendingArt.Add(new PendingArt
                {
                    Key = sourceId,
                    Holder = (RectTransform)tile.transform,
                    Fallback = plate.gameObject,
                    Uv = CardUv,
                });
            }

            var badge = Img("Count", tile.transform, GameArt.Hex(), new Color(0.82f, 0.13f, 0.15f, 1f));
            badge.GetComponent<Image>().type = Image.Type.Simple;
            Place(badge, 0, 0, 0.5f, 0.5f,
                  CardW * BadgeCx, CardH * (1f - BadgeCy), CardW * BadgeW, CardH * BadgeH);
            var n = GameArt.Label("Text_Bold", badge, count.ToString(), Color.white,
                                  TextAlignmentOptions.Center, 28);
            Stretch(n.rectTransform);
        }

        private static string CardName(string sourceId)
        {
            try
            {
                var cs = MatchLogic.CardCache.Get(sourceId);
                if (cs != null && !string.IsNullOrEmpty(cs.cardName)) return cs.cardName;
            }
            catch { }
            return sourceId;
        }

        private void CopyDeck()
        {
            if (_viewing == null) return;
            _copyDeckLabel.text = "COPYING...";
            StartCoroutine(DeckExport.CopyToClipboard(this, _viewing.OppCardList(), r =>
            {
                StartCoroutine(Confirm(_copyDeckLabel,
                                       r.Importable ? "COPIED!" : "COPIED (PLAIN)",
                                       "COPY AS DECKLIST"));
                Plugin.Log.LogInfo("deck copied: " + r.RealCards + " real + " + r.FillerCards +
                                   " filler, importable=" + r.Importable);
            }));
        }

        private static string AvatarKey(MatchRecord m)
        {
            return m.WhenUtc == default(DateTime) ? null : Avatars.KeyFor(m);
        }

        private static string Ago(DateTime whenUtc)
        {
            if (whenUtc == default(DateTime)) return "-";
            var d = DateTime.UtcNow - whenUtc;
            if (d.TotalMinutes < 1) return "just now";
            if (d.TotalHours < 1) return (int)d.TotalMinutes + "m ago";
            if (d.TotalDays < 1) return (int)d.TotalHours + "h ago";
            if (d.TotalDays < 7) return (int)d.TotalDays + "d ago";
            return whenUtc.ToLocalTime().ToString("d MMM");
        }

        // -----------------------------------------------------------------
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
            Place(rt, 0.5f, 0.5f, 0.5f, 0.5f, x, y, w, h);
        }

        internal static void Place(RectTransform rt, float ax, float ay, float px, float py,
                                   float x, float y, float w, float h)
        {
            rt.anchorMin = new Vector2(ax, ay);
            rt.anchorMax = new Vector2(ax, ay);
            rt.pivot = new Vector2(px, py);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);
            rt.localScale = Vector3.one;
        }

        internal static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;
        }
    }

    /// <summary>
    /// Borrows the client's own fonts and sprites out of memory.
    ///
    /// A TextMeshPro label built from scratch needs a font ASSET and a matching MATERIAL; getting
    /// either wrong renders the wrong typeface or nothing at all. Cloning a label that already uses
    /// the wanted font inherits both, which is why this looks up a live example by font name rather
    /// than constructing one.
    /// </summary>
    internal static class GameArt
    {
        private static Dictionary<string, Sprite> _sprites;
        private static readonly Dictionary<string, TextMeshProUGUI> _fontSamples =
            new Dictionary<string, TextMeshProUGUI>();
        private static Sprite _hex;

        public static Sprite Sprite(string name)
        {
            if (_sprites == null)
            {
                _sprites = new Dictionary<string, Sprite>();
                foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>())
                    if (s != null && !_sprites.ContainsKey(s.name)) _sprites[s.name] = s;
                Plugin.Log.LogInfo("game art: " + _sprites.Count + " sprites indexed.");
            }
            UnityEngine.Sprite found;
            if (_sprites.TryGetValue(name, out found)) return found;
            Plugin.Log.LogWarning("game art: sprite \"" + name + "\" not found; using a flat fill.");
            return null;
        }

        /// <summary>
        /// The card-count badge is a pointy-top hexagon, and the client's hex sprites are all
        /// wide button plates that do not scale down to a badge. Drawing one is a dozen lines and
        /// gives an exact shape at any size, so it is generated once and reused.
        /// </summary>
        public static Sprite Hex()
        {
            if (_hex != null) return _hex;

            const int W = 128, H = 144, SS = 3;      // supersampled for smooth edges
            var tex = new Texture2D(W, H, TextureFormat.ARGB32, false);
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int hits = 0;
                    for (int sy = 0; sy < SS; sy++)
                        for (int sx = 0; sx < SS; sx++)
                        {
                            float u = (x + (sx + 0.5f) / SS) / W * 2f - 1f;   // -1..1
                            float v = (y + (sy + 0.5f) / SS) / H * 2f - 1f;
                            if (InsideHex(u, v)) hits++;
                        }
                    byte a = (byte)(255 * hits / (SS * SS));
                    px[y * W + x] = new Color32(255, 255, 255, a);
                }
            tex.SetPixels32(px);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            _hex = UnityEngine.Sprite.Create(tex, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f));
            return _hex;
        }

        /// <summary>Pointy-top regular hexagon in normalised -1..1 space.</summary>
        private static bool InsideHex(float u, float v)
        {
            u = Mathf.Abs(u); v = Mathf.Abs(v);
            if (v > 1f || u > 0.866f) return false;
            return v <= 1f - u / 0.866f * 0.5f;
        }

        private static TextMeshProUGUI Sample(string fontName)
        {
            TextMeshProUGUI cached;
            if (_fontSamples.TryGetValue(fontName, out cached) && cached != null) return cached;

            TextMeshProUGUI any = null;
            foreach (var l in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>())
            {
                if (l == null || l.font == null || l.canvas == null) continue;
                if (any == null) any = l;
                if (l.font.name != fontName) continue;
                _fontSamples[fontName] = l;
                Plugin.Log.LogInfo("game art: font \"" + fontName + "\" cloned from " + l.name +
                                   " (material \"" + (l.fontSharedMaterial != null
                                       ? l.fontSharedMaterial.name : "none") + "\").");
                return l;
            }
            if (any != null) Plugin.Log.LogWarning("game art: font \"" + fontName + "\" not in use; " +
                                                  "falling back to \"" + any.font.name + "\".");
            _fontSamples[fontName] = any;
            return any;
        }

        public static TextMeshProUGUI Label(string fontName, Transform parent, string text, int size,
                                            Color color, TextAlignmentOptions align)
        {
            var src = Sample(fontName);
            if (src == null) throw new InvalidOperationException("no TextMeshPro label to clone");

            var go = UnityEngine.Object.Instantiate(src.gameObject, parent);
            go.name = "Label";
            go.SetActive(true);

            // A cloned label drags along whatever drove it in its old home - a localiser that would
            // rewrite the text, and layout components a parent would honour and move it.
            foreach (var c in go.GetComponents<MonoBehaviour>())
                if (c != null && !(c is TextMeshProUGUI)) UnityEngine.Object.Destroy(c);
            foreach (var child in go.GetComponentsInChildren<Transform>(true))
                if (child != go.transform) UnityEngine.Object.Destroy(child.gameObject);

            var lbl = go.GetComponent<TextMeshProUGUI>();

            // Reset to the font asset's OWN material.
            //
            // A label that lives inside a mask carries a stencil-modified material, and cloning it
            // brings that stencil along. In an unmasked canvas the stencil test never passes and
            // the label renders nothing at all - which is exactly how every Text_Medium label
            // vanished while Text_Bold and Text_Regular were fine: the sample happened to sit
            // inside a masked region.
            if (lbl.font != null) lbl.fontSharedMaterial = lbl.font.material;

            lbl.text = text;
            lbl.fontSize = size;
            lbl.color = color;
            lbl.alignment = align;
            lbl.enableWordWrapping = false;
            lbl.overflowMode = TextOverflowModes.Ellipsis;
            lbl.raycastTarget = false;
            lbl.rectTransform.localScale = Vector3.one;

            // Clear any case transform the source label happened to carry. Without this a cloned
            // label silently UPPERCASED whatever we set - so "3 - 6 prizes" rendered as
            // "3 - 6 PRIZES" while a label cloned from a different source stayed mixed case, which
            // reads as two different design decisions rather than one inherited accident.
            lbl.fontStyle &= ~(FontStyles.UpperCase | FontStyles.LowerCase | FontStyles.SmallCaps);
            return lbl;
        }

        /// <summary>Colour-then-size overload, for call sites that read better that way.</summary>
        public static TextMeshProUGUI Label(string fontName, Transform parent, string text,
                                            Color color, TextAlignmentOptions align, int size)
        {
            return Label(fontName, parent, text, size, color, align);
        }
    }
}
