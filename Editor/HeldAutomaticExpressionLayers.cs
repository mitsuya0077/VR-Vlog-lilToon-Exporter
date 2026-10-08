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
            FixedExpressionContext context, out HashSet<EditorCurveBinding> preservedMorphs,
            ISet<string> selectedInputs = null)
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
            var unitFxCommands = HasOnlyUnchangedUnitFxCommands(runtime, source, layers, others, context, selectedInputs);
            // An empty output plan asks only whether the existing strict rest
            // proof permits omission. It cannot alter the actual scalar plan.
            var proof = NeutralShapePlan.CreateProjection(avatar, Array.Empty<EditorCurveBinding>(), Array.Empty<AnimationClip>());
            foreach (var index in NeutralRandomRest.Preserve(runtime, source, proof, null, context))
            {
                var layer = layers[index];
                if (layer.Morphs.Overlaps(selectedMorphs) || layer.WeightControls.Count != 0 ||
                    selectedInputs?.Overlaps(layer.Writes) == true) continue;
                var needed = new HashSet<string>(layer.Reads, StringComparer.Ordinal); needed.UnionWith(layer.Writes);
                // An incoming write to a Random-owned destination joins its
                // autonomous program. Additional playables remain unresolved
                // even when they write only an input gate. FX gate producers,
                // however, stay in the native graph: their known typed SDK
                // operations are not replaced with fixed input values.
                if (others.Any(item => item.Writes.Overlaps(needed)) ||
                    layers.Where((item, otherIndex) => otherIndex != index).Any(item => item.Writes.Overlaps(layer.Writes))) continue;
                if (!unitFxCommands || layers.Concat(others).Any(item => item.WeightControls.Values.Any(control =>
                    control.Playable == "FX" && (!control.AnimatorLayer || control.LayerIndex == index)))) continue;
                foreach (var program in layers.SelectMany(item => item.DriverPrograms))
                    VrChatParameterDriver.ValidateTargets(program, types, needed);
                var gates = new HashSet<string>(layer.Reads, StringComparer.Ordinal); gates.ExceptWith(layer.Writes);
                // An unselected saved value can have been restored before any
                // producer runs. Even no producer cannot certify its authored
                // default: check every read-only input before omitting the layer.
                if (!HasKnownTransientGates(runtime, source, context, gates, layers, others, selectedInputs)) continue;
                var producers = layers.Where((item, otherIndex) => otherIndex != index && item.Writes.Overlaps(gates)).ToArray();
                if (producers.Any(item => item.CurveWrites.Overlaps(gates) || item.DriverPrograms.SelectMany(program => program.Operations)
                    .Any(operation => gates.Contains(operation.Destination) &&
                        (operation.UnresolvedRandom || operation.Kind != "Set" && operation.Kind != "Add" && operation.Kind != "Copy")))) continue;
                // A finite eventual reset does not establish the pose at the
                // sampler's first capture. A changed input must immediately
                // enter complete rest and keep it there, with no timed face or
                // uninterruptible blend between the input and the reset.
                var selectedGate = selectedInputs?.Overlaps(gates) == true;
                if ((producers.Length != 0 || selectedGate) && !GateRoutesReturnToPreparedRest(avatar, runtime, index, types,
                    source, context, gates, producers, layer.DriverPrograms, selectedGate)) continue;
                result.Add(index); preservedMorphs.UnionWith(layer.Morphs);
            }
            return result;
        }

        private static bool HasKnownTransientGates(RuntimeAnimatorController runtime, VrChatExpressionMenu.Source source,
            FixedExpressionContext context, ISet<string> gates, ExpressionDependencies.Layer[] layers,
            IList<ExpressionDependencies.Layer> others, ISet<string> selectedInputs)
        {
            if (gates.Count == 0) return true;
            var declarations = new Dictionary<string, List<AnimatorControllerParameter>>(StringComparer.Ordinal);
            foreach (var current in new[] { runtime }.Concat(source.OtherControllers.Where(value => value != null)))
            {
                var parameters = ExpressionDependencies.Controller(current).parameters;
                foreach (var name in gates.Concat(new[] { "IsLocal" }).Distinct(StringComparer.Ordinal))
                {
                    var matches = parameters.Where(parameter => parameter.name == name).ToArray();
                    if (matches.Length > 1) return false;
                    if (matches.Length == 0) continue;
                    if (!declarations.TryGetValue(name, out var values)) declarations.Add(name, values = new List<AnimatorControllerParameter>());
                    values.Add(matches[0]);
                }
            }
            float Default(AnimatorControllerParameter parameter) => parameter.type == AnimatorControllerParameterType.Bool ? (parameter.defaultBool ? 1 : 0) :
                parameter.type == AnimatorControllerParameterType.Int ? parameter.defaultInt : parameter.defaultFloat;
            var written = new HashSet<string>(layers.Concat(others).SelectMany(layer => layer.Writes), StringComparer.Ordinal);
            var local = !written.Contains("IsLocal") && selectedInputs?.Contains("IsLocal") != true &&
                context != null && context.Values.TryGetValue("IsLocal", out var localValue) && localValue.Equals(1f) &&
                declarations.TryGetValue("IsLocal", out var locals) && locals.All(parameter =>
                    parameter.type == AnimatorControllerParameterType.Bool && Default(parameter).Equals(Default(locals[0])));
            foreach (var name in gates)
            {
                if (!declarations.TryGetValue(name, out var values)) return false;
                var type = values[0].type;
                if (type != AnimatorControllerParameterType.Bool && type != AnimatorControllerParameterType.Int &&
                    type != AnimatorControllerParameterType.Float || values.Any(parameter => parameter.type != type ||
                    !NeutralShapeSnapshot.Finite(Default(parameter)) || !Default(parameter).Equals(Default(values[0])))) return false;
                if (VrChatParameterDriver.BuiltIn.Contains(name))
                {
                    if (context?.Values.ContainsKey(name) != true) return false;
                }
                else
                {
                    if (!source.NeutralInputInventoryComplete) return false;
                    if (source.ExpressionParameters.Contains(name) || source.MenuInputs.Contains(name) ||
                        source.ParameterPersistence.ContainsKey(name) || source.ExpressionParameterTypes.ContainsKey(name))
                    {
                        if (!source.ParameterPersistence.TryGetValue(name, out var persistence) || persistence.Saved ||
                            !source.ExpressionParameterTypes.TryGetValue(name, out var declaredType) || declaredType != type.ToString() ||
                            persistence.NetworkSynced && !local) return false;
                    }
                    if (source.ExternalParameters.Contains(name) && context?.Values.ContainsKey(name) != true) return false;
                }
                var value = Default(values[0]);
                if (source.Defaults.TryGetValue(name, out var supplied)) value = supplied;
                if (context?.Values.TryGetValue(name, out supplied) == true) value = supplied;
                if (!NeutralShapeSnapshot.Finite(value) || type == AnimatorControllerParameterType.Bool && value != 0 && value != 1 ||
                    type == AnimatorControllerParameterType.Int && (value != Math.Truncate(value) || value < int.MinValue || (double)value > int.MaxValue)) return false;
            }
            return true;
        }

        internal static bool HasOnlyUnchangedUnitFxCommands(RuntimeAnimatorController runtime, VrChatExpressionMenu.Source source,
            ExpressionDependencies.Layer[] layers, IList<ExpressionDependencies.Layer> others, FixedExpressionContext context,
            ISet<string> selectedInputs = null)
        {
            // Another playable's global command participates in the component.
            // Keep that unresolved cross-playable effect in ordinary evaluation.
            if (others.Any(layer => layer.FxControl)) return false;
            if (!layers.Any(layer => layer.FxControl)) return true;
            var controller = ExpressionDependencies.Controller(runtime);
            var normal = FixedExpressionContext.Create(runtime, source.Defaults, source);
            var written = new HashSet<string>(layers.Concat(others).SelectMany(layer => layer.Writes), StringComparer.Ordinal);
            var fixedValues = ExpressionDependencies.FixedNeutralValues(runtime, source, layers, null, context);
            // A selected input, SDK callback or dormant Animator curve cannot
            // establish a false gate. Use only explicitly modeled normal input
            // values that are unchanged in this probe and have no raw writer.
            foreach (var name in fixedValues.Keys.ToArray())
                if (context == null || !context.Values.TryGetValue(name, out var selected) ||
                    !normal.Values.TryGetValue(name, out var initial) || initial != selected || written.Contains(name) ||
                    selectedInputs?.Contains(name) == true)
                    fixedValues.Remove(name);
            foreach (var other in source.OtherControllers.Where(value => value != null))
            {
                var declared = ExpressionDependencies.Controller(other).parameters.GroupBy(parameter => parameter.name, StringComparer.Ordinal);
                foreach (var group in declared.Where(group => fixedValues.ContainsKey(group.Key)))
                {
                    var original = controller.parameters.Where(parameter => parameter.name == group.Key).ToArray();
                    if (group.Count() != 1 || original.Length != 1 || group.Single().type != original[0].type) fixedValues.Remove(group.Key);
                }
            }
            for (var index = 0; index < layers.Length; index++)
            {
                if (!layers[index].FxControl) continue;
                if (controller.layers[index].syncedLayerIndex >= 0 || layers[index].FxCommands.Count == 0 ||
                    !FixedExpressionContext.TryReachableFxCommands(controller.layers[index].stateMachine, fixedValues,
                        controller.parameters, out var reached)) return false;
                if (layers[index].FxCommands.Any(pair => reached.Contains(pair.Key) && pair.Value.FxWeight != 1)) return false;
            }
            // All commands remain in the native graph. This proof only says
            // they cannot turn the prepared unit-weight FX stream into another
            // contribution while its independent Random idle is held at rest.
            return true;
        }

        private static bool GateRoutesReturnToPreparedRest(GameObject avatar, RuntimeAnimatorController runtime, int layerIndex,
            IDictionary<string, AnimatorControllerParameterType> types, VrChatExpressionMenu.Source source,
            FixedExpressionContext context, ISet<string> gates, IEnumerable<ExpressionDependencies.Layer> producers,
            IEnumerable<VrChatParameterDriver.Program> ownPrograms, bool selectedGate)
        {
            var machine = ExpressionDependencies.Controller(runtime).layers[layerIndex].stateMachine;
            var overrides = ExpressionDependencies.Overrides(runtime);
            var parents = new Dictionary<AnimatorStateMachine, AnimatorStateMachine>();
            var owners = new Dictionary<AnimatorState, AnimatorStateMachine>();
            bool Index(AnimatorStateMachine current, AnimatorStateMachine parent)
            {
                if (current == null || parents.ContainsKey(current)) return false;
                parents.Add(current, parent);
                foreach (var child in current.states)
                {
                    if (child.state == null || owners.ContainsKey(child.state) || owners.Count >= 4096) return false;
                    owners.Add(child.state, current);
                }
                if (current.defaultState == null || !owners.TryGetValue(current.defaultState, out var owner) || owner != current) return false;
                return current.stateMachines.All(child => Index(child.stateMachine, current));
            }
            if (!Index(machine, null)) return false;
            bool Valid(AnimatorTransitionBase transition) => transition != null && (transition.isExit ?
                transition.destinationState == null && transition.destinationStateMachine == null :
                transition.destinationState != null ? transition.destinationStateMachine == null && owners.ContainsKey(transition.destinationState) :
                transition.destinationStateMachine != null && parents.ContainsKey(transition.destinationStateMachine));
            if (parents.Keys.Any(current => current.entryTransitions.Any(transition => !Valid(transition)) ||
                current.anyStateTransitions.Any(transition => !Valid(transition)) || current.stateMachines.Any(child =>
                    current.GetStateMachineTransitions(child.stateMachine).Any(transition => !Valid(transition)))) ||
                owners.Keys.Any(state => state.transitions.Any(transition => !Valid(transition)))) return false;
            AnimationClip Clip(AnimatorState state) => state.motion is AnimationClip clip ?
                (overrides.TryGetValue(clip, out var replacement) ? replacement : clip) : null;
            var outputs = new HashSet<EditorCurveBinding>(owners.Keys.Select(Clip).Where(clip => clip != null)
                .SelectMany(AnimationUtility.GetCurveBindings));
            bool Rest(AnimatorState state)
            {
                var clip = Clip(state); if (clip == null) return false;
                foreach (var binding in outputs)
                {
                    if (binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)) return false;
                    var skin = (binding.path.Length == 0 ? avatar.transform : avatar.transform.Find(binding.path))?.GetComponent<SkinnedMeshRenderer>();
                    var shape = skin?.sharedMesh == null ? -1 : skin.sharedMesh.GetBlendShapeIndex(binding.propertyName.Substring("blendShape.".Length));
                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    if (shape < 0 || curve == null || curve.length == 0 || !VrChatExpressionSampler.IsConstant(curve) ||
                        curve.keys[0].value != skin.GetBlendShapeWeight(shape)) return false;
                }
                // An omitted WD Off binding can retain a previous nonzero
                // face forever. Only a complete explicit reset establishes
                // prepared rest without assuming a native fallback value.
                return true;
            }
            IEnumerable<AnimatorStateTransition> Enabled(IEnumerable<AnimatorStateTransition> transitions)
            {
                var values = transitions.ToArray(); var solo = values.Any(value => value != null && value.solo);
                return values.Where(value => value == null || !value.mute && (!solo || value.solo));
            }
            bool FiniteClockExit(AnimatorState state, AnimatorStateTransition transition)
            {
                if (transition == null || transition.conditions.Length != 0 ||
                    !NeutralShapeSnapshot.Finite(transition.duration) || transition.duration < 0) return false;
                if (!transition.hasExitTime) return true;
                var clip = Clip(state);
                return clip != null && NeutralShapeSnapshot.Finite(clip.length) && clip.length > 0 &&
                    NeutralShapeSnapshot.Finite(transition.exitTime) && transition.exitTime >= 0 &&
                    NeutralShapeSnapshot.Finite(state.speed) && state.speed > 0 &&
                    NeutralShapeSnapshot.Finite(state.cycleOffset) && !state.speedParameterActive &&
                    !state.timeParameterActive && !state.cycleOffsetParameterActive;
            }
            var coverage = new Dictionary<AnimatorStateMachine, bool>(); var conditionChecks = 0;
            bool Covers(AnimatorTransition[] transitions)
            {
                var groups = transitions.SelectMany(transition => transition.conditions).GroupBy(condition => condition.parameter, StringComparer.Ordinal).ToArray();
                var options = new List<(string Name, float[] Values)>(); long combinations = 1;
                foreach (var group in groups)
                {
                    if (string.IsNullOrEmpty(group.Key) || !types.TryGetValue(group.Key, out var type) ||
                        group.Any(condition => !NeutralShapeSnapshot.Finite(condition.threshold))) return false;
                    var values = new HashSet<float>();
                    if (type == AnimatorControllerParameterType.Bool)
                    {
                        if (group.Any(condition => condition.mode != AnimatorConditionMode.If && condition.mode != AnimatorConditionMode.IfNot)) return false;
                        values.UnionWith(new[] { 0f, 1f });
                    }
                    else
                    {
                        if (type != AnimatorControllerParameterType.Int && type != AnimatorControllerParameterType.Float ||
                            group.Any(condition => Math.Abs(condition.threshold) > 1000000 ||
                                type == AnimatorControllerParameterType.Int && condition.threshold != Math.Truncate(condition.threshold) ||
                                condition.mode != AnimatorConditionMode.Greater && condition.mode != AnimatorConditionMode.Less &&
                                (type != AnimatorControllerParameterType.Int || condition.mode != AnimatorConditionMode.Equals && condition.mode != AnimatorConditionMode.NotEqual))) return false;
                        var thresholds = group.Select(condition => condition.threshold).Distinct().OrderBy(value => value).ToArray();
                        foreach (var value in thresholds) values.UnionWith(new[] { value - 1, value, value + 1 });
                        for (var index = 1; index < thresholds.Length; index++)
                        {
                            var middle = (float)(((double)thresholds[index - 1] + thresholds[index]) * .5);
                            if (type == AnimatorControllerParameterType.Float || middle == Math.Truncate(middle)) values.Add(middle);
                        }
                    }
                    combinations *= values.Count; if (combinations > 256) return false;
                    options.Add((group.Key, values.ToArray()));
                }
                var assigned = new Dictionary<string, float>(StringComparer.Ordinal);
                bool Match(AnimatorCondition condition)
                {
                    if (++conditionChecks > 65536) return false;
                    var value = assigned[condition.parameter];
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
                }
                bool EveryCell(int index)
                {
                    if (index == options.Count) return transitions.Any(transition => transition.conditions.All(Match));
                    foreach (var value in options[index].Values)
                    {
                        assigned[options[index].Name] = value;
                        if (!EveryCell(index + 1)) return false;
                    }
                    return true;
                }
                return EveryCell(0) && conditionChecks <= 65536;
            }
            bool RandomBoundary(IEnumerable<StateMachineBehaviour> behaviours) => behaviours.Where(VrChatParameterDriver.IsDriver)
                .Any(behaviour => VrChatParameterDriver.Read(behaviour, machine.name).Operations.Any(operation => operation.Kind == "Random"));
            bool Destinations(AnimatorTransitionBase transition, AnimatorStateMachine owner,
                HashSet<AnimatorState> destinations, HashSet<AnimatorStateMachine> visited, bool allowRestart, ref bool restart)
            {
                if (transition == null) return false;
                if (transition.isExit)
                {
                    var parent = parents[owner]; if (parent == null || !visited.Add(owner)) return false;
                    var values = parent.GetStateMachineTransitions(owner); var solo = values.Any(value => value != null && value.solo);
                    var exits = values.Where(value => value == null || !value.mute && (!solo || value.solo)).ToArray();
                    // Prove the complete typed partition. It may return zero or
                    // re-enter a Random machine; a missing gate combination
                    // cannot be replaced with a fabricated prepared rest.
                    if (!coverage.TryGetValue(owner, out var covered)) coverage[owner] = covered = Covers(exits);
                    if (!covered) return false;
                    foreach (var value in exits)
                        if (!Destinations(value, parent, destinations, new HashSet<AnimatorStateMachine>(visited), allowRestart, ref restart)) return false;
                    return true;
                }
                if (transition.destinationState != null)
                {
                    if (transition.destinationStateMachine != null || !owners.ContainsKey(transition.destinationState)) return false;
                    if (allowRestart && RandomBoundary(transition.destinationState.behaviours)) { restart = true; return true; }
                    destinations.Add(transition.destinationState); return true;
                }
                var child = transition.destinationStateMachine;
                if (child == null || !parents.ContainsKey(child)) return false;
                if (allowRestart && RandomBoundary(child.behaviours)) { restart = true; return true; }
                if (!visited.Add(child)) return false;
                if (child.defaultState == null || !owners.TryGetValue(child.defaultState, out var actualOwner) || actualOwner != child) return false;
                destinations.Add(child.defaultState);
                var entries = child.entryTransitions; var entrySolo = entries.Any(value => value != null && value.solo);
                foreach (var value in entries.Where(value => value == null || !value.mute && (!entrySolo || value.solo)))
                    if (!Destinations(value, child, destinations, new HashSet<AnimatorStateMachine>(visited), allowRestart, ref restart)) return false;
                return true;
            }
            var restStates = new HashSet<AnimatorState>(owners.Keys.Where(Rest));
            var proven = new HashSet<AnimatorState>(restStates);
            bool changed;
            do
            {
                changed = false;
                foreach (var state in owners.Keys.Where(state => !proven.Contains(state)).ToArray())
                {
                    var own = Enabled(state.transitions).ToArray();
                    var any = new List<AnimatorStateTransition>();
                    for (var owner = owners[state]; owner != null; owner = parents[owner])
                        any.AddRange(Enabled(owner.anyStateTransitions).Where(transition => transition == null ||
                            transition.destinationState != state || transition.canTransitionToSelf));
                    // Conditional cycles remain native. Every destination,
                    // including nested Entry/Exit routes, must already
                    // have a proof; one convenient timed exit is insufficient.
                    if (!own.Any(transition => FiniteClockExit(state, transition))) continue;
                    var destinations = new HashSet<AnimatorState>(); var restart = false; var valid = true;
                    foreach (var transition in own)
                        if (!Destinations(transition, owners[state], destinations, new HashSet<AnimatorStateMachine>(), true, ref restart)) valid = false;
                    // An AnyState re-entry may continuously reset a non-rest
                    // clock. It cannot establish the required timed return.
                    foreach (var transition in any)
                        if (!Destinations(transition, owners[state], destinations, new HashSet<AnimatorStateMachine>(), false, ref restart)) valid = false;
                    if (!valid || destinations.Count == 0 && !restart || destinations.Any(state => !proven.Contains(state))) continue;
                    proven.Add(state); changed = true;
                }
            } while (changed);
            if (proven.Count != owners.Count) return false;
            var initial = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var name in gates)
            {
                var declarations = ExpressionDependencies.Controller(runtime).parameters.Where(parameter => parameter.name == name).ToArray();
                if (declarations.Length != 1 || !types.TryGetValue(name, out var type)) return false;
                var parameter = declarations[0];
                var value = type == AnimatorControllerParameterType.Bool ? (parameter.defaultBool ? 1f : 0f) :
                    type == AnimatorControllerParameterType.Int ? parameter.defaultInt : parameter.defaultFloat;
                if (source.Defaults.TryGetValue(name, out var supplied)) value = supplied;
                if (context?.Values.TryGetValue(name, out supplied) == true) value = supplied;
                if (!NeutralShapeSnapshot.Finite(value) || type == AnimatorControllerParameterType.Bool && value != 0 && value != 1 ||
                    type == AnimatorControllerParameterType.Int && (value != Math.Truncate(value) || value < int.MinValue || (double)value > int.MaxValue)) return false;
                initial.Add(name, value);
            }
            bool UnchangedSet(VrChatParameterDriver.Operation operation)
            {
                if (!gates.Contains(operation.Destination)) return true;
                if (operation.Kind != "Set" || !types.TryGetValue(operation.Destination, out var type)) return false;
                var value = operation.Value;
                if (type == AnimatorControllerParameterType.Bool) value = value == 0 ? 0 : 1;
                if (type == AnimatorControllerParameterType.Int) value = (float)Math.Truncate(value);
                if (source.ExpressionParameters.Contains(operation.Destination))
                {
                    if (type == AnimatorControllerParameterType.Int) value = Mathf.Clamp(value, 0, 255);
                    if (type == AnimatorControllerParameterType.Float) value = Mathf.Clamp(value, -1, 1);
                }
                return value.Equals(initial[operation.Destination]);
            }
            if (!selectedGate && producers.SelectMany(layer => layer.DriverPrograms).SelectMany(program => program.Operations).All(UnchangedSet)) return true;

            // Check all input cells rather than assigning producer outputs or
            // simulating Random. At the supplied initial gates the original
            // independent idle is held by the existing policy. Every different
            // input must instead close the layer at explicit prepared rest.
            // Own Copy reads could transform a gate into another routing input;
            // they are not covered by a transition-only partition proof.
            if (ownPrograms.SelectMany(program => program.Operations).Any(operation => gates.Contains(operation.Source))) return false;
            var allTransitions = owners.Keys.SelectMany(state => Enabled(state.transitions)).Concat(parents.Keys.SelectMany(current => Enabled(current.anyStateTransitions))).ToArray();
            if (allTransitions.Any(transition => transition == null || transition.duration != 0 || transition.offset != 0)) return false;
            var conditionTransitions = allTransitions.Cast<AnimatorTransitionBase>().Concat(parents.Keys.SelectMany(current =>
                current.entryTransitions.Cast<AnimatorTransitionBase>().Concat(current.stateMachines.SelectMany(child =>
                    current.GetStateMachineTransitions(child.stateMachine))))).ToArray();
            var conditions = conditionTransitions.SelectMany(transition => transition.conditions).GroupBy(condition => condition.parameter, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var cells = new List<(string Name, float[] Values)>(); long cellCount = 1;
            foreach (var name in conditions.Keys.Concat(gates).Distinct(StringComparer.Ordinal))
            {
                if (!types.TryGetValue(name, out var type)) return false;
                conditions.TryGetValue(name, out var values); values = values ?? Array.Empty<AnimatorCondition>();
                if (values.Any(condition => !NeutralShapeSnapshot.Finite(condition.threshold))) return false;
                var samples = new HashSet<float>();
                if (type == AnimatorControllerParameterType.Bool)
                {
                    if (values.Any(condition => condition.mode != AnimatorConditionMode.If && condition.mode != AnimatorConditionMode.IfNot)) return false;
                    samples.UnionWith(new[] { 0f, 1f });
                }
                else
                {
                    if (type != AnimatorControllerParameterType.Int && type != AnimatorControllerParameterType.Float || values.Any(condition =>
                        Math.Abs(condition.threshold) > 1000000 || type == AnimatorControllerParameterType.Int && condition.threshold != Math.Truncate(condition.threshold) ||
                        condition.mode != AnimatorConditionMode.Greater && condition.mode != AnimatorConditionMode.Less &&
                        (type != AnimatorControllerParameterType.Int || condition.mode != AnimatorConditionMode.Equals && condition.mode != AnimatorConditionMode.NotEqual))) return false;
                    var thresholds = values.Select(condition => condition.threshold).Distinct().OrderBy(value => value).ToArray();
                    foreach (var value in thresholds) samples.UnionWith(new[] { value - 1, value, value + 1 });
                    for (var index = 1; index < thresholds.Length; index++)
                    {
                        var middle = (float)(((double)thresholds[index - 1] + thresholds[index]) * .5);
                        if (type == AnimatorControllerParameterType.Float || middle == Math.Truncate(middle)) samples.Add(middle);
                    }
                    if (samples.Count == 0) samples.UnionWith(new[] { -1f, 0f, 1f });
                }
                if (initial.TryGetValue(name, out var normal)) samples.Add(normal);
                cellCount *= samples.Count; if (cellCount > 256) return false;
                cells.Add((name, samples.ToArray()));
            }
            var assignment = new Dictionary<string, float>(StringComparer.Ordinal); var checks = 0;
            bool Matches(AnimatorTransitionBase transition)
            {
                if (++checks > 65536 || transition == null) return false;
                foreach (var condition in transition.conditions)
                {
                    var value = assignment[condition.parameter];
                    var match = condition.mode == AnimatorConditionMode.If ? value != 0 : condition.mode == AnimatorConditionMode.IfNot ? value == 0 :
                        condition.mode == AnimatorConditionMode.Equals ? value == condition.threshold : condition.mode == AnimatorConditionMode.NotEqual ? value != condition.threshold :
                        condition.mode == AnimatorConditionMode.Greater ? value > condition.threshold : condition.mode == AnimatorConditionMode.Less && value < condition.threshold;
                    if (!match) return false;
                }
                return true;
            }
            IEnumerable<AnimatorTransition> Active(IEnumerable<AnimatorTransition> transitions)
            {
                var values = transitions.ToArray(); var solo = values.Any(transition => transition != null && transition.solo);
                return values.Where(transition => transition != null && !transition.mute && (!solo || transition.solo));
            }
            bool ToRest(AnimatorTransitionBase transition, AnimatorStateMachine owner, HashSet<AnimatorStateMachine> visited)
            {
                if (transition.isExit)
                {
                    if (!visited.Add(owner) || parents[owner] == null) return false;
                    var parent = parents[owner];
                    var next = Active(parent.GetStateMachineTransitions(owner)).FirstOrDefault(Matches);
                    return next != null && ToRest(next, parent, visited);
                }
                if (transition.destinationState != null) return restStates.Contains(transition.destinationState);
                var child = transition.destinationStateMachine;
                if (child == null || !visited.Add(child)) return false;
                var entry = Active(child.entryTransitions).FirstOrDefault(Matches);
                return entry != null ? ToRest(entry, child, visited) : restStates.Contains(child.defaultState);
            }
            bool ClosedRestCell()
            {
                if (gates.All(name => assignment[name].Equals(initial[name]))) return true;
                foreach (var state in owners.Keys)
                {
                    var reset = restStates.Contains(state);
                    for (var owner = owners[state]; owner != null; owner = parents[owner])
                        foreach (var transition in Enabled(owner.anyStateTransitions).Where(Matches))
                        {
                            if (!ToRest(transition, owner, new HashSet<AnimatorStateMachine>())) return false;
                            reset |= !transition.hasExitTime;
                        }
                    foreach (var transition in Enabled(state.transitions).Where(Matches))
                    {
                        if (!ToRest(transition, owners[state], new HashSet<AnimatorStateMachine>())) return false;
                        if (!transition.hasExitTime) { reset = true; break; }
                    }
                    if (!reset) return false;
                }
                return checks <= 65536;
            }
            bool EveryChangedCell(int index)
            {
                if (index == cells.Count) return ClosedRestCell();
                foreach (var value in cells[index].Values)
                {
                    assignment[cells[index].Name] = value;
                    if (!EveryChangedCell(index + 1)) return false;
                }
                return true;
            }
            return EveryChangedCell(0) && checks <= 65536;
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
