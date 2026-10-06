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
            internal readonly HashSet<string> ControlReads = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<(string Source, string Destination)> CopyInputs = new HashSet<(string Source, string Destination)>();
            internal readonly HashSet<string> Writes = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<string> DriverWrites = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<string> CurveWrites = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<EditorCurveBinding> Morphs = new HashSet<EditorCurveBinding>();
            internal readonly HashSet<AnimationClip> Clips = new HashSet<AnimationClip>();
            internal readonly List<VrChatParameterDriver.Program> DriverPrograms = new List<VrChatParameterDriver.Program>();
            internal readonly Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program> FxCommands = new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>();
            internal readonly Dictionary<StateMachineBehaviour, SdkLayerWeightControl> WeightControls = new Dictionary<StateMachineBehaviour, SdkLayerWeightControl>();
            internal bool WriteDefaults, HasBindings, EmptyMotion, FxControl, DynamicMorph, Timed, NonMorphBindings;
        }

        internal readonly HashSet<int> Layers = new HashSet<int>();
        internal readonly HashSet<int> NativeSupportLayers = new HashSet<int>();
        internal bool HasFxControls;
        internal readonly HashSet<StateMachineBehaviour> IgnoredWeightControls = new HashSet<StateMachineBehaviour>();
        internal int IndependentTopOverrideLayer = -1;
        internal readonly HashSet<string> Parameters = new HashSet<string>(StringComparer.Ordinal);
        internal readonly HashSet<EditorCurveBinding> Morphs = new HashSet<EditorCurveBinding>();
        // Output capture can be restricted by a prepared neutral plan. Keep
        // the complete morph dependency closure for independence diagnostics;
        // a coupled automatic writer must not become independent just because
        // its companion channels are outside this particular capture group.
        internal readonly HashSet<EditorCurveBinding> NeutralDependencyMorphs = new HashSet<EditorCurveBinding>();
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
            var layers = Inspect(runtime, excludedPath, new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), unknown);
            if (unknown.Count > 0)
                throw new InvalidOperationException("FXの影響範囲を確定できないState Behaviourがあります: " + string.Join(", ", unknown));
            var weight = layers.SelectMany(layer => layer.WeightControls.Values).FirstOrDefault(control => control.Playable == "FX");
            if (weight != null) throw WeightControlCapability(weight, false);
        }

        internal static HashSet<string> SelectedLayerDriverWrites(RuntimeAnimatorController runtime, int layerIndex, Func<string, bool> excludedPath)
        {
            var unknown = new List<string>();
            var layers = Inspect(runtime, excludedPath, new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), unknown);
            if (unknown.Count > 0)
                throw new InvalidOperationException("FXの影響範囲を確定できないState Behaviourがあります: " + string.Join(", ", unknown));
            var weight = layers.SelectMany(layer => layer.WeightControls.Values).FirstOrDefault(control => control.Playable == "FX");
            if (weight != null) throw WeightControlCapability(weight, false);
            return layers[layerIndex].DriverWrites;
        }

        internal static void ValidateAdditionalProbeBehaviours(RuntimeAnimatorController runtime,
            VrChatExpressionMenu.Source source, FixedExpressionContext fixedContext, ISet<EditorCurveBinding> morphs)
        {
            foreach (var other in InspectOtherControllers(runtime, source, fixedContext))
            {
                var writes = other.Layers.SelectMany(layer => layer.Morphs).Where(morphs.Contains)
                    .Select(binding => binding.path + "/" + binding.propertyName).Distinct();
                var unresolved = other.Unknown.Concat(writes).ToArray();
                if (unresolved.Length > 0)
                    throw new InvalidOperationException("FX以外のPlayable Layerからの変更を再現できません: " +
                        other.Runtime.name + " / " + string.Join(", ", unresolved));
                var weight = other.Layers.SelectMany(layer => layer.WeightControls.Values).FirstOrDefault(control => control.Playable == "FX");
                if (weight != null) throw WeightControlCapability(weight, false);
            }
        }

        internal static ExpressionDependencies Analyze(RuntimeAnimatorController runtime, IEnumerable<string> selected,
            Func<string, bool> excludedPath, VrChatExpressionMenu.Source source = null, IDictionary<string, float> defaults = null, IDictionary<string, float> selection = null,
            IEnumerable<EditorCurveBinding> initialMorphs = null, bool preserveNativeBasePose = false, FixedExpressionContext fixedContext = null)
            => AnalyzeCore(runtime, selected, excludedPath, source, defaults, selection, null,
                fixedContext: fixedContext, initialMorphs: initialMorphs, preserveNativeBasePose: preserveNativeBasePose);

        internal static IEnumerable<HashSet<EditorCurveBinding>> NeutralRoots(RuntimeAnimatorController runtime, Func<string, bool> excludedPath,
            VrChatExpressionMenu.Source source, ISet<EditorCurveBinding> automatic = null, FixedExpressionContext fixedContext = null)
        {
            var unknown = new List<string>();
            var layers = Inspect(runtime, excludedPath, new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), unknown, true);
            if (layers.All(layer => layer.Morphs.Count == 0)) return Array.Empty<HashSet<EditorCurveBinding>>();
            if (unknown.Count > 0)
                throw new InvalidOperationException("FXの影響範囲を確定できないState Behaviourがあります: " + string.Join(", ", unknown));
            var trackingValues = FixedNeutralValues(runtime, source, layers, excludedPath);
            var trackingLayers = fixedContext == null ? null : Inspect(runtime, excludedPath,
                new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), new List<string>(), true, trackingValues);
            var fixedValues = fixedContext == null ? trackingValues : FixedNeutralValues(runtime, source, layers, excludedPath, fixedContext);
            layers = Inspect(runtime, excludedPath, new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(),
                new List<string>(), true, fixedValues);
            RemoveIndependentAutomaticWriters(layers, automatic, source, trackingValues, trackingLayers);
            return layers.Where(layer => layer.Morphs.Count > 0).Select(layer => new HashSet<EditorCurveBinding>(layer.Morphs));
        }

        internal static ExpressionDependencies AnalyzeNeutral(RuntimeAnimatorController runtime, ISet<EditorCurveBinding> morphs,
            Func<string, bool> excludedPath, VrChatExpressionMenu.Source source, ISet<EditorCurveBinding> automatic = null,
            FixedExpressionContext fixedContext = null, bool preserveCommittedMorphs = false)
        {
            var defaults = new Dictionary<string, float>(source.Defaults, StringComparer.Ordinal);
            if (fixedContext != null)
                foreach (var input in fixedContext.Values) defaults[input.Key] = input.Value;
            return AnalyzeCore(runtime, Array.Empty<string>(), excludedPath, source, defaults,
                new Dictionary<string, float>(), morphs, automatic, fixedContext, initialMorphs: morphs, preserveNativeBasePose: true,
                preserveCommittedMorphs: preserveCommittedMorphs);
        }

        // Appearance ownership includes custom menu alternatives. Normal
        // external inputs and proven transient cross-playable signals may
        // rule out dormant branches; authored wardrobe defaults alone cannot.
        internal static Layer[] NormalInputLayers(RuntimeAnimatorController runtime,
            VrChatExpressionMenu.Source source, FixedExpressionContext fixedContext, Func<string, bool> excludedPath,
            IDictionary<string, float> selection = null)
        {
            var unknown = new List<string>();
            var raw = Inspect(runtime, excludedPath, new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), unknown, true);
            if (fixedContext == null) return raw;
            var rawOther = new List<Layer>();
            var declarations = new Dictionary<string, List<AnimatorControllerParameter>>(StringComparer.Ordinal);
            foreach (var controller in new[] { runtime }.Concat(source?.OtherControllers.Where(value => value != null) ??
                Enumerable.Empty<RuntimeAnimatorController>()))
            {
                foreach (var parameter in Controller(controller).parameters)
                {
                    if (!declarations.TryGetValue(parameter.name, out var values))
                        declarations.Add(parameter.name, values = new List<AnimatorControllerParameter>());
                    values.Add(parameter);
                }
                if (controller != runtime)
                    rawOther.AddRange(Inspect(controller, null, new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), unknown,
                        typedConditions: true, fxLayerCount: Controller(runtime).layers.Length));
            }
            // Unknown callbacks can invalidate every constant. Retain their
            // complete clip graph so the dependency guards diagnose them.
            if (unknown.Count > 0) return raw;
            var fxWritten = new HashSet<string>(raw.SelectMany(layer => layer.Writes), StringComparer.Ordinal);
            var otherWritten = new HashSet<string>(rawOther.SelectMany(layer => layer.Writes), StringComparer.Ordinal);
            var written = new HashSet<string>(fxWritten, StringComparer.Ordinal); written.UnionWith(otherWritten);
            var incompatible = new HashSet<string>(declarations.Where(pair => pair.Value.Select(value => value.type).Distinct().Count() != 1)
                .Select(pair => pair.Key), StringComparer.Ordinal);
            var invariant = fixedContext.Values.Where(pair => !written.Contains(pair.Key) && !incompatible.Contains(pair.Key) &&
                    (selection == null || !selection.TryGetValue(pair.Key, out var selected) || selected == pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (source?.NeutralInputInventoryComplete == true)
            {
                var normalOther = InspectOtherControllers(runtime, source, fixedContext, selection: selection);
                if (normalOther.All(item => item.Unknown.Count == 0))
                {
                    var reachableWrites = new HashSet<string>(normalOther.SelectMany(item => item.Layers).SelectMany(layer => layer.Writes), StringComparer.Ordinal);
                    var locallyNormal = !written.Contains("IsLocal") && !incompatible.Contains("IsLocal") &&
                        (selection == null || !selection.TryGetValue("IsLocal", out var selectedLocal) || selectedLocal == 1) &&
                        (fixedContext.Values.TryGetValue("IsLocal", out var local) ? local == 1 :
                            source.Defaults.TryGetValue("IsLocal", out local) && local == 1);
                    foreach (var parameter in Controller(runtime).parameters)
                    {
                        var name = parameter.name;
                        // Only a signal with actual cross-playable producers
                        // can qualify. Unwritten user options never become an
                        // ownership constant merely from their default value.
                        if (!otherWritten.Contains(name) || fxWritten.Contains(name) || reachableWrites.Contains(name) ||
                            incompatible.Contains(name) || VrChatParameterDriver.BuiltIn.Contains(name) || source.ExternalParameters.Contains(name) ||
                            source.MenuInputs.Contains(name) || selection?.ContainsKey(name) == true ||
                            !source.ExpressionParameters.Contains(name) || !source.ParameterPersistence.TryGetValue(name, out var persistence) ||
                            persistence.Saved || persistence.NetworkSynced && !locallyNormal ||
                            !source.ExpressionParameterTypes.TryGetValue(name, out var declaredType) || declaredType != parameter.type.ToString() ||
                            parameter.type == AnimatorControllerParameterType.Trigger) continue;
                        float Default(AnimatorControllerParameter value) => value.type == AnimatorControllerParameterType.Bool ? (value.defaultBool ? 1 : 0) :
                            value.type == AnimatorControllerParameterType.Int ? value.defaultInt : value.defaultFloat;
                        var initial = Default(parameter);
                        if (!NeutralShapeSnapshot.Finite(initial) || !source.Defaults.TryGetValue(name, out var declaredDefault) ||
                            !NeutralShapeSnapshot.Finite(declaredDefault) || declaredDefault != initial ||
                            declarations[name].Any(value => !NeutralShapeSnapshot.Finite(Default(value)) || Default(value) != initial)) continue;
                        invariant[name] = initial;
                    }
                }
            }
            return Inspect(runtime, excludedPath, new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(),
                new List<string>(), true, invariant);
        }

        internal static HashSet<AnimationClip> NeutralClips(RuntimeAnimatorController runtime, IDictionary<string, float> fixedValues,
            Func<string, bool> excludedPath) => new HashSet<AnimationClip>(Inspect(runtime, excludedPath,
                new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), new List<string>(), true, fixedValues)
                .SelectMany(layer => layer.Clips));

        private static ExpressionDependencies AnalyzeCore(RuntimeAnimatorController runtime, IEnumerable<string> selected,
            Func<string, bool> excludedPath, VrChatExpressionMenu.Source source, IDictionary<string, float> defaults,
            IDictionary<string, float> selection, ISet<EditorCurveBinding> neutralMorphs, ISet<EditorCurveBinding> automatic = null,
            FixedExpressionContext fixedContext = null, IEnumerable<EditorCurveBinding> initialMorphs = null, bool preserveNativeBasePose = false,
            bool preserveCommittedMorphs = false)
        {
            var result = new ExpressionDependencies();
            var requiredMorphs = new HashSet<EditorCurveBinding>(initialMorphs ?? Enumerable.Empty<EditorCurveBinding>());
            result.Morphs.UnionWith(requiredMorphs);
            var controller = Controller(runtime);
            var unknown = new List<string>();
            var info = Inspect(runtime, excludedPath, result.Drivers, unknown, true);
            var rawWeightControls = info.SelectMany(layer => layer.WeightControls.Keys).ToArray();
            var nativeHasBindings = info.Select(layer => layer.HasBindings).ToArray();
            result.HasFxControls = info.Any(layer => layer.FxControl);
            // Arbitrary behaviours can affect any parameter, layer or scene
            // object. No name/path-based independence claim is safe for them.
            if (unknown.Count > 0) throw new InvalidOperationException("FXの影響範囲を確定できないState Behaviourがあります: " + string.Join(", ", unknown));
            var otherControllers = InspectOtherControllers(runtime, source, fixedContext, defaults, selection);
            if (neutralMorphs != null)
            {
                var trackingValues = FixedNeutralValues(runtime, source, info, excludedPath);
                var trackingLayers = fixedContext == null ? null : Inspect(runtime, excludedPath,
                    new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), new List<string>(), true, trackingValues);
                var fixedValues = fixedContext == null ? trackingValues : FixedNeutralValues(runtime, source, info, excludedPath, fixedContext);
                foreach (var pair in fixedValues) result.NeutralFixedValues.Add(pair.Key, pair.Value);
                // Keep all driver programs for the private controller copy, but
                // root neutral dependencies only in reachable default states.
                info = Inspect(runtime, excludedPath, result.Drivers, new List<string>(), true, result.NeutralFixedValues);
                // Live automatic layers still contribute native defaults to
                // additive and fractional blending. Remember their activity
                // before removing their morphs from neutral capture.
                nativeHasBindings = info.Select(layer => layer.HasBindings).ToArray();
                RemoveIndependentAutomaticWriters(info, automatic, source, trackingValues, trackingLayers);
            }
            var gateExternal = new HashSet<string>(StringComparer.Ordinal);
            var unsafeFxCommands = new List<VrChatParameterDriver.Program>();
            if (result.HasFxControls)
            {
                var writers = new HashSet<string>(info.SelectMany(layer => layer.Writes), StringComparer.Ordinal);
                foreach (var other in otherControllers)
                {
                    writers.UnionWith(other.Layers.SelectMany(layer => layer.Writes));
                    // An arbitrary callback could invalidate any constant.
                    if (other.Unknown.Count > 0) writers.UnionWith(controller.parameters.Select(p => p.name));
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
            bool AdditiveDefaults(int index) => index > 0 && nativeHasBindings[index] &&
                controller.layers[index].blendingMode == AnimatorLayerBlendingMode.Additive &&
                controller.layers[index].defaultWeight > 0;
            // Unity's additive generic-property stream includes authored
            // defaults for other animated morphs, even with WD Off. A clip
            // binding only a pupil can therefore add to a base face setting.
            // Keep disjoint layers as support, rather than baking their live
            // automatic or unrelated morphs into this dependency root.
            for (var index = 1; index < info.Length; index++)
                if (AdditiveDefaults(index) && !info[index].Morphs.Overlaps(requiredMorphs))
                    result.NativeSupportLayers.Add(index);
            // Classify native base support before Write Defaults closure can
            // promote it into the evaluation graph. An unrelated base motion
            // stays outside capture even when its native defaults matter.
            var needsNativeBasePose = preserveNativeBasePose && info.Length > 0 && nativeHasBindings[0] &&
                Enumerable.Range(1, Math.Max(0, info.Length - 1)).Any(index =>
                    (AdditiveDefaults(index) || controller.layers[index].blendingMode == AnimatorLayerBlendingMode.Override &&
                    controller.layers[index].defaultWeight > 0 && controller.layers[index].defaultWeight < 1) &&
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
                    var implicitWriter = result.Morphs.Count > 0 && (layer.WriteDefaults || layer.EmptyMotion || AdditiveDefaults(i));
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
            if (neutralMorphs != null) result.NeutralDependencyMorphs.UnionWith(result.Morphs);
            if (preserveCommittedMorphs)
            {
                // A neutral plan owns its explicit morph roots. WD, drivers
                // and additive defaults still require the full native closure,
                // but they must not promote unrelated appearance channels into
                // that plan's output. Keep their states as native support.
                result.Morphs.IntersectWith(requiredMorphs);
                foreach (var index in result.Layers)
                    if (!info[index].Morphs.Overlaps(requiredMorphs)) result.NativeSupportLayers.Add(index);
                if (neutralMorphs != null)
                    result.IndependentTopOverrideLayer = NeutralParameterDependencies.IndependentTopOverride(runtime, result.Morphs, excludedPath);
            }
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
            // Fixed menu sampling has no authored-rest fallback. Preserve its
            // existing actionable missing-input diagnostic precedence.
            if (external.Length > 0 && neutralMorphs == null)
                throw new InvalidOperationException("外部入力に依存する表情の値を確定できません: " + string.Join(", ", external));
            foreach (var other in otherControllers)
            {
                var writes = other.Layers.SelectMany(l => l.Writes).Where(result.Parameters.Contains).Distinct().ToArray();
                var otherMorphs = neutralMorphs == null && fixedContext == null ? Array.Empty<string>() : other.Layers.SelectMany(layer => layer.Morphs)
                    .Where(result.Morphs.Contains).Select(binding => binding.path + "/" + binding.propertyName).Distinct().ToArray();
                if (writes.Length > 0 || other.Unknown.Count > 0 || otherMorphs.Length > 0)
                    throw new InvalidOperationException("FX以外のPlayable Layerからの変更を再現できません: " + other.Runtime.name + " / " +
                        string.Join(", ", writes.Concat(other.Unknown).Concat(otherMorphs)));
            }
            if (unsafeFxCommands.Count > 0)
            {
                var message = unsafeFxCommands[0].Location + " / VRCPlayableLayerControl: FXの重みを変更する状態は固定表情に変換できません。";
                if (neutralMorphs != null) throw new NeutralShapeSamplingException(message, dependencyMorphs: result.NeutralDependencyMorphs);
                throw new InvalidOperationException(message);
            }
            var weightControls = info.SelectMany(layer => layer.WeightControls.Values)
                .Concat(otherControllers.SelectMany(other => other.Layers).SelectMany(layer => layer.WeightControls.Values))
                .Where(control => control.Playable == "FX").ToArray();
            // A documented SDK weight command is a known effect, not an arbitrary
            // callback. Retain prepared rest if its effect cannot be reproduced.
            // Only a final explicit full override can make all captured scalars
            // independent of every controlled lower layer. Global/top controls
            // invalidate that proof even when their goal happens to be one.
            var unresolvedWeight = weightControls.FirstOrDefault(control => neutralMorphs == null ||
                result.IndependentTopOverrideLayer < 0 || !control.AnimatorLayer || control.LayerIndex >= result.IndependentTopOverrideLayer);
            if (unresolvedWeight != null) throw WeightControlCapability(unresolvedWeight, neutralMorphs != null, result.NeutralDependencyMorphs);
            // The controller copy retains every state, including states proven
            // unreachable from fixed inputs. These exact validated identities
            // can be removed only after the capability/reachability guards pass.
            result.IgnoredWeightControls.UnionWith(rawWeightControls);
            // Missing live inputs can retain authored neutral weights, but must
            // not hide unsupported writers or callbacks found in the same graph.
            // A final explicit full override fixes every captured morph even
            // while the retained lower layers read unresolved live inputs.
            if (external.Length > 0 && result.IndependentTopOverrideLayer < 0)
            {
                var message = "外部入力に依存する表情の値を確定できません: " + string.Join(", ", external);
                throw new NeutralShapeSamplingException(message, dependencyMorphs: result.NeutralDependencyMorphs);
            }
            return result;
        }

        private static InvalidOperationException WeightControlCapability(SdkLayerWeightControl control, bool neutral,
            IEnumerable<EditorCurveBinding> morphs = null)
        {
            var message = ExporterLocalization.T("FXのレイヤー重み制御の影響範囲を固定表情として再現できません: ") +
                control.Location + " / " + (control.AnimatorLayer ? "VRCAnimatorLayerControl" : "VRCPlayableLayerControl") +
                " / " + control.Playable + (control.AnimatorLayer ? " layer " + control.LayerIndex : "") +
                " / goalWeight=" + control.GoalWeight.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " / blendDuration=" + control.BlendDuration.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return neutral ? new NeutralShapeSamplingException(message, dependencyMorphs: morphs) : new InvalidOperationException(message);
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
            VrChatExpressionMenu.Source source, IDictionary<string, float> fixedValues, Layer[] trackingLayers = null)
        {
            if (automatic == null) return;
            for (var index = 0; index < layers.Length; index++)
            {
                var layer = layers[index];
                // Classify live tracking before the fixed export environment
                // selects its quiet input. Otherwise Viseme=0 could turn an
                // independent lip-sync layer into a baked neutral writer.
                var tracking = trackingLayers == null ? layer : trackingLayers[index];
                if (tracking.Morphs.Count == 0 || !tracking.Morphs.All(automatic.Contains) || tracking.WriteDefaults || tracking.FxControl ||
                    tracking.Writes.Count != 0 || tracking.NonMorphBindings) continue;
                var external = tracking.Reads.Any(name => !fixedValues.ContainsKey(name) &&
                    (VrChatParameterDriver.BuiltIn.Contains(name) || source?.ExternalParameters.Contains(name) == true));
                // A timed initializer may write a constant zero and then hold
                // it forever with WD Off. Keep those writes; the sampler checks
                // whether its eventual active state still has a timed exit.
                if (!tracking.DynamicMorph && !external) continue;
                // A live tracking layer may share its automatic channel with a
                // constant DEFAULT FACE underlay. Its other effects must be
                // empty before it can be left out of the neutral graph.
                layer.Morphs.Clear(); layer.HasBindings = false; layer.EmptyMotion = false;
            }
        }

        internal static Dictionary<string, float> FixedNeutralValues(RuntimeAnimatorController runtime, VrChatExpressionMenu.Source source, Layer[] layers,
            Func<string, bool> excludedPath, FixedExpressionContext fixedContext = null)
        {
            var controller = Controller(runtime);
            var written = new HashSet<string>(layers.SelectMany(layer => layer.Writes), StringComparer.Ordinal);
            var otherWritten = new HashSet<string>(StringComparer.Ordinal);
            foreach (var other in InspectOtherControllers(runtime, source, fixedContext))
            {
                otherWritten.UnionWith(other.Layers.SelectMany(layer => layer.Writes));
                if (other.Unknown.Count > 0) otherWritten.UnionWith(controller.parameters.Select(parameter => parameter.name));
            }
            var result = new Dictionary<string, float>(StringComparer.Ordinal);
            var initial = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var parameter in controller.parameters)
            {
                var suppliedInput = fixedContext?.Values.ContainsKey(parameter.name) == true;
                if (otherWritten.Contains(parameter.name) || !suppliedInput && source?.ExternalParameters.Contains(parameter.name) == true ||
                    parameter.type == AnimatorControllerParameterType.Trigger) continue;
                var gesture = parameter.name == "GestureLeft" || parameter.name == "GestureRight" ||
                    parameter.name == "GestureLeftWeight" || parameter.name == "GestureRightWeight";
                var semanticZero = gesture || parameter.name == "AFK";
                if (!suppliedInput && VrChatParameterDriver.BuiltIn.Contains(parameter.name) && !semanticZero && parameter.name != "IsLocal" && parameter.name != "TrackingType") continue;
                var value = semanticZero ? 0f : parameter.type == AnimatorControllerParameterType.Bool ? (parameter.defaultBool ? 1f : 0f) :
                    parameter.type == AnimatorControllerParameterType.Int ? parameter.defaultInt : parameter.defaultFloat;
                if (!semanticZero && source != null && source.Defaults.TryGetValue(parameter.name, out var supplied)) value = supplied;
                if (suppliedInput) value = fixedContext.Values[parameter.name];
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

        internal sealed class OtherControllerInspection
        {
            internal RuntimeAnimatorController Runtime;
            internal Layer[] Layers;
            internal readonly List<string> Unknown = new List<string>();
        }

        internal static List<OtherControllerInspection> InspectNeutralOtherControllers(RuntimeAnimatorController runtime,
            VrChatExpressionMenu.Source source, FixedExpressionContext fixedContext) =>
            InspectOtherControllers(runtime, source, fixedContext);

        // Other playable layers share the avatar's inputs. A dormant AFK or
        // station branch cannot write the sampled face when its fixed input
        // proves the branch unreachable. Keep unknown callbacks and exclude
        // every authored parameter writer from that proof across all layers.
        private static List<OtherControllerInspection> InspectOtherControllers(RuntimeAnimatorController runtime,
            VrChatExpressionMenu.Source source, FixedExpressionContext fixedContext, IDictionary<string, float> defaults = null,
            IDictionary<string, float> selection = null)
        {
            var result = new List<OtherControllerInspection>();
            if (source == null) return result;
            foreach (var other in source.OtherControllers.Where(value => value != null))
            {
                var item = new OtherControllerInspection { Runtime = other };
                item.Layers = Inspect(other, null, new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), item.Unknown,
                    typedConditions: true, fxLayerCount: Controller(runtime).layers.Length);
                result.Add(item);
            }
            if (fixedContext == null || result.Count == 0 || result.Any(item => item.Unknown.Count > 0))
                return result;
            var fxUnknown = new List<string>();
            var fx = Inspect(runtime, null, new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), fxUnknown, true);
            if (fxUnknown.Count > 0) return result;
            var written = new HashSet<string>(fx.SelectMany(layer => layer.Writes)
                .Concat(result.SelectMany(item => item.Layers).SelectMany(layer => layer.Writes)), StringComparer.Ordinal);
            var declarations = new[] { runtime }.Concat(result.Select(item => item.Runtime))
                .SelectMany(value => Controller(value).parameters).GroupBy(parameter => parameter.name, StringComparer.Ordinal);
            var incompatible = new HashSet<string>(declarations.Where(group => group.Select(parameter => parameter.type).Distinct().Count() != 1)
                .Select(group => group.Key), StringComparer.Ordinal);
            foreach (var item in result)
            {
                var controller = Controller(item.Runtime);
                var inputs = FixedExpressionContext.Create(item.Runtime, defaults ?? source.Defaults, source);
                var invariant = new Dictionary<string, float>(StringComparer.Ordinal);
                foreach (var parameter in controller.parameters)
                {
                    if (written.Contains(parameter.name) || incompatible.Contains(parameter.name) ||
                        parameter.type != AnimatorControllerParameterType.Bool && parameter.type != AnimatorControllerParameterType.Int &&
                        parameter.type != AnimatorControllerParameterType.Float || !inputs.Values.TryGetValue(parameter.name, out var initial)) continue;
                    if (fixedContext.Values.TryGetValue(parameter.name, out var supplied)) initial = supplied;
                    var final = selection != null && selection.TryGetValue(parameter.name, out supplied) ? supplied : initial;
                    float Normalize(float value) => parameter.type == AnimatorControllerParameterType.Bool ? (value == 0 ? 0 : 1) :
                        parameter.type == AnimatorControllerParameterType.Int ? Mathf.RoundToInt(value) : value;
                    if (float.IsNaN(initial) || float.IsInfinity(initial) || float.IsNaN(final) || float.IsInfinity(final) ||
                        Normalize(initial) != Normalize(final)) continue;
                    invariant.Add(parameter.name, Normalize(initial));
                }
                // Known SDK weight commands cannot mutate an Animator input,
                // so they do not invalidate this typed reachability proof.
                // Keep each reachable effect separate from arbitrary callbacks,
                // including commands on an active ancestor machine.
                item.Unknown.Clear();
                item.Layers = Inspect(item.Runtime, null, new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(),
                    item.Unknown, neutralFixed: invariant, typedConditions: true,
                    fxLayerCount: Controller(runtime).layers.Length);
            }
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

        private static bool? TypedCondition(AnimatorCondition condition, IDictionary<string, float> fixedValues,
            IDictionary<string, AnimatorControllerParameterType> types)
        {
            if (fixedValues == null || string.IsNullOrEmpty(condition.parameter) ||
                !fixedValues.TryGetValue(condition.parameter, out var value) || !NeutralShapeSnapshot.Finite(value) ||
                !types.TryGetValue(condition.parameter, out var type)) return null;
            if (type == AnimatorControllerParameterType.Bool)
                return condition.mode == AnimatorConditionMode.If ? value != 0 :
                    condition.mode == AnimatorConditionMode.IfNot ? value == 0 : (bool?)null;
            if (type != AnimatorControllerParameterType.Float && type != AnimatorControllerParameterType.Int || !NeutralShapeSnapshot.Finite(condition.threshold) ||
                type == AnimatorControllerParameterType.Int && (value != Math.Truncate(value) || condition.threshold != Math.Truncate(condition.threshold))) return null;
            switch (condition.mode)
            {
                case AnimatorConditionMode.Equals: return type == AnimatorControllerParameterType.Int ? value == condition.threshold : (bool?)null;
                case AnimatorConditionMode.NotEqual: return type == AnimatorControllerParameterType.Int ? value != condition.threshold : (bool?)null;
                case AnimatorConditionMode.Greater: return value > condition.threshold;
                case AnimatorConditionMode.Less: return value < condition.threshold;
                default: return null;
            }
        }

        private static bool IsTypedFalse(AnimatorTransitionBase transition, IDictionary<string, float> fixedValues,
            IDictionary<string, AnimatorControllerParameterType> types) => transition.conditions.Any(condition => TypedCondition(condition, fixedValues, types) == false);

        private sealed class NeutralReachability
        {
            internal readonly HashSet<AnimatorState> States = new HashSet<AnimatorState>();
            internal readonly HashSet<AnimatorStateMachine> Machines = new HashSet<AnimatorStateMachine>();
        }

        // Over-approximate every state reachable from fixed neutral inputs,
        // including nested entry/exit and ancestor Any State transitions. A
        // transition is pruned only when an AND condition is provably false.
        private static NeutralReachability NeutralStates(AnimatorStateMachine root, IDictionary<string, float> fixedValues,
            IDictionary<string, AnimatorControllerParameterType> conditionTypes = null)
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
                return all.Where(value => !value.mute && (!solo || value.solo) &&
                    !(conditionTypes == null ? IsFalse(value, fixedValues) : IsTypedFalse(value, fixedValues, conditionTypes)));
            }
            bool Always(AnimatorTransitionBase transition) => transition.conditions.All(condition =>
            {
                if (conditionTypes != null) return TypedCondition(condition, fixedValues, conditionTypes) == true;
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

        internal static Layer[] Inspect(RuntimeAnimatorController runtime, Func<string, bool> excludedPath,
            Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program> drivers, List<string> unknown, bool allowFxControls = false,
            IDictionary<string, float> neutralFixed = null, bool typedConditions = false, int? fxLayerCount = null)
        {
            var controller = Controller(runtime);
            var replacements = Overrides(runtime);
            var layers = controller.layers;
            var result = new Layer[layers.Length];
            var conditionTypes = !typedConditions && neutralFixed == null ? null : controller.parameters.GroupBy(parameter => parameter.name, StringComparer.Ordinal)
                .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single().type, StringComparer.Ordinal);
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
                var reached = layers[index].syncedLayerIndex < 0 ? NeutralStates(layers[sourceIndex].stateMachine, neutralFixed, conditionTypes) : null;
                void ReadParameter(string name) { if (!string.IsNullOrEmpty(name)) info.Reads.Add(name); }
                void ReadControl(string name) { ReadParameter(name); if (!string.IsNullOrEmpty(name)) info.ControlReads.Add(name); }
                void Conditions(IEnumerable<AnimatorTransitionBase> transitions)
                {
                    var siblings = transitions.ToArray();
                    var solo = siblings.Any(t => t.solo);
                    foreach (var transition in siblings.Where(t => !t.mute && (!solo || t.solo) &&
                        !(conditionTypes == null ? IsFalse(t, neutralFixed) : IsTypedFalse(t, neutralFixed, conditionTypes))))
                    {
                        if (transition is AnimatorStateTransition timed && timed.hasExitTime) info.Timed = true;
                        foreach (var condition in transition.conditions) ReadControl(condition.parameter);
                    }
                }
                void Behaviours(IEnumerable<StateMachineBehaviour> behaviours, string path, bool onState = false)
                {
                    foreach (var behaviour in behaviours)
                    {
                        var sdkWeight = SdkLayerWeightControl.Read(behaviour, path, fxLayerCount ?? layers.Length);
                        if (sdkWeight != null)
                        {
                            if (allowFxControls && onState && VrChatParameterDriver.ReadInstantFxControl(behaviour, path, out var control))
                            {
                                info.FxControl = true;
                                if (!info.FxCommands.ContainsKey(behaviour)) info.FxCommands.Add(behaviour, control);
                                if (!drivers.ContainsKey(behaviour)) drivers.Add(behaviour, control);
                            }
                            else if (!info.WeightControls.ContainsKey(behaviour)) info.WeightControls.Add(behaviour, sdkWeight);
                            continue;
                        }
                        if (VrChatParameterDriver.IsTracking(behaviour) ||
                            VrChatParameterDriver.IsTemporaryPoseSpace(behaviour) || VrChatParameterDriver.IsLocomotionControl(behaviour)) continue;
                        if (!VrChatParameterDriver.IsDriver(behaviour))
                        {
                            unknown.Add(path + " / " + (behaviour == null ? "欠けたBehaviour" : behaviour.GetType().Name));
                            continue;
                        }
                        var program = VrChatParameterDriver.Read(behaviour, path);
                        info.DriverPrograms.Add(program);
                        if (!drivers.ContainsKey(behaviour)) drivers.Add(behaviour, program);
                        if (program.Error != null) { unknown.Add(path + " / Parameter Driver: " + program.Error); continue; }
                        foreach (var op in program.Operations)
                        {
                            info.Writes.Add(op.Destination);
                            info.DriverWrites.Add(op.Destination);
                            if (op.Kind == "Copy")
                            {
                                ReadParameter(op.Source);
                                if (op.Error == null) info.CopyInputs.Add((op.Source, op.Destination));
                            }
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
                            if (tree.blendType != BlendTreeType.Direct)
                            {
                                ReadControl(tree.blendParameter);
                                if (tree.blendType != BlendTreeType.Simple1D) ReadControl(tree.blendParameterY);
                            }
                            foreach (var child in tree.children)
                            {
                                ReadParameter(child.directBlendParameter);
                                if (tree.blendType == BlendTreeType.Direct) ReadControl(child.directBlendParameter);
                                InspectMotion(child.motion);
                            }
                        }
                        if (!(motion is AnimationClip clip)) return;
                        if (replacements.TryGetValue(clip, out var replacement)) clip = replacement;
                        info.Clips.Add(clip);
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
                        // Synced slots may replace the source state's callbacks
                        // independently of its motion. Use the effective list
                        // for both dropped-driver checks and dependency closure;
                        // an intentionally empty override must stay empty.
                        var behaviours = layers[index].syncedLayerIndex < 0 ? state.behaviours :
                            controller.GetStateEffectiveBehaviours(state, index) ?? Array.Empty<StateMachineBehaviour>();
                        Behaviours(behaviours, path + "/" + state.name, true);
                        Conditions(state.transitions);
                        if (state.timeParameterActive) ReadControl(state.timeParameter);
                        if (state.speedParameterActive) ReadControl(state.speedParameter);
                        if (state.cycleOffsetParameterActive) ReadControl(state.cycleOffsetParameter);
                        if (state.mirrorParameterActive) ReadControl(state.mirrorParameter);
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
