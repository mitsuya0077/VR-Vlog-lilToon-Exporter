using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // Explicit author selection, applied only to the temporary export avatar.
    // Pets and clothing can share shaders and names; neither identifies them.
    internal sealed class ExportObjectExclusions : IDisposable
    {
        private readonly GameObject source;
        private readonly List<Transform> roots = new List<Transform>();
        private readonly List<UnityEngine.Object> temporaryAssets = new List<UnityEngine.Object>();
        private Dictionary<string, bool> excludedPaths;

        internal ExportObjectExclusions(GameObject source, IEnumerable<GameObject> excluded)
        {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            foreach (var item in excluded ?? Enumerable.Empty<GameObject>())
            {
                if (item == null) continue;
                if (item == source || !item.transform.IsChildOf(source.transform))
                    throw new InvalidOperationException("除外するオブジェクトは、アバター本体以外の子オブジェクトを指定してください。");
                if (!roots.Contains(item.transform)) roots.Add(item.transform);
            }
            var animator = source.GetComponent<Animator>();
            if (animator != null && animator.isHuman)
                for (var bone = 0; bone < (int)HumanBodyBones.LastBone; bone++)
                {
                    var transform = animator.GetBoneTransform((HumanBodyBones)bone);
                    if (transform != null && Contains(transform))
                        throw new InvalidOperationException("Humanoidの骨格を含むオブジェクトは除外できません: " + transform.name);
                }
            foreach (var skin in source.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (!skin.enabled || Contains(skin.transform)) continue;
                if (skin.bones.Any(bone => bone != null && Contains(bone)) || skin.rootBone != null && Contains(skin.rootBone))
                    throw new InvalidOperationException(skin.name + ": 書き出すメッシュが使用している骨を除外することはできません。");
            }
        }

        internal bool Contains(Transform transform) => transform != null && roots.Any(root => transform == root || transform.IsChildOf(root));
        internal bool HasAny => roots.Count != 0;

        internal bool ContainsPath(string path)
        {
            if (roots.Count == 0 || path == null) return false;
            // Animation sampling can ask on every frame. The original hierarchy
            // stays unchanged during export, so index it once, including all
            // duplicate paths instead of choosing the first matching transform.
            if (excludedPaths == null)
                excludedPaths = source.GetComponentsInChildren<Transform>(true)
                    .GroupBy(t => AnimationUtility.CalculateTransformPath(t, source.transform), StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.All(Contains), StringComparer.Ordinal);
            return excludedPaths.TryGetValue(path, out var excluded) && excluded;
        }

        internal void FilterExpressions(VrChatExpressionMenu.Source menu, ICollection<string> warnings)
        {
            if (roots.Count == 0) return;
            foreach (var entry in menu.Entries)
            {
                if (entry.Error != null) continue;
                // Use the same exact path resolver as expression export, rather
                // than deleting entries on an ambiguous name prefix.
                bool Excluded(string path) => Contains(VrChatExpressionSampler.FindRenderer(source, path).transform);
                var removed = entry.Values.RemoveAll(value => Excluded(value.Path));
                removed += entry.Animation.RemoveAll(value => Excluded(value.Path));
                if (removed == 0) continue;
                if (entry.Values.Count == 0 && entry.Animation.Count == 0)
                    entry.Error = "除外したオブジェクトのみを変更する表情のため省略しました。";
                else warnings?.Add(entry.Name + ": 除外したオブジェクトの表情だけを省略しました。");
            }
        }

        internal void Apply(GameObject clone, ICollection<string> warnings)
        {
            if (clone == null || clone == source) throw new ArgumentException("An independent export clone is required.");
            // Resolve every sibling-index route before deleting any siblings.
            // Duplicate names and nested exclusions must not target a neighbor.
            var targets = new List<Transform>();
            foreach (var root in roots)
            {
                var route = new Stack<int>();
                for (var current = root; current != source.transform; current = current.parent)
                    route.Push(current.GetSiblingIndex());
                var target = clone.transform;
                while (route.Count > 0) target = target.GetChild(route.Pop());
                targets.Add(target);
                warnings?.Add("除外: " + AnimationUtility.CalculateTransformPath(root, source.transform));
            }
            if (targets.Count > 0) ExportReferencePruning.Prepare(clone, targets, temporaryAssets, warnings);
            foreach (var target in targets)
                if (target != null) UnityEngine.Object.DestroyImmediate(target.gameObject);
        }

        public void Dispose()
        {
            foreach (var asset in temporaryAssets)
                if (asset != null) UnityEngine.Object.DestroyImmediate(asset);
            temporaryAssets.Clear();
        }
    }
}
