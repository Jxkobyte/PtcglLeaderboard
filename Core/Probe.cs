using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PrizeTracker.Core
{
    /// <summary>
    /// One-shot structural dump of the game's own menu system, written to a file.
    ///
    /// This exists because every UI mistake in this project so far came from guessing how a screen
    /// was assembled and then paying a full game restart to find out. The real hierarchy - the
    /// navigation object, where screens are parented, what components each nav tab carries, and how
    /// an existing screen lays itself out - answers all of those questions at once.
    ///
    /// It writes to a FILE rather than the BepInEx log: the log interleaves with the client's own
    /// chatter and truncates long lines, and this output is deliberately large.
    ///
    /// Runs automatically once the main menu exists. Nothing here changes the game.
    /// </summary>
    internal class Probe : MonoBehaviour
    {
        private bool _done;
        private float _next;

        private void Update()
        {
            if (_done || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;

            var nav = FindNavigation();
            if (nav == null) return;          // menu not up yet

            _done = true;
            try { Run(nav); }
            catch (Exception e) { Plugin.Log.LogWarning("probe failed: " + e); }
        }

        private static MainMenuNavigation FindNavigation()
        {
            foreach (var n in Resources.FindObjectsOfTypeAll<MainMenuNavigation>())
                if (n != null && n.gameObject.scene.IsValid() && n.isActiveAndEnabled) return n;
            return null;
        }

        private void Run(MainMenuNavigation nav)
        {
            var sb = new StringBuilder();
            sb.AppendLine("PrizeTracker menu probe  " + DateTime.Now);
            sb.AppendLine(new string('=', 100));

            // ---- 1. navigation ------------------------------------------------
            sb.AppendLine();
            sb.AppendLine("## NAVIGATION");
            sb.AppendLine("type   : " + nav.GetType().FullName);
            sb.AppendLine("path   : " + Path(nav.transform));
            var cur = nav.currentScreen;
            sb.AppendLine("current: " + (cur == null ? "(none)" : cur.GetType().Name + " @ " + Path(cur.transform)));

            foreach (var f in nav.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic |
                                                      BindingFlags.Public | BindingFlags.FlattenHierarchy))
            {
                if (!typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType)) continue;
                var v = f.GetValue(nav) as UnityEngine.Object;
                var comp = v as Component;
                sb.AppendLine(string.Format("field  : {0,-22} {1,-34} {2}",
                    f.Name, f.FieldType.Name,
                    v == null ? "(null)" : (comp != null ? Path(comp.transform) : v.name)));
            }

            // ---- 2. every screen ----------------------------------------------
            sb.AppendLine();
            sb.AppendLine("## SCREENS (HUBScreenController)");
            var screens = Resources.FindObjectsOfTypeAll<HUBScreenController>()
                                   .Where(s => s != null && s.gameObject.scene.IsValid())
                                   .OrderBy(s => Path(s.transform)).ToList();
            foreach (var s in screens)
            {
                string type = "?", group = "?";
                try { type = s.screenType.ToString(); } catch { }
                try { group = s._mainHubGroup.ToString(); } catch { }
                var rt = s.transform as RectTransform;
                sb.AppendLine(string.Format("{0,-40} active={1,-5} type={2,-14} group={3,-12} size={4} anchors={5}..{6}",
                    s.GetType().Name, s.gameObject.activeSelf, type, group,
                    rt != null ? rt.rect.size.ToString() : "?",
                    rt != null ? rt.anchorMin.ToString() : "?",
                    rt != null ? rt.anchorMax.ToString() : "?"));
                sb.AppendLine("    path  " + Path(s.transform));
                sb.AppendLine("    comps " + Comps(s.transform));
            }

            // ---- 3. the top bar ------------------------------------------------
            sb.AppendLine();
            sb.AppendLine("## TOP BAR");
            var bar = FindTopBar();
            if (bar == null) sb.AppendLine("(not found)");
            else
            {
                sb.AppendLine("container " + Path(bar));
                sb.AppendLine("comps     " + Comps(bar));
                for (var a = bar.parent; a != null; a = a.parent)
                    sb.AppendLine("ancestor  " + a.name + "   " + Comps(a));

                foreach (Transform tab in bar)
                    Dump(sb, tab, 0, 3);
            }

            // ---- 4. a real screen's internals -----------------------------------
            // Two, so a structure common to both is clearly the house style rather than one
            // screen's quirk.
            foreach (var want in new[] { "HUBProfileScreenController", "HUBCardDexScreenController" })
            {
                var s = screens.FirstOrDefault(x => x.GetType().Name == want);
                if (s == null) continue;
                sb.AppendLine();
                sb.AppendLine("## SCREEN LAYOUT: " + want);
                Dump(sb, s.transform, 0, 4);
            }

            var path = System.IO.Path.Combine(
                BepInEx.Paths.ConfigPath, "PrizeTracker", "probe.txt");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            File.WriteAllText(path, sb.ToString());
            Plugin.Log.LogWarning("menu probe written: " + path + " (" + sb.Length + " chars)");
        }

        // ---------------------------------------------------------------
        private static Transform FindTopBar()
        {
            // Same structural test the nav tab uses: four or more immediate children that each
            // carry a top-bar caption.
            string[] tabs = { "HOME", "DECKS", "PROFILE", "SHOP", "BATTLE PASS", "CARD DEX" };
            foreach (var lbl in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>())
            {
                if (lbl == null || !lbl.gameObject.activeInHierarchy) continue;
                if (!string.Equals((lbl.text ?? "").Trim(), "DECKS", StringComparison.OrdinalIgnoreCase)) continue;

                for (var t = lbl.transform; t != null && t.parent != null; t = t.parent)
                {
                    int n = 0; bool home = false;
                    foreach (Transform c in t.parent)
                    {
                        bool hit = false;
                        foreach (var l in c.GetComponentsInChildren<TextMeshProUGUI>(true))
                        {
                            var v = (l.text ?? "").Trim();
                            if (tabs.Any(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase)))
                            {
                                hit = true;
                                if (string.Equals(v, "HOME", StringComparison.OrdinalIgnoreCase)) home = true;
                            }
                        }
                        if (hit) n++;
                    }
                    if (n >= 4 && home) return t.parent;
                }
            }
            return null;
        }

        private static void Dump(StringBuilder sb, Transform t, int depth, int maxDepth)
        {
            var rt = t as RectTransform;
            var img = t.GetComponent<Image>();
            var txt = t.GetComponent<TextMeshProUGUI>();

            sb.Append(new string(' ', depth * 2)).Append(depth == 0 ? "" : "- ").Append(t.name);
            if (!t.gameObject.activeSelf) sb.Append("  [INACTIVE]");
            if (rt != null) sb.Append("  size=" + F(rt.rect.width) + "x" + F(rt.rect.height));
            if (img != null) sb.Append("  img=" + img.color + (img.sprite != null ? " sprite=" + img.sprite.name : ""));
            if (txt != null) sb.Append("  text=\"" + Trim(txt.text) + "\" fs=" + txt.fontSize +
                                       " color=" + txt.color + " font=" + (txt.font != null ? txt.font.name : "?"));
            sb.Append("  {").Append(Comps(t)).AppendLine("}");

            if (depth >= maxDepth) return;
            foreach (Transform c in t) Dump(sb, c, depth + 1, maxDepth);
        }

        private static string F(float v) { return Mathf.RoundToInt(v).ToString(); }

        private static string Trim(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\n", " ").Replace("\r", "");
            return s.Length > 40 ? s.Substring(0, 40) + "..." : s;
        }

        private static string Comps(Transform t)
        {
            var names = new List<string>();
            foreach (var c in t.GetComponents<Component>())
                if (c != null && !(c is RectTransform) && !(c is Transform) && !(c is CanvasRenderer))
                    names.Add(c.GetType().Name);
            return string.Join(",", names.ToArray());
        }

        private static string Path(Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null; x = x.parent) parts.Insert(0, x.name);
            return string.Join("/", parts.ToArray());
        }
    }
}
