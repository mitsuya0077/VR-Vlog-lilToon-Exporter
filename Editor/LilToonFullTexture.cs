using System;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    internal static class LilToonFullTexture
    {
        internal static byte[] Read(Texture source, int mip, int face, bool normal, bool srgb, bool hdr, bool half=false)
        {
            var shader = Shader.Find("Hidden/VRVlog/FullTextureCopy");
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("原寸画像の保存シェーダーを利用できません。");
            var material = new Material(shader);
            var width = Math.Max(1, source.width >> mip); var height = Math.Max(1, source.height >> mip);
            var previous = RenderTexture.active; var previousWrite = GL.sRGBWrite;
            RenderTexture target=null;
            Texture2D readable=null;
            try
            {
                target = RenderTexture.GetTemporary(width, height, 0, half ? RenderTextureFormat.ARGBHalf : hdr ? RenderTextureFormat.ARGBFloat : RenderTextureFormat.ARGB32, srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
                readable = new Texture2D(width, height, half ? TextureFormat.RGBAHalf : hdr ? TextureFormat.RGBAFloat : TextureFormat.RGBA32, false, !srgb);
                material.SetFloat("_Mip", mip); material.SetFloat("_Face", face); material.SetFloat("_Normal", normal ? 1 : 0);
                material.SetFloat("_Cube", source is Cubemap ? 1 : 0);
                if (source is Cubemap) material.SetTexture("_CubeTex", source);
                GL.sRGBWrite = srgb && QualitySettings.activeColorSpace == ColorSpace.Linear;
                Graphics.Blit(source is Cubemap ? Texture2D.whiteTexture : source, target, material);
                RenderTexture.active = target;
                readable.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                readable.Apply(false, false);
                var bytes = readable.GetRawTextureData();
                VRVlog.LilToon.LilToonFullContract.ValidatePixels(bytes, half ? "rgbaHalf" : hdr ? "rgbaFloat" : "rgba32");
                return bytes;
            }
            finally
            {
                RenderTexture.active = previous; GL.sRGBWrite = previousWrite;
                if(target!=null)RenderTexture.ReleaseTemporary(target); UnityEngine.Object.DestroyImmediate(readable); UnityEngine.Object.DestroyImmediate(material);
            }
        }
    }
}
