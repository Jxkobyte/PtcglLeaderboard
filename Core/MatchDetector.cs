using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace PrizeTracker.Core
{
    /// <summary>
    /// Drives the tracker: notices when a match starts/ends, loads the decklist we queued with,
    /// and refreshes the snapshot on a timer.
    ///
    /// This replaces the old detector wholesale. That version walked ~8 hardcoded Unity paths
    /// (Board_Landscape/PlayArea/PlayerBoardElements_LS/Player/Offset:Player/...) to read Card3D
    /// components, could only ever see the local player's side, missed any zone that was not
    /// currently rendered, and wrote eight LogWarning lines every 0.25s -- about 2,000 lines a
    /// minute into the BepInEx console. All of that is gone: one read of the real MatchBoard gives
    /// both seats and every zone at once.
    /// </summary>
    internal class MatchDetector : MonoBehaviour
    {
        private const float PollInterval = 0.35f;

        private ManualLogSource _log;
        private Tracker _tracker;

        private bool _inMatch;
        private bool _deckLoaded;
        private float _nextDeckRetry;

        // match-history recording
        public MatchHistory History;
        private DateTime _matchStart;
        private bool _recorded;

        public void Initialize(ManualLogSource log, Tracker tracker)
        {
            _log = log;
            _tracker = tracker;
            StartCoroutine(Loop());
        }

        private IEnumerator Loop()
        {
            while (true)
            {
                yield return new WaitForSeconds(PollInterval);
                try { Tick(); }
                catch (Exception e) { _log.LogWarning("tracker tick failed: " + e.Message); }
            }
        }

        private void Tick()
        {
            // One read of the match record per tick: board and seat then provably describe the
            // same moment, and we avoid re-allocating the retriever several times a tick.
            var info = Game.Info();
            var board = Game.BoardOf(info);
            bool inMatch = board != null && Game.Manager() != null;

            if (inMatch && !_inMatch)
            {
                _inMatch = true;
                string key = null;
                try { key = board.matchID; } catch { }
                _tracker.ResetMatch(key);
                _deckLoaded = false;
                _nextDeckRetry = 0f;
                _matchStart = DateTime.UtcNow;
                _recorded = false;
                _log.LogInfo("Match started - tracking.");
            }
            else if (!inMatch && _inMatch)
            {
                _inMatch = false;
                _log.LogInfo("Match ended.");
            }

            // The active deck is sometimes not resolvable the instant a match spins up, so retry
            // until it lands rather than giving up after one attempt.
            if (!_deckLoaded && Time.unscaledTime >= _nextDeckRetry)
            {
                _nextDeckRetry = Time.unscaledTime + 2f;
                TryLoadDeck(false);
            }

            if (!inMatch) return;

            var snap = BoardSnapshot.Read(info);
            EntityCensus(info, snap);
            if (snap.Turn > 0) _lastTurn = snap.Turn;
            _tracker.Update(snap);
            TryRecordResult(info);
        }

        /// <summary>
        /// Write the match to the history once the client reports a result.
        ///
        /// Recorded while the match is still live rather than on teardown, because the board and
        /// the player details are gone by the time it disappears. The winner encoding is the
        /// client's own: EndGameModification uses 1 for player 1 and 2 for player 2, and the game
        /// itself decides "did I win" with exactly the comparison used here.
        /// </summary>
        private void TryRecordResult(global::RainierClientSDK.MatchInfo info)
        {
            if (_recorded || History == null || info == null) return;
            bool over;
            int winner;
            try { over = info.gameOver; winner = info.winner; } catch { return; }
            if (!over || (winner != 1 && winner != 2)) return;

            _recorded = true;
            bool iAmP1 = Game.IAmPlayer1Of(info);
            bool won = (winner == 1 && iAmP1) || (winner == 2 && !iAmP1);

            var rec = new MatchRecord
            {
                WhenUtc = DateTime.UtcNow,
                DurationSeconds = (int)Math.Max(0, (DateTime.UtcNow - _matchStart).TotalSeconds),
                MyDeck = _tracker.DeckName ?? "",
                DeckId = Game.ActiveDeckId(),
                Opponent = Game.OpponentName(info),
                OppArchetype = _tracker.OpponentArchetype(),
                Won = won,
                MyPrizesLeft = _tracker.MyPrizes,
                OppPrizesLeft = _tracker.OppPrizes,
                Turns = _lastTurn,
                DeckBox = Game.ActiveDeckBox(),
                OppCards = OppCardsSeen(),
                OppNames = OppNamesSeen(),
                OppTypes = OppTypesSeen(),
                // Captured here rather than on teardown for the same reason the rest of this record
                // is: the client's log data goes away with the match.
                Log = BattleLogCapture.Instance != null ? (BattleLogCapture.Instance.Capture() ?? "") : "",
            };
            History.Add(rec);
            // The end-game avatars are only on screen for a short while after this point.
            StartCoroutine(Avatars.CaptureAfterGame(this, rec));
            _log.LogInfo(string.Format("Match recorded: {0} vs {1} ({2}) - {3}; battle log {4}",
                rec.MyDeck, string.IsNullOrEmpty(rec.Opponent) ? "?" : rec.Opponent,
                string.IsNullOrEmpty(rec.OppArchetype) ? "?" : rec.OppArchetype, rec.Result,
                string.IsNullOrEmpty(rec.Log) ? "NOT captured" : rec.Log.Length + " chars"));
            _log.LogWarning(string.Format(
                "prize solve: fewest unidentified deck cards seen this match = {0}{1}",
                _tracker.MinDeckUnknown == int.MaxValue ? "never measured" : _tracker.MinDeckUnknown.ToString(),
                _tracker.PrizesSolved ? " (prizes SOLVED)"
                    : _tracker.MinDeckUnknown == 0 ? " (full reveal seen but solve did not fire - BUG)"
                    : " (no full deck reveal observed - a deck search is what makes prizes solvable)"));
        }

        private int _lastTurn;
        private int _censusLogs;

        /// <summary>
        /// While a picker is open, report where the client is keeping IDENTIFIED cards.
        ///
        /// The board's deck zone stays fully unidentified during a search - measured, not assumed -
        /// so the reveal must live somewhere else. MatchInfo keeps two private collections:
        /// matchEntities (everything the client knows by identity) and undefinedCards (the opaque
        /// placeholders that stand in for hidden cards). Counting identified cards per board
        /// position across matchEntities says exactly where a searched card goes, which is the one
        /// fact needed to make prize solving work.
        /// </summary>
        private void EntityCensus(global::RainierClientSDK.MatchInfo info, BoardSnapshot snap)
        {
            if (_censusLogs >= 4 || info == null) return;
            if (snap.Me.Pending.Count == 0) return;         // only while something is in flight

            try
            {
                var f = typeof(global::RainierClientSDK.MatchInfo).GetField("matchEntities",
                            System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.NonPublic);
                if (f == null) { _censusLogs = 99; _log.LogWarning("census: matchEntities not found."); return; }

                var list = f.GetValue(info) as System.Collections.IEnumerable;
                if (list == null) return;

                var byPos = new Dictionary<string, int>();
                int identified = 0, total = 0;
                foreach (var o in list)
                {
                    total++;
                    var ce = o as MatchLogic.CardEntity;
                    if (ce == null || string.IsNullOrEmpty(ce.cardSourceID)) continue;
                    identified++;
                    var key = ce.currentGamePos.ToString();
                    int n;
                    byPos[key] = byPos.TryGetValue(key, out n) ? n + 1 : 1;
                }

                var parts = new List<string>();
                foreach (var kv in byPos) parts.Add(kv.Key + "=" + kv.Value);
                parts.Sort();

                _censusLogs++;
                _log.LogWarning("census: matchEntities " + total + " total, " + identified +
                                " identified | by position: " + string.Join(" ", parts.ToArray()));
            }
            catch (Exception e) { _censusLogs = 99; _log.LogWarning("census failed: " + e.Message); }
        }

        /// <summary>Card types in the same order as OppCardsSeen, while CardCache still has them.</summary>
        private string OppTypesSeen()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var row in _tracker.OppSeen)
            {
                if (row == null || string.IsNullOrEmpty(row.SourceId)) continue;
                int rank = 3;
                try
                {
                    var cs = MatchLogic.CardCache.Get(row.SourceId);
                    if (cs != null)
                    {
                        switch (cs.cardFormat)
                        {
                            case CardFormat.Pokemon: rank = 0; break;
                            case CardFormat.BasicEnergy:
                            case CardFormat.SpecialEnergy: rank = 2; break;
                            default: rank = 1; break;      // items, supporters, tools, stadiums
                        }
                    }
                }
                catch { }
                sb.Append((char)('0' + rank));
            }
            return sb.ToString();
        }

        /// <summary>Names in the same order as OppCardsSeen, while CardCache still has them.</summary>
        private string OppNamesSeen()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var row in _tracker.OppSeen)
                {
                    if (row == null || string.IsNullOrEmpty(row.SourceId)) continue;
                    if (sb.Length > 0) sb.Append('|');
                    sb.Append((row.Name ?? "").Replace('|', ' '));
                }
                return sb.ToString();
            }
            catch { return ""; }
        }

        /// <summary>Freeze the opponent's revealed cards as "id*count" pairs while the board exists.</summary>
        private string OppCardsSeen()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var row in _tracker.OppSeen)
                {
                    if (row == null || string.IsNullOrEmpty(row.SourceId)) continue;
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(row.SourceId).Append('*').Append(row.Count);
                }
                return sb.ToString();
            }
            catch { return ""; }
        }

        /// <summary>Pull the queued decklist out of the client's own inventory.</summary>
        public bool TryLoadDeck(bool verbose)
        {
            string name;
            var cards = Game.ActiveDeck(out name);
            if (cards == null || cards.Count == 0)
            {
                if (verbose) _log.LogWarning("Could not read an active deck for the current game mode.");
                return false;
            }
            _tracker.SetDeck(cards, name);
            _deckLoaded = true;
            _log.LogInfo(string.Format("Decklist loaded: {0} ({1} cards).", name, _tracker.DeckSize));
            return true;
        }

        /// <summary>
        /// Fallback import: parse a decklist off the clipboard, for the cases the inventory lookup
        /// cannot cover. This is what the old build did for EVERY match (you had to click Export
        /// on the deck screen first, and it silently went stale if you switched decks); it is now
        /// only a manual escape hatch.
        /// </summary>
        public bool TryLoadDeckFromClipboard()
        {
            try
            {
                var text = GUIUtility.systemCopyBuffer;
                var names = ParseDeck(text);
                if (names.Count < 40)
                {
                    _log.LogWarning("Clipboard does not look like a decklist (" + names.Count + " cards parsed).");
                    return false;
                }
                var counts = new Dictionary<string, int>();
                foreach (var n in names)
                {
                    int cur;
                    counts[n] = counts.TryGetValue(n, out cur) ? cur + 1 : 1;
                }
                _tracker.SetDeckByName(counts, "clipboard");
                _deckLoaded = true;
                _log.LogInfo("Decklist imported from clipboard: " + names.Count + " cards.");
                return true;
            }
            catch (Exception e)
            {
                _log.LogWarning("Clipboard import failed: " + e.Message);
                return false;
            }
        }

        /// <summary>Parses PTCGL / Limitless "COUNT NAME SET NUMBER" export text into card names.</summary>
        public static List<string> ParseDeck(string deckList)
        {
            var deck = new List<string>();
            if (string.IsNullOrEmpty(deckList)) return deck;

            var lines = deckList.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.StartsWith("Energy:") || line.StartsWith("Trainer:") ||
                    line.StartsWith("Pok") || line.StartsWith("Total Cards:")) continue;

                var parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;

                int count;
                if (!int.TryParse(parts[0], out count)) continue;

                int length = parts.Length;
                if (parts[length - 1].Equals("PH", StringComparison.OrdinalIgnoreCase)) length--;
                if (length - 3 < 1) continue;

                string name = string.Join(" ", parts, 1, length - 3);
                for (int i = 0; i < count; i++) deck.Add(name);
            }
            return deck;
        }
    }
}
