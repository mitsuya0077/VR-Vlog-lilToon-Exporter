using System;
using System.Collections.Generic;
using UniGLTF;
using UnityEngine;
using ExportColorSpace = UniGLTF.ColorSpace;

namespace VRVlog.LilToonExporter
{
    internal static class MobileTextureEncoder
    {
        // Called after UniVRM unpacked normal maps/combined PBR channels and
        // converted texture color space. Never reinterpret packed Unity normals.
        internal static byte[] EncodeConverted(Texture2D source, bool srgb, ICollection<string> warnings = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var size = TextureResizePolicy.Size(source.width, source.height);
            if (size.Item1 == source.width && size.Item2 == source.height) return RequirePng(ImageConversion.EncodeToPNG(source), source.name);
            var pixels = source.GetPixels32();
            var rgba = new byte[checked(pixels.Length * 4)];
            for (var i = 0; i < pixels.Length; i++)
            {
                rgba[i * 4] = pixels[i].r; rgba[i * 4 + 1] = pixels[i].g;
                rgba[i * 4 + 2] = pixels[i].b; rgba[i * 4 + 3] = pixels[i].a;
            }
            var resized = TextureResizePolicy.Resize(rgba, source.width, source.height, srgb);
            var destination = new Texture2D(size.Item1, size.Item2, TextureFormat.RGBA32, false, !srgb);
            try
            {
                var colors = new Color32[resized.Length / 4];
                for (var i = 0; i < colors.Length; i++) colors[i] = new Color32(resized[i * 4], resized[i * 4 + 1], resized[i * 4 + 2], resized[i * 4 + 3]);
                destination.SetPixels32(colors);
                destination.Apply(false, false);
                var png = RequirePng(ImageConversion.EncodeToPNG(destination), source.name);
                Warn(warnings, source.name, source.width, source.height, size.Item1, size.Item2);
                return png;
            }
            finally { UnityEngine.Object.DestroyImmediate(destination); }
        }

        internal static byte[] EncodeSource(Texture source, bool srgb, ICollection<string> warnings = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var previous = RenderTexture.active;
            var previousSrgb = GL.sRGBWrite;
            Texture2D converted = null;
            try
            {
                GL.sRGBWrite = srgb && QualitySettings.activeColorSpace == UnityEngine.ColorSpace.Linear;
                converted = TextureConverter.CopyTexture(source, srgb ? ExportColorSpace.sRGB : ExportColorSpace.Linear, true, null);
                return EncodeConverted(converted, srgb, warnings);
            }
            finally
            {
                RenderTexture.active = previous;
                GL.sRGBWrite = previousSrgb;
                if (converted != null) UnityEngine.Object.DestroyImmediate(converted);
            }
        }

        internal static byte[] ResizeEncoded(byte[] encoded, bool srgb)
        {
            var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false, !srgb);
            try
            {
                if (!ImageConversion.LoadImage(decoded, encoded, false)) throw new InvalidOperationException("VRM内の画像を読み込めませんでした。");
                return EncodeConverted(decoded, srgb);
            }
            finally { UnityEngine.Object.DestroyImmediate(decoded); }
        }

        internal static void Warn(ICollection<string> warnings, string name, int width, int height, int resizedWidth, int resizedHeight)
        {
            var message = name + ": 出力用画像を " + width + "×" + height + " から " + resizedWidth + "×" + resizedHeight + " へ縮小しました。元の画像は変更していません。";
            if (warnings != null && !warnings.Contains(message)) warnings.Add(message);
        }
        private static byte[] RequirePng(byte[] bytes, string name) => bytes != null && bytes.Length > 0 ? bytes : throw new InvalidOperationException("画像 '" + name + "' をPNGへ変換できませんでした。");
    }

    internal sealed class MobileTextureSerializer : ITextureSerializer
    {
        private readonly ICollection<string> warnings;
        internal MobileTextureSerializer(ICollection<string> warnings) { this.warnings = warnings; }
        public bool CanExportAsEditorAssetFile(Texture texture, ExportColorSpace exportColorSpace) => false;
        // Deliberately no AssetImporter writes or SaveAndReimport calls. UniVRM
        // creates disposable conversion textures before invoking the serializer.
        public void ModifyTextureAssetBeforeExporting(Texture texture) { }
        public (byte[] bytes, string mime) ExportBytesWithMime(Texture2D texture, ExportColorSpace exportColorSpace) =>
            (MobileTextureEncoder.EncodeConverted(texture, exportColorSpace == ExportColorSpace.sRGB, warnings), "image/png");
    }
}
