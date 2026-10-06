using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    // These exercise real processed-controller playback. No FaceEmo assembly,
    // component or root Animator controller participates in discovery.
    public sealed class DirectFxExpressionDiscoveryTests
    {
        string folder;
        GameObject avatar;
        Mesh mesh;
        AnimatorController controller;
        AnimatorState neutral;

        [SetUp]
        public void SetUp()
        {
            var name = "__DirectFxDiscovery_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            avatar = new GameObject("Avatar", typeof(Animator));
            var face = new GameObject("Face", typeof(SkinnedMeshRenderer)); face.transform.SetParent(avatar.transform, false);
            mesh = BaseShapeFixture.Create(); face.GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
            face.GetComponent<SkinnedMeshRenderer>().SetBlendShapeWeight(0, 25);
            neutral = State(controller.layers[0].stateMachine, "Neutral", Clip("Neutral", 0));
            controller.layers[0].stateMachine.defaultState = neutral;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(avatar); Object.DestroyImmediate(mesh); AssetDatabase.DeleteAsset(folder);
        }

        AnimationClip Clip(string name, float weight)
        {
            var clip = new AnimationClip { name = name };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                AnimationCurve.Constant(0, 1, weight));
            AssetDatabase.AddObjectToAsset(clip, controller); return clip;
        }
        static AnimatorState State(AnimatorStateMachine machine, string name, Motion motion)
        {
            var state = machine.AddState(name); state.motion = motion; state.writeDefaultValues = false; return state;
        }
        static AnimatorStateTransition Transition(AnimatorState from, AnimatorState to, string parameter, float value,
            AnimatorConditionMode mode = AnimatorConditionMode.Equals)
        {
            var transition = from.AddTransition(to); transition.duration = 0; transition.hasExitTime = false;
            transition.AddCondition(mode, value, parameter); return transition;
        }
        VrChatExpressionMenu.Source Read(RuntimeAnimatorController runtime = null, VrChatExpressionMenu.Source source = null)
        {
            var before = AssetDatabase.LoadAllAssetsAtPath(folder + "/FX.controller")
                .ToDictionary(asset => asset, asset => EditorJsonUtility.ToJson(asset));
            var metadata = source ?? new VrChatExpressionMenu.Source(); metadata.Controller = runtime ?? controller;
            VrChatFxExpressions.Add(avatar, metadata);
            foreach (var pair in before) Assert.That(EditorJsonUtility.ToJson(pair.Key), Is.EqualTo(pair.Value), pair.Key.name);
            Assert.That(avatar.GetComponent<Animator>().runtimeAnimatorController, Is.Null);
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(25));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh, Is.SameAs(mesh));
            return metadata;
        }
        static float Weight(VrChatExpressionMenu.Entry entry) => entry.Values.Single(value => value.Shape == "Face size").Weight;

        [Test]
        public void CustomFxParameterDiscoversFaceWithoutAMenuOrFaceEmo()
        {
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Int);
            var selected = State(controller.layers[0].stateMachine, "Authored smile", Clip("Smile", 75));
            Transition(neutral, selected, "FaceChoice", 2);
            var entry = Read().Entries.Single();
            Assert.That(entry.Name, Is.EqualTo("FX / Authored smile"));
            Assert.That(entry.Parameters["FaceChoice"], Is.EqualTo(2));
            Assert.That(entry.Error, Is.Null); Assert.That(Weight(entry), Is.EqualTo(75).Within(.01));
        }

        [Test]
        public void DescriptorFxIsAuthoritativeWhenRootAnimatorControllerIsNone()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor"))
                .FirstOrDefault(value => value != null);
            if (type == null) Assert.Ignore("Install the real VRChat SDK to validate descriptor FX discovery.");
            var descriptor = avatar.AddComponent(type);
            using (var data = new SerializedObject(descriptor))
            {
                data.FindProperty("customizeAnimationLayers").boolValue = true;
                data.FindProperty("customExpressions").boolValue = false;
                var layers = data.FindProperty("baseAnimationLayers");
                layers.arraySize = 1;
                var fx = layers.GetArrayElementAtIndex(0);
                var typeProperty = fx.FindPropertyRelative("type");
                var fxIndex = Array.IndexOf(typeProperty.enumNames, "FX");
                Assert.That(fxIndex, Is.GreaterThanOrEqualTo(0), "The installed SDK must expose its FX layer kind.");
                typeProperty.enumValueIndex = fxIndex;
                fx.FindPropertyRelative("isDefault").boolValue = false;
                fx.FindPropertyRelative("animatorController").objectReferenceValue = controller;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Bool);
            var selected = State(controller.layers[0].stateMachine, "Hand-authored expression", Clip("Smile", 85));
            Transition(neutral, selected, "FaceChoice", 0, AnimatorConditionMode.If);
            var metadata = VrChatExpressionSampler.Analyze(avatar);
            Assert.That(metadata.Controller, Is.SameAs(controller));
            Assert.That(metadata.Entries.Single().Error, Is.Null);
            Assert.That(Weight(metadata.Entries.Single()), Is.EqualTo(85).Within(.01));
            Assert.That(avatar.GetComponent<Animator>().runtimeAnimatorController, Is.Null);
        }

        [Test]
        public void NestedMachineEntryCombinesItsGateAndItsExpressionCondition()
        {
            controller.AddParameter("FaceMode", AnimatorControllerParameterType.Bool);
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Int);
            var root = controller.layers[0].stateMachine;
            var nested = root.AddStateMachine("Authored faces");
            var idle = State(nested, "Nested neutral", Clip("Nested neutral", 0)); nested.defaultState = idle;
            var face = State(nested, "Nested smile", Clip("Smile", 65));
            var enter = neutral.AddTransition(nested); enter.duration = 0; enter.hasExitTime = false;
            enter.AddCondition(AnimatorConditionMode.If, 0, "FaceMode");
            Transition(idle, face, "FaceChoice", 3);
            var entry = Read().Entries.Single(value => value.Name == "FX / Nested smile");
            Assert.That(entry.Parameters["FaceMode"], Is.EqualTo(1));
            Assert.That(entry.Parameters["FaceChoice"], Is.EqualTo(3));
            Assert.That(entry.Error, Is.Null); Assert.That(Weight(entry), Is.EqualTo(65).Within(.01));
        }

        [Test]
        public void ChainedStateTransitionsKeepThePrecedingParameterGate()
        {
            controller.AddParameter("FaceMode", AnimatorControllerParameterType.Bool);
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Int);
            var root = controller.layers[0].stateMachine;
            var gate = State(root, "Face gate", null); Transition(neutral, gate, "FaceMode", 0, AnimatorConditionMode.If);
            var selected = State(root, "Gated smile", Clip("Gated smile", 70)); Transition(gate, selected, "FaceChoice", 2);
            var entry = Read().Entries.Single();
            Assert.That(entry.Parameters["FaceMode"], Is.EqualTo(1)); Assert.That(entry.Parameters["FaceChoice"], Is.EqualTo(2));
            Assert.That(entry.Error, Is.Null); Assert.That(Weight(entry), Is.EqualTo(70).Within(.01));
        }

        [Test]
        public void NestedMachineEntryRetainsItsPrecedingStateGate()
        {
            controller.AddParameter("EnableFaces", AnimatorControllerParameterType.Bool);
            controller.AddParameter("FaceMode", AnimatorControllerParameterType.Bool);
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Int);
            var root = controller.layers[0].stateMachine;
            var gate = State(root, "Face gate", null);
            Transition(neutral, gate, "EnableFaces", 0, AnimatorConditionMode.If);
            var nested = root.AddStateMachine("Authored faces");
            var idle = State(nested, "Nested neutral", Clip("Nested neutral", 0)); nested.defaultState = idle;
            var face = State(nested, "Nested smile", Clip("Smile", 65));
            var enter = gate.AddTransition(nested); enter.duration = 0; enter.hasExitTime = false;
            enter.AddCondition(AnimatorConditionMode.If, 0, "FaceMode");
            Transition(idle, face, "FaceChoice", 2);
            var entry = Read().Entries.Single(value => value.Name == "FX / Nested smile");
            Assert.That(entry.Parameters["EnableFaces"], Is.EqualTo(1));
            Assert.That(entry.Parameters["FaceMode"], Is.EqualTo(1));
            Assert.That(entry.Parameters["FaceChoice"], Is.EqualTo(2));
            Assert.That(entry.Error, Is.Null); Assert.That(Weight(entry), Is.EqualTo(65).Within(.01));
        }

        [Test]
        public void NestedEntryStateCycleTerminatesWithoutLosingAnIndependentExpression()
        {
            controller.AddParameter("EnableFaces", AnimatorControllerParameterType.Bool);
            controller.AddParameter("FaceMode", AnimatorControllerParameterType.Bool);
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Int);
            var root = controller.layers[0].stateMachine;
            var gate = State(root, "Face gate", null);
            Transition(neutral, gate, "EnableFaces", 0, AnimatorConditionMode.If);
            var nested = root.AddStateMachine("Nested relay");
            var idle = State(nested, "Relay idle", null); nested.defaultState = idle;
            var enter = gate.AddTransition(nested); enter.duration = 0; enter.hasExitTime = false;
            enter.AddCondition(AnimatorConditionMode.If, 0, "FaceMode");
            Transition(idle, gate, "FaceMode", 0, AnimatorConditionMode.If);
            var face = State(root, "Independent smile", Clip("Smile", 65)); Transition(neutral, face, "FaceChoice", 2);
            var source = Read();
            Assert.That(source.Messages, Is.Empty);
            Assert.That(source.Entries.Single().Error, Is.Null);
            Assert.That(source.Entries.Single().Name, Is.EqualTo("FX / Independent smile"));
            Assert.That(Weight(source.Entries.Single()), Is.EqualTo(65).Within(.01));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NestedExitRouteRetainsItsInnerAndParentTransitionConditions(bool upstreamGate)
        {
            controller.AddParameter("FaceMode", AnimatorControllerParameterType.Bool);
            controller.AddParameter("EnableExit", AnimatorControllerParameterType.Bool);
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Int);
            controller.AddParameter("ParentPermission", AnimatorControllerParameterType.Bool);
            var root = controller.layers[0].stateMachine;
            var nested = root.AddStateMachine("Nested selection");
            var idle = State(nested, "Nested idle", null); nested.defaultState = idle;
            var enter = neutral.AddTransition(nested); enter.duration = 0; enter.hasExitTime = false;
            enter.AddCondition(AnimatorConditionMode.If, 0, "FaceMode");
            var exitSource = idle;
            if (upstreamGate)
            {
                exitSource = State(nested, "Exit gate", null);
                Transition(idle, exitSource, "EnableExit", 0, AnimatorConditionMode.If);
            }
            var exit = exitSource.AddExitTransition(); exit.duration = 0; exit.hasExitTime = false;
            exit.AddCondition(AnimatorConditionMode.Equals, 1, "FaceChoice");
            var face = State(root, "Selected after exit", Clip("Smile", 75));
            root.AddStateMachineTransition(nested, face).AddCondition(AnimatorConditionMode.If, 0, "ParentPermission");
            var entry = Read().Entries.Single(); Assert.That(entry.Error, Is.Null);
            Assert.That(entry.Name, Is.EqualTo("FX / Selected after exit"));
            Assert.That(entry.Parameters["FaceMode"], Is.EqualTo(1));
            Assert.That(entry.Parameters["FaceChoice"], Is.EqualTo(1));
            Assert.That(entry.Parameters["ParentPermission"], Is.EqualTo(1));
            if (upstreamGate) Assert.That(entry.Parameters["EnableExit"], Is.EqualTo(1));
            Assert.That(Weight(entry), Is.EqualTo(75).Within(.01));
        }

        AnimatorState NestedExitSelection(bool anyState)
        {
            controller.AddParameter("FaceMode", AnimatorControllerParameterType.Bool);
            controller.AddParameter("ChooseExitPath", AnimatorControllerParameterType.Bool);
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Int);
            controller.AddParameter("ParentPermission", AnimatorControllerParameterType.Bool);
            var root = controller.layers[0].stateMachine;
            var nested = root.AddStateMachine("Nested selection");
            nested.defaultState = State(nested, "Nested idle", null);
            var step = State(nested, "Selected exit path", null);
            var enter = neutral.AddTransition(nested); enter.duration = 0; enter.hasExitTime = false;
            enter.AddCondition(AnimatorConditionMode.If, 0, "FaceMode");
            if (anyState)
            {
                var choose = nested.AddAnyStateTransition(step); choose.duration = 0; choose.hasExitTime = false;
                choose.canTransitionToSelf = false; choose.AddCondition(AnimatorConditionMode.If, 0, "ChooseExitPath");
            }
            else nested.AddEntryTransition(step).AddCondition(AnimatorConditionMode.If, 0, "ChooseExitPath");
            var exit = step.AddExitTransition(); exit.duration = 0; exit.hasExitTime = false;
            exit.AddCondition(AnimatorConditionMode.Equals, 1, "FaceChoice");
            var face = State(root, "Selected after exit", Clip("Smile", 75));
            root.AddStateMachineTransition(nested, face).AddCondition(AnimatorConditionMode.If, 0, "ParentPermission");
            return face;
        }

        (int Hash, float Weight) NativeNestedExitSelection() => NativeSelection(new Dictionary<string, float>
            { ["FaceMode"] = 1, ["ChooseExitPath"] = 1, ["FaceChoice"] = 1, ["ParentPermission"] = 1 });

        (int Hash, float Weight) NativeSelection(IDictionary<string, float> selected, IDictionary<int, float> layerGoals = null, int observedLayer = 0,
            Action<AnimatorControllerPlayable> afterDefaults = null, Action<AnimatorControllerPlayable> afterSelectionFrame = null, bool checkStability = false)
        {
            var clone = Object.Instantiate(avatar); clone.hideFlags = HideFlags.HideAndDontSave;
            var graph = PlayableGraph.Create("Original nested Exit controller oracle");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            try
            {
                foreach (var behaviour in clone.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                var animator = clone.GetComponent<Animator>(); animator.runtimeAnimatorController = null;
                animator.enabled = true; animator.applyRootMotion = false; animator.fireEvents = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                // Use the original source controller, without the exporter's
                // dependency pruning, copying, adapters or state validation.
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                var output = AnimationPlayableOutput.Create(graph, "Original FX", animator); output.SetSourcePlayable(playable);
                foreach (var parameter in controller.parameters)
                    if (parameter.type == AnimatorControllerParameterType.Bool) playable.SetBool(parameter.name, parameter.defaultBool);
                    else if (parameter.type == AnimatorControllerParameterType.Int) playable.SetInteger(parameter.name, parameter.defaultInt);
                    else if (parameter.type == AnimatorControllerParameterType.Float) playable.SetFloat(parameter.name, parameter.defaultFloat);
                graph.Play(); graph.Evaluate(0f);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                afterDefaults?.Invoke(playable);
                foreach (var parameter in controller.parameters)
                    if (selected.TryGetValue(parameter.name, out var value))
                    {
                        if (parameter.type == AnimatorControllerParameterType.Bool) playable.SetBool(parameter.name, value != 0);
                        else if (parameter.type == AnimatorControllerParameterType.Int) playable.SetInteger(parameter.name, Mathf.RoundToInt(value));
                        else if (parameter.type == AnimatorControllerParameterType.Float) playable.SetFloat(parameter.name, value);
                    }
                // Unity alone has no VRChat client layer-control delegate.
                // Apply its documented goal directly for independent native
                // composition; never register a process-wide SDK handler.
                if (layerGoals != null) foreach (var goal in layerGoals) playable.SetLayerWeight(goal.Key, goal.Value);
                for (var frame = 0; frame < 120; frame++) { graph.Evaluate(1f / 60f); afterSelectionFrame?.Invoke(playable); }
                var hash = playable.GetCurrentAnimatorStateInfo(observedLayer).fullPathHash;
                var weight = clone.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0);
                if (checkStability)
                    for (var frame = 0; frame < 24; frame++)
                    {
                        graph.Evaluate(1f / 60f); afterSelectionFrame?.Invoke(playable);
                        Assert.That(playable.GetCurrentAnimatorStateInfo(observedLayer).fullPathHash, Is.EqualTo(hash));
                        Assert.That(clone.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(weight).Within(.01));
                    }
                return (hash, weight);
            }
            finally { graph.Destroy(); Object.DestroyImmediate(clone); }
        }

        [Test]
        public void NestedExitFromEntryPreservesItsProvenNativeSelection()
        {
            var face = NestedExitSelection(false);
            var native = NativeNestedExitSelection();
            Assert.That(native.Hash, Is.EqualTo(Animator.StringToHash(controller.layers[0].name + "." + face.name)),
                "The original native controller must reach the claimed exported state.");
            Assert.That(native.Weight, Is.EqualTo(75).Within(.01));
            var entry = Read().Entries.Single(); Assert.That(entry.Error, Is.Null);
            Assert.That(entry.Parameters["FaceMode"], Is.EqualTo(1));
            Assert.That(entry.Parameters["ChooseExitPath"], Is.EqualTo(1));
            Assert.That(entry.Parameters["FaceChoice"], Is.EqualTo(1));
            Assert.That(entry.Parameters["ParentPermission"], Is.EqualTo(1));
            Assert.That(Weight(entry), Is.EqualTo(75).Within(.01));
        }

        [Test]
        public void NestedExitBlockedByActiveAnyStateKeepsTheNativeReachabilityDiagnostic()
        {
            var face = NestedExitSelection(true);
            var native = NativeNestedExitSelection();
            Assert.That(native.Hash, Is.Not.EqualTo(Animator.StringToHash(controller.layers[0].name + "." + face.name)),
                "The original native graph's persistent AnyState condition must prevent this endpoint too; do not weaken the sampler to accept it.");
            Assert.That(native.Weight, Is.Not.EqualTo(75).Within(.01));
            var entry = Read().Entries.Single();
            Assert.That(entry.Error, Does.Contain("状態に到達").And.Contain(face.name));
            Assert.That(entry.Values, Is.Empty);
            Assert.That(entry.Parameters["FaceMode"], Is.EqualTo(1));
            Assert.That(entry.Parameters["ChooseExitPath"], Is.EqualTo(1));
            Assert.That(entry.Parameters["FaceChoice"], Is.EqualTo(1));
            Assert.That(entry.Parameters["ParentPermission"], Is.EqualTo(1));
        }

        [Test]
        public void RecursiveNestedExitRetainsEachLevelOfConditions()
        {
            controller.AddParameter("FaceMode", AnimatorControllerParameterType.Bool);
            controller.AddParameter("InnerMode", AnimatorControllerParameterType.Bool);
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Int);
            controller.AddParameter("MiddlePermission", AnimatorControllerParameterType.Bool);
            controller.AddParameter("ParentPermission", AnimatorControllerParameterType.Bool);
            var root = controller.layers[0].stateMachine;
            var outer = root.AddStateMachine("Outer selection");
            var idle = State(outer, "Outer idle", null); outer.defaultState = idle;
            var enter = neutral.AddTransition(outer); enter.duration = 0; enter.hasExitTime = false;
            enter.AddCondition(AnimatorConditionMode.If, 0, "FaceMode");
            var inner = outer.AddStateMachine("Inner selection");
            var wait = State(inner, "Inner idle", null); inner.defaultState = wait;
            var choose = idle.AddTransition(inner); choose.duration = 0; choose.hasExitTime = false;
            choose.AddCondition(AnimatorConditionMode.If, 0, "InnerMode");
            var exit = wait.AddExitTransition(); exit.duration = 0; exit.hasExitTime = false;
            exit.AddCondition(AnimatorConditionMode.Equals, 1, "FaceChoice");
            outer.AddStateMachineExitTransition(inner).AddCondition(AnimatorConditionMode.If, 0, "MiddlePermission");
            var face = State(root, "Selected after recursive exit", Clip("Smile", 85));
            root.AddStateMachineTransition(outer, face).AddCondition(AnimatorConditionMode.If, 0, "ParentPermission");
            var entry = Read().Entries.Single(); Assert.That(entry.Error, Is.Null);
            Assert.That(entry.Parameters.Keys, Is.EquivalentTo(new[] { "FaceMode", "InnerMode", "FaceChoice", "MiddlePermission", "ParentPermission" }));
            Assert.That(entry.Parameters.Values, Is.All.EqualTo(1)); Assert.That(Weight(entry), Is.EqualTo(85).Within(.01));
        }

        [Test]
        public void NestedExitCycleDoesNotSuppressAnIndependentNativeFace()
        {
            controller.AddParameter("FaceMode", AnimatorControllerParameterType.Bool);
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Int);
            controller.AddParameter("IndependentFace", AnimatorControllerParameterType.Bool);
            var root = controller.layers[0].stateMachine;
            var nested = root.AddStateMachine("Recursive selection");
            var idle = State(nested, "Nested idle", null); nested.defaultState = idle;
            var enter = neutral.AddTransition(nested); enter.duration = 0; enter.hasExitTime = false;
            enter.AddCondition(AnimatorConditionMode.If, 0, "FaceMode");
            var exit = idle.AddExitTransition(); exit.duration = 0; exit.hasExitTime = false;
            exit.AddCondition(AnimatorConditionMode.Equals, 1, "FaceChoice");
            root.AddStateMachineTransition(nested, nested).AddCondition(AnimatorConditionMode.Equals, 3, "FaceChoice");
            var face = State(root, "Independent smile", Clip("Smile", 65));
            Transition(neutral, face, "IndependentFace", 0, AnimatorConditionMode.If);
            var source = Read(); Assert.That(source.Messages, Is.Empty);
            var entry = source.Entries.Single(); Assert.That(entry.Error, Is.Null);
            Assert.That(entry.Parameters.Keys, Is.EquivalentTo(new[] { "FaceMode", "IndependentFace" }));
            Assert.That(entry.Parameters["FaceMode"], Is.EqualTo(0)); Assert.That(entry.Parameters["IndependentFace"], Is.EqualTo(1));
            var native = NativeSelection(entry.Parameters, checkStability: true);
            Assert.That(native.Hash, Is.EqualTo(Animator.StringToHash(controller.layers[0].name + "." + face.name)));
            Assert.That(native.Weight, Is.EqualTo(65).Within(.01));
            Assert.That(Weight(entry), Is.EqualTo(65).Within(.01));
        }

        [Test]
        public void DeepNestedExitPathReportsAnAtomicOptionalDiagnostic()
        {
            controller.AddParameter("FaceMode", AnimatorControllerParameterType.Bool);
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Int);
            var root = controller.layers[0].stateMachine;
            var previousMachine = root;
            var previousState = neutral;
            AnimatorStateMachine first = null;
            for (var index = 0; index < 8; index++)
            {
                var machine = previousMachine.AddStateMachine("Exit gate " + index);
                var idle = State(machine, "Idle " + index, null); machine.defaultState = idle;
                var enter = previousState.AddTransition(machine); enter.duration = 0; enter.hasExitTime = false;
                enter.AddCondition(AnimatorConditionMode.If, 0, "FaceMode");
                if (first == null) first = machine;
                else previousMachine.AddStateMachineExitTransition(machine);
                previousMachine = machine; previousState = idle;
            }
            var exit = previousState.AddExitTransition(); exit.duration = 0; exit.hasExitTime = false;
            exit.AddCondition(AnimatorConditionMode.Equals, 1, "FaceChoice");
            var face = State(root, "Selected after deep exit", Clip("Smile", 75)); root.AddStateMachineTransition(first, face);
            var source = new VrChatExpressionMenu.Source { Controller = controller };
            var authored = new VrChatExpressionMenu.Entry { Id = "menu", Name = "Existing authored expression" }; source.Entries.Add(authored);
            Assert.DoesNotThrow(() => VrChatFxExpressions.Add(avatar, source));
            Assert.That(source.Entries.Single(), Is.SameAs(authored));
            Assert.That(source.Messages.Single(), Does.Contain("条件経路").And.Contain("深すぎる"));
        }

        void Parameter(string name, AnimatorControllerParameterType type, float initial)
        {
            controller.AddParameter(new AnimatorControllerParameter { name = name, type = type,
                defaultBool = initial != 0, defaultInt = Mathf.RoundToInt(initial), defaultFloat = initial });
        }

        (AnimatorStateMachine Machine, AnimatorState Default, AnimatorState Earlier) EntryFaces()
        {
            controller.AddParameter("Group", AnimatorControllerParameterType.Int);
            var nested = controller.layers[0].stateMachine.AddStateMachine("Priority faces");
            var fallback = State(nested, "Default face", Clip("Fallback smile", 75)); nested.defaultState = fallback;
            var earlier = State(nested, "Earlier face", Clip("Earlier face", 20));
            var enter = neutral.AddTransition(nested); enter.duration = 0; enter.hasExitTime = false;
            enter.AddCondition(AnimatorConditionMode.Equals, 1, "Group");
            return (nested, fallback, earlier);
        }

        void AssertNativeFace(VrChatExpressionMenu.Entry entry, string statePath, IDictionary<int, float> goals = null, int observedLayer = 0)
        {
            Assert.That(entry.Error, Is.Null);
            var native = NativeSelection(entry.Parameters, goals, observedLayer);
            Assert.That(native.Hash, Is.EqualTo(Animator.StringToHash(controller.layers[observedLayer].name + "." + statePath)));
            Assert.That(native.Weight, Is.EqualTo(Weight(entry)).Within(.01), "The complete exporter pose must match the original native graph.");
        }

        [TestCase(AnimatorControllerParameterType.Bool, AnimatorConditionMode.If, 1f, 0f)]
        [TestCase(AnimatorControllerParameterType.Bool, AnimatorConditionMode.IfNot, 0f, 0f)]
        [TestCase(AnimatorControllerParameterType.Int, AnimatorConditionMode.Equals, 1f, 1f)]
        [TestCase(AnimatorControllerParameterType.Int, AnimatorConditionMode.NotEqual, 0f, 1f)]
        [TestCase(AnimatorControllerParameterType.Int, AnimatorConditionMode.Greater, 2f, 1f)]
        [TestCase(AnimatorControllerParameterType.Int, AnimatorConditionMode.Less, 0f, 1f)]
        [TestCase(AnimatorControllerParameterType.Float, AnimatorConditionMode.Greater, 1f, .5f)]
        [TestCase(AnimatorControllerParameterType.Float, AnimatorConditionMode.Less, 0f, .5f)]
        public void NestedEntryDefaultFallthroughUsesLogicalComparisonBoundaries(AnimatorControllerParameterType type,
            AnimatorConditionMode mode, float initial, float threshold)
        {
            Parameter("Mode", type, initial); var faces = EntryFaces();
            faces.Machine.AddEntryTransition(faces.Earlier).AddCondition(mode, threshold, "Mode");
            var source = Read(); Assert.That(source.Entries.Select(entry => entry.Error), Is.All.Null);
            var fallback = source.Entries.Single(entry => entry.Name == "FX / Default face");
            Assert.That(fallback.Parameters["Mode"], Is.Not.EqualTo(initial));
            if (type == AnimatorControllerParameterType.Float) Assert.That(fallback.Parameters["Mode"], Is.EqualTo(threshold));
            AssertNativeFace(fallback, "Priority faces.Default face"); Assert.That(Weight(fallback), Is.EqualTo(75).Within(.01));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void FloatEntryFallthroughKeepsAdjacentRepresentableBounds(bool greater)
        {
            Parameter("Mode", AnimatorControllerParameterType.Float, greater ? 1 : 0); var faces = EntryFaces();
            var lower = .5f; var upper = .50000006f;
            var rootEnter = neutral.transitions.Single();
            rootEnter.AddCondition(greater ? AnimatorConditionMode.Greater : AnimatorConditionMode.Less, greater ? lower : upper, "Mode");
            faces.Machine.AddEntryTransition(faces.Earlier).AddCondition(greater ? AnimatorConditionMode.Greater : AnimatorConditionMode.Less,
                greater ? upper : lower, "Mode");
            var fallback = Read().Entries.Single(entry => entry.Name == "FX / Default face");
            Assert.That(fallback.Parameters["Mode"], Is.EqualTo(greater ? upper : lower));
            AssertNativeFace(fallback, "Priority faces.Default face");
        }

        [Test]
        public void DefaultBlendTreeUsesEntryFallthroughConstraintsForEveryPoint()
        {
            Parameter("Mode", AnimatorControllerParameterType.Int, 1); controller.AddParameter("ExpressionStrength", AnimatorControllerParameterType.Float);
            var faces = EntryFaces(); faces.Machine.AddEntryTransition(faces.Earlier).AddCondition(AnimatorConditionMode.Equals, 1, "Mode");
            var tree = new BlendTree { name = "Default faces", blendType = BlendTreeType.Simple1D,
                blendParameter = "ExpressionStrength", useAutomaticThresholds = false }; AssetDatabase.AddObjectToAsset(tree, controller);
            tree.AddChild(Clip("Default rest", 30), 0); tree.AddChild(Clip("Default smile", 75), 1); faces.Default.motion = tree;
            var source = Read(); Assert.That(source.Entries.Select(entry => entry.Error), Is.All.Null);
            var face = source.Entries.Single(entry => entry.Name.StartsWith("FX / Default face", StringComparison.Ordinal) &&
                entry.Parameters.TryGetValue("ExpressionStrength", out var value) && value == 1);
            Assert.That(face.Parameters["Mode"], Is.Not.EqualTo(1)); AssertNativeFace(face, "Priority faces.Default face");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LaterEntryRequiresEarlierConjunctionToBeFalse(bool readonlyFirst)
        {
            Parameter("EarlierA", AnimatorControllerParameterType.Bool, 1); Parameter("EarlierB", AnimatorControllerParameterType.Bool, 1);
            Parameter("Later", AnimatorControllerParameterType.Bool, 1); var faces = EntryFaces();
            var earlier = faces.Machine.AddEntryTransition(faces.Earlier); earlier.AddCondition(AnimatorConditionMode.If, 0, "EarlierA");
            earlier.AddCondition(AnimatorConditionMode.If, 0, "EarlierB");
            var later = State(faces.Machine, "Later face", Clip("Later face", 85));
            faces.Machine.AddEntryTransition(later).AddCondition(AnimatorConditionMode.If, 0, "Later");
            var metadata = new VrChatExpressionMenu.Source(); if (readonlyFirst) metadata.ExternalParameters.Add("EarlierA");
            var entry = Read(source: metadata).Entries.Single(value => value.Name == "FX / Later face");
            Assert.That(entry.Parameters[readonlyFirst ? "EarlierB" : "EarlierA"], Is.EqualTo(0));
            Assert.That(entry.Parameters["Later"], Is.EqualTo(1)); AssertNativeFace(entry, "Priority faces.Later face");
        }

        [Test]
        public void OverlappingEntriesKeepPriorityWhenSelectingTheLaterFace()
        {
            Parameter("Mode", AnimatorControllerParameterType.Int, 1); var faces = EntryFaces();
            faces.Machine.AddEntryTransition(faces.Earlier).AddCondition(AnimatorConditionMode.Equals, 1, "Mode");
            var later = State(faces.Machine, "Later face", Clip("Later face", 85));
            faces.Machine.AddEntryTransition(later).AddCondition(AnimatorConditionMode.Greater, 0, "Mode");
            var entry = Read().Entries.Single(value => value.Name == "FX / Later face");
            Assert.That(entry.Parameters["Mode"], Is.GreaterThan(1)); AssertNativeFace(entry, "Priority faces.Later face");
        }

        [Test]
        public void UnconditionalEntryCannotPublishLaterOrDefaultFaces()
        {
            Parameter("Mode", AnimatorControllerParameterType.Int, 1); var faces = EntryFaces(); faces.Machine.AddEntryTransition(faces.Earlier);
            faces.Machine.AddEntryTransition(faces.Default).AddCondition(AnimatorConditionMode.Equals, 1, "Mode");
            var entry = Read().Entries.Single(); Assert.That(entry.Name, Is.EqualTo("FX / Earlier face"));
            AssertNativeFace(entry, "Priority faces.Earlier face");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DisabledEntryCannotSuppressEnabledRoutes(bool solo)
        {
            Parameter("Mode", AnimatorControllerParameterType.Int, 1); var faces = EntryFaces();
            var ignored = faces.Machine.AddEntryTransition(faces.Earlier); ignored.mute = !solo;
            var active = State(faces.Machine, "Enabled face", Clip("Enabled face", 85));
            var entry = faces.Machine.AddEntryTransition(active); entry.solo = solo; entry.AddCondition(AnimatorConditionMode.Equals, 1, "Mode");
            var source = Read(); Assert.That(source.Entries.Any(value => value.Name == "FX / Earlier face"), Is.False);
            AssertNativeFace(source.Entries.Single(value => value.Name == "FX / Enabled face"), "Priority faces.Enabled face");
        }

        [TestCase("nested")]
        [TestCase("entry exit")]
        [TestCase("default exit")]
        public void EntryFallthroughPropagatesAcrossNestedAndExitRoutes(string route)
        {
            Parameter("Mode", AnimatorControllerParameterType.Int, 1); Parameter("ChooseExit", AnimatorControllerParameterType.Bool, 0);
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Int); var faces = EntryFaces(); faces.Default.motion = null;
            faces.Machine.AddEntryTransition(faces.Earlier).AddCondition(AnimatorConditionMode.Equals, 1, "Mode");
            if (route == "nested")
            {
                var inner = faces.Machine.AddStateMachine("Inner exit"); var idle = State(inner, "Inner idle", null); inner.defaultState = idle;
                faces.Machine.AddEntryTransition(inner).AddCondition(AnimatorConditionMode.If, 0, "ChooseExit");
                var exit = idle.AddExitTransition(); exit.duration = 0; exit.hasExitTime = false;
                exit.AddCondition(AnimatorConditionMode.Equals, 1, "FaceChoice"); faces.Machine.AddStateMachineExitTransition(inner);
            }
            else if (route == "entry exit")
            {
                var step = State(faces.Machine, "Selected entry exit", null);
                faces.Machine.AddEntryTransition(step).AddCondition(AnimatorConditionMode.If, 0, "ChooseExit");
                var exit = step.AddExitTransition(); exit.duration = 0; exit.hasExitTime = false;
                exit.AddCondition(AnimatorConditionMode.Equals, 1, "FaceChoice");
            }
            else
            {
                var exit = faces.Default.AddExitTransition(); exit.duration = 0; exit.hasExitTime = false;
                exit.AddCondition(AnimatorConditionMode.Equals, 1, "FaceChoice");
            }
            var face = State(controller.layers[0].stateMachine, "Face after priority exit", Clip("Exit face", 85));
            controller.layers[0].stateMachine.AddStateMachineTransition(faces.Machine, face);
            var native = NativeSelection(new Dictionary<string, float> { ["Group"] = 1, ["Mode"] = 0, ["ChooseExit"] = 1, ["FaceChoice"] = 1 },
                checkStability: true);
            Assert.That(native.Hash, Is.EqualTo(Animator.StringToHash(controller.layers[0].name + "." + face.name)));
            Assert.That(native.Weight, Is.EqualTo(85).Within(.01));
            var entry = Read().Entries.Single(value => value.Name == "FX / Face after priority exit");
            Assert.That(entry.Parameters["Mode"], Is.Not.EqualTo(1)); AssertNativeFace(entry, "Face after priority exit");
        }

        [Test]
        public void MalformedDirectEntryExitReportsBeforeNativePlayback()
        {
            var faces = EntryFaces(); var invalid = faces.Machine.AddEntryTransition(faces.Default);
            invalid.destinationState = null; invalid.isExit = true;
            var source = new VrChatExpressionMenu.Source { Controller = controller };
            var authored = new VrChatExpressionMenu.Entry { Id = "menu", Name = "Existing authored expression" }; source.Entries.Add(authored);
            Assert.DoesNotThrow(() => Read(source: source));
            Assert.That(source.Entries.Single(), Is.SameAs(authored));
            Assert.That(source.Messages.Single(), Does.Contain("EntryからExit"));
        }

        [Test]
        public void EntryNegationExpansionReportsAnAtomicDiscoveryBudgetDiagnostic()
        {
            var faces = EntryFaces();
            for (var index = 0; index < 14; index++)
            {
                Parameter("A" + index, AnimatorControllerParameterType.Bool, 1); Parameter("B" + index, AnimatorControllerParameterType.Bool, 1);
                var entry = faces.Machine.AddEntryTransition(faces.Earlier);
                entry.AddCondition(AnimatorConditionMode.If, 0, "A" + index); entry.AddCondition(AnimatorConditionMode.If, 0, "B" + index);
            }
            var source = new VrChatExpressionMenu.Source { Controller = controller };
            var authored = new VrChatExpressionMenu.Entry { Id = "menu", Name = "Authored face" }; source.Entries.Add(authored);
            Assert.DoesNotThrow(() => VrChatFxExpressions.Add(avatar, source));
            Assert.That(source.Entries.Single(), Is.SameAs(authored)); Assert.That(source.Messages.Single(), Does.Contain("探索").And.Contain("上限"));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void DefaultWarmupCanEstablishAnEarlierGateBeforeTheSelectedFace(bool warmup)
        {
            Parameter("Mode", AnimatorControllerParameterType.Int, warmup ? 1 : 0);
            var root = controller.layers[0].stateMachine; var primed = State(root, "Primed", null);
            Transition(neutral, primed, "Mode", 1); var face = State(root, "Warm selected face", Clip("Warm selected face", 80));
            Transition(primed, face, "Mode", 2);
            var native = NativeSelection(new Dictionary<string, float> { ["Mode"] = 2 }); var source = Read();
            if (warmup)
            {
                var entry = source.Entries.Single(); Assert.That(entry.Parameters["Mode"], Is.EqualTo(2));
                Assert.That(native.Hash, Is.EqualTo(Animator.StringToHash(controller.layers[0].name + "." + face.name)));
                Assert.That(entry.Error, Is.Null); Assert.That(Weight(entry), Is.EqualTo(native.Weight).Within(.01));
            }
            else
            {
                Assert.That(native.Hash, Is.Not.EqualTo(Animator.StringToHash(controller.layers[0].name + "." + face.name)));
                Assert.That(source.Entries, Is.Empty);
            }
        }

        [Test]
        public void StableAnyStatePriorityCanSelectTheLaterNativeFace()
        {
            controller.AddParameter("Mode", AnimatorControllerParameterType.Int); var root = controller.layers[0].stateMachine;
            var earlier = State(root, "Earlier Any face", Clip("Earlier Any face", 20));
            var later = State(root, "Later Any face", Clip("Later Any face", 85));
            var first = root.AddAnyStateTransition(earlier); first.duration = 0; first.hasExitTime = false; first.canTransitionToSelf = true;
            first.AddCondition(AnimatorConditionMode.NotEqual, 2, "Mode");
            var second = root.AddAnyStateTransition(later); second.duration = 0; second.hasExitTime = false; second.canTransitionToSelf = true;
            second.AddCondition(AnimatorConditionMode.NotEqual, 1, "Mode");
            var native = NativeSelection(new Dictionary<string, float> { ["Mode"] = 2 });
            Assert.That(native.Hash, Is.EqualTo(Animator.StringToHash(controller.layers[0].name + "." + later.name)));
            var entry = Read().Entries.Single(); Assert.That(entry.Parameters["Mode"], Is.EqualTo(2));
            Assert.That(entry.Error, Is.Null); Assert.That(Weight(entry), Is.EqualTo(native.Weight).Within(.01));
        }

        [Test]
        public void SelfDisabledAnyStatePriorDoesNotHideAStableDriverControlledFace()
        {
            Parameter("BlockA", AnimatorControllerParameterType.Bool, 1); controller.AddParameter("SelectFace", AnimatorControllerParameterType.Bool);
            var root = controller.layers[0].stateMachine;
            var earlier = State(root, "Default Any face", Clip("Default Any face", 20));
            var later = State(root, "Stable driver face", Clip("Stable driver face", 85));
            var first = root.AddAnyStateTransition(earlier); first.duration = 0; first.hasExitTime = false; first.canTransitionToSelf = false;
            first.AddCondition(AnimatorConditionMode.If, 0, "BlockA");
            var second = root.AddAnyStateTransition(later); second.duration = 0; second.hasExitTime = false; second.canTransitionToSelf = false;
            second.AddCondition(AnimatorConditionMode.If, 0, "SelectFace");
            ParameterDriverExpressionTests.Driver(later, ParameterDriverExpressionTests.Op("Set", "BlockA", 0));
            var laterHash = Animator.StringToHash(controller.layers[0].name + "." + later.name);
            var applied = false;
            var original = NativeSelection(new Dictionary<string, float> { ["SelectFace"] = 1 },
                afterDefaults: playable => Assert.That(playable.GetCurrentAnimatorStateInfo(0).fullPathHash,
                    Is.EqualTo(Animator.StringToHash(controller.layers[0].name + "." + earlier.name))),
                afterSelectionFrame: playable =>
                {
                    if (!applied && playable.GetCurrentAnimatorStateInfo(0).fullPathHash == laterHash)
                    { playable.SetBool("BlockA", false); applied = true; }
                }, checkStability: true);
            Assert.That(applied, Is.True, "The original native graph must reach B before applying B's documented Set operation.");
            Assert.That(original.Hash, Is.EqualTo(laterHash)); Assert.That(original.Weight, Is.EqualTo(85).Within(.01));
            // Native SDK-adapted sampling succeeds independently of discovery:
            // defaults establish A, whose self-disabled prior lets B enter,
            // and B's deterministic Set makes its final pose stable.
            var metadata = new VrChatExpressionMenu.Source { Controller = controller };
            var oracle = VrChatExpressionSampler.SampleFixed(avatar, controller, metadata.Defaults,
                new Dictionary<string, float> { ["SelectFace"] = 1 }, metadata: metadata,
                expectedLayer: 0, expectedStatePath: controller.layers[0].name + "." + later.name);
            Assert.That(oracle.Single(value => value.Shape == "Face size").Weight, Is.EqualTo(85).Within(.01));
            var entry = Read().Entries.Single(); Assert.That(entry.Error, Is.Null);
            Assert.That(entry.Parameters.Keys, Is.EqualTo(new[] { "SelectFace" })); Assert.That(Weight(entry), Is.EqualTo(85).Within(.01));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void StateTransitionPriorityUsesNativeSelfEligibility(bool self)
        {
            controller.AddParameter("Group", AnimatorControllerParameterType.Int); controller.AddParameter("Mode", AnimatorControllerParameterType.Int);
            var root = controller.layers[0].stateMachine;
            var earlier = self ? neutral : State(root, "Earlier face", Clip("Earlier face", 20));
            var first = Transition(neutral, earlier, "Group", 1); first.canTransitionToSelf = !self;
            first.AddCondition(AnimatorConditionMode.NotEqual, 2, "Mode");
            var later = State(root, "Later face", Clip("Later face", 85));
            var second = Transition(neutral, later, "Group", 1); second.AddCondition(AnimatorConditionMode.NotEqual, 1, "Mode");
            var nativeFirst = NativeSelection(new Dictionary<string, float> { ["Group"] = 1, ["Mode"] = 0 }, checkStability: true);
            Assert.That(nativeFirst.Hash, Is.EqualTo(Animator.StringToHash(controller.layers[0].name + "." + (self ? later : earlier).name)));
            Assert.That(nativeFirst.Weight, Is.EqualTo(self ? 85 : 20).Within(.01));
            if (self)
            {
                first.canTransitionToSelf = true;
                var nativeSelf = NativeSelection(new Dictionary<string, float> { ["Group"] = 1, ["Mode"] = 0 }, checkStability: true);
                Assert.That(nativeSelf.Hash, Is.EqualTo(Animator.StringToHash(controller.layers[0].name + "." + neutral.name)),
                    "The same native ordinary self transition takes priority when self transition is enabled.");
                Assert.That(nativeSelf.Weight, Is.EqualTo(0).Within(.01)); first.canTransitionToSelf = false;
            }
            var source = Read(); Assert.That(source.Entries.Select(entry => entry.Error), Is.All.Null);
            var entry = source.Entries.Single(value => value.Name == "FX / Later face");
            Assert.That(entry.Parameters["Mode"], Is.EqualTo(self ? 0 : 2)); AssertNativeFace(entry, "Later face");
        }

        [Test]
        public void StateMachineExitPriorityKeepsItsInnerGateAndLaterNativeFace()
        {
            controller.AddParameter("Group", AnimatorControllerParameterType.Int);
            controller.AddParameter("SelectExit", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Choice", AnimatorControllerParameterType.Int);
            var root = controller.layers[0].stateMachine; var nested = root.AddStateMachine("Exit selection");
            var idle = State(nested, "Exit source", null); nested.defaultState = idle;
            var enter = neutral.AddTransition(nested); enter.duration = 0; enter.hasExitTime = false;
            enter.AddCondition(AnimatorConditionMode.Equals, 1, "Group");
            var exit = idle.AddExitTransition(); exit.duration = 0; exit.hasExitTime = false;
            exit.AddCondition(AnimatorConditionMode.If, 0, "SelectExit");
            var earlier = State(root, "Earlier exit face", Clip("Earlier exit face", 20));
            var later = State(root, "Later exit face", Clip("Later exit face", 85));
            root.AddStateMachineTransition(nested, earlier).AddCondition(AnimatorConditionMode.NotEqual, 2, "Choice");
            root.AddStateMachineTransition(nested, later).AddCondition(AnimatorConditionMode.NotEqual, 1, "Choice");
            Action<AnimatorControllerPlayable> assertDefault = playable => Assert.That(playable.GetCurrentAnimatorStateInfo(0).fullPathHash,
                Is.EqualTo(Animator.StringToHash(controller.layers[0].name + "." + neutral.name)));
            var first = NativeSelection(new Dictionary<string, float> { ["Group"] = 1, ["SelectExit"] = 1, ["Choice"] = 0 },
                afterDefaults: assertDefault, checkStability: true);
            Assert.That(first.Hash, Is.EqualTo(Animator.StringToHash(controller.layers[0].name + "." + earlier.name)));
            Assert.That(first.Weight, Is.EqualTo(20).Within(.01));
            var native = NativeSelection(new Dictionary<string, float> { ["Group"] = 1, ["SelectExit"] = 1, ["Choice"] = 2 },
                afterDefaults: assertDefault, checkStability: true);
            Assert.That(native.Hash, Is.EqualTo(Animator.StringToHash(controller.layers[0].name + "." + later.name)));
            Assert.That(native.Weight, Is.EqualTo(85).Within(.01));
            var source = Read(); Assert.That(source.Messages, Is.Empty); Assert.That(source.Entries.Select(value => value.Error), Is.All.Null);
            var entry = source.Entries.Single(value => value.Name == "FX / Later exit face");
            Assert.That(entry.Parameters["Group"], Is.EqualTo(1)); Assert.That(entry.Parameters["SelectExit"], Is.EqualTo(1));
            Assert.That(entry.Parameters["Choice"], Is.EqualTo(2)); Assert.That(Weight(entry), Is.EqualTo(native.Weight).Within(.01));
        }

        StateMachineBehaviour LayerGoal(AnimatorState state, float goal, int target, float duration = 0)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly =>
                assembly.GetType("VRC.SDK3.Avatars.Components.VRCAnimatorLayerControl")).FirstOrDefault(value => value != null);
            if (type == null) Assert.Ignore("Install the real VRChat SDK to validate layer-command discovery.");
            var control = state.AddStateMachineBehaviour(type);
            using var data = new SerializedObject(control);
            var playable = data.FindProperty("playable"); playable.enumValueIndex = Array.IndexOf(playable.enumNames, "FX");
            data.FindProperty("layer").intValue = target; data.FindProperty("goalWeight").floatValue = goal;
            data.FindProperty("blendDuration").floatValue = duration; data.ApplyModifiedPropertiesWithoutUndo(); return control;
        }

        AnimatorState WeightCommandGraph(bool enable, string input = "FaceMode", bool curveRelay = false)
        {
            controller.AddParameter(input, AnimatorControllerParameterType.Int); neutral.motion = Clip("Native base face", 20);
            controller.AddLayer("SDK commands"); controller.AddLayer("Controlled face output");
            var layers = controller.layers; layers[1].defaultWeight = 1; layers[2].defaultWeight = enable ? 0 : 1; controller.layers = layers;
            var rest = State(layers[1].stateMachine, "Command rest", null); layers[1].stateMachine.defaultState = rest;
            var selected = State(layers[1].stateMachine, "Selected command", null); Transition(rest, selected, input, 1);
            LayerGoal(rest, enable ? 0 : 1, 2); LayerGoal(selected, enable ? 1 : 0, 2);
            Motion output = Clip("Controlled smile", 80);
            if (curveRelay)
            {
                controller.AddParameter("Internal face output", AnimatorControllerParameterType.Float);
                var clip = new AnimationClip { name = "Native curve relay" };
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "Internal face output"),
                    AnimationCurve.Constant(0, 1, 1)); AssetDatabase.AddObjectToAsset(clip, controller); output = clip;
                controller.AddLayer("Final face"); var all = controller.layers; all[3].defaultWeight = 1; controller.layers = all;
                var idle = State(all[3].stateMachine, "Final rest", Clip("Final rest", 20)); all[3].stateMachine.defaultState = idle;
                Transition(idle, State(all[3].stateMachine, "Final smile", Clip("Final smile", 80)),
                    "Internal face output", .5f, AnimatorConditionMode.Greater);
            }
            layers[2].stateMachine.defaultState = State(layers[2].stateMachine, "Controlled output", output); return selected;
        }

        [TestCase(true)]
        [TestCase(false)]
        public void EmptySdkLayerCommandKeepsTheNativeFaceLayerComposition(bool enable)
        {
            WeightCommandGraph(enable);
            var entry = Read().Entries.Single(); Assert.That(entry.Name, Is.EqualTo("FX / Selected command"));
            Assert.That(entry.Parameters.Keys, Is.EqualTo(new[] { "FaceMode" }));
            Assert.That(Weight(entry), Is.EqualTo(enable ? 80 : 20).Within(.01));
            AssertNativeFace(entry, "Selected command", new Dictionary<int, float> { [2] = enable ? 1 : 0 }, 1);
        }

        [Test]
        public void EmptySdkLayerCommandCanControlAnAnimatorCurveRelayToTheFace()
        {
            WeightCommandGraph(true, curveRelay: true);
            var entry = Read().Entries.Single(); Assert.That(entry.Parameters.Keys, Is.EqualTo(new[] { "FaceMode" }));
            Assert.That(Weight(entry), Is.EqualTo(80).Within(.01));
            AssertNativeFace(entry, "Selected command", new Dictionary<int, float> { [2] = 1 }, 1);
        }

        [Test]
        public void DriverRelayToSdkLayerCommandKeepsTheActualRootSelection()
        {
            WeightCommandGraph(true, "Internal command signal"); controller.AddParameter("UserFace", AnimatorControllerParameterType.Int);
            var producer = State(controller.layers[0].stateMachine, "Authored command relay", null);
            Transition(neutral, producer, "UserFace", 1);
            ParameterDriverExpressionTests.Driver(producer, ParameterDriverExpressionTests.Op("Set", "Internal command signal", 1));
            var entry = Read().Entries.Single(); Assert.That(entry.Name, Is.EqualTo("FX / Authored command relay"));
            Assert.That(entry.Parameters.Keys, Is.EqualTo(new[] { "UserFace" })); Assert.That(entry.Error, Is.Null);
            // Apply the fixture's explicit Set operation and layer goal to
            // the original graph for an independent native output oracle.
            var native = NativeSelection(new Dictionary<string, float> { ["UserFace"] = 1, ["Internal command signal"] = 1 },
                new Dictionary<int, float> { [2] = 1 });
            Assert.That(native.Hash, Is.EqualTo(Animator.StringToHash(controller.layers[0].name + "." + producer.name)));
            Assert.That(Weight(entry), Is.EqualTo(native.Weight).Within(.01)); Assert.That(Weight(entry), Is.EqualTo(80).Within(.01));
        }

        [TestCase("duration")]
        [TestCase("target")]
        [TestCase("callback")]
        public void EmptySdkLayerCommandRetainsUnsupportedOrMalformedDiagnostics(string kind)
        {
            var selected = WeightCommandGraph(true);
            if (kind == "callback") selected.AddStateMachineBehaviour<UnknownStateCallbackProbe>();
            else
            {
                using var data = new SerializedObject(selected.behaviours.Single());
                if (kind == "duration") data.FindProperty("blendDuration").floatValue = .5f;
                else data.FindProperty("layer").intValue = 99;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            var source = Read(); Assert.That(source.Entries.Where(entry => entry.Error == null), Is.Empty);
            if (kind == "target") Assert.That(source.Messages.Single(), Does.Contain("レイヤー制御").And.Contain("99"));
            else Assert.That(source.Entries.Single().Error, Is.Not.Null.And.Not.Empty);
            Assert.That(source.Entries.SelectMany(entry => entry.Values), Is.Empty);
        }

        [Test]
        public void ClothingOnlySdkLayerCommandCannotCreateAFacialCandidate()
        {
            WeightCommandGraph(true);
            var output = controller.layers[2].stateMachine.defaultState;
            var clip = new AnimationClip { name = "Clothing only" };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Clothes", typeof(GameObject), "m_IsActive"),
                AnimationCurve.Constant(0, 1, 1)); AssetDatabase.AddObjectToAsset(clip, controller); output.motion = clip;
            var source = Read(); Assert.That(source.Entries, Is.Empty); Assert.That(source.Messages, Is.Empty);
        }

        [Test]
        public void OneDimensionalTreePublishesAuthoredSelectionsThroughNativeBlending()
        {
            controller.AddParameter("ExpressionStrength", AnimatorControllerParameterType.Float);
            var tree = new BlendTree { name = "Authored face mix", blendType = BlendTreeType.Simple1D,
                blendParameter = "ExpressionStrength", useAutomaticThresholds = false };
            AssetDatabase.AddObjectToAsset(tree, controller);
            tree.AddChild(Clip("Off", 0), 0); tree.AddChild(Clip("Half", 40), .5f); tree.AddChild(Clip("Full", 80), 1);
            neutral.motion = tree;
            var entries = Read().Entries;
            Assert.That(entries, Has.Count.EqualTo(2)); Assert.That(entries.Select(entry => entry.Error), Is.All.Null);
            Assert.That(entries.Select(entry => entry.Parameters["ExpressionStrength"]), Is.EqualTo(new[] { .5f, 1f }));
            Assert.That(entries.Select(Weight), Is.EqualTo(new[] { 40f, 80f }).Within(.01));
        }

        [Test]
        public void NestedTreeRetainsBothAuthoredControlValues()
        {
            controller.AddParameter("ExpressionMix", AnimatorControllerParameterType.Float);
            controller.AddParameter("ExpressionStrength", AnimatorControllerParameterType.Float);
            var child = new BlendTree { name = "Strength", blendType = BlendTreeType.Simple1D,
                blendParameter = "ExpressionStrength", useAutomaticThresholds = false };
            var root = new BlendTree { name = "Mode", blendType = BlendTreeType.Simple1D,
                blendParameter = "ExpressionMix", useAutomaticThresholds = false };
            AssetDatabase.AddObjectToAsset(child, controller); AssetDatabase.AddObjectToAsset(root, controller);
            child.AddChild(Clip("Weak", 20), 0); child.AddChild(Clip("Strong", 90), 1);
            root.AddChild(Clip("Rest", 0), 0); root.AddChild(child, 1); neutral.motion = root;
            var entries = Read().Entries;
            Assert.That(entries, Has.Count.EqualTo(2)); Assert.That(entries.Select(entry => entry.Error), Is.All.Null);
            Assert.That(entries.All(entry => entry.Parameters["ExpressionMix"] == 1), Is.True);
            Assert.That(entries.Select(Weight), Is.EqualTo(new[] { 20f, 90f }).Within(.01));
        }

        [Test]
        public void NestedTreesSharingAControlRetainInnerKnotsBeyondTheParentRange()
        {
            controller.AddParameter("ExpressionStrength", AnimatorControllerParameterType.Float);
            var child = new BlendTree { name = "Inner faces", blendType = BlendTreeType.Simple1D,
                blendParameter = "ExpressionStrength", useAutomaticThresholds = false };
            var root = new BlendTree { name = "Shared control", blendType = BlendTreeType.Simple1D,
                blendParameter = "ExpressionStrength", useAutomaticThresholds = false };
            AssetDatabase.AddObjectToAsset(child, controller); AssetDatabase.AddObjectToAsset(root, controller);
            child.AddChild(Clip("Smile", 60), 1); child.AddChild(Clip("Angry", 90), 2);
            root.AddChild(Clip("Rest", 0), 0); root.AddChild(child, 1); neutral.motion = root;
            var entries = Read().Entries;
            Assert.That(entries, Has.Count.EqualTo(2)); Assert.That(entries.Select(entry => entry.Error), Is.All.Null);
            Assert.That(entries.Select(entry => entry.Parameters["ExpressionStrength"]), Is.EqualTo(new[] { 1f, 2f }));
            Assert.That(entries.Select(Weight), Is.EqualTo(new[] { 60f, 90f }).Within(.01));
        }

        [Test]
        public void UserSelectionAroundARuntimeControlledInnerTreeUsesTheFixedEnvironment()
        {
            controller.AddParameter("ExpressionStrength", AnimatorControllerParameterType.Float);
            controller.AddParameter("GestureRightWeight", AnimatorControllerParameterType.Float);
            var child = new BlendTree { name = "Runtime strength", blendType = BlendTreeType.Simple1D,
                blendParameter = "GestureRightWeight", useAutomaticThresholds = false };
            var root = new BlendTree { name = "User selection", blendType = BlendTreeType.Simple1D,
                blendParameter = "ExpressionStrength", useAutomaticThresholds = false };
            AssetDatabase.AddObjectToAsset(child, controller); AssetDatabase.AddObjectToAsset(root, controller);
            child.AddChild(Clip("Normal input face", 30), 0); child.AddChild(Clip("Runtime input face", 90), 1);
            root.AddChild(Clip("Rest", 0), 0); root.AddChild(child, 1); neutral.motion = root;
            var entry = Read().Entries.Single();
            Assert.That(entry.Error, Is.Null); Assert.That(entry.Parameters.Keys, Is.EqualTo(new[] { "ExpressionStrength" }));
            Assert.That(Weight(entry), Is.EqualTo(30).Within(.01));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void RuntimeParentKeepsInnerUserControlAtTheFixedEnvironment(bool active)
        {
            controller.AddParameter("ExpressionStrength", AnimatorControllerParameterType.Float);
            controller.AddParameter("GestureRightWeight", AnimatorControllerParameterType.Float);
            var child = new BlendTree { name = "User faces", blendType = BlendTreeType.Simple1D,
                blendParameter = "ExpressionStrength", useAutomaticThresholds = false };
            var root = new BlendTree { name = "Runtime parent", blendType = BlendTreeType.Simple1D,
                blendParameter = "GestureRightWeight", useAutomaticThresholds = false };
            AssetDatabase.AddObjectToAsset(child, controller); AssetDatabase.AddObjectToAsset(root, controller);
            child.AddChild(Clip("Weak face", 20), 0); child.AddChild(Clip("Strong face", 80), 1);
            root.AddChild(active ? (Motion)child : Clip("Runtime rest", 35), 0);
            root.AddChild(active ? Clip("Other runtime face", 35) : (Motion)child, 1); neutral.motion = root;
            var source = Read();
            if (active)
            {
                var entry = source.Entries.Single(); Assert.That(entry.Error, Is.Null);
                Assert.That(entry.Parameters.Keys, Is.EqualTo(new[] { "ExpressionStrength" }));
                Assert.That(entry.Parameters["ExpressionStrength"], Is.EqualTo(1));
                Assert.That(Weight(entry), Is.EqualTo(80).Within(.01));
            }
            else
            {
                Assert.That(source.Entries, Is.Empty);
                Assert.That(source.Messages.Single(), Does.Contain("顔の変化がない"));
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void GeneratedParentKeepsItsAuthoredValueWhileDiscoveringInnerUserControls(bool active)
        {
            controller.AddParameter("ExpressionStrength", AnimatorControllerParameterType.Float);
            controller.AddParameter(new AnimatorControllerParameter { name = "__MA/ActiveSelfProxy/Gate##0",
                type = AnimatorControllerParameterType.Float, defaultFloat = active ? 1 : 0 });
            var child = new BlendTree { name = "User faces", blendType = BlendTreeType.Simple1D,
                blendParameter = "ExpressionStrength", useAutomaticThresholds = false };
            var root = new BlendTree { name = "Generated parent", blendType = BlendTreeType.Simple1D,
                blendParameter = "__MA/ActiveSelfProxy/Gate##0", useAutomaticThresholds = false };
            AssetDatabase.AddObjectToAsset(child, controller); AssetDatabase.AddObjectToAsset(root, controller);
            child.AddChild(Clip("Weak face", 20), 0); child.AddChild(Clip("Strong face", 80), 1);
            root.AddChild(Clip("Inactive face", 35), 0); root.AddChild(child, 1); neutral.motion = root;
            var source = Read();
            if (active)
            {
                var entry = source.Entries.Single(); Assert.That(entry.Error, Is.Null);
                Assert.That(entry.Parameters.Keys, Is.EqualTo(new[] { "ExpressionStrength" }));
                Assert.That(Weight(entry), Is.EqualTo(80).Within(.01));
            }
            else
            {
                Assert.That(source.Entries, Is.Empty);
                Assert.That(source.Messages.Single(), Does.Contain("顔の変化がない"));
            }
        }

        [Test]
        public void DirectTreeRetainsReadonlyWeightsWhileDiscoveringItsNestedUserControl()
        {
            controller.AddParameter("ExpressionStrength", AnimatorControllerParameterType.Float);
            controller.AddParameter(new AnimatorControllerParameter { name = "__MA/ActiveSelfProxy/Gate##0",
                type = AnimatorControllerParameterType.Float, defaultFloat = .5f });
            controller.AddParameter("IsLocal", AnimatorControllerParameterType.Float);
            var child = new BlendTree { name = "User faces", blendType = BlendTreeType.Simple1D,
                blendParameter = "ExpressionStrength", useAutomaticThresholds = false };
            var root = new BlendTree { name = "Mixed readonly direct weights", blendType = BlendTreeType.Direct };
            AssetDatabase.AddObjectToAsset(child, controller); AssetDatabase.AddObjectToAsset(root, controller);
            child.AddChild(Clip("Weak face", 20), 0); child.AddChild(Clip("Strong face", 80), 1);
            root.AddChild(child); root.AddChild(Clip("Constant contribution", 20));
            var children = root.children; children[0].directBlendParameter = "__MA/ActiveSelfProxy/Gate##0";
            children[1].directBlendParameter = "IsLocal"; root.children = children;
            using (var data = new SerializedObject(root))
            { data.FindProperty("m_NormalizedBlendValues").boolValue = false; data.ApplyModifiedPropertiesWithoutUndo(); }
            neutral.motion = root;
            var entry = Read().Entries.Single(); Assert.That(entry.Error, Is.Null);
            Assert.That(entry.Parameters.Keys, Is.EqualTo(new[] { "ExpressionStrength" }));
            Assert.That(Weight(entry), Is.EqualTo(60).Within(.01),
                "The generated half-weight and normal local weight must both remain in native composition.");
        }

        [Test]
        public void TwoDimensionalTreeUsesItsAuthoredCoordinates()
        {
            controller.AddParameter("ExpressionX", AnimatorControllerParameterType.Float);
            controller.AddParameter("ExpressionY", AnimatorControllerParameterType.Float);
            var tree = new BlendTree { name = "2D faces", blendType = BlendTreeType.FreeformCartesian2D,
                blendParameter = "ExpressionX", blendParameterY = "ExpressionY" };
            AssetDatabase.AddObjectToAsset(tree, controller);
            tree.AddChild(Clip("Rest", 0), Vector2.zero); tree.AddChild(Clip("Right", 40), Vector2.right);
            tree.AddChild(Clip("Up", 85), Vector2.up); neutral.motion = tree;
            var entries = Read().Entries;
            Assert.That(entries, Has.Count.EqualTo(2)); Assert.That(entries.Select(entry => entry.Error), Is.All.Null);
            Assert.That(entries.Select(Weight), Is.EqualTo(new[] { 40f, 85f }).Within(.01));
        }

        [Test]
        public void DirectTreeUsesExplicitOneHotControlsInsteadOfFlattenedLeaves()
        {
            controller.AddParameter("ExpressionA", AnimatorControllerParameterType.Float);
            controller.AddParameter("ExpressionB", AnimatorControllerParameterType.Float);
            var tree = new BlendTree { name = "Direct faces", blendType = BlendTreeType.Direct };
            AssetDatabase.AddObjectToAsset(tree, controller);
            tree.AddChild(Clip("Face A", 40)); tree.AddChild(Clip("Face B", 85));
            var children = tree.children; children[0].directBlendParameter = "ExpressionA"; children[1].directBlendParameter = "ExpressionB";
            tree.children = children; neutral.motion = tree;
            var entries = Read().Entries;
            Assert.That(entries, Has.Count.EqualTo(2)); Assert.That(entries.Select(entry => entry.Error), Is.All.Null);
            Assert.That(entries.All(entry => entry.Parameters["ExpressionA"] + entry.Parameters["ExpressionB"] == 1), Is.True);
            Assert.That(entries.Select(Weight), Is.EqualTo(new[] { 40f, 85f }).Within(.01));
        }

        void InvalidateTreeControl(string name, string fault)
        {
            var declarations = controller.parameters;
            if (fault == "missing") controller.RemoveParameter(Array.FindIndex(declarations, value => value.name == name));
            else
            {
                declarations.Single(value => value.name == name).type = (AnimatorControllerParameterType)Enum.Parse(typeof(AnimatorControllerParameterType), fault);
                controller.parameters = declarations;
            }
        }

        void AssertRejectedTreeControl(string name, string fault)
        {
            var authored = new VrChatExpressionMenu.Entry { Id = "menu", Name = "Existing authored face" };
            var source = new VrChatExpressionMenu.Source(); source.Entries.Add(authored); Read(source: source);
            Assert.That(source.Entries.Single(), Is.SameAs(authored), "A malformed inferred tree must not replace authored expressions or publish any arbitrary subset.");
            Assert.That(source.Messages.Single(), Does.Contain("BlendTree").And.Contain(name).And.Contain(fault == "missing" ? "Controllerにありません" : "Float型"));
            Assert.That(source.Entries.SelectMany(value => value.Values), Is.Empty);
        }

        [TestCase(BlendTreeType.Simple1D, "ControlX", "missing")]
        [TestCase(BlendTreeType.Simple1D, "ControlX", "Bool")]
        [TestCase(BlendTreeType.Simple1D, "ControlX", "Int")]
        [TestCase(BlendTreeType.Simple1D, "ControlX", "Trigger")]
        [TestCase(BlendTreeType.SimpleDirectional2D, "ControlY", "missing")]
        [TestCase(BlendTreeType.FreeformDirectional2D, "ControlY", "Int")]
        [TestCase(BlendTreeType.FreeformCartesian2D, "ControlX", "missing")]
        [TestCase(BlendTreeType.FreeformCartesian2D, "ControlY", "missing")]
        [TestCase(BlendTreeType.FreeformCartesian2D, "ControlX", "Int")]
        [TestCase(BlendTreeType.FreeformCartesian2D, "ControlY", "Bool")]
        [TestCase(BlendTreeType.FreeformCartesian2D, "ControlY", "Trigger")]
        [TestCase(BlendTreeType.Direct, "ControlX", "missing")]
        [TestCase(BlendTreeType.Direct, "ControlY", "missing")]
        [TestCase(BlendTreeType.Direct, "ControlY", "Int")]
        public void BlendTreeControlDeclarationsAreRequiredBeforeDiscovery(BlendTreeType kind, string brokenControl, string fault)
        {
            controller.AddParameter("ControlX", AnimatorControllerParameterType.Float); controller.AddParameter("ControlY", AnimatorControllerParameterType.Float);
            var tree = new BlendTree { name = "Authored native face controls", blendType = kind, blendParameter = "ControlX",
                blendParameterY = "ControlY", useAutomaticThresholds = false }; AssetDatabase.AddObjectToAsset(tree, controller);
            if (kind == BlendTreeType.Simple1D) { tree.AddChild(Clip("Rest", 0), 0); tree.AddChild(Clip("Face", 80), 1); }
            else if (kind == BlendTreeType.Direct)
            {
                tree.AddChild(Clip("Rest", 0)); tree.AddChild(Clip("Face", 80)); var children = tree.children;
                children[0].directBlendParameter = "ControlX"; children[1].directBlendParameter = "ControlY"; tree.children = children;
            }
            else { tree.AddChild(Clip("Rest", 0), Vector2.zero); tree.AddChild(Clip("Right", 40), Vector2.right); tree.AddChild(Clip("Face", 80), Vector2.up); }
            neutral.motion = tree;
            // Prove the authored knot in the original complete native tree,
            // then reproduce deletion/type replacement in the actual source
            // controller. The malformed graph must never reach native sampling.
            var native = NativeSelection(new Dictionary<string, float> { ["ControlX"] = kind == BlendTreeType.Simple1D ? 1 : 0, ["ControlY"] = 1 },
                checkStability: true);
            Assert.That(native.Hash, Is.EqualTo(Animator.StringToHash(controller.layers[0].name + "." + neutral.name)));
            Assert.That(native.Weight, Is.EqualTo(80).Within(.01));
            InvalidateTreeControl(brokenControl, fault); AssertRejectedTreeControl(brokenControl, fault);
        }

        [TestCase(false, "missing")]
        [TestCase(false, "Int")]
        [TestCase(true, "missing")]
        [TestCase(true, "Bool")]
        public void NestedTreeRequiresBothUserAndReadonlyControlDeclarations(bool readonlyOuter, string fault)
        {
            var outerControl = "__MA/ActiveSelfProxy/Gate##0"; Parameter(outerControl, AnimatorControllerParameterType.Float, 1);
            controller.AddParameter("UserFace", AnimatorControllerParameterType.Float);
            var inner = new BlendTree { name = "Nested user face", blendType = BlendTreeType.Simple1D,
                blendParameter = "UserFace", useAutomaticThresholds = false }; AssetDatabase.AddObjectToAsset(inner, controller);
            inner.AddChild(Clip("Nested rest", 0), 0); inner.AddChild(Clip("Nested selected face", 80), 1);
            var outer = new BlendTree { name = "Readonly generated parent", blendType = BlendTreeType.Simple1D,
                blendParameter = outerControl, useAutomaticThresholds = false }; AssetDatabase.AddObjectToAsset(outer, controller);
            outer.AddChild(Clip("Inactive", 0), 0); outer.AddChild(inner, 1); neutral.motion = outer;
            var native = NativeSelection(new Dictionary<string, float> { ["UserFace"] = 1 }, checkStability: true);
            Assert.That(native.Weight, Is.EqualTo(80).Within(.01));
            var brokenControl = readonlyOuter ? outerControl : "UserFace";
            InvalidateTreeControl(brokenControl, fault); AssertRejectedTreeControl(brokenControl, fault);
        }

        [Test]
        public void StateEntryDriverKeepsItsCallbackDependentNativeLayerComposition()
        {
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Int);
            controller.AddParameter("LowerChoice", AnimatorControllerParameterType.Int);
            neutral.motion = Clip("Lower neutral", 30);
            var lower = State(controller.layers[0].stateMachine, "Lower alternative", Clip("Lower alternative", 60));
            Transition(neutral, lower, "LowerChoice", 1);
            controller.AddLayer("Expressions"); var layers = controller.layers; layers[1].defaultWeight = .5f; controller.layers = layers;
            var idle = State(layers[1].stateMachine, "Player neutral", Clip("Player neutral", 30)); layers[1].stateMachine.defaultState = idle;
            var selected = State(layers[1].stateMachine, "Callback smile", Clip("Callback smile", 80));
            Transition(idle, selected, "FaceChoice", 1);
            ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "LowerChoice", 1));
            var entry = Read().Entries.Single(value => value.Name == "FX / Callback smile");
            Assert.That(entry.Error, Is.Null); Assert.That(Weight(entry), Is.EqualTo(70).Within(.01));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DriverOnlyRootControlKeepsCompleteNativeFaceWithoutSelectingItsInternalOutputs(bool relay)
        {
            controller.AddParameter("FaceMode", AnimatorControllerParameterType.Int);
            controller.AddParameter("MouthChoice", AnimatorControllerParameterType.Int);
            controller.AddParameter("EyelidChoice", AnimatorControllerParameterType.Int);
            neutral.motion = null;
            var selected = State(controller.layers[0].stateMachine, "Authored whole face", null);
            Transition(neutral, selected, "FaceMode", 1);
            var producer = selected;
            if (relay)
            {
                controller.AddParameter("FaceRelay", AnimatorControllerParameterType.Int);
                ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "FaceRelay", 1));
                controller.AddLayer("Relay"); var layers = controller.layers; layers[layers.Length - 1].defaultWeight = 1; controller.layers = layers;
                var machine = layers[layers.Length - 1].stateMachine;
                var idle = State(machine, "Relay idle", null); machine.defaultState = idle;
                producer = State(machine, "Relay whole face", null); Transition(idle, producer, "FaceRelay", 1);
            }
            ParameterDriverExpressionTests.Driver(producer, ParameterDriverExpressionTests.Op("Set", "MouthChoice", 1),
                ParameterDriverExpressionTests.Op("Set", "EyelidChoice", 1));
            controller.AddLayer("Mouth"); controller.AddLayer("Eyelids");
            var outputs = controller.layers; outputs[outputs.Length - 2].defaultWeight = 1; outputs[outputs.Length - 1].defaultWeight = 1;
            controller.layers = outputs;
            var mouth = outputs[outputs.Length - 2].stateMachine;
            var mouthRest = State(mouth, "Mouth rest", Clip("Mouth rest", 0)); mouth.defaultState = mouthRest;
            Transition(mouthRest, State(mouth, "Selected mouth", Clip("Selected mouth", 75)), "MouthChoice", 1);
            AnimationClip EyelidClip(string name, float weight)
            {
                var clip = new AnimationClip { name = name };
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink"),
                    AnimationCurve.Constant(0, 1, weight)); AssetDatabase.AddObjectToAsset(clip, controller); return clip;
            }
            var eyelids = outputs[outputs.Length - 1].stateMachine;
            var eyelidRest = State(eyelids, "Eyelid rest", EyelidClip("Eyelid rest", 0)); eyelids.defaultState = eyelidRest;
            Transition(eyelidRest, State(eyelids, "Selected eyelids", EyelidClip("Selected eyelids", 100)), "EyelidChoice", 1);
            var entry = Read().Entries.Single(); Assert.That(entry.Error, Is.Null);
            Assert.That(entry.Name, Is.EqualTo("FX / Authored whole face"));
            Assert.That(entry.Parameters.Keys, Is.EqualTo(new[] { "FaceMode" }));
            Assert.That(Weight(entry), Is.EqualTo(75).Within(.01));
            Assert.That(entry.Values.Single(value => value.Shape == "Blink").Weight, Is.EqualTo(100).Within(.01));
            Assert.That(entry.Values, Has.Count.EqualTo(2), "Internal mouth/eyelid/relay values must never become partial inferred selections.");
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(1), Is.EqualTo(0));
        }

        [Test]
        public void ExplicitlyDeclaredDriverOutputRemainsAnAuthoredInput()
        {
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Int);
            var writer = State(controller.layers[0].stateMachine, "Unselected producer", null);
            ParameterDriverExpressionTests.Driver(writer, ParameterDriverExpressionTests.Op("Set", "FaceChoice", 0));
            var face = State(controller.layers[0].stateMachine, "Declared user face", Clip("Smile", 75));
            Transition(neutral, face, "FaceChoice", 1);
            var source = new VrChatExpressionMenu.Source { Controller = controller };
            source.ExpressionParameters.Add("FaceChoice"); VrChatFxExpressions.Add(avatar, source);
            var entry = source.Entries.Single(); Assert.That(entry.Error, Is.Null);
            Assert.That(entry.Parameters["FaceChoice"], Is.EqualTo(1)); Assert.That(Weight(entry), Is.EqualTo(75).Within(.01));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AnimatorCurveRelayKeepsTheAuthoredRootControlAndNativeOverrideOutput(bool overrideClip)
        {
            controller.AddParameter("ExpressionStrength", AnimatorControllerParameterType.Float);
            controller.AddParameter("__MA/ActiveSelfProxy/Gate##0", AnimatorControllerParameterType.Float);
            AnimationClip Output(string name, float value)
            {
                var clip = new AnimationClip { name = name };
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "__MA/ActiveSelfProxy/Gate##0"),
                    AnimationCurve.Constant(0, 1, value)); AssetDatabase.AddObjectToAsset(clip, controller); return clip;
            }
            var tree = new BlendTree { name = "Authored activation slider", blendType = BlendTreeType.Simple1D,
                blendParameter = "ExpressionStrength", useAutomaticThresholds = false }; AssetDatabase.AddObjectToAsset(tree, controller);
            tree.AddChild(Output("Inactive relay", 0), 0);
            var selectedOutput = new AnimationClip { name = "Authored output placeholder" }; AssetDatabase.AddObjectToAsset(selectedOutput, controller);
            var actual = Output("Active relay", 1); tree.AddChild(overrideClip ? selectedOutput : actual, 1); neutral.motion = tree;
            controller.AddLayer("Reactive face"); var layers = controller.layers; layers[1].defaultWeight = 1; controller.layers = layers;
            var idle = State(layers[1].stateMachine, "Reactive neutral", Clip("Reactive neutral", 0)); layers[1].stateMachine.defaultState = idle;
            Transition(idle, State(layers[1].stateMachine, "Reactive smile", Clip("Reactive smile", 85)),
                "__MA/ActiveSelfProxy/Gate##0", .5f, AnimatorConditionMode.Greater);
            var runtime = overrideClip ? new AnimatorOverrideController(controller) : null;
            try
            {
                if (runtime != null) runtime[selectedOutput] = actual;
                var entry = Read(runtime).Entries.Single(); Assert.That(entry.Error, Is.Null);
                Assert.That(entry.Parameters.Keys, Is.EqualTo(new[] { "ExpressionStrength" }));
                Assert.That(entry.Parameters["ExpressionStrength"], Is.EqualTo(1));
                Assert.That(Weight(entry), Is.EqualTo(85).Within(.01));
            }
            finally { if (runtime != null) Object.DestroyImmediate(runtime); }
        }

        [Test]
        public void CurveOnlyStateCandidateUsesTheEffectiveOverrideBindings()
        {
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Int);
            controller.AddParameter("Internal face output", AnimatorControllerParameterType.Float);
            neutral.motion = null;
            var placeholder = new AnimationClip { name = "Source placeholder" }; AssetDatabase.AddObjectToAsset(placeholder, controller);
            var actual = new AnimationClip { name = "Actual relay" };
            AnimationUtility.SetEditorCurve(actual, EditorCurveBinding.FloatCurve("", typeof(Animator), "Internal face output"),
                AnimationCurve.Constant(0, 1, 1)); AssetDatabase.AddObjectToAsset(actual, controller);
            var selected = State(controller.layers[0].stateMachine, "Author-selected relay", placeholder);
            Transition(neutral, selected, "FaceChoice", 1);
            controller.AddLayer("Face output"); var layers = controller.layers; layers[1].defaultWeight = 1; controller.layers = layers;
            var idle = State(layers[1].stateMachine, "Output neutral", Clip("Output neutral", 0)); layers[1].stateMachine.defaultState = idle;
            Transition(idle, State(layers[1].stateMachine, "Output smile", Clip("Output smile", 75)),
                "Internal face output", .5f, AnimatorConditionMode.Greater);
            var runtime = new AnimatorOverrideController(controller);
            try
            {
                runtime[placeholder] = actual;
                var entry = Read(runtime).Entries.Single(); Assert.That(entry.Error, Is.Null);
                Assert.That(entry.Name, Is.EqualTo("FX / Author-selected relay"));
                Assert.That(entry.Parameters.Keys, Is.EqualTo(new[] { "FaceChoice" }));
                Assert.That(Weight(entry), Is.EqualTo(75).Within(.01));
            }
            finally { Object.DestroyImmediate(runtime); }
        }

        [Test]
        public void ConjoinedFloatConditionsUseAValueInsideTheAuthoredRange()
        {
            controller.AddParameter("FaceStrength", AnimatorControllerParameterType.Float);
            var selected = State(controller.layers[0].stateMachine, "Smile", Clip("Smile", 75));
            var transition = Transition(neutral, selected, "FaceStrength", .3f, AnimatorConditionMode.Greater);
            transition.AddCondition(AnimatorConditionMode.Less, .7f, "FaceStrength");
            var entry = Read().Entries.Single();
            Assert.That(entry.Error, Is.Null); Assert.That(entry.Parameters["FaceStrength"], Is.GreaterThan(.3f).And.LessThan(.7f));
            Assert.That(Weight(entry), Is.EqualTo(75).Within(.01));
        }

        [Test]
        public void MutedAndImpossibleConditionsCannotCreateUnreachableCandidates()
        {
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Int);
            var face = State(controller.layers[0].stateMachine, "Face", Clip("Face", 75));
            Transition(neutral, face, "FaceChoice", 1).mute = true;
            var impossible = Transition(neutral, face, "FaceChoice", 2);
            impossible.AddCondition(AnimatorConditionMode.Equals, 3, "FaceChoice");
            Assert.That(Read().Entries, Is.Empty);
        }

        [Test]
        public void TriggerDrivenFaceKeepsAnExplicitCapabilityDiagnostic()
        {
            controller.AddParameter("ChooseFace", AnimatorControllerParameterType.Trigger);
            var selected = State(controller.layers[0].stateMachine, "Triggered face", Clip("Triggered face", 75));
            Transition(neutral, selected, "ChooseFace", 0, AnimatorConditionMode.If);
            var entry = Read().Entries.Single();
            Assert.That(entry.Error, Does.Contain("Trigger").And.Contain("ChooseFace")); Assert.That(entry.Values, Is.Empty);
        }

        [Test]
        public void InferredCandidateBudgetCannotRemoveAuthoredExpressionsOrPublishAPartialSubset()
        {
            controller.AddParameter("ExpressionStrength", AnimatorControllerParameterType.Float);
            var tree = new BlendTree { name = "Many inference points", blendType = BlendTreeType.Simple1D,
                blendParameter = "ExpressionStrength", useAutomaticThresholds = false };
            AssetDatabase.AddObjectToAsset(tree, controller);
            var clip = Clip("Shared face", 75);
            tree.children = Enumerable.Range(0, 260).Select(index => new ChildMotion { motion = clip, threshold = index, timeScale = 1 }).ToArray();
            neutral.motion = tree;
            var source = new VrChatExpressionMenu.Source { Controller = controller };
            var authored = new VrChatExpressionMenu.Entry { Id = "menu", Name = "Existing authored expression" }; source.Entries.Add(authored);
            Assert.DoesNotThrow(() => VrChatFxExpressions.Add(avatar, source));
            Assert.That(source.Entries.Single(), Is.SameAs(authored));
            Assert.That(source.Messages.Single(), Does.Contain("自動表情探索").And.Contain("256"));
        }

        [Test]
        public void DeepConditionChainReportsAnAtomicOptionalDiscoveryDiagnostic()
        {
            controller.AddParameter("FaceMode", AnimatorControllerParameterType.Bool);
            var previous = neutral;
            for (var index = 0; index < 17; index++)
            {
                var gate = State(controller.layers[0].stateMachine, "Gate " + index, null);
                Transition(previous, gate, "FaceMode", 0, AnimatorConditionMode.If); previous = gate;
            }
            var face = State(controller.layers[0].stateMachine, "Deep face", Clip("Deep face", 75));
            Transition(previous, face, "FaceMode", 0, AnimatorConditionMode.If);
            var source = new VrChatExpressionMenu.Source { Controller = controller };
            var authored = new VrChatExpressionMenu.Entry { Id = "menu", Name = "Existing authored expression" }; source.Entries.Add(authored);
            Assert.DoesNotThrow(() => VrChatFxExpressions.Add(avatar, source));
            Assert.That(source.Entries.Single(), Is.SameAs(authored));
            Assert.That(source.Messages.Single(), Does.Contain("条件経路").And.Contain("深すぎる"));
        }

        [Test]
        public void OverrideControllerAndFractionalNativeLayerKeepTheWholeFace()
        {
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Int);
            neutral.motion = Clip("Lower face", 30);
            controller.AddLayer("Expression layer"); var layers = controller.layers; layers[1].defaultWeight = .5f; controller.layers = layers;
            var idle = State(layers[1].stateMachine, "Idle", Clip("Idle", 30)); layers[1].stateMachine.defaultState = idle;
            var clip = Clip("Authored smile", 80); var selected = State(layers[1].stateMachine, "Smile", clip);
            Transition(idle, selected, "FaceChoice", 1);
            var runtime = new AnimatorOverrideController(controller);
            try
            {
                runtime[clip] = Clip("Actual smile", 90); var before = EditorJsonUtility.ToJson(runtime);
                var entry = Read(runtime).Entries.Single(); Assert.That(entry.Error, Is.Null);
                Assert.That(Weight(entry), Is.EqualTo(60).Within(.01));
                Assert.That(EditorJsonUtility.ToJson(runtime), Is.EqualTo(before));
            }
            finally { Object.DestroyImmediate(runtime); }
        }

        [TestCase("delayed exit")]
        [TestCase("moving curve")]
        [TestCase("visibility")]
        public void IncompatibleGraphReportsCandidateInsteadOfPublishingAnIsolatedClip(string kind)
        {
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Int);
            var clip = Clip("Smile", 75); var selected = State(controller.layers[0].stateMachine, "Smile", clip);
            Transition(neutral, selected, "FaceChoice", 1);
            if (kind == "delayed exit")
            {
                var exit = selected.AddTransition(neutral); exit.hasExitTime = true; exit.exitTime = 10; exit.duration = 0;
            }
            else AnimationUtility.SetEditorCurve(clip,
                kind == "moving curve" ? EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size") :
                    EditorCurveBinding.FloatCurve("Face", typeof(GameObject), "m_IsActive"),
                kind == "moving curve" ? AnimationCurve.Linear(0, 75, 30, 100) : AnimationCurve.Constant(0, 1, 0));
            var entries = Read().Entries;
            var entry = entries.Single(value => value.Name == "FX / Smile");
            Assert.That(entry.Error, Is.Not.Null.And.Not.Empty); Assert.That(entry.Values, Is.Empty);
            Assert.That(entries.Where(value => value.Error == null), Is.Empty,
                "Neither the authored face nor its automatic timed destination may become an isolated partial export.");
        }

        [Test]
        public void PriorityThatSelectsAnotherStateCannotMislabelTheCapturedFace()
        {
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Int);
            var root = controller.layers[0].stateMachine;
            var first = State(root, "Higher priority", Clip("Actual face", 20));
            var second = State(root, "Lower priority", Clip("Wrong face", 90));
            foreach (var state in new[] { first, second })
            {
                Transition(neutral, state, "FaceChoice", 1);
            }
            var entries = Read().Entries;
            var selected = entries.Single(); Assert.That(selected.Error, Is.Null);
            Assert.That(selected.Name, Is.EqualTo("FX / Higher priority"));
            Assert.That(Weight(selected), Is.EqualTo(20).Within(.01));
        }

        [Test]
        public void ExistingMenuOutcomeKeepsItsAuthoredLabelAndRecoveryDoesNotReintroduceMenuControls()
        {
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Int);
            var selected = State(controller.layers[0].stateMachine, "Smile", Clip("Smile", 75)); Transition(neutral, selected, "FaceChoice", 1);
            var source = new VrChatExpressionMenu.Source { Controller = controller, NeutralInputInventoryComplete = true };
            var menu = new VrChatExpressionMenu.Entry { Id = "menu", Name = "My menu label" }; menu.Parameters["FaceChoice"] = 1;
            menu.Values.AddRange(VrChatExpressionSampler.SampleFixed(avatar, controller, source.Defaults, menu.Parameters, metadata: source));
            source.Entries.Add(menu); VrChatFxExpressions.Add(avatar, source);
            Assert.That(source.Entries.Single(), Is.SameAs(menu));
            source.Entries.Clear(); source.MenuInputs.Add("FaceChoice");
            VrChatFxExpressions.Add(avatar, source, policy: new VrChatMenuImportPolicy { SkipAll = true });
            Assert.That(source.Entries, Is.Empty);
        }

        [Test]
        public void RecoveryWithUnknownMenuInventoryCannotInferPotentiallyExcludedControls()
        {
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Int);
            var selected = State(controller.layers[0].stateMachine, "Smile", Clip("Smile", 75)); Transition(neutral, selected, "FaceChoice", 1);
            var source = new VrChatExpressionMenu.Source { Controller = controller };
            VrChatFxExpressions.Add(avatar, source, policy: new VrChatMenuImportPolicy { SkipAll = true });
            Assert.That(source.Entries, Is.Empty);
            Assert.That(source.Messages.Single(), Does.Contain("除外範囲").And.Contain("省略"));
        }

        [Test]
        public void GestureAutomaticBlinkAndTrackingChannelsAreNotFloodedWithGenericCandidates()
        {
            controller.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
            controller.AddParameter("UE/MouthSmileLeft", AnimatorControllerParameterType.Float);
            var smile = State(controller.layers[0].stateMachine, "Gesture", Clip("Smile", 75)); Transition(neutral, smile, "GestureRight", 2);
            var blink = State(controller.layers[0].stateMachine, "Automatic blink", Clip("Blink", 100));
            var timed = neutral.AddTransition(blink); timed.hasExitTime = true; timed.exitTime = 10;
            var tree = new BlendTree { name = "Tracking", blendType = BlendTreeType.Simple1D, blendParameter = "UE/MouthSmileLeft" };
            AssetDatabase.AddObjectToAsset(tree, controller); tree.AddChild(Clip("Tracking rest", 0), 0); tree.AddChild(Clip("Tracked face", 100), 1);
            State(controller.layers[0].stateMachine, "Tracking", tree);
            Assert.That(Read().Entries, Is.Empty);
        }

        [TestCase("__MA/ActiveSelfProxy/Activation##0", false)]
        [TestCase("__ActiveSelfProxy/42", false)]
        [TestCase("Internal authored signal", true)]
        public void InternalActivationAndAnimatorWrittenSignalsCannotBecomeIndependentUserSelections(string parameter, bool written)
        {
            controller.AddParameter(parameter, AnimatorControllerParameterType.Float);
            if (written)
                AnimationUtility.SetEditorCurve((AnimationClip)neutral.motion, EditorCurveBinding.FloatCurve("", typeof(Animator), parameter),
                    AnimationCurve.Constant(0, 1, 0));
            var selected = State(controller.layers[0].stateMachine, "Internal face", Clip("Internal face", 100));
            Transition(neutral, selected, parameter, .5f, AnimatorConditionMode.Greater);
            Assert.That(Read().Entries, Is.Empty, "Inferred export must select actual user inputs instead of bypassing the authored signal producer.");
        }

        StateMachineBehaviour InstalledMaBuildMarker(bool disableInMmd)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly =>
                assembly.GetType("nadena.dev.modular_avatar.core.ModularAvatarMMDLayerControl")).FirstOrDefault(value => value != null);
            if (type == null) Assert.Ignore("Install the real Modular Avatar package to validate its generated data-only build marker.");
            var marker = (StateMachineBehaviour)ScriptableObject.CreateInstance(type);
            AssetDatabase.AddObjectToAsset(marker, controller);
            using (var data = new SerializedObject(marker))
            {
                data.FindProperty("m_DisableInMMDMode").boolValue = disableInMmd; data.ApplyModifiedPropertiesWithoutUndo();
            }
            Assert.That(ExpressionDependencies.IsInertAuthoringMarker(marker), Is.True,
                "The installed MA marker must match the exact sealed, callback-free field/property contract.");
            return marker;
        }

        [TestCase("machine", false)]
        [TestCase("machine", true)]
        [TestCase("state", false)]
        [TestCase("state", true)]
        public void InstalledMaDataOnlyMarkerPreservesNativeAndDirectExpressionSampling(string placement, bool disableInMmd)
        {
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Int);
            var root = controller.layers[0].stateMachine;
            var selected = State(root, "Smile", Clip("Smile", 75)); Transition(neutral, selected, "FaceChoice", 1);
            var marker = InstalledMaBuildMarker(disableInMmd);
            if (placement == "machine") root.behaviours = new[] { marker }; else selected.behaviours = new[] { marker };
            var markerJson = EditorJsonUtility.ToJson(marker); var controllerJson = EditorJsonUtility.ToJson(controller);
            var entry = Read().Entries.Single(); Assert.That(entry.Error, Is.Null); Assert.That(Weight(entry), Is.EqualTo(75).Within(.01));
            var direct = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, direct);
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, direct, 0, sourceState: selected);
            Assert.That(Weight(direct), Is.EqualTo(75).Within(.01));
            Assert.That(EditorJsonUtility.ToJson(marker), Is.EqualTo(markerJson));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(controllerJson));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(25));
        }

        [Test]
        public void InstalledMaMarkerCannotMakeAnUnknownCallbackSafe()
        {
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Int);
            var root = controller.layers[0].stateMachine;
            var selected = State(root, "Smile", Clip("Smile", 75)); Transition(neutral, selected, "FaceChoice", 1);
            var marker = InstalledMaBuildMarker(false);
            var callback = selected.AddStateMachineBehaviour<UnknownStateCallbackProbe>();
            Assert.That(ExpressionDependencies.IsInertAuthoringMarker(callback), Is.False);
            root.behaviours = new[] { marker };
            var entry = Read().Entries.Single();
            Assert.That(entry.Error, Does.Contain("UnknownStateCallbackProbe")); Assert.That(entry.Values, Is.Empty);
            var direct = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, direct);
            Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, direct, 0, sourceState: selected));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NativeProcessedControllerCanRetainAFiniteTransformCurveWithNoTarget(bool changing)
        {
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Int);
            var clip = Clip("Smile with unbound authoring curve", 75);
            var binding = EditorCurveBinding.FloatCurve("Absent processed transform", typeof(Transform), "m_LocalPosition.x");
            AnimationUtility.SetEditorCurve(clip, binding, changing ? AnimationCurve.Linear(0, 0, 1, 1) : AnimationCurve.Constant(0, 1, 0));
            var selected = State(controller.layers[0].stateMachine, "Smile", clip); Transition(neutral, selected, "FaceChoice", 1);
            Assert.That(AnimationUtility.GetAnimatedObject(avatar, binding), Is.Null);
            Assert.That(SelectedExpressionAppearance.IsUnboundTransform(avatar, clip, binding), Is.True);
            var entry = Read().Entries.Single(); Assert.That(entry.Error, Is.Null); Assert.That(Weight(entry), Is.EqualTo(75).Within(.01));
            Assert.Throws<InvalidOperationException>(() => VrChatGestureExpressions.ReadClip(avatar, clip, new VrChatExpressionMenu.Entry()),
                "An authoring-source direct clip must not assume a still-missing path cannot be created during preparation.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TheSameTransformCurveOnALiveOrInactiveTargetStillRejectsTheNativeExpression(bool inactive)
        {
            var target = new GameObject("Actual processed transform"); target.transform.SetParent(avatar.transform, false);
            target.SetActive(!inactive);
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Int);
            var clip = Clip("Smile with actual transform curve", 75);
            var binding = EditorCurveBinding.FloatCurve("Actual processed transform", typeof(Transform), "m_LocalPosition.x");
            AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, 1, 0));
            var selected = State(controller.layers[0].stateMachine, "Smile", clip); Transition(neutral, selected, "FaceChoice", 1);
            Assert.That(SelectedExpressionAppearance.IsUnboundTransform(avatar, clip, binding), Is.False);
            var entry = Read().Entries.Single(); Assert.That(entry.Error, Is.Not.Null); Assert.That(entry.Values, Is.Empty);
            Assert.That(target.transform.localPosition, Is.EqualTo(Vector3.zero));
        }
    }
}
