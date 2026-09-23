using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PtcglLeaderboard.Core
{
    /// <summary>
    /// Live-reloaded numbers, so laying things out does not cost a rebuild and a restart.
    ///
    /// Nearly every round of visual work here has been a number - a crop rectangle, a card size, an
    /// offset - and each one cost closing the game, building, relaunching and replaying to the
    /// point where the thing is visible. Reading those numbers from a text file that is re-read
    /// while the game runs turns a five-minute cycle into a save.
    ///
    /// The file is written with the current defaults on first run, so it documents itself. Unknown
    /// keys are ignored and a missing or malformed file just means defaults - tuning must never be
    /// able to break the plugin.
    /// </summary>
    internal static class Tuning
    {
        private static readonly Dictionary<string, float> _values =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> _defaults =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        private static DateTime _stamp;
        private static float _next;
        private static bool _wrote;

        public static string Path
        {
            get { return System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "PtcglLeaderboard/tuning.txt"); }
        }

        /// <summary>A tunable number. The default is remembered so the file can be regenerated.</summary>
        public static float Get(string key, float fallback)
        {
            if (!_defaults.ContainsKey(key)) _defaults[key] = fallback;
            float v;
            return _values.TryGetValue(key, out v) ? v : fallback;
        }

        /// <summary>Re-read when the file changes. Cheap enough to call every frame.</summary>
        public static void Poll()
        {
            if (UnityEngine.Time.unscaledTime < _next) return;
            _next = UnityEngine.Time.unscaledTime + 1f;

            try
            {
                if (!File.Exists(Path)) { WriteDefaults(); return; }

                var stamp = File.GetLastWriteTimeUtc(Path);
                if (stamp == _stamp) return;
                _stamp = stamp;

                var read = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in File.ReadAllLines(Path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    float v;
                    if (!float.TryParse(line.Substring(eq + 1).Trim(), NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out v)) continue;
                    read[line.Substring(0, eq).Trim()] = v;
                }

                _values.Clear();
                foreach (var kv in read) _values[kv.Key] = kv.Value;
                Plugin.Log.LogWarning("tuning reloaded: " + _values.Count + " value(s) from " + Path);
                Changed = true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("tuning read failed: " + e.Message); }
        }

        /// <summary>Set when the file has just been re-read; consumers clear it after rebuilding.</summary>
        public static bool Changed;

        private static void WriteDefaults()
        {
            if (_wrote || _defaults.Count == 0) return;
            _wrote = true;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                var lines = new List<string>
                {
                    "# PtcglLeaderboard live tuning. Save this file and the game picks it up within a second.",
                    "# Delete a line to go back to its default. Deleting the file rewrites it.",
                    "",
                };
                foreach (var kv in _defaults)
                    lines.Add(kv.Key + " = " + kv.Value.ToString("0.###", CultureInfo.InvariantCulture));
                File.WriteAllLines(Path, lines.ToArray());
                Plugin.Log.LogWarning("tuning file written: " + Path);
            }
            catch (Exception e) { Plugin.Log.LogWarning("tuning write failed: " + e.Message); }
        }
    }
}
