using System;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>Shipping MasterPainter stroke with the originating player's color mode, without a new raycast.</summary>
    internal static class DirtPainter
    {
        internal static byte[] Capture(CleanableObject target)
        {
            var source = target.GetCurrentDirtTex();
            if (source == null) throw new InvalidOperationException("CleanableObject has no dirt texture");
            if (source is Texture2D readable && readable.isReadable) return readable.EncodeToPNG();
            // Imported materials may use a GPU-only texture; loaded/saved paint is normally CPU-readable.
            var previous = RenderTexture.active;
            var buffer = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
            Texture2D copy = null;
            try
            {
                Graphics.Blit(source, buffer); RenderTexture.active = buffer;
                copy = new Texture2D(source.width, source.height, TextureFormat.ARGB32, false);
                copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0); copy.Apply();
                return copy.EncodeToPNG();
            }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(buffer); if (copy != null) UnityEngine.Object.Destroy(copy); }
        }
        internal static void Stroke(CleanableObject target, float u, float v, bool colorPass)
        {
            var painter = MasterPainter.instance;
            if (painter == null) throw new InvalidOperationException("MasterPainter is not loaded");
            var canvas = ItemComponents.Read<RenderTexture>(painter, "canvasTexture");
            var brush = ItemComponents.Read<Material>(painter, "brushMaterial");
            var color = ItemComponents.Read<Material>(painter, "colorMaterial");
            if (canvas == null || brush == null || color == null) throw new InvalidOperationException("MasterPainter materials are not loaded");
            var previous = RenderTexture.active;
            RenderTexture stamp = null; Texture2D texture = null;
            try
            {
                Graphics.Blit(target.GetCurrentDirtTex(), canvas);
                stamp = new RenderTexture(1024, 1024, 24);
                Graphics.Blit(brush.mainTexture, stamp, new Vector2(25, 25), -new Vector2(u, v) * 25 + new Vector2(0.5f, 0.5f));
                Graphics.Blit(stamp, canvas, colorPass ? color : brush);
                RenderTexture.active = canvas;
                texture = new Texture2D(canvas.width, canvas.height, TextureFormat.ARGB32, false);
                texture.ReadPixels(new Rect(0, 0, canvas.width, canvas.height), 0, 0); texture.Apply();
                target.ApplyNewDirtTexture(texture); texture = null; // Target now owns the saved texture.
            }
            finally
            {
                RenderTexture.active = previous;
                if (stamp != null) { stamp.Release(); UnityEngine.Object.Destroy(stamp); }
                if (texture != null) UnityEngine.Object.Destroy(texture);
            }
        }
        internal static void Clean(CleanableObject target)
        {
            // CleanFully otherwise copies the shared painter's most recent canvas at end-of-frame.
            // A complete clean has an explicit target and cannot borrow another surface's canvas.
            var texture = new Texture2D(128, 128, TextureFormat.ARGB32, false);
            try { texture.SetPixels(new Color[128 * 128]); texture.Apply(); target.ApplyNewDirtTexture(texture); texture = null; }
            finally { if (texture != null) UnityEngine.Object.Destroy(texture); }
        }
        internal static Texture2D Decode(byte[] png)
        {
            var texture = new Texture2D(2, 2, TextureFormat.ARGB32, false);
            try { if (!texture.LoadImage(png)) throw new System.IO.InvalidDataException("invalid dirt PNG"); return texture; }
            catch { UnityEngine.Object.Destroy(texture); throw; }
        }
    }
}
