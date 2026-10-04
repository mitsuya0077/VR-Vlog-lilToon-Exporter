using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
        internal readonly HashSet<int> StrictNonMorphLayers = new HashSet<int>();
        internal readonly HashSet<string> Parameters = new HashSet<string>(StringComparer.Ordinal);
        internal readonly HashSet<EditorCurveBinding> Morphs = new HashSet<EditorCurveBinding>();
        private readonly HashSet<EditorCurveBinding> capturedMorphs = new HashSet<EditorCurveBinding>();
        internal bool LimitMorphCapture;
        internal ISet<EditorCurveBinding> CapturedMorphs => LimitMorphCapture ? capturedMorphs : Morphs;
        internal readonly Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program> Drivers = new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>();
        internal readonly Dictionary<string, float> NeutralFixedValues = new Dictionary<string, float>(StringComparer.Ordinal);
        private IDictionary<string, float> fixedMenuProofValues;
        private IDictionary<string, float> fixedMotionTimeSetValues;
        private IDictionary<string, AnimatorControllerParameterType> fixedMenuProofTypes;
        private HashSet<string>[] fixedMenuLayerReads;
        private Dictionary<string, HashSet<string>> fixedMenuDerivedInputs;
        private Dictionary<string, float> fixedMenuExternalValues;

        // Retain native implicit/default support without serializing unrelated
        // object/bone motion as a facial morph. Selected roots and the captured
        // renderer's visibility/transform ancestry remain strict.
        internal bool IsAncillarySupportBinding(int layer, EditorCurveBinding binding)
        {
            if (fixedMenuProofValues == null || StrictNonMorphLayers.Contains(layer)) return false;
            if (binding.type != typeof(Transform) &&
                !(binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive")) return false;
            return !CapturedMorphs.Any(morph => string.IsNullOrEmpty(binding.path) || morph.path == binding.path ||
                (morph.path ?? "").StartsWith(binding.path + "/", StringComparison.Ordinal));
        }

        // A false edge can consume an export assumption without leaving a
        // runtime dependency. Report raw reads only for retained layers; this
        // metadata must never expand the sampling dependency closure.
        internal IEnumerable<string> FixedMenuProofInputs => fixedMenuLayerReads == null ? Enumerable.Empty<string>() :
            Layers.Concat(NativeSupportLayers).Distinct().SelectMany(index => fixedMenuLayerReads[index])
                .Where(fixedMenuProofValues.ContainsKey).SelectMany(name => new[] { name }.Concat(
                    fixedMenuDerivedInputs != null && fixedMenuDerivedInputs.TryGetValue(name, out var inputs) ? inputs : Enumerable.Empty<string>()))
                .Distinct(StringComparer.Ordinal);

        internal void RecordFixedMenuInputs(FixedExpressionContext context)
        {
            foreach (var name in Parameters.Concat(FixedMenuProofInputs).Distinct(StringComparer.Ordinal))
            {
                if (context.Values.ContainsKey(name)) context.UsedParameters.Add(name);
                else if (fixedMenuExternalValues != null && fixedMenuExternalValues.TryGetValue(name, out var value))
                {
                    // Other-only known inputs are reporting evidence. Do not
                    // add them to the FX evaluation environment or driver inputs.
                    context.ReportedInputs[name] = value;
                    context.UsedParameters.Add(name);
                }
            }
        }

        internal bool IsFalseFixedMenuTransition(AnimatorTransitionBase transition) => fixedMenuProofValues != null &&
            transition.conditions.Any(condition => FixedMenuCondition(condition, fixedMenuProofValues, fixedMenuProofTypes) == false);

        // Read only the same all-controller immutable proof used for pruning.
        // A sampled value, omitted callback or declaration alone cannot supply
        // fixed motion time, and conflicting parameter types are not evidence.
        internal bool TryFixedMenuParameter(string name, AnimatorControllerParameterType type, out float value)
        {
            value = 0;
            return !string.IsNullOrEmpty(name) && fixedMenuProofValues != null && fixedMenuProofTypes != null &&
                fixedMenuProofTypes.TryGetValue(name, out var actualType) && actualType == type &&
                fixedMenuProofValues.TryGetValue(name, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        // This certificate is ONLY a future invariant of Motion Time.
        // It never supplies initial values, state pruning or driver inputs.
        // Once the live Float is S, all possible authored writes being Set(S)
        // preserve S even if the declared initial value was different.
        internal bool TryFixedMotionTimeParameter(string name, Func<float> readNativeValue, out float value)
        {
            value = 0;
            if (!LimitMorphCapture || readNativeValue == null) return false;
            if (!TryFixedMenuParameter(name, AnimatorControllerParameterType.Float, out value) &&
                (string.IsNullOrEmpty(name) || fixedMotionTimeSetValues == null || !fixedMotionTimeSetValues.TryGetValue(name, out value))) return false;
            // Call GetFloat only after a unique, globally audited Float has
            // supplied a finite normalized position, never for a Trigger/name
            // mismatch or an unproven sampled parameter.
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0 || value > 1) return false;
            var native = readNativeValue();
            return !float.IsNaN(native) && !float.IsInfinity(native) && native == value;
        }

        internal HashSet<AnimatorState> FixedMenuReachableSupportStates(AnimatorController controller, int layerIndex)
        {
            // The same proof may be applied to the private graph copy, whose
            // parameter types and transition topology preserve the source.
            // Without a proven fixed environment, retain every support motion.
            if (fixedMenuProofValues == null || fixedMenuProofValues.Count == 0 ||
                controller.layers[layerIndex].syncedLayerIndex >= 0) return null;
            if (!FixedExpressionContext.TryReachableFxCommands(controller.layers[layerIndex].stateMachine,
                fixedMenuProofValues, controller.parameters, out _)) return null;
            return NeutralStates(controller.layers[layerIndex].stateMachine, fixedMenuProofValues, fixedMenuProofTypes)?.States;
        }

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
        internal static void ValidateProbeBehaviours(RuntimeAnimatorController runtime, Func<string, bool> excludedPath, bool allowFxControls = false)
        {
            var unknown = new List<string>();
            Inspect(runtime, excludedPath, new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), unknown, allowFxControls);
            if (unknown.Count > 0)
                throw new InvalidOperationException("FXの影響範囲を確定できないState Behaviourがあります: " + string.Join(", ", unknown));
        }

        internal static HashSet<string> SelectedLayerDriverWrites(RuntimeAnimatorController runtime, int layerIndex, Func<string, bool> excludedPath,
            bool allowFxControls = false)
        {
            var unknown = new List<string>();
            var layers = Inspect(runtime, excludedPath, new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), unknown, allowFxControls);
            if (unknown.Count > 0)
                throw new InvalidOperationException("FXの影響範囲を確定できないState Behaviourがあります: " + string.Join(", ", unknown));
            // The direct clip replaces this slot's callbacks. Even a command
            // behind a normally false gate cannot be replayed for its arbitrary
            // registered clip; only unit FX setup is harmless in a fixed context.
            var control = layers[layerIndex].FxCommands.Values.FirstOrDefault(program => program.FxWeight != 1);
            if (control != null)
                throw new InvalidOperationException(control.Location + " / VRCPlayableLayerControl: FXの重みを変更する状態は固定表情に変換できません。");
            return layers[layerIndex].DriverWrites;
        }

        internal static ExpressionDependencies Analyze(RuntimeAnimatorController runtime, IEnumerable<string> selected,
            Func<string, bool> excludedPath, VrChatExpressionMenu.Source source = null, IDictionary<string, float> defaults = null, IDictionary<string, float> selection = null,
            IEnumerable<EditorCurveBinding> initialMorphs = null, bool preserveNativeBasePose = false, FixedExpressionContext fixedContext = null,
            ISet<string> extraMutableParameters = null, IEnumerable<EditorCurveBinding> captureRoots = null,
            ISet<int> captureSelectedLayers = null)
            => AnalyzeCore(runtime, selected, excludedPath, source, defaults, selection, null,
                fixedContext: fixedContext, initialMorphs: initialMorphs, preserveNativeBasePose: preserveNativeBasePose,
                extraMutableParameters: extraMutableParameters, captureRoots: captureRoots, captureSelectedLayers: captureSelectedLayers);

        internal static IEnumerable<HashSet<EditorCurveBinding>> NeutralRoots(RuntimeAnimatorController runtime, Func<string, bool> excludedPath,
            VrChatExpressionMenu.Source source, ISet<EditorCurveBinding> automatic = null)
        {
            var unknown = new List<string>();
            var unsupported = new List<string>();
            var layers = Inspect(runtime, excludedPath, new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), unknown, true,
                neutralUnsupported: unsupported);
            if (layers.All(layer => layer.Morphs.Count == 0)) return Array.Empty<HashSet<EditorCurveBinding>>();
            if (unknown.Count > 0)
                throw new InvalidOperationException("FXの影響範囲を確定できないState Behaviourがあります: " + string.Join(", ", unknown));
            if (unsupported.Count > 0)
                throw new NeutralShapeSamplingException("FXの影響範囲を確定できないState Behaviourがあります: " + string.Join(", ", unsupported));
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
            FixedExpressionContext fixedContext = null, IEnumerable<EditorCurveBinding> initialMorphs = null, bool preserveNativeBasePose = false,
            ISet<string> extraMutableParameters = null, IEnumerable<EditorCurveBinding> captureRoots = null,
            ISet<int> captureSelectedLayers = null)
        {
            if (fixedContext == null) captureRoots = null;
            var result = new ExpressionDependencies { LimitMorphCapture = captureRoots != null };
            var requiredMorphs = new HashSet<EditorCurveBinding>(captureRoots ?? initialMorphs ?? Enumerable.Empty<EditorCurveBinding>());
            result.Morphs.UnionWith(initialMorphs ?? Enumerable.Empty<EditorCurveBinding>());
            result.Morphs.UnionWith(requiredMorphs);
            result.capturedMorphs.UnionWith(requiredMorphs);
            var controller = Controller(runtime);
            var unknown = new List<string>();
            var unsupported = neutralMorphs == null ? null : new List<string>();
            var info = Inspect(runtime, excludedPath, result.Drivers, unknown, true, neutralUnsupported: unsupported);
            var nativeBaseHasBindings = info.Length > 0 && info[0].HasBindings;
            result.HasFxControls = info.Any(layer => layer.FxControl);
            // Arbitrary behaviours can affect any parameter, layer or scene
            // object. No name/path-based independence claim is safe for them.
            if (unknown.Count > 0) throw new InvalidOperationException("FXの影響範囲を確定できないState Behaviourがあります: " + string.Join(", ", unknown));
            if (unsupported?.Count > 0) throw new NeutralShapeSamplingException("FXの影響範囲を確定できないState Behaviourがあります: " + string.Join(", ", unsupported));
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
            Dictionary<string, float> menuFixedValues = null;
            var directlySelectedLayers = new HashSet<int>();
            if (neutralMorphs == null && fixedContext != null)
            {
                // The export environment supplies inputs once; it does not
                // hold menu parameters against later drivers or animation.
                // Keep every authored writer when proving a constant, then
                // collect dependencies only from states that can be reached.
                menuFixedValues = FixedMenuValues(runtime, source, info, defaults, selection, fixedContext, extraMutableParameters,
                    out result.fixedMenuDerivedInputs, out result.fixedMenuExternalValues, out var motionTimeSets, result.LimitMorphCapture);
                result.fixedMotionTimeSetValues = new ReadOnlyDictionary<string, float>(motionTimeSets);
                // Reuse the exact immutable proof for later timed-state checks.
                // Runtime values or dropped writers cannot create new constants.
                result.fixedMenuProofValues = new ReadOnlyDictionary<string, float>(
                    new Dictionary<string, float>(menuFixedValues, StringComparer.Ordinal));
                result.fixedMenuLayerReads = info.Select(layer => new HashSet<string>(layer.Reads, StringComparer.Ordinal)).ToArray();
                result.fixedMenuProofTypes = new ReadOnlyDictionary<string, AnimatorControllerParameterType>(controller.parameters
                    .GroupBy(parameter => parameter.name).Where(group => group.Count() == 1)
                    .ToDictionary(group => group.Key, group => group.Single().type, StringComparer.Ordinal));
                for (var index = 0; index < info.Length; index++)
                    if (info[index].Reads.Overlaps(selected))
                    {
                        directlySelectedLayers.Add(index);
                        // An Off/default selection can leave only an empty
                        // reachable motion. Still capture the selected layer's
                        // authored channels at their prepared defaults/rest
                        // underlays so the exported entry can reset them.
                        requiredMorphs.UnionWith(info[index].Morphs);
                        result.Morphs.UnionWith(info[index].Morphs);
                        result.capturedMorphs.UnionWith(info[index].Morphs);
                    }
                info = Inspect(runtime, excludedPath, result.Drivers, new List<string>(), true,
                    menuFixedValues, validateFixedData: false);
                nativeBaseHasBindings = info.Length > 0 && info[0].HasBindings;
                result.HasFxControls = info.Any(layer => layer.FxControl);
            }
            if (result.LimitMorphCapture && captureSelectedLayers != null)
            {
                if (captureSelectedLayers.Any(index => index < 0 || index >= info.Length))
                    throw new InvalidOperationException("表情のFXレイヤーを特定できません。");
                // Proven state-entry Set programs remain in this native slot.
                // Their readers belong to evaluation as well as capture.
                directlySelectedLayers.UnionWith(captureSelectedLayers);
            }
            var gateExternal = new HashSet<string>(StringComparer.Ordinal);
            var unsafeFxCommands = new List<VrChatParameterDriver.Program>();
            if (result.HasFxControls)
            {
                var writers = new HashSet<string>(info.SelectMany(layer => layer.Writes), StringComparer.Ordinal);
                if (extraMutableParameters != null) writers.UnionWith(extraMutableParameters);
                if (source != null)
                    foreach (var other in source.OtherControllers.Where(c => c != null))
                    {
                        var otherUnknown = new List<string>();
                        var otherInfo = Inspect(other, null, new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), otherUnknown);
                        writers.UnionWith(otherInfo.SelectMany(layer => layer.Writes));
                        // An arbitrary callback could invalidate any constant.
                        if (otherUnknown.Count > 0) writers.UnionWith(controller.parameters.Select(p => p.name));
                    }
                var fixedValues = menuFixedValues ?? new Dictionary<string, float>(StringComparer.Ordinal);
                if (menuFixedValues == null && defaults != null && selection != null)
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
                    var drivenBySelection = layer.Reads.Overlaps(changed) || directlySelectedLayers.Contains(i);
                    var defaultsMayReset = result.Layers.Any(l => info[l].WriteDefaults) && layer.HasBindings;
                    var implicitWriter = result.Morphs.Count > 0 && (layer.WriteDefaults || layer.EmptyMotion);
                    if (!result.Layers.Contains(i) && !layer.FxControl && !drivenBySelection && !layer.Writes.Overlaps(read) &&
                        !layer.Morphs.Overlaps(result.Morphs) && !defaultsMayReset && !implicitWriter) continue;
                    modified |= result.Layers.Add(i);
                    if (drivenBySelection || layer.Morphs.Overlaps(requiredMorphs)) result.NativeSupportLayers.Remove(i);
                    // Parameter writers must stay in the native graph, but
                    // their unrelated object motion is not a selected morph.
                    // Its geometry scope is proved against the final endpoint.
                    if (directlySelectedLayers.Contains(i) || layer.Morphs.Overlaps(requiredMorphs))
                        result.StrictNonMorphLayers.Add(i);
                    // A layer included only to check implicit defaults is not
                    // evidence that its unrelated drivers were menu selections.
                    if (drivenBySelection) foreach (var name in layer.Writes) modified |= changed.Add(name);
                    foreach (var name in layer.Reads) modified |= read.Add(name);
                    if (!result.NativeSupportLayers.Contains(i))
                        foreach (var morph in layer.Morphs) modified |= result.Morphs.Add(morph);
                }
            } while (modified);
            if (result.LimitMorphCapture)
            {
                // Evaluation keeps the complete WD/native support graph.
                // Serialization starts with the selected roots and follows
                // their retained parameter writes, including selected Set.
                var roots = new HashSet<int>(directlySelectedLayers);
                var driven = new HashSet<string>(selected, StringComparer.Ordinal);
                do
                {
                    modified = false;
                    foreach (var index in result.Layers.Concat(result.NativeSupportLayers).Distinct())
                        if (roots.Contains(index) || info[index].Reads.Overlaps(driven))
                        {
                            result.capturedMorphs.UnionWith(info[index].Morphs);
                            foreach (var name in info[index].Writes) modified |= driven.Add(name);
                        }
                } while (modified);
                // Same-renderer underlays and WD-Off history can reset a
                // channel absent from the currently active selected clip.
                var paths = new HashSet<string>(result.capturedMorphs.Select(binding => binding.path), StringComparer.Ordinal);
                result.capturedMorphs.UnionWith(result.Morphs.Where(binding => paths.Contains(binding.path)));
            }
            if (result.Layers.Count == 0) throw new InvalidOperationException("このメニューに対応するFXの表情がありません。");
            result.Parameters.UnionWith(read);
            // Parameters supplied by the menu can be reset by a driver after
            // the selection; never reapply them on every sampled frame.
            result.Parameters.UnionWith(selected);
            if (neutralMorphs != null)
            {
                // Validate actual inputs before a recoverable capability exit
                // can leave their dependency group at the authored appearance.
                foreach (var parameter in controller.parameters.Where(parameter => result.Parameters.Contains(parameter.name)))
                {
                    var value = parameter.type == AnimatorControllerParameterType.Float ? parameter.defaultFloat : 0f;
                    if (source != null && source.Defaults.TryGetValue(parameter.name, out var supplied)) value = supplied;
                    if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidOperationException("表情パラメーターに不正な値があります。");
                }
                var operations = result.Drivers.Values.SelectMany(program => program.Operations.Select(operation => (program, operation)))
                    .Where(pair => result.Parameters.Contains(pair.operation.Destination)).ToArray();
                var invalid = operations.FirstOrDefault(pair => pair.operation.Error != null && pair.operation.Kind != "Random");
                if (invalid.operation != null)
                    throw new InvalidOperationException(invalid.program.Location + " / Parameter Driver: " + invalid.operation.Error);
                var types = controller.parameters.ToDictionary(parameter => parameter.name, parameter => parameter.type, StringComparer.Ordinal);
                foreach (var pair in operations)
                {
                    if (!types.ContainsKey(pair.operation.Destination))
                        throw new InvalidOperationException(pair.program.Location + " / Parameter Driver: 書き込み先の型を解決できません: " + pair.operation.Destination);
                    if (pair.operation.Kind == "Copy" && !types.ContainsKey(pair.operation.Source))
                        throw new InvalidOperationException(pair.program.Location + " / Parameter Driver: Copy元の型を解決できません: " + pair.operation.Source);
                }
                var trigger = operations.FirstOrDefault(pair => types[pair.operation.Destination] == AnimatorControllerParameterType.Trigger ||
                    pair.operation.Kind == "Copy" && types[pair.operation.Source] == AnimatorControllerParameterType.Trigger);
                if (trigger.operation != null)
                    throw new NeutralShapeSamplingException(trigger.program.Location + " / Parameter Driver: Triggerは固定表情に変換できません。");
                var random = operations.FirstOrDefault(pair => pair.operation.Kind == "Random");
                if (random.operation != null)
                    throw new NeutralShapeSamplingException(random.program.Location + " / Parameter Driver: Randomは固定表情の値を確定できません。");
                var booleanAdd = operations.FirstOrDefault(pair => pair.operation.Kind == "Add" &&
                    controller.parameters.Any(parameter => parameter.name == pair.operation.Destination && parameter.type == AnimatorControllerParameterType.Bool));
                if (booleanAdd.operation != null)
                    throw new NeutralShapeSamplingException(booleanAdd.program.Location + " / Parameter Driver: BoolへのAddは未対応です。");
            }
            if (result.Layers.Concat(result.NativeSupportLayers).Any(i => controller.layers[i].syncedLayerIndex >= 0))
            {
                const string message = "このメニューに影響する同期Animatorレイヤーの表情変換は未対応です。";
                if (neutralMorphs != null) throw new NeutralShapeSamplingException(message);
                throw new InvalidOperationException(message);
            }
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
                    var otherUnsupported = neutralMorphs == null ? null : new List<string>();
                    var otherInfo = Inspect(other, null, new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), otherUnknown,
                        neutralFixed: menuFixedValues, neutralUnsupported: otherUnsupported, validateFixedData: menuFixedValues == null);
                    var writes = otherInfo.SelectMany(l => l.Writes).Where(result.Parameters.Contains).Distinct().ToArray();
                    var otherMorphs = neutralMorphs == null ? Array.Empty<string>() : otherInfo.SelectMany(layer => layer.Morphs)
                        .Where(result.Morphs.Contains).Select(binding => binding.path + "/" + binding.propertyName).Distinct().ToArray();
                    if (neutralMorphs != null && otherUnknown.Count > 0)
                        throw new InvalidOperationException("FX以外のPlayable Layerに不正なState Behaviourがあります: " + other.name + " / " + string.Join(", ", otherUnknown));
                    if (writes.Length > 0 || otherUnknown.Count > 0 || otherMorphs.Length > 0 || otherUnsupported?.Count > 0)
                    {
                        var message = "FX以外のPlayable Layerからの変更を再現できません: " + other.name + " / " +
                            string.Join(", ", writes.Concat(otherUnknown).Concat(otherMorphs).Concat(otherUnsupported ?? Enumerable.Empty<string>()));
                        if (neutralMorphs != null) throw new NeutralShapeSamplingException(message);
                        throw new InvalidOperationException(message);
                    }
                }
            }
            if (unsafeFxCommands.Count > 0)
            {
                var message = unsafeFxCommands[0].Location + " / VRCPlayableLayerControl: FXの重みを変更する状態は固定表情に変換できません。";
                if (neutralMorphs != null) throw new NeutralShapeSamplingException(message);
                throw new InvalidOperationException(message);
            }
            return result;
        }

        private static Dictionary<string, float> FixedMenuValues(RuntimeAnimatorController runtime, VrChatExpressionMenu.Source source,
            Layer[] layers, IDictionary<string, float> defaults, IDictionary<string, float> selection, FixedExpressionContext fixedContext,
            ISet<string> extraMutableParameters, out Dictionary<string, HashSet<string>> derivedInputs,
            out Dictionary<string, float> externalValues, out Dictionary<string, float> motionTimeSets, bool includeMotionTimeSets)
        {
            motionTimeSets = new Dictionary<string, float>(StringComparer.Ordinal);
            derivedInputs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            externalValues = new Dictionary<string, float>(StringComparer.Ordinal);
            var reportedDependencies = derivedInputs;
            var controllers = new[] { runtime }.Concat(source?.OtherControllers ?? Enumerable.Empty<RuntimeAnimatorController>())
                .Where(value => value != null).Distinct().ToArray();
            var written = new HashSet<string>(layers.SelectMany(layer => layer.Writes), StringComparer.Ordinal);
            var inspectedControllers = new Dictionary<RuntimeAnimatorController, Layer[]> { [runtime] = layers };
            // A direct native probe drops its selected slot's callbacks. Their
            // writes must invalidate gate proofs before dependency pruning;
            // the caller then diagnoses any remaining read of those writes.
            if (extraMutableParameters != null) written.UnionWith(extraMutableParameters);
            var arbitrary = false;
            foreach (var other in controllers.Where(value => value != runtime))
            {
                var unknown = new List<string>();
                var inspected = Inspect(other, null, new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), unknown);
                inspectedControllers.Add(other, inspected);
                written.UnionWith(inspected.SelectMany(layer => layer.Writes));
                arbitrary |= unknown.Count > 0;
            }
            var result = new Dictionary<string, float>(StringComparer.Ordinal);
            // An unknown callback may change any gate. Retain the existing
            // conservative checks rather than inventing independence for it.
            if (arbitrary) return result;
            var types = new Dictionary<string, AnimatorControllerParameterType>(StringComparer.Ordinal);
            var conflicting = new HashSet<string>(StringComparer.Ordinal);
            var candidates = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var current in controllers)
            {
                var context = current == runtime ? fixedContext : FixedExpressionContext.Create(current, defaults, source);
                foreach (var parameter in Controller(current).parameters)
                {
                    var name = parameter.name;
                    if (types.TryGetValue(name, out var type) && type != parameter.type)
                    { conflicting.Add(name); continue; }
                    types[name] = parameter.type;
                    var supplied = context.Values.TryGetValue(name, out var initial);
                    if (parameter.type == AnimatorControllerParameterType.Trigger ||
                        !supplied && (VrChatParameterDriver.BuiltIn.Contains(name) || source?.ExternalParameters.Contains(name) == true)) continue;
                    if (!supplied)
                    {
                        initial = parameter.type == AnimatorControllerParameterType.Bool ? (parameter.defaultBool ? 1f : 0f) :
                            parameter.type == AnimatorControllerParameterType.Int ? parameter.defaultInt : parameter.defaultFloat;
                        if (source != null && source.Defaults.TryGetValue(name, out var value)) initial = value;
                        if (defaults != null && defaults.TryGetValue(name, out value)) initial = value;
                    }
                    var final = selection != null && selection.TryGetValue(name, out var selectedValue) ? selectedValue : initial;
                    float Normalize(float value) => parameter.type == AnimatorControllerParameterType.Bool ? (value == 0 ? 0 : 1) :
                        parameter.type == AnimatorControllerParameterType.Int ? Mathf.RoundToInt(value) : value;
                    if (float.IsNaN(initial) || float.IsInfinity(initial) || float.IsNaN(final) || float.IsInfinity(final) ||
                        Normalize(initial) != Normalize(final)) { conflicting.Add(name); continue; }
                    initial = Normalize(initial);
                    if (candidates.TryGetValue(name, out var previous) && previous != initial)
                    { conflicting.Add(name); continue; }
                    candidates[name] = initial;
                    if (supplied) externalValues[name] = initial;
                    if (!written.Contains(name)) result[name] = initial;
                }
            }
            foreach (var name in conflicting) { result.Remove(name); candidates.Remove(name); externalValues.Remove(name); }
            if (includeMotionTimeSets)
                motionTimeSets = FixedMotionTimeSets(runtime, source, candidates, types, selection, extraMutableParameters);

            // Start only with globally unwritten constants. A known Set can
            // preserve an initial value while an independently fixed gate
            // makes every different writer unreachable. Grow the proof from
            // those established constants; a parameter cannot prove its own
            // non-default writer unreachable, or rescue a mutually changing
            // cycle. Never infer constants from samples or discarded callbacks.
            var permanentlyMutable = new HashSet<string>(extraMutableParameters ?? new HashSet<string>(), StringComparer.Ordinal);
            permanentlyMutable.UnionWith(inspectedControllers.Values.SelectMany(info => info).SelectMany(layer => layer.CurveWrites));
            permanentlyMutable.UnionWith(written.Where(VrChatParameterDriver.BuiltIn.Contains));
            var programs = new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>();
            bool KeepsInitial(VrChatParameterDriver.Operation operation, AnimatorControllerParameterType type, float initial)
            {
                if (operation.Kind != "Set" || operation.Error != null || float.IsNaN(operation.Value) || float.IsInfinity(operation.Value)) return false;
                double value = operation.Value;
                if (type == AnimatorControllerParameterType.Bool) value = value == 0 ? 0 : 1;
                else if (type == AnimatorControllerParameterType.Int) value = Math.Truncate(value);
                else if (type != AnimatorControllerParameterType.Float) return false;
                if (source?.ExpressionParameters.Contains(operation.Destination) == true)
                {
                    if (type == AnimatorControllerParameterType.Int) value = Math.Max(0, Math.Min(255, value));
                    else if (type == AnimatorControllerParameterType.Float) value = Math.Max(-1, Math.Min(1, value));
                }
                if (type == AnimatorControllerParameterType.Int && (value < int.MinValue || value > int.MaxValue)) return false;
                return value == initial;
            }
            // Derive reporting provenance separately from the admission
            // proof. Remove redundant assumptions while still proving every
            // unsafe writer unreachable; never report an unrelated reader.
            HashSet<string> RequiredInputs(string name)
            {
                var rawReads = new HashSet<string>(inspectedControllers.Values.SelectMany(info => info)
                    .Where(layer => layer.DriverWrites.Contains(name)).SelectMany(layer => layer.Reads), StringComparer.Ordinal);
                var subset = result.Where(pair => rawReads.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                bool Preserves(IDictionary<string, float> proof)
                {
                    foreach (var pair in inspectedControllers)
                    {
                        var current = Controller(pair.Key); var currentLayers = current.layers;
                        var matchingParameters = current.parameters.Where(parameter => parameter.name == name).ToArray();
                        AnimatorControllerParameterType? type = matchingParameters.Length == 1 ? matchingParameters[0].type : (AnimatorControllerParameterType?)null;
                        for (var index = 0; index < pair.Value.Length; index++)
                        {
                            if (!pair.Value[index].DriverWrites.Contains(name)) continue;
                            if (type == null || currentLayers[index].syncedLayerIndex >= 0 ||
                                !FixedExpressionContext.TryReachableFxCommands(currentLayers[index].stateMachine, proof, current.parameters, out var commands)) return false;
                            foreach (var behaviour in commands.Where(VrChatParameterDriver.IsDriver))
                            {
                                if (!programs.TryGetValue(behaviour, out var program))
                                    programs.Add(behaviour, program = VrChatParameterDriver.Read(behaviour, current.name));
                                if (program.Error != null || program.Operations.Any(operation => operation.Destination == name &&
                                    !KeepsInitial(operation, type.Value, candidates[name]))) return false;
                            }
                        }
                    }
                    return true;
                }
                foreach (var input in subset.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray())
                {
                    var value = subset[input]; subset.Remove(input);
                    if (!Preserves(subset)) subset.Add(input, value);
                }
                return new HashSet<string>(subset.Keys.SelectMany(input => reportedDependencies.TryGetValue(input, out var previous) ?
                    previous : new HashSet<string>(new[] { input }, StringComparer.Ordinal)), StringComparer.Ordinal);
            }
            bool expanded;
            do
            {
                var unsafeWrites = new HashSet<string>(permanentlyMutable, StringComparer.Ordinal);
                foreach (var pair in inspectedControllers)
                {
                    var current = Controller(pair.Key);
                    var currentTypes = current.parameters.GroupBy(parameter => parameter.name).Where(group => group.Count() == 1)
                        .ToDictionary(group => group.Key, group => group.Single().type, StringComparer.Ordinal);
                    var currentLayers = current.layers;
                    for (var index = 0; index < pair.Value.Length; index++)
                    {
                        var raw = pair.Value[index];
                        if (raw.DriverWrites.Count == 0) continue;
                        if (currentLayers[index].syncedLayerIndex >= 0 ||
                            !FixedExpressionContext.TryReachableFxCommands(currentLayers[index].stateMachine, result, current.parameters, out var commands))
                        { unsafeWrites.UnionWith(raw.DriverWrites); continue; }
                        foreach (var behaviour in commands.Where(VrChatParameterDriver.IsDriver))
                        {
                            if (!programs.TryGetValue(behaviour, out var program))
                                programs.Add(behaviour, program = VrChatParameterDriver.Read(behaviour, current.name));
                            if (program.Error != null) { unsafeWrites.UnionWith(raw.DriverWrites); continue; }
                            foreach (var operation in program.Operations)
                                if (!candidates.TryGetValue(operation.Destination, out var initial) ||
                                    !currentTypes.TryGetValue(operation.Destination, out var type) ||
                                    !KeepsInitial(operation, type, initial)) unsafeWrites.Add(operation.Destination);
                        }
                    }
                }
                expanded = false;
                foreach (var candidate in candidates)
                    if (!result.ContainsKey(candidate.Key) && !unsafeWrites.Contains(candidate.Key))
                    {
                        reportedDependencies[candidate.Key] = RequiredInputs(candidate.Key);
                        result.Add(candidate.Key, candidate.Value); expanded = true;
                    }
            } while (expanded);
            return result;
        }

        private static Dictionary<string, float> FixedMotionTimeSets(RuntimeAnimatorController runtime, VrChatExpressionMenu.Source source,
            IDictionary<string, float> candidates, IDictionary<string, AnimatorControllerParameterType> types,
            IDictionary<string, float> selection, ISet<string> extraMutableParameters)
        {
            var result = new Dictionary<string, float>(StringComparer.Ordinal);
            if (runtime == null || source?.OtherControllers.Any(value => value == null) == true) return result;
            var eligible = new HashSet<string>(candidates.Keys.Where(name =>
                types.TryGetValue(name, out var type) && type == AnimatorControllerParameterType.Float &&
                !VrChatParameterDriver.BuiltIn.Contains(name) && source?.ExternalParameters.Contains(name) != true &&
                selection?.ContainsKey(name) != true && extraMutableParameters?.Contains(name) != true), StringComparer.Ordinal);
            if (eligible.Count == 0) return result;
            var rejected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var current in new[] { runtime }.Concat(source?.OtherControllers ?? Enumerable.Empty<RuntimeAnimatorController>()).Distinct())
            {
                var controller = Controller(current);
                var parameters = controller.parameters;
                if (parameters.Any(parameter => parameter == null || string.IsNullOrEmpty(parameter.name)) ||
                    parameters.GroupBy(parameter => parameter.name).Any(group => group.Count() != 1) ||
                    controller.layers.Any(layer => layer.syncedLayerIndex >= 0) ||
                    current.animationClips.Where(clip => clip != null).Any(clip => AnimationUtility.GetAnimationEvents(clip).Length != 0))
                    return new Dictionary<string, float>(StringComparer.Ordinal);
                var declared = parameters.ToDictionary(parameter => parameter.name, parameter => parameter.type, StringComparer.Ordinal);
                foreach (var name in eligible)
                    if (declared.TryGetValue(name, out var type) && type != AnimatorControllerParameterType.Float) rejected.Add(name);
                var programs = new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>();
                var unknown = new List<string>();
                // Inspect every authored state, effective synced callback and
                // replacement clip without exclusions or a reachability proof.
                // A dormant different writer still invalidates this invariant.
                var raw = Inspect(current, null, programs, unknown, true);
                if (unknown.Count != 0) return new Dictionary<string, float>(StringComparer.Ordinal);
                rejected.UnionWith(raw.SelectMany(layer => layer.CurveWrites).Where(eligible.Contains));
                foreach (var program in programs.Values)
                {
                    if (program.Error != null) return new Dictionary<string, float>(StringComparer.Ordinal);
                    foreach (var operation in program.Operations.Where(operation => eligible.Contains(operation.Destination)))
                    {
                        var name = operation.Destination;
                        if (operation.Kind != "Set" || operation.Error != null || float.IsNaN(operation.Value) || float.IsInfinity(operation.Value) ||
                            !declared.TryGetValue(name, out var type) || type != AnimatorControllerParameterType.Float)
                        { rejected.Add(name); continue; }
                        // Match Execute: expression Float parameters clamp to
                        // [-1,1] after the finite literal Set value is decoded.
                        var value = source?.ExpressionParameters.Contains(name) == true ? Mathf.Clamp(operation.Value, -1, 1) : operation.Value;
                        if (result.TryGetValue(name, out var previous) && previous != value) rejected.Add(name);
                        else result[name] = value;
                    }
                }
            }
            foreach (var name in rejected) result.Remove(name);
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
                    var unsupported = new List<string>();
                    var otherLayers = Inspect(other, null, new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), unknown,
                        neutralUnsupported: unsupported);
                    if (unknown.Count > 0)
                        throw new InvalidOperationException("FX以外のPlayable Layerに不正なState Behaviourがあります: " + other.name + " / " + string.Join(", ", unknown));
                    if (unsupported.Count > 0)
                        throw new NeutralShapeSamplingException("FX以外のPlayable LayerのState Behaviourを再現できません: " + other.name + " / " + string.Join(", ", unsupported));
                    otherWritten.UnionWith(otherLayers.SelectMany(layer => layer.Writes));
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

        private static bool? FixedMenuCondition(AnimatorCondition condition, IDictionary<string, float> values,
            IDictionary<string, AnimatorControllerParameterType> types)
        {
            if (values == null || !values.TryGetValue(condition.parameter, out var value) ||
                !types.TryGetValue(condition.parameter, out var type)) return null;
            if (type == AnimatorControllerParameterType.Bool)
            {
                if (condition.mode == AnimatorConditionMode.If) return value != 0;
                if (condition.mode == AnimatorConditionMode.IfNot) return value == 0;
                return null;
            }
            if (type == AnimatorControllerParameterType.Trigger || float.IsNaN(condition.threshold) || float.IsInfinity(condition.threshold) ||
                type == AnimatorControllerParameterType.Int && condition.threshold != Math.Truncate(condition.threshold)) return null;
            switch (condition.mode)
            {
                case AnimatorConditionMode.Equals: return type == AnimatorControllerParameterType.Int ? value == condition.threshold : (bool?)null;
                case AnimatorConditionMode.NotEqual: return type == AnimatorControllerParameterType.Int ? value != condition.threshold : (bool?)null;
                case AnimatorConditionMode.Greater: return value > condition.threshold;
                case AnimatorConditionMode.Less: return value < condition.threshold;
                default: return null;
            }
        }

        // Over-approximate every state reachable from fixed neutral inputs,
        // including nested entry/exit and ancestor Any State transitions. A
        // transition is pruned only when an AND condition is provably false.
        private static NeutralReachability NeutralStates(AnimatorStateMachine root, IDictionary<string, float> fixedValues,
            IDictionary<string, AnimatorControllerParameterType> fixedTypes = null)
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
                return all.Where(value => !value.mute && (!solo || value.solo) && !(fixedTypes == null ? IsFalse(value, fixedValues) :
                    value.conditions.Any(condition => FixedMenuCondition(condition, fixedValues, fixedTypes) == false)));
            }
            bool Always(AnimatorTransitionBase transition) => transition.conditions.All(condition =>
            {
                if (fixedTypes != null) return FixedMenuCondition(condition, fixedValues, fixedTypes) == true;
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
            IDictionary<string, float> neutralFixed = null, List<string> neutralUnsupported = null, bool validateFixedData = true)
        {
            var controller = Controller(runtime);
            var replacements = Overrides(runtime);
            var layers = controller.layers;
            var validateNeutralData = neutralUnsupported != null || neutralFixed != null && validateFixedData;
            var fixedTypes = !validateFixedData && neutralFixed != null ? controller.parameters.GroupBy(parameter => parameter.name)
                .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single().type, StringComparer.Ordinal) : null;
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
                var reached = layers[index].syncedLayerIndex < 0 ? NeutralStates(layers[sourceIndex].stateMachine, neutralFixed, fixedTypes) : null;
                void ReadParameter(string name) { if (!string.IsNullOrEmpty(name)) info.Reads.Add(name); }
                void Conditions(IEnumerable<AnimatorTransitionBase> transitions)
                {
                    var siblings = transitions.ToArray();
                    var solo = siblings.Any(t => t.solo);
                    foreach (var transition in siblings.Where(t => !t.mute && (!solo || t.solo) && !(fixedTypes == null ? IsFalse(t, neutralFixed) :
                        t.conditions.Any(condition => FixedMenuCondition(condition, neutralFixed, fixedTypes) == false))))
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
                            // An arbitrary callback prevents a neutral proof,
                            // but a missing script or malformed SDK control is
                            // still invalid data rather than a capability limit.
                            var target = behaviour == null || IsMalformedSdkControl(behaviour) ? unknown : neutralUnsupported ?? unknown;
                            target.Add(path + " / " + (behaviour == null ? "欠けたBehaviour" : behaviour.GetType().Name));
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
                            if (validateNeutralData && !binding.isPPtrCurve &&
                                (binding.type == typeof(Animator) || excludedPath?.Invoke(binding.path) != true))
                            {
                                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                                // A declared float binding must have readable keys.
                                // Unity may return an empty curve for non-finite
                                // authored values; do not let it pass as a static
                                // curve before dependency pruning or fallback.
                                // Object-reference bindings use a separate API.
                                if (curve == null || curve.length == 0 || VrChatGestureExpressions.HasInvalidCurveNumbers(curve))
                                    throw new InvalidOperationException("FXのアニメーション曲線に不正な値があります: " + clip.name + " / " + binding.path + " / " + binding.propertyName);
                            }
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
                                        // Neutral discovery evaluates native clips rather than
                                        // exporting their animation data. Finite static curves
                                        // need no portable key-count, time or value limits.
                                        info.DynamicMorph |= !VrChatGestureExpressions.IsConstantCurve(curve);
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
                        // Synced slots may replace the source state's callbacks
                        // independently of its motion. Use the effective list
                        // for both dropped-driver checks and dependency closure;
                        // an intentionally empty override must stay empty.
                        var behaviours = layers[index].syncedLayerIndex < 0 ? state.behaviours :
                            controller.GetStateEffectiveBehaviours(state, index) ?? Array.Empty<StateMachineBehaviour>();
                        Behaviours(behaviours, path + "/" + state.name, true);
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

        private static bool IsMalformedSdkControl(StateMachineBehaviour behaviour)
        {
            var name = behaviour.GetType().FullName;
            using var data = new SerializedObject(behaviour);
            bool WrongType(SerializedProperty property, SerializedPropertyType type) => property != null && property.propertyType != type;
            if (name == "VRC.SDK3.Avatars.Components.VRCAnimatorTemporaryPoseSpace" || name == "VRC.SDKBase.VRC_AnimatorTemporaryPoseSpace")
            {
                var enter = data.FindProperty("enterPoseSpace"); var fixedDelay = data.FindProperty("fixedDelay"); var delay = data.FindProperty("delayTime");
                return WrongType(enter, SerializedPropertyType.Boolean) || WrongType(fixedDelay, SerializedPropertyType.Boolean) ||
                    WrongType(delay, SerializedPropertyType.Float) || delay != null &&
                    (float.IsNaN(delay.floatValue) || float.IsInfinity(delay.floatValue) || delay.floatValue < 0);
            }
            if (name == "VRC.SDK3.Avatars.Components.VRCAnimatorLocomotionControl" || name == "VRC.SDKBase.VRC_AnimatorLocomotionControl")
                return WrongType(data.FindProperty("disableLocomotion"), SerializedPropertyType.Boolean);
            if (name != "VRC.SDK3.Avatars.Components.VRCPlayableLayerControl" && name != "VRC.SDKBase.VRC_PlayableLayerControl") return false;
            var layer = data.FindProperty("layer"); var weight = data.FindProperty("goalWeight"); var duration = data.FindProperty("blendDuration");
            // A different SDK schema is an unsupported capability. Reject only
            // fields we can read and prove malformed in this schema.
            return WrongType(layer, SerializedPropertyType.Enum) || WrongType(weight, SerializedPropertyType.Float) || WrongType(duration, SerializedPropertyType.Float) ||
                layer != null && (layer.enumValueIndex < 0 || layer.enumValueIndex >= layer.enumNames.Length) ||
                weight != null && (float.IsNaN(weight.floatValue) || float.IsInfinity(weight.floatValue) || weight.floatValue < 0 || weight.floatValue > 1) ||
                duration != null && (float.IsNaN(duration.floatValue) || float.IsInfinity(duration.floatValue) || duration.floatValue < 0);
        }
    }
}
