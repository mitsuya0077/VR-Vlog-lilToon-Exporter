using System;
using System.Collections.Generic;
using System.Linq;
using UniVRM10;
using UnityEngine;
using VRVlog.FaceTracking;

namespace VRVlog.LilToonExporter
{
    // Preserve required morph identity across authoring passes. Names on another
    // renderer cannot conceal loss of the tracked face or a selected endpoint.
    internal sealed class UnifiedExpressionPreparation
    {
        private readonly Dictionary<SkinnedMeshRenderer, string[]> required = new Dictionary<SkinnedMeshRenderer, string[]>();

        internal UnifiedExpressionPreparation(GameObject clone)
        {
            var meshes = ExportRendererSelection.Enumerate(clone).OfType<SkinnedMeshRenderer>()
                .Where(skin => skin.sharedMesh != null).ToDictionary(skin => skin, skin =>
                    Enumerable.Range(0, skin.sharedMesh.blendShapeCount).Select(skin.sharedMesh.GetBlendShapeName).ToArray());
            var clips = clone.GetComponent<Vrm10Instance>()?.Vrm?.Expression?.CustomClips?.Where(clip => clip != null).ToArray()
                ?? Array.Empty<VRM10Expression>();
            var supportsUnified = VrmUnifiedExpressions.HasEvidence(meshes.Values.SelectMany(names => names).Concat(clips.Select(clip => clip.name)));
            if (!supportsUnified) return;
            var reserved = new HashSet<string>(StringComparer.Ordinal);
            var coverage = new Dictionary<SkinnedMeshRenderer, HashSet<string>>();
            var globalCoverage = new HashSet<string>(StringComparer.Ordinal);
            var authoredShapes = new Dictionary<SkinnedMeshRenderer, HashSet<string>>();
            foreach (var clip in clips)
            {
                if (!UnifiedExpressionRegistry.TryCanonicalize(clip.name, out var canonical) || !HasBindings(clip)) continue;
                // Final export preserves each nonempty authored channel rather
                // than synthesizing another raw endpoint for that same name.
                reserved.Add(canonical);
                if ((clip.MorphTargetBindings?.Length ?? 0) == 0) globalCoverage.Add(canonical);
                foreach (var binding in clip.MorphTargetBindings ?? Array.Empty<MorphTargetBinding>())
                {
                    var target = string.IsNullOrEmpty(binding.RelativePath) ? clone.transform : clone.transform.Find(binding.RelativePath);
                    var skin = target == null ? null : target.GetComponent<SkinnedMeshRenderer>();
                    if (skin == null || !meshes.TryGetValue(skin, out var names)) continue;
                    if (!coverage.TryGetValue(skin, out var covered)) coverage.Add(skin, covered = new HashSet<string>(StringComparer.Ordinal));
                    // A known renderer reserves declared coverage even for an
                    // invalid index/weight, matching final GLB selection.
                    covered.Add(canonical);
                    if (binding.Index < 0 || binding.Index >= names.Length) continue;
                    if (!authoredShapes.TryGetValue(skin, out var shapes)) authoredShapes.Add(skin, shapes = new HashSet<string>(StringComparer.Ordinal));
                    shapes.Add(names[binding.Index]);
                }
            }
            var selected = meshes.ToDictionary(pair => pair.Key, pair => VrmUnifiedExpressions.Resolve(pair.Value, avatarSupportsUnified: true,
                authoredCoverage: globalCoverage.Concat(coverage.TryGetValue(pair.Key, out var covered) ? covered : Enumerable.Empty<string>()),
                reservedAuthoredNames: reserved));
            var materials = new HashSet<string>(ExportRendererSelection.Enumerate(clone).SelectMany(renderer => renderer.sharedMaterials)
                .Where(material => material != null).Select(material => material.name), StringComparer.Ordinal);
            if (!VrmUnifiedExpressions.HasEvidence(clips.Where(clip => IsUsable(clip, clone, materials)).Select(clip => clip.name)
                .Concat(selected.SelectMany(pair => pair.Value.Values.Select(index => meshes[pair.Key][index]))))) return;
            foreach (var pair in meshes)
            {
                var shapes = new HashSet<string>(selected[pair.Key].Values.Select(index => pair.Value[index]), StringComparer.Ordinal);
                if (authoredShapes.TryGetValue(pair.Key, out var authored)) shapes.UnionWith(authored);
                if (shapes.Count > 0) required.Add(pair.Key, shapes.ToArray());
            }
        }

        private static bool IsUsable(VRM10Expression clip, GameObject clone, ISet<string> materials)
        {
            var positiveMorph = false;
            foreach (var binding in clip.MorphTargetBindings ?? Array.Empty<MorphTargetBinding>())
            {
                var target = string.IsNullOrEmpty(binding.RelativePath) ? clone.transform : clone.transform.Find(binding.RelativePath);
                var mesh = target == null ? null : target.GetComponent<SkinnedMeshRenderer>()?.sharedMesh;
                if (mesh == null || binding.Index < 0 || binding.Index >= mesh.blendShapeCount ||
                    !Finite(binding.Weight) || binding.Weight < 0 || binding.Weight > 1) return false;
                positiveMorph |= binding.Weight > 0;
            }
            foreach (var binding in clip.MaterialColorBindings ?? Array.Empty<MaterialColorBinding>())
                if (!materials.Contains(binding.MaterialName) || !Enum.IsDefined(typeof(MaterialColorType), binding.BindType) ||
                    !Finite(binding.TargetValue.r) || !Finite(binding.TargetValue.g) || !Finite(binding.TargetValue.b) || !Finite(binding.TargetValue.a)) return false;
            foreach (var binding in clip.MaterialUVBindings ?? Array.Empty<MaterialUVBinding>())
                if (!materials.Contains(binding.MaterialName) || !Finite(binding.Scaling.x) || !Finite(binding.Scaling.y) ||
                    !Finite(binding.Offset.x) || !Finite(binding.Offset.y)) return false;
            return positiveMorph || (clip.MaterialColorBindings?.Length ?? 0) + (clip.MaterialUVBindings?.Length ?? 0) > 0;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool HasBindings(VRM10Expression clip) =>
            (clip.MorphTargetBindings?.Length ?? 0) + (clip.MaterialColorBindings?.Length ?? 0) + (clip.MaterialUVBindings?.Length ?? 0) > 0;

        internal void Verify()
        {
            foreach (var pair in required)
            {
                var skin = pair.Key;
                if (skin == null || !skin.enabled || !skin.gameObject.activeInHierarchy || skin.sharedMesh == null ||
                    pair.Value.Any(name => skin.sharedMesh.GetBlendShapeIndex(name) < 0))
                    throw new InvalidOperationException("Modular Avatar / NDMF の処理で Unified Expressions の追跡用変形が失われました。メッシュや BlendShape を変更する追加ツールの設定を確認してください。");
            }
        }
    }
}
