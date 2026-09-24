using System;
using System.IO;

namespace PtcglLeaderboard.Core
{
    /// <summary>
    /// Where the plugin keeps data that must OUTLIVE the game folder.
    ///
    /// Everything under BepInEx\ is inside the folder a PTCGL update can wipe - that is the whole
    /// reason the launcher and its repair payload exist. Match history is the one thing a repair
    /// can never restore: it is the user's own record, we have no copy of it, and inventing one is
    /// not possible. Keeping it in BepInEx\config meant the very update the launcher exists to
    /// survive still silently destroyed the thing the product is named after.
    ///
    /// So it lives in %LOCALAPPDATA%\PtcglLeaderboard, next to the launcher's own cache, which is
    /// demonstrably untouched by game updates.
    ///
    /// Dev-only artefacts (probe.txt, debug\, tuning.txt) deliberately stay under BepInEx\config.
    /// They are disposable and it is convenient to have them beside the game.
    /// </summary>
    internal static class DataPaths
    {
        public static string Root => _root ?? (_root = FindRoot());
        private static string _root;

        /// <summary>
        /// %LOCALAPPDATA%\PtcglLeaderboard on Windows. On a Mac,
        /// ~/Library/Application Support/PtcglLeaderboard - the folder the Mac installer puts
        /// BepInEx in, and the one its uninstaller knows to leave alone. Mono maps
        /// LocalApplicationData to ~/.local/share on a Mac, a hidden folder nobody would find.
        ///
        /// Mono reports a Mac as PlatformID.Unix; there is no Linux build of the game, so Unix
        /// means a Mac here. No Unity call, so this is safe from any thread.
        /// </summary>
        private static string FindRoot()
        {
            var platform = Environment.OSVersion.Platform;
            if (platform == PlatformID.Unix || platform == PlatformID.MacOSX)
            {
                var home = Environment.GetEnvironmentVariable("HOME");
                if (string.IsNullOrEmpty(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                if (!string.IsNullOrEmpty(home))
                    return Path.Combine(Path.Combine(Path.Combine(home, "Library"), "Application Support"), "PtcglLeaderboard");
            }
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PtcglLeaderboard");
        }

        public static string MatchLog => Path.Combine(Root, "matches.jsonl");
        public static string Avatars => Path.Combine(Root, "avatars");

        /// <summary>
        /// A copy of the leaderboard PlayerId. The real one lives in the BepInEx config file, and
        /// BepInEx\config is inside the folder a PTCGL update can wipe - losing it means a fresh
        /// random id, i.e. the player turns up on the board as somebody new. This copy survives.
        /// </summary>
        public static string PlayerIdFile => Path.Combine(Root, "player-id.txt");

        /// <summary>The saved PlayerId, or null. Never throws.</summary>
        public static string ReadPlayerId()
        {
            try
            {
                if (!File.Exists(PlayerIdFile)) return null;
                var id = File.ReadAllText(PlayerIdFile).Trim();
                return id.Length > 0 ? id : null;
            }
            catch { return null; }
        }

        /// <summary>Save the PlayerId if the copy is missing or different. Never throws.</summary>
        public static void WritePlayerId(string id, Action<string> log = null)
        {
            if (string.IsNullOrEmpty(id) || id == ReadPlayerId()) return;
            try { Directory.CreateDirectory(Root); File.WriteAllText(PlayerIdFile, id); }
            catch (Exception ex) { log?.Invoke("could not save the player id: " + ex.Message); }
        }

        /// <summary>Folder names this data has lived under inside BepInEx\config, oldest first.</summary>
        private static readonly string[] LegacyFolders = { "PrizeTracker", "PtcglLeaderboard" };

        /// <summary>
        /// Bring forward data written by an earlier build. Called once at startup with BepInEx's
        /// config path (passed in rather than referenced, so Core keeps no BepInEx dependency).
        ///
        /// COPIES rather than moves, and never overwrites a destination that already exists. A
        /// migration that deletes the only copy of the user's history in order to relocate it is
        /// exactly the kind of thing that goes wrong once and is unrecoverable; leaving the old
        /// file where it is costs nothing, and a future game update cleaning it up is fine because
        /// by then the real copy is elsewhere.
        /// </summary>
        public static void MigrateFromLegacy(string bepInExConfigPath, Action<string> log = null)
        {
            if (string.IsNullOrEmpty(bepInExConfigPath)) return;

            try { Directory.CreateDirectory(Root); }
            catch (Exception ex) { log?.Invoke("could not create data folder: " + ex.Message); return; }

            foreach (var folder in LegacyFolders)
            {
                var oldDir = Path.Combine(bepInExConfigPath, folder);
                if (!Directory.Exists(oldDir)) continue;

                CopyIfAbsent(Path.Combine(oldDir, "matches.jsonl"), MatchLog, log);
                CopyTreeIfAbsent(Path.Combine(oldDir, "avatars"), Avatars, log);
            }
        }

        private static void CopyIfAbsent(string src, string dst, Action<string> log)
        {
            try
            {
                if (!File.Exists(src) || File.Exists(dst)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(src, dst);
                log?.Invoke("migrated " + src + " -> " + dst + " (" + new FileInfo(dst).Length + " bytes)");
            }
            catch (Exception ex) { log?.Invoke("could not migrate " + src + ": " + ex.Message); }
        }

        private static void CopyTreeIfAbsent(string srcDir, string dstDir, Action<string> log)
        {
            try
            {
                if (!Directory.Exists(srcDir)) return;
                Directory.CreateDirectory(dstDir);
                var n = 0;
                foreach (var src in Directory.GetFiles(srcDir))
                {
                    var dst = Path.Combine(dstDir, Path.GetFileName(src));
                    if (File.Exists(dst)) continue;
                    File.Copy(src, dst);
                    n++;
                }
                if (n > 0) log?.Invoke("migrated " + n + " file(s) from " + srcDir);
            }
            catch (Exception ex) { log?.Invoke("could not migrate " + srcDir + ": " + ex.Message); }
        }
    }
}
