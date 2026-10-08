using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // VRM expressions reset their bound morphs to zero every frame. Store the
    // authored rest face in geometry, with morphs relative to that rest face.
    internal static class AvatarBaseShape
    {
        // A native renderer keeps the unbounded Animator scalar, but legacy
        // mesh evaluation uses zero and the final authored frame as its bounds.
        // The first frame is an interpolation knot, not the lower bound. Keep
        // this policy separate from explicit VRM/profile endpoint arithmetic.
        internal static double ClampSourceWeight(Mesh mesh, int shape, double weight)
        {
            var frame = mesh.GetBlendShapeFrameCount(shape) - 1;
            if (frame < 0) throw new InvalidOperationException("BlendShape has no frames.");
            var maximum = mesh.GetBlendShapeFrameWeight(shape, frame);
            if (!Finite(maximum) || double.IsNaN(weight) || double.IsInfinity(weight))
                throw new InvalidOperationException("表情のBlendShape値が不正です。");
            // Unity evaluates a lone negative frame at the undeformed base when
            // legacy clamping is enabled. Multiple negative frames still use
            // their final authored frame as the upper bound.
            if (frame == 0 && maximum < 0f) return 0d;
            return Math.Min(maximum, Math.Max(0d, weight));
        }

        internal static void NormalizeSourceWeights(GameObject prepared, bool clampToSourceRange)
        {
            if (!clampToSourceRange) return;
            foreach (var renderer in ExportRendererSelection.Enumerate(prepared))
            {
                if (!(renderer is SkinnedMeshRenderer skin) || skin.sharedMesh == null) continue;
                for (var shape = 0; shape < skin.sharedMesh.blendShapeCount; shape++)
                    skin.SetBlendShapeWeight(shape, (float)ClampSourceWeight(skin.sharedMesh, shape, skin.GetBlendShapeWeight(shape)));
            }
        }

        internal static void Preserve(GameObject source, GameObject clone, ICollection<Mesh> temporaryMeshes, ICollection<string> warnings,
            Func<Transform, bool> excluded = null)
        {
            foreach (var renderer in ExportRendererSelection.Enumerate(source))
            {
                if (excluded?.Invoke(renderer.transform) == true) continue;
                if (!(renderer is SkinnedMeshRenderer skin) || skin.sharedMesh == null) continue;
                var mesh = skin.sharedMesh;
                var weights = new float[mesh.blendShapeCount];
                var changed = false;
                for (var i = 0; i < weights.Length; i++)
                {
                    weights[i] = skin.GetBlendShapeWeight(i);
                    if (float.IsNaN(weights[i]) || float.IsInfinity(weights[i]))
                        throw new InvalidOperationException($"{skin.name}: BlendShapeの初期値が不正です。");
                    changed |= weights[i] != 0f || HasZeroRestOffset(mesh, i);
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

        // Predict the endpoint that Rebase exports, without mutating source
        // geometry. Only a finite residual with remaining range is usable raw
        // tracking evidence for avatars whose eye rig cannot provide blink.
        internal static bool HasUsableRawEndpoint(SkinnedMeshRenderer skin, int shape)
        {
            if (skin == null || skin.sharedMesh == null || shape < 0 || shape >= skin.sharedMesh.blendShapeCount) return false;
            return HasUsableRawEndpoint(skin.sharedMesh, shape, skin.GetBlendShapeWeight(shape));
        }

        internal static bool HasUsableRawEndpoint(Mesh mesh, int shape, float weight)
        {
            // Source frame endpoints may exceed100. The exported shape is
            // normalized to100 only after subtracting its actual authored rest.
            if (!Finite(weight) || weight < 0f) return false;
            return HasUsableMorphEndpoint(mesh, shape, weight);
        }

        internal static bool HasUsableMorphEndpoint(SkinnedMeshRenderer skin, int shape)
        {
            if (skin == null || skin.sharedMesh == null || shape < 0 || shape >= skin.sharedMesh.blendShapeCount) return false;
            return HasUsableMorphEndpoint(skin.sharedMesh, shape, skin.GetBlendShapeWeight(shape));
        }

        // Explicit authored bindings retain the existing rebase semantics for
        // finite negative or extrapolated rest values. They still need a real
        // finite exported residual, rather than just a valid target index.
        internal static bool HasUsableMorphEndpoint(Mesh mesh, int shape, float weight)
        {
            if (mesh == null || mesh.vertexCount == 0 || shape < 0 || shape >= mesh.blendShapeCount || !Finite(weight)) return false;
            var frames = mesh.GetBlendShapeFrameCount(shape);
            if (frames == 0) return false;
            var finalWeight = mesh.GetBlendShapeFrameWeight(shape, frames - 1);
            if (!Finite(finalWeight) || finalWeight <= 0f) return false;
            return HasUsableMorphEndpoint(mesh, shape, weight, finalWeight);
        }

        // Authored VRM/profile binds declare an absolute source endpoint. In
        // particular a zero endpoint can move away from nonzero neutral, and a
        // partial endpoint can be inert even when the final frame moves.
        internal static bool HasUsableMorphEndpoint(Mesh mesh, int shape, float neutralWeight, float endpointWeight)
        {
            if (mesh == null || mesh.vertexCount == 0 || shape < 0 || shape >= mesh.blendShapeCount ||
                !Finite(neutralWeight) || !Finite(endpointWeight)) return false;
            var frames = mesh.GetBlendShapeFrameCount(shape);
            if (frames == 0) return false;
            var endpoint = new Deltas(mesh.vertexCount);
            for (var frame = 0; frame < frames; frame++)
            {
                var frameWeight = mesh.GetBlendShapeFrameWeight(shape, frame);
                if (!Finite(frameWeight) || frame > 0 && frameWeight <= mesh.GetBlendShapeFrameWeight(shape, frame - 1) ||
                    frameWeight == 0f && frame != 0 && !SpansZero(mesh, shape)) return false;
                mesh.GetBlendShapeFrameVertices(shape, frame, endpoint.Vertices, endpoint.Normals, endpoint.Tangents);
                for (var vertex = 0; vertex < mesh.vertexCount; vertex++)
                {
                    if (!Finite(endpoint.Vertices[vertex]) || !Finite(endpoint.Normals[vertex]) || !Finite(endpoint.Tangents[vertex])) return false;
                    if (frameWeight == 0f && (Nonzero(endpoint.Vertices[vertex]) || Nonzero(endpoint.Normals[vertex]) || Nonzero(endpoint.Tangents[vertex]))) return false;
                }
            }
            Deltas rest;
            try
            {
                rest = Evaluate(mesh, shape, neutralWeight);
                endpoint = Evaluate(mesh, shape, endpointWeight);
            }
            catch (InvalidOperationException) { return false; }
            var meaningful = false;
            for (var vertex = 0; vertex < mesh.vertexCount; vertex++)
            {
                var vertices = endpoint.Vertices[vertex] - rest.Vertices[vertex];
                var normals = endpoint.Normals[vertex] - rest.Normals[vertex];
                var tangents = endpoint.Tangents[vertex] - rest.Tangents[vertex];
                if (!Finite(vertices) || !Finite(normals) || !Finite(tangents)) return false;
                meaningful |= Nonzero(vertices) || Nonzero(normals) || Nonzero(tangents);
            }
            return meaningful;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Nonzero(Vector3 value) => value.x != 0f || value.y != 0f || value.z != 0f;

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
        internal static void AppendExpression(Mesh source, Mesh target, string name, float[] rest, float[] expression,
            bool clampToSourceRange = false)
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
                var before = Evaluate(source, shape, clampToSourceRange ? ClampSourceWeight(source, shape, rest[shape]) : rest[shape]);
                var after = Evaluate(source, shape, clampToSourceRange ? ClampSourceWeight(source, shape, expression[shape]) : expression[shape]);
                for (var v = 0; v < source.vertexCount; v++)
                {
                    delta.Vertices[v] += after.Vertices[v] - before.Vertices[v];
                    delta.Normals[v] += after.Normals[v] - before.Normals[v];
                    delta.Tangents[v] += after.Tangents[v] - before.Tangents[v];
                }
            }
            target.AddBlendShapeFrame(name, 100f, delta.Vertices, delta.Normals, delta.Tangents);
        }

        internal sealed class Deltas
        {
            internal readonly Vector3[] Vertices, Normals, Tangents;
            internal Deltas(int count)
            {
                Vertices = new Vector3[count];
                Normals = new Vector3[count];
                Tangents = new Vector3[count];
            }
        }

        internal static void AppendAnimatedShape(Mesh source, Mesh target, string name, int shape, double initial, double weight,
            bool clampToSourceRange = false)
        {
            if (ReferenceEquals(source, target)) throw new ArgumentException("The source mesh must remain unchanged.");
            var delta = AnimatedDeltas(source, shape, initial, weight, clampToSourceRange);
            target.AddBlendShapeFrame(name, 100f, delta.Vertices, delta.Normals, delta.Tangents);
        }

        // Baking can compare the exact generated residual before storing it.
        // Keep the same interpolation/subtraction as the append operation.
        internal static Deltas AnimatedDeltas(Mesh source, int shape, double initial, double weight,
            bool clampToSourceRange = false)
        {
            var before = Evaluate(source, shape, clampToSourceRange ? ClampSourceWeight(source, shape, initial) : initial);
            var after = Evaluate(source, shape, clampToSourceRange ? ClampSourceWeight(source, shape, weight) : weight);
            for (var v = 0; v < source.vertexCount; v++)
            {
                after.Vertices[v] -= before.Vertices[v];
                after.Normals[v] -= before.Normals[v];
                after.Tangents[v] -= before.Tangents[v];
            }
            return after;
        }

        private static bool SpansZero(Mesh mesh, int shape)
        {
            var count = mesh.GetBlendShapeFrameCount(shape);
            return count > 1 && mesh.GetBlendShapeFrameWeight(shape, 0) < 0 && mesh.GetBlendShapeFrameWeight(shape, count - 1) > 0;
        }

        private static bool HasZeroRestOffset(Mesh mesh, int shape)
        {
            if (!SpansZero(mesh, shape)) return false;
            var rest = Evaluate(mesh, shape, 0);
            for (var vertex = 0; vertex < mesh.vertexCount; vertex++)
                if (Nonzero(rest.Vertices[vertex]) || Nonzero(rest.Normals[vertex]) || Nonzero(rest.Tangents[vertex])) return true;
            return false;
        }

        private static bool IsZeroFrame(Mesh mesh, int shape, int frame)
        {
            var deltas = new Deltas(mesh.vertexCount);
            mesh.GetBlendShapeFrameVertices(shape, frame, deltas.Vertices, deltas.Normals, deltas.Tangents);
            for (var vertex = 0; vertex < mesh.vertexCount; vertex++)
                if (!Finite(deltas.Vertices[vertex]) || !Finite(deltas.Normals[vertex]) || !Finite(deltas.Tangents[vertex]) ||
                    Nonzero(deltas.Vertices[vertex]) || Nonzero(deltas.Normals[vertex]) || Nonzero(deltas.Tangents[vertex])) return false;
            return true;
        }

        static Deltas Evaluate(Mesh mesh, int shape, double weight)
        {
            var result = new Deltas(mesh.vertexCount);
            var spansZero = SpansZero(mesh, shape);
            var knots = new List<KeyValuePair<float, int>>();
            // Unity interpolates a negative/positive bracket through zero;
            // inventing a neutral knot changes its actual rest geometry.
            if (!spansZero) knots.Add(new KeyValuePair<float, int>(0f, -1));
            for (var i = 0; i < mesh.GetBlendShapeFrameCount(shape); i++)
            {
                var frameWeight = mesh.GetBlendShapeFrameWeight(shape, i);
                // A leading all-zero frame is the same implicit origin already
                // present in knots. Do not duplicate it or reject valid curves.
                if (frameWeight == 0f && i == 0 && IsZeroFrame(mesh, shape, i)) continue;
                if (frameWeight == 0f && !(spansZero && IsZeroFrame(mesh, shape, i)) || float.IsNaN(frameWeight) || float.IsInfinity(frameWeight))
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
