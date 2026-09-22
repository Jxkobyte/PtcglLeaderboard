using System;
using System.Collections;
using System.Collections.Generic;
using TPCI.AssetBundleSystem;
using UnityEngine;

namespace PrizeTracker.Core
{
    /// <summary>
    /// Textures for a deck's CUSTOMISATION items - card sleeve, deck box, coin.
    ///
    /// Separate from CardArt because these are not cards and do not load like cards: CardArt goes
    /// through CardUIGraphicLoader, which knows about the MaterialManifest indirection and the
    /// alt-art rule, none of which applies here. These load exactly the way the client's own deck
    /// tile loads them, in DeckCustomizationTrio.LoadThumbnailAndAssign:
    ///
    ///     LoadParams { bundleKey = "CustTrio", bundleName = assetName + "-t" }
    ///     AssetRequest&lt;Texture&gt; { bundleName = ..., assetName = ... }   // both the same
    ///
    /// Note the "-t": the ITEM ID is not the bundle name, the id plus a thumbnail suffix is, and
    /// the asset inside the bundle carries that same name. This corrects an earlier conclusion in
    /// this project that a deck box "is a 3D UV unwrap and cannot be drawn" - that was the model's
    /// texture. There is a flat thumbnail alongside it, and it is what the deck tile actually
    /// shows.
    /// </summary>
    internal class ItemArt : MonoBehaviour
    {
        public static ItemArt Instance { get; private set; }

        private readonly Dictionary<string, Texture> _cache = new Dictionary<string, Texture>();
        private readonly HashSet<string> _inFlight = new HashSet<string>();
        private readonly HashSet<string> _failed = new HashSet<string>();
        private int _failLogs;

        private void Awake() { Instance = this; }

        /// <summary>
        /// Texture for an item id, or null until it arrives. Asking is what queues the load, so
        /// drawing repeatedly is the whole mechanism. Safe to call every frame.
        /// </summary>
        public Texture Get(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return null;

            Texture tex;
            if (_cache.TryGetValue(itemId, out tex))
            {
                // A Unity object can be destroyed underneath us when its bundle is unloaded, and
                // the managed reference stays non-null when that happens.
                if (tex != null) return tex;
                _cache.Remove(itemId);
            }

            if (_failed.Contains(itemId) || _inFlight.Contains(itemId)) return null;
            _inFlight.Add(itemId);
            StartCoroutine(Load(itemId));
            return null;
        }

        private IEnumerator Load(string itemId)
        {
            AssetBundleManager mgr = null;
            try { mgr = AssetBundleManager.instance; } catch { }
            if (mgr == null)
            {
                // Asset system not up yet. Allow a retry instead of marking it unavailable.
                _inFlight.Remove(itemId);
                yield break;
            }

            var bundleName = itemId + "-t";

            IEnumerator load = null;
            try
            {
                load = mgr.LoadAssetBundle(new AssetBundleManager.LoadParams
                {
                    bundleKey = "PrizeTrackerItems",
                    bundleName = bundleName,
                    keepAlive = true,
                });
            }
            catch (Exception e) { Fail(itemId, e.Message); yield break; }
            yield return StartCoroutine(load);

            var request = new AssetBundleManager.AssetRequest<Texture>
            {
                bundleName = bundleName,
                assetName = bundleName,
            };

            IEnumerator fetch = null;
            try { fetch = mgr.LoadAssetFromAssetBundleAsync(request); }
            catch (Exception e) { Fail(itemId, e.Message); yield break; }
            yield return StartCoroutine(fetch);

            _inFlight.Remove(itemId);
            if (request.asset != null)
            {
                _cache[itemId] = request.asset;
                // Saved once so the sleeve's shape can be LOOKED AT rather than assumed. Card
                // textures turned out to be square with the card stretched to fill them, which is
                // the opposite of what "keep its proportions" would do; a sleeve may or may not
                // follow the same convention and guessing has cost a round each time.
                TexDump.Once(request.asset, "item_" + request.asset.width + "x"
                             + request.asset.height + "_" + itemId);
            }
            else Fail(itemId, "no texture in bundle " + bundleName);
        }

        private void Fail(string itemId, string why)
        {
            _inFlight.Remove(itemId);
            _failed.Add(itemId);
            if (_failLogs++ < 5) Plugin.Log.LogWarning("item art failed for " + itemId + ": " + why);
        }
    }
}
