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
            internal bool WriteDefaults, HasBindings, EmptyMotion;
        }

        internal readonly HashSet<int> Layers = new HashSet<int>();
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
            Func<string, bool> excludedPath, VrChatExpressionMenu.Source source = null)
        {
            var result = new ExpressionDependencies();
            var controller = Controller(runtime);
            var unknown = new List<string>();
            var info = Inspect(runtime, excludedPath, result.Drivers, unknown);
            // Arbitrary behaviours can affect any parameter, layer or scene
            // object. No name/path-based independence claim is safe for them.
            if (unknown.Count > 0) throw new InvalidOperationException("FXの影響範囲を確定できないState Behaviourがあります: " + string.Join(", ", unknown));
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
                    if (!result.Layers.Contains(i) && !drivenBySelection && !layer.Writes.Overlaps(read) &&
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
            if (source != null)
            {
                var external = source.ExternalParameters.Where(result.Parameters.Contains).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                if (external.Length > 0) throw new InvalidOperationException("外部入力に依存する表情の値を確定できません: " + string.Join(", ", external));
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
            return result;
        }

        private static Layer[] Inspect(RuntimeAnimatorController runtime, Func<string, bool> excludedPath,
            Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program> drivers, List<string> unknown)
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
                void Behaviours(IEnumerable<StateMachineBehaviour> behaviours, string path)
                {
                    foreach (var behaviour in behaviours)
                    {
                        if (VrChatParameterDriver.IsTracking(behaviour)) continue;
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
                        Behaviours(state.behaviours, path + "/" + state.name);
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
