using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Animations;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // A direct expression forces one authored slot to the selected clip. Only
    // explicit state/clip identity can identify the callbacks that enter with
    // it; layer names, clip names and matching curve values are not evidence.
    internal sealed class SelectedExpressionDriverProof
    {
        internal StateMachineBehaviour[] Behaviours { get; private set; }
        internal readonly HashSet<string> OmittedWrites = new HashSet<string>(StringComparer.Ordinal);

        private sealed class Candidate
        {
            internal AnimatorStateMachine[] Ancestors;
            internal StateMachineBehaviour[] Behaviours;
            internal VrChatParameterDriver.Program[] Programs;
        }

        internal static SelectedExpressionDriverProof TryCreate(RuntimeAnimatorController runtime, int layerIndex,
            AnimatorState selectedState = null, AnimationClip selectedClip = null)
        {
            if (selectedState == null && selectedClip == null) return null;
            var controller = ExpressionDependencies.Controller(runtime);
            var layers = controller.layers;
            if (layerIndex < 0 || layerIndex >= layers.Length)
                throw new InvalidOperationException("選択した表情のFXレイヤー参照が不正です。");
            var sourceIndex = layerIndex;
            var sync = new HashSet<int>();
            while (layers[sourceIndex].syncedLayerIndex >= 0)
            {
                if (!sync.Add(sourceIndex)) throw new InvalidOperationException("FXの同期レイヤー参照が循環しています。");
                sourceIndex = layers[sourceIndex].syncedLayerIndex;
                if (sourceIndex < 0 || sourceIndex >= layers.Length)
                    throw new InvalidOperationException("FXの同期レイヤー参照が不正です。");
            }
            var replacements = ExpressionDependencies.Overrides(runtime);
            var candidates = new List<Candidate>();
            var machines = new HashSet<AnimatorStateMachine>();
            var states = new HashSet<AnimatorState>();
            var stateFound = false;
            bool ContainsClip(Motion motion, HashSet<Motion> visiting)
            {
                if (motion == null) return false;
                if (motion is AnimationClip clip)
                {
                    if (replacements.TryGetValue(clip, out var replacement)) clip = replacement;
                    return clip == selectedClip;
                }
                if (!(motion is BlendTree tree)) return false;
                if (!visiting.Add(motion)) throw new InvalidOperationException("FXのBlendTreeが循環しています。");
                try { return tree.children.Any(child => ContainsClip(child.motion, visiting)); }
                finally { visiting.Remove(motion); }
            }
            void Visit(AnimatorStateMachine machine, AnimatorStateMachine[] parents)
            {
                if (machine == null || !machines.Add(machine))
                    throw new InvalidOperationException("選択した表情のFX状態参照が不正です。");
                var ancestors = parents.Concat(new[] { machine }).ToArray();
                foreach (var child in machine.states)
                {
                    var state = child.state;
                    if (state == null || !states.Add(state))
                        throw new InvalidOperationException("選択した表情のFX状態参照が不正です。");
                    if (state == selectedState) stateFound = true;
                    if (selectedState != null && state != selectedState) continue;
                    var motion = controller.GetStateEffectiveMotion(state, layerIndex) ?? state.motion;
                    if (selectedClip != null && !ContainsClip(motion, new HashSet<Motion>())) continue;
                    var behaviours = layers[layerIndex].syncedLayerIndex < 0 ? state.behaviours :
                        controller.GetStateEffectiveBehaviours(state, layerIndex) ?? Array.Empty<StateMachineBehaviour>();
                    var drivers = behaviours.Where(VrChatParameterDriver.IsDriver).ToArray();
                    candidates.Add(new Candidate { Ancestors = ancestors, Behaviours = drivers,
                        Programs = drivers.Select(driver => VrChatParameterDriver.Read(driver, state.name)).ToArray() });
                }
                foreach (var child in machine.stateMachines) Visit(child.stateMachine, ancestors);
            }
            Visit(layers[sourceIndex].stateMachine, Array.Empty<AnimatorStateMachine>());
            if (selectedState != null && !stateFound)
                throw new InvalidOperationException("選択した表情のFX状態が指定したレイヤーにありません。");
            if (candidates.Count == 0) return null;
            // Unsupported operations remain omitted, so the existing dependency
            // guard still rejects relevant writes without rejecting unrelated
            // callbacks which were already safe to omit.
            if (candidates.Any(candidate => candidate.Programs.Any(program => program.Error != null ||
                program.Operations.Any(operation => operation.Error != null || operation.Kind != "Set")))) return null;
            bool Equivalent(Candidate candidate)
            {
                var original = candidates[0].Programs;
                if (candidate.Programs.Length != original.Length) return false;
                for (var index = 0; index < original.Length; index++)
                {
                    var first = original[index]; var next = candidate.Programs[index];
                    if (first.LocalOnly != next.LocalOnly || first.Operations.Count != next.Operations.Count) return false;
                    for (var operation = 0; operation < first.Operations.Count; operation++)
                        if (first.Operations[operation].Destination != next.Operations[operation].Destination ||
                            first.Operations[operation].Value != next.Operations[operation].Value) return false;
                }
                return true;
            }
            if (!candidates.All(Equivalent)) return null;
            var proof = new SelectedExpressionDriverProof { Behaviours = candidates[0].Behaviours };
            // Machine callbacks are not state-entry callbacks. Do not flatten
            // them into a different invocation order on the scalar probe.
            foreach (var machine in candidates.SelectMany(candidate => candidate.Ancestors).Distinct())
                foreach (var driver in machine.behaviours.Where(VrChatParameterDriver.IsDriver))
                    foreach (var operation in VrChatParameterDriver.Read(driver, machine.name).Operations)
                        proof.OmittedWrites.Add(operation.Destination);
            return proof;
        }
    }
}
