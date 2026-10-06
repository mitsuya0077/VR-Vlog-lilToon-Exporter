using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // Native evaluation retains WD/additive support even when its parameter
    // animation cannot affect the captured pose. Keep that execution graph;
    // only narrow which parameter curves need a fixed-value proof.
    internal static class NeutralParameterDependencies
    {
        // Restrict this proof to the actual last layer: a later WD/additive
        // layer could otherwise change a channel that appears fully overridden.
        // Execution dependencies stay intact; only scalar capture is independent.
        internal static int IndependentTopOverride(RuntimeAnimatorController runtime,
            ISet<EditorCurveBinding> capturedMorphs, Func<string, bool> excludedPath)
        {
            if (capturedMorphs == null || capturedMorphs.Count == 0) return -1;
            var layers = ExpressionDependencies.Controller(runtime).layers;
            if (layers.Length == 0) return -1;
            var index = layers.Length - 1;
            var layer = layers[index];
            if (layer.blendingMode != AnimatorLayerBlendingMode.Override || layer.defaultWeight != 1 || layer.avatarMask != null ||
                !ExpressionDependencies.StationaryLayers(runtime, excludedPath).TryGetValue(index, out var clip) ||
                AnimationUtility.GetAnimationEvents(clip).Length != 0) return -1;
            foreach (var binding in capturedMorphs)
            {
                if (binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal) ||
                    excludedPath?.Invoke(binding.path) == true) return -1;
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null || curve.length == 0 || !VrChatExpressionSampler.IsConstant(curve)) return -1;
                VrChatGestureExpressions.ReadCurve(curve);
            }
            return index;
        }

        internal static HashSet<string> Required(RuntimeAnimatorController runtime, ExpressionDependencies dependencies,
            Func<string, bool> excludedPath)
        {
            var controller = ExpressionDependencies.Controller(runtime);
            var replacements = ExpressionDependencies.Overrides(runtime);
            var unknown = new List<string>();
            var drivers = new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>();
            var info = ExpressionDependencies.Inspect(runtime, excludedPath, drivers, unknown, true, dependencies.NeutralFixedValues);
            // Dependency analysis normally rejects these first. An incomplete
            // graph must never turn into permission to ignore its parameters.
            if (unknown.Count != 0) return null;
            // No parameter timeline can change these explicit final scalar
            // values. Structural curve validation and all native support still
            // run; the sampler also checks the evaluated override weight.
            if (dependencies.IndependentTopOverrideLayer >= 0) return new HashSet<string>(StringComparer.Ordinal);
            var retained = dependencies.Layers.Concat(dependencies.NativeSupportLayers).Distinct().ToArray();
            var required = new HashSet<string>(StringComparer.Ordinal);
            var internalFloats = new HashSet<string>(controller.parameters.GroupBy(parameter => parameter.name, StringComparer.Ordinal)
                .Where(group => group.Count() == 1 && group.Single().type == AnimatorControllerParameterType.Float &&
                    !VrChatParameterDriver.BuiltIn.Contains(group.Key)).Select(group => group.Key), StringComparer.Ordinal);
            foreach (var index in retained)
            {
                // This exception is for declared internal float parameters, not
                // arbitrary Animator properties or built-in avatar semantics.
                required.UnionWith(info[index].CurveWrites.Where(name => !internalFloats.Contains(name)));
                // An independent support graph has no captured morph bindings;
                // every state/normalized tree has the same binding coverage and
                // WD configuration. Its timeline and parameter feedback cannot
                // change an explicit or implicit captured value. Other graphs
                // keep every transition, motion and callback dependency.
                if (!IsIndependentSupport(controller.layers[index], replacements, dependencies.Drivers, internalFloats, dependencies.Morphs))
                    required.UnionWith(info[index].Reads);
            }
            bool changed;
            do
            {
                changed = false;
                foreach (var index in retained)
                    if (info[index].Writes.Overlaps(required))
                        foreach (var name in info[index].Reads) changed |= required.Add(name);
            } while (changed);
            return required;
        }

        private static bool IsIndependentSupport(AnimatorControllerLayer layer, IDictionary<AnimationClip, AnimationClip> replacements,
            IDictionary<StateMachineBehaviour, VrChatParameterDriver.Program> drivers, ISet<string> internalFloats,
            ISet<EditorCurveBinding> capturedMorphs)
        {
            var machine = layer.stateMachine;
            if (layer.syncedLayerIndex >= 0 || layer.iKPass || machine == null || machine.behaviours.Length != 0 ||
                machine.stateMachines.Length != 0 || machine.anyStateTransitions.Length != 0 || machine.entryTransitions.Length != 0 ||
                machine.states.Length == 0) return false;
            var states = new HashSet<AnimatorState>(machine.states.Select(child => child.state));
            if (states.Contains(null) || machine.defaultState == null || !states.Contains(machine.defaultState)) return false;
            HashSet<EditorCurveBinding> coverage = null;
            var visiting = new HashSet<Motion>();
            bool MotionHasUniformBindings(Motion motion)
            {
                if (motion == null || !visiting.Add(motion)) return false;
                try
                {
                    if (motion is BlendTree tree)
                    {
                        // Simple1D weights form a normalized blend. Direct trees
                        // can scale implicit defaults as their total weight varies.
                        var children = tree.children;
                        return tree.blendType == BlendTreeType.Simple1D && children.Length > 0 &&
                            children.All(child => Finite(child.threshold) && Finite(child.timeScale) && Finite(child.cycleOffset)) &&
                            children.Select(child => child.threshold).Distinct().Count() == children.Length &&
                            children.All(child => MotionHasUniformBindings(child.motion));
                    }
                    if (!(motion is AnimationClip clip)) return false;
                    if (replacements.TryGetValue(clip, out var replacement)) clip = replacement;
                    // A custom additive reference can contain captured channels
                    // absent from the main clip; equal visible binding coverage
                    // is then insufficient to prove equal native contributions.
                    if (AnimationUtility.GetAnimationClipSettings(clip).hasAdditiveReferencePose) return false;
                    var bindings = AnimationUtility.GetCurveBindings(clip);
                    if (bindings.Length == 0 || bindings.Any(binding =>
                        !(binding.type == typeof(Animator) && binding.path == "" ||
                            binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal) &&
                            !capturedMorphs.Contains(binding))) ||
                        AnimationUtility.GetAnimationEvents(clip).Length != 0 || AnimationUtility.GetObjectReferenceCurveBindings(clip).Length != 0) return false;
                    if (coverage == null) coverage = new HashSet<EditorCurveBinding>(bindings);
                    return coverage.SetEquals(bindings);
                }
                finally { visiting.Remove(motion); }
            }
            return states.All(state => !state.iKOnFeet && state.writeDefaultValues == machine.defaultState.writeDefaultValues &&
                state.transitions.All(transition => !transition.isExit && transition.destinationStateMachine == null &&
                    states.Contains(transition.destinationState)) &&
                state.behaviours.All(behaviour => behaviour != null && drivers.TryGetValue(behaviour, out var program) &&
                    !program.FxControl && program.Error == null && program.Operations.All(operation => operation.Kind == "Set" &&
                        operation.Error == null && internalFloats.Contains(operation.Destination) && Finite(operation.Value))) &&
                MotionHasUniformBindings(state.motion));
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
