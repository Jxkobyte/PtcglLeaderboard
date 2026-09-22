using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using _Rainier.Scripts.BattleLog;

namespace PrizeTracker.Core
{
    /// <summary>
    /// Captures the client's own battle log for a match, so a finished match can hand you the same
    /// text the in-game Export button produces.
    ///
    /// It does NOT reimplement the formatting. BattleLogExporter.ExportBattleLog walks phases,
    /// entries and sub-entries and has its own indentation rules; duplicating that would drift the
    /// moment the client changed it, and the whole point is to give you the log you would have
    /// copied yourself. Instead the client's exporter is called on the client's own data.
    ///
    /// That method writes straight to the system clipboard, which we do not want to do behind your
    /// back mid-game - so the previous clipboard contents are put back immediately afterwards, and
    /// the text is taken from the exporter's public OnExport event rather than from the clipboard.
    ///
    /// Two sources, because one is not always available:
    ///   - OnExport fires whenever ANYTHING exports, so if you hit Export in game we capture it too;
    ///   - at match end we drive the export ourselves off BattleLogMenu's own export data.
    ///
    /// If neither yields anything the match is simply recorded without a log, and the row says so.
    /// </summary>
    internal class BattleLogCapture : MonoBehaviour
    {
        public static BattleLogCapture Instance { get; private set; }

        /// <summary>Most recently seen log text, whoever produced it.</summary>
        public string Latest { get; private set; }

        private bool _hooked;
        private bool _loggedShape;

        private void Awake()
        {
            Instance = this;
            try
            {
                BattleLogExporter.OnExport += OnAnyExport;
                _hooked = true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("battle log hook failed: " + e.Message); }
        }

        private void OnDestroy()
        {
            if (_hooked) { try { BattleLogExporter.OnExport -= OnAnyExport; } catch { } }
            if (Instance == this) Instance = null;
        }

        private void OnAnyExport(string text)
        {
            if (!string.IsNullOrEmpty(text)) Latest = text;
        }

        /// <summary>
        /// Produce the log for the match that is ending. Returns null when the client has no log
        /// data to give us, which is not an error - it just means there is nothing to store.
        /// </summary>
        public string Capture()
        {
            try
            {
                var data = FindExportData();
                if (data == null) return Latest;        // may still hold an in-game export

                var before = GUIUtility.systemCopyBuffer;
                string captured = null;
                Action<string> grab = t => captured = t;

                BattleLogExporter.OnExport += grab;
                try { new BattleLogExporter().ExportBattleLog(data); }
                finally
                {
                    BattleLogExporter.OnExport -= grab;
                    // Put the clipboard back exactly as we found it.
                    try { GUIUtility.systemCopyBuffer = before; } catch { }
                }

                if (!string.IsNullOrEmpty(captured)) Latest = captured;
                return Latest;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("battle log capture failed: " + e.Message);
                return Latest;
            }
        }

        /// <summary>
        /// Where the log actually lives.
        ///
        /// The first attempt read BattleLogMenu._exportData, and it captured nothing: that field is
        /// only filled when the menu is OPENED, and nothing opens it. Decompiling the end-game
        /// screen's own Battle Log button showed where it gets the data from -
        /// MatchManager.BattleLogExportData, a public property - so the log can be read directly
        /// with no UI involved at all. The menu scan stays as a fallback.
        /// </summary>
        private IEnumerable<IBattleLogData> FindExportData()
        {
            try
            {
                var mm = MonoSingleton<MatchManager>.instance;   // global namespace
                if (mm != null)
                {
                    var data = mm.BattleLogExportData;
                    if (data != null)
                    {
                        if (!_loggedShape)
                        {
                            _loggedShape = true;
                            Plugin.Log.LogInfo("battle log: read from MatchManager.BattleLogExportData.");
                        }
                        return data;
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("battle log: MatchManager read failed: " + e.Message); }

            foreach (var menu in Resources.FindObjectsOfTypeAll<BattleLogMenu>())
            {
                if (menu == null || !menu.gameObject.scene.IsValid()) continue;
                for (var t = menu.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                {
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic |
                                                  BindingFlags.Public))
                    {
                        if (!typeof(IEnumerable<IBattleLogData>).IsAssignableFrom(f.FieldType)) continue;
                        var v = f.GetValue(menu) as IEnumerable<IBattleLogData>;
                        if (v == null) continue;
                        if (!_loggedShape)
                        {
                            _loggedShape = true;
                            Plugin.Log.LogInfo("battle log: export data found on " + t.Name + "." + f.Name);
                        }
                        return v;
                    }
                }
            }
            return null;
        }
    }
}
