using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace VRVlog.LilToonExporter.Tests
{
    // Real Unity graph/animation tests. The command-line mesh doubles do not
    // execute these tests and must not be reported as Animator validation.
    public class VrChatMenuExpressionTests
    {
        private string directory;
        private GameObject avatar;
        private Mesh mesh;
        private AnimatorController controller;

        [SetUp]
        public void SetUp()
        {
            var folder = "__VRVlogMenuTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder);
            directory = "Assets/" + folder;
            controller = AnimatorController.CreateAnimatorControllerAtPath(directory + "/FX.controller");
            avatar = new GameObject("Avatar", typeof(Animator));
            var face = new GameObject("Face", typeof(SkinnedMeshRenderer));
            face.transform.SetParent(avatar.transform, false);
            mesh = BaseShapeFixture.Create();
            var renderer = face.GetComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.SetBlendShapeWeight(0, 25);
            renderer.SetBlendShapeWeight(1, 50);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(avatar);
            UnityEngine.Object.DestroyImmediate(mesh);
            AssetDatabase.DeleteAsset(directory);
        }

        [Test]
        public void MenuSelectionCapturesACombinationAndLeavesSourceUntouched()
        {
            DiscreteController();
            var values = VrChatExpressionSampler.Sample(avatar, controller, Params("Face", 0), Params("Face", 1));
            Assert.That(values, Has.Count.EqualTo(2));
            Assert.That(values.Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
            Assert.That(values.Single(v => v.Shape == "Blink").Weight, Is.EqualTo(0).Within(.01));
            var source = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
            Assert.That(source.GetBlendShapeWeight(0), Is.EqualTo(25));
            Assert.That(source.GetBlendShapeWeight(1), Is.EqualTo(50));
            Assert.That(source.sharedMesh, Is.SameAs(mesh));

            var clone = UnityEngine.Object.Instantiate(avatar);
            var temporary = new List<Mesh>();
            try
            {
                AvatarBaseShape.Preserve(avatar, clone, temporary, null);
                var menu = new VrChatExpressionMenu.Source();
                var entry = new VrChatExpressionMenu.Entry { Id = "face-smile", Name = "顔 / 笑顔" };
                entry.Values.AddRange(values);
                menu.Entries.Add(entry);
                var expressions = VrChatExpressionBaker.Bake(avatar, clone, menu, null, temporary, null);
                Assert.That(expressions, Has.Count.EqualTo(1));
                var baked = clone.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh;
                var v = new Vector3[3]; var n = new Vector3[3]; var t = new Vector3[3];
                baked.GetBlendShapeFrameVertices(2, 0, v, n, t);
                Assert.That(baked.vertices[0] + v[0], Is.EqualTo(new Vector3(4, 0, 0)));
                Assert.That(mesh.blendShapeCount, Is.EqualTo(2));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clone);
                foreach (var item in temporary) UnityEngine.Object.DestroyImmediate(item);
            }
        }

        [Test]
        public void WriteDefaultsOffRetainsTheWholeEvaluatedFace()
        {
            DiscreteController();
            var states = controller.layers[0].stateMachine.states;
            var idle = (AnimationClip)states.Single(s => s.state.name == "Idle").state.motion;
            var smile = (AnimationClip)states.Single(s => s.state.name == "Smile").state.motion;
            var binding = EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size");
            AnimationUtility.SetEditorCurve(idle, binding, AnimationCurve.Constant(0, 1, 60));
            AnimationUtility.SetEditorCurve(smile, binding, null);
            var values = VrChatExpressionSampler.Sample(avatar, controller, Params("Face", 0), Params("Face", 1));
            Assert.That(values.Single(v => v.Shape == "Face size").Weight, Is.EqualTo(60).Within(.01),
                "The previous state owns the retained value, even though the current clip does not bind it.");
        }

        [Test]
        public void DelayedExitCannotMasqueradeAsAFixedExpression()
        {
            DiscreteController();
            var states = controller.layers[0].stateMachine.states;
            var idle = states.Single(s => s.state.name == "Idle").state;
            var smile = states.Single(s => s.state.name == "Smile").state;
            var transition = smile.AddTransition(idle);
            transition.hasExitTime = true;
            transition.exitTime = 10;
            transition.duration = 0;
            Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.Sample(avatar, controller, Params("Face", 0), Params("Face", 1)));
        }

        [Test]
        public void DelayedNonLoopingStepCannotMasqueradeAsAFixedExpression()
        {
            DiscreteController();
            var clip = (AnimationClip)controller.layers[0].stateMachine.states.Single(s => s.state.name == "Smile").state.motion;
            var curve = new AnimationCurve(new Keyframe(0, 75, 0, float.PositiveInfinity), new Keyframe(10, 100, float.PositiveInfinity, 0));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"), curve);
            Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.Sample(avatar, controller, Params("Face", 0), Params("Face", 1)));
        }

        [Test]
        public void BlendTreeUsesTheMenuValueInsteadOfPublishingItsIndividualShapes()
        {
            controller.AddParameter("Strength", AnimatorControllerParameterType.Float);
            var tree = new BlendTree { name = "Composed smile", blendType = BlendTreeType.Simple1D, blendParameter = "Strength", useAutomaticThresholds = false };
            AssetDatabase.AddObjectToAsset(tree, controller);
            tree.AddChild(Clip("Off", 0, 0), 0);
            tree.AddChild(Clip("On", 80, 100), 1);
            var state = controller.layers[0].stateMachine.AddState("Smile");
            state.motion = tree;
            controller.layers[0].stateMachine.defaultState = state;
            var values = VrChatExpressionSampler.Sample(avatar, controller, Params("Strength", 0), Params("Strength", .5f));
            Assert.That(values.Single(v => v.Shape == "Face size").Weight, Is.EqualTo(40).Within(.01));
            Assert.That(values.Single(v => v.Shape == "Blink").Weight, Is.EqualTo(50).Within(.01));
        }

        [Test]
        public void UnsupportedVisibilityIsReportedInsteadOfDroppingHalfAnExpression()
        {
            DiscreteController();
            var clip = (AnimationClip)controller.layers[0].stateMachine.states.Single(s => s.state.name == "Smile").state.motion;
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1));
            Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.Sample(avatar, controller, Params("Face", 0), Params("Face", 1)));
        }

        [Test]
        public void DuplicateHierarchyPathsCannotCaptureTheWrongFace()
        {
            DiscreteController();
            var duplicate = new GameObject("Face");
            duplicate.transform.SetParent(avatar.transform, false);
            Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.Sample(avatar, controller, Params("Face", 0), Params("Face", 1)));
        }

        [Test]
        public void MenuTraversalKeepsLabelsAncestorGatesAndReportsPuppets()
        {
            MenuTraversalFixture.Run((condition, description) => Assert.That(condition, Is.True, description));
        }

        [Test]
        public void GestureClipKeepsTheAuthoredCombinationDespiteAutomaticBlink()
        {
            DiscreteController();
            controller.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
            var machine = controller.layers[0].stateMachine;
            var idle = machine.states.Single(s => s.state.name == "Idle").state;
            var smile = machine.states.Single(s => s.state.name == "Smile").state;
            var gesture = idle.AddTransition(smile);
            gesture.AddCondition(AnimatorConditionMode.Equals, 2, "GestureRight");
            var blink = machine.AddState("EyeClose");
            blink.motion = Clip("AutomaticBlink", 0, 100);
            var timed = idle.AddTransition(blink);
            timed.hasExitTime = true;
            timed.exitTime = 10;
            var source = new VrChatExpressionMenu.Source { Controller = controller };
            VrChatGestureExpressions.Add(avatar, source);
            Assert.That(source.Entries, Has.Count.EqualTo(1));
            var expression = source.Entries.Single();
            Assert.That(expression.Error, Is.Null);
            Assert.That(expression.Name, Is.EqualTo("ジェスチャー / Smile"));
            Assert.That(expression.Values.Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75));
            Assert.That(expression.Values.Single(v => v.Shape == "Blink").Weight, Is.EqualTo(0));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(25));
        }

        [Test]
        public void GestureClipReadsOverridesAndDeduplicatesSharedAnimations()
        {
            DiscreteController();
            controller.AddParameter("GestureLeft", AnimatorControllerParameterType.Int);
            var machine = controller.layers[0].stateMachine;
            var smile = machine.states.Single(s => s.state.name == "Smile").state;
            var same = machine.AddState("Same smile");
            same.motion = smile.motion;
            foreach (var state in new[] { smile, same })
                machine.AddAnyStateTransition(state).AddCondition(AnimatorConditionMode.Equals, 3, "GestureLeft");
            var overrides = new AnimatorOverrideController(controller);
            try
            {
                overrides[(AnimationClip)smile.motion] = Clip("My smile", 42, 80);
                var source = new VrChatExpressionMenu.Source { Controller = overrides };
                VrChatGestureExpressions.Add(avatar, source);
                Assert.That(source.Entries, Has.Count.EqualTo(1));
                Assert.That(source.Entries.Single().Name, Is.EqualTo("ジェスチャー / My smile"));
                Assert.That(source.Entries.Single().Values.Single(v => v.Shape == "Face size").Weight, Is.EqualTo(42));
            }
            finally { UnityEngine.Object.DestroyImmediate(overrides); }
        }

        [Test]
        public void GestureClipRejectsChangingCurvesAndPartialMaterialFaces()
        {
            var clip = Clip("Smile", 75, 0);
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink"),
                AnimationCurve.Linear(0, 0, 10, 100));
            Assert.Throws<InvalidOperationException>(() => VrChatGestureExpressions.ReadPose(avatar, clip));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink"),
                AnimationCurve.Constant(0, 1, 0));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(GameObject), "m_IsActive"),
                AnimationCurve.Constant(0, 1, 1));
            Assert.Throws<InvalidOperationException>(() => VrChatGestureExpressions.ReadPose(avatar, clip));
        }

        [Test]
        public void GestureBlendTreeLeavesAreNotRegisteredAsSeparateExpressions()
        {
            controller.AddParameter("GestureLeft", AnimatorControllerParameterType.Int);
            var tree = new BlendTree { name = "Composed gesture" };
            AssetDatabase.AddObjectToAsset(tree, controller);
            tree.AddChild(Clip("Part A", 75, 0));
            tree.AddChild(Clip("Part B", 0, 50));
            var machine = controller.layers[0].stateMachine;
            var state = machine.AddState("Composed");
            state.motion = tree;
            machine.AddAnyStateTransition(state).AddCondition(AnimatorConditionMode.Equals, 3, "GestureLeft");
            var source = new VrChatExpressionMenu.Source { Controller = controller };
            VrChatGestureExpressions.Add(avatar, source);
            Assert.That(source.Entries, Has.Count.EqualTo(1));
            Assert.That(source.Entries[0].Error, Does.Contain("BlendTree"));
            Assert.That(source.Entries[0].Values, Is.Empty);
        }

        [Test]
        public void GlbRegistrationKeepsComposedExpressions()
        {
            MenuExpressionFixture.Run((condition, description) => Assert.That(condition, Is.True, description));
        }

        private void DiscreteController()
        {
            controller.AddParameter("Face", AnimatorControllerParameterType.Int);
            var machine = controller.layers[0].stateMachine;
            var idle = machine.AddState("Idle");
            idle.motion = Clip("Idle", 0, 0);
            var smile = machine.AddState("Smile");
            smile.motion = Clip("Smile", 75, 0);
            idle.writeDefaultValues = smile.writeDefaultValues = false;
            machine.defaultState = idle;
            var transition = idle.AddTransition(smile);
            transition.hasExitTime = false;
            transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "Face");
        }

        private AnimationClip Clip(string name, float size, float blink)
        {
            var clip = new AnimationClip { name = name };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"), AnimationCurve.Constant(0, 1, size));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink"), AnimationCurve.Constant(0, 1, blink));
            AssetDatabase.AddObjectToAsset(clip, controller);
            return clip;
        }

        private static Dictionary<string, float> Params(string name, float value) => new Dictionary<string, float> { [name] = value };
    }
}
