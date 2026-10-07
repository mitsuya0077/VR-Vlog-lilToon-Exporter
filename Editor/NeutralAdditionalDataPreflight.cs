using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // A known but unsupported weight command can retain prepared neutral rest.
    // That capability limit must not hide malformed reachable data in another
    // playable. Use the same normal-input reachability proof as dependency
    // analysis, without executing its callbacks or resolving its scene bindings.
    internal static class NeutralAdditionalDataPreflight
    {
        internal static void Validate(RuntimeAnimatorController runtime, VrChatExpressionMenu.Source source,
            FixedExpressionContext fixedContext, NeutralShapePlan plan, Func<string, bool> excludedPath)
        {
            var others = ExpressionDependencies.InspectNeutralOtherControllers(runtime, source, fixedContext);
            if (others.Count == 0) return;

            var raw = ExpressionDependencies.Inspect(runtime, excludedPath,
                new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), new List<string>(), true);
            var fixedValues = ExpressionDependencies.FixedNeutralValues(runtime, source, raw, excludedPath, fixedContext);
            var fx = ExpressionDependencies.Inspect(runtime, excludedPath,
                new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), new List<string>(), true, fixedValues);
            // Either playable can write a parameter read only by the other.
            // Include every reachable reader when checking both sets of drivers.
            var needed = new HashSet<string>(fx.SelectMany(layer => layer.Reads)
                .Concat(others.SelectMany(other => other.Layers).SelectMany(layer => layer.Reads)), StringComparer.Ordinal);

            void CheckPrograms(string context, RuntimeAnimatorController current, IEnumerable<ExpressionDependencies.Layer> layers)
            {
                var types = new Dictionary<string, AnimatorControllerParameterType>(StringComparer.Ordinal);
                foreach (var parameter in ExpressionDependencies.Controller(current).parameters)
                {
                    if (parameter == null || string.IsNullOrEmpty(parameter.name) || types.ContainsKey(parameter.name) ||
                        parameter.type != AnimatorControllerParameterType.Bool && parameter.type != AnimatorControllerParameterType.Int &&
                        parameter.type != AnimatorControllerParameterType.Float && parameter.type != AnimatorControllerParameterType.Trigger)
                        throw new InvalidOperationException(context + " / Invalid or duplicate Animator parameter declaration.");
                    types.Add(parameter.name, parameter.type);
                }
                foreach (var program in layers.SelectMany(layer => layer.DriverPrograms).Distinct())
                    try { VrChatParameterDriver.ValidateTargets(program, types, needed); }
                    catch (InvalidOperationException error) { throw new InvalidOperationException(context + " / " + error.Message, error); }
            }
            CheckPrograms("FX / " + runtime.name, runtime, fx);
            foreach (var other in others)
            {
                var context = "Additional Playable / " + other.Runtime.name;
                CheckPrograms(context, other.Runtime, other.Layers);
                foreach (var clip in other.Layers.SelectMany(layer => layer.Clips).Where(clip => clip != null).Distinct())
                {
                    var clipContext = context + " / " + clip.name;
                    if (AnimationUtility.GetAnimationEvents(clip).Length != 0)
                        throw new InvalidOperationException(ExporterLocalization.T(
                            "追加PlayableにAnimationEventがあるため、表情への影響範囲を確定できません: ") + clipContext + " / AnimationEvent");
                    // These clips belong to another playable, not the neutral
                    // probe. Inspect all numeric data without applying the
                    // plan's binding permissions or mesh-export exclusions.
                    foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                        VrChatExpressionSampler.ValidateNativeParameterCurve(AnimationUtility.GetEditorCurve(clip, binding),
                            clipContext + " / " + binding.path + " / " + binding.propertyName);
                    foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    {
                        var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                        for (var index = 0; index < keys.Length; index++)
                            if (!NeutralShapeSnapshot.Finite(keys[index].time) || index > 0 && keys[index].time <= keys[index - 1].time)
                                throw new InvalidOperationException(ExporterLocalization.T("追加Playableのオブジェクト参照曲線の時刻が不正です: ") +
                                    clipContext + " / " + binding.path + " / " + binding.propertyName);
                    }
                }
            }
        }
    }
}
