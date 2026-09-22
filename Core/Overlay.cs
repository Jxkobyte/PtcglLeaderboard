using System;
using System.Collections.Generic;
using UnityEngine;

namespace PrizeTracker.Core
{
    /// <summary>One card as drawn in the grid. Built from the Tracker's rows.</summary>
    internal class Tile
    {
        public string Name;
        public string SourceId;
        public string Badge;    // "x4" - copies, top-right of the art
        public string Footer;   // "35%" or "2 PRIZED"
        public bool Certain;    // provably prized / confirmed in deck
        public bool FaceDown;   // an unknown prize card: draw a card back, not a name
        public double Fill = -1; // probability bar, -1 for none
    }

    /// <summary>
    /// The on-screen panel: a grid of CARD ART rather than a list of names.
    ///
    /// Recognising a card by its art is far faster than reading a name mid-turn, which is the whole
    /// point of an overlay you glance at. Each tile carries the copy count, and either a
    /// probability bar (how likely it is prized / how likely it is the next draw) or a certainty
    /// flag when the answer is provable.
    ///
    /// Art is loaded from the client's own asset bundles via <see cref="CardArt"/>. If a texture is
    /// missing or has not arrived yet the tile falls back to the card's name, so the overlay is
    /// fully usable even when art cannot be loaded.
    ///
    /// Layout is done with explicit rects rather than nested GUILayout groups: a grid of fixed-size
    /// cells is simpler and cheaper that way, and it keeps the tile drawing identical in every tab.
    /// </summary>
    internal class Overlay
    {
        public bool Visible = true;
        public Rect WindowRect = new Rect(40, 120, 360, 460);

        private const int WindowId = 60217;

        public Theme Theme = new Theme();
        public Tracker Tracker;
        public PerformanceTuner Perf;
        public MatchHistory History;
        public Action OnSettingsChanged;

        private enum Tab { Prizes, Deck, Opponent, History, Settings }
        private Tab _tab = Tab.Prizes;

        private Vector2 _scroll;
        private bool _resizing;
        private Vector2 _resizeStartMouse, _resizeStartSize;
        private const float Grip = 16f;

        // A card is 5:7. 72px wide keeps four columns inside the default panel width and is still
        // big enough to recognise the art at a glance.
        private const float TileW = 72f;
        private const float ArtH = TileW * 7f / 5f;
        private const float FootH = 14f;
        private const float BarH = 4f;
        private const float Gap = 4f;

        /// <summary>Set by the detector each tick.</summary>
        public bool InMatch;

        // Outside a match the panel stays out of the way: the deck records you want between games
        // are drawn on the game's own deck tiles instead. F1 still peeks at it, and that peek is
        // dropped as soon as the match state changes so it never lingers into a game.
        private bool _peek;
        private bool _lastInMatch;

        /// <summary>F1. In a match this hides/shows the panel; out of one it is a temporary peek.</summary>
        public void ToggleRequested()
        {
            if (InMatch) Visible = !Visible;
            else _peek = !_peek;
        }

        public void OnGUI()
        {
            if (InMatch != _lastInMatch) { _peek = false; _lastInMatch = InMatch; }
            if (!Visible) return;
            if (!InMatch && !_peek) return;
            Theme.EnsureBuilt();

            WindowRect.x = Mathf.Clamp(WindowRect.x, -WindowRect.width + 60, Screen.width - 60);
            WindowRect.y = Mathf.Clamp(WindowRect.y, 0, Screen.height - 40);

            WindowRect = GUI.Window(WindowId, WindowRect, DrawWindow, "Prize Tracker", Theme.Window);
        }

        private void DrawWindow(int id)
        {
            var t = Theme;

            var closeRect = new Rect(WindowRect.width - 22, 3, 18, 16);
            if (GUI.Button(closeRect, "x", t.Btn)) Visible = false;

            DrawTabs();
            if (Tracker != null) DrawScoreline();
            t.HLine(WindowRect.width);

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            try
            {
                switch (_tab)
                {
                    case Tab.Prizes: DrawPrizes(); break;
                    case Tab.Deck: DrawDeck(); break;
                    case Tab.Opponent: DrawOpponent(); break;
                    case Tab.History: DrawHistory(); break;
                    case Tab.Settings: DrawSettings(); break;
                }
            }
            catch (Exception e)
            {
                GUILayout.Label("render error: " + e.Message, t.Dim);
            }
            GUILayout.EndScrollView();

            t.HLine(WindowRect.width);
            GUILayout.Label(FooterText(), t.Small);

            HandleResize();
            GUI.DragWindow(new Rect(0, 0, WindowRect.width - 26, 20));
        }

