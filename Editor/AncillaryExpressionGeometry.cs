using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Curve = VRVlog.Expressions.ExpressionAnimationData.Curve;
using Key = VRVlog.Expressions.ExpressionAnimationData.Key;

namespace VRVlog.LilToonExporter
{
    internal sealed class AncillaryExpressionGeometryException : InvalidOperationException
    {
        internal AncillaryExpressionGeometryException(string message) : base(message) { }
    }
    // Evidence belongs to one prepared export and one expression. Native
    // probes retain the support graph; only its serialization scope is deferred
    // until the actual absolute endpoint and final neutral are both known.
    internal sealed class AncillaryExpressionGeometry
    {
        private sealed class SupportBinding
        {
            internal string Layer, Clip, Path, Property;
        }

        private sealed class Endpoint
        {
            internal PreparedExpressionBindings.Binding Binding;
            internal float[] Pose;
            internal readonly Dictionary<int, float> Values = new Dictionary<int, float>();
            internal readonly Dictionary<int, Curve> Animation = new Dictionary<int, Curve>();
        }

        private readonly GameObject root;
        private readonly Dictionary<SkinnedMeshRenderer, Mesh> captured = new Dictionary<SkinnedMeshRenderer, Mesh>();
        private readonly HashSet<string> capturedPaths = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<SupportBinding> support = new List<SupportBinding>();
        private List<(string Path, string Shape, float Weight)> validatedValues;
        private List<(string Path, string Shape, Curve Curve)> validatedAnimation;

        internal AncillaryExpressionGeometry(GameObject root) { this.root = root; }

        internal void CaptureRenderers(IEnumerable<EditorCurveBinding> morphs)
        {
            foreach (var path in morphs.Select(binding => binding.path).Distinct())
            {
                if (capturedPaths.Contains(path)) continue;
                var renderer = VrChatExpressionSampler.FindRenderer(root, path);
                if (captured.TryGetValue(renderer, out var mesh) && mesh != renderer.sharedMesh) throw UnknownScope();
                captured[renderer] = renderer.sharedMesh;
                capturedPaths.Add(path);
            }
        }

        internal void Record(string layer, string clip, EditorCurveBinding binding)
        {
            if (support.Any(value => value.Layer == layer && value.Clip == clip && value.Path == binding.path && value.Property == binding.propertyName)) return;
            support.Add(new SupportBinding { Layer = layer, Clip = clip, Path = binding.path, Property = binding.propertyName });
            validatedValues = null; validatedAnimation = null;
        }

        internal bool IsValidatedFor(VrChatExpressionMenu.Entry entry) => validatedValues != null && validatedAnimation != null &&
            validatedValues.SequenceEqual(entry.Values.Select(value => (value.Path, value.Shape, value.Weight))) &&
            validatedAnimation.Count == entry.Animation.Count && validatedAnimation.Select((value, index) =>
                value.Path == entry.Animation[index].Path && value.Shape == entry.Animation[index].Shape &&
                VrChatExpressionBaker.SameCurve(value.Curve, entry.Animation[index].Curve)).All(value => value);

