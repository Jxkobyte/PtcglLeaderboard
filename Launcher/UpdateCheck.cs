using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace PtcglLeaderboard.Launcher
{
    /// <summary>
    /// Tells the player when a newer version has been released.
    ///
    /// The launcher is what the desktop shortcut runs, every time the game is started, so it is
    /// the one piece of ours guaranteed to run outside the game - the right place to ask.
    ///
    /// Deliberately modest:
    ///   - At most once a day, and never more than a few seconds: offline, slow or rate-limited all
    ///     just mean "no news", and the game starts as normal.
    ///   - It only ever OPENS the fixed download page. It never downloads or runs anything itself:
    ///     an unsigned program that fetches and executes a binary is precisely what antivirus
    ///     quarantines, and this one already writes winhttp.dll into a game folder.
    ///   - The URL it opens is a constant, not taken from the response, so nothing the network says
    ///     can send a player somewhere else.
    /// </summary>
    internal static class UpdateCheck
    {
        private const string Repo = "Jxkobyte/ptcgl-leaderboard";
        private const string LatestApi = "https://api.github.com/repos/" + Repo + "/releases/latest";
        public const string DownloadPage = "https://github.com/" + Repo + "/releases/latest";

        private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(20);
        private const int TimeoutMs = 3000;

        private static string StampFile => Path.Combine(Repair.CacheRoot, "update-check.txt");

        /// <summary>This build's version, as major.minor.patch.</summary>
        public static Version Current
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        /// <summary>
        /// Check, and if a newer release exists ask whether to download it. Returns true when the
        /// player chose to update - the caller should then NOT start the game, because the installer
        /// has to run with the game closed.
        /// </summary>
        public static bool OfferIfNewer(bool force = false)
        {
            try
            {
                if (!force && !Due()) return false;
                var latest = Latest();
                Stamp();
                if (latest == null || latest <= Current) return false;

                Repair.Log("update available: " + latest + " (have " + Current + ")");
                var answer = MessageBox.Show(
                    "A new version of PTCGL Leaderboard & Match History is available.\n\n" +
                    "You have " + Current + ". The latest is " + latest + ".\n\n" +
                    "Download it now? The game won't start, so you can run the new installer straight away.\n\n" +
                    "Choose No to play now - you'll be reminded tomorrow.",
                    "PTCGL Leaderboard & Match History", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer != DialogResult.Yes) return false;

                Process.Start(new ProcessStartInfo(DownloadPage) { UseShellExecute = true });
                Repair.Log("opened the download page for " + latest);
                return true;
            }
            catch (Exception ex)
            {
                // Never the reason the game does not start.
                Repair.Log("update check failed: " + ex.Message);
                return false;
            }
        }

        private static bool Due()
        {
            try
            {
                if (!File.Exists(StampFile)) return true;
                long ticks;
                if (!long.TryParse(File.ReadAllText(StampFile).Trim(), out ticks)) return true;
                return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) >= CheckEvery;
            }
            catch { return true; }
        }

        private static void Stamp()
        {
            try
            {
                Directory.CreateDirectory(Repair.CacheRoot);
                File.WriteAllText(StampFile, DateTime.UtcNow.Ticks.ToString());
            }
            catch { }
        }

        /// <summary>The latest release's version, from its tag ("v1.2.3"), or null.</summary>
        private static Version Latest()
        {
            // .NET Framework's default protocol list can predate TLS 1.2, which GitHub requires.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            var req = (HttpWebRequest)WebRequest.Create(LatestApi);
            req.UserAgent = "PtcglLeaderboardLauncher/" + Current;   // GitHub refuses requests without one
            req.Accept = "application/vnd.github+json";
            req.Timeout = TimeoutMs;
            req.ReadWriteTimeout = TimeoutMs;

            string body;
            using (var res = (HttpWebResponse)req.GetResponse())
            using (var r = new StreamReader(res.GetResponseStream()))
                body = r.ReadToEnd();

            // One field, so a regex rather than a JSON dependency: the launcher ships as a single
            // exe with no DLLs beside it, on purpose.
            var m = Regex.Match(body, "\"tag_name\"\\s*:\\s*\"v?(\\d+)\\.(\\d+)(?:\\.(\\d+))?\"");
            if (!m.Success) return null;
            return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                               m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        }
    }
}
