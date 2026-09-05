using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class MaterialFallbackTests
    {
        [Test]
        public void OutlineMaskConvertsRedToGreenAndRetainsSource()
        {
            var source = new Material(Shader.Find("Hidden/VRVlogTests/lilToon"));
            var mask = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            var materials = new List<Material>();
            var textures = new List<Texture2D>();
            var masks = new Dictionary<Texture, Texture2D>();
            try
            {
                mask.SetPixels(new[] { new Color(.2f,.9f,1), Color.black, Color.white, Color.black });
                mask.Apply();
                source.SetTexture("_OutlineWidthMask", mask);
                source.SetTextureScale("_MainTex", new Vector2(2f, 3f));
                source.SetTextureOffset("_MainTex", new Vector2(.1f, .2f));
                var fallback = UniVrmOneClickExporter.CreateMToonFallback(source, materials, null, true, textures, masks);
                var converted = (Texture2D)fallback.GetTexture("_OutlineWidthTex");
                Assert.AreNotSame(mask, converted);
                Assert.AreEqual(.2f, converted.GetPixels()[0].g, .01f);
                Assert.AreSame(mask, source.GetTexture("_OutlineWidthMask"));
                Assert.AreEqual(.9f, mask.GetPixels()[0].g, .01f);
                Assert.AreEqual(source.GetTextureScale("_MainTex"), fallback.GetTextureScale("_MainTex"));
                Assert.AreEqual(source.GetTextureOffset("_MainTex"), fallback.GetTextureOffset("_MainTex"));
                var second = new Material(source);
                materials.Add(second);
                var shared = UniVrmOneClickExporter.CreateMToonFallback(second, materials, null, true, textures, masks);
                Assert.AreSame(converted, shared.GetTexture("_OutlineWidthTex"));
                Assert.AreEqual(1, textures.Count, "Shared masks must not duplicate GPU/CPU storage.");
                var nextExport = UniVrmOneClickExporter.CreateMToonFallback(second, materials, null, true, textures, new Dictionary<Texture, Texture2D>());
                Assert.AreNotSame(converted, nextExport.GetTexture("_OutlineWidthTex"), "A later export must not reuse a stale conversion.");
                source.SetFloat("_OutlineVertexR2Width", 1f);
                var warnings = new List<string>();
                fallback = UniVrmOneClickExporter.CreateMToonFallback(source, materials, warnings, true, textures);
                Assert.AreEqual(0, fallback.GetInt("_OutlineWidthMode"));
                Assert.IsFalse(LilToonMaterialReader.Read(source,0,(_,__)=>0).features.Contains("outline"));
                Assert.IsNotEmpty(warnings);
            }
            finally
            {
                foreach (var material in materials) Object.DestroyImmediate(material);
                foreach (var texture in textures) Object.DestroyImmediate(texture);
                Object.DestroyImmediate(mask); Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void EnabledShadowUsesBaseImageForNullOrBuiltInWhiteShadeMap()
        {
            var source = new Material(Shader.Find("Hidden/VRVlogTests/lilToon"));
            var image = new Texture2D(2, 2);
            var shade = new Texture2D(2, 2);
            var created = new List<Material>();
            try
            {
                source.SetTexture("_MainTex", image);
                source.SetFloat("_UseShadow", 1f);
                foreach (var unset in new Texture[] { null, Texture2D.whiteTexture })
                {
                    source.SetTexture("_ShadowColorTex", unset);
                    var fallback = UniVrmOneClickExporter.CreateMToonFallback(source, created, null);
                    Assert.AreSame(image, fallback.GetTexture("_ShadeTex"));
                    Assert.AreSame(unset, source.GetTexture("_ShadowColorTex"));
                    var record = LilToonMaterialReader.Read(source, 0, (_, __) => 0);
                    Assert.IsFalse(record.textures.Any(t => t.semantic == "shadow"));
                }
                source.SetTexture("_ShadowColorTex", shade);
                Assert.AreSame(shade, UniVrmOneClickExporter.CreateMToonFallback(source, created, null).GetTexture("_ShadeTex"));
            }
            finally
            {
                foreach (var material in created) Object.DestroyImmediate(material);
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(image);
                Object.DestroyImmediate(shade);
            }
        }

        [Test]
        public void DefaultExportKeepsShadeImageAndSuppressesSharedEmissionWithoutChangingSource()
        {
            var source = new Material(Shader.Find("Hidden/VRVlogTests/lilToon"));
            var image = new Texture2D(2, 2);
            var created = new List<Material>();
            try
            {
                source.SetTexture("_MainTex", image);
                source.SetTexture("_EmissionMap", image);
                source.SetTexture("_OutlineTex", image);
                var originalEmission = source.GetColor("_EmissionColor");
                var fallback = UniVrmOneClickExporter.CreateMToonFallback(source, created, new List<string>());
                Assert.AreSame(image, fallback.GetTexture("_MainTex"));
                Assert.AreSame(image, fallback.GetTexture("_ShadeTex"));
                Assert.AreEqual(0f, fallback.GetColor("_EmissionColor").maxColorComponent);
                Assert.AreEqual(0.0014f, fallback.GetFloat("_OutlineWidth"), 0.000001f);
                Assert.AreNotSame(image, fallback.GetTexture("_OutlineWidthTex"));
                var record = LilToonMaterialReader.Read(source, 0, (_, __) => 0);
                Assert.IsFalse(record.features.Contains("emission"));
                Assert.IsFalse(record.textures.Any(t => t.semantic == "emission"));
                Assert.AreEqual(originalEmission, source.GetColor("_EmissionColor"));
                Assert.AreEqual(0.14f, source.GetFloat("_OutlineWidth"), 0.000001f);
                Assert.AreSame(image, source.GetTexture("_EmissionMap"));
            }
            finally
            {
                foreach (var material in created) Object.DestroyImmediate(material);
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(image);
            }
        }

        [Test]
        public void OptOutRetainsEmissionInBothMaterialRepresentations()
        {
            var source = new Material(Shader.Find("Hidden/VRVlogTests/lilToon"));
            var image = new Texture2D(2, 2);
            var created = new List<Material>();
            try
            {
                source.SetTexture("_MainTex", image);
                source.SetTexture("_EmissionMap", image);
                var fallback = UniVrmOneClickExporter.CreateMToonFallback(source, created, null, false);
                Assert.Greater(fallback.GetColor("_EmissionColor").maxColorComponent, 0f);
                Assert.AreSame(image, fallback.GetTexture("_EmissionMap"));
                Assert.IsTrue(LilToonMaterialReader.Read(source, 0, (_, __) => 0,
                    suppressSharedTextureEmission: false).features.Contains("emission"));
            }
            finally
            {
                foreach (var material in created) Object.DestroyImmediate(material);
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(image);
            }
        }
    }
}
