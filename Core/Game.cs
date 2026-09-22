using System;
using System.Collections.Generic;
using MatchLogic;
using SharedSDKUtils;
using ML = MatchLogic;

namespace PrizeTracker.Core
{
    /// <summary>
    /// Thin, exception-safe accessors for the live client's real match state.
    ///
    /// Everything the tracker needs comes from <see cref="MatchBoard"/>, the board type the game's
    /// own rules engine runs on, reached via MatchManager.currentMatch.GetBoardState(). That is the
    /// engine's own accounting, so zone membership and card conservation are correct by
    /// construction.
    ///
    /// This deliberately replaces the old approach of walking hardcoded Unity GameObject paths
    /// (Board_Landscape/PlayArea/...) and reading Card3D.cardInfo: those paths break on any UI
    /// change, only ever saw the local player, and could not see a zone that wasn't rendered.
    /// </summary>
    /// <summary>One deck from the player's inventory.</summary>
    internal class DeckSummary
    {
        public string Id;
        public string Name;
        public int Cards;
        public bool Valid;
        public DateTime LastPlayed;
        public string DeckBox;   // deck box art id, as shown on the deck tiles
    }

    internal static class Game
    {
        /// <summary>Placeholder entityID the server stamps on every card hidden from us.</summary>
        public const string PrivateEntityID = "PRIVATE";

        public static MatchManager Manager()
        {
            try { return MatchManager.instance; } catch { return null; }
        }

        /// <summary>
        /// The live match record, or null outside a match. This property allocates a small
        /// retriever per access, so anything needing both the board and the seat should grab it
        /// ONCE and pass it down. That also stops the board and the seat being read from two
        /// different states if a match ends mid-tick.
        /// </summary>
        public static global::RainierClientSDK.MatchInfo Info()
        {
            try { return NetworkMatchController.currentMatchInfo; } catch { return null; }
        }

        /// <summary>The live board, or null when not in a match / not yet dealt.</summary>
        public static MatchBoard Board() { return BoardOf(Info()); }

        public static MatchBoard BoardOf(global::RainierClientSDK.MatchInfo info)
        {
            if (info == null) return null;
            try { return info.GetBoardState(); } catch { return null; }
        }

        /// <summary>True when we are seated as player 1 in the current match.</summary>
        public static bool IAmPlayer1() { return IAmPlayer1Of(Info()); }

        public static bool IAmPlayer1Of(global::RainierClientSDK.MatchInfo info)
        {
            if (info == null) return true;
            try { return info.isPlayer1; } catch { return true; }
        }

        public static bool InMatch()
        {
            try { return Manager() != null && Board() != null; }
            catch { return false; }
        }

        /// <summary>
        /// The 60-card list we actually queued with, straight from the client's own inventory
        /// (cardID -> count). Replaces the old clipboard/"press Export" flow, which required a
        /// manual click before every match and silently went stale if you switched decks.
        /// </summary>
        public static Dictionary<string, int> ActiveDeck(out string deckName)
        {
            deckName = null;
            try
            {
                var gameMode = NetworkMatchController.gameMode;
                var pim = ManagerSingleton<PlayerInventoryManager>.instance;
                if (pim == null) return null;
                DeckInfo deck;
                if (!pim.TryGetActiveDeck(gameMode, out deck) || deck == null) return null;
                if (deck.cards == null || deck.cards.Count == 0) return null;
                deckName = deck.deckName;
                return new Dictionary<string, int>(deck.cards);
            }
            catch { return null; }
        }

