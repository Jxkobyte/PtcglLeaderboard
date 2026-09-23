using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UI;

namespace PtcglLeaderboard.Core
{
    /// <summary>
    /// Asks the CLIENT what shape a card texture is meant to be drawn at.
    ///
    /// The texture itself does not answer this reliably. It is 1024x1024 and fully opaque, which
    /// suggested it was a card squashed into a square - but squeezing it back to 0.716 makes the
    /// card look narrow, so that reading was wrong, and measuring circles inside the art keeps
    /// picking up the card's own background instead of the symbol.
    ///
    /// The client draws these exact textures correctly somewhere on screen, so the aspect it uses
    /// is a fact available for the asking rather than something to infer. This finds RawImages
    /// whose texture is a card and reports the rect they are drawn into.
    /// </summary>
    internal class CardAspectProbe : MonoBehaviour
    {
        private static readonly Regex CardTex = new Regex(@"^[a-z0-9\-]+_[a-z]{2}_\d+", RegexOptions.IgnoreCase);

        // Deck customisation items: sleeves (cs_*), deck boxes (db_*) and coins, as the client's
        // own deck tile draws them. Same question as cards - is the square texture squashed, or
        // padded? - and the same way of settling it: read the rect the client uses.
        private static readonly Regex ItemTex = new Regex(@"^(cs|db|co)[_-]", RegexOptions.IgnoreCase);

        private float _next;
        private int _reports;

        private void Update()
        {
            if (_reports >= 1 || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 2f;

            try { Scan(); }
            catch (Exception e) { _reports = 99; Plugin.Log.LogWarning("card aspect probe failed: " + e.Message); }
        }

        private void Scan()
        {
            var seen = new List<string>();
            foreach (var raw in Resources.FindObjectsOfTypeAll<RawImage>())
            {
                if (raw == null || !raw.gameObject.activeInHierarchy) continue;
                if (raw.texture == null) continue;
                bool isCard = CardTex.IsMatch(raw.texture.name);
                bool isItem = ItemTex.IsMatch(raw.texture.name);
                if (!isCard && !isItem) continue;

                var rt = raw.rectTransform;
                float w = rt.rect.width, h = rt.rect.height;
                if (w < 20f || h < 20f) continue;

                // Our own drawing must not be mistaken for the client's.
                if (Owned(rt)) continue;

                seen.Add(raw.texture.name + "  rect " + Mathf.RoundToInt(w) + "x" + Mathf.RoundToInt(h)
                         + "  aspect " + (w / h).ToString("F3")
                         + "  uv " + raw.uvRect
                         + "  tex " + raw.texture.width + "x" + raw.texture.height
                         + "  at " + Path(rt));
                if (seen.Count >= 6) break;
            }

            // Nothing on screen right now? The client's card PREFABS still carry their designed
            // rect, so the intended shape is readable without waiting for a screen that shows
            // cards. Inactive objects are included deliberately - that is the whole point.
            if (seen.Count == 0)
            {
                foreach (var raw in Resources.FindObjectsOfTypeAll<RawImage>())
                {
                    if (raw == null) continue;
                    var n = raw.name;
                    if (n.IndexOf("card", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (n.IndexOf("back", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    var prt = raw.rectTransform;
                    float pw = prt.rect.width, ph = prt.rect.height;
                    if (pw < 20f || ph < 20f) continue;
                    if (Owned(prt)) continue;
                    seen.Add("[prefab] " + n + "  rect " + Mathf.RoundToInt(pw) + "x" + Mathf.RoundToInt(ph)
                             + "  aspect " + (pw / ph).ToString("F3")
                             + "  uv " + raw.uvRect + "  at " + Path(prt));
                    if (seen.Count >= 8) break;
                }
            }

            if (seen.Count == 0) return;
            _reports++;
            Plugin.Log.LogWarning("card aspect probe - how the CLIENT draws card textures:");
            foreach (var s in seen) Plugin.Log.LogWarning("  " + s);
        }

        private static bool Owned(Transform t)
        {
            for (var x = t; x != null; x = x.parent)
                if (x.name.StartsWith("PtcglLeaderboard", StringComparison.Ordinal) ||
                    x.name == "DeckModal") return true;
            return false;
        }

        private static string Path(Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null; x = x.parent) parts.Insert(0, x.name);
            return string.Join("/", parts.ToArray());
        }
    }
}
