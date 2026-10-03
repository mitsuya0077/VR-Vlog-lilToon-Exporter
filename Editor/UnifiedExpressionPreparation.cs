using System;
using System.Collections.Generic;
using System.Linq;
using UniGLTF.Extensions.VRMC_vrm;
using UniVRM10;
using UnityEngine;
using VRM10.MToon10;
using VRVlog.FaceTracking;

namespace VRVlog.LilToonExporter
{
    // Preserve required morph identity across authoring passes. Names on another
    // renderer cannot conceal loss of the tracked face or a selected endpoint.
    internal sealed class UnifiedExpressionPreparation
    {
        private readonly Dictionary<SkinnedMeshRenderer, string[]> required = new Dictionary<SkinnedMeshRenderer, string[]>();
        private readonly Dictionary<SkinnedMeshRenderer, string[]> effectiveRaw = new Dictionary<SkinnedMeshRenderer, string[]>();
        private readonly HashSet<string> usableAuthoredNames = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<(string Canonical, SkinnedMeshRenderer Renderer, string Shape, float Weight)> effectiveAuthoredRoutes =
            new HashSet<(string, SkinnedMeshRenderer, string, float)>();
        private readonly HashSet<(string Canonical, string Material, string Kind)> effectiveMaterialRoutes = new HashSet<(string, string, string)>();
        private readonly Dictionary<string, (Renderer Renderer, int Slot)> materialScopes = new Dictionary<string, (Renderer, int)>(StringComparer.Ordinal);
        private readonly HashSet<(string Material, Renderer Renderer, int Slot)> referencedMaterialScopes = new HashSet<(string, Renderer, int)>();
        private readonly HashSet<string> ambiguousMaterials = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<SkinnedMeshRenderer, Dictionary<string, string>> selectedRawRoutes = new Dictionary<SkinnedMeshRenderer, Dictionary<string, string>>();
        private readonly Dictionary<(SkinnedMeshRenderer Renderer, string Shape), float> capturedWeights =
            new Dictionary<(SkinnedMeshRenderer, string), float>();
        // Preserve baselines for every potentially selected raw channel and
        // declared authored UE morph. Unrelated body/clothing keys never affect
        // this guard and need no arbitrary baseline after renderer merging.
        private readonly HashSet<(SkinnedMeshRenderer Renderer, string Shape)> baselineChannels =
            new HashSet<(SkinnedMeshRenderer, string)>();
        private readonly bool declaresUnified;
        private readonly Func<SkinnedMeshRenderer, int, float> neutralWeight;
        private readonly GameObject avatar;
        private readonly Func<Transform, bool> excluded;
        private readonly bool suppressSharedTextureEmission, suppressHdrTextureEmission;
        internal const string LostTracking = "Modular Avatar / NDMF の処理で Unified Expressions の追跡用変形が失われました。メッシュや BlendShape を変更する追加ツールの設定を確認してください。";

        internal const string ConflictingMergedRoutes = "Modular Avatar / NDMF のメッシュ統合で同じ追跡名に異なる BlendShape が対応しました。元の追跡設定を統一してから書き出してください。";
        internal const string ConflictingMergedWeights = "Modular Avatar / NDMF のメッシュ統合で同じ BlendShape の元の初期値が異なります。元の形を一意に保存できないため、初期値を統一してから書き出してください。";

        internal bool SupportsUnified { get; private set; }

        internal IEnumerable<(SkinnedMeshRenderer Renderer, string Shape, string Canonical)> OptimizationRawRoutes =>
            selectedRawRoutes.SelectMany(pair => pair.Value.Select(route => (pair.Key, route.Value, route.Key)));

        internal IEnumerable<(SkinnedMeshRenderer Renderer, string Shape)> OptimizationMorphs =>
            required.SelectMany(pair => pair.Value.Select(shape => (pair.Key, shape)));

        internal IEnumerable<(string Canonical, string Kind, Renderer Renderer, int Slot)> OptimizationMaterialEvidence =>
            effectiveMaterialRoutes.Select(route => (route.Canonical, route.Kind,
                materialScopes[route.Material].Renderer, materialScopes[route.Material].Slot));

        internal static bool HasUsableEvidence(GameObject source, Func<Transform, bool> excluded = null,
            bool suppressSharedTextureEmission = false, bool suppressHdrTextureEmission = false) =>
            new UnifiedExpressionPreparation(source, excluded, suppressSharedTextureEmission, suppressHdrTextureEmission).SupportsUnified;

