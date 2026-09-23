using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using RainierClientSDK.Inventory;
using SharedLogicUtils.DataTypes;
using SharedSDKUtils;

namespace PtcglLeaderboard.Core
{
    /// <summary>
    /// A player's avatar, as something small enough to put on a leaderboard.
    ///
    /// An avatar is not a picture. The client builds it as a 3D model from an Outfit - a look plus
    /// a dictionary of item ids - and renders it live. That is why match history has to photograph
    /// the end-game screen to show a face: for a player whose game we never watched there is
    /// nothing to draw.
    ///
    /// The board solves that differently, and much better: every client can upload its OWN outfit,
    /// which is a few hundred bytes of ids, and every other client can then build the real 3D
    /// figure locally. No images are stored, transferred, or hosted - just the ids, which is what
    /// AvatarBaseController.LoadAvatar takes as its argument.
    ///
    /// Only ever the local player's own outfit, and only when they have opted into sharing.
    /// </summary>
    internal static class OutfitCode
    {
        private static bool _reported;

        /// <summary>
        /// Wire shape. Deliberately not the SDK's Outfit type: that carries collection ids and
        /// versioning that mean nothing to anyone else, and pinning our own shape means a client
        /// update cannot silently change what the board stores.
        /// </summary>
        internal class Wire
        {
            [JsonProperty("look")] public int Look;
            [JsonProperty("items")] public Dictionary<string, string> Items;
        }

        /// <summary>
        /// Which body a stored outfit is for. The camera frames a male and a female figure
        /// differently, so this is asked before the figure is shown rather than after.
        /// </summary>
        public static bool IsMale(string json)
        {
            try
            {
                var wire = JsonConvert.DeserializeObject<Wire>(json);
                return wire != null && wire.Look == 0;
            }
            catch { return false; }
        }

        /// <summary>The local player's current outfit as JSON, or null if it is not available.</summary>
        public static string Mine()
        {
            try
            {
                var outfit = InventoryService.currentOutfit;
                if (outfit == null || outfit.customizations == null || outfit.customizations.Count == 0)
                {
                    if (!_reported) { _reported = true; Plugin.Log.LogWarning("outfit: no current outfit to share."); }
                    return null;
                }

                var items = new Dictionary<string, string>();
                foreach (var kv in outfit.customizations)
                {
                    if (string.IsNullOrEmpty(kv.Value)) continue;
                    items[((int)kv.Key).ToString()] = kv.Value;
                }
                if (items.Count == 0) return null;

                if (!_reported)
                {
                    _reported = true;
                    Plugin.Log.LogInfo("outfit: read " + items.Count + " avatar items (look " +
                                       outfit.look + ") - the board can show a 3D figure for us.");
                }
                return JsonConvert.SerializeObject(new Wire { Look = (int)outfit.look, Items = items });
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("outfit: could not read the local outfit: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// The dictionary AvatarBaseController.LoadAvatar wants, from a stored outfit, or null.
        ///
        /// The keys are stored as the ENUM'S NUMERIC VALUE rather than its name, because a name is
        /// a compile-time detail of whichever client version wrote the row and the numbers are
        /// what the client actually switches on.
        /// </summary>
        public static Dictionary<AvatarCustomizationType, string> Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var wire = JsonConvert.DeserializeObject<Wire>(json);
                if (wire == null || wire.Items == null || wire.Items.Count == 0) return null;

                var map = new Dictionary<AvatarCustomizationType, string>();
                foreach (var kv in wire.Items)
                {
                    int key;
                    if (!int.TryParse(kv.Key, out key)) continue;
                    if (string.IsNullOrEmpty(kv.Value)) continue;
                    map[(AvatarCustomizationType)key] = kv.Value;
                }
                return map.Count > 0 ? map : null;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("outfit: could not read a stored outfit: " + e.Message);
                return null;
            }
        }
    }
}
