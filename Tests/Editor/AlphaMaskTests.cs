using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class AlphaMaskTests
    {
        [TestCase(1, .4f)]
        [TestCase(2, .1f)]
        [TestCase(3, .65f)]
        [TestCase(4, 0f)]
        public void AlphaModesPreserveRgbAndIncludeColorAlphaOnce(int mode, float expected)
        {
            var shader = Shader.Find("lilToonTransparent");
            if (shader == null) shader = Shader.Find("Hidden/lilToonTransparent");
            Assert.That(shader, Is.Not.Null, "Install supported lilToon.");
            var material = new Material(shader);
            var source = new Texture2D(2,2,TextureFormat.RGBA32,false,true);
            var mask = new Texture2D(2,2,TextureFormat.RGBA32,false,true);
            var owned = new List<Texture2D>();
            try
            {
                source.SetPixels(new[] { new Color(.2f,.3f,.4f,.5f),new Color(.2f,.3f,.4f,.5f),new Color(.2f,.3f,.4f,.5f),new Color(.2f,.3f,.4f,.5f) }); source.Apply();
                mask.SetPixels(new[] { Color.gray,Color.gray,Color.gray,Color.gray }); mask.Apply();
                material.SetTexture("_MainTex",source); material.SetTexture("_AlphaMask",mask);
                material.SetColor("_Color",new Color(1,1,1,.5f));
                material.SetFloat("_AlphaMaskMode",mode); material.SetFloat("_AlphaMaskScale",.6f); material.SetFloat("_AlphaMaskValue",.1f);
                AlphaMaskBaker.Bake(material,owned,null);
                Assert.That(owned, Has.Count.EqualTo(1));
                var pixel = owned[0].GetPixel(0,0);
                Assert.That(pixel.a, Is.EqualTo(expected).Within(.012f));
                Assert.That(material.GetColor("_Color").a, Is.EqualTo(1));
                Assert.That(material.GetFloat("_AlphaMaskMode"), Is.Zero);
                Assert.That(source.GetPixel(0,0).a, Is.EqualTo(.5f).Within(.01f));
                var rgb = QualitySettings.activeColorSpace == ColorSpace.Linear ? pixel.linear : pixel;
                Assert.That(rgb.r, Is.EqualTo(.2f).Within(.015f));
            }
            finally { foreach (var texture in owned) Object.DestroyImmediate(texture); Object.DestroyImmediate(source); Object.DestroyImmediate(mask); Object.DestroyImmediate(material); }
        }
    }
}
