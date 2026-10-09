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
            NeutralInputProof.Read(avatar, source);
            source.FacialProjectionSession = new FacialProjectionScope.Session(avatar);
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
                VrChatFxExpressions.Add(avatar, source, excludedPath, menuPolicy);
                VrChatAuthoredFaces.Add(avatar, source, excludedPath, menuPolicy);
                FaceEmoExpressions.Add(avatar, source, excludedPath, authoringSource, faceEmoBindings);
            }
            finally { source.FacialProjectionSession = null; EditorUtility.ClearProgressBar(); }
            return source;
        }

        // The user-facing export captures one explicit environment. Runtime
        // VRChat inputs are not required to replay the resulting VRM morphs.
        internal static List<MorphValue> SampleFixed(GameObject avatar, RuntimeAnimatorController runtime,
            IDictionary<string, float> defaults, IDictionary<string, float> selected, Func<string, bool> excludedPath = null,
            VrChatExpressionMenu.Source metadata = null, IList<MorphValue> unevaluated = null, FixedExpressionContext fixedContext = null,
            int? expectedLayer = null, string expectedStatePath = null)
            => Evaluate(avatar, runtime, defaults, selected, excludedPath, metadata, unevaluated,
                StationaryBindings(avatar, runtime, excludedPath),
                fixedContext: fixedContext ?? FixedExpressionContext.Create(runtime, defaults, metadata),
                expectedLayer: expectedLayer, expectedStatePath: expectedStatePath);

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
            VrChatExpressionMenu.Source metadata = null, AnimatorState sourceState = null, IDictionary<string, float> nativeInputs = null)
        {
            if (runtime == null || entry.Error != null || entry.Values.Count == 0) return;
            // Inspect the authored graph before any early exit: a layer control
            // can enable a stationary layer whose serialized default weight is
            // zero, as well as disable one that the probe would otherwise keep.
            var authoredGraph = ExpressionDependencies.ValidateProbeBehaviours(runtime, excludedPath);
            var hasLayerControls = authoredGraph.Any(layer => layer.WeightControls.Values.Any(control => control.Playable == "FX"));
            metadata = metadata ?? VrChatExpressionMenu.Read(avatar, new VrChatMenuImportPolicy { SkipAll = true });
            void SelectedInputs(FixedExpressionContext context)
            {
                if (nativeInputs == null) return;
                var groups = ExpressionDependencies.Controller(runtime).parameters.GroupBy(parameter => parameter.name, StringComparer.Ordinal).ToArray();
                if (groups.Any(group => group.Count() != 1))
                    throw new InvalidOperationException("合成表情のネイティブ入力宣言が重複しています。");
                var declared = groups.ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
                foreach (var pair in nativeInputs)
                {
                    if (!declared.TryGetValue(pair.Key, out var parameter) || float.IsNaN(pair.Value) || float.IsInfinity(pair.Value) ||
                        parameter.type != AnimatorControllerParameterType.Float && parameter.type != AnimatorControllerParameterType.Int &&
                            parameter.type != AnimatorControllerParameterType.Bool ||
                        parameter.type == AnimatorControllerParameterType.Bool && pair.Value != 0 && pair.Value != 1 ||
                        parameter.type == AnimatorControllerParameterType.Int && (pair.Value != Math.Truncate(pair.Value) || (double)pair.Value < int.MinValue || (double)pair.Value > int.MaxValue) ||
                        (pair.Key == "GestureLeft" || pair.Key == "GestureRight") && (pair.Value < 0 || pair.Value > 7) ||
                        (pair.Key == "GestureLeftWeight" || pair.Key == "GestureRightWeight") && (pair.Value < 0 || pair.Value > 1))
                        throw new InvalidOperationException("合成表情のネイティブ入力が不正です: " + pair.Key);
                    context.Values[pair.Key] = pair.Value;
                }
            }
            if (metadata.OtherControllers.Count > 0)
            {
                if (!metadata.NeutralInputInventoryComplete) NeutralInputProof.Read(avatar, metadata);
                var inputContext = FixedExpressionContext.Create(runtime, metadata.Defaults, metadata);
                SelectedInputs(inputContext);
                var outputMorphs = new HashSet<EditorCurveBinding>(entry.Values.Select(value =>
                    EditorCurveBinding.FloatCurve(value.Path, typeof(SkinnedMeshRenderer), "blendShape." + value.Shape)));
                ExpressionDependencies.ValidateAdditionalProbeBehaviours(runtime, metadata, inputContext, outputMorphs);
            }
            var stationaryBindings = StationaryBindings(avatar, runtime, excludedPath);
            var permanent = ExpressionDependencies.StationaryLayers(runtime, excludedPath);
            var originalController = ExpressionDependencies.Controller(runtime);
            var originalLayers = originalController.layers;
            var replacements = ExpressionDependencies.Overrides(runtime);
            var controlScope = authoredGraph;
            if (hasLayerControls)
            {
                if (!metadata.NeutralInputInventoryComplete) NeutralInputProof.Read(avatar, metadata);
                var controlContext = FixedExpressionContext.Create(runtime, metadata.Defaults, metadata);
                SelectedInputs(controlContext);
                var controlInputs = ExpressionDependencies.FixedNeutralValues(runtime, metadata, authoredGraph, excludedPath, controlContext);
                controlScope = ExpressionDependencies.Inspect(runtime, excludedPath,
                    new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), new List<string>(), true, controlInputs);
            }
            var nativeSlot = layerIndex.HasValue && layerIndex.Value > 0;
            // Inactive authored slots still expose their direct clip as a
            // candidate. Preserve that legacy route rather than evaluating an
            // inactive native layer and erasing the clip's authored values.
            if (nativeSlot && layerIndex.Value < originalLayers.Length)
                nativeSlot = originalLayers[layerIndex.Value].defaultWeight > 0 || controlScope.Any(layer =>
                    layer.WeightControls.Values.Any(control => control.Playable == "FX" && control.AnimatorLayer &&
                        control.LayerIndex == layerIndex.Value));
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
            if (layerIndex.HasValue && originalLayers[layerIndex.Value].blendingMode == AnimatorLayerBlendingMode.Additive)
                throw new InvalidOperationException("Additiveレイヤーの表情クリップと常時適用FXの合成は、加算の基準ポーズを確定できないため省略しました。");
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
                if (sourceMachines.Any(machine => ExpressionDependencies.HasEffectfulBehaviours(machine.behaviours)))
                    throw new InvalidOperationException("表情の元FX状態の親StateMachineにコールバックがあるため、進入順序を確定できません。");
                sourceBehaviours = originalController.GetStateEffectiveBehaviours(sourceState, layerIndex.Value) ?? Array.Empty<StateMachineBehaviour>();
                // Explicit state provenance can activate a callback outside the
                // normal/default route. Preserve its state entry at the authored
                // weight even when the command controls a different FX slot.
                if (sourceBehaviours.Select(behaviour => SdkLayerWeightControl.Read(behaviour, sourceState.name, originalLayers.Length))
                    .Any(control => control != null && control.Playable == "FX"))
                    nativeSlot = true;
                if (layerIndex.Value == 0) nativeSlot = true;
                else if (!nativeSlot && originalLayers[layerIndex.Value].defaultWeight <= 0)
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
                        SelectedInputs(inactiveContext);
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
            if (stationaryBindings.Count == 0 && parameterLayers.Count == 0 && !nativeSlot && !hasLayerControls) return;
            // A slot without proven state provenance cannot choose callbacks.
            // Known selected states retain their effective state callbacks;
            // unmodelled ancestor callbacks remain a conservative rejection.
            var omittedDriverWrites = layerIndex.HasValue && sourceState == null ?
                ExpressionDependencies.SelectedLayerDriverWrites(runtime, layerIndex.Value, excludedPath) : null;
            if (!metadata.NeutralInputInventoryComplete) NeutralInputProof.Read(avatar, metadata);
            var defaults = metadata.Defaults;
            // Direct gesture/registered clips use the same explicit normal
            // environment as menu expressions. Their support graph still
            // contains contact gates and built-in inputs from the full FX.
            var context = FixedExpressionContext.Create(runtime, defaults, metadata);
            SelectedInputs(context);
            var explicitMorphs = new HashSet<EditorCurveBinding>(entry.Values.Select(value =>
                EditorCurveBinding.FloatCurve(value.Path, typeof(SkinnedMeshRenderer), "blendShape." + value.Shape)));
            var intendedMorphs = new HashSet<EditorCurveBinding>(stationaryBindings); intendedMorphs.UnionWith(explicitMorphs);
            var ownershipMessages = new List<string>();
            var scalarPlan = NeutralShapePlan.Create(avatar, runtime, new[] { intendedMorphs }, excludedPath,
                requiredMorphs: explicitMorphs, warnings: ownershipMessages, source: metadata, fixedContext: context, allowUnchangedAppearance: true);
            var projectionClips = ExpressionDependencies.NormalInputLayers(runtime, metadata, context, excludedPath).SelectMany(layer => layer.Clips).Distinct().ToArray();
            // A disjoint wardrobe layer must not narrow an already valid pure
            // morph clip. Rescue only ownership that actually blocked this
            // endpoint, or a clip already projected while reading appearance.
            var projection = (explicitMorphs.Overlaps(scalarPlan.PreservedMorphs) || entry.UsesFacialProjection) ? FacialProjectionScope.For(avatar, metadata) : null;
            if (projection != null)
            {
                projection.WarnOmitted(intendedMorphs, entry.Messages);
                explicitMorphs.IntersectWith(projection.Morphs); intendedMorphs.IntersectWith(projection.Morphs);
                entry.Values.RemoveAll(value => !projection.Morphs.Contains(EditorCurveBinding.FloatCurve(value.Path, typeof(SkinnedMeshRenderer), "blendShape." + value.Shape)));
                entry.Animation.RemoveAll(value => !projection.Morphs.Contains(EditorCurveBinding.FloatCurve(value.Path, typeof(SkinnedMeshRenderer), "blendShape." + value.Shape)));
                if (entry.Values.Count == 0) throw new InvalidOperationException("有効な顔のBlendShapeアニメーションがありません。");
                if (entry.Animation.Count == 0) { entry.Duration = 0; entry.Loop = false; }
                scalarPlan = NeutralShapePlan.CreateProjection(avatar, intendedMorphs, projectionClips, excludedPath);
            }
            else entry.Messages.AddRange(ownershipMessages);
            // This is a scalar response on a private native probe. Prepared
            // clothing, materials and bones remain authoritative, together
            // with their coupled morphs; they cannot become endpoint channels
            // merely through WD/additive dependency closure.
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
                if (nativeInputs != null) causalWrites.UnionWith(nativeInputs.Keys);
                causalWrites.UnionWith(sourceBehaviours.Where(VrChatParameterDriver.IsDriver)
                    .SelectMany(behaviour => VrChatParameterDriver.Read(behaviour, sourceState.name).Operations)
                    .Select(operation => operation.Destination));
                var support = new HashSet<int>();
                var controlledTargets = new HashSet<int>();
                bool RetainTargets(IEnumerable<SdkLayerWeightControl> controls)
                {
                    var added = false;
                    foreach (var control in controls)
                        if (control != null && control.Playable == "FX" && control.AnimatorLayer)
                            added |= controlledTargets.Add(control.LayerIndex);
                    return added;
                }
                RetainTargets(sourceBehaviours.Select(behaviour =>
                    SdkLayerWeightControl.Read(behaviour, sourceState.name, originalLayers.Length)));
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
                    // State-entry weight commands are dependencies just like
                    // Parameter Driver writes. Keep their actual target graph,
                    // including further commands and inputs in that graph.
                    // A selected write can open a route outside normal inputs;
                    // otherwise retain the normally reachable command scope.
                    foreach (var index in Enumerable.Range(0, layerIndex.Value).Concat(support))
                        changed |= RetainTargets((authored[index].Reads.Overlaps(causalWrites)
                            ? authored[index] : controlScope[index]).WeightControls.Values);
                    for (var index = layerIndex.Value + 1; index < originalLayers.Length; index++)
                        if (!support.Contains(index) && (authored[index].Reads.Overlaps(causalWrites) || controlledTargets.Contains(index)))
                        {
                            support.Add(index); causalWrites.UnionWith(authored[index].Writes); changed = true;
                        }
                } while (changed);
                // A direct clip is the selected authored face. A separate,
                // non-permanent upper face selector must not replace it with
                // that selector's normal/default alternative. Keep lower native
                // pose, permanent/equivalent/additive and single-state BlendTree
                // configuration support, and upper routes
                // causally driven by the selected parameter or weight callbacks. Disjoint
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
                            if (hasLayerControls && !selected)
                            {
                                if (index == 0) original.defaultWeight = 1;
                                if (original.syncedLayerIndex >= 0 && !layerIndex.HasValue) original.syncedLayerIndex++;
                                layers.Add(original); continue;
                            }
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
                    if ((nativeSlot || hasLayerControls) && replacements.Count > 0)
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
                    if (sourceState != null && nativeSlot)
                    {
                        var suspended = HeldAutomaticExpressionLayers.Find(probeRuntime, metadata,
                            NeutralShapeSampler.AutomaticChannels(avatar), layerIndex.Value, explicitMorphs);
                        var knownAutomatic = suspended.Count > 0;
                        var unownedRest = HeldAutomaticExpressionLayers.FindUnownedRandomRest(avatar, probeRuntime, metadata,
                            explicitMorphs, context, out var preparedRestMorphs);
                        scalarPlan.CommittedMorphs.ExceptWith(preparedRestMorphs);
                        suspended.UnionWith(unownedRest);
                        if (suspended.Count > 0)
                        {
                            foreach (var index in suspended) layers[index] = Layer(originalLayers[index], null, false, 0);
                            controller.layers = layers.ToArray();
                            if (knownAutomatic)
                            {
                                var warning = ExporterLocalization.T("選択した表情を保持するため、独立した自動まばたき・リップシンクのFXを停止しました: ") +
                                    string.Join(", ", suspended.Except(unownedRest).OrderBy(index => index).Select(index => originalLayers[index].name));
                                if (!entry.Messages.Contains(warning)) entry.Messages.Add(warning);
                            }
                            if (unownedRest.Count > 0)
                            {
                                var restWarning = ExporterLocalization.T("Randomを使う独立した待機アニメーションは実行せず、選択した表情が所有しないBlendShapeの現在値を保持しました: ") +
                                    string.Join(", ", unownedRest.OrderBy(index => index).Select(index => originalLayers[index].name));
                                if (!entry.Messages.Contains(restWarning)) entry.Messages.Add(restWarning);
                            }
                        }
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
                        excludedPath, metadata, fixedContext: context, preserveCommittedMorphs: true, evaluateLayerWeights: true,
                        weightLayerOffset: !nativeSlot && !layerIndex.HasValue ? 1 : 0);
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
            bool preserveNativeBasePose = false, FixedExpressionContext fixedContext = null, ISet<string> omittedDriverWrites = null,
            int? expectedLayer = null, string expectedStatePath = null)
        {
            using var selectedRest = !preserveNativeBasePose && fixedContext != null && metadata != null && selected.Count > 0
                ? SelectedRandomRestProbe.Create(avatar, runtime, metadata, selected, fixedContext) : null;
            if (selectedRest != null)
            {
                runtime = selectedRest.Runtime;
                initialMorphs = initialMorphs.Except(selectedRest.PreservedMorphs);
            }
            if (fixedContext != null)
            {
                var resolved = new Dictionary<string, float>(defaults, StringComparer.Ordinal);
                foreach (var input in fixedContext.Values) resolved[input.Key] = input.Value;
                defaults = resolved;
            }
            var originalController = ExpressionDependencies.Controller(runtime);
            var dependencies = ExpressionDependencies.Analyze(runtime, selected.Keys, excludedPath, metadata, defaults, selected, initialMorphs, preserveNativeBasePose, fixedContext);
            // Keep the complete original dependency graph and native support.
            // Inactive alternative clips may mention the preserved rest, but
            // cannot make it a selected capture root. The observed contributing
            // clips are checked against the original proof before capture.
            if (selectedRest != null) dependencies.Morphs.ExceptWith(selectedRest.PreservedMorphs);
            NeutralShapePlan projectionPlan = null;
            if (!preserveNativeBasePose)
            {
                var projectionLayers = ExpressionDependencies.NormalInputLayers(runtime, metadata, fixedContext, excludedPath);
                var clips = dependencies.Layers.Concat(dependencies.NativeSupportLayers).Distinct().SelectMany(index => projectionLayers[index].Clips).Distinct().ToArray();
                var projection = FacialProjectionScope.NeedsProjection(avatar, clips, excludedPath) ? FacialProjectionScope.For(avatar, metadata) : null;
                if (projection != null)
                {
                    projection.WarnOmitted(dependencies.Morphs, metadata?.Messages);
                    dependencies.Morphs.IntersectWith(projection.Morphs);
                    if (dependencies.Morphs.Count == 0) throw new InvalidOperationException("有効な顔のBlendShapeアニメーションがありません。");
                    foreach (var index in dependencies.Layers)
                        if (!projectionLayers[index].Morphs.Overlaps(dependencies.Morphs)) dependencies.NativeSupportLayers.Add(index);
                    projectionPlan = NeutralShapePlan.CreateProjection(avatar, dependencies.Morphs, clips, excludedPath);
                }
            }
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
            var unchangedAppearance = UnchangedAppearanceClips(controller, affected.Concat(dependencies.NativeSupportLayers));
            unevaluated = unevaluated ?? new List<MorphValue>();
            ValidateNativeSupportMotions(controller, projectionPlan == null ? dependencies.NativeSupportLayers : dependencies.Layers,
                excludedPath, avatar, projectionPlan,
                unchangedAppearanceClips: unchangedAppearance);

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
                    selectedRest?.Observe(playable, controller, clone);
                    RememberBindings(playable, affected, history, visitedClips, excludedPath, neutralAvatar: avatar,
                        neutralPlan: projectionPlan, capturedMorphs: projectionPlan?.CommittedMorphs,
                        unchangedAppearanceClips: unchangedAppearance);
                    // Support clips retain history safety checks without
                    // making their unrelated morphs export targets.
                    RememberBindings(playable, supportLayers, history, visitedSupportClips, excludedPath, dependencies.Morphs, avatar,
                        neutralPlan: projectionPlan,
                        unchangedAppearanceClips: unchangedAppearance);
                };
                // Initialize before a short-lived WD-Off state exits; its writes
                // remain part of the eventual neutral and selected appearance.
                graph.Evaluate(0f);
                remember();
                Advance(graph, 120, remember);
                evaluation.CheckNeutralFx();
                SetParameters(playable, controller, selected);
                Advance(graph, 120, remember);
                void CheckSelectedState()
                {
                    if (expectedLayer.HasValue && (expectedLayer.Value < 0 || expectedLayer.Value >= controller.layers.Length ||
                        string.IsNullOrEmpty(expectedStatePath) || playable.IsInTransition(expectedLayer.Value) ||
                        !MatchesExpectedStatePath(controller, expectedLayer.Value, expectedStatePath,
                            playable.GetCurrentAnimatorStateInfo(expectedLayer.Value).fullPathHash)))
                        throw new InvalidOperationException("FXの表情候補の状態に到達できませんでした。条件・優先順位・Parameter Driverを確認してください: " + expectedStatePath);
                }
                CheckSelectedState();
                var stableWeights = evaluation.CaptureLayerWeights(playable);
                var shadowedCurves = PreserveTemporalRest(avatar, playable, controller,
                    affected.Concat(supportLayers), dependencies.Morphs, excludedPath, null);
                ValidateFixedPose(avatar, playable, controller, excludedPath, excludedLayers, equivalentStates,
                    dependencies.NativeSupportLayers, dependencies.Morphs, dependencies: dependencies, metadata: metadata,
                    restrictCapturedMorphs: projectionPlan != null, neutralPlan: projectionPlan, fixedMotionTimeInputs: selected,
                    motionTimeRuntime: runtime, shadowedNeutralCurves: shadowedCurves);
                selectedRest?.Validate();
                selectedRest?.ValidateNativeRest(clone);
                var bindings = ActiveBindings(playable, affected, excludedPath, equivalentStates, neutralAvatar: avatar,
                    neutralPlan: projectionPlan, capturedMorphs: projectionPlan?.CommittedMorphs,
                    unchangedAppearanceClips: unchangedAppearance);
                var values = Capture(avatar, clone, history, excludedPath, dependencies.Morphs, unevaluated);
                // A state transition, a changing curve or changing active clip
                // set cannot be represented as one fixed VRM expression.
                for (var checkpoint = 0; checkpoint < 3; checkpoint++)
                {
                    Advance(graph, 7 + checkpoint, evaluation.Check);
                    evaluation.CheckLayerWeights(playable, stableWeights);
                    selectedRest?.Observe(playable, controller, clone);
                    selectedRest?.Validate();
                    selectedRest?.ValidateNativeRest(clone);
                    CheckSelectedState();
                    var nextBindings = ActiveBindings(playable, affected, excludedPath, equivalentStates, neutralAvatar: avatar,
                        neutralPlan: projectionPlan, capturedMorphs: projectionPlan?.CommittedMorphs,
                        unchangedAppearanceClips: unchangedAppearance);
                    if (!bindings.SetEquals(nextBindings)) throw new InvalidOperationException("表情が時間で切り替わるため、固定表情に変換できません。");
                    var next = Capture(avatar, clone, history, excludedPath, dependencies.Morphs, unevaluated);
                    if (values.Count != next.Count || values.Where((v, i) => v.Path != next[i].Path || v.Shape != next[i].Shape || Math.Abs(v.Weight - next[i].Weight) > 0.01f).Any())
                        throw new InvalidOperationException("表情のアニメーションが静止しません。固定表情のみ取り込めます。");
                }
                if (values.Count == 0 && unevaluated.Count == 0) throw new InvalidOperationException("有効な顔のBlendShapeアニメーションがありません。");
                if (fixedContext != null)
                    fixedContext.UsedParameters.UnionWith(dependencies.Parameters.Where(fixedContext.Values.ContainsKey));
                selectedRest?.RecordNotice();
                return values;
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        // A selector owns its native parameter-driven writers, rather than one
        // detached clip. Prove rest on the original complete graph, then keep
        // every selected writer and callback in its authored layer and slot.
        private sealed class SelectedRandomRestProbe : IDisposable
        {
            private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
            internal RuntimeAnimatorController Runtime;
            internal HashSet<EditorCurveBinding> PreservedMorphs;
            private RuntimeAnimatorController originalRuntime;
            private VrChatExpressionMenu.Source source;
            private GameObject avatar;
            private FixedExpressionContext normal;
            private HashSet<string> selectedInputs;
            private HashSet<int> omitted, writerLayers;
            private readonly HashSet<AnimationClip> observed = new HashSet<AnimationClip>();
            private readonly HashSet<EditorCurveBinding> explicitWriters = new HashSet<EditorCurveBinding>();
            private int validatedWriterCount = -1;
            private string notice;
            private FacialProjectionScope geometry;
            private bool geometryComputed;
            private readonly Dictionary<(int Layer, int Hash), AnimatorState> observedStates = new Dictionary<(int, int), AnimatorState>();

            internal void Observe(AnimatorControllerPlayable playable, AnimatorController controller, GameObject evaluated)
            {
                foreach (var layer in writerLayers)
                {
                    var weight = layer == 0 ? 1 : playable.GetLayerWeight(layer);
                    if (!NeutralShapeSnapshot.Finite(weight) || weight < 0)
                        throw new InvalidOperationException("選択したFXのレイヤー重みが不正なため、Randomの待機表情を保持できません。");
                    if (weight > 0)
                    {
                        CheckState(playable.GetCurrentAnimatorStateInfo(layer).fullPathHash);
                        if (playable.IsInTransition(layer)) CheckState(playable.GetNextAnimatorStateInfo(layer).fullPathHash);
                    }
                    void CheckState(int hash)
                    {
                        if (hash == 0) throw new InvalidOperationException("選択したFXの状態が確定しないため、Randomの待機表情を保持できません。");
                        if (!observedStates.TryGetValue((layer, hash), out var state))
                        {
                            var definition = controller.layers[layer];
                            var definitions = controller.layers; var sourceIndex = layer; var seen = new HashSet<int>();
                            while (definitions[sourceIndex].syncedLayerIndex >= 0)
                            {
                                if (!seen.Add(sourceIndex)) throw new InvalidOperationException("同期FXの参照が循環しています。");
                                sourceIndex = definitions[sourceIndex].syncedLayerIndex;
                                if (sourceIndex < 0 || sourceIndex >= definitions.Length) throw new InvalidOperationException("同期FXの参照が不正です。");
                            }
                            var machine = definitions[sourceIndex].stateMachine;
                            var matches = new HashSet<AnimatorState>();
                            void Visit(AnimatorStateMachine machine, string path)
                            {
                                foreach (var child in machine.states)
                                    if (Animator.StringToHash(path + "." + child.state.name) == hash) matches.Add(child.state);
                                foreach (var child in machine.stateMachines) Visit(child.stateMachine, path + "." + child.stateMachine.name);
                            }
                            foreach (var root in new[] { definition.name, definitions[sourceIndex].name, machine.name }.Distinct(StringComparer.Ordinal))
                                Visit(machine, root);
                            if (matches.Count != 1) throw new InvalidOperationException("選択したFXの状態名を一意に特定できません。");
                            state = matches.Single(); observedStates.Add((layer, hash), state);
                        }
                        if (state.writeDefaultValues)
                            throw new InvalidOperationException("選択したFXのWrite Defaultsによる暗黙の出力を確定できません。");
                        var motion = controller.GetStateEffectiveMotion(state, layer) ?? state.motion;
                        if (motion == null || motion is AnimationClip clip && AnimationUtility.GetCurveBindings(clip).Length == 0 &&
                            AnimationUtility.GetObjectReferenceCurveBindings(clip).Length == 0)
                            // An actual empty WD-Off stream may retain earlier
                            // writes. Every contributing clip stays in history,
                            // and its omitted channels must equal prepared rest.
                            ValidateNativeRest(evaluated);
                    }
                    foreach (var info in playable.GetCurrentAnimatorClipInfo(layer).Concat(playable.GetNextAnimatorClipInfo(layer)))
                    {
                        if (info.clip == null) continue;
                        if (!NeutralShapeSnapshot.Finite(info.weight) || info.weight < 0)
                            throw new InvalidOperationException("選択したFXのClip重みが不正なため、Randomの待機表情を保持できません。");
                        if (info.weight == 0 || !observed.Add(info.clip)) continue;
                        foreach (var binding in AnimationUtility.GetCurveBindings(info.clip))
                        {
                            if (binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)) continue;
                            // A fully explicit prepared-zero facial companion
                            // cannot become a selected automatic contribution.
                            // Use the entire effective curve and geometry proof.
                            if (PreservedMorphs.Contains(binding))
                            {
                                if (!geometryComputed) { geometry = FacialProjectionScope.For(avatar, source); geometryComputed = true; }
                                var curve = AnimationUtility.GetEditorCurve(info.clip, binding);
                                VrChatGestureExpressions.ReadCurve(curve);
                                var skin = FindRenderer(avatar, binding.path);
                                var shape = skin.sharedMesh.GetBlendShapeIndex(binding.propertyName.Substring("blendShape.".Length));
                                if (geometry?.Morphs.Contains(binding) == true && shape >= 0 && skin.GetBlendShapeWeight(shape) == 0 && curve.length > 0 &&
                                    IsConstant(curve) && curve.keys[0].value == 0) continue;
                            }
                            explicitWriters.Add(binding);
                        }
                    }
                }
            }

            internal void Validate()
            {
                // The original graph, normal inputs and selection are fixed
                // throughout this probe. This set only grows when native clip
                // history reveals a new writer; re-prove that expanded domain.
                // Native rest, state and weight checks still run every time.
                if (validatedWriterCount == explicitWriters.Count) return;
                var proved = HeldAutomaticExpressionLayers.FindUnownedRandomRest(avatar, originalRuntime, source,
                    explicitWriters, normal, out _, selectedInputs);
                if (!omitted.IsSubsetOf(proved))
                    throw new InvalidOperationException("選択したFXの表情がRandomを使う待機レイヤーの出力も所有するため、現在のBlendShapeを保持できません。");
                validatedWriterCount = explicitWriters.Count;
            }

            internal void ValidateNativeRest(GameObject evaluated)
            {
                foreach (var binding in PreservedMorphs)
                {
                    var name = binding.propertyName.Substring("blendShape.".Length);
                    var prepared = FindRenderer(avatar, binding.path); var actual = FindRenderer(evaluated, binding.path);
                    var preparedIndex = prepared.sharedMesh.GetBlendShapeIndex(name); var actualIndex = actual.sharedMesh.GetBlendShapeIndex(name);
                    if (preparedIndex < 0 || actualIndex < 0)
                        throw new InvalidOperationException("待機表情のBlendShapeが見つかりません: " + binding.path + " / " + binding.propertyName);
                    var preparedWeight = prepared.GetBlendShapeWeight(preparedIndex); var actualWeight = actual.GetBlendShapeWeight(actualIndex);
                    if (!NeutralShapeSnapshot.Finite(preparedWeight) || !NeutralShapeSnapshot.Finite(actualWeight) ||
                        Math.Abs(preparedWeight - actualWeight) > .01f)
                        throw new InvalidOperationException("選択したFXの暗黙の出力が待機表情の現在値と一致しません: " + binding.path + " / " + binding.propertyName);
                }
            }

            internal void RecordNotice()
            {
                if (!source.Messages.Contains(notice)) source.Messages.Add(notice);
            }

            internal static SelectedRandomRestProbe Create(GameObject avatar, RuntimeAnimatorController runtime,
                VrChatExpressionMenu.Source source, IDictionary<string, float> selected, FixedExpressionContext normal)
            {
                var selectedInputs = new HashSet<string>(selected.Keys, StringComparer.Ordinal);
                var omitted = HeldAutomaticExpressionLayers.FindUnownedRandomRest(avatar, runtime, source,
                    new HashSet<EditorCurveBinding>(), normal, out _, selectedInputs);
                if (omitted.Count == 0) return null;
                var unknown = new List<string>();
                var raw = ExpressionDependencies.Inspect(runtime, null,
                    new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), unknown, true);
                var others = source.OtherControllers.Where(value => value != null)
                    .SelectMany(value => ExpressionDependencies.Inspect(value, null,
                        new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), unknown,
                        typedConditions: true, fxLayerCount: raw.Length)).ToArray();
                if (unknown.Count != 0) return null;
                var definitions = ExpressionDependencies.Controller(runtime).layers;
                var allMorphs = new HashSet<EditorCurveBinding>(raw.Concat(others).SelectMany(layer => layer.Morphs));
                HashSet<EditorCurveBinding> preserved;
                var writerLayers = new HashSet<int>();
                while (true)
                {
                    var changed = new HashSet<string>(selectedInputs, StringComparer.Ordinal);
                    var writers = new HashSet<EditorCurveBinding>();
                    var controlled = new HashSet<int>();
                    writerLayers.Clear();
                    bool added;
                    do
                    {
                        added = false;
                        for (var index = 0; index < raw.Length; index++)
                        {
                            var layer = raw[index];
                            if (omitted.Contains(index) || !controlled.Contains(index) && !layer.Reads.Overlaps(changed)) continue;
                            foreach (var name in layer.Writes) added |= changed.Add(name);
                            writerLayers.Add(index);
                            if (layer.WriteDefaults || definitions[index].syncedLayerIndex >= 0 && layer.EmptyMotion && layer.Morphs.Count > 0 ||
                                definitions[index].blendingMode == AnimatorLayerBlendingMode.Additive && layer.HasBindings)
                                writers.UnionWith(allMorphs);
                            foreach (var command in layer.WeightControls.Values.Where(control => control.Playable == "FX"))
                                if (command.AnimatorLayer) added |= controlled.Add(command.LayerIndex);
                                else writers.UnionWith(allMorphs);
                        }
                        foreach (var layer in others.Where(layer => layer.Reads.Overlaps(changed)))
                        {
                            foreach (var name in layer.Writes) added |= changed.Add(name);
                            writers.UnionWith(layer.Morphs);
                            if (layer.WriteDefaults || layer.EmptyMotion && layer.Morphs.Count > 0 ||
                                layer.WeightControls.Values.Any(control => control.Playable == "FX"))
                                writers.UnionWith(allMorphs);
                        }
                    } while (added);
                    var accepted = HeldAutomaticExpressionLayers.FindUnownedRandomRest(avatar, runtime, source,
                        writers, normal, out preserved, selectedInputs);
                    // Additional playable morph streams have no replacement
                    // proof here, even when their raw branch is currently idle.
                    accepted.RemoveWhere(index => others.Any(layer => layer.Morphs.Overlaps(raw[index].Morphs)));
                    accepted.IntersectWith(omitted);
                    if (accepted.SetEquals(omitted)) break;
                    omitted = accepted;
                    if (omitted.Count == 0) return null;
                }
                preserved = new HashSet<EditorCurveBinding>(omitted.SelectMany(index => raw[index].Morphs));
                var probe = new SelectedRandomRestProbe { PreservedMorphs = preserved, originalRuntime = runtime,
                    source = source, avatar = avatar, normal = normal, selectedInputs = selectedInputs,
                    omitted = omitted, writerLayers = writerLayers };
                try
                {
                    var original = ExpressionDependencies.Controller(runtime);
                    var controller = new AnimatorController { name = original.name, hideFlags = HideFlags.HideAndDontSave };
                    probe.owned.Add(controller);
                    controller.parameters = original.parameters.Select(parameter => new AnimatorControllerParameter
                    {
                        name = parameter.name, type = parameter.type, defaultBool = parameter.defaultBool,
                        defaultInt = parameter.defaultInt, defaultFloat = parameter.defaultFloat
                    }).ToArray();
                    var layers = original.layers;
                    foreach (var index in omitted)
                    {
                        var machine = new AnimatorStateMachine { name = layers[index].name, hideFlags = HideFlags.HideAndDontSave };
                        probe.owned.Add(machine);
                        var idle = machine.AddState("Prepared independent rest"); probe.owned.Add(idle);
                        idle.writeDefaultValues = false; machine.defaultState = idle;
                        layers[index] = new AnimatorControllerLayer { name = layers[index].name, stateMachine = machine,
                            defaultWeight = 0, syncedLayerIndex = -1, blendingMode = AnimatorLayerBlendingMode.Override };
                    }
                    controller.layers = layers; probe.Runtime = controller;
                    var replacements = ExpressionDependencies.Overrides(runtime);
                    if (replacements.Count > 0)
                    {
                        var overrides = new AnimatorOverrideController(controller) { hideFlags = HideFlags.HideAndDontSave };
                        probe.owned.Add(overrides);
                        var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>(); overrides.GetOverrides(pairs);
                        for (var index = 0; index < pairs.Count; index++)
                            if (replacements.TryGetValue(pairs[index].Key, out var replacement))
                                pairs[index] = new KeyValuePair<AnimationClip, AnimationClip>(pairs[index].Key, replacement);
                        overrides.ApplyOverrides(pairs); probe.Runtime = overrides;
                    }
                    probe.notice = ExporterLocalization.T("Randomを使う独立した待機アニメーションは実行せず、選択した表情が所有しないBlendShapeの現在値を保持しました: ") +
                        string.Join(", ", omitted.OrderBy(index => index).Select(index => original.layers[index].name));
                    return probe;
                }
                catch { probe.Dispose(); throw; }
            }

            public void Dispose()
            {
                for (var index = owned.Count - 1; index >= 0; index--)
                    if (owned[index] != null) UnityEngine.Object.DestroyImmediate(owned[index]);
            }
        }

        internal static bool MatchesExpectedStatePath(AnimatorController controller, int layer, string expectedStatePath, int fullPathHash)
        {
            var layers = controller == null ? null : controller.layers;
            if (layers == null || layer < 0 || layer >= layers.Length || string.IsNullOrEmpty(expectedStatePath)) return false;
            // Inferred paths start at the selected layer name. A renamed layer
            // can retain its root state-machine name in Unity's compiled hash.
            // Synced layers use their source machine, not their own empty graph.
            var source = layer;
            var visited = new HashSet<int>();
            while (layers[source].syncedLayerIndex >= 0)
            {
                if (!visited.Add(source)) return false;
                source = layers[source].syncedLayerIndex;
                if (source < 0 || source >= layers.Length) return false;
            }
            var machine = layers[source].stateMachine;
            if (machine == null) return false;
            if (fullPathHash == Animator.StringToHash(expectedStatePath)) return true;
            var prefix = layers[layer].name + ".";
            return expectedStatePath.StartsWith(prefix, StringComparison.Ordinal) &&
                fullPathHash == Animator.StringToHash(machine.name + "." + expectedStatePath.Substring(prefix.Length));
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
            var unchangedAppearance = neutralPlan?.AllowUnchangedAppearance == true
                ? UnchangedAppearanceClips(controller, affected.Concat(dependencies.NativeSupportLayers)) : null;
            // A dominating scalar override can ignore lower timing, but cannot
            // authorize future visibility, mesh or component changes. Check
            // every retained reachable clip before relaxing those timed exits.
            var validationLayers = dependencies.IndependentTopOverrideLayer >= 0
                ? affected.Concat(dependencies.NativeSupportLayers) : dependencies.NativeSupportLayers;
            ValidateNativeSupportMotions(controller, validationLayers, excludedPath, avatar, neutralPlan,
                ExpressionDependencies.NeutralClips(runtime, dependencies.NeutralFixedValues, excludedPath), unchangedAppearance);
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
                    // The static proof keeps all native support. Confirm its
                    // dominating layer is actually full weight on every frame;
                    // callbacks must never turn that proof into a partial pose.
                    var top = dependencies.IndependentTopOverrideLayer;
                    if (top > 0 && playable.GetLayerWeight(top) != 1)
                        throw UnsupportedAppearance(neutralPlan, ExporterLocalization.T("最上位のFXレイヤーが初期表情を完全には固定していません。"));
                    RememberBindings(playable, affected, history, visited, excludedPath,
                        neutralPlan == null ? null : dependencies.Morphs, avatar, neutralPlan, unchangedAppearance);
                    RememberBindings(playable, supportLayers, history, visitedSupportClips, excludedPath, dependencies.Morphs, avatar, neutralPlan, unchangedAppearance);
                };
                // Initialize at time zero before a short-lived default state can exit.
                // Its WD-Off writes remain part of the eventual neutral appearance.
                graph.Evaluate(0f);
                remember();
                Advance(graph, 120, remember);
                evaluation.CheckNeutralFx();
                var equivalentStates = EquivalentNeutralLayers(playable, controller, affected.Concat(supportLayers), dependencies, metadata);
                if (dependencies.IndependentTopOverrideLayer >= 0)
                    equivalentStates.UnionWith(Enumerable.Range(0, dependencies.IndependentTopOverrideLayer));
                var capturedMorphs = new HashSet<EditorCurveBinding>(dependencies.Morphs);
                ISet<(int Layer, AnimationClip Clip, EditorCurveBinding Binding)> shadowedCurves = null;
                if (preserveTemporalRest || neutralPlan != null)
                {
                    shadowedCurves = PreserveTemporalRest(avatar, playable, controller, affected.Concat(supportLayers), capturedMorphs, excludedPath,
                        preserveTemporalRest ? neutralPlan : null);
                    if (preserveTemporalRest) capturedMorphs.ExceptWith(neutralPlan.TemporalMorphs);
                }
                ValidateFixedPose(avatar, playable, controller, excludedPath, excludedLayers, equivalentStates,
                    dependencies.NativeSupportLayers, capturedMorphs, neutral: true,
                    neutralFixed: dependencies.NeutralFixedValues, dependencies: dependencies, metadata: metadata,
                    restrictCapturedMorphs: neutralPlan != null, neutralPlan: neutralPlan, shadowedNeutralCurves: shadowedCurves,
                    requiredParameterCurves: requiredParameterCurves, motionTimeRuntime: runtime);
                var stableWeights = evaluation.CaptureLayerWeights(playable);
                var bindings = ActiveBindings(playable, affected, excludedPath, equivalentStates, neutral: true, neutralAvatar: avatar,
                    neutralPlan: neutralPlan, capturedMorphs: neutralPlan == null ? null : capturedMorphs, unchangedAppearanceClips: unchangedAppearance);
                var unresolved = new List<MorphValue>();
                var values = Capture(avatar, clone, history.Where(capturedMorphs.Contains), excludedPath, capturedMorphs, unresolved);
                for (var checkpoint = 0; checkpoint < 3; checkpoint++)
                {
                    Advance(graph, 7 + checkpoint, evaluation.Check);
                    evaluation.CheckLayerWeights(playable, stableWeights);
                    var nextBindings = ActiveBindings(playable, affected, excludedPath, equivalentStates, neutral: true, neutralAvatar: avatar,
                        neutralPlan: neutralPlan, capturedMorphs: neutralPlan == null ? null : capturedMorphs, unchangedAppearanceClips: unchangedAppearance);
                    if (!bindings.SetEquals(nextBindings))
                        throw new NeutralShapeSamplingException("初期表情が時間で切り替わるため、基本の顔として保存できません。");
                    var next = Capture(avatar, clone, history.Where(capturedMorphs.Contains), excludedPath, capturedMorphs, unresolved);
                    if (values.Count != next.Count || values.Where((value, index) => value.Path != next[index].Path ||
                        value.Shape != next[index].Shape || Math.Abs(value.Weight - next[index].Weight) > .01f).Any())
                        throw new NeutralShapeSamplingException("初期表情のアニメーションが静止しません。");
                }
                return values;
            }
            catch (InvalidOperationException error) when (preserveTemporalRest && VrChatParameterDriver.IsRandomCapability(error))
            {
                // An automatic rest has no fixed random phase. Keep the entire
                // dependent prepared component; manual probes still reject it.
                throw new NeutralShapeSamplingException(error.Message, error, dependencies.NeutralDependencyMorphs, dependencies.NeutralCoupledMorphs);
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        // Inspect full effective curves on the native active graph, including
        // delayed/weighted variation, without removing WD/additive/control
        // support. A selected scalar probe can ignore an exactly dominated
        // lower curve, but never preserve an undominated temporal channel.
        // Only neutral rest reconstruction supplies a preservation plan.
        private static HashSet<(int Layer, AnimationClip Clip, EditorCurveBinding Binding)> PreserveTemporalRest(GameObject avatar, AnimatorControllerPlayable playable,
            AnimatorController controller,
            IEnumerable<int> layers, ISet<EditorCurveBinding> capturedMorphs, Func<string, bool> excludedPath, NeutralShapePlan plan)
        {
            var included = layers.Distinct().OrderBy(index => index).ToArray();
            var shadowed = new HashSet<(int Layer, AnimationClip Clip, EditorCurveBinding Binding)>();
            // This inspection never advances the playable. A terminal proof
            // validates the complete effective clip, so share it across exact
            // lower bindings only for this one native observation (including
            // a failed proof). Later observations get a fresh cache.
            var terminalClips = new Dictionary<int, AnimationClip>();
            AnimationClip Terminal(int layer)
            {
                if (!terminalClips.TryGetValue(layer, out var clip))
                { clip = NativeTerminalClip(playable, controller, layer); terminalClips.Add(layer, clip); }
                return clip;
            }
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
                    if (curve == null || curve.length == 0 || !IsConstant(curve) && Terminal(upper) != current[0].clip) continue;
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
                        plan?.PreserveTemporalRest(info.clip, binding);
                    }
                }
            }
            return shadowed;
        }

        // A nonlooping native clip can reach a constant authored tail before
        // its clamped end. Earlier variation does not make that reached pose
        // dynamic. This proof covers only one explicit morph-only stream;
        // state/SDK/future-transition and pose stability checks remain separate.
        private static AnimationClip NativeTerminalClip(AnimatorControllerPlayable playable,
            AnimatorController controller, int layer)
        {
            bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
            var definition = controller.layers[layer];
            if (definition.syncedLayerIndex >= 0 || playable.IsInTransition(layer)) return null;
            var info = playable.GetCurrentAnimatorStateInfo(layer);
            if (info.loop || !Finite(info.normalizedTime) || info.normalizedTime < 0 ||
                !Finite(info.speed) || !Finite(info.speedMultiplier) || !Finite(info.speed * info.speedMultiplier) ||
                info.speed * info.speedMultiplier <= 0) return null;
            var current = playable.GetCurrentAnimatorClipInfo(layer).Where(value => value.clip != null && value.weight > .00001f).ToArray();
            if (current.Length != 1 || current[0].weight != 1 ||
                playable.GetNextAnimatorClipInfo(layer).Any(value => value.clip != null && value.weight > .00001f)) return null;
            var clip = current[0].clip;
            if (clip.isLooping || !Finite(clip.length) || clip.length <= 0 ||
                AnimationUtility.GetAnimationEvents(clip).Length != 0 || AnimationUtility.GetObjectReferenceCurveBindings(clip).Length != 0) return null;
            var matches = new HashSet<AnimatorState>();
            void Visit(AnimatorStateMachine machine, string path)
            {
                foreach (var child in machine.states)
                    if (Animator.StringToHash(path + "." + child.state.name) == info.fullPathHash) matches.Add(child.state);
                foreach (var child in machine.stateMachines) Visit(child.stateMachine, path + "." + child.stateMachine.name);
            }
            foreach (var root in new[] { definition.name, definition.stateMachine.name }.Distinct(StringComparer.Ordinal))
                Visit(definition.stateMachine, root);
            if (matches.Count != 1) return null;
            var state = matches.Single();
            if ((controller.GetStateEffectiveMotion(state, layer) ?? state.motion) != clip || state.timeParameterActive || state.speedParameterActive ||
                state.cycleOffsetParameterActive || state.mirrorParameterActive || state.cycleOffset != 0 || state.mirror || state.iKOnFeet ||
                !Finite(state.speed) || state.speed <= 0) return null;
            var bindings = AnimationUtility.GetCurveBindings(clip);
            if (bindings.Length == 0 || bindings.Any(binding => binding.type != typeof(SkinnedMeshRenderer) ||
                !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))) return null;
            var phase = Math.Min(1.0, info.normalizedTime) * clip.length;
            foreach (var binding in bindings)
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                ValidateNativeParameterCurve(curve, clip.name + " / " + binding.propertyName);
                if (curve == null || curve.length == 0) return null;
                if (info.normalizedTime >= 1) continue; // Native clip time is already clamped.
                // Before the clip end, prove every remaining segment rather
                // than comparing sampled endpoints. Equal values alone cannot
                // exclude Hermite/weighted overshoot or a later changing key.
                if (curve.postWrapMode == WrapMode.Loop || curve.postWrapMode == WrapMode.PingPong) return null;
                var keys = curve.keys;
                for (var index = 1; index < keys.Length; index++)
                {
                    var left = keys[index - 1]; var right = keys[index];
                    if (right.time <= phase) continue;
                    if (left.value != right.value ||
                        left.outTangent != 0 && !float.IsInfinity(left.outTangent) ||
                        right.inTangent != 0 && !float.IsInfinity(right.inTangent)) return null;
                }
            }
            return clip;
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
                            !ExpressionDependencies.IsInertAuthoringMarker(b) && !VrChatParameterDriver.IsTracking(b) && !VrChatParameterDriver.IsNonFxPlayableControl(b) && !VrChatParameterDriver.IsTemporaryPoseSpace(b) && !VrChatParameterDriver.IsLocomotionControl(b) && (!VrChatParameterDriver.IsDriver(b) ||
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

        internal static HashSet<AnimationClip> UnchangedAppearanceClips(RuntimeAnimatorController runtime, IEnumerable<int> retainedLayers)
        {
            var controller = ExpressionDependencies.Controller(runtime);
            var indices = retainedLayers.Distinct().ToArray();
            // Override blends of the same prepared value remain unchanged,
            // including Write Defaults and fractional weights. An additive
            // layer can contribute implicit defaults to that same property.
            if (indices.Any(index => index != 0 && controller.layers[index].blendingMode != AnimatorLayerBlendingMode.Override))
                return new HashSet<AnimationClip>();
            var allowed = new HashSet<AnimationClip>();
            var blocked = new HashSet<AnimationClip>();
            var replacements = ExpressionDependencies.Overrides(runtime);
            var stack = new HashSet<Motion>();
            void Motion(Motion motion, bool normalized)
            {
                if (motion == null || !stack.Add(motion)) return;
                try
                {
                    if (motion is AnimationClip clip)
                    {
                        if (replacements.TryGetValue(clip, out var replacement)) clip = replacement;
                        if (normalized && !AnimationUtility.GetAnimationClipSettings(clip).hasAdditiveReferencePose) allowed.Add(clip);
                        else blocked.Add(clip);
                    }
                    else if (motion is BlendTree tree)
                    {
                        // A Direct tree can amplify two identical raw values.
                        // Only a convex Simple1D blend is proven here.
                        var children = tree.children;
                        var convex = tree.blendType == BlendTreeType.Simple1D && children.Length > 0 &&
                            children.Select((child, index) => NeutralShapeSnapshot.Finite(child.threshold) &&
                                (index == 0 || child.threshold > children[index - 1].threshold)).All(value => value);
                        foreach (var child in children) Motion(child.motion, normalized && convex);
                    }
                }
                finally { stack.Remove(motion); }
            }
            var machines = new HashSet<AnimatorStateMachine>();
            void Machine(AnimatorStateMachine machine)
            {
                if (machine == null || !machines.Add(machine)) return;
                foreach (var child in machine.states) Motion(child.state.motion, true);
                foreach (var child in machine.stateMachines) Machine(child.stateMachine);
            }
            foreach (var index in indices) Machine(controller.layers[index].stateMachine);
            allowed.ExceptWith(blocked);
            return allowed;
        }

        private static bool HarmlessAppearance(GameObject avatar, AnimationClip clip, EditorCurveBinding binding,
            ISet<AnimationClip> unchangedAppearanceClips, Func<string, bool> excludedPath)
        {
            if (SelectedExpressionAppearance.IsUnboundTransform(avatar, clip, binding))
            {
                SelectedExpressionAppearance.ValidateClipData(clip, excludedPath);
                return true;
            }
            if (unchangedAppearanceClips == null) return IsHarmlessNeutralActivation(avatar, clip, binding);
            if (!unchangedAppearanceClips.Contains(clip) || !SelectedExpressionAppearance.IsUnchanged(avatar, clip, binding)) return false;
            SelectedExpressionAppearance.ValidateClipData(clip, excludedPath);
            return true;
        }

        // Native evaluation can enter and leave zero-duration states before
        // clip-info history observes them. Validate all support motions before
        // evaluation; unrelated morph curves may vary, but callbacks and object
        // or non-morph changes cannot safely supply a native base pose.
        private static void ValidateNativeSupportMotions(AnimatorController controller, IEnumerable<int> supportLayers,
            Func<string, bool> excludedPath, GameObject neutralAvatar = null, NeutralShapePlan neutralPlan = null,
            ISet<AnimationClip> reachableClips = null, ISet<AnimationClip> unchangedAppearanceClips = null)
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
                var objectChange = ObjectChangeDiagnostic(clip, excludedPath, neutralPlan);
                if (objectChange != null)
                    throw UnsupportedAppearance(neutralPlan, "表情への遷移にマテリアル・オブジェクトの差し替えが含まれます。" +
                        (neutralPlan?.RetainUnresolvedRest == true ? " " + objectChange : ""));
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (binding.type == typeof(Animator) || excludedPath?.Invoke(binding.path) == true) continue;
                    if (HarmlessAppearance(neutralAvatar, clip, binding, unchangedAppearanceClips, excludedPath)) continue;
                    if (neutralPlan?.AllowsEvaluationBinding(clip, binding) == true) continue;
                    if (binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                        throw UnsupportedAppearance(neutralPlan, "表情への遷移にBlendShape以外の変化が含まれます: " +
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
            ISet<EditorCurveBinding> capturedMorphs = null, GameObject neutralAvatar = null, NeutralShapePlan neutralPlan = null,
            ISet<AnimationClip> unchangedAppearanceClips = null)
        {
            foreach (var layer in layers)
                foreach (var info in playable.GetCurrentAnimatorClipInfo(layer).Concat(playable.GetNextAnimatorClipInfo(layer)))
                {
                    if (info.clip == null || info.weight <= 0.00001f || !visited.Add(info.clip)) continue;
                    if (capturedMorphs != null && AnimationUtility.GetAnimationEvents(info.clip).Length != 0)
                        throw new InvalidOperationException("常時適用FXと表情の影響範囲を確定できません。");
                    var objectChange = ObjectChangeDiagnostic(info.clip, excludedPath, neutralPlan);
                    if (objectChange != null)
                        throw UnsupportedAppearance(neutralPlan, "表情への遷移にマテリアル・オブジェクトの差し替えが含まれます。" +
                            (neutralPlan?.RetainUnresolvedRest == true ? " " + objectChange : ""));
                    foreach (var binding in AnimationUtility.GetCurveBindings(info.clip))
                    {
                        if (binding.type == typeof(Animator)) continue; // Parameter curves are checked for stability separately.
                        if (excludedPath?.Invoke(binding.path) == true) continue;
                        if (HarmlessAppearance(neutralAvatar, info.clip, binding, unchangedAppearanceClips, excludedPath)) continue;
                        if (neutralPlan?.AllowsEvaluationBinding(info.clip, binding) == true) continue;
                        if (binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                            throw UnsupportedAppearance(neutralPlan, "表情への遷移にBlendShape以外の変化が含まれます: " +
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
            if (layer.syncedLayerIndex >= 0 || layer.iKPass || machine == null || ExpressionDependencies.HasEffectfulBehaviours(machine.behaviours) ||
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
            if (states.Any(state => EffectiveClip(state) != clip || state.writeDefaultValues != writeDefaults || ExpressionDependencies.HasEffectfulBehaviours(state.behaviours) ||
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
                    ExpressionDependencies.HasEffectfulBehaviours(machine.behaviours) || machine.anyStateTransitions.Length != 0 || machine.entryTransitions.Length != 0 ||
                    machine.states.Length != 1 || machine.defaultState != machine.states[0].state) return false;
                var state = machine.defaultState;
                if (state == null || ExpressionDependencies.HasEffectfulBehaviours(state.behaviours) || state.transitions.Length != 0 || state.iKOnFeet ||
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
            ISet<string> requiredParameterCurves = null, IDictionary<string, float> fixedMotionTimeInputs = null,
            RuntimeAnimatorController motionTimeRuntime = null)
        {
            InvalidOperationException Unstable(string message) => neutral ?
                new NeutralShapeSamplingException(message) : new InvalidOperationException(message);
            var timedValues = neutral ? NeutralCurveConditions.FixedTimedValues(playable, controller, neutralFixed, dependencies, metadata) : neutralFixed;
            var layers = controller.layers;
            var fixedClocks = FixedMotionTimeInputs(motionTimeRuntime ?? controller, metadata, fixedMotionTimeInputs ?? neutralFixed, excludedPath);
            for (var layer = 0; layer < layers.Length; layer++)
            {
                if (excludedLayers.Contains(layer)) continue;
                // Only complete constant-motion control proofs may use their
                // observed invariant signals in the future-edge check. Native
                // settled-control validation runs before this selected guard;
                // unrelated timed expression layers keep their existing proof.
                var layerTimedValues = !neutral && dependencies?.RequireSettledWeightControls == true &&
                    dependencies.WeightControlLayers.Contains(layer) ?
                    NeutralCurveConditions.FixedTimedValues(playable, controller, timedValues, dependencies, null) : timedValues;
                if (layer > 0 && playable.GetLayerWeight(layer) <= 0.00001f && dependencies?.WeightControlLayers.Contains(layer) != true) continue;
                if (playable.IsInTransition(layer) && equivalentStates?.Contains(layer) != true)
                    throw Unstable("FXの状態遷移が静止していません。");
                var hash = playable.GetCurrentAnimatorStateInfo(layer).fullPathHash;
                if (hash == 0) continue;
                var found = false;
                var fixedMotionTime = false;
                var terminalClip = NativeTerminalClip(playable, controller, layer);
                void Visit(AnimatorStateMachine machine, string path, bool timedAncestor)
                {
                    var timed = timedAncestor || machine.anyStateTransitions.Any(t => !t.mute && t.hasExitTime && !ExpressionDependencies.IsFalse(t, layerTimedValues));
                    foreach (var child in machine.states)
                    {
                        if (Animator.StringToHash(path + "." + child.state.name) != hash) continue;
                        if (found) throw new InvalidOperationException("FXの状態名を一意に特定できません。");
                        found = true;
                        var state = child.state;
                        fixedMotionTime = state.timeParameterActive && state.motion is AnimationClip &&
                            !state.speedParameterActive && state.speed == 1 && !state.cycleOffsetParameterActive && state.cycleOffset == 0 &&
                            !state.mirrorParameterActive && !state.mirror && !state.iKOnFeet &&
                            !string.IsNullOrEmpty(state.timeParameter) && fixedClocks.TryGetValue(state.timeParameter, out var clock) &&
                            playable.GetFloat(state.timeParameter) == clock;
                        if (dependencies?.WeightControlLayers.Contains(layer) == true)
                        {
                            // State callbacks still run at zero layer weight.
                            // Do not use weighted clip-info to prove that their
                            // transition inputs cannot change after this probe.
                            var visited = new HashSet<Motion>();
                            void CheckControlMotion(Motion motion)
                            {
                                if (motion == null || !visited.Add(motion)) return;
                                if (motion is BlendTree tree)
                                { foreach (var item in tree.children) CheckControlMotion(item.motion); return; }
                                if (!(motion is AnimationClip clip)) throw new InvalidOperationException("未対応のAnimator Motionです。");
                                foreach (var binding in AnimationUtility.GetCurveBindings(clip).Where(binding =>
                                    binding.type == typeof(Animator) && dependencies.Parameters.Contains(binding.propertyName)))
                                {
                                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                                    ValidateNativeParameterCurve(curve, path + "." + child.state.name + " / " + binding.propertyName);
                                    if (!IsConstant(curve)) throw Unstable("時間で変わるFXレイヤー制御の入力は固定表情に変換できません: " +
                                        path + "." + child.state.name + " / " + binding.propertyName);
                                }
                            }
                            CheckControlMotion(child.state.motion);
                        }
                        if (equivalentStates?.Contains(layer) != true && (timed || child.state.transitions.Any(t =>
                            !t.mute && t.hasExitTime && !ExpressionDependencies.IsFalse(t, layerTimedValues))))
                            throw Unstable("時間で遷移するFX状態は固定表情に変換できません: " + path + "." + child.state.name);
                    }
                    foreach (var child in machine.stateMachines) Visit(child.stateMachine, path + "." + child.stateMachine.name, timed);
                }
                // Renaming a layer does not necessarily rename its root state
                // machine. Unity can retain either root in the compiled state
                // hash; accept only a unique match in the actual copied graph.
                foreach (var rootName in new[] { layers[layer].name, layers[layer].stateMachine.name }.Distinct(StringComparer.Ordinal))
                    Visit(layers[layer].stateMachine, rootName, false);
                if (!found) throw new InvalidOperationException("評価中のFX状態を特定できません。");
                foreach (var info in playable.GetCurrentAnimatorClipInfo(layer).Concat(playable.GetNextAnimatorClipInfo(layer)))
                    if (info.clip != null && info.weight > 0.00001f)
                    {
                        if (nativeSupportLayers?.Contains(layer) == true && AnimationUtility.GetAnimationEvents(info.clip).Length != 0)
                            throw new InvalidOperationException("常時適用FXと表情の影響範囲を確定できません。");
                        if (nativeSupportLayers?.Contains(layer) == true &&
                            AnimationUtility.GetObjectReferenceCurveBindings(info.clip).Any(binding => excludedPath?.Invoke(binding.path) != true &&
                                neutralPlan?.AllowsEvaluationBinding(info.clip, binding) != true))
                            throw UnsupportedAppearance(neutralPlan, "常時適用FXと表情の影響範囲を確定できません。");
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
                            if (shadowedNeutralCurves?.Contains((layer, info.clip, binding)) == true) continue;
                            if (fixedMotionTime)
                            {
                                // A readonly normalized Motion Time freezes the
                                // active clip's actual native interpolation.
                                // Keep data checks and the later graph/weight/
                                // binding/pose stability checkpoints intact.
                                ValidateNativeParameterCurve(curve, layers[layer].name + " / " + info.clip.name + " / " + binding.propertyName);
                                continue;
                            }
                            if (terminalClip == info.clip && binding.type == typeof(SkinnedMeshRenderer))
                            {
                                ValidateNativeParameterCurve(curve, layers[layer].name + " / " + info.clip.name + " / " + binding.propertyName);
                                continue;
                            }
                            if (!IsConstant(curve)) throw Unstable("時間で変わるBlendShape・パラメーター曲線は固定表情に変換できません: " +
                                layers[layer].name + " / " + info.clip.name + " / " + binding.path + " / " + binding.propertyName);
                        }
                    }
            }
        }

        private static Dictionary<string, float> FixedMotionTimeInputs(RuntimeAnimatorController original,
            VrChatExpressionMenu.Source metadata, IDictionary<string, float> supplied, Func<string, bool> excludedPath)
        {
            var result = new Dictionary<string, float>(StringComparer.Ordinal);
            if (supplied == null || supplied.Count == 0) return result;
            var unknown = new List<string>();
            var controller = ExpressionDependencies.Controller(original);
            var written = new HashSet<string>(StringComparer.Ordinal);
            var types = new Dictionary<string, HashSet<AnimatorControllerParameterType>>(StringComparer.Ordinal);
            foreach (var runtime in new[] { original }.Concat(metadata?.OtherControllers.Where(value => value != null) ??
                Enumerable.Empty<RuntimeAnimatorController>()))
            {
                foreach (var parameter in ExpressionDependencies.Controller(runtime).parameters)
                {
                    if (!types.TryGetValue(parameter.name, out var declarations))
                        types.Add(parameter.name, declarations = new HashSet<AnimatorControllerParameterType>());
                    declarations.Add(parameter.type);
                }
                written.UnionWith(ExpressionDependencies.Inspect(runtime, runtime == original ? excludedPath : null,
                    new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), unknown, true,
                    typedConditions: true, fxLayerCount: controller.layers.Length).SelectMany(layer => layer.Writes));
            }
            if (unknown.Count != 0) return result;
            foreach (var pair in supplied)
                if (!float.IsNaN(pair.Value) && !float.IsInfinity(pair.Value) && pair.Value >= 0 && pair.Value <= 1 &&
                    !written.Contains(pair.Key) && types.TryGetValue(pair.Key, out var declarations) &&
                    declarations.Count == 1 && declarations.Contains(AnimatorControllerParameterType.Float)) result.Add(pair.Key, pair.Value);
            return result;
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

        // Unsupported appearance support is not invalid VRM data. Only neutral
        // preparation can retain its authored rest; selected expression endpoints
        // still require their complete fixed value to be reproduced.
        private static InvalidOperationException UnsupportedAppearance(NeutralShapePlan plan, string message) =>
            plan?.RetainUnresolvedRest == true ? new NeutralShapeSamplingException(message) : new InvalidOperationException(message);

        private static string ObjectChangeDiagnostic(AnimationClip clip, Func<string, bool> excludedPath, NeutralShapePlan plan)
        {
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                if (excludedPath?.Invoke(binding.path) != true && plan?.AllowsEvaluationBinding(clip, binding) != true)
                    return clip.name + " / " + binding.path + " / " + binding.propertyName + NeutralBindingDiagnostic(plan, clip, binding);
            return null;
        }

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
            NeutralShapePlan neutralPlan = null, ISet<EditorCurveBinding> capturedMorphs = null,
            ISet<AnimationClip> unchangedAppearanceClips = null)
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
                    var objectChange = ObjectChangeDiagnostic(info.clip, excludedPath, neutralPlan);
                    if (objectChange != null)
                        throw UnsupportedAppearance(neutralPlan, "マテリアル・オブジェクトの差し替えを含む表情は未対応です。" +
                            (neutralPlan?.RetainUnresolvedRest == true ? " " + objectChange : ""));
                    foreach (var binding in AnimationUtility.GetCurveBindings(info.clip))
                    {
                        if (binding.type == typeof(Animator)) continue;
                        if (excludedPath?.Invoke(binding.path) == true) continue;
                        if (HarmlessAppearance(neutralAvatar, info.clip, binding, unchangedAppearanceClips, excludedPath)) continue;
                        if (neutralPlan?.AllowsEvaluationBinding(info.clip, binding) == true) continue;
                        if (binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                            throw UnsupportedAppearance(neutralPlan, "BlendShape以外の変化を含みます: " +
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
