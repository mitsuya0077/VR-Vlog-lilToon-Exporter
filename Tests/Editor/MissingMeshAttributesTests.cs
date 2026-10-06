using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UniGLTF;
using UniVRM10;
using UnityEditor;
using UnityEngine;
using F = VRVlog.LilToon.LilToonFullContract;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class MissingMeshAttributesTests
    {
        // Ascending source indices used by the pinned divided MeshWriter,
        // followed by UniVRM's concatenation of the two imported primitives.
        static readonly int[][] PrimitiveSources = { new[] { 0, 1, 3 }, new[] { 1, 2, 3 } };
        static readonly int[] ImportedSources = PrimitiveSources.SelectMany(indices => indices).ToArray();

        [TestCase(false, true, false)] [TestCase(false, false, true)] [TestCase(false, true, true)]
        [TestCase(true, true, false)] [TestCase(true, false, true)] [TestCase(true, true, true)]
        [TestCase(false, false, false)] [TestCase(true, false, false)]
        public async Task DirectUniVrmExportRepairsOnlyAbsentAttributesAndKeepsAuthoredMorphs(bool skinned, bool missingNormals, bool missingUv)
        {
            using var fixture = new Fixture(skinned, missingNormals, missingUv);
            var before = fixture.Snapshot();
            var warnings = new List<string>();
            var bytes = fixture.ExportDirect(warnings);
            await CheckOutput(fixture, bytes, false);
            fixture.AssertUnchanged(before);
            Assert.That(warnings.Count > 0, Is.EqualTo(missingNormals || missingUv));
        }

        [TestCase(false)] [TestCase(true)]
        public async Task MissingUv0NeverBorrowsASecondUvChannel(bool skinned)
        {
            using var fixture = new Fixture(skinned, false, true, uv1Only: true);
            var before = fixture.Snapshot();
            Assert.That(fixture.Mesh.uv, Is.Empty);
            Assert.That(fixture.Mesh.uv2.Any(uv => uv != Vector2.zero), Is.True);
            var bytes = fixture.ExportDirect();
            await CheckOutput(fixture, bytes, false);
            fixture.AssertUnchanged(before);
        }

        [Test]
        public async Task SharedStaticMeshRepairsEveryExportedMeshGroupWithoutSplittingSourceIdentity()
        {
            using var fixture = new Fixture(false, true, true, twin: true);
            var before = fixture.Snapshot();
            var bytes = fixture.ExportDirect();
            await CheckOutput(fixture, bytes, false);
            fixture.AssertUnchanged(before);
            Assert.That(fixture.Targets.Select(target => target.GetComponent<MeshFilter>().sharedMesh).Distinct().Count(), Is.EqualTo(1));
        }

        [TestCase(false, false)] [TestCase(true, false)]
        [TestCase(false, true)] [TestCase(true, true)]
        public async Task AdditionalVertexStreamsPreserveAuthoredChannelInsteadOfUsingFallback(bool normals, bool fullExporter)
        {
            using var fixture = new Fixture(false, normals, !normals);
            if (fullExporter) fixture.UseLilToon();
            var streams = new Mesh { name = "Authored additional stream", vertices = fixture.Positions };
            var authoredNormals = Enumerable.Range(0, 4).Select(index => new Vector3(.3f + .1f * index, .2f, 1).normalized).ToArray();
            var authoredUv = new[] { new Vector2(.07f, .12f), new Vector2(.32f, .29f), new Vector2(.61f, .72f), new Vector2(.95f, .87f) };
            if (normals) streams.normals = authoredNormals; else streams.uv = authoredUv;
            var renderer = fixture.Targets.Single().GetComponent<MeshRenderer>();
            renderer.additionalVertexStreams = streams;
            try
            {
                var before = fixture.Snapshot(); var beforeStream = EditorJsonUtility.ToJson(streams);
                var bytes = fullExporter
                    ? UniVrmOneClickExporter.Export(fixture.Root, "Additional streams", "Tests",
                        exporterVersion: "mesh-attribute-regression", lilToonVersion: "2.3.4",
                        blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None })
                    : fixture.ExportDirect();
                if (fullExporter) LilToonGlbExtension.Validate(bytes);
                await CheckOutput(fixture, bytes, false, normals ? authoredNormals : null, normals ? null : authoredUv);
                fixture.AssertUnchanged(before);
                Assert.That(renderer.additionalVertexStreams, Is.SameAs(streams));
                Assert.That(EditorJsonUtility.ToJson(streams), Is.EqualTo(beforeStream));
            }
            finally { renderer.additionalVertexStreams = null; Object.DestroyImmediate(streams); }
        }

        [TestCase(false)] [TestCase(true)]
        public void SharedStaticMeshKeepsEachRenderersAdditionalStreamsInFullBindings(bool normalPipeline)
        {
            using var fixture = new Fixture(false, true, true, twin: true);
            fixture.UseLilToon();
            var streams = new List<Mesh>();
            var uvs = new List<Vector4[][]>();
            try
            {
                for (var side = 0; side < fixture.Targets.Count; side++)
                {
                    var offset = side;
                    var stream = new Mesh { name = "Distinct stream " + side };
                    streams.Add(stream);
                    stream.vertices = fixture.Positions.Select((value, index) => value + new Vector3(.03f * offset, .004f * index, .01f * offset)).ToArray();
                    stream.normals = Enumerable.Range(0, 4).Select(index => new Vector3(.15f + .2f * offset, .03f * index, 1).normalized).ToArray();
                    stream.colors = Enumerable.Range(0, 4).Select(index => new Color(.1f + .2f * offset, .1f * index, .8f - .1f * offset, 1)).ToArray();
                    var channels = Enumerable.Range(0, 8).Select(channel => Enumerable.Range(0, 4)
                        .Select(index => new Vector4(.05f + .2f * offset + .02f * channel, .1f + .04f * index, .125f * channel, .2f * offset)).ToArray()).ToArray();
                    uvs.Add(channels);
                    for (var channel = 0; channel < 8; channel++) stream.SetUVs(channel, channels[channel]);
                    fixture.Targets[side].GetComponent<MeshRenderer>().additionalVertexStreams = stream;
                }
                var before = fixture.Snapshot(); var beforeStreams = streams.Select(stream => EditorJsonUtility.ToJson(stream)).ToArray();
                byte[] bytes;
                if (normalPipeline)
                    bytes = UniVrmOneClickExporter.Export(fixture.Root, "Shared streams", "Tests",
                        exporterVersion: "mesh-attribute-regression", lilToonVersion: "2.3.4",
                        blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                else
                {
                    // Keep the same source mesh in both renderers through the
                    // bridge, without the normal pipeline's mesh uniquing.
                    var snapshot = LilToonFullSnapshot.Capture(fixture.Root);
                    var fallback = new Material(Shader.Find("VRM10/MToon10"));
                    try
                    {
                        bytes = Vrm10AppearanceExporter.Export(new GltfExportSettings { ExportVertexColor = true },
                            fixture.Root, new PortableMaterialExporter(fallback), new MobileTextureSerializer(null),
                            new VRM10ObjectMeta { Name = "Shared streams", Version = "1", Authors = new List<string> { "Tests" } },
                            afterExport: (converter, model, storage) => snapshot.Bind(converter, model, storage));
                    }
                    finally { Object.DestroyImmediate(fallback); }
                    bytes = snapshot.Inject(bytes, "mesh-attribute-regression", "2.3.4");
                }
                LilToonGlbExtension.Validate(bytes);
                var document = GlbDocument.Read(bytes); var extension = F.Root(document.Json);
                using (var data = new GlbBinaryParser(bytes, "shared stream regression").Parse())
                {
                    Assert.That(F.List(extension, "bindings").Count, Is.EqualTo(2));
                    for (var side = 0; side < fixture.Targets.Count; side++)
                    {
                        var nodeIndex = data.GLTF.nodes.FindIndex(node => node.name == fixture.Targets[side].name);
                        Assert.That(nodeIndex, Is.GreaterThanOrEqualTo(0));
                        var node = data.GLTF.nodes[nodeIndex]; var mesh = data.GLTF.meshes[node.mesh];
                        var binding = F.List(extension, "bindings").Select(F.Object).Single(value => F.Int(value, "node") == nodeIndex);
                        Assert.That(F.Int(binding, "mesh"), Is.EqualTo(node.mesh));
                        Assert.That(mesh.primitives.Count, Is.EqualTo(2));
                        for (var primitiveIndex = 0; primitiveIndex < 2; primitiveIndex++)
                        {
                            var primitive = mesh.primitives[primitiveIndex];
                            var positions = data.GetArrayFromAccessor<Vector3>(primitive.attributes.POSITION);
                            var normals = data.GetArrayFromAccessor<Vector3>(primitive.attributes.NORMAL);
                            var colors = data.GetArrayFromAccessor<Vector4>(primitive.attributes.COLOR_0);
                            var uv0 = data.GetArrayFromAccessor<Vector2>(primitive.attributes.TEXCOORD_0);
                            for (var vertex = 0; vertex < 3; vertex++)
                            {
                                var source = PrimitiveSources[primitiveIndex][vertex];
                                AssertVector(Flip(streams[side].vertices[source]), positions[vertex]);
                                AssertVector(Flip(streams[side].normals[source]), normals[vertex]);
                                Assert.That(Vector4.Distance(streams[side].colors[source], colors[vertex]), Is.LessThan(.00001f));
                                Assert.That(Vector2.Distance(new Vector2(uvs[side][0][source].x, 1 - uvs[side][0][source].y), uv0[vertex]), Is.LessThan(.00001f));
                            }
                        }
                        var attributes = F.List(binding, "uv").Select(F.Object).ToArray();
                        Assert.That(attributes.Select(value => F.Int(value, "channel")), Is.EquivalentTo(Enumerable.Range(0, 8)));
                        foreach (var attribute in attributes)
                        {
                            var channel = F.Int(attribute, "channel");
                            var decoded = DecodeChunk(document, extension, F.Int(attribute, "payload"));
                            Assert.That(decoded.Length, Is.EqualTo(ImportedSources.Length * 16));
                            for (var vertex = 0; vertex < ImportedSources.Length; vertex++)
                                for (var component = 0; component < 4; component++)
                                    Assert.That(BitConverter.ToSingle(decoded, vertex * 16 + component * 4),
                                        Is.EqualTo(uvs[side][channel][ImportedSources[vertex]][component]));
                        }
                    }
                }
                fixture.AssertUnchanged(before);
                for (var side = 0; side < streams.Count; side++)
                {
                    Assert.That(fixture.Targets[side].GetComponent<MeshRenderer>().additionalVertexStreams, Is.SameAs(streams[side]));
                    Assert.That(EditorJsonUtility.ToJson(streams[side]), Is.EqualTo(beforeStreams[side]));
                }
            }
            finally
            {
                for (var side = 0; side < streams.Count; side++)
                { fixture.Targets[side].GetComponent<MeshRenderer>().additionalVertexStreams = null; Object.DestroyImmediate(streams[side]); }
            }
        }

        static byte[] DecodeChunk(GlbDocument document, Dictionary<string, object> extension, int payload)
        {
            var chunk = F.Object(F.List(extension, "chunks")[payload]);
            var view = F.Object(F.List(document.Json, "bufferViews")[F.Int(chunk, "bufferView")]);
            using var encoded = new MemoryStream(document.Binary, F.Int(view, "byteOffset"), F.Int(view, "byteLength"), false);
            using var decoder = new DeflateStream(encoded, CompressionMode.Decompress);
            using var decoded = new MemoryStream(); decoder.CopyTo(decoded);
            Assert.That(decoded.Length, Is.EqualTo(F.Int(chunk, "decodedBytes")));
            return decoded.ToArray();
        }

        [TestCase(false)] [TestCase(true)]
        public void MissingNormalsWithoutMorphsAlsoExportsThroughMeshWriter(bool skinned)
        {
            using var fixture = new Fixture(skinned, true, false);
            fixture.Mesh.ClearBlendShapes();
            var before = EditorJsonUtility.ToJson(fixture.Mesh);
            var bytes = fixture.ExportDirect();
            using (var data = new GlbBinaryParser(bytes, "missing normal without morph").Parse())
                foreach (var primitive in data.GLTF.meshes.SelectMany(mesh => mesh.primitives))
                {
                    Assert.That(primitive.attributes.NORMAL, Is.GreaterThanOrEqualTo(0));
                    var normals = data.GetArrayFromAccessor<Vector3>(primitive.attributes.NORMAL);
                    Assert.That(normals.Length, Is.EqualTo(3));
                    Assert.That(normals.All(normal => !float.IsNaN(normal.x) && !float.IsNaN(normal.y) && !float.IsNaN(normal.z)), Is.True);
                }
            Assert.That(EditorJsonUtility.ToJson(fixture.Mesh), Is.EqualTo(before));
            var target = fixture.Targets.Single();
            Assert.That(skinned ? target.GetComponent<SkinnedMeshRenderer>().sharedMesh : target.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(fixture.Mesh));
        }

        [TestCase(false)] [TestCase(true)]
        public void DegenerateGeometryReceivesFiniteUnitNormalsWithoutLosingMorphs(bool skinned)
        {
            using var fixture = new Fixture(skinned, true, false);
            var mesh = fixture.Mesh; var bindposes = mesh.bindposes;
            mesh.Clear(false);
            mesh.vertices = Enumerable.Repeat(new Vector3(0, 1.8f, 0), 3).ToArray();
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
            mesh.triangles = new[] { 0, 1, 2 };
            if (skinned)
            {
                mesh.bindposes = bindposes;
                mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 3).ToArray();
            }
            var vertices = Enumerable.Repeat(new Vector3(.01f, 0, 0), 3).ToArray();
            var normals = Enumerable.Repeat(new Vector3(.02f, .01f, 0), 3).ToArray();
            mesh.AddBlendShapeFrame("Degenerate detail", 100, vertices, normals, null);
            var target = fixture.Targets.Single();
            if (skinned) target.GetComponent<SkinnedMeshRenderer>().SetBlendShapeWeight(0, 25);
            Assert.That(mesh.normals, Is.Empty);
            var before = EditorJsonUtility.ToJson(mesh); var warnings = new List<string>();
            var bytes = fixture.ExportDirect(warnings);
            Assert.That(warnings, Is.Not.Empty);
            using (var data = new GlbBinaryParser(bytes, "degenerate missing normals").Parse())
            {
                var primitive = data.GLTF.meshes.Single().primitives.Single();
                var outputNormals = data.GetArrayFromAccessor<Vector3>(primitive.attributes.NORMAL);
                Assert.That(outputNormals.Length, Is.EqualTo(3));
                foreach (var normal in outputNormals)
                {
                    Assert.That(float.IsNaN(normal.x) || float.IsInfinity(normal.x) ||
                        float.IsNaN(normal.y) || float.IsInfinity(normal.y) || float.IsNaN(normal.z) || float.IsInfinity(normal.z), Is.False);
                    Assert.That(normal.magnitude, Is.EqualTo(1).Within(.00001f));
                }
                var morph = primitive.targets.Single();
                var deltaVertices = data.GetArrayFromAccessor<Vector3>(morph.POSITION);
                var deltaNormals = data.GetArrayFromAccessor<Vector3>(morph.NORMAL);
                Assert.That(deltaVertices.Length, Is.EqualTo(3)); Assert.That(deltaNormals.Length, Is.EqualTo(3));
                for (var vertex = 0; vertex < 3; vertex++)
                { AssertVector(Flip(vertices[vertex]), deltaVertices[vertex]); AssertVector(Flip(normals[vertex]), deltaNormals[vertex]); }
            }
            Assert.That(EditorJsonUtility.ToJson(mesh), Is.EqualTo(before));
            Assert.That(skinned ? target.GetComponent<SkinnedMeshRenderer>().sharedMesh : target.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(mesh));
            if (skinned) Assert.That(target.GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(25));
        }

        [TestCase(false)] [TestCase(true)]
        public void FailureDuringExportRestoresMissingAttributeSourceMesh(bool skinned)
        {
            using var fixture = new Fixture(skinned, true, true);
            var target = fixture.Targets.Single();
            Mesh streams = null, temporaryMesh = null;
            if (skinned) target.GetComponent<SkinnedMeshRenderer>().SetBlendShapeWeight(0, 35);
            else
            {
                streams = new Mesh { name = "Failure stream", vertices = fixture.Positions, normals = fixture.ExpectedNormals };
                target.GetComponent<MeshRenderer>().additionalVertexStreams = streams;
            }
            try
            {
                var before = fixture.Snapshot(); var beforeStreams = streams != null ? EditorJsonUtility.ToJson(streams) : null;
                var error = Assert.Throws<InvalidOperationException>(() => Vrm10AppearanceExporter.Export(new GltfExportSettings(),
                    fixture.Root, new BuiltInVrm10MaterialExporter(), new MobileTextureSerializer(null),
                    new VRM10ObjectMeta { Name = "Failing fixture", Version = "1", Authors = new List<string> { "Tests" } },
                    afterExport: (converter, model, storage) =>
                    {
                        temporaryMesh = skinned ? target.GetComponent<SkinnedMeshRenderer>().sharedMesh : target.GetComponent<MeshFilter>().sharedMesh;
                        Assert.That(temporaryMesh, Is.Not.SameAs(fixture.Mesh));
                        throw new InvalidOperationException("Intentional callback failure");
                    }));
                Assert.That(error.Message, Is.EqualTo("Intentional callback failure"));
                fixture.AssertUnchanged(before);
                Assert.That(temporaryMesh == null, Is.True, "The temporary repaired mesh must be destroyed even after a callback failure.");
                if (!skinned)
                {
                    Assert.That(target.GetComponent<MeshRenderer>().additionalVertexStreams, Is.SameAs(streams));
                    Assert.That(EditorJsonUtility.ToJson(streams), Is.EqualTo(beforeStreams));
                }
            }
            finally { if (streams != null) { target.GetComponent<MeshRenderer>().additionalVertexStreams = null; Object.DestroyImmediate(streams); } }
        }

        [TestCase(false, true, false, false)] [TestCase(false, false, true, false)] [TestCase(false, true, true, false)]
        [TestCase(true, true, false, false)] [TestCase(true, false, true, false)] [TestCase(true, true, true, false)]
        [TestCase(false, true, false, true)] [TestCase(false, false, true, true)] [TestCase(false, true, true, true)]
        [TestCase(true, true, false, true)] [TestCase(true, false, true, true)] [TestCase(true, true, true, true)]
        public async Task NormalExporterPreservesMissingAttributeMeshesThroughRealVrmRoundTrip(bool skinned, bool missingNormals, bool missingUv, bool fullLilToon)
        {
            using var fixture = new Fixture(skinned, missingNormals, missingUv);
            fixture.UseLilToon();
            if (skinned) fixture.Targets.Single().GetComponent<SkinnedMeshRenderer>().SetBlendShapeWeight(0, 35);
            var before = fixture.Snapshot();
            var warnings = new List<string>();
            var bytes = UniVrmOneClickExporter.Export(fixture.Root, "Missing mesh attributes", "Tests", warnings,
                exporterVersion: fullLilToon ? "mesh-attribute-regression" : null,
                lilToonVersion: fullLilToon ? "2.3.4" : null,
                blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
            if (fullLilToon) LilToonGlbExtension.Validate(bytes);
            await CheckOutput(fixture, bytes, skinned);
            fixture.AssertUnchanged(before);
        }

        static async Task CheckOutput(Fixture fixture, byte[] bytes, bool rebased, Vector3[] overrideNormals = null, Vector2[] overrideUv = null)
        {
            // Check the actual serialized primitives, including morph NORMAL.
            // Existing normals must not be recalculated just because UV is absent.
            using (var data = new GlbBinaryParser(bytes, "missing mesh attribute regression").Parse())
            {
                var nodes = data.GLTF.nodes.Where(node => fixture.Targets.Any(target => target.name == node.name)).ToArray();
                Assert.That(nodes.Length, Is.EqualTo(fixture.Targets.Count));
                foreach (var node in nodes)
                {
                    Assert.That(node.mesh, Is.GreaterThanOrEqualTo(0));
                    var mesh = data.GLTF.meshes[node.mesh];
                    Assert.That(mesh.primitives.Count, Is.EqualTo(2));
                    for (var submesh = 0; submesh < 2; submesh++)
                    {
                        var primitive = mesh.primitives[submesh];
                        Assert.That(primitive.attributes.NORMAL, Is.GreaterThanOrEqualTo(0));
                        Assert.That(primitive.attributes.TEXCOORD_0, Is.GreaterThanOrEqualTo(0));
                        Assert.That(primitive.attributes.COLOR_0, Is.GreaterThanOrEqualTo(0));
                        var positions = data.GetArrayFromAccessor<Vector3>(primitive.attributes.POSITION);
                        var normals = data.GetArrayFromAccessor<Vector3>(primitive.attributes.NORMAL);
                        var uv = data.GetArrayFromAccessor<Vector2>(primitive.attributes.TEXCOORD_0);
                        Assert.That(positions.Length, Is.EqualTo(3)); Assert.That(normals.Length, Is.EqualTo(3)); Assert.That(uv.Length, Is.EqualTo(3));
                        var morph = primitive.targets[0];
                        Assert.That(morph.NORMAL, Is.GreaterThanOrEqualTo(0), "An authored normal delta is not optional when only the base normal was missing.");
                        var deltaVertices = data.GetArrayFromAccessor<Vector3>(morph.POSITION);
                        var deltaNormals = data.GetArrayFromAccessor<Vector3>(morph.NORMAL);
                        for (var vertex = 0; vertex < 3; vertex++)
                        {
                            var source = PrimitiveSources[submesh][vertex];
                            var rest = rebased ? .35f : 0;
                            AssertVector(Flip(fixture.Positions[source] + fixture.PositionDeltas[source] * rest), positions[vertex]);
                            var normal = (overrideNormals ?? fixture.ExpectedNormals)[source] + (!fixture.MissingNormals ? fixture.NormalDeltas[source] * rest : Vector3.zero);
                            AssertVector(Flip(normal), normals[vertex]);
                            AssertVector(Flip(fixture.PositionDeltas[source] * (1 - rest)), deltaVertices[vertex]);
                            AssertVector(Flip(fixture.NormalDeltas[source] * (1 - rest)), deltaNormals[vertex]);
                        }
                    }
                }
            }
            Vrm10Instance imported = null;
            try
            {
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller(),
                    controlRigGenerationOption: ControlRigGenerationOption.None);
                _ = imported.Runtime; imported.enabled = false;
                foreach (var input in fixture.Targets)
                {
                    var renderer = imported.GetComponentsInChildren<SkinnedMeshRenderer>().Single(skin => skin.name == input.name);
                    var mesh = renderer.sharedMesh;
                    Assert.That(mesh.vertexCount, Is.EqualTo(ImportedSources.Length));
                    Assert.That(mesh.uv.Length, Is.EqualTo(mesh.vertexCount));
                    Assert.That(mesh.normals.Length, Is.EqualTo(mesh.vertexCount));
                    for (var vertex = 0; vertex < ImportedSources.Length; vertex++)
                    {
                        var expectedUv = overrideUv != null ? overrideUv[ImportedSources[vertex]] : fixture.MissingUv ? Vector2.zero : fixture.Uv[ImportedSources[vertex]];
                        Assert.That(Vector2.Distance(mesh.uv[vertex], expectedUv), Is.LessThan(.00001f));
                    }
                    var shape = mesh.GetBlendShapeIndex("Authored detail"); Assert.That(shape, Is.GreaterThanOrEqualTo(0));
                    foreach (var weight in new[] { 0f, 100f, 0f })
                    {
                        renderer.SetBlendShapeWeight(shape, weight);
                        var baked = new Mesh();
                        try
                        {
                            renderer.BakeMesh(baked, false);
                            var rest = rebased ? .35f : 0;
                            for (var vertex = 0; vertex < ImportedSources.Length; vertex++)
                            {
                                var source = ImportedSources[vertex];
                                var expected = fixture.Positions[source] + fixture.PositionDeltas[source] * (rest + (1 - rest) * weight / 100);
                                AssertVector(input.transform.TransformPoint(expected), renderer.transform.TransformPoint(baked.vertices[vertex]));
                            }
                        }
                        finally { Object.DestroyImmediate(baked); }
                    }
                }
            }
            finally
            {
                if (imported != null) { imported.DisposeRuntime(); Object.DestroyImmediate(imported.gameObject); }
            }
        }

        static Vector3 Flip(Vector3 value) => new Vector3(-value.x, value.y, value.z);
        static void AssertVector(Vector3 expected, Vector3 actual)
            => Assert.That(Vector3.Distance(expected, actual), Is.LessThan(.00005f));

        // Full snapshots require a portable MToon material alongside the
        // original lilToon record. Supply one without editing the source.
        sealed class PortableMaterialExporter : IMaterialExporter
        {
            readonly Material fallback;
            internal PortableMaterialExporter(Material fallback) { this.fallback = fallback; }
            public glTFMaterial ExportMaterial(Material material, ITextureExporter textureExporter, GltfExportSettings settings)
                => new BuiltInVrm10MaterialExporter().ExportMaterial(fallback, textureExporter, settings);
        }

        sealed class Fixture : IDisposable
        {
            readonly AttachmentConnectionTests.Fixture avatar = new AttachmentConnectionTests.Fixture();
            internal GameObject Root => avatar.Source;
            internal readonly Mesh Mesh;
            internal readonly List<GameObject> Targets = new List<GameObject>();
            internal readonly bool MissingNormals, MissingUv;
            internal readonly Vector3[] Positions, PositionDeltas, NormalDeltas, ExpectedNormals;
            internal readonly Vector2[] Uv;
            internal Fixture(bool skinned, bool missingNormals, bool missingUv, bool uv1Only = false, bool twin = false)
            {
                MissingNormals = missingNormals; MissingUv = missingUv;
                Object.DestroyImmediate(Root.transform.Find("Back").gameObject);
                var target = Root.transform.Find("Front").gameObject;
                target.name = "Missing attribute surface"; Targets.Add(target);
                Mesh = new Mesh { name = "Authored attribute fixture" };
                Positions = new[] { new Vector3(-.1f, 1.7f, .08f), new Vector3(.1f, 1.7f, .08f), new Vector3(-.1f, 1.9f, .08f), new Vector3(.1f, 1.9f, .08f) };
                Uv = new[] { new Vector2(.1f, .2f), new Vector2(.3f, .4f), new Vector2(.5f, .6f), new Vector2(.7f, .8f) };
                Mesh.vertices = Positions;
                if (!missingNormals) Mesh.normals = Enumerable.Range(0, 4).Select(index => new Vector3(.1f * index, .1f, 1).normalized).ToArray();
                if (!missingUv) Mesh.uv = Uv;
                if (uv1Only) Mesh.uv2 = new[] { new Vector2(.2f, .3f), new Vector2(.4f, .5f), new Vector2(.6f, .7f), new Vector2(.8f, .9f) };
                Mesh.colors = new[] { Color.red, Color.green, Color.blue, Color.white };
                Mesh.tangents = Enumerable.Repeat(new Vector4(1, 0, 0, -1), 4).ToArray();
                Mesh.subMeshCount = 2; Mesh.SetTriangles(new[] { 3, 1, 0 }, 0); Mesh.SetTriangles(new[] { 2, 3, 1 }, 1);
                PositionDeltas = Enumerable.Repeat(new Vector3(.02f, .01f, 0), 4).ToArray();
                NormalDeltas = Enumerable.Range(0, 4).Select(index => new Vector3(.02f + .01f * index, .005f, 0)).ToArray();
                Mesh.AddBlendShapeFrame("Authored detail", 50, PositionDeltas.Select(value => value * .5f).ToArray(), NormalDeltas.Select(value => value * .5f).ToArray(), null);
                Mesh.AddBlendShapeFrame("Authored detail", 100, PositionDeltas, NormalDeltas, null);
                Assert.That(Mesh.normals.Length, Is.EqualTo(missingNormals ? 0 : Positions.Length));
                Assert.That(Mesh.uv.Length, Is.EqualTo(missingUv ? 0 : Positions.Length));
                var renderer = target.GetComponent<SkinnedMeshRenderer>(); var material = renderer.sharedMaterial;
                if (skinned)
                {
                    Mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 4).ToArray();
                    Mesh.bindposes = avatar.Mesh.bindposes;
                    renderer.sharedMesh = Mesh; renderer.sharedMaterials = new[] { material, material }; renderer.SetBlendShapeWeight(0, 0);
                }
                else
                {
                    Object.DestroyImmediate(renderer);
                    target.AddComponent<MeshFilter>().sharedMesh = Mesh;
                    target.AddComponent<MeshRenderer>().sharedMaterials = new[] { material, material };
                }
                Mesh.RecalculateBounds();
                var expected = Object.Instantiate(Mesh);
                try { if (missingNormals) expected.RecalculateNormals(); ExpectedNormals = expected.normals; }
                finally { Object.DestroyImmediate(expected); }
                if (twin)
                {
                    var copy = Object.Instantiate(target, Root.transform); copy.name = "Shared missing attribute surface";
                    copy.transform.localPosition = Vector3.right * .2f; Targets.Add(copy);
                }
            }
            internal void UseLilToon()
            {
                var shader = Shader.Find("lilToon"); if (shader == null) Assert.Ignore("Install lilToon.");
                foreach (var target in Targets) target.GetComponent<Renderer>().sharedMaterial.shader = shader;
            }
            internal (string Mesh, float[] Weights) Snapshot() => (EditorJsonUtility.ToJson(Mesh),
                Targets.Select(target => target.GetComponent<SkinnedMeshRenderer>()).Where(skin => skin != null)
                    .Select(skin => skin.GetBlendShapeWeight(0)).ToArray());
            internal void AssertUnchanged((string Mesh, float[] Weights) before)
            {
                var after = Snapshot();
                Assert.That(after.Mesh, Is.EqualTo(before.Mesh)); Assert.That(after.Weights, Is.EqualTo(before.Weights));
                foreach (var target in Targets)
                {
                    var skin = target.GetComponent<SkinnedMeshRenderer>();
                    Assert.That(skin != null ? skin.sharedMesh : target.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(Mesh));
                }
                Assert.That(Mesh.normals.Length, Is.EqualTo(MissingNormals ? 0 : 4));
                Assert.That(Mesh.uv.Length, Is.EqualTo(MissingUv ? 0 : 4));
                Assert.That(Mesh.blendShapeCount, Is.EqualTo(1)); Assert.That(Mesh.GetBlendShapeFrameCount(0), Is.EqualTo(2));
            }
            internal byte[] ExportDirect(ICollection<string> warnings = null) => Vrm10AppearanceExporter.Export(new GltfExportSettings { ExportVertexColor = true },
                Root, new BuiltInVrm10MaterialExporter(), new MobileTextureSerializer(null),
                new VRM10ObjectMeta { Name = "Missing attribute fixture", Version = "1", Authors = new List<string> { "Tests" } },
                afterExport: (converter, model, storage) => Assert.That(converter.Meshes.ContainsKey(Mesh), Is.True, "The bridge must restore original source-mesh identity."), warnings: warnings);
            public void Dispose() { avatar.Dispose(); Object.DestroyImmediate(Mesh); }
        }
    }
}