        internal UnifiedExpressionPreparation(GameObject clone, Func<Transform, bool> excluded = null,
            bool suppressSharedTextureEmission = false, bool suppressHdrTextureEmission = false,
            Func<SkinnedMeshRenderer, int, float> neutralWeight = null)
        {
            avatar = clone;
            this.excluded = excluded;
            this.suppressSharedTextureEmission = suppressSharedTextureEmission;
            this.suppressHdrTextureEmission = suppressHdrTextureEmission;
            this.neutralWeight = neutralWeight ?? ((skin, index) => skin.GetBlendShapeWeight(index));
            var renderers = ExportRendererSelection.Enumerate(clone)
                .Where(renderer => excluded?.Invoke(renderer.transform) != true).ToArray();
            var meshes = renderers.OfType<SkinnedMeshRenderer>()
                .Where(skin => skin.sharedMesh != null).ToDictionary(skin => skin, skin =>
                    Enumerable.Range(0, skin.sharedMesh.blendShapeCount).Select(skin.sharedMesh.GetBlendShapeName).ToArray());
            foreach (var pair in meshes)
                for (var index = 0; index < pair.Value.Length; index++)
                {
                    var shape = pair.Value[index];
                    capturedWeights[(pair.Key, shape)] = this.neutralWeight(pair.Key, index);
                    // Shared raw aliases may become eligible when a build adds
                    // explicit UE evidence, so retain them even before that gate.
                    if (UnifiedExpressionRegistry.TryCanonicalize(shape, out _)) baselineChannels.Add((pair.Key, shape));
                }
            var materialMap = renderers.SelectMany(renderer => renderer.sharedMaterials).Where(material => material != null)
                .GroupBy(material => material.name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var materialSlots = renderers.SelectMany(renderer => renderer.sharedMaterials.Select((material, slot) =>
                (Renderer: renderer, Slot: slot, Material: material))).Where(item => item.Material != null)
                .GroupBy(item => item.Material.name, StringComparer.Ordinal).ToArray();
            foreach (var group in materialSlots)
            {
                var first = group.First(); materialScopes.Add(group.Key, (first.Renderer, first.Slot));
            }
            ambiguousMaterials.UnionWith(materialSlots.Where(group => group.Select(item => item.Material).Distinct().Count() > 1)
                .Select(group => group.Key));
            var materials = new HashSet<string>(materialMap.Keys, StringComparer.Ordinal);
            var omittedMaterials = new HashSet<string>(clone.GetComponentsInChildren<Renderer>(true)
                .Where(renderer => excluded?.Invoke(renderer.transform) == true).SelectMany(renderer => renderer.sharedMaterials)
                .Where(material => material != null).Select(material => material.name), StringComparer.Ordinal);
            omittedMaterials.ExceptWith(materials);
            var clips = clone.GetComponent<Vrm10Instance>()?.Vrm?.Expression?.CustomClips?.Where(clip => clip != null).ToArray()
                ?? Array.Empty<VRM10Expression>();
            declaresUnified = VrmUnifiedExpressions.HasEvidence(meshes.Values.SelectMany(names => names).Concat(clips.Select(clip => clip.name)));
            if (!declaresUnified) return;
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
                // Keep the original first target even when another distinct
                // same-name material is introduced by a later authoring pass.
                foreach (var name in RetainedColors(clip, omittedMaterials).Select(binding => binding.MaterialName)
                    .Concat(RetainedUV(clip, omittedMaterials).Select(binding => binding.MaterialName)))
                    if (name != null && materialScopes.TryGetValue(name, out var scope))
                        referencedMaterialScopes.Add((name, scope.Renderer, scope.Slot));

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
                    baselineChannels.Add((skin, names[binding.Index]));
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
                    return preferred.Length == 1 && IsUsable(preferred[0], clone, materialMap, meshes, excluded, omittedMaterials, suppressSharedTextureEmission, suppressHdrTextureEmission)
                        ? preferred : Array.Empty<VRM10Expression>();
                }).ToArray();
            usableAuthoredNames.UnionWith(usableClips.Select(clip => clip.name));
            var usableAuthored = usableAuthoredNames.ToArray();
            // Blink validation uses the same alias, authored-coverage and
            // representation selection as preparation. A retained empty or
            // disabled route and an inert/fully resting raw shape cannot alone
            // waive the ordinary missing-blink safeguard.
            SupportsUnified = VrmUnifiedExpressions.HasEvidence(usableAuthored.Concat(selected.SelectMany(pair =>
                pair.Value.Values.Where(index => RawUsable(pair.Key, index))
                    .Select(index => meshes[pair.Key][index]))));
            if (SupportsUnified)
            {
                foreach (var pair in selected)
                {
                    var shapes = pair.Value.Values.Where(index => RawUsable(pair.Key, index))
                        .Select(index => meshes[pair.Key][index]).ToArray();
                    if (shapes.Length > 0) effectiveRaw.Add(pair.Key, shapes);
                }
                foreach (var clip in usableClips)
                {
                    UnifiedExpressionRegistry.TryCanonicalize(clip.name, out var canonical);
                    foreach (var route in MaterialRoutes(clip, materialMap, omittedMaterials, suppressSharedTextureEmission, suppressHdrTextureEmission))
                        effectiveMaterialRoutes.Add((canonical, route.Material, route.Kind));
                    foreach (var binding in EffectiveMorphs(clip, clone, excluded))
                    {
                        var target = Target(clone, binding.RelativePath);
                        var skin = target == null ? null : target.GetComponent<SkinnedMeshRenderer>();
                        if (binding.Weight > 0 && AvatarBaseShape.HasUsableMorphEndpoint(skin.sharedMesh, binding.Index,
                                this.neutralWeight(skin, binding.Index), binding.Weight * 100f))
                            effectiveAuthoredRoutes.Add((canonical, skin, skin.sharedMesh.GetBlendShapeName(binding.Index), binding.Weight));
                    }
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

        private bool IsUsable(VRM10Expression clip, GameObject clone, IDictionary<string, Material> materials,
            IDictionary<SkinnedMeshRenderer, string[]> meshes, Func<Transform, bool> excluded, ISet<string> omittedMaterials,
            bool suppressSharedTextureEmission, bool suppressHdrTextureEmission)
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
                positiveMorph |= targets.Add((skin, binding.Index)) && binding.Weight > 0 &&
                    AvatarBaseShape.HasUsableMorphEndpoint(skin.sharedMesh, binding.Index,
                        neutralWeight(skin, binding.Index), binding.Weight * 100f);
            }
            foreach (var binding in RetainedColors(clip, omittedMaterials))
                if (!materials.ContainsKey(binding.MaterialName) || !Enum.IsDefined(typeof(MaterialColorType), binding.BindType) ||
                    !Finite(binding.TargetValue.x) || !Finite(binding.TargetValue.y) || !Finite(binding.TargetValue.z) || !Finite(binding.TargetValue.w)) return false;
            foreach (var binding in RetainedUV(clip, omittedMaterials))
                if (!materials.ContainsKey(binding.MaterialName) || !Finite(binding.Scaling.x) || !Finite(binding.Scaling.y) ||
                    !Finite(binding.Offset.x) || !Finite(binding.Offset.y)) return false;
            return positiveMorph || MaterialRoutes(clip, materials, omittedMaterials, suppressSharedTextureEmission, suppressHdrTextureEmission).Any();
        }

