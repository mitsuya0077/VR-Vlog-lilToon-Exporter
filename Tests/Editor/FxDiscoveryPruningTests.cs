using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class FxDiscoveryPruningTests
    {
        string folder;
        GameObject avatar;
        Mesh mesh;
        AnimatorController controller;
        VrChatExpressionMenu.Source source;

        [SetUp]
        public void SetUp()
        {
            var name = "__FxDiscoveryPruning_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            avatar = new GameObject("Avatar", typeof(Animator));
            var face = new GameObject("Face", typeof(SkinnedMeshRenderer)); face.transform.SetParent(avatar.transform, false);
            mesh = BaseShapeFixture.Create(); face.GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
            face.GetComponent<SkinnedMeshRenderer>().SetBlendShapeWeight(0, 25);
            var root = controller.layers[0].stateMachine;
            root.defaultState = State(root, "Neutral", Clip("Neutral", 0));
            source = new VrChatExpressionMenu.Source { Controller = controller };
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(avatar); Object.DestroyImmediate(mesh); AssetDatabase.DeleteAsset(folder);
        }

        AnimationClip Clip(string name, float value)
        {
            var clip = new AnimationClip { name = name };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                AnimationCurve.Constant(0, 1, value));
            AssetDatabase.AddObjectToAsset(clip, controller); return clip;
        }
        static AnimatorState State(AnimatorStateMachine machine, string name, Motion motion)
        {
            var state = machine.AddState(name); state.motion = motion; state.writeDefaultValues = false; return state;
        }
        static AnimatorStateTransition Any(AnimatorStateMachine machine, AnimatorState target)
        {
            var transition = machine.AddAnyStateTransition(target);
            transition.hasExitTime = false; transition.duration = 0; transition.canTransitionToSelf = false; return transition;
        }

        [Test]
        public void HundredsOfDormantHandBranchesCannotHideALaterCustomFace()
        {
            controller.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
            controller.AddParameter("HandOption", AnimatorControllerParameterType.Int);
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Bool);
            var root = controller.layers[0].stateMachine;
            var sharedHandClip = Clip("Shared hand face", 20);
            for (var index = 1; index <= 768; index++)
            {
                var hand = State(root, "Dormant hand " + index, sharedHandClip);
                var transition = Any(root, hand); transition.canTransitionToSelf = true;
                transition.AddCondition(AnimatorConditionMode.Equals, 1, "GestureRight");
                transition.AddCondition(AnimatorConditionMode.Equals, index, "HandOption");
            }
            var selected = State(root, "Later custom face", Clip("Custom face", 75));
            Any(root, selected).AddCondition(AnimatorConditionMode.If, 0, "FaceChoice");
            var before = AssetDatabase.LoadAllAssetsAtPath(folder + "/FX.controller")
                .ToDictionary(asset => asset, asset => EditorJsonUtility.ToJson(asset));
            VrChatFxExpressions.Add(avatar, source);
            Assert.That(source.Messages, Is.Empty, "Normal hand input proves every earlier conjunction false without expanding its priority exclusions.");
            var entry = source.Entries.Single(); Assert.That(entry.Error, Is.Null, entry.Error);
            Assert.That(entry.Name, Is.EqualTo("FX / Later custom face"));
            Assert.That(entry.Parameters["FaceChoice"], Is.EqualTo(1));
            var copy = Object.Instantiate(avatar); var graph = PlayableGraph.Create("Dormant branches independent native reference");
            try
            {
                var animator = copy.GetComponent<Animator>(); animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.fireEvents = false;
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                AnimationPlayableOutput.Create(graph, "Original graph", animator).SetSourcePlayable(playable);
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual); graph.Play(); graph.Evaluate(0);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60);
                playable.SetBool("FaceChoice", true);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60);
                Assert.That(playable.GetCurrentAnimatorStateInfo(0).fullPathHash,
                    Is.EqualTo(Animator.StringToHash(controller.layers[0].name + "." + selected.name)));
                Assert.That(entry.Values.Single(value => value.Shape == "Face size").Weight,
                    Is.EqualTo(copy.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0)).Within(.01));
            }
            finally { graph.Destroy(); Object.DestroyImmediate(copy); }
            foreach (var pair in before) Assert.That(EditorJsonUtility.ToJson(pair.Key), Is.EqualTo(pair.Value), pair.Key.name);
            Assert.That(avatar.GetComponent<Animator>().runtimeAnimatorController, Is.Null);
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh, Is.SameAs(mesh));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(25));
        }

        [TestCase("none", false)]
        [TestCase("Animator", false)]
        [TestCase("Animator", true)]
        [TestCase("Set", false)]
        [TestCase("Set", true)]
        [TestCase("Add", false)]
        [TestCase("Add", true)]
        [TestCase("Copy", false)]
        [TestCase("Copy", true)]
        [TestCase("Random", false)]
        [TestCase("Random", true)]
        [TestCase("callback", false)]
        [TestCase("callback", true)]
        [TestCase("conflict", true)]
        public void EveryPlayableWriterAndUnknownCallbackPreventsAnImmutableInputProof(string writer, bool otherPlayable)
        {
            controller.AddParameter("ContactValue", AnimatorControllerParameterType.Float); source.ExternalParameters.Add("ContactValue");
            controller.AddParameter(new AnimatorControllerParameter { name = "Guard", type = AnimatorControllerParameterType.Bool, defaultBool = true });
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Bool);
            var root = controller.layers[0].stateMachine;
            var prior = State(root, "Conditional prior", Clip("Prior", 20));
            var first = Any(root, prior); first.canTransitionToSelf = true;
            first.AddCondition(AnimatorConditionMode.Greater, .5f, "ContactValue"); first.AddCondition(AnimatorConditionMode.If, 0, "Guard");
            var selected = State(root, "Later face", Clip("Later", 75));
            Any(root, selected).AddCondition(AnimatorConditionMode.If, 0, "FaceChoice");
            if (writer != "none")
            {
                AnimatorController writerController;
                AnimatorState writerState;
                if (otherPlayable)
                {
                    writerController = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Other.controller");
                    writerController.AddParameter("ContactValue", writer == "conflict" ? AnimatorControllerParameterType.Int : AnimatorControllerParameterType.Float);
                    var machine = writerController.layers[0].stateMachine;
                    writerState = State(machine, "Producer", null); machine.defaultState = writerState; source.OtherControllers.Add(writerController);
                }
                else
                {
                    writerController = controller; controller.AddLayer("Producer");
                    var machine = controller.layers.Last().stateMachine;
                    writerState = State(machine, "Producer", null); machine.defaultState = writerState;
                }
                if (writer == "Animator")
                {
                    var clip = new AnimationClip { name = "Native input producer" };
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "ContactValue"), AnimationCurve.Constant(0, 1, 1));
                    AssetDatabase.AddObjectToAsset(clip, writerController); writerState.motion = clip;
                }
                else if (writer == "callback") writerState.AddStateMachineBehaviour<UnknownStateCallbackProbe>();
                else if (writer != "conflict")
                {
                    if (writer == "Copy") writerController.AddParameter("Input", AnimatorControllerParameterType.Float);
                    ParameterDriverExpressionTests.Driver(writerState, ParameterDriverExpressionTests.Op(writer, "ContactValue", 1, "Input"));
                }
            }
            // Inspect discovery before native capability checks: global writers
            // must retain both authored priority alternatives, including the
            // Guard=0 witness. Sampling still owns acceptance of the native pose.
            var method = typeof(VrChatFxExpressions).GetMethod("Discover", BindingFlags.Static | BindingFlags.NonPublic);
            var arguments = new object[method.GetParameters().Length];
            arguments[0] = source; arguments[1] = FixedExpressionContext.Create(controller, source.Defaults, source);
            var candidates = ((IEnumerable)method.Invoke(null, arguments)).Cast<object>().ToArray();
            var values = candidates.Select(candidate => (Dictionary<string, float>)candidate.GetType()
                .GetField("Values", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(candidate)).ToArray();
            Assert.That(values.Any(value => value.TryGetValue("Guard", out var guard) && guard == 0), Is.EqualTo(writer != "none"),
                "A fixed contact default is readonly only with complete compatible declarations and no producer or unknown callback in any playable.");
        }
    }
}
