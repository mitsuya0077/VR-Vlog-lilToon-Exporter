using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UniGLTF;
using UniVRM10;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public class DependencyRoundTripTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task SupportedBackendPreservesMeshesMorphsAndMaterialBindingsOnReimport(bool fullLilToon)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var skins = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
            var shader = Shader.Find("lilToon");
            Assert.That(shader, Is.Not.Null, "The real lilToon package is required.");
            foreach (var skin in skins) skin.sharedMaterial.shader = shader;
            var originalMeshes = skins.Select(s => s.sharedMesh).ToArray();
            var originalMaterials = skins.Select(s => s.sharedMaterial).ToArray();
            var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Dependency regression", "Tests",
                exporterVersion: fullLilToon ? "compatibility-test" : null,
                lilToonVersion: fullLilToon ? "2.3.4" : null,
                blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
            Vrm10Instance imported = null;
            try
            {
                Assert.That(bytes.Length, Is.GreaterThan(20));
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported, Is.Not.Null);
                foreach (var name in new[] { "Front", "Back" })
                {
                    var skin = imported.GetComponentsInChildren<SkinnedMeshRenderer>().Single(s => s.name == name);
                    Assert.That(skin.sharedMesh.vertexCount, Is.EqualTo(3));
                    Assert.That(skin.sharedMesh.blendShapeCount, Is.GreaterThanOrEqualTo(1));
                    Assert.That(skin.sharedMaterials.Length, Is.EqualTo(skin.sharedMesh.subMeshCount));
                    Assert.That(skin.sharedMaterials.All(m => m != null && m.shader != null), Is.True);
                    Assert.That(skin.bones.All(b => b != null), Is.True);
                }
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); }
            Assert.That(skins.Select(s => s.sharedMesh), Is.EqualTo(originalMeshes));
            Assert.That(skins.Select(s => s.sharedMaterial), Is.EqualTo(originalMaterials));
        }
    }
}
