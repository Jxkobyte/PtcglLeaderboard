using System;
using System.Collections.Generic;
using MatchLogic;
using SharedSDKUtils;
using ML = MatchLogic;

namespace PtcglLeaderboard.Core
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
        /// <summary>
        /// The deck actually being played THIS match, from the match's own player details.
        ///
        /// Not the same thing as the inventory's "active deck". Test vs AI plays whichever deck you
        /// pressed Test on, and the inventory's active deck can be something else entirely - in
        /// which case the tracker loads 60 cards that are not on the table, the decklist sanity
        /// gate trips, and prizes never solve. The match tells us which deck it dealt; that is
        /// always the right answer, so it is tried first.
        /// </summary>
        public static Dictionary<string, int> MatchDeck(global::RainierClientSDK.MatchInfo info,
                                                        out string deckName, out DeckInfo deck)
        {
            deckName = null; deck = null;
            try
            {
                var all = NetworkMatchController.playerDetails;
                if (all == null || info == null) return null;
                foreach (var p in all)
                {
                    if (p == null || p.deckInfo == null) continue;
                    if (string.IsNullOrEmpty(info.accountID) || p.playerId != info.accountID) continue;
                    if (p.deckInfo.cards == null || p.deckInfo.cards.Count == 0) continue;
                    deck = p.deckInfo;
                    deckName = p.deckInfo.deckName;
                    return new Dictionary<string, int>(p.deckInfo.cards);
                }
            }
            catch { }
            return null;
        }

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

        /// <summary>
        /// A card that stands for the deck, for the match history thumbnail.
        ///
        /// The deck BOX texture turned out to be the UV unwrap for a 3D model - front panel, wrap
        /// pattern and end faces all laid out in one image - so drawing it needs a crop measured
        /// off the model, and an approximate crop looks worse than no art. A card is already the
        /// right shape and loads through the same path as every other card.
        ///
        /// Picks the deck's headline Pokemon: highest prize value first (an ex or a Mega), then
        /// highest HP. That is the card a player would name the deck after.
        /// </summary>
        public static string ActiveDeckCoverCard()
        {
            string name;
            var cards = ActiveDeck(out name);
            return CoverCardOf(cards);
        }

        /// <summary>
        /// The deck's CARD SLEEVE id - the card-shaped art the client's own deck tile shows in
        /// front of the box, which is what a player recognises a deck by.
        ///
        /// This is not a card from the deck. DeckCustomizationTrio.SetupForDeck draws exactly
        /// three things - deckBox, coin and sleeve - and the card-shaped one is the sleeve, so
        /// picking a "headline Pokemon" out of the deck list was never going to match it.
        /// </summary>
        public static string SleeveOf(DeckInfo deck)
        {
            return deck == null ? "" : (deck.sleeve ?? "");
        }

        /// <summary>The active deck's sleeve id, or empty.</summary>
        public static string ActiveDeckSleeve()
        {
            try
            {
                var pim = ManagerSingleton<PlayerInventoryManager>.instance;
                if (pim == null) return "";
                DeckInfo deck;
                if (!pim.TryGetActiveDeck(NetworkMatchController.gameMode, out deck) || deck == null) return "";
                return deck.sleeve ?? "";
            }
            catch { return ""; }
        }

        /// <summary>Cover card for a specific deck, or null when it cannot be read.</summary>
        public static string CoverCardOf(DeckInfo deck)
        {
            return deck == null || deck.cards == null ? null : CoverCardOf(deck.cards);
        }

        private static string CoverCardOf(Dictionary<string, int> cards)
        {
            try
            {
                if (cards == null) return "";

                string best = null;
                int bestPrize = -1, bestHp = -1;
                foreach (var kvp in cards)
                {
                    CardSource cs;
                    try { cs = CardCache.Get(kvp.Key); } catch { continue; }
                    if (cs == null || cs.cardFormat != CardFormat.Pokemon) continue;
                    if (cs.prizeValue > bestPrize || (cs.prizeValue == bestPrize && cs.hp > bestHp))
                    {
                        best = kvp.Key; bestPrize = cs.prizeValue; bestHp = cs.hp;
                    }
                }
                return best ?? "";
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
        /// <summary>
        /// Our own in-game screen name, readable only while a match is running - it comes from the
        /// match's player list, matched on our account id. Callers remember it for use outside.
        /// </summary>
        public static string MyName()
        {
            try
            {
                var info = Info();
                var all = NetworkMatchController.playerDetails;
                if (all == null || info == null || string.IsNullOrEmpty(info.accountID)) return "";
                foreach (var p in all)
                    if (p != null && p.playerId == info.accountID && !string.IsNullOrEmpty(p.playerName))
                        return p.playerName;
            }
            catch { }
            return "";
        }

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
