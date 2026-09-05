using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // VRM expressions reset their bound morphs to zero every frame. Store the
    // authored rest face in geometry, with morphs relative to that rest face.
    internal static class AvatarBaseShape
    {
        internal static void Preserve(GameObject source, GameObject clone, ICollection<Mesh> temporaryMeshes, ICollection<string> warnings)
        {
            foreach (var renderer in ExportRendererSelection.Enumerate(source))
            {
                if (!(renderer is SkinnedMeshRenderer skin) || skin.sharedMesh == null) continue;
                var mesh = skin.sharedMesh;
                var weights = new float[mesh.blendShapeCount];
                var changed = false;
                for (var i = 0; i < weights.Length; i++)
                {
                    weights[i] = skin.GetBlendShapeWeight(i);
                    if (float.IsNaN(weights[i]) || float.IsInfinity(weights[i]))
                        throw new InvalidOperationException($"{skin.name}: BlendShapeの初期値が不正です。");
                    changed |= weights[i] != 0f;
                }
                if (!changed) continue;

                // Match hierarchy indices and component order, not names: customized
                // avatars can contain duplicate renderer names and shared meshes.
                var route = new Stack<int>();
                for (var t = skin.transform; t != source.transform; t = t.parent) route.Push(t.GetSiblingIndex());
                var target = clone.transform;
                while (route.Count > 0) target = target.GetChild(route.Pop());
                var index = Array.IndexOf(skin.GetComponents<SkinnedMeshRenderer>(), skin);
                var copy = target.GetComponents<SkinnedMeshRenderer>()[index];
                var baked = UnityEngine.Object.Instantiate(mesh);
                temporaryMeshes.Add(baked); // also owned if conversion fails
                Rebase(mesh, baked, weights);
                copy.sharedMesh = baked;
                for (var i = 0; i < weights.Length; i++) copy.SetBlendShapeWeight(i, 0f);
                warnings?.Add($"{skin.name}: 調整済みBlendShapeを基本の顔・体形として保存しました。表情はこの形から変化します。");
            }
        }

        internal static void Rebase(Mesh source, Mesh target, float[] weights)
        {
            if (ReferenceEquals(source, target)) throw new ArgumentException("The source mesh must remain unchanged.");
            if (weights.Length != source.blendShapeCount) throw new ArgumentException("BlendShape weight count mismatch.");
            var vertices = source.vertices;
            var normals = source.normals;
            var tangents = source.tangents;
            target.ClearBlendShapes();
            for (var shape = 0; shape < weights.Length; shape++)
            {
                var rest = Evaluate(source, shape, weights[shape]);
                // UniVRM emits one morph target per shape. Keep its existing final
                // frame endpoint, subtracting just this shape's authored offset.
                // A half-closed default eye can then close fully without doubling.
                var frame = source.GetBlendShapeFrameCount(shape) - 1;
                if (frame < 0) throw new InvalidOperationException("BlendShape has no frames.");
                var end = new Deltas(vertices.Length);
                source.GetBlendShapeFrameVertices(shape, frame, end.Vertices, end.Normals, end.Tangents);
                for (var v = 0; v < vertices.Length; v++)
                {
                    vertices[v] += rest.Vertices[v];
                    if (normals.Length > 0) normals[v] += rest.Normals[v];
                    if (tangents.Length > 0)
                    {
                        var tangent = tangents[v];
                        tangent.x += rest.Tangents[v].x;
                        tangent.y += rest.Tangents[v].y;
                        tangent.z += rest.Tangents[v].z;
                        tangents[v] = tangent; // preserve handedness (w)
                    }
                    end.Vertices[v] -= rest.Vertices[v];
                    end.Normals[v] -= rest.Normals[v];
                    end.Tangents[v] -= rest.Tangents[v];
                }
                // Preserve shape order/names, including zero residuals: VRM clips
                // refer to these indices. Do not bake skinning or modify bind poses.
                target.AddBlendShapeFrame(source.GetBlendShapeName(shape), 100f, end.Vertices, end.Normals, end.Tangents);
            }
            target.vertices = vertices;
            if (normals.Length > 0) target.normals = normals;
            if (tangents.Length > 0) target.tangents = tangents;
            target.RecalculateBounds();
        }

        // One composed expression is one residual morph per renderer. Computing
        // absolute source deltas before subtracting the authored rest also handles
        // partial weights, negative weights and restoring a customized shape to 0.
        internal static void AppendExpression(Mesh source, Mesh target, string name, float[] rest, float[] expression)
        {
            if (ReferenceEquals(source, target)) throw new ArgumentException("The source mesh must remain unchanged.");
            if (rest.Length != source.blendShapeCount || expression.Length != rest.Length)
                throw new ArgumentException("BlendShape weight count mismatch.");
            var delta = new Deltas(source.vertexCount);
            for (var shape = 0; shape < rest.Length; shape++)
            {
                if (float.IsNaN(rest[shape]) || float.IsInfinity(rest[shape]) || float.IsNaN(expression[shape]) || float.IsInfinity(expression[shape]))
                    throw new InvalidOperationException("表情のBlendShape値が不正です。");
                if (rest[shape] == expression[shape]) continue;
                var before = Evaluate(source, shape, rest[shape]);
                var after = Evaluate(source, shape, expression[shape]);
                for (var v = 0; v < source.vertexCount; v++)
                {
                    delta.Vertices[v] += after.Vertices[v] - before.Vertices[v];
                    delta.Normals[v] += after.Normals[v] - before.Normals[v];
                    delta.Tangents[v] += after.Tangents[v] - before.Tangents[v];
                }
            }
            target.AddBlendShapeFrame(name, 100f, delta.Vertices, delta.Normals, delta.Tangents);
        }

        sealed class Deltas
        {
            internal readonly Vector3[] Vertices, Normals, Tangents;
            internal Deltas(int count)
            {
                Vertices = new Vector3[count];
                Normals = new Vector3[count];
                Tangents = new Vector3[count];
            }
        }

        internal static void AppendAnimatedShape(Mesh source, Mesh target, string name, int shape, double initial, double weight)
        {
            if (ReferenceEquals(source, target)) throw new ArgumentException("The source mesh must remain unchanged.");
            var before = Evaluate(source, shape, initial);
            var after = Evaluate(source, shape, weight);
            for (var v = 0; v < source.vertexCount; v++)
            {
                after.Vertices[v] -= before.Vertices[v];
                after.Normals[v] -= before.Normals[v];
                after.Tangents[v] -= before.Tangents[v];
            }
            target.AddBlendShapeFrame(name, 100f, after.Vertices, after.Normals, after.Tangents);
        }

        static Deltas Evaluate(Mesh mesh, int shape, double weight)
        {
            var result = new Deltas(mesh.vertexCount);
            if (weight == 0f) return result;
            var knots = new List<KeyValuePair<float, int>> { new KeyValuePair<float, int>(0f, -1) };
            for (var i = 0; i < mesh.GetBlendShapeFrameCount(shape); i++)
            {
                var frameWeight = mesh.GetBlendShapeFrameWeight(shape, i);
                if (frameWeight == 0f || float.IsNaN(frameWeight) || float.IsInfinity(frameWeight))
                    throw new InvalidOperationException($"{mesh.name}/{mesh.GetBlendShapeName(shape)}: 初期値の保存に対応していないBlendShapeフレームです（重み{frameWeight}）。");
                knots.Add(new KeyValuePair<float, int>(frameWeight, i));
            }
            knots.Sort((a, b) => a.Key.CompareTo(b.Key));
            if (knots.Count < 2) throw new InvalidOperationException("BlendShape has no frames.");
            var right = 1;
            while (right < knots.Count - 1 && weight > knots[right].Key) right++;
            var left = knots[right - 1];
            var upper = knots[right];
            var lowerDelta = new Deltas(mesh.vertexCount);
            if (left.Value >= 0) mesh.GetBlendShapeFrameVertices(shape, left.Value, lowerDelta.Vertices, lowerDelta.Normals, lowerDelta.Tangents);
            if (upper.Value >= 0) mesh.GetBlendShapeFrameVertices(shape, upper.Value, result.Vertices, result.Normals, result.Tangents);
            var t = (float)((weight - left.Key) / (upper.Key - left.Key));
            for (var v = 0; v < mesh.vertexCount; v++)
            {
                result.Vertices[v] = Vector3.LerpUnclamped(lowerDelta.Vertices[v], result.Vertices[v], t);
                result.Normals[v] = Vector3.LerpUnclamped(lowerDelta.Normals[v], result.Normals[v], t);
                result.Tangents[v] = Vector3.LerpUnclamped(lowerDelta.Tangents[v], result.Tangents[v], t);
            }
            return result;
        }
    }
}
