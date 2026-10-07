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
    public sealed class SelectedScalarDominanceTests
    {
        string folder;
        GameObject avatar;
        Mesh mesh;
        AnimatorController controller;
        AnimatorState lower, selected;
        AnimationClip moving, face;
        static EditorCurveBinding Binding(string shape) => EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape." + shape);

        [SetUp]
        public void SetUp()
        {
            var name = "__ScalarDominance_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            avatar = new GameObject("Selected scalar dominance", typeof(Animator));
            var skin = new GameObject("Body", typeof(SkinnedMeshRenderer)).GetComponent<SkinnedMeshRenderer>(); skin.transform.SetParent(avatar.transform, false);
            mesh = new Mesh { name = "Prepared native underlay" }; mesh.vertices = new[] { Vector3.zero, Vector3.up, Vector3.right }; mesh.triangles = new[] { 0, 1, 2 };
            foreach (var shape in new[] { "Face", "Rest" }) mesh.AddBlendShapeFrame(shape, 100, Enumerable.Repeat(Vector3.up * .01f, 3).ToArray(), null, null);
            skin.sharedMesh = mesh; skin.SetBlendShapeWeight(0, 17); skin.SetBlendShapeWeight(1, 23);
            State(0, "Independent permanent rest", Clip("Rest", ("Rest", AnimationCurve.Constant(0, 1, 12))));
            AddLayer("Moving lower face");
            moving = Clip("Delayed mouth movement", ("Face", new AnimationCurve(new Keyframe(0, 0), new Keyframe(10, 0), new Keyframe(10.5f, 5), new Keyframe(12, 0))));
            lower = State(1, "Moving", moving);
            AddLayer("Selected upper face"); face = Clip("Held face", ("Face", AnimationCurve.Constant(0, 1, 75))); selected = State(2, "Selected", face);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(avatar); Object.DestroyImmediate(mesh); if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        void AddLayer(string name)
        {
            controller.AddLayer(name); var layers = controller.layers; layers[layers.Length - 1].defaultWeight = 1; controller.layers = layers;
        }

        AnimationClip Clip(string name, params (string Shape, AnimationCurve Curve)[] curves)
        {
            var clip = new AnimationClip { name = name }; AssetDatabase.AddObjectToAsset(clip, controller);
            foreach (var item in curves) AnimationUtility.SetEditorCurve(clip, Binding(item.Shape), item.Curve);
            return clip;
        }

        AnimatorState State(int layer, string name, AnimationClip clip)
        {
            var machine = controller.layers[layer].stateMachine; var state = machine.AddState(name); state.motion = clip; state.writeDefaultValues = false;
            if (machine.states.Length == 1) machine.defaultState = state; return state;
        }

        VrChatExpressionMenu.Entry Hold()
        {
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, face, entry);
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 2, false,
                metadata: new VrChatExpressionMenu.Source { Controller = controller }, sourceState: selected);
            return entry;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ExactUpperScalarDominanceRetainsIndependentRestAndSelectedCallbacks(bool callback)
        {
            if (callback)
            {
                controller.AddParameter("Choice", AnimatorControllerParameterType.Bool);
                ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "Choice", 1));
                AddLayer("Selected callback response");
                var normal = State(3, "Normal", Clip("Normal response", ("Face", AnimationCurve.Constant(0, 1, 75))));
                var response = State(3, "Response", Clip("Callback response", ("Face", AnimationCurve.Constant(0, 1, 20))));
                var transition = normal.AddTransition(response); transition.hasExitTime = false; transition.duration = 0; transition.AddCondition(AnimatorConditionMode.If, 0, "Choice");
            }
            var sourceBefore = ExportSourceFingerprint.Compute(avatar); var controllerBefore = EditorJsonUtility.ToJson(controller);
            if (!callback)
            {
                // Independent full native graph: the lower mouth keeps moving
                // while the held upper face and the unrelated rest stay exact.
                var clone = Object.Instantiate(avatar); var graph = PlayableGraph.Create("Selected dominance native oracle");
                try
                {
                    var animator = clone.GetComponent<Animator>(); animator.runtimeAnimatorController = null; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    graph.SetTimeUpdateMode(DirectorUpdateMode.Manual); var playable = AnimatorControllerPlayable.Create(graph, controller);
                    AnimationPlayableOutput.Create(graph, "Face", animator).SetSourcePlayable(playable); graph.Play(); graph.Evaluate(0);
                    for (var frame = 0; frame < 650; frame++) graph.Evaluate(1f / 60f);
                    var skin = clone.transform.Find("Body").GetComponent<SkinnedMeshRenderer>();
                    Assert.That(playable.GetCurrentAnimatorStateInfo(1).normalizedTime, Is.GreaterThan(.85));
                    Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(75).Within(.01)); Assert.That(skin.GetBlendShapeWeight(1), Is.EqualTo(12).Within(.01));
                }
                finally { graph.Destroy(); Object.DestroyImmediate(clone); }
            }
            var entry = Hold();
            Assert.That(entry.Values.Single(value => value.Shape == "Face").Weight, Is.EqualTo(callback ? 20 : 75).Within(.01));
            Assert.That(entry.Values.Single(value => value.Shape == "Rest").Weight, Is.EqualTo(12).Within(.01));
            Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(sourceBefore)); Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(controllerBefore));
        }

        [TestCase("fractional")]
        [TestCase("masked")]
        [TestCase("additive")]
        [TestCase("undominated moving rest")]
        public void ScalarDominanceNeverPreservesAnUndominatedMovingChannel(string kind)
        {
            var layers = controller.layers;
            if (kind == "fractional") layers[2].defaultWeight = .5f;
            else if (kind == "additive") layers[2].blendingMode = AnimatorLayerBlendingMode.Additive;
            else if (kind == "masked")
            {
                var mask = new AvatarMask(); AssetDatabase.CreateAsset(mask, folder + "/Mask.mask");
                mask.AddTransformPath(avatar.transform); layers[2].avatarMask = mask;
            }
            else AnimationUtility.SetEditorCurve(moving, Binding("Rest"), AnimationCurve.Linear(0, 0, 12, 50));
            controller.layers = layers;
            Assert.Catch<InvalidOperationException>(() => Hold());
        }

        [TestCase("curve")]
        [TestCase("event")]
        [TestCase("unknown callback")]
        [TestCase("malformed needed callback")]
        public void ExactDominanceCannotConcealInvalidPrograms(string kind)
        {
            if (kind == "curve")
            {
                AnimationUtility.SetEditorCurve(moving, Binding("Face"), new AnimationCurve(new Keyframe(0, 0, 0, float.NaN), new Keyframe(12, 5, 0, 0)));
                AnimationUtility.SetEditorCurve(moving, Binding("Rest"), AnimationCurve.Constant(0, 12, 0));
                Assert.That(float.IsNaN(AnimationUtility.GetEditorCurve(moving, Binding("Face")).keys[0].outTangent), Is.True,
                    "The native fixture must retain an invalid active interpolation tangent before testing dominance.");
                Assert.That(VrChatExpressionSampler.IsConstant(AnimationUtility.GetEditorCurve(moving, Binding("Face"))), Is.False);
            }
            else if (kind == "event") AnimationUtility.SetAnimationEvents(moving, new[] { new AnimationEvent { time = 10.5f, functionName = "UnknownEvent" } });
            else if (kind == "unknown callback") Assert.That(lower.AddStateMachineBehaviour<UnknownStateCallbackProbe>(), Is.Not.Null);
            else
            {
                controller.AddParameter("Clock", AnimatorControllerParameterType.Float); lower.timeParameterActive = true; lower.timeParameter = "Clock";
                ParameterDriverExpressionTests.Driver(lower, ParameterDriverExpressionTests.Op("Set", "Clock", float.NaN));
            }
            var before = EditorJsonUtility.ToJson(controller); Assert.Catch<InvalidOperationException>(() => Hold()); Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
        }
    }
}
