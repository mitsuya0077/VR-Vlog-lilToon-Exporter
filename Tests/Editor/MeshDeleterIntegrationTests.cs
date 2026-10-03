using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UniGLTF;
using UniVRM10;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class MeshDeleterIntegrationTests
    {
        // The tool remains optional: call its real public implementation only
        // when installed, without adding an assembly or package dependency.
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task DeletedSharedMeshAndMorphSurviveOneClickExportAndReimport(bool useInstalledMeshDeleter, bool fullLilToon)
        {
            MethodInfo remove = null;
            if (useInstalledMeshDeleter)
            {
                var deleter = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("Gatosyocora.MeshDeleterWithTexture.MeshDeleter"))
                    .FirstOrDefault(t => t != null);
                if (deleter == null) Assert.Ignore("Install the official MeshDeleterWithTexture package for this integration test.");
                remove = deleter.GetMethod("RemoveTriangles", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(Mesh), typeof(bool[]), typeof(Vector2Int), typeof(List<int>), typeof(bool) }, null);
                Assert.That(remove, Is.Not.Null, "Requires the public MeshDeleterWithTexture 0.10.5 RemoveTriangles API.");
            }
            var shader = Shader.Find("lilToon");
            if (shader == null) Assert.Ignore("Install lilToon for the real exporter entry point.");

            using var fixture = new AttachmentConnectionTests.Fixture();
            var folder = "Assets/__MeshDeleterRoundTrip_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            var original = Object.Instantiate(fixture.Mesh);
            Mesh deleted = null;
            var material = new Material(shader);
            Vrm10Instance imported = null;
            try
            {
                original.name = "Original before MeshDeleter";
                original.Clear();
                original.vertices = new[]
                {
                    new Vector3(-.2f, 1.7f, .08f), new Vector3(-.1f, 1.7f, .08f), new Vector3(-.15f, 1.9f, .08f),
                    new Vector3(.1f, 1.7f, .08f), new Vector3(.2f, 1.7f, .08f), new Vector3(.15f, 1.9f, .08f)
                };
                original.triangles = new[] { 0, 1, 2, 3, 4, 5 };
                original.uv = Enumerable.Repeat(new Vector2(.25f, .5f), 3)
                    .Concat(Enumerable.Repeat(new Vector2(.75f, .5f), 3)).ToArray();
                original.normals = Enumerable.Repeat(Vector3.forward, 6).ToArray();
                original.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 6).ToArray();
                original.bindposes = fixture.Mesh.bindposes;
                var deltas = Enumerable.Range(0, 6).Select(i => new Vector3(.01f * (i + 1), .02f, 0)).ToArray();
                original.AddBlendShapeFrame("Hair detail", 100, deltas, new Vector3[6], new Vector3[6]);
                AssetDatabase.CreateAsset(original, folder + "/Original.asset");

                foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    skin.sharedMesh = original;
                    skin.sharedMaterial = material;
                    skin.SetBlendShapeWeight(0, 0);
                }
                var front = fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>();
                var back = fixture.Source.transform.Find("Back").GetComponent<SkinnedMeshRenderer>();
                if (useInstalledMeshDeleter)
                {
                    var result = (ValueTuple<Mesh, bool[]>)remove.Invoke(null,
                        new object[] { original, new[] { true, false }, new Vector2Int(2, 1), new List<int> { 0 }, false });
                    deleted = result.Item1;
                    Assert.That(result.Item2, Is.EqualTo(new[] { false }));
                }
                else
                {
                    // External mesh editors publish their result by replacing
                    // sharedMesh. This contract runs on CI without the tool.
                    deleted = Object.Instantiate(original);
                    deleted.Clear();
                    deleted.vertices = original.vertices.Skip(3).ToArray();
                    deleted.triangles = new[] { 0, 1, 2 };
                    deleted.uv = original.uv.Skip(3).ToArray();
                    deleted.normals = original.normals.Skip(3).ToArray();
                    deleted.boneWeights = original.boneWeights.Skip(3).ToArray();
                    deleted.bindposes = original.bindposes;
                    deleted.AddBlendShapeFrame("Hair detail", 100, deltas.Skip(3).ToArray(), new Vector3[3], new Vector3[3]);
                }
                Assert.That(deleted, Is.Not.Null);
                Assert.That(deleted.vertexCount, Is.EqualTo(3), "The externally deleted mesh must contain only the surviving triangle.");
                Assert.That(deleted.triangles, Is.EqualTo(new[] { 0, 1, 2 }));
                var retainedDeltas = new Vector3[3];
                deleted.GetBlendShapeFrameVertices(0, 0, retainedDeltas, null, null);
                Assert.That(retainedDeltas, Is.EqualTo(deltas.Skip(3).ToArray()));
                deleted.name = "Actual MeshDeleter result";
                AssetDatabase.CreateAsset(deleted, folder + "/Deleted.asset");
                front.sharedMesh = deleted;
                AssetDatabase.SaveAssets();
                var originalAssetFile = Path.Combine(Application.dataPath, Path.GetFileName(folder), "Original.asset");
                var deletedAssetFile = Path.Combine(Application.dataPath, Path.GetFileName(folder), "Deleted.asset");
                var originalBytes = File.ReadAllBytes(originalAssetFile);
                var deletedBytes = File.ReadAllBytes(deletedAssetFile);
                var originalVertices = original.vertices;
                var originalTriangles = original.triangles;
                var deletedVertices = deleted.vertices;
                var warnings = new List<string>();

                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "MeshDeleter regression", "Tests", warnings,
                    exporterVersion: fullLilToon ? "meshdeleter-regression" : null,
                    lilToonVersion: fullLilToon ? "2.3.4" : null,
                    blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported, Is.Not.Null, string.Join("\n", warnings));
                imported.Runtime.Process();
                var skins = imported.GetComponentsInChildren<SkinnedMeshRenderer>();
                var importedFront = skins.Single(s => s.name == "Front");
                var importedBack = skins.Single(s => s.name == "Back");
                Assert.That(importedFront.sharedMesh.vertexCount, Is.EqualTo(3));
                Assert.That(importedFront.sharedMesh.triangles.Length, Is.EqualTo(3));
                Assert.That(importedBack.sharedMesh.triangles.Length, Is.EqualTo(6), "The unaffected renderer retains both triangles.");
                var morph = importedFront.sharedMesh.GetBlendShapeIndex("Hair detail");
                Assert.That(morph, Is.GreaterThanOrEqualTo(0));
                var reloadedDeltas = new Vector3[importedFront.sharedMesh.vertexCount];
                importedFront.sharedMesh.GetBlendShapeFrameVertices(morph, 0, reloadedDeltas, null, null);
                AssertVectors(deletedVertices, importedFront.sharedMesh.vertices);
                AssertVectors(retainedDeltas, reloadedDeltas);
                var baked = new Mesh();
                try
                {
                    importedFront.SetBlendShapeWeight(morph, 100);
                    importedFront.BakeMesh(baked);
                    Assert.That(baked.triangles.Length, Is.EqualTo(3));
                    AssertVectors(deletedVertices.Zip(retainedDeltas, (v, d) => v + d).ToArray(), baked.vertices);
                }
                finally { Object.DestroyImmediate(baked); }

                Assert.That(front.sharedMesh, Is.SameAs(deleted));
                Assert.That(back.sharedMesh, Is.SameAs(original));
                Assert.That(front.GetBlendShapeWeight(0), Is.Zero);
                Assert.That(back.GetBlendShapeWeight(0), Is.Zero);
                Assert.That(original.vertices, Is.EqualTo(originalVertices));
                Assert.That(original.triangles, Is.EqualTo(originalTriangles));
                var originalDeltas = new Vector3[original.vertexCount];
                original.GetBlendShapeFrameVertices(0, 0, originalDeltas, null, null);
                Assert.That(originalDeltas, Is.EqualTo(deltas));
                Assert.That(deleted.vertices, Is.EqualTo(deletedVertices));
                Assert.That(File.ReadAllBytes(originalAssetFile), Is.EqualTo(originalBytes));
                Assert.That(File.ReadAllBytes(deletedAssetFile), Is.EqualTo(deletedBytes));
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                AssetDatabase.DeleteAsset(folder);
                if (original != null && !EditorUtility.IsPersistent(original)) Object.DestroyImmediate(original);
                if (deleted != null && !EditorUtility.IsPersistent(deleted)) Object.DestroyImmediate(deleted);
                Object.DestroyImmediate(material);
            }
        }

        private static void AssertVectors(Vector3[] expected, Vector3[] actual)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            for (var i = 0; i < expected.Length; i++)
                Assert.That(Vector3.Distance(expected[i], actual[i]), Is.LessThan(.0001f));
        }
    }
}
