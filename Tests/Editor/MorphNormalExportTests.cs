using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UniGLTF;
using UniVRM10;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class MorphNormalExportTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task AuthoredNormalTargetsKeepSourceIdentityAndSubmeshVertexOrder(bool positionsOnly)
        {
            using var f = new AttachmentConnectionTests.Fixture();
            var baseVertices = new[] { new Vector3(-.1f, 1.7f, .08f), new Vector3(.1f, 1.7f, .08f),
                new Vector3(-.1f, 1.9f, .08f), new Vector3(.1f, 1.9f, .08f) };
            f.Mesh.Clear();
            f.Mesh.name = "Identical authored mesh";
            f.Mesh.vertices = baseVertices;
            f.Mesh.normals = Enumerable.Repeat(Vector3.forward, 4).ToArray();
            f.Mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
            f.Mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 4).ToArray();
            f.Mesh.bindposes = new[] { f.Hair.GetChild(0).worldToLocalMatrix, f.Head.worldToLocalMatrix };
            f.Mesh.subMeshCount = 2;
            f.Mesh.SetTriangles(new[] { 3, 1, 0 }, 0);
            f.Mesh.SetTriangles(new[] { 2, 3, 1 }, 1);
            var deltaVertices = Enumerable.Range(0, 4).Select(i => new Vector3(.001f * i, .02f + i * .001f, 0)).ToArray();
            Vector3[] NormalDelta(float side) => Enumerable.Range(0, 4)
                .Select(i => new Vector3(side * (.03f + i * .01f), -.005f * i, .002f * i)).ToArray();
            f.Mesh.AddBlendShapeFrame("Authored shading", 50, deltaVertices.Select(v => v * .5f).ToArray(), NormalDelta(1).Select(v => v * .5f).ToArray(), null);
            f.Mesh.AddBlendShapeFrame("Authored shading", 100, deltaVertices, NormalDelta(1), null);
            f.Mesh.RecalculateBounds();
            var second = Object.Instantiate(f.Mesh); second.name = f.Mesh.name;
            second.ClearBlendShapes();
            second.AddBlendShapeFrame("Authored shading", 50, deltaVertices.Select(v => v * .5f).ToArray(), NormalDelta(-1).Select(v => v * .5f).ToArray(), null);
            second.AddBlendShapeFrame("Authored shading", 100, deltaVertices, NormalDelta(-1), null);
            Vrm10Instance imported = null;
            try
            {
                var input = f.Copy.GetComponentsInChildren<SkinnedMeshRenderer>();
                Assert.That(input.Select(skin => skin.name), Is.EquivalentTo(new[] { "Front", "Back" }));
                foreach (var skin in input)
                {
                    skin.sharedMesh = skin.name == "Front" ? f.Mesh : second;
                    skin.sharedMaterials = new[] { skin.sharedMaterial, skin.sharedMaterial };
                    skin.SetBlendShapeWeight(0, 0);
                }
                var expected = new Dictionary<string, (Vector3[] Vertices, Vector3[] Normals)[]>();
                foreach (var skin in input)
                {
                    expected.Add(skin.name, new[] { Measure(skin, 0), Measure(skin, 50), Measure(skin, 100) });
                    skin.SetBlendShapeWeight(0, 0);
                }
                var sourceVertices = f.Mesh.vertices; var sourceNormals = f.Mesh.normals;
                var sourceBinds = f.Mesh.bindposes; var sourceWeights = f.Mesh.boneWeights;
                var sourceDeltaVertices = new Vector3[4]; var sourceDeltaNormals = new Vector3[4];
                f.Mesh.GetBlendShapeFrameVertices(0, 1, sourceDeltaVertices, sourceDeltaNormals, null);
                var outputIndices = new Dictionary<UnityEngine.Mesh, int>();
                var bytes = Vrm10AppearanceExporter.Export(new GltfExportSettings { ExportOnlyBlendShapePosition = positionsOnly },
                    f.Copy, new BuiltInVrm10MaterialExporter(), new MobileTextureSerializer(null),
                    new VRM10ObjectMeta { Name = "Morph normal fixture", Version = "1", Authors = new List<string> { "Tests" } },
                    afterExport: (converter, model, storage) =>
                    {
                        Assert.That(converter.Meshes.Count, Is.EqualTo(2));
                        foreach (var pair in converter.Meshes)
                        {
                            Assert.That(pair.Key == f.Mesh || pair.Key == second, Is.True);
                            var index = model.MeshGroups.IndexOf(pair.Value); Assert.That(index, Is.GreaterThanOrEqualTo(0));
                            outputIndices.Add(pair.Key, index);
                            Assert.That(pair.Value.Meshes.Single().MorphTargets.Single().VertexBuffer.Normals != null, Is.EqualTo(!positionsOnly));
                        }
                    });
                Assert.That(outputIndices.Count, Is.EqualTo(2));
                // Pinned MeshWriter enumerates referenced source indices in
                // ascending order for each primitive. No name/position matching.
                var primitiveSources = new[] { new[] { 0, 1, 3 }, new[] { 1, 2, 3 } };
                var importedSources = primitiveSources.SelectMany(indices => indices).ToArray();
                using (var data = new GlbBinaryParser(bytes, "synthetic normal fixture").Parse())
                {
                    foreach (var pair in outputIndices)
                    {
                        var mesh = data.GLTF.meshes[pair.Value];
                        Assert.That(mesh.primitives.Count, Is.EqualTo(2));
                        var delta = NormalDelta(pair.Key == f.Mesh ? 1 : -1);
                        for (var submesh = 0; submesh < 2; submesh++)
                        {
                            var primitive = mesh.primitives[submesh]; var target = primitive.targets.Single();
                            Assert.That(target.NORMAL >= 0, Is.EqualTo(!positionsOnly));
                            var positions = data.GetArrayFromAccessor<Vector3>(primitive.attributes.POSITION);
                            Assert.That(positions.Length, Is.EqualTo(primitiveSources[submesh].Length));
                            var normals = positionsOnly ? default : data.GetArrayFromAccessor<Vector3>(target.NORMAL);
                            for (var vertex = 0; vertex < positions.Length; vertex++)
                            {
                                var sourceIndex = primitiveSources[submesh][vertex];
                                var sourcePosition = baseVertices[sourceIndex];
                                AssertVector(new Vector3(-sourcePosition.x, sourcePosition.y, sourcePosition.z), positions[vertex]);
                                if (!positionsOnly)
                                {
                                    var sourceNormal = delta[sourceIndex];
                                    AssertVector(new Vector3(-sourceNormal.x, sourceNormal.y, sourceNormal.z), normals[vertex]);
                                }
                            }
                        }
                    }
                }
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller(),
                    controlRigGenerationOption: ControlRigGenerationOption.None);
                _ = imported.Runtime; imported.enabled = false;
                var importedSkins = imported.GetComponentsInChildren<SkinnedMeshRenderer>();
                Assert.That(importedSkins.Length, Is.EqualTo(2));
                foreach (var skin in importedSkins)
                {
                    Assert.That(expected.ContainsKey(skin.name), Is.True);
                    Assert.That(skin.sharedMesh.vertexCount, Is.EqualTo(importedSources.Length));
                    var shape = skin.sharedMesh.GetBlendShapeIndex("Authored shading"); Assert.That(shape, Is.GreaterThanOrEqualTo(0));
                    for (var step = 0; step < 3; step++)
                    {
                        skin.SetBlendShapeWeight(shape, step * 50);
                        var actual = Measure(skin, step * 50);
                        for (var vertex = 0; vertex < importedSources.Length; vertex++)
                        {
                            var sourceIndex = importedSources[vertex];
                            AssertVector(expected[skin.name][step].Vertices[sourceIndex], actual.Vertices[vertex]);
                            var expectedNormal = expected[skin.name][positionsOnly ? 0 : step].Normals[sourceIndex].normalized;
                            Assert.That(Vector3.Distance(expectedNormal, actual.Normals[vertex].normalized), Is.LessThan(.00035f));
                        }
                    }
                }
                Assert.That(f.Mesh.vertices, Is.EqualTo(sourceVertices)); Assert.That(f.Mesh.normals, Is.EqualTo(sourceNormals));
                Assert.That(f.Mesh.bindposes, Is.EqualTo(sourceBinds)); Assert.That(f.Mesh.boneWeights, Is.EqualTo(sourceWeights));
                var afterVertices = new Vector3[4]; var afterNormals = new Vector3[4];
                f.Mesh.GetBlendShapeFrameVertices(0, 1, afterVertices, afterNormals, null);
                Assert.That(afterVertices, Is.EqualTo(sourceDeltaVertices)); Assert.That(afterNormals, Is.EqualTo(sourceDeltaNormals));
                Assert.That(f.Source.GetComponentsInChildren<SkinnedMeshRenderer>().All(skin => skin.sharedMesh == f.Mesh && skin.GetBlendShapeWeight(0) == 35), Is.True);
            }
            finally
            {
                if (imported != null) { imported.DisposeRuntime(); Object.DestroyImmediate(imported.gameObject); }
                Object.DestroyImmediate(second);
            }
        }

        static (Vector3[] Vertices, Vector3[] Normals) Measure(SkinnedMeshRenderer skin, float weight)
        {
            skin.SetBlendShapeWeight(0, weight);
            var mesh = new UnityEngine.Mesh();
            try { skin.BakeMesh(mesh); return (mesh.vertices, mesh.normals); }
            finally { Object.DestroyImmediate(mesh); }
        }
        static void AssertVector(Vector3 expected, Vector3 actual)
            => Assert.That(Vector3.Distance(expected, actual), Is.LessThan(.00002f));
    }
}
