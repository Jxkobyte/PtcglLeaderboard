using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using MatchLogic;
using SharedSDKUtils;
using UnityEngine;

namespace PrizeTracker.Core
{
    /// <summary>
    /// Turns a partial "cards we saw" list into a decklist on the clipboard.
    ///
    /// The PTCGL export format needs a PRINTED set code and collector number ("4 Ultra Ball SVI
    /// 196"), and CardSource carries neither - only an internal id like sv1_196. Rather than invent
    /// a prefix-to-set-code table that would rot on the next set release, this hands a DeckInfo to
    /// the client's own exporter, which is what the deck screen's Export button uses.
    ///
    /// No padding. This was originally built to top the list up to 60 with Basic Energy, on the
    /// assumption that PTCGL would only import a complete deck. Tested in game, it imports a
    /// partial list quite happily - so the padding solved a problem that did not exist, and every
    /// padded card was an invented one in a list whose whole purpose is knowing what they actually
    /// played.
    ///
    /// Degrades rather than fails: if the client's exporter cannot be reached, a plain readable
    /// "count name" list is copied instead and the caller is told which one it got.
    /// </summary>
    internal static class DeckExport
    {
        public const int DeckSize = 60;

        /// <summary>Outcome of an export, so the UI can be honest about what landed on the clipboard.</summary>
        public class Result
        {
            public bool Importable;     // true = real PTCGL format from the client's exporter
            public int RealCards;
            public int FillerCards;
            public string Text = "";
        }

        /// <summary>
        /// Build the deck and copy it. Runs as a coroutine because the client's export data is
        /// loaded asynchronously; <paramref name="onDone"/> reports what actually happened.
        /// </summary>
        public static IEnumerator CopyToClipboard(MonoBehaviour host,
                                                  List<KeyValuePair<string, int>> seen,
                                                  Action<Result> onDone)
        {
            var result = new Result();
            var cards = new Dictionary<string, int>();

            foreach (var kv in seen)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;
                int cur;
                cards[kv.Key] = cards.TryGetValue(kv.Key, out cur) ? cur + kv.Value : kv.Value;
                result.RealCards += kv.Value;
            }

            // No padding. Copying the cards actually seen keeps the list honest; a 60-card version
            // would be mostly invented, and the point of this is scouting, not a legal import.

            var deck = new DeckInfo { deckName = "Opponent (partial)", cards = cards };

            // Ask the client to format it. Everything is reflected: these are deep client types and
            // a signature change should degrade to the plain list, not throw into the UI.
            string text = null;
            IEnumerator load = null;
            object importInfo = null;
            try { load = BeginLoad(out importInfo); }
            catch (Exception e) { Plugin.Log.LogWarning("deck export: " + e.Message); }

            if (load != null) yield return host.StartCoroutine(load);

            try { text = Format(importInfo, deck); }
            catch (Exception e) { Plugin.Log.LogWarning("deck export format failed: " + e.Message); }

            if (!string.IsNullOrEmpty(text)) result.Importable = true;
            else text = Plain(cards);

            result.Text = text;
            try { GUIUtility.systemCopyBuffer = text; } catch { }
            if (onDone != null) onDone(result);
        }

        // ---------------------------------------------------------------
        private static IEnumerator BeginLoad(out object importInfo)
        {
            importInfo = null;
            var mgrType = Type.GetType("DeckImportDataManager, TPCI.RainierClient");
            if (mgrType == null) return null;

            var singleton = typeof(MonoSingleton<>).MakeGenericType(mgrType);
            var instProp = singleton.GetProperty("instance",
                               BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            var mgr = instProp != null ? instProp.GetValue(null, null) : null;
            if (mgr == null) return null;

            var infoProp = mgrType.GetProperty("DeckImportInfo");
            var request = mgrType.GetMethod("RequestDeckImportData");

            // Read the info AFTER the request completes; capture the manager now and resolve later.
            importInfo = new Pending { Manager = mgr, InfoProperty = infoProp };
            if (request == null) return null;
            return (IEnumerator)request.Invoke(mgr, new object[] { null });
        }

        private class Pending
        {
            public object Manager;
            public PropertyInfo InfoProperty;
        }

        private static string Format(object pendingObj, DeckInfo deck)
        {
            var pending = pendingObj as Pending;
            if (pending == null || pending.InfoProperty == null) return null;

            var info = pending.InfoProperty.GetValue(pending.Manager, null);
            if (info == null) return null;

            var export = info.GetType().GetMethod("ExportDeckToText", new[] { typeof(DeckInfo) });
            if (export == null) return null;

            return export.Invoke(info, new object[] { deck }) as string;
        }

        /// <summary>
        /// A Basic Energy to pad with, preferring one they actually played so the filler at least
        /// matches their colours. Falls back to any Basic Energy the card cache knows.
        /// </summary>
        private static CardSource Safe(string id)
        {
            try { return CardCache.Get(id); } catch { return null; }
        }

        private static string Plain(Dictionary<string, int> cards)
        {
            var sb = new StringBuilder();
            foreach (var kv in cards)
            {
                var cs = Safe(kv.Key);
                sb.Append(kv.Value).Append(' ')
                  .Append(cs != null && !string.IsNullOrEmpty(cs.cardName) ? cs.cardName : kv.Key)
                  .AppendLine();
            }
            return sb.ToString();
        }
    }
}
