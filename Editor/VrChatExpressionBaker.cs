using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VRVlog.Expressions;

namespace VRVlog.LilToonExporter
{
    internal static class VrChatExpressionBaker
    {
        private sealed class Channel
        {
            internal string Path, Shape;
            internal SkinnedMeshRenderer Renderer;
            internal Mesh Mesh;
            internal float[] Rest;
            internal bool HasValue;
            internal float Weight;
            internal ExpressionAnimationData.Curve Curve;
        }

        private sealed class Pose
        {
            internal SkinnedMeshRenderer Renderer;
            internal Mesh Mesh;
            internal float[] Rest;
            internal readonly List<Channel> Channels = new List<Channel>();
        }

        private sealed class Plan
        {
            internal VrChatExpressionMenu.Entry Entry;
            internal readonly List<Pose> Poses = new List<Pose>();
            internal readonly List<Channel> Animation = new List<Channel>();
        }

        internal static bool SameCurve(ExpressionAnimationData.Curve first, ExpressionAnimationData.Curve second)
        {
            if (ReferenceEquals(first, second)) return true;
            if (first == null || second == null || first.PreWrap != second.PreWrap || first.PostWrap != second.PostWrap ||
                first.Keys.Count != second.Keys.Count) return false;
            for (var i = 0; i < first.Keys.Count; i++)
            {
                var a = first.Keys[i]; var b = second.Keys[i];
                if (!a.Time.Equals(b.Time) || !a.Value.Equals(b.Value) || !a.InTangent.Equals(b.InTangent) ||
                    !a.OutTangent.Equals(b.OutTangent) || !a.InWeight.Equals(b.InWeight) || !a.OutWeight.Equals(b.OutWeight)) return false;
            }
            return true;
        }

        internal static bool ConstantAt(ExpressionAnimationData.Curve curve, float weight)
        {
            if (curve == null) return true;
            curve.Range(out var minimum, out var maximum);
            return minimum.Equals(maximum) && minimum.Equals((double)weight);
        }

        // Authoring paths retain provenance through NDMF. Baking addresses the
        // final renderer/channel: one pose and one temporal program per channel,
        // with every collision proved compatible before any mesh is changed.
        private static List<Plan> Prepare(GameObject source, GameObject clone, VrChatExpressionMenu.Source menu,
            PreparedExpressionBindings prepared)
        {
            var plans = new List<Plan>();
            foreach (var entry in menu.Entries.Where(value => value.Error == null))
            {
                var origins = new Dictionary<(string Path, string Shape), Channel>();
                Channel Origin(string path, string shape)
                {
                    var key = (path, shape);
                    if (origins.TryGetValue(key, out var found)) return found;
                    var binding = prepared?.Get(path);
                    var original = binding == null ? VrChatExpressionSampler.FindRenderer(source, path) : null;
                    var mesh = binding?.Mesh ?? original.sharedMesh;
                    var renderer = binding?.Renderer ?? VrChatExpressionSampler.FindRenderer(clone, path);
                    var rest = binding?.Weights ?? Enumerable.Range(0, mesh.blendShapeCount).Select(original.GetBlendShapeWeight).ToArray();
                    var channel = new Channel { Path = path, Shape = shape, Renderer = renderer, Mesh = mesh, Rest = rest };
                    origins.Add(key, channel); return channel;
                }
                InvalidOperationException Conflict(Channel channel) => new InvalidOperationException(
                    "Modular Avatar / NDMF の処理で同じ表情の変形に異なる値・アニメーションが統合されました。統合前の表情設定を一致させてください: " +
                    entry.Name + " / " + UnityEditor.AnimationUtility.CalculateTransformPath(channel.Renderer.transform, clone.transform) + " / " + channel.Shape);
                foreach (var value in entry.Values)
                {
                    var channel = Origin(value.Path, value.Shape);
                    if (channel.HasValue && !channel.Weight.Equals(value.Weight)) throw Conflict(channel);
                    channel.HasValue = true; channel.Weight = value.Weight;
                }
                foreach (var value in entry.Animation)
                {
                    var channel = Origin(value.Path, value.Shape);
                    if (channel.Curve != null && !SameCurve(channel.Curve, value.Curve)) throw Conflict(channel);
                    channel.Curve = value.Curve;
                }
                foreach (var channel in origins.Values)
                {
                    if (channel.Curve != null)
                    {
                        var initial = (float)channel.Curve.Evaluate(0);
                        if (channel.HasValue && !channel.Weight.Equals(initial)) throw Conflict(channel);
                        channel.Weight = initial;
                    }
                }
                var plan = new Plan { Entry = entry };
                foreach (var group in origins.Values.GroupBy(channel => (channel.Renderer, channel.Shape)))
                {
                    var channel = group.First();
                    foreach (var other in group.Skip(1))
                    {
                        if (!channel.Weight.Equals(other.Weight) ||
                            !(SameCurve(channel.Curve, other.Curve) || ConstantAt(channel.Curve, channel.Weight) && ConstantAt(other.Curve, other.Weight)))
                            throw Conflict(channel);
                        if (!ReferenceEquals(channel.Mesh, other.Mesh) || !channel.Rest.SequenceEqual(other.Rest))
                            throw new InvalidOperationException("処理後の表情の基準形を一意に確定できません: " + entry.Name + " / " + channel.Shape);
                    }
                    var pose = plan.Poses.SingleOrDefault(value => value.Renderer == channel.Renderer);
                    if (pose == null)
                    {
                        pose = new Pose { Renderer = channel.Renderer, Mesh = channel.Mesh, Rest = channel.Rest };
                        plan.Poses.Add(pose);
                    }
                    else if (!ReferenceEquals(pose.Mesh, channel.Mesh) || !pose.Rest.SequenceEqual(channel.Rest))
                        throw new InvalidOperationException("処理後の表情の基準形を一意に確定できません: " + entry.Name + " / " + channel.Shape);
                    pose.Channels.Add(channel);
                    if (channel.Curve != null) plan.Animation.Add(channel);
                }
                plans.Add(plan);
            }
            return plans;
        }

