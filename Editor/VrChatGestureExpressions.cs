using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRVlog.Expressions;

namespace VRVlog.LilToonExporter
{
    // A gesture's authored AnimationClip is a composed face in its own
    // right. Read that clip, not every mesh morph and not an arbitrary frame of
    // the live FX controller (whose independent blink can keep running forever).
    // Retain the exact authored state so its deterministic callbacks and native
    // layer contributions are evaluated together with the selected face.
    internal static class VrChatGestureExpressions
    {
        internal static void Add(GameObject avatar, VrChatExpressionMenu.Source source, Func<string, bool> excludedPath = null)
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

            var seen = new Dictionary<AnimationClip, List<VrChatExpressionMenu.Entry>>();
            foreach (var target in Discover(controller))
            {
                var path = target.Path;
                var motion = EffectiveMotion(controller, target.State, target.Layer);
                var clip = motion as AnimationClip;
                if (clip != null && replacements.TryGetValue(clip, out var replacement)) clip = replacement;
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
                    var bindings = AnimationUtility.GetCurveBindings(clip).Where(b => excludedPath?.Invoke(b.path) != true).ToArray();
                    if (!bindings.Any(IsMorph)) continue; // Hand/bone motions are not facial expressions.
                    ReadClip(avatar, clip, entry, excludedPath, source);
                    VrChatExpressionSampler.ApplyPermanentOverrides(avatar, runtime, entry, target.Layer, target.State.writeDefaultValues,
                        excludedPath, metadata: source, sourceState: target.State);
                }
                catch (InvalidOperationException error) { entry.Error = error.Message; }
                // Sharing a clip does not establish the same expression:
                // effective state callbacks and native layer blending can
                // change its result. Evaluate every registration first, keep
                // distinct outcomes/errors, and merge only proven duplicates.
                if (clip != null && entry.Error == null)
                {
                    if (!seen.TryGetValue(clip, out var outcomes))
                        seen.Add(clip, outcomes = new List<VrChatExpressionMenu.Entry>());
                    if (outcomes.Any(previous => SameOutcome(previous, entry))) continue;
                    outcomes.Add(entry);
                }
                source.Entries.Add(entry);
                if (source.Entries.Count > 512) throw new InvalidOperationException("表情候補が512件を超えています。FXの表情登録を整理してください。");
            }
        }

        private static bool SameOutcome(VrChatExpressionMenu.Entry first, VrChatExpressionMenu.Entry second)
        {
            if (first.Error != null || second.Error != null || !first.Duration.Equals(second.Duration) || first.Loop != second.Loop ||
                first.ControlType != second.ControlType || first.ControlParameter != second.ControlParameter ||
                first.Parameters.Count != second.Parameters.Count || first.Animation.Count != second.Animation.Count ||
                !first.Messages.SequenceEqual(second.Messages) || !SameMorphs(first.Values, second.Values) ||
                !SameMorphs(first.Unevaluated, second.Unevaluated)) return false;
            if (first.Parameters.Any(pair => !second.Parameters.TryGetValue(pair.Key, out var value) || !pair.Value.Equals(value))) return false;
            var a = first.Animation.OrderBy(value => value.Path, StringComparer.Ordinal).ThenBy(value => value.Shape, StringComparer.Ordinal).ToArray();
            var b = second.Animation.OrderBy(value => value.Path, StringComparer.Ordinal).ThenBy(value => value.Shape, StringComparer.Ordinal).ToArray();
            for (var index = 0; index < a.Length; index++)
                if (a[index].Path != b[index].Path || a[index].Shape != b[index].Shape ||
                    !VrChatExpressionBaker.SameCurve(a[index].Curve, b[index].Curve)) return false;
            return true;
        }

        private static bool SameMorphs(IList<VrChatExpressionMenu.MorphValue> first, IList<VrChatExpressionMenu.MorphValue> second)
        {
            if (first.Count != second.Count) return false;
            var a = first.OrderBy(value => value.Path, StringComparer.Ordinal).ThenBy(value => value.Shape, StringComparer.Ordinal).ToArray();
            var b = second.OrderBy(value => value.Path, StringComparer.Ordinal).ThenBy(value => value.Shape, StringComparer.Ordinal).ToArray();
            for (var index = 0; index < a.Length; index++)
                if (a[index].Path != b[index].Path || a[index].Shape != b[index].Shape || !a[index].Weight.Equals(b[index].Weight)) return false;
            return true;
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
                var parents = new Dictionary<AnimatorStateMachine, AnimatorStateMachine>();
                void Index(AnimatorStateMachine machine, string path, AnimatorStateMachine parent)
                {
                    machines.Add(machine);
                    parents[machine] = parent;
                    foreach (var child in machine.states) paths[child.state] = path + "/" + child.state.name;
                    foreach (var child in machine.stateMachines) Index(child.stateMachine, path + "/" + child.stateMachine.name, machine);
                }
                Index(layers[sourceIndex].stateMachine, layers[layerIndex].name, null);
                var entering = new HashSet<AnimatorStateMachine>();
                var exiting = new HashSet<AnimatorStateMachine>();
                void Enter(AnimatorStateMachine machine)
                {
                    if (!entering.Add(machine)) return;
                    try
                    {
                        foreach (var entry in EnabledTransitions(machine.entryTransitions))
                        {
                            Destination(entry, machine);
                            // An unconditional entry takes precedence over later
                            // entries and over the default state.
                            if (entry.conditions.Length == 0) return;
                        }
                        if (machine.defaultState != null) targets.Add(machine.defaultState);
                    }
                    finally { entering.Remove(machine); }
                }
                void Exit(AnimatorStateMachine machine)
                {
                    if (!parents.TryGetValue(machine, out var parent) || parent == null || !exiting.Add(machine)) return;
                    try
                    {
                        foreach (var transition in EnabledTransitions(parent.GetStateMachineTransitions(machine)))
                        {
                            Destination(transition, parent);
                            if (transition.conditions.Length == 0) break;
                        }
                    }
                    finally { exiting.Remove(machine); }
                }
                void Destination(AnimatorTransitionBase transition, AnimatorStateMachine owner)
                {
                    if (transition.isExit) { Exit(owner); return; }
                    if (transition.destinationState != null) targets.Add(transition.destinationState);
                    if (transition.destinationStateMachine != null) Enter(transition.destinationStateMachine);
                }
                void Inspect(AnimatorTransitionBase transition, AnimatorStateMachine owner)
                {
                    if (transition.conditions.Any(c => IsGesture(c.parameter))) Destination(transition, owner);
                }
                foreach (var machine in machines)
                {
                    foreach (var transition in EnabledTransitions(machine.anyStateTransitions)) Inspect(transition, machine);
                    foreach (var transition in EnabledTransitions(machine.entryTransitions)) Inspect(transition, machine);
                    foreach (var child in machine.states)
                        foreach (var transition in EnabledTransitions(child.state.transitions)) Inspect(transition, machine);
                    foreach (var child in machine.stateMachines)
                        foreach (var transition in EnabledTransitions(machine.GetStateMachineTransitions(child.stateMachine))) Inspect(transition, machine);
                }
                foreach (var state in targets.Where(paths.ContainsKey).OrderBy(s => paths[s], StringComparer.Ordinal))
                    yield return new Target { State = state, Layer = layerIndex, Path = layerIndex + "/" + paths[state] };
            }
        }

        private static IEnumerable<T> EnabledTransitions<T>(IEnumerable<T> transitions) where T : AnimatorTransitionBase
        {
            var siblings = transitions.ToArray();
            var solo = siblings.Any(t => t.solo);
            return siblings.Where(t => !t.mute && (!solo || t.solo));
        }

        private static Motion EffectiveMotion(AnimatorController controller, AnimatorState state, int layer)
        {
            var motion = controller.GetStateEffectiveMotion(state, layer);
            var parent = controller.layers[layer].syncedLayerIndex;
            return motion != null || parent < 0 ? motion : EffectiveMotion(controller, state, parent);
        }

        internal static List<VrChatExpressionMenu.MorphValue> ReadPose(GameObject avatar, AnimationClip clip, Func<string, bool> excludedPath = null,
            VrChatExpressionMenu.Entry entry = null, VrChatExpressionMenu.Source metadata = null)
        {
            SelectedExpressionAppearance.ValidateClipData(clip, excludedPath);
            var projection = FacialProjectionScope.NeedsProjection(avatar, new[] { clip }, excludedPath) ? FacialProjectionScope.For(avatar, metadata) : null;
            var plan = projection == null ? null : NeutralShapePlan.CreateProjection(avatar, projection.Morphs, new[] { clip }, excludedPath);
            if (AnimationUtility.GetObjectReferenceCurveBindings(clip).Any(b => excludedPath?.Invoke(b.path) != true && plan?.AllowsEvaluationBinding(clip, b) != true))
                throw new InvalidOperationException("マテリアル・オブジェクトの差し替えを含む表情アニメーションは未対応です。");
            if (projection != null)
            {
                projection.WarnOmitted(AnimationUtility.GetCurveBindings(clip).Where(IsMorph), entry?.Messages);
                if (entry != null) entry.UsesFacialProjection = true;
            }
            var result = new List<VrChatExpressionMenu.MorphValue>();
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (excludedPath?.Invoke(binding.path) == true) continue;
                if (!IsMorph(binding))
                {
                    if (SelectedExpressionAppearance.IsUnchanged(avatar, clip, binding)) continue;
                    if (plan?.AllowsEvaluationBinding(clip, binding) == true) continue;
                    throw new InvalidOperationException("BlendShape以外の変化を含む表情アニメーションです: " + binding.propertyName);
                }
                if (projection != null && !projection.Morphs.Contains(binding)) continue;
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null) throw new InvalidOperationException("表情アニメーションの曲線を読み取れません: " + clip.name);
                // The standard VRM expression is the first pose. The animated
                // channels are exported separately and replayed by VR Vlog.
                var weight = (float)ReadCurve(curve).Evaluate(0);
                var renderer = VrChatExpressionSampler.FindRenderer(avatar, binding.path);
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    throw new InvalidOperationException("非表示のRendererを使う表情アニメーションです: " + binding.path);
                var shape = binding.propertyName.Substring("blendShape.".Length);
                // Keep authored values, even for unresolved morphs, until the
                // prepared mesh is available. Preparation may create that morph.
                result.Add(new VrChatExpressionMenu.MorphValue { Path = binding.path, Shape = shape, Weight = weight });
            }
            if (result.Count == 0) throw new InvalidOperationException("顔のBlendShapeを含まないアニメーションです。");
            return result;
        }

        // Both gesture and FaceEmo clips use this path. Values and animation
        // channels are later resolved/pruned together by PreparedExpressionBindings.
        internal static void ReadClip(GameObject avatar, AnimationClip clip, VrChatExpressionMenu.Entry entry,
            Func<string, bool> excludedPath = null, VrChatExpressionMenu.Source metadata = null)
        {
            var values = ReadPose(avatar, clip, excludedPath, entry, metadata);
            var animated = new List<VrChatExpressionMenu.AnimatedMorph>();
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (excludedPath?.Invoke(binding.path) == true || !IsMorph(binding)) continue;
                if (!values.Any(value => value.Path == binding.path && "blendShape." + value.Shape == binding.propertyName)) continue;
                var curve = ReadCurve(AnimationUtility.GetEditorCurve(clip, binding));
                curve.Range(out var minimum, out var maximum);
                if (minimum == maximum) continue;
                animated.Add(new VrChatExpressionMenu.AnimatedMorph
                {
                    Path = binding.path, Shape = binding.propertyName.Substring("blendShape.".Length), Curve = curve
                });
            }
            if (animated.Count > 0 && (clip.length <= 0 || clip.length > 600))
                throw new InvalidOperationException("表情アニメーションの長さは0秒より長く600秒以下である必要があります: " + clip.name);
            entry.Values.AddRange(values);
            entry.Animation.AddRange(animated);
            entry.Duration = animated.Count > 0 ? clip.length : 0;
            entry.Loop = animated.Count > 0 && clip.isLooping;
        }

        internal static ExpressionAnimationData.Curve ReadCurve(AnimationCurve curve)
        {
            if (curve == null) throw new InvalidOperationException("表情アニメーションの曲線を読み取れません。");
            string Wrap(WrapMode mode) => mode == WrapMode.Loop ? "loop" : mode == WrapMode.PingPong ? "pingPong" : "clamp";
            var result = new ExpressionAnimationData.Curve { PreWrap = Wrap(curve.preWrapMode), PostWrap = Wrap(curve.postWrapMode) };
            foreach (var k in curve.keys)
                result.Keys.Add(new ExpressionAnimationData.Key
                {
                    Time = k.time, Value = k.value,
                    InTangent = float.IsInfinity(k.inTangent) ? (double?)null : k.inTangent,
                    OutTangent = float.IsInfinity(k.outTangent) ? (double?)null : k.outTangent,
                    InWeight = (k.weightedMode & WeightedMode.In) != 0 ? k.inWeight : 1.0 / 3,
                    OutWeight = (k.weightedMode & WeightedMode.Out) != 0 ? k.outWeight : 1.0 / 3
                });
            result.Validate();
            return result;
        }

        private static bool IsGesture(string name) => name == "GestureLeft" || name == "GestureRight";
        private static bool IsMorph(EditorCurveBinding binding) => binding.type == typeof(SkinnedMeshRenderer) &&
            binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal);
    }
}
