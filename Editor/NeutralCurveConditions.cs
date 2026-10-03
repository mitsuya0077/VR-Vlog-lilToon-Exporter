using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;

namespace VRVlog.LilToonExporter
{
    // Post-evaluation proof only. Never use these values as startup defaults or
    // to prune reachability before Unity has evaluated the parameter curves.
    internal static class NeutralCurveConditions
    {
        internal static IDictionary<string, float> FixedTimedValues(AnimatorControllerPlayable playable,
            AnimatorController controller, IDictionary<string, float> fixedValues,
            ExpressionDependencies dependencies, VrChatExpressionMenu.Source metadata)
        {
            var result = fixedValues == null ? new Dictionary<string, float>(StringComparer.Ordinal)
                : new Dictionary<string, float>(fixedValues, StringComparer.Ordinal);
            if (dependencies == null || metadata == null || metadata.OtherControllers.Any(value => value != null)) return result;
            var layers = controller.layers;
            for (var owner = 0; owner < layers.Length; owner++)
            {
                var layer = layers[owner];
                if (layer.syncedLayerIndex >= 0 || layer.blendingMode != AnimatorLayerBlendingMode.Override ||
                    playable.IsInTransition(owner) || owner > 0 && (!Finite(playable.GetLayerWeight(owner)) ||
                        playable.GetLayerWeight(owner) != 1)) continue;
                var machine = layer.stateMachine;
                if (machine == null || machine.stateMachines.Length != 0 || machine.behaviours.Length != 0 ||
                    machine.anyStateTransitions.Length != 0 || machine.entryTransitions.Length != 0) continue;
                var states = machine.states.Select(value => value.state).ToArray();
                var members = new HashSet<AnimatorState>(states);
                if (states.Length == 0 || members.Contains(null) || machine.defaultState == null || !members.Contains(machine.defaultState)) continue;
                if (states.Any(state => state.behaviours.Length != 0 || state.iKOnFeet || state.timeParameterActive ||
                    state.speedParameterActive || state.mirrorParameterActive || state.cycleOffsetParameterActive ||
                    !(state.motion is AnimationClip) || state.transitions.Any(transition => transition.isExit ||
                        transition.destinationStateMachine != null || !members.Contains(transition.destinationState)))) continue;
                var clips = states.Select(state => (AnimationClip)state.motion).Distinct().ToArray();
                // This narrow relay owns parameter curves only. A body/morph/
                // object change in the same layer needs its normal pose proof.
                if (clips.Any(clip => AnimationUtility.GetObjectReferenceCurveBindings(clip).Length != 0 ||
                    AnimationUtility.GetCurveBindings(clip).Any(binding => binding.type != typeof(Animator) || binding.path != ""))) continue;
                var active = playable.GetCurrentAnimatorClipInfo(owner);
                if (active.Length != 1 || active[0].clip == null || !Finite(active[0].weight) ||
                    active[0].weight != 1 || !clips.Contains(active[0].clip)) continue;
                foreach (var binding in AnimationUtility.GetCurveBindings(clips[0]))
                {
                    var name = binding.propertyName;
                    var parameter = controller.parameters.SingleOrDefault(value => value.name == name);
                    if (parameter == null || parameter.type != AnimatorControllerParameterType.Float ||
                        VrChatParameterDriver.BuiltIn.Contains(name) || metadata.ExternalParameters.Contains(name) ||
                        dependencies.Drivers.Values.Any(program => program.Operations.Any(operation => operation.Destination == name))) continue;
                    if (!Constant(AnimationUtility.GetEditorCurve(clips[0], binding), out var value) ||
                        clips.Any(clip => !Constant(AnimationUtility.GetEditorCurve(clip, binding), out var other) || other != value) ||
                        !Finite(playable.GetFloat(name)) || playable.GetFloat(name) != value) continue;
                    var safe = true;
                    for (var other = 0; other < layers.Length && safe; other++)
                    {
                        if (other == owner) continue;
                        var competing = AllStates(layers[other].stateMachine).ToArray();
                        if (competing.SelectMany(state => Clips(state.motion)).Any(clip => AnimationUtility.GetCurveBindings(clip)
                            .Any(curve => curve.type == typeof(Animator) && curve.propertyName == name))) { safe = false; break; }
                        // WD can implicitly reset a parameter without listing
                        // its binding. Only an unchanging WD layer is harmless:
                        // its already-observed contribution cannot change later.
                        if (competing.Any(state => state.writeDefaultValues) && !StaticLayer(layers[other])) safe = false;
                    }
                    if (safe) result[name] = value;
                }
            }
            return result;
        }

        static bool StaticLayer(AnimatorControllerLayer layer)
        {
            var machine = layer.stateMachine;
            if (layer.syncedLayerIndex >= 0 || layer.iKPass || machine == null || machine.stateMachines.Length != 0 ||
                machine.behaviours.Length != 0 || machine.anyStateTransitions.Length != 0 || machine.entryTransitions.Length != 0 ||
                machine.states.Length != 1 || machine.defaultState != machine.states[0].state) return false;
            var state = machine.defaultState;
            if (state == null || state.behaviours.Length != 0 || state.transitions.Length != 0 || state.iKOnFeet ||
                state.timeParameterActive || state.speedParameterActive || state.mirrorParameterActive || state.cycleOffsetParameterActive) return false;
            if (state.motion == null) return true;
            return state.motion is AnimationClip clip && AnimationUtility.GetObjectReferenceCurveBindings(clip).Length == 0 &&
                AnimationUtility.GetCurveBindings(clip).All(binding => Constant(AnimationUtility.GetEditorCurve(clip, binding), out _));
        }

        static bool Constant(AnimationCurve curve, out float value)
        {
            value = 0;
            if (curve == null || curve.length == 0) return false;
            value = curve.keys[0].value;
            if (float.IsNaN(value) || float.IsInfinity(value)) return false;
            var constant = value;
            return curve.keys.All(key => Finite(key.time) && key.value == constant && (key.inTangent == 0 || float.IsInfinity(key.inTangent)) &&
                (key.outTangent == 0 || float.IsInfinity(key.outTangent)));
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        static IEnumerable<AnimatorState> AllStates(AnimatorStateMachine root)
        {
            if (root == null) yield break;
            var seen = new HashSet<AnimatorStateMachine>();
            var pending = new Stack<AnimatorStateMachine>(); pending.Push(root);
            while (pending.Count > 0)
            {
                var machine = pending.Pop();
                if (machine == null || !seen.Add(machine)) continue;
                foreach (var state in machine.states) if (state.state != null) yield return state.state;
                foreach (var child in machine.stateMachines) pending.Push(child.stateMachine);
            }
        }

        static IEnumerable<AnimationClip> Clips(Motion root)
        {
            var seen = new HashSet<Motion>();
            var pending = new Stack<Motion>(); pending.Push(root);
            while (pending.Count > 0)
            {
                var motion = pending.Pop();
                if (motion == null || !seen.Add(motion)) continue;
                if (motion is AnimationClip clip) yield return clip;
                else if (motion is BlendTree tree) foreach (var child in tree.children) pending.Push(child.motion);
            }
        }
    }
}
