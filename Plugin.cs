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
        private Overlay _overlay;
        private Tracker _tracker;
        private MatchDetector _detector;
        private PerformanceTuner _perf;
        private MatchHistory _history;
        private SettingsSection _settings;

        // persisted settings
        private ConfigEntry<bool> _cfgFpsEnabled;
        private ConfigEntry<int> _cfgMatchFps, _cfgMenuFps, _cfgUnfocusedFps;
        private ConfigEntry<float> _cfgX, _cfgY, _cfgW, _cfgH;
        private ConfigEntry<bool> _cfgVisible;
        private ConfigEntry<bool> _cfgDeckBadge;

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
            var nav = _host.AddComponent<NavTab>();
            nav.History = _history;
            nav.Screen = screen;

            // Promote the tab to a real screen in the client's own navigation. If this cannot find
            // what it expects it installs nothing and says so, and the overlay screen above keeps
            // working - so the feature degrades rather than disappearing.
            var native = _host.AddComponent<NativeScreenInstaller>();
            native.History = _history;
            native.Tab = nav;
            native.SelfTest = Config.Bind("Debug", "SelfTestOpenHistoryOnStart", false,
                "Open the Match History screen once at startup and log the result. For verifying "
                + "the screen without needing to drive the mouse.").Value;
            DeckBadge.Enabled = _cfgDeckBadge.Value;

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
            _host.AddComponent<CardArt>();   // serves card textures to the overlay
            _host.AddComponent<BattleLogCapture>();   // keeps each match's battle log text
            _host.AddComponent<Probe>();     // one-shot structural dump of the game's menu system

            _perf = _host.AddComponent<PerformanceTuner>();
            _perf.Enabled = _cfgFpsEnabled.Value;
            _perf.MatchFps = _cfgMatchFps.Value;
            _perf.MenuFps = _cfgMenuFps.Value;
            _perf.UnfocusedFps = _cfgUnfocusedFps.Value;
            _perf.Apply();

            // Must come AFTER _perf exists - wiring it earlier passed a null and the frame-cap
            // row silently did nothing.
            _settings = _host.AddComponent<SettingsSection>();
            _settings.Perf = _perf;
            _settings.OnChanged = SaveSettings;

            _overlay = new Overlay();
            _overlay.Tracker = _tracker;
            _overlay.Perf = _perf;
            _overlay.History = _history;
            _overlay.Visible = _cfgVisible.Value;
            _overlay.WindowRect = new Rect(_cfgX.Value, _cfgY.Value, _cfgW.Value, _cfgH.Value);
            _overlay.OnSettingsChanged = SaveSettings;

            // AFTER _overlay exists, for the same reason _settings.Perf is wired after _perf:
            // assigning it earlier stores a null and the toggle silently does nothing.
            _settings.Ov = _overlay;

            _detector = _host.AddComponent<MatchDetector>();
            _detector.History = _history;
            _detector.Initialize(Log, _tracker);

            Log.LogWarning(NAME + " v" + VERSION + " loaded. F1 overlay, F3 reload deck, F4 clipboard deck.");
        }

        private void BindConfig()
        {
            _cfgVisible = Config.Bind("Overlay", "Visible", true, "Show the overlay on startup.");
            _cfgX = Config.Bind("Overlay", "X", 40f, "Overlay position X.");
            _cfgY = Config.Bind("Overlay", "Y", 120f, "Overlay position Y.");
            _cfgW = Config.Bind("Overlay", "Width", 340f, "Overlay width.");
            _cfgH = Config.Bind("Overlay", "Height", 420f, "Overlay height.");

            _cfgDeckBadge = Config.Bind("Overlay", "ShowDeckWinrates", true,
                "Draw each deck's win/loss record onto the game's own deck tiles in the deck screen.");

            _cfgFpsEnabled = Config.Bind("Performance", "LimitFrameRate", true,
                "Cap the client's frame rate. PTCGL otherwise renders a static board at hundreds of fps.");
            _cfgMatchFps = Config.Bind("Performance", "MatchFps", 60,
                "Frame cap while in a match. 0 = uncapped.");
            _cfgMenuFps = Config.Bind("Performance", "MenuFps", 60,
                "Frame cap in menus / deck builder. 0 = uncapped.");
            _cfgUnfocusedFps = Config.Bind("Performance", "UnfocusedFps", 10,
                "Frame cap while the game window is not focused. 0 = uncapped.");
        }

        private void SaveSettings()
        {
            if (_overlay == null || _perf == null) return;
            _cfgVisible.Value = _overlay.Visible;
            _cfgX.Value = _overlay.WindowRect.x;
            _cfgY.Value = _overlay.WindowRect.y;
            _cfgW.Value = _overlay.WindowRect.width;
            _cfgH.Value = _overlay.WindowRect.height;

            _cfgDeckBadge.Value = DeckBadge.Enabled;
            _cfgFpsEnabled.Value = _perf.Enabled;
            _cfgMatchFps.Value = _perf.MatchFps;
            _cfgMenuFps.Value = _perf.MenuFps;
            _cfgUnfocusedFps.Value = _perf.UnfocusedFps;
            _perf.Apply();
        }

        private void Update()
        {
            if (_overlay == null) return;

            if (Input.GetKeyDown(KeyCode.F1))
            {
                _overlay.ToggleRequested();
                SaveSettings();
            }

            // Keep the overlay in step with whether a match is actually running.
            _overlay.InMatch = Game.InMatch();

            if (_detector == null) return;

            if (Input.GetKeyDown(KeyCode.F3))
                _detector.TryLoadDeck(true);

            if (Input.GetKeyDown(KeyCode.F4))
                _detector.TryLoadDeckFromClipboard();

            // F6 dumps whatever UI is on screen, for working out how a screen is built without
            // guessing and restarting the game each time.
            if (Input.GetKeyDown(KeyCode.F6) && _settings != null)
                _settings.DumpNow();
        }

        private void OnGUI()
        {
            if (_overlay != null) _overlay.OnGUI();
        }

        private void OnDestroy()
        {
            SaveSettings();
            if (_host != null) Destroy(_host);
        }
    }
}
