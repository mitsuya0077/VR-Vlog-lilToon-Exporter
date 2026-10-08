using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRVlog.FaceTracking;

namespace VRVlog.LilToonExporter
{
    // Discover user-authored FX selections, regardless of the expression tool.
    // Candidates are parameter assignments into the prepared controller, never
    // BlendTree leaves or clips detached from their native graph. The sampler
    // proves the target was reached and keeps its existing capability checks.
    internal static class VrChatFxExpressions
    {
        const int MaximumNodes = 8192, MaximumDepth = 16, MaximumCandidates = 256;
        const int MaximumConditionChecks = MaximumNodes * 32;

        sealed class Candidate
        {
            internal int Layer;
            internal AnimatorState State;
            internal string Path, Error;
            internal Dictionary<string, float> Values, DefaultValues, ArrivalValues;
            internal bool SpeculativeWarmStart;
        }

        sealed class Point
        {
            internal Dictionary<string, float> Values = new Dictionary<string, float>(StringComparer.Ordinal);
            internal bool PreservesInputs;
        }

        sealed class Constraint
        {
            internal AnimatorCondition Condition;
            internal bool Negated;
        }

        sealed class Edge
        {
            internal AnimatorStateMachine Owner, SourceMachine;
            internal AnimatorState Source;
            internal AnimatorTransitionBase Transition;
            internal IEnumerable<AnimatorTransitionBase> PreviousEntries;
        }

        sealed class PriorityPrefix
        {
            internal readonly Dictionary<AnimatorTransitionBase, PriorityPrefix> Children = new Dictionary<AnimatorTransitionBase, PriorityPrefix>();
            internal Constraint[][] Alternatives;
        }

        internal static void Add(GameObject avatar, VrChatExpressionMenu.Source source,
            Func<string, bool> excludedPath = null, VrChatMenuImportPolicy policy = null)
        {
            if (source.Controller == null) return;
            if (!source.NeutralInputInventoryComplete) NeutralInputProof.Read(avatar, source);
            if ((policy?.SkipAll == true || policy?.ExcludedBranches.Count > 0) && !source.NeutralInputInventoryComplete)
            {
                source.Messages.Add("メニューの除外範囲の入力を完全に確認できないため、FXの自動表情探索を省略しました。");
                return;
            }
            var pending = new List<VrChatExpressionMenu.Entry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var context = FixedExpressionContext.Create(source.Controller, source.Defaults, source);
                foreach (var candidate in Discover(source, context, excludedPath).ToArray())
                {
                    // Recovery exclusions are authoritative even when the menu's
                    // original selection can also be inferred from FX conditions.
                    if ((policy?.SkipAll == true || policy?.ExcludedBranches.Count > 0) &&
                        candidate.Values.Keys.Any(source.MenuInputs.Contains)) continue;
                    var assignment = Assignment(candidate.Values);
                    var key = candidate.Layer + "/" + candidate.Path + "/" + assignment;
                    if (!seen.Add(key)) continue;
                    if (candidate.Error == null && source.Entries.Any(entry => entry.Error == null &&
                        entry.Id?.StartsWith("fx/", StringComparison.Ordinal) != true && SameParameters(entry.Parameters, candidate.Values))) continue;
                    var entry = new VrChatExpressionMenu.Entry {
                        Id = "fx/" + key, Name = "FX / " + candidate.State.name +
                            (candidate.State.motion is BlendTree ? " (" + assignment + ")" : ""),
                        Error = candidate.Error
                    };
                    foreach (var value in candidate.Values) entry.Parameters.Add(value.Key, value.Value);
                    if (entry.Error == null)
                    {
                        try
                        {
                            entry.Values.AddRange(VrChatExpressionSampler.SampleFixed(avatar, source.Controller, source.Defaults,
                                entry.Parameters, excludedPath, source, entry.Unevaluated, context,
                                candidate.Layer, candidate.Path));
                            if (candidate.DefaultValues != null)
                            {
                                // A readonly parent may make an authored inner
                                // knot inactive. Prove its actual effect against
                                // native defaults with the same dependency roots.
                                var baseline = new VrChatExpressionMenu.Entry();
                                baseline.Values.AddRange(VrChatExpressionSampler.SampleFixed(avatar, source.Controller, source.Defaults,
                                    candidate.DefaultValues, excludedPath, source, baseline.Unevaluated, context));
                                if (SamePose(baseline, entry))
                                {
                                    source.Messages.Add("FXの表情候補は通常の出力環境で顔の変化がないため追加していません: " + entry.Name);
                                    continue;
                                }
                            }
                            entry.Messages.Add("FXの条件・BlendTreeの設定値から表情を読み込みました: " + assignment);
                        }
                        catch (InvalidOperationException error)
                        {
                            if (candidate.SpeculativeWarmStart) continue;
                            entry.Error = error.Message; entry.Values.Clear(); entry.Unevaluated.Clear();
                        }
                    }
                    // Existing authored menu and gesture labels remain primary.
                    // Evaluate first: equal clips alone do not prove equal native
                    // outcomes in the presence of callbacks and layer blending.
                    if (entry.Error == null && source.Entries.Concat(pending).Any(previous => SamePose(previous, entry))) continue;
                    pending.Add(entry);
                    if (source.Entries.Count + pending.Count > 512)
                        throw new InvalidOperationException("表情候補が512件を超えています。FXの表情登録を整理してください。");
                }
                source.Entries.AddRange(pending);
            }
            catch (InvalidOperationException error)
            {
                // Automatic inference is optional. Publish it atomically so a
                // budget or topology failure cannot leave an arbitrary subset
                // of inferred faces or prevent existing authored imports.
                source.Messages.Add("FXの自動表情探索を完了できなかったため、推定候補は追加していません: " + error.Message);
            }
        }

