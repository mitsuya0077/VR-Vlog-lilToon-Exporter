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
    public sealed class DirectExpressionNativeLayersTests
    {
        private string folder;
        private GameObject avatar;
        private Mesh mesh;
        private AnimatorController controller;
        private AnimatorState lower, alternate, selected;

        [SetUp]
        public void SetUp()
        {
            var name = "__DirectNativeLayers_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            controller.AddParameter("Face", AnimatorControllerParameterType.Int);
            avatar = new GameObject("Avatar", typeof(Animator));
            var face = new GameObject("Face", typeof(SkinnedMeshRenderer)); face.transform.SetParent(avatar.transform, false);
            mesh = BaseShapeFixture.Create(); face.GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
            face.GetComponent<SkinnedMeshRenderer>().SetBlendShapeWeight(0, 20);
            var machine = controller.layers[0].stateMachine;
            lower = State(machine, "Default lower face", Clip("Lower default", 30)); machine.defaultState = lower;
            alternate = State(machine, "Alternate lower face", Clip("Lower alternate", 60));
            var transition = lower.AddTransition(alternate); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "Face");
            controller.AddLayer("Authored player"); var layers = controller.layers; layers[1].defaultWeight = .5f;
            selected = State(layers[1].stateMachine, "Authored expression", Clip("Selected", 80));
            layers[1].stateMachine.defaultState = selected; controller.layers = layers;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(avatar); Object.DestroyImmediate(mesh);
            if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        private AnimationClip Clip(string name, float weight)
        {
            var clip = new AnimationClip { name = name };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                AnimationCurve.Constant(0, 1, weight));
            AssetDatabase.AddObjectToAsset(clip, controller); return clip;
        }

        private static AnimatorState State(AnimatorStateMachine machine, string name, AnimationClip clip)
        {
            var state = machine.AddState(name); state.motion = clip; state.writeDefaultValues = false; return state;
        }

        // This reference plays the complete authored graph and its actual clips.
        // It does not reconstruct an exporter probe or use dependency analysis.
        private float Native(RuntimeAnimatorController runtime, IDictionary<string, float> defaults = null)
        {
            var copy = Object.Instantiate(avatar); var graph = PlayableGraph.Create("Direct expression native reference");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            try
            {
                var animator = copy.GetComponent<Animator>(); animator.runtimeAnimatorController = null;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.applyRootMotion = false; animator.fireEvents = false;
                var playable = AnimatorControllerPlayable.Create(graph, runtime);
                for (var index = 0; index < controller.layers.Length; index++)
                    playable.SetLayerWeight(index, index == 0 ? 1 : controller.layers[index].defaultWeight);
                AnimationPlayableOutput.Create(graph, "Face", animator).SetSourcePlayable(playable);
                if (defaults != null)
                    foreach (var pair in defaults) playable.SetInteger(pair.Key, Mathf.RoundToInt(pair.Value));
                graph.Play(); graph.Evaluate(0);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                Assert.That(playable.IsInTransition(0), Is.False);
                return copy.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0);
            }
            finally { if (graph.IsValid()) graph.Destroy(); Object.DestroyImmediate(copy); }
        }

        private static int ProbeObjects() => Resources.FindObjectsOfTypeAll<Object>().Count(value => value != null &&
            value.hideFlags == HideFlags.HideAndDontSave &&
            (value.name == "VRVlog expression override probe" || value.name == "VRVlog expression effective override probe"));

        private void AssertUnchanged(string sourceJson, string meshJson, int probes)
        {
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(sourceJson));
            Assert.That(EditorJsonUtility.ToJson(mesh), Is.EqualTo(meshJson));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(20));
            Assert.That(ProbeObjects(), Is.EqualTo(probes), "Both successful and rejected probes must dispose their private controllers and clips.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FractionalPlayerBlendsWithTheEvaluatedLowerDefaultAcrossPossibleTransitions(bool writeDefaults)
        {
            selected.writeDefaultValues = writeDefaults;
            var expected = Native(controller);
            Assert.That(expected, Is.EqualTo(55).Within(.01), "The authored lower 30 and selected 80 at weight .5 must blend natively.");
            Assert.That(ExpressionDependencies.StationaryLayers(controller).ContainsKey(0), Is.False,
                "The lower graph has multiple states and a transition, so a stationary-clip-only reconstruction loses it.");
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1, writeDefaults);
            Assert.That(entry.Values.Single(value => value.Shape == "Face size").Weight, Is.EqualTo(expected).Within(.01));
            AssertUnchanged(sourceJson, meshJson, probes);
        }

        [Test]
        public void EffectiveOverrideClipsSurviveThePrivateFullGraphProbe()
        {
            var replacementLower = Clip("Effective lower", 50); var replacementSelected = Clip("Effective selected", 90);
            var runtime = new AnimatorOverrideController(controller);
            runtime[(AnimationClip)lower.motion] = replacementLower; runtime[(AnimationClip)selected.motion] = replacementSelected;
            AssetDatabase.CreateAsset(runtime, folder + "/Effective.overrideController");
            var expected = Native(runtime); Assert.That(expected, Is.EqualTo(70).Within(.01));
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, replacementSelected, entry);
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var overrideJson = EditorJsonUtility.ToJson(runtime);
            var probes = ProbeObjects();
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, runtime, entry, 1);
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(expected).Within(.01));
            Assert.That(EditorJsonUtility.ToJson(runtime), Is.EqualTo(overrideJson));
            AssertUnchanged(sourceJson, meshJson, probes);
        }

        [Test]
        public void ExpressionParameterDefaultsSelectTheDeclaredLowerPose()
        {
            var metadata = new VrChatExpressionMenu.Source { Controller = controller }; metadata.Defaults.Add("Face", 1);
            var expected = Native(controller, metadata.Defaults); Assert.That(expected, Is.EqualTo(70).Within(.01));
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1, metadata: metadata);
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(expected).Within(.01));
            AssertUnchanged(sourceJson, meshJson, probes);
        }

        [TestCase("external")]
        [TestCase("parameter curve")]
        [TestCase("other controller")]
        [TestCase("moving lower morph")]
        public void LowerPoseDependenciesAreValidatedBeforeRewritingTheEntry(string dependency)
        {
            var metadata = new VrChatExpressionMenu.Source { Controller = controller };
            var expectedMessage = "時間で変わる";
            if (dependency == "external") { metadata.ExternalParameters.Add("Face"); expectedMessage = "外部入力"; }
            else if (dependency == "moving lower morph")
                AnimationUtility.SetEditorCurve((AnimationClip)lower.motion,
                    EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"), AnimationCurve.Linear(0, 30, 30, 60));
            else
            {
                var clip = new AnimationClip { name = "Global Face writer" };
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "Face"),
                    dependency == "parameter curve" ? AnimationCurve.Linear(0, 0, 30, 1) : AnimationCurve.Constant(0, 1, 1));
                if (dependency == "parameter curve")
                {
                    AssetDatabase.AddObjectToAsset(clip, controller); controller.AddLayer("Changing parameter writer"); var layers = controller.layers;
                    layers[2].defaultWeight = 1; layers[2].stateMachine.defaultState = State(layers[2].stateMachine, "Writer", clip); controller.layers = layers;
                }
                else
                {
                    var other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Gesture.controller");
                    other.AddParameter("Face", AnimatorControllerParameterType.Int); AssetDatabase.AddObjectToAsset(clip, other);
                    other.layers[0].stateMachine.defaultState = State(other.layers[0].stateMachine, "Writer", clip);
                    metadata.OtherControllers.Add(other); expectedMessage = "FX以外";
                }
            }
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            var error = Assert.Throws<InvalidOperationException>(() =>
                VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1, metadata: metadata));
            Assert.That(error.Message, Does.Contain(expectedMessage));
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(80), "Rejected dependencies cannot leave a partially rewritten expression.");
            AssertUnchanged(sourceJson, meshJson, probes);
        }
    }
}
