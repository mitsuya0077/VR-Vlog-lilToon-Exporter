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
        internal static VrChatExpressionMenu.Source Analyze(GameObject avatar)
        {
            var source = VrChatExpressionMenu.Read(avatar);
            try
            {
                for (var index = 0; index < source.Entries.Count; index++)
                {
                    var entry = source.Entries[index];
                    if (entry.Error != null) continue;
                    if (EditorUtility.DisplayCancelableProgressBar("VRChatの表情を読み込み中", entry.Name, (float)index / source.Entries.Count))
                        throw new OperationCanceledException();
                    try { entry.Values.AddRange(Sample(avatar, source.Controller, source.Defaults, entry.Parameters)); }
                    catch (InvalidOperationException error) { entry.Error = error.Message; }
                }
            }
            finally { EditorUtility.ClearProgressBar(); }
            return source;
        }

        // Only stable, discrete, morph-based FX expressions are portable. Do not
        // silently flatten clothes, material swaps, drivers or animated puppets.
        internal static List<MorphValue> Sample(GameObject avatar, RuntimeAnimatorController runtime,
            IDictionary<string, float> defaults, IDictionary<string, float> selected)
        {
            var controller = runtime as AnimatorController;
            if (runtime is AnimatorOverrideController overrides) controller = overrides.runtimeAnimatorController as AnimatorController;
            if (controller == null) throw new InvalidOperationException("FX Animator Controllerを取得できません。");
            ValidateBehaviours(controller);
            var layers = controller.layers;
            var affected = Enumerable.Range(0, layers.Length).Where(i => Uses(layers[i].stateMachine, selected.Keys)).ToArray();
            if (affected.Length == 0) throw new InvalidOperationException("このメニューに対応するFXの表情がありません。");
            if (layers.Any(l => l.syncedLayerIndex >= 0)) throw new InvalidOperationException("同期Animatorレイヤーの表情変換は未対応です。");

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
                clone.SetActive(true);
                graph = PlayableGraph.Create("VR Vlog expression sampling");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, runtime);
                var output = AnimationPlayableOutput.Create(graph, "Expression", animator);
                output.SetSourcePlayable(playable);
                SetParameters(playable, controller, defaults);
                graph.Play();
                var history = new HashSet<EditorCurveBinding>();
                var visitedClips = new HashSet<AnimationClip>();
                Action remember = () => RememberBindings(playable, affected, history, visitedClips);
                Advance(graph, 120, remember);
                SetParameters(playable, controller, selected);
                Advance(graph, 120, remember);
                ValidateFixedPose(playable, controller);
                var bindings = ActiveBindings(playable, affected);
                var values = Capture(avatar, clone, history);
                // A state transition, a changing curve or changing active clip
                // set cannot be represented as one fixed VRM expression.
                for (var checkpoint = 0; checkpoint < 3; checkpoint++)
                {
                    Advance(graph, 7 + checkpoint);
                    var nextBindings = ActiveBindings(playable, affected);
                    if (!bindings.SetEquals(nextBindings)) throw new InvalidOperationException("表情が時間で切り替わるため、固定表情に変換できません。");
                    var next = Capture(avatar, clone, history);
                    if (values.Count != next.Count || values.Where((v, i) => v.Path != next[i].Path || v.Shape != next[i].Shape || Math.Abs(v.Weight - next[i].Weight) > 0.01f).Any())
                        throw new InvalidOperationException("表情のアニメーションが静止しません。固定表情のみ取り込めます。");
                }
                if (values.Count == 0) throw new InvalidOperationException("有効な顔のBlendShapeアニメーションがありません。");
                return values;
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static void Advance(PlayableGraph graph, int frames, Action afterFrame = null)
        {
            for (var i = 0; i < frames; i++) { graph.Evaluate(1f / 60f); afterFrame?.Invoke(); }
        }

        private static void RememberBindings(AnimatorControllerPlayable playable, int[] layers, HashSet<EditorCurveBinding> history, HashSet<AnimationClip> visited)
        {
            foreach (var layer in layers)
                foreach (var info in playable.GetCurrentAnimatorClipInfo(layer).Concat(playable.GetNextAnimatorClipInfo(layer)))
                {
                    if (info.clip == null || info.weight <= 0.00001f || !visited.Add(info.clip)) continue;
                    if (AnimationUtility.GetObjectReferenceCurveBindings(info.clip).Length > 0)
                        throw new InvalidOperationException("表情への遷移にマテリアル・オブジェクトの差し替えが含まれます。");
                    foreach (var binding in AnimationUtility.GetCurveBindings(info.clip))
                    {
                        if (binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                            throw new InvalidOperationException("表情への遷移にBlendShape以外の変化が含まれます: " + binding.propertyName);
                        history.Add(binding);
                    }
                }
        }

        // Short samples alone cannot prove a fixed pose. Inspect every active
        // state's possible timed exits and the entire lifetime of active morph
        // and parameter curves, including delayed steps after the sample window.
        private static void ValidateFixedPose(AnimatorControllerPlayable playable, AnimatorController controller)
        {
            var layers = controller.layers;
            for (var layer = 0; layer < layers.Length; layer++)
            {
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
                            if (binding.type != typeof(Animator) &&
                                !(binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))) continue;
                            var curve = AnimationUtility.GetEditorCurve(info.clip, binding);
                            if (!IsConstant(curve)) throw new InvalidOperationException("時間で変わるBlendShape・パラメーター曲線は固定表情に変換できません。");
                        }
            }
        }

        private static bool IsConstant(AnimationCurve curve)
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

        private static HashSet<EditorCurveBinding> ActiveBindings(AnimatorControllerPlayable playable, int[] layers)
        {
            var bindings = new HashSet<EditorCurveBinding>();
            foreach (var layer in layers)
            {
                if (playable.IsInTransition(layer)) throw new InvalidOperationException("FXの表情遷移が完了しません（2秒以内に静止する表情が必要です）。");
                if (layer > 0 && playable.GetLayerWeight(layer) <= 0.00001f) continue;
                foreach (var info in playable.GetCurrentAnimatorClipInfo(layer))
                {
                    if (info.weight <= 0.00001f || info.clip == null) continue;
                    if (AnimationUtility.GetObjectReferenceCurveBindings(info.clip).Length > 0)
                        throw new InvalidOperationException("マテリアル・オブジェクトの差し替えを含む表情は未対応です。");
                    foreach (var binding in AnimationUtility.GetCurveBindings(info.clip))
                    {
                        if (binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                            throw new InvalidOperationException("BlendShape以外の変化を含みます: " + binding.propertyName);
                        bindings.Add(binding);
                    }
                }
            }
            return bindings;
        }

        private static List<MorphValue> Capture(GameObject source, GameObject clone, IEnumerable<EditorCurveBinding> bindings)
        {
            var owned = new HashSet<EditorCurveBinding>(bindings);
            // WD-Off values can outlive the clips that wrote them. Also capture
            // evaluated morphs that differ from the authored scene, even when no
            // currently active selected clip contains their bindings.
            foreach (var renderer in source.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.sharedMesh == null) continue;
                var path = AnimationUtility.CalculateTransformPath(renderer.transform, source.transform);
                var animated = FindRenderer(clone, path);
                if (animated.sharedMesh != renderer.sharedMesh)
                    throw new InvalidOperationException("FXによるメッシュ差し替えは表情変換に未対応です: " + path);
                for (var i = 0; i < renderer.sharedMesh.blendShapeCount; i++)
                    if (animated.GetBlendShapeWeight(i) != renderer.GetBlendShapeWeight(i))
                        owned.Add(EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + renderer.sharedMesh.GetBlendShapeName(i)));
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
                if (index < 0) throw new InvalidOperationException("BlendShapeが見つかりません: " + binding.path + "/" + shape);
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

        private static bool Uses(AnimatorStateMachine machine, IEnumerable<string> names)
        {
            var parameters = new HashSet<string>(names, StringComparer.Ordinal);
            bool Conditions(IEnumerable<AnimatorCondition> conditions) => conditions.Any(c => parameters.Contains(c.parameter));
            if (machine.anyStateTransitions.Any(t => Conditions(t.conditions)) || machine.entryTransitions.Any(t => Conditions(t.conditions))) return true;
            foreach (var child in machine.states)
            {
                var state = child.state;
                if (state.transitions.Any(t => Conditions(t.conditions)) || UsesMotion(state.motion, parameters) ||
                    state.timeParameterActive && parameters.Contains(state.timeParameter)) return true;
            }
            return machine.stateMachines.Any(child => Uses(child.stateMachine, parameters) ||
                machine.GetStateMachineTransitions(child.stateMachine).Any(t => Conditions(t.conditions)));
        }

        private static bool UsesMotion(Motion motion, HashSet<string> names)
        {
            if (!(motion is BlendTree tree)) return false;
            return names.Contains(tree.blendParameter) || names.Contains(tree.blendParameterY) ||
                tree.children.Any(c => names.Contains(c.directBlendParameter) || UsesMotion(c.motion, names));
        }

        private static void ValidateBehaviours(AnimatorController controller)
        {
            void Check(IEnumerable<StateMachineBehaviour> behaviours)
            {
                foreach (var behaviour in behaviours)
                {
                    if (behaviour == null) throw new InvalidOperationException("FXに欠けたState Behaviourがあります。");
                    var name = behaviour.GetType().Name;
                    // Tracking control only tells VRChat whether to run its own
                    // blink/lip tracking. Imported fixed faces block VRM tracking.
                    if (name == "VRCAnimatorTrackingControl" || name == "VRC_AnimatorTrackingControl") continue;
                    throw new InvalidOperationException("FXのState Behaviourは未対応です: " + name + "（Parameter Driver・Layer Control等は自動変換しません）");
                }
            }
            void Visit(AnimatorStateMachine machine)
            {
                Check(machine.behaviours);
                foreach (var state in machine.states) Check(state.state.behaviours);
                foreach (var child in machine.stateMachines) Visit(child.stateMachine);
            }
            foreach (var layer in controller.layers) Visit(layer.stateMachine);
        }
    }
}