        // Hand trees and motion-time clips expose native input knots. Select
        // those inputs in the whole controller; do not detach mixed children
        // or turn a hand-controlled clock into an autoplaying animation.
        internal static List<VrChatExpressionMenu.Entry> ReadGestureState(GameObject avatar,
            VrChatExpressionMenu.Source source, int layer, AnimatorState state, string label,
            Func<string, bool> excludedPath = null)
        {
            var handInputs = new HashSet<string>(new[] { "GestureLeft", "GestureRight", "GestureLeftWeight", "GestureRightWeight" }, StringComparer.Ordinal);
            var context = FixedExpressionContext.Create(source.Controller, source.Defaults, source);
            // Buffer discovery before native evaluation. A malformed tree or
            // exhausted graph budget cannot publish an arbitrary partial set.
            var candidates = Discover(source, context, excludedPath, handInputs,
                (candidateLayer, candidateState) => candidateState == state, layer).ToArray();
            var result = new List<VrChatExpressionMenu.Entry>();
            foreach (var candidate in candidates)
            {
                var entry = new VrChatExpressionMenu.Entry {
                    Id = "gesture-state/" + layer + "/" + candidate.Path + "/" + Assignment(candidate.Values),
                    Name = label, Error = candidate.Error
                };
                foreach (var pair in candidate.Values)
                    entry.Parameters.Add(pair.Key, pair.Key == "GestureLeftWeight" || pair.Key == "GestureRightWeight" ? Mathf.Clamp01(pair.Value) : pair.Value);
                foreach (var hand in new[] { "Left", "Right" })
                    if (entry.Parameters.TryGetValue("Gesture" + hand, out var gesture) && gesture > 0 &&
                        !entry.Parameters.ContainsKey("Gesture" + hand + "Weight") &&
                        ExpressionDependencies.Controller(source.Controller).parameters.Any(parameter => parameter.name == "Gesture" + hand + "Weight"))
                        entry.Parameters.Add("Gesture" + hand + "Weight", 1);
                entry.Name += " (" + Assignment(entry.Parameters) + ")";
                // Some hand trees place knots slightly outside the hand's
                // documented [0,1] domain to shape their native interpolation.
                // Sample the reachable boundary with the original mixing.
                foreach (var pair in entry.Parameters.Where(pair => handInputs.Contains(pair.Key)))
                    if (pair.Key.EndsWith("Weight", StringComparison.Ordinal) ? pair.Value < 0 || pair.Value > 1 :
                        pair.Value < 0 || pair.Value > 7 || pair.Value != Mathf.RoundToInt(pair.Value))
                        entry.Error = "FXのBlendTreeの設定値が不正です。";
                if (entry.Error == null)
                    try
                    {
                        entry.Values.AddRange(VrChatExpressionSampler.SampleFixed(avatar, source.Controller, source.Defaults,
                            entry.Parameters, excludedPath, source, entry.Unevaluated, context, candidate.Layer, candidate.Path));
                    }
                    catch (InvalidOperationException error)
                    {
                        if (candidate.SpeculativeWarmStart && candidate.ArrivalValues == null) continue;
                        entry.Values.Clear(); entry.Unevaluated.Clear(); entry.Animation.Clear();
                        if (ExpressionDependencies.Controller(source.Controller).GetStateEffectiveMotion(candidate.State, candidate.Layer) is BlendTree)
                        {
                            try { AnimatedGestureTree.Read(avatar, source, candidate.Layer, candidate.State, entry, excludedPath, candidate.ArrivalValues); }
                            catch (InvalidOperationException composedError)
                            { entry.Error = composedError.Message; entry.Values.Clear(); entry.Unevaluated.Clear(); entry.Animation.Clear(); }
                        }
                        else entry.Error = error.Message;
                    }
                if (entry.Error == null && source.Entries.Concat(result).Any(previous => SamePose(previous, entry))) continue;
                result.Add(entry);
            }
            return result;
        }

