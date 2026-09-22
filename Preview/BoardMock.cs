using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using PrizeTracker.Core;

namespace Preview
{
    /// <summary>
    /// A 1920x1080 recreation of the PTCGL play field, with the overlay drawn on top at its real
    /// pixel size.
    ///
    /// The point is not to look exactly like the game - it is to answer the questions you can only
    /// answer at full resolution with a real board underneath: is the panel readable, is it the
    /// right size, does it cover anything that matters, are the card tiles big enough to recognise
    /// at a glance. Those are invisible in an isolated panel mock and cost a minute of game loading
    /// to check for real.
    ///
    /// The board state is a REAL one, lifted from a PokeAI self-play replay, and the overlay
    /// beside it is driven by the REAL Tracker fed from that same state - so the two genuinely
    /// describe the same game rather than being two unrelated mockups.
    /// </summary>
    internal static class BoardMock
    {
        // ---- card art -------------------------------------------------
        /// <summary>
        /// Maps an engine cardSourceID to a TCGdex image URL.
        ///
        /// The engine writes set prefixes unpadded ("sv6_128"), TCGdex pads them ("sv06"), and
        /// half-set expansions use a "-5" marker that becomes ".5" ("me2-5" -> "me02.5"). Basic
        /// energy (sve/mee) has no art hosted anywhere, so it deliberately returns null.
        /// </summary>
        public static string Art(string srcId)
        {
            if (string.IsNullOrEmpty(srcId) || !srcId.Contains("_")) return null;
            var parts = srcId.Split('_');
            var set = parts[0];
            var num = parts[parts.Length - 1];
            if (set == "sve" || set == "mee") return null;

            var m = System.Text.RegularExpressions.Regex.Match(set, @"^(sv|me)(\d+)(-5)?$");
            if (!m.Success) return null;
            var fam = m.Groups[1].Value;
            var n = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var half = m.Groups[3].Success ? ".5" : "";
            var tcg = fam + n.ToString("00", CultureInfo.InvariantCulture) + half;
            return "https://assets.tcgdex.net/en/" + fam + "/" + tcg + "/" + num + "/low.png";
        }

        /// <summary>
        /// Candidate art URLs in priority order. TCGdex does not pad collector numbers
        /// consistently across sets - some store "96", others "096" - so a single guessed URL is
        /// unreliable even when the set id is right. The page walks these on error and finally
        /// degrades to the card's name.
        /// </summary>
        public static string[] ArtCandidates(string srcId)
        {
            var first = Art(srcId);
            if (first == null) return new string[0];
            var num = srcId.Split('_').Last();
            var list = new List<string> { first };
            int v;
            if (int.TryParse(num, out v))
            {
                var tail = "/" + num + "/low.png";
                var padded = first.Replace(tail, "/" + v.ToString("000", CultureInfo.InvariantCulture) + "/low.png");
                if (!list.Contains(padded)) list.Add(padded);
                var bare = first.Replace(tail, "/" + v.ToString(CultureInfo.InvariantCulture) + "/low.png");
                if (!list.Contains(bare)) list.Add(bare);
            }
            var high = first.Replace("/low.png", "/high.png");
            if (!list.Contains(high)) list.Add(high);
            return list.ToArray();
        }

        /// <summary>An img that walks the candidate list on error, then shows the card name.</summary>
        private static string Img(string srcId, string name, string fallbackClass)
        {
            var cands = ArtCandidates(srcId);
            if (cands.Length == 0)
                return "<div class='" + fallbackClass + "'>" + Esc(name) + "</div>";
            var rest = string.Join("|", cands.Skip(1).ToArray());
            return "<img src='" + cands[0] + "' data-alt='" + Esc(rest) + "' data-name='" +
                   Esc(name) + "' data-fb='" + fallbackClass + "' onerror='artFail(this)'>";
        }

        private static string Esc(string s)
        {
            // Attributes here are single-quoted, so the apostrophe MUST be escaped too - otherwise
            // a card like "Boss's Orders" truncates its title and, worse, breaks the data-* art
            // fallback chain.
            return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                            .Replace("\"", "&quot;").Replace("'", "&#39;");
        }

        // ---- board pieces ---------------------------------------------
        private static string CardEl(string name, string srcId, double x, double y, double w,
                                     int damage = 0, int energy = 0, string cls = "")
        {
            double h = w * 7.0 / 5.0;
            var sb = new StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "<div class='card {4}' style='left:{0}px;top:{1}px;width:{2}px;height:{3}px'>",
                x, y, w, h, cls);
            sb.Append(Img(srcId, name, "cname"));
            if (damage > 0) sb.Append("<div class='dmg'>" + damage + "</div>");
            if (energy > 0) sb.Append("<div class='nrg'>" + energy + "</div>");
            sb.Append("</div>");
            return sb.ToString();
        }

