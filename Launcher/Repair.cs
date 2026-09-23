using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PtcglLeaderboard.Launcher
{
    /// <summary>
    /// What to do when an installed file no longer matches the payload we shipped.
    /// </summary>
    internal enum Policy
    {
        /// <summary>Restore if missing OR altered. For anything the injector needs to be exactly
        /// what we shipped: winhttp.dll, BepInEx\core, the plugin itself.</summary>
        Always,

        /// <summary>Restore only if the file is gone. For files that are legitimately WRITTEN by
        /// something else at runtime - BepInEx.cfg is rewritten by BepInEx itself, and the
        /// tracker's own ptcgl.leaderboard.cfg holds window position and frame caps. Treating
        /// those as Always would silently reset the user's settings on every repair.</summary>
        IfMissing
    }

    internal sealed class Entry
    {
        public Policy Policy;
        public string Sha256;
        public string RelPath;   // stored with forward slashes so the manifest is stable text
    }

    /// <summary>
    /// Verifies and restores the BepInEx injector payload in the PTCGL install folder.
    ///
    /// WHY THIS EXISTS AT ALL: a PTCGL update can replace the game folder and strip the injector
    /// (winhttp.dll, doorstop_config.ini, BepInEx\core). When that happens the plugin is not
    /// loaded, so it cannot repair itself - by the time anything is wrong, none of our code is
    /// running inside the game. The repair has to come from a process outside the game.
    ///
    /// It does NOT happen on every patch. winhttp.dll survived the 2026-08-27 update that
    /// replaced both Pokemon TCG Live.exe and UnityPlayer.dll. So this is written to be a cheap
    /// no-op in the common case - hash a handful of small files and exit - rather than something
    /// that reinstalls on a schedule.
    ///
    /// Nothing here touches the game's own files. It only ever writes paths that came out of our
    /// own payload.
    /// </summary>
    internal static class Repair
    {
        public static string CacheRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PtcglLeaderboard");

        public static string PayloadRoot => Path.Combine(CacheRoot, "payload");
        public static string ManifestPath => Path.Combine(CacheRoot, "manifest.tsv");
        public static string GamePathFile => Path.Combine(CacheRoot, "gamepath.txt");
        public static string LogPath => Path.Combine(CacheRoot, "repair.log");

        public const string GameExe = "Pokemon TCG Live.exe";

        /// <summary>Default per-user install location. PTCGL installs under the user profile, which
        /// is why the installer can run without admin rights.</summary>
        public static string DefaultGameDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "The Pokémon Company International",
            "Pokémon Trading Card Game Live");

        // ---------------------------------------------------------------- paths

        public static bool IsGameDir(string dir) =>
            !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, GameExe));

        /// <summary>
        /// Resolve the install folder, most-trustworthy source first. The installer writes
        /// gamepath.txt at the location the user actually chose, so that wins over the default.
        /// </summary>
        public static string ResolveGameDir(string explicitPath = null)
        {
            if (IsGameDir(explicitPath)) return explicitPath;

            try
            {
                if (File.Exists(GamePathFile))
                {
                    var saved = File.ReadAllText(GamePathFile).Trim();
                    if (IsGameDir(saved)) return saved;
                }
            }
            catch { /* unreadable cache is not fatal - fall through to the default */ }

            if (IsGameDir(DefaultGameDir)) return DefaultGameDir;
            return null;
        }

        // ---------------------------------------------------------------- hashing

        public static string Hash(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
            {
                var bytes = sha.ComputeHash(fs);
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Policy is decided by path, not stored per-file by hand, so adding a file to the payload
        /// cannot forget to classify it. Everything under BepInEx/config is user/runtime state;
        /// everything else is ours and must match.
        /// </summary>
        public static Policy PolicyFor(string relPath) =>
            relPath.StartsWith("BepInEx/config/", StringComparison.OrdinalIgnoreCase)
                ? Policy.IfMissing
                : Policy.Always;

        // ---------------------------------------------------------------- manifest

        private static string Rel(string root, string full) =>
            full.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');

        /// <summary>Walk the cached payload and describe it. The payload IS the source of truth;
        /// the manifest is just a fast index over it.</summary>
        public static List<Entry> BuildManifestFromPayload()
        {
            var list = new List<Entry>();
            if (!Directory.Exists(PayloadRoot)) return list;

            foreach (var full in Directory.GetFiles(PayloadRoot, "*", SearchOption.AllDirectories))
            {
                var rel = Rel(PayloadRoot, full);
                if (IsLegacyPlugin(rel)) { Log("manifest: skipping legacy payload file " + rel); continue; }
                list.Add(new Entry { RelPath = rel, Sha256 = Hash(full), Policy = PolicyFor(rel) });
            }
            return list.OrderBy(e => e.RelPath, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Never let the old combined plugin into the manifest, even if a stale copy is sitting in
        /// the payload folder. Found by testing a repair: DisableLegacyPlugin renames it to
        /// .disabled, the manifest then sees it "missing" and restores it, and the next launch
        /// renames it again - an endless churn that also leaves two overlays running in between.
        /// </summary>
        private static bool IsLegacyPlugin(string relPath) =>
            relPath.EndsWith("/GameStateReader.dll", StringComparison.OrdinalIgnoreCase) ||
            relPath.Equals("GameStateReader.dll", StringComparison.OrdinalIgnoreCase);

        public static void SaveManifest(List<Entry> entries)
        {
            Directory.CreateDirectory(CacheRoot);
            var sb = new StringBuilder();
            sb.AppendLine("# PtcglLeaderboard payload manifest v1 - policy<TAB>sha256<TAB>relative path");
            foreach (var e in entries)
                sb.AppendLine(e.Policy.ToString().ToLowerInvariant() + "\t" + e.Sha256 + "\t" + e.RelPath);
            File.WriteAllText(ManifestPath, sb.ToString(), new UTF8Encoding(false));
        }

        public static List<Entry> LoadManifest()
        {
            var list = new List<Entry>();
            if (!File.Exists(ManifestPath)) return list;

            foreach (var line in File.ReadAllLines(ManifestPath))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var p = line.Split('\t');
                if (p.Length != 3) continue;
                list.Add(new Entry
                {
                    Policy = p[0].Equals("ifmissing", StringComparison.OrdinalIgnoreCase) ? Policy.IfMissing : Policy.Always,
                    Sha256 = p[1],
                    RelPath = p[2]
                });
            }
            return list;
        }

        // ---------------------------------------------------------------- seed / verify / repair

        /// <summary>
        /// Called once by the installer, after it has written the payload to BOTH the game folder
        /// and the cache. Records where the game is and what a good install looks like.
        /// </summary>
        public static void Seed(string gameDir)
        {
            Directory.CreateDirectory(CacheRoot);
            File.WriteAllText(GamePathFile, gameDir ?? "", new UTF8Encoding(false));
            SaveManifest(BuildManifestFromPayload());
            Log("seeded: game=" + gameDir + " payload=" + PayloadRoot);
        }

        /// <summary>Relative paths that are missing or (for Always entries) altered.</summary>
        public static List<string> Verify(string gameDir, List<Entry> manifest)
        {
            var bad = new List<string>();
            foreach (var e in manifest)
            {
                var target = Path.Combine(gameDir, e.RelPath.Replace('/', '\\'));
                if (!File.Exists(target)) { bad.Add(e.RelPath); continue; }
                if (e.Policy != Policy.Always) continue;

                try { if (!string.Equals(Hash(target), e.Sha256, StringComparison.OrdinalIgnoreCase)) bad.Add(e.RelPath); }
                catch (IOException) { /* locked while the game runs - not evidence of damage */ }
            }
            return bad;
        }

        /// <summary>
        /// Restore the listed files from the cache. Returns what was actually written.
        /// Copy failures are collected rather than thrown: one locked file should not abandon the
        /// rest of the repair.
        /// </summary>
        public static List<string> Restore(string gameDir, IEnumerable<string> relPaths, out List<string> failures)
        {
            var done = new List<string>();
            failures = new List<string>();

            foreach (var rel in relPaths)
            {
                var src = Path.Combine(PayloadRoot, rel.Replace('/', '\\'));
                var dst = Path.Combine(gameDir, rel.Replace('/', '\\'));
                try
                {
                    if (!File.Exists(src)) { failures.Add(rel + " (not in cache)"); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(src, dst, overwrite: true);
                    done.Add(rel);
                }
                catch (Exception ex) { failures.Add(rel + " (" + ex.GetType().Name + ": " + ex.Message + ")"); }
            }
            return done;
        }

        /// <summary>Verify then restore. The whole repair in one call.</summary>
        public static List<string> VerifyAndRestore(string gameDir, out List<string> failures)
        {
            var manifest = LoadManifest();
            failures = new List<string>();
            if (manifest.Count == 0 || gameDir == null) return new List<string>();

            var bad = Verify(gameDir, manifest);
            if (bad.Count == 0) return new List<string>();

            Log("repair needed: " + string.Join(", ", bad));
            var done = Restore(gameDir, bad, out failures);
            Log("restored " + done.Count + " file(s)" + (failures.Count > 0 ? "; FAILED: " + string.Join(", ", failures) : ""));
            return done;
        }

        /// <summary>
        /// The old combined plugin carried an early tracker AND the PokeAI export tooling. Running
        /// both gives two overlays. BepInEx scans *.dll including subdirectories, so renaming the
        /// extension is the only way to disable it - moving it to a subfolder does not work.
        /// </summary>
        public static bool DisableLegacyPlugin(string gameDir)
        {
            try
            {
                var legacy = Path.Combine(gameDir, "BepInEx", "plugins", "GameStateReader.dll");
                if (!File.Exists(legacy)) return false;
                var disabled = legacy + ".disabled";
                if (File.Exists(disabled)) File.Delete(disabled);
                File.Move(legacy, disabled);
                Log("disabled legacy plugin: " + legacy);
                return true;
            }
            catch (Exception ex) { Log("could not disable legacy plugin: " + ex.Message); return false; }
        }

        // ---------------------------------------------------------------- log

        public static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(CacheRoot);
                // Small and self-trimming: this runs at every logon and must never grow unbounded.
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 256 * 1024)
                {
                    var keep = File.ReadAllLines(LogPath);
                    File.WriteAllLines(LogPath, keep.Skip(Math.Max(0, keep.Length - 500)));
                }
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
            }
            catch { /* logging must never be the reason a repair fails */ }
        }
    }
}