        static IEnumerable<Candidate> Discover(VrChatExpressionMenu.Source source, FixedExpressionContext context,
            Func<string, bool> excludedPath, ISet<string> selectableBuiltIns = null,
            Func<int, AnimatorState, bool> stateFilter = null, int? onlyLayer = null)
        {
            var controller = ExpressionDependencies.Controller(source.Controller);
            var parameters = controller.parameters.ToDictionary(value => value.name, StringComparer.Ordinal);
            var defaults = parameters.Values.ToDictionary(value => value.name, value =>
                value.type == AnimatorControllerParameterType.Bool ? (value.defaultBool ? 1f : 0f) :
                value.type == AnimatorControllerParameterType.Int ? value.defaultInt : value.defaultFloat, StringComparer.Ordinal);
            foreach (var pair in source.Defaults) defaults[pair.Key] = pair.Value;
            foreach (var pair in context.Values) defaults[pair.Key] = pair.Value;
            // Animator- and driver-written values are graph outputs (for example
            // an MA activeSelf proxy), not independent controls. Retain parameters
            // explicitly declared as user expression/menu inputs, but do not
            // manufacture a facial state by overriding an internal signal.
            var drivers = new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>();
            var unknown = new List<string>();
            var dependencies = ExpressionDependencies.Inspect(source.Controller, excludedPath, drivers, unknown);
            var weightControls = dependencies.SelectMany(value => value.WeightControls)
                .GroupBy(pair => pair.Key).ToDictionary(group => group.Key, group => group.First().Value);
            var writtenInputs = new HashSet<string>(dependencies.SelectMany(value => value.CurveWrites.Concat(value.DriverWrites)), StringComparer.Ordinal);
            // Follow graph outputs backwards from morph-reading layers, so an
            // empty state that selects a complete face through driver relays is
            // discovered through its authored root control, not relay outputs.
            var faceInputs = new HashSet<string>(dependencies.Where(value => value.Morphs.Count > 0)
                .SelectMany(value => value.Reads), StringComparer.Ordinal);
            bool changed;
            do
            {
                changed = false;
                foreach (var dependency in dependencies.Where(value => value.Writes.Overlaps(faceInputs)))
                    foreach (var input in dependency.Reads) changed |= faceInputs.Add(input);
                foreach (var dependency in dependencies.Where(value => value.WeightControls.Values.Any(control =>
                    control.AnimatorLayer && control.Playable == "FX" && (dependencies[control.LayerIndex].Morphs.Count > 0 ||
                        dependencies[control.LayerIndex].Writes.Overlaps(faceInputs)))))
                    foreach (var input in dependency.Reads) changed |= faceInputs.Add(input);
            } while (changed);
            bool UserInput(string name) => source.ExpressionParameters.Contains(name) || source.MenuInputs.Contains(name);
            bool Selectable(string name) => !string.IsNullOrEmpty(name) &&
                (!VrChatParameterDriver.BuiltIn.Contains(name) || selectableBuiltIns?.Contains(name) == true) &&
                !source.ExternalParameters.Contains(name) && !TrackingParameter(name) &&
                (UserInput(name) || !writtenInputs.Contains(name) && !GeneratedParameter(name));
            // Normal inputs can eliminate dormant hand/voice/contact branches
            // before priority negations expand. A default is an invariant only
            // when no playable writes it through an Animator curve or any
            // driver operation. Unknown callbacks invalidate this proof.
            var allWritten = new HashSet<string>(dependencies.SelectMany(value => value.Writes), StringComparer.Ordinal);
            var declarations = new Dictionary<string, HashSet<AnimatorControllerParameterType>>(StringComparer.Ordinal);
            foreach (var runtime in new[] { source.Controller }.Concat(source.OtherControllers.Where(value => value != null)))
            {
                foreach (var parameter in ExpressionDependencies.Controller(runtime).parameters)
                {
                    if (!declarations.TryGetValue(parameter.name, out var types))
                        declarations.Add(parameter.name, types = new HashSet<AnimatorControllerParameterType>());
                    types.Add(parameter.type);
                }
                if (runtime != source.Controller)
                    allWritten.UnionWith(ExpressionDependencies.Inspect(runtime, null,
                        new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), unknown,
                        typedConditions: true, fxLayerCount: controller.layers.Length).SelectMany(value => value.Writes));
            }
            var invariant = unknown.Count == 0 ? context.Values.Where(pair => !Selectable(pair.Key) &&
                !allWritten.Contains(pair.Key) && declarations.TryGetValue(pair.Key, out var types) && types.Count == 1)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) :
                new Dictionary<string, float>(StringComparer.Ordinal);
            var impossible = new Dictionary<AnimatorTransitionBase, bool>();
            var replacements = ExpressionDependencies.Overrides(source.Controller);
            var layers = controller.layers;
            var nodes = 0;
            var conditionChecks = 0;
            var candidates = 0;
            var emitted = new HashSet<string>(StringComparer.Ordinal);
            var faceOutputs = new Dictionary<Motion, bool>();
            var controlPoints = new Dictionary<BlendTree, Point[]>();
            void Visit()
            {
                if (++nodes > MaximumNodes)
                    throw new InvalidOperationException("FXの表情探索が上限を超えています。Controllerの構成を確認してください。");
            }
            void CheckCondition()
            {
                // A single priority branch can contain many exclusions. Its
                // solver work grows independently of the number of graph nodes.
                if (++conditionChecks > MaximumConditionChecks)
                    throw new InvalidOperationException("FXの表情条件の探索が上限を超えています。Controllerの構成を確認してください。");
            }
            bool Impossible(AnimatorTransitionBase transition)
            {
                if (impossible.TryGetValue(transition, out var cached)) return cached;
                var disproved = false;
                foreach (var condition in transition.conditions)
                {
                    CheckCondition();
                    // Malformed conditions retain their existing diagnostics.
                    // Fractional Int thresholds are not a portable proof.
                    if (string.IsNullOrEmpty(condition.parameter) || !parameters.TryGetValue(condition.parameter, out var parameter) ||
                        !ValidMode(parameter.type, condition.mode) || !Finite(condition.threshold) ||
                        parameter.type == AnimatorControllerParameterType.Int && condition.threshold != Math.Truncate(condition.threshold))
                    { impossible.Add(transition, false); return false; }
                    if (invariant.TryGetValue(condition.parameter, out var value) && !Matches(condition, value)) disproved = true;
                }
                impossible.Add(transition, disproved); return disproved;
            }
            for (var layer = 0; layer < layers.Length; layer++)
            {
                if (onlyLayer.HasValue && layer != onlyLayer.Value) continue;
                // Wardrobe/gimmick graphs cannot supply a face unless they
                // write a facial dependency or control one of its layer weights.
                // Do not spend the route-expansion budget on unrelated graphs.
                var dependency = dependencies[layer];
                if (dependency.Morphs.Count == 0 && !dependency.Writes.Overlaps(faceInputs) &&
                    !dependency.WeightControls.Values.Any(control => control.AnimatorLayer && control.Playable == "FX" &&
                        (dependencies[control.LayerIndex].Morphs.Count > 0 || dependencies[control.LayerIndex].Writes.Overlaps(faceInputs))))
                    continue;
                // A complete layer inventory includes transition conditions,
                // ancestor routes, driver sources, tree controls and clocks.
                // Without a selectable read, neither route conditions nor a
                // motion point can supply a candidate assignment. Avoid walking
                // that readonly graph before the same candidate eligibility
                // check below; dense automatic graphs must not consume the
                // budget needed to discover an independent authored face.
                if (!dependency.Reads.Any(Selectable)) continue;
                var origin = layer;
                var sync = new HashSet<int>();
                while (layers[origin].syncedLayerIndex >= 0)
                {
                    if (!sync.Add(origin)) throw new InvalidOperationException("FXの同期レイヤー参照が不正です。");
                    origin = layers[origin].syncedLayerIndex;
                    if (origin < 0 || origin >= layers.Length) throw new InvalidOperationException("FXの同期レイヤー参照が不正です。");
                }
                var paths = new Dictionary<AnimatorState, string>();
                var owners = new Dictionary<AnimatorState, AnimatorStateMachine>();
                var parents = new Dictionary<AnimatorStateMachine, AnimatorStateMachine>();
                var edges = new List<Edge>();
                void Index(AnimatorStateMachine machine, string path, AnimatorStateMachine parent, int depth)
                {
                    Visit();
                    if (machine == null || depth > MaximumDepth || parents.ContainsKey(machine))
                        throw new InvalidOperationException("FXのStateMachine階層を安全に探索できません。");
                    parents.Add(machine, parent);
                    foreach (var child in machine.states)
                    {
                        Visit();
                        if (child.state == null || paths.ContainsKey(child.state))
                            throw new InvalidOperationException("FXの状態参照が不正です。");
                        paths.Add(child.state, path + "." + child.state.name); owners.Add(child.state, machine);
                        var previousStateTransitions = new List<AnimatorTransitionBase>();
                        foreach (var transition in Enabled(child.state.transitions))
                        {
                            Visit();
                            // Native Animator also makes an ordinary self
                            // transition ineligible when this flag is false.
                            if (transition.destinationState == child.state && !transition.canTransitionToSelf) continue;
                            edges.Add(new Edge { Owner = machine, Source = child.state, Transition = transition,
                                PreviousEntries = previousStateTransitions.Take(previousStateTransitions.Count) });
                            // Timed transitions have an additional native
                            // eligibility condition, not just their parameters.
                            if (!transition.hasExitTime) previousStateTransitions.Add(transition);
                        }
                    }
                    var previousAnyTransitions = new List<AnimatorStateTransition>();
                    foreach (var transition in Enabled(machine.anyStateTransitions))
                    {
                        Visit();
                        edges.Add(new Edge { Owner = machine, Transition = transition, PreviousEntries = previousAnyTransitions.Take(previousAnyTransitions.Count)
                            // Without an active source, a self-disabled prior
                            // may be ineligible during default initialization.
                            // Its outcome remains subject to the native proof.
                            .Where(previous => previous.canTransitionToSelf).Cast<AnimatorTransitionBase>() });
                        if (!transition.hasExitTime) previousAnyTransitions.Add(transition);
                    }
                    var previousEntries = new List<AnimatorTransitionBase>();
                    foreach (var transition in Enabled(machine.entryTransitions))
                    {
                        Visit();
                        // A manually serialized Entry -> Exit has no actual
                        // source state and can crash native Animator playback.
                        // Reject it before sampling any inferred candidate.
                        if (transition.isExit)
                            throw new InvalidOperationException("FXのEntryからExitへの直接遷移は安全に評価できません。Entryの行き先を実際の状態に設定してください。");
                        edges.Add(new Edge { Owner = machine, Transition = transition, PreviousEntries = previousEntries.Take(previousEntries.Count) });
                        previousEntries.Add(transition);
                    }
                    foreach (var child in machine.stateMachines)
                    {
                        var previousMachineTransitions = new List<AnimatorTransitionBase>();
                        foreach (var transition in Enabled(machine.GetStateMachineTransitions(child.stateMachine)))
                        {
                            Visit();
                            edges.Add(new Edge { Owner = machine, SourceMachine = child.stateMachine, Transition = transition,
                                PreviousEntries = previousMachineTransitions.Take(previousMachineTransitions.Count) });
                            previousMachineTransitions.Add(transition);
                        }
                        Index(child.stateMachine, path + "." + child.stateMachine.name, machine, depth + 1);
                    }
                }
                Index(layers[origin].stateMachine, layers[layer].name, null, 0);
                var wantedStates = stateFilter == null ? null : new HashSet<AnimatorState>(paths.Keys.Where(state => stateFilter(layer, state)));
                var wantedMachines = new HashSet<AnimatorStateMachine>();
                if (wantedStates != null)
                    foreach (var state in wantedStates)
                        for (var machine = owners[state]; machine != null; machine = parents[machine]) wantedMachines.Add(machine);
                bool RelevantDestination(AnimatorTransitionBase transition) => wantedStates == null ||
                    transition.destinationState != null && wantedStates.Contains(transition.destinationState) ||
                    transition.destinationStateMachine != null && wantedMachines.Contains(transition.destinationStateMachine);
                var incoming = edges.Where(edge => edge.Transition.destinationStateMachine != null)
                    .GroupBy(edge => edge.Transition.destinationStateMachine).ToDictionary(group => group.Key, group => group.ToArray());
                var incomingStates = edges.Where(edge => edge.Transition.destinationState != null)
                    .GroupBy(edge => edge.Transition.destinationState).ToDictionary(group => group.Key, group => group.ToArray());
                var exits = edges.Where(edge => edge.Transition.isExit)
                    .GroupBy(edge => edge.Owner).ToDictionary(group => group.Key, group => group.ToArray());
                var gateQueries = new Dictionary<string, Constraint[][]>(StringComparer.Ordinal);
                IEnumerable<Constraint[]> RememberGates(string kind, UnityEngine.Object node,
                    HashSet<UnityEngine.Object> stack, Func<IEnumerable<Constraint[]>> compute)
                {
                    Visit();
                    // Recursive eligibility depends on the complete visited
                    // set, including individual Exit edges. A state-only cache
                    // would mix distinct historical entry/exit restrictions.
                    var key = kind + "/" + node.GetInstanceID() + "/" +
                        string.Join(",", stack.Select(value => value.GetInstanceID()).OrderBy(value => value));
                    if (!gateQueries.TryGetValue(key, out var values))
                    {
                        var pendingGates = new List<Constraint[]>();
                        foreach (var gate in compute())
                        {
                            Visit(); // Also bound materialized cached routes.
                            pendingGates.Add(gate);
                        }
                        values = pendingGates.ToArray(); gateQueries.Add(key, values);
                    }
                    foreach (var gate in values) yield return gate;
                }
                Constraint[] Append(Constraint[] gate, IEnumerable<AnimatorCondition> conditions) => gate.Concat(
                    conditions.Select(condition => new Constraint { Condition = condition })).ToArray();
                var priorityRoot = new PriorityPrefix { Alternatives = new[] { Array.Empty<Constraint>() } };
                IEnumerable<Constraint[]> Fallthrough(Constraint[] gate, IEnumerable<AnimatorTransitionBase> entries, Constraint[] simultaneous = null)
                {
                    // Simplify only simultaneous priority exclusions. The
                    // incoming gate may describe an earlier warm-up state and
                    // must remain intact for WarmPrefix/native reachability.
                    // Many generated selectors request the same growing
                    // sequence of priority exclusions for every destination.
                    // Solve each immutable prefix once. Cache only simultaneous
                    // exclusions, never the caller's possibly historical gate.
                    var prefix = priorityRoot;
                    foreach (var entry in entries)
                    {
                        // An immutable false AND branch cannot block a later
                        // transition and needs no disjunctive priority prefix.
                        if (Impossible(entry)) continue;
                        // The destination's own conditions and its earlier
                        // priority alternatives are tested at the same native
                        // decision. A proven contradictory AND cannot block
                        // this targeted route. Historical source gates remain
                        // separate and are never treated as simultaneous.
                        if (simultaneous != null && Solve(Append(simultaneous, entry.conditions), new Dictionary<string, float>(),
                            parameters, defaults, _ => true, CheckCondition, out var contradiction) == null && contradiction == null) continue;
                        CheckCondition(); // Bound cache traversal as well as misses.
                        if (prefix.Children.TryGetValue(entry, out var cached))
                        { prefix = cached; if (prefix.Alternatives.Length == 0) yield break; continue; }
                        // !(A && B) is !A || !B. Keep the original comparison
                        // plus its logical negation, including Float equality
                        // at the boundary of a negated Greater/Less condition.
                        if (entry.conditions.Length == 0)
                        {
                            prefix.Children.Add(entry, new PriorityPrefix { Alternatives = Array.Empty<Constraint[]>() });
                            yield break;
                        }
                        var next = new Dictionary<int, Dictionary<string, (Constraint[] Conditions, HashSet<string> Keys)>>();
                        foreach (var alternative in prefix.Alternatives)
                            foreach (var condition in entry.conditions)
                            {
                                Visit();
                                var combined = alternative.Concat(new[] { new Constraint { Condition = condition, Negated = true } })
                                    .GroupBy(ConstraintKey).Select(group => group.First()).ToArray();
                                // Keep malformed/unsupported conditions for
                                // their normal diagnostics; prune only proven
                                // contradictory parameter conjunctions.
                                if (Solve(combined, new Dictionary<string, float>(), parameters, defaults, _ => true, CheckCondition, out var error) == null && error == null)
                                    continue;
                                var keys = new HashSet<string>(combined.Select(ConstraintKey), StringComparer.Ordinal);
                                var key = string.Concat(keys.OrderBy(value => value, StringComparer.Ordinal).Select(value => value.Length + ":" + value));
                                if (!next.TryGetValue(keys.Count, out var sameSize))
                                    next.Add(keys.Count, sameSize = new Dictionary<string, (Constraint[], HashSet<string>)>(StringComparer.Ordinal));
                                if (sameSize.ContainsKey(key)) continue;
                                bool Contains(HashSet<string> superset, HashSet<string> subset)
                                { Visit(); return superset.IsSupersetOf(subset); }
                                // Equal-sized alternatives use a hash lookup.
                                // Bound the remaining subset comparisons too,
                                // so independent conditions cannot create an
                                // unaccounted quadratic expansion.
                                if (next.Where(group => group.Key < keys.Count).SelectMany(group => group.Value.Values)
                                    .Any(existing => Contains(keys, existing.Keys))) continue;
                                foreach (var group in next.Where(group => group.Key > keys.Count))
                                    foreach (var existing in group.Value.Where(value => Contains(value.Value.Keys, keys)).Select(value => value.Key).ToArray())
                                        group.Value.Remove(existing);
                                sameSize.Add(key, (combined, keys));
                            }
                        var child = new PriorityPrefix { Alternatives = next.Values.SelectMany(group => group.Values).Select(value => value.Conditions).ToArray() };
                        prefix.Children.Add(entry, child); prefix = child;
                        if (prefix.Alternatives.Length == 0) yield break;
                    }
                    foreach (var alternative in prefix.Alternatives) yield return gate.Concat(alternative).ToArray();
                }
                IEnumerable<Constraint[]> SourceGates(Edge edge, HashSet<UnityEngine.Object> stack)
                {
                    if (Impossible(edge.Transition)) yield break;
                    var gates = edge.Source != null ? StateGates(edge.Source, stack) : edge.SourceMachine != null ? ExitGates(edge.SourceMachine, stack) :
                        Gates(edge.Owner, stack);
                    foreach (var gate in gates)
                        if (edge.PreviousEntries == null) yield return gate;
                        else foreach (var prior in Fallthrough(gate, edge.PreviousEntries,
                            Append(Array.Empty<Constraint>(), edge.Transition.conditions))) yield return prior;
                }
                IEnumerable<Constraint[]> DefaultGates(AnimatorStateMachine machine, HashSet<UnityEngine.Object> stack)
                {
                    foreach (var gate in Gates(machine, stack))
                        foreach (var fallback in Fallthrough(gate, Enabled(machine.entryTransitions))) yield return fallback;
                }
                IEnumerable<Constraint[]> ExitGates(AnimatorStateMachine machine, HashSet<UnityEngine.Object> stack)
                    => RememberGates("exit", machine, stack, () => ComputeExitGates(machine, stack));
                IEnumerable<Constraint[]> ComputeExitGates(AnimatorStateMachine machine, HashSet<UnityEngine.Object> stack)
                {
                    if (!exits.TryGetValue(machine, out var entries)) yield break;
                    foreach (var entry in entries)
                    {
                        if (stack.Count >= MaximumDepth)
                            throw new InvalidOperationException("FXの条件経路が深すぎるため、自動表情探索を完全に完了できません。");
                        // An exit and its machine's entry are distinct graph
                        // steps. Track the exit edge itself so valid entry
                        // gates remain available while recursive exits stop.
                        if (!stack.Add(entry.Transition)) continue;
                        try
                        {
                            foreach (var previous in SourceGates(entry, stack))
                                yield return Append(previous, entry.Transition.conditions);
                        }
                        finally { stack.Remove(entry.Transition); }
                    }
                }
                IEnumerable<Constraint[]> Gates(AnimatorStateMachine machine, HashSet<UnityEngine.Object> stack)
                    => RememberGates("machine", machine, stack, () => ComputeMachineGates(machine, stack));
                IEnumerable<Constraint[]> ComputeMachineGates(AnimatorStateMachine machine, HashSet<UnityEngine.Object> stack)
                {
                    if (stack.Count >= MaximumDepth)
                        throw new InvalidOperationException("FXの条件経路が深すぎるため、自動表情探索を完全に完了できません。");
                    if (!stack.Add(machine)) yield break;
                    try
                    {
                        if (parents[machine] == null || !incoming.TryGetValue(machine, out var entries))
                        { yield return Array.Empty<Constraint>(); yield break; }
                        foreach (var entry in entries)
                            foreach (var previous in SourceGates(entry, stack))
                                yield return Append(previous, entry.Transition.conditions);
                    }
                    finally { stack.Remove(machine); }
                }
                IEnumerable<Constraint[]> StateGates(AnimatorState state, HashSet<UnityEngine.Object> stack)
                    => RememberGates("state", state, stack, () => ComputeStateGates(state, stack));
                IEnumerable<Constraint[]> ComputeStateGates(AnimatorState state, HashSet<UnityEngine.Object> stack)
                {
                    if (stack.Count >= MaximumDepth)
                        throw new InvalidOperationException("FXの条件経路が深すぎるため、自動表情探索を安全に完了できません。");
                    if (!stack.Add(state)) yield break;
                    try
                    {
                        var owner = owners[state];
                        if (owner.defaultState == state)
                            foreach (var gate in DefaultGates(owner, stack)) yield return gate;
                        else if (!incomingStates.TryGetValue(state, out _))
                            foreach (var gate in Gates(owner, stack)) yield return gate;
                        if (incomingStates.TryGetValue(state, out var entries))
                            foreach (var entry in entries)
                                foreach (var prior in SourceGates(entry, stack))
                                    yield return Append(prior, entry.Transition.conditions);
                    }
                    finally { stack.Remove(state); }
                }
                int WarmPrefix(Constraint[] gate) => gate.All(constraint => !string.IsNullOrEmpty(constraint.Condition.parameter) &&
                    defaults.TryGetValue(constraint.Condition.parameter, out var value) &&
                    Matches(constraint.Condition, value) != constraint.Negated) ? gate.Length : 0;
                bool SuppliesFace(AnimatorState state)
                {
                    var behaviours = controller.GetStateEffectiveBehaviours(state, layer) ?? Array.Empty<StateMachineBehaviour>();
                    if (behaviours.Any(behaviour => behaviour != null && drivers.TryGetValue(behaviour, out var program) &&
                        (program.Error != null || program.Operations.Any(operation => faceInputs.Contains(operation.Destination)))) ||
                        behaviours.Any(behaviour => behaviour != null && weightControls.TryGetValue(behaviour, out var control) &&
                        control.AnimatorLayer && control.Playable == "FX" && (dependencies[control.LayerIndex].Morphs.Count > 0 ||
                            dependencies[control.LayerIndex].Writes.Overlaps(faceInputs)))) return true;
                    var motion = EffectiveMotion(controller, state, layer);
                    if (motion == null) return false;
                    if (!faceOutputs.TryGetValue(motion, out var hasFace))
                    { hasFace = HasFaceOutput(motion, replacements, excludedPath, faceInputs, new HashSet<Motion>(), Visit); faceOutputs.Add(motion, hasFace); }
                    return hasFace;
                }
                var routes = new List<(AnimatorState State, Constraint[] Conditions, int WarmPrefix)>();
                void Destination(AnimatorTransitionBase transition, Constraint[] conditions, HashSet<AnimatorStateMachine> stack, int warmPrefix)
                {
                    if (Impossible(transition) || !RelevantDestination(transition)) return;
                    Visit();
                    if (transition.destinationState != null && paths.ContainsKey(transition.destinationState))
                        routes.Add((transition.destinationState, conditions, warmPrefix));
                    var machine = transition.destinationStateMachine;
                    if (machine == null || !parents.ContainsKey(machine) || !stack.Add(machine)) return;
                    try
                    {
                        var previousEntries = new List<AnimatorTransitionBase>();
                        foreach (var entry in Enabled(machine.entryTransitions))
                        {
                            if (RelevantDestination(entry))
                                foreach (var prior in Fallthrough(conditions, previousEntries,
                                    Append(Array.Empty<Constraint>(), entry.conditions)))
                                    Destination(entry, Append(prior, entry.conditions), stack, warmPrefix);
                            if (entry.conditions.Length == 0) return;
                            previousEntries.Add(entry);
                        }
                        // A fallback with no face/relay/weight output cannot
                        // supply a candidate. Historical gates for later real
                        // outputs are still audited by StateGates separately.
                        if (machine.defaultState != null && SuppliesFace(machine.defaultState) &&
                            (wantedStates == null || wantedStates.Contains(machine.defaultState)))
                            foreach (var fallback in Fallthrough(conditions, previousEntries)) routes.Add((machine.defaultState, fallback, warmPrefix));
                    }
                    finally { stack.Remove(machine); }
                }
                foreach (var edge in edges)
                    if (RelevantDestination(edge.Transition))
                        foreach (var gate in SourceGates(edge, new HashSet<UnityEngine.Object>()))
                            Destination(edge.Transition, Append(gate, edge.Transition.conditions), new HashSet<AnimatorStateMachine>(), WarmPrefix(gate));
                // A default BlendTree or motion-time clip may expose a face
                // slider without any transition. Include its input knots too.
                foreach (var state in paths.Keys)
                    if ((wantedStates == null || wantedStates.Contains(state)) && (EffectiveMotion(controller, state, layer) is BlendTree || state.timeParameterActive &&
                        EffectiveMotion(controller, state, layer) is AnimationClip timedDefault &&
                        VrChatGestureExpressions.HasChangingMorph(replacements.TryGetValue(timedDefault, out var effectiveDefault) ? effectiveDefault : timedDefault, excludedPath)))
                        foreach (var gate in StateGates(state, new HashSet<UnityEngine.Object>())) routes.Add((state, gate, WarmPrefix(gate)));
                foreach (var route in routes)
                {
                    if (stateFilter?.Invoke(layer, route.State) == false) continue;
                    var motion = EffectiveMotion(controller, route.State, layer);
                    var behaviours = controller.GetStateEffectiveBehaviours(route.State, layer) ?? Array.Empty<StateMachineBehaviour>();
                    var driverMorph = behaviours
                        .Any(behaviour => behaviour != null && drivers.TryGetValue(behaviour, out var program) &&
                            (program.Error != null || program.Operations.Any(operation => faceInputs.Contains(operation.Destination))));
                    var weightMorph = behaviours.Any(behaviour => behaviour != null && weightControls.TryGetValue(behaviour, out var control) &&
                        control.AnimatorLayer && control.Playable == "FX" && (dependencies[control.LayerIndex].Morphs.Count > 0 ||
                            dependencies[control.LayerIndex].Writes.Overlaps(faceInputs)));
                    if (!driverMorph && !weightMorph)
                    {
                        if (motion == null) continue;
                        if (!faceOutputs.TryGetValue(motion, out var hasFace))
                        { hasFace = HasFaceOutput(motion, replacements, excludedPath, faceInputs, new HashSet<Motion>(), Visit); faceOutputs.Add(motion, hasFace); }
                        if (!hasFace) continue;
                    }
                    Point[] points;
                    if (motion is BlendTree tree)
                    {
                        if (!controlPoints.TryGetValue(tree, out points))
                        { points = Points(tree, parameters, Selectable, new HashSet<BlendTree>(), Visit).ToArray(); controlPoints.Add(tree, points); }
                    }
                    else if (motion is AnimationClip timedClip && route.State.timeParameterActive &&
                        VrChatGestureExpressions.HasChangingMorph(replacements.TryGetValue(timedClip, out var effectiveTimed) ? effectiveTimed : timedClip, excludedPath))
                    {
                        if (replacements.TryGetValue(timedClip, out var replacement)) timedClip = replacement;
                        points = MotionTimePoints(route.State, timedClip, parameters, Selectable, excludedPath, Visit).ToArray();
                    }
                    else points = new[] { new Point() };
                    if (points.Length == 0) continue;
                    foreach (var point in points)
                    {
                        if (!route.Conditions.Any(constraint => Selectable(constraint.Condition.parameter)) && !point.Values.Keys.Any(Selectable)) continue;
                        var values = Solve(route.Conditions, point.Values, parameters, defaults, Selectable, CheckCondition, out var error);
                        var speculativeWarmStart = false;
                        Dictionary<string, float> arrivalValues = null;
                        // A gate established during the sampler's native default
                        // initialization need not remain true after selection.
                        // Only try this when that past gate matches every default
                        // and is the reason the current conjunction is unsolvable.
                        // Full native reachability/stability must still succeed.
                        if (values == null && route.WarmPrefix > 0)
                        {
                            values = Solve(route.Conditions.Skip(route.WarmPrefix), point.Values, parameters, defaults, Selectable, CheckCondition, out error);
                            speculativeWarmStart = true;
                        }
                        if (values == null && error == null && stateFilter != null && motion is BlendTree && selectableBuiltIns != null)
                        {
                            // An authored tree knot can remain held after the
                            // hand that entered the state changes. Preserve
                            // the original arrival route separately, using
                            // only normal or final hand values so the complete
                            // normal/final dependency audit covers every stage.
                            var conditions = route.Conditions.Skip(speculativeWarmStart ? route.WarmPrefix : 0).ToArray();
                            var controls = point.Values.Where(pair => !selectableBuiltIns.Contains(pair.Key))
                                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                            var arrival = Solve(conditions, controls, parameters, defaults, Selectable, CheckCondition, out var arrivalError);
                            if (arrival != null && arrivalError == null)
                            {
                                var final = new Dictionary<string, float>(arrival, StringComparer.Ordinal);
                                foreach (var pair in point.Values) final[pair.Key] = selectableBuiltIns.Contains(pair.Key) &&
                                    pair.Key.EndsWith("Weight", StringComparison.Ordinal) ? Mathf.Clamp01(pair.Value) : pair.Value;
                                var possible = true;
                                foreach (var name in final.Keys.Where(selectableBuiltIns.Contains).ToArray())
                                {
                                    var group = conditions.Where(constraint => constraint.Condition.parameter == name).ToArray();
                                    var options = new[] { final[name], defaults.TryGetValue(name, out var normal) ? normal : 0 };
                                    var found = options.Where(option => group.All(constraint =>
                                        Matches(constraint.Condition, option) != constraint.Negated)).ToArray();
                                    if (found.Length == 0) { possible = false; break; }
                                    arrival[name] = found[0];
                                }
                                if (possible && arrival.Any(pair => final.TryGetValue(pair.Key, out var value) && pair.Value != value))
                                { values = final; arrivalValues = arrival; }
                            }
                        }
                        if (values == null || error == null && values.All(pair => defaults.TryGetValue(pair.Key, out var value) && value == pair.Value)) continue;
                        if (!emitted.Add(layer + "/" + paths[route.State] + "/" + Assignment(values) + "/" + error)) continue;
                        if (++candidates > MaximumCandidates)
                            throw new InvalidOperationException("FXの自動表情候補が256件を超えています。表情の登録を整理してください。");
                        yield return new Candidate { Layer = layer, State = route.State, Path = paths[route.State], Values = values,
                            SpeculativeWarmStart = speculativeWarmStart, ArrivalValues = arrivalValues,
                            DefaultValues = point.PreservesInputs || weightMorph ? values.Keys.ToDictionary(name => name,
                                name => defaults.TryGetValue(name, out var value) ? value : 0, StringComparer.Ordinal) : null,
                            Error = paths.Values.Count(path => path == paths[route.State]) != 1 ? "FXの表情状態のパスが重複しています。状態名を確認してください。" : error };
                    }
                }
            }
        }

        static Dictionary<string, float> Solve(IEnumerable<Constraint> conditions, IDictionary<string, float> point,
            IDictionary<string, AnimatorControllerParameter> parameters, IDictionary<string, float> defaults,
            Func<string, bool> selectable, Action checkCondition, out string error)
        {
            error = null;
            var result = new Dictionary<string, float>(point, StringComparer.Ordinal);
            foreach (var group in conditions.GroupBy(constraint => { checkCondition(); return constraint.Condition.parameter; }))
            {
                if (string.IsNullOrEmpty(group.Key) || !parameters.TryGetValue(group.Key, out var parameter))
                { error = "FXの条件のパラメーターがありません: " + group.Key; return result; }
                if (parameter.type == AnimatorControllerParameterType.Trigger)
                { error = "Triggerの条件は固定表情として再現できません: " + group.Key; return result; }
                var constraints = group.ToArray();
                var current = result.TryGetValue(group.Key, out var selected) ? selected : defaults[group.Key];
                if (!Finite(current) || constraints.Any(constraint => !Finite(constraint.Condition.threshold)))
                { error = "FXの条件の値が不正です: " + group.Key; return result; }
                if (constraints.Any(constraint => !ValidMode(parameter.type, constraint.Condition.mode)))
                { error = "FXの条件の型と比較方法を再現できません: " + group.Key; return result; }
                bool Accept(float value) => constraints.All(constraint =>
                { checkCondition(); return Matches(constraint.Condition, value) != constraint.Negated; });
                if (!selectable(group.Key) || result.ContainsKey(group.Key))
                { if (!Accept(current)) return null; }
                else
                {
                    var options = new List<float> { current };
                    foreach (var constraint in constraints)
                    {
                        var condition = constraint.Condition;
                        var step = parameter.type == AnimatorControllerParameterType.Int ? 1f : Mathf.Max(.001f, Mathf.Abs(condition.threshold) * .0001f);
                        if (condition.mode == AnimatorConditionMode.If || condition.mode == AnimatorConditionMode.IfNot) { options.Add(0); options.Add(1); }
                        else { options.Add(condition.threshold); options.Add(condition.threshold + step); options.Add(condition.threshold - step); }
                    }
                    var boundaries = constraints.Select(constraint => constraint.Condition.threshold).Distinct().OrderBy(value => value).ToArray();
                    for (var index = 1; index < boundaries.Length; index++)
                    {
                        var middle = ((double)boundaries[index - 1] + boundaries[index]) * .5;
                        options.Add(parameter.type == AnimatorControllerParameterType.Int ? (float)Math.Floor(middle) : (float)middle);
                    }
                    var found = options.Where(Finite).FirstOrDefault(value => Accept(value));
                    if (!Accept(found)) return null;
                    current = found;
                }
                if (selectable(group.Key)) result[group.Key] = current;
            }
            return result;
        }

        static IEnumerable<Point> Points(BlendTree tree, IDictionary<string, AnimatorControllerParameter> parameters, Func<string, bool> selectable,
            HashSet<BlendTree> stack, Action visit)
        {
            visit();
            if (stack.Count >= MaximumDepth || !stack.Add(tree))
                throw new InvalidOperationException("FXのBlendTree階層を安全に探索できません。");
            try
            {
                var children = tree.children;
                void ValidateControl(string name)
                {
                    if (string.IsNullOrEmpty(name) || !parameters.TryGetValue(name, out var parameter))
                        throw new InvalidOperationException("FXのBlendTreeの入力パラメーターがControllerにありません: " + tree.name + " / " + (name ?? "(未指定)"));
                    if (parameter.type != AnimatorControllerParameterType.Float)
                        throw new InvalidOperationException("FXのBlendTreeの入力パラメーターはFloat型である必要があります: " + tree.name + " / " + name);
                }
                // Validate native controls even when they are readonly or
                // generated. An undeclared input is not a selectable knot:
                // native SetParameters cannot apply the inferred assignment.
                if (tree.blendType == BlendTreeType.Direct)
                    foreach (var child in children) ValidateControl(child.directBlendParameter);
                else
                {
                    ValidateControl(tree.blendParameter);
                    if (tree.blendType != BlendTreeType.Simple1D) ValidateControl(tree.blendParameterY);
                }
                for (var index = 0; index < children.Length; index++)
                {
                    visit();
                    var child = children[index];
                    if (tree.blendType != BlendTreeType.Direct && (tree.blendType == BlendTreeType.Simple1D ? !Finite(child.threshold) :
                        !Finite(child.position.x) || !Finite(child.position.y)))
                        throw new InvalidOperationException("FXのBlendTreeの設定値が不正です。");
                    var point = new Point();
                    if (tree.blendType == BlendTreeType.Direct)
                    {
                        point.PreservesInputs = children.Any(value => !selectable(value.directBlendParameter));
                        foreach (var value in children.Where(value => selectable(value.directBlendParameter))) point.Values[value.directBlendParameter] = 0;
                        if (selectable(child.directBlendParameter)) point.Values[child.directBlendParameter] = 1;
                    }
                    else
                    {
                        if (selectable(tree.blendParameter))
                            point.Values[tree.blendParameter] = tree.blendType == BlendTreeType.Simple1D ? child.threshold : child.position.x;
                        else point.PreservesInputs = true;
                        if (tree.blendType != BlendTreeType.Simple1D)
                        {
                            if (selectable(tree.blendParameterY)) point.Values[tree.blendParameterY] = child.position.y;
                            else point.PreservesInputs = true;
                        }
                    }
                    if (point.Values.Values.Any(value => !Finite(value))) throw new InvalidOperationException("FXのBlendTreeの設定値が不正です。");
                    if (!(child.motion is BlendTree nested)) { yield return point; continue; }
                    var innerPoints = Points(nested, parameters, selectable, stack, visit).ToArray();
                    // Empty trees do not erase a surrounding user selection.
                    // Readonly inputs at every level stay in the native graph;
                    // only authored user knots become selected assignments.
                    if (innerPoints.Length == 0) { yield return point; continue; }
                    foreach (var inner in innerPoints)
                    {
                        // One control may drive several tree levels. An inner
                        // knot is still an authored input value when the outer
                        // tree clamps or blends at that value; sample the full
                        // native tree rather than imposing parent-knot equality.
                        var combined = new Point { Values = new Dictionary<string, float>(point.Values, StringComparer.Ordinal),
                            PreservesInputs = point.PreservesInputs || inner.PreservesInputs };
                        foreach (var pair in inner.Values) combined.Values[pair.Key] = pair.Value;
                        yield return combined;
                    }
                }
            }
            finally { stack.Remove(tree); }
        }

        static IEnumerable<Point> MotionTimePoints(AnimatorState state, AnimationClip clip,
            IDictionary<string, AnimatorControllerParameter> parameters, Func<string, bool> selectable,
            Func<string, bool> excludedPath, Action visit)
        {
            var name = state.timeParameter;
            if (string.IsNullOrEmpty(name) || !parameters.TryGetValue(name, out var parameter) || parameter.type != AnimatorControllerParameterType.Float)
                throw new InvalidOperationException("FXのMotion TimeにはControllerで宣言されたFloat入力が必要です: " + state.name + " / " + name);
            if (!Finite(clip.length) || clip.length <= 0 || clip.isLooping)
                throw new InvalidOperationException("FXのMotion Timeは有限の長さを持つ非ループのClipが必要です: " + state.name + " / " + clip.name);
            var knots = new SortedSet<float> { 0, 1 };
            foreach (var binding in AnimationUtility.GetCurveBindings(clip).Where(binding => excludedPath?.Invoke(binding.path) != true &&
                binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null || curve.length == 0)
                    throw new InvalidOperationException("FXのMotion TimeのBlendShape曲線を読み取れません: " + clip.name + " / " + binding.propertyName);
                VrChatExpressionSampler.ValidateNativeParameterCurve(curve, clip.name + " / " + binding.propertyName);
                foreach (var key in curve.keys)
                {
                    visit();
                    if (key.time < 0 || key.time > clip.length)
                        throw new InvalidOperationException("FXのMotion Timeのキー時刻がClipの範囲外です: " + clip.name);
                    knots.Add(key.time / clip.length);
                }
            }
            if (!selectable(name)) { yield return new Point { PreservesInputs = true }; yield break; }
            foreach (var value in knots)
            {
                visit();
                var point = new Point(); point.Values.Add(name, value); yield return point;
            }
        }

        static bool HasFaceOutput(Motion motion, IDictionary<AnimationClip, AnimationClip> replacements, Func<string, bool> excludedPath,
            ISet<string> faceInputs,
            HashSet<Motion> stack, Action visit)
        {
            if (motion == null) return false;
            visit();
            if (stack.Count >= MaximumDepth || !stack.Add(motion))
                throw new InvalidOperationException("FXのMotion参照が循環または深すぎる階層になっています。");
            try
            {
                if (motion is AnimationClip clip)
                {
                    if (replacements.TryGetValue(clip, out var replacement)) clip = replacement;
                    return AnimationUtility.GetCurveBindings(clip).Any(binding =>
                        binding.type == typeof(Animator) && faceInputs.Contains(binding.propertyName) ||
                        excludedPath?.Invoke(binding.path) != true && binding.type == typeof(SkinnedMeshRenderer) &&
                        binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal));
                }
                return motion is BlendTree tree && tree.children.Any(child => HasFaceOutput(child.motion, replacements, excludedPath, faceInputs, stack, visit));
            }
            finally { stack.Remove(motion); }
        }

        static Motion EffectiveMotion(AnimatorController controller, AnimatorState state, int layer)
        {
            var motion = controller.GetStateEffectiveMotion(state, layer);
            var parent = controller.layers[layer].syncedLayerIndex;
            return motion != null || parent < 0 ? motion : EffectiveMotion(controller, state, parent);
        }
        static IEnumerable<T> Enabled<T>(IEnumerable<T> transitions) where T : AnimatorTransitionBase
        {
            var values = transitions.ToArray(); var solo = values.Any(value => value.solo);
            return values.Where(value => !value.mute && (!solo || value.solo));
        }
        static bool TrackingParameter(string name) => name.StartsWith("FT/", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("VRCFT/", StringComparison.OrdinalIgnoreCase) || name.StartsWith("v2/", StringComparison.Ordinal) ||
            UnifiedExpressionRegistry.TryCanonicalize(name, out _);
        static bool GeneratedParameter(string name) => name.StartsWith("__MA/", StringComparison.Ordinal) ||
            name.StartsWith("__ActiveSelf", StringComparison.Ordinal);
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static string ConstraintKey(Constraint value) => value.Condition.parameter + "\u001f" + (int)value.Condition.mode + "/" +
            value.Condition.threshold.ToString("R", CultureInfo.InvariantCulture) + "/" + value.Negated;
        static bool ValidMode(AnimatorControllerParameterType type, AnimatorConditionMode mode) =>
            type == AnimatorControllerParameterType.Bool ? mode == AnimatorConditionMode.If || mode == AnimatorConditionMode.IfNot :
            type == AnimatorControllerParameterType.Int ? mode == AnimatorConditionMode.Equals || mode == AnimatorConditionMode.NotEqual ||
                mode == AnimatorConditionMode.Greater || mode == AnimatorConditionMode.Less :
            type == AnimatorControllerParameterType.Float && (mode == AnimatorConditionMode.Greater || mode == AnimatorConditionMode.Less);
        static bool Matches(AnimatorCondition condition, float value)
        {
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
        static string Assignment(IDictionary<string, float> values) => string.Join(", ", values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + "=" + pair.Value.ToString("G9", CultureInfo.InvariantCulture)));
        static bool SameParameters(IDictionary<string, float> first, IDictionary<string, float> second) => first.Count == second.Count &&
            first.All(pair => second.TryGetValue(pair.Key, out var value) && value.Equals(pair.Value));
        static bool SamePose(VrChatExpressionMenu.Entry first, VrChatExpressionMenu.Entry second)
        {
            if (first.Error != null || second.Error != null || first.Animation.Count != second.Animation.Count ||
                first.Values.Count != second.Values.Count || first.Unevaluated.Count != second.Unevaluated.Count ||
                first.Loop != second.Loop || Math.Abs(first.Duration - second.Duration) > .0001f) return false;
            bool Same(IList<VrChatExpressionMenu.MorphValue> a, IList<VrChatExpressionMenu.MorphValue> b) => a.All(value =>
                b.Any(other => value.Path == other.Path && value.Shape == other.Shape && Mathf.Abs(value.Weight - other.Weight) < .001f));
            return Same(first.Values, second.Values) && Same(first.Unevaluated, second.Unevaluated) && first.Animation.All(channel =>
                second.Animation.Any(other => channel.Path == other.Path && channel.Shape == other.Shape &&
                    VrChatExpressionBaker.SameCurve(channel.Curve, other.Curve)));
        }
    }
}
