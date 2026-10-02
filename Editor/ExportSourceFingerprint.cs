using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter
{
    // Source validity must not depend on a JSON dump of all referenced assets.
    // The native Hash128 digest consumes deterministic bounded chunks; meshes use native binary data and one
    // reusable set of delta arrays, independent of the number of blendshapes.
    internal static class ExportSourceFingerprint
    {
        internal static Hash128 Compute(GameObject source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            using var hash = new Digest();
            hash.Boolean(source.activeInHierarchy);
            hash.Matrix(source.transform.localToWorldMatrix);
            var visited = new HashSet<Object>(); var assets = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<Object>(source.GetComponentsInChildren<Component>(true).Cast<Object>());
            foreach (var transform in source.GetComponentsInChildren<Transform>(true)) queue.Enqueue(transform.gameObject);
            while (queue.Count != 0)
            {
                var value = queue.Dequeue();
                if (value == null) { hash.Integer(0); continue; }
                if (!visited.Add(value)) continue;
                hash.Integer(value.GetInstanceID()); hash.Text(value.GetType().FullName);
                hash.Text(value.name); hash.Integer((int)value.hideFlags);
                var path = AssetDatabase.GetAssetPath(value);
                if (!string.IsNullOrEmpty(path)) assets.Add(path);
                if (value is Mesh mesh) { MeshData(hash, mesh); continue; }
                if (value is Texture texture) { TextureData(hash, texture); continue; }
                // Shader/script bytes belong to their dependency hash. Their
                // compiled payload is not an editable serialized avatar input.
                if (value is Shader || value is MonoScript) continue;
                SerializedData(hash, value, queue);
            }
            foreach (var path in assets.OrderBy(path => path, StringComparer.Ordinal))
            { hash.Text(path); hash.Text(AssetDatabase.GetAssetDependencyHash(path).ToString()); }
            foreach (var path in new[] { "ProjectSettings/lilToonSetting.json", "Packages/manifest.json", "Packages/packages-lock.json" })
            {
                hash.Text(path);
                var fullPath = Path.Combine(Application.dataPath, "..", path);
                hash.Boolean(File.Exists(fullPath));
                if (File.Exists(fullPath)) hash.File(fullPath);
            }
            return hash.Finish();
        }

        static void MeshData(Digest hash, Mesh mesh)
        {
            hash.Integer(mesh.vertexCount); hash.Integer((int)mesh.indexFormat); hash.Boolean(mesh.isReadable); hash.Bounds(mesh.bounds);
            var attributes = mesh.GetVertexAttributes(); hash.Integer(attributes.Length);
            foreach (var attribute in attributes)
            { hash.Integer((int)attribute.attribute); hash.Integer((int)attribute.format); hash.Integer(attribute.dimension); hash.Integer(attribute.stream); }
            // Editor access also supports imported meshes with Read/Write off.
            using (var snapshot = MeshUtility.AcquireReadOnlyMeshData(mesh))
            {
                var data = snapshot[0]; hash.Integer(data.vertexBufferCount);
                for (var stream = 0; stream < data.vertexBufferCount; stream++)
                { hash.Integer(data.GetVertexBufferStride(stream)); hash.Native(data.GetVertexData<byte>(stream)); }
                hash.Native(data.GetIndexData<byte>()); hash.Integer(data.subMeshCount);
                for (var submesh = 0; submesh < data.subMeshCount; submesh++)
                {
                    var descriptor = data.GetSubMesh(submesh);
                    hash.Integer(descriptor.indexStart); hash.Integer(descriptor.indexCount); hash.Integer((int)descriptor.topology);
                    hash.Integer(descriptor.baseVertex); hash.Integer(descriptor.firstVertex); hash.Integer(descriptor.vertexCount); hash.Bounds(descriptor.bounds);
                }
            }
            var poses = mesh.bindposes; hash.Integer(poses.Length); foreach (var pose in poses) hash.Matrix(pose);
            // These are non-owning views of the Mesh, not temporary allocations.
            var counts = mesh.GetBonesPerVertex(); var weights = mesh.GetAllBoneWeights();
            hash.Native(counts); hash.Integer(weights.Length);
            for (var i = 0; i < weights.Length; i++) { hash.Integer(weights[i].boneIndex); hash.Float(weights[i].weight); }
            hash.Integer(mesh.blendShapeCount);
            if (mesh.blendShapeCount == 0) return;
            var vertices = new Vector3[mesh.vertexCount]; var normals = new Vector3[mesh.vertexCount]; var tangents = new Vector3[mesh.vertexCount];
            using var vertexBytes = new PinnedVectors(vertices);
            using var normalBytes = new PinnedVectors(normals);
            using var tangentBytes = new PinnedVectors(tangents);
            for (var shape = 0; shape < mesh.blendShapeCount; shape++)
            {
                hash.Text(mesh.GetBlendShapeName(shape)); var frames = mesh.GetBlendShapeFrameCount(shape); hash.Integer(frames);
                for (var frame = 0; frame < frames; frame++)
                {
                    hash.Float(mesh.GetBlendShapeFrameWeight(shape, frame));
                    mesh.GetBlendShapeFrameVertices(shape, frame, vertices, normals, tangents);
                    hash.Vectors(vertexBytes); hash.Vectors(normalBytes); hash.Vectors(tangentBytes);
                }
            }
        }

        static void TextureData(Digest hash, Texture texture)
        {
            hash.Integer(texture.width); hash.Integer(texture.height); hash.Integer((int)texture.dimension);
            hash.Integer((int)texture.graphicsFormat); hash.Integer((int)texture.filterMode);
            hash.Integer((int)texture.wrapModeU); hash.Integer((int)texture.wrapModeV); hash.Integer((int)texture.wrapModeW);
            hash.Integer(texture.anisoLevel); hash.Float(texture.mipMapBias);
            if (texture is Texture2D image)
            { hash.Integer(image.mipmapCount); hash.Boolean(image.isReadable); hash.Boolean(image.streamingMipmaps); }
            if (texture is Cubemap cube) { hash.Integer(cube.mipmapCount); hash.Boolean(cube.isReadable); }
            if (texture is Texture3D volume)
            { hash.Integer(volume.depth); hash.Integer(volume.mipmapCount); hash.Boolean(volume.isReadable); }
            if (texture is Texture2DArray array)
            { hash.Integer(array.depth); hash.Integer(array.mipmapCount); hash.Boolean(array.isReadable); }
            if (texture is CubemapArray cubes)
            { hash.Integer(cubes.cubemapCount); hash.Integer(cubes.mipmapCount); hash.Boolean(cubes.isReadable); }
            if (texture is WebCamTexture camera)
            {
                hash.Text(camera.deviceName); hash.Integer(camera.requestedWidth); hash.Integer(camera.requestedHeight);
                hash.Float(camera.requestedFPS);
            }
            if (texture is RenderTexture render)
            {
                var descriptor = render.descriptor;
                hash.Integer(descriptor.volumeDepth); hash.Integer(descriptor.msaaSamples); hash.Integer(descriptor.mipCount);
                hash.Integer((int)descriptor.depthStencilFormat); hash.Integer((int)descriptor.stencilFormat);
                hash.Integer((int)descriptor.memoryless); hash.Integer((int)descriptor.vrUsage);
                hash.Boolean(descriptor.sRGB); hash.Boolean(descriptor.useMipMap); hash.Boolean(descriptor.autoGenerateMips);
                hash.Boolean(descriptor.enableRandomWrite); hash.Boolean(descriptor.bindMS); hash.Boolean(descriptor.useDynamicScale);
            }
            // Preserve the existing distinction: live camera/render outputs can change
            // frame-by-frame, whereas static image changes invalidate a preview.
            if (texture is Texture2D || texture is Cubemap || texture is Texture3D || texture is Texture2DArray || texture is CubemapArray)
            {
                hash.Text(texture.imageContentsHash.ToString()); hash.Unsigned(texture.updateCount);
                // Writable native CPU views can change before Apply without
                // advancing either signal. Hash those views without copying
                // their payload into managed arrays or making them readable.
                if (texture is Texture2D readableImage && readableImage.isReadable)
                    hash.Native(readableImage.GetRawTextureData<byte>());
                else if (texture is Cubemap readableCube && readableCube.isReadable)
                    for (var mip = 0; mip < readableCube.mipmapCount; mip++)
                    for (var face = 0; face < 6; face++) hash.Native(readableCube.GetPixelData<byte>(mip, (CubemapFace)face));
                else if (texture is Texture3D readableVolume && readableVolume.isReadable)
                    for (var mip = 0; mip < readableVolume.mipmapCount; mip++) hash.Native(readableVolume.GetPixelData<byte>(mip));
                else if (texture is Texture2DArray readableArray && readableArray.isReadable)
                    for (var mip = 0; mip < readableArray.mipmapCount; mip++)
                    for (var layer = 0; layer < readableArray.depth; layer++) hash.Native(readableArray.GetPixelData<byte>(mip, layer));
                else if (texture is CubemapArray readableCubes && readableCubes.isReadable)
                    for (var mip = 0; mip < readableCubes.mipmapCount; mip++)
                    for (var layer = 0; layer < readableCubes.cubemapCount; layer++)
                    for (var face = 0; face < 6; face++) hash.Native(readableCubes.GetPixelData<byte>(mip, (CubemapFace)face, layer));
            }
        }

        static void SerializedData(Digest hash, Object value, Queue<Object> queue)
        {
            using var serialized = new SerializedObject(value);
            using var property = serialized.GetIterator();
            var managed = new HashSet<long>(); var enterChildren = true;
            while (property.Next(enterChildren))
            {
                enterChildren = property.propertyType == SerializedPropertyType.Generic || property.propertyType == SerializedPropertyType.ExposedReference;
                hash.Text(property.propertyPath); hash.Integer((int)property.propertyType);
                switch (property.propertyType)
                {
                    case SerializedPropertyType.Generic: break; // children carry their own type/value
                    case SerializedPropertyType.Integer: hash.Long(property.longValue); break;
                    case SerializedPropertyType.Boolean: hash.Boolean(property.boolValue); break;
                    case SerializedPropertyType.Float: hash.Double(property.doubleValue); break;
                    case SerializedPropertyType.String: hash.Text(property.stringValue); break;
                    case SerializedPropertyType.Color: hash.Vector((Vector4)property.colorValue); break;
                    case SerializedPropertyType.ObjectReference: Reference(hash, property.objectReferenceValue, queue); break;
                    case SerializedPropertyType.ExposedReference: Reference(hash, property.exposedReferenceValue, queue); break;
                    case SerializedPropertyType.LayerMask:
                    case SerializedPropertyType.Enum:
                    case SerializedPropertyType.ArraySize:
                    case SerializedPropertyType.Character: hash.Integer(property.intValue); break;
                    case SerializedPropertyType.Vector2: hash.Vector(property.vector2Value); break;
                    case SerializedPropertyType.Vector3: hash.Vector(property.vector3Value); break;
                    case SerializedPropertyType.Vector4: hash.Vector(property.vector4Value); break;
                    case SerializedPropertyType.Rect: var rect = property.rectValue; hash.Vector(rect.position); hash.Vector(rect.size); break;
                    case SerializedPropertyType.AnimationCurve:
                        var curve = property.animationCurveValue; hash.Boolean(curve != null); if (curve == null) break;
                        hash.Integer((int)curve.preWrapMode); hash.Integer((int)curve.postWrapMode); hash.Integer(curve.length);
                        for (var i = 0; i < curve.length; i++)
                        {
                            var key = curve[i]; hash.Float(key.time); hash.Float(key.value); hash.Float(key.inTangent); hash.Float(key.outTangent);
                            hash.Float(key.inWeight); hash.Float(key.outWeight); hash.Integer((int)key.weightedMode);
                            hash.Integer((int)AnimationUtility.GetKeyLeftTangentMode(curve, i)); hash.Integer((int)AnimationUtility.GetKeyRightTangentMode(curve, i));
                            hash.Boolean(AnimationUtility.GetKeyBroken(curve, i));
                        }
                        break;
                    case SerializedPropertyType.Bounds: hash.Bounds(property.boundsValue); break;
                    case SerializedPropertyType.Gradient:
                        var gradient = property.gradientValue; hash.Boolean(gradient != null); if (gradient == null) break;
                        hash.Integer((int)gradient.mode); hash.Integer((int)gradient.colorSpace); var colors = gradient.colorKeys; hash.Integer(colors.Length);
                        foreach (var key in colors) { hash.Float(key.time); hash.Vector((Vector4)key.color); }
                        var alphas = gradient.alphaKeys; hash.Integer(alphas.Length);
                        foreach (var key in alphas) { hash.Float(key.time); hash.Float(key.alpha); } break;
                    case SerializedPropertyType.Quaternion: var rotation = property.quaternionValue; hash.Vector(new Vector4(rotation.x, rotation.y, rotation.z, rotation.w)); break;
                    case SerializedPropertyType.FixedBufferSize: hash.Integer(property.fixedBufferSize); break;
                    case SerializedPropertyType.Vector2Int: var v2 = property.vector2IntValue; hash.Integer(v2.x); hash.Integer(v2.y); break;
                    case SerializedPropertyType.Vector3Int: var v3 = property.vector3IntValue; hash.Integer(v3.x); hash.Integer(v3.y); hash.Integer(v3.z); break;
                    case SerializedPropertyType.RectInt: var ri = property.rectIntValue; hash.Integer(ri.x); hash.Integer(ri.y); hash.Integer(ri.width); hash.Integer(ri.height); break;
                    case SerializedPropertyType.BoundsInt: var bi = property.boundsIntValue; hash.Integer(bi.position.x); hash.Integer(bi.position.y); hash.Integer(bi.position.z); hash.Integer(bi.size.x); hash.Integer(bi.size.y); hash.Integer(bi.size.z); break;
                    case SerializedPropertyType.ManagedReference:
                        hash.Text(property.managedReferenceFullTypename); hash.Long(property.managedReferenceId);
                        enterChildren = property.managedReferenceId >= 0 && managed.Add(property.managedReferenceId); break;
                    case SerializedPropertyType.Hash128: hash.Text(property.hash128Value.ToString()); break;
                    default: throw new NotSupportedException("Unsupported source fingerprint property: " + property.propertyType);
                }
            }
        }

        static void Reference(Digest hash, Object reference, Queue<Object> queue)
        {
            hash.Integer(reference == null ? 0 : reference.GetInstanceID());
            if (reference != null && !(reference is Component) && !(reference is GameObject)) queue.Enqueue(reference);
        }

        internal sealed class PinnedVectors : IDisposable
        {
            GCHandle handle;
            internal readonly IntPtr Data;
            internal readonly int Length;
            internal PinnedVectors(Vector3[] values)
            {
                Length = checked(values.Length * Marshal.SizeOf<Vector3>());
                handle = GCHandle.Alloc(values, GCHandleType.Pinned);
                Data = handle.AddrOfPinnedObject();
            }
            public void Dispose() { if (handle.IsAllocated) handle.Free(); }
        }

        internal sealed class Digest : IDisposable
        {
            Hash128 algorithm;
            readonly byte[] bytes = new byte[8192]; readonly char[] chars = new char[1024];
            readonly Encoder encoder = Encoding.UTF8.GetEncoder(); int count;
            void Flush() { if (count == 0) return; algorithm.Append(bytes, 0, count); count = 0; }
            void Byte(byte value) { if (count == bytes.Length) Flush(); bytes[count++] = value; }
            internal void Boolean(bool value) => Byte(value ? (byte)1 : (byte)0);
            internal void Integer(int value) => Unsigned(unchecked((uint)value));
            internal void Unsigned(uint value) { for (var i = 0; i < 4; i++) Byte((byte)(value >> (i * 8))); }
            internal void Long(long value) { var bits = unchecked((ulong)value); for (var i = 0; i < 8; i++) Byte((byte)(bits >> (i * 8))); }
            internal void Float(float value) => Integer(BitConverter.SingleToInt32Bits(value));
            internal void Double(double value) => Long(BitConverter.DoubleToInt64Bits(value));
            internal void Vector(Vector2 value) { Float(value.x); Float(value.y); }
            internal void Vector(Vector3 value) { Float(value.x); Float(value.y); Float(value.z); }
            internal void Vector(Vector4 value) { Float(value.x); Float(value.y); Float(value.z); Float(value.w); }
            internal void Matrix(Matrix4x4 value) { for (var i = 0; i < 16; i++) Float(value[i]); }
            internal void Bounds(Bounds value) { Vector(value.center); Vector(value.extents); }
            internal void Text(string value)
            {
                Boolean(value != null); if (value == null) return; Integer(Encoding.UTF8.GetByteCount(value)); encoder.Reset();
                for (var offset = 0; offset < value.Length;)
                {
                    var length = Math.Min(chars.Length, value.Length - offset); value.CopyTo(offset, chars, 0, length); offset += length;
                    var consumed = 0;
                    while (consumed < length)
                    {
                        if (bytes.Length - count < 4) Flush();
                        encoder.Convert(chars, consumed, length - consumed, bytes, count, bytes.Length - count, offset == value.Length,
                            out var read, out var written, out var completed);
                        consumed += read; count += written;
                    }
                }
            }
            internal void Native(NativeArray<byte> data)
            {
                Integer(data.Length);
                for (var offset = 0; offset < data.Length;)
                {
                    if (count == bytes.Length) Flush(); var length = Math.Min(bytes.Length - count, data.Length - offset);
                    NativeArray<byte>.Copy(data, offset, bytes, count, length); count += length; offset += length;
                }
            }
            internal void Vectors(PinnedVectors data)
            {
                Integer(data.Length);
                for (var offset = 0; offset < data.Length;)
                {
                    if (count == bytes.Length) Flush(); var length = Math.Min(bytes.Length - count, data.Length - offset);
                    Marshal.Copy(IntPtr.Add(data.Data, offset), bytes, count, length); count += length; offset += length;
                }
            }
            internal void File(string path)
            {
                using var stream = System.IO.File.OpenRead(path); Long(stream.Length); Flush();
                // Native Append is seeded per block. Fill each block even if
                // Read returns short, so identical files have identical framing.
                while (true)
                {
                    var filled = 0; int read;
                    while (filled < bytes.Length && (read = stream.Read(bytes, filled, bytes.Length - filled)) != 0) filled += read;
                    if (filled == 0) break;
                    algorithm.Append(bytes, 0, filled);
                }
            }
            internal Hash128 Finish()
            {
                Flush(); return algorithm;
            }
            public void Dispose() { }
        }
    }
}
