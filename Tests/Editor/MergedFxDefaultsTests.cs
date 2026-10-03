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
    // These tests execute Unity's real layer blending and Write Defaults.
    // A host-only source check cannot replace running them in the Editor.
    public sealed class MergedFxDefaultsTests
    {
        string folder;
        GameObject avatar;
        Mesh mesh;
        AnimatorController controller;

        [SetUp]
        public void SetUp()
        {
            var name = "__MergedFxDefaults_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            controller.AddParameter("Menu", AnimatorControllerParameterType.Int);
            avatar = new GameObject("Avatar", typeof(Animator));
            var face = new GameObject("Face", typeof(SkinnedMeshRenderer)); face.transform.SetParent(avatar.transform, false);
            mesh = BaseShapeFixture.Create();
            var zeros = new Vector3[mesh.vertexCount];
            var delta = new Vector3[mesh.vertexCount]; delta[0] = Vector3.up;
            mesh.AddBlendShapeFrame("Pupil removal", 100, delta, zeros, zeros);
            face.GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(avatar); Object.DestroyImmediate(mesh); AssetDatabase.DeleteAsset(folder);
        }

        AnimationClip Clip(string name, params (string Shape, float Value)[] shapes)
        {
            var clip = new AnimationClip { name = name };
            foreach (var shape in shapes)
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape." + shape.Shape),
                    AnimationCurve.Constant(0, 1, shape.Value));
            AssetDatabase.AddObjectToAsset(clip, controller); return clip;
        }

        AnimatorState State(AnimatorStateMachine machine, string name, AnimationClip clip, bool writeDefaults = false)
        { var state = machine.AddState(name); state.motion = clip; state.writeDefaultValues = writeDefaults; return state; }

        AnimatorStateMachine AddLayer(string name)
        {
            controller.AddLayer(name); var layers = controller.layers;
            layers[layers.Length - 1].defaultWeight = 1; controller.layers = layers;
            return layers[layers.Length - 1].stateMachine;
        }

        void Menu(AnimatorStateMachine machine, bool writesPupil = false, bool writeDefaults = false)
        {
            var idle = State(machine, "Idle", writesPupil ? Clip("Idle", ("Face size", 0), ("Pupil removal", 0)) : Clip("Idle", ("Face size", 0)), writeDefaults);
            var selected = State(machine, "Selected", writesPupil ? Clip("Selected", ("Face size", 75), ("Pupil removal", 0)) : Clip("Selected", ("Face size", 75)), writeDefaults);
            machine.defaultState = idle;
            var transition = idle.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "Menu");
        }

        void Permanent(AnimatorStateMachine machine, bool writeDefaults = false)
        { machine.defaultState = State(machine, "Always", Clip("Always remove pupil", ("Pupil removal", 100)), writeDefaults); }

        static Dictionary<string, float> Parameters(int value) => new Dictionary<string, float> { ["Menu"] = value };

        [TestCase(false)]
        [TestCase(true)]
        public void ConstantIndependentEffectAppliesToNeutralAndSelectedExpression(bool writeDefaults)
        {
            Menu(controller.layers[0].stateMachine, writeDefaults: writeDefaults);
            Permanent(AddLayer("Permanent pupil override"), writeDefaults);
            var neutral = VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0));
            var selected = VrChatExpressionSampler.Sample(avatar, controller, Parameters(0), Parameters(1));
            Assert.That(neutral.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(100).Within(.01));
            Assert.That(selected.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(100).Within(.01));
            Assert.That(selected.Single(value => value.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.Zero);
            Assert.That(mesh.blendShapeCount, Is.EqualTo(3));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void PermanentEffectUsesAuthoredLayerPriority(bool permanentLast, bool writeDefaults)
        {
            if (permanentLast) { Menu(controller.layers[0].stateMachine, true, writeDefaults); Permanent(AddLayer("Permanent"), writeDefaults); }
            else { Permanent(controller.layers[0].stateMachine, writeDefaults); Menu(AddLayer("Menu"), true, writeDefaults); }
            var selected = VrChatExpressionSampler.Sample(avatar, controller, Parameters(0), Parameters(1));
            Assert.That(selected.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(permanentLast ? 100 : 0).Within(.01));
        }

        [Test]
        public void UnrelatedGestureLayerDoesNotBecomeANeutralSamplingRoot()
        {
            Menu(controller.layers[0].stateMachine);
            Permanent(AddLayer("Permanent"));
            controller.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
            var machine = AddLayer("Unrelated gesture");
            var idle = State(machine, "Idle", Clip("Gesture neutral", ("Blink", 0))); machine.defaultState = idle;
            var gesture = State(machine, "Gesture", Clip("Gesture eye", ("Blink", 100)));
            var transition = idle.AddTransition(gesture); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 2, "GestureRight");
            var seeds = ExpressionDependencies.StationaryMorphBindings(controller);
            Assert.That(seeds.Select(binding => binding.propertyName), Is.EquivalentTo(new[] { "blendShape.Pupil removal" }));
            var neutral = VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0));
            Assert.That(neutral.Select(value => value.Shape), Is.EquivalentTo(new[] { "Pupil removal" }));
            var selected = VrChatExpressionSampler.Sample(avatar, controller, Parameters(0), Parameters(1));
            Assert.That(selected.Any(value => value.Shape == "Blink"), Is.False);
        }

        [Test]
        public void TimeVaryingDefaultLayerIsNotDeclaredPermanent()
        {
            Permanent(controller.layers[0].stateMachine);
            var clip = (AnimationClip)controller.layers[0].stateMachine.defaultState.motion;
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"),
                AnimationCurve.Linear(0, 0, 1, 100));
            Assert.That(VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0)), Is.Empty);
        }

        [Test]
        public void OrdinaryMenuDoesNotNeedAStationaryLayer()
        {
            Menu(controller.layers[0].stateMachine);
            Assert.That(VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0)), Is.Empty);
            var selected = VrChatExpressionSampler.Sample(avatar, controller, Parameters(0), Parameters(1));
            Assert.That(selected.Single().Shape, Is.EqualTo("Face size"));
            Assert.That(selected.Single().Weight, Is.EqualTo(75).Within(.01));
        }

        [Test]
        public void PermanentOverrideSuppressesPupilAnimationWithoutLosingOtherAnimatedChannels()
        {
            Menu(controller.layers[0].stateMachine, true);
            Permanent(AddLayer("Permanent"));
            var clip = (AnimationClip)controller.layers[0].stateMachine.states.Single(item => item.state.name == "Selected").state.motion;
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"), AnimationCurve.Linear(0, 0, 1, 80));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"), AnimationCurve.Linear(0, 0, 1, 75));
            var entry = new VrChatExpressionMenu.Entry();
            VrChatGestureExpressions.ReadClip(avatar, clip, entry);
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 0);
            Assert.That(entry.Values.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(100).Within(.01));
            Assert.That(entry.Animation.Select(animation => animation.Shape), Is.EquivalentTo(new[] { "Face size" }));
            Assert.That(entry.Animation.Single().Curve.Evaluate(.5), Is.EqualTo(37.5).Within(.01));
            Assert.That(AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Pupil removal")).Evaluate(.5f), Is.EqualTo(40).Within(.01),
                "The source clip is never rewritten by the overlay probes.");
        }

        [Test]
        public void DirectAnimatedClipRetainsNativeLayerWeightAndPriority()
        {
            Permanent(controller.layers[0].stateMachine);
            var machine = AddLayer("Weighted expression"); Menu(machine, true);
            var layers = controller.layers; layers[1].defaultWeight = .5f; controller.layers = layers;
            var clip = (AnimationClip)machine.states.Single(item => item.state.name == "Selected").state.motion;
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"), AnimationCurve.Linear(0, 0, 1, 80));
            var entry = new VrChatExpressionMenu.Entry();
            VrChatGestureExpressions.ReadClip(avatar, clip, entry);
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1);
            Assert.That(entry.Values.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(50).Within(.01));
            var curve = entry.Animation.Single(animation => animation.Shape == "Pupil removal").Curve;
            Assert.That(curve.Evaluate(0), Is.EqualTo(50).Within(.01));
            Assert.That(curve.Evaluate(1), Is.EqualTo(90).Within(.01));
        }

        [Test]
        public void StandaloneRegisteredClipKeepsPermanentFxAboveItsBasePose()
        {
            Permanent(controller.layers[0].stateMachine);
            var clip = Clip("Registered expression", ("Face size", 75), ("Pupil removal", 0));
            var entry = new VrChatExpressionMenu.Entry();
            VrChatGestureExpressions.ReadClip(avatar, clip, entry);
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry);
            Assert.That(entry.Values.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(100).Within(.01));
            Assert.That(entry.Values.Single(value => value.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void DirectClipUsesLayerPriorityWithWriteDefaults(bool permanentLast, bool writeDefaults)
        {
            AnimatorStateMachine selectedMachine;
            var selectedLayer = permanentLast ? 0 : 1;
            if (permanentLast) { selectedMachine = controller.layers[0].stateMachine; Menu(selectedMachine, true, writeDefaults); Permanent(AddLayer("Permanent"), writeDefaults); }
            else { Permanent(controller.layers[0].stateMachine, writeDefaults); selectedMachine = AddLayer("Selected"); Menu(selectedMachine, true, writeDefaults); }
            var clip = (AnimationClip)selectedMachine.states.Single(item => item.state.name == "Selected").state.motion;
            var entry = new VrChatExpressionMenu.Entry();
            VrChatGestureExpressions.ReadClip(avatar, clip, entry);
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, selectedLayer, writeDefaults);
            Assert.That(entry.Values.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(permanentLast ? 100 : 0).Within(.01));
        }

        [Test]
        public void AdditiveDirectClipIsRejectedWithoutGuessingItsReferencePose()
        {
            Permanent(controller.layers[0].stateMachine);
            var machine = AddLayer("Additive expression"); Menu(machine, true);
            var layers = controller.layers; layers[1].blendingMode = AnimatorLayerBlendingMode.Additive; controller.layers = layers;
            var clip = (AnimationClip)machine.states.Single(item => item.state.name == "Selected").state.motion;
            var entry = new VrChatExpressionMenu.Entry();
            VrChatGestureExpressions.ReadClip(avatar, clip, entry);
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1));
            StringAssert.Contains("Additive", error.Message);
            Assert.That(entry.Values.Single(value => value.Shape == "Pupil removal").Weight, Is.Zero);
        }

        [Test]
        public void ExcludedPermanentTargetDoesNotBecomeANeutralSamplingRoot()
        {
            Permanent(controller.layers[0].stateMachine);
            Assert.That(VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0), path => path == "Face"), Is.Empty);
        }

        [Test]
        public void ApplyingDefaultsRequiresAnIndependentExportCopy()
        { Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.ApplyMergedDefaults(avatar, avatar)); }

        [Test]
        public void ApplyingCommittedFxDefaultsChangesOnlyTheCopy()
        {
            var descriptorType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor"))
                .FirstOrDefault(type => type != null);
            if (descriptorType == null) Assert.Ignore("Install the real VRChat SDK for committed descriptor integration.");
            Permanent(controller.layers[0].stateMachine);
            var descriptor = avatar.AddComponent(descriptorType);
            using (var data = new SerializedObject(descriptor))
            {
                data.FindProperty("customizeAnimationLayers").boolValue = true;
                var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = 1;
                var item = layers.GetArrayElementAtIndex(0);
                var type = item.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
                item.FindPropertyRelative("isDefault").boolValue = false;
                item.FindPropertyRelative("animatorController").objectReferenceValue = controller;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            var clone = Object.Instantiate(avatar);
            try
            {
                VrChatExpressionSampler.ApplyMergedDefaults(avatar, clone);
                Assert.That(clone.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.EqualTo(100).Within(.01));
                Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.Zero);
                Assert.That(clone.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh, Is.SameAs(mesh));
                Assert.That(controller.layers[0].stateMachine.defaultState.motion, Is.Not.Null);
            }
            finally { Object.DestroyImmediate(clone); }
        }
    }
}
