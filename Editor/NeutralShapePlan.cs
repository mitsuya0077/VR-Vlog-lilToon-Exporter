using System;
using System.Collections.Generic;
using System.Linq;
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
        internal bool RetainUnresolvedRest { get; private set; }
        internal bool AllowUnchangedAppearance { get; private set; }
        internal readonly HashSet<EditorCurveBinding> PreservedMorphs = new HashSet<EditorCurveBinding>();
        // A changing rest curve has no single native neutral value. Keep that
        // channel's prepared authored appearance, separately from wardrobe
        // ownership and from the explicit endpoints/programs exported later.
        internal readonly HashSet<EditorCurveBinding> TemporalMorphs = new HashSet<EditorCurveBinding>();
        private readonly Dictionary<EditorCurveBinding, string> temporalReasons = new Dictionary<EditorCurveBinding, string>();
        private readonly GameObject avatar;
        private readonly HashSet<EditorCurveBinding> appearanceBindings = new HashSet<EditorCurveBinding>();
        private readonly HashSet<EditorCurveBinding> unboundAppearanceBindings = new HashSet<EditorCurveBinding>();
        private readonly HashSet<EditorCurveBinding> constraintBindings = new HashSet<EditorCurveBinding>();
        private readonly HashSet<EditorCurveBinding> existingProperties = new HashSet<EditorCurveBinding>();
        private readonly HashSet<AnimationClip> validatedClips = new HashSet<AnimationClip>();
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

        // Explicit face endpoints have a geometry-proven output domain. Their
        // native support may animate appearance on the disposable probe; those
        // values never replace prepared materials, outfit morphs or object state.
        // This is deliberately separate from rest ownership in Create below.
        internal static NeutralShapePlan CreateProjection(GameObject prepared, IEnumerable<EditorCurveBinding> morphs,
            IEnumerable<AnimationClip> clips, Func<string, bool> excludedPath = null)
        {
            var plan = new NeutralShapePlan(prepared, morphs);
            plan.AllowUnchangedAppearance = true;
            plan.ResolveCommittedMorphs();
            var motions = clips.Where(clip => clip != null).Distinct().ToArray();
            plan.ValidateBindings(motions, excludedPath);
            foreach (var clip in motions)
                foreach (var binding in AnimationUtility.GetCurveBindings(clip).Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip)))
                    if (excludedPath?.Invoke(binding.path) != true && plan.IsAppearanceBinding(binding)) plan.appearanceBindings.Add(binding);
            plan.ProtectCommittedHierarchy();
            return plan;
        }

        internal static NeutralShapePlan Create(GameObject prepared, RuntimeAnimatorController runtime,
            IEnumerable<IEnumerable<EditorCurveBinding>> roots, Func<string, bool> excludedPath = null,
            IEnumerable<EditorCurveBinding> requiredMorphs = null, ICollection<string> warnings = null,
            VrChatExpressionMenu.Source source = null, FixedExpressionContext fixedContext = null, bool retainUnresolvedRest = false,
            bool allowUnchangedAppearance = false)
        {
            var plan = new NeutralShapePlan(prepared, roots.SelectMany(group => group));
            plan.RetainUnresolvedRest = retainUnresolvedRest;
            plan.AllowUnchangedAppearance = allowUnchangedAppearance;
            plan.ResolveCommittedMorphs();
            var ownershipLayers = ExpressionDependencies.NormalInputLayers(runtime, source, fixedContext, excludedPath);
            var definitions = ExpressionDependencies.Controller(runtime).layers;
            // Matching raw values prove an Override contribution. Additive
            // defaults/reference poses can change another layer's appearance;
            // do not discard ownership based on a per-clip comparison there.
            var unchangedClips = allowUnchangedAppearance
                ? VrChatExpressionSampler.UnchangedAppearanceClips(runtime, Enumerable.Range(0, definitions.Length)) : null;
            // Validate before any appearance group is preserved. A recoverable
            // unsupported binding must not conceal malformed data in another
            // reachable clip. The caller also supplies the neutral probe's
            // reachable clips through this same preflight entry point.
            plan.ValidateBindings(ownershipLayers.SelectMany(layer => layer.Clips), excludedPath);
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
                foreach (var binding in bindings.Where(binding => plan.IsAppearanceBinding(binding) && !HarmlessActivation(prepared, clip, binding)))
                {
                    if (unchangedClips?.Contains(clip) == true &&
                        SelectedExpressionAppearance.IsUnchanged(prepared, clip, binding)) continue;
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
            // Rest reconstruction is optional even for a required expression
            // channel. Preserve its complete prepared appearance here; the
            // explicit blink/tracking/expression endpoint retains its own
            // identity and deformation validation later in the export.
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
                warnings?.Add(ExporterLocalization.T("FXの衣装・髪・材質・ギミックに連動するBlendShapeは、書き出し用コピーの現在の見た目を保持しました: ") +
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

        internal void ValidateBindings(IEnumerable<AnimationClip> clips, Func<string, bool> excludedPath = null)
        {
            foreach (var clip in clips.Where(value => value != null).Distinct())
            {
                if (validatedClips.Contains(clip)) continue;
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    // Animator parameters are global even when their transform
                    // path happens to be excluded from mesh export.
                    if (binding.type != typeof(Animator) && excludedPath?.Invoke(binding.path) == true) continue;
                    VrChatExpressionSampler.ValidateNativeParameterCurve(AnimationUtility.GetEditorCurve(clip, binding), Describe(clip, binding));
                    ValidateTarget(clip, binding, false);
                }
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    if (excludedPath?.Invoke(binding.path) == true) continue;
                    var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    for (var index = 0; index < keys.Length; index++)
                        if (!NeutralShapeSnapshot.Finite(keys[index].time) || index > 0 && keys[index].time <= keys[index - 1].time)
                            throw new InvalidOperationException(ExporterLocalization.T("FXのオブジェクト参照曲線の時刻が不正です: ") + Describe(clip, binding));
                    ValidateTarget(clip, binding, true);
                }
                validatedClips.Add(clip);
            }
        }

        private void ValidateTarget(AnimationClip clip, EditorCurveBinding binding, bool objectReference)
        {
            // Parameter and morph identities have their own controller/mesh
            // checks. Missing optional morph targets are resolved separately.
            if (binding.type == null || binding.path == null || string.IsNullOrEmpty(binding.propertyName))
                throw new InvalidOperationException(ExporterLocalization.T("FXの見た目の曲線の参照が不正です: ") + Describe(clip, binding));
            if (binding.type == typeof(Animator) || IsMorph(binding)) return;
            if (!targets.TryGetValue(binding.path, out var matches))
            { unboundAppearanceBindings.Add(binding); return; }
            if (matches.Length != 1)
                throw new InvalidOperationException("FXの見た目の対象を一意に指定できません: " + Describe(clip, binding));
            UnityEngine.Object target;
            if (binding.type == typeof(GameObject)) target = matches[0].gameObject;
            else
            {
                // An unsupported binding type is not evidence of a damaged
                // object. Leave its classification to the neutral fallback.
                if (!typeof(Component).IsAssignableFrom(binding.type)) return;
                var components = matches[0].GetComponents(binding.type);
                if (components.Length == 0) { unboundAppearanceBindings.Add(binding); return; }
                if (components.Length != 1)
                    throw new InvalidOperationException("FXの見た目のComponentを一意に指定できません: " + Describe(clip, binding));
                target = components[0];
            }
            // These Unity animation properties have no direct serialized field
            // (raw Euler angles and shader/material channels in particular).
            if (IsBuiltInAppearanceBinding(binding))
            { existingProperties.Add(binding); return; }
            using var data = new SerializedObject(target);
            var property = data.FindProperty(binding.propertyName);
            if (property == null) return;
            var scalar = property.propertyType == SerializedPropertyType.Float || property.propertyType == SerializedPropertyType.Integer ||
                property.propertyType == SerializedPropertyType.Boolean || property.propertyType == SerializedPropertyType.Enum;
            if (objectReference ? property.propertyType == SerializedPropertyType.ObjectReference : scalar)
                existingProperties.Add(binding);
            // Only known constraint implementations may participate in native
            // scalar sampling. A valid arbitrary MonoBehaviour field is still
            // unsupported; serialized existence alone proves no independence.
            if (!objectReference && scalar && IsKnownConstraint(binding.type)) constraintBindings.Add(binding);
        }

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
            if (!appearanceBindings.Contains(binding)) return existingProperties.Contains(binding)
                ? ExporterLocalization.T("対象プロパティは存在しますが、通常入力での到達範囲外、またはBlendShapeへの影響が未対応です")
                : "記録されていない見た目の曲線です（通常入力での到達範囲外、または未対応のプロパティ）";
            if (!targets.TryGetValue(binding.path, out var matches)) return "見た目の対象パスがありません";
            if (matches.Length != 1) return "見た目の対象を一意に特定できません";
            var target = matches[0];
            // The probe disables Behaviours. A curve that enables a constraint
            // can restart component execution, including SDK jobs or external
            // target references. It still owns its paired appearance, but must
            // retain the prepared rest instead of running in the native probe.
            if (constraintBindings.Contains(binding) && binding.propertyName == "m_Enabled")
                return ExporterLocalization.T("Constraintの有効状態を変更するため、評価中のComponent実行を安全に制限できません");
            // Capture reads only GetBlendShapeWeight on the private probe;
            // no sampled geometry, pose or material is committed. Independent
            // supported Transform/material/constraint curves cannot change that scalar,
            // even on an ancestor, root bone or influencing bone. Keep original
            // clips native so their WD/additive stream contribution survives.
            // Explicitly paired/shared-input morphs were already preserved
            // above, together with their complete prepared appearance.
            if (binding.type == typeof(Transform) || IsMaterial(binding) || constraintBindings.Contains(binding)) return null;
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

        private bool IsAppearanceBinding(EditorCurveBinding binding) => IsBuiltInAppearanceBinding(binding) || constraintBindings.Contains(binding);

        private static bool IsKnownConstraint(Type type)
        {
            if (type == null || !typeof(Component).IsAssignableFrom(type)) return false;
            // Do not accept an arbitrary script merely because it implements
            // IConstraint. Unity's native constraint types share this assembly.
            if (typeof(UnityEngine.Animations.IConstraint).IsAssignableFrom(type) &&
                type.Assembly == typeof(UnityEngine.Animations.ParentConstraint).Assembly) return true;
            // Optional SDK bridge: no SDK assembly reference is required. Use
            // the SDK's documented concrete types, never avatar/object names
            // or a namespace-wide allowance for unknown MonoBehaviours.
            switch (type.FullName)
            {
                case "VRC.SDK3.Dynamics.Constraint.Components.VRCAimConstraint":
                case "VRC.SDK3.Dynamics.Constraint.Components.VRCLookAtConstraint":
                case "VRC.SDK3.Dynamics.Constraint.Components.VRCParentConstraint":
                case "VRC.SDK3.Dynamics.Constraint.Components.VRCPositionConstraint":
                case "VRC.SDK3.Dynamics.Constraint.Components.VRCRotationConstraint":
                case "VRC.SDK3.Dynamics.Constraint.Components.VRCScaleConstraint": return true;
                default: return false;
            }
        }

        private static bool IsBuiltInAppearanceBinding(EditorCurveBinding binding)
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

    }
}
