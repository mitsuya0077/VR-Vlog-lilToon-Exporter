using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    internal sealed class ExpressionDependencies
    {
        internal sealed class Layer
        {
            internal readonly HashSet<string> Reads = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<string> Writes = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<EditorCurveBinding> Morphs = new HashSet<EditorCurveBinding>();
            internal readonly Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program> FxCommands = new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>();
            internal bool WriteDefaults, HasBindings, EmptyMotion, FxControl;
        }

        internal readonly HashSet<int> Layers = new HashSet<int>();
        internal bool HasFxControls;
        internal readonly HashSet<string> Parameters = new HashSet<string>(StringComparer.Ordinal);
        internal readonly HashSet<EditorCurveBinding> Morphs = new HashSet<EditorCurveBinding>();
        internal readonly Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program> Drivers = new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>();

        internal static AnimatorController Controller(RuntimeAnimatorController runtime)
        {
            if (runtime is AnimatorOverrideController overrides) runtime = overrides.runtimeAnimatorController;
            return runtime as AnimatorController ?? throw new InvalidOperationException("FX Animator Controllerを取得できません。");
        }

        internal static Dictionary<AnimationClip, AnimationClip> Overrides(RuntimeAnimatorController runtime)
        {
            var result = new Dictionary<AnimationClip, AnimationClip>();
            if (runtime is AnimatorOverrideController overrides)
            {
                var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                overrides.GetOverrides(pairs);
                foreach (var pair in pairs) if (pair.Value != null) result[pair.Key] = pair.Value;
            }
            return result;
        }

        internal static ExpressionDependencies Analyze(RuntimeAnimatorController runtime, IEnumerable<string> selected,
            Func<string, bool> excludedPath, VrChatExpressionMenu.Source source = null, IDictionary<string, float> defaults = null, IDictionary<string, float> selection = null,
            IEnumerable<EditorCurveBinding> initialMorphs = null)
        {
            var result = new ExpressionDependencies();
            if (initialMorphs != null) result.Morphs.UnionWith(initialMorphs);
            var controller = Controller(runtime);
            var unknown = new List<string>();
            var info = Inspect(runtime, excludedPath, result.Drivers, unknown, true);
            result.HasFxControls = info.Any(layer => layer.FxControl);
            // Arbitrary behaviours can affect any parameter, layer or scene
            // object. No name/path-based independence claim is safe for them.
            if (unknown.Count > 0) throw new InvalidOperationException("FXの影響範囲を確定できないState Behaviourがあります: " + string.Join(", ", unknown));
            var gateExternal = new HashSet<string>(StringComparer.Ordinal);
            var unsafeFxCommands = new List<VrChatParameterDriver.Program>();
            if (result.HasFxControls)
            {
                var writers = new HashSet<string>(info.SelectMany(layer => layer.Writes), StringComparer.Ordinal);
                if (source != null)
                    foreach (var other in source.OtherControllers.Where(c => c != null))
                    {
                        var otherUnknown = new List<string>();
                        var otherInfo = Inspect(other, null, new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), otherUnknown);
                        writers.UnionWith(otherInfo.SelectMany(layer => layer.Writes));
                        // An arbitrary callback could invalidate any constant.
                        if (otherUnknown.Count > 0) writers.UnionWith(controller.parameters.Select(p => p.name));
                    }
                var fixedValues = new Dictionary<string, float>(StringComparer.Ordinal);
                if (defaults != null && selection != null)
                    foreach (var parameter in controller.parameters)
                    {
                        if (writers.Contains(parameter.name) || VrChatParameterDriver.BuiltIn.Contains(parameter.name) ||
                            source?.ExternalParameters.Contains(parameter.name) == true || parameter.type == AnimatorControllerParameterType.Trigger) continue;
                        var initial = parameter.type == AnimatorControllerParameterType.Bool ? (parameter.defaultBool ? 1f : 0f) :
                            parameter.type == AnimatorControllerParameterType.Int ? parameter.defaultInt : parameter.defaultFloat;
                        if (defaults.TryGetValue(parameter.name, out var supplied)) initial = supplied;
                        var final = selection.TryGetValue(parameter.name, out supplied) ? supplied : initial;
                        float Normalize(float value) => parameter.type == AnimatorControllerParameterType.Bool ? (value == 0 ? 0 : 1) :
                            parameter.type == AnimatorControllerParameterType.Int ? Mathf.RoundToInt(value) : value;
                        if (!float.IsNaN(initial) && !float.IsInfinity(initial) && !float.IsNaN(final) && !float.IsInfinity(final) &&
                            Normalize(initial) == Normalize(final)) fixedValues.Add(parameter.name, Normalize(initial));
                    }
                for (var index = 0; index < info.Length; index++)
                    if (info[index].FxControl)
                    {
                        HashSet<StateMachineBehaviour> reachedCommands = null;
                        if (controller.layers[index].syncedLayerIndex < 0 &&
                            TryFixedGateReads(controller.layers[index].stateMachine, controller.parameters, fixedValues, out var reads, out var reached))
                        {
                            info[index].Reads.IntersectWith(reads);
                            reachedCommands = reached;
                        }
                        // Native callbacks can omit states entered and exited
                        // within one Evaluate. A command cannot be accepted just
                        // because its callback was absent from the sample.
                        unsafeFxCommands.AddRange(info[index].FxCommands.Where(pair => pair.Value.FxWeight != 1 &&
                            (reachedCommands == null || reachedCommands.Contains(pair.Key))).Select(pair => pair.Value));
                        gateExternal.UnionWith(info[index].Reads.Where(VrChatParameterDriver.BuiltIn.Contains));
                    }
            }
            var changed = new HashSet<string>(selected, StringComparer.Ordinal);
            var read = new HashSet<string>(StringComparer.Ordinal);
            bool modified;
            do
            {
                modified = false;
                for (var i = 0; i < info.Length; i++)
                {
                    var layer = info[i];
                    var drivenBySelection = layer.Reads.Overlaps(changed);
                    var defaultsMayReset = result.Layers.Any(l => info[l].WriteDefaults) && layer.HasBindings;
                    var implicitWriter = result.Morphs.Count > 0 && (layer.WriteDefaults || layer.EmptyMotion);
                    if (!result.Layers.Contains(i) && !layer.FxControl && !drivenBySelection && !layer.Writes.Overlaps(read) &&
                        !layer.Morphs.Overlaps(result.Morphs) && !defaultsMayReset && !implicitWriter) continue;
                    modified |= result.Layers.Add(i);
                    // A layer included only to check implicit defaults is not
                    // evidence that its unrelated drivers were menu selections.
                    if (drivenBySelection) foreach (var name in layer.Writes) modified |= changed.Add(name);
                    foreach (var name in layer.Reads) modified |= read.Add(name);
                    foreach (var morph in layer.Morphs) modified |= result.Morphs.Add(morph);
                }
            } while (modified);
            if (result.Layers.Count == 0) throw new InvalidOperationException("このメニューに対応するFXの表情がありません。");
            result.Parameters.UnionWith(read);
            // Parameters supplied by the menu can be reset by a driver after
            // the selection; never reapply them on every sampled frame.
            result.Parameters.UnionWith(selected);
            if (result.Layers.Any(i => controller.layers[i].syncedLayerIndex >= 0))
                throw new InvalidOperationException("このメニューに影響する同期Animatorレイヤーの表情変換は未対応です。");
            var external = gateExternal.Concat(source?.ExternalParameters ?? Enumerable.Empty<string>())
                .Where(result.Parameters.Contains).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
            if (external.Length > 0) throw new InvalidOperationException("外部入力に依存する表情の値を確定できません: " + string.Join(", ", external));
            if (source != null)
            {
                foreach (var other in source.OtherControllers.Where(c => c != null))
                {
                    var otherUnknown = new List<string>();
                    var otherInfo = Inspect(other, null, new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), otherUnknown);
                    var writes = otherInfo.SelectMany(l => l.Writes).Where(result.Parameters.Contains).Distinct().ToArray();
                    if (writes.Length > 0 || otherUnknown.Count > 0)
                        throw new InvalidOperationException("FX以外のPlayable Layerからの変更を再現できません: " + other.name + " / " +
                            string.Join(", ", writes.Concat(otherUnknown)));
                }
            }
            if (unsafeFxCommands.Count > 0)
                throw new InvalidOperationException(unsafeFxCommands[0].Location + " / VRCPlayableLayerControl: FXの重みを変更する状態は固定表情に変換できません。");
            return result;
        }

        // A permanent override does not read a menu parameter, so parameter-
        // rooted dependency traversal alone cannot discover it. Seed only a
        // proven stationary default clip; gesture and timed layers still enter
        // the graph solely through their real property/parameter dependencies.
        internal static HashSet<EditorCurveBinding> StationaryMorphBindings(RuntimeAnimatorController runtime,
            Func<string, bool> excludedPath = null)
        {
            var result = new HashSet<EditorCurveBinding>();
            foreach (var clip in StationaryLayers(runtime, excludedPath).Values)
                result.UnionWith(AnimationUtility.GetCurveBindings(clip).Where(binding => excludedPath?.Invoke(binding.path) != true));
            return result;
        }

        internal static Dictionary<int, AnimationClip> StationaryLayers(RuntimeAnimatorController runtime,
            Func<string, bool> excludedPath = null)
        {
            var result = new Dictionary<int, AnimationClip>();
            var controller = Controller(runtime);
            var replacements = Overrides(runtime);
            var layers = controller.layers;
            for (var index = 0; index < layers.Length; index++)
            {
                var layer = layers[index];
                var machine = layer.stateMachine;
                if (layer.syncedLayerIndex >= 0 || layer.iKPass || index > 0 && layer.defaultWeight <= 0 ||
                    machine == null || machine.behaviours.Length != 0 || machine.stateMachines.Length != 0 ||
                    machine.anyStateTransitions.Length != 0 || machine.entryTransitions.Length != 0) continue;
                var state = machine.defaultState;
                if (state == null || state.transitions.Length != 0 || state.behaviours.Length != 0 || state.iKOnFeet ||
                    state.timeParameterActive || state.speedParameterActive || state.mirrorParameterActive || state.cycleOffsetParameterActive ||
                    !(state.motion is AnimationClip clip)) continue;
                if (replacements.TryGetValue(clip, out var replacement)) clip = replacement;
                if (AnimationUtility.GetObjectReferenceCurveBindings(clip).Any(binding => excludedPath?.Invoke(binding.path) != true)) continue;
                var bindings = AnimationUtility.GetCurveBindings(clip).Where(binding => excludedPath?.Invoke(binding.path) != true).ToArray();
                if (bindings.Length == 0 || bindings.Any(binding => binding.type != typeof(SkinnedMeshRenderer) ||
                    !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal) ||
                    !VrChatExpressionSampler.IsConstant(AnimationUtility.GetEditorCurve(clip, binding)))) continue;
                result.Add(index, clip);
            }
            return result;
        }

        // This proof applies only to a flat, motionless command layer. An
        // unwritten, unchanged non-external parameter may block an entire AND
        // transition regardless of every external input. Keep all other reads.
        private static bool TryFixedGateReads(AnimatorStateMachine machine, AnimatorControllerParameter[] parameters,
            IDictionary<string, float> fixedValues, out HashSet<string> reads, out HashSet<StateMachineBehaviour> commands)
        {
            reads = new HashSet<string>(StringComparer.Ordinal);
            commands = new HashSet<StateMachineBehaviour>();
            if (machine == null || machine.behaviours.Length != 0 || machine.stateMachines.Length != 0 ||
                machine.anyStateTransitions.Length != 0 || machine.entryTransitions.Length != 0) return false;
            var states = new HashSet<AnimatorState>(machine.states.Select(child => child.state));
            if (machine.defaultState == null || !states.Contains(machine.defaultState) || states.Contains(null)) return false;
            foreach (var state in states)
            {
                if (state.motion != null || state.writeDefaultValues || state.iKOnFeet || state.timeParameterActive || state.speedParameterActive ||
                    state.mirrorParameterActive || state.cycleOffsetParameterActive || state.behaviours.Any(b =>
                    !VrChatParameterDriver.IsTracking(b) && !VrChatParameterDriver.IsNonFxPlayableControl(b) && !VrChatParameterDriver.IsTemporaryPoseSpace(b) && !VrChatParameterDriver.IsLocomotionControl(b) &&
                    !VrChatParameterDriver.ReadInstantFxControl(b, state.name, out _))) return false;
                if (state.transitions.Any(t => t.isExit || t.destinationStateMachine != null || !states.Contains(t.destinationState) ||
                    t.hasExitTime || t.duration != 0)) return false;
            }
            var types = parameters.ToDictionary(p => p.name, p => p.type, StringComparer.Ordinal);
            bool IsFalse(AnimatorCondition condition)
            {
                if (!fixedValues.TryGetValue(condition.parameter, out var value) || !types.TryGetValue(condition.parameter, out var type)) return false;
                if (type == AnimatorControllerParameterType.Bool)
                {
                    if (condition.mode == AnimatorConditionMode.If) return value == 0;
                    if (condition.mode == AnimatorConditionMode.IfNot) return value != 0;
                    return false;
                }
                if (float.IsNaN(condition.threshold) || float.IsInfinity(condition.threshold)) return false;
                switch (condition.mode)
                {
                    case AnimatorConditionMode.Equals: return value != condition.threshold;
                    case AnimatorConditionMode.NotEqual: return value == condition.threshold;
                    case AnimatorConditionMode.Greater: return value <= condition.threshold;
                    case AnimatorConditionMode.Less: return value >= condition.threshold;
                    default: return false;
                }
            }
            var visited = new HashSet<AnimatorState>();
            var pending = new Stack<AnimatorState>(); pending.Push(machine.defaultState);
            while (pending.Count > 0)
            {
                var state = pending.Pop(); if (!visited.Add(state)) continue;
                commands.UnionWith(state.behaviours);
                var solo = state.transitions.Any(t => t.solo);
                foreach (var transition in state.transitions.Where(t => !t.mute && (!solo || t.solo)))
                {
                    if (transition.conditions.Any(IsFalse)) continue;
                    foreach (var condition in transition.conditions) if (!string.IsNullOrEmpty(condition.parameter)) reads.Add(condition.parameter);
                    pending.Push(transition.destinationState);
                }
            }
            return true;
        }

        private static Layer[] Inspect(RuntimeAnimatorController runtime, Func<string, bool> excludedPath,
            Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program> drivers, List<string> unknown, bool allowFxControls = false)
        {
            var controller = Controller(runtime);
            var replacements = Overrides(runtime);
            var layers = controller.layers;
            var result = new Layer[layers.Length];
            for (var index = 0; index < layers.Length; index++)
            {
                var info = result[index] = new Layer();
                var sourceIndex = index;
                var chain = new HashSet<int>();
                while (sourceIndex >= 0 && sourceIndex < layers.Length && layers[sourceIndex].syncedLayerIndex >= 0)
                {
                    if (!chain.Add(sourceIndex)) throw new InvalidOperationException("FXの同期レイヤー参照が循環しています。");
                    sourceIndex = layers[sourceIndex].syncedLayerIndex;
                }
                if (sourceIndex < 0 || sourceIndex >= layers.Length) throw new InvalidOperationException("FXの同期レイヤー参照が不正です。");
                void ReadParameter(string name) { if (!string.IsNullOrEmpty(name)) info.Reads.Add(name); }
                void Conditions(IEnumerable<AnimatorTransitionBase> transitions)
                {
                    var siblings = transitions.ToArray();
                    var solo = siblings.Any(t => t.solo);
                    foreach (var transition in siblings.Where(t => !t.mute && (!solo || t.solo)))
                        foreach (var condition in transition.conditions) ReadParameter(condition.parameter);
                }
                void Behaviours(IEnumerable<StateMachineBehaviour> behaviours, string path, bool onState = false)
                {
                    foreach (var behaviour in behaviours)
                    {
                        if (VrChatParameterDriver.IsTracking(behaviour) || VrChatParameterDriver.IsNonFxPlayableControl(behaviour) ||
                            VrChatParameterDriver.IsTemporaryPoseSpace(behaviour) || VrChatParameterDriver.IsLocomotionControl(behaviour)) continue;
                        if (allowFxControls && onState && VrChatParameterDriver.ReadInstantFxControl(behaviour, path, out var control))
                        {
                            info.FxControl = true;
                            if (!info.FxCommands.ContainsKey(behaviour)) info.FxCommands.Add(behaviour, control);
                            if (!drivers.ContainsKey(behaviour)) drivers.Add(behaviour, control);
                            continue;
                        }
                        if (!VrChatParameterDriver.IsDriver(behaviour))
                        {
                            unknown.Add(path + " / " + (behaviour == null ? "欠けたBehaviour" : behaviour.GetType().Name));
                            continue;
                        }
                        var program = VrChatParameterDriver.Read(behaviour, path);
                        if (!drivers.ContainsKey(behaviour)) drivers.Add(behaviour, program);
                        if (program.Error != null) { unknown.Add(path + " / Parameter Driver: " + program.Error); continue; }
                        foreach (var op in program.Operations)
                        {
                            info.Writes.Add(op.Destination);
                            if (op.Kind == "Copy") ReadParameter(op.Source);
                            if (op.Kind == "Add") ReadParameter(op.Destination);
                        }
                    }
                }
                var motions = new HashSet<Motion>();
                void InspectMotion(Motion motion)
                {
                    if (motion == null) { info.EmptyMotion = true; return; }
                    if (!motions.Add(motion)) throw new InvalidOperationException("FXのBlendTreeが循環しています。");
                    try
                    {
                        if (motion is BlendTree tree)
                        {
                            ReadParameter(tree.blendParameter); ReadParameter(tree.blendParameterY);
                            foreach (var child in tree.children) { ReadParameter(child.directBlendParameter); InspectMotion(child.motion); }
                        }
                        if (!(motion is AnimationClip clip)) return;
                        if (replacements.TryGetValue(clip, out var replacement)) clip = replacement;
                        var bindings = AnimationUtility.GetCurveBindings(clip).Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip)).ToArray();
                        if (bindings.Length == 0) info.EmptyMotion = true;
                        foreach (var binding in bindings)
                        {
                            // Animator parameters are global even if a caller
                            // happens to exclude the animator's transform path.
                            if (binding.type == typeof(Animator)) { info.Writes.Add(binding.propertyName); info.HasBindings = true; continue; }
                            if (excludedPath?.Invoke(binding.path) == true) continue;
                            info.HasBindings = true;
                            if (binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                                info.Morphs.Add(binding);
                        }
                    }
                    finally { motions.Remove(motion); }
                }
                var machines = new HashSet<AnimatorStateMachine>();
                void Visit(AnimatorStateMachine machine, string path)
                {
                    if (machine == null || !machines.Add(machine)) throw new InvalidOperationException("FXのStateMachine参照が不正です。");
                    Behaviours(machine.behaviours, path);
                    Conditions(machine.anyStateTransitions); Conditions(machine.entryTransitions);
                    foreach (var child in machine.states)
                    {
                        var state = child.state;
                        info.WriteDefaults |= state.writeDefaultValues;
                        Behaviours(state.behaviours, path + "/" + state.name, true);
                        Conditions(state.transitions);
                        if (state.timeParameterActive) ReadParameter(state.timeParameter);
                        if (state.speedParameterActive) ReadParameter(state.speedParameter);
                        if (state.cycleOffsetParameterActive) ReadParameter(state.cycleOffsetParameter);
                        if (state.mirrorParameterActive) ReadParameter(state.mirrorParameter);
                        InspectMotion(controller.GetStateEffectiveMotion(state, index) ?? state.motion);
                    }
                    foreach (var child in machine.stateMachines)
                    {
                        Conditions(machine.GetStateMachineTransitions(child.stateMachine));
                        Visit(child.stateMachine, path + "/" + child.stateMachine.name);
                    }
                    machines.Remove(machine);
                }
                Visit(layers[sourceIndex].stateMachine, layers[index].name);
            }
            return result;
        }
    }
}
