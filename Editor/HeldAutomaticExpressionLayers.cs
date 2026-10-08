using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // A selected authored expression holds its explicit automatic channels.
    // Suspend only a complete autonomous component, including its SDK driver
    // readers. This is an expression policy; it does not approximate Random or
    // establish a fixed phase of the original native automatic animation.
    internal static class HeldAutomaticExpressionLayers
    {
        // A constant idle/reset clip can promote an unowned Random channel to
        // a stationary scalar root. Reuse the neutral rest proof rather than
        // executing that unrelated automatic program in a selected face probe.
        // Its complete authored output must remain disjoint from the selected
        // expression, and every incoming/escaping effect is still inspected.
        internal static HashSet<int> FindUnownedRandomRest(GameObject avatar, RuntimeAnimatorController runtime,
            VrChatExpressionMenu.Source source, ISet<EditorCurveBinding> selectedMorphs,
            FixedExpressionContext context, out HashSet<EditorCurveBinding> preservedMorphs)
        {
            preservedMorphs = new HashSet<EditorCurveBinding>();
            var result = new HashSet<int>();
            var unknown = new List<string>();
            var layers = ExpressionDependencies.Inspect(runtime, null,
                new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), unknown, true);
            var others = new List<ExpressionDependencies.Layer>();
            foreach (var other in source.OtherControllers.Where(value => value != null))
                others.AddRange(ExpressionDependencies.Inspect(other, null,
                    new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), unknown,
                    typedConditions: true, fxLayerCount: layers.Length));
            if (unknown.Count != 0) return result;
            var types = ExpressionDependencies.Controller(runtime).parameters.GroupBy(parameter => parameter.name, StringComparer.Ordinal)
                .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single().type, StringComparer.Ordinal);
            // An empty output plan asks only whether the existing strict rest
            // proof permits omission. It cannot alter the actual scalar plan.
            var proof = NeutralShapePlan.CreateProjection(avatar, Array.Empty<EditorCurveBinding>(), Array.Empty<AnimationClip>());
            foreach (var index in NeutralRandomRest.Preserve(runtime, source, proof, null, context))
            {
                var layer = layers[index];
                if (layer.Morphs.Overlaps(selectedMorphs) || layer.WeightControls.Count != 0) continue;
                var needed = new HashSet<string>(layer.Reads, StringComparer.Ordinal); needed.UnionWith(layer.Writes);
                // An incoming write to a Random-owned destination joins its
                // autonomous program. Additional playables remain unresolved
                // even when they write only an input gate. FX gate producers,
                // however, stay in the native graph: their known typed SDK
                // operations are not replaced with fixed input values.
                if (others.Any(item => item.Writes.Overlaps(needed)) ||
                    layers.Where((item, otherIndex) => otherIndex != index).Any(item => item.Writes.Overlaps(layer.Writes))) continue;
                if (layers.Concat(others).Any(item => item.FxControl || item.WeightControls.Values.Any(control =>
                    control.Playable == "FX" && (!control.AnimatorLayer || control.LayerIndex == index)))) continue;
                foreach (var program in layers.SelectMany(item => item.DriverPrograms))
                    VrChatParameterDriver.ValidateTargets(program, types, needed);
                var gates = new HashSet<string>(layer.Reads, StringComparer.Ordinal); gates.ExceptWith(layer.Writes);
                var producers = layers.Where((item, otherIndex) => otherIndex != index && item.Writes.Overlaps(gates)).ToArray();
                if (producers.Any(item => item.CurveWrites.Overlaps(gates) || item.DriverPrograms.SelectMany(program => program.Operations)
                    .Any(operation => gates.Contains(operation.Destination) &&
                        (operation.UnresolvedRandom || operation.Kind != "Set" && operation.Kind != "Add" && operation.Kind != "Copy")))) continue;
                result.Add(index); preservedMorphs.UnionWith(layer.Morphs);
            }
            return result;
        }

        internal static HashSet<int> Find(RuntimeAnimatorController runtime, VrChatExpressionMenu.Source source,
            ISet<EditorCurveBinding> automatic, int selectedLayer, ISet<EditorCurveBinding> selectedMorphs)
        {
            var result = new HashSet<int>();
            if (automatic == null || automatic.Count == 0 || !selectedMorphs.Overlaps(automatic)) return result;
            var controller = ExpressionDependencies.Controller(runtime); var definitions = controller.layers;
            var unknown = new List<string>();
            var layers = ExpressionDependencies.Inspect(runtime, null,
                new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), unknown, true);
            var others = new List<ExpressionDependencies.Layer>();
            foreach (var other in source.OtherControllers.Where(value => value != null))
                others.AddRange(ExpressionDependencies.Inspect(other, null,
                    new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), unknown,
                    typedConditions: true, fxLayerCount: definitions.Length));
            // The ordinary sampler retains its location-rich unknown callback
            // diagnostic. Never suspend a component when its effect is unknown.
            if (unknown.Count != 0) return result;
            var types = controller.parameters.GroupBy(parameter => parameter.name, StringComparer.Ordinal)
                .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single().type, StringComparer.Ordinal);
            var compatible = new HashSet<int>(); var seeds = new HashSet<int>();
            bool ClipOnly(AnimatorStateMachine machine, HashSet<AnimatorStateMachine> visited)
            {
                if (machine == null || !visited.Add(machine)) return false;
                return machine.states.All(child => child.state != null &&
                    (child.state.motion == null || child.state.motion is AnimationClip)) &&
                    machine.stateMachines.All(child => ClipOnly(child.stateMachine, visited));
            }
            for (var index = 1; index < layers.Length; index++)
            {
                if (index == selectedLayer) continue;
                var layer = layers[index]; var definition = definitions[index];
                if (definition.syncedLayerIndex >= 0 || definition.avatarMask != null || definition.iKPass ||
                    definition.defaultWeight != 1 || definition.blendingMode != AnimatorLayerBlendingMode.Override ||
                    layer.FxControl || layer.WeightControls.Count != 0 || layer.NonMorphBindings || layer.CurveWrites.Count != 0 ||
                    layer.Morphs.Count == 0 && layer.WriteDefaults ||
                    layer.Morphs.Count > 0 && !layer.Morphs.Overlaps(automatic) ||
                    definitions.Any(item => item.syncedLayerIndex == index) ||
                    !ClipOnly(definition.stateMachine, new HashSet<AnimatorStateMachine>())) continue;
                var safe = true; var varyingAutomatic = false;
                var automaticConstants = new Dictionary<EditorCurveBinding, float>();
                foreach (var clip in layer.Clips)
                {
                    if (AnimationUtility.GetAnimationEvents(clip).Length != 0 || AnimationUtility.GetObjectReferenceCurveBindings(clip).Any())
                    { safe = false; break; }
                    foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                    {
                        if (binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                        { safe = false; break; }
                        var curve = AnimationUtility.GetEditorCurve(clip, binding);
                        VrChatGestureExpressions.ReadCurve(curve);
                        if (automatic.Contains(binding))
                        {
                            varyingAutomatic |= !VrChatExpressionSampler.IsConstant(curve) ||
                                automaticConstants.TryGetValue(binding, out var previous) && previous != curve.keys[0].value;
                            automaticConstants[binding] = curve.keys[0].value;
                        }
                    }
                }
                if (!safe) continue;
                // Constant eye-expression resets appear in blink clips too.
                // They are not evidence that the layer varies that expression.
                // Sparse zero resets with WD Off are also an automatic blink
                // convention: the held selected clip explicitly owns them.
                foreach (var binding in layer.Morphs.Where(binding => !automatic.Contains(binding)))
                {
                    if (!selectedMorphs.Contains(binding)) { safe = false; break; }
                    float? invariant = null; var missing = false;
                    foreach (var clip in layer.Clips)
                    {
                        var curve = AnimationUtility.GetEditorCurve(clip, binding);
                        if (curve == null) { missing = true; continue; }
                        if (curve.length == 0 || !VrChatExpressionSampler.IsConstant(curve) ||
                            invariant.HasValue && curve.keys[0].value != invariant.Value) { safe = false; break; }
                        invariant = curve.keys[0].value;
                    }
                    if (!safe || !invariant.HasValue || missing && (layer.WriteDefaults || invariant.Value != 0))
                    { safe = false; break; }
                }
                if (!safe || layer.Writes.Any(source.ExternalParameters.Contains)) continue;
                compatible.Add(index);
                if (varyingAutomatic) seeds.Add(index);
            }
            foreach (var seed in seeds)
            {
                var component = new HashSet<int> { seed }; var writes = new HashSet<string>(layers[seed].Writes, StringComparer.Ordinal);
                var escaped = false; bool changed;
                do
                {
                    changed = false;
                    if (others.Any(layer => layer.Reads.Overlaps(writes))) { escaped = true; break; }
                    for (var index = 0; index < layers.Length; index++)
                    {
                        if (component.Contains(index) || !layers[index].Reads.Overlaps(writes)) continue;
                        if (!compatible.Contains(index)) { escaped = true; break; }
                        component.Add(index); writes.UnionWith(layers[index].Writes); changed = true;
                    }
                } while (changed && !escaped);
                if (escaped || layers.Concat(others).Any(layer => layer.FxControl || layer.WeightControls.Values.Any(control =>
                    control.Playable == "FX" && (!control.AnimatorLayer || component.Contains(control.LayerIndex))))) continue;
                // Validate every suspended SDK operation, including a malformed
                // operation after a valid Random. Suppression cannot hide data
                // corruption, an invalid typed destination or Copy source.
                var needed = new HashSet<string>(writes, StringComparer.Ordinal);
                needed.UnionWith(component.SelectMany(index => layers[index].Reads));
                // An additional playable which initializes a component input
                // participates in that automatic program even if it never
                // reads its output. Do not suspend it or hide its typed data.
                if (others.Any(layer => layer.Writes.Overlaps(needed))) continue;
                foreach (var program in layers.SelectMany(layer => layer.DriverPrograms))
                    VrChatParameterDriver.ValidateTargets(program, types, needed);
                result.UnionWith(component);
            }
            return result;
        }
    }
}
