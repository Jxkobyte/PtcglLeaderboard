using System.Collections.Generic;
using UnityEngine;

namespace PrizeTracker.Core
{
    /// <summary>
    /// Makes the win/loss badge ride the deck tile's hover animation.
    ///
    /// Two attempts at guessing which node moves both failed - the deck-name container stays put,
    /// and "the largest Image" turned out to be a masked "Gradient" overlay, which hid the badge
    /// completely. So this stops guessing and MEASURES instead: it watches the tile's children and
    /// copies whichever one is currently displaced furthest from its resting place.
    ///
    /// Rest is taken as the LOWEST position each child has been seen at, because the hover lifts
    /// the card upward. That self-corrects even if the very first frame is sampled mid-hover.
    /// </summary>
    internal class BadgeFollower : MonoBehaviour
    {
        private RectTransform _entry;
        private RectTransform _self;
        private Vector2 _base;
        private readonly Dictionary<Transform, Vector3> _rest = new Dictionary<Transform, Vector3>();

        private List<Transform> _watch;

        public void Setup(RectTransform entry, RectTransform self, Vector2 basePos)
        {
            _entry = entry;
            _self = self;
            _base = basePos;
        }

        /// <summary>
        /// Watch ALL descendants, not just the tile's direct children.
        ///
        /// The first version only looked one level down and saw nothing move, because the node the
        /// hover animates sits deeper in the tile. Collected once and reused, since walking the
        /// hierarchy every frame for every tile would not be free.
        /// </summary>
        private void Collect()
        {
            _watch = new List<Transform>();
            foreach (var t in _entry.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t == _entry || t == _self.transform) continue;
                if (t.IsChildOf(_self.transform)) continue;   // never chase ourselves
                _watch.Add(t);
                if (_watch.Count >= 60) break;
            }
        }

        private void LateUpdate()
        {
            if (_entry == null || _self == null) return;

            if (_watch == null) Collect();

            Vector3 offset = Vector3.zero;
            float best = 0f;

            foreach (var child in _watch)
            {
                if (child == null || child == _self.transform) continue;

                var pos = child.localPosition;
                Vector3 rest;
                if (!_rest.TryGetValue(child, out rest)) { _rest[child] = pos; continue; }

                // The resting place is the lowest we have seen; a hover only ever raises it.
                if (pos.y < rest.y) { rest.y = pos.y; _rest[child] = rest; }

                var delta = pos - rest;
                float mag = Mathf.Abs(delta.y);
                if (mag > best) { best = mag; offset = delta; }
            }

            _self.anchoredPosition = _base + new Vector2(offset.x, offset.y);
        }
    }
}
