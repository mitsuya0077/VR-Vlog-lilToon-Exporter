using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // A proof over the reachable state graph, not a bag of referenced clips or
    // a few sampled frames. Anything outside this bounded subset is reported.
    internal static class PoseMenuResolver
    {
        internal static object Member(object value, string name) => VrChatExpressionMenu.Member(value, name);
        internal static IEnumerable<object> Items(object value) => value is IEnumerable e ? e.Cast<object>() : Enumerable.Empty<object>();
        internal static List<PoseCandidate> Read(GameObject avatar)
        {
            var result = new List<PoseCandidate>();
            var descriptor = avatar.GetComponents<Component>().FirstOrDefault(c => c != null && c.GetType().FullName == "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            if (descriptor == null) return result;
            var menu = VrChatExpressionMenu.Read(avatar);
            menu.ExternalParameters.UnionWith(VrChatParameterDriver.BuiltIn);
            var layers = Items(Member(descriptor, "baseAnimationLayers")).Concat(Items(Member(descriptor, "specialAnimationLayers")))
                .Where(l => !(Member(l, "isDefault") is bool d && d) && Member(l, "animatorController") is RuntimeAnimatorController).ToArray();
            foreach (var entry in menu.Entries)
            {
                var split = entry.Name.LastIndexOf(" / ", StringComparison.Ordinal);
                var row = new PoseCandidate { Name = split < 0 ? entry.Name : entry.Name.Substring(split + 3),
                    Category = split < 0 ? "VRChat" : entry.Name.Substring(0, split), Source = "VRChat menu",
                    Conditions = JsonDom.Serialize(entry.Parameters.ToDictionary(p => p.Key, p => (object)p.Value)), Error = entry.Error };
                try
                {
                    if (row.Error != null) throw new InvalidOperationException(row.Error);
                    if (Member(descriptor, "customizeAnimationLayers") is bool custom && !custom)
                        throw new InvalidOperationException("カスタムPlayable Layerが無効です。");
                    var weights = new Dictionary<string, float> { ["Base"] = 1, ["Additive"] = 1, ["Gesture"] = 1, ["Action"] = 0, ["FX"] = 1,
                        ["Sitting"] = 0, ["TPose"] = 0, ["IKPose"] = 0 };
                    var resolved = new List<(string type, AnimatorControllerLayer layer, AnimatorState state, AnimationClip clip, int index, bool skipMuscles, AvatarMask outer)>();
                    var controlledWeights = new Dictionary<string, float>();
                    var selectedWeightTargets = new HashSet<string>();
                    var selectedAffectsBody = false;
                    foreach (var source in layers)
                    {
                        var type = Member(source, "type")?.ToString() ?? "";
                        var runtime = (RuntimeAnimatorController)Member(source, "animatorController");
                        var controller = ExpressionDependencies.Controller(runtime); var replacements = ExpressionDependencies.Overrides(runtime);
                        var outer = Member(source, "mask") as AvatarMask;
                        var skipMuscles = type == "FX" && controller.layers.Length > 0 && controller.layers[0].avatarMask == null;
                        for (var i = 0; i < controller.layers.Length; i++)
                        {
                            var layer = controller.layers[i];
                            var all = States(layer.stateMachine).ToArray();
                            AnimationClip Clip(AnimatorState state) => state.motion is AnimationClip clip && replacements.TryGetValue(clip, out var replacement) ? replacement : state.motion as AnimationClip;
                            var hasBody = all.Any(s => HasBody(Clip(s), skipMuscles)) || all.Any(s => s.motion is BlendTree);
                            var behaviours = all.SelectMany(s => s.behaviours).Concat(Behaviours(layer.stateMachine)).ToArray();
                            var controls = behaviours.Any(b => b == null || !Tracking(b));
                            if (!hasBody && !controls) continue;
                            if (layer.syncedLayerIndex >= 0) throw new InvalidOperationException("同期Animatorレイヤーは未対応です。");
                            foreach (var b in behaviours)
                                if (b == null || !Tracking(b) && b.GetType().Name != "VRCPlayableLayerControl")
                                    throw new InvalidOperationException("外部操作・パラメーター変更を伴うBehaviourは未対応です: " + (b == null ? "missing" : b.GetType().Name));
                            var state = Resolve(layer.stateMachine, entry.Parameters, menu.ExternalParameters);
                            if (state == null) continue;
                            if (Behaviours(layer.stateMachine).Any(b => !Tracking(b)))
                                throw new InvalidOperationException("StateMachine Behaviourによる重み変更は未対応です。");
                            if (all.Where(s => s != state).Any(s => s.behaviours.Any(b => !Tracking(b))))
                                throw new InvalidOperationException("開始／終了状態のBehaviourに依存する重みは未対応です。");
                            var selectedByMenu = Transitions(layer.stateMachine).Any(t => t.conditions.Any(c => entry.Parameters.ContainsKey(c.parameter)));
                            selectedAffectsBody |= hasBody && selectedByMenu;
                            foreach (var b in state.behaviours.Where(b => !Tracking(b)))
                            {
                                var target = Member(b, "layer")?.ToString();
                                var weight = Convert.ToSingle(Member(b, "goalWeight"));
                                var duration = Convert.ToSingle(Member(b, "blendDuration"));
                                if (duration != 0 || !PoseSampling.Finite(weight) || weight < 0 || weight > 1 || target == null || !weights.ContainsKey(target))
                                    throw new InvalidOperationException("時間依存のPlayable Layer重みは未対応です。");
                                if (controlledWeights.TryGetValue(target, out var previous) && previous != weight)
                                    throw new InvalidOperationException("複数レイヤーから重みが変更されるため順序を確定できません。");
                                controlledWeights[target] = weights[target] = weight;
                                if (selectedByMenu && weight > 0) selectedWeightTargets.Add(target);
                            }
                            if (hasBody) resolved.Add((type, layer, state, Clip(state), i, skipMuscles, outer));
                        }
                    }
                    foreach (var item in resolved.OrderBy(r => Array.IndexOf(new[] { "Base", "Additive", "Gesture", "Action", "FX", "Sitting", "TPose", "IKPose" }, r.type)))
                    {
                        if (!weights.TryGetValue(item.type, out var playableWeight)) throw new InvalidOperationException("不明なPlayable Layerです。");
                        var weight = item.index == 0 ? 1 : item.layer.defaultWeight;
                        if (weight == 0 || playableWeight == 0) continue;
                        if (!PoseSampling.Finite(weight) || weight < 0 || weight > 1) throw new InvalidOperationException("レイヤー重みが不正です。");
                        if (item.state.motion == null) continue;
                        if (item.clip == null) throw new InvalidOperationException("BlendTreeによる合成は未対応です。");
                        if (!HasBody(item.clip, item.skipMuscles)) continue;
                        selectedAffectsBody |= selectedWeightTargets.Contains(item.type);
                        if (item.state.writeDefaultValues && resolved.Count > 1)
                            throw new InvalidOperationException("複数レイヤーのWrite Defaultsによる暗黙の姿勢合成は未対応です。");
                        if (item.layer.blendingMode != AnimatorLayerBlendingMode.Override || item.type == "Additive")
                            throw new InvalidOperationException("加算レイヤーの姿勢は未対応です。");
                        if (item.state.mirror || item.state.mirrorParameterActive || item.state.timeParameterActive || item.state.speedParameterActive || item.state.cycleOffsetParameterActive || item.state.iKOnFeet)
                            throw new InvalidOperationException("ミラー・時刻パラメーター・IKに依存する状態は未対応です。");
                        if (PoseSampling.Moving(item.clip)) throw new InvalidOperationException("動くメニュークリップです。手動追加で採用時刻を指定できます。");
                        if (AnimationUtility.GetAnimationEvents(item.clip).Length != 0)
                            throw new InvalidOperationException("Animation Eventを伴うメニューです。");
                        row.Layers.Add(new PoseLayer { Clip = item.clip, Weight = weight, Mask = item.layer.avatarMask, SkipMuscles = item.skipMuscles,
                            OuterMask = item.outer, Group = item.type, GroupWeight = playableWeight, ClipIdentity = PoseSampling.GeneratedClipIdentity(item.clip) });
                    }
                    if (!selectedAffectsBody || row.Layers.Count == 0) throw new InvalidOperationException("この操作から適用するHumanoid静止姿勢を確定できません。");
                }
                catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is FormatException) { row.Error = e.Message; }
                row.Id = PoseSampling.Identity(row);
                result.Add(row);
            }
            return result;
        }

        internal static AnimatorState Resolve(AnimatorStateMachine machine, IDictionary<string, float> selected, ISet<string> external = null)
        {
            var states = States(machine).ToArray();
            var parents = new Dictionary<AnimatorState, List<AnimatorStateMachine>>();
            void Index(AnimatorStateMachine current, List<AnimatorStateMachine> ancestors)
            {
                if (ancestors.Contains(current)) throw new InvalidOperationException("Animator状態機械の参照が循環しています。");
                var chain = new List<AnimatorStateMachine>(ancestors) { current };
                if (current.entryTransitions.Length != 0) throw new InvalidOperationException("条件付きEntry遷移は未対応です。");
                foreach (var s in current.states) parents.Add(s.state, chain);
                foreach (var child in current.stateMachines)
                {
                    if (current.GetStateMachineTransitions(child.stateMachine).Length != 0)
                        throw new InvalidOperationException("サブStateMachine間の遷移は未対応です。");
                    Index(child.stateMachine, chain);
                }
            }
            Index(machine, new List<AnimatorStateMachine>());
            if (states.Length > 256) throw new InvalidOperationException("Animatorの状態数が上限を超えています。");
            var edges = new Dictionary<AnimatorState, AnimatorState>();
            foreach (var state in states)
            {
                AnimatorState destination = null;
                foreach (var transition in parents[state].SelectMany(m => m.anyStateTransitions).Concat(state.transitions).Where(t => !t.mute))
                {
                    // Check *all* conditions before evaluating: a currently false
                    // condition cannot hide an external or historical dependency.
                    foreach (var c in transition.conditions)
                        if (!selected.ContainsKey(c.parameter) || external?.Contains(c.parameter) == true)
                            throw new InvalidOperationException("他のメニュー・外部入力・操作履歴に依存します: " + c.parameter);
                    if (!transition.conditions.All(c => Condition(c, selected[c.parameter]))) continue;
                    if (transition.hasExitTime || transition.duration != 0 || transition.offset != 0 || transition.isExit || transition.destinationState == null || transition.solo)
                        throw new InvalidOperationException("開始・終了・時間付きまたは複雑な遷移は未対応です。");
                    if (transition.destinationState == state && !transition.canTransitionToSelf) continue;
                    if (destination != null && destination != transition.destinationState) throw new InvalidOperationException("複数の遷移先があり姿勢を確定できません。");
                    destination = transition.destinationState;
                }
                edges[state] = destination;
            }
            // Every possible previous state must converge to the same terminal.
            AnimatorState final = null;
            foreach (var start in states)
            {
                var current = start; var visited = new HashSet<AnimatorState>();
                while (edges.TryGetValue(current, out var next) && next != null)
                { if (!visited.Add(current)) throw new InvalidOperationException("循環遷移または再入場があるため静止姿勢を確定できません。"); current = next; }
                if (final != null && final != current) throw new InvalidOperationException("以前の操作で到達姿勢が変わります。");
                final = current;
            }
            if (machine.entryTransitions.Length != 0) throw new InvalidOperationException("条件付きEntry遷移は未対応です。");
            return final;
        }
        static bool Condition(AnimatorCondition c, float value)
        {
            switch (c.mode)
            {
                case AnimatorConditionMode.If: return value != 0;
                case AnimatorConditionMode.IfNot: return value == 0;
                case AnimatorConditionMode.Equals: return value == c.threshold;
                case AnimatorConditionMode.NotEqual: return value != c.threshold;
                case AnimatorConditionMode.Greater: return value > c.threshold;
                case AnimatorConditionMode.Less: return value < c.threshold;
                default: throw new InvalidOperationException("未対応のAnimator条件です。");
            }
        }
        static bool Tracking(StateMachineBehaviour b) => b != null && (b.GetType().Name == "VRCAnimatorTrackingControl" || b.GetType().Name == "VRCAnimatorLocomotionControl");
        internal static bool HasBody(AnimationClip clip, bool skipMuscles = false) => clip != null && AnimationUtility.GetCurveBindings(clip).Any(b =>
            !skipMuscles && b.type == typeof(Animator) && b.path == "" && PoseSampling.IsBodyMuscle(b.propertyName) || b.type == typeof(Transform));
        static IEnumerable<AnimatorStateMachine> Machines(AnimatorStateMachine root)
        {
            var pending = new Stack<AnimatorStateMachine>(); var visited = new HashSet<AnimatorStateMachine>(); pending.Push(root);
            while (pending.Count > 0)
            {
                var next = pending.Pop();
                if (next == null || !visited.Add(next) || visited.Count > 256) throw new InvalidOperationException("Animator状態機械の循環または件数上限です。");
                yield return next;
                foreach (var child in next.stateMachines) pending.Push(child.stateMachine);
            }
        }
        static IEnumerable<AnimatorState> States(AnimatorStateMachine m) => Machines(m).SelectMany(s => s.states.Select(c => c.state));
        static IEnumerable<StateMachineBehaviour> Behaviours(AnimatorStateMachine m) => Machines(m).SelectMany(s => s.behaviours);
        static IEnumerable<AnimatorStateTransition> Transitions(AnimatorStateMachine m) => Machines(m).SelectMany(s => s.anyStateTransitions).Concat(States(m).SelectMany(s => s.transitions));
    }
}