        private string FooterText()
        {
            string perf = Perf != null ? Perf.Describe() : "";
            return "F1 hide  |  " + perf;
        }

        private void DrawTabs()
        {
            GUILayout.BeginHorizontal();
            TabButton("PRIZES", Tab.Prizes);
            TabButton("DECK", Tab.Deck);
            TabButton("OPP", Tab.Opponent);
            TabButton("LOG", Tab.History);
            TabButton("SET", Tab.Settings);
            GUILayout.EndHorizontal();
        }

        private void TabButton(string label, Tab tab)
        {
            var style = (_tab == tab) ? Theme.TabOn : Theme.Tab;
            if (GUILayout.Button(label, style, GUILayout.ExpandWidth(true))) _tab = tab;
        }

        private void DrawScoreline()
        {
            var t = Theme;
            var tr = Tracker;
            GUILayout.BeginHorizontal();
            GUILayout.Label("You " + tr.MyPrizes, t.Title, GUILayout.Width(52));
            GUILayout.Label("Opp " + tr.OppPrizes, t.Title, GUILayout.Width(52));
            GUILayout.FlexibleSpace();
            GUILayout.Label(string.Format("deck {0}  hand {1}", tr.MyDeckCount, tr.MyHand), t.Dim);
            GUILayout.EndHorizontal();
            GUILayout.Label(tr.Status, t.Small);
        }

        // ------------------------------------------------------------------
        /// <summary>
        /// The prize cards themselves - one tile per remaining prize, face down until known.
        ///
        /// This deliberately shows SIX cards (then five, then four...) rather than every card that
        /// might be prized: the prizes are six physical cards, and a list of twenty candidates at
        /// 30-odd percent each is really just a picture of your deck.
        /// </summary>
        private void DrawPrizes()
        {
            var t = Theme;
            if (Tracker == null) return;

            if (!Tracker.DeckKnown)
            {
                GUILayout.Label("Waiting for your decklist...", t.Dim);
                return;
            }

            var slots = Tracker.PrizeSlots;
            if (slots.Count == 0)
            {
                // Zero prize slots means one of two very different things. Before setup finishes
                // the prizes have not been dealt yet, and saying "no prizes left" there reads as a
                // finished game - which is exactly how it looked on screen at the start of a match.
                GUILayout.Label(Tracker.MyPrizes == 0 && Tracker.OppPrizes == 0
                                    ? "Waiting for prizes to be dealt..."
                                    : "No prizes left.", t.Dim);
                return;
            }

            var tiles = new List<Tile>();
            foreach (var p in slots)
                tiles.Add(new Tile
                {
                    Name = p.Known ? p.Name : "?",
                    SourceId = p.Known ? p.SourceId : null,
                    FaceDown = !p.Known,
                    Certain = p.Known,
                    Footer = "",
                    Fill = -1,
                });
            DrawGrid(tiles);

            int known = Tracker.PrizeSlotsKnown;
            GUILayout.Space(3);
            GUILayout.Label(known == slots.Count
                ? "All " + slots.Count + " prizes known."
                : known + " of " + slots.Count + " known - search your deck to solve the rest.", t.Small);

            // While unsolved, the odds are still real information - but they are a hint under the
            // prizes, not the prize list itself.
            var likely = Tracker.Likely;
            if (known < slots.Count && likely.Count > 0)
            {
                GUILayout.Space(6);
                GUILayout.Label("MOST LIKELY PRIZED", t.Header);
                int shown = 0;
                foreach (var r in likely)
                {
                    if (shown >= 6) break;
                    GUILayout.BeginHorizontal(shown % 2 == 0 ? t.RowStyle : t.RowAltStyle);
                    GUILayout.Label(r.Unaccounted > 1 ? r.Name + "  x" + r.Unaccounted : r.Name, t.Label);
                    GUILayout.FlexibleSpace();
                    var bar = GUILayoutUtility.GetRect(40, 14, GUILayout.Width(40));
                    bar.y += 5; bar.height = 5;
                    t.Bar(bar, (float)r.ProbAnyPrized, t.Accent);
                    GUILayout.Label(Pct(r.ProbAnyPrized), t.Value, GUILayout.Width(34));
                    GUILayout.EndHorizontal();
                    shown++;
                }
            }
        }

