using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TPCI.AssetBundleSystem;
using UnityEngine;

namespace PtcglLeaderboard.Core
{
    /// <summary>
    /// Supplies card art to the overlay, cheapest source first.
    ///
    /// 1. ALREADY IN MEMORY. The client has to load a card's texture to draw it, so anything that
    ///    has been on screen this session is sitting in memory already. A scan of loaded Texture2D
    ///    objects by name resolves those for free, with no bundle I/O at all.
    ///
    /// 2. THE CLIENT'S OWN LOADER, for cards that have not been drawn - which is most of what the
    ///    PRIZES tab wants, since prize cards are by definition face down.
    ///
    /// The first attempt at (2) failed in game ("no texture in bundle" for mee_16) because it
    /// assumed the texture inside a bundle is named after the bundle. Often it is not: the client
    /// falls back to loading a "MaterialManifest" asset and reading the real texture name out of
    /// its ColorTexture field. Rather than reimplement that (and the alt-art and _t suffix rules
    /// around it), this now calls the client's own CardUIGraphicLoader.LoadCardGraphicFromAsset,
    /// which is public and static, after loading the bundle the same way the client's
    /// AssetBundleGraphicLoader.StartLoad does. It also asks for Texture2D rather than Texture,
    /// matching the client exactly.
    ///
    /// Everything degrades to null: the overlay draws a name tile when there is no texture, so it
    /// stays fully usable even if art cannot be loaded at all.
    /// </summary>
    internal class CardArt : MonoBehaviour
    {
        private const int MaxConcurrent = 3;

        private readonly Dictionary<string, Texture> _cache = new Dictionary<string, Texture>();
        private readonly HashSet<string> _inFlight = new HashSet<string>();
        private readonly HashSet<string> _failed = new HashSet<string>();
        private readonly Queue<string> _queue = new Queue<string>();
        private int _active;

        // name -> texture, rebuilt from whatever the client currently has loaded
        private Dictionary<string, Texture> _memory = new Dictionary<string, Texture>();
        private float _memoryStale;

        public static CardArt Instance { get; private set; }

        private void Awake() { Instance = this; }

        public int CachedCount { get { return _cache.Count; } }
        public int FailedCount { get { return _failed.Count; } }
        public int MemoryCount { get { return _memory.Count; } }

        /// <summary>
        /// Texture for a card, or null if it is not available yet. Requesting queues a load, so
        /// simply drawing the overlay is what pulls art in. Safe to call every frame.
        /// </summary>
        public Texture Get(string cardSourceId)
        {
            if (string.IsNullOrEmpty(cardSourceId)) return null;

            Texture tex;
            if (_cache.TryGetValue(cardSourceId, out tex))
            {
                // A Unity object can be destroyed underneath us when its bundle is unloaded, and
                // the C# reference stays non-null when that happens - hence the Unity null check.
                if (tex != null) return tex;
                _cache.Remove(cardSourceId);
            }

            var fromMemory = FromMemory(cardSourceId);
            if (fromMemory != null)
            {
                _cache[cardSourceId] = fromMemory;
                return fromMemory;
            }

            if (_failed.Contains(cardSourceId) || _inFlight.Contains(cardSourceId)) return null;

            _inFlight.Add(cardSourceId);
            _queue.Enqueue(cardSourceId);
            return null;
        }

        /// <summary>
        /// Look the card up among textures the client already has loaded.
        ///
        /// The scan is not cheap (it walks every loaded texture), so it is rebuilt at most once
        /// every few seconds and only while something is actually unresolved.
        /// </summary>
        private Texture FromMemory(string id)
        {
            Texture t;
            if (_memory.TryGetValue(id, out t) && t != null) return t;

            if (Time.unscaledTime < _memoryStale) return null;
            _memoryStale = Time.unscaledTime + 5f;
            RebuildMemoryIndex();

            return _memory.TryGetValue(id, out t) && t != null ? t : null;
        }

