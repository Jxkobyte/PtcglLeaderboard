using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using PtcglLeaderboard.Core;
using System;
using UnityEngine;

namespace PtcglLeaderboard
{
    /// <summary>
    /// PTCGL Leaderboard & Match History.
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
        private const string ID = "ptcgl.leaderboard";
        private const string NAME = "PTCGL Leaderboard & Match History";
        private const string VERSION = "1.0.3";

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
        private ConfigEntry<bool> _cfgEnabled, _cfgMigrated;

        // community leaderboard identity - managed automatically, no switches
        private ConfigEntry<string> _cfgLbName, _cfgLbLearnedName, _cfgLbApi, _cfgLbPlayerId;

        private void Awake()
        {
            Log = Logger;
            BindConfig();

            _host = new GameObject("PtcglLeaderboard_Systems");
            DontDestroyOnLoad(_host);
            _host.hideFlags = HideFlags.HideAndDontSave;

            _tracker = new Tracker();
            // Core has no BepInEx dependency by design, so it is handed a log sink instead.
            Tracker.Diagnostic = msg => Log.LogWarning(msg);

            // The match log lives OUTSIDE the game folder, in %LOCALAPPDATA%. Anything under
            // BepInEx\ sits inside the folder a PTCGL update can wipe, and history is the one
            // thing no repair can ever put back - we have no copy of the user's own record. The
            // old comment here claimed config survived updates; it is exactly what does not.
            DataPaths.MigrateFromLegacy(Paths.ConfigPath, m => Log.LogInfo(m));
            _history = new MatchHistory(DataPaths.MatchLog);
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
            native.ScreenName = "PtcglLeaderboardMatchHistoryScreen";
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
            Log.LogInfo("Match history: " + _history.Count + " recorded (" + DataPaths.MatchLog + ")");
            // ---- community leaderboard -------------------------------------------------
            // Season id, end date and the league ladder, from the client's own cached config
            // documents - the same response the client reads.
            _season = Season.Load(Season.DefaultCacheDir());
            Log.LogInfo(_season == null
                ? "season: no cached season config found"
                : "season " + _season.Id + " ends " +
                  (_season.EndUtc.HasValue ? _season.EndUtc.Value.ToString("u") : "?") +
                  " (" + _season.Leagues.Count + " leagues)");

            // The id is backed up outside the game folder, because a PTCGL update can wipe the
            // config file it lives in. An empty config with a backup present is exactly that case:
            // restore rather than mint a new id and become a second player on the board.
            if (string.IsNullOrEmpty(_cfgLbPlayerId.Value))
            {
                var saved = DataPaths.ReadPlayerId();
                if (saved != null)
                {
                    _cfgLbPlayerId.Value = saved;
                    Log.LogInfo("leaderboard: restored player id from " + DataPaths.PlayerIdFile);
                }
                else
                {
                    _cfgLbPlayerId.Value = Leaderboard.NewPlayerId();
                    Log.LogInfo("leaderboard: generated player id");
                }
            }
            DataPaths.WritePlayerId(_cfgLbPlayerId.Value, m => Log.LogWarning(m));
            _board = _host.AddComponent<Leaderboard>();
            _board.DisplayName = _cfgLbName.Value;
            _board.ApiBase = _cfgLbApi.Value;
            _board.PlayerId = _cfgLbPlayerId.Value;
            _board.Season = _season;
            _board.History = _history;
            _board.OnLearnedName = n => { _cfgLbLearnedName.Value = n; };
            if (!string.IsNullOrEmpty(_cfgLbLearnedName.Value)) _board.SetLearnedName(_cfgLbLearnedName.Value);

            var lbTab = _lbTab = _host.AddComponent<NavTab>();
            lbTab.TabName = "PtcglLeaderboardLeaderboardTab";
            lbTab.Caption = "LEADERBOARD";
            lbTab.InsertAfter = "CARD DEX";     // the end of the text tabs
            lbTab.History = _history;

            var lbNative = _lbNative = _host.AddComponent<NativeScreenInstaller>();
            lbNative.Tab = lbTab;
            lbNative.ScreenName = "PtcglLeaderboardLeaderboardScreen";
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
            _host.AddComponent<BattleLogCapture>();   // keeps each match's battle log text
#if DEVTOOLS
            _host.AddComponent<HotReload>(); // arms ScriptEngine's watcher so builds apply themselves
            _host.AddComponent<CardAspectProbe>(); // what shape does the CLIENT draw a card texture at
            _host.AddComponent<Probe>();     // one-shot structural dump of the game's menu system
#endif




            _detector = _host.AddComponent<MatchDetector>();
            _detector.History = _history;
            _detector.Board = _board;
            _detector.Initialize(Log, _tracker);

            // The LEADERBOARD & MATCH HISTORY card in the client's Settings screen: the one switch.
            _settings = _host.AddComponent<SettingsSection>();
            _settings.IsEnabled = () => _cfgEnabled.Value;
            _settings.SetEnabled = ApplyEnabled;

            ApplyEnabled(_cfgEnabled.Value);

#if DEVTOOLS
            Log.LogWarning(NAME + " v" + VERSION + " loaded (DEV build). F3 reload deck, F4 clipboard deck, F6 hot reload.");
#else
            Log.LogInfo(NAME + " v" + VERSION + " loaded.");
#endif
        }

