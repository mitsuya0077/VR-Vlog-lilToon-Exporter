using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VRVlog.Expressions;

namespace VRVlog.LilToonExporter
{
    internal static class VrChatExpressionBaker
    {
        internal static List<VrmMenuExpressions.Expression> Bake(GameObject source, GameObject clone,
            VrChatExpressionMenu.Source menu, ICollection<Mesh> meshes, ICollection<string> warnings)
        {
            var result = new List<VrmMenuExpressions.Expression>();
            foreach (var message in menu.Messages) warnings?.Add(message);
            // Unique internal names survive UniVRM's renderer/node reordering.
            // Bind against the actual exported names, never guessed mesh indices.
            var prefix = "__VRVlog_Menu_" + Guid.NewGuid().ToString("N") + "_";
            var serial = 0;
            long generatedBytes = 0;
            var basis = new Dictionary<(Mesh mesh, int shape, double initial, double value), string>();
            void Reserve(Mesh mesh)
            {
                generatedBytes += mesh.vertexCount * 36L;
                if (generatedBytes > 128L * 1024 * 1024)
                    throw new InvalidOperationException("表情の追加データが上限の128 MiBを超えます。元のメッシュの頂点数や表情アニメーションを整理してください。");
            }
            foreach (var entry in menu.Entries)
            {
                if (entry.Error != null) { warnings?.Add(entry.Name + ": " + entry.Error); continue; }
                var expression = new VrmMenuExpressions.Expression { Name = entry.Name };
                foreach (var group in entry.Values.GroupBy(v => v.Path))
                {
                    var original = VrChatExpressionSampler.FindRenderer(source, group.Key);
                    var copy = VrChatExpressionSampler.FindRenderer(clone, group.Key);
                    Reserve(original.sharedMesh);
                    if (!meshes.Contains(copy.sharedMesh))
                    {
                        copy.sharedMesh = UnityEngine.Object.Instantiate(copy.sharedMesh);
                        meshes.Add(copy.sharedMesh);
                    }
                    var rest = Enumerable.Range(0, original.sharedMesh.blendShapeCount).Select(original.GetBlendShapeWeight).ToArray();
                    var pose = (float[])rest.Clone();
                    foreach (var value in group)
                    {
                        var index = original.sharedMesh.GetBlendShapeIndex(value.Shape);
                        if (index < 0) throw new InvalidOperationException("表情の元BlendShapeが見つかりません: " + value.Shape);
                        pose[index] = value.Weight;
                    }
                    var name = prefix + serial++;
                    AvatarBaseShape.AppendExpression(original.sharedMesh, copy.sharedMesh, name, rest, pose);
                    expression.Targets.Add(name);
                }
                if (entry.Animation.Count > 0)
                {
                    expression.Animation = new ExpressionAnimationData { Duration = entry.Duration, Loop = entry.Loop };
                    foreach (var animated in entry.Animation)
                    {
                        var original = VrChatExpressionSampler.FindRenderer(source, animated.Path).sharedMesh;
                        var copy = VrChatExpressionSampler.FindRenderer(clone, animated.Path).sharedMesh;
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
                                AvatarBaseShape.AppendAnimatedShape(original, copy, target, shape, initial, weight);
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