        internal static List<VrmMenuExpressions.Expression> Bake(GameObject source, GameObject clone,
            VrChatExpressionMenu.Source menu, ICollection<Mesh> meshes, ICollection<string> warnings)
            => Bake(source, clone, menu, meshes, warnings, null);

        internal static List<VrmMenuExpressions.Expression> Bake(GameObject source, GameObject clone,
            VrChatExpressionMenu.Source menu, ICollection<Mesh> meshes, ICollection<string> warnings,
            PreparedExpressionBindings prepared)
        {
            var result = new List<VrmMenuExpressions.Expression>();
            if (prepared == null) new PreparedExpressionBindings(clone, menu, source).Capture(menu);
            var plans = Prepare(source, clone, menu, prepared);
            foreach (var message in menu.Messages) warnings?.Add(message);
            // A controller-wide limitation or a stale shared curve should not
            // print the same explanation once for every menu item.
            var diagnostics = menu.Entries.SelectMany(e => e.Messages.Concat(e.Error == null ? Array.Empty<string>() : new[] { e.Error })
                .Select(message => (e.Name, Message: message)));
            foreach (var group in diagnostics.GroupBy(d => d.Message, StringComparer.Ordinal))
                warnings?.Add(string.Join(", ", group.Select(d => d.Name).Distinct()) + ": " + group.Key);
            // Unique internal names survive UniVRM's renderer/node reordering.
            // Bind against the actual exported names, never guessed mesh indices.
            var prefix = "__VRVlog_Menu_" + Guid.NewGuid().ToString("N") + "_";
            var serial = 0;
            var clampToSourceRange = UnityEditor.PlayerSettings.legacyClampBlendShapeWeights;
            long generatedBytes = 0;
            var basis = new Dictionary<(Mesh mesh, int shape, double initial, double value), string>();
            void Reserve(Mesh mesh)
            {
                generatedBytes += mesh.vertexCount * 36L;
                if (generatedBytes > 128L * 1024 * 1024)
                    throw new InvalidOperationException("表情の追加データが上限の128 MiBを超えます。元のメッシュの頂点数や表情アニメーションを整理してください。");
            }
            foreach (var plan in plans)
            {
                var entry = plan.Entry;
                var expression = new VrmMenuExpressions.Expression { Name = entry.Name };
                foreach (var group in plan.Poses)
                {
                    var originalMesh = group.Mesh;
                    var copy = group.Renderer;
                    Reserve(originalMesh);
                    if (!meshes.Contains(copy.sharedMesh) || ReferenceEquals(copy.sharedMesh, originalMesh))
                    {
                        copy.sharedMesh = UnityEngine.Object.Instantiate(copy.sharedMesh);
                        meshes.Add(copy.sharedMesh);
                    }
                    var rest = group.Rest;
                    var pose = (float[])rest.Clone();
                    foreach (var value in group.Channels)
                    {
                        var index = originalMesh.GetBlendShapeIndex(value.Shape);
                        if (index < 0) throw new InvalidOperationException("表情の元BlendShapeが見つかりません: " + value.Shape);
                        pose[index] = value.Weight;
                    }
                    var name = prefix + serial++;
                    AvatarBaseShape.AppendExpression(originalMesh, copy.sharedMesh, name, rest, pose, clampToSourceRange);
                    expression.Targets.Add(name);
                }
                if (plan.Animation.Count > 0)
                {
                    expression.Animation = new ExpressionAnimationData { Duration = entry.Duration, Loop = entry.Loop };
                    foreach (var animated in plan.Animation)
                    {
                        var original = animated.Mesh;
                        var copy = animated.Renderer.sharedMesh;
                        var shape = original.GetBlendShapeIndex(animated.Shape);
                        animated.Curve.Range(out var minimum, out var maximum);
                        // Morph geometry is linear between source frame weights.
                        // Store those knots once instead of a mesh per video frame.
                        var knots = new SortedSet<double> { minimum, maximum };
                        if (minimum < 0 && maximum > 0) knots.Add(0);
                        for (var frame = 0; frame < original.GetBlendShapeFrameCount(shape); frame++)
                        {
                            var weight = original.GetBlendShapeFrameWeight(shape, frame);
                            if (weight > minimum && weight < maximum) knots.Add(weight);
                        }
                        var channel = new ExpressionAnimationData.Channel { Curve = animated.Curve };
                        var initial = animated.Curve.Evaluate(0);
                        foreach (var weight in knots)
                        {
                            string target = null;
                            if (weight != initial && !basis.TryGetValue((copy, shape, initial, weight), out target))
                            {
                                Reserve(original);
                                target = ExpressionAnimationData.TargetPrefix + prefix + serial++;
                                AvatarBaseShape.AppendAnimatedShape(original, copy, target, shape, initial, weight, clampToSourceRange);
                                basis.Add((copy, shape, initial, weight), target);
                            }
                            channel.Points.Add(new ExpressionAnimationData.Point { Value = weight, Target = target });
                        }
                        expression.Animation.Channels.Add(channel);
                    }
                }
                if (expression.Targets.Count > 0) result.Add(expression);
            }
            if (result.Count > 0) warnings?.Add($"VRChatから{result.Count}個の表情を登録しました。選択中は顔の形を保つため瞬き・口・視線の自動変形を止めます。");
            else warnings?.Add("VRChatから追加できた表情は0件です。選択用の表情がVRMにない場合、アプリの表情ボタンは表示されません。");
            return result;
        }
    }
}
