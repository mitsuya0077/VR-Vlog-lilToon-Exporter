using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
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
        VrChatExpressionMenu.Source Read(RuntimeAnimatorController runtime = null)
        {
            var before = AssetDatabase.LoadAllAssetsAtPath(folder + "/FX.controller")
                .ToDictionary(asset => asset, asset => EditorJsonUtility.ToJson(asset));
            var metadata = new VrChatExpressionMenu.Source { Controller = runtime ?? controller };
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
            var rejected = entries.Single(entry => entry.Name == "FX / Lower priority");
            Assert.That(rejected.Error, Does.Contain("状態に到達")); Assert.That(rejected.Values, Is.Empty);
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