        internal void Validate(VrChatExpressionMenu.Entry entry, PreparedExpressionBindings prepared)
        {
            validatedValues = null; validatedAnimation = null;
            if (root == null || prepared == null || prepared.Root != root) throw UnknownScope();
            if (support.Count == 0) { SaveValidation(entry); return; }
            var endpoints = new Dictionary<SkinnedMeshRenderer, Endpoint>();
            Endpoint Resolve(string path, string shape, out int index)
            {
                var binding = prepared.Get(path);
                if (binding?.Renderer == null || binding.Mesh == null || binding.Weights == null || !binding.Mesh.isReadable ||
                    !captured.TryGetValue(binding.Renderer, out var mesh) || mesh != binding.Mesh ||
                    binding.Weights.Length != mesh.blendShapeCount) throw UnknownScope();
                index = mesh.GetBlendShapeIndex(shape);
                if (index < 0) throw UnknownScope();
                if (!endpoints.TryGetValue(binding.Renderer, out var endpoint))
                {
                    endpoint = new Endpoint { Binding = binding, Pose = (float[])binding.Weights.Clone() };
                    endpoints.Add(binding.Renderer, endpoint);
                }
                else if (endpoint.Binding.Mesh != binding.Mesh || !endpoint.Binding.Weights.SequenceEqual(binding.Weights)) throw UnknownScope();
                return endpoint;
            }
            foreach (var value in entry.Values)
            {
                if (!Finite(value.Weight)) throw new InvalidOperationException("表情のBlendShape値が不正です。");
                var endpoint = Resolve(value.Path, value.Shape, out var shape);
                if (endpoint.Values.TryGetValue(shape, out var old) && old != value.Weight) throw UnknownScope();
                endpoint.Values[shape] = value.Weight; endpoint.Pose[shape] = value.Weight;
            }
            foreach (var value in entry.Animation)
            {
                var endpoint = Resolve(value.Path, value.Shape, out var shape);
                if (value.Curve == null) throw UnknownScope();
                if (endpoint.Animation.TryGetValue(shape, out var old) && !VrChatExpressionBaker.SameCurve(old, value.Curve)) throw UnknownScope();
                var initial = (float)value.Curve.Evaluate(0);
                if (!Finite(initial) || endpoint.Values.TryGetValue(shape, out var pose) && pose != initial) throw UnknownScope();
                endpoint.Animation[shape] = value.Curve; endpoint.Pose[shape] = initial;
            }

            var protectedPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var endpoint in endpoints.Values)
            {
                var binding = endpoint.Binding; var mesh = binding.Mesh; var renderer = binding.Renderer;
                var changed = AvatarBaseShape.ExpressionVertexChanges(mesh, binding.Weights, endpoint.Pose);
                foreach (var animation in endpoint.Animation)
                {
                    var initial = animation.Value.Evaluate(0);
                    animation.Value.Range(out var minimum, out var maximum);
                    // These are the same geometry knots emitted by the baker.
                    // Between knots, morph geometry is a linear combination.
                    var knots = new SortedSet<double> { minimum, maximum };
                    if (minimum < 0 && maximum > 0) knots.Add(0);
                    for (var frame = 0; frame < mesh.GetBlendShapeFrameCount(animation.Key); frame++)
                    {
                        var weight = mesh.GetBlendShapeFrameWeight(animation.Key, frame);
                        if (weight > minimum && weight < maximum) knots.Add(weight);
                    }
                    foreach (var weight in knots.Where(weight => weight != initial))
                    {
                        var basis = AvatarBaseShape.AnimatedVertexChanges(mesh, animation.Key, initial, weight);
                        for (var vertex = 0; vertex < changed.Length; vertex++) changed[vertex] |= basis[vertex];
                    }
                }
                // An unchanged endpoint and zero emitted animation bases add
                // no position, normal or tangent geometry for this renderer.
                // Its native support visibility/transform must not block a
                // different renderer's actual facial expression.
                if (!changed.Any(value => value)) continue;
                Protect(renderer.transform, protectedPaths);
                if (renderer.rootBone != null) Protect(renderer.rootBone, protectedPaths);
                ProtectInfluences(renderer, mesh, changed, protectedPaths);
            }
            foreach (var binding in support)
                if (protectedPaths.Any(path => path == binding.Path || path.StartsWith(binding.Path + "/", StringComparison.Ordinal)))
                    throw new AncillaryExpressionGeometryException("表情への遷移にBlendShape以外の変化が含まれます: " +
                        binding.Layer + " / " + binding.Clip + " / " + binding.Path + " / " + binding.Property);

            SaveValidation(entry);
            if (support.Count > 0)
            {
                const string message = "付随するオブジェクトの表示・TransformアニメーションはVRM表情に含めません。";
                if (!entry.Messages.Contains(message)) entry.Messages.Add(message);
            }
        }

        private void SaveValidation(VrChatExpressionMenu.Entry entry)
        {
            validatedValues = entry.Values.Select(value => (value.Path, value.Shape, value.Weight)).ToList();
            validatedAnimation = entry.Animation.Select(value => (value.Path, value.Shape, Copy(value.Curve))).ToList();
        }

        private void Protect(Transform transform, ISet<string> paths)
        {
            if (transform == null || transform != root.transform && !transform.IsChildOf(root.transform)) throw UnknownScope();
            paths.Add(AnimationUtility.CalculateTransformPath(transform, root.transform));
        }

        private void ProtectInfluences(SkinnedMeshRenderer renderer, Mesh mesh, bool[] changed, ISet<string> paths)
        {
            var bones = renderer.bones ?? Array.Empty<Transform>();
            var poses = mesh.bindposes;
            if (poses.Length != bones.Length) throw UnknownScope();
            foreach (var pose in poses) for (var index = 0; index < 16; index++) if (!Finite(pose[index])) throw UnknownScope();
            // Immediate mesh-owned read-only views (Allocator.None).
            var counts = mesh.GetBonesPerVertex(); var weights = mesh.GetAllBoneWeights();
            if (weights.Length == 0 && bones.Length == 0 && counts.Length == 0) return;
            if (counts.Length != changed.Length) throw UnknownScope();
            var offset = 0;
            for (var vertex = 0; vertex < counts.Length; vertex++)
                for (var influence = 0; influence < counts[vertex]; influence++)
                {
                    if (offset >= weights.Length) throw UnknownScope();
                    var weight = weights[offset++];
                    if (!Finite(weight.weight) || weight.weight < 0) throw UnknownScope();
                    if (weight.weight == 0) continue;
                    if (weight.boneIndex < 0 || weight.boneIndex >= bones.Length || bones[weight.boneIndex] == null) throw UnknownScope();
                    if (changed[vertex]) Protect(bones[weight.boneIndex], paths);
                }
            if (offset != weights.Length) throw UnknownScope();
        }

        private static Curve Copy(Curve curve)
        {
            if (curve == null) return null;
            var result = new Curve { PreWrap = curve.PreWrap, PostWrap = curve.PostWrap };
            result.Keys.AddRange(curve.Keys.Select(key => new Key { Time = key.Time, Value = key.Value,
                InTangent = key.InTangent, OutTangent = key.OutTangent, InWeight = key.InWeight, OutWeight = key.OutWeight }));
            return result;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static AncillaryExpressionGeometryException UnknownScope() => new AncillaryExpressionGeometryException("表情の付随アニメーションと最終基準形の影響範囲を確定できません。");
    }
}
