using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UniGLTF;
using UniGLTF.Extensions.VRMC_vrm;
using UniVRM10;
using UnityEditor;
using UnityEngine;
using VRVlog.FaceTracking;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter
{
    // Keeps expression routes by renderer identity while optimizers remove,
    // rename and merge meshes. All Unity assets created here belong to the
    // disposable clone; the profile and original VRM assets remain untouched.
    internal sealed class ExportOptimizationBindings : IDisposable
    {
        sealed class ClipSnapshot
        {
            internal VRM10Expression Template;
            internal (MorphTargetBinding Binding, OptimizationMorphRoute Route)[] Morphs;
            internal (MaterialColorBinding Binding, OptimizationMaterialRoute Route)[] Colors;
            internal (MaterialUVBinding Binding, OptimizationMaterialRoute Route)[] UV;
        }

        sealed class ProfileExpression
        {
            internal string Name;
            internal readonly List<(OptimizationMorphRoute Route, float Weight)> Morphs = new List<(OptimizationMorphRoute, float)>();
        }

        readonly GameObject avatar;
        readonly ExportOptimizationMarker marker;
        readonly Dictionary<(int Renderer, string Shape), OptimizationMorphRoute> morphs = new Dictionary<(int, string), OptimizationMorphRoute>();
        readonly Dictionary<(int Renderer, int Slot), OptimizationMaterialRoute> materials = new Dictionary<(int, int), OptimizationMaterialRoute>();
        readonly Dictionary<VRM10Expression, ClipSnapshot> clips = new Dictionary<VRM10Expression, ClipSnapshot>();
        readonly List<Action<VRM10ObjectExpression, Dictionary<VRM10Expression, VRM10Expression>>> rebuildClips =
            new List<Action<VRM10ObjectExpression, Dictionary<VRM10Expression, VRM10Expression>>>();
        readonly HashSet<ExpressionPreset> capturedPresets = new HashSet<ExpressionPreset>();
        readonly HashSet<object> capturedCustomClips = new HashSet<object>();
        readonly object objectRegistry;
        readonly MethodInfo assetReference;
        readonly List<(string Canonical, OptimizationMorphRoute Route)> unified = new List<(string, OptimizationMorphRoute)>();
        readonly List<(string Canonical, string Kind, OptimizationMaterialRoute Route)> materialEvidence =
            new List<(string, string, OptimizationMaterialRoute)>();
        readonly UnifiedExpressionPreparation materialEvidencePreparation;
        readonly List<ProfileExpression> profile = new List<ProfileExpression>();
        readonly List<Object> owned = new List<Object>();
        readonly Dictionary<SkinnedMeshRenderer, int> nodes = new Dictionary<SkinnedMeshRenderer, int>();
        readonly bool supportsUnified;
        readonly bool expectsMapping;
        bool applied, disposed;

        internal static ExportOptimizationBindings Capture(GameObject clone, VrmTrackingProfile trackingProfile = null,
            UnifiedExpressionPreparation preparation = null, object objectRegistry = null, NeutralShapeSnapshot neutral = null)
        {
            if (clone == null) throw new ArgumentNullException(nameof(clone));
            ExportRendererSelection.RequireActiveRoot(clone);
            if (EditorUtility.IsPersistent(clone)) throw new ArgumentException("An independent export copy is required.", nameof(clone));
            return new ExportOptimizationBindings(clone, trackingProfile, preparation, objectRegistry, neutral);
        }

        ExportOptimizationBindings(GameObject clone, VrmTrackingProfile trackingProfile, UnifiedExpressionPreparation preparation,
            object objectRegistry, NeutralShapeSnapshot neutral)
        {
            avatar = clone;
            this.objectRegistry = objectRegistry;
            if (objectRegistry != null)
            {
                // AAO registers cloned expression assets with the build's
                // public NDMF registry. Retain that provenance after Finish;
                // clip names and CustomClips list positions are mutable.
                var registryInterface = objectRegistry.GetType().GetInterfaces().FirstOrDefault(type => type.FullName == "nadena.dev.ndmf.IObjectRegistry");
                assetReference = registryInterface?.GetMethod("GetReference", new[] { typeof(Object), typeof(bool) });
                if (assetReference == null) throw new InvalidOperationException("最適化後の表情の移動情報を取得できませんでした。");
            }
            if (clone.GetComponent<ExportOptimizationMarker>() != null)
                throw new InvalidOperationException("表情の最適化保護が既に開始されています。");
            expectsMapping = clone.GetComponentsInChildren<Component>(true).Any(IsAvatarOptimizerAuthoring);
            if (expectsMapping && !ExportOptimizationMarker.AvatarOptimizerAdapterAvailable)
                throw new InvalidOperationException("Avatar Optimizer の表情互換処理を読み込めません。対応する Avatar Optimizer 1.7 以上・2.0 未満を導入し、Unity のコンパイル完了後に再度書き出してください。");
            marker = clone.AddComponent<ExportOptimizationMarker>();
            marker.hideFlags = HideFlags.HideInInspector | HideFlags.DontSave;
            try
            {
                foreach (var skin in ExportRendererSelection.Enumerate(clone).OfType<SkinnedMeshRenderer>())
                    if (skin.sharedMesh != null)
                        for (var index = 0; index < skin.sharedMesh.blendShapeCount; index++)
                        {
                            var name = skin.sharedMesh.GetBlendShapeName(index);
                            if (Generated(name)) AddMorph(skin, name);
                        }
                CaptureAuthored();
                if (trackingProfile != null) CaptureProfile(trackingProfile);
                else
                {
                    preparation = preparation ?? new UnifiedExpressionPreparation(clone);
                    materialEvidencePreparation = preparation;
                    supportsUnified = preparation.SupportsUnified || VrmUnifiedExpressions.HasEvidence(preparation.OptimizationRawRoutes.Select(route => route.Shape));
                    foreach (var route in preparation.OptimizationMorphs) AddMorph(route.Renderer, route.Shape, requireUsableEndpoint: false);
                    foreach (var route in preparation.OptimizationRawRoutes)
                        unified.Add((route.Canonical, AddMorph(route.Renderer, route.Shape)));
                    foreach (var evidence in preparation.OptimizationMaterialEvidence)
                    {
                        if (evidence.Renderer == null || !materials.TryGetValue((evidence.Renderer.GetInstanceID(), evidence.Slot), out var route))
                            throw new InvalidOperationException(UnifiedExpressionPreparation.LostTracking);
                        materialEvidence.Add((evidence.Canonical, evidence.Kind, route));
                    }
                }
                marker.Morphs = morphs.Values.ToArray();
                marker.Materials = materials.Values.ToArray();
                // These raw curves still exist in the prepared FX controller,
                // but their neutral contributions are already in mesh vertices.
                // Mark rebased properties as variable so AAO cannot apply their
                // old constant animation values to the new residual a second time.
                marker.PropertyMutations = marker.PropertyMutations.Concat((neutral?.Renderers ?? Array.Empty<NeutralShapeSnapshot.RendererState>())
                    .Where(state => state.Renderer != null && state.Renderer.sharedMesh != null)
                    .Select(state => new OptimizationPropertyMutation {
                        Renderer = state.Renderer,
                        Properties = Enumerable.Range(0, state.Weights.Length)
                            .Where(index => state.Weights[index] != 0f)
                            .Select(index => state.OriginalMesh.GetBlendShapeName(index))
                            .Where(name => state.Renderer.sharedMesh.GetBlendShapeIndex(name) >= 0)
                            .Select(name => "blendShape." + name).ToArray()
                    }).Where(mutation => mutation.Properties.Length != 0)).ToArray();
                marker.Dependencies = marker.Dependencies.Concat(marker.PropertyMutations
                    .Select(mutation => (Component)mutation.Renderer)).Distinct().ToArray();
            }
            catch { Dispose(); throw; }
        }

        static bool IsAvatarOptimizerAuthoring(Component component)
        {
            if (component == null) return false;
            for (var type = component.GetType(); type != null; type = type.BaseType)
                if (type.FullName == "Anatawa12.AvatarOptimizer.AvatarTagComponent") return true;
            return false;
        }

        static bool Generated(string name) => name != null && (name.StartsWith("__VRVlog_Menu_", StringComparison.Ordinal) ||
            name.StartsWith("__VRVlog_Endpoint_", StringComparison.Ordinal) ||
            name.StartsWith("__VRVlog_Anim_", StringComparison.Ordinal) || name.StartsWith("__VRVlog_Blink_", StringComparison.Ordinal) ||
            name.StartsWith("__VRVlog_BlinkNone_", StringComparison.Ordinal) || name.StartsWith(UnifiedExpressionRegistry.RestPrefix, StringComparison.Ordinal));

        OptimizationMorphRoute AddMorph(SkinnedMeshRenderer skin, string name, bool requireUsableEndpoint = true)
        {
            if (skin == null || skin.sharedMesh == null || string.IsNullOrEmpty(name) || skin.sharedMesh.GetBlendShapeIndex(name) < 0)
                throw new InvalidOperationException("最適化前の表情の対象を解決できません: " + name);
            var key = (skin.GetInstanceID(), name);
            var shapeIndex = skin.sharedMesh.GetBlendShapeIndex(name);
            if (morphs.TryGetValue(key, out var existing))
            {
                if (requireUsableEndpoint && !existing.RequireUsableEndpoint)
                    existing.RequireUsableEndpoint = AvatarBaseShape.HasUsableMorphEndpoint(skin, shapeIndex);
                return existing;
            }
            var usable = AvatarBaseShape.HasUsableMorphEndpoint(skin, shapeIndex);
            var route = new OptimizationMorphRoute { SourceRendererId = key.Item1, SourceShape = name, Renderer = skin, Shape = name,
                Label = AnimationUtility.CalculateTransformPath(skin.transform, avatar.transform) + " / " + name,
                RequireUsableEndpoint = requireUsableEndpoint && usable, NoOp = !usable && ZeroMorph(skin.sharedMesh, shapeIndex) };
            morphs.Add(key, route);
            return route;
        }

        static bool ZeroMorph(Mesh mesh, int shape)
        {
            if (mesh.GetBlendShapeFrameCount(shape) == 0) return false;
            var vertices = new Vector3[mesh.vertexCount]; var normals = new Vector3[mesh.vertexCount]; var tangents = new Vector3[mesh.vertexCount];
            for (var frame = 0; frame < mesh.GetBlendShapeFrameCount(shape); frame++)
            {
                var weight = mesh.GetBlendShapeFrameWeight(shape, frame);
                if (float.IsNaN(weight) || float.IsInfinity(weight)) return false;
                mesh.GetBlendShapeFrameVertices(shape, frame, vertices, normals, tangents);
                if (vertices.Concat(normals).Concat(tangents).Any(delta => delta.x != 0 || delta.y != 0 || delta.z != 0)) return false;
            }
            return true;
        }

        void CaptureAuthored()
        {
            var instance = avatar.GetComponent<Vrm10Instance>();
            if (instance == null || instance.Vrm == null || instance.Vrm.Expression == null) return;
            marker.Dependencies = new Component[] { instance };
            var skins = new HashSet<SkinnedMeshRenderer>(ExportRendererSelection.Enumerate(avatar).OfType<SkinnedMeshRenderer>());
            var slots = ExportRendererSelection.Enumerate(avatar).SelectMany(renderer => renderer.sharedMaterials.Select((material, slot) =>
                (Renderer: renderer, Slot: slot, Material: material))).Where(slot => slot.Material != null).ToArray();
            var propertyMutations = new Dictionary<Renderer, HashSet<string>>();
            OptimizationMaterialRoute MaterialRoute(string name, IEnumerable<string> mutations)
            {
                var match = slots.FirstOrDefault(slot => string.Equals(slot.Material.name, name, StringComparison.Ordinal));
                if (match.Renderer == null) return null; // Retain malformed declarations for the existing VRM diagnostics.
                var key = (match.Renderer.GetInstanceID(), match.Slot);
                if (!materials.TryGetValue(key, out var route))
                {
                    route = new OptimizationMaterialRoute { SourceRendererId = key.Item1, SourceSlot = key.Item2,
                        Renderer = match.Renderer, Slot = match.Slot, Label = name };
                    materials.Add(key, route);
                }
                route.Mutations = route.Mutations.Concat(mutations).Distinct(StringComparer.Ordinal).ToArray();
                foreach (var renderer in slots.Where(slot => slot.Material == match.Material).Select(slot => slot.Renderer).Distinct())
                {
                    if (!propertyMutations.TryGetValue(renderer, out var properties)) propertyMutations.Add(renderer, properties = new HashSet<string>(StringComparer.Ordinal));
                    properties.UnionWith(mutations);
                }
                return route;
            }
            foreach (var entry in instance.Vrm.Expression.Clips)
            {
                var original = entry.Clip;
                if (original == null) continue;
                if (entry.Preset == ExpressionPreset.custom) capturedCustomClips.Add(ClipIdentity(original));
                else capturedPresets.Add(entry.Preset);
                if (!clips.ContainsKey(original))
                {
                    var template = Object.Instantiate(original); template.name = original.name; owned.Add(template);
                    var snapshot = new ClipSnapshot { Template = template };
                    snapshot.Morphs = (original.MorphTargetBindings ?? Array.Empty<MorphTargetBinding>()).Select(binding =>
                    {
                        var target = string.IsNullOrEmpty(binding.RelativePath) ? avatar.transform : avatar.transform.Find(binding.RelativePath);
                        var skin = target == null ? null : target.GetComponent<SkinnedMeshRenderer>();
                        var route = skin != null && skins.Contains(skin) && skin.sharedMesh != null &&
                            binding.Index >= 0 && binding.Index < skin.sharedMesh.blendShapeCount ? AddMorph(skin, skin.sharedMesh.GetBlendShapeName(binding.Index),
                                requireUsableEndpoint: binding.Weight > 0 && binding.Weight <= 1 && !float.IsInfinity(binding.Weight)) : null;
                        return (new MorphTargetBinding(binding.RelativePath, binding.Index, binding.Weight), route);
                    }).ToArray();
                    snapshot.Colors = (original.MaterialColorBindings ?? Array.Empty<MaterialColorBinding>()).Select(binding =>
                    {
                        var properties = new[] { UnifiedExpressionPreparation.ColorProperty(binding.BindType), SourceColorProperty(binding.BindType) }
                            .Where(property => property != null).Distinct(StringComparer.Ordinal);
                        var mutations = properties.SelectMany(property => new[] { ".r", ".g", ".b", ".a" }.Select(channel => "material." + property + channel)).ToArray();
                        return (new MaterialColorBinding { MaterialName = binding.MaterialName, BindType = binding.BindType, TargetValue = binding.TargetValue },
                            MaterialRoute(binding.MaterialName, mutations));
                    }).ToArray();
                    snapshot.UV = (original.MaterialUVBindings ?? Array.Empty<MaterialUVBinding>()).Select(binding =>
                        (new MaterialUVBinding { MaterialName = binding.MaterialName, Scaling = binding.Scaling, Offset = binding.Offset },
                            MaterialRoute(binding.MaterialName, new[] { "material._MainTex_ST.x", "material._MainTex_ST.y", "material._MainTex_ST.z", "material._MainTex_ST.w" }))).ToArray();
                    clips.Add(original, snapshot);
                }
                var preset = entry.Preset;
                rebuildClips.Add((expression, replacements) => expression.AddClip(preset, replacements[original]));
            }
            marker.PropertyMutations = propertyMutations.Select(pair => new OptimizationPropertyMutation { Renderer = pair.Key, Properties = pair.Value.ToArray() }).ToArray();
        }

        static string SourceColorProperty(MaterialColorType type)
        {
            switch (type)
            {
                case MaterialColorType.color: return "_Color";
                case MaterialColorType.emissionColor: return "_EmissionColor";
                case MaterialColorType.shadeColor: return "_ShadowColor";
                case MaterialColorType.matcapColor: return "_MatCapColor";
                case MaterialColorType.rimColor: return "_RimColor";
                case MaterialColorType.outlineColor: return "_OutlineColor";
                default: return null;
            }
        }

        void CaptureProfile(VrmTrackingProfile source)
        {
            var entries = source.expressions ?? Array.Empty<TrackingExpression>();
            if (entries.Length != VrmTrackingExpressions.Names.Length || entries.Any(entry => entry == null) ||
                entries.Select(entry => entry.name).Distinct(StringComparer.Ordinal).Count() != VrmTrackingExpressions.Names.Length ||
                VrmTrackingExpressions.Names.Any(name => entries.All(entry => entry.name != name)))
                throw new InvalidOperationException("追跡設定にはARKit標準52項目を一度ずつ設定してください。");
            var skins = ExportRendererSelection.Enumerate(avatar).OfType<SkinnedMeshRenderer>().Where(skin => skin.sharedMesh != null).ToArray();
            foreach (var entry in entries)
            {
                if (entry.morphs == null || entry.morphs.Length == 0) throw new InvalidOperationException("追跡表情にシェイプキーがありません: " + entry.name);
                var expression = new ProfileExpression { Name = entry.name };
                foreach (var morph in entry.morphs)
                {
                    if (morph == null || string.IsNullOrWhiteSpace(morph.shape) || float.IsNaN(morph.weight) || float.IsInfinity(morph.weight) || morph.weight <= 0 || morph.weight > 1)
                        throw new InvalidOperationException("追跡表情の設定が不正です: " + entry.name);
                    var targets = skins.Where(skin => skin.sharedMesh.GetBlendShapeIndex(morph.shape) >= 0).ToArray();
                    if (targets.Length == 0) throw new InvalidOperationException("VRMにシェイプキーがありません: " + morph.shape + " (" + entry.name + ")");
                    foreach (var target in targets) expression.Morphs.Add((AddMorph(target, morph.shape), morph.weight));
                }
                profile.Add(expression);
            }
        }

        internal void ValidateAndApply(Action<Object, Object> assetCopyObserver = null)
        {
            RequireAlive();
            if (applied) return;
            if (marker == null || expectsMapping && !marker.MappingApplied)
                throw new InvalidOperationException("最適化後の表情の移動情報を取得できませんでした。");
            if (marker.MappingError != null) throw new InvalidOperationException(marker.MappingError);
            // Detect a lossy merge before recreating no-op meshes, cloning
            // materials, invoking copy observers or replacing VRM settings.
            ValidateAuthoredMorphWeights();
            foreach (var route in morphs.Values)
            {
                if (route.NoOp && (route.Removed || route.Renderer == null || route.Renderer.sharedMesh == null || route.Renderer.sharedMesh.GetBlendShapeIndex(route.Shape) < 0))
                    RecreateNoOp(route);
                var skin = route.Renderer;
                if (route.Removed || skin == null || !skin.enabled || !skin.gameObject.activeInHierarchy ||
                    !skin.transform.IsChildOf(avatar.transform) || skin.sharedMesh == null || skin.sharedMesh.GetBlendShapeIndex(route.Shape) < 0 ||
                    route.RequireUsableEndpoint && !AvatarBaseShape.HasUsableMorphEndpoint(skin, skin.sharedMesh.GetBlendShapeIndex(route.Shape)))
                    throw new InvalidOperationException("最適化で書き出し用の表情が失われました: " + route.Label);
            }
            foreach (var route in materials.Values)
                if (route.Removed || route.Renderer == null || !route.Renderer.enabled || !route.Renderer.gameObject.activeInHierarchy ||
                    !route.Renderer.transform.IsChildOf(avatar.transform) || route.Slot < 0 || route.Slot >= route.Renderer.sharedMaterials.Length || route.Renderer.sharedMaterials[route.Slot] == null)
                    throw new InvalidOperationException("最適化でVRM表情のマテリアルが失われました: " + route.Label);
            ApplyAuthored(assetCopyObserver);
            applied = true;
            Object.DestroyImmediate(marker);
        }

        void RecreateNoOp(OptimizationMorphRoute route)
        {
            var skin = route.Renderer;
            if (skin == null || !skin.enabled || !skin.gameObject.activeInHierarchy || skin.sharedMesh == null || skin.sharedMesh.vertexCount == 0)
                skin = ExportRendererSelection.Enumerate(avatar).OfType<SkinnedMeshRenderer>().FirstOrDefault(candidate => candidate.sharedMesh != null && candidate.sharedMesh.vertexCount > 0);
            if (skin == null) throw new InvalidOperationException("変形しない表情の出力先メッシュがありません: " + route.Label);
            var mesh = Object.Instantiate(skin.sharedMesh); owned.Add(mesh); skin.sharedMesh = mesh;
            var name = (route.SourceShape.StartsWith("__VRVlog_BlinkNone_", StringComparison.Ordinal) ? "__VRVlog_BlinkNone_" : "__VRVlog_Empty_") + Guid.NewGuid().ToString("N");
            var zeros = new Vector3[mesh.vertexCount]; mesh.AddBlendShapeFrame(name, 100, zeros, zeros, zeros);
            route.Renderer = skin; route.Shape = name; route.Removed = false;
        }

        void ValidateAuthoredMorphWeights()
        {
            foreach (var snapshot in clips.Values)
            {
                var originals = new HashSet<OptimizationMorphRoute>();
                var weights = new Dictionary<(int Renderer, int Index), float>();
                foreach (var item in snapshot.Morphs)
                {
                    var route = item.Route;
                    // Pinned UniVRM playback uses the first declaration for an
                    // original property, including a first weight of zero.
                    // Only distinct source properties newly combined by the
                    // optimizer can introduce a conflicting output weight.
                    if (route == null || !originals.Add(route) || route.Removed || route.Renderer == null || route.Renderer.sharedMesh == null) continue;
                    var index = route.Renderer.sharedMesh.GetBlendShapeIndex(route.Shape);
                    if (index < 0) continue; // Lost routes retain the existing validation/recovery path.
                    var key = (route.Renderer.GetInstanceID(), index);
                    if (weights.TryGetValue(key, out var previous) && !previous.Equals(item.Binding.Weight))
                        throw new InvalidOperationException("最適化で元のVRM表情の適用量が衝突しました: " + snapshot.Template.name + " / " + route.Label);
                    weights[key] = item.Binding.Weight;
                }
            }
        }

        MorphTargetBinding[] RemapAuthoredMorphs(ClipSnapshot snapshot)
        {
            var result = new List<MorphTargetBinding>();
            var owners = new Dictionary<(int Renderer, int Index), OptimizationMorphRoute>();
            foreach (var item in snapshot.Morphs)
            {
                if (item.Route == null) { result.Add(item.Binding); continue; }
                var route = item.Route;
                var index = route.Renderer.sharedMesh.GetBlendShapeIndex(route.Shape);
                var key = (route.Renderer.GetInstanceID(), index);
                // Keep the winning original property's entire declaration
                // array: pinned UniVRM playback uses its first binding. Only
                // a distinct source property newly coalesced by AAO is omitted.
                if (owners.TryGetValue(key, out var owner) && !ReferenceEquals(owner, route)) continue;
                owners[key] = route;
                result.Add(new MorphTargetBinding(AnimationUtility.CalculateTransformPath(route.Renderer.transform, avatar.transform), index, item.Binding.Weight));
            }
            return result.ToArray();
        }

        object ClipIdentity(VRM10Expression clip) => assetReference == null ? clip :
            assetReference.Invoke(objectRegistry, new object[] { clip, true }) ?? clip;

        void ApplyAuthored(Action<Object, Object> assetCopyObserver)
        {
            if (clips.Count == 0) return;
            var instance = avatar.GetComponent<Vrm10Instance>();
            if (instance == null || instance.Vrm == null) throw new InvalidOperationException("最適化で元のVRM表情設定が失われました。");
            var settings = Object.Instantiate(instance.Vrm); settings.name = instance.Vrm.name; owned.Add(settings);
            var replacements = new Dictionary<VRM10Expression, VRM10Expression>();
            var materialNames = new Dictionary<OptimizationMaterialRoute, string>();
            foreach (var route in materials.Values)
            {
                var material = route.Renderer.sharedMaterials[route.Slot];
                var sameName = ExportRendererSelection.Enumerate(avatar).SelectMany(renderer => renderer.sharedMaterials)
                    .Where(candidate => candidate != null && string.Equals(candidate.name, material.name, StringComparison.Ordinal)).Distinct().ToArray();
                if (sameName.Length > 1)
                {
                    var copy = Object.Instantiate(material); copy.name = material.name + "__VRVlog_Expression_" + Guid.NewGuid().ToString("N"); owned.Add(copy);
                    assetCopyObserver?.Invoke(copy, material);
                    foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
                    {
                        var slots = renderer.sharedMaterials;
                        if (!slots.Any(candidate => candidate == material)) continue;
                        renderer.sharedMaterials = slots.Select(candidate => candidate == material ? copy : candidate).ToArray();
                    }
                    material = copy;
                }
                materialNames.Add(route, material.name);
            }
            foreach (var pair in clips)
            {
                var snapshot = pair.Value;
                var copy = Object.Instantiate(snapshot.Template); copy.name = snapshot.Template.name; owned.Add(copy);
                copy.Prefab = null; // The remapped routes belong to this export copy.
                copy.MorphTargetBindings = RemapAuthoredMorphs(snapshot);
                copy.MaterialColorBindings = snapshot.Colors.Select(item => new MaterialColorBinding { MaterialName = item.Route == null ? item.Binding.MaterialName : materialNames[item.Route],
                    BindType = item.Binding.BindType, TargetValue = item.Binding.TargetValue }).ToArray();
                copy.MaterialUVBindings = snapshot.UV.Select(item => new MaterialUVBinding { MaterialName = item.Route == null ? item.Binding.MaterialName : materialNames[item.Route],
                    Scaling = item.Binding.Scaling, Offset = item.Binding.Offset }).ToArray();
                replacements.Add(pair.Key, copy);
            }
            var additional = settings.Expression?.Clips.Where(entry => entry.Clip != null &&
                (entry.Preset == ExpressionPreset.custom ? !capturedCustomClips.Contains(ClipIdentity(entry.Clip)) : !capturedPresets.Contains(entry.Preset))).ToArray();
            settings.Expression = new VRM10ObjectExpression();
            foreach (var rebuild in rebuildClips) rebuild(settings.Expression, replacements);
            if (additional != null) foreach (var entry in additional) settings.Expression.AddClip(entry.Preset, entry.Clip);
            instance.Vrm = settings;
        }

        internal (SkinnedMeshRenderer Renderer, string Shape) MapMorph(SkinnedMeshRenderer renderer, string shape)
        {
            RequireApplied();
            // GetInstanceID remains available on destroyed managed Unity references.
            if (ReferenceEquals(renderer, null) || !morphs.TryGetValue((renderer.GetInstanceID(), shape), out var route))
                throw new InvalidOperationException("最適化前の表情の参照がありません: " + shape);
            return (route.Renderer, route.Shape);
        }

        internal void VerifyMaterialEvidence()
        {
            RequireApplied();
            materialEvidencePreparation?.VerifyMappedMaterialEvidence(materialEvidence.Select(evidence =>
                (evidence.Canonical, evidence.Kind, evidence.Route.Renderer, evidence.Route.Slot)));
        }

        internal string MapGeneratedName(string name)
        {
            RequireApplied();
            var routes = morphs.Values.Where(route => string.Equals(route.SourceShape, name, StringComparison.Ordinal)).ToArray();
            if (routes.Length != 1) throw new InvalidOperationException("表情の出力先を一意に特定できません: " + name);
            var mapped = routes[0].Shape;
            var matches = ExportRendererSelection.Enumerate(avatar).OfType<SkinnedMeshRenderer>()
                .Sum(skin => skin.sharedMesh == null ? 0 : Enumerable.Range(0, skin.sharedMesh.blendShapeCount).Count(index => skin.sharedMesh.GetBlendShapeName(index) == mapped));
            if (matches != 1) throw new InvalidOperationException("最適化後の表情名が重複しています: " + mapped);
            return mapped;
        }

        internal void Bind(ModelExporter converter, VrmLib.Model model, ExportingGltfData storage)
        {
            BindNodes(skin => converter.Nodes.TryGetValue(skin.gameObject, out var node) ? model.Nodes.IndexOf(node) : -1);
        }

        internal void BindNodes(Func<SkinnedMeshRenderer, int> nodeIndex)
        {
            RequireApplied();
            nodes.Clear();
            foreach (var skin in unified.Select(item => item.Route.Renderer).Concat(profile.SelectMany(expression => expression.Morphs).Select(item => item.Route.Renderer)).Distinct())
            {
                var index = nodeIndex(skin);
                if (index < 0)
                    throw new InvalidOperationException("追跡表情の対象が最終VRMにありません: " + skin.name);
                nodes.Add(skin, index);
            }
        }

        internal byte[] ApplyTracking(byte[] bytes)
        {
            RequireApplied();
            if (profile.Count == 0) return bytes;
            var glb = GlbDocument.Read(bytes);
            var custom = Custom(glb.Json);
            foreach (var expression in profile)
            {
                if (custom.ContainsKey(expression.Name)) throw new InvalidOperationException("既存のVRM表情と追跡名が重複しています: " + expression.Name);
                // AAO can coalesce independent source routes into one output
                // property. Register that property once, retaining its first
                // configured weight rather than adding repeated contributions.
                var binds = expression.Morphs.Select(item => OutputBinding(glb.Json, item.Route, item.Weight))
                    .GroupBy(binding => (binding["node"], binding["index"]))
                    .Select(group => (object)group.First());
                custom.Add(expression.Name, Expression(binds));
            }
            return glb.Write();
        }

        internal byte[] ApplyUnified(byte[] bytes, ICollection<string> warnings = null)
        {
            RequireApplied();
            if (!supportsUnified) return VrmUnifiedExpressions.Add(bytes, warnings);
            // Validate authored declarations with the existing implementation,
            // but suppress raw-name inference: optimization can move two same-
            // named raw routes into one mesh or rename them beyond recognition.
            var input = GlbDocument.Read(bytes);
            var meshList = JsonArray(input.Json, "meshes");
            var originalNames = new Dictionary<int, List<object>>();
            for (var index = 0; index < meshList.Count; index++)
            {
                var extras = Obj(meshList[index] as Dictionary<string, object>, "extras", false);
                if (extras == null || !extras.TryGetValue("targetNames", out var raw) || !(raw is List<object> names)) continue;
                originalNames.Add(index, names);
                extras["targetNames"] = names.Select((name, shape) => (object)("__VRVlog_Optimized_" + shape)).ToList();
            }
            var glb = GlbDocument.Read(VrmUnifiedExpressions.Add(input.Write(), warnings));
            var outputMeshes = JsonArray(glb.Json, "meshes");
            foreach (var pair in originalNames) Obj(outputMeshes[pair.Key] as Dictionary<string, object>, "extras", true)["targetNames"] = pair.Value;
            var custom = Custom(glb.Json);
            var added = 0;
            foreach (var group in unified.GroupBy(item => item.Canonical, StringComparer.Ordinal))
            {
                var aliases = custom.Keys.Where(name => UnifiedExpressionRegistry.TryCanonicalize(name, out var canonical) && canonical == group.Key).ToArray();
                if (aliases.Any(name => HasBindings(custom[name] as Dictionary<string, object>))) continue;
                foreach (var alias in aliases) custom.Remove(alias);
                var binds = group.Select(item => OutputBinding(glb.Json, item.Route, 1)).GroupBy(binding => (binding["node"], binding["index"]))
                    .Select(bindings => (object)bindings.First()).ToList();
                custom.Add(UnifiedExpressionRegistry.Prefix + group.Key, Expression(binds)); added++;
            }
            if (added > 0) warnings?.Add("Unified Expressions の追跡表情を " + added + " 項目登録しました。端末で検出できる動きだけを反映します。");
            return glb.Write();
        }

        Dictionary<string, object> OutputBinding(Dictionary<string, object> json, OptimizationMorphRoute route, float weight)
        {
            if (!nodes.TryGetValue(route.Renderer, out var node)) throw new InvalidOperationException("追跡表情の出力nodeが未確定です。");
            var nodeList = JsonArray(json, "nodes"); var meshList = JsonArray(json, "meshes");
            if (node < 0 || node >= nodeList.Count || !(nodeList[node] is Dictionary<string, object> outputNode) || !outputNode.TryGetValue("mesh", out var rawMesh))
                throw new InvalidOperationException("追跡表情の出力nodeが不正です。");
            var meshIndex = Convert.ToInt32(rawMesh);
            if (meshIndex < 0 || meshIndex >= meshList.Count || !(meshList[meshIndex] is Dictionary<string, object> mesh))
                throw new InvalidOperationException("追跡表情の出力メッシュが不正です。");
            var names = JsonArray(Obj(mesh, "extras", false), "targetNames");
            var matches = names.Select((name, index) => (name, index)).Where(pair => string.Equals(pair.name as string, route.Shape, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("追跡表情のモーフを一意に解決できません: " + route.Label);
            var shape = matches[0].index;
            foreach (var primitive in JsonArray(mesh, "primitives").OfType<Dictionary<string, object>>())
                if (shape >= JsonArray(primitive, "targets").Count) throw new InvalidOperationException("追跡表情のmorph target番号が不正です: " + route.Label);
            return new Dictionary<string, object> { ["node"] = (long)node, ["index"] = (long)shape, ["weight"] = (double)weight };
        }

        static Dictionary<string, object> Expression(IEnumerable<object> bindings) => new Dictionary<string, object> {
            ["morphTargetBinds"] = bindings.ToList(), ["isBinary"] = false,
            ["overrideBlink"] = "none", ["overrideMouth"] = "none", ["overrideLookAt"] = "none"
        };
        static bool HasBindings(Dictionary<string, object> expression) => expression != null && new[] { "morphTargetBinds", "materialColorBinds", "textureTransformBinds" }
            .Any(key => expression.TryGetValue(key, out var value) && value is List<object> bindings && bindings.Count > 0);
        static Dictionary<string, object> Custom(Dictionary<string, object> json) => Obj(Obj(Obj(Obj(json, "extensions", false), "VRMC_vrm", false), "expressions", true), "custom", true);
        static Dictionary<string, object> Obj(Dictionary<string, object> parent, string name, bool create)
        {
            if (parent == null) throw new InvalidOperationException("追跡表情のVRM情報がありません: " + name);
            if (!parent.TryGetValue(name, out var value))
            {
                if (!create) return null;
                parent[name] = value = new Dictionary<string, object>();
            }
            return value as Dictionary<string, object> ?? throw new InvalidOperationException("追跡表情のVRM情報が不正です: " + name);
        }
        static List<object> JsonArray(Dictionary<string, object> parent, string name) => parent != null && parent.TryGetValue(name, out var value) && value is List<object> list
            ? list : throw new InvalidOperationException("追跡表情のVRM配列がありません: " + name);
        void RequireAlive() { if (disposed) throw new ObjectDisposedException(nameof(ExportOptimizationBindings)); }
        void RequireApplied() { RequireAlive(); if (!applied) throw new InvalidOperationException("最適化後の表情参照が未確定です。"); }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (marker != null) Object.DestroyImmediate(marker);
            foreach (var asset in owned) if (asset != null) Object.DestroyImmediate(asset);
            owned.Clear();
        }
    }
}