        /// <summary>
        /// Every deck in the player's inventory, newest-played first, as (name, cardCount).
        ///
        /// Read from the client's own cached inventory, so it lists decks you have never played as
        /// well - which is the point when you are picking one.
        /// </summary>
        public static List<DeckSummary> AllDecks()
        {
            var list = new List<DeckSummary>();
            try
            {
                var pim = ManagerSingleton<PlayerInventoryManager>.instance;
                if (pim == null) return list;
                var decks = new Dictionary<string, DeckInfo>();
                pim.GetCachedDecks(ref decks);
                foreach (var kvp in decks)
                {
                    var d = kvp.Value;
                    if (d == null || string.IsNullOrEmpty(d.deckName)) continue;
                    list.Add(new DeckSummary
                    {
                        DeckBox = d.deckBox ?? "",
                        Id = d.id,
                        Name = d.deckName,
                        Cards = d.size,
                        Valid = d.valid,
                        LastPlayed = d.lastPlayed,
                    });
                }
                list.Sort((a, b) => b.LastPlayed.CompareTo(a.LastPlayed));
            }
            catch { }
            return list;
        }

        /// <summary>The name of the deck currently set active, or empty.</summary>
        public static string ActiveDeckName()
        {
            string name;
            var cards = ActiveDeck(out name);
            return cards == null ? "" : (name ?? "");
        }

        /// <summary>The stable inventory id of the active deck, or empty.</summary>
        public static string ActiveDeckId()
        {
            try
            {
                var pim = ManagerSingleton<PlayerInventoryManager>.instance;
                if (pim == null) return "";
                DeckInfo deck;
                if (!pim.TryGetActiveDeck(NetworkMatchController.gameMode, out deck) || deck == null) return "";
                return deck.id ?? "";
            }
            catch { return ""; }
        }

        /// <summary>The active deck's box art id, or empty. Used for the match history thumbnail.</summary>
        public static string ActiveDeckBox()
        {
            try
            {
                var pim = ManagerSingleton<PlayerInventoryManager>.instance;
                if (pim == null) return "";
                DeckInfo deck;
                if (!pim.TryGetActiveDeck(NetworkMatchController.gameMode, out deck) || deck == null) return "";
                return deck.deckBox ?? "";
            }
            catch { return ""; }
        }

        /// <summary>The opponent's display name, or empty if it cannot be read.</summary>
        public static string OpponentName(global::RainierClientSDK.MatchInfo info)
        {
            try
            {
                var all = NetworkMatchController.playerDetails;
                if (all == null || info == null) return "";
                foreach (var p in all)
                {
                    if (p == null) continue;
                    if (!string.IsNullOrEmpty(info.accountID) && p.playerId == info.accountID) continue;
                    if (!string.IsNullOrEmpty(p.playerName)) return p.playerName;
                }
            }
            catch { }
            return "";
        }

        /// <summary>Display name for a cardSourceID, via the client's card cache.</summary>
        public static string NameOf(string cardSourceID)
        {
            if (string.IsNullOrEmpty(cardSourceID)) return null;
            try
            {
                var src = ML.CardCache.Get(cardSourceID);
                if (src != null && !string.IsNullOrEmpty(src.cardName)) return Pretty(src.cardName);
            }
            catch { }
            return null;
        }

        public static CardFormat FormatOf(string cardSourceID)
        {
            if (string.IsNullOrEmpty(cardSourceID)) return CardFormat.NONE;
            try
            {
                var src = ML.CardCache.Get(cardSourceID);
                if (src != null) return src.cardFormat;
            }
            catch { }
            return CardFormat.NONE;
        }

        private static readonly Dictionary<string, string> EnergySymbols = new Dictionary<string, string>
        {
            { "{W}", "Water" },   { "{G}", "Grass" },    { "{R}", "Fire" },
            { "{L}", "Lightning" },{ "{P}", "Psychic" }, { "{F}", "Fighting" },
            { "{D}", "Darkness" },{ "{M}", "Metal" },    { "{Y}", "Fairy" },
            { "{N}", "Dragon" },  { "{C}", "Colorless" },
        };

        /// <summary>
        /// Card names store energy types as symbols ("Basic {D} Energy"); decklists and humans
        /// spell them out.
        /// </summary>
        public static string Pretty(string cardName)
        {
            if (string.IsNullOrEmpty(cardName)) return cardName;
            if (cardName.IndexOf('{') < 0) return cardName;
            foreach (var kvp in EnergySymbols)
                cardName = cardName.Replace(kvp.Key, kvp.Value);
            return cardName;
        }
    }

}
