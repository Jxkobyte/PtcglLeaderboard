using System;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PrizeTracker.Core
{
    /// <summary>
    /// Registers the Match History screen with the client's own menu navigation, and makes the
    /// cloned nav tab drive it the way a real tab drives a real screen.
    ///
    /// How the client does it, established by decompiling TPCI.RainierClient and dumping the live
    /// hierarchy (see Probe):
    ///   - each top-bar tab carries a PersistentNavButtonGroup whose _navDestination is the screen
    ///     it opens, and whose selected look (yellow label + the TabIndicator underline) is an
    ///     Animator driven by Activate()/Deactivate() on that group;
    ///   - HUBTopController_L binds every tab's button to UpdatePersistentNav, which ends in
    ///     BusyNavigation: deactivate the old tab, activate the new one, then
    ///     navDestination.Activate() - which is navigation.GoToScreen(screen).
    ///
    /// We reuse all of that EXCEPT BusyNavigation. That method marks the client busy and then spins
    /// on `currentNav.IsOn()`, an Animator state check; if a tab we assembled ever failed to reach
    /// that state the menu would stay busy forever, which is a soft-lock. Doing its two meaningful
    /// lines ourselves costs nothing and cannot hang. Everything else - the animator, the screen
    /// transition, the back stack - is the client's own code on the client's own objects.
    ///
    /// Failure is contained: if any step does not find what it expects, nothing is installed, the
    /// caller is told, and the old overlay screen keeps working.
    /// </summary>
    /// <summary>A screen the installer can create: it gets Build() once navigation is wired.</summary>
    internal interface INativeScreen
    {
        void Build();
    }

    internal class NativeScreenInstaller : MonoBehaviour
    {
        /// <summary>The cloned tab that drives this screen.</summary>
        public NavTab Tab;

        /// <summary>GameObject name for the screen, also used by Plugin.OnDestroy to clean up.</summary>
        public string ScreenName = "PrizeTrackerMatchHistoryScreen";

        /// <summary>Short name for log lines.</summary>
        public string Label = "history";

        /// <summary>
        /// Attach the screen component to the GameObject and return it. The installer then sets
        /// the client's navigation onto it and calls Build() (via INativeScreen).
        /// </summary>
        public Func<GameObject, HUBScreenController> Attach;

        /// <summary>
        /// Open the screen once, by itself, shortly after installing, and report what happened.
        ///
        /// Verifying this by driving the mouse needs the game to hold the foreground, which Windows
        /// refuses while the user is working in another window - the clicks then land in whatever
        /// app IS focused. Asking the plugin to navigate itself removes that dependency entirely
        /// and reports the resulting state rather than a screenshot's worth of inference.
        ///
        /// Off unless switched on in the config file.
        /// </summary>
        public bool SelfTest;

        /// <summary>Set once the screen is live; NavTab stops hand-rolling tab styling.</summary>
        public bool Installed { get; private set; }

        private HUBScreenController _screen;
        private PersistentNavButtonGroup _group;
        private HUBTopController_L _top;
        private MainMenuNavigation _nav;
        private Transform _installedTab;
        private float _next;
        private int _attempts;

        private void Update()
        {
            // The top bar is rebuilt on navigation, so NavTab re-clones the tab. When it does, the
            // wiring belongs to a tab that no longer exists and has to be redone - but the screen
            // itself is kept, along with anything already on it.
            if (_selfTestAt > 0f && Time.unscaledTime >= _selfTestAt) { _selfTestAt = 0f; RunSelfTest(); }
            if (Installed && Tab != null && Tab.TabRoot == _installedTab) return;
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;

            try { TryInstall(); }
            catch (Exception e)
            {
                Installed = false;
                _attempts = int.MaxValue;
                Plugin.Log.LogWarning("native " + Label + " screen not installed (" + e.Message + ").");
            }
        }

        private void TryInstall()
        {
            if (Tab == null || Tab.TabRoot == null) return;

            var nav = Resources.FindObjectsOfTypeAll<MainMenuNavigation>()
                               .FirstOrDefault(n => n != null && n.isActiveAndEnabled &&
                                                    n.gameObject.scene.IsValid());
            if (nav == null) return;

            // Count attempts only from the point the menu actually EXISTS. Counting from plugin
            // load would have expired the budget during the client's own two-minute startup and
            // left this quietly doing nothing - the exact failure shape this project keeps hitting.
            if (_attempts++ > 60)
            {
                if (_attempts == 62) Plugin.Log.LogWarning(
                    "native " + Label + " screen: menu is up but the expected objects never appeared.");
                return;
            }

            _top = Resources.FindObjectsOfTypeAll<HUBTopController_L>()
                            .FirstOrDefault(t => t != null && t.gameObject.scene.IsValid());
            if (_top == null) return;

            var parent = FindInactiveScreens(nav.transform);
            if (parent == null) return;

            _group = Tab.TabRoot.GetComponent<PersistentNavButtonGroup>();
            if (_group == null)
                throw new InvalidOperationException("the cloned tab has no PersistentNavButtonGroup");

            var button = _group.navButton;
            if (button == null) button = Tab.TabRoot.GetComponentInChildren<Button>(true);
            if (button == null)
                throw new InvalidOperationException("the cloned tab has no Button");

            // ---- the screen ------------------------------------------------
            if (_screen != null) { WireTab(button); return; }

            if (Attach == null) throw new InvalidOperationException("no screen factory");
            var go = new GameObject(ScreenName, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            NativeHistoryScreen.Stretch((RectTransform)go.transform);

            _screen = Attach(go);
            if (_screen == null) throw new InvalidOperationException("screen factory returned nothing");

            // HUBScreenController.Activate() is navigation.GoToScreen(this), and `navigation` is a
            // private serialized field with no setter - inspector-assigned on the real screens.
            var f = typeof(HUBScreenController).GetField("navigation",
                        BindingFlags.Instance | BindingFlags.NonPublic);
            if (f == null)
                throw new InvalidOperationException("HUBScreenController.navigation field not found");
            f.SetValue(_screen, nav);
            _nav = nav;

            var buildable = _screen as INativeScreen;
            if (buildable != null) buildable.Build();

            // Real screens sit under InactiveScreens with activeSelf still true - they are hidden by
            // where they are parented, not by being switched off - and SetScreen never re-enables
            // them, so starting inactive would mean never appearing at all.
            go.SetActive(true);

            // ---- the tab ---------------------------------------------------
            WireTab(button);
            Plugin.Log.LogWarning("native " + Label + " screen installed under " + parent.name +
                                  "; tab drives it through the client's own navigation.");
        }

        /// <summary>Point this tab at our screen. Safe to repeat after the bar is rebuilt.</summary>
        private void WireTab(Button button)
        {
            var dest = typeof(PersistentNavButtonGroup).GetField("_navDestination",
                           BindingFlags.Instance | BindingFlags.NonPublic);
            if (dest != null) dest.SetValue(_group, _screen);
            _group.navButton = button;

            Tab.GoNative();          // drop the stand-in click catcher and the hand-rolled styling

            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(Navigate);

            _installedTab = Tab.TabRoot;
            Installed = true;

            if (SelfTest && !_selfTestScheduled)
            {
                _selfTestScheduled = true;
                // Late enough that the menu's own opening transition has finished - GoToScreen
                // refuses while a screen holder is still animating.
                _selfTestAt = Time.unscaledTime + 10f;
            }
        }

        /// <summary>
        /// The meaningful half of HUBTopController_L.BusyNavigation, without the busy-wait.
        /// </summary>
        private void Navigate()
        {
            try
            {
                var f = typeof(HUBTopController_L).GetField("currentNav",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                var current = f != null ? f.GetValue(_top) as PersistentNavButtonGroup : null;
                if (current == _group) return;            // already here

                // Move the SCREEN first, and only claim the tab if it actually moved.
                //
                // Doing it the other way round produced a UI that lied: the tab lit up and stayed
                // lit while the content behind it was still the previous screen. HUBScreenController
                // .Activate() is gated on a busy check and GoToScreen bails mid-transition, so it
                // can legitimately decline - and when it does, the honest thing is to change nothing.
                _screen.Activate();
                if (_nav.currentScreen != _screen) _nav.GoToScreen(_screen);   // past the busy gate

                if (_nav.currentScreen != _screen)
                {
                    Plugin.Log.LogWarning(Label + " screen declined to open (currentScreen=" +
                        (_nav.currentScreen == null ? "null" : _nav.currentScreen.GetType().Name) +
                        "); leaving the tabs as they were.");
                    return;
                }

                if (current != null) current.Deactivate();
                _group.Activate();                        // yellow label + TabIndicator, the real way
                if (f != null) f.SetValue(_top, _group);  // so the next real tab click deselects us

                Plugin.Log.LogInfo(Label + " screen opened; parent=" +
                                   _screen.transform.parent.name);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning(Label + " tab navigation failed: " + e);
            }
        }

        private float _selfTestAt;
        private bool _selfTestScheduled;
        private int _selfTestTries;

        private void RunSelfTest()
        {
            try
            {
                Plugin.Log.LogWarning("self-test: opening the " + Label + " screen (attempt " +
                                      (_selfTestTries + 1) + ")...");
                Navigate();

                // The client refuses to navigate while its busy provider is set or a screen holder
                // is mid-transition, and after a cold start that can last a while. Retrying is the
                // difference between a diagnostic that works and one that reports a false failure.
                if (_nav != null && _nav.currentScreen != _screen && ++_selfTestTries < 12)
                {
                    _selfTestAt = Time.unscaledTime + 3f;
                    return;
                }

                var t = _screen.transform;
                var path = t.name;
                for (var a = t.parent; a != null; a = a.parent) path = a.name + "/" + path;
                var rt = (RectTransform)t;
                var canvas = _screen.GetComponentInParent<Canvas>();

                Plugin.Log.LogWarning("self-test: screen parent=" + path);
                Plugin.Log.LogWarning("self-test: activeInHierarchy=" + _screen.gameObject.activeInHierarchy +
                                      " size=" + rt.rect.size + " canvas=" +
                                      (canvas != null ? canvas.name + " order=" + canvas.sortingOrder : "none"));
                Plugin.Log.LogWarning("self-test: tab width=" + ((RectTransform)Tab.TabRoot).rect.width +
                                      " clickCatcher=" + (Tab.TabRoot.Find("ClickCatcher") != null) +
                                      " label=\"" + Tab.TabRoot.GetComponentInChildren<TMPro.TextMeshProUGUI>(true).text + "\"");
            }
            catch (Exception e) { Plugin.Log.LogWarning("self-test failed: " + e); }
        }

        /// <summary>Where the client parks screens that are not currently shown.</summary>
        private static Transform FindInactiveScreens(Transform canvas)
        {
            foreach (var t in canvas.GetComponentsInChildren<Transform>(true))
                if (t.name == "InactiveScreens") return t;
            return null;
        }
    }
}
