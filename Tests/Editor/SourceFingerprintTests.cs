using System;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class SourceFingerprintTests
    {
        [TestCase("vertices")][TestCase("normals")][TestCase("tangents")][TestCase("colors")]
        [TestCase("uv0")][TestCase("uv7")][TestCase("layout")][TestCase("indices")]
        [TestCase("submesh")][TestCase("bounds")][TestCase("bindpose")][TestCase("boneIndex")][TestCase("boneWeight")]
        [TestCase("morphVertex")][TestCase("morphNormal")][TestCase("morphTangent")]
        [TestCase("frameWeight")][TestCase("shapeName")][TestCase("frameCount")]
        public void UnsavedMeshInputsInvalidateTheStamp(string change)
        {
            using var fixture = new MeshFixture(3, 2);
            var stamp = ExportRecoverySourceStamp.Capture(fixture.Source);
            Assert.That(stamp.Matches(fixture.Source), Is.True);
            var mesh = fixture.Mesh;
            switch (change)
            {
                case "vertices": var vertices = mesh.vertices; vertices[0].x += .1f; mesh.vertices = vertices; break;
                case "normals": var normals = mesh.normals; normals[0] = Vector3.up; mesh.normals = normals; break;
                case "tangents": var tangents = mesh.tangents; tangents[0].w = -1; mesh.tangents = tangents; break;
                case "colors": var colors = mesh.colors; colors[0] = Color.red; mesh.colors = colors; break;
                case "uv0": var uv = mesh.uv; uv[0].x += .25f; mesh.uv = uv; break;
                case "uv7": mesh.SetUVs(7, new[] { Vector4.one, Vector4.zero, Vector4.zero }); break;
                case "layout": mesh.SetUVs(0, new[] { Vector4.zero, Vector4.one, Vector4.zero }); break;
                case "indices": mesh.triangles = new[] { 2, 1, 0 }; break;
                case "submesh": mesh.SetIndices(new[] { 0, 1 }, MeshTopology.Lines, 0); break;
                case "bounds": mesh.bounds = new Bounds(Vector3.one, Vector3.one * 2); break;
                case "bindpose": var poses = mesh.bindposes; poses[0] = Matrix4x4.Translate(Vector3.up); mesh.bindposes = poses; break;
                case "boneIndex": fixture.SetSkinning(true, false); break;
                case "boneWeight": fixture.SetSkinning(false, true); break;
                default: fixture.SetShapes(2, change); break;
            }
            Assert.That(stamp.Matches(fixture.Source), Is.False, change);
            Assert.That(ExportRecoverySourceStamp.Capture(fixture.Source).Matches(fixture.Source), Is.True, "A new stamp accepts the changed native state.");
        }

        [Test]
        public void LargeFaceHashUsesOneMeshSizedWorkspaceAndNeverMutatesTheSource()
        {
            using var fixture = new MeshFixture(10000, 4);
            ExportRecoverySourceStamp.Capture(fixture.Source); // initialize Editor/hash machinery outside calibration
            // Some Unity Mono versions expose this API as a zero-returning stub.
            // Retain a real 1 MiB allocation before treating it as a measurement.
            var beforeCalibration = GC.GetAllocatedBytesForCurrentThread();
            var retained = new byte[1024 * 1024]; retained[0] = 1; retained[retained.Length - 1] = 1;
            var calibration = GC.GetAllocatedBytesForCurrentThread() - beforeCalibration; GC.KeepAlive(retained);
            var hasAllocationCounter = calibration >= retained.Length;
            var beforeSmall = GC.GetAllocatedBytesForCurrentThread(); ExportRecoverySourceStamp.Capture(fixture.Source);
            var small = GC.GetAllocatedBytesForCurrentThread() - beforeSmall;
            fixture.SetShapes(104);
            var vertices = fixture.Mesh.vertices; var bounds = fixture.Mesh.bounds;
            var beforeLarge = GC.GetAllocatedBytesForCurrentThread(); var stamp = ExportRecoverySourceStamp.Capture(fixture.Source);
            var large = GC.GetAllocatedBytesForCurrentThread() - beforeLarge;
            if (hasAllocationCounter)
            {
                TestContext.WriteLine("Calibrated managed allocations: 4 morphs=" + small + " bytes; 104 morphs=" + large + " bytes.");
                Assert.That(large, Is.LessThan(8L * 1024 * 1024), "Fingerprinting does not create a whole-mesh JSON string.");
                Assert.That(large, Is.LessThan(small + 2L * 1024 * 1024), "Adding100 morphs must not allocate100 vertex-sized workspaces or aggregate payload strings.");
            }
            else TestContext.WriteLine("Managed allocation counter unavailable: retained 1 MiB calibration reported " + calibration + " bytes. Allocation assertions skipped; large native-mesh correctness/source-preservation checks still run.");
            Assert.That(stamp.Matches(fixture.Source), Is.True);
            Assert.That(fixture.Mesh.vertices, Is.EqualTo(vertices)); Assert.That(fixture.Mesh.bounds, Is.EqualTo(bounds));
            Assert.That(fixture.Skin.sharedMesh, Is.SameAs(fixture.Mesh)); Assert.That(fixture.Mesh.blendShapeCount, Is.EqualTo(104));
            Assert.That(fixture.Skin.GetBlendShapeWeight(0), Is.Zero); Assert.That(fixture.Mesh.GetBlendShapeFrameWeight(0, 0), Is.Zero);
            var delta = new Vector3[fixture.Mesh.vertexCount]; var normal = new Vector3[delta.Length]; var tangent = new Vector3[delta.Length];
            fixture.Mesh.GetBlendShapeFrameVertices(0, 0, delta, normal, tangent);
            Assert.That(delta.All(value => value == Vector3.zero) && normal.All(value => value == Vector3.zero) && tangent.All(value => value == Vector3.zero), Is.True);
            Assert.That(fixture.Mesh.GetBlendShapeFrameWeight(0, 1), Is.EqualTo(100));
            fixture.Mesh.GetBlendShapeFrameVertices(0, 1, delta, normal, tangent); Assert.That(delta[0].y, Is.EqualTo(.01f));
        }

        [TestCase(0)][TestCase(2)]
        public void EditorFingerprintSupportsReadWriteDisabledMeshWithoutReenablingIt(int shapes)
        {
            using var fixture = new MeshFixture(3, shapes);
            fixture.Mesh.UploadMeshData(true); Assert.That(fixture.Mesh.isReadable, Is.False);
            var stamp = ExportRecoverySourceStamp.Capture(fixture.Source);
            Assert.That(stamp.Matches(fixture.Source), Is.True); Assert.That(fixture.Mesh.isReadable, Is.False);
        }

        [TestCase("tailWeight")][TestCase("tailIndex")][TestCase("influenceCount")]
        public void BoneInfluencesBeyondTheFirstFourRemainTracked(string change)
        {
            using var fixture = new MeshFixture(3, 0, false);
            var bones = new Transform[6];
            for (var i = 0; i < bones.Length; i++)
            {
                var bone = new GameObject("Influence " + i).transform; bone.SetParent(fixture.Source.transform, false); bones[i] = bone;
            }
            fixture.Skin.bones = bones; fixture.Mesh.bindposes = Enumerable.Repeat(Matrix4x4.identity, 6).ToArray();
            SetManyWeights(fixture.Mesh, null);
            var stamp = ExportRecoverySourceStamp.Capture(fixture.Source); Assert.That(stamp.Matches(fixture.Source), Is.True);
            var initial = fixture.Mesh.GetAllBoneWeights().ToArray(); Assert.That(fixture.Mesh.GetBonesPerVertex()[0], Is.EqualTo(6));
            SetManyWeights(fixture.Mesh, change);
            var changed = fixture.Mesh.GetAllBoneWeights().ToArray();
            for (var i = 0; i < 4; i++) { Assert.That(changed[i].boneIndex, Is.EqualTo(initial[i].boneIndex)); Assert.That(changed[i].weight, Is.EqualTo(initial[i].weight)); }
            Assert.That(stamp.Matches(fixture.Source), Is.False, "A lower influence must not be lost by a four-weight-only fingerprint.");
        }

        [Test]
        public void BulkMorphBytesPreserveFloatPayloadBitsAndLengthFraming()
        {
            // Include signed zero, distinct NaN payloads and infinities without
            // asking Mesh to validate those artificial geometry values.
            var values = new[] { new Vector3(BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)),
                BitConverter.Int32BitsToSingle(0x7fc00001), float.PositiveInfinity), new Vector3(1, -1, float.NegativeInfinity) };
            using var serialized = new MemoryStream();
            using (var writer = new BinaryWriter(serialized, System.Text.Encoding.UTF8, true))
            {
                writer.Write(values.Length * 12);
                foreach (var value in values) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); }
            }
            using var sha = SHA256.Create(); var expected = sha.ComputeHash(serialized.ToArray());
            using var pinned = new ExportSourceFingerprint.PinnedVectors(values);
            using var hash = new ExportSourceFingerprint.Digest(); hash.Vectors(pinned);
            Assert.That(hash.Finish(), Is.EqualTo(new Hash128(BitConverter.ToUInt32(expected, 0), BitConverter.ToUInt32(expected, 4),
                BitConverter.ToUInt32(expected, 8), BitConverter.ToUInt32(expected, 12))));
        }

        [Test]
        public void HundredMorphsAtHundredThousandVerticesRemainCorrectAndReportNativeTiming()
        {
            // Sparse native targets bound fixture storage; the API still fills
            // and hashes every vertex/normal/tangent slot of every frame.
            using var fixture = new MeshFixture(100000, 100);
            var watch = Stopwatch.StartNew(); var stamp = ExportRecoverySourceStamp.Capture(fixture.Source);
            watch.Stop(); var capture = watch.Elapsed;
            watch.Restart(); Assert.That(stamp.Matches(fixture.Source), Is.True); watch.Stop();
            TestContext.WriteLine("Native 100,000-vertex / 100-morph source: Capture=" + capture.TotalMilliseconds.ToString("F1")
                + " ms; Matches=" + watch.Elapsed.TotalMilliseconds.ToString("F1") + " ms. Timings are observed, not a platform-independent bound.");
            Assert.That(fixture.Skin.sharedMesh, Is.SameAs(fixture.Mesh)); Assert.That(fixture.Mesh.blendShapeCount, Is.EqualTo(100));
            fixture.SetShapes(100, "morphNormal");
            Assert.That(stamp.Matches(fixture.Source), Is.False, "Bulk bytes preserve normal-only target changes at realistic scale.");
        }

        static void SetManyWeights(Mesh mesh, string change)
        {
            var influences = change == "influenceCount" ? 5 : 6;
            using var counts = new NativeArray<byte>(Enumerable.Repeat((byte)influences, mesh.vertexCount).ToArray(), Allocator.Temp);
            var weights = new NativeArray<BoneWeight1>(mesh.vertexCount * influences, Allocator.Temp);
            try
            {
                var values = new[] { .3f, .25f, .2f, .15f, .06f, .04f };
                for (var vertex = 0; vertex < mesh.vertexCount; vertex++)
                for (var bone = 0; bone < influences; bone++)
                {
                    var weight = influences == 5 && bone == 4 ? .1f : change == "tailWeight" && bone == 4 ? .055f : change == "tailWeight" && bone == 5 ? .045f : values[bone];
                    var index = change == "tailIndex" && bone >= 4 ? 9 - bone : bone;
                    weights[vertex * influences + bone] = new BoneWeight1 { boneIndex = index, weight = weight };
                }
                mesh.SetBoneWeights(counts, weights);
            }
            finally { weights.Dispose(); }
        }

        [TestCase("volume")][TestCase("array")][TestCase("cubeArray")]
        public void StaticTextureContentUpdatesInvalidateTheStamp(string kind)
        {
            var source = new GameObject("Static texture input");
            Texture texture = kind == "volume" ? new Texture3D(2, 2, 2, TextureFormat.RGBA32, false)
                : kind == "array" ? (Texture)new Texture2DArray(2, 2, 2, TextureFormat.RGBA32, false)
                : new CubemapArray(2, 2, TextureFormat.RGBA32, false);
            try
            {
                source.AddComponent<SourceFingerprintTextureProbe>().Texture = texture;
                ApplyStaticTexture(texture, Color.black);
                var stamp = ExportRecoverySourceStamp.Capture(source);
                Assert.That(stamp.Matches(source), Is.True);
                ApplyStaticTexture(texture, Color.red);
                Assert.That(stamp.Matches(source), Is.False, "A static non-2D texture content change remains an input change.");
                Assert.That(ExportRecoverySourceStamp.Capture(source).Matches(source), Is.True);
            }
            finally { Object.DestroyImmediate(source); Object.DestroyImmediate(texture); }
        }

        static void ApplyStaticTexture(Texture texture, Color color)
        {
            if (texture is Texture3D volume) { volume.SetPixels(Enumerable.Repeat(color, 8).ToArray()); volume.Apply(false); }
            else if (texture is Texture2DArray array) { array.SetPixels(Enumerable.Repeat(color, 4).ToArray(), 1); array.Apply(false); }
            else if (texture is CubemapArray cubes) { cubes.SetPixels(Enumerable.Repeat(color, 4).ToArray(), CubemapFace.PositiveX, 1); cubes.Apply(false); }
        }

        [TestCase("image")][TestCase("cube")][TestCase("volume")][TestCase("array")][TestCase("cubeArray")]
        public void UnappliedNativeCpuTextureEditsInvalidateTheStamp(string kind)
        {
            var source = new GameObject("Native texture input");
            Texture texture = kind == "image" ? new Texture2D(4, 4, TextureFormat.RGBA32, true)
                : kind == "cube" ? (Texture)new Cubemap(4, TextureFormat.RGBA32, true)
                : kind == "volume" ? new Texture3D(4, 4, 4, TextureFormat.RGBA32, true)
                : kind == "array" ? (Texture)new Texture2DArray(4, 4, 2, TextureFormat.RGBA32, true)
                : new CubemapArray(4, 2, TextureFormat.RGBA32, true);
            try
            {
                source.AddComponent<SourceFingerprintTextureProbe>().Texture = texture;
                var stamp = ExportRecoverySourceStamp.Capture(source); Assert.That(stamp.Matches(source), Is.True);
                var updateCount = texture.updateCount; var contentsHash = texture.imageContentsHash;
                // The last mip and a non-first layer/face prove the complete
                // CPU layout is covered, including changes before Apply.
                var data = texture is Texture2D image ? image.GetRawTextureData<byte>()
                    : texture is Cubemap cube ? cube.GetPixelData<byte>(cube.mipmapCount - 1, CubemapFace.NegativeZ)
                    : texture is Texture3D volume ? volume.GetPixelData<byte>(volume.mipmapCount - 1)
                    : texture is Texture2DArray array ? array.GetPixelData<byte>(array.mipmapCount - 1, 1)
                    : ((CubemapArray)texture).GetPixelData<byte>(((CubemapArray)texture).mipmapCount - 1, CubemapFace.NegativeZ, 1);
                data[data.Length - 1] ^= 255;
                Assert.That(texture.updateCount, Is.EqualTo(updateCount)); Assert.That(texture.imageContentsHash, Is.EqualTo(contentsHash));
                Assert.That(stamp.Matches(source), Is.False, "A writable CPU view is an input change before Apply.");
                Assert.That(ExportRecoverySourceStamp.Capture(source).Matches(source), Is.True);
            }
            finally { Object.DestroyImmediate(source); Object.DestroyImmediate(texture); }
        }

        sealed class MeshFixture : IDisposable
        {
            internal readonly GameObject Source = new GameObject("Fingerprint source");
            internal readonly Mesh Mesh = new Mesh { name = "Unsaved tracking mesh" };
            internal readonly SkinnedMeshRenderer Skin;
            internal MeshFixture(int vertices, int shapes, bool initializeSkinning = true)
            {
                Mesh.vertices = Enumerable.Range(0, vertices).Select(i => new Vector3(i * .00001f, 0, 0)).ToArray();
                Mesh.normals = Enumerable.Repeat(Vector3.forward, vertices).ToArray(); Mesh.tangents = Enumerable.Repeat(new Vector4(1, 0, 0, 1), vertices).ToArray();
                Mesh.colors = Enumerable.Repeat(Color.white, vertices).ToArray(); Mesh.uv = Enumerable.Repeat(Vector2.zero, vertices).ToArray();
                Mesh.triangles = new[] { 0, 1, 2 }; Mesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity };
                Skin = Source.AddComponent<SkinnedMeshRenderer>();
                var boneA = new GameObject("Bone A").transform; boneA.SetParent(Source.transform, false);
                var boneB = new GameObject("Bone B").transform; boneB.SetParent(Source.transform, false);
                Skin.bones = new[] { boneA, boneB }; Skin.rootBone = boneA; Skin.sharedMesh = Mesh;
                if (initializeSkinning) SetSkinning(false, false); SetShapes(shapes);
            }
            internal void SetSkinning(bool index, bool weight)
            {
                using var counts = new NativeArray<byte>(Enumerable.Repeat((byte)2, Mesh.vertexCount).ToArray(), Allocator.Temp);
                var weights = new NativeArray<BoneWeight1>(Mesh.vertexCount * 2, Allocator.Temp);
                try
                {
                    for (var i = 0; i < Mesh.vertexCount; i++)
                    {
                        weights[i * 2] = new BoneWeight1 { boneIndex = index ? 1 : 0, weight = weight ? .7f : .6f };
                        weights[i * 2 + 1] = new BoneWeight1 { boneIndex = index ? 0 : 1, weight = weight ? .3f : .4f };
                    }
                    Mesh.SetBoneWeights(counts, weights);
                }
                finally { weights.Dispose(); }
            }
            internal void SetShapes(int count, string change = null)
            {
                Mesh.ClearBlendShapes();
                var delta = new Vector3[Mesh.vertexCount]; var normal = new Vector3[delta.Length]; var tangent = new Vector3[delta.Length];
                for (var shape = 0; shape < count; shape++)
                {
                    Array.Clear(delta, 0, delta.Length); Array.Clear(normal, 0, normal.Length); Array.Clear(tangent, 0, tangent.Length);
                    var name = shape == 0 && change == "shapeName" ? "Changed face channel" : "Face channel " + shape;
                    if (shape == 0 && change != "frameCount") Mesh.AddBlendShapeFrame(name, 0, delta, normal, tangent);
                    delta[0] = Vector3.up * (shape == 0 && change == "morphVertex" ? .02f : .01f);
                    normal[0] = shape == 0 && change == "morphNormal" ? Vector3.up : Vector3.zero;
                    tangent[0] = shape == 0 && change == "morphTangent" ? Vector3.right : Vector3.zero;
                    Mesh.AddBlendShapeFrame(name, shape == 0 && change == "frameWeight" ? 75 : 100, delta, normal, tangent);
                }
            }
            public void Dispose() { Object.DestroyImmediate(Source); Object.DestroyImmediate(Mesh); }
        }
    }
}
