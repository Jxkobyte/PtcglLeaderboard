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
        /// How tall an avatar is, in world units. Measured off all three groups, which agreed to
        /// within a hundredth - they are the same rig wearing different clothes.
        /// </summary>
        private const float FigureHeight = 1.80f;

        /// <summary>The field of view all three podium cameras are held at (the client's own 25).</summary>
        private const float Fov = 25f;

        /// <summary>
        /// One figure loads at a time. The body type is a global on AvatarManager rather than part
        /// of the outfit, so three concurrent loads would each overwrite the others' answer.
        /// </summary>
        private static bool _loading;

        /// <summary>
        /// End of frame, not end of Update. The client re-frames the player's avatar camera in
        /// its own LateUpdate, so a correction written from an ordinary coroutine step is
        /// overwritten before anything is drawn - which is why first place kept rendering larger
        /// than the other two even while being corrected every single frame.
        /// </summary>
        private static readonly WaitForEndOfFrame _endOfFrame = new WaitForEndOfFrame();

        /// <summary>
        /// How wide a block is, in world units. Public because the podium has to space its columns
        /// exactly this far apart for the three to meet without overlapping, and a number the two
        /// sides each keep their own copy of is a number that drifts apart.
        ///
        /// Not wider: the FRONT face is nearer the camera than the block's centre and so is
        /// magnified by perspective, while the frame it must fit inside is narrower at that depth.
        /// At 2.2 the top-front corner was being clipped at the edge of the render texture.
        /// </summary>
        public const float BlockWidth = 1.9f;

        /// <summary>
        /// How deep a block is, front to back. Shallow on purpose: the camera sits above these,
        /// so the deeper the block the more of its TOP face is in view, and the top face eats the
        /// front face - which is the one carrying the position numeral.
        /// </summary>
        private const float BlockDepth = 0.7f;

        /// <summary>
        /// About how tall the framed figure is, head to foot plus that headroom, in world units.
        /// The podium screen needs the same number to work out where the block ends up inside the
        /// picture, and one shared constant is better than the same 2-ish typed in two places.
        /// </summary>
        public const float FramedHeight = FigureHeight + Headspace;

        /// <summary>What a place borrowed, so it can be handed back exactly as it was.</summary>
        private class Borrowed
        {
            public AvatarBaseController Ctrl;
            public Camera Cam;
            public Vector3 CamPos;      // where the client had it, to give back
            public Vector3 Framed;      // where we need it, to hold it there
            public float Fov;           // and at what field of view
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
            // Which BODY to build is a global on the manager, not part of the outfit, so it has
            // to be set before the load - and only one figure can be loading at a time or the
            // three would race over it. Without this, a male outfit loaded onto a manager still
            // set to female produced no figure at all: no model, and no block either, because
            // staging never ran.
            while (_loading) yield return null;
            _loading = true;
            try
            {
                var m = Mgr;
                if (m != null) m.SetCurrentGender(!male ? false : true);
            }
            catch (Exception e) { Plugin.Log.LogWarning("podium: could not set the body type: " + e.Message); }

            // LoadAvatar pulls each item's asset bundle, so this takes real time and must not be
            // wrapped in a try/catch around the yield - a failure inside it is reported by the
            // client's own logging, and the worst case is a figure that does not appear.
            yield return ctrl.LoadAvatar(outfit);

            // Let the figure settle before anything measures it. LoadAvatar returns once the
            // parts are requested, not once they are all in place, and the ground is read off
            // those parts - measuring on the same frame gave each podium a different answer.
            for (int f = 0; f < 3; f++) yield return null;
            _loading = false;

            if (target == null) { _loading = false; yield break; }   // the screen closed mid-load
            if (!Stage(ctrl, place, target, male, blockH, blockColour))
            {
                Plugin.Log.LogWarning("podium " + place + ": no camera, so no figure.");
                yield break;
            }

            var mgr = Mgr;
            if (mgr == null) yield break;

            // A podium is a victory, so they play the animation the client plays when a player
            // wins a match - ONCE - and then settle into their idle and stay there. Cycling it
            // meant a figure lurching back into a celebration every few seconds for as long as
            // the screen was open, which is a lot of movement for a leaderboard.
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
            string anim = null;
            try { anim = mgr.GetRandomVictoryAnimation(); }
            catch { }
            if (string.IsNullOrEmpty(anim)) yield break;

            var anims = Animators(ctrl);
            if (anims.Length == 0) yield break;
            foreach (var a in anims) if (a != null) a.SetTrigger(anim);

            // Wait for the pose to actually start before asking how long it is - the trigger
            // takes a frame or two to leave idle, and asking too early measures the idle.
            // Bounded, so a pose the controller has no state for cannot hang this.
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


            // Hold the framing. Setting it once is not enough for first place: that is the
            // PLAYER group, which the client drives for its own profile screen and re-frames
            // whenever it feels like it - opening Settings is enough. The result was first
            // place's block rendering a quarter larger than the other two, because its camera
            // had quietly moved closer.
            //
            // Cheap: three vector comparisons a frame, and only while the podium is on screen.
            var mine = _held[place];
            while (target != null && mine != null && mine.Cam != null && _held[place] == mine)
            {
                if ((mine.Cam.transform.position - mine.Framed).sqrMagnitude > 0.0001f)
                    mine.Cam.transform.position = mine.Framed;
                // The field of view has to be held as well: the distance was worked out FROM it,
                // so the client changing it silently rescales the whole picture.
                if (Mathf.Abs(mine.Cam.fieldOfView - mine.Fov) > 0.01f)
                    mine.Cam.fieldOfView = mine.Fov;
                yield return _endOfFrame;
            }
        }

        // Short. A long blend reads as the figure drifting out of the pose rather than
        // finishing it, and because the fade starts that far before the animation ends, it also
        // eats that much of the pose itself.
        private const float Blend = 0.15f;
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

                // The ground is MEASURED from the figure; the height above it is FIXED.
                //
                // Both halves matter. Measuring the height too made the three blocks come out
                // different sizes, because a silhouette changes with the pose and with what has
                // finished loading - so the height is a constant and all three are framed
                // identically. But the ground cannot be taken from the group's transform: the
                // client offsets male and female models differently inside their group (that is
                // what SetPlatformOffset is for), so once the three stopped being the same body
                // type, the transform stopped being where the feet are and the blocks drifted
                // apart again by about 12 units.
                var rends = ctrl.GetComponentsInChildren<Renderer>(true);
                if (rends.Length == 0) return false;
                int layer = rends[0].gameObject.layer;
                var bounds = rends[0].bounds;
                for (int i = 1; i < rends.Length; i++)
                    if (rends[i] != null) bounds.Encapsulate(rends[i].bounds);

                var ground = new Vector3(ctrl.transform.position.x, bounds.min.y,
                                         ctrl.transform.position.z);
                float feet = ground.y;
                float head = feet + FigureHeight;
                // Which side the camera looks from, kept as it was: the professor's camera faces
                // the other way (probed: y rotation 180) and moving it to the near side would put
                // the figure behind it.
                float side = Mathf.Abs(Mathf.DeltaAngle(cam.transform.eulerAngles.y, 0f)) < 90f ? -1f : 1f;

                // The block is pushed AWAY from the camera by half its depth, so the figure ends
                // up standing on the front edge of the top face rather than in the middle of it.
                // Centred, the block's front-top edge sits nearer the camera than the feet do and
                // therefore lower on screen - which read as the figure standing in a recess cut
                // into the podium.
                var blockAt = new Vector3(ground.x, ground.y, ground.z - side * BlockDepth * 0.5f);
                SweepStaleBlocks(layer);
                held.Block = MakeBlock(layer, blockAt, feet, blockH, blockColour);


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
                // The field of view is SET, not read. Inheriting whatever each camera happened to
                // be on made the three podiums render at slightly different scales, because the
                // distance is worked out from it - and the client had already been at these
                // cameras. Pinning it makes all three provably identical.
                cam.fieldOfView = Fov;
                float dist = (top - bottom) / (2f * Mathf.Tan(Fov * 0.5f * Mathf.Deg2Rad));

                held.Framed = new Vector3(ground.x, (top + bottom) * 0.5f, ground.z + side * dist);
                held.Fov = Fov;
                cam.transform.position = held.Framed;

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
            // 1.9 rather than 2.2, which was clipping. The FRONT face is nearer the camera than
            // the block's centre and so is magnified by perspective - about 5% here - while the
            // frame it has to fit inside is correspondingly narrower at that depth. At 2.2 that
            // left roughly 13 units of margin and the top-front corner was being cut off at the
            // edge of the render texture.
            cube.transform.localScale = new Vector3(BlockWidth, h, BlockDepth);

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
                // The figure was dropping a shadow onto the block's top face, which came out as a
                // pale ring under its feet rather than anything shadow-shaped.
                mr.receiveShadows = false;
            }
            return cube;
        }

        /// <summary>
        /// Destroy any block of ours already on this layer.
        ///
        /// This is what was actually wrong with the podiums, found by listing everything the
        /// cameras could see: NINE of our own blocks piled up at the professor's feet, one from
        /// every previous open of the screen. Give() destroys the block it is holding, but a hot
        /// reload resets the statics that hold it, so every reload orphaned the blocks in the
        /// scene and the next open built new ones on top. Stacked cubes at slightly different
        /// heights read as a stepped second top face and a lip along the front, and the picture
        /// changed with every reload as one more went on the pile. Blocks are the only thing we
        /// ever name PodiumBlock, so sweeping by name is safe and survives a reload.
        /// </summary>
        private static void SweepStaleBlocks(int layer)
        {
            int swept = 0;
            foreach (var mr in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
            {
                if (mr == null || mr.gameObject.layer != layer) continue;
                if (mr.gameObject.name != "PodiumBlock") continue;
                UnityEngine.Object.Destroy(mr.gameObject);
                swept++;
            }
            if (swept > 0) Plugin.Log.LogInfo("podium: swept " + swept + " leftover block(s) on layer " + layer);
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

            // Belt and braces: whatever Give() could not reach - a block orphaned by a hot
            // reload - must not be left standing in the avatar scene for the profile screen to
            // find. Every block of ours has the one name, on any layer.
            foreach (var mr in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
                if (mr != null && mr.gameObject.name == "PodiumBlock")
                    UnityEngine.Object.Destroy(mr.gameObject);

            var ctrl = Controller(0);
            if (host == null || ctrl == null) return;
            try { host.StartCoroutine(ctrl.ResyncCurrentAvatar()); }
            catch (Exception e) { Plugin.Log.LogWarning("podium: could not restore our avatar: " + e.Message); }
        }
    }
}
