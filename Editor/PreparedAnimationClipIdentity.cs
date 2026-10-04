using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Animations;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // Authoring passes can commit a new clip object while preserving its
    // registered origin. Accept only a unique clip in the selected native slot
    // whose exact identity or recorded reference chain reaches that origin.
    internal static class PreparedAnimationClipIdentity
    {
        internal static AnimationClip Resolve(AnimationClip original, AnimationClip isolated,
            RuntimeAnimatorController runtime, int layerIndex, Func<AnimationClip, AnimationClip> origin)
        {
            if (original == null || runtime == null) return null;
            var controller = ExpressionDependencies.Controller(runtime);
            var layers = controller.layers;
            if (layerIndex < 0 || layerIndex >= layers.Length)
                throw new InvalidOperationException("登録表情のFXレイヤー参照が不正です。");
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
            var candidates = new HashSet<AnimationClip>();
            var machines = new HashSet<AnimatorStateMachine>();
            var states = new HashSet<AnimatorState>();
            void Motion(Motion motion, HashSet<Motion> visiting)
            {
                if (motion == null) return;
                if (!visiting.Add(motion)) throw new InvalidOperationException("FXのBlendTreeが循環しています。");
                try
                {
                    if (motion is AnimationClip clip)
                    {
                        if (replacements.TryGetValue(clip, out var replacement)) clip = replacement;
                        candidates.Add(clip);
                    }
                    else if (motion is BlendTree tree)
                        foreach (var child in tree.children) Motion(child.motion, visiting);
                }
                finally { visiting.Remove(motion); }
            }
            void Machine(AnimatorStateMachine machine)
            {
                if (machine == null || !machines.Add(machine))
                    throw new InvalidOperationException("登録表情のFX状態参照が不正です。");
                foreach (var child in machine.states)
                {
                    if (child.state == null || !states.Add(child.state))
                        throw new InvalidOperationException("登録表情のFX状態参照が不正です。");
                    Motion(controller.GetStateEffectiveMotion(child.state, layerIndex) ?? child.state.motion, new HashSet<Motion>());
                }
                foreach (var child in machine.stateMachines) Machine(child.stateMachine);
            }
            Machine(layers[sourceIndex].stateMachine);
            bool Matches(AnimationClip current)
            {
                var seen = new HashSet<AnimationClip>();
                var matches = false;
                while (current != null)
                {
                    // A malformed provenance cycle cannot establish a mapping,
                    // even if it happens to pass through a matching identity.
                    if (!seen.Add(current)) return false;
                    matches |= current == original || isolated != null && current == isolated;
                    var next = origin?.Invoke(current);
                    if (next == current) break;
                    current = next;
                }
                return matches;
            }
            var matching = candidates.Where(Matches).Take(2).ToArray();
            return matching.Length == 1 ? matching[0] : null;
        }
    }
}
