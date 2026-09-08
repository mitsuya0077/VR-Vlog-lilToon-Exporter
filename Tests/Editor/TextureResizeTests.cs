using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UniGLTF;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public class TextureResizeTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void ConvertedFourKTexturePreservesColorAlphaAndSource(bool srgb)
        {
            var source = Image(4096, 8, !srgb, _ => new Color32(102, 51, 204, 128));
            var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false, !srgb);
            try
            {
                var original = source.GetPixels32();
                var warnings = new List<string>();
                var bytes = new MobileTextureSerializer(warnings).ExportBytesWithMime(source, srgb ? UniGLTF.ColorSpace.sRGB : UniGLTF.ColorSpace.Linear);
                Assert.That(bytes.mime, Is.EqualTo("image/png"));
                Assert.That(ImageConversion.LoadImage(decoded, bytes.bytes, false), Is.True);
                Assert.That(decoded.width, Is.EqualTo(1024));
                Assert.That(decoded.height, Is.EqualTo(2));
                Assert.That(decoded.GetPixels32().All(p => p.r == 102 && p.g == 51 && p.b == 204 && p.a == 128), Is.True);
                Assert.That(source.width, Is.EqualTo(4096));
                Assert.That(source.GetPixels32(), Is.EqualTo(original));
                Assert.That(warnings.Count, Is.EqualTo(1));
            }
            finally { Object.DestroyImmediate(source); Object.DestroyImmediate(decoded); }
        }

        [Test]
        public void SharedEncodedColorAndDataImagesResizeWithDifferentCorrectFiltering()
        {
            var source = Image(4096, 4, false, i => i % 2 == 0 ? new Color32(0, 0, 0, 255) : new Color32(255, 255, 255, 255));
            var colorImage = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            var dataImage = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                var png = ImageConversion.EncodeToPNG(source);
                var root = GlbDocument.Read(MaterialBindingFixture.Build("Body"));
                root.Json["buffers"] = new List<object> { Obj("byteLength", 0L) };
                var offset = root.AppendBinary(png);
                root.Json["bufferViews"] = new List<object> { Obj("buffer", 0L, "byteOffset", (long)offset, "byteLength", (long)png.Length) };
                root.Json["images"] = new List<object> { Obj("name", "Shared image", "mimeType", "image/png", "bufferView", 0L) };
                root.Json["textures"] = new List<object> { Obj("source", 0L) };
                var material = (Dictionary<string, object>)((List<object>)root.Json["materials"])[0];
                material["pbrMetallicRoughness"] = Obj("baseColorTexture", Obj("index", 0L));
                material["normalTexture"] = Obj("index", 0L);
                var original = root.Write();
                var before = (byte[])original.Clone();
                var resized = GlbTextureDownsizer.Resize(GlbDocument.Read(original));
                Assert.That(ImageConversion.LoadImage(colorImage, ImageBytes(resized, 0), false), Is.True);
                Assert.That(ImageConversion.LoadImage(dataImage, ImageBytes(resized, 1), false), Is.True);
                Assert.That(colorImage.width, Is.EqualTo(1024));
                Assert.That(colorImage.height, Is.EqualTo(1));
                Assert.That(dataImage.width, Is.EqualTo(1024));
                Assert.That(colorImage.GetPixels32().All(p => Math.Abs(p.r - 188) <= 1 && p.a == 255), Is.True);
                Assert.That(dataImage.GetPixels32().All(p => Math.Abs(p.r - 128) <= 1 && p.a == 255), Is.True);
                var outputMaterial = (Dictionary<string, object>)((List<object>)resized.Json["materials"])[0];
                Assert.That(Convert.ToInt32(((Dictionary<string,object>)outputMaterial["normalTexture"])["index"]), Is.EqualTo(1));
                Assert.That(original, Is.EqualTo(before));
                Assert.That(source.width, Is.EqualTo(4096));
            }
            finally { Object.DestroyImmediate(source); Object.DestroyImmediate(colorImage); Object.DestroyImmediate(dataImage); }
        }

        [Test]
        public void FourKNormalMapIsConvertedBeforeResizeWithoutReimportingSource()
        {
            var path = "Assets/__VRVlogResizeNormal_" + Guid.NewGuid().ToString("N") + ".png";
            var originalImage = Image(4096, 8, true, _ => new Color32(128, 128, 255, 255));
            var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                File.WriteAllBytes(path, ImageConversion.EncodeToPNG(originalImage));
                AssetDatabase.ImportAsset(path);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.textureType = TextureImporterType.NormalMap;
                importer.textureCompression = TextureImporterCompression.Compressed;
                importer.maxTextureSize = 4096;
                importer.isReadable = false;
                importer.SaveAndReimport();
                var source = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                var settingsBefore = EditorJsonUtility.ToJson(importer);
                var fileBefore = File.ReadAllBytes(path);
                var serializer = new MobileTextureSerializer(null);
                using (var exporter = new TextureExporter(serializer))
                {
                    exporter.RegisterExportingAsNormal(source);
                    var converted = exporter.Export().Single();
                    Assert.That(converted.Item2, Is.EqualTo(UniGLTF.ColorSpace.Linear));
                    var bytes = serializer.ExportBytesWithMime(converted.Item1, converted.Item2);
                    Assert.That(ImageConversion.LoadImage(decoded, bytes.bytes, false), Is.True);
                }
                Assert.That(decoded.width, Is.EqualTo(1024));
                Assert.That(decoded.height, Is.EqualTo(2));
                var pixel = decoded.GetPixels32()[0];
                Assert.That((int)pixel.r, Is.EqualTo(128).Within(4));
                Assert.That((int)pixel.g, Is.EqualTo(128).Within(4));
                Assert.That((int)pixel.b, Is.GreaterThanOrEqualTo(251));
                Assert.That(source.width, Is.EqualTo(4096));
                Assert.That(EditorJsonUtility.ToJson(importer), Is.EqualTo(settingsBefore));
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(fileBefore));
            }
            finally
            {
                AssetDatabase.DeleteAsset(path);
                Object.DestroyImmediate(originalImage);
                Object.DestroyImmediate(decoded);
            }
        }

        [Test]
        public void ExtensionOnlyFourKBacklightTextureIsResizedDuringInjection()
        {
            var shader = Shader.Find("lilToon");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader) { name = "Body" };
            var source = Image(4096, 8, false, _ => new Color32(102, 51, 204, 128));
            var avatar = new GameObject("Avatar");
            var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            try
            {
                source.name = "Backlight 4K";
                material.SetTexture("_MainTex", null);
                material.SetFloat("_UseBacklight", 1);
                material.SetTexture("_BacklightColorTex", source);
                avatar.AddComponent<MeshRenderer>().sharedMaterial = material;
                var fallback = GlbDocument.Read(MaterialBindingFixture.Build("Body"));
                fallback.Json["buffers"] = new List<object> { Obj("byteLength", 0L) };
                var warnings = new List<string>();
                var output = LilToonGlbExtension.Inject(fallback.Write(), avatar, "0.7.4", "2.3.4", warnings);
                var glb = GlbDocument.Read(output);
                Assert.That(ImageConversion.LoadImage(decoded, ImageBytes(glb, 0), false), Is.True);
                Assert.That(decoded.width, Is.EqualTo(1024));
                Assert.That(decoded.height, Is.EqualTo(2));
                var pixel = decoded.GetPixels32()[0];
                Assert.That((int)pixel.r, Is.EqualTo(102).Within(2));
                Assert.That((int)pixel.a, Is.EqualTo(128).Within(1));
                Assert.That(material.GetTexture("_BacklightColorTex"), Is.SameAs(source));
                Assert.That(source.width, Is.EqualTo(4096));
                Assert.That(warnings.Any(w => w.Contains("1024×2")), Is.True);
                Assert.DoesNotThrow(() => LilToonGlbExtension.Validate(output));
            }
            finally { Object.DestroyImmediate(avatar); Object.DestroyImmediate(material); Object.DestroyImmediate(source); Object.DestroyImmediate(decoded); }
        }

        [Test]
        public void FourKOutlineWidthMaskIsResizedOnItsExportCopy()
        {
            var source = Image(4096, 8, true, _ => new Color32(64, 200, 100, 255));
            var owned = new List<Texture2D>();
            try
            {
                var copy = OutlineMaskTexture.Create(source, owned);
                Assert.That(copy.width, Is.EqualTo(1024));
                Assert.That(copy.height, Is.EqualTo(2));
                Assert.That(copy.GetPixel(0,0).g, Is.EqualTo(64 / 255f).Within(.01));
                Assert.That(source.width, Is.EqualTo(4096));
                Assert.That(source.GetPixels32()[0].g, Is.EqualTo(200));
                Assert.That(copy, Is.Not.SameAs(source));
            }
            finally { foreach (var texture in owned) Object.DestroyImmediate(texture); Object.DestroyImmediate(source); }
        }

        private static Texture2D Image(int width, int height, bool linear, Func<int,Color32> pixel)
        {
            var image = new Texture2D(width, height, TextureFormat.RGBA32, false, linear);
            image.SetPixels32(Enumerable.Range(0, width * height).Select(pixel).ToArray());
            image.Apply(false, false);
            return image;
        }
        private static Dictionary<string,object> Obj(params object[] pairs)
        {
            var result = new Dictionary<string,object>();
            for (var i=0;i<pairs.Length;i+=2) result.Add((string)pairs[i],pairs[i+1]);
            return result;
        }
        private static byte[] ImageBytes(GlbDocument glb,int index)
        {
            var image=(Dictionary<string,object>)((List<object>)glb.Json["images"])[index];
            var view=(Dictionary<string,object>)((List<object>)glb.Json["bufferViews"])[Convert.ToInt32(image["bufferView"])];
            return glb.Binary.Skip(Convert.ToInt32(view["byteOffset"])).Take(Convert.ToInt32(view["byteLength"])).ToArray();
        }
    }
}
