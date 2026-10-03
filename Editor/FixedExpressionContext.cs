using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Animations;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // External producers are absent from the export preview. Define their initial
    // input explicitly, while allowing the authored menu and drivers to run once.
    internal sealed class FixedExpressionContext
    {
        internal readonly Dictionary<string, float> Values = new Dictionary<string, float>(StringComparer.Ordinal);
        internal readonly HashSet<string> UsedParameters = new HashSet<string>(StringComparer.Ordinal);

        private static readonly Dictionary<string, float> Normal = new Dictionary<string, float>(StringComparer.Ordinal)
        {
            ["IsLocal"] = 1, ["IsAnimatorEnabled"] = 1, ["Grounded"] = 1, ["Upright"] = 1,
            ["AvatarVersion"] = 3, ["VRMode"] = 1, ["ScaleFactor"] = 1, ["ScaleFactorInverse"] = 1,
            ["InStation"] = 0, ["Seated"] = 0, ["AFK"] = 0, ["Voice"] = 0, ["Viseme"] = 0,
            ["GestureLeft"] = 0, ["GestureRight"] = 0, ["GestureLeftWeight"] = 0, ["GestureRightWeight"] = 0,
            ["AngularY"] = 0, ["VelocityX"] = 0, ["VelocityY"] = 0, ["VelocityZ"] = 0, ["VelocityMagnitude"] = 0,
            ["MuteSelf"] = 0, ["Earmuffs"] = 0, ["IsOnFriendsList"] = 0, ["ScaleModified"] = 0, ["PreviewMode"] = 0
        };

        internal static FixedExpressionContext Create(RuntimeAnimatorController runtime, IDictionary<string, float> defaults,
            VrChatExpressionMenu.Source source = null)
        {
            var result = new FixedExpressionContext();
            foreach (var parameter in ExpressionDependencies.Controller(runtime).parameters)
            {
                var builtIn = VrChatParameterDriver.BuiltIn.Contains(parameter.name);
                if ((!builtIn && source?.ExternalParameters.Contains(parameter.name) != true) ||
                    parameter.type == AnimatorControllerParameterType.Trigger) continue;
                var value = parameter.type == AnimatorControllerParameterType.Bool ? (parameter.defaultBool ? 1f : 0f) :
                    parameter.type == AnimatorControllerParameterType.Int ? parameter.defaultInt : parameter.defaultFloat;
                if (source != null && source.Defaults.TryGetValue(parameter.name, out var supplied)) value = supplied;
                if (defaults != null && defaults.TryGetValue(parameter.name, out supplied)) value = supplied;
                if (Normal.TryGetValue(parameter.name, out var normal)) value = normal;
                if (parameter.name == "TrackingType" &&
                    (defaults == null || !defaults.ContainsKey(parameter.name)) &&
                    (source == null || !source.Defaults.ContainsKey(parameter.name))) value = 6;
                if (float.IsNaN(value) || float.IsInfinity(value))
                    throw new InvalidOperationException("表情を書き出す外部入力の初期値が不正です: " + parameter.name);
                if (parameter.type == AnimatorControllerParameterType.Bool) value = value == 0 ? 0 : 1;
                if (parameter.type == AnimatorControllerParameterType.Int) value = Mathf.RoundToInt(value);
                result.Values[parameter.name] = value;
            }
            return result;
        }

        // Over-approximate every possible path; only a proven false AND condition
        // removes an edge. Invalid/shared graph topology returns false so callers
        // retain their conservative command check instead of accepting a sample.
        internal static bool TryReachableFxCommands(AnimatorStateMachine machine, IDictionary<string, float> fixedValues,
            AnimatorControllerParameter[] parameters, out HashSet<StateMachineBehaviour> commands)
        {
            commands = new HashSet<StateMachineBehaviour>();
            if (machine == null || fixedValues == null || parameters == null) return false;
            var reachedCommands = commands;
            var parents = new Dictionary<AnimatorStateMachine, AnimatorStateMachine>();
            var owners = new Dictionary<AnimatorState, AnimatorStateMachine>();
            var types = new Dictionary<string, AnimatorControllerParameterType>(StringComparer.Ordinal);
            foreach (var parameter in parameters)
            {
                if (parameter == null || string.IsNullOrEmpty(parameter.name) || types.ContainsKey(parameter.name)) return false;
                types.Add(parameter.name, parameter.type);
            }
            bool Index(AnimatorStateMachine current, AnimatorStateMachine parent)
            {
                if (current == null || parents.ContainsKey(current)) return false;
                parents.Add(current, parent);
                foreach (var child in current.states)
                {
                    if (child.state == null || owners.ContainsKey(child.state)) return false;
                    owners.Add(child.state, current);
                }
                foreach (var child in current.stateMachines) if (!Index(child.stateMachine, current)) return false;
                return true;
            }
            if (!Index(machine, null)) return false;

            bool Valid(AnimatorTransitionBase transition)
            {
                if (transition == null) return false;
                if (transition.isExit) return transition.destinationState == null && transition.destinationStateMachine == null;
                if (transition.destinationState != null)
                    return transition.destinationStateMachine == null && owners.ContainsKey(transition.destinationState);
                return transition.destinationStateMachine != null && parents.ContainsKey(transition.destinationStateMachine);
            }
            foreach (var current in parents.Keys)
            {
                if (current.defaultState != null && (!owners.TryGetValue(current.defaultState, out var owner) || owner != current)) return false;
                if (current.entryTransitions.Any(t => !Valid(t)) || current.anyStateTransitions.Any(t => !Valid(t))) return false;
                foreach (var child in current.states) if (child.state.transitions.Any(t => !Valid(t))) return false;
                foreach (var child in current.stateMachines)
                    if (current.GetStateMachineTransitions(child.stateMachine).Any(t => !Valid(t))) return false;
            }

            bool? Condition(AnimatorCondition condition)
            {
                if (string.IsNullOrEmpty(condition.parameter) || !fixedValues.TryGetValue(condition.parameter, out var value) ||
                    !types.TryGetValue(condition.parameter, out var type) || float.IsNaN(value) || float.IsInfinity(value)) return null;
                if (type == AnimatorControllerParameterType.Bool)
                {
                    if (condition.mode == AnimatorConditionMode.If) return value != 0;
                    if (condition.mode == AnimatorConditionMode.IfNot) return value == 0;
                    return null;
                }
                if (type == AnimatorControllerParameterType.Trigger || float.IsNaN(condition.threshold) || float.IsInfinity(condition.threshold)) return null;
                // Fractional Int thresholds have no portable authored semantics.
                if (type == AnimatorControllerParameterType.Int && (condition.threshold != Math.Truncate(condition.threshold) || value != Math.Truncate(value))) return null;
                switch (condition.mode)
                {
                    case AnimatorConditionMode.Equals: return type == AnimatorControllerParameterType.Int ? value == condition.threshold : (bool?)null;
                    case AnimatorConditionMode.NotEqual: return type == AnimatorControllerParameterType.Int ? value != condition.threshold : (bool?)null;
                    case AnimatorConditionMode.Greater: return value > condition.threshold;
                    case AnimatorConditionMode.Less: return value < condition.threshold;
                    default: return null;
                }
            }
            IEnumerable<AnimatorTransitionBase> Enabled(IEnumerable<AnimatorTransitionBase> transitions)
            {
                var siblings = transitions.ToArray();
                var solo = siblings.Any(t => t.solo);
                return siblings.Where(t => !t.mute && (!solo || t.solo));
            }
            bool CanTake(AnimatorTransitionBase transition) => !transition.conditions.Any(c => Condition(c) == false);
            bool AlwaysTakes(AnimatorTransitionBase transition) => transition.conditions.All(c => Condition(c) == true);

            var active = new HashSet<AnimatorStateMachine>();
            var entered = new HashSet<AnimatorStateMachine>();
            var exited = new HashSet<AnimatorStateMachine>();
            var visited = new HashSet<AnimatorState>();
            var pending = new Queue<AnimatorState>();
            var valid = true;
            void Activate(AnimatorStateMachine current)
            {
                if (!active.Add(current)) return;
                var parent = parents[current];
                if (parent != null) Activate(parent);
                reachedCommands.UnionWith(current.behaviours);
                foreach (var transition in Enabled(current.anyStateTransitions))
                    if (CanTake(transition)) Destination(transition, current);
            }
            void Enter(AnimatorStateMachine current)
            {
                if (!entered.Add(current)) return;
                Activate(current);
                var fallsThrough = true;
                foreach (var transition in Enabled(current.entryTransitions))
                {
                    if (!CanTake(transition)) continue;
                    Destination(transition, current);
                    if (AlwaysTakes(transition)) { fallsThrough = false; break; }
                }
                if (fallsThrough)
                {
                    if (current.defaultState == null) valid = false;
                    else pending.Enqueue(current.defaultState);
                }
            }
            void Exit(AnimatorStateMachine current)
            {
                if (!exited.Add(current)) return;
                var parent = parents[current];
                if (parent == null) return;
                foreach (var transition in Enabled(parent.GetStateMachineTransitions(current)))
                    if (CanTake(transition)) Destination(transition, parent);
            }
            void Destination(AnimatorTransitionBase transition, AnimatorStateMachine current)
            {
                if (transition.isExit) Exit(current);
                else if (transition.destinationState != null) pending.Enqueue(transition.destinationState);
                else Enter(transition.destinationStateMachine);
            }

            Enter(machine);
            while (valid && pending.Count > 0)
            {
                var state = pending.Dequeue();
                if (!visited.Add(state)) continue;
                var owner = owners[state];
                Activate(owner);
                reachedCommands.UnionWith(state.behaviours);
                foreach (var transition in Enabled(state.transitions))
                    if (CanTake(transition)) Destination(transition, owner);
            }
            return valid;
        }
    }
}