        private static IEnumerable<(string Material, string Kind)> MaterialRoutes(VRM10Expression clip,
            IDictionary<string, Material> materials, ISet<string> omittedMaterials, bool suppressSharedTextureEmission, bool suppressHdrTextureEmission)
        {
            // UniVRM sums all target-minus-base contributions for a property.
            // Opposing authored endpoints can therefore cancel completely.
            foreach (var group in RetainedColors(clip, omittedMaterials).GroupBy(binding => (binding.MaterialName, binding.BindType)))
            {
                if (!materials.TryGetValue(group.Key.MaterialName, out var material)) continue;
                var property = ColorProperty(group.Key.BindType);
                if (property == null || !LilToonMaterialReader.IsLilToon(material) && !material.HasProperty(property)) continue;
                Vector4 baseline = LilToonMaterialReader.IsLilToon(material)
                    ? (Vector4)(group.Key.BindType == MaterialColorType.emissionColor && suppressHdrTextureEmission && LilToonEmissionPolicy.HasHdrTextureEmission(material)
                        ? Color.black : UniVrmOneClickExporter.FallbackColor(material, group.Key.BindType, suppressSharedTextureEmission)) : material.GetVector(property);
                // Three-component glTF factors import with alpha one.
                if (group.Key.BindType != MaterialColorType.color) baseline.w = 1;
                var delta = Vector4.zero;
                foreach (var binding in group) delta += binding.TargetValue - baseline;
                if (Moving(delta)) yield return (group.Key.MaterialName, group.Key.BindType.ToString());
            }
            foreach (var group in RetainedUV(clip, omittedMaterials).GroupBy(binding => binding.MaterialName, StringComparer.Ordinal))
            {
                if (!materials.TryGetValue(group.Key, out var material) || !material.HasProperty("_MainTex")) continue;
                var scale = material.mainTextureScale; var offset = material.mainTextureOffset;
                var baseline = new Vector4(scale.x, scale.y, offset.x, offset.y);
                var delta = Vector4.zero;
                foreach (var binding in group) delta += binding.ScalingOffset - baseline;
                if (Moving(delta)) yield return (group.Key, "uv");
            }
        }

