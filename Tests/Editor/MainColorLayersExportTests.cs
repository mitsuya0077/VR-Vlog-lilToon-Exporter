using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class MainColorLayersExportTests
    {
        [TestCase(1, false, false)] [TestCase(2, false, false)] [TestCase(3, false, false)]
        [TestCase(1, true, false)] [TestCase(2, true, false)] [TestCase(3, true, false)]
        [TestCase(1, true, true)] [TestCase(2, true, true)] [TestCase(3, true, true)]
        public void MainLayersSurviveActualVrmExport(int layers, bool textured, bool multi)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var material = new Material(Shader.Find(multi ? "_lil/lilToonMulti" : "lilToon"));
            var texture = new Texture2D(8, 8, TextureFormat.RGBA32, false, false);
            texture.SetPixels(Enumerable.Repeat(new Color(.8f,.9f,.7f,.8f),64).ToArray());texture.Apply();
            try
            {
                material.SetTexture("_MainTex", Texture2D.whiteTexture);
                material.SetColor("_Color", Color.red);
                material.SetFloat("_AsUnlit", 1);
                material.SetFloat("_UseShadow", 0);
                material.SetFloat("_Cull", 0);
                foreach (var layer in new[] { "2nd", "3rd" })
                {
                    material.SetFloat("_UseMain" + layer + "Tex", (layers & (layer == "2nd" ? 1 : 2)) != 0 ? 1 : 0);
                    material.SetTexture("_Main" + layer + "Tex", textured ? texture : null);
                    material.SetTexture("_Main" + layer + "BlendMask", null);
                    material.SetColor("_Color" + layer, layer == "2nd" ? new Color(0,1,0,.5f) : new Color(0,0,1,.5f));
                }
                if (multi)
                {
                    var utility=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("lilToon.lilMaterialUtils")).First(t=>t!=null);
                    utility.GetMethod("SetupMultiMaterial", new[] {typeof(Material)}).Invoke(null,new object[] {material});
                }
                foreach (var renderer in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) renderer.sharedMaterial = material;
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Main color layers", "Tests", exporterVersion: "0.11.12", lilToonVersion: "2.3.4", blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                var glb = GlbDocument.Read(bytes);
                Assert.That(VRVlog.LilToon.LilToonFullContract.SourceColorSpace(glb.Json),
                    Is.EqualTo(QualitySettings.activeColorSpace == ColorSpace.Linear ? "linear" : "gamma"));
                var asset = VRVlog.LilToon.LilToonFullContract.Object(glb.Json["asset"]);
                var extras = VRVlog.LilToon.LilToonFullContract.Object(asset["extras"]);
                Assert.That(extras[VRVlog.LilToon.LilToonFullContract.SourceColorSpaceKey], Is.Not.Null);
                extras[VRVlog.LilToon.LilToonFullContract.SourceColorSpaceKey] = "gamma";
                Assert.That(VRVlog.LilToon.LilToonFullContract.SourceColorSpace(glb.Json), Is.EqualTo("gamma"));
                extras[VRVlog.LilToon.LilToonFullContract.SourceColorSpaceKey] = "unknown";
                Assert.Throws<InvalidDataException>(() => VRVlog.LilToon.LilToonFullContract.Validate(glb.Json, bytes.Length, glb.Binary.Length));
                extras.Remove(VRVlog.LilToon.LilToonFullContract.SourceColorSpaceKey);
                Assert.That(VRVlog.LilToon.LilToonFullContract.SourceColorSpace(glb.Json), Is.EqualTo("linear"));
                var root = VRVlog.LilToon.LilToonFullContract.Root(glb.Json);
                var specs = VRVlog.LilToon.LilToonFullContract.List(root, "materials").Select(VRVlog.LilToon.LilToonFullContract.Object);
                foreach (var spec in specs)
                {
                    var values = VRVlog.LilToon.LilToonFullContract.List(spec, "values").Select(VRVlog.LilToon.LilToonFullContract.Object).ToDictionary(v => VRVlog.LilToon.LilToonFullContract.Text(v, "name"));
                    foreach (var layer in new[] { "2nd", "3rd" })
                    {
                        var toggle = "_UseMain" + layer + "Tex";
                        Assert.That(VRVlog.LilToon.LilToonFullContract.Vector(VRVlog.LilToon.LilToonFullContract.Get(values[toggle], "value"))[0], Is.EqualTo(material.GetFloat(toggle)));
                        var color = "_Color" + layer;
                        Assert.That(VRVlog.LilToon.LilToonFullContract.Vector(VRVlog.LilToon.LilToonFullContract.Get(values[color], "value")), Is.EqualTo(new[] { material.GetColor(color).r, material.GetColor(color).g, material.GetColor(color).b, material.GetColor(color).a }));
                    }
                }
                foreach (var layer in new[] { "2nd", "3rd" })
                {
                    Assert.That(material.GetColor("_Color" + layer), Is.EqualTo(layer == "2nd" ? new Color(0,1,0,.5f) : new Color(0,0,1,.5f)));
                    Assert.That(material.GetTexture("_Main" + layer + "Tex"), Is.SameAs(textured ? texture : null));
                    Assert.That(material.GetTexture("_Main" + layer + "BlendMask"), Is.Null);
                }
                var directory = Environment.GetEnvironmentVariable("MAIN_COLOR_EVIDENCE");
                if (!string.IsNullOrEmpty(directory)) File.WriteAllBytes(Path.Combine(directory, "layers-" + layers + "-" + textured + "-" + multi + ".vrm"), bytes);
            }
            finally { Object.DestroyImmediate(material); Object.DestroyImmediate(texture); }
        }

    }
}
