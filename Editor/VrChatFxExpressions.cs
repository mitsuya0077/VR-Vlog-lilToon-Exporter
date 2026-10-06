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

        sealed class Candidate
        {
            internal int Layer;
            internal AnimatorState State;
            internal string Path, Error;
            internal Dictionary<string, float> Values;
        }

        sealed class Edge
        {
            internal AnimatorStateMachine Owner;
            internal AnimatorState Source;
            internal AnimatorTransitionBase Transition;
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
                            entry.Messages.Add("FXの条件・BlendTreeの設定値から表情を読み込みました: " + assignment);
                        }
                        catch (InvalidOperationException error) { entry.Error = error.Message; }
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

        static IEnumerable<Candidate> Discover(VrChatExpressionMenu.Source source, FixedExpressionContext context,
            Func<string, bool> excludedPath)
        {
            var controller = ExpressionDependencies.Controller(source.Controller);
            var parameters = controller.parameters.ToDictionary(value => value.name, StringComparer.Ordinal);
            var defaults = parameters.Values.ToDictionary(value => value.name, value =>
                value.type == AnimatorControllerParameterType.Bool ? (value.defaultBool ? 1f : 0f) :
                value.type == AnimatorControllerParameterType.Int ? value.defaultInt : value.defaultFloat, StringComparer.Ordinal);
            foreach (var pair in source.Defaults) defaults[pair.Key] = pair.Value;
            foreach (var pair in context.Values) defaults[pair.Key] = pair.Value;
            // Animator-written values are graph outputs (for example an MA
            // activeSelf proxy), not independent controls. Retain parameters
            // explicitly declared as user expression/menu inputs, but do not
            // manufacture a facial state by overriding an internal signal.
            var curveInputs = new HashSet<string>(ExpressionDependencies.Inspect(source.Controller, excludedPath,
                new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), new List<string>())
                .SelectMany(value => value.CurveWrites), StringComparer.Ordinal);
            bool UserInput(string name) => source.ExpressionParameters.Contains(name) || source.MenuInputs.Contains(name);
            bool Selectable(string name) => !string.IsNullOrEmpty(name) && !VrChatParameterDriver.BuiltIn.Contains(name) &&
                !source.ExternalParameters.Contains(name) && !TrackingParameter(name) &&
                (UserInput(name) || !curveInputs.Contains(name) && !GeneratedParameter(name));
            var replacements = ExpressionDependencies.Overrides(source.Controller);
            var layers = controller.layers;
            var nodes = 0;
            var candidates = 0;
            var emitted = new HashSet<string>(StringComparer.Ordinal);
            void Visit()
            {
                if (++nodes > MaximumNodes)
                    throw new InvalidOperationException("FXの表情探索が上限を超えています。Controllerの構成を確認してください。");
            }
            for (var layer = 0; layer < layers.Length; layer++)
            {
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
                        foreach (var transition in Enabled(child.state.transitions))
                            edges.Add(new Edge { Owner = machine, Source = child.state, Transition = transition });
                    }
                    foreach (var transition in Enabled(machine.anyStateTransitions).Cast<AnimatorTransitionBase>().Concat(Enabled(machine.entryTransitions)))
                        edges.Add(new Edge { Owner = machine, Transition = transition });
                    foreach (var child in machine.stateMachines)
                    {
                        foreach (var transition in Enabled(machine.GetStateMachineTransitions(child.stateMachine)))
                            edges.Add(new Edge { Owner = machine, Transition = transition });
                        Index(child.stateMachine, path + "." + child.stateMachine.name, machine, depth + 1);
                    }
                }
                Index(layers[origin].stateMachine, layers[layer].name, null, 0);
                var incoming = edges.Where(edge => edge.Transition.destinationStateMachine != null)
                    .GroupBy(edge => edge.Transition.destinationStateMachine).ToDictionary(group => group.Key, group => group.ToArray());
                var incomingStates = edges.Where(edge => edge.Transition.destinationState != null)
                    .GroupBy(edge => edge.Transition.destinationState).ToDictionary(group => group.Key, group => group.ToArray());
                IEnumerable<AnimatorCondition[]> Gates(AnimatorStateMachine machine, HashSet<AnimatorStateMachine> stack)
                {
                    if (parents[machine] == null || !incoming.TryGetValue(machine, out var entries))
                    { yield return Array.Empty<AnimatorCondition>(); yield break; }
                    if (!stack.Add(machine)) yield break;
                    try
                    {
                        foreach (var entry in entries)
                            foreach (var previous in Gates(entry.Owner, stack))
                                yield return previous.Concat(entry.Transition.conditions).ToArray();
                    }
                    finally { stack.Remove(machine); }
                }
                IEnumerable<AnimatorCondition[]> StateGates(AnimatorState state, HashSet<AnimatorState> stack)
                {
                    Visit();
                    if (stack.Count >= MaximumDepth)
                        throw new InvalidOperationException("FXの条件経路が深すぎるため、自動表情探索を安全に完了できません。");
                    if (!stack.Add(state)) yield break;
                    try
                    {
                        var owner = owners[state];
                        if (owner.defaultState == state || !incomingStates.TryGetValue(state, out var entries))
                            foreach (var gate in Gates(owner, new HashSet<AnimatorStateMachine>())) yield return gate;
                        if (incomingStates.TryGetValue(state, out entries))
                            foreach (var entry in entries)
                                foreach (var prior in entry.Source != null ? StateGates(entry.Source, stack) :
                                    Gates(entry.Owner, new HashSet<AnimatorStateMachine>()))
                                    yield return prior.Concat(entry.Transition.conditions).ToArray();
                    }
                    finally { stack.Remove(state); }
                }
                var routes = new List<(AnimatorState State, AnimatorCondition[] Conditions)>();
                void Destination(AnimatorTransitionBase transition, AnimatorCondition[] conditions, HashSet<AnimatorStateMachine> stack)
                {
                    Visit();
                    if (transition.destinationState != null && paths.ContainsKey(transition.destinationState))
                        routes.Add((transition.destinationState, conditions));
                    var machine = transition.destinationStateMachine;
                    if (machine == null || !parents.ContainsKey(machine) || !stack.Add(machine)) return;
                    try
                    {
                        foreach (var entry in Enabled(machine.entryTransitions))
                        {
                            Destination(entry, conditions.Concat(entry.conditions).ToArray(), stack);
                            if (entry.conditions.Length == 0) return;
                        }
                        if (machine.defaultState != null) routes.Add((machine.defaultState, conditions));
                    }
                    finally { stack.Remove(machine); }
                }
                foreach (var edge in edges)
                    foreach (var gate in edge.Source != null ? StateGates(edge.Source, new HashSet<AnimatorState>()) :
                        Gates(edge.Owner, new HashSet<AnimatorStateMachine>()))
                        Destination(edge.Transition, gate.Concat(edge.Transition.conditions).ToArray(), new HashSet<AnimatorStateMachine>());
                // A default BlendTree may expose a face slider without any
                // transition at all. Include its authored control points too.
                foreach (var state in paths.Keys)
                    if (EffectiveMotion(controller, state, layer) is BlendTree)
                        foreach (var gate in StateGates(state, new HashSet<AnimatorState>())) routes.Add((state, gate));
                foreach (var route in routes)
                {
                    var motion = EffectiveMotion(controller, route.State, layer);
                    if (!HasMorph(motion, replacements, excludedPath, new HashSet<Motion>(), Visit)) continue;
                    var points = motion is BlendTree tree ? Points(tree, Selectable, new HashSet<BlendTree>(), Visit).ToArray() :
                        new[] { new Dictionary<string, float>(StringComparer.Ordinal) };
                    if (points.Length == 0) continue;
                    foreach (var point in points)
                    {
                        if (!route.Conditions.Any(condition => Selectable(condition.parameter)) && !point.Keys.Any(Selectable)) continue;
                        var values = Solve(route.Conditions, point, parameters, defaults, Selectable, out var error);
                        if (values == null || error == null && values.All(pair => defaults.TryGetValue(pair.Key, out var value) && value == pair.Value)) continue;
                        if (!emitted.Add(layer + "/" + paths[route.State] + "/" + Assignment(values) + "/" + error)) continue;
                        if (++candidates > MaximumCandidates)
                            throw new InvalidOperationException("FXの自動表情候補が256件を超えています。表情の登録を整理してください。");
                        yield return new Candidate { Layer = layer, State = route.State, Path = paths[route.State], Values = values,
                            Error = paths.Values.Count(path => path == paths[route.State]) != 1 ? "FXの表情状態のパスが重複しています。状態名を確認してください。" : error };
                    }
                }
            }
        }

        static Dictionary<string, float> Solve(IEnumerable<AnimatorCondition> conditions, IDictionary<string, float> point,
            IDictionary<string, AnimatorControllerParameter> parameters, IDictionary<string, float> defaults,
            Func<string, bool> selectable, out string error)
        {
            error = null;
            var result = new Dictionary<string, float>(point, StringComparer.Ordinal);
            foreach (var group in conditions.GroupBy(condition => condition.parameter))
            {
                if (string.IsNullOrEmpty(group.Key) || !parameters.TryGetValue(group.Key, out var parameter))
                { error = "FXの条件のパラメーターがありません: " + group.Key; return result; }
                if (parameter.type == AnimatorControllerParameterType.Trigger)
                { error = "Triggerの条件は固定表情として再現できません: " + group.Key; return result; }
                var constraints = group.ToArray();
                var current = result.TryGetValue(group.Key, out var selected) ? selected : defaults[group.Key];
                if (!Finite(current) || constraints.Any(condition => !Finite(condition.threshold)))
                { error = "FXの条件の値が不正です: " + group.Key; return result; }
                if (constraints.Any(condition => !ValidMode(parameter.type, condition.mode)))
                { error = "FXの条件の型と比較方法を再現できません: " + group.Key; return result; }
                bool Accept(float value) => constraints.All(condition => Matches(condition, value));
                if (!selectable(group.Key) || result.ContainsKey(group.Key))
                { if (!Accept(current)) return null; }
                else
                {
                    var options = new List<float> { current };
                    foreach (var condition in constraints)
                    {
                        var step = parameter.type == AnimatorControllerParameterType.Int ? 1f : Mathf.Max(.001f, Mathf.Abs(condition.threshold) * .0001f);
                        if (condition.mode == AnimatorConditionMode.If) options.Add(1);
                        else if (condition.mode == AnimatorConditionMode.IfNot) options.Add(0);
                        else { options.Add(condition.threshold); options.Add(condition.threshold + step); options.Add(condition.threshold - step); }
                    }
                    var boundaries = constraints.Select(condition => condition.threshold).Distinct().OrderBy(value => value).ToArray();
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

        static IEnumerable<Dictionary<string, float>> Points(BlendTree tree, Func<string, bool> selectable,
            HashSet<BlendTree> stack, Action visit)
        {
            visit();
            if (stack.Count >= MaximumDepth || !stack.Add(tree))
                throw new InvalidOperationException("FXのBlendTree階層を安全に探索できません。");
            try
            {
                var children = tree.children;
                for (var index = 0; index < children.Length; index++)
                {
                    visit();
                    var child = children[index];
                    var point = new Dictionary<string, float>(StringComparer.Ordinal);
                    if (tree.blendType == BlendTreeType.Direct)
                    {
                        if (children.Any(value => !selectable(value.directBlendParameter))) continue;
                        foreach (var value in children) point[value.directBlendParameter] = 0;
                        point[child.directBlendParameter] = 1;
                    }
                    else
                    {
                        if (!selectable(tree.blendParameter)) continue;
                        point[tree.blendParameter] = tree.blendType == BlendTreeType.Simple1D ? child.threshold : child.position.x;
                        if (tree.blendType != BlendTreeType.Simple1D)
                        {
                            if (!selectable(tree.blendParameterY)) continue;
                            point[tree.blendParameterY] = child.position.y;
                        }
                    }
                    if (point.Values.Any(value => !Finite(value))) throw new InvalidOperationException("FXのBlendTreeの設定値が不正です。");
                    if (!(child.motion is BlendTree nested)) { yield return point; continue; }
                    var innerPoints = Points(nested, selectable, stack, visit).ToArray();
                    // An inner tree driven by a reserved runtime signal stays
                    // at the explicit fixed export environment. Its enclosing
                    // user-controlled knot remains a valid composed selection.
                    if (innerPoints.Length == 0) { yield return point; continue; }
                    foreach (var inner in innerPoints)
                    {
                        // One control may drive several tree levels. An inner
                        // knot is still an authored input value when the outer
                        // tree clamps or blends at that value; sample the full
                        // native tree rather than imposing parent-knot equality.
                        var combined = new Dictionary<string, float>(point, StringComparer.Ordinal);
                        foreach (var pair in inner) combined[pair.Key] = pair.Value;
                        yield return combined;
                    }
                }
            }
            finally { stack.Remove(tree); }
        }

        static bool HasMorph(Motion motion, IDictionary<AnimationClip, AnimationClip> replacements, Func<string, bool> excludedPath,
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
                    return AnimationUtility.GetCurveBindings(clip).Any(binding => excludedPath?.Invoke(binding.path) != true &&
                        binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal));
                }
                return motion is BlendTree tree && tree.children.Any(child => HasMorph(child.motion, replacements, excludedPath, stack, visit));
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
            if (first.Error != null || first.Animation.Count != 0 || first.Values.Count != second.Values.Count ||
                first.Unevaluated.Count != second.Unevaluated.Count) return false;
            bool Same(IList<VrChatExpressionMenu.MorphValue> a, IList<VrChatExpressionMenu.MorphValue> b) => a.All(value =>
                b.Any(other => value.Path == other.Path && value.Shape == other.Shape && Mathf.Abs(value.Weight - other.Weight) < .001f));
            return Same(first.Values, second.Values) && Same(first.Unevaluated, second.Unevaluated);
        }
    }
}
