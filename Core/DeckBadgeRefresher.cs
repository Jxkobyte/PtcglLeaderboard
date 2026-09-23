using UnityEngine;

namespace PtcglLeaderboard.Core
{
    /// <summary>
    /// Keeps the deck tiles' win/loss badges current.
    ///
    /// DeckBadge hangs off a Harmony patch on DeckSelectEntry.Setup, which the client calls when a
    /// tile binds to a deck. Tiles already built never bind again, so nothing re-runs over them -
    /// and after a hot reload the labels from the PREVIOUS build stay on screen, since they are
    /// plain objects that outlive the code that wrote them. That reads as a change not working.
    ///
    /// A one-second sweep fixes it without fighting the patch: the patch still does the work when
    /// a tile binds, and this catches everything already there. It also keeps a record current
    /// after a match ends while the deck screen happens to be open.
    /// </summary>
    internal class DeckBadgeRefresher : MonoBehaviour
    {
        private float _next;

        private void Update()
        {
            if (!DeckBadge.Enabled || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            DeckBadge.RefreshExisting();
        }
    }
}
