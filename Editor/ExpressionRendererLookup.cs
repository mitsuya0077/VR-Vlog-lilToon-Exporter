using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // One synchronous native evaluation owns a frozen hierarchy. Animation
    // may change activation or mesh weights, but supported FX cannot rename,
    // reparent, create or replace its objects. Keep this lookup local to that
    // evaluation, and keep all live renderer/mesh/visibility checks at use.
    internal sealed class ExpressionRendererLookup
    {
        private sealed class Path
        {
            internal int TransformCount;
            internal readonly List<SkinnedMeshRenderer> Renderers = new List<SkinnedMeshRenderer>();
        }

        private readonly GameObject root;
        private readonly Dictionary<string, Path> paths = new Dictionary<string, Path>(StringComparer.Ordinal);

        internal ExpressionRendererLookup(GameObject root)
        {
            this.root = root;
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                var path = AnimationUtility.CalculateTransformPath(transform, root.transform);
                if (!paths.TryGetValue(path, out var value)) paths.Add(path, value = new Path());
                value.TransformCount++;
                value.Renderers.AddRange(transform.GetComponents<SkinnedMeshRenderer>());
            }
        }

        internal SkinnedMeshRenderer Resolve(string path)
        {
            if (path == null || !paths.TryGetValue(path, out var value))
                throw new InvalidOperationException("Rendererのパスを一意に解決できません: " + path);
            if (value.TransformCount > 1)
                throw new InvalidOperationException("重複する階層パスの表情は取り込めません: " + path);
            if (value.Renderers.Count != 1 || value.Renderers[0] == null || value.Renderers[0].sharedMesh == null)
                throw new InvalidOperationException("Rendererのパスを一意に解決できません: " + path);
            var renderer = value.Renderers[0];
            // The cache never substitutes a moved object or a stale identity.
            if (root == null || renderer.transform != root.transform && !renderer.transform.IsChildOf(root.transform) ||
                AnimationUtility.CalculateTransformPath(renderer.transform, root.transform) != path)
                throw new InvalidOperationException("Rendererのパスを一意に解決できません: " + path);
            for (var current = renderer.transform; current != root.transform; current = current.parent)
                if (current.name.Contains("/")) throw new InvalidOperationException("名前に / を含む階層の表情は取り込めません: " + path);
            return renderer;
        }
    }
}
