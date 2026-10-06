using System;
using System.Collections.Generic;
using UniVRM10;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter
{
    // The pinned ModelExporter only reads sharedMesh, ignoring additional
    // vertex streams. It also cannot export a morph mesh without base normals.
    // Keep effective meshes assigned until all exporter callbacks have read
    // their attributes, then restore every source reference even on failure.
    internal sealed class VrmMeshAttributePreparation : IDisposable
    {
        sealed class Entry
        {
            internal Renderer Renderer;
            internal MeshFilter Filter;
            internal Mesh Source, Prepared;
            internal float[] Weights;
            internal bool Assigned;
        }

        readonly List<Entry> entries = new List<Entry>();

        internal static VrmMeshAttributePreparation Prepare(GameObject root, ICollection<string> warnings)
        {
            var scope = new VrmMeshAttributePreparation();
            try
            {
                foreach (var renderer in root.GetComponentsInChildren<Renderer>())
                {
                    if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                    var skin = renderer as SkinnedMeshRenderer;
                    var meshRenderer = renderer as MeshRenderer;
                    var filter = meshRenderer != null ? renderer.GetComponent<MeshFilter>() : null;
                    var source = skin != null ? skin.sharedMesh : filter != null ? filter.sharedMesh : null;
                    if (!ExportRendererSelection.HasGeometry(source)) continue;
                    var stream = meshRenderer != null ? meshRenderer.additionalVertexStreams : null;
                    if (stream == null && source.HasVertexAttribute(VertexAttribute.Normal)) continue;

                    var entry = new Entry { Renderer = renderer, Filter = filter, Source = source };
                    scope.entries.Add(entry);
                    entry.Prepared = Object.Instantiate(source);
                    entry.Prepared.name = source.name;
                    entry.Prepared.hideFlags = HideFlags.HideAndDontSave;
                    if (stream != null) ApplyAdditionalStream(entry.Prepared, stream, renderer.name);
                    if (!entry.Prepared.HasVertexAttribute(VertexAttribute.Normal))
                    {
                        entry.Prepared.RecalculateNormals();
                        var normals = entry.Prepared.normals;
                        if (normals.Length != source.vertexCount)
                            throw new InvalidOperationException(renderer.name + ": 未設定の頂点法線を生成できませんでした。");
                        var zeroNormals = 0;
                        for (var i = 0; i < normals.Length; i++)
                        {
                            var normal = normals[i];
                            if (!Finite(normal.x) || !Finite(normal.y) || !Finite(normal.z))
                                throw new InvalidOperationException(renderer.name + ": 頂点法線の生成結果に非有限値が含まれています。");
                            // Degenerate/cancelling faces cannot define a
                            // direction. Keep their geometry and morphs, but
                            // do not emit invalid zero-length glTF normals.
                            if (normal.x == 0 && normal.y == 0 && normal.z == 0)
                            { normals[i] = Vector3.forward; zeroNormals++; }
                        }
                        if (zeroNormals > 0)
                        {
                            entry.Prepared.normals = normals;
                            warnings?.Add(renderer.name + ": 面から方向を決められない頂点法線を+Z方向で補いました。形状と表情は保持しますが、陰影は近似になります。");
                        }
                        warnings?.Add(renderer.name + ": 未設定の頂点法線を面の形状から生成しました。照明や輪郭の見え方が変わる場合があります。");
                    }
                    if (skin != null)
                    {
                        entry.Weights = new float[source.blendShapeCount];
                        for (var i = 0; i < entry.Weights.Length; i++) entry.Weights[i] = skin.GetBlendShapeWeight(i);
                    }
                    entry.Assigned = true;
                    Assign(entry, entry.Prepared);
                }
                return scope;
            }
            catch
            {
                scope.Dispose();
                throw;
            }
        }

        static void ApplyAdditionalStream(Mesh destination, Mesh stream, string rendererName)
        {
            if (stream.vertexCount != destination.vertexCount)
                throw new InvalidOperationException(rendererName + ": 追加頂点ストリームと元メッシュの頂点数が一致しません。");
            // Only attributes are overridden; indices, submeshes, bind poses,
            // blend-shape frames and topology always come from the base mesh.
            if (stream.HasVertexAttribute(VertexAttribute.Position)) destination.vertices = stream.vertices;
            if (stream.HasVertexAttribute(VertexAttribute.Normal)) destination.normals = stream.normals;
            if (stream.HasVertexAttribute(VertexAttribute.Tangent)) destination.tangents = stream.tangents;
            if (stream.HasVertexAttribute(VertexAttribute.Color)) destination.colors = stream.colors;
            for (var channel = 0; channel < 8; channel++)
            {
                var attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
                if (!stream.HasVertexAttribute(attribute)) continue;
                // Preserve Z/W used by custom shaders and the full-lilToon
                // extension. A one-component UV has an implicit zero Y.
                var dimension = stream.GetVertexAttributeDimension(attribute);
                if (dimension <= 2)
                {
                    var values = new List<Vector2>(); stream.GetUVs(channel, values);
                    destination.SetUVs(channel, values);
                }
                else if (dimension == 3)
                {
                    var values = new List<Vector3>(); stream.GetUVs(channel, values);
                    destination.SetUVs(channel, values);
                }
                else
                {
                    var values = new List<Vector4>(); stream.GetUVs(channel, values);
                    destination.SetUVs(channel, values);
                }
            }
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        internal void RestoreSourceMeshKeys(ModelExporter converter)
        {
            foreach (var entry in entries)
            {
                if (!converter.Meshes.TryGetValue(entry.Prepared, out var group)) continue;
                converter.Meshes.Remove(entry.Prepared);
                // Like ModelExporter's shared static-mesh map, this dictionary
                // can represent only the first use of a shared source. Nodes
                // retain each renderer's exact group, including distinct AVS.
                if (!converter.Meshes.ContainsKey(entry.Source)) converter.Meshes.Add(entry.Source, group);
            }
        }

        static void Assign(Entry entry, Mesh mesh)
        {
            if (entry.Renderer is SkinnedMeshRenderer skin)
            {
                skin.sharedMesh = mesh;
                for (var i = 0; i < entry.Weights.Length; i++) skin.SetBlendShapeWeight(i, entry.Weights[i]);
            }
            else if (entry.Filter != null) entry.Filter.sharedMesh = mesh;
        }

        public void Dispose()
        {
            foreach (var entry in entries)
            {
                if (entry.Assigned && entry.Renderer != null) Assign(entry, entry.Source);
                if (entry.Prepared != null) Object.DestroyImmediate(entry.Prepared);
            }
            entries.Clear();
        }
    }
}