        internal static string ColorProperty(MaterialColorType type)
        {
            switch (type)
            {
                case MaterialColorType.color: return MToon10Prop.BaseColorFactor.ToUnityShaderLabName();
                case MaterialColorType.emissionColor: return MToon10Prop.EmissiveFactor.ToUnityShaderLabName();
                case MaterialColorType.shadeColor: return MToon10Prop.ShadeColorFactor.ToUnityShaderLabName();
                case MaterialColorType.matcapColor: return MToon10Prop.MatcapColorFactor.ToUnityShaderLabName();
                case MaterialColorType.rimColor: return MToon10Prop.ParametricRimColorFactor.ToUnityShaderLabName();
                case MaterialColorType.outlineColor: return MToon10Prop.OutlineColorFactor.ToUnityShaderLabName();
                default: return null;
            }
        }

        private static bool Moving(Vector4 delta) => Finite(delta.x) && Finite(delta.y) && Finite(delta.z) && Finite(delta.w) &&
            (Mathf.Abs(delta.x) > .00001f || Mathf.Abs(delta.y) > .00001f || Mathf.Abs(delta.z) > .00001f || Mathf.Abs(delta.w) > .00001f);

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

        private bool RawUsable(SkinnedMeshRenderer skin, int index) => skin != null && skin.sharedMesh != null &&
            index >= 0 && index < skin.sharedMesh.blendShapeCount &&
            AvatarBaseShape.HasUsableRawEndpoint(skin.sharedMesh, index, neutralWeight(skin, index));

        private float CapturedWeight(SkinnedMeshRenderer skin, int index) =>
            capturedWeights.TryGetValue((skin, skin.sharedMesh.GetBlendShapeName(index)), out var weight)
                ? weight : skin.GetBlendShapeWeight(index);

        // A build pass can intentionally change serialized weights before FX
        // establishes neutral. Protect the selected channels and their geometry
        // against the captured reference, then assess output usability anew.
        internal void VerifyIdentityAndDeformation() => Verify(false, true);

