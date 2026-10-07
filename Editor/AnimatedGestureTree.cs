using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter
{
    // An animated hand tree is a composed temporal program. Native coefficients
    // choose its authored curves; native phase witnesses validate the analytic
    // combination and its scalar response through the original support graph.
    internal static class AnimatedGestureTree
    {
        internal static void Read(GameObject avatar, VrChatExpressionMenu.Source source, int layer, AnimatorState state,
            VrChatExpressionMenu.Entry entry, Func<string, bool> excludedPath, IDictionary<string, float> arrivalInputs = null)
        {
            var controller = ExpressionDependencies.Controller(source.Controller);
            var motion = controller.GetStateEffectiveMotion(state, layer) ?? state.motion;
            var overrides = ExpressionDependencies.Overrides(source.Controller);
            var leaves = new HashSet<AnimationClip>(); var stack = new HashSet<Motion>();
            void Collect(Motion current)
            {
                if (current == null || !stack.Add(current) || stack.Count > 16)
                    throw new InvalidOperationException("BlendTreeのアニメーション階層を安全に確定できません。");
                try
                {
                    if (current is AnimationClip clip)
                    {
                        if (overrides.TryGetValue(clip, out var replacement)) clip = replacement;
                        SelectedExpressionAppearance.ValidateClipData(clip, excludedPath);
                        if (AnimationUtility.GetAnimationEvents(clip).Length != 0 || AnimationUtility.GetObjectReferenceCurveBindings(clip).Length != 0 ||
                            AnimationUtility.GetCurveBindings(clip).Any(binding => binding.type != typeof(SkinnedMeshRenderer) ||
                                !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)))
                            throw new InvalidOperationException("BlendTreeの合成アニメーションはBlendShapeだけを持つ子Clipが必要です: " + clip.name);
                        leaves.Add(clip); if (leaves.Count > 256) throw new InvalidOperationException("BlendTreeの子Clipが多すぎます。");
                    }
                    else if (current is BlendTree tree)
                    {
                        foreach (var child in tree.children)
                        {
                            if (child.timeScale != 1 || child.cycleOffset != 0 || child.mirror)
                                throw new InvalidOperationException("BlendTreeの子Clipの速度・位相・Mirrorを同一の時間軸に確定できません: " + tree.name);
                            Collect(child.motion);
                        }
                    }
                    else throw new InvalidOperationException("未対応のAnimator Motionです。");
                }
                finally { stack.Remove(current); }
            }
            Collect(motion);
            var bindings = new HashSet<EditorCurveBinding>(leaves.SelectMany(AnimationUtility.GetCurveBindings)
                .Where(binding => excludedPath?.Invoke(binding.path) != true));
            if (bindings.Count == 0) throw new InvalidOperationException("BlendTreeに顔のBlendShape曲線がありません。");
            VrChatExpressionMenu.Entry Scalar(float weight)
            {
                var probe = new VrChatExpressionMenu.Entry();
                foreach (var binding in bindings)
                    probe.Values.Add(new VrChatExpressionMenu.MorphValue { Path = binding.path,
                        Shape = binding.propertyName.Substring("blendShape.".Length), Weight = weight });
                VrChatExpressionSampler.ApplyPermanentOverrides(avatar, source.Controller, probe, layer, state.writeDefaultValues,
                    excludedPath, source, state, nativeInputs: entry.Parameters);
                return probe;
            }
            var zero = Scalar(0).Values.ToDictionary(value => EditorCurveBinding.FloatCurve(value.Path, typeof(SkinnedMeshRenderer), "blendShape." + value.Shape));
            var full = Scalar(100).Values.ToDictionary(value => EditorCurveBinding.FloatCurve(value.Path, typeof(SkinnedMeshRenderer), "blendShape." + value.Shape));
            if (!zero.Keys.ToHashSet().SetEquals(full.Keys)) throw new InvalidOperationException("BlendTreeの合成と常時FXの影響範囲を確定できません。");
            var retained = new HashSet<EditorCurveBinding>(zero.Keys);
            var phases = new SortedSet<float> { 0, .25f, .5f, .75f, 1 };
            foreach (var clip in leaves)
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    VrChatExpressionSampler.ValidateNativeParameterCurve(curve, clip.name + " / " + binding.propertyName);
                    if (curve == null || curve.length == 0) throw new InvalidOperationException("BlendTreeの子曲線を読み取れません: " + clip.name);
                    if (curve.keys.Any(key => key.weightedMode != WeightedMode.None))
                        throw new InvalidOperationException("BlendTreeの加重接線の合成アニメーションは再現できません: " + clip.name);
                    if (!VrChatExpressionSampler.IsConstant(curve))
                    {
                        if (clip.length <= 0 || !Finite(clip.length)) throw new InvalidOperationException("BlendTreeの子アニメーションの長さが不正です。");
                        foreach (var key in curve.keys) phases.Add(key.time / clip.length);
                    }
                }
            if (phases.Any(phase => phase < 0 || phase > 1) || phases.Count > 256)
                throw new InvalidOperationException("BlendTreeのアニメーション位相を安全に確定できません。");
            var boundaries = phases.ToArray();
            for (var index = 1; index < boundaries.Length; index++) phases.Add((boundaries[index - 1] + boundaries[index]) * .5f);
            var native = NativeTreeTrajectory.Probe(avatar, source, layer, state, entry.Parameters, retained, phases.ToArray(), excludedPath, arrivalInputs);
            foreach (var binding in native.InheritedMorphs)
                if (native.Samples.Values.Any(sample => !sample.TryGetValue(binding, out var value) || Math.Abs(value - native.Initial[binding]) > .02f))
                    throw new InvalidOperationException("BlendTree inherited facial channel is not a constant held contribution: " + binding.path + " / " + binding.propertyName);
            if (Math.Abs(native.Coefficients.Values.Sum() - 1) > .0001f)
                throw new InvalidOperationException("BlendTreeの合成係数が凸結合ではありません。");
            var active = native.Coefficients.Where(pair => pair.Value > .000001f).ToArray();
            if (active.Any(pair => !leaves.Contains(pair.Key))) throw new InvalidOperationException("BlendTreeの実際の子Clipを確定できません。");
            var changing = active.Where(pair => AnimationUtility.GetCurveBindings(pair.Key).Any(binding =>
                !VrChatExpressionSampler.IsConstant(AnimationUtility.GetEditorCurve(pair.Key, binding)))).ToArray();
            if (changing.Select(pair => pair.Key.length).Distinct().Count() > 1)
                throw new InvalidOperationException("BlendTreeの動く子Clipの長さ・Loopが一致しません。");
            // ClipInfo weights describe whole clips. When an active leaf has
            // no curve for a changing binding, Unity can normalize that
            // binding differently. Prove its actual coefficients in the same
            // native graph, changing only the selected cloned tree's curves.
            // Constant sparse resets already have their native initial value
            // and do not need a coefficient to reconstruct a zero delta.
            var sparse = new HashSet<EditorCurveBinding>(bindings.Where(retained.Contains).Where(binding =>
                active.Any(pair => AnimationUtility.GetEditorCurve(pair.Key, binding) == null) &&
                active.Any(pair => AnimationUtility.GetEditorCurve(pair.Key, binding) is AnimationCurve curve &&
                    !VrChatExpressionSampler.IsConstant(curve))));
            var bindingCoefficients = sparse.Count == 0 ? new Dictionary<EditorCurveBinding, Dictionary<AnimationClip, float>>() :
                NativeTreeTrajectory.CalibrateBindingCoefficients(avatar, source, layer, state, entry.Parameters, native, sparse,
                    zero.ToDictionary(pair => pair.Key, pair => pair.Value.Weight),
                    full.ToDictionary(pair => pair.Key, pair => pair.Value.Weight), excludedPath, arrivalInputs);
            if (native.Duration == 0 && changing.Length == 0)
            {
                // These are original full-graph final values, already including
                // the selected callbacks and support. Project the native held
                // pose through the same facial ownership path, without applying
                // the support graph a second time or publishing a detached leaf.
                var held = new AnimationClip { name = entry.Name + " / native held pose", hideFlags = HideFlags.HideAndDontSave };
                try
                {
                    foreach (var pair in native.Initial)
                        AnimationUtility.SetEditorCurve(held, pair.Key, new AnimationCurve(new Keyframe(0, pair.Value)));
                    VrChatGestureExpressions.ReadClip(avatar, held, entry, excludedPath, source);
                    foreach (var value in native.Initial)
                    {
                        var shape = value.Key.propertyName.Substring("blendShape.".Length);
                        var actual = entry.Values.SingleOrDefault(channel => channel.Path == value.Key.path && channel.Shape == shape);
                        if (actual == null || Math.Abs(actual.Weight - value.Value) > .02)
                            throw new InvalidOperationException("BlendTreeの固定表情が元のUnity保持状態と一致しません: " + value.Key.path + " / " + shape);
                    }
                    entry.Messages.Add("元のFXの到達経路と保持状態を確認したBlendTreeの固定表情を保存しました。");
                    return;
                }
                finally { Object.DestroyImmediate(held); }
            }
            if (!Finite(native.Duration) || native.Duration <= 0 || native.Duration > 600)
                throw new InvalidOperationException("BlendTreeの合成アニメーションの長さが不正です。");
            var composed = new AnimationClip { name = entry.Name + " / native composition", hideFlags = HideFlags.HideAndDontSave };
            try
            {
                foreach (var binding in bindings.Where(retained.Contains))
                {
                    var scale = (full[binding].Weight - zero[binding].Weight) / 100f;
                    var initial = native.Initial[binding];
                    var terms = active.Select(pair => (Clip: pair.Key, Weight: bindingCoefficients.TryGetValue(binding, out var calibrated) ?
                        calibrated[pair.Key] : pair.Value, Curve: AnimationUtility.GetEditorCurve(pair.Key, binding)))
                        .Where(term => term.Curve != null).ToArray();
                    if (Math.Abs(scale) < .000001f && Math.Abs(initial - zero[binding].Weight) > .01f)
                        throw new InvalidOperationException("BlendTreeの常時FXによる上書きを分離できません: " + binding.propertyName);
                    var rawInitial = Math.Abs(scale) < .000001f ? 0 : (initial - zero[binding].Weight) / scale;
                    var times = new SortedSet<float> { 0, 1 };
                    foreach (var term in terms.Where(term => !VrChatExpressionSampler.IsConstant(term.Curve)))
                        foreach (var key in term.Curve.keys) times.Add(key.time / term.Clip.length);
                    var ordered = times.ToArray(); var keys = new Keyframe[ordered.Length];
                    for (var index = 0; index < ordered.Length; index++)
                    {
                        var phase = ordered[index];
                        var value = rawInitial + terms.Sum(term => term.Weight *
                            (term.Curve.Evaluate(phase * term.Clip.length) - term.Curve.Evaluate(0)));
                        float Tangent(bool incoming)
                        {
                            var values = terms.Select(term => term.Weight * Derivative(term.Curve, phase * term.Clip.length, incoming) * term.Clip.length / native.Duration).ToArray();
                            if (values.Any(float.IsInfinity))
                            {
                                if (values.Any(tangent => !float.IsInfinity(tangent) && tangent != 0))
                                    throw new InvalidOperationException("BlendTreeのStepと連続曲線の合成を再現できません: " + binding.propertyName);
                                return float.PositiveInfinity;
                            }
                            return values.Sum();
                        }
                        keys[index] = new Keyframe(phase * native.Duration, value, Tangent(true), Tangent(false));
                    }
                    AnimationUtility.SetEditorCurve(composed, binding, new AnimationCurve(keys));
                }
                var settings = AnimationUtility.GetAnimationClipSettings(composed); settings.loopTime = native.Loop;
                AnimationUtility.SetAnimationClipSettings(composed, settings);
                VrChatGestureExpressions.ReadClip(avatar, composed, entry, excludedPath, source);
                VrChatExpressionSampler.ApplyPermanentOverrides(avatar, source.Controller, entry, layer, state.writeDefaultValues,
                    excludedPath, source, state, nativeInputs: entry.Parameters);
                // Inherited channels are already complete original native
                // values. The selected tree has no curve for them; append their
                // proven constant contribution after scalar transformation so
                // permanent/default support cannot be applied a second time.
                foreach (var binding in native.InheritedMorphs)
                {
                    var shape = binding.propertyName.Substring("blendShape.".Length);
                    var value = entry.Values.SingleOrDefault(channel => channel.Path == binding.path && channel.Shape == shape);
                    if (value == null) entry.Values.Add(new VrChatExpressionMenu.MorphValue {
                        Path = binding.path, Shape = shape, Weight = native.Initial[binding] });
                    else value.Weight = native.Initial[binding];
                    entry.Animation.RemoveAll(channel => channel.Path == binding.path && channel.Shape == shape);
                }
                foreach (var sample in native.Samples)
                    foreach (var value in sample.Value)
                    {
                        var shape = value.Key.propertyName.Substring("blendShape.".Length);
                        var animation = entry.Animation.SingleOrDefault(channel => channel.Path == value.Key.path && channel.Shape == shape);
                        var actual = animation == null ? entry.Values.Single(channel => channel.Path == value.Key.path && channel.Shape == shape).Weight :
                            animation.Curve.Evaluate(native.SamplePhases[sample.Key] * native.Duration);
                        if (Math.Abs(actual - value.Value) > .02)
                            throw new InvalidOperationException("BlendTreeの合成アニメーションが元のUnity動作と一致しません: " + value.Key.path + " / " + shape);
                    }
                entry.Messages.Add("BlendTreeを元のUnityの合成係数・位相・常時FXで検証した表情アニメーションとして保存しました。");
            }
            finally { Object.DestroyImmediate(composed); }
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        static float Derivative(AnimationCurve curve, float time, bool incoming)
        {
            if (VrChatExpressionSampler.IsConstant(curve)) return 0;
            var keys = curve.keys;
            for (var index = 0; index < keys.Length; index++)
                if (Math.Abs(time - keys[index].time) < .000001f)
                    return incoming ? index == 0 ? 0 : keys[index].inTangent : index + 1 == keys.Length ? 0 : keys[index].outTangent;
            if (time < keys[0].time || time > keys[keys.Length - 1].time) return 0;
            for (var index = 1; index < keys.Length; index++)
            {
                var left = keys[index - 1]; var right = keys[index];
                if (time > right.time) continue;
                if (float.IsInfinity(left.outTangent) || float.IsInfinity(right.inTangent)) return float.PositiveInfinity;
                var width = right.time - left.time; var t = (time - left.time) / width;
                return (6 * t * t - 6 * t) * (left.value - right.value) / width +
                    (3 * t * t - 4 * t + 1) * left.outTangent + (3 * t * t - 2 * t) * right.inTangent;
            }
            return 0;
        }
    }
}
