using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace PtcglLeaderboard.Launcher
{
    /// <summary>
    /// Keeps the BepInEx injector alive across PTCGL updates.
    ///
    /// Modes:
    ///   --seed --game "&lt;dir&gt;"   installer only: record the install path and hash the payload
    ///   --repair                 silent verify+restore, for the logon scheduled task
    ///   (no arguments)           repair, launch the game, then WATCH for an update wiping us
    ///
    /// The watch is the part that matters. PTCGL patches itself when you launch it, so a
    /// check that only runs BEFORE launch runs before the damage: the user then plays their
    /// entire first post-update session with no overlay and assumes the mod is broken. Staying
    /// alive for a couple of minutes after launch is what turns that into one restart prompt.
    /// </summary>
    internal static class Program
    {
        private const string Title = "PTCGL Leaderboard & Match History";

        /// <summary>How long to watch after launch. The updater runs immediately at startup, so
        /// this only has to outlive the patch step, not the session.</summary>
        private static readonly TimeSpan WatchFor = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(3);

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (Has(args, "--seed")) return SeedMode(args);
                if (Has(args, "--repair")) return RepairMode();
                return LaunchMode(args);
            }
            catch (Exception ex)
            {
                Repair.Log("FATAL " + ex);
                if (!Has(args, "--repair"))
                    MessageBox.Show("Something went wrong:\n\n" + ex.Message, Title,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 3;
            }
        }

        // ---------------------------------------------------------------- modes

        private static int SeedMode(string[] args)
        {
            var gameDir = Repair.ResolveGameDir(Value(args, "--game"));
            if (gameDir == null) { Repair.Log("seed: game folder not found"); return 1; }

            Repair.Seed(gameDir);
            Repair.DisableLegacyPlugin(gameDir);
            return 0;
        }

        /// <summary>Silent. Runs at logon, so it must be fast and say nothing when all is well.</summary>
        private static int RepairMode()
        {
            var gameDir = Repair.ResolveGameDir();
            if (gameDir == null) return 1;

            var done = Repair.VerifyAndRestore(gameDir, out var failures);
            if (done.Count > 0) Repair.Log("logon repair restored " + done.Count + " file(s)");
            return failures.Count > 0 ? 2 : 0;
        }

        private static int LaunchMode(string[] args)
        {
            var gameDir = Repair.ResolveGameDir(Value(args, "--game"));
            if (gameDir == null)
            {
                MessageBox.Show(
                    "Could not find your Pokémon TCG Live folder.\n\n" +
                    "Reinstall the Leaderboard & Match History, or start it with:\n" +
                    "    PtcglLeaderboardLauncher.exe --game \"C:\\path\\to\\Pokémon Trading Card Game Live\"",
                    Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            Repair.VerifyAndRestore(gameDir, out _);
            Repair.DisableLegacyPlugin(gameDir);

            var exe = Path.Combine(gameDir, Repair.GameExe);
            var game = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = gameDir, UseShellExecute = true });
            Repair.Log("launched " + exe);

            WatchForWipe(gameDir, game);
            return 0;
        }

        // ---------------------------------------------------------------- the watch

        /// <summary>
        /// Poll for the injector disappearing out from under a running game.
        ///
        /// Existence only, deliberately - NOT hashes. The game holds winhttp.dll loaded while it
        /// runs, and an update is a deletion, not a subtle edit. Existence is both the reliable
        /// signal and the cheap one.
        /// </summary>
        private static void WatchForWipe(string gameDir, Process game)
        {
            var manifest = Repair.LoadManifest();
            var watched = manifest.Where(e => e.Policy == Policy.Always).ToList();
            if (watched.Count == 0) return;

            var deadline = DateTime.UtcNow + WatchFor;
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(PollEvery);

                // If the player quit, a repair now lands before their next launch instead of
                // fighting a running game for locked files. Do it and stop.
                if (game != null && game.HasExited)
                {
                    Repair.VerifyAndRestore(gameDir, out _);
                    return;
                }

                var missing = watched
                    .Where(e => !File.Exists(Path.Combine(gameDir, e.RelPath.Replace('/', '\\'))))
                    .Select(e => e.RelPath)
                    .ToList();

                if (missing.Count == 0) continue;

                Repair.Log("update detected mid-session, missing: " + string.Join(", ", missing));
                Repair.Restore(gameDir, missing, out var failures);

                if (failures.Count > 0)
                {
                    MessageBox.Show(
                        "Pokémon TCG Live updated and removed the Leaderboard & Match History, and it could not be " +
                        "put back automatically.\n\nClose the game and run the Leaderboard & Match History installer again.",
                        Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                PromptRestart(gameDir, game);
                return;
            }
        }

        private static void PromptRestart(string gameDir, Process game)
        {
            var answer = MessageBox.Show(
                "Pokémon TCG Live just updated itself, which removed the Leaderboard & Match History.\n\n" +
                "It has been reinstalled, but the game has to be restarted before the overlay " +
                "comes back.\n\nRestart the game now?",
                Title, MessageBoxButtons.YesNo, MessageBoxIcon.Information);

            if (answer != DialogResult.Yes) return;

            try
            {
                if (game != null && !game.HasExited)
                {
                    game.CloseMainWindow();
                    if (!game.WaitForExit(20000))
                    {
                        MessageBox.Show("The game did not close. Close it yourself, then start it again.",
                            Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                }

                // The updater can still be holding files for a moment after the window goes.
                Thread.Sleep(2000);
                Repair.VerifyAndRestore(gameDir, out _);
                Process.Start(new ProcessStartInfo(Path.Combine(gameDir, Repair.GameExe))
                { WorkingDirectory = gameDir, UseShellExecute = true });
                Repair.Log("relaunched after update");
            }
            catch (Exception ex)
            {
                Repair.Log("restart failed: " + ex.Message);
                MessageBox.Show("Could not restart the game automatically - start it yourself.\n\n" + ex.Message,
                    Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ---------------------------------------------------------------- args

        private static bool Has(string[] args, string flag) =>
            args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

        private static string Value(string[] args, string flag)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
    }
}
