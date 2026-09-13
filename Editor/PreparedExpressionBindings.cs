using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // Resolve authoring paths once, before NDMF moves objects. Geometry and rest
    // weights are captured only after the authoring passes finish.
    internal sealed class PreparedExpressionBindings
    {
        internal sealed class Binding
        {
            internal SkinnedMeshRenderer Renderer;
            internal Mesh Mesh;
            internal float[] Weights;
            internal HashSet<string> OriginalShapes;
        }

        private readonly Dictionary<string, Binding> bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);

        internal PreparedExpressionBindings(GameObject clone, VrChatExpressionMenu.Source menu, GameObject source = null)
        {
            foreach (var path in menu.Entries.Where(e => e.Error == null)
                         .SelectMany(e => e.Values.Select(v => v.Path).Concat(e.Animation.Select(v => v.Path))
                             .Concat(e.Unevaluated.Select(v => v.Path))).Distinct())
            {
                var renderer = VrChatExpressionSampler.FindRenderer(clone, path);
                var original = source == null ? renderer.sharedMesh : VrChatExpressionSampler.FindRenderer(source, path).sharedMesh;
                bindings.Add(path, new Binding
                {
                    Renderer = renderer,
                    OriginalShapes = new HashSet<string>(Enumerable.Range(0, original.blendShapeCount).Select(original.GetBlendShapeName), StringComparer.Ordinal)
                });
            }
        }

        internal void Capture(VrChatExpressionMenu.Source menu)
        {
            foreach (var binding in bindings.Values)
            {
                var renderer = binding.Renderer;
                binding.Mesh = null;
                binding.Weights = null;
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.sharedMesh == null) continue;
                binding.Mesh = renderer.sharedMesh;
                binding.Weights = Enumerable.Range(0, binding.Mesh.blendShapeCount).Select(renderer.GetBlendShapeWeight).ToArray();
            }
            foreach (var entry in menu.Entries.Where(e => e.Error == null))
            {
                var missing = new HashSet<(string Path, string Shape)>();
                var unevaluated = new HashSet<(string Path, string Shape)>(entry.Unevaluated.Select(v => (v.Path, v.Shape)));
                foreach (var value in entry.Values.Select(v => (v.Path, v.Shape)).Concat(entry.Animation.Select(v => (v.Path, v.Shape)))
                             .Concat(unevaluated).Distinct())
                {
                    if (!bindings.TryGetValue(value.Path, out var binding) || binding.Mesh == null ||
                        binding.OriginalShapes.Contains(value.Shape) && binding.Mesh.GetBlendShapeIndex(value.Shape) < 0)
                    {
                        entry.Error = "衣装・体形の処理後に表情の対象が残っていないため、この表情を省略しました: " + value.Path + " / " + value.Shape;
                        break;
                    }
                    if (binding.Mesh.GetBlendShapeIndex(value.Shape) < 0) missing.Add(value);
                    else if (unevaluated.Contains(value))
                    {
                        entry.Error = "前処理後に生成されたBlendShapeのメニュー選択値を確定できません: " + value.Path + " / " + value.Shape;
                        break;
                    }
                }
                if (entry.Error != null) continue;
                entry.Values.RemoveAll(v => missing.Contains((v.Path, v.Shape)));
                entry.Animation.RemoveAll(v => missing.Contains((v.Path, v.Shape)));
                entry.Unevaluated.Clear();
                if (missing.Count > 0)
                    entry.Messages.Add("元モデルにも出力モデルにもないBlendShape参照を省略しました: " +
                                       string.Join(", ", missing.OrderBy(v => v.Path, StringComparer.Ordinal).ThenBy(v => v.Shape, StringComparer.Ordinal).Select(v => v.Path + "/" + v.Shape)));
                if (entry.Animation.Count == 0) { entry.Duration = 0; entry.Loop = false; }
                if (entry.Values.Count == 0) entry.Error = "参照を解決した結果、有効な顔のBlendShapeがないため、この表情を省略しました。";
            }
        }

        internal void FilterRemoved(VrChatExpressionMenu.Source menu, ISet<Renderer> removed, ICollection<string> warnings)
        {
            if (menu == null) return;
            bool Excluded(string path) => bindings.TryGetValue(path, out var binding) && removed.Contains(binding.Renderer);
            foreach (var entry in menu.Entries.Where(e => e.Error == null))
            {
                var count = entry.Values.RemoveAll(v => Excluded(v.Path)) + entry.Animation.RemoveAll(v => Excluded(v.Path)) + entry.Unevaluated.RemoveAll(v => Excluded(v.Path));
                if (count == 0) continue;
                if (entry.Values.Count == 0 && entry.Animation.Count == 0 && entry.Unevaluated.Count == 0)
                    entry.Error = "除外した補助Rendererだけを変更する表情のため省略しました。";
                else warnings?.Add(entry.Name + ": 補助Rendererの表情だけを省略し、残る表情を保持しました。");
            }
        }

        internal Binding Get(string path) => bindings[path];
    }
}
