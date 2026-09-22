using System;
using RainierClientSDK.source.Friend;

namespace PrizeTracker.Core
{
    /// <summary>
    /// Sending a friend request, through the client's own friend service.
    ///
    /// This mirrors AddFriendFromMatchResults, the button the client itself puts on the end-of-match
    /// screen: it resolves an IFriendService, checks the existing friends list by screen name, and
    /// calls SendFriendRequestAsync(accountId: empty, screenName, Friend.ProfileKeys, onError). We
    /// reach the same service through the static Friend.helper rather than the scene's reference
    /// provider, which is the only difference - the request sent is the client's own.
    ///
    /// Nothing here ever fires by itself. A friend request is a message to a real person, so it
    /// happens on an explicit click and nowhere else.
    /// </summary>
    internal static class Friends
    {
        /// <summary>The client's friend service, or null when it is not up yet.</summary>
        private static IFriendService Service
        {
            get { try { return Friend.helper; } catch { return null; } }
        }

        public static bool Available { get { return Service != null; } }

        /// <summary>Are we already friends with this screen name? Unknown counts as no.</summary>
        public static bool AlreadyFriends(string screenName)
        {
            if (string.IsNullOrEmpty(screenName)) return false;
            var svc = Service;
            if (svc == null) return false;
            try
            {
                // FriendInfo is a struct, so there is no entry to be null - only the name.
                foreach (var kv in svc.friends)
                    if (kv.Value.screenName != null &&
                        kv.Value.screenName.Equals(screenName, StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Send the request. <paramref name="done"/> reports whether it went out, with a reason
        /// when it did not - the caller shows that on the button rather than failing silently.
        ///
        /// The SDK call is async and this is fire-and-forget by design: the client's own button
        /// does the same, and the result that matters to a player is "sent", not the HTTP status.
        /// </summary>
        public static void Send(string screenName, Action<bool, string> done)
        {
            if (string.IsNullOrEmpty(screenName)) { Report(done, false, "no name"); return; }

            var svc = Service;
            if (svc == null) { Report(done, false, "friends service unavailable"); return; }
            if (AlreadyFriends(screenName)) { Report(done, false, "already friends"); return; }

            try
            {
                var task = svc.SendFriendRequestAsync(string.Empty, screenName, Friend.ProfileKeys,
                                                      err => Plugin.Log.LogWarning(
                                                          "friend request error: " + (err != null ? err.ToString() : "?")));
                if (task == null) { Report(done, false, "no response"); return; }
                Plugin.Log.LogInfo("friend request sent to " + screenName);
                Report(done, true, null);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("friend request failed for " + screenName + ": " + e.Message);
                Report(done, false, "failed");
            }
        }

        private static void Report(Action<bool, string> done, bool ok, string why)
        {
            if (done != null) done(ok, why);
        }
    }
}