        private static string BackEl(double x, double y, double w, string label = null)
        {
            double h = w * 7.0 / 5.0;
            var sb = new StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "<div class='back' style='left:{0}px;top:{1}px;width:{2}px;height:{3}px'>", x, y, w, h);
            if (label != null) sb.Append("<span>" + Esc(label) + "</span>");
            sb.Append("</div>");
            return sb.ToString();
        }

        private static string Label(string text, double x, double y, double w)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "<div class='lbl' style='left:{0}px;top:{1}px;width:{2}px'>{3}</div>", x, y, w, Esc(text));
        }

        /// <summary>Draws one player's half of the field.</summary>
        private static string Side(JObject side, bool mine, string stadium)
        {
            var sb = new StringBuilder();

            // Vertical bands. The midline is at y=539: the opponent's actives/bench sit above it,
            // mine below, the way the two sides face each other in the real client.
            double benchY = mine ? 750 : 100;
            double activeY = mine ? 560 : 300;
            double pileY = mine ? 640 : 260;
            double prizeY = mine ? 600 : 140;

            const double ACT = 120, BEN = 92, PILE = 78, PRZ = 54;

            // active
            var act = side["active"] as JObject;
            if (act != null)
                sb.Append(CardEl((string)act["name"], (string)act["srcId"], 900, activeY, ACT,
                                 (int)(act["dmg"] ?? 0), ((JArray)act["energy"] ?? new JArray()).Count, "active"));

            // bench, centred
            var bench = (JArray)side["bench"] ?? new JArray();
            double bw = bench.Count * (BEN + 10) - 10;
            double bx = 960 - bw / 2;
            for (int i = 0; i < bench.Count; i++)
            {
                var b = (JObject)bench[i];
                sb.Append(CardEl((string)b["name"], (string)b["srcId"], bx + i * (BEN + 10), benchY, BEN,
                                 (int)(b["dmg"] ?? 0), ((JArray)b["energy"] ?? new JArray()).Count));
            }

            // deck + discard on the right
            int deckCount = (int)side["deck"];
            sb.Append(BackEl(1640, pileY, PILE, "deck " + deckCount));
            var disc = (JArray)side["discardList"] ?? new JArray();
            if (disc.Count > 0)
            {
                var top = (JObject)disc[0];
                sb.Append(CardEl((string)top["name"], (string)top["srcId"], 1740, pileY, PILE, 0, 0, "dim"));
            }
            sb.Append(Label("discard " + disc.Count, 1740, pileY + PILE * 1.4 + 4, PILE));

            // prizes on the left, 2 x 3
            int prizes = (int)side["prize"];
            for (int i = 0; i < prizes; i++)
                sb.Append(BackEl(150 + (i % 2) * (PRZ + 8), prizeY + (i / 2) * (PRZ * 1.4 + 8), PRZ));
            sb.Append(Label("prizes " + prizes, 150, prizeY + 3 * (PRZ * 1.4 + 8), PRZ * 2 + 8));

            // my hand, fanned along the bottom
            if (mine)
            {
                var hand = (JArray)side["hand"] ?? new JArray();
                const double HW = 104;
                double hx = 960 - (hand.Count * (HW + 12) - 12) / 2;
                for (int i = 0; i < hand.Count; i++)
                {
                    var c = (JObject)hand[i];
                    sb.Append(CardEl((string)c["name"], (string)c["srcId"], hx + i * (HW + 12), 900, HW));
                }
            }
            else
            {
                // opponent hand is face down
                const double HW = 80;
                int n = 5;
                double hx = 960 - (n * (HW * 0.55) ) / 2;
                for (int i = 0; i < n; i++) sb.Append(BackEl(hx + i * HW * 0.55, -40, HW));
            }

            return sb.ToString();
        }

        // ---- page ------------------------------------------------------
        public static string Render(JObject fx, Tracker tracker)
        {
            var sb = new StringBuilder();
            sb.Append(@"<!doctype html><meta charset='utf-8'><title>Board + overlay @ 1920x1080</title>
<style>
 html,body{margin:0;background:#0a0c10;font:13px/1.4 'Segoe UI',sans-serif;color:#e9edf3}
 .stage{position:relative;width:1920px;height:1080px;overflow:hidden;
        background:radial-gradient(ellipse at 50% 50%, #1d3a4d 0%, #101c26 60%, #0a1016 100%)}
 .mat{position:absolute;left:120px;top:90px;width:1680px;height:900px;border:2px solid #24425a;
      border-radius:14px;background:rgba(255,255,255,.015)}
 .midline{position:absolute;left:120px;top:539px;width:1680px;height:2px;background:#24425a}
 .card{position:absolute;border-radius:5px;overflow:hidden;background:#0c0e12;
       box-shadow:0 3px 10px rgba(0,0,0,.6)}
 .card img{width:100%;height:100%;object-fit:cover;display:block}
 .card.active{box-shadow:0 0 0 2px #ffd479, 0 4px 16px rgba(0,0,0,.7)}
 .card.dim{opacity:.85}
 .cname{font-size:10px;padding:4px;text-align:center;color:#c7cedb}
 .dmg{position:absolute;right:3px;top:3px;background:#c0392b;color:#fff;font-weight:700;
      font-size:11px;border-radius:50%;width:20px;height:20px;line-height:20px;text-align:center}
 .nrg{position:absolute;left:3px;bottom:3px;background:rgba(6,8,12,.85);color:#9fe0ff;font-size:10px;
      font-weight:700;padding:1px 4px;border-radius:3px}
 .back{position:absolute;border-radius:5px;background:linear-gradient(145deg,#1f4a7a,#122b47);
       border:1px solid #2f5f8f;box-shadow:0 2px 8px rgba(0,0,0,.5);display:flex;align-items:center;
       justify-content:center}
 .back span{font-size:10px;color:#9fc4e4}
 .lbl{position:absolute;font-size:10px;color:#7c93a8;text-align:center}
 /* the overlay, at its real size */
 .overlay{position:absolute;left:40px;top:120px;width:360px;height:460px;background:#12141a;
          border:1px solid #2b2e36;box-sizing:border-box;padding:8px;
          box-shadow:0 8px 30px rgba(0,0,0,.65)}
 .title{font-weight:700;font-size:12px;margin:0 0 6px}
 .tabs{display:flex;gap:2px;margin-bottom:6px}
 .tab{flex:1;text-align:center;font-size:10px;font-weight:700;padding:4px 0;background:#1c1f26;color:#79808f}
 .tab.on{background:#66b0ff;color:#0d0f13}
 .score{display:flex;gap:10px;align-items:center;margin:2px 0}
 .score b{color:#66b0ff;font-size:12px}
 .score span{margin-left:auto;color:#79808f;font-size:11px}
 .status{color:#79808f;font-size:10px;margin-bottom:6px;border-bottom:1px solid #2b2e36;padding-bottom:5px}
 .grid{display:grid;grid-template-columns:repeat(4,1fr);gap:5px}
 .tile{background:#20242c;padding:3px}
 .tile.certain{background:#1d3326;outline:1px solid #4fd47f}
 .art{position:relative;aspect-ratio:5/7;background:#0c0e12;overflow:hidden}
 .art img{width:100%;height:100%;object-fit:cover;display:block}
 .noart{font-size:9px;color:#9aa3b2;padding:4px;text-align:center}
 .count{position:absolute;right:2px;top:2px;background:rgba(8,10,14,.85);color:#fff;font-size:10px;
        font-weight:700;padding:1px 4px}
 .bar{height:3px;background:#2b2e36;margin-top:3px}
 .bar i{display:block;height:3px;background:#66b0ff}
 .foot{font-size:10px;text-align:center;padding-top:2px;color:#c7cedb}
 .tile.certain .foot{color:#4fd47f;font-weight:700}
 .art.facedown{display:flex;align-items:center;justify-content:center;
   background:linear-gradient(145deg,#28425f,#16283c);border:4px solid #3a5f88;
   box-sizing:border-box;color:#9fc4e4;font-weight:700;font-size:18px}
 .hint{font-size:10px;color:#79808f;margin-top:5px}
 .seclbl{font-size:10px;font-weight:700;color:#79808f;margin:8px 0 3px;letter-spacing:.06em}
 .lrow{display:flex;align-items:center;gap:6px;background:#20242c;padding:2px 4px;margin-bottom:1px}
 .lrow span{font-size:10px;color:#c7cedb;flex:1;overflow:hidden;white-space:nowrap}
 .lrow .lbar{width:40px;height:4px;background:#2b2e36;display:block}
 .lrow .lbar b{display:block;height:4px;background:#66b0ff}
 .lrow em{font-size:10px;color:#c7cedb;font-style:normal;width:30px;text-align:right}
 .foot-bar{margin-top:7px;border-top:1px solid #2b2e36;padding-top:5px;color:#79808f;font-size:10px}
 .caption{position:absolute;left:120px;top:20px;color:#7c93a8;font-size:12px}
</style>
<script>
// Walk the remaining candidate URLs, then degrade to the card name.
function artFail(el){
  var alts=(el.getAttribute('data-alt')||'').split('|').filter(Boolean);
  if(alts.length){ el.src=alts.shift(); el.setAttribute('data-alt',alts.join('|')); return; }
  var d=document.createElement('div');
  d.className=el.getAttribute('data-fb')||'cname';
  d.textContent=el.getAttribute('data-name')||'';
  el.parentNode.replaceChild(d,el);
}
</script>
<div class='stage'>
  <div class='mat'></div><div class='midline'></div>
");
            sb.Append("<div class='caption'>PTCGL board recreation @ 1920&times;1080 &mdash; real replay state (turn "
                      + fx["turn"] + "), overlay at real size, driven by the real Tracker</div>");

            sb.Append(Side((JObject)fx["opp"], false, (string)fx["stadium"]));
            sb.Append(Side((JObject)fx["me"], true, (string)fx["stadium"]));

            // stadium in the middle
            var stad = (string)fx["stadium"];
            if (!string.IsNullOrEmpty(stad))
            {
                sb.Append("<div class='card' style='left:640px;top:470px;width:92px;height:129px'>" +
                          "<div class='cname'>" + Esc(stad) + "</div></div>");
                sb.Append(Label("stadium", 640, 604, 92));
            }

            sb.Append(Overlay(tracker));
            sb.Append("</div>");
            return sb.ToString();
        }

        private static string Pct(double p)
        {
            if (p <= 0) return "-";
            if (p >= 0.995) return "100%";
            return (int)Math.Round(p * 100) + "%";
        }

        /// <summary>
        /// The prize cards: one tile per remaining prize, face down until solved. Shared by the
        /// board mock and the standalone panel preview so both show the same thing.
        /// </summary>
        public static string PrizeGrid(Tracker t)
        {
            var sb = new StringBuilder();
            sb.Append("<div class='grid'>");
            foreach (var p in t.PrizeSlots)
            {
                if (p.Known)
                {
                    sb.Append("<div class='tile certain' title='" + Esc(p.Name) + "'><div class='art'>");
                    sb.Append(Img(p.SourceId, p.Name, "noart"));
                    sb.Append("</div><div class='foot'>PRIZED</div></div>");
                }
                else
                {
                    sb.Append("<div class='tile' title='unknown prize'><div class='art facedown'>?</div>" +
                              "<div class='foot'>&nbsp;</div></div>");
                }
            }
            sb.Append("</div>");

            int known = t.PrizeSlotsKnown, total = t.PrizeSlots.Count;
            sb.Append("<div class='hint'>" + (known == total
                ? "All " + total + " prizes known."
                : known + " of " + total + " known - search your deck to solve the rest.") + "</div>");

            var likely = t.Likely;
            if (known < total && likely.Count > 0)
            {
                sb.Append("<div class='seclbl'>MOST LIKELY PRIZED</div>");
                int shown = 0;
                foreach (var r in likely)
                {
                    if (shown++ >= 6) break;
                    sb.Append("<div class='lrow'><span>" + Esc(r.Name) +
                              (r.Unaccounted > 1 ? " &times;" + r.Unaccounted : "") + "</span>" +
                              "<i class='lbar'><b style='width:" + (int)Math.Round(r.ProbAnyPrized * 100) +
                              "%'></b></i><em>" + Pct(r.ProbAnyPrized) + "</em></div>");
                }
            }
            return sb.ToString();
        }

        /// <summary>The overlay panel, at the size it really is in game.</summary>
        private static string Overlay(Tracker t)
        {
            var sb = new StringBuilder();
            sb.Append("<div class='overlay'><div class='title'>Prize Tracker</div><div class='tabs'>");
            foreach (var n in new[] { "PRIZES", "DECK", "OPP", "SET" })
                sb.Append("<span class='tab" + (n == "PRIZES" ? " on" : "") + "'>" + n + "</span>");
            sb.Append("</div>");
            sb.Append("<div class='score'><b>You " + t.MyPrizes + "</b><b>Opp " + t.OppPrizes + "</b>" +
                      "<span>deck " + t.MyDeckCount + " &middot; hand " + t.MyHand + "</span></div>");
            sb.Append("<div class='status'>" + Esc(t.Status) + "</div>");
            sb.Append(PrizeGrid(t));
            sb.Append("<div class='foot-bar'>F1 hide &nbsp;|&nbsp; FPS cap: 60 (match)</div></div>");
            return sb.ToString();
        }
    }
}
