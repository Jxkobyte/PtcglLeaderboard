using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PtcglLeaderboard.MacLoader
{
    /// <summary>
    /// Starts BepInEx - and through it the plugin - inside the macOS game.
    ///
    /// Unity calls Init() before the first scene's objects wake, because the installer lists it in
    /// the game's RuntimeInitializeOnLoads.json (and this assembly in ScriptingAssemblies.json).
    /// That is where a Unity game's own start-up code runs, so everything the plugin hooks is
    /// still ahead of it.
    ///
    /// Everything here is conditional and every failure is swallowed. A loader that throws from a
    /// Unity start-up callback, or brings BepInEx up where it cannot work, costs the player the
    /// game; one that quietly does nothing costs them the leaderboard. So:
    ///
    ///   - No PTCGL_LEADERBOARD_BEPINEX variable: return at once. Only the launcher sets it, so the
    ///     game opened from the Dock, Finder or Launchpad runs exactly as it shipped.
    ///   - Running natively on Apple Silicon: return. Harmony rewrites machine code, and the version
    ///     BepInEx 5 ships can only do that for Intel code, so there the game has to run under
    ///     Rosetta - which is how the launcher starts it. This check is only the backstop.
    ///   - BepInEx already loaded (another mod's loader got there first): return. Two BepInEx
    ///     instances in one game is two of everything.
    ///
    /// What it does is what BepInEx's own preloader does once it reaches the game: set BepInEx's
    /// paths - here to a folder outside the game, which game updates cannot touch - then
    /// Chainloader.Initialize(null, false, ...) and Chainloader.Start(), the exact calls the
    /// preloader injects on Windows. Patcher plugins and the preloader's runtime fixes are
    /// skipped; nothing in this product uses them.
    ///
    /// BepInEx is reached only by reflection. A compile-time reference would make Mono resolve
    /// BepInEx.dll while compiling this method - before the resolver below exists, from a folder
    /// it would never look in.
    /// </summary>
    public static class Entrypoint
    {
        /// <summary>The BepInEx folder. Set only by the launcher; see launch.sh.</summary>
        public const string RootVariable = "PTCGL_LEADERBOARD_BEPINEX";

        /// <summary>Written into the BepInEx folder on every start. launch.sh reads the pid from it.</summary>
        public const string LogName = "PtcglLeaderboardLoader.log";

        private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static bool _ran;
        private static string _root;

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Init()
        {
            if (_ran) return;
            _ran = true;

            string root = null;
            try { root = Environment.GetEnvironmentVariable(RootVariable); } catch { }
            if (string.IsNullOrEmpty(root)) return;

            _root = root;
            try { Start(); }
            catch (Exception e) { Log("BepInEx NOT started: " + e); }
        }

        private static void Start()
        {
            try { File.WriteAllText(LogPath, ""); } catch { }
            Log("PTCGL Leaderboard loader " + typeof(Entrypoint).Assembly.GetName().Version);
            Log("pid=" + Pid());
            Log("system: " + Environment.OSVersion + ", " + (IntPtr.Size * 8) + "-bit, runtime " + Environment.Version);

            var bepinex = Path.Combine(Path.Combine(_root, "core"), "BepInEx.dll");
            if (!File.Exists(bepinex)) { Log("no BepInEx at " + bepinex + " - nothing to start."); return; }

            string arch;
            if (IsNativeAppleSilicon(out arch))
            {
                Log("running natively on Apple Silicon (" + arch + "). BepInEx needs Rosetta, so the game is left as it shipped.");
                return;
            }
            Log("architecture: " + arch);

            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = null;
                try { name = a.GetName().Name; } catch { }
                if (name == "BepInEx")
                {
                    Log("BepInEx is already loaded by something else; not starting a second copy.");
                    return;
                }
            }

            AppDomain.CurrentDomain.AssemblyResolve += Resolve;

            var exe = ExecutablePath();
            var managed = ManagedPath();
            Log("executable: " + exe);
            Log("managed: " + (managed ?? "(BepInEx default)"));
            Log("BepInEx: " + _root);

            var asm = Assembly.LoadFrom(bepinex);

            var paths = asm.GetType("BepInEx.Paths", true);
            Find(paths, "SetExecutablePath", 4).Invoke(null, new object[] { exe, _root, managed, new string[0] });

            // Exactly what BepInEx's preloader injects into the game on Windows: gameExePath null
            // because the paths are already set, and no console window.
            var chain = asm.GetType("BepInEx.Bootstrap.Chainloader", true);
            Find(chain, "Initialize", 3).Invoke(null, new object[] { null, false, null });
            Find(chain, "Start", 0).Invoke(null, null);

            int plugins = -1;
            try
            {
                var infos = chain.GetProperty("PluginInfos", AnyStatic);
                var c = infos == null ? null : infos.GetValue(null, null) as ICollection;
                if (c != null) plugins = c.Count;
            }
            catch { }
            Log("BepInEx started, " + plugins + " plugin(s) loaded");
        }

        /// <summary>The one static method with this name and parameter count, or an exception.</summary>
        private static MethodInfo Find(Type type, string name, int parameters)
        {
            foreach (var m in type.GetMethods(AnyStatic))
                if (m.Name == name && m.GetParameters().Length == parameters) return m;
            throw new MissingMethodException(type.FullName, name);
        }

        /// <summary>BepInEx's own assemblies and anything a plugin references.</summary>
        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            try
            {
                var name = new AssemblyName(args.Name).Name;
                foreach (var sub in new[] { "core", "plugins", "patchers" })
                {
                    var dir = Path.Combine(_root, sub);
                    if (!Directory.Exists(dir)) continue;
                    var hits = Directory.GetFiles(dir, name + ".dll", SearchOption.AllDirectories);
                    if (hits.Length > 0) return Assembly.LoadFrom(hits[0]);
                }
            }
            catch (Exception e) { Log("could not resolve " + args.Name + ": " + e.Message); }
            return null;
        }

        // ------------------------------------------------------------------------------------
        // where things are

        private static string ExecutablePath()
        {
            string exe = null;
            try { exe = Process.GetCurrentProcess().MainModule.FileName; } catch { }
            if (IsFile(exe)) return exe;

            try
            {
                var args = Environment.GetCommandLineArgs();
                if (args.Length > 0 && IsFile(args[0])) return Path.GetFullPath(args[0]);
            }
            catch { }

            // From the Managed folder: <app>/Contents/Resources/Data/Managed -> <app>/Contents/MacOS/*.
            try
            {
                var managed = ManagedPath();
                if (managed != null)
                {
                    var contents = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(managed)));
                    var macos = Path.Combine(contents, "MacOS");
                    if (Directory.Exists(macos))
                        foreach (var f in Directory.GetFiles(macos)) return f;
                }
            }
            catch { }

            // BepInEx only uses this for its process name and log header.
            return exe ?? "Pokemon TCG Live";
        }

        /// <summary>The game's Managed folder: where UnityEngine.CoreModule was loaded from.</summary>
        private static string ManagedPath()
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (a.GetName().Name != "UnityEngine.CoreModule") continue;
                    var dir = Path.GetDirectoryName(a.Location);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
                }
                catch { }
            }
            try
            {
                // This assembly is installed into the same folder.
                var dir = Path.GetDirectoryName(typeof(Entrypoint).Assembly.Location);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
            }
            catch { }
            return null;
        }

        private static bool IsFile(string path)
        {
            try { return !string.IsNullOrEmpty(path) && File.Exists(path); }
            catch { return false; }
        }

        // ------------------------------------------------------------------------------------
        // Apple Silicon

        /// <summary>
        /// True only when this is positively a native arm64 process. Anything unknown counts as
        /// "not native": the launcher already asked for Intel code, and this is only the backstop.
        /// </summary>
        private static bool IsNativeAppleSilicon(out string detail)
        {
            // Mono reports the architecture it was BUILT for, so under Rosetta this is X64.
            string process = null;
            try
            {
                var t = Type.GetType("System.Runtime.InteropServices.RuntimeInformation, mscorlib", false)
                     ?? Type.GetType("System.Runtime.InteropServices.RuntimeInformation, System.Runtime.InteropServices.RuntimeInformation", false);
                var p = t == null ? null : t.GetProperty("ProcessArchitecture", BindingFlags.Static | BindingFlags.Public);
                var v = p == null ? null : p.GetValue(null, null);
                if (v != null) process = v.ToString();
            }
            catch { }
            detail = "process " + (process ?? "unknown");
            if (process == "Arm64") return true;

            var platform = Environment.OSVersion.Platform;
            if (platform != PlatformID.Unix && platform != PlatformID.MacOSX) return false;

            // sysctl.proc_translated: 1 under Rosetta, 0 native, absent on Intel Macs.
            int translated = Sysctl("sysctl.proc_translated");
            int arm64 = Sysctl("hw.optional.arm64");
            detail += ", translated " + translated + ", arm64 cpu " + arm64;
            return arm64 == 1 && translated == 0;
        }

        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "sysctlbyname")]
        private static extern int SysctlByName(string name, ref int value, ref IntPtr size, IntPtr newValue, IntPtr newSize);

        /// <summary>An integer sysctl, or -1 when it does not exist or cannot be read.</summary>
        private static int Sysctl(string name)
        {
            try
            {
                int value = 0;
                var size = new IntPtr(sizeof(int));
                return SysctlByName(name, ref value, ref size, IntPtr.Zero, IntPtr.Zero) == 0 ? value : -1;
            }
            catch { return -1; }
        }

        // ------------------------------------------------------------------------------------
        // log

        private static string LogPath { get { return Path.Combine(_root, LogName); } }

        private static int Pid()
        {
            try { return Process.GetCurrentProcess().Id; }
            catch { return -1; }
        }

        private static void Log(string line)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine); }
            catch { }
            try { UnityEngine.Debug.Log("[PTCGL Leaderboard loader] " + line); }
            catch { }
        }
    }
}