        private void DrawDeck()
        {
            var t = Theme;
            if (Tracker == null) return;

            GUILayout.Label(string.Format("Deck {0}  |  hidden {1}  |  discard {2}",
                Tracker.MyDeckCount, Tracker.TotalUnknown, Tracker.MyDiscardCount), t.Dim);

            DrawBottomStack();

            var rows = Tracker.DeckRows;
            if (rows.Count == 0) { GUILayout.Label("Nothing to show yet.", t.Dim); return; }

            var tiles = new List<Tile>();
            foreach (var r in rows)
            {
                tiles.Add(new Tile
                {
                    Name = r.Name,
                    SourceId = r.SourceId,
                    Badge = r.Count > 1 ? "x" + r.Count : null,
                    Footer = r.Confirmed ? "IN DECK" : Pct(r.ProbTopDeck),
                    Certain = r.Confirmed,
                    Fill = -1,
                });
            }
            DrawGrid(tiles);
            GUILayout.Space(4);
            GUILayout.Label("% = chance the next card off the deck is that card.", t.Small);
        }

        /// <summary>
        /// Cards sitting on the bottom of the deck, nearest first, each showing how many draws
        /// away it is. Only appears when there is something down there - normally after a Metang
        /// ability or anything else that puts known cards on the bottom.
        /// </summary>
        private void DrawBottomStack()
        {
            var t = Theme;
            var bottom = Tracker.Bottom;
            if (!bottom.Active) return;

            GUILayout.Space(5);
            GUILayout.Label("ON THE BOTTOM", t.Header);

            var cards = bottom.Cards;
            var tiles = new List<Tile>();
            bool anyUnordered = false;
            foreach (var c in cards)
            {
                string foot;
                if (c.OrderKnown)
                    foot = c.DrawsAway <= 0 ? "NEXT" : "in " + c.DrawsAway;
                else
                {
                    // The group was shuffled before being placed, so all we honestly know is the
                    // window it arrives in - not which of them lands first.
                    anyUnordered = true;
                    foot = c.GroupFirst == c.GroupLast
                        ? "in " + c.GroupFirst
                        : c.GroupFirst + "-" + c.GroupLast;
                }
                tiles.Add(new Tile
                {
                    Name = c.Name,
                    SourceId = c.SourceId,
                    Footer = foot,
                    Certain = c.GroupFirst <= 2,
                    Fill = -1,
                });
            }
            DrawGrid(tiles);
            GUILayout.Space(2);
            GUILayout.Label(bottom.GroupCount > 1
                ? bottom.GroupCount + " groups stacked, oldest first - draws until each arrives"
                : "draws until it comes off the top", t.Small);
            if (anyUnordered)
                GUILayout.Label("a range means that group was shuffled before being placed", t.Small);
            GUILayout.Label("clears if the deck is shuffled", t.Small);
            GUILayout.Space(5);
            t.HLine(WindowRect.width);
        }

        private void DrawOpponent()
        {
            var t = Theme;
            if (Tracker == null) return;

            GUILayout.Label(string.Format("Prizes {0}  |  hand {1}  |  deck {2}",
                Tracker.OppPrizes, Tracker.OppHand, Tracker.OppDeck), t.Dim);
            GUILayout.Label(string.Format("Discard {0}  |  lost zone {1}",
                Tracker.OppDiscardCount, Tracker.OppLost), t.Dim);

            GUILayout.Space(4);
            GUILayout.Label("SEEN THIS MATCH", t.Header);
            var seen = Tracker.OppSeen;
            if (seen == null || seen.Count == 0)
            {
                GUILayout.Label("Nothing revealed yet.", t.Dim);
                return;
            }

            var tiles = new List<Tile>();
            foreach (var r in seen)
                tiles.Add(new Tile
                {
                    Name = r.Name,
                    SourceId = r.SourceId,
                    Badge = r.Count > 1 ? "x" + r.Count : null,
                    Footer = "",
                    Fill = -1,
                });
            DrawGrid(tiles);
            GUILayout.Space(4);
            GUILayout.Label("Each physical card counts once, even after it changes zone.", t.Small);
        }

        // ------------------------------------------------------------------
        private void DrawGrid(List<Tile> tiles)
        {
            float avail = WindowRect.width - 26f;   // panel padding + scrollbar
            int cols = Mathf.Max(2, Mathf.FloorToInt((avail + Gap) / (TileW + Gap)));
            float cellH = ArtH + BarH + FootH + Gap;

            for (int i = 0; i < tiles.Count; i += cols)
            {
                var row = GUILayoutUtility.GetRect(avail, cellH, GUILayout.ExpandWidth(true));
                for (int c = 0; c < cols; c++)
                {
                    int idx = i + c;
                    if (idx >= tiles.Count) break;
                    var cell = new Rect(row.x + c * (TileW + Gap), row.y, TileW, cellH - Gap);
                    DrawTile(cell, tiles[idx]);
                }
            }
        }

