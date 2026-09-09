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
        }

        private readonly Dictionary<string, Binding> bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);

        internal PreparedExpressionBindings(GameObject clone, VrChatExpressionMenu.Source menu)
        {
            foreach (var path in menu.Entries.Where(e => e.Error == null)
                         .SelectMany(e => e.Values.Select(v => v.Path).Concat(e.Animation.Select(v => v.Path))).Distinct())
                bindings.Add(path, new Binding { Renderer = VrChatExpressionSampler.FindRenderer(clone, path) });
        }

        internal void Capture(VrChatExpressionMenu.Source menu)
        {
            foreach (var binding in bindings.Values)
            {
                var renderer = binding.Renderer;
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.sharedMesh == null) continue;
                binding.Mesh = renderer.sharedMesh;
                binding.Weights = Enumerable.Range(0, binding.Mesh.blendShapeCount).Select(renderer.GetBlendShapeWeight).ToArray();
            }
            foreach (var entry in menu.Entries.Where(e => e.Error == null))
                foreach (var value in entry.Values.Select(v => (v.Path, v.Shape)).Concat(entry.Animation.Select(v => (v.Path, v.Shape))))
                    if (!bindings.TryGetValue(value.Path, out var binding) || binding.Mesh == null || binding.Mesh.GetBlendShapeIndex(value.Shape) < 0)
                    {
                        entry.Error = "衣装・体形の処理後に表情の対象が残っていないため、この表情を省略しました: " + value.Path + " / " + value.Shape;
                        break;
                    }
        }

        internal void FilterRemoved(VrChatExpressionMenu.Source menu, ISet<Renderer> removed, ICollection<string> warnings)
        {
            if (menu == null) return;
            bool Excluded(string path) => bindings.TryGetValue(path, out var binding) && removed.Contains(binding.Renderer);
            foreach (var entry in menu.Entries.Where(e => e.Error == null))
            {
                var count = entry.Values.RemoveAll(v => Excluded(v.Path)) + entry.Animation.RemoveAll(v => Excluded(v.Path));
                if (count == 0) continue;
                if (entry.Values.Count == 0 && entry.Animation.Count == 0)
                    entry.Error = "除外した補助Rendererだけを変更する表情のため省略しました。";
                else warnings?.Add(entry.Name + ": 補助Rendererの表情だけを省略し、残る表情を保持しました。");
            }
        }

        internal Binding Get(string path) => bindings[path];
    }
}
