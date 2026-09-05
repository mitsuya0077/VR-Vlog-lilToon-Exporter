using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class MaterialFallbackTests
    {
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
