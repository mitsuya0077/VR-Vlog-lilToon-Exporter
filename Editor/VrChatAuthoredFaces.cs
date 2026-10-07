using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // Hardware/contact routes are not menu controls in a VRM. Their reachable
    // authored facial clips are still useful held expressions. Keep the clip's
    // state provenance and native permanent contributions, and require geometry
    // to prove facial ownership before registering these additional candidates.
    internal static class VrChatAuthoredFaces
    {
        internal static void Add(GameObject avatar, VrChatExpressionMenu.Source source,
            Func<string, bool> excludedPath = null, VrChatMenuImportPolicy policy = null)
        {
            if (source.Controller == null || policy?.SkipAll == true || policy?.ExcludedBranches.Count > 0) return;
            if (!source.NeutralInputInventoryComplete) NeutralInputProof.Read(avatar, source);
            if (!source.NeutralInputInventoryComplete) return;
            var scope = FacialProjectionScope.For(avatar, source);
            if (scope == null) return;
            var controller = ExpressionDependencies.Controller(source.Controller);
            if (source.ExternalParameters.Count == 0 && !controller.parameters.Any(parameter => parameter.name == "AFK")) return;
            ExpressionDependencies.Layer[] dependencies;
            Target[][] reachable;
            try
            {
                // Validate and buffer the graph before publishing recovery
                // entries. Invalid topology must not leave an arbitrary prefix.
                reachable = Enumerable.Range(0, controller.layers.Length).Select(layer => Reachable(controller, layer).ToArray()).ToArray();
                dependencies = ExpressionDependencies.ValidateProbeBehaviours(source.Controller, excludedPath);
            }
            catch (InvalidOperationException error)
            {
                source.Messages.Add("外部入力の表情クリップを安全に確定できないため追加していません: " + error.Message);
                return;
            }
            var replacements = ExpressionDependencies.Overrides(source.Controller);
            for (var layer = 0; layer < controller.layers.Length; layer++)
            {
                var inputs = new HashSet<string>(dependencies[layer].Reads, StringComparer.Ordinal);
                bool changed;
                do
                {
                    changed = false;
                    foreach (var relay in dependencies.Where(item => item.Writes.Overlaps(inputs)))
                        foreach (var input in relay.Reads) changed |= inputs.Add(input);
                } while (changed);
                if (!inputs.Overlaps(source.ExternalParameters) && !inputs.Contains("AFK")) continue;
                foreach (var target in reachable[layer])
                {
                    var motion = controller.GetStateEffectiveMotion(target.State, layer) ?? target.State.motion;
                    if (!(motion is AnimationClip clip)) continue;
                    if (replacements.TryGetValue(clip, out var replacement)) clip = replacement;
                    if (target.State.timeParameterActive && VrChatGestureExpressions.HasChangingMorph(clip, excludedPath)) continue;
                    if (!AnimationUtility.GetCurveBindings(clip).Any(binding => scope.Morphs.Contains(binding) &&
                        excludedPath?.Invoke(binding.path) != true)) continue;
                    var entry = new VrChatExpressionMenu.Entry {
                        Id = "authored-fx/" + layer + "/" + target.Path,
                        Name = "FXクリップ / " + clip.name,
                        UsesFacialProjection = true
                    };
                    try
                    {
                        VrChatGestureExpressions.ReadClip(avatar, clip, entry, excludedPath, source);
                        entry.Values.RemoveAll(value => !scope.Morphs.Contains(Binding(value.Path, value.Shape)));
                        entry.Animation.RemoveAll(value => !scope.Morphs.Contains(Binding(value.Path, value.Shape)));
                        if (!entry.Values.Any(value => value.Weight != 0) && entry.Animation.Count == 0) continue;
                        if (entry.Animation.Count == 0) { entry.Duration = 0; entry.Loop = false; }
                        VrChatExpressionSampler.ApplyPermanentOverrides(avatar, source.Controller, entry, layer,
                            target.State.writeDefaultValues, excludedPath, metadata: source, sourceState: target.State);
                        if (source.Entries.Any(previous => Equivalent(previous, entry))) continue;
                        entry.Messages.Add("接触・外部入力で使う元の表情クリップを、顔の形状として保存しました。外部入力による自動切り替えは再現しません。");
                    }
                    catch (InvalidOperationException error) { entry.Error = error.Message; entry.Values.Clear(); entry.Animation.Clear(); }
                    source.Entries.Add(entry);
                    if (source.Entries.Count > 512) throw new InvalidOperationException("表情候補が512個を超えています。FXの表情登録を整理してください。");
                }
            }
        }

        static EditorCurveBinding Binding(string path, string shape) =>
            EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + shape);

        static bool Equivalent(VrChatExpressionMenu.Entry first, VrChatExpressionMenu.Entry second)
        {
            if (first.Error != null || first.Values.Count != second.Values.Count || first.Animation.Count != second.Animation.Count ||
                first.Duration != second.Duration || first.Loop != second.Loop) return false;
            var values = first.Values.ToDictionary(value => (value.Path, value.Shape), value => value.Weight);
            if (second.Values.Any(value => !values.TryGetValue((value.Path, value.Shape), out var weight) || weight != value.Weight)) return false;
            return second.Animation.All(value => first.Animation.Any(previous => previous.Path == value.Path && previous.Shape == value.Shape &&
                VrChatExpressionBaker.SameCurve(previous.Curve, value.Curve)));
        }

        sealed class Target { internal AnimatorState State; internal string Path; }

        static IEnumerable<Target> Reachable(AnimatorController controller, int layer)
        {
            var layers = controller.layers; var origin = layer; var chain = new HashSet<int>();
            var parameters = new Dictionary<string, AnimatorControllerParameterType>(StringComparer.Ordinal);
            foreach (var parameter in controller.parameters)
            {
                if (parameter == null || string.IsNullOrEmpty(parameter.name) || parameters.ContainsKey(parameter.name))
                    throw new InvalidOperationException("FXの入力パラメーター宣言が不正・重複しています。");
                parameters.Add(parameter.name, parameter.type);
            }
            bool Possible(AnimatorTransitionBase transition)
            {
                var impossible = false;
                foreach (var group in transition.conditions.GroupBy(condition => condition.parameter))
                {
                    if (string.IsNullOrEmpty(group.Key) || !parameters.TryGetValue(group.Key, out var type))
                        throw new InvalidOperationException("FXの条件に宣言されていない入力があります: " + group.Key);
                    var conditions = group.ToArray();
                    if (conditions.Any(condition => float.IsNaN(condition.threshold) || float.IsInfinity(condition.threshold)))
                        throw new InvalidOperationException("FXの条件値が有限ではありません: " + group.Key);
                    if (type == AnimatorControllerParameterType.Bool || type == AnimatorControllerParameterType.Trigger)
                    {
                        if (conditions.Any(condition => condition.mode != AnimatorConditionMode.If && condition.mode != AnimatorConditionMode.IfNot))
                            throw new InvalidOperationException("FXの条件の入力型と比較方法が不正です: " + group.Key);
                        if (conditions.Any(condition => condition.mode == AnimatorConditionMode.If) &&
                            conditions.Any(condition => condition.mode == AnimatorConditionMode.IfNot)) impossible = true;
                    }
                    else if (type == AnimatorControllerParameterType.Int)
                    {
                        double minimum = int.MinValue, maximum = int.MaxValue;
                        var excluded = new HashSet<double>();
                        foreach (var condition in conditions)
                        {
                            var threshold = (double)condition.threshold;
                            switch (condition.mode)
                            {
                                case AnimatorConditionMode.Equals:
                                    if (threshold != Math.Truncate(threshold)) { impossible = true; break; }
                                    minimum = Math.Max(minimum, threshold); maximum = Math.Min(maximum, threshold); break;
                                case AnimatorConditionMode.NotEqual:
                                    if (threshold == Math.Truncate(threshold)) excluded.Add(threshold); break;
                                case AnimatorConditionMode.Greater: minimum = Math.Max(minimum, Math.Floor(threshold) + 1); break;
                                case AnimatorConditionMode.Less: maximum = Math.Min(maximum, Math.Ceiling(threshold) - 1); break;
                                default: throw new InvalidOperationException("FXの条件の入力型と比較方法が不正です: " + group.Key);
                            }
                        }
                        if (minimum > maximum || excluded.Count(value => value >= minimum && value <= maximum) >= maximum - minimum + 1) impossible = true;
                    }
                    else if (type == AnimatorControllerParameterType.Float)
                    {
                        var minimum = float.NegativeInfinity; var maximum = float.PositiveInfinity;
                        foreach (var condition in conditions)
                            if (condition.mode == AnimatorConditionMode.Greater) minimum = Math.Max(minimum, condition.threshold);
                            else if (condition.mode == AnimatorConditionMode.Less) maximum = Math.Min(maximum, condition.threshold);
                            else throw new InvalidOperationException("FXの条件の入力型と比較方法が不正です: " + group.Key);
                        if (minimum >= maximum) impossible = true;
                    }
                    else throw new InvalidOperationException("FXの条件の入力型が未対応です: " + group.Key);
                }
                return !impossible;
            }
            while (layers[origin].syncedLayerIndex >= 0)
            {
                if (!chain.Add(origin)) throw new InvalidOperationException("FXの同期レイヤー参照が不正です。");
                origin = layers[origin].syncedLayerIndex;
                if (origin < 0 || origin >= layers.Length) throw new InvalidOperationException("FXの同期レイヤー参照が不正です。");
            }
            var owners = new Dictionary<AnimatorState, AnimatorStateMachine>();
            var parents = new Dictionary<AnimatorStateMachine, AnimatorStateMachine>();
            var paths = new Dictionary<AnimatorState, string>();
            var nodes = 0;
            void Index(AnimatorStateMachine machine, AnimatorStateMachine parent, string path, int depth)
            {
                if (machine == null || parents.ContainsKey(machine) || depth > 16 || ++nodes > 8192)
                    throw new InvalidOperationException("FXの状態階層が不正、または深すぎます。");
                parents.Add(machine, parent);
                foreach (var child in machine.states)
                {
                    if (child.state == null || owners.ContainsKey(child.state) || ++nodes > 8192)
                        throw new InvalidOperationException("FXの状態参照が不正です。");
                    var statePath = path + "." + child.state.name;
                    if (paths.Values.Contains(statePath)) throw new InvalidOperationException("FXの状態パスが重複しています: " + statePath);
                    owners.Add(child.state, machine); paths.Add(child.state, statePath);
                }
                foreach (var child in machine.stateMachines)
                {
                    if (child.stateMachine == null) throw new InvalidOperationException("FXの状態階層が不正です。");
                    Index(child.stateMachine, machine, path + "." + child.stateMachine.name, depth + 1);
                }
            }
            Index(layers[origin].stateMachine, null, layers[layer].name, 0);
            var machines = new HashSet<AnimatorStateMachine>(); var states = new HashSet<AnimatorState>();
            var entered = new HashSet<AnimatorStateMachine>(); var exited = new HashSet<AnimatorStateMachine>();
            var pending = new Queue<AnimatorState>();
            IEnumerable<AnimatorTransitionBase> Enabled(IEnumerable<AnimatorTransitionBase> transitions)
            {
                var items = transitions.ToArray();
                if (items.Any(item => item == null)) throw new InvalidOperationException("FXの遷移参照が不正です。");
                var solo = items.Any(item => item.solo);
                return items.Where(item => !item.mute && (!solo || item.solo) && Possible(item));
            }
            void Activate(AnimatorStateMachine machine)
            {
                if (!machines.Add(machine)) return;
                if (parents[machine] != null) Activate(parents[machine]);
                foreach (AnimatorStateTransition transition in Enabled(machine.anyStateTransitions))
                {
                    Destination(transition, machine);
                    // A self-disabled AnyState edge can become ineligible in
                    // its destination, exposing later edges on a future frame.
                    if (!transition.hasExitTime && transition.canTransitionToSelf && transition.conditions.Length == 0) break;
                }
            }
            void Enter(AnimatorStateMachine machine)
            {
                if (!parents.ContainsKey(machine)) throw new InvalidOperationException("FXの遷移先が状態階層にありません。");
                if (!entered.Add(machine)) return;
                Activate(machine);
                foreach (var transition in Enabled(machine.entryTransitions))
                { Destination(transition, machine); if (transition.conditions.Length == 0) return; }
                if (machine.defaultState != null)
                {
                    if (!owners.TryGetValue(machine.defaultState, out var owner) || owner != machine)
                        throw new InvalidOperationException("FXの初期状態が状態階層にありません。");
                    pending.Enqueue(machine.defaultState);
                }
            }
            void Exit(AnimatorStateMachine machine)
            {
                var parent = parents[machine]; if (parent == null || !exited.Add(machine)) return;
                foreach (var transition in Enabled(parent.GetStateMachineTransitions(machine)))
                { Destination(transition, parent); if (transition.conditions.Length == 0) break; }
            }
            void Destination(AnimatorTransitionBase transition, AnimatorStateMachine machine)
            {
                if (transition.isExit) Exit(machine);
                else if (transition.destinationState != null)
                {
                    if (!owners.ContainsKey(transition.destinationState)) throw new InvalidOperationException("FXの遷移先が状態階層にありません。");
                    pending.Enqueue(transition.destinationState);
                }
                else if (transition.destinationStateMachine != null) Enter(transition.destinationStateMachine);
                else throw new InvalidOperationException("FXの遷移先がありません。");
            }
            Enter(layers[origin].stateMachine);
            while (pending.Count > 0)
            {
                var state = pending.Dequeue(); if (!states.Add(state)) continue;
                Activate(owners[state]);
                foreach (AnimatorStateTransition transition in Enabled(state.transitions))
                {
                    if (transition.destinationState == state && !transition.canTransitionToSelf) continue;
                    Destination(transition, owners[state]);
                    if (!transition.hasExitTime && transition.conditions.Length == 0) break;
                }
            }
            return states.OrderBy(state => paths[state], StringComparer.Ordinal)
                .Select(state => new Target { State = state, Path = paths[state] }).ToArray();
        }
    }
}
