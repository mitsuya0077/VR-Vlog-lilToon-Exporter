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
    public sealed class ExcludedLayerTests
    {
        private string directory;
        private GameObject avatar, pet;
        private Mesh mesh;
        private AnimatorController controller;
        private AnimatorStateMachine petMachine;
        private AnimatorState petState;
        private AnimationClip petClip;

        [SetUp]
        public void SetUp()
        {
            var folder = "__VRVlogExcludedLayers_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder);
            directory = "Assets/" + folder;
            controller = AnimatorController.CreateAnimatorControllerAtPath(directory + "/FX.controller");
            controller.AddParameter("Smile", AnimatorControllerParameterType.Bool);
            avatar = new GameObject("Avatar", typeof(Animator));
            var face = new GameObject("Face", typeof(SkinnedMeshRenderer));
            face.transform.SetParent(avatar.transform, false);
            mesh = new Mesh { vertices = new[] { Vector3.zero } };
            mesh.AddBlendShapeFrame("Smile", 100, new[] { Vector3.up }, new Vector3[1], new Vector3[1]);
            face.GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
            pet = new GameObject("Pet");
            pet.transform.SetParent(avatar.transform, false);

            var machine = controller.layers[0].stateMachine;
            var idle = machine.AddState("Idle");
            idle.motion = FaceClip("Idle", 0);
            idle.writeDefaultValues = false;
            var smile = machine.AddState("Smile");
            smile.motion = FaceClip("Smile", 60);
            smile.writeDefaultValues = false;
            machine.defaultState = idle;
            var selected = idle.AddTransition(smile);
            selected.hasExitTime = false;
            selected.duration = 0;
            selected.AddCondition(AnimatorConditionMode.If, 0, "Smile");

            controller.AddLayer("Pet layer");
            var layers = controller.layers;
            layers[1].defaultWeight = 1;
            controller.layers = layers;
            petMachine = layers[1].stateMachine;
            petState = petMachine.AddState("Pet moving");
            petClip = Clip("Pet move", "Pet", typeof(Transform), "m_LocalPosition.x", AnimationCurve.Linear(0, 0, 1, 10));
            petState.motion = petClip;
            petState.writeDefaultValues = false;
            petMachine.defaultState = petState;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(avatar);
            Object.DestroyImmediate(mesh);
            AssetDatabase.DeleteAsset(directory);
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void TimedPetLayerDoesNotRejectRetainedFaceWhenEntireLayerIsExcluded(bool petUsesMenuParameter, bool ongoingTransition)
        {
            var next = petMachine.AddState("Pet next");
            next.motion = petClip;
            next.writeDefaultValues = false;
            var transition = DelayedTransition(next);
            if (ongoingTransition) { transition.exitTime = 0.5f; transition.duration = 10; }
            if (petUsesMenuParameter) transition.AddCondition(AnimatorConditionMode.If, 0, "Smile");
            Assert.Throws<InvalidOperationException>(() => Sample(controller));
            using var exclusions = new ExportObjectExclusions(avatar, new[] { pet });
            Assert.That(VrChatExpressionSampler.FindExcludedLayers(controller, controller, exclusions.ContainsPath), Does.Contain(1));
            var values = Sample(controller, exclusions.ContainsPath);
            Assert.That(values.Count, Is.EqualTo(1));
            Assert.That(values.Single().Path, Is.EqualTo("Face"));
            Assert.That(values.Single().Weight, Is.EqualTo(60).Within(.01f));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.Zero);
            Assert.That(pet.transform.localPosition, Is.EqualTo(Vector3.zero));
            Assert.That(controller.layers[1].defaultWeight, Is.EqualTo(1));
        }

        [Test]
        public void DelayedRetainedFaceInNestedStatePreventsExcludingLayer()
        {
            var future = petMachine.AddStateMachine("Later").AddState("Retained face");
            future.motion = FaceClip("Delayed face", 90);
            future.writeDefaultValues = false;
            DelayedTransition(future);
            using var exclusions = new ExportObjectExclusions(avatar, new[] { pet });
            Assert.That(VrChatExpressionSampler.FindExcludedLayers(controller, controller, exclusions.ContainsPath), Does.Not.Contain(1));
            var error = Assert.Throws<InvalidOperationException>(() => Sample(controller, exclusions.ContainsPath));
            Assert.That(error.Message, Does.Contain("時間で遷移"));
        }

        [Test]
        public void BlendTreeRetainedLeafPreventsExclusionEvenWhenCurrentWeightIsZero()
        {
            controller.AddParameter("Pet blend", AnimatorControllerParameterType.Float);
            var tree = new BlendTree
            {
                name = "Pet and future face", blendType = BlendTreeType.Simple1D,
                blendParameter = "Pet blend", useAutomaticThresholds = false
            };
            AssetDatabase.AddObjectToAsset(tree, controller);
            tree.AddChild(petClip, 0);
            tree.AddChild(FaceClip("Blend face", 90), 1);
            petState.motion = tree;
            var next = petMachine.AddState("Pet next");
            next.motion = petClip;
            next.writeDefaultValues = false;
            DelayedTransition(next);
            using var exclusions = new ExportObjectExclusions(avatar, new[] { pet });
            Assert.That(VrChatExpressionSampler.FindExcludedLayers(controller, controller, exclusions.ContainsPath), Does.Not.Contain(1));
            Assert.Throws<InvalidOperationException>(() => Sample(controller, exclusions.ContainsPath));
        }

        [Test]
        public void LayerProofUsesEffectiveAnimatorOverrideClipsInBothDirections()
        {
            var future = petMachine.AddState("Later face");
            future.writeDefaultValues = false;
            var faceClip = FaceClip("Override face", 90);
            future.motion = faceClip;
            DelayedTransition(future);
            using var exclusions = new ExportObjectExclusions(avatar, new[] { pet });
            var overrides = new AnimatorOverrideController(controller);
            try
            {
                Assert.That(VrChatExpressionSampler.FindExcludedLayers(controller, overrides, exclusions.ContainsPath), Does.Not.Contain(1),
                    "A null override still uses the original retained-face clip.");
                overrides[faceClip] = petClip;
                Assert.That(VrChatExpressionSampler.FindExcludedLayers(controller, overrides, exclusions.ContainsPath), Does.Contain(1));
                Assert.That(Sample(overrides, exclusions.ContainsPath).Single().Weight, Is.EqualTo(60).Within(.01f));
                overrides[petClip] = faceClip;
                Assert.That(VrChatExpressionSampler.FindExcludedLayers(controller, overrides, exclusions.ContainsPath), Does.Not.Contain(1),
                    "Replacing an excluded clip with a retained face must restore timed-state validation.");
                Assert.Throws<InvalidOperationException>(() => Sample(overrides, exclusions.ContainsPath));
            }
            finally { Object.DestroyImmediate(overrides); }
        }

        [TestCase(true, "pet")]
        [TestCase(false, "empty")]
        [TestCase(false, "none")]
        public void FutureStateWithImplicitDefaultsDoesNotProveLayerExclusion(bool writeDefaults, string motionKind)
        {
            var future = petMachine.AddState("Future defaults");
            future.writeDefaultValues = writeDefaults;
            if (motionKind == "pet") future.motion = petClip;
            if (motionKind == "empty")
            {
                var empty = new AnimationClip { name = "Empty future" };
                AssetDatabase.CreateAsset(empty, directory + "/Empty.anim");
                future.motion = empty;
            }
            DelayedTransition(future);
            using var exclusions = new ExportObjectExclusions(avatar, new[] { pet });
            Assert.That(VrChatExpressionSampler.FindExcludedLayers(controller, controller, exclusions.ContainsPath), Does.Not.Contain(1));
            Assert.Throws<InvalidOperationException>(() => Sample(controller, exclusions.ContainsPath));
        }

        [Test]
        public void AnimatorParameterCurvesAndEmptyLayersDoNotProveExclusion()
        {
            controller.AddParameter("Pet driver", AnimatorControllerParameterType.Float);
            AnimationUtility.SetEditorCurve(petClip, EditorCurveBinding.FloatCurve("", typeof(Animator), "Pet driver"), AnimationCurve.Linear(0, 0, 1, 1));
            controller.AddLayer("Empty layer");
            // Even an overbroad path predicate must not hide global Animator
            // parameter curves, and a binding-free layer proves nothing.
            var ignored = VrChatExpressionSampler.FindExcludedLayers(controller, controller, _ => true);
            Assert.That(ignored, Does.Not.Contain(1));
            Assert.That(ignored, Does.Not.Contain(2));
        }

        private List<VrChatExpressionMenu.MorphValue> Sample(RuntimeAnimatorController runtime, Func<string, bool> excludedPath = null) =>
            VrChatExpressionSampler.Sample(avatar, runtime,
                new Dictionary<string, float> { ["Smile"] = 0 },
                new Dictionary<string, float> { ["Smile"] = 1 }, excludedPath);

        private AnimatorStateTransition DelayedTransition(AnimatorState next)
        {
            var transition = petState.AddTransition(next);
            transition.hasExitTime = true;
            transition.exitTime = 10;
            transition.duration = 0;
            return transition;
        }

        private AnimationClip FaceClip(string name, float weight) =>
            Clip(name, "Face", typeof(SkinnedMeshRenderer), "blendShape.Smile", AnimationCurve.Constant(0, 1, weight));

        private AnimationClip Clip(string name, string path, Type type, string property, AnimationCurve curve)
        {
            var clip = new AnimationClip { name = name };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, type, property), curve);
            AssetDatabase.CreateAsset(clip, directory + "/" + name + ".anim");
            return clip;
        }
    }
}
