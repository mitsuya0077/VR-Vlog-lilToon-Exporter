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
    public sealed class InferredStatePathTests
    {
        string folder;
        GameObject avatar;
        Mesh mesh;
        AnimatorController controller;
        AnimatorState neutral;

        [SetUp]
        public void SetUp()
        {
            var name = "__InferredStatePaths_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            var layers = controller.layers;
            layers[0].stateMachine.name = "Authored face root";
            layers[0].name = "Renamed FX layer";
            controller.layers = layers;
            controller.AddParameter("FaceChoice", AnimatorControllerParameterType.Bool);
            avatar = new GameObject("Avatar", typeof(Animator));
            var face = new GameObject("Face", typeof(SkinnedMeshRenderer)); face.transform.SetParent(avatar.transform, false);
            mesh = BaseShapeFixture.Create(); face.GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
            face.GetComponent<SkinnedMeshRenderer>().SetBlendShapeWeight(0, 25);
            neutral = State(layers[0].stateMachine, "Neutral", Clip("Neutral", 0));
            layers[0].stateMachine.defaultState = neutral;
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

        static void Transition(AnimatorState from, AnimatorState to, string parameter)
        {
            var transition = from.AddTransition(to); transition.duration = 0; transition.hasExitTime = false;
            transition.AddCondition(AnimatorConditionMode.If, 0, parameter);
        }

        (AnimatorState Face, string RelativePath) ConfigureFace(bool nested)
        {
            var root = controller.layers[0].stateMachine;
            if (!nested)
            {
                var face = State(root, "Selected face", Clip("Selected face", 85));
                Transition(neutral, face, "FaceChoice"); return (face, face.name);
            }
            controller.AddParameter("FaceMode", AnimatorControllerParameterType.Bool);
            var machine = root.AddStateMachine("Nested faces");
            var idle = State(machine, "Nested neutral", Clip("Nested neutral", 0)); machine.defaultState = idle;
            var enter = neutral.AddTransition(machine); enter.duration = 0; enter.hasExitTime = false;
            enter.AddCondition(AnimatorConditionMode.If, 0, "FaceMode");
            var selected = State(machine, "Selected face", Clip("Selected face", 85));
            Transition(idle, selected, "FaceChoice"); return (selected, machine.name + "." + selected.name);
        }

        Dictionary<string, float> Selection() => controller.parameters.ToDictionary(parameter => parameter.name, parameter => 1f);

        (int Hash, float Weight) NativeSelection(int layer)
        {
            AssetDatabase.SaveAssets();
            var clone = Object.Instantiate(avatar); clone.hideFlags = HideFlags.HideAndDontSave;
            var graph = PlayableGraph.Create("Original renamed-root FX oracle");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            try
            {
                var animator = clone.GetComponent<Animator>(); animator.runtimeAnimatorController = null;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.applyRootMotion = false; animator.fireEvents = false;
                // Use the authored controller directly, without exporter copies
                // or state-path validation, to establish Unity's actual hash.
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                var output = AnimationPlayableOutput.Create(graph, "Original FX", animator); output.SetSourcePlayable(playable);
                foreach (var parameter in controller.parameters) playable.SetBool(parameter.name, parameter.defaultBool);
                graph.Play(); graph.Evaluate(0f);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                foreach (var parameter in controller.parameters) playable.SetBool(parameter.name, true);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                Assert.That(playable.IsInTransition(layer), Is.False);
                return (playable.GetCurrentAnimatorStateInfo(layer).fullPathHash,
                    clone.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0));
            }
            finally { graph.Destroy(); Object.DestroyImmediate(clone); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RenamedLayerDiscoversItsReachableFaceUsingTheNativeMachineRoot(bool nested)
        {
            var face = ConfigureFace(nested); var native = NativeSelection(0);
            var layer = controller.layers[0];
            Assert.That(layer.stateMachine.name, Is.Not.EqualTo(layer.name));
            Assert.That(native.Hash, Is.EqualTo(Animator.StringToHash(layer.stateMachine.name + "." + face.RelativePath)));
            Assert.That(native.Weight, Is.EqualTo(85).Within(.01));
            var before = AssetDatabase.LoadAllAssetsAtPath(folder + "/FX.controller")
                .ToDictionary(asset => asset, asset => EditorJsonUtility.ToJson(asset));
            var source = new VrChatExpressionMenu.Source { Controller = controller };
            VrChatFxExpressions.Add(avatar, source);
            Assert.That(source.Entries.Select(value => value.Error), Is.All.Null);
            var entry = source.Entries.Single(value => value.Name == "FX / Selected face");
            Assert.That(entry.Name, Is.EqualTo("FX / Selected face")); Assert.That(entry.Error, Is.Null);
            Assert.That(entry.Values.Single(value => value.Shape == "Face size").Weight, Is.EqualTo(native.Weight).Within(.01));
            if (nested)
            {
                var nestedNeutral = source.Entries.Single(value => value.Name == "FX / Nested neutral");
                Assert.That(nestedNeutral.Parameters["FaceMode"], Is.EqualTo(1));
                Assert.That(nestedNeutral.Values.Single(value => value.Shape == "Face size").Weight, Is.EqualTo(0).Within(.01));
            }
            foreach (var pair in before) Assert.That(EditorJsonUtility.ToJson(pair.Key), Is.EqualTo(pair.Value), pair.Key.name);
            Assert.That(avatar.GetComponent<Animator>().runtimeAnimatorController, Is.Null);
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(25));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RenamedRootStillRejectsADifferentCandidateState(bool nested)
        {
            ConfigureFace(nested); var native = NativeSelection(0);
            var wrong = controller.layers[0].name + "." + (nested ? "Other nested faces.Selected face" : neutral.name);
            Assert.That(VrChatExpressionSampler.MatchesExpectedStatePath(controller, 0, wrong, native.Hash), Is.False);
            var metadata = new VrChatExpressionMenu.Source { Controller = controller };
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleFixed(avatar, controller,
                metadata.Defaults, Selection(), metadata: metadata, expectedLayer: 0, expectedStatePath: wrong));
            StringAssert.Contains("表情候補の状態に到達できません", error.Message);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SyncedCandidateUsesItsActualSourceMachineRootAndRejectsOtherStates(bool nested)
        {
            var face = ConfigureFace(nested);
            controller.AddLayer("Synced face layer");
            var layers = controller.layers; layers[1].syncedLayerIndex = 0; layers[1].defaultWeight = 1; controller.layers = layers;
            var native = NativeSelection(1);
            var expected = layers[1].name + "." + face.RelativePath;
            Assert.That(native.Hash, Is.Not.EqualTo(0)); Assert.That(native.Weight, Is.EqualTo(85).Within(.01));
            Assert.That(VrChatExpressionSampler.MatchesExpectedStatePath(controller, 1, expected, native.Hash), Is.True);
            Assert.That(VrChatExpressionSampler.MatchesExpectedStatePath(controller, 1, expected,
                Animator.StringToHash(layers[0].stateMachine.name + "." + face.RelativePath)), Is.True);
            Assert.That(VrChatExpressionSampler.MatchesExpectedStatePath(controller, 1,
                layers[1].name + "." + neutral.name, native.Hash), Is.False);
            var metadata = new VrChatExpressionMenu.Source { Controller = controller };
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleFixed(avatar, controller,
                metadata.Defaults, Selection(), metadata: metadata, expectedLayer: 1, expectedStatePath: expected));
            StringAssert.Contains("同期Animatorレイヤー", error.Message);
        }
    }
}
