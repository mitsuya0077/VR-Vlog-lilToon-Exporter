using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class LayerAlphaTests
    {
        readonly List<Object> owned = new List<Object>();
        T Own<T>(T item) where T : Object { owned.Add(item); return item; }
        [TearDown] public void Cleanup() { foreach (var item in owned) Object.DestroyImmediate(item); owned.Clear(); }
        Texture2D Image(Color color)
        {
            var image = Own(new Texture2D(16, 16, TextureFormat.RGBA32, false, true));
            image.SetPixels(Enumerable.Repeat(color, 256).ToArray()); image.Apply();
            image.filterMode = FilterMode.Point;
            return image;
        }
        Material Source(string variant = "Transparent")
        {
            var shader = Shader.Find("Hidden/lilToon" + variant) ?? Shader.Find("lilToon" + variant);
            Assert.That(shader, Is.Not.Null);
            var material = Own(new Material(shader));
            material.SetTexture("_MainTex", Image(new Color(.2f, .3f, .4f, .5f)));
            material.SetColor("_Color", new Color(1, 1, 1, .5f));
            material.SetFloat("_Cutoff", 0);
            material.SetFloat("_AsUnlit", 1);
            material.SetFloat("_UseShadow", 0);
            return material;
        }
        void Layer(Material material, string layer, int alphaMode, float alpha = .5f)
        {
            material.SetFloat("_UseMain" + layer + "Tex", 1);
            material.SetTexture("_Main" + layer + "Tex", Image(new Color(.6f, .1f, .2f, alpha)));
            material.SetColor("_Color" + layer, new Color(1, 1, 1, .5f));
            material.SetTexture("_Main" + layer + "BlendMask", Image(Color.gray));
            material.SetFloat("_Main" + layer + "TexAlphaMode", alphaMode);
        }
        Material Bake(Material source, bool alphaMask = false)
        {
            var copy = Own(new Material(source));
            var textures = new List<Texture2D>();
            try
            {
                LilToonMainTextureBaker.Bake(copy, textures, null);
                if (alphaMask) AlphaMaskBaker.Bake(copy, textures, null);
            }
            finally { owned.AddRange(textures); }
            return copy;
        }

        [TestCase(1, .125f)] [TestCase(2, .03125f)]
        [TestCase(3, .375f)] [TestCase(4, .125f)]
        public void AlphaModesIncludeTextureTintAndMaskOnce(int mode, float expected)
        {
            foreach (var variant in new[] { "Transparent", "Cutout" })
            foreach (var layer in new[] { "2nd", "3rd" })
            {
                var source = Source(variant); Layer(source, layer, mode);
                var copy = Bake(source);
                var pixel = ((Texture2D)copy.GetTexture("_MainTex")).GetPixel(4, 4);
                Assert.That(pixel.a, Is.EqualTo(expected).Within(.012f), variant + layer);
                var rgb = QualitySettings.activeColorSpace == ColorSpace.Linear ? pixel.linear : pixel;
                Assert.That(rgb.r, Is.EqualTo(.6f).Within(.015f), "Alpha mode makes the layer's RGB full strength");
                Assert.That(copy.GetColor("_Color"), Is.EqualTo(Color.white));
                Assert.That(copy.GetFloat("_UseMain" + layer + "Tex"), Is.Zero);
                Assert.That(source.GetFloat("_UseMain" + layer + "Tex"), Is.EqualTo(1));
                Assert.That(source.GetColor("_Color").a, Is.EqualTo(.5f));
                var record = LilToonMaterialReader.Read(copy, 0, (_, __) => 0);
                Assert.That(record.renderMode, Is.EqualTo(variant == "Transparent" ? "transparent" : "cutout"));
                var materials = new List<Material>();
                try
                {
                    var fallback = UniVrmOneClickExporter.CreateMToonFallback(copy, materials, null);
                    Assert.That(fallback.GetTexture("_MainTex"), Is.SameAs(copy.GetTexture("_MainTex")));
                    Assert.That(fallback.GetFloat("_AlphaMode"), Is.EqualTo(variant == "Transparent" ? 2 : 1));
                }
                finally { owned.AddRange(materials); }
            }
        }

        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void SecondThenThirdThenAlphaMaskMatchesLiveLilToon(int maskMode)
        {
            var source = Source();
            Layer(source, "2nd", 3); Layer(source, "3rd", 2);
            source.SetTexture("_AlphaMask", Image(Color.gray));
            source.SetFloat("_AlphaMaskMode", maskMode);
            source.SetFloat("_AlphaMaskScale", .4f);
            source.SetFloat("_AlphaMaskValue", .1f);
            AssertRenderedEqual(source, Bake(source, true));
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
        public void EveryRgbBlendModeMatchesLiveAlphaLayer(int blendMode)
        {
            foreach (var mode in new[] { 1, 2, 3, 4 })
            {
                var source = Source(); Layer(source, "2nd", mode);
                source.SetFloat("_Main2ndTexBlendMode", blendMode);
                Layer(source, "3rd", 0); // Ordinary alpha-weighted RGB still works afterwards.
                AssertRenderedEqual(source, Bake(source));
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void StaticPatternWithTransformedMainUvAndDecalMatchesLiveShader(bool decal)
        {
            var source = Source(); Layer(source, "2nd", 1);
            var pattern = Image(Color.white);
            pattern.SetPixels(Enumerable.Range(0, 256).Select(i => i % 16 < 8
                ? new Color(.1f, .3f, .6f, .2f) : new Color(.6f, .2f, .1f, .8f)).ToArray()); pattern.Apply();
            source.SetTexture("_Main2ndTex", pattern);
            source.SetTextureScale("_MainTex", new Vector2(.5f, .5f));
            source.SetTextureOffset("_MainTex", new Vector2(.25f, .25f));
            if (decal)
            {
                source.SetFloat("_Main2ndTexIsDecal", 1);
                source.SetTextureScale("_Main2ndTex", new Vector2(2, 1));
                source.SetTextureOffset("_Main2ndTex", new Vector2(-1, 0));
            }
            AssertRenderedEqual(source, Bake(source));
        }

        void AssertRenderedEqual(Material source, Material baked)
        {
            var before = Render(source); var after = Render(baked);
            foreach (var x in new[] { 6, 13, 22, 28 })
            {
                var a = before.GetPixel(x, 16); var b = after.GetPixel(x, 16);
                for (var channel = 0; channel < 4; channel++)
                    Assert.That(b[channel], Is.EqualTo(a[channel]).Within(.035f), "x=" + x + " RGBA channel=" + channel);
            }
        }
        Texture2D Render(Material original)
        {
            var material = Own(new Material(original));
            // Read the actual upstream shader's unlit RGBA without framebuffer blending.
            material.SetFloat("_SrcBlend", (float)BlendMode.One);
            material.SetFloat("_DstBlend", (float)BlendMode.Zero);
            material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            material.SetFloat("_DstBlendAlpha", (float)BlendMode.Zero);
            material.SetFloat("_AlphaToMask", 0); material.SetFloat("_ZWrite", 0); material.SetFloat("_Cull", 0);
            var cameraObject = Own(new GameObject("Layer alpha reference camera"));
            var camera = cameraObject.AddComponent<Camera>();
            camera.orthographic = true; camera.orthographicSize = .5f;
            camera.transform.position = new Vector3(0, 0, -2); camera.cullingMask = 1 << 31;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.clear;
            camera.allowHDR = false; camera.allowMSAA = false;
            var quad = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); quad.layer = 31;
            quad.GetComponent<Renderer>().sharedMaterial = material;
            var target = RenderTexture.GetTemporary(32, 32, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            try
            {
                camera.targetTexture = target; camera.Render(); RenderTexture.active = target;
                var image = Own(new Texture2D(32, 32, TextureFormat.RGBA32, false, false));
                image.ReadPixels(new Rect(0, 0, 32, 32), 0, 0); image.Apply(); return image;
            }
            finally
            {
                camera.targetTexture = null; RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(quad); Object.DestroyImmediate(cameraObject);
            }
        }
    }
}
