using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using MorphValue = VRVlog.LilToonExporter.VrChatExpressionMenu.MorphValue;

namespace VRVlog.LilToonExporter
{
    internal static class VrChatExpressionSampler
    {
        // Source exclusions name the original hierarchy. After authoring passes,
        // an active retained renderer can occupy an omitted object's old path.
        // The omission must not transfer to that unrelated retained identity.
        internal static bool IsExcludedPreparedPath(GameObject avatar, string path, Func<string, bool> originalExcludedPath)
        {
            if (originalExcludedPath?.Invoke(path) != true) return false;
            return !avatar.GetComponentsInChildren<SkinnedMeshRenderer>().Any(renderer => renderer.enabled && renderer.sharedMesh != null &&
                renderer.gameObject.activeInHierarchy && AnimationUtility.CalculateTransformPath(renderer.transform, avatar.transform) == path);
        }

        internal static VrChatExpressionMenu.Source Analyze(GameObject avatar, Func<string, bool> excludedPath = null, VrChatMenuImportPolicy menuPolicy = null,
            GameObject authoringSource = null, FaceEmoExpressions.BindingSnapshot faceEmoBindings = null)
        {
            var source = VrChatExpressionMenu.Read(avatar, menuPolicy);
            try
            {
                for (var index = 0; index < source.Entries.Count; index++)
                {
                    var entry = source.Entries[index];
                    if (entry.Error != null) continue;
                    if (EditorUtility.DisplayCancelableProgressBar("VRChatの表情を読み込み中", entry.Name, (float)index / source.Entries.Count))
                        throw new OperationCanceledException();
                    try
                    {
                        var context = FixedExpressionContext.Create(source.Controller, source.Defaults, source);
                        entry.Values.AddRange(SampleFixed(avatar, source.Controller, source.Defaults, entry.Parameters, excludedPath, source, entry.Unevaluated, context));
                        if (context.UsedParameters.Count > 0)
                            entry.Messages.Add("外部入力は通常状態・設定済み初期値で固定表情として保存しました（自動切り替えは再現しません）: " +
                                string.Join(", ", context.UsedParameters.OrderBy(name => name, StringComparer.Ordinal).Select(name => name + "=" +
                                    (entry.Parameters.TryGetValue(name, out var selectedValue) ? selectedValue : context.Values[name]).ToString("G9", CultureInfo.InvariantCulture))));
                    }
                    catch (InvalidOperationException error) { entry.Error = error.Message; }
                }
                VrChatGestureExpressions.Add(avatar, source, excludedPath);
                FaceEmoExpressions.Add(avatar, source, excludedPath, authoringSource, faceEmoBindings);
            }
            finally { EditorUtility.ClearProgressBar(); }
            return source;
        }

        // The user-facing export captures one explicit environment. Runtime
        // VRChat inputs are not required to replay the resulting VRM morphs.
        internal static List<MorphValue> SampleFixed(GameObject avatar, RuntimeAnimatorController runtime,
            IDictionary<string, float> defaults, IDictionary<string, float> selected, Func<string, bool> excludedPath = null,
            VrChatExpressionMenu.Source metadata = null, IList<MorphValue> unevaluated = null, FixedExpressionContext fixedContext = null)
            => Sample(avatar, runtime, defaults, selected, excludedPath, metadata, unevaluated,
                fixedContext ?? FixedExpressionContext.Create(runtime, defaults, metadata));

        // Stable, discrete, morph-based FX expressions, including deterministic
        // parameter drivers. Each menu is evaluated from its own fresh defaults.
        internal static List<MorphValue> Sample(GameObject avatar, RuntimeAnimatorController runtime,
            IDictionary<string, float> defaults, IDictionary<string, float> selected, Func<string, bool> excludedPath = null,
            VrChatExpressionMenu.Source metadata = null, IList<MorphValue> unevaluated = null, FixedExpressionContext fixedContext = null)
            => Evaluate(avatar, runtime, defaults, selected, excludedPath, metadata, unevaluated,
                StationaryBindings(avatar, runtime, excludedPath), fixedContext: fixedContext);

        internal static List<MorphValue> SampleDefaults(GameObject avatar, RuntimeAnimatorController runtime,
            IDictionary<string, float> defaults, Func<string, bool> excludedPath = null, VrChatExpressionMenu.Source metadata = null)
        {
            if (runtime == null) return new List<MorphValue>();
            var stationary = StationaryBindings(avatar, runtime, excludedPath);
            if (stationary.Count == 0) return new List<MorphValue>();
            var unresolved = new List<MorphValue>();
            var values = Evaluate(avatar, runtime, defaults, new Dictionary<string, float>(), excludedPath, metadata, unresolved, stationary,
                preserveNativeBasePose: true);
            if (unresolved.Count != 0)
                throw new InvalidOperationException("常時適用するFXの変形を確定できません。統合後のBlendShape設定を確認してください。");
            return values;
        }

        // Invoke on the committed post-Transforming copy. Native animation
        // evaluation occurs on another disposable copy and writes back weights
        // only; shared meshes/controllers and the source remain untouched. Call
        // after Analyze: all expression probes must use the prepared authored
        // weights rather than evaluating an already blended neutral base again.
        internal static void ApplyMergedDefaults(GameObject source, GameObject clone, ICollection<string> warnings = null,
            VrChatMenuImportPolicy menuPolicy = null, Func<string, bool> excludedPath = null)
        {
            if (source == null || clone == null) throw new ArgumentNullException(source == null ? nameof(source) : nameof(clone));
            if (source == clone || clone.transform.IsChildOf(source.transform) || source.transform.IsChildOf(clone.transform) || EditorUtility.IsPersistent(clone))
                throw new InvalidOperationException("FXの初期状態には原本から独立した書き出し用コピーが必要です。");
            var metadata = VrChatExpressionMenu.Read(clone, menuPolicy);
            var values = SampleDefaults(clone, metadata.Controller, metadata.Defaults, excludedPath, metadata);
            foreach (var value in values)
            {
                var renderer = FindRenderer(clone, value.Path);
                var index = renderer.sharedMesh.GetBlendShapeIndex(value.Shape);
                if (index < 0) throw new InvalidOperationException("常時適用するFXのBlendShapeが見つかりません: " + value.Shape);
                renderer.SetBlendShapeWeight(index, value.Weight);
            }
            if (values.Count > 0) warnings?.Add("統合後のFXにある常時適用の顔・体形設定を、一時コピーと表情の初期状態へ反映しました。");
        }

