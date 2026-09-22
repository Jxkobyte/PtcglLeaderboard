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

        private void Update()
        {
            // Nothing to do. A reload used to be held back while a match was running, on the
            // belief that it would wipe the solved prizes - and that belief was simply wrong.
            // The client keeps the identified deck in MatchInfo.matchEntities, so after a reload
            // the tracker re-derives the solve from the board within a tick. Measured, not
            // assumed: a reload mid-match logged "should solve" and re-dressed every prize
            // straight away.
            //
            // The hold cost three interruptions and protected nothing, and its release logic had
            // a bug of its own - it set ScriptEngine's countdown low and then raised it again on
            // the very next frame, so a released reload could never actually fire.
        }
    }
}