        private void BindConfig()
        {

            _cfgEnabled = Config.Bind("General", "Enabled", true,
                "Whether the mod is on. Off switches off everything - the MATCH HISTORY and " +
                "LEADERBOARD tabs, deck win-rate badges, match recording and sharing your season " +
                "record with the leaderboard - except this mod's card in Settings, where " +
                "it can be switched back on. Also changeable from that card.");
            _cfgLbName = Config.Bind("Leaderboard", "DisplayName", "",
                "Name shown on the leaderboard. Leave empty to use your in-game name.");
            _cfgLbLearnedName = Config.Bind("Leaderboard", "InGameName", "",
                "Your in-game name, learned during a match. Managed automatically.");
            // The live service is the default: a player will never paste a URL into a config file,
            // so an empty default meant a leaderboard that silently never submitted. The worker keeps
            // its original "prizetracker" name because that is its deployed URL.
            _cfgLbApi = Config.Bind("Leaderboard", "Server", DefaultServer,
                "Base URL of the leaderboard service.");
            _cfgLbPlayerId = Config.Bind("Leaderboard", "PlayerId", "",
                "Random id identifying you on the leaderboard. Generated once. Not your account id.");
            _cfgMigrated = Config.Bind("Leaderboard", "MigratedFromPrizeTracker", false,
                "Managed automatically. Set once the identity from the old ptcgl.prizetracker.cfg has been carried over.");

            MigrateLegacyConfig();

        }

        private const string DefaultServer = "https://prizetracker-leaderboard.jakobi832.workers.dev";

        /// <summary>
        /// Carry the leaderboard identity over from the config file of the plugin's old name.
        ///
        /// BepInEx names a config file after the plugin GUID, so renaming ptcgl.prizetracker to
        /// ptcgl.leaderboard silently started a fresh file - and a fresh file means a fresh random
        /// PlayerId, which orphans the player's existing leaderboard row and makes them a second
        /// player. Runs once (guarded by MigratedFromPrizeTracker), before anything can submit, and
        /// never touches the old file. The old id WINS over one the new file generated, because the
        /// new one has never been submitted: until now the Server default was empty.
        /// </summary>
        private void MigrateLegacyConfig()
        {
            if (_cfgMigrated.Value) return;
            try
            {
                var legacy = System.IO.Path.Combine(Paths.ConfigPath, "ptcgl.prizetracker.cfg");
                if (System.IO.File.Exists(legacy))
                {
                    var old = ReadSection(legacy, "Leaderboard");
                    string v;
                    if (old.TryGetValue("PlayerId", out v) && v.Length > 0) _cfgLbPlayerId.Value = v;
                    if (old.TryGetValue("InGameName", out v) && v.Length > 0) _cfgLbLearnedName.Value = v;
                    if (old.TryGetValue("DisplayName", out v) && v.Length > 0) _cfgLbName.Value = v;
                    if (old.TryGetValue("Server", out v) && v.Length > 0) _cfgLbApi.Value = v;
                    Log.LogInfo("config: carried the leaderboard identity over from ptcgl.prizetracker.cfg");
                }
            }
            catch (Exception e) { Log.LogWarning("config migration failed: " + e.Message); return; }
            _cfgMigrated.Value = true;
        }

        /// <summary>key = value pairs of one [section] of a BepInEx .cfg - comments and other sections skipped.</summary>
        private static System.Collections.Generic.Dictionary<string, string> ReadSection(string path, string section)
        {
            var d = new System.Collections.Generic.Dictionary<string, string>();
            bool inside = false;
            foreach (var raw in System.IO.File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (line[0] == '[') { inside = line == "[" + section + "]"; continue; }
                if (!inside) continue;
                int eq = line.IndexOf('=');
                if (eq > 0) d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return d;
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
            else { try { DestroyAllNamed("PtcglLeaderboardWinrate"); } catch { } }

            Log.LogInfo(on ? "enabled" : "disabled - only the Settings card stays on");
        }

        private void Update()
        {

#if DEVTOOLS
            Tuning.Poll();

            if (_detector == null || !_cfgEnabled.Value) return;

            if (Input.GetKeyDown(KeyCode.F3))
                _detector.TryLoadDeck(true);

            if (Input.GetKeyDown(KeyCode.F4))
                _detector.TryLoadDeckFromClipboard();
#endif

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
            // Cameras back where they were, blocks gone, the client's floor decal switched back
            // on. The screen's own OnDeactivate does this normally, but a hot reload tears the
            // screen down without deactivating it.
            try { PodiumAvatars.Release(null); } catch { }

            try { _harmony.UnpatchSelf(); }
            catch (Exception e) { Log.LogWarning("unpatch failed: " + e.Message); }

            foreach (var name in new[]
            {
                "PtcglLeaderboardHistoryTab",          // clone in the client's top bar
                "PtcglLeaderboardMatchHistoryScreen",  // screen under InactiveScreens
                "PtcglLeaderboardSettings",            // card in the client's Settings screen
                "PtcglLeaderboardHistoryCanvas",       // fallback overlay canvas
                "PtcglLeaderboardSlotArt",             // art painted into prize slots (old approach)
                "PtcglLeaderboardLeaderboardTab",      // second clone in the top bar
                "PtcglLeaderboardLeaderboardScreen",   // its screen under InactiveScreens
                "PtcglLeaderboardWinrate",             // win/loss labels cloned onto deck tiles
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
