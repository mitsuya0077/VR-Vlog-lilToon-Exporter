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
                    try { entry.Values.AddRange(Sample(avatar, source.Controller, source.Defaults, entry.Parameters, excludedPath, source, entry.Unevaluated)); }
                    catch (InvalidOperationException error) { entry.Error = error.Message; }
                }
                VrChatGestureExpressions.Add(avatar, source, excludedPath);
                FaceEmoExpressions.Add(avatar, source, excludedPath, authoringSource, faceEmoBindings);
            }
            finally { EditorUtility.ClearProgressBar(); }
            return source;
        }

        // Stable, discrete, morph-based FX expressions, including deterministic
        // parameter drivers. Each menu is evaluated from its own fresh defaults.
        internal static List<MorphValue> Sample(GameObject avatar, RuntimeAnimatorController runtime,
            IDictionary<string, float> defaults, IDictionary<string, float> selected, Func<string, bool> excludedPath = null,
            VrChatExpressionMenu.Source metadata = null, IList<MorphValue> unevaluated = null)
            => Evaluate(avatar, runtime, defaults, selected, excludedPath, metadata, unevaluated,
                StationaryBindings(avatar, runtime, excludedPath));

        internal static List<MorphValue> SampleDefaults(GameObject avatar, RuntimeAnimatorController runtime,
            IDictionary<string, float> defaults, Func<string, bool> excludedPath = null, VrChatExpressionMenu.Source metadata = null)
        {
            if (runtime == null) return new List<MorphValue>();
            var stationary = StationaryBindings(avatar, runtime, excludedPath);
            if (stationary.Count == 0) return new List<MorphValue>();
            var unresolved = new List<MorphValue>();
            var values = Evaluate(avatar, runtime, defaults, new Dictionary<string, float>(), excludedPath, metadata, unresolved, stationary);
            if (unresolved.Count != 0)
                throw new InvalidOperationException("常時適用するFXの変形を確定できません。統合後のBlendShape設定を確認してください。");
            return values;
        }

        // Invoke on the committed post-Transforming copy. Native animation
        // evaluation occurs on another disposable copy and writes back weights
        // only; shared meshes/controllers and the source remain untouched.
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
        // Permanent layers contain only stationary morph curves, so this
        // response is affine and preserves weighted/stepped tangent timing.
        internal static void ApplyPermanentOverrides(GameObject avatar, RuntimeAnimatorController runtime,
            VrChatExpressionMenu.Entry entry, int? layerIndex = null, bool writeDefaults = false, Func<string, bool> excludedPath = null)
        {
            if (runtime == null || entry.Error != null || entry.Values.Count == 0) return;
            // Inspect the authored graph before any early exit: a layer control
            // can enable a stationary layer whose serialized default weight is
            // zero, as well as disable one that the probe would otherwise keep.
            ExpressionDependencies.ValidateProbeBehaviours(runtime, excludedPath);
            var stationaryBindings = StationaryBindings(avatar, runtime, excludedPath);
            if (stationaryBindings.Count == 0) return;
            var permanent = ExpressionDependencies.StationaryLayers(runtime, excludedPath);
            var originalLayers = ExpressionDependencies.Controller(runtime).layers;
            if (layerIndex.HasValue && (layerIndex.Value < 0 || layerIndex.Value >= originalLayers.Length))
                throw new InvalidOperationException("表情のFXレイヤーを特定できません。");
            if (layerIndex.HasValue && originalLayers[layerIndex.Value].blendingMode == AnimatorLayerBlendingMode.Additive)
                throw new InvalidOperationException("Additiveレイヤーの表情クリップと常時適用FXの合成は、加算の基準ポーズを確定できないため省略しました。");
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
                    // FaceEmo's registered clips are standalone expressions.
                    // They form the base; the prepared avatar's permanent FX
                    // layers remain above them. Gestures retain their FX slot.
                    if (!layerIndex.HasValue) layers.Add(Layer(null, clip, false, 1));
                    for (var index = 0; index < originalLayers.Length; index++)
                    {
                        var original = originalLayers[index];
                        var selected = layerIndex == index;
                        permanent.TryGetValue(index, out var fixedClip);
                        if (fixedClip != null && !AnimationUtility.GetCurveBindings(fixedClip).Any(stationaryBindings.Contains)) fixedClip = null;
                        var motion = selected ? clip : fixedClip;
                        layers.Add(Layer(original, motion, selected ? writeDefaults : motion != null && original.stateMachine.defaultState.writeDefaultValues,
                            motion == null ? 0 : index == 0 ? 1 : original.defaultWeight));
                    }
                    controller.layers = layers.ToArray();
                    var seeds = new HashSet<EditorCurveBinding>(stationaryBindings);
                    seeds.UnionWith(AnimationUtility.GetCurveBindings(clip));
                    return Evaluate(avatar, controller, new Dictionary<string, float>(), new Dictionary<string, float>(), excludedPath,
                        null, new List<MorphValue>(), seeds);
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
            VrChatExpressionMenu.Source metadata, IList<MorphValue> unevaluated, IEnumerable<EditorCurveBinding> initialMorphs)
        {
            var originalController = ExpressionDependencies.Controller(runtime);
            var dependencies = ExpressionDependencies.Analyze(runtime, selected.Keys, excludedPath, metadata, defaults, selected, initialMorphs);
            dependencies.Layers.ExceptWith(FindExcludedLayers(originalController, runtime, excludedPath, dependencies.Parameters));
            var affected = dependencies.Layers.OrderBy(i => i).ToArray();
            if (affected.Length == 0) throw new InvalidOperationException("このメニューに対応するFXの表情がありません。");
            var excludedLayers = new HashSet<int>(Enumerable.Range(0, originalController.layers.Length).Except(affected));
            using var evaluation = new ExpressionEvaluationSession(runtime, dependencies, metadata?.ExpressionParameters,
                !defaults.TryGetValue("IsLocal", out var local) || local != 0);
            var controller = evaluation.Controller;
            unevaluated = unevaluated ?? new List<MorphValue>();

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
                Action remember = () => { evaluation.Check(); RememberBindings(playable, affected, history, visitedClips, excludedPath); };
                Advance(graph, 120, remember);
                evaluation.CheckNeutralFx();
                SetParameters(playable, controller, selected);
                Advance(graph, 120, remember);
                ValidateFixedPose(avatar, playable, controller, excludedPath, excludedLayers);
                var bindings = ActiveBindings(playable, affected, excludedPath);
                var values = Capture(avatar, clone, history, excludedPath, dependencies.Morphs, unevaluated);
                // A state transition, a changing curve or changing active clip
                // set cannot be represented as one fixed VRM expression.
                for (var checkpoint = 0; checkpoint < 3; checkpoint++)
                {
                    Advance(graph, 7 + checkpoint, evaluation.Check);
                    var nextBindings = ActiveBindings(playable, affected, excludedPath);
                    if (!bindings.SetEquals(nextBindings)) throw new InvalidOperationException("表情が時間で切り替わるため、固定表情に変換できません。");
                    var next = Capture(avatar, clone, history, excludedPath, dependencies.Morphs, unevaluated);
                    if (values.Count != next.Count || values.Where((v, i) => v.Path != next[i].Path || v.Shape != next[i].Shape || Math.Abs(v.Weight - next[i].Weight) > 0.01f).Any())
                        throw new InvalidOperationException("表情のアニメーションが静止しません。固定表情のみ取り込めます。");
                }
                if (values.Count == 0 && unevaluated.Count == 0) throw new InvalidOperationException("有効な顔のBlendShapeアニメーションがありません。");
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

        private static void RememberBindings(AnimatorControllerPlayable playable, int[] layers, HashSet<EditorCurveBinding> history, HashSet<AnimationClip> visited, Func<string, bool> excludedPath)
        {
            foreach (var layer in layers)
                foreach (var info in playable.GetCurrentAnimatorClipInfo(layer).Concat(playable.GetNextAnimatorClipInfo(layer)))
                {
                    if (info.clip == null || info.weight <= 0.00001f || !visited.Add(info.clip)) continue;
                    if (AnimationUtility.GetObjectReferenceCurveBindings(info.clip).Any(b => excludedPath?.Invoke(b.path) != true))
                        throw new InvalidOperationException("表情への遷移にマテリアル・オブジェクトの差し替えが含まれます。");
                    foreach (var binding in AnimationUtility.GetCurveBindings(info.clip))
                    {
                        if (binding.type == typeof(Animator)) continue; // Parameter curves are checked for stability separately.
                        if (excludedPath?.Invoke(binding.path) == true) continue;
                        if (binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                            throw new InvalidOperationException("表情への遷移にBlendShape以外の変化が含まれます: " + binding.propertyName);
                        history.Add(binding);
                    }
                }
        }

        // Short samples alone cannot prove a fixed pose. Inspect every active
        // state's possible timed exits and the entire lifetime of active morph
        // and parameter curves, including delayed steps after the sample window.
        private static void ValidateFixedPose(GameObject avatar, AnimatorControllerPlayable playable, AnimatorController controller, Func<string, bool> excludedPath,
            ISet<int> excludedLayers)
        {
            var layers = controller.layers;
            for (var layer = 0; layer < layers.Length; layer++)
            {
                if (excludedLayers.Contains(layer)) continue;
                if (layer > 0 && playable.GetLayerWeight(layer) <= 0.00001f) continue;
                if (playable.IsInTransition(layer)) throw new InvalidOperationException("FXの状態遷移が静止していません。");
                var hash = playable.GetCurrentAnimatorStateInfo(layer).fullPathHash;
                if (hash == 0) continue;
                var found = false;
                void Visit(AnimatorStateMachine machine, string path, bool timedAncestor)
                {
                    var timed = timedAncestor || machine.anyStateTransitions.Any(t => !t.mute && t.hasExitTime);
                    foreach (var child in machine.states)
                    {
                        if (Animator.StringToHash(path + "." + child.state.name) != hash) continue;
                        if (found) throw new InvalidOperationException("FXの状態名を一意に特定できません。");
                        found = true;
                        if (timed || child.state.transitions.Any(t => !t.mute && t.hasExitTime))
                            throw new InvalidOperationException("時間で遷移するFX状態は固定表情に変換できません: " + path + "." + child.state.name);
                    }
                    foreach (var child in machine.stateMachines) Visit(child.stateMachine, path + "." + child.stateMachine.name, timed);
                }
                Visit(layers[layer].stateMachine, layers[layer].name, false);
                if (!found) throw new InvalidOperationException("評価中のFX状態を特定できません。");
                foreach (var info in playable.GetCurrentAnimatorClipInfo(layer))
                    if (info.clip != null && info.weight > 0.00001f)
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
                            var curve = AnimationUtility.GetEditorCurve(info.clip, binding);
                            if (!IsConstant(curve)) throw new InvalidOperationException("時間で変わるBlendShape・パラメーター曲線は固定表情に変換できません。");
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

        private static HashSet<EditorCurveBinding> ActiveBindings(AnimatorControllerPlayable playable, int[] layers, Func<string, bool> excludedPath)
        {
            var bindings = new HashSet<EditorCurveBinding>();
            foreach (var layer in layers)
            {
                if (playable.IsInTransition(layer)) throw new InvalidOperationException("FXの表情遷移が完了しません（2秒以内に静止する表情が必要です）。");
                if (layer > 0 && playable.GetLayerWeight(layer) <= 0.00001f) continue;
                foreach (var info in playable.GetCurrentAnimatorClipInfo(layer))
                {
                    if (info.weight <= 0.00001f || info.clip == null) continue;
                    if (AnimationUtility.GetObjectReferenceCurveBindings(info.clip).Any(b => excludedPath?.Invoke(b.path) != true))
                        throw new InvalidOperationException("マテリアル・オブジェクトの差し替えを含む表情は未対応です。");
                    foreach (var binding in AnimationUtility.GetCurveBindings(info.clip))
                    {
                        if (binding.type == typeof(Animator)) continue;
                        if (excludedPath?.Invoke(binding.path) == true) continue;
                        if (binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                            throw new InvalidOperationException("BlendShape以外の変化を含みます: " + binding.propertyName);
                        bindings.Add(binding);
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
