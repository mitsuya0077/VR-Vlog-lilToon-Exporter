using System;
using System.Collections.Generic;
using System.Linq;
using UniGLTF.Extensions.VRMC_vrm;
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
        private readonly Dictionary<SkinnedMeshRenderer, string[]> effectiveRaw = new Dictionary<SkinnedMeshRenderer, string[]>();
        private readonly Dictionary<SkinnedMeshRenderer, string[]> effectiveAuthored = new Dictionary<SkinnedMeshRenderer, string[]>();
        private readonly HashSet<string> usableAuthoredNames = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<(string Canonical, SkinnedMeshRenderer Renderer, string Shape, float Weight)> effectiveAuthoredRoutes =
            new HashSet<(string, SkinnedMeshRenderer, string, float)>();
        private readonly Dictionary<SkinnedMeshRenderer, Dictionary<string, string>> selectedRawRoutes = new Dictionary<SkinnedMeshRenderer, Dictionary<string, string>>();
        private readonly GameObject avatar;
        private readonly Func<Transform, bool> excluded;
        private const string LostTracking = "Modular Avatar / NDMF の処理で Unified Expressions の追跡用変形が失われました。メッシュや BlendShape を変更する追加ツールの設定を確認してください。";

        internal bool SupportsUnified { get; private set; }

        internal static bool HasUsableEvidence(GameObject source, Func<Transform, bool> excluded = null) =>
            new UnifiedExpressionPreparation(source, excluded).SupportsUnified;

        internal UnifiedExpressionPreparation(GameObject clone, Func<Transform, bool> excluded = null)
        {
            avatar = clone;
            this.excluded = excluded;
            var renderers = ExportRendererSelection.Enumerate(clone)
                .Where(renderer => excluded?.Invoke(renderer.transform) != true).ToArray();
            var meshes = renderers.OfType<SkinnedMeshRenderer>()
                .Where(skin => skin.sharedMesh != null).ToDictionary(skin => skin, skin =>
                    Enumerable.Range(0, skin.sharedMesh.blendShapeCount).Select(skin.sharedMesh.GetBlendShapeName).ToArray());
            var materials = new HashSet<string>(renderers.SelectMany(renderer => renderer.sharedMaterials)
                .Where(material => material != null).Select(material => material.name), StringComparer.Ordinal);
            var omittedMaterials = new HashSet<string>(clone.GetComponentsInChildren<Renderer>(true)
                .Where(renderer => excluded?.Invoke(renderer.transform) == true).SelectMany(renderer => renderer.sharedMaterials)
                .Where(material => material != null).Select(material => material.name), StringComparer.Ordinal);
            omittedMaterials.ExceptWith(materials);
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
                if (!UnifiedExpressionRegistry.TryCanonicalize(clip.name, out var canonical) || !HasBindings(clip, clone, excluded, omittedMaterials)) continue;
                // Final export preserves each nonempty authored channel rather
                // than synthesizing another raw endpoint for that same name.
                reserved.Add(canonical);
                var hasScopedMorph = false;
                foreach (var binding in RetainedMorphs(clip, clone, excluded))
                {
                    var target = string.IsNullOrEmpty(binding.RelativePath) ? clone.transform : clone.transform.Find(binding.RelativePath);
                    var skin = target == null ? null : target.GetComponent<SkinnedMeshRenderer>();
                    if (skin == null || !meshes.TryGetValue(skin, out var names)) continue;
                    hasScopedMorph = true;
                    if (!coverage.TryGetValue(skin, out var covered)) coverage.Add(skin, covered = new HashSet<string>(StringComparer.Ordinal));
                    // A known renderer reserves declared coverage even for an
                    // invalid index/weight, matching final GLB selection.
                    covered.Add(canonical);
                    if (binding.Index < 0 || binding.Index >= names.Length) continue;
                    if (!authoredShapes.TryGetValue(skin, out var shapes)) authoredShapes.Add(skin, shapes = new HashSet<string>(StringComparer.Ordinal));
                    shapes.Add(names[binding.Index]);
                }
                if (!hasScopedMorph && (RetainedColors(clip, omittedMaterials).Any() || RetainedUV(clip, omittedMaterials).Any()))
                    globalCoverage.Add(canonical);
            }
            var selected = meshes.ToDictionary(pair => pair.Key, pair => VrmUnifiedExpressions.Resolve(pair.Value, avatarSupportsUnified: true,
                authoredCoverage: globalCoverage.Concat(coverage.TryGetValue(pair.Key, out var covered) ? covered : Enumerable.Empty<string>()),
                reservedAuthoredNames: reserved));
            foreach (var pair in selected)
                selectedRawRoutes.Add(pair.Key, pair.Value.ToDictionary(route => route.Key, route => meshes[pair.Key][route.Value], StringComparer.Ordinal));
            // All declarations still reserve coverage, while actual playback
            // chooses one preferred authored alias. A disabled or ambiguous
            // preferred alias cannot be bypassed through an older lower alias.
            var usableClips = clips.Where(clip => UnifiedExpressionRegistry.TryCanonicalize(clip.name, out _) &&
                HasBindings(clip, clone, excluded, omittedMaterials)).GroupBy(clip => {
                    UnifiedExpressionRegistry.TryCanonicalize(clip.name, out var canonical); return canonical;
                }, StringComparer.Ordinal).SelectMany(group => {
                    var priority = group.Min(clip => VrmUnifiedExpressions.RawPriority(clip.name, group.Key));
                    var preferred = group.Where(clip => VrmUnifiedExpressions.RawPriority(clip.name, group.Key) == priority).ToArray();
                    return preferred.Length == 1 && IsUsable(preferred[0], clone, materials, meshes, excluded, omittedMaterials)
                        ? preferred : Array.Empty<VRM10Expression>();
                }).ToArray();
            usableAuthoredNames.UnionWith(usableClips.Select(clip => clip.name));
            var usableAuthored = usableAuthoredNames.ToArray();
            // Blink validation uses the same alias, authored-coverage and
            // representation selection as preparation. A retained empty or
            // disabled route and an inert/fully resting raw shape cannot alone
            // waive the ordinary missing-blink safeguard.
            SupportsUnified = VrmUnifiedExpressions.HasEvidence(usableAuthored.Concat(selected.SelectMany(pair =>
                pair.Value.Values.Where(index => AvatarBaseShape.HasUsableRawEndpoint(pair.Key, index))
                    .Select(index => meshes[pair.Key][index]))));
            if (SupportsUnified)
            {
                foreach (var pair in selected)
                {
                    var shapes = pair.Value.Values.Where(index => AvatarBaseShape.HasUsableRawEndpoint(pair.Key, index))
                        .Select(index => meshes[pair.Key][index]).ToArray();
                    if (shapes.Length > 0) effectiveRaw.Add(pair.Key, shapes);
                }
                foreach (var clip in usableClips)
                {
                    UnifiedExpressionRegistry.TryCanonicalize(clip.name, out var canonical);
                    foreach (var binding in EffectiveMorphs(clip, clone, excluded))
                    {
                        var target = Target(clone, binding.RelativePath);
                        var skin = target == null ? null : target.GetComponent<SkinnedMeshRenderer>();
                        if (binding.Weight > 0 && AvatarBaseShape.HasUsableMorphEndpoint(skin, binding.Index))
                            effectiveAuthoredRoutes.Add((canonical, skin, skin.sharedMesh.GetBlendShapeName(binding.Index), binding.Weight));
                    }
                }
                foreach (var skin in meshes.Keys)
                {
                    var shapes = usableClips.SelectMany(clip => EffectiveMorphs(clip, clone, excluded))
                        .Where(binding => binding.Weight > 0 &&
                            (string.IsNullOrEmpty(binding.RelativePath) ? clone.transform : clone.transform.Find(binding.RelativePath)) == skin.transform &&
                            AvatarBaseShape.HasUsableMorphEndpoint(skin, binding.Index))
                        .Select(binding => meshes[skin][binding.Index]).Distinct(StringComparer.Ordinal).ToArray();
                    if (shapes.Length > 0) effectiveAuthored.Add(skin, shapes);
                }
            }
            if (!VrmUnifiedExpressions.HasEvidence(usableAuthored
                .Concat(selected.SelectMany(pair => pair.Value.Values.Select(index => meshes[pair.Key][index]))))) return;
            foreach (var pair in meshes)
            {
                var shapes = new HashSet<string>(selected[pair.Key].Values.Select(index => pair.Value[index]), StringComparer.Ordinal);
                if (authoredShapes.TryGetValue(pair.Key, out var authored)) shapes.UnionWith(authored);
                if (shapes.Count > 0) required.Add(pair.Key, shapes.ToArray());
            }
        }

        private static bool IsUsable(VRM10Expression clip, GameObject clone, ISet<string> materials,
            IDictionary<SkinnedMeshRenderer, string[]> meshes, Func<Transform, bool> excluded, ISet<string> omittedMaterials)
        {
            var positiveMorph = false;
            var targets = new HashSet<(SkinnedMeshRenderer, int)>();
            foreach (var binding in RetainedMorphs(clip, clone, excluded))
            {
                var target = string.IsNullOrEmpty(binding.RelativePath) ? clone.transform : clone.transform.Find(binding.RelativePath);
                var skin = target == null ? null : target.GetComponent<SkinnedMeshRenderer>();
                // Unity's missing component compares equal to null but is not
                // a CLR null, so null-conditional property access is unsafe.
                if (skin == null || !meshes.ContainsKey(skin)) return false;
                var mesh = skin.sharedMesh;
                if (mesh == null || binding.Index < 0 || binding.Index >= mesh.blendShapeCount ||
                    !Finite(binding.Weight) || binding.Weight < 0 || binding.Weight > 1) return false;
                                // VRM1 keeps the first binding to each renderer/index. Later
                // duplicates remain validated and preserved but cannot enable
                // a route whose first surviving binding deliberately disables it.
                positiveMorph |= targets.Add((skin, binding.Index)) && binding.Weight > 0 && AvatarBaseShape.HasUsableMorphEndpoint(skin, binding.Index);
            }
            foreach (var binding in RetainedColors(clip, omittedMaterials))
                if (!materials.Contains(binding.MaterialName) || !Enum.IsDefined(typeof(MaterialColorType), binding.BindType) ||
                    !Finite(binding.TargetValue.x) || !Finite(binding.TargetValue.y) || !Finite(binding.TargetValue.z) || !Finite(binding.TargetValue.w)) return false;
            foreach (var binding in RetainedUV(clip, omittedMaterials))
                if (!materials.Contains(binding.MaterialName) || !Finite(binding.Scaling.x) || !Finite(binding.Scaling.y) ||
                    !Finite(binding.Offset.x) || !Finite(binding.Offset.y)) return false;
            return positiveMorph || RetainedColors(clip, omittedMaterials).Any() || RetainedUV(clip, omittedMaterials).Any();
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static Transform Target(GameObject clone, string path) =>
            string.IsNullOrEmpty(path) ? clone.transform : clone.transform.Find(path);

        private static IEnumerable<MorphTargetBinding> RetainedMorphs(VRM10Expression clip, GameObject clone, Func<Transform, bool> excluded) =>
            (clip.MorphTargetBindings ?? Array.Empty<MorphTargetBinding>()).Where(binding => {
                var target = Target(clone, binding.RelativePath);
                return target == null || excluded?.Invoke(target) != true;
            });

        private static IEnumerable<MorphTargetBinding> EffectiveMorphs(VRM10Expression clip, GameObject clone, Func<Transform, bool> excluded) =>
            RetainedMorphs(clip, clone, excluded).GroupBy(binding => (Target(clone, binding.RelativePath), binding.Index)).Select(group => group.First());

        private static IEnumerable<MaterialColorBinding> RetainedColors(VRM10Expression clip, ISet<string> omittedMaterials) =>
            (clip.MaterialColorBindings ?? Array.Empty<MaterialColorBinding>()).Where(binding => !omittedMaterials.Contains(binding.MaterialName));

        private static IEnumerable<MaterialUVBinding> RetainedUV(VRM10Expression clip, ISet<string> omittedMaterials) =>
            (clip.MaterialUVBindings ?? Array.Empty<MaterialUVBinding>()).Where(binding => !omittedMaterials.Contains(binding.MaterialName));

        private static bool HasBindings(VRM10Expression clip, GameObject clone, Func<Transform, bool> excluded, ISet<string> omittedMaterials) =>
            RetainedMorphs(clip, clone, excluded).Any() || RetainedColors(clip, omittedMaterials).Any() || RetainedUV(clip, omittedMaterials).Any();

        internal void Verify(bool requireUsableEvidence = false)
        {
            foreach (var pair in required)
            {
                var skin = pair.Key;
                if (skin == null || !skin.enabled || !skin.gameObject.activeInHierarchy || skin.sharedMesh == null ||
                    pair.Value.Any(name => skin.sharedMesh.GetBlendShapeIndex(name) < 0))
                    throw new InvalidOperationException(LostTracking);
            }
            VerifyEffective(effectiveRaw, AvatarBaseShape.HasUsableRawEndpoint);
            VerifyEffective(effectiveAuthored, AvatarBaseShape.HasUsableMorphEndpoint);
            // Automatic blink was resolved against the source before appearance
            // snapshots. Carry its requirement across that earlier stage too.
            if (SupportsUnified || requireUsableEvidence)
            {
                if (avatar == null || !avatar.activeInHierarchy) throw new InvalidOperationException(LostTracking);
                var current = new UnifiedExpressionPreparation(avatar, excluded);
                if (!current.SupportsUnified || usableAuthoredNames.Any(name => !current.usableAuthoredNames.Contains(name)) ||
                    effectiveAuthoredRoutes.Any(route => !current.effectiveAuthoredRoutes.Contains(route)))
                    throw new InvalidOperationException(LostTracking);
                foreach (var pair in effectiveRaw)
                    foreach (var name in pair.Value)
                    {
                        UnifiedExpressionRegistry.TryCanonicalize(name, out var canonical);
                        if (!current.selectedRawRoutes.TryGetValue(pair.Key, out var routes) ||
                            !routes.TryGetValue(canonical, out var selectedName) || !string.Equals(name, selectedName, StringComparison.Ordinal))
                            throw new InvalidOperationException(LostTracking);
                    }
            }
        }

        private static void VerifyEffective(Dictionary<SkinnedMeshRenderer, string[]> targets,
            Func<SkinnedMeshRenderer, int, bool> usable)
        {
            foreach (var pair in targets)
            {
                var skin = pair.Key;
                if (skin == null || !skin.enabled || !skin.gameObject.activeInHierarchy || skin.sharedMesh == null ||
                    pair.Value.Any(name => !usable(skin, skin.sharedMesh.GetBlendShapeIndex(name))))
                    throw new InvalidOperationException(LostTracking);
            }
        }
    }
}
