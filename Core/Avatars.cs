using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace PrizeTracker.Core
{
    /// <summary>
    /// Stores a picture of each player's avatar as it appeared on the end-game screen, so a match
    /// row can show the actual pose from that actual game - winner celebrating, loser not.
    ///
    /// Why capture rather than re-render: an Outfit is a look plus a dictionary of item ids, not an
    /// image. The client builds the avatar as a 3D model and renders it into a RenderTexture. For a
    /// match that finished last week there is nothing to draw from, so the only way to have a
    /// likeness is to take one while the client is still showing it. The end-game screen is the
    /// right moment because the client is already playing the win/lose poses there - we get the
    /// expression for free instead of trying to drive an animator.
    ///
    /// Files live beside the match log, named after the match, and are just PNGs.
    /// </summary>
    internal static class Avatars
    {
        private static readonly Dictionary<string, Texture2D> _cache =
            new Dictionary<string, Texture2D>();
        private static readonly HashSet<string> _missing = new HashSet<string>();

        public static string Folder
        {
            get { return Path.Combine(BepInEx.Paths.ConfigPath, "PrizeTracker/avatars"); }
        }

        /// <summary>
        /// The client's own names for the two avatar render targets, found by probing a real
        /// game-over rather than guessing a GameObject path:
        ///   BehindHandUI_Landscape/.../OpponentAvatarFrame/.../OpponentAvatarRenderTextureDisplay
        /// Matching on the TEXTURE name rather than that path means a UI reshuffle does not break
        /// the capture, and it tells the two players apart unambiguously.
        /// </summary>
        public const string OppTexture = "OpponentAvatarRenderTexture";
        public const string MeTexture = "PlayerAvatarRenderTexture";

        /// <summary>Stable per-match key. The timestamp is unique enough and needs no extra field.</summary>
        public static string KeyFor(MatchRecord m) { return Key(m, "opp"); }

        public static string Key(MatchRecord m, string who)
        {
            return m == null ? null : "m" + m.WhenUtc.Ticks + "-" + who;
        }

        /// <summary>
        /// The most recent likeness we hold of a named player, or null if we have never seen them.
        ///
        /// An avatar is a 3D model the client renders into a RenderTexture, so there is nothing to
        /// draw for a player whose game we have not watched - see the note at the top of this
        /// file. That makes this a lookup over matches we have actually played: our own face comes
        /// from the newest match, anyone else's from the newest match against them. Callers are
        /// expected to have something to show when it returns null.
        /// </summary>
        public static Texture2D ForPlayer(MatchHistory history, string screenName, bool isMe)
        {
            if (history == null) return null;
            var records = history.Records;
            if (records == null) return null;

            MatchRecord best = null;
            for (int i = 0; i < records.Count; i++)
            {
                var m = records[i];
                if (m == null) continue;
                if (!isMe)
                {
                    if (string.IsNullOrEmpty(screenName) || string.IsNullOrEmpty(m.Opponent)) continue;
                    if (!m.Opponent.Equals(screenName, StringComparison.OrdinalIgnoreCase)) continue;
                }
                if (best == null || m.WhenUtc > best.WhenUtc) best = m;
            }
            if (best == null) return null;

            // Newest first is only the usual order, not a guarantee, so the newest match wins on
            // its timestamp rather than on its position in the list.
            return Get(Key(best, isMe ? "me" : "opp"));
        }

        public static Texture2D Get(string key)
        {
            if (string.IsNullOrEmpty(key) || _missing.Contains(key)) return null;
            Texture2D cached;
            if (_cache.TryGetValue(key, out cached) && cached != null) return cached;

            try
            {
                var path = Path.Combine(Folder, key + ".png");
                if (!File.Exists(path)) { _missing.Add(key); return null; }

                var tex = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                if (!ImageConv.Load(tex, File.ReadAllBytes(path))) { _missing.Add(key); return null; }
                tex.hideFlags = HideFlags.HideAndDontSave;
                _cache[key] = tex;
                return tex;
            }
            catch { _missing.Add(key); return null; }
        }

        // -----------------------------------------------------------------
        /// <summary>
        /// Look for the end-game avatars for a few seconds after the result lands, and record what
        /// is on screen.
        ///
        /// The poses do not appear the instant the match is decided - the end-game screen animates
        /// in - so this polls rather than taking one look. Every candidate is logged WITH ITS PATH:
        /// until that names the real avatar targets, guessing which RawImage is the opponent would
        /// be exactly the blind GameObject-path guessing that has cost this project several rounds.
        /// </summary>
        public static IEnumerator CaptureAfterGame(MonoBehaviour host, MatchRecord match)
        {
            if (host == null || match == null) yield break;

            var deadline = Time.unscaledTime + 30f;
            string signature = null;
            int savedOppArea = 0, savedMeArea = 0;
            float oppSettleUntil = 0f, meSettleUntil = 0f;

            // How long to keep re-reading a target after it first appears.
            //
            // The posed avatars ANIMATE in, so the first frame of a full-size target catches the
            // character mid-move - arms out, halfway through a gesture. Re-reading for a few
            // seconds and letting the last read win lands on the settled pose instead.
            //
            // Bounded deliberately rather than running to the 30s deadline: a RenderTexture is a
            // live surface, and once the results screen closes it is recycled, so a late read
            // captures whatever replaced it. That mistake already produced a blank once.
            const float SettleSeconds = 5f;

            while (Time.unscaledTime < deadline)
            {
                yield return new WaitForSeconds(1f);

                List<Candidate> found;
                try { found = Scan(); }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("avatar scan failed: " + e.Message);
                    yield break;
                }
                if (found.Count == 0) continue;

                // Report whenever the SET CHANGES, not just the first time.
                //
                // The first version logged once and stopped: it saw the small in-match HUD
                // portraits and never noticed that the Victory screen's full-body posed avatars
                // appear a moment later. Watching for changes is what makes the difference between
                // capturing a headshot and capturing the pose.
                var sig = string.Join(";", found.Select(c => c.Kind + "@" + c.Size).ToArray());
                if (sig != signature)
                {
                    signature = sig;
                    Plugin.Log.LogWarning("avatar probe: " + found.Count + " render target(s):");
                    foreach (var c in found)
                        Plugin.Log.LogWarning("  " + c.Size + "  " + c.Kind + "  " + c.Path);
                }

                // Save the moment a bigger target is seen, NOT at the end of the window.
                //
                // The first version remembered the best RenderTexture and read it after 30s - by
                // which time the results screen had closed and the texture had been recycled, so
                // it wrote a 1225-byte blank. A RenderTexture is a live surface, not a snapshot:
                // it is only worth what is rendered into it AT THE MOMENT you read it.
                foreach (var c in found)
                {
                    var rt = c.Texture as RenderTexture;
                    if (rt == null) continue;
                    int area = rt.width * rt.height;

                    // A bigger target always wins; the same size keeps winning for a few seconds
                    // so the pose can settle. ">=" is what allows the re-read at all.
                    bool bigger = area > savedOppArea;
                    if (rt.name == OppTexture &&
                        (bigger || (area >= savedOppArea && Time.unscaledTime < oppSettleUntil)))
                    {
                        if (Save(Key(match, "opp"), rt))
                        {
                            if (bigger)
                            {
                                oppSettleUntil = Time.unscaledTime + SettleSeconds;
                                Plugin.Log.LogWarning("avatar capture: opponent " + rt.width + "x" + rt.height +
                                                      " from " + c.Path + " (re-reading for " +
                                                      SettleSeconds + "s so the pose settles)");
                            }
                            savedOppArea = area;
                        }
                    }
                    else if (rt.name == MeTexture &&
                             (area > savedMeArea ||
                              (area >= savedMeArea && Time.unscaledTime < meSettleUntil)))
                    {
                        bool meBigger = area > savedMeArea;
                        if (Save(Key(match, "me"), rt))
                        {
                            if (meBigger) meSettleUntil = Time.unscaledTime + SettleSeconds;
                            savedMeArea = area;
                        }
                    }
                }
            }

            if (savedOppArea == 0 && signature == null)
                Plugin.Log.LogWarning("avatar probe: no render targets in the 30s after the game ended " +
                                      "- the posed avatars are drawn some other way.");
        }

        internal class Candidate
        {
            public string Path = "", Kind = "", Size = "";
            public Texture Texture;
            public RectTransform Rect;
        }

        /// <summary>Every live RawImage backed by a RenderTexture - how the client draws avatars.</summary>
        private static List<Candidate> Scan()
        {
            var list = new List<Candidate>();
            foreach (var raw in Resources.FindObjectsOfTypeAll<RawImage>())
            {
                if (raw == null || raw.texture == null) continue;
                if (!raw.gameObject.activeInHierarchy) continue;
                if (!(raw.texture is RenderTexture)) continue;

                var rt = raw.rectTransform;
                list.Add(new Candidate
                {
                    Path = PathOf(raw.transform),
                    Kind = raw.texture.GetType().Name + " \"" + raw.texture.name + "\"",
                    Size = Mathf.RoundToInt(rt.rect.width) + "x" + Mathf.RoundToInt(rt.rect.height),
                    Texture = raw.texture,
                    Rect = rt,
                });
            }
            return list;
        }

        /// <summary>Copy a RenderTexture into a PNG on disk. Used once the target is known.</summary>
        public static bool Save(string key, RenderTexture source, int maxSide = 256)
        {
            if (string.IsNullOrEmpty(key) || source == null) return false;
            RenderTexture previous = RenderTexture.active;
            try
            {
                int w = source.width, h = source.height;
                float scale = Mathf.Min(1f, (float)maxSide / Mathf.Max(w, h));
                int tw = Mathf.Max(1, Mathf.RoundToInt(w * scale));
                int th = Mathf.Max(1, Mathf.RoundToInt(h * scale));

                var full = new Texture2D(w, h, TextureFormat.ARGB32, false);
                RenderTexture.active = source;
                full.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                full.Apply();

                Texture2D outTex = full;
                if (scale < 1f)
                {
                    var small = new Texture2D(tw, th, TextureFormat.ARGB32, false);
                    for (int y = 0; y < th; y++)
                        for (int x = 0; x < tw; x++)
                            small.SetPixel(x, y, full.GetPixelBilinear((x + 0.5f) / tw, (y + 0.5f) / th));
                    small.Apply();
                    outTex = small;
                }

                Directory.CreateDirectory(Folder);
                var png = ImageConv.Png(outTex);
                if (png == null) { Plugin.Log.LogWarning("avatar save: no PNG encoder available."); return false; }
                File.WriteAllBytes(Path.Combine(Folder, key + ".png"), png);

                UnityEngine.Object.Destroy(full);
                if (outTex != full) UnityEngine.Object.Destroy(outTex);
                _missing.Remove(key);
                _cache.Remove(key);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("avatar save failed: " + e.Message);
                return false;
            }
            finally { RenderTexture.active = previous; }
        }

        /// <summary>
        /// EncodeToPNG and LoadImage live in UnityEngine.ImageConversionModule, which targets
        /// netstandard2.1. This plugin has to be net472 for BepInEx's Mono runtime, so referencing
        /// that module fails to COMPILE (CS1705) even though it is present and works at RUNTIME.
        /// Reflection gets us the methods without the reference.
        /// </summary>
        private static class ImageConv
        {
            private static bool _resolved;
            private static System.Reflection.MethodInfo _png, _load;

            private static void Resolve()
            {
                if (_resolved) return;
                _resolved = true;
                try
                {
                    var t = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
                    if (t == null) return;
                    _png = t.GetMethod("EncodeToPNG", new[] { typeof(Texture2D) });
                    _load = t.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) });
                }
                catch { }
            }

            public static byte[] Png(Texture2D tex)
            {
                Resolve();
                if (_png == null || tex == null) return null;
                try { return _png.Invoke(null, new object[] { tex }) as byte[]; }
                catch { return null; }
            }

            public static bool Load(Texture2D tex, byte[] data)
            {
                Resolve();
                if (_load == null || tex == null || data == null) return false;
                try { return (bool)_load.Invoke(null, new object[] { tex, data }); }
                catch { return false; }
            }
        }

        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null; x = x.parent) parts.Insert(0, x.name);
            return string.Join("/", parts.ToArray());
        }
    }
}
