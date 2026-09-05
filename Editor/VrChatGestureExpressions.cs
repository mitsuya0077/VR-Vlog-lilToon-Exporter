using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // A gesture's authored, constant AnimationClip is a composed face in its own
    // right. Read that clip, not every mesh morph and not an arbitrary frame of
    // the live FX controller (whose independent blink can keep running forever).
    // This route exports the clip on the authored base face, not a simulation of
    // state behaviours, retained WD-Off values or additional Animator layers.
    internal static class VrChatGestureExpressions
    {
        internal static void Add(GameObject avatar, VrChatExpressionMenu.Source source)
        {
            var runtime = source.Controller;
            var controller = runtime as AnimatorController;
            var replacements = new Dictionary<AnimationClip, AnimationClip>();
            if (runtime is AnimatorOverrideController overrides)
            {
                controller = overrides.runtimeAnimatorController as AnimatorController;
                var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                overrides.GetOverrides(pairs);
                foreach (var pair in pairs) if (pair.Value != null) replacements[pair.Key] = pair.Value;
            }
            if (controller == null) return;

            var seen = new HashSet<AnimationClip>();
            foreach (var target in Discover(controller))
            {
                var path = target.Path;
                var motion = EffectiveMotion(controller, target.State, target.Layer);
                var clip = motion as AnimationClip;
                if (clip != null && replacements.TryGetValue(clip, out var replacement)) clip = replacement;
                if (clip != null && !seen.Add(clip)) continue;
                if (motion == null) continue;
                // A BlendTree's children are not independent expressions. Never
                // publish its leaves as if their authored mixing did not exist.
                var entry = new VrChatExpressionMenu.Entry
                {
                    Id = "gesture/" + path,
                    Name = "ジェスチャー / " + (clip != null ? clip.name : path)
                };
                try
                {
                    if (clip == null) throw new InvalidOperationException("ジェスチャーのBlendTreeは単体の固定表情アニメーションではないため省略しました。");
                    var bindings = AnimationUtility.GetCurveBindings(clip);
                    if (!bindings.Any(IsMorph)) continue; // Hand/bone motions are not facial expressions.
                    entry.Values.AddRange(ReadPose(avatar, clip));
                }
                catch (InvalidOperationException error) { entry.Error = error.Message; }
                source.Entries.Add(entry);
                if (source.Entries.Count > 512) throw new InvalidOperationException("表情候補が512件を超えています。FXの表情登録を整理してください。");
            }
        }

        private sealed class Target
        {
            internal AnimatorState State;
            internal int Layer;
            internal string Path;
        }

        private static IEnumerable<Target> Discover(AnimatorController controller)
        {
            var layers = controller.layers;
            for (var layerIndex = 0; layerIndex < layers.Length; layerIndex++)
            {
                // Synced layers share their source state machine but can replace
                // each state's motion. Keep the evaluated layer with the target.
                var sourceIndex = layerIndex;
                var chain = new HashSet<int>();
                while (true)
                {
                    if (sourceIndex < 0 || sourceIndex >= layers.Length || !chain.Add(sourceIndex))
                        throw new InvalidOperationException("FXの同期レイヤー参照が不正です。");
                    var parent = layers[sourceIndex].syncedLayerIndex;
                    if (parent < 0) break;
                    sourceIndex = parent;
                }
                var paths = new Dictionary<AnimatorState, string>();
                var targets = new HashSet<AnimatorState>();
                var machines = new List<AnimatorStateMachine>();
                void Index(AnimatorStateMachine machine, string path)
                {
                    machines.Add(machine);
                    foreach (var child in machine.states) paths[child.state] = path + "/" + child.state.name;
                    foreach (var child in machine.stateMachines) Index(child.stateMachine, path + "/" + child.stateMachine.name);
                }
                Index(layers[sourceIndex].stateMachine, layers[layerIndex].name);
                var entering = new HashSet<AnimatorStateMachine>();
                void Enter(AnimatorStateMachine machine)
                {
                    if (!entering.Add(machine)) return;
                    try
                    {
                        var entries = machine.entryTransitions.Where(t => !t.mute).ToArray();
                        if (entries.Any(t => t.solo)) entries = entries.Where(t => t.solo).ToArray();
                        foreach (var entry in entries)
                        {
                            Destination(entry);
                            // An unconditional entry takes precedence over later
                            // entries and over the default state.
                            if (entry.conditions.Length == 0) return;
                        }
                        if (machine.defaultState != null) targets.Add(machine.defaultState);
                    }
                    finally { entering.Remove(machine); }
                }
                void Destination(AnimatorTransitionBase transition)
                {
                    if (transition.destinationState != null) targets.Add(transition.destinationState);
                    if (transition.destinationStateMachine != null) Enter(transition.destinationStateMachine);
                }
                void Inspect(AnimatorTransitionBase transition)
                {
                    if (!transition.mute && transition.conditions.Any(c => IsGesture(c.parameter))) Destination(transition);
                }
                foreach (var machine in machines)
                {
                    foreach (var transition in machine.anyStateTransitions) Inspect(transition);
                    foreach (var transition in machine.entryTransitions) Inspect(transition);
                    foreach (var child in machine.states)
                        foreach (var transition in child.state.transitions) Inspect(transition);
                    foreach (var child in machine.stateMachines)
                        foreach (var transition in machine.GetStateMachineTransitions(child.stateMachine)) Inspect(transition);
                }
                foreach (var state in targets.Where(paths.ContainsKey).OrderBy(s => paths[s], StringComparer.Ordinal))
                    yield return new Target { State = state, Layer = layerIndex, Path = layerIndex + "/" + paths[state] };
            }
        }

        private static Motion EffectiveMotion(AnimatorController controller, AnimatorState state, int layer)
        {
            var motion = controller.GetStateEffectiveMotion(state, layer);
            var parent = controller.layers[layer].syncedLayerIndex;
            return motion != null || parent < 0 ? motion : EffectiveMotion(controller, state, parent);
        }

        internal static List<VrChatExpressionMenu.MorphValue> ReadPose(GameObject avatar, AnimationClip clip)
        {
            if (AnimationUtility.GetObjectReferenceCurveBindings(clip).Length > 0)
                throw new InvalidOperationException("マテリアル・オブジェクトの差し替えを含む表情アニメーションは未対応です。");
            var result = new List<VrChatExpressionMenu.MorphValue>();
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (!IsMorph(binding)) throw new InvalidOperationException("BlendShape以外の変化を含む表情アニメーションです: " + binding.propertyName);
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null) throw new InvalidOperationException("表情アニメーションの曲線を読み取れません: " + clip.name);
                if (!VrChatFixedExpressionCurve.TryRead(curve.keys.Select(k => new VrChatFixedExpressionCurve.Key
                    { Value = k.value, InTangent = k.inTangent, OutTangent = k.outTangent }).ToArray(), out var weight))
                    throw new InvalidOperationException("時間で変わる表情アニメーションは固定表情として取り込めません: " + clip.name);
                var renderer = VrChatExpressionSampler.FindRenderer(avatar, binding.path);
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    throw new InvalidOperationException("非表示のRendererを使う表情アニメーションです: " + binding.path);
                var shape = binding.propertyName.Substring("blendShape.".Length);
                if (renderer.sharedMesh.GetBlendShapeIndex(shape) < 0)
                    throw new InvalidOperationException("表情のBlendShapeがありません: " + binding.path + "/" + shape);
                result.Add(new VrChatExpressionMenu.MorphValue { Path = binding.path, Shape = shape, Weight = weight });
            }
            if (result.Count == 0) throw new InvalidOperationException("顔のBlendShapeを含まないアニメーションです。");
            return result;
        }

        private static bool IsGesture(string name) => name == "GestureLeft" || name == "GestureRight";
        private static bool IsMorph(EditorCurveBinding binding) => binding.type == typeof(SkinnedMeshRenderer) &&
            binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal);
    }
}
