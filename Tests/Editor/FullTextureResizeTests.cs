using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using F = VRVlog.LilToon.LilToonFullContract;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class FullTextureResizeTests
    {
        [TestCase(false, 4096, 8, 1024, 2)]
        [TestCase(true, 4096, 8, 1024, 2)]
        [TestCase(true, 8, 4096, 2, 1024)]
        [TestCase(true, 1500, 7, 1024, 5)]
        public void CaptureCapsLargeImagesAndPreservesAspectColorAlphaAndSource(bool srgb, int width, int height, int outputWidth, int outputHeight)
        {
            var source = Image(width, height, !srgb, _ => new Color32(102, 51, 204, 128));
            using var fixture = new SurfaceFixture();
            try
            {
                source.name = "Full color 4K";
                source.filterMode = FilterMode.Point;
                source.wrapModeU = TextureWrapMode.Clamp;
                source.wrapModeV = TextureWrapMode.Mirror;
                source.anisoLevel = 3;
                source.mipMapBias = .25f;
                fixture.Material.SetTexture("_MainTex", source);
                var original = source.GetPixels32();
                var warnings = new List<string>();
                var snapshot = LilToonFullSnapshot.Capture(fixture.Avatar, warnings: warnings);
                var record = Texture(snapshot, source.name);
                Assert.That(F.Int(record, "width"), Is.EqualTo(outputWidth));
                Assert.That(F.Int(record, "height"), Is.EqualTo(outputHeight));
                Assert.That(F.Int(record, "mips"), Is.EqualTo(1));
                Assert.That(F.Bool(record, "srgb"), Is.EqualTo(srgb));
                Assert.That(F.Text(record, "format"), Is.EqualTo("rgba32"));
                Assert.That(F.Int(record, "filter"), Is.EqualTo((int)FilterMode.Point));
                Assert.That(F.Int(record, "wrapU"), Is.EqualTo((int)TextureWrapMode.Clamp));
                Assert.That(F.Int(record, "wrapV"), Is.EqualTo((int)TextureWrapMode.Mirror));
                Assert.That(F.Int(record, "aniso"), Is.EqualTo(3));
                Assert.That(F.Number(record, "mipBias"), Is.EqualTo(.25));
                var bytes = Pixels(snapshot, record, 0, 0);
                for (var pixel = 0; pixel < bytes.Length / 4; pixel++)
                {
                    Assert.That((int)bytes[pixel * 4], Is.EqualTo(102).Within(1));
                    Assert.That((int)bytes[pixel * 4 + 1], Is.EqualTo(51).Within(1));
                    Assert.That((int)bytes[pixel * 4 + 2], Is.EqualTo(204).Within(1));
                    Assert.That((int)bytes[pixel * 4 + 3], Is.EqualTo(128).Within(1));
                }
                Assert.That(source.width, Is.EqualTo(width));
                Assert.That(source.height, Is.EqualTo(height));
                Assert.That(source.GetPixels32(), Is.EqualTo(original));
                Assert.That(LilToonFullTexture.Read(source, 0, 0, false, srgb, false).Length, Is.EqualTo(width * height * 4), "Low-level original-size reads remain available.");
                Assert.That(source.filterMode, Is.EqualTo(FilterMode.Point));
                Assert.That(source.wrapModeU, Is.EqualTo(TextureWrapMode.Clamp));
                Assert.That(fixture.Material.GetTexture("_MainTex"), Is.SameAs(source));
                Assert.That(warnings.Any(w => w.Contains(source.name) && w.Contains("1024")), Is.True);
            }
            finally { Object.DestroyImmediate(source); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CaptureLeavesSmallImagesAndAuthoredMipPixelsUnchanged(bool srgb)
        {
            var source = new Texture2D(8, 8, TextureFormat.RGBA32, 4, !srgb) { name = "Small authored mips", filterMode = FilterMode.Point };
            using var fixture = new SurfaceFixture();
            try
            {
                var expected = new List<byte[]>();
                for (var mip = 0; mip < 4; mip++)
                {
                    var size = Math.Max(1, 8 >> mip);
                    var color = new Color32((byte)(31 + mip * 13), 97, 181, 203);
                    source.SetPixels32(Enumerable.Repeat(color, size * size).ToArray(), mip);
                    expected.Add(Rgba(size, size, color));
                }
                source.Apply(false, false);
                fixture.Material.SetTexture("_MainTex", source);
                var warnings = new List<string>();
                var snapshot = LilToonFullSnapshot.Capture(fixture.Avatar, warnings: warnings);
                var record = Texture(snapshot, source.name);
                Assert.That(F.Int(record, "width"), Is.EqualTo(8));
                Assert.That(F.Int(record, "height"), Is.EqualTo(8));
                Assert.That(F.Int(record, "mips"), Is.EqualTo(4));
                for (var mip = 0; mip < 4; mip++)
                {
                    var actual = Pixels(snapshot, record, 0, mip);
                    Assert.That(actual.Length, Is.EqualTo(expected[mip].Length));
                    for (var i = 0; i < actual.Length; i++)
                        Assert.That((int)actual[i], Is.EqualTo((int)expected[mip][i]).Within(1), "mip " + mip + " component " + i);
                    Assert.That(source.GetPixels32(mip)[0].r, Is.EqualTo(expected[mip][0]));
                }
                Assert.That(warnings, Is.Empty);
            }
            finally { Object.DestroyImmediate(source); }
        }

        [Test]
        public void FourKSquareCaptureReducesDecodedUploadBytesBySixteen()
        {
            // One real 4K square proves the memory reduction without creating a
            // collection of 64 MiB fixtures. No CPU-readable source is required.
            var source = new Texture2D(4096, 4096, TextureFormat.RGBA32, false, true) { name = "Single 4K memory fixture" };
            using var fixture = new SurfaceFixture();
            try
            {
                source.Apply(false, true);
                fixture.Material.SetTexture("_MainTex", source);
                var snapshot = LilToonFullSnapshot.Capture(fixture.Avatar);
                var record = Texture(snapshot, source.name);
                var payload = Pixels(snapshot, record, 0, 0);
                Assert.That(F.Int(record, "width"), Is.EqualTo(1024));
                Assert.That(F.Int(record, "height"), Is.EqualTo(1024));
                Assert.That(payload.LongLength, Is.EqualTo(4L * 1024 * 1024));
                Assert.That(4096L * 4096 * 4 / payload.LongLength, Is.EqualTo(16));
                Assert.That(source.width, Is.EqualTo(4096));
                Assert.That(source.isReadable, Is.False);
            }
            finally { Object.DestroyImmediate(source); }
        }

        [Test]
        public void CaptureCompletesTheResizedMipTailFromAnExplicitShortSourceChain()
        {
            var source = new Texture2D(2048, 2048, TextureFormat.RGBA32, 2, false)
                { name = "Read-only short authored mip chain", filterMode = FilterMode.Point };
            using var fixture = new SurfaceFixture();
            try
            {
                var expected = new Color32(57, 113, 181, 203);
                source.SetPixelData(Rgba(1024, 1024, expected), 1);
                source.Apply(false, true);
                fixture.Material.SetTexture("_MainTex", source);
                var snapshot = LilToonFullSnapshot.Capture(fixture.Avatar);
                var record = Texture(snapshot, source.name);
                Assert.That(F.Int(record, "width"), Is.EqualTo(1024));
                Assert.That(F.Int(record, "height"), Is.EqualTo(1024));
                Assert.That(F.Int(record, "mips"), Is.EqualTo(11), "A short source chain still produces a complete resized sampling chain.");
                for (var mip = 0; mip < 11; mip++)
                {
                    var size = Math.Max(1, 1024 >> mip);
                    var bytes = Pixels(snapshot, record, 0, mip);
                    Assert.That(bytes.Length, Is.EqualTo(size * size * 4));
                    foreach (var offset in new[] { 0, ((bytes.Length / 8) * 4), bytes.Length - 4 }.Distinct())
                    {
                        Assert.That((int)bytes[offset], Is.EqualTo((int)expected.r).Within(2), "mip " + mip);
                        Assert.That((int)bytes[offset + 1], Is.EqualTo((int)expected.g).Within(2));
                        Assert.That((int)bytes[offset + 2], Is.EqualTo((int)expected.b).Within(2));
                        Assert.That((int)bytes[offset + 3], Is.EqualTo((int)expected.a).Within(1));
                    }
                }
                Assert.That(source.width, Is.EqualTo(2048));
                Assert.That(source.mipmapCount, Is.EqualTo(2));
                Assert.That(source.isReadable, Is.False);
                Assert.That(source.filterMode, Is.EqualTo(FilterMode.Point));
            }
            finally { Object.DestroyImmediate(source); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ResizedColorUsesLinearLightAndTransparentEdgesKeepTheirColor(bool srgb)
        {
            var source = Image(4096, 4, !srgb, i => (i & 1) == 0 ? new Color32(0, 0, 0, 255) : new Color32(255, 255, 255, 255));
            using var fixture = new SurfaceFixture();
            try
            {
                source.name = "Alternating color samples";
                source.filterMode = FilterMode.Point;
                fixture.Material.SetTexture("_MainTex", source);
                var snapshot = LilToonFullSnapshot.Capture(fixture.Avatar);
                var bytes = Pixels(snapshot, Texture(snapshot, source.name), 0, 0);
                for (var pixel = 0; pixel < bytes.Length / 4; pixel++)
                    Assert.That((int)bytes[pixel * 4], Is.EqualTo(srgb ? 188 : 128).Within(2), "Color filtering follows the image encoding.");

                source.SetPixels32(Enumerable.Range(0, source.width * source.height)
                    .Select(i => (i & 1) == 0 ? new Color32(255, 0, 0, 255) : new Color32(0, 0, 255, 0)).ToArray());
                source.Apply(false, false);
                snapshot = LilToonFullSnapshot.Capture(fixture.Avatar);
                bytes = Pixels(snapshot, Texture(snapshot, source.name), 0, 0);
                Assert.That((int)bytes[0], Is.EqualTo(srgb ? 255 : 128).Within(2));
                Assert.That((int)bytes[2], Is.EqualTo(srgb ? 0 : 128).Within(2));
                Assert.That((int)bytes[3], Is.EqualTo(128).Within(1));
            }
            finally { Object.DestroyImmediate(source); }
        }

        [TestCase(TextureFormat.RGBAHalf)]
        [TestCase(TextureFormat.RGBAFloat)]
        public void ResizedHdrKeepsSignedRangePrecisionAndAlpha(TextureFormat format)
        {
            var source = new Texture2D(4096, 8, format, false, true) { name = "Resized HDR " + format, filterMode = FilterMode.Point };
            using var fixture = new SurfaceFixture();
            try
            {
                var color = new Color(3.5f, -.75f, 12.25f, .375f);
                source.SetPixels(Enumerable.Repeat(color, source.width * source.height).ToArray());
                source.Apply(false, false);
                fixture.Material.SetTexture("_EmissionMap", source);
                var snapshot = LilToonFullSnapshot.Capture(fixture.Avatar);
                var record = Texture(snapshot, source.name);
                Assert.That(F.Int(record, "width"), Is.EqualTo(1024));
                Assert.That(F.Int(record, "height"), Is.EqualTo(2));
                Assert.That(F.Text(record, "format"), Is.EqualTo(format == TextureFormat.RGBAHalf ? "rgbaHalf" : "rgbaFloat"));
                Assert.That(F.Bool(record, "srgb"), Is.False);
                var copy = new Texture2D(1024, 2, format, false, true);
                try
                {
                    copy.LoadRawTextureData(Pixels(snapshot, record, 0, 0));
                    copy.Apply(false, false);
                    foreach (var pixel in copy.GetPixels())
                        Assert.That(((Vector4)pixel - (Vector4)color).sqrMagnitude, Is.LessThan(1e-7f));
                }
                finally { Object.DestroyImmediate(copy); }
                Assert.That(source.GetPixel(0, 0), Is.EqualTo(color));
                Assert.That(fixture.Material.GetTexture("_EmissionMap"), Is.SameAs(source));
            }
            finally { Object.DestroyImmediate(source); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ResizedFiniteFloatExtremesDoNotOverflowTheAreaAverage(bool maximum)
        {
            var width = maximum ? 1500 : 2048;
            var height = maximum ? 7 : 4;
            var source = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true)
                { name = "Finite HDR extrema", filterMode = FilterMode.Point };
            using var fixture = new SurfaceFixture();
            try
            {
                var magnitude = maximum ? float.MaxValue : 1e38f;
                var expected = new[] { magnitude, -magnitude, magnitude, .375f };
                source.SetPixels(Enumerable.Repeat(new Color(expected[0], expected[1], expected[2], expected[3]), source.width * source.height).ToArray());
                source.Apply(false, false);
                var original = source.GetRawTextureData();
                fixture.Material.SetTexture("_EmissionMap", source);
                var snapshot = LilToonFullSnapshot.Capture(fixture.Avatar);
                var record = Texture(snapshot, source.name);
                Assert.That(F.Int(record, "width"), Is.EqualTo(1024));
                Assert.That(F.Int(record, "height"), Is.EqualTo(maximum ? 5 : 2));
                Assert.That(F.Text(record, "format"), Is.EqualTo("rgbaFloat"));
                var bytes = Pixels(snapshot, record, 0, 0);
                for (var pixel = 0; pixel < bytes.Length / 16; pixel++)
                for (var component = 0; component < 4; component++)
                {
                    var actual = BitConverter.ToSingle(bytes, pixel * 16 + component * 4);
                    Assert.That(float.IsNaN(actual) || float.IsInfinity(actual), Is.False);
                    if (component < 3)
                        Assert.That(Math.Abs(((double)actual - expected[component]) / expected[component]), Is.LessThanOrEqualTo(2e-6));
                    else Assert.That(actual, Is.EqualTo(expected[component]).Within(1e-6f));
                }
                Assert.That(source.GetRawTextureData(), Is.EqualTo(original));
                Assert.That(source.width, Is.EqualTo(width));
                Assert.That(source.mipmapCount, Is.EqualTo(1));
                Assert.That(fixture.Material.GetTexture("_EmissionMap"), Is.SameAs(source));
            }
            finally { Object.DestroyImmediate(source); }
        }

        [Test]
        public void CubeCaptureCapsEveryFaceAndRetainsTheAuthoredMipTail()
        {
            var source = new Cubemap(2048, TextureFormat.RGBA32, true) { name = "Oversized authored cubemap", filterMode = FilterMode.Point };
            using var fixture = new SurfaceFixture();
            try
            {
                // Capture chooses mip 1 for the 1024 base. Leave the unneeded
                // 2048 level alone and release its CPU copy before capture.
                for (var face = 0; face < 6; face++)
                for (var mip = 1; mip < source.mipmapCount; mip++)
                {
                    var size = Math.Max(1, 2048 >> mip);
                    source.SetPixelData(Rgba(size, size, CubeColor(face, mip)), mip, (CubemapFace)face);
                }
                source.Apply(false, true);
                fixture.Material.SetTexture("_ReflectionCubeTex", source);
                var snapshot = LilToonFullSnapshot.Capture(fixture.Avatar);
                var record = Texture(snapshot, source.name);
                Assert.That(F.Int(record, "faces"), Is.EqualTo(6));
                Assert.That(F.Int(record, "width"), Is.EqualTo(1024));
                Assert.That(F.Int(record, "height"), Is.EqualTo(1024));
                Assert.That(F.Int(record, "mips"), Is.EqualTo(11));
                Assert.That(F.List(record, "chunks").Count, Is.EqualTo(66));
                long decodedBytes = 0;
                for (var face = 0; face < 6; face++)
                for (var mip = 0; mip < 11; mip++)
                {
                    var size = Math.Max(1, 1024 >> mip);
                    var bytes = Pixels(snapshot, record, face, mip);
                    decodedBytes += bytes.LongLength;
                    Assert.That(bytes.Length, Is.EqualTo(size * size * 4));
                    var expected = CubeColor(face, mip + 1);
                    foreach (var offset in new[] { 0, bytes.Length - 4 })
                    {
                        Assert.That((int)bytes[offset], Is.EqualTo((int)expected.r).Within(1), "face " + face + " mip " + mip);
                        Assert.That((int)bytes[offset + 1], Is.EqualTo((int)expected.g).Within(1));
                        Assert.That((int)bytes[offset + 2], Is.EqualTo((int)expected.b).Within(1));
                        Assert.That((int)bytes[offset + 3], Is.EqualTo((int)expected.a).Within(1));
                    }
                }
                Assert.That(decodedBytes, Is.EqualTo(33554424L), "Six full 1024 mip chains occupy just under 32 MiB.");
                Assert.That(source.width, Is.EqualTo(2048));
                Assert.That(source.mipmapCount, Is.EqualTo(12));
                Assert.That(source.isReadable, Is.False);
            }
            finally { Object.DestroyImmediate(source); }
        }

        [Test]
        public void ImportedNormalCaptureIsCanonicalAndDoesNotReimportOrEditTheSource()
        {
            var path = "Assets/__VRVlogFullResizeNormal_" + Guid.NewGuid().ToString("N") + ".png";
            var original = Image(4096, 8, true, _ => new Color32(128, 128, 255, 255));
            using var fixture = new SurfaceFixture();
            try
            {
                File.WriteAllBytes(path, ImageConversion.EncodeToPNG(original));
                AssetDatabase.ImportAsset(path);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.textureType = TextureImporterType.NormalMap;
                importer.textureCompression = TextureImporterCompression.Compressed;
                importer.maxTextureSize = 4096;
                importer.isReadable = false;
                importer.mipmapEnabled = true;
                importer.SaveAndReimport();
                var source = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                var settingsBefore = EditorJsonUtility.ToJson(importer);
                var fileBefore = File.ReadAllBytes(path);
                var metaBefore = File.ReadAllBytes(path + ".meta");
                fixture.Material.SetTexture("_BumpMap", source);
                var snapshot = LilToonFullSnapshot.Capture(fixture.Avatar);
                var record = Texture(snapshot, source.name);
                Assert.That(F.Int(record, "width"), Is.EqualTo(1024));
                Assert.That(F.Int(record, "height"), Is.EqualTo(2));
                Assert.That(F.Bool(record, "normal"), Is.True);
                Assert.That(F.Bool(record, "srgb"), Is.False);
                Assert.That(F.Text(record, "format"), Is.EqualTo("rgbaHalf"));
                for (var mip = 0; mip < F.Int(record, "mips"); mip++)
                {
                    var bytes = Pixels(snapshot, record, 0, mip);
                    for (var pixel = 0; pixel < bytes.Length / 8; pixel++)
                    {
                        Assert.That(Mathf.HalfToFloat(BitConverter.ToUInt16(bytes, pixel * 8)), Is.EqualTo(.5f).Within(.02f));
                        Assert.That(Mathf.HalfToFloat(BitConverter.ToUInt16(bytes, pixel * 8 + 2)), Is.EqualTo(.5f).Within(.02f));
                        Assert.That(Mathf.HalfToFloat(BitConverter.ToUInt16(bytes, pixel * 8 + 4)), Is.GreaterThan(.99f));
                        Assert.That(Mathf.HalfToFloat(BitConverter.ToUInt16(bytes, pixel * 8 + 6)), Is.EqualTo(1));
                    }
                }
                Assert.That(source.width, Is.EqualTo(4096));
                Assert.That(source.isReadable, Is.False);
                Assert.That(EditorJsonUtility.ToJson(importer), Is.EqualTo(settingsBefore));
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(fileBefore));
                Assert.That(File.ReadAllBytes(path + ".meta"), Is.EqualTo(metaBefore));
                Assert.That(fixture.Material.GetTexture("_BumpMap"), Is.SameAs(source));
            }
            finally { AssetDatabase.DeleteAsset(path); Object.DestroyImmediate(original); }
        }

        [TestCase(TextureImporterCompression.Uncompressed, FilterMode.Bilinear)]
        [TestCase(TextureImporterCompression.Compressed, FilterMode.Trilinear)]
        public void ImportedLdrNormalPreservesAllMipsAndSamplerWithinHalfPrecision(
            TextureImporterCompression compression, FilterMode filter)
        {
            var path = "Assets/__VRVlogFilterableNormal_" + Guid.NewGuid().ToString("N") + ".png";
            var original = Image(16, 16, true, index => new Color32(
                (byte)(48 + index % 16 * 11), (byte)(48 + index / 16 * 11), 255, 255));
            using var fixture = new SurfaceFixture();
            try
            {
                File.WriteAllBytes(path, ImageConversion.EncodeToPNG(original));
                AssetDatabase.ImportAsset(path);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.textureType = TextureImporterType.NormalMap;
                importer.textureCompression = compression;
                importer.isReadable = false;
                importer.mipmapEnabled = true;
                importer.filterMode = filter;
                importer.wrapModeU = TextureWrapMode.Clamp;
                importer.wrapModeV = TextureWrapMode.Mirror;
                importer.anisoLevel = 2;
                importer.mipMapBias = .125f;
                importer.SaveAndReimport();
                var source = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                var settingsBefore = EditorJsonUtility.ToJson(importer);
                var fileBefore = File.ReadAllBytes(path);
                var metaBefore = File.ReadAllBytes(path + ".meta");
                fixture.Material.SetFloat("_UseBumpMap", 1);
                fixture.Material.SetTexture("_BumpMap", source);
                var snapshot = LilToonFullSnapshot.Capture(fixture.Avatar);
                var record = Texture(snapshot, source.name);
                Assert.That(F.Text(record, "format"), Is.EqualTo("rgbaHalf"));
                Assert.That(F.Bool(record, "normal"), Is.True);
                Assert.That(F.Bool(record, "srgb"), Is.False);
                Assert.That(F.Int(record, "mips"), Is.EqualTo(source.mipmapCount));
                Assert.That(F.Int(record, "filter"), Is.EqualTo((int)filter));
                Assert.That(F.Int(record, "wrapU"), Is.EqualTo((int)TextureWrapMode.Clamp));
                Assert.That(F.Int(record, "wrapV"), Is.EqualTo((int)TextureWrapMode.Mirror));
                Assert.That(F.Int(record, "aniso"), Is.EqualTo(2));
                Assert.That(F.Number(record, "mipBias"), Is.EqualTo(.125));
                for (var mip = 0; mip < source.mipmapCount; mip++)
                {
                    var expected = LilToonFullTexture.Read(source, mip, 0, true, false, true);
                    var actual = Pixels(snapshot, record, 0, mip);
                    Assert.That(actual.Length * 2, Is.EqualTo(expected.Length));
                    for (var component = 0; component < actual.Length / 2; component++)
                        Assert.That(Mathf.HalfToFloat(BitConverter.ToUInt16(actual, component * 2)),
                            Is.EqualTo(BitConverter.ToSingle(expected, component * 4)).Within(1f / 2048f),
                            "mip " + mip + " component " + component);
                }
                Assert.That(source.isReadable, Is.False);
                Assert.That(EditorJsonUtility.ToJson(importer), Is.EqualTo(settingsBefore));
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(fileBefore));
                Assert.That(File.ReadAllBytes(path + ".meta"), Is.EqualTo(metaBefore));
                Assert.That(fixture.Material.GetTexture("_BumpMap"), Is.SameAs(source));
            }
            finally { AssetDatabase.DeleteAsset(path); Object.DestroyImmediate(original); }
        }

        [Test]
        public void CompleteExportCapsAllFallbackAndFullImagesIncludingExtensionOnlySlots()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var material = new Material(Shader.Find("lilToon")) { name = "Resize integration material" };
            var main = Image(4096, 8, false, _ => new Color32(102, 51, 204, 255));
            var backlight = Image(4096, 16, false, _ => new Color32(27, 83, 119, 255));
            var mask = Image(4096, 8, true, _ => new Color32(97, 33, 181, 211));
            var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                main.name = "Integration main 4K";
                backlight.name = "Integration extension-only backlight 4K";
                mask.name = "Integration numeric mask 4K";
                material.SetTexture("_MainTex", main);
                material.SetFloat("_UseBacklight", 1);
                material.SetTexture("_BacklightColorTex", backlight);
                material.SetFloat("_UseEmission", 1);
                material.SetTexture("_EmissionBlendMask", mask);
                foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial = material;
                var sourcePixels = new[] { main, backlight, mask }.ToDictionary(t => t, t => t.GetPixels32());
                var warnings = new List<string>();
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Texture memory fixture", "Tests", warnings,
                    exporterVersion: "0.10.0-preview.1", lilToonVersion: "2.3.4", blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                var glb = GlbDocument.Read(bytes);
                Assert.DoesNotThrow(() => F.Validate(glb.Json, bytes.LongLength, glb.Binary.Length));
                var root = F.Root(glb.Json);
                foreach (var record in F.List(root, "textures").Select(F.Object))
                {
                    Assert.That(F.Int(record, "width"), Is.LessThanOrEqualTo(1024), F.Text(record, "name"));
                    Assert.That(F.Int(record, "height"), Is.LessThanOrEqualTo(1024), F.Text(record, "name"));
                    var bpp = F.Text(record, "format") == "rgba32" ? 4 : F.Text(record, "format") == "rgbaHalf" ? 8 : 16;
                    for (var face = 0; face < F.Int(record, "faces"); face++)
                    for (var mip = 0; mip < F.Int(record, "mips"); mip++)
                    {
                        var chunk = F.Object(F.At(F.List(root, "chunks"), F.Integer(F.List(record, "chunks")[face * F.Int(record, "mips") + mip])));
                        Assert.That(F.Int(chunk, "decodedBytes"), Is.EqualTo(Math.Max(1, F.Int(record, "width") >> mip) * Math.Max(1, F.Int(record, "height") >> mip) * bpp));
                    }
                }
                foreach (var image in F.List(glb.Json, "images").Select(F.Object))
                {
                    var view = F.Object(F.At(F.List(glb.Json, "bufferViews"), F.Int(image, "bufferView")));
                    var offset = view.ContainsKey("byteOffset") ? F.Int(view, "byteOffset") : 0;
                    var imageBytes = new byte[F.Int(view, "byteLength")];
                    Buffer.BlockCopy(glb.Binary, offset, imageBytes, 0, imageBytes.Length);
                    Assert.That(ImageConversion.LoadImage(decoded, imageBytes, false), Is.True);
                    Assert.That(decoded.width, Is.LessThanOrEqualTo(1024));
                    Assert.That(decoded.height, Is.LessThanOrEqualTo(1024));
                }
                Assert.That(F.List(glb.Json, "images").Count, Is.GreaterThan(0), "The portable fallback is exercised too.");
                foreach (var source in new[] { main, backlight, mask })
                {
                    var record = F.List(root, "textures").Select(F.Object).Single(t => F.Text(t, "name") == source.name);
                    var chunkBytes = F.List(record, "chunks").Sum(id => (long)F.Int(F.Object(F.At(F.List(root, "chunks"), F.Integer(id))), "decodedBytes"));
                    Assert.That(chunkBytes * 16, Is.EqualTo((long)source.width * source.height * 4), source.name + " decoded data is reduced 16-fold.");
                    Assert.That(source.width, Is.EqualTo(4096));
                    Assert.That(source.GetPixels32(), Is.EqualTo(sourcePixels[source]), source.name + " original pixels remain intact.");
                }
                Assert.That(material.GetTexture("_MainTex"), Is.SameAs(main));
                Assert.That(material.GetTexture("_BacklightColorTex"), Is.SameAs(backlight));
                Assert.That(material.GetTexture("_EmissionBlendMask"), Is.SameAs(mask));
            }
            finally
            {
                Object.DestroyImmediate(decoded);
                Object.DestroyImmediate(main);
                Object.DestroyImmediate(backlight);
                Object.DestroyImmediate(mask);
                Object.DestroyImmediate(material);
            }
        }

        static Color32 CubeColor(int face, int sourceMip) => new Color32((byte)(31 + face * 27), (byte)(17 + sourceMip * 13), 181, 203);

        static Texture2D Image(int width, int height, bool linear, Func<int, Color32> color)
        {
            var image = new Texture2D(width, height, TextureFormat.RGBA32, false, linear);
            image.SetPixels32(Enumerable.Range(0, width * height).Select(color).ToArray());
            image.Apply(false, false);
            return image;
        }

        static byte[] Rgba(int width, int height, Color32 color)
        {
            var bytes = new byte[checked(width * height * 4)];
            for (var i = 0; i < bytes.Length; i += 4)
            {
                bytes[i] = color.r;
                bytes[i + 1] = color.g;
                bytes[i + 2] = color.b;
                bytes[i + 3] = color.a;
            }
            return bytes;
        }

        static Dictionary<string, object> Texture(LilToonFullSnapshot snapshot, string name)
        {
            var textures = (List<object>)typeof(LilToonFullSnapshot).GetField("textures", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(snapshot);
            return textures.Select(F.Object).Single(t => F.Text(t, "name") == name);
        }

        static byte[] Pixels(LilToonFullSnapshot snapshot, Dictionary<string, object> texture, int face, int mip)
        {
            var payloads = (List<DeflatePayload>)typeof(LilToonFullSnapshot).GetField("payloads", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(snapshot);
            var payload = payloads[F.Integer(F.List(texture, "chunks")[face * F.Int(texture, "mips") + mip])];
            using var encoded = new MemoryStream(payload.Encoded, false);
            using var decoder = new DeflateStream(encoded, CompressionMode.Decompress);
            using var decoded = new MemoryStream();
            decoder.CopyTo(decoded);
            Assert.That(decoded.Length, Is.EqualTo(payload.DecodedBytes));
            return decoded.ToArray();
        }

        sealed class SurfaceFixture : IDisposable
        {
            internal readonly GameObject Avatar = new GameObject("Full texture resize surface");
            internal readonly Material Material;
            readonly Mesh mesh;

            internal SurfaceFixture()
            {
                var shader = Shader.Find("lilToon");
                Assert.That(shader, Is.Not.Null);
                Material = new Material(shader);
                mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
                Avatar.AddComponent<MeshFilter>().sharedMesh = mesh;
                Avatar.AddComponent<MeshRenderer>().sharedMaterial = Material;
            }

            public void Dispose()
            {
                Object.DestroyImmediate(Avatar);
                Object.DestroyImmediate(Material);
                Object.DestroyImmediate(mesh);
            }
        }
    }
}
