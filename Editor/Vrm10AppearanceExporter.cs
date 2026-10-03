using System;
using System.Collections.Generic;
using System.Linq;
using UniGLTF;
using UniVRM10;
using UnityEngine;
using VrmLib;

namespace VRVlog.LilToonExporter
{
    internal static class Vrm10AppearanceExporter
    {
        internal static byte[] Export(GltfExportSettings settings, GameObject avatar,
            IMaterialExporter materialExporter, ITextureSerializer textureSerializer, VRM10ObjectMeta vrmMeta,
            IDictionary<Material, int> materialIndices = null,
            Action<ModelExporter, Model, ExportingGltfData> afterExport = null)
        {
            using var arrays = new NativeArrayManager();
            var converter = new ModelExporter();
            var model = converter.Export(settings, arrays, avatar);
            PreserveMorphNormals(converter, arrays, settings);
            model.ConvertCoordinate(Coordinates.Vrm1);
            using var exporter = new Vrm10Exporter(settings, materialExporter, textureSerializer);
            exporter.Export(avatar, model, converter, new ExportArgs { removeMorphNormal = settings.ExportOnlyBlendShapePosition }, vrmMeta);
            // Vrm10Exporter emits materials in model.Materials order. Preserve
            // object identity for extension injection, even with duplicate names.
            if (materialIndices != null)
                for (var i = 0; i < model.Materials.Count; i++) materialIndices.Add((Material)model.Materials[i], i);
            if (settings.ExportVertexColor) PreserveVertexColors(model, exporter.Storage);
            afterExport?.Invoke(converter, model, exporter.Storage);
            return exporter.Storage.ToGlbBytes();
        }

        // The pinned ModelExporter computes useNormal but only copies each
        // morph's POSITION buffer. Fill NORMAL from its exact source Mesh map
        // while both buffers still use Unity coordinates and source vertex order.
        // MeshWriter subsequently applies its own submesh index remapping.
        private static void PreserveMorphNormals(ModelExporter converter, NativeArrayManager arrays, GltfExportSettings settings)
        {
            if (settings.ExportOnlyBlendShapePosition) return;
            foreach (var pair in converter.Meshes)
            {
                var source = pair.Key;
                var group = pair.Value;
                if (group.Meshes.Count != 1 || group.Meshes[0].VertexBuffer.Count != source.vertexCount ||
                    group.Meshes[0].MorphTargets.Count != source.blendShapeCount)
                    throw new InvalidOperationException("UniVRMの元メッシュとMorphTargetの対応が一致しないため、表情の法線を安全に保存できません。");
                if (source.normals.Length != source.vertexCount) continue;
                var mesh = group.Meshes[0];
                var vertices = new Vector3[source.vertexCount];
                var normals = new Vector3[source.vertexCount];
                for (var shape = 0; shape < source.blendShapeCount; shape++)
                {
                    var target = mesh.MorphTargets[shape];
                    var frames = source.GetBlendShapeFrameCount(shape);
                    if (frames == 0 || target.Name != source.GetBlendShapeName(shape) || target.VertexBuffer.Count != source.vertexCount)
                        throw new InvalidOperationException("UniVRMの元BlendShapeとMorphTargetの対応が一致しないため、表情の法線を安全に保存できません: " + source.name);
                    source.GetBlendShapeFrameVertices(shape, frames - 1, vertices, normals, null);
                    var buffer = arrays.CreateNativeArray(normals).Reinterpret<byte>(12);
                    target.VertexBuffer.Add(VertexBuffer.NormalKey,
                        new BufferAccessor(arrays, buffer, AccessorValueType.FLOAT, AccessorVectorType.VEC3, normals.Length));
                }
            }
        }

        // UniVRM 0.131 ModelExporter retains COLOR_0 but MeshWriter's divided
        // primitive path drops it. Use the model/primitive correspondence while
        // it is still available, never mesh names or nearest-position matching.
        internal static void PreserveVertexColors(Model model, ExportingGltfData storage)
        {
            if (model.MeshGroups.Count != storage.Gltf.meshes.Count)
                throw new InvalidOperationException("UniVRMのメッシュ対応が変更されています。頂点カラーを安全に保存できません。");
            for (var meshIndex = 0; meshIndex < model.MeshGroups.Count; meshIndex++)
            {
                var group = model.MeshGroups[meshIndex];
                if (group.Meshes.Count != 1) throw new InvalidOperationException("UniVRMのメッシュ構成に対応していません。");
                var mesh = group.Meshes[0];
                if (mesh.VertexBuffer.Colors == null) continue;
                var colors = mesh.VertexBuffer.Colors.GetSpan<Color>();
                var indices = mesh.IndexBuffer.GetAsIntArray();
                var primitives = storage.Gltf.meshes[meshIndex].primitives;
                if (primitives.Count != mesh.Submeshes.Count) throw new InvalidOperationException("頂点カラーのサブメッシュ対応が一致しません。");
                for (var submeshIndex = 0; submeshIndex < mesh.Submeshes.Count; submeshIndex++)
                {
                    var submesh = mesh.Submeshes[submeshIndex];
                    // Same ascending source-index order as 0.131 MeshWriter.
                    var used = new SortedSet<int>();
                    for (var i = submesh.Offset; i < submesh.Offset + submesh.DrawCount; i++) used.Add(indices[i]);
                    var output = used.Select(i => colors[i]).ToArray();
                    var attributes = primitives[submeshIndex].attributes;
                    if (storage.Gltf.accessors[attributes.POSITION].count != output.Length)
                        throw new InvalidOperationException("頂点カラーと出力形状の頂点数が一致しません。");
                    attributes.COLOR_0 = storage.ExtendBufferAndGetAccessorIndex(output, glBufferTarget.ARRAY_BUFFER);
                }
            }
        }
    }
}
