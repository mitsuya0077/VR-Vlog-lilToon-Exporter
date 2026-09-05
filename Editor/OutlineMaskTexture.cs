using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    internal static class OutlineMaskTexture
    {
        internal static Texture2D Create(Texture source, ICollection<Texture2D> owned)
        {
            if (source == null || source == Texture2D.whiteTexture) return null;
            if (owned == null) throw new ArgumentNullException(nameof(owned));
            if (source.width <= 0 || source.height <= 0 ||
                source.width > LilToonMobileProfile.MaximumTextureSize || source.height > LilToonMobileProfile.MaximumTextureSize)
                throw new InvalidOperationException("輪郭線マスクがモバイルの画像サイズ上限を超えています。");
            var temporary = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            var previousSrgbWrite = GL.sRGBWrite;
            try
            {
                GL.sRGBWrite = false;
                Graphics.Blit(source, temporary);
                RenderTexture.active = temporary;
                var copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, true, true)
                {
                    name = source.name + "_VRVlogOutlineWidth",
                    filterMode = source.filterMode, wrapModeU = source.wrapModeU, wrapModeV = source.wrapModeV
                };
                owned.Add(copy);
                copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
                var pixels = copy.GetPixels();
                for (var i = 0; i < pixels.Length; i++) pixels[i] = MobileMaterialMath.OutlineMask(pixels[i]);
                copy.SetPixels(pixels);
                copy.Apply(true, false);
                return copy;
            }
            finally
            {
                RenderTexture.active = previous;
                GL.sRGBWrite = previousSrgbWrite;
                RenderTexture.ReleaseTemporary(temporary);
            }
        }
    }
}
