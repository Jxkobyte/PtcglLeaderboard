using System;
using System.IO;
using UnityEngine;

namespace PrizeTracker.Core
{
    /// <summary>
    /// Saves a texture to disk, once per name.
    ///
    /// Card textures are square (the log reads 256x256 against a card's 0.716), and how the card
    /// sits inside that square decides the fix: squashed to fill it wants a plain stretch, while
    /// letterboxed with padding wants a crop, and those are opposite. Guessing between them has
    /// already produced one stretched version and one cropped one, so this looks instead.
    ///
    /// A blit through a RenderTexture works even when the source is not CPU-readable, which card
    /// textures generally are not.
    /// </summary>
    internal static class TexDump
    {
        private static readonly System.Collections.Generic.HashSet<string> _done =
            new System.Collections.Generic.HashSet<string>();

        public static void Once(Texture tex, string name)
        {
            if (tex == null || string.IsNullOrEmpty(name) || !_done.Add(name)) return;

            RenderTexture prev = RenderTexture.active;
            RenderTexture rt = null;
            try
            {
                rt = RenderTexture.GetTemporary(tex.width, tex.height, 0);
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;

                var flat = new Texture2D(tex.width, tex.height, TextureFormat.ARGB32, false);
                flat.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                flat.Apply();

                var dir = Path.Combine(BepInEx.Paths.ConfigPath, "PrizeTracker/debug");
                Directory.CreateDirectory(dir);

                // Resolved by name: UnityEngine.ImageConversionModule targets netstandard2.1 and
                // cannot be referenced from this net472 plugin, though it loads fine at runtime.
                var t = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
                var png = t != null ? t.GetMethod("EncodeToPNG", new[] { typeof(Texture2D) }) : null;
                var bytes = png != null ? png.Invoke(null, new object[] { flat }) as byte[] : null;

                if (bytes != null)
                {
                    var safe = name.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
                    var file = Path.Combine(dir, safe + ".png");
                    File.WriteAllBytes(file, bytes);
                    Plugin.Log.LogWarning("texture written: " + file + "  (" + tex.width + "x" + tex.height + ")");
                }
                UnityEngine.Object.Destroy(flat);
            }
            catch (Exception e) { Plugin.Log.LogWarning("texture dump failed: " + e.Message); }
            finally
            {
                RenderTexture.active = prev;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
