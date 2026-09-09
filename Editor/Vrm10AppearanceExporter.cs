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
            model.ConvertCoordinate(Coordinates.Vrm1);
            using var exporter = new Vrm10Exporter(settings, materialExporter, textureSerializer);
            exporter.Export(avatar, model, converter, new ExportArgs(), vrmMeta);
            // Vrm10Exporter emits materials in model.Materials order. Preserve
            // object identity for extension injection, even with duplicate names.
            if (materialIndices != null)
                for (var i = 0; i < model.Materials.Count; i++) materialIndices.Add((Material)model.Materials[i], i);
            if (settings.ExportVertexColor) PreserveVertexColors(model, exporter.Storage);
            afterExport?.Invoke(converter, model, exporter.Storage);
            return exporter.Storage.ToGlbBytes();
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
