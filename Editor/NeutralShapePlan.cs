using System;
using System.Collections.Generic;
using System.Linq;
using UniVRM10;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // Prepared hierarchy appearance is authoritative. FX can reconstruct its
    // independent morph rest, but cannot replace only the morph half of a
    // clothing/material/transform configuration. Evaluation support remains
    // native and is separate from the channels this plan commits.
    internal sealed class NeutralShapePlan
    {
        internal readonly HashSet<EditorCurveBinding> CommittedMorphs;
        internal readonly HashSet<EditorCurveBinding> PreservedMorphs = new HashSet<EditorCurveBinding>();
        // A changing rest curve has no single native neutral value. Keep that
        // channel's prepared authored appearance, separately from wardrobe
        // ownership and from the explicit endpoints/programs exported later.
        internal readonly HashSet<EditorCurveBinding> TemporalMorphs = new HashSet<EditorCurveBinding>();
        private readonly Dictionary<EditorCurveBinding, string> temporalReasons = new Dictionary<EditorCurveBinding, string>();
        private readonly GameObject avatar;
        private readonly HashSet<EditorCurveBinding> appearanceBindings = new HashSet<EditorCurveBinding>();
        private readonly HashSet<EditorCurveBinding> unboundAppearanceBindings = new HashSet<EditorCurveBinding>();
        private readonly Dictionary<EditorCurveBinding, string> reasons = new Dictionary<EditorCurveBinding, string>();
        private readonly Dictionary<string, Transform[]> targets;
        private readonly HashSet<Transform> protectedHierarchy = new HashSet<Transform>();
        private readonly Dictionary<Transform, string> hierarchyOwners = new Dictionary<Transform, string>();
        private readonly Dictionary<EditorCurveBinding, string> omittedMorphs = new Dictionary<EditorCurveBinding, string>();

        private NeutralShapePlan(GameObject avatar, IEnumerable<EditorCurveBinding> roots)
        {
            this.avatar = avatar;
            CommittedMorphs = new HashSet<EditorCurveBinding>(roots);
            targets = avatar.GetComponentsInChildren<Transform>(true).GroupBy(transform =>
                AnimationUtility.CalculateTransformPath(transform, avatar.transform), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        }

        internal static NeutralShapePlan Create(GameObject prepared, RuntimeAnimatorController runtime,
            IEnumerable<IEnumerable<EditorCurveBinding>> roots, Func<string, bool> excludedPath = null,
            IEnumerable<EditorCurveBinding> requiredMorphs = null, ICollection<string> warnings = null,
            VrChatExpressionMenu.Source source = null, FixedExpressionContext fixedContext = null)
        {
            var plan = new NeutralShapePlan(prepared, roots.SelectMany(group => group));
            plan.ResolveCommittedMorphs();
            var ownershipLayers = ExpressionDependencies.NormalInputLayers(runtime, source, fixedContext, excludedPath);
            var customInputs = new HashSet<string>(ExpressionDependencies.Controller(runtime).parameters.Where(parameter =>
                parameter.type != AnimatorControllerParameterType.Trigger && !VrChatParameterDriver.BuiltIn.Contains(parameter.name) &&
                source?.ExternalParameters.Contains(parameter.name) != true).Select(parameter => parameter.name), StringComparer.Ordinal);
            var copiedInputs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var copy in ownershipLayers.SelectMany(layer => layer.CopyInputs))
            {
                if (!customInputs.Contains(copy.Source) || !customInputs.Contains(copy.Destination)) continue;
                void Link(string from, string to)
                {
                    if (!copiedInputs.TryGetValue(from, out var linked)) copiedInputs.Add(from, linked = new HashSet<string>(StringComparer.Ordinal));
                    linked.Add(to);
                }
                // A valid Copy (including range conversion) makes its source
                // and destination part of one appearance-input component.
                // Other driver operations establish no such relationship.
                Link(copy.Source, copy.Destination); Link(copy.Destination, copy.Source);
            }
            var visualClips = new Dictionary<AnimationClip, string>();
            foreach (var clip in ownershipLayers.SelectMany(layer => layer.Clips).Distinct())
            {
                var bindings = AnimationUtility.GetCurveBindings(clip).Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    .Where(binding => excludedPath?.Invoke(binding.path) != true).ToArray();
                var appearance = new List<EditorCurveBinding>();
                foreach (var binding in bindings.Where(binding => IsAppearanceBinding(binding) && !HarmlessActivation(prepared, clip, binding)))
                {
                    // An alternate layout path absent from the prepared copy
                    // cannot bind in Unity. It is neither a visual change nor
                    // evidence that accompanying morphs belong to appearance.
                    if (!plan.targets.TryGetValue(binding.path, out var matches))
                    { plan.unboundAppearanceBindings.Add(binding); continue; }
                    if (matches.Length != 1)
                        throw new InvalidOperationException("FXの見た目の対象を一意に指定できません: " + Describe(clip, binding));
                    var components = binding.type == typeof(GameObject) ? 1 : matches[0].GetComponents(binding.type).Length;
                    if (components == 0) { plan.unboundAppearanceBindings.Add(binding); continue; }
                    if (components != 1)
                        throw new InvalidOperationException("FXの見た目のComponentを一意に指定できません: " + Describe(clip, binding));
                    plan.appearanceBindings.Add(binding);
                    appearance.Add(binding);
                }
                if (appearance.Count == 0) continue;
                visualClips.Add(clip, Describe(clip, appearance[0]));
                foreach (var morph in bindings.Where(IsMorph))
                {
                    if (!plan.CommittedMorphs.Contains(morph)) continue;
                    plan.PreservedMorphs.Add(morph);
                    plan.reasons[morph] = Describe(clip, appearance[0]);
                }
            }
            foreach (var visualLayer in ownershipLayers.Where(layer => layer.Clips.Any(visualClips.ContainsKey)))
            {
                var controls = new HashSet<string>(visualLayer.ControlReads.Where(customInputs.Contains), StringComparer.Ordinal);
                if (controls.Count == 0) continue;
                var linkedControls = controls.ToDictionary(name => name, name => name, StringComparer.Ordinal);
                var pending = new Queue<string>(controls.OrderBy(name => name, StringComparer.Ordinal));
                while (pending.Count > 0)
                {
                    var current = pending.Dequeue();
                    if (!copiedInputs.TryGetValue(current, out var copies)) continue;
                    foreach (var copied in copies.OrderBy(name => name, StringComparer.Ordinal))
                    {
                        if (linkedControls.ContainsKey(copied)) continue;
                        linkedControls.Add(copied, linkedControls[current]); pending.Enqueue(copied);
                    }
                }
                var reason = visualClips[visualLayer.Clips.First(visualClips.ContainsKey)];
                foreach (var morphLayer in ownershipLayers)
                {
                    var shared = morphLayer.ControlReads.Where(linkedControls.ContainsKey).OrderBy(name => name, StringComparer.Ordinal).FirstOrDefault();
                    if (shared == null) continue;
                    // Direct control of the same appearance option proves
                    // coupling across separate activation/mask layers, also
                    // through reachable Copy aliases. The finite input graph
                    // never propagates through generic reset morph bindings,
                    // Set/Add/Random or arbitrary parameter dependencies.
                    var input = linkedControls[shared] == shared ? shared : linkedControls[shared] + " → " + shared + " (Parameter Driver Copy)";
                    foreach (var morph in morphLayer.Morphs.Where(plan.CommittedMorphs.Contains))
                    {
                        plan.PreservedMorphs.Add(morph);
                        if (!plan.reasons.ContainsKey(morph)) plan.reasons[morph] = "共有の入力 " + input + " / " + reason;
                    }
                }
            }
            var required = RequiredChannels(prepared, excludedPath);
            required.UnionWith(requiredMorphs ?? Enumerable.Empty<EditorCurveBinding>());
            var conflict = plan.PreservedMorphs.Where(required.Contains).OrderBy(binding => binding.path, StringComparer.Ordinal)
                .ThenBy(binding => binding.propertyName, StringComparer.Ordinal).FirstOrDefault();
            if (plan.PreservedMorphs.Any(required.Contains))
                throw new InvalidOperationException("必須の表情・追跡変形が衣装・材質・Transformの変更と結び付いているため、基準形を確定できません: " +
                    conflict.path + " / " + conflict.propertyName + " / " + plan.reasons[conflict]);
            plan.CommittedMorphs.ExceptWith(plan.PreservedMorphs);
            plan.ProtectCommittedHierarchy();
            if (plan.omittedMorphs.Count > 0)
            {
                var descriptions = plan.omittedMorphs.OrderBy(pair => pair.Key.path, StringComparer.Ordinal)
                    .ThenBy(pair => pair.Key.propertyName, StringComparer.Ordinal).Select(pair =>
                        pair.Key.path + " / " + pair.Key.propertyName + " (" + pair.Value + ")").ToArray();
                warnings?.Add("書き出し対象に結び付かないFXの初期BlendShapeを評価から省略しました: " +
                    string.Join(", ", descriptions.Take(8)) + (descriptions.Length > 8 ? " ほか" + (descriptions.Length - 8) + "件" : ""));
            }
            if (plan.PreservedMorphs.Count > 0)
            {
                var descriptions = plan.PreservedMorphs.OrderBy(binding => binding.path, StringComparer.Ordinal)
                    .ThenBy(binding => binding.propertyName, StringComparer.Ordinal).Select(binding =>
                        binding.path + " / " + binding.propertyName + " (" + plan.reasons[binding] + ")").ToArray();
                warnings?.Add("FXの衣装・髪・材質に連動するBlendShapeは、書き出し用コピーの現在の見た目を保持しました: " +
                    string.Join(", ", descriptions.Take(8)) + (descriptions.Length > 8 ? " ほか" + (descriptions.Length - 8) + "件" : ""));
            }
            return plan;
        }

        private void ResolveCommittedMorphs()
        {
            var exported = new HashSet<SkinnedMeshRenderer>(ExportRendererSelection.Enumerate(avatar).OfType<SkinnedMeshRenderer>());
            foreach (var binding in CommittedMorphs.ToArray())
            {
                string omitted = null;
                if (!targets.TryGetValue(binding.path, out var matches)) omitted = "対象パスがありません";
                else
                {
                    if (matches.Length != 1)
                        throw new InvalidOperationException("FXの初期BlendShapeの対象を一意に指定できません: " + binding.path + " / " + binding.propertyName);
                    var renderers = matches[0].GetComponents<SkinnedMeshRenderer>();
                    if (renderers.Length > 1)
                        throw new InvalidOperationException("FXの初期BlendShapeのRendererを一意に指定できません: " + binding.path + " / " + binding.propertyName);
                    var renderer = renderers.SingleOrDefault();
                    if (renderer == null) omitted = "SkinnedMeshRendererがありません";
                    else if (renderer.sharedMesh == null) omitted = "Meshがありません";
                    else
                    {
                        var shape = renderer.sharedMesh.GetBlendShapeIndex(binding.propertyName.Substring("blendShape.".Length));
                        if (shape < 0) omitted = "BlendShapeがありません";
                        else if (renderer.sharedMesh.GetBlendShapeFrameCount(shape) == 0) omitted = "BlendShapeのフレームがありません";
                        else if (!exported.Contains(renderer)) omitted = "現在の見た目で書き出されないRendererです";
                    }
                }
                // Raw FX often also addresses another avatar layout or hidden
                // wardrobe. Those optional bindings cannot supply an exported
                // rest channel. Keep their native clips as evaluation support,
                // without making unrelated committed geometry unprovable.
                // Required endpoint routes retain their own validation.
                if (omitted == null) continue;
                CommittedMorphs.Remove(binding);
                omittedMorphs.Add(binding, omitted);
            }
        }

        internal bool AllowsEvaluationBinding(AnimationClip clip, EditorCurveBinding binding) => EvaluationBindingRejection(clip, binding) == null;

        internal void PreserveTemporalRest(AnimationClip clip, EditorCurveBinding binding)
        {
            if (!CommittedMorphs.Contains(binding) || !IsMorph(binding))
                throw new InvalidOperationException("時間で変わる初期BlendShapeの対象を確定できません。");
            if (TemporalMorphs.Add(binding)) temporalReasons.Add(binding, Describe(clip, binding));
        }

        internal void ReportTemporalRest(ICollection<string> warnings)
        {
            if (TemporalMorphs.Count == 0) return;
            var descriptions = TemporalMorphs.OrderBy(binding => binding.path, StringComparer.Ordinal)
                .ThenBy(binding => binding.propertyName, StringComparer.Ordinal).Select(binding => temporalReasons[binding]).ToArray();
            warnings?.Add("時間で変わるFXの待機表情は基準形に焼き込まず、書き出し用コピーの現在の見た目を保持しました: " +
                string.Join(", ", descriptions.Take(8)) + (descriptions.Length > 8 ? " ほか" + (descriptions.Length - 8) + "件" : ""));
        }

        internal string EvaluationBindingRejection(AnimationClip clip, EditorCurveBinding binding)
        {
            if (HarmlessActivation(avatar, clip, binding) || unboundAppearanceBindings.Contains(binding)) return null;
            if (!appearanceBindings.Contains(binding)) return "記録されていない見た目の曲線です（通常入力での到達範囲外、または未対応のプロパティ）";
            if (!targets.TryGetValue(binding.path, out var matches)) return "見た目の対象パスがありません";
            if (matches.Length != 1) return "見た目の対象を一意に特定できません";
            var target = matches[0];
            // Capture reads only GetBlendShapeWeight on the private probe;
            // no sampled geometry, pose or material is committed. Independent
            // supported Transform/material curves cannot change that scalar,
            // even on an ancestor, root bone or influencing bone. Keep original
            // clips native so their WD/additive stream contribution survives.
            // Explicitly paired/shared-input morphs were already preserved or
            // rejected as required above, together with prepared appearance.
            if (binding.type == typeof(Transform) || IsMaterial(binding)) return null;
            if (protectedHierarchy.Contains(target)) return "基準形のRenderer階層に影響します: " + hierarchyOwners[target];
            return null;
        }

        private void ProtectCommittedHierarchy()
        {
            // Activation/enablement can prevent a captured Renderer from
            // receiving its scalar curves. Its ancestors therefore remain
            // protected, independently of the retained prepared pose.
            foreach (var path in CommittedMorphs.Select(morph => morph.path).Distinct(StringComparer.Ordinal))
                for (var current = targets[path][0]; current != null; current = current.parent)
                    if (protectedHierarchy.Add(current)) hierarchyOwners.Add(current, path);
        }

        internal static bool HarmlessActivation(GameObject avatar, AnimationClip clip, EditorCurveBinding binding)
        {
            if (avatar == null || binding.type != typeof(GameObject) || binding.propertyName != "m_IsActive") return false;
            var curve = AnimationUtility.GetEditorCurve(clip, binding);
            if (curve == null || curve.length == 0 || !VrChatExpressionSampler.IsConstant(curve)) return false;
            var value = curve.keys[0].value;
            if (value != 0 && value != 1) return false;
            var matches = avatar.GetComponentsInChildren<Transform>(true).Where(transform =>
                AnimationUtility.CalculateTransformPath(transform, avatar.transform) == binding.path).ToArray();
            return matches.Length == 0 || matches.Length == 1 && matches[0].gameObject.activeSelf == (value != 0);
        }

        private static bool IsMorph(EditorCurveBinding binding) => binding.type == typeof(SkinnedMeshRenderer) &&
            binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal);

        private static bool IsMaterial(EditorCurveBinding binding) => typeof(Renderer).IsAssignableFrom(binding.type) &&
            (binding.propertyName.StartsWith("material.", StringComparison.Ordinal) ||
             binding.propertyName.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal));

        private static bool IsAppearanceBinding(EditorCurveBinding binding)
        {
            if (binding.type == typeof(GameObject)) return binding.propertyName == "m_IsActive";
            if (typeof(Renderer).IsAssignableFrom(binding.type)) return binding.propertyName == "m_Enabled" || IsMaterial(binding);
            if (binding.type != typeof(Transform)) return false;
            return new[] { "m_LocalPosition.", "localEulerAnglesRaw.", "m_LocalScale." }
                .Any(prefix => new[] { "x", "y", "z" }.Any(axis => binding.propertyName == prefix + axis)) ||
                new[] { "x", "y", "z", "w" }.Any(axis => binding.propertyName == "m_LocalRotation." + axis);
        }

        private static string Describe(AnimationClip clip, EditorCurveBinding binding) =>
            clip.name + " / " + binding.path + " / " + binding.propertyName;

        private static HashSet<EditorCurveBinding> RequiredChannels(GameObject prepared, Func<string, bool> excludedPath)
        {
            var result = new HashSet<EditorCurveBinding>();
            void Add(SkinnedMeshRenderer renderer, string shape)
            {
                if (renderer == null || renderer.sharedMesh == null || string.IsNullOrEmpty(shape) ||
                    renderer.sharedMesh.GetBlendShapeIndex(shape) < 0 || !renderer.transform.IsChildOf(prepared.transform)) return;
                var path = AnimationUtility.CalculateTransformPath(renderer.transform, prepared.transform);
                if (excludedPath?.Invoke(path) != true) result.Add(EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + shape));
            }
            var expression = prepared.GetComponent<Vrm10Instance>()?.Vrm?.Expression;
            if (expression != null)
                foreach (var clip in expression.Clips.Select(item => item.Clip).Where(clip => clip != null).Distinct())
                {
                    var bindings = clip.MorphTargetBindings ?? Array.Empty<MorphTargetBinding>();
                    if (bindings.Any(binding => !NeutralShapeSnapshot.Finite(binding.Weight) || binding.Weight < 0 || binding.Weight > 1)) continue;
                    var first = new HashSet<(string Path, int Shape)>();
                    foreach (var binding in bindings)
                    {
                        if (!first.Add((binding.RelativePath, binding.Index)) || binding.Weight <= 0) continue;
                        if (!string.IsNullOrEmpty(binding.RelativePath) && excludedPath?.Invoke(binding.RelativePath) == true) continue;
                        var target = string.IsNullOrEmpty(binding.RelativePath) ? prepared.transform : prepared.transform.Find(binding.RelativePath);
                        var renderer = target == null ? null : target.GetComponent<SkinnedMeshRenderer>();
                        if (renderer != null && renderer.sharedMesh != null && binding.Index >= 0 && binding.Index < renderer.sharedMesh.blendShapeCount)
                            Add(renderer, renderer.sharedMesh.GetBlendShapeName(binding.Index));
                    }
                }
            if (BlinkExportSession.TryDescriptor(prepared, null, out var blink)) Add(blink.Renderer, blink.Shape);
            var profile = prepared.GetComponentInChildren<VrmTrackingMarker>(true)?.profile;
            if (profile != null)
                foreach (var shape in (profile.expressions ?? Array.Empty<TrackingExpression>()).Where(entry => entry != null)
                    .SelectMany(entry => entry.morphs ?? Array.Empty<TrackingMorph>()).Where(morph => morph != null && morph.weight > 0).Select(morph => morph.shape))
                    foreach (var renderer in prepared.GetComponentsInChildren<SkinnedMeshRenderer>(true)) Add(renderer, shape);
            return result;
        }
    }
}
