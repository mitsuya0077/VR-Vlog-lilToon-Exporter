using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter
{
    internal sealed class ExpressionEvaluationSession : IDisposable
    {
        private static readonly Dictionary<int, ExpressionEvaluationSession> Sessions = new Dictionary<int, ExpressionEvaluationSession>();
        private static int nextId;
        private readonly int id;
        private readonly List<Object> owned = new List<Object>();
        private readonly List<VrChatParameterDriver.Program> programs = new List<VrChatParameterDriver.Program>();
        private readonly Dictionary<string, AnimatorControllerParameterType> types;
        private readonly ISet<string> expressionParameters;
        private readonly ExpressionDependencies dependencies;
        private readonly bool defaultLocal;
        private InvalidOperationException failure;
        private int entries;

        internal Animator Animator;
        internal AnimatorController Controller { get; private set; }

        internal ExpressionEvaluationSession(RuntimeAnimatorController runtime, ExpressionDependencies dependencies,
            ISet<string> expressionParameters, bool defaultLocal = true)
        {
            this.dependencies = dependencies;
            this.expressionParameters = expressionParameters ?? new HashSet<string>();
            this.defaultLocal = defaultLocal;
            var original = ExpressionDependencies.Controller(runtime);
            types = original.parameters.ToDictionary(p => p.name, p => p.type, StringComparer.Ordinal);
            id = ++nextId;
            Sessions.Add(id, this);
            try { Controller = CopyController(original, ExpressionDependencies.Overrides(runtime)); }
            catch { Dispose(); throw; }
        }

        private AnimatorController CopyController(AnimatorController original, IDictionary<AnimationClip, AnimationClip> replacements)
        {
            var copies = new Dictionary<Object, Object>();
            Object Own(Object value) { value.hideFlags = HideFlags.HideAndDontSave; owned.Add(value); return value; }
            // AnimatorStateMachine owns native strong references. Instantiate or
            // generic serialized-object copying can duplicate these incorrectly.
            // Rebuild through the supported Animator graph APIs instead.
            var root = (AnimatorController)Own(new AnimatorController { name = original.name });
            root.parameters = original.parameters.Select(p => new AnimatorControllerParameter
            {
                name = p.name, type = p.type, defaultBool = p.defaultBool, defaultFloat = p.defaultFloat, defaultInt = p.defaultInt
            }).ToArray();
            StateMachineBehaviour[] Behaviours(IEnumerable<StateMachineBehaviour> values)
            {
                var result = new List<StateMachineBehaviour>();
                foreach (var behaviour in values)
                {
                    if (VrChatParameterDriver.IsTracking(behaviour)) continue;
                    if (!dependencies.Drivers.TryGetValue(behaviour, out var program))
                        throw new InvalidOperationException("評価用Controllerに未解決のState Behaviourがあります。");
                    var adapter = (ExpressionDriverBehaviour)Own(ScriptableObject.CreateInstance<ExpressionDriverBehaviour>());
                    adapter.Session = id; adapter.Program = programs.Count;
                    programs.Add(program); result.Add(adapter);
                }
                return result.ToArray();
            }
            Motion Motion(Motion value)
            {
                if (value == null) return null;
                if (value is AnimationClip clip) return replacements.TryGetValue(clip, out var replacement) ? replacement : clip;
                if (copies.TryGetValue(value, out var existing)) return (Motion)existing;
                if (!(value is BlendTree originalTree)) throw new InvalidOperationException("未対応のAnimator Motionです。");
                var tree = (BlendTree)Own(new BlendTree { name = value.name }); copies.Add(value, tree);
                tree.blendType = originalTree.blendType;
                tree.blendParameter = originalTree.blendParameter; tree.blendParameterY = originalTree.blendParameterY;
                tree.useAutomaticThresholds = originalTree.useAutomaticThresholds;
                tree.minThreshold = originalTree.minThreshold; tree.maxThreshold = originalTree.maxThreshold;
                var children = originalTree.children;
                for (var i = 0; i < children.Length; i++) children[i].motion = Motion(children[i].motion);
                tree.children = children;
                // Direct BlendTree normalization is serialized but has no public
                // setter in Unity 2022. Copy only this scalar, not graph pointers.
                using (var from = new SerializedObject(originalTree))
                using (var to = new SerializedObject(tree))
                {
                    var property = from.FindProperty("m_NormalizedBlendValues");
                    if (property != null) { to.FindProperty("m_NormalizedBlendValues").boolValue = property.boolValue; to.ApplyModifiedPropertiesWithoutUndo(); }
                }
                return tree;
            }
            AnimatorTransitionBase Transition(AnimatorTransitionBase value)
            {
                if (copies.TryGetValue(value, out var existing)) return (AnimatorTransitionBase)existing;
                AnimatorTransitionBase copy;
                if (value is AnimatorStateTransition originalTransition)
                    copy = new AnimatorStateTransition
                    {
                        duration = originalTransition.duration, offset = originalTransition.offset,
                        interruptionSource = originalTransition.interruptionSource, orderedInterruption = originalTransition.orderedInterruption,
                        exitTime = originalTransition.exitTime, hasExitTime = originalTransition.hasExitTime,
                        hasFixedDuration = originalTransition.hasFixedDuration, canTransitionToSelf = originalTransition.canTransitionToSelf
                    };
                else copy = new AnimatorTransition();
                Own(copy); copies.Add(value, copy);
                copy.name = value.name; copy.mute = value.mute; copy.solo = value.solo; copy.conditions = value.conditions;
                copy.destinationState = State(value.destinationState);
                copy.destinationStateMachine = Machine(value.destinationStateMachine);
                copy.isExit = value.isExit;
                return copy;
            }
            AnimatorState State(AnimatorState value)
            {
                if (value == null) return null;
                if (copies.TryGetValue(value, out var existing)) return (AnimatorState)existing;
                var state = (AnimatorState)Own(new AnimatorState { name = value.name }); copies.Add(value, state);
                state.speed = value.speed; state.cycleOffset = value.cycleOffset; state.mirror = value.mirror;
                state.iKOnFeet = value.iKOnFeet; state.writeDefaultValues = value.writeDefaultValues; state.tag = value.tag;
                state.speedParameter = value.speedParameter; state.speedParameterActive = value.speedParameterActive;
                state.timeParameter = value.timeParameter; state.timeParameterActive = value.timeParameterActive;
                state.mirrorParameter = value.mirrorParameter; state.mirrorParameterActive = value.mirrorParameterActive;
                state.cycleOffsetParameter = value.cycleOffsetParameter; state.cycleOffsetParameterActive = value.cycleOffsetParameterActive;
                state.motion = Motion(value.motion); state.behaviours = Behaviours(value.behaviours);
                state.transitions = value.transitions.Select(t => (AnimatorStateTransition)Transition(t)).ToArray();
                return state;
            }
            AnimatorStateMachine Machine(AnimatorStateMachine value)
            {
                if (value == null) return null;
                if (copies.TryGetValue(value, out var existing)) return (AnimatorStateMachine)existing;
                var machine = (AnimatorStateMachine)Own(new AnimatorStateMachine { name = value.name }); copies.Add(value, machine);
                machine.anyStatePosition = value.anyStatePosition; machine.entryPosition = value.entryPosition;
                machine.exitPosition = value.exitPosition; machine.parentStateMachinePosition = value.parentStateMachinePosition;
                machine.states = value.states.Select(s => new ChildAnimatorState { state = State(s.state), position = s.position }).ToArray();
                machine.stateMachines = value.stateMachines.Select(s => new ChildAnimatorStateMachine { stateMachine = Machine(s.stateMachine), position = s.position }).ToArray();
                machine.defaultState = State(value.defaultState); machine.behaviours = Behaviours(value.behaviours);
                machine.anyStateTransitions = value.anyStateTransitions.Select(t => (AnimatorStateTransition)Transition(t)).ToArray();
                machine.entryTransitions = value.entryTransitions.Select(t => (AnimatorTransition)Transition(t)).ToArray();
                foreach (var child in value.stateMachines)
                    machine.SetStateMachineTransitions(Machine(child.stateMachine), value.GetStateMachineTransitions(child.stateMachine).Select(t => (AnimatorTransition)Transition(t)).ToArray());
                return machine;
            }
            var layers = original.layers;
            for (var i = 0; i < layers.Length; i++)
            {
                if (dependencies.Layers.Contains(i)) layers[i].stateMachine = Machine(layers[i].stateMachine);
                else
                {
                    var empty = (AnimatorStateMachine)Own(new AnimatorStateMachine { name = layers[i].name });
                    var idle = empty.AddState("Unrelated layer"); Own(idle); idle.writeDefaultValues = false;
                    empty.defaultState = idle;
                    layers[i].stateMachine = empty;
                    layers[i].syncedLayerIndex = -1;
                    layers[i].defaultWeight = 0;
                    layers[i].iKPass = false;
                }
            }
            root.layers = layers;
            return root;
        }

        internal static void Enter(int sessionId, int programIndex, Animator animator, AnimatorControllerPlayable playable)
        {
            if (!Sessions.TryGetValue(sessionId, out var session) || session.Animator != animator || session.failure != null) return;
            try
            {
                if (++session.entries > 4096) throw new InvalidOperationException("Parameter Driverの進入回数が上限を超えました。循環する表情は変換できません。");
                var program = session.programs[programIndex];
                double Read(string name)
                {
                    switch (session.types[name])
                    {
                        case AnimatorControllerParameterType.Bool: return playable.GetBool(name) ? 1 : 0;
                        case AnimatorControllerParameterType.Int: return playable.GetInteger(name);
                        case AnimatorControllerParameterType.Float: return playable.GetFloat(name);
                        default: throw new InvalidOperationException("Triggerは固定表情に変換できません。");
                    }
                }
                void Write(string name, double value)
                {
                    switch (session.types[name])
                    {
                        case AnimatorControllerParameterType.Bool: playable.SetBool(name, value != 0); break;
                        case AnimatorControllerParameterType.Int: playable.SetInteger(name, (int)value); break;
                        case AnimatorControllerParameterType.Float: playable.SetFloat(name, (float)value); break;
                    }
                }
                var local = session.types.ContainsKey("IsLocal") ? Read("IsLocal") != 0 : session.defaultLocal;
                VrChatParameterDriver.Execute(program, session.types, session.expressionParameters, session.dependencies.Parameters, local, Read, Write);
            }
            catch (Exception error)
            {
                // Unity can log/swallow exceptions from native animation
                // callbacks. Surface failures synchronously after Evaluate.
                session.failure = error as InvalidOperationException ?? new InvalidOperationException("Parameter Driverの評価に失敗しました: " + error.Message, error);
            }
        }

        internal void Check() { if (failure != null) throw failure; }

        public void Dispose()
        {
            Sessions.Remove(id);
            // All objects are owned copies; source clips/masks are read-only.
            for (var i = owned.Count - 1; i >= 0; i--) if (owned[i] != null) Object.DestroyImmediate(owned[i]);
            owned.Clear();
        }
    }
}
