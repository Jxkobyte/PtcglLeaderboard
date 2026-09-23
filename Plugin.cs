using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using PrizeTracker.Core;
using System;
using UnityEngine;

namespace PrizeTracker
{
    /// <summary>
    /// PTCGL Prize Tracker.
    ///
    /// Standalone: this plugin does nothing except read the live match state and draw an overlay.
    /// It contains none of the PokeAI simulator tooling (card/game-data export, bulk card fetch)
    /// that lived in the old combined GameStateReader plugin - that stays in the AI project.
    ///
    /// The GUID is deliberately distinct from the old plugin's ("PrizeChecker") so that if both
    /// DLLs are ever present BepInEx loads them as separate plugins rather than silently picking
    /// one. Do not reuse the old GUID.
    /// </summary>
    [BepInPlugin(ID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        private const string ID = "ptcgl.prizetracker";
        private const string NAME = "PTCGL Prize Tracker";
        private const string VERSION = "1.0.0";

        internal static ManualLogSource Log;
        private readonly Harmony _harmony = new Harmony(ID);

        private GameObject _host;
        private Tracker _tracker;
        private MatchDetector _detector;
        private MatchHistory _history;
        private Leaderboard _board;
        private Season _season;

        // Everything the one setting switches off. Only the Settings card itself stays on,
        // so the mod can be switched back on from where it was switched off.
        private NavTab _historyTab, _lbTab;
        private NativeScreenInstaller _historyNative, _lbNative;
        private DeckBadgeRefresher _badges;
        private SettingsSection _settings;

        // The one persisted setting: whether the mod is on at all. Sharing the season record
        // with the leaderboard is part of what the mod does, not a separate switch.
        private ConfigEntry<bool> _cfgEnabled;

        // community leaderboard identity - managed automatically, no switches
        private ConfigEntry<string> _cfgLbName, _cfgLbLearnedName, _cfgLbApi, _cfgLbPlayerId;

        private void Awake()
        {
            Log = Logger;
            BindConfig();

            _host = new GameObject("PrizeTracker_Systems");
            DontDestroyOnLoad(_host);
            _host.hideFlags = HideFlags.HideAndDontSave;

            _tracker = new Tracker();
            // Core has no BepInEx dependency by design, so it is handed a log sink instead.
            Tracker.Diagnostic = msg => Log.LogWarning(msg);

            // Match log lives beside the plugin config so it survives game updates.
            var historyPath = System.IO.Path.Combine(Paths.ConfigPath, "PrizeTracker", "matches.jsonl");
            _history = new MatchHistory(historyPath);
            _history.Load();
            DeckBadge.History = _history;

            // "MATCH HISTORY" tab in the game's own nav bar, plus the screen it opens.
            var screen = _host.AddComponent<HistoryScreen>();
            screen.History = _history;
            var nav = _historyTab = _host.AddComponent<NavTab>();
            nav.History = _history;
            nav.Screen = screen;

            // Promote the tab to a real screen in the client's own navigation. If this cannot find
            // what it expects it installs nothing and says so, and the overlay screen above keeps
            // working - so the feature degrades rather than disappearing.
            var native = _historyNative = _host.AddComponent<NativeScreenInstaller>();
            native.Tab = nav;
            native.ScreenName = "PrizeTrackerMatchHistoryScreen";
            native.Label = "history";
            var history = _history;
            native.Attach = go =>
            {
                var s = go.AddComponent<NativeHistoryScreen>();
                s.History = history;
                return s;
            };
            native.SelfTest = Config.Bind("Debug", "SelfTestOpenHistoryOnStart", false,
                "Open the Match History screen once at startup and log the result. For verifying "
                + "the screen without needing to drive the mouse.").Value;
            // The Harmony patch only fires when a tile binds to a deck, so tiles already on screen
            // keep the previous build's badges after a reload. This sweeps them.
            _badges = _host.AddComponent<DeckBadgeRefresher>();

            // Patches DeckSelectEntry.Setup so each deck tile in the deck screen shows its record.
            try
            {
                _harmony.PatchAll();
                int n = 0;
                foreach (var m in _harmony.GetPatchedMethods()) { n++; Log.LogInfo("patched: " + m.DeclaringType + "." + m.Name); }
                if (n == 0) Log.LogWarning("no methods were patched - deck win-rate badges will not appear.");
            }
            catch (Exception e) { Log.LogWarning("deck screen patch failed: " + e.Message); }
            Log.LogInfo("Match history: " + _history.Count + " recorded (" + historyPath + ")");
            // ---- community leaderboard -------------------------------------------------
            // Season id, end date and the league ladder, from the client's own cached config
            // documents - the same response the client reads.
            _season = Season.Load(Season.DefaultCacheDir());
            Log.LogInfo(_season == null
                ? "season: no cached season config found"
                : "season " + _season.Id + " ends " +
                  (_season.EndUtc.HasValue ? _season.EndUtc.Value.ToString("u") : "?") +
                  " (" + _season.Leagues.Count + " leagues)");

            if (string.IsNullOrEmpty(_cfgLbPlayerId.Value))
            {
                _cfgLbPlayerId.Value = Leaderboard.NewPlayerId();
                Log.LogInfo("leaderboard: generated player id");
            }
            _board = _host.AddComponent<Leaderboard>();
            _board.DisplayName = _cfgLbName.Value;
            _board.ApiBase = _cfgLbApi.Value;
            _board.PlayerId = _cfgLbPlayerId.Value;
            _board.Season = _season;
            _board.History = _history;
            _board.OnLearnedName = n => { _cfgLbLearnedName.Value = n; };
            if (!string.IsNullOrEmpty(_cfgLbLearnedName.Value)) _board.SetLearnedName(_cfgLbLearnedName.Value);

            var lbTab = _lbTab = _host.AddComponent<NavTab>();
            lbTab.TabName = "PrizeTrackerLeaderboardTab";
            lbTab.Caption = "LEADERBOARD";
            lbTab.InsertAfter = "CARD DEX";     // the end of the text tabs
            lbTab.History = _history;

            var lbNative = _lbNative = _host.AddComponent<NativeScreenInstaller>();
            lbNative.Tab = lbTab;
            lbNative.ScreenName = "PrizeTrackerLeaderboardScreen";
            lbNative.Label = "leaderboard";
            var board = _board; var season = _season;
            lbNative.Attach = go =>
            {
                var s = go.AddComponent<NativeLeaderboardScreen>();
                s.Board = board;
                s.Season = season;
                s.History = history;   // the podium's avatars come from matches we watched
                return s;
            };

            _host.AddComponent<CardArt>();   // serves card textures to the overlay
            _host.AddComponent<ItemArt>();   // serves sleeve/box/coin thumbnails
            _host.AddComponent<HotReload>(); // arms ScriptEngine's watcher so builds apply themselves
            _host.AddComponent<CardAspectProbe>(); // what shape does the CLIENT draw a card texture at
            _host.AddComponent<BattleLogCapture>();   // keeps each match's battle log text
            _host.AddComponent<Probe>();     // one-shot structural dump of the game's menu system




            _detector = _host.AddComponent<MatchDetector>();
            _detector.History = _history;
            _detector.Board = _board;
            _detector.Initialize(Log, _tracker);

            // The PRIZE TRACKER card in the client's Settings screen: the one switch.
            _settings = _host.AddComponent<SettingsSection>();
            _settings.IsEnabled = () => _cfgEnabled.Value;
            _settings.SetEnabled = ApplyEnabled;

            ApplyEnabled(_cfgEnabled.Value);

            Log.LogWarning(NAME + " v" + VERSION + " loaded. F3 reload deck, F4 clipboard deck, F6 hot reload, F8 dump UI.");
        }

        private void BindConfig()
        {

            _cfgEnabled = Config.Bind("General", "Enabled", true,
                "Whether the mod is on. Off switches off everything - the MATCH HISTORY and " +
                "LEADERBOARD tabs, deck win-rate badges, match recording and sharing your season " +
                "record with the leaderboard - except the PRIZE TRACKER card in Settings, where " +
                "it can be switched back on. Also changeable from that card.");
            _cfgLbName = Config.Bind("Leaderboard", "DisplayName", "",
                "Name shown on the leaderboard. Leave empty to use your in-game name.");
            _cfgLbLearnedName = Config.Bind("Leaderboard", "InGameName", "",
                "Your in-game name, learned during a match. Managed automatically.");
            _cfgLbApi = Config.Bind("Leaderboard", "Server", "",
                "Base URL of the leaderboard service, e.g. https://prizetracker-leaderboard.example.workers.dev");
            _cfgLbPlayerId = Config.Bind("Leaderboard", "PlayerId", "",
                "Random id identifying you on the leaderboard. Generated once. Not your account id.");

        }

        /// <summary>
        /// Switch everything on or off. Only the Settings card is left running when off - it is
        /// how the mod gets switched back on - and the config entry saves itself on assignment.
        /// </summary>
        private void ApplyEnabled(bool on)
        {
            _cfgEnabled.Value = on;

            if (_historyTab != null) _historyTab.SetHidden(!on);
            if (_lbTab != null) _lbTab.SetHidden(!on);
            if (_historyNative != null) _historyNative.enabled = on;
            if (_lbNative != null) _lbNative.enabled = on;
            if (_detector != null) _detector.Active = on;
            if (_board != null) _board.Enabled = on;
            if (_badges != null) _badges.enabled = on;

            DeckBadge.Enabled = on;
            // Badges already painted onto deck tiles are not un-painted by the flag alone.
            if (on) DeckBadge.RefreshExisting();
            else { try { DestroyAllNamed("PrizeTrackerWinrate"); } catch { } }

            Log.LogInfo(on ? "enabled" : "disabled - only the Settings card stays on");
        }

        private void Update()
        {

            Tuning.Poll();

            if (_detector == null || !_cfgEnabled.Value) return;

            if (Input.GetKeyDown(KeyCode.F3))
                _detector.TryLoadDeck(true);

            if (Input.GetKeyDown(KeyCode.F4))
                _detector.TryLoadDeckFromClipboard();

            // F8 dumps whatever UI is on screen, for working out how a screen is built without
            // guessing and restarting the game each time.
            //
            // NOT F6: that is ScriptEngine's reload key, so pressing it would dump the UI from the
            // instance being torn down at the same moment a new one is loading - noise in the log
            // at exactly the point the log matters most.
        }

        /// <summary>
        /// Leave the client exactly as we found it.
        ///
        /// This matters for hot reloading: everything below is injected INTO the client's own
        /// hierarchy and survives the plugin being unloaded. Without cleanup a reload leaves the
        /// old nav tab, screen and settings card in place and adds a second set - and re-applies
        /// the Harmony patch on top of the existing one, so DeckSelectEntry.Setup runs our postfix
        /// twice. It is also simply correct: a plugin that unloads should not leave litter.
        /// </summary>
        private void OnDestroy()
        {
            try { _harmony.UnpatchSelf(); }
            catch (Exception e) { Log.LogWarning("unpatch failed: " + e.Message); }

            foreach (var name in new[]
            {
                "PrizeTrackerHistoryTab",          // clone in the client's top bar
                "PrizeTrackerMatchHistoryScreen",  // screen under InactiveScreens
                "PrizeTrackerSettings",            // card in the client's Settings screen
                "PrizeTrackerHistoryCanvas",       // fallback overlay canvas
                "PrizeTrackerSlotArt",             // art painted into prize slots (old approach)
                "PrizeTrackerLeaderboardTab",      // second clone in the top bar
                "PrizeTrackerLeaderboardScreen",   // its screen under InactiveScreens
                "PrizeTrackerWinrate",             // win/loss labels cloned onto deck tiles
            })
            {
                try { DestroyAllNamed(name); } catch { }
            }

            if (_host != null) Destroy(_host);
        }

        private static void DestroyAllNamed(string name)
        {
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t == null || t.name != name) continue;
                if (!t.gameObject.scene.IsValid()) continue;   // never touch prefabs
                DestroyImmediate(t.gameObject);
            }
        }
    }
}
