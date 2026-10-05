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
    public sealed class NeutralParameterCurveTests
    {
        private string folder;
        private GameObject avatar;
        private Mesh mesh;
        private SkinnedMeshRenderer skin;
        private AnimatorController controller;
        private AnimatorState bodyState;
        private readonly EditorCurveBinding bodyBinding = EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Size");

        [SetUp]
        public void SetUp()
        {
            var name = "__NeutralParameterCurves_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            avatar = new GameObject("Avatar", typeof(Animator));
            var body = new GameObject("Body", typeof(SkinnedMeshRenderer)); body.transform.SetParent(avatar.transform, false);
            skin = body.GetComponent<SkinnedMeshRenderer>();
            mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            mesh.AddBlendShapeFrame("Size", 100, Enumerable.Repeat(Vector3.up, 3).ToArray(), null, null);
            mesh.AddBlendShapeFrame("Face", 100, Enumerable.Repeat(Vector3.right, 3).ToArray(), null, null);
            skin.sharedMesh = mesh; skin.SetBlendShapeWeight(0, 17);
            bodyState = State(controller.layers[0].stateMachine, "Authored body", BodyClip("Authored body", 63), true);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(avatar); Object.DestroyImmediate(mesh);
            if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        private AnimationClip Clip(string name, EditorCurveBinding binding, AnimationCurve curve)
        {
            var clip = new AnimationClip { name = name };
            AnimationUtility.SetEditorCurve(clip, binding, curve);
            AssetDatabase.AddObjectToAsset(clip, controller); return clip;
        }

        private AnimationClip BodyClip(string name, float value) => Clip(name, bodyBinding, AnimationCurve.Constant(0, 12, value));

        private static AnimatorState State(AnimatorStateMachine machine, string name, Motion motion, bool writeDefaults)
        {
            var state = machine.AddState(name); state.motion = motion; state.writeDefaultValues = writeDefaults;
            if (machine.states.Length == 1) machine.defaultState = state;
            return state;
        }

        private AnimatorState Helper(string name = "Internal helper", bool writeDefaults = false,
            float weight = 1, AnimatorLayerBlendingMode mode = AnimatorLayerBlendingMode.Override)
        {
            controller.AddParameter(name, AnimatorControllerParameterType.Float);
            var clip = Clip(name, EditorCurveBinding.FloatCurve("", typeof(Animator), name),
                new AnimationCurve(new Keyframe(0, 0), new Keyframe(10, 0), new Keyframe(12, 1)));
            controller.AddLayer("Parameter support"); var layers = controller.layers;
            layers[layers.Length - 1].defaultWeight = weight; layers[layers.Length - 1].blendingMode = mode; controller.layers = layers;
            return State(layers[layers.Length - 1].stateMachine, "Internal animation", clip, writeDefaults);
        }

        private ExpressionDependencies Dependencies(VrChatExpressionMenu.Source metadata) => ExpressionDependencies.AnalyzeNeutral(
            controller, new HashSet<EditorCurveBinding> { bodyBinding }, null, metadata, preserveCommittedMorphs: true);

        private List<VrChatExpressionMenu.MorphValue> Sample()
        {
            var metadata = new VrChatExpressionMenu.Source { Controller = controller };
            return VrChatExpressionSampler.SampleNeutral(avatar, controller, Dependencies(metadata), metadata, null);
        }

        // Use Unity's unchanged authored controller as the independent baseline,
        // including implicit WD/default streams and fractional/additive weights.
        private float NativeBodyWeight()
        {
            var clone = Object.Instantiate(avatar);
            var graph = PlayableGraph.Create("Parameter support reference");
            try
            {
                var animator = clone.GetComponent<Animator>(); animator.runtimeAnimatorController = null;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.fireEvents = false;
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                AnimationPlayableOutput.Create(graph, "Native reference", animator).SetSourcePlayable(playable);
                graph.Play(); graph.Evaluate(0);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                return clone.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0);
            }
            finally { graph.Destroy(); Object.DestroyImmediate(clone); }
        }

        [TestCase(false, 1f, AnimatorLayerBlendingMode.Override, false)]
        [TestCase(true, 1f, AnimatorLayerBlendingMode.Override, false)]
        [TestCase(true, .5f, AnimatorLayerBlendingMode.Override, false)]
        [TestCase(false, .5f, AnimatorLayerBlendingMode.Additive, false)]
        [TestCase(true, .5f, AnimatorLayerBlendingMode.Additive, false)]
        [TestCase(true, 1f, AnimatorLayerBlendingMode.Override, true)]
        public void IndependentParameterAnimationKeepsTheNativeBodyPose(bool writeDefaults, float weight,
            AnimatorLayerBlendingMode mode, bool selfTimed)
        {
            var helper = Helper(writeDefaults: writeDefaults, weight: weight, mode: mode);
            if (selfTimed) { helper.timeParameterActive = true; helper.timeParameter = "Internal helper"; }
            var metadata = new VrChatExpressionMenu.Source { Controller = controller };
            var dependencies = Dependencies(metadata);
            Assert.That(dependencies.NativeSupportLayers, Does.Contain(1), "WD closure must retain the helper's native state.");
            var before = EditorJsonUtility.ToJson(controller);
            var clipBefore = EditorJsonUtility.ToJson(helper.motion);
            var expected = NativeBodyWeight();
            var values = VrChatExpressionSampler.SampleNeutral(avatar, controller, dependencies, metadata, null);
            Assert.That(values.Single(value => value.Shape == "Size").Weight, Is.EqualTo(expected).Within(.01));
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
            Assert.That(EditorJsonUtility.ToJson(helper.motion), Is.EqualTo(clipBefore));
        }

        private void BodyBlendTree(string parameter)
        {
            var tree = new BlendTree { name = "Body controlled by helper", blendType = BlendTreeType.Simple1D,
                blendParameter = parameter, useAutomaticThresholds = false };
            tree.AddChild(BodyClip("Small", 20), 0); tree.AddChild(BodyClip("Large", 80), 1);
            AssetDatabase.AddObjectToAsset(tree, controller); bodyState.motion = tree;
        }

        [Test]
        public void ParameterThatControlsCapturedMorphsMustRemainFixedEvenWhenItsChangeIsDelayed()
        {
            Helper(); BodyBlendTree("Internal helper");
            var error = Assert.Throws<NeutralShapeSamplingException>(() => Sample());
            StringAssert.Contains("Internal helper", error.Message,
                "A quiet two-second window must not establish that a later parameter change is harmless.");
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17));
        }

        [Test]
        public void ParameterDependencePropagatesThroughAnotherMotionTimeRelay()
        {
            Helper("First helper");
            var relay = Helper("Body input"); relay.timeParameterActive = true; relay.timeParameter = "First helper";
            BodyBlendTree("Body input");
            var metadata = new VrChatExpressionMenu.Source { Controller = controller };
            var required = NeutralParameterDependencies.Required(controller, Dependencies(metadata), null);
            Assert.That(required, Does.Contain("First helper"));
            Assert.That(required, Does.Contain("Body input"));
            Assert.Throws<NeutralShapeSamplingException>(() => Sample());
        }

        [Test]
        public void FutureTimedTransitionsAreStillValidatedForIndependentParameterSupport()
        {
            var helper = Helper();
            var machine = controller.layers[1].stateMachine;
            var later = State(machine, "Later defaults", helper.motion, true);
            var transition = helper.AddTransition(later); transition.hasExitTime = true;
            transition.exitTime = 1; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Greater, .5f, "Internal helper");
            Assert.Throws<NeutralShapeSamplingException>(() => Sample());
        }

        [Test]
        public void SelectedMenuExpressionRetainsItsStrictParameterCurveContract()
        {
            Helper();
            var metadata = new VrChatExpressionMenu.Source { Controller = controller };
            controller.AddParameter("Menu", AnimatorControllerParameterType.Float);
            BodyBlendTree("Menu");
            Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleFixed(avatar, controller,
                new Dictionary<string, float>(), new Dictionary<string, float> { ["Menu"] = 1 }, null, metadata));
        }

        private BlendTree Tree(string name, string parameter, Motion zero, Motion one)
        {
            var tree = new BlendTree { name = name, blendType = BlendTreeType.Simple1D, blendParameter = parameter,
                useAutomaticThresholds = false, minThreshold = 0, maxThreshold = 1 };
            tree.AddChild(zero, 0); tree.AddChild(one, 1);
            AssetDatabase.AddObjectToAsset(tree, controller); return tree;
        }

        // Mirrors the published CGE analog-fist smoothing topology, with names
        // intentionally unrelated to that package: two Motion Time states share
        // nested 1D feedback trees, constant smoothing curves and a linear proxy.
        private (AnimatorState Waiting, AnimatorState Listening, BlendTree Factor) GestureSmoothing(bool writeDefaults)
        {
            foreach (var name in new[] { "Proxy", "Smoothed", "Factor", "GestureLeftWeight" })
                controller.AddParameter(new AnimatorControllerParameter { name = name, type = AnimatorControllerParameterType.Float,
                    defaultFloat = name == "Factor" ? .7f : 0 });
            controller.AddParameter("GestureLeft", AnimatorControllerParameterType.Int);
            AnimationClip SmoothingClip(string name, float value)
            {
                var clip = Clip(name, EditorCurveBinding.FloatCurve("", typeof(Animator), "Proxy"), AnimationCurve.Linear(0, 0, 1, 1));
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "Smoothed"),
                    AnimationCurve.Constant(0, 1f / 60f, value));
                return clip;
            }
            var zero = SmoothingClip("Zero smoothing", 0); var one = SmoothingClip("Full smoothing", 1);
            var factor = Tree("Factor tree", "Factor", Tree("Proxy interpolation", "Proxy", zero, one),
                Tree("Smoothed interpolation", "Smoothed", zero, one));
            controller.AddLayer("Generated gesture feedback"); var layers = controller.layers;
            layers[layers.Length - 1].defaultWeight = 1; controller.layers = layers;
            var machine = layers[layers.Length - 1].stateMachine;
            var waiting = State(machine, "Waiting", factor, writeDefaults);
            var listening = State(machine, "Listening", factor, writeDefaults);
            waiting.timeParameterActive = true; waiting.timeParameter = "Proxy";
            listening.timeParameterActive = true; listening.timeParameter = "GestureLeftWeight";
            foreach (var state in new[] { waiting, listening })
                ParameterDriverExpressionTests.Driver(state, ParameterDriverExpressionTests.Op("Set", "Factor", .7f));
            var enter = waiting.AddTransition(listening); enter.hasExitTime = false; enter.duration = 0;
            enter.AddCondition(AnimatorConditionMode.Equals, 1, "GestureLeft");
            var leave = listening.AddTransition(waiting); leave.hasExitTime = false; leave.duration = 0;
            leave.AddCondition(AnimatorConditionMode.NotEqual, 1, "GestureLeft");
            return (waiting, listening, factor);
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void GestureSmoothingFeedbackAndUnrelatedFacialConsumerKeepNativeBody(bool writeDefaults, bool facialConsumer)
        {
            GestureSmoothing(writeDefaults);
            if (facialConsumer)
            {
                var binding = EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Face");
                var zero = Clip("No facial gesture", binding, AnimationCurve.Constant(0, 1, 0));
                var full = Clip("Full facial gesture", binding, AnimationCurve.Constant(0, 1, 100));
                controller.AddLayer("Unrelated facial response"); var layers = controller.layers;
                layers[layers.Length - 1].defaultWeight = 1; controller.layers = layers;
                State(layers[layers.Length - 1].stateMachine, "Facial response", Tree("Face tree", "Smoothed", zero, full), writeDefaults);
            }
            var before = EditorJsonUtility.ToJson(controller);
            var expected = NativeBodyWeight();
            var metadata = new VrChatExpressionMenu.Source { Controller = controller };
            var dependencies = Dependencies(metadata);
            Assert.That(dependencies.NativeSupportLayers, Does.Contain(1));
            var values = VrChatExpressionSampler.SampleNeutral(avatar, controller, dependencies, metadata, null);
            Assert.That(values.Select(value => value.Shape), Is.EqualTo(new[] { "Size" }));
            Assert.That(values.Single().Weight, Is.EqualTo(expected).Within(.01));
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
        }

        [Test]
        public void GestureSmoothingThatActuallyControlsBodyStillRequiresItsParameterProof()
        {
            GestureSmoothing(true); BodyBlendTree("Smoothed");
            Assert.Throws<NeutralShapeSamplingException>(() => Sample());
        }

        [TestCase("Direct")]
        [TestCase("Mixed defaults")]
        [TestCase("Custom additive reference")]
        public void FeedbackWithUnprovedNativeDefaultContributionIsNotExempt(string variation)
        {
            var helper = GestureSmoothing(true);
            if (variation == "Direct")
            {
                helper.Factor.blendType = BlendTreeType.Direct;
                var children = helper.Factor.children;
                children[0].directBlendParameter = "Proxy"; children[1].directBlendParameter = "Smoothed";
                helper.Factor.children = children;
                var parameters = controller.parameters;
                foreach (var parameter in parameters.Where(parameter => parameter.name == "Proxy" || parameter.name == "Smoothed"))
                    parameter.defaultFloat = .5f;
                controller.parameters = parameters;
            }
            else if (variation == "Custom additive reference")
            {
                var layers = controller.layers; layers[1].blendingMode = AnimatorLayerBlendingMode.Additive; controller.layers = layers;
                var leaf = (AnimationClip)((BlendTree)helper.Factor.children[0].motion).children[0].motion;
                // Unity requires the additive reference's curve layout to
                // match the source clip. The proof conservatively excludes
                // custom references even when this particular one is harmless.
                var reference = Clip("Custom parameter reference", EditorCurveBinding.FloatCurve("", typeof(Animator), "Proxy"),
                    AnimationCurve.Constant(0, 1, 0));
                AnimationUtility.SetEditorCurve(reference, EditorCurveBinding.FloatCurve("", typeof(Animator), "Smoothed"),
                    AnimationCurve.Constant(0, 1, 0));
                var settings = AnimationUtility.GetAnimationClipSettings(leaf);
                settings.hasAdditiveReferencePose = true; settings.additiveReferencePoseClip = reference; settings.additiveReferencePoseTime = 0;
                AnimationUtility.SetAnimationClipSettings(leaf, settings);
                AnimationUtility.SetAdditiveReferencePose(leaf, reference, 0);
            }
            else helper.Listening.writeDefaultValues = false;
            Assert.Throws<NeutralShapeSamplingException>(() => Sample());
        }
    }
}