        private void RebuildMemoryIndex()
        {
            try
            {
                var found = new Dictionary<string, Texture>(StringComparer.OrdinalIgnoreCase);
                var all = Resources.FindObjectsOfTypeAll<Texture2D>();
                foreach (var tex in all)
                {
                    if (tex == null || string.IsNullOrEmpty(tex.name)) continue;
                    // Card textures are named after the card; some carry a "_t" thumbnail suffix.
                    var n = tex.name;
                    if (n.EndsWith("_t", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 2);
                    if (!found.ContainsKey(n)) found[n] = tex;
                }
                _memory = found;

                // One-time diagnostic. If art still does not resolve, the useful question is what
                // the client actually calls these textures - guessing the convention a third time
                // would be worse than logging it once.
                if (!_loggedIndex && found.Count > 0)
                {
                    _loggedIndex = true;
                    var sample = new List<string>();
                    foreach (var k in found.Keys)
                    {
                        if (sample.Count >= 12) break;
                        // card-id shaped: letters, digits, an underscore
                        if (k.IndexOf('_') > 0 && k.Length <= 20) sample.Add(k);
                    }
                    Plugin.Log.LogInfo("card art: " + all.Length + " textures loaded, " + found.Count +
                                       " indexed. sample: " + string.Join(", ", sample.ToArray()));
                }
            }
            catch { }
        }

        private bool _loggedIndex;

        private void Update()
        {
            while (_active < MaxConcurrent && _queue.Count > 0)
            {
                var id = _queue.Dequeue();
                _active++;
                StartCoroutine(Load(id));
            }
        }

        private IEnumerator Load(string id)
        {
            var mgr = SafeManager();
            if (mgr == null)
            {
                // Asset system not up yet (early boot). Allow a later retry rather than marking
                // the card permanently unavailable.
                _inFlight.Remove(id);
                _active--;
                yield break;
            }

            // Load the bundle first, exactly as AssetBundleGraphicLoader.StartLoad does. keepAlive
            // matters: CardUIGraphicLoader sets it, and without it the bundle (and our texture) can
            // be unloaded again straight away.
            var bundleName = ResolveBundle(id) ?? id;

            IEnumerator bundle = null;
            try { bundle = mgr.LoadAssetBundle(bundleName, bundleName, 0, null, true); }
            catch (Exception e) { Fail(id, e.Message); yield break; }
            yield return StartCoroutine(bundle);

            // Then the client's own card-graphic loader, which knows about the MaterialManifest
            // indirection, the alt-art rule and the thumbnail suffix. It expects the LOWERCASED
            // name, the same way CardUIGraphicLoader.OnLoadComplete calls it.
            var slot = new _CoreUtils.Ref<Texture2D>(null);
            IEnumerator load = null;
            try { load = CardUIGraphicLoader.LoadCardGraphicFromAsset(bundleName.ToLower(), slot); }
            catch (Exception e) { Fail(id, e.Message); yield break; }
            yield return StartCoroutine(load);

            if (slot.Value != null)
            {
                _cache[id] = slot.Value;
                _inFlight.Remove(id);
                _active--;
            }
            else
            {
                // One last look in memory: the bundle load above may well have put the texture
                // there even if the named lookup missed it.
                RebuildMemoryIndex();
                Texture t;
                if (_memory.TryGetValue(id, out t) && t != null)
                {
                    _cache[id] = t;
                    _inFlight.Remove(id);
                    _active--;
                }
                else Fail(id, "no texture in bundle or memory");
            }
        }

        private AssetBundleManager SafeManager()
        {
            try { return AssetBundleManager.instance; } catch { return null; }
        }

        private int _failLogs;
        private static bool _dumpedBundles;
        private static Dictionary<string, string> _bundleByCard;

        /// <summary>
        /// Map card id -> real asset bundle name, built from the client's own bundle list.
        ///
        /// A card bundle is NOT named after the card. The client names them
        /// "&lt;set&gt;_&lt;lang&gt;_&lt;number padded to 3&gt;" with an optional variant suffix, so card
        /// sv8-5_165 lives in bundle sv8-5_en_165. Assuming otherwise is why art failed for every
        /// card, not just energy.
        ///
        /// The rule is DERIVED from the 41,928 names the client already has rather than hardcoded:
        /// a future set, a different language or a new variant suffix then needs no change here. A
        /// "_t" thumbnail never overwrites the full-size entry.
        /// </summary>
        private static string ResolveBundle(string cardId)
        {
            if (string.IsNullOrEmpty(cardId)) return null;
            BuildBundleIndex();
            if (_bundleByCard == null) return null;

            string bundle;
            return _bundleByCard.TryGetValue(Normalise(cardId), out bundle) ? bundle : null;
        }

        /// <summary>"sv8-5_en_165" and "sv8-5_165" both reduce to "sv8-5|165".</summary>
        private static string Normalise(string name)
        {
            var parts = name.Split('_');
            if (parts.Length < 2) return name.ToLowerInvariant();

            // Find the numeric part - the collector number - and treat what follows as the variant.
            for (int i = 1; i < parts.Length; i++)
            {
                int num;
                if (!int.TryParse(parts[i], out num)) continue;

                var set = string.Join("_", parts, 0, i);
                // A two-letter token immediately before the number is the language tag.
                if (i >= 2 && parts[i - 1].Length == 2)
                    set = string.Join("_", parts, 0, i - 1);

                var variant = "";
                for (int j = i + 1; j < parts.Length; j++) variant += "_" + parts[j];
                return (set + "|" + num + variant).ToLowerInvariant();
            }
            return name.ToLowerInvariant();
        }

        private static void BuildBundleIndex()
        {
            if (_bundleByCard != null) return;
            _bundleByCard = new Dictionary<string, string>();
            try
            {
                var mgr = AssetBundleManager.instance;
                if (mgr == null) { _bundleByCard = null; return; }
                var f = mgr.GetType().GetField("availableBundles",
                            System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.NonPublic);
                if (f == null) { _bundleByCard = null; return; }
                var dict = f.GetValue(mgr) as System.Collections.IDictionary;
                if (dict == null) { _bundleByCard = null; return; }

                foreach (var k in dict.Keys)
                {
                    var name = Convert.ToString(k);
                    if (string.IsNullOrEmpty(name) || name.IndexOf('_') < 0) continue;
                    var key = Normalise(name);
                    // Prefer the full-size asset: never let a thumbnail replace one.
                    if (_bundleByCard.ContainsKey(key) && name.EndsWith("_t")) continue;
                    _bundleByCard[key] = name;
                }
                Plugin.Log.LogInfo("card art: indexed " + _bundleByCard.Count + " card bundles.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("card art: bundle index failed: " + e.Message);
                _bundleByCard = null;
            }
        }

        /// <summary>
        /// Print a sample of the bundle names the client actually knows about.
        ///
        /// Every card id probed so far reports bundleExists=false - not just energy, but Pokemon
        /// and Trainers too - which means a card bundle is simply NOT named after its card id.
        /// That premise has now failed three times, so rather than guess a fourth naming rule this
        /// reads the real list out of AssetBundleManager. It is private, hence reflection, and it
        /// runs once.
        /// </summary>
        private static void DumpBundleNames()
        {
            if (_dumpedBundles) return;
            _dumpedBundles = true;
            try
            {
                var mgr = AssetBundleManager.instance;
                if (mgr == null) { Plugin.Log.LogWarning("bundle dump: no AssetBundleManager."); return; }

                var f = mgr.GetType().GetField("availableBundles",
                            System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.NonPublic);
                if (f == null) { Plugin.Log.LogWarning("bundle dump: availableBundles not found."); return; }

                var dict = f.GetValue(mgr) as System.Collections.IDictionary;
                if (dict == null) { Plugin.Log.LogWarning("bundle dump: not a dictionary."); return; }

                var names = new List<string>();
                foreach (var k in dict.Keys) names.Add(Convert.ToString(k));
                names.Sort();

                Plugin.Log.LogWarning("bundle dump: " + names.Count + " bundles known to the client.");

                // Anything that looks like it could carry card art, plus a plain sample for shape.
                var cardish = names.Where(n => n.IndexOf("card", StringComparison.OrdinalIgnoreCase) >= 0)
                                   .Take(15).ToArray();
                if (cardish.Length > 0)
                    Plugin.Log.LogWarning("  containing 'card': " + string.Join(" | ", cardish));

                var sample = new List<string>();
                for (int i = 0; i < names.Count && sample.Count < 30; i += Math.Max(1, names.Count / 30))
                    sample.Add(names[i]);
                Plugin.Log.LogWarning("  sample: " + string.Join(" | ", sample.ToArray()));
            }
            catch (Exception e) { Plugin.Log.LogWarning("bundle dump failed: " + e.Message); }
        }

        /// <summary>
        /// Report a failure with the two facts that actually distinguish the possible causes.
        ///
        /// A bare "no texture in bundle" cannot tell apart "this card has no bundle under that
        /// name" from "the bundle exists but the texture inside is named something else" - and the
        /// answer decides whether the fix is a name mapping or the MaterialManifest indirection.
        /// DoesAssetBundleExist consults the manifest of AVAILABLE bundles; IsAssetBundleLoaded
        /// says whether our load actually landed. Logged for the first few only, then counted.
        /// </summary>
        private void Fail(string id, string why)
        {
            _failed.Add(id);
            _inFlight.Remove(id);
            _active--;

            if (_failLogs++ >= 5)
            {
                if (_failLogs % 25 == 0)
                    Plugin.Log.LogWarning("card art: " + _failed.Count + " cards unavailable, " +
                                          _cache.Count + " resolved.");
                return;
            }

            DumpBundleNames();

            string exists = "?", existsLower = "?", loaded = "?";
            try
            {
                var mgr = SafeManager();
                if (mgr != null)
                {
                    exists = mgr.DoesAssetBundleExist(id).ToString();
                    existsLower = mgr.DoesAssetBundleExist(id.ToLower()).ToString();
                    loaded = mgr.IsAssetBundleLoaded(id).ToString();
                }
            }
            catch { }

            Plugin.Log.LogWarning("card art unavailable for \"" + id + "\" (" + why +
                                  ") bundleExists=" + exists + " bundleExistsLower=" + existsLower +
                                  " bundleLoaded=" + loaded + "; drawing a text tile instead.");
        }
    }
}