        private void DrawTile(Rect cell, Tile tile)
        {
            var t = Theme;
            var white = Texture2D.whiteTexture;
            var prev = GUI.color;

            var artRect = new Rect(cell.x, cell.y, cell.width, ArtH);

            // backing plate (also the "certain" highlight)
            GUI.color = tile.Certain ? new Color(t.Good.r, t.Good.g, t.Good.b, 0.22f) : t.Row;
            GUI.DrawTexture(new Rect(cell.x - 1, cell.y - 1, cell.width + 2, cell.height + 2), white);
            GUI.color = prev;

            Texture art = null;
            if (!tile.FaceDown && CardArt.Instance != null) art = CardArt.Instance.Get(tile.SourceId);

            if (tile.FaceDown)
            {
                // A prize we have not solved yet: draw it as what it actually is on the table.
                GUI.color = new Color(0.16f, 0.26f, 0.40f, 1f);
                GUI.DrawTexture(artRect, white);
                GUI.color = new Color(0.30f, 0.45f, 0.62f, 1f);
                GUI.DrawTexture(new Rect(artRect.x + 5, artRect.y + 5, artRect.width - 10, artRect.height - 10), white);
                GUI.color = prev;
                var q = new GUIStyle(t.Label);
                q.alignment = TextAnchor.MiddleCenter;
                q.fontStyle = FontStyle.Bold;
                q.normal.textColor = new Color(0.62f, 0.76f, 0.90f, 1f);
                GUI.Label(artRect, "?", q);
            }
            else if (art != null)
            {
                GUI.DrawTexture(artRect, art, ScaleMode.ScaleAndCrop);
            }
            else
            {
                GUI.color = new Color(0.05f, 0.06f, 0.08f, 1f);
                GUI.DrawTexture(artRect, white);
                GUI.color = prev;
                var nameStyle = new GUIStyle(t.Small);
                nameStyle.wordWrap = true;
                nameStyle.alignment = TextAnchor.MiddleCenter;
                nameStyle.normal.textColor = t.Text;
                GUI.Label(artRect, tile.Name, nameStyle);
            }

            if (tile.Certain)
            {
                GUI.color = t.Good;
                // 1px outline around the art
                GUI.DrawTexture(new Rect(artRect.x, artRect.y, artRect.width, 1), white);
                GUI.DrawTexture(new Rect(artRect.x, artRect.yMax - 1, artRect.width, 1), white);
                GUI.DrawTexture(new Rect(artRect.x, artRect.y, 1, artRect.height), white);
                GUI.DrawTexture(new Rect(artRect.xMax - 1, artRect.y, 1, artRect.height), white);
                GUI.color = prev;
            }

            if (!string.IsNullOrEmpty(tile.Badge))
            {
                var badgeStyle = new GUIStyle(t.Small);
                badgeStyle.alignment = TextAnchor.MiddleCenter;
                badgeStyle.fontStyle = FontStyle.Bold;
                badgeStyle.normal.textColor = Color.white;
                var br = new Rect(artRect.xMax - 22, artRect.y + 2, 20, 13);
                GUI.color = new Color(0.03f, 0.04f, 0.06f, 0.85f);
                GUI.DrawTexture(br, white);
                GUI.color = prev;
                GUI.Label(br, tile.Badge, badgeStyle);
            }

            float y = artRect.yMax;
            if (tile.Fill >= 0)
            {
                t.Bar(new Rect(cell.x, y + 1, cell.width, BarH - 2), (float)tile.Fill, t.Accent);
            }
            y += BarH;

            if (!string.IsNullOrEmpty(tile.Footer))
            {
                var footStyle = new GUIStyle(t.Small);
                footStyle.alignment = TextAnchor.MiddleCenter;
                if (tile.Certain)
                {
                    footStyle.normal.textColor = t.Good;
                    footStyle.fontStyle = FontStyle.Bold;
                }
                else footStyle.normal.textColor = t.Text;
                GUI.Label(new Rect(cell.x, y, cell.width, FootH), tile.Footer, footStyle);
            }
        }

