using System;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    internal static class LilToonFullTexture
    {
        internal static byte[] Read(Texture source, int mip, int face, bool normal, bool srgb, bool hdr, bool half=false)
            => ReadPixels(source, mip, Math.Max(1, source.width >> mip), Math.Max(1, source.height >> mip), face, normal, srgb, hdr, half, false);

        internal static int MipCount(int width, int height)
        {
            var count = 1;
            for (var size = Math.Max(width, height); size > 1; size >>= 1) count++;
            return count;
        }

        // Select an existing mip before filtering. Exact matches retain authored
        // mip pixels, while NPOT sizes and short chains use an area average.
        // Source dimensions, pixels, importer and sampler settings stay intact.
        internal static byte[] ReadResized(Texture source, int width, int height, int face, bool normal, bool srgb, bool hdr, bool half=false)
        {
            if (width <= 0 || height <= 0 || width > source.width || height > source.height)
                throw new ArgumentOutOfRangeException(nameof(width), "縮小画像の寸法が元画像の範囲外です。");
            var count = source is Texture2D two ? two.mipmapCount : source is Cubemap cube ? cube.mipmapCount :
                throw new NotSupportedException("動的テクスチャは保存できません: " + source.name);
            var mip = 0;
            while (mip + 1 < count && Math.Max(1, source.width >> (mip + 1)) >= width && Math.Max(1, source.height >> (mip + 1)) >= height) mip++;
            var exact = Math.Max(1, source.width >> mip) == width && Math.Max(1, source.height >> mip) == height;
            if (exact) return ReadPixels(source, mip, width, height, face, normal, srgb, hdr, half, false);
            var sourceWidth = Math.Max(1, source.width >> mip);
            var sourceHeight = Math.Max(1, source.height >> mip);
            var current = source;
            RenderTexture intermediate = null;
            var decodeNormal = normal;
            try
            {
                // A short source chain must not average millions of texels in
                // one final 1x1 fragment. Spread the work over <=1024px targets,
                // then reduce by at most four per axis. Intermediate surfaces
                // contain canonical normals, so only the first pass unpacks.
                while (sourceWidth > (long)width * 4 || sourceHeight > (long)height * 4)
                {
                    var nextWidth = Math.Max(width, Math.Min(LilToonMobileProfile.DefaultMaximumTextureSize, (sourceWidth + 3) / 4));
                    var nextHeight = Math.Max(height, Math.Min(LilToonMobileProfile.DefaultMaximumTextureSize, (sourceHeight + 3) / 4));
                    var next = Target(nextWidth, nextHeight, srgb, hdr, half);
                    try { Blit(current, mip, sourceWidth, sourceHeight, next, face, decodeNormal, srgb, true); }
                    catch { RenderTexture.ReleaseTemporary(next); throw; }
                    if (intermediate != null) RenderTexture.ReleaseTemporary(intermediate);
                    intermediate = next;
                    current = next;
                    mip = 0;
                    sourceWidth = nextWidth;
                    sourceHeight = nextHeight;
                    decodeNormal = false;
                }
                return ReadPixels(current, mip, width, height, face, decodeNormal, srgb, hdr, half, sourceWidth != width || sourceHeight != height);
            }
            finally { if (intermediate != null) RenderTexture.ReleaseTemporary(intermediate); }
        }

        static RenderTexture Target(int width, int height, bool srgb, bool hdr, bool half)
            => RenderTexture.GetTemporary(width, height, 0, half ? RenderTextureFormat.ARGBHalf : hdr ? RenderTextureFormat.ARGBFloat : RenderTextureFormat.ARGB32, srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);

        static byte[] ReadPixels(Texture source, int mip, int width, int height, int face, bool normal, bool srgb, bool hdr, bool half, bool resample)
        {
            var previous = RenderTexture.active;
            RenderTexture target=null;
            Texture2D readable=null;
            try
            {
                target = Target(width, height, srgb, hdr, half);
                readable = new Texture2D(width, height, half ? TextureFormat.RGBAHalf : hdr ? TextureFormat.RGBAFloat : TextureFormat.RGBA32, false, !srgb);
                Blit(source, mip, Math.Max(1, source.width >> mip), Math.Max(1, source.height >> mip), target, face, normal, srgb, resample);
                RenderTexture.active = target;
                readable.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                readable.Apply(false, false);
                var bytes = readable.GetRawTextureData();
                VRVlog.LilToon.LilToonFullContract.ValidatePixels(bytes, half ? "rgbaHalf" : hdr ? "rgbaFloat" : "rgba32");
                return bytes;
            }
            finally
            {
                RenderTexture.active = previous;
                if(target!=null)RenderTexture.ReleaseTemporary(target); UnityEngine.Object.DestroyImmediate(readable);
            }
        }

        static void Blit(Texture source, int mip, int sourceWidth, int sourceHeight, RenderTexture target, int face, bool normal, bool srgb, bool resample)
        {
            var shader = Shader.Find("Hidden/VRVlog/FullTextureCopy");
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("lilToon画像の保存シェーダーを利用できません。");
            var material = new Material(shader);
            var previous = RenderTexture.active; var previousWrite = GL.sRGBWrite;
            try
            {
                material.SetFloat("_Mip", mip); material.SetFloat("_Face", face); material.SetFloat("_Normal", normal ? 1 : 0);
                material.SetFloat("_Cube", source is Cubemap ? 1 : 0);
                material.SetFloat("_Resample", resample ? 1 : 0);
                material.SetFloat("_Color", srgb && !normal ? 1 : 0);
                // Gamma projects sample sRGB pixels without the hardware decode
                // used by Linear projects. Do it once around the area average.
                material.SetFloat("_ManualSrgb", srgb && QualitySettings.activeColorSpace == ColorSpace.Gamma ? 1 : 0);
                material.SetVector("_SourceSize", new Vector4(sourceWidth, sourceHeight, 0, 0));
                material.SetVector("_TargetSize", new Vector4(target.width, target.height, 0, 0));
                if (source is Cubemap) material.SetTexture("_CubeTex", source);
                GL.sRGBWrite = srgb && QualitySettings.activeColorSpace == ColorSpace.Linear;
                Graphics.Blit(source is Cubemap ? Texture2D.whiteTexture : source, target, material);
            }
            finally
            {
                RenderTexture.active = previous; GL.sRGBWrite = previousWrite;
                UnityEngine.Object.DestroyImmediate(material);
            }
        }
    }
}
