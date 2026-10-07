using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter
{
    // Native coefficients and poses are witnesses for an analytic tree program,
    // not a resampled replacement curve. Keep SDK callbacks, prepared reference
    // values and the selected state route in the original FX dependency graph.
    internal static class NativeTreeTrajectory
    {
        internal sealed class Result
        {
            internal float Duration;
            internal float NativeDuration;
            internal bool Loop;
            internal bool StateLoop;
            internal readonly Dictionary<AnimationClip, float> Coefficients = new Dictionary<AnimationClip, float>();
            internal readonly Dictionary<EditorCurveBinding, float> Initial = new Dictionary<EditorCurveBinding, float>();
            internal readonly HashSet<EditorCurveBinding> InheritedMorphs = new HashSet<EditorCurveBinding>();
            internal readonly Dictionary<float, Dictionary<EditorCurveBinding, float>> Samples = new Dictionary<float, Dictionary<EditorCurveBinding, float>>();
            internal readonly Dictionary<float, float> SamplePhases = new Dictionary<float, float>();
        }

        internal static Result Probe(GameObject avatar, VrChatExpressionMenu.Source source, int layer, AnimatorState state,
            IDictionary<string, float> inputs, ISet<EditorCurveBinding> morphs, float[] phases, Func<string, bool> excludedPath = null,
            IDictionary<string, float> arrivalInputs = null)
            => ProbeCore(avatar, source, layer, state, inputs, morphs, phases, excludedPath, null, arrivalInputs);

        private static Result ProbeCore(GameObject avatar, VrChatExpressionMenu.Source source, int layer, AnimatorState state,
            IDictionary<string, float> inputs, ISet<EditorCurveBinding> morphs, float[] phases, Func<string, bool> excludedPath,
            IDictionary<AnimationClip, AnimationClip> selectedLeaves, IDictionary<string, float> arrivalInputs)
        {
            if (avatar == null || source == null || state == null || inputs == null || morphs == null || phases == null)
                throw new ArgumentNullException("Native tree trajectory input");
            var original = ExpressionDependencies.Controller(source.Controller);
            if (layer < 0 || layer >= original.layers.Length || original.layers[layer].syncedLayerIndex >= 0 ||
                state.timeParameterActive || state.speedParameterActive || state.cycleOffsetParameterActive || state.mirrorParameterActive ||
                state.speed != 1 || state.cycleOffset != 0 || state.mirror)
                throw new InvalidOperationException("BlendTreeの時間・速度・位相を同一のアニメーションとして確定できません。");
            if (phases.Any(phase => !Finite(phase) || phase < 0 || phase > 1))
                throw new InvalidOperationException("BlendTreeの検証位相が不正です。");
            var paths = new List<string>(); var machines = new HashSet<AnimatorStateMachine>();
            void Find(AnimatorStateMachine machine, string path)
            {
                if (machine == null || !machines.Add(machine)) throw new InvalidOperationException("BlendTreeの元の状態階層が不正です。");
                foreach (var child in machine.states) if (child.state == state) paths.Add(path + "." + state.name);
                foreach (var child in machine.stateMachines) Find(child.stateMachine, path + "." + child.stateMachine.name);
            }
            Find(original.layers[layer].stateMachine, original.layers[layer].name);
            if (paths.Count != 1) throw new InvalidOperationException("BlendTreeの元の状態を一意に特定できません。");
            var types = new Dictionary<string, AnimatorControllerParameterType>(StringComparer.Ordinal);
            foreach (var parameter in original.parameters)
            {
                if (parameter == null || string.IsNullOrEmpty(parameter.name) || types.ContainsKey(parameter.name) ||
                    parameter.type != AnimatorControllerParameterType.Bool && parameter.type != AnimatorControllerParameterType.Int &&
                    parameter.type != AnimatorControllerParameterType.Float && parameter.type != AnimatorControllerParameterType.Trigger)
                    throw new InvalidOperationException("BlendTreeのControllerパラメーターの宣言が不正・重複しています。");
                types.Add(parameter.name, parameter.type);
            }
            var validatedInputs = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var pair in inputs)
            {
                if (string.IsNullOrEmpty(pair.Key) || !types.TryGetValue(pair.Key, out var type) ||
                    type == AnimatorControllerParameterType.Trigger || !Finite(pair.Value) ||
                    type == AnimatorControllerParameterType.Bool && pair.Value != 0 && pair.Value != 1 ||
                    type == AnimatorControllerParameterType.Int && (pair.Value != Math.Truncate(pair.Value) || (double)pair.Value < int.MinValue || (double)pair.Value > int.MaxValue) ||
                    (pair.Key == "GestureLeft" || pair.Key == "GestureRight") && (pair.Value < 0 || pair.Value > 7) ||
                    (pair.Key == "GestureLeftWeight" || pair.Key == "GestureRightWeight") && (pair.Value < 0 || pair.Value > 1))
                    throw new InvalidOperationException("BlendTreeの入力パラメーターが不正です: " + pair.Key);
                validatedInputs.Add(pair.Key, pair.Value);
            }
            inputs = validatedInputs;
            var context = FixedExpressionContext.Create(source.Controller, source.Defaults, source);
            var defaults = new Dictionary<string, float>(source.Defaults, StringComparer.Ordinal);
            foreach (var pair in context.Values) defaults[pair.Key] = pair.Value;
            var arrival = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var pair in arrivalInputs ?? new Dictionary<string, float>())
            {
                if (string.IsNullOrEmpty(pair.Key) || !types.TryGetValue(pair.Key, out var type) ||
                    type == AnimatorControllerParameterType.Trigger || !Finite(pair.Value) ||
                    type == AnimatorControllerParameterType.Bool && pair.Value != 0 && pair.Value != 1 ||
                    type == AnimatorControllerParameterType.Int && (pair.Value != Math.Truncate(pair.Value) || (double)pair.Value < int.MinValue || (double)pair.Value > int.MaxValue) ||
                    (pair.Key == "GestureLeft" || pair.Key == "GestureRight") && (pair.Value < 0 || pair.Value > 7) ||
                    (pair.Key == "GestureLeftWeight" || pair.Key == "GestureRightWeight") && (pair.Value < 0 || pair.Value > 1))
                    throw new InvalidOperationException("BlendTree arrival input is invalid: " + pair.Key);
                var declaration = original.parameters.Single(parameter => parameter.name == pair.Key);
                var normal = defaults.TryGetValue(pair.Key, out var value) ? value : type == AnimatorControllerParameterType.Bool ?
                    (declaration.defaultBool ? 1 : 0) : type == AnimatorControllerParameterType.Int ? declaration.defaultInt : declaration.defaultFloat;
                var final = inputs.TryGetValue(pair.Key, out value) ? value : normal;
                if (pair.Value != normal && pair.Value != final)
                    throw new InvalidOperationException("BlendTree arrival inputs must use the normal or final value: " + pair.Key);
                arrival.Add(pair.Key, pair.Value);
            }
            // The whole graph warms normal inputs before the selection. Keep
            // that normal context separate from inputs so an additional
            // playable's normal-only initializer cannot be pruned as dormant.
            // Scalar probes freeze the selected state from the start and use
            // their own selected-input context in ApplyPermanentOverrides.
            var dependencies = ExpressionDependencies.Analyze(source.Controller, inputs.Keys, excludedPath, source,
                defaults, inputs, morphs, preserveNativeBasePose: true, fixedContext: context);
            var inspected = ExpressionDependencies.Inspect(source.Controller, excludedPath,
                new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), new List<string>(), true);
            foreach (var clip in dependencies.Layers.Concat(dependencies.NativeSupportLayers).Distinct()
                .SelectMany(index => inspected[index].Clips).Distinct())
            {
                SelectedExpressionAppearance.ValidateClipData(clip, excludedPath);
                foreach (var binding in AnimationUtility.GetCurveBindings(clip).Where(binding => binding.type == typeof(Animator) &&
                    dependencies.Parameters.Contains(binding.propertyName)))
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    VrChatExpressionSampler.ValidateNativeParameterCurve(curve, clip.name + " / " + binding.propertyName);
                    if (!VrChatExpressionSampler.IsConstant(curve))
                        throw new InvalidOperationException("BlendTreeの入力を時間で変えるAnimator曲線は固定の合成係数にできません: " + clip.name + " / " + binding.propertyName);
                }
            }
            foreach (var program in dependencies.Drivers.Values)
                VrChatParameterDriver.ValidateTargets(program, types, dependencies.Parameters);
            // WD-Off states can leave a face channel behind when the selected
            // tree has no binding for it. A scalar probe starts directly in the
            // selected state, so its roots cannot identify that native history.
            // Admit only the selected source layer's descriptor-proven facial
            // channels, and keep prepared wardrobe/appearance ownership.
            var inheritedDomain = new HashSet<EditorCurveBinding>();
            var face = FacialProjectionScope.For(avatar, source);
            if (face != null)
            {
                var selectedBindings = new HashSet<EditorCurveBinding>(); var stack = new HashSet<Motion>();
                var overrides = ExpressionDependencies.Overrides(source.Controller);
                void Bindings(Motion motion)
                {
                    if (motion == null || !stack.Add(motion) || stack.Count > 16)
                        throw new InvalidOperationException("BlendTree inherited face motion hierarchy is invalid.");
                    try
                    {
                        if (motion is AnimationClip clip)
                        {
                            if (overrides.TryGetValue(clip, out var replacement)) clip = replacement;
                            selectedBindings.UnionWith(AnimationUtility.GetCurveBindings(clip));
                        }
                        else if (motion is BlendTree tree) foreach (var child in tree.children) Bindings(child.motion);
                        else throw new InvalidOperationException("BlendTree inherited face motion type is unsupported.");
                    }
                    finally { stack.Remove(motion); }
                }
                Bindings(original.GetStateEffectiveMotion(state, layer) ?? state.motion);
                inheritedDomain.UnionWith(inspected[layer].Morphs.Where(binding => face.Morphs.Contains(binding) &&
                    dependencies.Morphs.Contains(binding) && !selectedBindings.Contains(binding) && !morphs.Contains(binding)));
            }
            var capturedMorphs = new HashSet<EditorCurveBinding>(morphs);
            var inheritedHistory = new HashSet<EditorCurveBinding>();
            var observedClips = new HashSet<AnimationClip>(); var historyClips = new HashSet<AnimationClip>();
            var observedLayers = dependencies.Layers.Concat(dependencies.NativeSupportLayers).Append(layer).Distinct().ToArray();
            var unchangedAppearanceClips = VrChatExpressionSampler.UnchangedAppearanceClips(source.Controller,
                Enumerable.Range(0, original.layers.Length));
            using var evaluation = new ExpressionEvaluationSession(source.Controller, dependencies, source.ExpressionParameters,
                !defaults.TryGetValue("IsLocal", out var local) || local != 0, context);
            var controller = evaluation.Controller;
            var scene = EditorSceneManager.NewPreviewScene(); var graph = default(PlayableGraph); GameObject clone = null;
            var scopedTrees = new List<BlendTree>(); AnimatorState scopedState = null; Motion originalMotion = null;
            try
            {
                if (selectedLeaves != null)
                {
                    var targets = new List<AnimatorState>();
                    void Target(AnimatorStateMachine machine, string path)
                    {
                        foreach (var child in machine.states) if (path + "." + child.state.name == paths.Single()) targets.Add(child.state);
                        foreach (var child in machine.stateMachines) Target(child.stateMachine, path + "." + child.stateMachine.name);
                    }
                    Target(controller.layers[layer].stateMachine, controller.layers[layer].name);
                    if (targets.Count != 1) throw new InvalidOperationException("BlendTreeの係数検証でコピーした選択状態を一意に確認できません。");
                    var copies = new Dictionary<BlendTree, BlendTree>();
                    var replaced = new HashSet<AnimationClip>();
                    Motion Replace(Motion motion)
                    {
                        if (motion is AnimationClip clip)
                        {
                            if (!selectedLeaves.TryGetValue(clip, out var replacement)) return clip;
                            replaced.Add(clip); return replacement;
                        }
                        if (!(motion is BlendTree tree)) throw new InvalidOperationException("BlendTreeの係数検証に未対応のMotionがあります。");
                        if (copies.TryGetValue(tree, out var existing)) return existing;
                        var copy = new BlendTree { name = tree.name, hideFlags = HideFlags.HideAndDontSave,
                            blendType = tree.blendType, blendParameter = tree.blendParameter, blendParameterY = tree.blendParameterY,
                            useAutomaticThresholds = tree.useAutomaticThresholds, minThreshold = tree.minThreshold, maxThreshold = tree.maxThreshold };
                        copies.Add(tree, copy); scopedTrees.Add(copy);
                        var children = tree.children;
                        for (var index = 0; index < children.Length; index++) children[index].motion = Replace(children[index].motion);
                        copy.children = children;
                        var stored = copy.children;
                        if (stored.Length != children.Length || stored.Where((child, index) => child.threshold != children[index].threshold ||
                            !child.position.Equals(children[index].position) || child.timeScale != children[index].timeScale ||
                            child.cycleOffset != children[index].cycleOffset || child.mirror != children[index].mirror ||
                            child.directBlendParameter != children[index].directBlendParameter).Any())
                            throw new InvalidOperationException("BlendTreeの係数検証で元の子のしきい値・時間を保持できません。");
                        using (var from = new SerializedObject(tree))
                        using (var to = new SerializedObject(copy))
                        {
                            var normalized = from.FindProperty("m_NormalizedBlendValues");
                            if (normalized != null) { to.FindProperty("m_NormalizedBlendValues").boolValue = normalized.boolValue; to.ApplyModifiedPropertiesWithoutUndo(); }
                        }
                        return copy;
                    }
                    scopedState = targets.Single(); originalMotion = scopedState.motion; scopedState.motion = Replace(originalMotion);
                    if (!selectedLeaves.Keys.All(replaced.Contains))
                        throw new InvalidOperationException("BlendTreeの係数検証で実効Overrideの子Clipを選択状態に対応できません。");
                }
                clone = Object.Instantiate(avatar); clone.name = avatar.name; clone.hideFlags = HideFlags.HideAndDontSave;
                SceneManager.MoveGameObjectToScene(clone, scene);
                foreach (var behaviour in clone.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                var animator = clone.GetComponent<Animator>() ?? clone.AddComponent<Animator>();
                animator.runtimeAnimatorController = null; animator.enabled = true; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.applyRootMotion = false; animator.fireEvents = false; evaluation.Animator = animator; clone.SetActive(true);
                graph = PlayableGraph.Create("VR Vlog native tree trajectory"); graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                AnimationPlayableOutput.Create(graph, "Tree trajectory", animator).SetSourcePlayable(playable);
                void RememberInherited()
                {
                    foreach (var clip in playable.GetCurrentAnimatorClipInfo(layer).Concat(playable.GetNextAnimatorClipInfo(layer))
                        .Where(info => info.clip != null && info.weight > 0).Select(info => info.clip).Distinct())
                        if ((layer == 0 || playable.GetLayerWeight(layer) > 0) && historyClips.Add(clip))
                            foreach (var binding in AnimationUtility.GetCurveBindings(clip).Where(inheritedDomain.Contains)) inheritedHistory.Add(binding);
                    foreach (var retained in observedLayers.Where(index => index == 0 || playable.GetLayerWeight(index) > 0))
                        foreach (var clip in playable.GetCurrentAnimatorClipInfo(retained).Concat(playable.GetNextAnimatorClipInfo(retained))
                            .Where(info => info.clip != null && info.weight > 0).Select(info => info.clip)) observedClips.Add(clip);
                }
                SetParameters(playable, controller, defaults); graph.Play(); graph.Evaluate(0); evaluation.Check(); RememberInherited();
                void Advance(int frames)
                {
                    for (var frame = 0; frame < frames; frame++) { graph.Evaluate(1f / 60); evaluation.Check(); RememberInherited(); }
                }
                Advance(120); evaluation.CheckNeutralFx();
                // A selector can leave the old hand state latched when all
                // inputs change in the same native decision. Select authored
                // menu/custom inputs while hands retain their normal values,
                // then enter the requested hand through the original graph.
                var selectors = inputs.Where(pair => !VrChatParameterDriver.BuiltIn.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                if (selectors.Count > 0) { SetParameters(playable, controller, selectors); Advance(120); evaluation.CheckNeutralFx(); }
                // Some authored states latch a hand arrival and then accept a
                // different final knot. Every staged value is already a normal
                // or final input, so the complete inventory above also covers
                // this route through the original SDK graph.
                if (arrival.Count > 0) { SetParameters(playable, controller, arrival); Advance(120); evaluation.CheckNeutralFx(); SelectedState(); }
                SetParameters(playable, controller, inputs); Advance(120);
                void SelectedState()
                {
                    if (playable.IsInTransition(layer) || !VrChatExpressionSampler.MatchesExpectedStatePath(controller, layer, paths.Single(),
                        playable.GetCurrentAnimatorStateInfo(layer).fullPathHash))
                    {
                        var observed = new List<string>(); var hash = playable.GetCurrentAnimatorStateInfo(layer).fullPathHash;
                        void Observe(AnimatorStateMachine machine, string path)
                        {
                            foreach (var child in machine.states)
                                if (VrChatExpressionSampler.MatchesExpectedStatePath(controller, layer, path + "." + child.state.name, hash)) observed.Add(path + "." + child.state.name);
                            foreach (var child in machine.stateMachines) Observe(child.stateMachine, path + "." + child.stateMachine.name);
                        }
                        Observe(controller.layers[layer].stateMachine, controller.layers[layer].name);
                        var values = inputs.Keys.OrderBy(name => name, StringComparer.Ordinal).Concat(dependencies.Parameters.Except(inputs.Keys)
                            .OrderBy(name => name, StringComparer.Ordinal)).Where(name => types.TryGetValue(name, out var type) && type != AnimatorControllerParameterType.Trigger)
                            .Take(24).Select(name => name + "=" + Parameter(playable, types, name).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                        throw new InvalidOperationException("BlendTreeの元の状態に到達・保持できません: " + paths.Single() + " / current=" +
                            string.Join(", ", observed) + " / hash=" + hash + " / transition=" + playable.IsInTransition(layer) + " / inputs=" + string.Join(", ", values));
                    }
                }
                SelectedState();
                if (inheritedHistory.Count > 0)
                    inheritedHistory.IntersectWith(NeutralShapePlan.CreateObservedHistoryProjection(avatar,
                        inheritedHistory, observedClips, unchangedAppearanceClips, excludedPath).CommittedMorphs);
                var stableParameters = dependencies.Parameters.ToDictionary(name => name, name => Parameter(playable, types, name), StringComparer.Ordinal);
                var stableWeights = evaluation.CaptureLayerWeights(playable);
                var initialInfo = playable.GetCurrentAnimatorStateInfo(layer);
                var rate = initialInfo.speed * initialInfo.speedMultiplier;
                if (!Finite(initialInfo.length) || initialInfo.length < 0 || rate != 1)
                    throw new InvalidOperationException("BlendTreeのアニメーション周期が不正です。");
                var result = new Result { Duration = initialInfo.length / rate, NativeDuration = initialInfo.length / rate, StateLoop = initialInfo.loop };
                result.InheritedMorphs.UnionWith(inheritedHistory); capturedMorphs.UnionWith(inheritedHistory);
                Dictionary<AnimationClip, float> Coefficients()
                {
                    var values = new Dictionary<AnimationClip, float>();
                    foreach (var item in playable.GetCurrentAnimatorClipInfo(layer))
                    {
                        if (item.clip == null || !Finite(item.weight) || item.weight < 0)
                            throw new InvalidOperationException("BlendTreeのネイティブ合成係数が不正です。");
                        if (item.weight == 0) continue;
                        if (!values.ContainsKey(item.clip)) values.Add(item.clip, 0);
                        values[item.clip] += item.weight;
                    }
                    if (values.Count == 0 || values.Values.Any(value => !Finite(value)))
                        throw new InvalidOperationException("BlendTreeの有効な子アニメーションがありません。");
                    return values;
                }
                foreach (var pair in Coefficients()) result.Coefficients.Add(pair.Key, pair.Value);
                var changing = result.Coefficients.Keys.Where(clip => AnimationUtility.GetCurveBindings(clip).Any(binding =>
                    !VrChatExpressionSampler.IsConstant(AnimationUtility.GetEditorCurve(clip, binding)))).ToArray();
                if (changing.Select(clip => clip.isLooping).Distinct().Count() > 1 || changing.Select(clip => clip.length).Distinct().Count() > 1)
                    throw new InvalidOperationException("BlendTreeの動く子Clipの長さ・Loopが一致しません。");
                // A zero-duration constant child can make Unity's state.loop
                // false while the changing leaves continue to loop. Its state
                // length remains the native clock for the weighted program.
                result.Loop = changing.Length > 0 && changing.All(clip => clip.isLooping);
                if (changing.Length == 0) result.Duration = 0;
                else if (!Finite(result.Duration) || result.Duration <= 0)
                    throw new InvalidOperationException("BlendTreeのアニメーション周期が不正です。");
                Dictionary<EditorCurveBinding, float> Capture()
                {
                    var values = new Dictionary<EditorCurveBinding, float>();
                    foreach (var binding in capturedMorphs)
                    {
                        if (excludedPath?.Invoke(binding.path) == true) continue;
                        if (binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                            throw new InvalidOperationException("BlendTreeの検証対象にBlendShape以外が含まれています。");
                        var renderer = VrChatExpressionSampler.FindRenderer(clone, binding.path);
                        var index = renderer.sharedMesh == null ? -1 : renderer.sharedMesh.GetBlendShapeIndex(binding.propertyName.Substring("blendShape.".Length));
                        if (index < 0 || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                            throw new InvalidOperationException("BlendTreeの検証対象を現在のアバターで解決できません: " + binding.path + " / " + binding.propertyName);
                        var value = renderer.GetBlendShapeWeight(index);
                        if (!Finite(value)) throw new InvalidOperationException("BlendTreeのネイティブ表情値が不正です。");
                        values.Add(binding, value);
                    }
                    return values;
                }
                void Stable()
                {
                    evaluation.Check(); evaluation.CheckLayerWeights(playable, stableWeights); SelectedState();
                    var current = playable.GetCurrentAnimatorStateInfo(layer);
                    if (current.loop != result.StateLoop || !Finite(current.length) || Math.Abs(current.length - initialInfo.length) > .0001f ||
                        current.speed != initialInfo.speed || current.speedMultiplier != initialInfo.speedMultiplier ||
                        stableParameters.Any(pair => Parameter(playable, types, pair.Key) != pair.Value))
                        throw new InvalidOperationException("BlendTreeの入力・速度・周期が時間で変化するため、固定の合成アニメーションにできません。");
                    var coefficients = Coefficients();
                    if (coefficients.Count != result.Coefficients.Count || result.Coefficients.Any(pair =>
                        !coefficients.TryGetValue(pair.Key, out var weight) || Math.Abs(weight - pair.Value) > .00001f))
                        throw new InvalidOperationException("BlendTreeの合成係数が時間で変化します。");
                }
                float Seek(float phase)
                {
                    playable.Play(initialInfo.fullPathHash, layer, phase); graph.Evaluate(0); graph.Evaluate(0); Stable();
                    var nativePhase = playable.GetCurrentAnimatorStateInfo(layer).normalizedTime;
                    if (!Finite(nativePhase) || Math.Abs(nativePhase - phase) > .0001f)
                        throw new InvalidOperationException("BlendTreeのネイティブ時間を検証位相に合わせられません。");
                    return nativePhase;
                }
                void SamePose(Dictionary<EditorCurveBinding, float> expected, Dictionary<EditorCurveBinding, float> actual, string history = null)
                {
                    if (history != null && (expected.Count != actual.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out var value) || Math.Abs(value - pair.Value) > .02f)))
                    {
                        var binding = expected.Keys.FirstOrDefault(key => !actual.TryGetValue(key, out var value) || Math.Abs(value - expected[key]) > .02f);
                        throw new InvalidOperationException("BlendTreeの同じ位相の表情が評価履歴に依存するため、固定した合成アニメーションにできません: " +
                            history + " / " + binding.path + " / " + binding.propertyName);
                    }
                    if (expected.Count != actual.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out var value) || Math.Abs(value - pair.Value) > .02f))
                        throw new InvalidOperationException("BlendTreeの動く子ClipはLoopですが、元のUnityグラフの表情が同じ位相で繰り返されません。");
                }
                void Idempotent(Dictionary<EditorCurveBinding, float> expected, float phase)
                {
                    // A sparse WD-Off stream can feed its previous complete
                    // pose back into a missing leaf binding. Clip coefficients
                    // then appear stable while the same phase keeps changing.
                    // Do not replace that history with an equilibrium sample.
                    for (var checkpoint = 0; checkpoint < 20; checkpoint++) { graph.Evaluate(0); Stable(); }
                    var currentPhase = playable.GetCurrentAnimatorStateInfo(layer).normalizedTime;
                    if (!Finite(currentPhase) || Math.Abs(currentPhase - phase) > .0001f)
                        throw new InvalidOperationException("BlendTreeの同じ位相の評価が履歴で変わります: " + paths.Single());
                    SamePose(expected, Capture(), paths.Single() + " / phase=" + phase);
                }
                if (result.InheritedMorphs.Count > 0)
                    foreach (var retained in dependencies.Layers.Concat(dependencies.NativeSupportLayers).Append(layer).Distinct())
                        foreach (var clip in playable.GetCurrentAnimatorClipInfo(retained).Where(info => info.clip != null && info.weight > 0)
                            .Select(info => info.clip).Distinct())
                            foreach (var binding in AnimationUtility.GetCurveBindings(clip).Where(result.InheritedMorphs.Contains))
                                if (!VrChatExpressionSampler.IsConstant(AnimationUtility.GetEditorCurve(clip, binding)))
                                    throw new InvalidOperationException("BlendTree inherited facial channel has a changing native support curve: " + clip.name + " / " + binding.propertyName);
                if (result.Loop || changing.Length == 0 || result.InheritedMorphs.Count > 0)
                {
                    // Repetition witnesses cannot establish an indefinitely
                    // held state if an enabled future timed edge can escape it.
                    // Keep every retained current state's ancestor AnyState
                    // timers and every authored writer in this proof.
                    var written = new HashSet<string>(inspected.SelectMany(item => item.Writes), StringComparer.Ordinal);
                    foreach (var other in source.OtherControllers.Where(value => value != null))
                        written.UnionWith(ExpressionDependencies.Inspect(other, null,
                            new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), new List<string>(), true,
                            fxLayerCount: controller.layers.Length).SelectMany(item => item.Writes));
                    var invariant = stableParameters.Where(pair => !written.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                    foreach (var retained in dependencies.Layers.Concat(dependencies.NativeSupportLayers).Append(layer).Distinct())
                    {
                        var matches = 0; var hash = playable.GetCurrentAnimatorStateInfo(retained).fullPathHash;
                        void Check(AnimatorState current, IEnumerable<AnimatorStateTransition> transitions, string path)
                        {
                            var edges = transitions.Where(edge => edge != null).ToArray(); var solo = edges.Any(edge => edge.solo);
                            foreach (var edge in edges.Where(edge => !edge.mute && (!solo || edge.solo) && edge.hasExitTime))
                                if (!(edge.destinationState == current && !edge.canTransitionToSelf) && !ExpressionDependencies.IsFalse(edge, invariant))
                                    throw new InvalidOperationException("BlendTreeのLoopを保持できない時間遷移があります: " + path);
                        }
                        void Timers(AnimatorStateMachine machine, string path, AnimatorStateMachine[] ancestors)
                        {
                            var lineage = ancestors.Concat(new[] { machine }).ToArray();
                            foreach (var child in machine.states)
                                if (VrChatExpressionSampler.MatchesExpectedStatePath(controller, retained, path + "." + child.state.name, hash))
                                {
                                    matches++; Check(child.state, child.state.transitions, path + "." + child.state.name);
                                    foreach (var ancestor in lineage) Check(child.state, ancestor.anyStateTransitions, path + " / AnyState");
                                }
                            foreach (var child in machine.stateMachines) Timers(child.stateMachine, path + "." + child.stateMachine.name, lineage);
                        }
                        var origin = retained;
                        while (controller.layers[origin].syncedLayerIndex >= 0) origin = controller.layers[origin].syncedLayerIndex;
                        Timers(controller.layers[origin].stateMachine, controller.layers[retained].name, Array.Empty<AnimatorStateMachine>());
                        if (matches != 1) throw new InvalidOperationException("BlendTreeのLoop検証で現在のFX状態を一意に確認できません。");
                    }
                    if (result.Loop)
                    {
                        foreach (var phase in new[] { 0f, .17f, .5f, .83f })
                        {
                            Seek(phase); var expected = Capture(); Idempotent(expected, phase); Advance(2); Stable();
                            for (var epoch = 1; epoch <= 2; epoch++)
                            {
                                Seek(phase + epoch); var repeated = Capture();
                                SamePose(expected, repeated, paths.Single() + " / phase=" + phase + " / epoch=" + epoch);
                                Idempotent(repeated, phase + epoch); Advance(2); Stable();
                            }
                        }
                        // Cross the first epoch naturally as well: a seek must
                        // not hide a native state exit or a clamped leaf clock.
                        Seek(1 - .5f / (60 * result.Duration)); Advance(1); Stable();
                        var crossed = playable.GetCurrentAnimatorStateInfo(layer).normalizedTime;
                        if (!Finite(crossed) || crossed <= 1 || crossed >= 2)
                            throw new InvalidOperationException("BlendTreeの元のUnityグラフがLoopの境界を進みません。");
                        var natural = Capture(); Seek(crossed - 1); SamePose(natural, Capture(), paths.Single() + " / natural loop boundary");
                    }
                }
                if (changing.Length == 0)
                {
                    // Publish the full original held pose after the authored
                    // route. A constant final knot has no temporal program and
                    // must not be rewound into a different state-entry history.
                    if (selectedLeaves == null)
                        foreach (var retained in dependencies.Layers.Concat(dependencies.NativeSupportLayers).Append(layer).Distinct())
                            foreach (var clip in playable.GetCurrentAnimatorClipInfo(retained).Where(info => info.clip != null && info.weight > 0).Select(info => info.clip).Distinct())
                                foreach (var binding in AnimationUtility.GetCurveBindings(clip).Where(binding =>
                                    excludedPath?.Invoke(binding.path) != true && (binding.type == typeof(Animator) && dependencies.Parameters.Contains(binding.propertyName) ||
                                    binding.type == typeof(SkinnedMeshRenderer) && capturedMorphs.Contains(binding))))
                                    if (!VrChatExpressionSampler.IsConstant(AnimationUtility.GetEditorCurve(clip, binding)))
                                        throw new InvalidOperationException("BlendTree held pose has a changing native support curve: " + clip.name + " / " + binding.propertyName);
                    var held = Capture();
                    for (var checkpoint = 0; checkpoint < 3; checkpoint++)
                    {
                        Advance(7 + checkpoint); Stable(); SamePose(held, Capture());
                    }
                    foreach (var phase in phases.Concat(new[] { 0f }).Distinct())
                    {
                        result.Samples.Add(phase, new Dictionary<EditorCurveBinding, float>(held)); result.SamplePhases.Add(phase, 0);
                    }
                    foreach (var pair in held) result.Initial.Add(pair.Key, pair.Value);
                    context.UsedParameters.UnionWith(dependencies.Parameters.Where(context.Values.ContainsKey));
                    return result;
                }
                foreach (var phase in phases.Concat(new[] { 0f }).Distinct().OrderBy(value => value))
                {
                    var actual = result.Loop && phase == 1 ? 1f - .000001f : phase;
                    var nativePhase = Seek(actual);
                    var values = Capture(); Idempotent(values, nativePhase);
                    result.Samples.Add(phase, values); result.SamplePhases.Add(phase, nativePhase);
                    if (phase == 0) foreach (var pair in values) result.Initial.Add(pair.Key, pair.Value);
                    // Natural progression checks guard against a short-lived
                    // callback or transition which a seek alone would conceal.
                    Advance(2); Stable();
                }
                // Revisit every original witness after a different ordered
                // seek history. An analytic curve must give the same original
                // native value at its phase, independent of earlier seeks.
                foreach (var phase in result.Samples.Keys.OrderByDescending(value => value))
                {
                    var actual = Seek(result.SamplePhases[phase]); var values = Capture();
                    SamePose(result.Samples[phase], values, paths.Single() + " / repeated phase=" + actual);
                    Idempotent(values, actual); Advance(2); Stable();
                }
                context.UsedParameters.UnionWith(dependencies.Parameters.Where(context.Values.ContainsKey));
                return result;
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy(); if (clone != null) Object.DestroyImmediate(clone); EditorSceneManager.ClosePreviewScene(scene);
                if (scopedState != null) scopedState.motion = originalMotion;
                foreach (var tree in scopedTrees) tree.children = Array.Empty<ChildMotion>();
                foreach (var tree in scopedTrees) Object.DestroyImmediate(tree);
            }
        }

        internal static Dictionary<EditorCurveBinding, Dictionary<AnimationClip, float>> CalibrateBindingCoefficients(
            GameObject avatar, VrChatExpressionMenu.Source source, int layer, AnimatorState state, IDictionary<string, float> inputs,
            Result native, ISet<EditorCurveBinding> bindings, IDictionary<EditorCurveBinding, float> scalarZero,
            IDictionary<EditorCurveBinding, float> scalarFull, Func<string, bool> excludedPath = null,
            IDictionary<string, float> arrivalInputs = null)
        {
            var active = native.Coefficients.Where(pair => pair.Value > .000001f).Select(pair => pair.Key).ToArray();
            var result = bindings.ToDictionary(binding => binding, binding => active.ToDictionary(clip => clip, clip => 0f));
            var owned = new List<AnimationClip>();
            try
            {
                var zeros = new Dictionary<AnimationClip, AnimationClip>(); var hundreds = new Dictionary<AnimationClip, AnimationClip>();
                AnimationClip Constant(AnimationClip original, float value)
                {
                    if (AnimationUtility.GetAnimationEvents(original).Length != 0 || AnimationUtility.GetObjectReferenceCurveBindings(original).Length != 0)
                        throw new InvalidOperationException("BlendTreeの係数検証はBlendShapeだけの子Clipが必要です: " + original.name);
                    var copy = new AnimationClip { name = original.name + " / coefficient " + value, hideFlags = HideFlags.HideAndDontSave, frameRate = original.frameRate, wrapMode = original.wrapMode };
                    owned.Add(copy);
                    foreach (var binding in AnimationUtility.GetCurveBindings(original))
                    {
                        if (binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                            throw new InvalidOperationException("BlendTreeの係数検証にBlendShape以外の曲線があります: " + original.name);
                        var curve = AnimationUtility.GetEditorCurve(original, binding);
                        if (curve == null || curve.length == 0) throw new InvalidOperationException("BlendTreeの係数検証で子曲線を読めません: " + original.name);
                        AnimationUtility.SetEditorCurve(copy, binding, new AnimationCurve(curve.keys.Select(key => new Keyframe(key.time, value, 0, 0)).ToArray()));
                    }
                    AnimationUtility.SetAnimationClipSettings(copy, AnimationUtility.GetAnimationClipSettings(original));
                    if (copy.length != original.length || copy.isLooping != original.isLooping ||
                        !AnimationUtility.GetCurveBindings(copy).ToHashSet().SetEquals(AnimationUtility.GetCurveBindings(original)))
                        throw new InvalidOperationException("BlendTreeの係数検証で子Clipの時間・バインドを保持できません: " + original.name);
                    return copy;
                }
                foreach (var clip in active) { zeros.Add(clip, Constant(clip, 0)); hundreds.Add(clip, Constant(clip, 100)); }
                Result Sample(IDictionary<AnimationClip, AnimationClip> replacements)
                {
                    var measured = ProbeCore(avatar, source, layer, state, inputs, new HashSet<EditorCurveBinding>(native.Initial.Keys), new[] { 0f }, excludedPath, replacements, arrivalInputs);
                    var reverse = replacements.ToDictionary(pair => pair.Value, pair => pair.Key);
                    var coefficients = measured.Coefficients.ToDictionary(pair => reverse.TryGetValue(pair.Key, out var original) ? original : pair.Key, pair => pair.Value);
                    if (measured.StateLoop != native.StateLoop || Math.Abs(measured.NativeDuration - native.NativeDuration) > .0001f ||
                        coefficients.Count != native.Coefficients.Count || native.Coefficients.Any(pair =>
                            !coefficients.TryGetValue(pair.Key, out var value) || Math.Abs(value - pair.Value) > .00001f))
                        throw new InvalidOperationException("BlendTreeの係数検証が元の子Clip・時間・合成係数を変更しました。");
                    return measured;
                }
                var baseline = Sample(zeros);
                foreach (var clip in active)
                {
                    var replacements = new Dictionary<AnimationClip, AnimationClip>(zeros) { [clip] = hundreds[clip] };
                    var single = Sample(replacements);
                    foreach (var binding in bindings)
                    {
                        if (!scalarZero.TryGetValue(binding, out var zero) || !scalarFull.TryGetValue(binding, out var full))
                            throw new InvalidOperationException("BlendTreeの係数検証に常時FXの応答がありません: " + binding.propertyName);
                        var response = full - zero; var delta = single.Initial[binding] - baseline.Initial[binding];
                        if (Math.Abs(response) < .0001f)
                        {
                            if (Math.Abs(delta) > .02f) throw new InvalidOperationException("BlendTreeの常時FXによる上書きを係数検証で分離できません: " + binding.propertyName);
                            continue;
                        }
                        var coefficient = delta / response;
                        if (!Finite(coefficient) || coefficient < -.0001f || coefficient > 1.0001f ||
                            AnimationUtility.GetEditorCurve(clip, binding) == null && Math.Abs(coefficient) > .0001f)
                            throw new InvalidOperationException("BlendTreeのバインド別合成係数を安全に確認できません: " + binding.propertyName + " / " + clip.name);
                        result[binding][clip] = Mathf.Clamp01(coefficient);
                    }
                }
                if (result.Any(pair => pair.Value.Values.Sum() > 1.0001f))
                    throw new InvalidOperationException("BlendTreeのバインド別合成係数の合計が通常の混合ではありません。");
                return result;
            }
            finally { foreach (var clip in owned) Object.DestroyImmediate(clip); }
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        static float Parameter(AnimatorControllerPlayable playable, IDictionary<string, AnimatorControllerParameterType> types, string name)
        {
            if (!types.TryGetValue(name, out var type)) throw new InvalidOperationException("BlendTreeの入力パラメーターがControllerにありません: " + name);
            float value;
            switch (type)
            {
                case AnimatorControllerParameterType.Bool: value = playable.GetBool(name) ? 1 : 0; break;
                case AnimatorControllerParameterType.Int: value = playable.GetInteger(name); break;
                case AnimatorControllerParameterType.Float: value = playable.GetFloat(name); break;
                default: throw new InvalidOperationException("BlendTreeの入力にTriggerを使えません: " + name);
            }
            if (!Finite(value)) throw new InvalidOperationException("BlendTreeの入力パラメーターが不正です: " + name);
            return value;
        }

        static void SetParameters(AnimatorControllerPlayable playable, AnimatorController controller, IDictionary<string, float> values)
        {
            foreach (var parameter in controller.parameters)
            {
                if (!values.TryGetValue(parameter.name, out var value)) continue;
                if (!Finite(value)) throw new InvalidOperationException("BlendTreeの入力パラメーターが不正です: " + parameter.name);
                switch (parameter.type)
                {
                    case AnimatorControllerParameterType.Bool: playable.SetBool(parameter.name, value != 0); break;
                    case AnimatorControllerParameterType.Int: playable.SetInteger(parameter.name, Mathf.RoundToInt(value)); break;
                    case AnimatorControllerParameterType.Float: playable.SetFloat(parameter.name, value); break;
                    default: throw new InvalidOperationException("BlendTreeの入力にTriggerを使えません: " + parameter.name);
                }
            }
        }
    }
}
