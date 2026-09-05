using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    internal static class VrChatExpressionBaker
    {
        internal static List<VrmMenuExpressions.Expression> Bake(GameObject source, GameObject clone,
            VrChatExpressionMenu.Source menu, ISet<string> excluded, ICollection<Mesh> meshes, ICollection<string> warnings)
        {
            var result = new List<VrmMenuExpressions.Expression>();
            foreach (var message in menu.Messages) warnings?.Add(message);
            // Unique internal names survive UniVRM's renderer/node reordering.
            // Bind against the actual exported names, never guessed mesh indices.
            var prefix = "__VRVlog_Menu_" + Guid.NewGuid().ToString("N") + "_";
            var serial = 0;
            long generatedBytes = 0;
            foreach (var entry in menu.Entries)
            {
                if (excluded != null && excluded.Contains(entry.Id)) continue;
                if (entry.Error != null) { warnings?.Add(entry.Name + ": " + entry.Error); continue; }
                var expression = new VrmMenuExpressions.Expression { Name = entry.Name };
                foreach (var group in entry.Values.GroupBy(v => v.Path))
                {
                    var original = VrChatExpressionSampler.FindRenderer(source, group.Key);
                    var copy = VrChatExpressionSampler.FindRenderer(clone, group.Key);
                    generatedBytes += original.sharedMesh.vertexCount * 36L;
                    if (generatedBytes > 128L * 1024 * 1024)
                        throw new InvalidOperationException("表情の追加データが128 MiBを超えます。取り込む表情を減らしてください。");
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
                if (expression.Targets.Count > 0) result.Add(expression);
            }
            if (result.Count > 0) warnings?.Add($"VRChatメニューから{result.Count}個の表情を登録しました。選択中は顔の形を保つため瞬き・口・視線の自動変形を止めます。");
            return result;
        }
    }
}