        internal void RebindPrepared(Func<SkinnedMeshRenderer, SkinnedMeshRenderer> replacement)
        {
            if (replacement == null) throw new ArgumentNullException(nameof(replacement));
            var mapped = new Dictionary<SkinnedMeshRenderer, SkinnedMeshRenderer>();
            SkinnedMeshRenderer Resolve(SkinnedMeshRenderer old)
            {
                if (!mapped.TryGetValue(old, out var current)) mapped.Add(old, current = replacement(old));
                return current;
            }
            SkinnedMeshRenderer Map(SkinnedMeshRenderer old)
            {
                var current = Resolve(old);
                if (current == null) throw new InvalidOperationException(NdmfExportPreparation.UnknownRendererRelocation);
                return current;
            }
            Dictionary<SkinnedMeshRenderer, string[]> Remap(Dictionary<SkinnedMeshRenderer, string[]> values)
            {
                var result = new Dictionary<SkinnedMeshRenderer, string[]>();
                foreach (var pair in values)
                {
                    var current = Map(pair.Key);
                    result[current] = result.TryGetValue(current, out var previous)
                        ? previous.Concat(pair.Value).Distinct(StringComparer.Ordinal).ToArray() : pair.Value;
                }
                return result;
            }
            var nextRequired = Remap(required);
            var nextEffectiveRaw = Remap(effectiveRaw);
            var nextRaw = new Dictionary<SkinnedMeshRenderer, Dictionary<string, string>>();
            foreach (var pair in selectedRawRoutes)
            {
                var current = Resolve(pair.Key);
                if (current == null) continue;
                if (!nextRaw.TryGetValue(current, out var routes))
                    nextRaw.Add(current, routes = new Dictionary<string, string>(StringComparer.Ordinal));
                foreach (var route in pair.Value)
                {
                    if (routes.TryGetValue(route.Key, out var shape) && !string.Equals(shape, route.Value, StringComparison.Ordinal))
                        throw new InvalidOperationException(ConflictingMergedRoutes + ": " + current.name + " / " + route.Key);
                    routes[route.Key] = route.Value;
                }
            }
            // Shared aliases are speculative until source or prepared output
            // declares UE. Use structural evidence here: final FX can reopen a
            // serialized fully-resting channel only after this identity guard.
            var preparedNames = ExportRendererSelection.Enumerate(avatar).OfType<SkinnedMeshRenderer>()
                .Where(skin => skin.sharedMesh != null && excluded?.Invoke(skin.transform) != true)
                .SelectMany(skin => Enumerable.Range(0, skin.sharedMesh.blendShapeCount).Select(skin.sharedMesh.GetBlendShapeName));
            var preparedClips = avatar.GetComponent<Vrm10Instance>()?.Vrm?.Expression?.CustomClips
                ?.Where(clip => clip != null).Select(clip => clip.name) ?? Enumerable.Empty<string>();
            var preserveBaselines = declaresUnified || VrmUnifiedExpressions.HasEvidence(preparedNames.Concat(preparedClips));
            var nextWeights = new Dictionary<(SkinnedMeshRenderer Renderer, string Shape), float>();
            foreach (var pair in capturedWeights)
            {
                if (!preserveBaselines || !baselineChannels.Contains(pair.Key)) continue;
                var current = Resolve(pair.Key.Renderer);
                if (current == null) continue;
                var key = (current, pair.Key.Shape);
                if (nextWeights.TryGetValue(key, out var weight) && !weight.Equals(pair.Value))
                    throw new InvalidOperationException(ConflictingMergedWeights + ": " + current.name + " / " + pair.Key.Shape);
                nextWeights[key] = pair.Value;
            }
            var nextAuthored = new HashSet<(string Canonical, SkinnedMeshRenderer Renderer, string Shape, float Weight)>(
                effectiveAuthoredRoutes.Select(route => (route.Canonical, Map(route.Renderer), route.Shape, route.Weight)));
            Renderer MaterialTarget(Renderer renderer) => renderer is SkinnedMeshRenderer skin ? Map(skin) : renderer;
            var nextMaterialScopes = materialScopes.ToDictionary(pair => pair.Key, pair => {
                var scope = pair.Value;
                // An unselected omitted material scope does not require relocation.
                var current = scope.Renderer is SkinnedMeshRenderer skin ? Resolve(skin) : null;
                return current != null ? (Renderer: (Renderer)current, scope.Slot) : scope;
            }, StringComparer.Ordinal);
            var nextReferencedScopes = new HashSet<(string Material, Renderer Renderer, int Slot)>(
                referencedMaterialScopes.Select(scope => (scope.Material, MaterialTarget(scope.Renderer), scope.Slot)));

            // Publish only once every original route has a compatible destination.
            // Equal baselines can share one final channel; conflicting origins cannot.
            void Replace<TKey, TValue>(Dictionary<TKey, TValue> target, Dictionary<TKey, TValue> values)
            {
                target.Clear(); foreach (var pair in values) target.Add(pair.Key, pair.Value);
            }
            Replace(required, nextRequired); Replace(effectiveRaw, nextEffectiveRaw);
            Replace(selectedRawRoutes, nextRaw); Replace(capturedWeights, nextWeights);
            baselineChannels.Clear(); baselineChannels.UnionWith(nextWeights.Keys);
            effectiveAuthoredRoutes.Clear(); effectiveAuthoredRoutes.UnionWith(nextAuthored);
            Replace(materialScopes, nextMaterialScopes);
            referencedMaterialScopes.Clear(); referencedMaterialScopes.UnionWith(nextReferencedScopes);
        }


        internal void Verify(bool requireUsableEvidence = false) => Verify(requireUsableEvidence, false);

