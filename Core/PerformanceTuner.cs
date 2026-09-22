using UnityEngine;

namespace PrizeTracker.Core
{
    /// <summary>
    /// Caps the client's frame rate.
    ///
    /// PTCGL renders an essentially static board as fast as the GPU allows, which on a decent card
    /// means hundreds of fps, a hot laptop and loud fans for no visual benefit whatsoever. Capping
    /// is the single cheapest quality-of-life win available to a mod here.
    ///
    /// Three separate caps, because the useful values differ a lot:
    ///   - in match:  what you actually play at
    ///   - in menus:  deck building / lobby, where even less is needed
    ///   - unfocused: alt-tabbed, where the game should be nearly free
    ///
    /// The cap is re-asserted on a timer rather than set once: Unity resets targetFrameRate across
    /// scene loads and the game's own quality/settings code writes to it too, so a one-shot
    /// assignment silently stops applying the moment you enter a match.
    /// </summary>
    internal class PerformanceTuner : MonoBehaviour
    {
        public int MatchFps = 60;
        public int MenuFps = 60;
        public int UnfocusedFps = 10;
        public bool Enabled = true;

        private float _next;
        private int _lastApplied = int.MinValue;
        private bool _focused = true;

        private void OnApplicationFocus(bool focus) { _focused = focus; _next = 0f; }

        private void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            Apply();
        }

        public void Apply()
        {
            if (!Enabled)
            {
                if (_lastApplied != -1)
                {
                    Application.targetFrameRate = -1;
                    _lastApplied = -1;
                }
                return;
            }

            int want = _focused ? (Game.InMatch() ? MatchFps : MenuFps) : UnfocusedFps;
            if (want <= 0) want = -1;

            // targetFrameRate is ignored entirely while vSync is on, so it has to be off for the
            // cap to mean anything.
            if (want > 0 && QualitySettings.vSyncCount != 0)
                QualitySettings.vSyncCount = 0;

            // Only take over background behaviour when we actually have a background cap to
            // enforce. Forcing runInBackground while UnfocusedFps is 0 (uncapped) would make
            // alt-tabbing WORSE than stock -- full-speed rendering on a window nobody is looking at.
            if (UnfocusedFps > 0 && !Application.runInBackground)
                Application.runInBackground = true;

            if (Application.targetFrameRate != want || _lastApplied != want)
            {
                Application.targetFrameRate = want;
                _lastApplied = want;
            }
        }

        public string Describe()
        {
            if (!Enabled) return "FPS cap: off";
            int want = _focused ? (Game.InMatch() ? MatchFps : MenuFps) : UnfocusedFps;
            string where = !_focused ? "unfocused" : (Game.InMatch() ? "match" : "menu");
            return string.Format("FPS cap: {0} ({1})", want <= 0 ? "off" : want.ToString(), where);
        }
    }
}
