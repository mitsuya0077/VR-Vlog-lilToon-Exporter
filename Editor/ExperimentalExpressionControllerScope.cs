using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter
{
    // Only the opt-in capture session uses this owned graph. The ordinary
    // exporter and the author's controller assets retain their strict policy.
    internal sealed class ExperimentalExpressionControllerScope : IDisposable
    {
        readonly List<Object> owned = new List<Object>();
        readonly Dictionary<Object, Object> copies = new Dictionary<Object, Object>();
        internal int OmittedAudioBehaviours { get; private set; }

        internal ExperimentalExpressionControllerScope(GameObject avatar, ICollection<string> warnings)
        {
            try
            {
                var descriptors = avatar.GetComponents<Component>().Where(component => component != null &&
                    component.GetType().FullName == "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor").ToArray();
                foreach (var descriptor in descriptors)
                {
                    using var data = new SerializedObject(descriptor);
                    var fields = data.GetIterator();
                    while (fields.Next(true))
                        if (fields.propertyType == SerializedPropertyType.ObjectReference && fields.objectReferenceValue is RuntimeAnimatorController runtime)
                            fields.objectReferenceValue = Copy(runtime, avatar);
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
                if (OmittedAudioBehaviours > 0)
                    warnings?.Add("表情収録用コピーではSDKの音声再生だけを省略しました（" + OmittedAudioBehaviours + "件）。音声はVRM表情へ保存しません。");
            }
            catch { Dispose(); throw; }
        }

        Object Copy(Object value, GameObject avatar)
        {
            if (copies.TryGetValue(value, out var existing)) return existing;
            var controller = CopyController(ExpressionDependencies.Controller((RuntimeAnimatorController)value),
                ExpressionDependencies.Overrides((RuntimeAnimatorController)value), avatar);
            copies.Add(value, controller);
            return controller;
        }

        AnimatorController CopyController(AnimatorController original, IDictionary<AnimationClip, AnimationClip> replacements, GameObject avatar)
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
            StateMachineBehaviour[] Behaviours(IEnumerable<StateMachineBehaviour> values) => Filter(values.ToArray(), avatar);
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
            for (var i = 0; i < layers.Length; i++) layers[i].stateMachine = Machine(layers[i].stateMachine);
            root.layers = layers;
            RemapSyncedOverrides(root, original, State, Motion, Behaviours);
            return root;
        }

        internal static void RemapSyncedOverrides(AnimatorController target, AnimatorController original,
            Func<AnimatorState, AnimatorState> state, Func<Motion, Motion> motion,
            Func<IEnumerable<StateMachineBehaviour>, StateMachineBehaviour[]> behaviours)
        {
            var authored = original.layers;
            var current = target.layers;
            target.layers = current.Select(layer => new AnimatorControllerLayer { name = layer.name,
                stateMachine = layer.stateMachine, avatarMask = layer.avatarMask, blendingMode = layer.blendingMode,
                defaultWeight = layer.defaultWeight, iKPass = layer.iKPass, syncedLayerIndex = layer.syncedLayerIndex,
                syncedLayerAffectsTiming = layer.syncedLayerAffectsTiming }).ToArray();
            for (var index = 0; index < authored.Length; index++)
            {
                if (authored[index].syncedLayerIndex < 0 || current[index].syncedLayerIndex < 0) continue;
                var source = index; var seen = new HashSet<int>();
                while (authored[source].syncedLayerIndex >= 0)
                {
                    if (!seen.Add(source)) throw new InvalidOperationException("表情の同期レイヤー参照が循環しています。");
                    source = authored[source].syncedLayerIndex;
                    if (source >= authored.Length) throw new InvalidOperationException("表情の同期レイヤー参照が不正です。");
                }
                void Visit(AnimatorStateMachine machine)
                {
                    foreach (var child in machine.states)
                    {
                        target.SetStateEffectiveMotion(state(child.state), motion(original.GetStateEffectiveMotion(child.state, index)), index);
                        target.SetStateEffectiveBehaviours(state(child.state), index, behaviours(original.GetStateEffectiveBehaviours(child.state, index) ?? Array.Empty<StateMachineBehaviour>()));
                    }
                    foreach (var child in machine.stateMachines) Visit(child.stateMachine);
                }
                Visit(authored[source].stateMachine);
            }
        }

        StateMachineBehaviour[] Filter(StateMachineBehaviour[] behaviours, GameObject avatar)
        {
            return behaviours.Where(behaviour =>
            {
                if (behaviour == null || behaviour.GetType().FullName != "VRC.SDK3.Avatars.Components.VRCAnimatorPlayAudio") return true;
                // This exact SDK behaviour only reads a clip-selection parameter
                // and writes AudioSource playback properties. Do not generalize
                // that contract to subclasses or arbitrary unknown callbacks.
                var type = behaviour.GetType();
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(type.Assembly);
                var basePackage = UnityEditor.PackageManager.PackageInfo.FindForAssembly(type.BaseType.Assembly);
                if (type.BaseType.FullName != "VRC.SDKBase.VRC_AnimatorPlayAudio" || package?.name != "com.vrchat.avatars" || package.version != "3.10.5" ||
                    basePackage?.name != "com.vrchat.base" || basePackage.version != "3.10.5")
                    throw new InvalidOperationException("このSDKの音声処理は表情への影響を確認できません: " + type.FullName);
                var reactive = avatar.GetComponentsInChildren<Renderer>(true).SelectMany(renderer => renderer.sharedMaterials)
                    .Where(material => material != null).Any(material => material.HasProperty("_UseAudioLink") && material.GetFloat("_UseAudioLink") != 0 ||
                        material.shader != null && material.shader.name.IndexOf("AudioLink", StringComparison.OrdinalIgnoreCase) >= 0);
                if (reactive) throw new InvalidOperationException("音声に反応する材質があるため、音声を省略した表情収録はできません。");
                OmittedAudioBehaviours++;
                return false;
            }).ToArray();
        }

        public void Dispose()
        {
            for (var i = owned.Count - 1; i >= 0; i--) if (owned[i] != null) Object.DestroyImmediate(owned[i]);
            owned.Clear(); copies.Clear();
        }
    }
}
