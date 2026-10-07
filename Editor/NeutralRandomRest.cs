using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // A self-contained random idle has no authored fixed phase. Preserve its
    // prepared scalar channels without running its callbacks. This proof is
    // deliberately narrower than general driver/parameter dependency analysis:
    // no written parameter may escape the layer, and no implicit native stream
    // contribution or non-morph output may be removed with it.
    internal static class NeutralRandomRest
    {
        internal static HashSet<int> Preserve(RuntimeAnimatorController runtime, VrChatExpressionMenu.Source metadata,
            NeutralShapePlan plan, Func<string, bool> excludedPath, FixedExpressionContext fixedContext, ICollection<string> warnings = null)
        {
            var result = new HashSet<int>();
            var unknown = new List<string>();
            var layers = ExpressionDependencies.Inspect(runtime, excludedPath,
                new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), unknown, true);
            if (unknown.Count != 0) return result;
            var fixedValues = ExpressionDependencies.FixedNeutralValues(runtime, metadata, layers, excludedPath, fixedContext);
            var reachable = ExpressionDependencies.Inspect(runtime, excludedPath,
                new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), new List<string>(), true, fixedValues);
            var other = new List<ExpressionDependencies.Layer>();
            foreach (var controller in metadata.OtherControllers.Where(value => value != null))
                other.AddRange(ExpressionDependencies.Inspect(controller, null,
                    new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), unknown,
                    typedConditions: true, fxLayerCount: layers.Length));
            if (unknown.Count != 0) return result;
            var original = ExpressionDependencies.Controller(runtime);
            var definitions = original.layers;
            var stationary = ExpressionDependencies.StationaryLayers(runtime, excludedPath);
            bool Dominated(int lower, EditorCurveBinding binding)
            {
                foreach (var pair in stationary.Where(pair => pair.Key > lower))
                {
                    var upper = definitions[pair.Key];
                    if (upper.blendingMode != AnimatorLayerBlendingMode.Override || upper.defaultWeight != 1 || upper.avatarMask != null) continue;
                    var curve = AnimationUtility.GetEditorCurve(pair.Value, binding);
                    if (curve == null || curve.length == 0 || !VrChatExpressionSampler.IsConstant(curve)) continue;
                    VrChatGestureExpressions.ReadCurve(curve);
                    return true;
                }
                return false;
            }
            var types = original.parameters.GroupBy(parameter => parameter.name, StringComparer.Ordinal)
                .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single().type, StringComparer.Ordinal);
            for (var index = 1; index < layers.Length; index++)
            {
                var layer = layers[index]; var definition = definitions[index];
                var active = reachable[index];
                // A dormant random branch cannot erase a constant authored
                // neutral state. Use the same proven fixed inputs as sampling.
                if (!active.DriverPrograms.Any(program => program.Operations.Any(operation => operation.Kind == "Random"))) continue;
                if (definition.syncedLayerIndex >= 0 || definition.avatarMask != null || definition.iKPass ||
                    definition.blendingMode != AnimatorLayerBlendingMode.Override || definition.defaultWeight != 1 ||
                    layer.WriteDefaults || layer.EmptyMotion || layer.FxControl || layer.NonMorphBindings ||
                    layer.CurveWrites.Count != 0 || active.Morphs.Count == 0 || !active.Timed ||
                    layer.DriverPrograms.Count == 0 || layer.DriverPrograms.Any(program => program.Error != null ||
                        program.Operations.Count == 0 || program.Operations.Any(operation => operation.Kind != "Random"))) continue;
                if (layer.Writes.Any(name => !types.TryGetValue(name, out var type) || type == AnimatorControllerParameterType.Trigger ||
                    VrChatParameterDriver.BuiltIn.Contains(name) || metadata.ExternalParameters.Contains(name))) continue;
                // Include every state, including dormant menu alternatives and
                // Copy sources, so an input alias cannot conceal an escape.
                if (layers.Where((item, otherIndex) => otherIndex != index).Concat(other)
                    .Any(item => item.Reads.Overlaps(layer.Writes))) continue;
                if (definitions.Any(item => item.syncedLayerIndex == index)) continue;
                // A nonstationary upper writer may own a constant selected
                // rest or require its own dependency checks. Do not hide those
                // checks by dropping the shared root as a random channel.
                if (Enumerable.Range(index + 1, layers.Length - index - 1).Any(upper =>
                    !stationary.ContainsKey(upper) && reachable[upper].Morphs.Any(binding =>
                        active.Morphs.Contains(binding) && !Dominated(upper, binding)))) continue;
                var changing = false; var safe = true;
                foreach (var clip in layer.Clips)
                {
                    if (AnimationUtility.GetAnimationEvents(clip).Length != 0 ||
                        AnimationUtility.GetObjectReferenceCurveBindings(clip).Any()) { safe = false; break; }
                    foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                    {
                        // Inspect the original clip as a whole. A caller's
                        // exclusion must not hide geometry or parameter effects.
                        if (binding.type != typeof(SkinnedMeshRenderer) ||
                            !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)) { safe = false; break; }
                        var curve = AnimationUtility.GetEditorCurve(clip, binding);
                        VrChatGestureExpressions.ReadCurve(curve);
                        changing |= active.Clips.Contains(clip) && !VrChatExpressionSampler.IsConstant(curve);
                    }
                }
                if (!safe || !changing) continue;
                var preserved = active.Morphs.Where(binding => plan.CommittedMorphs.Contains(binding) && !Dominated(index, binding))
                    .OrderBy(binding => binding.path, StringComparer.Ordinal).ThenBy(binding => binding.propertyName, StringComparer.Ordinal).ToArray();
                foreach (var binding in preserved)
                    plan.PreserveTemporalRest(layer.Clips.First(clip => AnimationUtility.GetCurveBindings(clip).Contains(binding)), binding);
                if (preserved.Length > 0)
                {
                    var warning = ExporterLocalization.T("Randomを使うFXの待機表情は固定できないため、書き出し用コピーの現在のBlendShapeを保持しました: ") +
                        definition.name + " / " + string.Join(", ", preserved.Take(8).Select(binding => binding.path + " / " + binding.propertyName)) +
                        (preserved.Length > 8 ? " (" + preserved.Length + ")" : "");
                    if (warnings != null && !warnings.Contains(warning)) warnings.Add(warning);
                }
                result.Add(index);
            }
            return result;
        }
    }
}