        // Direct clips (including moving gestures) bypass menu parameter
        // sampling. Probe their scalar animation properties through the same
        // native layer blending, then transform their curves by that response.
        // Required contributing layers must provide a fixed pose, so this
        // response is affine and preserves weighted/stepped tangent timing.
        internal static void ApplyPermanentOverrides(GameObject avatar, RuntimeAnimatorController runtime,
            VrChatExpressionMenu.Entry entry, int? layerIndex = null, bool writeDefaults = false, Func<string, bool> excludedPath = null,
            VrChatExpressionMenu.Source metadata = null, AnimatorState sourceState = null)
        {
            if (runtime == null || entry.Error != null || entry.Values.Count == 0) return;
            // Inspect the authored graph before any early exit: a layer control
            // can enable a stationary layer whose serialized default weight is
            // zero, as well as disable one that the probe would otherwise keep.
            ExpressionDependencies.ValidateProbeBehaviours(runtime, excludedPath);
            var stationaryBindings = StationaryBindings(avatar, runtime, excludedPath);
            var permanent = ExpressionDependencies.StationaryLayers(runtime, excludedPath);
            var originalController = ExpressionDependencies.Controller(runtime);
            var originalLayers = originalController.layers;
            var replacements = ExpressionDependencies.Overrides(runtime);
            var nativeSlot = layerIndex.HasValue && layerIndex.Value > 0;
            // Inactive authored slots still expose their direct clip as a
            // candidate. Preserve that legacy route rather than evaluating an
            // inactive native layer and erasing the clip's authored values.
            if (nativeSlot && layerIndex.Value < originalLayers.Length)
                nativeSlot = originalLayers[layerIndex.Value].defaultWeight > 0;
            // Constant parameter/empty clips can still impose Write Defaults.
            // Keep proven equivalent layers in their authored slots even when
            // none of their explicit bindings name a stationary morph.
            var parameterLayers = new Dictionary<int, AnimationClip>();
            for (var index = 0; index < originalLayers.Length; index++)
                if (HasEquivalentConstantStates(originalController, index, replacements))
                {
                    var clip = (AnimationClip)originalLayers[index].stateMachine.defaultState.motion;
                    if (replacements.TryGetValue(clip, out var replacement)) clip = replacement;
                    if (AnimationUtility.GetCurveBindings(clip).All(binding => binding.type == typeof(Animator))) parameterLayers.Add(index, clip);
                }
            if (layerIndex.HasValue && (layerIndex.Value < 0 || layerIndex.Value >= originalLayers.Length))
                throw new InvalidOperationException("表情のFXレイヤーを特定できません。");
            AnimatorStateMachine[] sourceMachines = null;
            StateMachineBehaviour[] sourceBehaviours = null;
            if (sourceState != null)
            {
                if (!layerIndex.HasValue || sourceState.writeDefaultValues != writeDefaults)
                    throw new InvalidOperationException("表情の元FX状態とWrite Defaultsを特定できません。");
                var sourceLayer = layerIndex.Value;
                var synced = new HashSet<int>();
                while (originalLayers[sourceLayer].syncedLayerIndex >= 0)
                {
                    if (!synced.Add(sourceLayer)) throw new InvalidOperationException("FXの同期レイヤー参照が不正です。");
                    sourceLayer = originalLayers[sourceLayer].syncedLayerIndex;
                    if (sourceLayer < 0 || sourceLayer >= originalLayers.Length)
                        throw new InvalidOperationException("FXの同期レイヤー参照が不正です。");
                }
                var paths = new List<AnimatorStateMachine[]>();
                var visited = new HashSet<AnimatorStateMachine>();
                void FindSource(AnimatorStateMachine machine, AnimatorStateMachine[] ancestors)
                {
                    if (machine == null || !visited.Add(machine)) throw new InvalidOperationException("表情の元FX状態の階層が不正です。");
                    var path = ancestors.Concat(new[] { machine }).ToArray();
                    foreach (var child in machine.states) if (child.state == sourceState) paths.Add(path);
                    foreach (var child in machine.stateMachines) FindSource(child.stateMachine, path);
                }
                FindSource(originalLayers[sourceLayer].stateMachine, Array.Empty<AnimatorStateMachine>());
                if (paths.Count != 1) throw new InvalidOperationException("表情の元FX状態を一意に特定できません。");
                sourceMachines = paths.Single();
                // The deterministic adapter models state entry. It does not
                // establish state-machine callback timing or ordering.
                if (sourceMachines.Any(machine => machine.behaviours.Length != 0))
                    throw new InvalidOperationException("表情の元FX状態の親StateMachineにコールバックがあるため、進入順序を確定できません。");
                sourceBehaviours = originalController.GetStateEffectiveBehaviours(sourceState, layerIndex.Value) ?? Array.Empty<StateMachineBehaviour>();
                if (layerIndex.Value == 0) nativeSlot = true;
                else if (originalLayers[layerIndex.Value].defaultWeight <= 0)
                {
                    var inactiveWrites = new HashSet<string>(sourceBehaviours.Where(VrChatParameterDriver.IsDriver)
                        .SelectMany(behaviour => VrChatParameterDriver.Read(behaviour, sourceState.name).Operations)
                        .Select(operation => operation.Destination), StringComparer.Ordinal);
                    if (sourceBehaviours.Any(behaviour => VrChatParameterDriver.ReadInstantFxControl(behaviour, sourceState.name, out var control) && control.FxWeight != 1))
                        throw new InvalidOperationException("無効なFXレイヤーの表情コールバックがFXの重みに影響するため、直接クリップを確定できません。");
                    if (inactiveWrites.Count > 0)
                    {
                        var initial = metadata?.Defaults ?? new Dictionary<string, float>();
                        var inactiveContext = FixedExpressionContext.Create(runtime, initial, metadata);
                        var roots = entry.Values.Select(value => EditorCurveBinding.FloatCurve(value.Path, typeof(SkinnedMeshRenderer), "blendShape." + value.Shape));
                        var retained = ExpressionDependencies.Analyze(runtime, Array.Empty<string>(), excludedPath, metadata,
                            initial, new Dictionary<string, float>(), roots, preserveNativeBasePose: true, fixedContext: inactiveContext);
                        if (inactiveWrites.Overlaps(retained.Parameters))
                            throw new InvalidOperationException("無効なFXレイヤーのParameter Driverが他の表情レイヤーに影響するため、直接クリップを確定できません: " +
                                string.Join(", ", inactiveWrites.Intersect(retained.Parameters).OrderBy(name => name, StringComparer.Ordinal)));
                    }
                    // A dormant authored slot remains a direct-clip candidate.
                    // Do not force its callbacks/layer active to invent a pose.
                    return;
                }
            }
            if (stationaryBindings.Count == 0 && parameterLayers.Count == 0 && !nativeSlot) return;
            if (layerIndex.HasValue && originalLayers[layerIndex.Value].blendingMode == AnimatorLayerBlendingMode.Additive)
                throw new InvalidOperationException("Additiveレイヤーの表情クリップと常時適用FXの合成は、加算の基準ポーズを確定できないため省略しました。");
            // A slot without proven state provenance cannot choose callbacks.
            // Known selected states retain their effective state callbacks;
            // unmodelled ancestor callbacks remain a conservative rejection.
            var omittedDriverWrites = nativeSlot && sourceState == null ?
                ExpressionDependencies.SelectedLayerDriverWrites(runtime, layerIndex.Value, excludedPath) : null;
            metadata = metadata ?? VrChatExpressionMenu.Read(avatar, new VrChatMenuImportPolicy { SkipAll = true });
            if (!metadata.NeutralInputInventoryComplete) NeutralInputProof.Read(avatar, metadata);
            var defaults = metadata.Defaults;
            // Direct gesture/registered clips use the same explicit normal
            // environment as menu expressions. Their support graph still
            // contains contact gates and built-in inputs from the full FX.
            var context = FixedExpressionContext.Create(runtime, defaults, metadata);
            var explicitMorphs = new HashSet<EditorCurveBinding>(entry.Values.Select(value =>
                EditorCurveBinding.FloatCurve(value.Path, typeof(SkinnedMeshRenderer), "blendShape." + value.Shape)));
            var intendedMorphs = new HashSet<EditorCurveBinding>(stationaryBindings); intendedMorphs.UnionWith(explicitMorphs);
            // This is a scalar response on a private native probe. Prepared
            // clothing, materials and bones remain authoritative, together
            // with their coupled morphs; they cannot become endpoint channels
            // merely through WD/additive dependency closure.
            var scalarPlan = NeutralShapePlan.Create(avatar, runtime, new[] { intendedMorphs }, excludedPath,
                requiredMorphs: explicitMorphs, warnings: entry.Messages, source: metadata, fixedContext: context);
            foreach (var binding in explicitMorphs.Except(scalarPlan.CommittedMorphs))
                if (FindRenderer(avatar, binding.path).sharedMesh.GetBlendShapeIndex(binding.propertyName.Substring("blendShape.".Length)) >= 0)
                    throw new InvalidOperationException("表情クリップのBlendShapeが現在の書き出し対象に結び付いていません。");
            // ReadPose deliberately retains not-yet-known authored channels.
            // Leave their values and curves unchanged for prepared resolution;
            // neither a stale reference nor a future shape has a native scalar
            // response on the current mesh.
            if (scalarPlan.CommittedMorphs.Count == 0) return;
            var competingUpperLayers = new HashSet<int>();
            if (nativeSlot && sourceState != null)
            {
                var authored = ExpressionDependencies.NormalInputLayers(runtime, metadata, null, excludedPath);
                var causalWrites = new HashSet<string>(authored.Take(layerIndex.Value).SelectMany(layer => layer.Writes), StringComparer.Ordinal);
                causalWrites.UnionWith(sourceBehaviours.Where(VrChatParameterDriver.IsDriver)
                    .SelectMany(behaviour => VrChatParameterDriver.Read(behaviour, sourceState.name).Operations)
                    .Select(operation => operation.Destination));
                var support = new HashSet<int>();
                bool SingleStateBlendTree(AnimatorControllerLayer layer)
                {
                    var states = new List<AnimatorState>(); var machines = new HashSet<AnimatorStateMachine>();
                    bool Visit(AnimatorStateMachine machine)
                    {
                        if (machine == null || !machines.Add(machine)) return false;
                        states.AddRange(machine.states.Select(child => child.state));
                        return machine.stateMachines.All(child => Visit(child.stateMachine));
                    }
                    return Visit(layer.stateMachine) && states.Count == 1 && states[0] != null && states[0].motion is BlendTree;
                }
                for (var index = layerIndex.Value + 1; index < originalLayers.Length; index++)
                    if (permanent.ContainsKey(index) || parameterLayers.ContainsKey(index) ||
                        originalLayers[index].blendingMode == AnimatorLayerBlendingMode.Additive ||
                        SingleStateBlendTree(originalLayers[index]) ||
                        HasEquivalentConstantStates(originalController, index, replacements))
                    {
                        support.Add(index); causalWrites.UnionWith(authored[index].Writes);
                    }
                bool changed;
                do
                {
                    changed = false;
                    for (var index = layerIndex.Value + 1; index < originalLayers.Length; index++)
                        if (!support.Contains(index) && authored[index].Reads.Overlaps(causalWrites))
                        {
                            support.Add(index); causalWrites.UnionWith(authored[index].Writes); changed = true;
                        }
                } while (changed);
                // A direct clip is the selected authored face. A separate,
                // non-permanent upper face selector must not replace it with
                // that selector's normal/default alternative. Keep lower native
                // pose, permanent/equivalent/additive and single-state BlendTree
                // configuration support, and upper routes
                // causally driven by the actual selected callbacks. Disjoint
                // appearance layers retain their native WD and authored slots.
                for (var index = layerIndex.Value + 1; index < originalLayers.Length; index++)
                    if (!support.Contains(index) && authored[index].Morphs.Overlaps(explicitMorphs))
                        competingUpperLayers.Add(index);
            }
            var owned = new List<UnityEngine.Object>();
            try
            {
                List<MorphValue> Probe(float input)
                {
                    var clip = new AnimationClip { name = "VRVlog expression override probe", hideFlags = HideFlags.HideAndDontSave }; owned.Add(clip);
                    foreach (var value in entry.Values)
                        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(value.Path, typeof(SkinnedMeshRenderer), "blendShape." + value.Shape),
                            AnimationCurve.Constant(0, 1, input));
                    var controller = new AnimatorController { name = "VRVlog expression override probe", hideFlags = HideFlags.HideAndDontSave }; owned.Add(controller);
                    controller.parameters = originalController.parameters.Select(parameter => new AnimatorControllerParameter
                    {
                        name = parameter.name, type = parameter.type, defaultBool = parameter.defaultBool,
                        defaultInt = parameter.defaultInt, defaultFloat = parameter.defaultFloat
                    }).ToArray();
                    var layers = new List<AnimatorControllerLayer>();
                    AnimatorControllerLayer Layer(AnimatorControllerLayer original, AnimationClip motion, bool defaults, float weight, bool selected = false)
                    {
                        var layerName = original?.name ?? "Direct expression";
                        // Unity hashes a root state's path using its root
                        // machine name. Keep that equal to the layer name,
                        // as AnimatorController.AddLayer does, so the native
                        // state can be resolved by fixed-pose validation.
                        var machine = new AnimatorStateMachine { name = layerName, hideFlags = HideFlags.HideAndDontSave }; owned.Add(machine);
                        var leaf = machine;
                        if (selected && sourceMachines != null)
                            for (var index = 0; index < sourceMachines.Length; index++)
                            {
                                leaf.behaviours = sourceMachines[index].behaviours;
                                if (index + 1 == sourceMachines.Length) break;
                                var child = leaf.AddStateMachine(sourceMachines[index + 1].name); owned.Add(child);
                                var enter = leaf.AddEntryTransition(child); owned.Add(enter); leaf = child;
                            }
                        var state = leaf.AddState("Probe"); owned.Add(state); state.motion = motion; state.writeDefaultValues = defaults;
                        if (selected && sourceBehaviours != null) state.behaviours = sourceBehaviours;
                        leaf.defaultState = state;
                        return new AnimatorControllerLayer { name = layerName, stateMachine = machine,
                            avatarMask = original?.avatarMask, blendingMode = original?.blendingMode ?? AnimatorLayerBlendingMode.Override,
                            defaultWeight = weight, syncedLayerIndex = -1, iKPass = false };
                    }
                    if (nativeSlot)
                    {
                        // A fractional authored slot blends with the evaluated
                        // lower pose, including stable default states that have
                        // other possible transitions. Preserve their graph;
                        // dependency analysis and fixed-pose checks decide which
                        // layers can safely support this scalar response.
                        layers.AddRange(originalLayers);
                        var original = originalLayers[layerIndex.Value];
                        layers[layerIndex.Value] = Layer(original, clip, writeDefaults,
                            layerIndex.Value == 0 ? 1 : original.defaultWeight, selected: true);
                        foreach (var index in competingUpperLayers)
                            layers[index] = Layer(originalLayers[index], null, false, 0);
                    }
                    else
                    {
                        // Standalone registered clips form the base below the
                        // permanent FX layers. Keep their authored direct-clip
                        // contract, as well as clips in the original base slot.
                        if (!layerIndex.HasValue) layers.Add(Layer(null, clip, false, 1));
                        for (var index = 0; index < originalLayers.Length; index++)
                        {
                            var original = originalLayers[index];
                            var selected = layerIndex == index;
                            permanent.TryGetValue(index, out var fixedClip);
                            if (fixedClip != null && !AnimationUtility.GetCurveBindings(fixedClip).Any(stationaryBindings.Contains)) fixedClip = null;
                            if (parameterLayers.TryGetValue(index, out var parameterClip)) fixedClip = parameterClip;
                            var motion = selected ? clip : fixedClip;
                            layers.Add(Layer(original, motion, selected ? writeDefaults : motion != null && original.stateMachine.defaultState.writeDefaultValues,
                                motion == null ? 0 : index == 0 ? 1 : original.defaultWeight, selected));
                        }
                    }
                    controller.layers = layers.ToArray();
                    RuntimeAnimatorController probeRuntime = controller;
                    if (nativeSlot && replacements.Count > 0)
                    {
                        var overrides = new AnimatorOverrideController(controller)
                        { name = "VRVlog expression effective override probe", hideFlags = HideFlags.HideAndDontSave };
                        owned.Add(overrides);
                        var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                        overrides.GetOverrides(pairs);
                        for (var index = 0; index < pairs.Count; index++)
                            if (replacements.TryGetValue(pairs[index].Key, out var replacement))
                                pairs[index] = new KeyValuePair<AnimationClip, AnimationClip>(pairs[index].Key, replacement);
                        overrides.ApplyOverrides(pairs);
                        probeRuntime = overrides;
                    }
                    if (omittedDriverWrites?.Count > 0)
                    {
                        // Removing an unproven callback must not establish a
                        // custom default as an invariant. Check its writes
                        // against the unpruned graph before neutral reachability
                        // can hide an alternative lower pose.
                        var originalScope = ExpressionDependencies.Analyze(probeRuntime, Array.Empty<string>(), excludedPath,
                            metadata, defaults, new Dictionary<string, float>(), scalarPlan.CommittedMorphs, nativeSlot, context);
                        if (omittedDriverWrites.Overlaps(originalScope.Parameters))
                            throw new InvalidOperationException("選択したFXレイヤーのParameter Driverが他の表情レイヤーに影響するため、直接クリップとの合成を確定できません: " +
                                string.Join(", ", omittedDriverWrites.Intersect(originalScope.Parameters).OrderBy(name => name, StringComparer.Ordinal)));
                    }
                    var dependencies = ExpressionDependencies.AnalyzeNeutral(probeRuntime, scalarPlan.CommittedMorphs,
                        excludedPath, metadata, fixedContext: context, preserveCommittedMorphs: true);
                    var values = SampleNeutral(avatar, probeRuntime, dependencies, metadata, excludedPath, context, scalarPlan);
                    context.UsedParameters.UnionWith(dependencies.Parameters.Where(context.Values.ContainsKey));
                    // A supplied input can establish that a gate is false,
                    // disappearing from the reachable dependency reads. That
                    // proof still used its fixed value. Record raw reads only
                    // on retained evaluation/support layers, not every input
                    // declared anywhere on the avatar.
                    var authoredInputs = ExpressionDependencies.NormalInputLayers(probeRuntime, metadata, null, excludedPath);
                    context.UsedParameters.UnionWith(dependencies.Layers.Concat(dependencies.NativeSupportLayers)
                        .SelectMany(index => authoredInputs[index].Reads).Where(context.Values.ContainsKey));
                    return values;
                }
                var zero = Probe(0).ToDictionary(value => (value.Path, value.Shape));
                var full = Probe(100).ToDictionary(value => (value.Path, value.Shape));
                if (!zero.Keys.ToHashSet().SetEquals(full.Keys))
                    throw new InvalidOperationException("常時適用FXと表情の影響範囲を確定できません。");
                var responses = zero.ToDictionary(pair => pair.Key, pair => (Offset: (double)pair.Value.Weight,
                    Scale: (full[pair.Key].Weight - pair.Value.Weight) / 100.0));
                foreach (var value in entry.Values)
                    if (responses.TryGetValue((value.Path, value.Shape), out var response))
                        value.Weight = (float)(response.Offset + response.Scale * value.Weight);
                var existing = new HashSet<(string, string)>(entry.Values.Select(value => (value.Path, value.Shape)));
                foreach (var pair in zero)
                    if (existing.Add(pair.Key)) entry.Values.Add(pair.Value);
                foreach (var animation in entry.Animation)
                {
                    if (!responses.TryGetValue((animation.Path, animation.Shape), out var response)) continue;
                    foreach (var key in animation.Curve.Keys)
                    {
                        key.Value = response.Offset + response.Scale * key.Value;
                        if (key.InTangent.HasValue) key.InTangent *= response.Scale;
                        if (key.OutTangent.HasValue) key.OutTangent *= response.Scale;
                    }
                    animation.Curve.Validate();
                }
                entry.Animation.RemoveAll(animation => { animation.Curve.Range(out var minimum, out var maximum); return minimum == maximum; });
                if (entry.Animation.Count == 0) { entry.Duration = 0; entry.Loop = false; }
                if (context.UsedParameters.Count > 0)
                    entry.Messages.Add("外部入力は通常状態・設定済み初期値で固定表情として保存しました（自動切り替えは再現しません）: " +
                        string.Join(", ", context.UsedParameters.OrderBy(name => name, StringComparer.Ordinal).Select(name => name + "=" +
                            context.Values[name].ToString("G9", CultureInfo.InvariantCulture))));
            }
            finally
            {
                foreach (var asset in owned) if (asset != null) UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        private static HashSet<EditorCurveBinding> StationaryBindings(GameObject avatar, RuntimeAnimatorController runtime,
            Func<string, bool> excludedPath)
        {
            var result = ExpressionDependencies.StationaryMorphBindings(runtime, excludedPath);
            // Unused inactive wardrobe layers cannot become a reason to sample
            // another outfit or reject an otherwise retained facial expression.
            result.RemoveWhere(binding =>
            {
                var target = string.IsNullOrEmpty(binding.path) ? avatar.transform : avatar.transform.Find(binding.path);
                var renderer = target == null ? null : target.GetComponent<SkinnedMeshRenderer>();
                return renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.sharedMesh == null;
            });
            return result;
        }

        private static List<MorphValue> Evaluate(GameObject avatar, RuntimeAnimatorController runtime,
            IDictionary<string, float> defaults, IDictionary<string, float> selected, Func<string, bool> excludedPath,
            VrChatExpressionMenu.Source metadata, IList<MorphValue> unevaluated, IEnumerable<EditorCurveBinding> initialMorphs,
            bool preserveNativeBasePose = false, FixedExpressionContext fixedContext = null, ISet<string> omittedDriverWrites = null)
        {
            if (fixedContext != null)
            {
                var resolved = new Dictionary<string, float>(defaults, StringComparer.Ordinal);
                foreach (var input in fixedContext.Values) resolved[input.Key] = input.Value;
                defaults = resolved;
            }
            var originalController = ExpressionDependencies.Controller(runtime);
            var dependencies = ExpressionDependencies.Analyze(runtime, selected.Keys, excludedPath, metadata, defaults, selected, initialMorphs, preserveNativeBasePose, fixedContext);
            if (omittedDriverWrites?.Overlaps(dependencies.Parameters) == true)
                throw new InvalidOperationException("選択したFXレイヤーのParameter Driverが他の表情レイヤーに影響するため、直接クリップとの合成を確定できません: " +
                    string.Join(", ", omittedDriverWrites.Intersect(dependencies.Parameters).OrderBy(name => name, StringComparer.Ordinal)));
            var omittedLayers = FindExcludedLayers(originalController, runtime, excludedPath, dependencies.Parameters);
            dependencies.Layers.ExceptWith(omittedLayers);
            dependencies.NativeSupportLayers.ExceptWith(omittedLayers);
            var affected = dependencies.Layers.Except(dependencies.NativeSupportLayers).OrderBy(i => i).ToArray();
            if (affected.Length == 0) throw new InvalidOperationException("このメニューに対応するFXの表情がありません。");
            var excludedLayers = new HashSet<int>(Enumerable.Range(0, originalController.layers.Length)
                .Except(affected.Concat(dependencies.NativeSupportLayers)));
            using var evaluation = new ExpressionEvaluationSession(runtime, dependencies, metadata?.ExpressionParameters,
                !defaults.TryGetValue("IsLocal", out var local) || local != 0, fixedContext);
            var controller = evaluation.Controller;
            var equivalentStates = new HashSet<int>(affected.Concat(dependencies.NativeSupportLayers)
                .Where(layer => HasEquivalentConstantStates(controller, layer)));
            unevaluated = unevaluated ?? new List<MorphValue>();
            ValidateNativeSupportMotions(controller, dependencies.NativeSupportLayers, excludedPath);

            var scene = EditorSceneManager.NewPreviewScene();
            GameObject clone = null;
            var graph = default(PlayableGraph);
            try
            {
                clone = UnityEngine.Object.Instantiate(avatar);
                clone.name = avatar.name;
                clone.hideFlags = HideFlags.HideAndDontSave;
                SceneManager.MoveGameObjectToScene(clone, scene);
                foreach (var behaviour in clone.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                var animator = clone.GetComponent<Animator>() ?? clone.AddComponent<Animator>();
                animator.runtimeAnimatorController = null;
                animator.enabled = true;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.applyRootMotion = false;
                animator.fireEvents = false;
                evaluation.Animator = animator;
                clone.SetActive(true);
                graph = PlayableGraph.Create("VR Vlog expression sampling");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                var output = AnimationPlayableOutput.Create(graph, "Expression", animator);
                output.SetSourcePlayable(playable);
                SetParameters(playable, controller, defaults);
                graph.Play();
                var history = new HashSet<EditorCurveBinding>();
                var visitedClips = new HashSet<AnimationClip>();
                var visitedSupportClips = new HashSet<AnimationClip>();
                var supportLayers = dependencies.NativeSupportLayers.OrderBy(i => i).ToArray();
                Action remember = () =>
                {
                    evaluation.Check();
                    RememberBindings(playable, affected, history, visitedClips, excludedPath);
                    // Support clips retain history safety checks without
                    // making their unrelated morphs export targets.
                    RememberBindings(playable, supportLayers, history, visitedSupportClips, excludedPath, dependencies.Morphs);
                };
                // Initialize before a short-lived WD-Off state exits; its writes
                // remain part of the eventual neutral and selected appearance.
                graph.Evaluate(0f);
                remember();
                Advance(graph, 120, remember);
                evaluation.CheckNeutralFx();
                SetParameters(playable, controller, selected);
                Advance(graph, 120, remember);
                ValidateFixedPose(avatar, playable, controller, excludedPath, excludedLayers, equivalentStates,
                    dependencies.NativeSupportLayers, dependencies.Morphs);
                var bindings = ActiveBindings(playable, affected, excludedPath, equivalentStates);
                var values = Capture(avatar, clone, history, excludedPath, dependencies.Morphs, unevaluated);
                // A state transition, a changing curve or changing active clip
                // set cannot be represented as one fixed VRM expression.
                for (var checkpoint = 0; checkpoint < 3; checkpoint++)
                {
                    Advance(graph, 7 + checkpoint, evaluation.Check);
                    var nextBindings = ActiveBindings(playable, affected, excludedPath, equivalentStates);
                    if (!bindings.SetEquals(nextBindings)) throw new InvalidOperationException("表情が時間で切り替わるため、固定表情に変換できません。");
                    var next = Capture(avatar, clone, history, excludedPath, dependencies.Morphs, unevaluated);
                    if (values.Count != next.Count || values.Where((v, i) => v.Path != next[i].Path || v.Shape != next[i].Shape || Math.Abs(v.Weight - next[i].Weight) > 0.01f).Any())
                        throw new InvalidOperationException("表情のアニメーションが静止しません。固定表情のみ取り込めます。");
                }
                if (values.Count == 0 && unevaluated.Count == 0) throw new InvalidOperationException("有効な顔のBlendShapeアニメーションがありません。");
                if (fixedContext != null)
                    fixedContext.UsedParameters.UnionWith(dependencies.Parameters.Where(fixedContext.Values.ContainsKey));
                return values;
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        // Neutral appearance has no selected menu parameter. Its dependency
        // roots are the morph writers themselves, including startup WD-Off
        // values retained after an initializer leaves its state.
        internal static List<MorphValue> SampleNeutral(GameObject avatar, RuntimeAnimatorController runtime,
            ExpressionDependencies dependencies, VrChatExpressionMenu.Source metadata, Func<string, bool> excludedPath,
            FixedExpressionContext fixedContext = null, NeutralShapePlan neutralPlan = null, bool preserveTemporalRest = false)
        {
            if (preserveTemporalRest && neutralPlan == null) throw new ArgumentNullException(nameof(neutralPlan));
            var originalController = ExpressionDependencies.Controller(runtime);
            var omittedLayers = FindExcludedLayers(originalController, runtime, excludedPath, dependencies.Parameters);
            dependencies.Layers.ExceptWith(omittedLayers);
            dependencies.NativeSupportLayers.ExceptWith(omittedLayers);
            var affected = dependencies.Layers.Except(dependencies.NativeSupportLayers).OrderBy(index => index).ToArray();
            if (affected.Length == 0) return new List<MorphValue>();
            var excludedLayers = new HashSet<int>(Enumerable.Range(0, originalController.layers.Length)
                .Except(affected.Concat(dependencies.NativeSupportLayers)));
            var requiredParameterCurves = NeutralParameterDependencies.Required(runtime, dependencies, excludedPath);
            using var evaluation = new ExpressionEvaluationSession(runtime, dependencies, metadata.ExpressionParameters,
                !metadata.Defaults.TryGetValue("IsLocal", out var local) || local != 0, fixedContext);
            var controller = evaluation.Controller;
            ValidateNativeSupportMotions(controller, dependencies.NativeSupportLayers, excludedPath, avatar, neutralPlan,
                ExpressionDependencies.NeutralClips(runtime, dependencies.NeutralFixedValues, excludedPath));
            var scene = EditorSceneManager.NewPreviewScene();
            GameObject clone = null;
            var graph = default(PlayableGraph);
            try
            {
                clone = UnityEngine.Object.Instantiate(avatar);
                clone.name = avatar.name;
                clone.hideFlags = HideFlags.HideAndDontSave;
                SceneManager.MoveGameObjectToScene(clone, scene);
                foreach (var behaviour in clone.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                var animator = clone.GetComponent<Animator>() ?? clone.AddComponent<Animator>();
                animator.runtimeAnimatorController = null;
                animator.enabled = true;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.applyRootMotion = false;
                animator.fireEvents = false;
                evaluation.Animator = animator;
                clone.SetActive(true);
                graph = PlayableGraph.Create("VR Vlog neutral shape sampling");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                AnimationPlayableOutput.Create(graph, "Neutral shape", animator).SetSourcePlayable(playable);
                var defaults = new Dictionary<string, float>(metadata.Defaults, StringComparer.Ordinal);
                if (fixedContext != null)
                    foreach (var input in fixedContext.Values) defaults[input.Key] = input.Value;
                foreach (var pair in dependencies.NeutralFixedValues) defaults[pair.Key] = pair.Value;
                SetParameters(playable, controller, defaults);
                graph.Play();
                var history = new HashSet<EditorCurveBinding>();
                var visited = new HashSet<AnimationClip>();
                var visitedSupportClips = new HashSet<AnimationClip>();
                var supportLayers = dependencies.NativeSupportLayers.OrderBy(index => index).ToArray();
                Action remember = () =>
                {
                    evaluation.Check();
                    RememberBindings(playable, affected, history, visited, excludedPath,
                        neutralPlan == null ? null : dependencies.Morphs, avatar, neutralPlan);
                    RememberBindings(playable, supportLayers, history, visitedSupportClips, excludedPath, dependencies.Morphs, avatar, neutralPlan);
                };
                // Initialize at time zero before a short-lived default state can exit.
                // Its WD-Off writes remain part of the eventual neutral appearance.
                graph.Evaluate(0f);
                remember();
                Advance(graph, 120, remember);
                evaluation.CheckNeutralFx();
                var equivalentStates = EquivalentNeutralLayers(playable, controller, affected.Concat(supportLayers), dependencies, metadata);
                var capturedMorphs = new HashSet<EditorCurveBinding>(dependencies.Morphs);
                ISet<(int Layer, AnimationClip Clip, EditorCurveBinding Binding)> shadowedCurves = null;
                if (preserveTemporalRest)
                {
                    shadowedCurves = PreserveTemporalRest(avatar, playable, controller, affected.Concat(supportLayers), capturedMorphs, excludedPath, neutralPlan);
                    capturedMorphs.ExceptWith(neutralPlan.TemporalMorphs);
                }
                ValidateFixedPose(avatar, playable, controller, excludedPath, excludedLayers, equivalentStates,
                    dependencies.NativeSupportLayers, capturedMorphs, neutral: true,
                    neutralFixed: dependencies.NeutralFixedValues, dependencies: dependencies, metadata: metadata,
                    restrictCapturedMorphs: neutralPlan != null, neutralPlan: neutralPlan, shadowedNeutralCurves: shadowedCurves,
                    requiredParameterCurves: requiredParameterCurves);
                var bindings = ActiveBindings(playable, affected, excludedPath, equivalentStates, neutral: true, neutralAvatar: avatar,
                    neutralPlan: neutralPlan, capturedMorphs: neutralPlan == null ? null : capturedMorphs);
                var unresolved = new List<MorphValue>();
                var values = Capture(avatar, clone, history.Where(capturedMorphs.Contains), excludedPath, capturedMorphs, unresolved);
                for (var checkpoint = 0; checkpoint < 3; checkpoint++)
                {
                    Advance(graph, 7 + checkpoint, evaluation.Check);
                    var nextBindings = ActiveBindings(playable, affected, excludedPath, equivalentStates, neutral: true, neutralAvatar: avatar,
                        neutralPlan: neutralPlan, capturedMorphs: neutralPlan == null ? null : capturedMorphs);
                    if (!bindings.SetEquals(nextBindings))
                        throw new NeutralShapeSamplingException("初期表情が時間で切り替わるため、基本の顔として保存できません。");
                    var next = Capture(avatar, clone, history.Where(capturedMorphs.Contains), excludedPath, capturedMorphs, unresolved);
                    if (values.Count != next.Count || values.Where((value, index) => value.Path != next[index].Path ||
                        value.Shape != next[index].Shape || Math.Abs(value.Weight - next[index].Weight) > .01f).Any())
                        throw new NeutralShapeSamplingException("初期表情のアニメーションが静止しません。");
                }
                return values;
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        // Rest reconstruction is different from selecting an authored animated
        // expression. Inspect full effective curves on the native active graph,
        // including delayed/weighted variation, without projecting its clips or
        // removing any WD/additive/control support. Only the final scalar capture
        // loses these exact temporal channels; all graph safety checks still run.
        private static HashSet<(int Layer, AnimationClip Clip, EditorCurveBinding Binding)> PreserveTemporalRest(GameObject avatar, AnimatorControllerPlayable playable,
            AnimatorController controller,
            IEnumerable<int> layers, ISet<EditorCurveBinding> capturedMorphs, Func<string, bool> excludedPath, NeutralShapePlan plan)
        {
            var included = layers.Distinct().OrderBy(index => index).ToArray();
            var shadowed = new HashSet<(int Layer, AnimationClip Clip, EditorCurveBinding Binding)>();
            bool Dominated(int lower, EditorCurveBinding binding)
            {
                foreach (var upper in included.Where(index => index > lower))
                {
                    var definition = controller.layers[upper];
                    if (definition.blendingMode != AnimatorLayerBlendingMode.Override || definition.avatarMask != null ||
                        playable.GetLayerWeight(upper) != 1 || playable.IsInTransition(upper)) continue;
                    var current = playable.GetCurrentAnimatorClipInfo(upper).Where(info => info.clip != null && info.weight > .00001f).ToArray();
                    if (current.Length != 1 || current[0].weight != 1 ||
                        playable.GetNextAnimatorClipInfo(upper).Any(info => info.clip != null && info.weight > .00001f)) continue;
                    var curve = AnimationUtility.GetEditorCurve(current[0].clip, binding);
                    if (curve == null || curve.length == 0 || !IsConstant(curve)) continue;
                    VrChatGestureExpressions.ReadCurve(curve);
                    return true;
                }
                return false;
            }
            foreach (var layer in included)
            {
                if (layer > 0 && playable.GetLayerWeight(layer) <= .00001f) continue;
                foreach (var info in playable.GetCurrentAnimatorClipInfo(layer).Concat(playable.GetNextAnimatorClipInfo(layer)))
                {
                    if (info.clip == null || info.weight <= .00001f) continue;
                    foreach (var binding in AnimationUtility.GetCurveBindings(info.clip))
                    {
                        if (!capturedMorphs.Contains(binding) || excludedPath?.Invoke(binding.path) == true ||
                            binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)) continue;
                        var curve = AnimationUtility.GetEditorCurve(info.clip, binding);
                        if (IsConstant(curve)) continue;
                        if (FindRenderer(avatar, binding.path).sharedMesh.GetBlendShapeIndex(binding.propertyName.Substring("blendShape.".Length)) < 0) continue;
                        // A malformed or unrepresentable curve is not a temporal
                        // preservation proof. Use the authored program's complete
                        // curve validation rather than a few sampled values.
                        VrChatGestureExpressions.ReadCurve(curve);
                        // A fully weighted, unmasked Override with one explicit
                        // constant curve replaces this exact lower scalar. This
                        // bounded native rule does not infer cancellation across
                        // fractional, Additive, masked or blended upper writers.
                        // Full state/parameter/callback safety is still checked.
                        if (Dominated(layer, binding)) { shadowed.Add((layer, info.clip, binding)); continue; }
                        plan.PreserveTemporalRest(info.clip, binding);
                    }
                }
            }
            return shadowed;
        }

        // A current pet-only state can still transition to a facial state later.
        // Ignore a layer only after proving every possible motion writes solely
        // to excluded objects. Write Defaults and empty motions can reset
        // properties not listed in their own curves, so they do not establish
        // that proof. Parameter dependencies and unknown behaviours are checked first.
        internal static HashSet<int> FindExcludedLayers(AnimatorController controller, RuntimeAnimatorController runtime,
            Func<string, bool> excludedPath, ISet<string> requiredParameters = null)
        {
            var result = new HashSet<int>();
            if (excludedPath == null) return result;
            var replacements = new Dictionary<AnimationClip, AnimationClip>();
            if (runtime is AnimatorOverrideController overrides)
            {
                var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                overrides.GetOverrides(pairs);
                foreach (var pair in pairs) if (pair.Value != null) replacements[pair.Key] = pair.Value;
            }
            var layers = controller.layers;
            for (var layer = 0; layer < layers.Length; layer++)
            {
                if (layers[layer].syncedLayerIndex >= 0 || layers[layer].iKPass) continue;
                var foundExcludedBinding = false;
                var motionStack = new HashSet<Motion>();
                var machineStack = new HashSet<AnimatorStateMachine>();
                bool BindingIsExcluded(EditorCurveBinding binding)
                {
                    if (binding.type == typeof(Animator) || excludedPath(binding.path) != true) return false;
                    foundExcludedBinding = true;
                    return true;
                }
                bool MotionIsExcluded(Motion motion)
                {
                    if (motion == null) return false;
                    if (!motionStack.Add(motion)) return false;
                    try
                    {
                        if (motion is AnimationClip clip)
                        {
                            if (replacements.TryGetValue(clip, out var replacement)) clip = replacement;
                            var bindings = AnimationUtility.GetCurveBindings(clip)
                                .Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip)).ToArray();
                            return bindings.Length > 0 && bindings.All(BindingIsExcluded);
                        }
                        return motion is BlendTree tree && tree.children.Length > 0 && tree.children.All(child => MotionIsExcluded(child.motion));
                    }
                    finally { motionStack.Remove(motion); }
                }
                bool MachineIsExcluded(AnimatorStateMachine machine)
                {
                    if (machine == null || !machineStack.Add(machine)) return false;
                    try
                    {
                        bool HasEffect(IEnumerable<StateMachineBehaviour> behaviours) => behaviours.Any(b =>
                            !VrChatParameterDriver.IsTracking(b) && !VrChatParameterDriver.IsNonFxPlayableControl(b) && !VrChatParameterDriver.IsTemporaryPoseSpace(b) && !VrChatParameterDriver.IsLocomotionControl(b) && (!VrChatParameterDriver.IsDriver(b) ||
                            requiredParameters == null || VrChatParameterDriver.Read(b, machine.name).Operations.Any(op => requiredParameters.Contains(op.Destination))));
                        return machine.states.Length + machine.stateMachines.Length > 0 && !HasEffect(machine.behaviours) &&
                            machine.states.All(child => !child.state.writeDefaultValues && !child.state.iKOnFeet && !HasEffect(child.state.behaviours) && MotionIsExcluded(child.state.motion)) &&
                            machine.stateMachines.All(child => MachineIsExcluded(child.stateMachine));
                    }
                    finally { machineStack.Remove(machine); }
                }
                if (MachineIsExcluded(layers[layer].stateMachine) && foundExcludedBinding) result.Add(layer);
            }
            return result;
        }

        private static void Advance(PlayableGraph graph, int frames, Action afterFrame = null)
        {
            for (var i = 0; i < frames; i++) { graph.Evaluate(1f / 60f); afterFrame?.Invoke(); }
        }

        // Native evaluation can enter and leave zero-duration states before
        // clip-info history observes them. Validate all support motions before
        // evaluation; unrelated morph curves may vary, but callbacks and object
        // or non-morph changes cannot safely supply a native base pose.
        private static void ValidateNativeSupportMotions(AnimatorController controller, IEnumerable<int> supportLayers,
            Func<string, bool> excludedPath, GameObject neutralAvatar = null, NeutralShapePlan neutralPlan = null,
            ISet<AnimationClip> reachableClips = null)
        {
            var machines = new HashSet<AnimatorStateMachine>();
            var motions = new HashSet<Motion>();
            void Motion(Motion motion)
            {
                if (motion == null || !motions.Add(motion)) return;
                if (motion is BlendTree tree)
                {
                    foreach (var child in tree.children) Motion(child.motion);
                    return;
                }
                if (!(motion is AnimationClip clip)) throw new InvalidOperationException("未対応のAnimator Motionです。");
                if (reachableClips != null && !reachableClips.Contains(clip)) return;
                if (AnimationUtility.GetAnimationEvents(clip).Length != 0)
                    throw new InvalidOperationException("常時適用FXと表情の影響範囲を確定できません。");
                if (AnimationUtility.GetObjectReferenceCurveBindings(clip).Any(binding => excludedPath?.Invoke(binding.path) != true &&
                    neutralPlan?.AllowsEvaluationBinding(clip, binding) != true))
                    throw new InvalidOperationException("表情への遷移にマテリアル・オブジェクトの差し替えが含まれます。");
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (binding.type == typeof(Animator) || excludedPath?.Invoke(binding.path) == true) continue;
                    if (IsHarmlessNeutralActivation(neutralAvatar, clip, binding)) continue;
                    if (neutralPlan?.AllowsEvaluationBinding(clip, binding) == true) continue;
                    if (binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                        throw new InvalidOperationException("表情への遷移にBlendShape以外の変化が含まれます: " +
                            clip.name + " / " + binding.path + " / " + binding.propertyName + NeutralBindingDiagnostic(neutralPlan, clip, binding));
                }
            }
            void Machine(AnimatorStateMachine machine)
            {
                if (machine == null || !machines.Add(machine)) return;
                foreach (var child in machine.states) Motion(child.state.motion);
                foreach (var child in machine.stateMachines) Machine(child.stateMachine);
            }
            foreach (var layer in supportLayers) Machine(controller.layers[layer].stateMachine);
        }

        private static void RememberBindings(AnimatorControllerPlayable playable, int[] layers, HashSet<EditorCurveBinding> history, HashSet<AnimationClip> visited, Func<string, bool> excludedPath,
            ISet<EditorCurveBinding> capturedMorphs = null, GameObject neutralAvatar = null, NeutralShapePlan neutralPlan = null)
        {
            foreach (var layer in layers)
                foreach (var info in playable.GetCurrentAnimatorClipInfo(layer).Concat(playable.GetNextAnimatorClipInfo(layer)))
                {
                    if (info.clip == null || info.weight <= 0.00001f || !visited.Add(info.clip)) continue;
                    if (capturedMorphs != null && AnimationUtility.GetAnimationEvents(info.clip).Length != 0)
                        throw new InvalidOperationException("常時適用FXと表情の影響範囲を確定できません。");
                    if (AnimationUtility.GetObjectReferenceCurveBindings(info.clip).Any(b => excludedPath?.Invoke(b.path) != true &&
                        neutralPlan?.AllowsEvaluationBinding(info.clip, b) != true))
                        throw new InvalidOperationException("表情への遷移にマテリアル・オブジェクトの差し替えが含まれます。");
                    foreach (var binding in AnimationUtility.GetCurveBindings(info.clip))
                    {
                        if (binding.type == typeof(Animator)) continue; // Parameter curves are checked for stability separately.
                        if (excludedPath?.Invoke(binding.path) == true) continue;
                        if (IsHarmlessNeutralActivation(neutralAvatar, info.clip, binding)) continue;
                        if (neutralPlan?.AllowsEvaluationBinding(info.clip, binding) == true) continue;
                        if (binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                            throw new InvalidOperationException("表情への遷移にBlendShape以外の変化が含まれます: " +
                                info.clip.name + " / " + binding.path + " / " + binding.propertyName + NeutralBindingDiagnostic(neutralPlan, info.clip, binding));
                        if (capturedMorphs == null || capturedMorphs.Contains(binding)) history.Add(binding);
                    }
                }
        }

        // A transition need not be stationary when its entire layer always
        // produces the same pose and parameter values. Keep the layer in native
        // evaluation, including its Write Defaults; prove equivalence without
        // assuming that any transition condition remains false in the future.
        internal static bool HasEquivalentConstantStates(AnimatorController controller, int layerIndex,
            IDictionary<AnimationClip, AnimationClip> replacements = null)
        {
            var layer = controller.layers[layerIndex];
            var machine = layer.stateMachine;
            if (layer.syncedLayerIndex >= 0 || layer.iKPass || machine == null || machine.behaviours.Length != 0 ||
                machine.stateMachines.Length != 0 || machine.states.Length == 0) return false;
            var states = new HashSet<AnimatorState>(machine.states.Select(child => child.state));
            if (states.Contains(null) || machine.defaultState == null || !states.Contains(machine.defaultState)) return false;
            AnimationClip EffectiveClip(AnimatorState state)
            {
                if (!(state.motion is AnimationClip value)) return null;
                return replacements != null && replacements.TryGetValue(value, out var replacement) ? replacement : value;
            }
            var clip = EffectiveClip(machine.defaultState);
            if (clip == null) return false;
            var writeDefaults = machine.defaultState.writeDefaultValues;
            if (states.Any(state => EffectiveClip(state) != clip || state.writeDefaultValues != writeDefaults || state.behaviours.Length != 0 ||
                state.iKOnFeet || state.timeParameterActive || state.speedParameterActive || state.mirrorParameterActive ||
                state.cycleOffsetParameterActive || state.speed != 1 || state.cycleOffset != 0 || state.mirror)) return false;
            var parameters = new HashSet<string>(controller.parameters.Where(parameter => parameter.type != AnimatorControllerParameterType.Trigger)
                .Select(parameter => parameter.name), StringComparer.Ordinal);
            if (AnimationUtility.GetAnimationEvents(clip).Length != 0 || AnimationUtility.GetObjectReferenceCurveBindings(clip).Length != 0 ||
                AnimationUtility.GetCurveBindings(clip).Any(binding =>
                    !(binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal) ||
                      binding.type == typeof(Animator) && parameters.Contains(binding.propertyName)) ||
                    !IsConstant(AnimationUtility.GetEditorCurve(clip, binding)))) return false;
            return machine.anyStateTransitions.Cast<AnimatorTransitionBase>().Concat(machine.entryTransitions)
                .Concat(states.SelectMany(state => state.transitions)).All(transition =>
                    !transition.isExit && transition.destinationStateMachine == null && states.Contains(transition.destinationState));
        }

        // Equal clips prove a layer's pose, but an Animator-parameter relay
        // also participates in global timed conditions. Its constant curve is
        // not a proof of the global parameter when a driver/another layer may
        // write it, or when a fractional layer blends it with another value.
        // Preserve the neutral sampler's stricter full-writer contract even
        // while accepting a genuinely equivalent layer mid-transition.
        private static HashSet<int> EquivalentNeutralLayers(AnimatorControllerPlayable playable, AnimatorController controller,
            IEnumerable<int> affected, ExpressionDependencies dependencies, VrChatExpressionMenu.Source metadata)
        {
            var layers = controller.layers;
            var writers = new HashSet<string>[layers.Length];
            var implicitWriters = new bool[layers.Length];
            for (var index = 0; index < layers.Length; index++)
            {
                var parameters = writers[index] = new HashSet<string>(StringComparer.Ordinal);
                var hasDefaults = false;
                var machines = new HashSet<AnimatorStateMachine>(); var motions = new HashSet<Motion>();
                void Motion(Motion motion)
                {
                    if (motion == null || !motions.Add(motion)) return;
                    if (motion is AnimationClip clip)
                        foreach (var binding in AnimationUtility.GetCurveBindings(clip).Where(binding => binding.type == typeof(Animator)))
                            parameters.Add(binding.propertyName);
                    else if (motion is BlendTree tree)
                        foreach (var child in tree.children) Motion(child.motion);
                }
                void Machine(AnimatorStateMachine machine)
                {
                    if (machine == null || !machines.Add(machine)) return;
                    foreach (var child in machine.states) { hasDefaults |= child.state.writeDefaultValues; Motion(child.state.motion); }
                    foreach (var child in machine.stateMachines) Machine(child.stateMachine);
                }
                Machine(layers[index].stateMachine);
                implicitWriters[index] = hasDefaults;
            }
            bool StaticDefaults(int index)
            {
                var layer = layers[index]; var machine = layer.stateMachine;
                if (layer.syncedLayerIndex >= 0 || layer.iKPass || machine == null || machine.stateMachines.Length != 0 ||
                    machine.behaviours.Length != 0 || machine.anyStateTransitions.Length != 0 || machine.entryTransitions.Length != 0 ||
                    machine.states.Length != 1 || machine.defaultState != machine.states[0].state) return false;
                var state = machine.defaultState;
                if (state == null || state.behaviours.Length != 0 || state.transitions.Length != 0 || state.iKOnFeet ||
                    state.timeParameterActive || state.speedParameterActive || state.mirrorParameterActive || state.cycleOffsetParameterActive) return false;
                return state.motion == null || state.motion is AnimationClip clip && AnimationUtility.GetObjectReferenceCurveBindings(clip).Length == 0 &&
                    AnimationUtility.GetCurveBindings(clip).All(binding => IsConstant(AnimationUtility.GetEditorCurve(clip, binding)));
            }
            var result = new HashSet<int>();
            foreach (var index in affected)
            {
                if (!HasEquivalentConstantStates(controller, index)) continue;
                var parameters = writers[index];
                if (parameters.Count > 0)
                {
                    if (index > 0 && playable.GetLayerWeight(index) != 1 ||
                        layers[index].blendingMode != AnimatorLayerBlendingMode.Override ||
                        metadata.OtherControllers.Any(other => other != null) ||
                        parameters.Any(name => VrChatParameterDriver.BuiltIn.Contains(name) || metadata.ExternalParameters.Contains(name) ||
                            dependencies.Drivers.Values.Any(program => program.Operations.Any(operation => operation.Destination == name))) ||
                        writers.Where((_, other) => other != index).Any(other => other.Overlaps(parameters)) ||
                        Enumerable.Range(0, layers.Length).Any(other => other != index && implicitWriters[other] && !StaticDefaults(other))) continue;
                }
                result.Add(index);
            }
            return result;
        }

        // Short samples alone cannot prove a fixed pose. Inspect every active
        // state's possible timed exits and the entire lifetime of active morph
        // and parameter curves, including delayed steps after the sample window.
        private static void ValidateFixedPose(GameObject avatar, AnimatorControllerPlayable playable, AnimatorController controller, Func<string, bool> excludedPath,
            ISet<int> excludedLayers, ISet<int> equivalentStates = null, ISet<int> nativeSupportLayers = null,
            ISet<EditorCurveBinding> relevantMorphs = null, bool neutral = false, IDictionary<string, float> neutralFixed = null,
            ExpressionDependencies dependencies = null, VrChatExpressionMenu.Source metadata = null, bool restrictCapturedMorphs = false,
            NeutralShapePlan neutralPlan = null, ISet<(int Layer, AnimationClip Clip, EditorCurveBinding Binding)> shadowedNeutralCurves = null,
            ISet<string> requiredParameterCurves = null)
        {
            InvalidOperationException Unstable(string message) => neutral ?
                new NeutralShapeSamplingException(message) : new InvalidOperationException(message);
            var timedValues = neutral ? NeutralCurveConditions.FixedTimedValues(playable, controller, neutralFixed, dependencies, metadata) : neutralFixed;
            var layers = controller.layers;
            for (var layer = 0; layer < layers.Length; layer++)
            {
                if (excludedLayers.Contains(layer)) continue;
                if (layer > 0 && playable.GetLayerWeight(layer) <= 0.00001f) continue;
                if (playable.IsInTransition(layer) && equivalentStates?.Contains(layer) != true)
                    throw Unstable("FXの状態遷移が静止していません。");
                var hash = playable.GetCurrentAnimatorStateInfo(layer).fullPathHash;
                if (hash == 0) continue;
                var found = false;
                void Visit(AnimatorStateMachine machine, string path, bool timedAncestor)
                {
                    var timed = timedAncestor || machine.anyStateTransitions.Any(t => !t.mute && t.hasExitTime && !ExpressionDependencies.IsFalse(t, timedValues));
                    foreach (var child in machine.states)
                    {
                        if (Animator.StringToHash(path + "." + child.state.name) != hash) continue;
                        if (found) throw new InvalidOperationException("FXの状態名を一意に特定できません。");
                        found = true;
                        if (equivalentStates?.Contains(layer) != true && (timed || child.state.transitions.Any(t =>
                            !t.mute && t.hasExitTime && !ExpressionDependencies.IsFalse(t, timedValues))))
                            throw Unstable("時間で遷移するFX状態は固定表情に変換できません: " + path + "." + child.state.name);
                    }
                    foreach (var child in machine.stateMachines) Visit(child.stateMachine, path + "." + child.stateMachine.name, timed);
                }
                Visit(layers[layer].stateMachine, layers[layer].name, false);
                if (!found) throw new InvalidOperationException("評価中のFX状態を特定できません。");
                foreach (var info in playable.GetCurrentAnimatorClipInfo(layer).Concat(playable.GetNextAnimatorClipInfo(layer)))
                    if (info.clip != null && info.weight > 0.00001f)
                    {
                        if (nativeSupportLayers?.Contains(layer) == true && (AnimationUtility.GetAnimationEvents(info.clip).Length != 0 ||
                            AnimationUtility.GetObjectReferenceCurveBindings(info.clip).Any(binding => excludedPath?.Invoke(binding.path) != true &&
                                neutralPlan?.AllowsEvaluationBinding(info.clip, binding) != true)))
                            throw new InvalidOperationException("常時適用FXと表情の影響範囲を確定できません。");
                        foreach (var binding in AnimationUtility.GetCurveBindings(info.clip))
                        {
                            if (excludedPath?.Invoke(binding.path) == true) continue;
                            if (binding.type != typeof(Animator) &&
                                !(binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))) continue;
                            // WD/additive support remains active, including its
                            // native parameter curves. An internal parameter with
                            // no path to this pose need not itself become static.
                            if (neutral && binding.type == typeof(Animator) && binding.path == "" && requiredParameterCurves != null &&
                                !requiredParameterCurves.Contains(binding.propertyName))
                            {
                                var parameterCurve = AnimationUtility.GetEditorCurve(info.clip, binding);
                                ValidateNativeParameterCurve(parameterCurve, layers[layer].name + " / " + info.clip.name + " / " + binding.propertyName);
                                continue;
                            }
                            // An uncaptured support/appearance morph may target
                            // another authored layout absent from this copy. It
                            // must not require a renderer lookup for this plan.
                            if ((nativeSupportLayers?.Contains(layer) == true || restrictCapturedMorphs) && binding.type == typeof(SkinnedMeshRenderer) &&
                                !relevantMorphs.Contains(binding)) continue;
                            // Missing source morphs cannot be sampled. Defer
                            // them to prepared bindings, which either omit a
                            // stale reference or reject an unresolved new morph.
                            if (binding.type == typeof(SkinnedMeshRenderer) && FindRenderer(avatar, binding.path).sharedMesh
                                .GetBlendShapeIndex(binding.propertyName.Substring("blendShape.".Length)) < 0) continue;
                            var curve = AnimationUtility.GetEditorCurve(info.clip, binding);
                            if (neutral && shadowedNeutralCurves?.Contains((layer, info.clip, binding)) == true) continue;
                            if (!IsConstant(curve)) throw Unstable("時間で変わるBlendShape・パラメーター曲線は固定表情に変換できません: " +
                                layers[layer].name + " / " + info.clip.name + " / " + binding.path + " / " + binding.propertyName);
                        }
                    }
            }
        }

        // These curves stay inside Unity; they are never serialized into the
        // portable expression-animation format. Validate the data Unity uses,
        // not that format's key-count, time or morph-range limits. Generators
        // can leave NaN on the first incoming or last outgoing tangent: neither
        // belongs to an interpolation segment, including with loop/ping-pong.
        internal static void ValidateNativeParameterCurve(AnimationCurve curve, string context)
        {
            if (curve == null || curve.length == 0) return;
            bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
            void Invalid(int index, string field) => throw new InvalidOperationException(string.Format(ExporterLocalization.T(
                "FXの内部パラメーター曲線が不正です（{0}、キー {1}、{2}）。"), context, index, field));
            var keys = curve.keys;
            for (var index = 0; index < keys.Length; index++)
            {
                var key = keys[index];
                if (!Finite(key.time) || index > 0 && key.time <= keys[index - 1].time) Invalid(index, "time");
                if (!Finite(key.value)) Invalid(index, "value");
                if (index > 0)
                {
                    if (float.IsNaN(key.inTangent)) Invalid(index, "inTangent");
                    if ((key.weightedMode & WeightedMode.In) != 0 &&
                        (!Finite(key.inWeight) || key.inWeight < 0 || key.inWeight > 1)) Invalid(index, "inWeight");
                }
                if (index < keys.Length - 1)
                {
                    if (float.IsNaN(key.outTangent)) Invalid(index, "outTangent");
                    if ((key.weightedMode & WeightedMode.Out) != 0 &&
                        (!Finite(key.outWeight) || key.outWeight < 0 || key.outWeight > 1)) Invalid(index, "outWeight");
                }
            }
        }

        internal static bool IsConstant(AnimationCurve curve)
        {
            if (curve == null || curve.length == 0) return true;
            var keys = curve.keys;
            return keys.All(k => k.value == keys[0].value &&
                (k.inTangent == 0 || float.IsInfinity(k.inTangent)) && (k.outTangent == 0 || float.IsInfinity(k.outTangent)));
        }

        // Neutral capture writes morph weights only. A constant activation is
        // harmless when Unity cannot resolve its dummy target, or when it
        // exactly preserves the prepared object's current appearance. Never
        // infer an object identity from an ambiguous path or accept a toggle.
        private static bool IsHarmlessNeutralActivation(GameObject avatar, AnimationClip clip, EditorCurveBinding binding)
            => NeutralShapePlan.HarmlessActivation(avatar, clip, binding);

        private static string NeutralBindingDiagnostic(NeutralShapePlan plan, AnimationClip clip, EditorCurveBinding binding)
        {
            var reason = plan?.EvaluationBindingRejection(clip, binding);
            return reason == null ? "" : "（判定理由: " + reason + "）";
        }

        private static void SetParameters(AnimatorControllerPlayable playable, AnimatorController controller, IDictionary<string, float> values)
        {
            foreach (var parameter in controller.parameters)
            {
                if (!values.TryGetValue(parameter.name, out var value)) continue;
                if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidOperationException("表情パラメーターに不正な値があります。");
                switch (parameter.type)
                {
                    case AnimatorControllerParameterType.Bool: playable.SetBool(parameter.name, value != 0); break;
                    case AnimatorControllerParameterType.Int: playable.SetInteger(parameter.name, Mathf.RoundToInt(value)); break;
                    case AnimatorControllerParameterType.Float: playable.SetFloat(parameter.name, value); break;
                    default: throw new InvalidOperationException("Triggerパラメーターの表情は未対応です。");
                }
            }
        }

        private static HashSet<EditorCurveBinding> ActiveBindings(AnimatorControllerPlayable playable, int[] layers, Func<string, bool> excludedPath,
            ISet<int> equivalentStates = null, bool neutral = false, GameObject neutralAvatar = null,
            NeutralShapePlan neutralPlan = null, ISet<EditorCurveBinding> capturedMorphs = null)
        {
            var bindings = new HashSet<EditorCurveBinding>();
            foreach (var layer in layers)
            {
                if (playable.IsInTransition(layer) && equivalentStates?.Contains(layer) != true)
                {
                    const string message = "FXの表情遷移が完了しません（2秒以内に静止する表情が必要です）。";
                    if (neutral) throw new NeutralShapeSamplingException(message);
                    throw new InvalidOperationException(message);
                }
                if (layer > 0 && playable.GetLayerWeight(layer) <= 0.00001f) continue;
                foreach (var info in playable.GetCurrentAnimatorClipInfo(layer).Concat(playable.GetNextAnimatorClipInfo(layer)))
                {
                    if (info.weight <= 0.00001f || info.clip == null) continue;
                    if (AnimationUtility.GetObjectReferenceCurveBindings(info.clip).Any(b => excludedPath?.Invoke(b.path) != true &&
                        neutralPlan?.AllowsEvaluationBinding(info.clip, b) != true))
                        throw new InvalidOperationException("マテリアル・オブジェクトの差し替えを含む表情は未対応です。");
                    foreach (var binding in AnimationUtility.GetCurveBindings(info.clip))
                    {
                        if (binding.type == typeof(Animator)) continue;
                        if (excludedPath?.Invoke(binding.path) == true) continue;
                        if (IsHarmlessNeutralActivation(neutralAvatar, info.clip, binding)) continue;
                        if (neutralPlan?.AllowsEvaluationBinding(info.clip, binding) == true) continue;
                        if (binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                            throw new InvalidOperationException("BlendShape以外の変化を含みます: " +
                                info.clip.name + " / " + binding.path + " / " + binding.propertyName + NeutralBindingDiagnostic(neutralPlan, info.clip, binding));
                        if (capturedMorphs == null || capturedMorphs.Contains(binding)) bindings.Add(binding);
                    }
                }
            }
            return bindings;
        }

        private static List<MorphValue> Capture(GameObject source, GameObject clone, IEnumerable<EditorCurveBinding> bindings,
            Func<string, bool> excludedPath, ISet<EditorCurveBinding> relevant, IList<MorphValue> unevaluated)
        {
            var owned = new HashSet<EditorCurveBinding>(bindings);
            // WD-Off values can outlive the clips that wrote them. Also capture
            // evaluated morphs that differ from the authored scene, even when no
            // currently active selected clip contains their bindings.
            foreach (var renderer in source.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.sharedMesh == null) continue;
                var path = AnimationUtility.CalculateTransformPath(renderer.transform, source.transform);
                if (excludedPath?.Invoke(path) == true) continue;
                var animated = FindRenderer(clone, path);
                if (animated.sharedMesh != renderer.sharedMesh)
                    throw new InvalidOperationException("FXによるメッシュ差し替えは表情変換に未対応です: " + path);
                for (var i = 0; i < renderer.sharedMesh.blendShapeCount; i++)
                {
                    var binding = EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + renderer.sharedMesh.GetBlendShapeName(i));
                    if (relevant.Contains(binding) && animated.GetBlendShapeWeight(i) != renderer.GetBlendShapeWeight(i)) owned.Add(binding);
                }
            }
            var result = new List<MorphValue>();
            foreach (var binding in owned.OrderBy(b => b.path, StringComparer.Ordinal).ThenBy(b => b.propertyName, StringComparer.Ordinal))
            {
                var original = FindRenderer(source, binding.path);
                var animated = FindRenderer(clone, binding.path);
                if (!original.enabled || !original.gameObject.activeInHierarchy)
                    throw new InvalidOperationException("非表示のRendererを動かす項目は取り込めません: " + binding.path);
                var shape = binding.propertyName.Substring("blendShape.".Length);
                var index = animated.sharedMesh.GetBlendShapeIndex(shape);
                if (index < 0)
                {
                    if (!unevaluated.Any(v => v.Path == binding.path && v.Shape == shape))
                        unevaluated.Add(new MorphValue { Path = binding.path, Shape = shape });
                    continue;
                }
                var weight = animated.GetBlendShapeWeight(index);
                if (float.IsNaN(weight) || float.IsInfinity(weight)) throw new InvalidOperationException("表情のBlendShape値が不正です。");
                result.Add(new MorphValue { Path = binding.path, Shape = shape, Weight = weight });
            }
            return result;
        }

        internal static SkinnedMeshRenderer FindRenderer(GameObject root, string path)
        {
            if (root.GetComponentsInChildren<Transform>(true).Count(t => AnimationUtility.CalculateTransformPath(t, root.transform) == path) != 1)
                throw new InvalidOperationException("重複する階層パスの表情は取り込めません: " + path);
            var matches = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => AnimationUtility.CalculateTransformPath(r.transform, root.transform) == path).ToArray();
            if (matches.Length != 1 || matches[0].sharedMesh == null)
                throw new InvalidOperationException("Rendererのパスを一意に解決できません: " + path);
            for (var current = matches[0].transform; current != root.transform; current = current.parent)
                if (current.name.Contains("/")) throw new InvalidOperationException("名前に / を含む階層の表情は取り込めません: " + path);
            return matches[0];
        }

    }
}