        private void Verify(bool requireUsableEvidence, bool capturedBaseline)
        {
            foreach (var pair in required)
            {
                var skin = pair.Key;
                if (skin == null || !skin.enabled || !skin.gameObject.activeInHierarchy || skin.sharedMesh == null ||
                    pair.Value.Any(name => skin.sharedMesh.GetBlendShapeIndex(name) < 0))
                    throw new InvalidOperationException(LostTracking);
            }
            Func<SkinnedMeshRenderer, int, float> weight = (skin, index) =>
                capturedBaseline ? CapturedWeight(skin, index) : skin.GetBlendShapeWeight(index);
            VerifyEffective(effectiveRaw, (skin, index) => index >= 0 &&
                AvatarBaseShape.HasUsableRawEndpoint(skin.sharedMesh, index, weight(skin, index)));
            foreach (var route in effectiveAuthoredRoutes)
            {
                var skin = route.Renderer;
                var index = skin == null || skin.sharedMesh == null ? -1 : skin.sharedMesh.GetBlendShapeIndex(route.Shape);
                if (skin == null || !skin.enabled || !skin.gameObject.activeInHierarchy || index < 0 ||
                    !AvatarBaseShape.HasUsableMorphEndpoint(skin.sharedMesh, index, weight(skin, index), route.Weight * 100f))
                    throw new InvalidOperationException(LostTracking);
            }
            // Output verification uses the current neutral. Identity verification
            // uses the captured baseline while build passes establish that neutral.
            if (SupportsUnified || requireUsableEvidence)
            {
                if (avatar == null || !avatar.activeInHierarchy) throw new InvalidOperationException(LostTracking);
                var current = new UnifiedExpressionPreparation(avatar, excluded, suppressSharedTextureEmission, suppressHdrTextureEmission,
                    capturedBaseline ? CapturedWeight : (Func<SkinnedMeshRenderer, int, float>)null);
                if (!current.SupportsUnified || usableAuthoredNames.Any(name => !current.usableAuthoredNames.Contains(name)) ||
                    effectiveAuthoredRoutes.Any(route => !current.effectiveAuthoredRoutes.Contains(route)) ||
                    effectiveMaterialRoutes.Any(route => !current.effectiveMaterialRoutes.Contains(route)) ||
                    referencedMaterialScopes.Any(scope => (ambiguousMaterials.Contains(scope.Material) || current.ambiguousMaterials.Contains(scope.Material)) &&
                        (!current.materialScopes.TryGetValue(scope.Material, out var selected) || selected.Renderer != scope.Renderer || selected.Slot != scope.Slot)))
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

        // Fallback baking changes material baselines after AAO has moved the
        // renderers. Check the mapped slots rather than the original hierarchy
        // or raw morph names, and preserve each formerly moving material route.
        internal void VerifyMappedMaterialEvidence(IEnumerable<(string Canonical, string Kind, Renderer Renderer, int Slot)> routes)
        {
            var expected = routes.ToArray();
            if (expected.Length == 0) return;
            if (avatar == null || !avatar.activeInHierarchy) throw new InvalidOperationException(LostTracking);
            var materials = ExportRendererSelection.Enumerate(avatar).SelectMany(renderer => renderer.sharedMaterials)
                .Where(material => material != null).GroupBy(material => material.name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var clips = avatar.GetComponent<Vrm10Instance>()?.Vrm?.Expression?.CustomClips?.Where(clip => clip != null).ToArray()
                ?? Array.Empty<VRM10Expression>();
            var selected = clips.Where(clip => UnifiedExpressionRegistry.TryCanonicalize(clip.name, out _))
                .GroupBy(clip => { UnifiedExpressionRegistry.TryCanonicalize(clip.name, out var canonical); return canonical; }, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => {
                    var priority = group.Min(clip => VrmUnifiedExpressions.RawPriority(clip.name, group.Key));
                    var preferred = group.Where(clip => VrmUnifiedExpressions.RawPriority(clip.name, group.Key) == priority).ToArray();
                    return preferred.Length == 1 ? preferred[0] : null;
                }, StringComparer.Ordinal);
            var omitted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var route in expected)
            {
                var renderer = route.Renderer;
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy ||
                    !renderer.transform.IsChildOf(avatar.transform) || route.Slot < 0 || route.Slot >= renderer.sharedMaterials.Length)
                    throw new InvalidOperationException(LostTracking);
                var material = renderer.sharedMaterials[route.Slot];
                if (material == null || !materials.TryGetValue(material.name, out var selectedMaterial) || selectedMaterial != material ||
                    !selected.TryGetValue(route.Canonical, out var clip) || clip == null ||
                    !MaterialRoutes(clip, materials, omitted, suppressSharedTextureEmission, suppressHdrTextureEmission)
                        .Any(endpoint => endpoint.Material == material.name && endpoint.Kind == route.Kind))
                    throw new InvalidOperationException(LostTracking);
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
