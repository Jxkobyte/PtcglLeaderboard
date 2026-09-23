using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using SharedSDKUtils;
using TPCI.Avatar;
using UnityEngine;
using UnityEngine.UI;

namespace PrizeTracker.Core
{
    /// <summary>
    /// Live 3D figures on the podium, built from the outfits the board carries.
    ///
    /// This drives the client's OWN avatar system rather than anything of ours. AvatarManager
    /// keeps three avatar groups - player, opponent, and the learning lab's professor - each a
    /// rigged model with its own camera already rendering to its own RenderTexture. That is
    /// exactly three, which is exactly a podium, so the top three places map one-to-one onto them
    /// and nothing has to be instantiated.
    ///
    /// Cloning the group prefab instead was the first design and it is worse: all three of the
    /// client's groups sit at the origin and are told apart by their cameras alone, so extra
    /// copies would either overlap or need their own layers and lighting rebuilt around them. The
    /// existing groups already have all of that.
    ///
    /// The cost is that these are shared: loading a stranger onto the player group changes what
    /// the profile screen would draw. Release() puts our own avatar back, and the opponent and
    /// professor groups are reloaded by the client whenever it next needs them.
    ///
    /// Everything here is reflection-tolerant and guarded. A client update that renames a field
    /// costs the podium its figures and falls back to the still likeness - it does not throw on a
    /// menu screen.
    /// </summary>
    internal static class PodiumAvatars
    {
        private const int Places = 3;

        private static AvatarManager Mgr
        {
            get
            {
                try { return ManagerSingleton<AvatarManager>.instance; }
                catch { return null; }
            }
        }

        public static bool Available { get { return Controller(0) != null; } }

        /// <summary>
        /// The controller for a podium place. First place gets the player group because it is the
        /// one the client keeps most ready; the other two take the opponent and professor groups.
        /// </summary>
        private static AvatarBaseController Controller(int place)
        {
            var mgr = Mgr;
            if (mgr == null) return null;
            try
            {
                switch (place)
                {
                    case 0: return mgr.GetPlayerAvatarController();
                    case 1: return Opponent(mgr);
                    case 2: return mgr.GetLearningLabProfAvatarController();
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// The opponent group. Unlike the other two it has no accessor, so it is read off the
        /// field - the only reflection here, and it degrades to "no figure" rather than throwing.
        /// </summary>
        private static AvatarBaseController Opponent(AvatarManager mgr)
        {
            var f = typeof(AvatarManager).GetField("_opponentAvatarController",
                                                   BindingFlags.Instance | BindingFlags.NonPublic);
            return f == null ? null : f.GetValue(mgr) as AvatarBaseController;
        }

        /// <summary>
        /// Point the image at this place's camera, and make sure that camera is actually running.
        ///
        /// NOT AvatarManager's own connect, which was the first attempt and silently did nothing.
        /// It reads Camera.activeTexture - the texture being rendered into RIGHT NOW - and on a
        /// menu screen these cameras are disabled, so it is always null even though targetTexture
        /// is perfectly good. Probing said exactly that: "enabled=False tex=NULL target=ok".
        ///
        /// It also went through a dictionary that only ever held playerCam and profCam, so third
        /// place could never have connected at all. Going through the controller's own camera
        /// reaches all three.
        /// </summary>
        private static bool Connect(AvatarBaseController ctrl, RawImage image, bool male)
        {
            if (ctrl == null || image == null) return false;
            try
            {
                var camCtrl = ctrl.GetControllerCamera();
                if (camCtrl == null) return false;

                // By component rather than by field name: the camera sits somewhere under the
                // controller and which child it is, is not ours to depend on.
                var cam = camCtrl.GetComponentInChildren<Camera>(true);
                if (cam == null) return false;

                if (cam.targetTexture == null)
                    cam.targetTexture = new RenderTexture(512, 640, 24) { name = "PodiumAvatar" };

                try { camCtrl.SetView(AvatarCameraViewMode.FullBody, male); }
                catch { }                                   // framing is a nicety, not a blocker

                cam.gameObject.SetActive(true);
                cam.enabled = true;
                image.texture = cam.targetTexture;
                image.color = Color.white;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("podium: could not connect a camera: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Dress the figure for a podium place and set it going.
        ///
        /// The texture is connected AFTER the avatar loads, not before: the client's own connect
        /// only takes when the camera has an active texture, which it does not necessarily have
        /// before something has been drawn into it.
        /// </summary>
        public static void Show(MonoBehaviour host, int place, RawImage target,
                                Dictionary<AvatarCustomizationType, string> outfit, bool male)
        {
            if (host == null || target == null || outfit == null) return;
            if (place < 0 || place >= Places) return;
            var ctrl = Controller(place);
            if (ctrl == null) return;
            host.StartCoroutine(Dress(ctrl, place, target, outfit, male));
        }

        private static IEnumerator Dress(AvatarBaseController ctrl, int place, RawImage target,
                                         Dictionary<AvatarCustomizationType, string> outfit, bool male)
        {
            // LoadAvatar pulls each item's asset bundle, so this takes real time and must not be
            // wrapped in a try/catch around the yield - a failure inside it is reported by the
            // client's own logging, and the worst case is a figure that does not appear.
            yield return ctrl.LoadAvatar(outfit);

            if (target == null) yield break;     // the screen closed while we were loading
            if (!Connect(ctrl, target, male))
            {
                Plugin.Log.LogWarning("podium " + place + ": no camera, so no figure.");
                yield break;
            }

            var mgr = Mgr;
            if (mgr == null) yield break;

            // A podium is a victory, so they play the victory animations the client plays when a
            // player wins a match, over and over. Once through and they would stand frozen for as
            // long as the screen is open, which reads as a still image that happens to be 3D.
            //
            // The wait is staggered by place so the three of them are not moving in lockstep, and
            // it runs even if PlayPoseAnimation returns straight away - otherwise a pose the
            // client declines to play would spin this loop as fast as the frame rate.
            while (target != null)
            {
                string anim = null;
                try { anim = mgr.GetRandomVictoryAnimation(); }
                catch { }
                if (string.IsNullOrEmpty(anim)) yield break;

                yield return ctrl.PlayPoseAnimation(anim, false, place == 0, true, null, false);
                yield return new WaitForSeconds(2.5f + place * 0.7f);
            }
        }

        /// <summary>
        /// Put our own avatar back on the player group.
        ///
        /// Called when the screen closes. Without it the profile screen would show whoever
        /// happened to be first on the leaderboard, which is a confusing thing to leave behind.
        /// </summary>
        public static void Release(MonoBehaviour host)
        {
            var ctrl = Controller(0);
            if (host == null || ctrl == null) return;
            try { host.StartCoroutine(ctrl.ResyncCurrentAvatar()); }
            catch (Exception e) { Plugin.Log.LogWarning("podium: could not restore our avatar: " + e.Message); }
        }
    }
}
