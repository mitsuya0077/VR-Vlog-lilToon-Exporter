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
            VrChatExpressionMenu.Source metadata = null)
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
            if (stationaryBindings.Count == 0 && parameterLayers.Count == 0 && !nativeSlot) return;
            if (layerIndex.HasValue && (layerIndex.Value < 0 || layerIndex.Value >= originalLayers.Length))
                throw new InvalidOperationException("表情のFXレイヤーを特定できません。");
            if (layerIndex.HasValue && originalLayers[layerIndex.Value].blendingMode == AnimatorLayerBlendingMode.Additive)
                throw new InvalidOperationException("Additiveレイヤーの表情クリップと常時適用FXの合成は、加算の基準ポーズを確定できないため省略しました。");
            // A registered clip supplies a slot, not a unique original state.
            // Do not guess which entry callbacks to run. Check any dropped
            // selected-slot driver against the retained graph's real inputs.
            var omittedDriverWrites = nativeSlot ? ExpressionDependencies.SelectedLayerDriverWrites(runtime, layerIndex.Value, excludedPath) : null;
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
                    AnimatorControllerLayer Layer(AnimatorControllerLayer original, AnimationClip motion, bool defaults, float weight)
                    {
                        var layerName = original?.name ?? "Direct expression";
                        // Unity hashes a root state's path using its root
                        // machine name. Keep that equal to the layer name,
                        // as AnimatorController.AddLayer does, so the native
                        // state can be resolved by fixed-pose validation.
                        var machine = new AnimatorStateMachine { name = layerName, hideFlags = HideFlags.HideAndDontSave }; owned.Add(machine);
                        var state = machine.AddState("Probe"); owned.Add(state); state.motion = motion; state.writeDefaultValues = defaults;
                        machine.defaultState = state;
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
                        layers[layerIndex.Value] = Layer(original, clip, writeDefaults, original.defaultWeight);
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
                                motion == null ? 0 : index == 0 ? 1 : original.defaultWeight));
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
                    var seeds = new HashSet<EditorCurveBinding>(stationaryBindings);
                    seeds.UnionWith(AnimationUtility.GetCurveBindings(clip));
                    return Evaluate(avatar, probeRuntime, metadata?.Defaults ?? new Dictionary<string, float>(), new Dictionary<string, float>(), excludedPath,
                        metadata, new List<MorphValue>(), seeds, preserveNativeBasePose: nativeSlot, omittedDriverWrites: omittedDriverWrites);
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
            ExpressionDependencies dependencies, VrChatExpressionMenu.Source metadata, Func<string, bool> excludedPath)
        {
            var originalController = ExpressionDependencies.Controller(runtime);
            var omittedLayers = FindExcludedLayers(originalController, runtime, excludedPath, dependencies.Parameters);
            dependencies.Layers.ExceptWith(omittedLayers);
            dependencies.NativeSupportLayers.ExceptWith(omittedLayers);
            var affected = dependencies.Layers.Except(dependencies.NativeSupportLayers).OrderBy(index => index).ToArray();
            if (affected.Length == 0) return new List<MorphValue>();
            var excludedLayers = new HashSet<int>(Enumerable.Range(0, originalController.layers.Length)
                .Except(affected.Concat(dependencies.NativeSupportLayers)));
            using var evaluation = new ExpressionEvaluationSession(runtime, dependencies, metadata.ExpressionParameters,
                !metadata.Defaults.TryGetValue("IsLocal", out var local) || local != 0, neutral: true);
            var controller = evaluation.Controller;
            ValidateNativeSupportMotions(controller, dependencies.NativeSupportLayers, excludedPath, neutral: true);
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
                foreach (var pair in dependencies.NeutralFixedValues) defaults[pair.Key] = pair.Value;
                SetParameters(playable, controller, defaults, neutral: true);
                graph.Play();
                var history = new HashSet<EditorCurveBinding>();
                var visited = new HashSet<AnimationClip>();
                var visitedSupportClips = new HashSet<AnimationClip>();
                var supportLayers = dependencies.NativeSupportLayers.OrderBy(index => index).ToArray();
                Action remember = () =>
                {
                    evaluation.Check();
                    RememberBindings(playable, affected, history, visited, excludedPath, neutral: true);
                    RememberBindings(playable, supportLayers, history, visitedSupportClips, excludedPath, dependencies.Morphs, neutral: true);
                };
                // Initialize at time zero before a short-lived default state can exit.
                // Its WD-Off writes remain part of the eventual neutral appearance.
                graph.Evaluate(0f);
                remember();
                Advance(graph, 120, remember);
                evaluation.CheckNeutralFx();
                var equivalentStates = EquivalentNeutralLayers(playable, controller, affected.Concat(supportLayers), dependencies, metadata);
                ValidateFixedPose(avatar, playable, controller, excludedPath, excludedLayers, equivalentStates,
                    dependencies.NativeSupportLayers, dependencies.Morphs, neutral: true,
                    neutralFixed: dependencies.NeutralFixedValues, dependencies: dependencies, metadata: metadata);
                var bindings = ActiveBindings(playable, affected, excludedPath, equivalentStates, neutral: true);
                var unresolved = new List<MorphValue>();
                var values = Capture(avatar, clone, history, excludedPath, dependencies.Morphs, unresolved, neutral: true);
                for (var checkpoint = 0; checkpoint < 3; checkpoint++)
                {
                    Advance(graph, 7 + checkpoint, evaluation.Check);
                    var nextBindings = ActiveBindings(playable, affected, excludedPath, equivalentStates, neutral: true);
                    if (!bindings.SetEquals(nextBindings))
                        throw new NeutralShapeSamplingException("初期表情が時間で切り替わるため、基本の顔として保存できません。");
                    var next = Capture(avatar, clone, history, excludedPath, dependencies.Morphs, unresolved, neutral: true);
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
            Func<string, bool> excludedPath, bool neutral = false)
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
                if (!(motion is AnimationClip clip)) throw UnsupportedPose("未対応のAnimator Motionです。", neutral);
                if (AnimationUtility.GetAnimationEvents(clip).Length != 0)
                    throw UnsupportedPose("常時適用FXと表情の影響範囲を確定できません。", neutral);
                if (AnimationUtility.GetObjectReferenceCurveBindings(clip).Any(binding => excludedPath?.Invoke(binding.path) != true))
                    throw UnsupportedPose("表情への遷移にマテリアル・オブジェクトの差し替えが含まれます。", neutral);
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (binding.type == typeof(Animator) || excludedPath?.Invoke(binding.path) == true) continue;
                    if (binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                        throw UnsupportedPose("表情への遷移にBlendShape以外の変化が含まれます: " + binding.propertyName, neutral);
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
            ISet<EditorCurveBinding> capturedMorphs = null, bool neutral = false)
        {
            foreach (var layer in layers)
                foreach (var info in playable.GetCurrentAnimatorClipInfo(layer).Concat(playable.GetNextAnimatorClipInfo(layer)))
                {
                    if (info.clip == null || info.weight <= 0.00001f || !visited.Add(info.clip)) continue;
                    if (capturedMorphs != null && AnimationUtility.GetAnimationEvents(info.clip).Length != 0)
                        throw UnsupportedPose("常時適用FXと表情の影響範囲を確定できません。", neutral);
                    if (AnimationUtility.GetObjectReferenceCurveBindings(info.clip).Any(b => excludedPath?.Invoke(b.path) != true))
                        throw UnsupportedPose("表情への遷移にマテリアル・オブジェクトの差し替えが含まれます。", neutral);
                    foreach (var binding in AnimationUtility.GetCurveBindings(info.clip))
                    {
                        if (binding.type == typeof(Animator)) continue; // Parameter curves are checked for stability separately.
                        if (excludedPath?.Invoke(binding.path) == true) continue;
                        if (binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                            throw UnsupportedPose("表情への遷移にBlendShape以外の変化が含まれます: " + binding.propertyName, neutral);
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
            ExpressionDependencies dependencies = null, VrChatExpressionMenu.Source metadata = null)
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
                            AnimationUtility.GetObjectReferenceCurveBindings(info.clip).Any(binding => excludedPath?.Invoke(binding.path) != true)))
                            throw UnsupportedPose("常時適用FXと表情の影響範囲を確定できません。", neutral);
                        foreach (var binding in AnimationUtility.GetCurveBindings(info.clip))
                        {
                            if (excludedPath?.Invoke(binding.path) == true) continue;
                            if (binding.type != typeof(Animator) &&
                                !(binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))) continue;
                            // Missing source morphs cannot be sampled. Defer
                            // them to prepared bindings, which either omit a
                            // stale reference or reject an unresolved new morph.
                            if (binding.type == typeof(SkinnedMeshRenderer) && FindRenderer(avatar, binding.path).sharedMesh
                                .GetBlendShapeIndex(binding.propertyName.Substring("blendShape.".Length)) < 0) continue;
                            // Native base activity must retain its state and
                            // parameter validation. Its unrelated face/eye
                            // curves are not stationary export targets.
                            if (nativeSupportLayers?.Contains(layer) == true && binding.type == typeof(SkinnedMeshRenderer) &&
                                !relevantMorphs.Contains(binding)) continue;
                            var curve = AnimationUtility.GetEditorCurve(info.clip, binding);
                            if (!IsConstant(curve)) throw Unstable("時間で変わるBlendShape・パラメーター曲線は固定表情に変換できません: " +
                                layers[layer].name + " / " + info.clip.name + " / " + binding.path + " / " + binding.propertyName);
                        }
                    }
            }
        }

        internal static bool IsConstant(AnimationCurve curve)
            => VrChatGestureExpressions.IsConstantCurve(curve);

        private static void SetParameters(AnimatorControllerPlayable playable, AnimatorController controller, IDictionary<string, float> values, bool neutral = false)
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
                    default: throw UnsupportedPose("Triggerパラメーターの表情は未対応です。", neutral);
                }
            }
        }

        private static HashSet<EditorCurveBinding> ActiveBindings(AnimatorControllerPlayable playable, int[] layers, Func<string, bool> excludedPath,
            ISet<int> equivalentStates = null, bool neutral = false)
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
                    if (AnimationUtility.GetObjectReferenceCurveBindings(info.clip).Any(b => excludedPath?.Invoke(b.path) != true))
                        throw UnsupportedPose("マテリアル・オブジェクトの差し替えを含む表情は未対応です。", neutral);
                    foreach (var binding in AnimationUtility.GetCurveBindings(info.clip))
                    {
                        if (binding.type == typeof(Animator)) continue;
                        if (excludedPath?.Invoke(binding.path) == true) continue;
                        if (binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                            throw UnsupportedPose("BlendShape以外の変化を含みます: " + binding.propertyName, neutral);
                        bindings.Add(binding);
                    }
                }
            }
            return bindings;
        }

        private static InvalidOperationException UnsupportedPose(string message, bool neutral) => neutral ?
            new NeutralShapeSamplingException(message) : new InvalidOperationException(message);

        private static List<MorphValue> Capture(GameObject source, GameObject clone, IEnumerable<EditorCurveBinding> bindings,
            Func<string, bool> excludedPath, ISet<EditorCurveBinding> relevant, IList<MorphValue> unevaluated, bool neutral = false)
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
                    throw UnsupportedPose("非表示のRendererを動かす項目は取り込めません: " + binding.path, neutral);
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
