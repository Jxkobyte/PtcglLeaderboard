using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PrizeTracker.Core
{
    /// <summary>
    /// Reports how the CLIENT builds its own add-friend button, so ours can be the same object
    /// rather than an impression of it.
    ///
    /// AddFriendFromMatchResults holds the button in a private serialized field, _sendRequestButton.
    /// Resources.FindObjectsOfTypeAll reaches it even while the results screen is closed, because
    /// the prefab is loaded, so this does not need a match to have just ended.
    ///
    /// Same discipline as the card-aspect probe: ask, do not infer. Guessing at the card crop cost
    /// two wrong answers before the client was simply read.
    /// </summary>
    internal class AddFriendProbe : MonoBehaviour
    {
        private float _next;
        private bool _done;
        private int _tries;

        private void Update()
        {
            if (_done || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 3f;
            try { Run(); } catch (Exception e) { _done = true; Plugin.Log.LogWarning("add-friend probe failed: " + e.Message); }
        }

        private void Run()
        {
            var field = typeof(AddFriendFromMatchResults).GetField(
                "_sendRequestButton", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) { _done = true; Plugin.Log.LogWarning("add-friend probe: no _sendRequestButton field"); return; }

            foreach (var host in Resources.FindObjectsOfTypeAll<AddFriendFromMatchResults>())
            {
                if (host == null) continue;
                var btn = field.GetValue(host) as Button;
                if (btn == null) continue;

                _done = true;
                Plugin.Log.LogWarning("=== client add-friend button ===");
                Plugin.Log.LogWarning("  host: " + Path(host.transform));
                Dump(btn.transform, 0);
                return;
            }

            if (++_tries == 10)
            {
                _done = true;
                Plugin.Log.LogWarning("add-friend probe: no AddFriendFromMatchResults with a button " +
                                      "found in 30s - the results screen prefab only loads with a match. " +
                                      "Listing candidate icon sprites instead.");
                Icons();
            }
        }

        /// <summary>
        /// The sprites that could be the client's friend icon, by name.
        ///
        /// The button itself is only in memory while the results screen is, but its ICON is an
        /// atlas sprite that the rest of the UI also uses, so it can be found without a match.
        /// This is the difference between drawing an approximation of the icon and using the one
        /// the client draws.
        /// </summary>
        private static void Icons()
        {
            var words = new[] { "friend", "social", "addfriend", "add_friend", "person", "player", "invite" };
            var hits = new List<string>();
            foreach (var sp in Resources.FindObjectsOfTypeAll<Sprite>())
            {
                if (sp == null || string.IsNullOrEmpty(sp.name)) continue;
                var n = sp.name.ToLowerInvariant();
                foreach (var w in words)
                    if (n.Contains(w)) { hits.Add(sp.name + "  " + sp.rect.width + "x" + sp.rect.height); break; }
            }
            hits = hits.Distinct().OrderBy(h => h).ToList();
            Plugin.Log.LogWarning("add-friend probe: " + hits.Count + " candidate icon sprites");
            foreach (var h in hits) Plugin.Log.LogWarning("  " + h);
        }

        private static void Dump(Transform t, int depth)
        {
            var pad = new string(' ', 2 + depth * 2);
            var rt = t as RectTransform;
            var img = t.GetComponent<Image>();
            var lbl = t.GetComponent<TextMeshProUGUI>();
            var comps = string.Join(",", t.GetComponents<Component>()
                .Where(c => c != null && !(c is RectTransform) && !(c is CanvasRenderer))
                .Select(c => c.GetType().Name).ToArray());

            Plugin.Log.LogWarning(pad + t.name
                + (t.gameObject.activeSelf ? "" : " [INACTIVE]")
                + (rt != null ? "  size=" + Mathf.RoundToInt(rt.rect.width) + "x" + Mathf.RoundToInt(rt.rect.height) : "")
                + (img != null ? "  sprite=" + (img.sprite != null ? img.sprite.name : "none")
                                 + " color=" + img.color + " type=" + img.type : "")
                + (lbl != null ? "  text=\"" + (lbl.text ?? "") + "\" font=" + (lbl.font != null ? lbl.font.name : "?")
                                 + " size=" + lbl.fontSize + " color=" + lbl.color
                                 + " style=" + lbl.fontStyle + " align=" + lbl.alignment : "")
                + "  {" + comps + "}");

            if (depth >= 3) return;
            foreach (Transform c in t) Dump(c, depth + 1);
        }

        private static string Path(Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null; x = x.parent) parts.Insert(0, x.name);
            return string.Join("/", parts.ToArray());
        }
    }
}
