using System;
using System.IO;
using System.Reflection;
using BepInEx.Bootstrap;
using UnityEngine;

namespace PrizeTracker.Core
{
    /// <summary>
    /// Turns ScriptEngine's auto-reload on from inside the game, and holds a reload back until a
    /// match is over.
    ///
    /// ScriptEngine only reads EnableFileSystemWatcher in its own Awake, so setting it in the
    /// config file does nothing until the next launch - which is exactly the restart the whole
    /// hot-reload setup exists to avoid. StartFileSystemWatcher() is self-contained though
    /// (it builds a FileSystemWatcher over the scripts folder and subscribes its own handler), so
    /// calling it is enough to start the watcher in the session that is already running. After
    /// that ScriptEngine does the work itself: a rebuilt DLL lands, its handler fires, and the
    /// plugin reloads with no keypress.
    ///
    /// Its filter is "*.dll", so the .pdb written beside the DLL cannot trigger a second reload.
    ///
    /// The match hold exists because a reload resets statics and kills coroutines - it drops
    /// everything the tracker has worked out, so a build landing mid-game would silently wipe the
    /// solved prizes and leave the display looking broken. Rather than cancel the reload, which
    /// would quietly lose the build, it keeps pushing ScriptEngine's own countdown out while a
    /// match is running and lets it fire the moment the match ends.
    /// </summary>
    internal class HotReload : MonoBehaviour
    {
        private const string ScriptEngineGuid = "com.bepis.bepinex.scriptengine";

        /// <summary>
        /// Drop this file to apply a held build immediately, without touching the keyboard.
        ///
        /// The hold is worth keeping - losing solved prizes mid-match cannot simply be undone,
        /// since you cannot search your deck on demand - but it should not be the PLAYER who has
        /// to override it. Whoever made the build knows whether it is safe to apply mid-match, so
        /// the override is a file they can write rather than a key the player has to press.
        /// </summary>
        public static string ReleasePath
        {
            get { return System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "PrizeTracker/reload-now"); }
        }

        public Tracker Tracker;

        private object _engine;
        private FieldInfo _shouldReload, _timer, _watcher;
        private bool _held;

        /// <summary>
        /// Is there actually anything a reload would destroy?
        ///
        /// Holding for the whole match was too blunt: it also blocked builds that only touch
        /// diagnostics, at exactly the moment a match is running and the diagnostics are wanted.
        /// What a reload really costs is the SOLVED PRIZES - so hold for those, and not for the
        /// mere fact of being in a match.
        /// </summary>
        /// <summary>True once, if the release marker is present. Consumes it.</summary>
        private bool Released()
        {
            try
            {
                if (!File.Exists(ReleasePath)) return false;
                File.Delete(ReleasePath);
                Plugin.Log.LogWarning("reload released by marker - applying the held build now.");
                return true;
            }
            catch { return false; }
        }

        private bool WouldLoseWork()
        {
            if (!Game.InMatch()) return false;
            if (Tracker == null) return false;
            foreach (var p in Tracker.PrizeSlots) if (p.Known) return true;
            return false;
        }

        private void Start()
        {
            try { Install(); }
            catch (Exception e) { Plugin.Log.LogWarning("hot reload not armed: " + e.Message); }
        }

        private void Install()
        {
            BepInEx.PluginInfo info;
            if (!Chainloader.PluginInfos.TryGetValue(ScriptEngineGuid, out info) || info.Instance == null)
            {
                Plugin.Log.LogInfo("ScriptEngine not present - builds will need a restart to take effect.");
                return;
            }

            _engine = info.Instance;
            var t = _engine.GetType();
            const BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;

            _shouldReload = t.GetField("shouldReload", Priv);
            _timer = t.GetField("autoReloadTimer", Priv);
            _watcher = t.GetField("fileSystemWatcher", Priv);

            // Only if it is not already watching. Our own plugin is reloaded by this very
            // mechanism, so Start() runs again on every reload - starting a second watcher each
            // time would leak one per build and reload us once per watcher.
            if (_watcher != null && _watcher.GetValue(_engine) == null)
            {
                var start = t.GetMethod("StartFileSystemWatcher", Priv);
                if (start == null) { Plugin.Log.LogWarning("ScriptEngine has no StartFileSystemWatcher."); return; }
                start.Invoke(_engine, null);
                Plugin.Log.LogWarning("hot reload armed: rebuilding the plugin now reloads it automatically.");
            }
            else
            {
                Plugin.Log.LogInfo("hot reload already armed.");
            }
        }

        private void Update()
        {
            if (_engine == null || _shouldReload == null || _timer == null) return;

            bool pending;
            try { pending = (bool)_shouldReload.GetValue(_engine); }
            catch { _engine = null; return; }

            if (!pending) { _held = false; return; }

            if (WouldLoseWork() && !Released())
            {
                // Keep it just out of reach. ScriptEngine subtracts unscaledDeltaTime every frame
                // and fires at zero, so holding the countdown above zero defers without cancelling.
                try { _timer.SetValue(_engine, 5f); } catch { }
                if (!_held)
                {
                    _held = true;
                    Plugin.Log.LogWarning("new build detected - holding the reload until this match ends "
                                          + "(prizes are solved and a reload would wipe them). "
                                          + "Create " + ReleasePath + " to apply it now.");
                }
                return;
            }

            if (_held)
            {
                _held = false;
                try { _timer.SetValue(_engine, 0.5f); } catch { }
                Plugin.Log.LogWarning("nothing left to lose - applying the held build.");
            }
        }
    }
}