        // ------------------------------------------------------------------
        /// <summary>
        /// Match history and win rates. Most useful between games, which is where you actually sit
        /// and look at it.
        /// </summary>
        private void DrawHistory()
        {
            var t = Theme;
            if (History == null) { GUILayout.Label("history unavailable", t.Dim); return; }

            if (History.Count == 0)
            {
                DrawDeckRecords();
                GUILayout.Label("No matches recorded yet.", t.Dim);
                GUILayout.Label("Finish a game and it will appear here.", t.Small);
                return;
            }

            DrawDeckRecords();

            var all = History.Overall();
            GUILayout.BeginHorizontal();
            GUILayout.Label(all.Wins + "W - " + all.Losses + "L", t.Title, GUILayout.Width(80));
            GUILayout.Label(Pct(all.Rate), t.Title, GUILayout.Width(48));
            GUILayout.FlexibleSpace();
            GUILayout.Label("streak " + History.Streak(), t.Dim);
            GUILayout.EndHorizontal();

            Rates("BY DECK", History.ByDeck());
            Rates("BY OPPONENT DECK", History.ByMatchup());

            GUILayout.Space(6);
            GUILayout.Label("RECENT", t.Header);
            int i = 0;
            foreach (var r in History.Recent(12))
            {
                GUILayout.BeginHorizontal(i++ % 2 == 0 ? t.RowStyle : t.RowAltStyle);
                var res = new GUIStyle(t.Label);
                res.fontStyle = FontStyle.Bold;
                res.normal.textColor = r.Won ? t.Good : t.Warn;
                GUILayout.Label(r.Result, res, GUILayout.Width(16));
                GUILayout.Label(string.IsNullOrEmpty(r.OppArchetype) ? "(unknown deck)" : r.OppArchetype, t.Label);
                GUILayout.FlexibleSpace();
                GUILayout.Label(r.MyPrizesLeft + "-" + r.OppPrizesLeft, t.Value, GUILayout.Width(34));
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(4);
            GUILayout.Label("Prize column is prizes LEFT at the end, yours first.", t.Small);
        }

        /// <summary>
        /// Every deck in your inventory with its win/loss, so you can see a deck's record while
        /// you are choosing one. Listed most-recently-played first, with the active deck marked,
        /// and decks you have never played shown too - a blank record is information when picking.
        /// </summary>
        private void DrawDeckRecords()
        {
            var t = Theme;
            if (_decks == null || Time.unscaledTime >= _decksStale)
            {
                _decksStale = Time.unscaledTime + 5f;
                _decks = Game.AllDecks();
                _activeDeck = Game.ActiveDeckName();
            }
            if (_decks.Count == 0) return;

            GUILayout.Label("YOUR DECKS", t.Header);
            int i = 0;
            foreach (var d in _decks)
            {
                if (i >= 12) break;
                var w = History.ForDeck(d.Id, d.Name);
                GUILayout.BeginHorizontal(i++ % 2 == 0 ? t.RowStyle : t.RowAltStyle);

                bool active = !string.IsNullOrEmpty(_activeDeck) &&
                              string.Equals(_activeDeck, d.Name, StringComparison.OrdinalIgnoreCase);
                GUILayout.Label(d.Name, active ? t.Title : t.Label);
                GUILayout.FlexibleSpace();
                if (w.Played == 0)
                {
                    GUILayout.Label("-", t.Dim, GUILayout.Width(78));
                }
                else
                {
                    GUILayout.Label(w.Wins + "-" + w.Losses, t.Value, GUILayout.Width(40));
                    GUILayout.Label(Pct(w.Rate), t.Value, GUILayout.Width(38));
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(4);
            t.HLine(WindowRect.width);
            GUILayout.Space(4);
        }

        private List<DeckSummary> _decks;
        private string _activeDeck = "";
        private float _decksStale;

        private void Rates(string title, List<Winrate> rows)
        {
            var t = Theme;
            if (rows == null || rows.Count == 0) return;
            GUILayout.Space(6);
            GUILayout.Label(title, t.Header);
            int i = 0;
            foreach (var w in rows)
            {
                if (i >= 8) break;
                GUILayout.BeginHorizontal(i++ % 2 == 0 ? t.RowStyle : t.RowAltStyle);
                GUILayout.Label(w.Label, t.Label);
                GUILayout.FlexibleSpace();
                GUILayout.Label(w.Wins + "-" + w.Losses, t.Value, GUILayout.Width(40));
                GUILayout.Label(Pct(w.Rate), t.Value, GUILayout.Width(38));
                GUILayout.EndHorizontal();
            }
        }

        // ------------------------------------------------------------------
        private void DrawSettings()
        {
            var t = Theme;
            GUILayout.Label("FRAME RATE CAP", t.Header);
            if (Perf == null) { GUILayout.Label("unavailable", t.Dim); return; }

            GUILayout.BeginHorizontal(t.RowStyle);
            GUILayout.Label("Limiter", t.Label);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Perf.Enabled ? "ON" : "OFF", t.Btn, GUILayout.Width(52)))
            {
                Perf.Enabled = !Perf.Enabled;
                Perf.Apply();
                Changed();
            }
            GUILayout.EndHorizontal();

            FpsRow("In match", ref Perf.MatchFps, 1);
            FpsRow("In menus", ref Perf.MenuFps, 2);
            FpsRow("Alt-tabbed", ref Perf.UnfocusedFps, 3);

            GUILayout.Space(6);
            GUILayout.Label("Caps switch vsync off so the limit applies. 0 = uncapped.", t.Small);

            if (CardArt.Instance != null)
            {
                GUILayout.Space(8);
                GUILayout.Label("CARD ART", t.Header);
                GUILayout.Label(string.Format("{0} loaded, {1} unavailable",
                    CardArt.Instance.CachedCount, CardArt.Instance.FailedCount), t.Dim);
                GUILayout.Label(CardArt.Instance.MemoryCount + " textures found in memory", t.Small);
                GUILayout.Space(8);
                GUILayout.Label("DECK SCREEN", t.Header);
                GUILayout.Label(DeckBadge.Applied + " deck tiles badged this session", t.Dim);
                if (History != null && History.Count == 0)
                    GUILayout.Label("decks with no recorded games show nothing", t.Small);
            }
        }

        private static readonly int[] FpsSteps = { 0, 10, 15, 24, 30, 45, 60, 75, 90, 120, 144, 165, 240 };

        /// <summary>
        /// Takes the value by ref so the new number is committed BEFORE Changed() persists it -
        /// returning it instead meant SaveSettings wrote the pre-click value.
        /// </summary>
        private void FpsRow(string label, ref int value, int idx)
        {
            var t = Theme;
            GUILayout.BeginHorizontal(idx % 2 == 0 ? t.RowStyle : t.RowAltStyle);
            GUILayout.Label(label, t.Label);
            GUILayout.FlexibleSpace();
            bool changed = false;
            if (GUILayout.Button("-", t.Btn, GUILayout.Width(24))) { value = Step(value, -1); changed = true; }
            GUILayout.Label(value <= 0 ? "off" : value.ToString(), t.Value, GUILayout.Width(34));
            if (GUILayout.Button("+", t.Btn, GUILayout.Width(24))) { value = Step(value, +1); changed = true; }
            GUILayout.EndHorizontal();
            if (changed) { Perf.Apply(); Changed(); }
        }

        private static int Step(int current, int dir)
        {
            int nearest = 0;
            for (int i = 0; i < FpsSteps.Length; i++)
            {
                if (FpsSteps[i] == current) { nearest = i; break; }
                if (FpsSteps[i] < current) nearest = i;
            }
            return FpsSteps[Mathf.Clamp(nearest + dir, 0, FpsSteps.Length - 1)];
        }

        private void Changed()
        {
            var cb = OnSettingsChanged;
            if (cb != null) cb();
        }

        private static string Pct(double p)
        {
            if (p <= 0) return "-";
            if (p >= 0.995) return "100%";
            return Mathf.RoundToInt((float)(p * 100f)) + "%";
        }

        private void HandleResize()
        {
            var r = new Rect(WindowRect.width - Grip, WindowRect.height - Grip, Grip, Grip);
            var prev = GUI.color;
            GUI.color = Theme.Line;
            GUI.DrawTexture(r, Theme.LineTex);
            GUI.color = prev;

            var e = Event.current;
            if (!_resizing && e.type == EventType.MouseDown && r.Contains(e.mousePosition))
            {
                _resizing = true;
                _resizeStartMouse = e.mousePosition;
                _resizeStartSize = new Vector2(WindowRect.width, WindowRect.height);
                e.Use();
            }
            if (_resizing)
            {
                var d = e.mousePosition - _resizeStartMouse;
                WindowRect.width = Mathf.Clamp(_resizeStartSize.x + d.x, 240, Screen.width);
                WindowRect.height = Mathf.Clamp(_resizeStartSize.y + d.y, 180, Screen.height);
                if (e.type == EventType.MouseUp) { _resizing = false; Changed(); e.Use(); }
            }
        }
    }
}
