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
            internal readonly HashSet<string> DriverWrites = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<string> CurveWrites = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<EditorCurveBinding> Morphs = new HashSet<EditorCurveBinding>();
            internal readonly Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program> FxCommands = new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>();
            internal bool WriteDefaults, HasBindings, EmptyMotion, FxControl, DynamicMorph, Timed, NonMorphBindings;
        }

        internal readonly HashSet<int> Layers = new HashSet<int>();
        internal readonly HashSet<int> NativeSupportLayers = new HashSet<int>();
        internal bool HasFxControls;
        internal readonly HashSet<string> Parameters = new HashSet<string>(StringComparer.Ordinal);
        internal readonly HashSet<EditorCurveBinding> Morphs = new HashSet<EditorCurveBinding>();
        internal readonly Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program> Drivers = new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>();
        internal readonly Dictionary<string, float> NeutralFixedValues = new Dictionary<string, float>(StringComparer.Ordinal);

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

        // A reduced probe controller has no authored state callbacks. Validate
        // those callbacks on the original graph before reconstruction so layer
        // controls cannot silently turn a permanent effect on, off or down.
        internal static void ValidateProbeBehaviours(RuntimeAnimatorController runtime, Func<string, bool> excludedPath)
        {
            var unknown = new List<string>();
            Inspect(runtime, excludedPath, new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), unknown);
            if (unknown.Count > 0)
                throw new InvalidOperationException("FXの影響範囲を確定できないState Behaviourがあります: " + string.Join(", ", unknown));
        }

        internal static HashSet<string> SelectedLayerDriverWrites(RuntimeAnimatorController runtime, int layerIndex, Func<string, bool> excludedPath)
        {
            var unknown = new List<string>();
            var layers = Inspect(runtime, excludedPath, new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), unknown);
            if (unknown.Count > 0)
                throw new InvalidOperationException("FXの影響範囲を確定できないState Behaviourがあります: " + string.Join(", ", unknown));
            return layers[layerIndex].DriverWrites;
        }

        internal static ExpressionDependencies Analyze(RuntimeAnimatorController runtime, IEnumerable<string> selected,
            Func<string, bool> excludedPath, VrChatExpressionMenu.Source source = null, IDictionary<string, float> defaults = null, IDictionary<string, float> selection = null,
            IEnumerable<EditorCurveBinding> initialMorphs = null, bool preserveNativeBasePose = false, FixedExpressionContext fixedContext = null)
            => AnalyzeCore(runtime, selected, excludedPath, source, defaults, selection, null,
                fixedContext: fixedContext, initialMorphs: initialMorphs, preserveNativeBasePose: preserveNativeBasePose);

        internal static IEnumerable<HashSet<EditorCurveBinding>> NeutralRoots(RuntimeAnimatorController runtime, Func<string, bool> excludedPath,
            VrChatExpressionMenu.Source source, ISet<EditorCurveBinding> automatic = null)
        {
            var unknown = new List<string>();
            var layers = Inspect(runtime, excludedPath, new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), unknown, true);
            if (layers.All(layer => layer.Morphs.Count == 0)) return Array.Empty<HashSet<EditorCurveBinding>>();
            if (unknown.Count > 0)
                throw new InvalidOperationException("FXの影響範囲を確定できないState Behaviourがあります: " + string.Join(", ", unknown));
            var fixedValues = FixedNeutralValues(runtime, source, layers, excludedPath);
            layers = Inspect(runtime, excludedPath, new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(),
                new List<string>(), true, fixedValues);
            RemoveIndependentAutomaticWriters(layers, automatic, source, fixedValues);
            return layers.Where(layer => layer.Morphs.Count > 0).Select(layer => new HashSet<EditorCurveBinding>(layer.Morphs));
        }

        internal static ExpressionDependencies AnalyzeNeutral(RuntimeAnimatorController runtime, ISet<EditorCurveBinding> morphs,
            Func<string, bool> excludedPath, VrChatExpressionMenu.Source source, ISet<EditorCurveBinding> automatic = null)
            => AnalyzeCore(runtime, Array.Empty<string>(), excludedPath, source, source.Defaults,
                new Dictionary<string, float>(), morphs, automatic, initialMorphs: morphs, preserveNativeBasePose: true);

        private static ExpressionDependencies AnalyzeCore(RuntimeAnimatorController runtime, IEnumerable<string> selected,
            Func<string, bool> excludedPath, VrChatExpressionMenu.Source source, IDictionary<string, float> defaults,
            IDictionary<string, float> selection, ISet<EditorCurveBinding> neutralMorphs, ISet<EditorCurveBinding> automatic = null,
            FixedExpressionContext fixedContext = null, IEnumerable<EditorCurveBinding> initialMorphs = null, bool preserveNativeBasePose = false)
        {
            var result = new ExpressionDependencies();
            var requiredMorphs = new HashSet<EditorCurveBinding>(initialMorphs ?? Enumerable.Empty<EditorCurveBinding>());
            result.Morphs.UnionWith(requiredMorphs);
            var controller = Controller(runtime);
            var unknown = new List<string>();
            var info = Inspect(runtime, excludedPath, result.Drivers, unknown, true);
            var nativeBaseHasBindings = info.Length > 0 && info[0].HasBindings;
            result.HasFxControls = info.Any(layer => layer.FxControl);
            // Arbitrary behaviours can affect any parameter, layer or scene
            // object. No name/path-based independence claim is safe for them.
            if (unknown.Count > 0) throw new InvalidOperationException("FXの影響範囲を確定できないState Behaviourがあります: " + string.Join(", ", unknown));
            if (neutralMorphs != null)
            {
                foreach (var pair in FixedNeutralValues(runtime, source, info, excludedPath)) result.NeutralFixedValues.Add(pair.Key, pair.Value);
                // Keep all driver programs for the private controller copy, but
                // root neutral dependencies only in reachable default states.
                info = Inspect(runtime, excludedPath, result.Drivers, new List<string>(), true, result.NeutralFixedValues);
                // An automatic-only base still contributes native layer
                // activity to fractional overrides, even when its live morphs
                // must remain outside the authored neutral roots.
                nativeBaseHasBindings = info.Length > 0 && info[0].HasBindings;
                RemoveIndependentAutomaticWriters(info, automatic, source, result.NeutralFixedValues);
            }
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
                        var suppliedInput = fixedContext?.Values.ContainsKey(parameter.name) == true;
                        if (writers.Contains(parameter.name) || parameter.type == AnimatorControllerParameterType.Trigger ||
                            !suppliedInput && (VrChatParameterDriver.BuiltIn.Contains(parameter.name) ||
                            source?.ExternalParameters.Contains(parameter.name) == true)) continue;
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
                        if (fixedContext != null && controller.layers[index].syncedLayerIndex < 0 &&
                            FixedExpressionContext.TryReachableFxCommands(controller.layers[index].stateMachine, fixedValues, controller.parameters, out var fixedReached))
                            reachedCommands = fixedReached;
                        else if (controller.layers[index].syncedLayerIndex < 0 &&
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
            // Classify native base support before Write Defaults closure can
            // promote it into the evaluation graph. An unrelated base motion
            // stays outside capture even when its native defaults matter.
            var needsNativeBasePose = preserveNativeBasePose && nativeBaseHasBindings &&
                Enumerable.Range(1, Math.Max(0, info.Length - 1)).Any(index =>
                    controller.layers[index].blendingMode == AnimatorLayerBlendingMode.Override &&
                    controller.layers[index].defaultWeight > 0 && controller.layers[index].defaultWeight < 1 &&
                    info[index].Morphs.Overlaps(requiredMorphs));
            if (needsNativeBasePose)
            {
                gateExternal.UnionWith(info[0].Reads.Where(name => VrChatParameterDriver.BuiltIn.Contains(name) &&
                    (neutralMorphs == null || !result.NeutralFixedValues.ContainsKey(name))));
                if (!info[0].Morphs.Overlaps(requiredMorphs))
                {
                    result.NativeSupportLayers.Add(0);
                    read.UnionWith(info[0].Reads);
                }
            }
            if (neutralMorphs != null) result.Morphs.UnionWith(neutralMorphs);
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
                    if (drivenBySelection || layer.Morphs.Overlaps(requiredMorphs)) result.NativeSupportLayers.Remove(i);
                    // A layer included only to check implicit defaults is not
                    // evidence that its unrelated drivers were menu selections.
                    if (drivenBySelection) foreach (var name in layer.Writes) modified |= changed.Add(name);
                    foreach (var name in layer.Reads) modified |= read.Add(name);
                    if (!result.NativeSupportLayers.Contains(i))
                        foreach (var morph in layer.Morphs) modified |= result.Morphs.Add(morph);
                }
            } while (modified);
            if (result.Layers.Count == 0) throw new InvalidOperationException("このメニューに対応するFXの表情がありません。");
            result.Parameters.UnionWith(read);
            // Parameters supplied by the menu can be reset by a driver after
            // the selection; never reapply them on every sampled frame.
            result.Parameters.UnionWith(selected);
            if (result.Layers.Concat(result.NativeSupportLayers).Any(i => controller.layers[i].syncedLayerIndex >= 0))
                throw new InvalidOperationException("このメニューに影響する同期Animatorレイヤーの表情変換は未対応です。");
            var external = gateExternal.Concat(source?.ExternalParameters ?? Enumerable.Empty<string>())
                .Concat(fixedContext != null ? VrChatParameterDriver.BuiltIn : Enumerable.Empty<string>())
                .Concat(neutralMorphs == null ? Enumerable.Empty<string>() : VrChatParameterDriver.BuiltIn.Where(name => !result.NeutralFixedValues.ContainsKey(name)))
                .Where(name => result.Parameters.Contains(name) && fixedContext?.Values.ContainsKey(name) != true)
                .Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
            if (external.Length > 0)
            {
                var message = "外部入力に依存する表情の値を確定できません: " + string.Join(", ", external);
                if (neutralMorphs != null) throw new NeutralShapeSamplingException(message);
                throw new InvalidOperationException(message);
            }
            if (source != null)
            {
                foreach (var other in source.OtherControllers.Where(c => c != null))
                {
                    var otherUnknown = new List<string>();
                    var otherInfo = Inspect(other, null, new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), otherUnknown);
                    var writes = otherInfo.SelectMany(l => l.Writes).Where(result.Parameters.Contains).Distinct().ToArray();
                    var otherMorphs = neutralMorphs == null ? Array.Empty<string>() : otherInfo.SelectMany(layer => layer.Morphs)
                        .Where(result.Morphs.Contains).Select(binding => binding.path + "/" + binding.propertyName).Distinct().ToArray();
                    if (writes.Length > 0 || otherUnknown.Count > 0 || otherMorphs.Length > 0)
                        throw new InvalidOperationException("FX以外のPlayable Layerからの変更を再現できません: " + other.name + " / " +
                            string.Join(", ", writes.Concat(otherUnknown).Concat(otherMorphs)));
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

        private static void RemoveIndependentAutomaticWriters(Layer[] layers, ISet<EditorCurveBinding> automatic,
            VrChatExpressionMenu.Source source, IDictionary<string, float> fixedValues)
        {
            if (automatic == null) return;
            foreach (var layer in layers)
            {
                if (layer.Morphs.Count == 0 || !layer.Morphs.All(automatic.Contains) || layer.WriteDefaults || layer.FxControl ||
                    layer.Writes.Count != 0 || layer.NonMorphBindings) continue;
                var external = layer.Reads.Any(name => !fixedValues.ContainsKey(name) &&
                    (VrChatParameterDriver.BuiltIn.Contains(name) || source?.ExternalParameters.Contains(name) == true));
                // A timed initializer may write a constant zero and then hold
                // it forever with WD Off. Keep those writes; the sampler checks
                // whether its eventual active state still has a timed exit.
                if (!layer.DynamicMorph && !external) continue;
                // A live tracking layer may share its automatic channel with a
                // constant DEFAULT FACE underlay. Its other effects must be
                // empty before it can be left out of the neutral graph.
                layer.Morphs.Clear(); layer.HasBindings = false; layer.EmptyMotion = false;
            }
        }

        private static Dictionary<string, float> FixedNeutralValues(RuntimeAnimatorController runtime, VrChatExpressionMenu.Source source, Layer[] layers,
            Func<string, bool> excludedPath)
        {
            var controller = Controller(runtime);
            var written = new HashSet<string>(layers.SelectMany(layer => layer.Writes), StringComparer.Ordinal);
            var otherWritten = new HashSet<string>(StringComparer.Ordinal);
            if (source != null)
                foreach (var other in source.OtherControllers.Where(value => value != null))
                {
                    var unknown = new List<string>();
                    var otherLayers = Inspect(other, null, new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), unknown);
                    otherWritten.UnionWith(otherLayers.SelectMany(layer => layer.Writes));
                    if (unknown.Count > 0) otherWritten.UnionWith(controller.parameters.Select(parameter => parameter.name));
                }
            var result = new Dictionary<string, float>(StringComparer.Ordinal);
            var initial = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var parameter in controller.parameters)
            {
                if (otherWritten.Contains(parameter.name) || source?.ExternalParameters.Contains(parameter.name) == true ||
                    parameter.type == AnimatorControllerParameterType.Trigger) continue;
                var gesture = parameter.name == "GestureLeft" || parameter.name == "GestureRight" ||
                    parameter.name == "GestureLeftWeight" || parameter.name == "GestureRightWeight";
                var semanticZero = gesture || parameter.name == "AFK";
                if (VrChatParameterDriver.BuiltIn.Contains(parameter.name) && !semanticZero && parameter.name != "IsLocal" && parameter.name != "TrackingType") continue;
                var value = semanticZero ? 0f : parameter.type == AnimatorControllerParameterType.Bool ? (parameter.defaultBool ? 1f : 0f) :
                    parameter.type == AnimatorControllerParameterType.Int ? parameter.defaultInt : parameter.defaultFloat;
                if (!semanticZero && source != null && source.Defaults.TryGetValue(parameter.name, out var supplied)) value = supplied;
                if (float.IsNaN(value) || float.IsInfinity(value)) continue;
                value = parameter.type == AnimatorControllerParameterType.Bool ? (value == 0 ? 0 : 1) :
                    parameter.type == AnimatorControllerParameterType.Int ? Mathf.RoundToInt(value) : value;
                initial.Add(parameter.name, value);
                if (!written.Contains(parameter.name)) result.Add(parameter.name, value);
            }
            // Reachability can prove that a generated driver's only possible
            // writes preserve the initial value. Propagate those constants,
            // never a changed startup value or a guessed external input.
            bool changed;
            do
            {
                changed = false;
                var programs = new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>();
                var reachable = Inspect(runtime, excludedPath, programs, new List<string>(), true, result);
                var curveWrites = new HashSet<string>(reachable.SelectMany(layer => layer.CurveWrites), StringComparer.Ordinal);
                foreach (var parameter in controller.parameters.Where(parameter => initial.ContainsKey(parameter.name) && !result.ContainsKey(parameter.name)))
                {
                    if (curveWrites.Contains(parameter.name)) continue;
                    var operations = programs.Values.SelectMany(program => program.Operations).Where(operation => operation.Destination == parameter.name).ToArray();
                    float Normalize(float value)
                    {
                        if (parameter.type == AnimatorControllerParameterType.Bool) return value == 0 ? 0 : 1;
                        if (parameter.type == AnimatorControllerParameterType.Int) value = (float)Math.Truncate(value);
                        if (source?.ExpressionParameters.Contains(parameter.name) == true)
                        {
                            if (parameter.type == AnimatorControllerParameterType.Int) value = Mathf.Clamp(value, 0, 255);
                            else if (parameter.type == AnimatorControllerParameterType.Float) value = Mathf.Clamp(value, -1, 1);
                        }
                        return value;
                    }
                    if (operations.Any(operation => operation.Kind != "Set" || operation.Error != null ||
                        float.IsNaN(operation.Value) || float.IsInfinity(operation.Value) || Normalize(operation.Value) != initial[parameter.name])) continue;
                    result.Add(parameter.name, initial[parameter.name]); changed = true;
                }
            } while (changed);
            return result;
        }

        internal static bool IsFalse(AnimatorTransitionBase transition, IDictionary<string, float> fixedValues)
        {
            if (fixedValues == null) return false;
            foreach (var condition in transition.conditions)
            {
                if (!fixedValues.TryGetValue(condition.parameter, out var value)) continue;
                switch (condition.mode)
                {
                    case AnimatorConditionMode.If: if (value == 0) return true; break;
                    case AnimatorConditionMode.IfNot: if (value != 0) return true; break;
                    case AnimatorConditionMode.Equals: if (value != condition.threshold) return true; break;
                    case AnimatorConditionMode.NotEqual: if (value == condition.threshold) return true; break;
                    case AnimatorConditionMode.Greater: if (value <= condition.threshold) return true; break;
                    case AnimatorConditionMode.Less: if (value >= condition.threshold) return true; break;
                }
            }
            return false;
        }

        private sealed class NeutralReachability
        {
            internal readonly HashSet<AnimatorState> States = new HashSet<AnimatorState>();
            internal readonly HashSet<AnimatorStateMachine> Machines = new HashSet<AnimatorStateMachine>();
        }

        // Over-approximate every state reachable from fixed neutral inputs,
        // including nested entry/exit and ancestor Any State transitions. A
        // transition is pruned only when an AND condition is provably false.
        private static NeutralReachability NeutralStates(AnimatorStateMachine root, IDictionary<string, float> fixedValues)
        {
            if (fixedValues == null) return null;
            var parents = new Dictionary<AnimatorStateMachine, AnimatorStateMachine>();
            var owners = new Dictionary<AnimatorState, AnimatorStateMachine>();
            bool Index(AnimatorStateMachine machine, AnimatorStateMachine parent)
            {
                if (machine == null || parents.ContainsKey(machine)) return false;
                parents.Add(machine, parent);
                foreach (var child in machine.states)
                {
                    if (child.state == null || owners.ContainsKey(child.state)) return false;
                    owners.Add(child.state, machine);
                }
                return machine.stateMachines.All(child => Index(child.stateMachine, machine));
            }
            if (!Index(root, null)) return null;
            IEnumerable<AnimatorTransitionBase> Enabled(IEnumerable<AnimatorTransitionBase> values)
            {
                var all = values.ToArray(); var solo = all.Any(value => value.solo);
                return all.Where(value => !value.mute && (!solo || value.solo) && !IsFalse(value, fixedValues));
            }
            bool Always(AnimatorTransitionBase transition) => transition.conditions.All(condition =>
            {
                if (!fixedValues.TryGetValue(condition.parameter, out var value)) return false;
                switch (condition.mode)
                {
                    case AnimatorConditionMode.If: return value != 0;
                    case AnimatorConditionMode.IfNot: return value == 0;
                    case AnimatorConditionMode.Equals: return value == condition.threshold;
                    case AnimatorConditionMode.NotEqual: return value != condition.threshold;
                    case AnimatorConditionMode.Greater: return value > condition.threshold;
                    case AnimatorConditionMode.Less: return value < condition.threshold;
                    default: return false;
                }
            });
            var reached = new NeutralReachability();
            var pending = new Queue<AnimatorState>();
            var entered = new HashSet<AnimatorStateMachine>();
            var exited = new HashSet<AnimatorStateMachine>();
            var malformed = false;
            void Activate(AnimatorStateMachine machine)
            {
                if (machine == null || !parents.ContainsKey(machine)) { malformed = true; return; }
                if (!reached.Machines.Add(machine)) return;
                if (parents[machine] != null) Activate(parents[machine]);
                foreach (var transition in Enabled(machine.anyStateTransitions)) Destination(transition, machine);
            }
            void Enter(AnimatorStateMachine machine)
            {
                if (machine == null || !parents.ContainsKey(machine)) { malformed = true; return; }
                Activate(machine);
                if (!entered.Add(machine)) return;
                foreach (var transition in Enabled(machine.entryTransitions))
                {
                    Destination(transition, machine);
                    if (Always(transition)) return;
                }
                if (machine.defaultState != null) pending.Enqueue(machine.defaultState);
                else foreach (var child in machine.stateMachines) Enter(child.stateMachine);
            }
            void Exit(AnimatorStateMachine machine)
            {
                if (!parents.TryGetValue(machine, out var parent) || parent == null || !exited.Add(machine)) return;
                foreach (var transition in Enabled(parent.GetStateMachineTransitions(machine))) Destination(transition, parent);
                // If an exit falls through, Unity re-enters the parent flow.
                // Include that flow conservatively even when an exit is taken.
                Enter(parent);
            }
            void Destination(AnimatorTransitionBase transition, AnimatorStateMachine owner)
            {
                if (transition.isExit) { Exit(owner); return; }
                if (transition.destinationState != null) pending.Enqueue(transition.destinationState);
                else if (transition.destinationStateMachine != null) Enter(transition.destinationStateMachine);
                else malformed = true;
            }
            Enter(root);
            while (pending.Count > 0)
            {
                var state = pending.Dequeue();
                if (!owners.TryGetValue(state, out var owner)) { malformed = true; continue; }
                Activate(owner);
                if (!reached.States.Add(state)) continue;
                foreach (var transition in Enabled(state.transitions)) Destination(transition, owner);
            }
            return malformed ? null : reached;
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
            Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program> drivers, List<string> unknown, bool allowFxControls = false,
            IDictionary<string, float> neutralFixed = null)
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
                var reached = layers[index].syncedLayerIndex < 0 ? NeutralStates(layers[sourceIndex].stateMachine, neutralFixed) : null;
                void ReadParameter(string name) { if (!string.IsNullOrEmpty(name)) info.Reads.Add(name); }
                void Conditions(IEnumerable<AnimatorTransitionBase> transitions)
                {
                    var siblings = transitions.ToArray();
                    var solo = siblings.Any(t => t.solo);
                    foreach (var transition in siblings.Where(t => !t.mute && (!solo || t.solo) && !IsFalse(t, neutralFixed)))
                    {
                        if (transition is AnimatorStateTransition timed && timed.hasExitTime) info.Timed = true;
                        foreach (var condition in transition.conditions) ReadParameter(condition.parameter);
                    }
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
                            info.DriverWrites.Add(op.Destination);
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
                            if (binding.type == typeof(Animator)) { info.Writes.Add(binding.propertyName); info.CurveWrites.Add(binding.propertyName); info.HasBindings = true; continue; }
                            if (excludedPath?.Invoke(binding.path) == true) continue;
                            info.HasBindings = true;
                            if (binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                            {
                                info.Morphs.Add(binding);
                                if (neutralFixed != null)
                                {
                                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                                    if (curve != null)
                                    {
                                        VrChatGestureExpressions.ReadCurve(curve).Range(out var minimum, out var maximum);
                                        info.DynamicMorph |= minimum != maximum;
                                    }
                                }
                            }
                            else info.NonMorphBindings = true;
                        }
                    }
                    finally { motions.Remove(motion); }
                }
                var machines = new HashSet<AnimatorStateMachine>();
                void Visit(AnimatorStateMachine machine, string path)
                {
                    if (reached != null && !reached.Machines.Contains(machine)) return;
                    if (machine == null || !machines.Add(machine)) throw new InvalidOperationException("FXのStateMachine参照が不正です。");
                    Behaviours(machine.behaviours, path);
                    Conditions(machine.anyStateTransitions); Conditions(machine.entryTransitions);
                    foreach (var child in machine.states)
                    {
                        var state = child.state;
                        if (reached != null && !reached.States.Contains(state)) continue;
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
                        if (reached != null && !reached.Machines.Contains(child.stateMachine)) continue;
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
