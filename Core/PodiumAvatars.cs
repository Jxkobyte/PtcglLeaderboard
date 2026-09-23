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
    /// Live 3D figures standing on real 3D blocks, built from the outfits the board carries.
    ///
    /// This drives the client's OWN avatar system rather than anything of ours. AvatarManager
    /// keeps three avatar groups - player, opponent, and the learning lab's professor - each a
    /// rigged model with its own camera, its own layer and its own corner of the world. That is
    /// exactly three, which is exactly a podium, so the places map one-to-one and nothing has to
    /// be instantiated. Probed, not assumed: the groups sit at x = -113, 887 and 0 on layers 16,
    /// 17 and 31, so a block added to one is invisible to the other two.
    ///
    /// The BLOCK is a real cube in front of the real camera, not a flat panel drawn behind the
    /// picture. That is the whole reason it reads as three dimensional: it takes the same
    /// perspective and the same lighting as the figure, and the figure genuinely stands on it
    /// rather than being composited above it.
    ///
    /// Everything borrowed here is given back in Release(): the camera's position, its target
    /// texture, and our own avatar on the player group - which the profile screen shares, so
    /// leaving a stranger on it would be a confusing thing to walk away from.
    /// </summary>
    internal static class PodiumAvatars
    {
        private const int Places = 3;

        /// <summary>
        /// Room left above the head, in world units, for arms raised by an animation.
        /// </summary>
        private const float Headspace = 0.50f;

        /// <summary>
        /// About how tall the framed figure is, head to foot plus that headroom, in world units.
        /// The podium screen needs the same number to work out where the block ends up inside the
        /// picture, and one shared constant is better than the same 2-ish typed in two places.
        /// </summary>
        public const float FramedHeight = 1.80f + Headspace;

        /// <summary>What a place borrowed, so it can be handed back exactly as it was.</summary>
        private class Borrowed
        {
            public AvatarBaseController Ctrl;
            public Camera Cam;
            public Vector3 CamPos;
            public RenderTexture OriginalTarget;
            public RenderTexture Ours;
            public GameObject Block;
        }

        private static readonly Borrowed[] _held = new Borrowed[Places];

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
        /// Dress a place's figure, stand it on a block, frame both, and set it going.
        ///
        /// <paramref name="blockH"/> is in WORLD units and is what makes first place taller than
        /// third: the camera frames block plus figure, so a taller block pushes its figure higher
        /// inside an image the same size as its neighbours'. Aligning the three images along their
        /// bottoms then produces the stepped shape without any of them being positioned
        /// differently.
        /// </summary>
        public static void Show(MonoBehaviour host, int place, RawImage target,
                                Dictionary<AvatarCustomizationType, string> outfit, bool male,
                                float blockH, Color blockColour)
        {
            if (host == null || target == null || outfit == null) return;
            if (place < 0 || place >= Places) return;
            var ctrl = Controller(place);
            if (ctrl == null) return;
            host.StartCoroutine(Dress(ctrl, place, target, outfit, male, blockH, blockColour));
        }

        private static IEnumerator Dress(AvatarBaseController ctrl, int place, RawImage target,
                                         Dictionary<AvatarCustomizationType, string> outfit,
                                         bool male, float blockH, Color blockColour)
        {
            // LoadAvatar pulls each item's asset bundle, so this takes real time and must not be
            // wrapped in a try/catch around the yield - a failure inside it is reported by the
            // client's own logging, and the worst case is a figure that does not appear.
            yield return ctrl.LoadAvatar(outfit);

            if (target == null) yield break;     // the screen closed while we were loading
            if (!Stage(ctrl, place, target, male, blockH, blockColour))
            {
                Plugin.Log.LogWarning("podium " + place + ": no camera, so no figure.");
                yield break;
            }

            var mgr = Mgr;
            if (mgr == null) yield break;

            // A podium is a victory, so they play the animations the client plays when a player
            // wins a match - and then ease back into their idle instead of snapping out of the
            // pose.
            //
            // The snap is the client's, and it cannot be turned off from outside. PlayPoseAnimation
            // takes a forceIdle flag but its body ignores it, always finishing with
            // ResetAnimationFromTrigger, and every route back to idle in there goes through
            // Animator.Play("idle") - which is a hard cut, never a blend. So the pose is driven
            // here instead: the client's own trigger to start it, and CrossFade to finish, which
            // is the same thing with a blend.
            //
            // Holding the final pose was the other way to avoid the cut, and it looked like a
            // photograph rather than a person.
            while (target != null)
            {
                string anim = null;
                try { anim = mgr.GetRandomVictoryAnimation(); }
                catch { }
                if (string.IsNullOrEmpty(anim)) yield break;

                var anims = Animators(ctrl);
                if (anims.Length == 0) yield break;
                foreach (var a in anims) if (a != null) a.SetTrigger(anim);

                // Wait for the pose to actually start before asking how long it is - the trigger
                // takes a frame or two to leave idle, and asking too early measures the idle.
                // Bounded, so a pose the controller has no state for cannot hang the loop.
                float waited = 0f;
                while (target != null && waited < 2f && IsIdle(anims[0]))
                {
                    waited += Time.deltaTime;
                    yield return null;
                }
                if (target == null) yield break;

                float length = Length(anims[0]);
                if (length > Blend) yield return new WaitForSeconds(length - Blend);
                if (target == null) yield break;

                foreach (var a in anims) if (a != null) a.CrossFade("idle", Blend);
                yield return new WaitForSeconds(RestSeconds + place * 0.8f);
            }
        }

        private const float Blend = 0.35f;
        private const float RestSeconds = 3f;
        private static readonly int IdleHash = Animator.StringToHash("idle");

        private static bool IsIdle(Animator a)
        {
            return a != null && a.GetCurrentAnimatorStateInfo(0).shortNameHash == IdleHash;
        }

        private static float Length(Animator a)
        {
            return a == null ? 0f : a.GetCurrentAnimatorStateInfo(0).length;
        }

        /// <summary>
        /// The animators that drive one figure - body, face, hair and the rest, which all have to
        /// be blended together or the head finishes the pose after the body.
        ///
        /// The client keeps them in a private dictionary keyed by customisation type. That is the
        /// exact set, so it is preferred; the child scan behind it is a fallback for a client
        /// update that renames the field, and picks up the same animators plus possibly a few
        /// unrelated ones, which is survivable where guessing would not be.
        /// </summary>
        private static Animator[] Animators(AvatarBaseController ctrl)
        {
            if (ctrl == null) return new Animator[0];
            try
            {
                var f = typeof(AvatarBaseController).GetField("_currentAvatarAnimatorDictionary",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                var d = f == null ? null : f.GetValue(ctrl) as System.Collections.IDictionary;
                if (d != null && d.Count > 0)
                {
                    var list = new List<Animator>();
                    foreach (System.Collections.DictionaryEntry e in d)
                    {
                        var a = e.Value as Animator;
                        if (a != null) list.Add(a);
                    }
                    if (list.Count > 0) return list.ToArray();
                }
            }
            catch { }
            return ctrl.GetComponentsInChildren<Animator>(true);
        }

        /// <summary>
        /// Put a block under the figure, point the camera at both, and hand the result to the
        /// image.
        ///
        /// NOT AvatarManager's own connect, which was the first attempt and silently did nothing.
        /// It reads Camera.activeTexture - the texture being rendered RIGHT NOW - and on a menu
        /// screen these cameras are disabled, so it is always null even though targetTexture is
        /// perfectly good. Probing said exactly that: "enabled=False tex=NULL target=ok". It also
        /// went through a dictionary holding only playerCam and profCam, so third place could
        /// never have connected at all.
        /// </summary>
        private static bool Stage(AvatarBaseController ctrl, int place, RawImage image, bool male,
                                  float blockH, Color blockColour)
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

                try { camCtrl.SetView(AvatarCameraViewMode.FullBody, male); }
                catch { }                                   // framing is a nicety, not a blocker

                var held = Take(place, cam);
                held.Ctrl = ctrl;

                var rend = ctrl.GetComponentInChildren<Renderer>(true);
                if (rend == null) return false;
                var b = rend.bounds;
                float feet = b.center.y - b.extents.y;
                float head = b.center.y + b.extents.y;

                held.Block = MakeBlock(rend.gameObject.layer, b.center, feet, blockH, blockColour);

                // Frame the block AND the figure. Visible height at a perspective camera is
                // 2 * distance * tan(fov / 2), so the distance follows from how much has to fit -
                // recomputed per place, because the three groups do not share a ground height
                // (probed: their roots sit at y = 0.05, 0 and 0.63).
                // Generous, and deliberately so. The bounds are measured while the figure is
                // STANDING STILL, and then it plays victory animations that throw both arms well
                // above its head - so framing tightly to the idle silhouette cut their hands off
                // at the top of the picture. There is no cheap way to know a pose's extent in
                // advance, so the frame allows for the tallest one.
                const float Headroom = Headspace;
                float bottom = feet - blockH;
                float top = head + Headroom;
                float dist = (top - bottom) / (2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));

                // Which side the camera looks from, kept as it was: the professor's camera faces
                // the other way (probed: y rotation 180) and moving it to the near side would put
                // the figure behind it.
                float side = Mathf.Abs(Mathf.DeltaAngle(cam.transform.eulerAngles.y, 0f)) < 90f ? -1f : 1f;
                cam.transform.position = new Vector3(b.center.x, (top + bottom) * 0.5f,
                                                     b.center.z + side * dist);

                cam.gameObject.SetActive(true);
                cam.enabled = true;
                image.texture = held.Ours;
                image.color = Color.white;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("podium: could not stage a figure: " + e.Message);
                return false;
            }
        }

        /// <summary>Borrow a camera, remembering what to give back.</summary>
        private static Borrowed Take(int place, Camera cam)
        {
            Give(place);
            var held = new Borrowed
            {
                Cam = cam,
                CamPos = cam.transform.position,
                OriginalTarget = cam.targetTexture,
                // Our own target rather than the client's: the aspect has to match the shape we
                // draw it at, and the client's is sized for its own screens.
                // Matches the shape it is drawn at, or the figure comes out stretched.
                Ours = new RenderTexture(540, 616, 24) { name = "PodiumAvatar" },
            };
            cam.targetTexture = held.Ours;
            _held[place] = held;
            return held;
        }

        /// <summary>
        /// The block itself: a cube on the figure's own layer, so only that figure's camera can
        /// see it. Wider than it is deep, because it is only ever viewed head on.
        /// </summary>
        private static GameObject MakeBlock(int layer, Vector3 centre, float feet, float h, Color colour)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "PodiumBlock";
            // By type NAME, not by type: naming Collider drags in UnityEngine.PhysicsModule as a
            // compile-time reference for the sake of one Destroy call on a decorative cube.
            foreach (var c in cube.GetComponents<Component>())
                if (c != null && c.GetType().Name.EndsWith("Collider", StringComparison.Ordinal))
                    UnityEngine.Object.Destroy(c);
            cube.layer = layer;
            cube.transform.position = new Vector3(centre.x, feet - h * 0.5f, centre.z);
            // Shallow on purpose. The camera sits above these blocks, so the deeper the block
            // the more of its TOP face is in view - and the top face eats the front face, which
            // is the one carrying the position numeral. At depth 1.2 third place had more top
            // than front and its numeral had nowhere to sit.
            cube.transform.localScale = new Vector3(2.5f, h, 0.7f);

            var mr = cube.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                // The client is on a scriptable pipeline, so Standard may not exist here; fall
                // through until something does rather than shipping a magenta block.
                var sh = Shader.Find("Universal Render Pipeline/Lit")
                      ?? Shader.Find("Standard")
                      ?? Shader.Find("Unlit/Color");
                if (sh != null)
                {
                    var mat = new Material(sh) { color = colour };
                    if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", colour);
                    mr.sharedMaterial = mat;
                }
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            return cube;
        }

        /// <summary>Give one place's camera back, and destroy the block we put in front of it.</summary>
        private static void Give(int place)
        {
            var held = _held[place];
            _held[place] = null;
            if (held == null) return;
            try
            {
                // Resumed defensively: nothing here pauses it any more, but an earlier visit
                // that held a pose did, and a camera handed back paused never redraws.
                if (held.Ctrl != null) held.Ctrl.ResumeAvatarCamera(false);
                if (held.Cam != null)
                {
                    held.Cam.transform.position = held.CamPos;
                    held.Cam.targetTexture = held.OriginalTarget;
                }
                if (held.Block != null) UnityEngine.Object.Destroy(held.Block);
                if (held.Ours != null) held.Ours.Release();
            }
            catch { }
        }

        /// <summary>
        /// Hand everything back when the screen closes: cameras where they were, blocks gone, and
        /// our own avatar on the player group again.
        /// </summary>
        public static void Release(MonoBehaviour host)
        {
            for (int i = 0; i < Places; i++) Give(i);

            var ctrl = Controller(0);
            if (host == null || ctrl == null) return;
            try { host.StartCoroutine(ctrl.ResyncCurrentAvatar()); }
            catch (Exception e) { Plugin.Log.LogWarning("podium: could not restore our avatar: " + e.Message); }
        }
    }
}
