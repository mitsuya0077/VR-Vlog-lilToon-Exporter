using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class AvatarOptimizerVertexBufferTests
    {
        static Type Installed(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(name)).FirstOrDefault(type => type != null);

        [Test]
        public void InstalledAvatarOptimizerRepeatedlyReadsUnreadableVertexStreamsWithoutChangingSourceBuffers()
        {
            if (Installed("Anatawa12.AvatarOptimizer.TraceAndOptimize") == null)
                Assert.Ignore("Install Avatar Optimizer for the real vertex-buffer readback regression.");
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("Avatar Optimizer vertex-buffer readback requires a graphics device.");

            var meshInfo = Installed("Anatawa12.AvatarOptimizer.Processors.SkinnedMeshes.MeshInfo2");
            Assert.That(meshInfo, Is.Not.Null, "Installed AAO must expose its mesh-processing assembly.");
            var readBuffers = meshInfo.GetMethod("GetVertexBuffers", BindingFlags.Static | BindingFlags.NonPublic,
                null, new[] { typeof(Mesh) }, null);
            Assert.That(readBuffers, Is.Not.Null, "The pinned AAO GetVertexBuffers(Mesh) method is required.");

            var positions = new[] {
                new Vector3(-1.25f, .5f, 0),
                new Vector3(1.5f, -.75f, .25f),
                new Vector3(.125f, 2, -.5f),
            };
            var colors = new[] {
                new Color32(1, 17, 231, 255),
                new Color32(203, 0, 91, 127),
                new Color32(42, 254, 3, 64),
            };
            var positionValues = positions.SelectMany(position => new[] { position.x, position.y, position.z }).ToArray();
            var positionBytes = new byte[positionValues.Length * sizeof(float)];
            Buffer.BlockCopy(positionValues, 0, positionBytes, 0, positionBytes.Length);
            var expected = new[] {
                positionBytes,
                colors.SelectMany(color => new[] { color.r, color.g, color.b, color.a }).ToArray(),
            };
            var strides = new[] { 12, 4 };

            var mesh = new Mesh { name = "Owned AAO two-stream GPU readback fixture" };
            try
            {
                mesh.SetVertexBufferParams(positions.Length,
                    new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
                    new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4, 1));
                mesh.SetVertexBufferData(positions, 0, 0, positions.Length, 0, MeshUpdateFlags.DontRecalculateBounds);
                mesh.SetVertexBufferData(colors, 0, 0, colors.Length, 1, MeshUpdateFlags.DontRecalculateBounds);
                mesh.subMeshCount = 1;
                mesh.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 0, false);
                mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 6);
                Assert.That(mesh.vertexBufferCount, Is.EqualTo(2));
                mesh.UploadMeshData(true);
                Assert.That(mesh.isReadable, Is.False, "Readback must use GPU data after Unity discards the CPU copy.");

                // Invoke AAO itself; cleanup of its acquired handles remains AAO's responsibility.
                // The native allocation gate runs this case with stack tracing, then reloads the domain.
                for (var iteration = 0; iteration < 8; iteration++)
                {
                    var buffers = readBuffers.Invoke(null, new object[] { mesh }) as Array;
                    Assert.That(buffers, Is.Not.Null);
                    Assert.That(buffers.Length, Is.EqualTo(2));
                    for (var stream = 0; stream < buffers.Length; stream++)
                    {
                        var tuple = buffers.GetValue(stream);
                        var tupleType = tuple.GetType();
                        var dataField = tupleType.GetField("Item1");
                        var strideField = tupleType.GetField("Item2");
                        Assert.That(dataField, Is.Not.Null);
                        Assert.That(strideField, Is.Not.Null);
                        Assert.That(strideField.GetValue(tuple), Is.EqualTo(strides[stream]),
                            $"AAO iteration {iteration}, stream {stream} stride");
                        Assert.That(dataField.GetValue(tuple), Is.EqualTo(expected[stream]),
                            $"AAO iteration {iteration}, stream {stream} copied bytes");
                    }
                    Assert.That(mesh.isReadable, Is.False);
                }

                // Disposing AAO's wrappers must leave the source mesh's GPU buffers usable.
                for (var stream = 0; stream < expected.Length; stream++)
                {
                    using var verifier = mesh.GetVertexBuffer(stream);
                    Assert.That(verifier.IsValid(), Is.True);
                    Assert.That(verifier.stride, Is.EqualTo(strides[stream]));
                    Assert.That(verifier.count, Is.EqualTo(positions.Length));
                    var data = new byte[verifier.count * verifier.stride];
                    verifier.GetData(data);
                    Assert.That(data, Is.EqualTo(expected[stream]), $"Source stream {stream} remains unchanged after AAO readback");
                }
                Assert.That(mesh.isReadable, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }
    }
}
