using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UniGLTF;
using UniVRM10;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
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

        AnimatorStateMachine EquivalentRelay(int transitionMode, bool writeDefaults, out AnimatorState other)
        {
            controller.AddParameter("Relay", AnimatorControllerParameterType.Float);
            var relay = AddLayer("Parameter relay");
            var clip = Clip("Shared constant relay");
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "Relay"), AnimationCurve.Constant(0, 1, 1));
            var initial = State(relay, "Initial", clip, writeDefaults);
            other = State(relay, "Other", clip, writeDefaults); relay.defaultState = initial;
            var transition = initial.AddTransition(other);
            transition.hasExitTime = transitionMode == 0; transition.exitTime = 10;
            transition.hasFixedDuration = true; transition.duration = transitionMode == 2 ? 10 : 0;
            transition.AddCondition(transitionMode == 0 ? AnimatorConditionMode.Less : AnimatorConditionMode.Equals,
                transitionMode == 0 ? .5f : 1, transitionMode == 0 ? "Relay" : "Menu");
            return relay;
        }

        // Match the sampler's default dwell and selection dwell using the
        // original controller, independently of its equivalence proof.
        (Dictionary<string, float> Weights, bool InTransition) NativeRelayPose(int selected)
        {
            var copy = Object.Instantiate(avatar);
            var graph = PlayableGraph.Create("Constant relay reference"); graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            try
            {
                var animator = copy.GetComponent<Animator>(); animator.runtimeAnimatorController = null;
                animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                for (var index = 0; index < controller.layers.Length; index++) playable.SetLayerWeight(index, index == 0 ? 1 : controller.layers[index].defaultWeight);
                var output = AnimationPlayableOutput.Create(graph, "Face", animator); output.SetSourcePlayable(playable);
                playable.SetInteger("Menu", 0); graph.Play();
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                playable.SetInteger("Menu", selected);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                var renderer = copy.GetComponentInChildren<SkinnedMeshRenderer>();
                return (Enumerable.Range(0, mesh.blendShapeCount).ToDictionary(mesh.GetBlendShapeName, renderer.GetBlendShapeWeight), playable.IsInTransition(1));
            }
            finally { if (graph.IsValid()) graph.Destroy(); Object.DestroyImmediate(copy); }
        }

        [TestCase(0, false)]
        [TestCase(0, true)]
        [TestCase(1, false)]
        [TestCase(1, true)]
        [TestCase(2, false)]
        [TestCase(2, true)]
        public void EquivalentConstantRelayPreservesNativeNeutralAndMenuEvenDuringTransitions(int transitionMode, bool writeDefaults)
        {
            Menu(controller.layers[0].stateMachine);
            EquivalentRelay(transitionMode, writeDefaults, out _); Permanent(AddLayer("Permanent pupil"));
            var original = EditorJsonUtility.ToJson(controller);
            var neutralReference = NativeRelayPose(0); var selectedReference = NativeRelayPose(1);
            Assert.That(neutralReference.InTransition, Is.False);
            Assert.That(selectedReference.InTransition, Is.EqualTo(transitionMode == 2), "The long-transition case must actually be sampled mid-transition.");
            var neutral = VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0));
            var selected = VrChatExpressionSampler.Sample(avatar, controller, Parameters(0), Parameters(1));
            foreach (var values in new[] { neutral, selected })
            {
                var expected = ReferenceEquals(values, neutral) ? neutralReference.Weights : selectedReference.Weights;
                foreach (var value in values) Assert.That(value.Weight, Is.EqualTo(expected[value.Shape]).Within(.01), value.Shape);
                Assert.That(values.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(100).Within(.01));
            }
            Assert.That(selected.Any(value => value.Shape == "Face size"), Is.True);
            var selectedClip = (AnimationClip)controller.layers[0].stateMachine.states.Single(child => child.state.name == "Selected").state.motion;
            foreach (var standalone in new[] { false, true })
            {
                var direct = new VrChatExpressionMenu.Entry { Name = standalone ? "Registered FaceEmo" : "Gesture" };
                VrChatGestureExpressions.ReadClip(avatar, selectedClip, direct);
                VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, direct, standalone ? (int?)null : 0);
                foreach (var value in direct.Values) Assert.That(value.Weight, Is.EqualTo(selectedReference.Weights[value.Shape]).Within(.01), direct.Name + ": " + value.Shape);
                Assert.That(direct.Values.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(100).Within(.01));
            }
            Assert.That(VrChatExpressionSampler.HasEquivalentConstantStates(controller, 1), Is.True);
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(original));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ParameterOrEmptyWriteDefaultsLayerPreservesStandaloneClipsWithoutPermanentMorphs(bool parameterCurve)
        {
            Menu(controller.layers[0].stateMachine);
            var clip = Clip(parameterCurve ? "Constant parameter only" : "Empty Write Defaults");
            if (parameterCurve)
            {
                controller.AddParameter("Relay", AnimatorControllerParameterType.Float);
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "Relay"), AnimationCurve.Constant(0, 1, 1));
            }
            var upper = AddLayer("Write Defaults only");
            upper.defaultState = State(upper, "Always", clip, writeDefaults: true);
            Assert.That(ExpressionDependencies.StationaryMorphBindings(controller).Count, Is.Zero,
                "This regression must reach the former early-return path without a permanent morph layer.");
            var selected = (AnimationClip)controller.layers[0].stateMachine.states.Single(child => child.state.name == "Selected").state.motion;
            var reference = NativeStandalonePose(avatar, controller, selected, "Face");
            var entry = new VrChatExpressionMenu.Entry { Name = "Registered face" };
            VrChatGestureExpressions.ReadClip(avatar, selected, entry);
            var authored = entry.Values.Single(value => value.Shape == "Face size").Weight;
            Assert.That(reference.Weights["Face size"], Is.EqualTo(authored).Within(.01),
                "This authored WD layer has no morph curves and must preserve the standalone expression.");
            var beforeController = EditorJsonUtility.ToJson(controller);
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry);
            Assert.That(entry.Error, Is.Null);
            Assert.That(entry.Values.Single(value => value.Shape == "Face size").Weight,
                Is.EqualTo(reference.Weights["Face size"]).Within(.01));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(beforeController));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(1), Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FractionalGestureWithoutPermanentMorphsKeepsEquivalentParameterOrEmptyBaseLayer(bool parameterCurve)
        {
            var baseClip = Clip(parameterCurve ? "Constant base parameter" : "Empty base Write Defaults");
            if (parameterCurve)
            {
                controller.AddParameter("Relay", AnimatorControllerParameterType.Float);
                AnimationUtility.SetEditorCurve(baseClip, EditorCurveBinding.FloatCurve("", typeof(Animator), "Relay"), AnimationCurve.Constant(0, 1, 1));
            }
            var baseMachine = controller.layers[0].stateMachine;
            baseMachine.defaultState = State(baseMachine, "Base", baseClip, writeDefaults: true);
            var gestureMachine = AddLayer("Fractional gesture"); Menu(gestureMachine);
            var layers = controller.layers; layers[1].defaultWeight = .5f; controller.layers = layers;
            Assert.That(ExpressionDependencies.StationaryMorphBindings(controller).Count, Is.Zero);
            Assert.That(VrChatExpressionSampler.HasEquivalentConstantStates(controller, 0), Is.True);
            var selected = (AnimationClip)gestureMachine.states.Single(child => child.state.name == "Selected").state.motion;
            var reference = NativePose(avatar, controller, "Face", new Dictionary<string, int> { ["Menu"] = 1 });
            var entry = new VrChatExpressionMenu.Entry { Name = "Fractional gesture" };
            VrChatGestureExpressions.ReadClip(avatar, selected, entry);
            var authored = entry.Values.Single(value => value.Shape == "Face size").Weight;
            Assert.That(authored, Is.EqualTo(75));
            Assert.That(Math.Abs(reference.Weights["Face size"] - authored), Is.GreaterThan(.01),
                "The authored non-base layer must change its clip weight in native Unity; the old early return would leave it at 75.");
            var beforeController = EditorJsonUtility.ToJson(controller);
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1);
            Assert.That(entry.Error, Is.Null);
            Assert.That(entry.Values.Single(value => value.Shape == "Face size").Weight,
                Is.EqualTo(reference.Weights["Face size"]).Within(.01));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(beforeController));
            Assert.That(controller.layers[1].defaultWeight, Is.EqualTo(.5f));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(1), Is.Zero);
        }

        [TestCase("Pet", AnimatorControllerParameterType.Float, 0f)]
        [TestCase("Pet", AnimatorControllerParameterType.Float, 1f)]
        [TestCase("CNST_TOUCH_EMOTE_LOCK_TRIGGER_L", AnimatorControllerParameterType.Bool, 0f)]
        [TestCase("CNST_TOUCH_EMOTE_LOCK_TRIGGER_R", AnimatorControllerParameterType.Bool, 1f)]
        [TestCase("AFK", AnimatorControllerParameterType.Bool, 1f)]
        public void DirectGestureProbeUsesNormalAndAuthoredContactInputs(string parameter, AnimatorControllerParameterType type, float authoredDefault)
        {
            controller.AddParameter(new AnimatorControllerParameter
            { name = parameter, type = type, defaultFloat = 1 - authoredDefault, defaultBool = authoredDefault == 0 });
            var baseMachine = controller.layers[0].stateMachine;
            var idle = State(baseMachine, "No contact", Clip("No contact", ("Pupil removal", 0)));
            var active = State(baseMachine, "Contact", Clip("Contact", ("Pupil removal", 100)));
            baseMachine.defaultState = idle;
            var transition = idle.AddTransition(active); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(type == AnimatorControllerParameterType.Bool ? AnimatorConditionMode.If : AnimatorConditionMode.Greater,
                type == AnimatorControllerParameterType.Bool ? 0 : .5f, parameter);
            var gestureMachine = AddLayer("Fractional gesture"); Menu(gestureMachine);
            var layers = controller.layers; layers[1].defaultWeight = .5f; controller.layers = layers;
            var metadata = new VrChatExpressionMenu.Source { Controller = controller };
            metadata.ExternalParameters.Add(parameter); metadata.Defaults[parameter] = authoredDefault;
            var normal = parameter == "AFK" ? 0 : authoredDefault;
            var reference = NativePose(avatar, controller, "Face", Parameters(1).ToDictionary(pair => pair.Key, pair => (int)pair.Value),
                new Dictionary<string, float> { [parameter] = normal });
            var selected = (AnimationClip)gestureMachine.states.Single(child => child.state.name == "Selected").state.motion;
            var entry = new VrChatExpressionMenu.Entry { Name = "Registered gesture" };
            VrChatGestureExpressions.ReadClip(avatar, selected, entry);
            var original = EditorJsonUtility.ToJson(controller);
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1, metadata: metadata);
            Assert.That(entry.Values.Select(value => value.Shape), Is.EquivalentTo(new[] { "Face size" }),
                "The selected gesture channel must survive; unrelated native-support channels remain outside capture.");
            foreach (var value in entry.Values)
                Assert.That(value.Weight, Is.EqualTo(reference.Weights[value.Shape]).Within(.01), value.Shape);
            Assert.That(entry.Messages.Any(message => message.Contains(parameter + "=")), Is.True);
            Assert.That(metadata.Defaults[parameter], Is.EqualTo(authoredDefault));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(original));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.Zero);
        }

        [TestCase("EyeHeightAsMeters", AnimatorControllerParameterType.Float)]
        [TestCase("Contact trigger", AnimatorControllerParameterType.Trigger)]
        public void DirectGestureProbeStillRejectsUnresolvedExternalInputs(string parameter, AnimatorControllerParameterType type)
        {
            controller.AddParameter(parameter, type);
            var baseMachine = controller.layers[0].stateMachine;
            var idle = State(baseMachine, "Unknown neutral", Clip("Unknown neutral", ("Face size", 20)));
            var active = State(baseMachine, "Unknown active", Clip("Unknown active", ("Face size", 90)));
            baseMachine.defaultState = idle;
            var transition = idle.AddTransition(active); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(type == AnimatorControllerParameterType.Trigger ? AnimatorConditionMode.If : AnimatorConditionMode.Greater,
                type == AnimatorControllerParameterType.Trigger ? 0 : .5f, parameter);
            var gestureMachine = AddLayer("Gesture"); Menu(gestureMachine);
            var layers = controller.layers; layers[1].defaultWeight = .5f; controller.layers = layers;
            var metadata = new VrChatExpressionMenu.Source { Controller = controller };
            metadata.ExternalParameters.Add(parameter);
            var zero = NativePose(avatar, controller, "Face", new Dictionary<string, int> { ["Menu"] = 1 },
                new Dictionary<string, float> { [parameter] = 0 });
            var changed = NativePose(avatar, controller, "Face", new Dictionary<string, int> { ["Menu"] = 1 },
                new Dictionary<string, float> { [parameter] = 1 });
            Assert.That(Math.Abs(zero.Weights["Face size"] - changed.Weights["Face size"]), Is.GreaterThan(10),
                "The unresolved input must change the selected native scalar through its lower fractional pose.");
            var selected = (AnimationClip)gestureMachine.states.Single(child => child.state.name == "Selected").state.motion;
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, selected, entry);
            var original = entry.Values.Select(value => (value.Path, value.Shape, value.Weight)).ToArray();
            Assert.That(Assert.Catch<InvalidOperationException>(() =>
                VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1, metadata: metadata)).Message,
                Does.Contain(parameter));
            Assert.That(entry.Values.Select(value => (value.Path, value.Shape, value.Weight)), Is.EqualTo(original));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DirectScalarSupportKeepsPreparedInfluencingBoneAndWardrobeMorphs(bool writeDefaults)
        {
            var renderer = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
            var bone = new GameObject("Influencing bone"); bone.transform.SetParent(avatar.transform, false);
            bone.transform.localRotation = Quaternion.Euler(0, 0, 25);
            renderer.bones = new[] { bone.transform }; renderer.rootBone = bone.transform;
            mesh.bindposes = new[] { bone.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix };
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, mesh.vertexCount).ToArray();
            renderer.SetBlendShapeWeight(0, 20); renderer.SetBlendShapeWeight(2, 35);
            controller.layers[0].stateMachine.defaultState = State(controller.layers[0].stateMachine, "Base", Clip("Base", ("Face size", 30)));
            var selectedMachine = AddLayer("Fractional player"); Menu(selectedMachine, writeDefaults: writeDefaults);
            var moving = Clip("Independent additive bone loop");
            AnimationUtility.SetEditorCurve(moving, EditorCurveBinding.FloatCurve("Influencing bone", typeof(Transform), "localEulerAnglesRaw.z"),
                AnimationCurve.Linear(0, 0, 1, 60));
            var settings = AnimationUtility.GetAnimationClipSettings(moving); settings.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(moving, settings);
            var support = AddLayer("Independent additive bone"); support.defaultState = State(support, "Loop", moving);
            var wardrobe = Clip("Prepared wardrobe", ("Pupil removal", 100));
            AnimationUtility.SetEditorCurve(wardrobe, EditorCurveBinding.FloatCurve("Influencing bone", typeof(Transform), "localEulerAnglesRaw.x"),
                AnimationCurve.Constant(0, 1, 40));
            var clothing = AddLayer("Wardrobe"); clothing.defaultState = State(clothing, "On", wardrobe, writeDefaults);
            var layers = controller.layers; layers[1].defaultWeight = .5f;
            layers[2].blendingMode = AnimatorLayerBlendingMode.Additive; layers[2].defaultWeight = .5f; controller.layers = layers;
            var reference = NativePose(avatar, controller, "Face", new Dictionary<string, int> { ["Menu"] = 1 });
            var selected = selectedMachine.states.Single(child => child.state.name == "Selected").state;
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var sourceController = EditorJsonUtility.ToJson(controller); var sourceMesh = EditorJsonUtility.ToJson(mesh);
            var rotation = bone.transform.localRotation; var before = WorldVertices(renderer);
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1, writeDefaults, sourceState: selected);
            Assert.That(entry.Values.Select(value => value.Shape), Is.EquivalentTo(new[] { "Face size" }),
                "A native wardrobe support layer cannot become an exported facial morph endpoint.");
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(reference.Weights["Face size"]).Within(.01));
            Assert.That(bone.transform.localRotation, Is.EqualTo(rotation));
            Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(20)); Assert.That(renderer.GetBlendShapeWeight(2), Is.EqualTo(35));
            AssertVertices(before, WorldVertices(renderer));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(sourceController));
            Assert.That(EditorJsonUtility.ToJson(mesh), Is.EqualTo(sourceMesh));
            var mixed = Clip("Explicit selected transform", ("Face size", 80));
            AnimationUtility.SetEditorCurve(mixed, EditorCurveBinding.FloatCurve("Influencing bone", typeof(Transform), "localEulerAnglesRaw.z"),
                AnimationCurve.Constant(0, 1, 40));
            Assert.Throws<InvalidOperationException>(() => VrChatGestureExpressions.ReadClip(avatar, mixed, new VrChatExpressionMenu.Entry()),
                "Only independent evaluation support is allowed; authored mixed selected clips remain unsupported.");
        }

        [Test]
        public void DirectScalarSupportCannotDisableTheCapturedRenderer()
        {
            controller.layers[0].stateMachine.defaultState = State(controller.layers[0].stateMachine, "Base", Clip("Base", ("Face size", 30)));
            var selectedMachine = AddLayer("Fractional player"); Menu(selectedMachine);
            var disable = Clip("Disable captured renderer");
            AnimationUtility.SetEditorCurve(disable, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "m_Enabled"),
                AnimationCurve.Constant(0, 1, 0));
            var support = AddLayer("Additive activation"); support.defaultState = State(support, "Disable", disable);
            var layers = controller.layers; layers[1].defaultWeight = .5f; layers[2].blendingMode = AnimatorLayerBlendingMode.Additive;
            layers[2].defaultWeight = .5f; controller.layers = layers;
            var selected = selectedMachine.states.Single(child => child.state.name == "Selected").state;
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var original = entry.Values.Single().Weight;
            Assert.That(Assert.Throws<InvalidOperationException>(() =>
                VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1, sourceState: selected)).Message,
                Does.Contain("m_Enabled"));
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(original));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().enabled, Is.True);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void DirectScalarProbeDefersUnknownAuthoredChannelsToPreparedResolution(bool generated, bool animated)
        {
            Permanent(controller.layers[0].stateMachine); var machine = AddLayer("Fractional direct player"); Menu(machine);
            var layers = controller.layers; layers[1].defaultWeight = .5f; controller.layers = layers;
            var selected = machine.states.Single(child => child.state.name == "Selected").state;
            AnimationUtility.SetEditorCurve((AnimationClip)selected.motion,
                EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Future authored shape"),
                animated ? AnimationCurve.Linear(0, 20, 1, 80) : AnimationCurve.Constant(0, 1, 20));
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1, sourceState: selected);
            Assert.That(entry.Values.Single(value => value.Shape == "Future authored shape").Weight, Is.EqualTo(20));
            Assert.That(entry.Values.Any(value => value.Shape == "Face size"), Is.True);
            if (animated) Assert.That(entry.Animation.Single().Curve.Evaluate(.5), Is.EqualTo(50).Within(1e-9));
            var menu = new VrChatExpressionMenu.Source(); menu.Entries.Add(entry);
            var copy = Object.Instantiate(avatar); var preparedMesh = Object.Instantiate(mesh);
            try
            {
                using var bindings = new PreparedExpressionBindings(copy, menu);
                if (generated) preparedMesh.AddBlendShapeFrame("Future authored shape", 100,
                    Enumerable.Repeat(Vector3.right * .01f, preparedMesh.vertexCount).ToArray(), null, null);
                copy.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh = preparedMesh;
                bindings.Capture(menu);
                Assert.That(entry.Error, Is.Null);
                Assert.That(entry.Values.Any(value => value.Shape == "Future authored shape"), Is.EqualTo(generated));
                Assert.That(entry.Animation.Any(value => value.Shape == "Future authored shape"), Is.EqualTo(generated && animated));
                Assert.That(entry.Values.Any(value => value.Shape == "Face size"), Is.True);
            }
            finally { Object.DestroyImmediate(copy); Object.DestroyImmediate(preparedMesh); }
        }

        [Test]
        public void ProvenBaseStateRetainsItsCallbackAndTheDynamicUpperNativeGraph()
        {
            controller.AddParameter("Face input", AnimatorControllerParameterType.Int); Menu(controller.layers[0].stateMachine);
            var selected = controller.layers[0].stateMachine.states.Single(child => child.state.name == "Selected").state;
            selected.motion = Clip("Base selected", ("Face size", 80));
            ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "Face input", 1));
            var upper = AddLayer("Dynamic upper face");
            var idle = State(upper, "Idle", Clip("Upper idle", ("Face size", 30)));
            var active = State(upper, "Active", Clip("Upper active", ("Face size", 20))); upper.defaultState = idle;
            var transition = idle.AddTransition(active); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "Face input");
            Assert.That(ExpressionDependencies.StationaryMorphBindings(controller), Is.Empty,
                "Both authored graphs must be non-stationary so the old base-slot early return is exercised.");
            var expected = NativePose(avatar, controller, "Face", new Dictionary<string, int> { ["Menu"] = 1, ["Face input"] = 1 });
            Assert.That(expected.Weights["Face size"], Is.EqualTo(20).Within(.01));
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var original = EditorJsonUtility.ToJson(controller);
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 0, sourceState: selected);
            Assert.That(entry.Values.Select(value => value.Shape), Is.EquivalentTo(new[] { "Face size" }));
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(expected.Weights["Face size"]).Within(.01));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(original));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.Zero);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void ProvenMovingDirectClipKeepsNativeSupportWithoutAnUnrelatedUpperExpressionReset(bool additive, bool writeDefaults)
        {
            controller.layers[0].stateMachine.defaultState = State(controller.layers[0].stateMachine, "Native lower pose",
                Clip("Native lower pose", ("Face size", 20)));
            var selectedMachine = AddLayer("Selected gesture"); Menu(selectedMachine, writeDefaults: writeDefaults);
            var selected = selectedMachine.states.Single(child => child.state.name == "Selected").state;
            var moving = (AnimationClip)selected.motion;
            var binding = EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size");
            AnimationUtility.SetEditorCurve(moving, binding, AnimationCurve.Linear(0, 20, 1, 80));
            controller.AddParameter("Other face selection", AnimatorControllerParameterType.Int);
            var competitor = AddLayer("Other registered face player");
            var reset = State(competitor, "DEFAULT", Clip("Other default face", ("Face size", 0)));
            var other = State(competitor, "BYPASS", Clip("Other selected face", ("Face size", 95)));
            competitor.defaultState = reset;
            var transition = reset.AddTransition(other); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "Other face selection");
            var support = AddLayer("Authored permanent support");
            support.defaultState = State(support, "Always", additive ?
                Clip("Additive authored support", ("Face size", 10), ("Pupil removal", 100)) :
                Clip("Permanent pupil", ("Pupil removal", 100)), writeDefaults);
            var layers = controller.layers; layers[1].defaultWeight = .5f;
            if (additive) { layers[3].blendingMode = AnimatorLayerBlendingMode.Additive; layers[3].defaultWeight = .5f; }
            controller.layers = layers;
            var referenceClips = new[] { 20f, 50f, 80f }.ToDictionary(value => value,
                value => Clip("Independent native selected " + value, ("Face size", value)));
            var expected = new Dictionary<float, Dictionary<string, float>>();
            var original = EditorJsonUtility.ToJson(controller);
            var fullNative = NativePose(avatar, controller, "Face", new Dictionary<string, int> { ["Menu"] = 1 });
            try
            {
                foreach (var pair in referenceClips)
                {
                    selected.motion = pair.Value;
                    expected.Add(pair.Key, NativePose(avatar, controller, "Face", new Dictionary<string, int> { ["Menu"] = 1 },
                        disabledLayers: new HashSet<int> { 2 }).Weights);
                }
            }
            finally { selected.motion = moving; }
            Assert.That(Math.Abs(expected[80]["Face size"] - fullNative.Weights["Face size"]), Is.GreaterThan(10),
                "The unrelated upper default must actually mask the authored animated face in the whole normal controller.");
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, moving, entry);
            Assert.That(entry.Animation.Select(value => value.Shape), Is.EquivalentTo(new[] { "Face size" }));
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1, writeDefaults, sourceState: selected);
            Assert.That(entry.Animation.Select(value => value.Shape), Is.EquivalentTo(new[] { "Face size" }),
                "The explicit moving face must survive rather than becoming a named zero-delta endpoint.");
            foreach (var sample in new[] { (Time: 0.0, Input: 20f), (Time: .5, Input: 50f), (Time: 1.0, Input: 80f) })
                Assert.That(entry.Animation.Single().Curve.Evaluate(sample.Time), Is.EqualTo(expected[sample.Input]["Face size"]).Within(.01));
            Assert.That(entry.Values.Single(value => value.Shape == "Pupil removal").Weight,
                Is.EqualTo(expected[20]["Pupil removal"]).Within(.01), "Permanent native support must survive competing face selection.");
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(original));
            Assert.That(AnimationUtility.GetEditorCurve(moving, binding).Evaluate(.5f), Is.EqualTo(50).Within(.01));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ProvenMovingDirectClipStillHonorsAProvenPermanentUpperOverride(bool writeDefaults)
        {
            Menu(controller.layers[0].stateMachine, writeDefaults: writeDefaults);
            var selected = controller.layers[0].stateMachine.states.Single(child => child.state.name == "Selected").state;
            var moving = (AnimationClip)selected.motion;
            AnimationUtility.SetEditorCurve(moving, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                AnimationCurve.Linear(0, 20, 1, 80));
            var permanent = AddLayer("Permanent explicit face override");
            permanent.defaultState = State(permanent, "Always", Clip("Permanent face", ("Face size", 35)), writeDefaults);
            var expected = NativePose(avatar, controller, "Face", new Dictionary<string, int> { ["Menu"] = 1 });
            var original = EditorJsonUtility.ToJson(controller);
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, moving, entry);
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 0, writeDefaults, sourceState: selected);
            Assert.That(entry.Values.Single(value => value.Shape == "Face size").Weight, Is.EqualTo(expected.Weights["Face size"]).Within(.01));
            Assert.That(expected.Weights["Face size"], Is.EqualTo(35).Within(.01));
            Assert.That(entry.Animation, Is.Empty, "A real permanent upper override retains its native priority.");
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(original));
        }

        [Test]
        public void ProvenMovingDirectClipRetainsTheConfiguredSingleStateBlendTreeSupport()
        {
            controller.layers[0].stateMachine.defaultState = State(controller.layers[0].stateMachine, "Lower pose",
                Clip("Lower pose", ("Face size", 20), ("Pupil removal", 0)));
            var player = AddLayer("Moving direct player"); Menu(player, writesPupil: true);
            var selected = player.states.Single(child => child.state.name == "Selected").state;
            var moving = (AnimationClip)selected.motion;
            AnimationUtility.SetEditorCurve(moving, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                AnimationCurve.Constant(0, 1, 0));
            AnimationUtility.SetEditorCurve(moving, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"),
                AnimationCurve.Linear(0, 0, 1, 80));
            controller.AddParameter(new AnimatorControllerParameter
            { name = "Configured size", type = AnimatorControllerParameterType.Float, defaultFloat = .25f });
            var configured = AddLayer("Configured size BlendTree");
            var tree = new BlendTree { name = "Authored size setting", blendType = BlendTreeType.Simple1D,
                blendParameter = "Configured size", useAutomaticThresholds = false };
            AssetDatabase.AddObjectToAsset(tree, controller);
            tree.AddChild(Clip("Small configured size", ("Face size", 20)), 0);
            tree.AddChild(Clip("Large configured size", ("Face size", 80)), 1);
            var configuredState = configured.AddState("Always configured"); configuredState.motion = tree;
            configuredState.writeDefaultValues = false; configured.defaultState = configuredState;
            var layers = controller.layers; layers[1].defaultWeight = .5f; controller.layers = layers;
            Assert.That(ExpressionDependencies.StationaryLayers(controller).ContainsKey(2), Is.False);
            Assert.That(VrChatExpressionSampler.HasEquivalentConstantStates(controller, 2), Is.False,
                "An always-active BlendTree configuration is not a constant-clip or alternate expression proof.");
            var referenceClips = new[] { 0f, 40f, 80f }.ToDictionary(value => value,
                value => Clip("Native direct phase " + value, ("Face size", 0), ("Pupil removal", value)));
            var expected = new Dictionary<float, Dictionary<string, float>>();
            var original = EditorJsonUtility.ToJson(controller);
            try
            {
                foreach (var pair in referenceClips)
                {
                    selected.motion = pair.Value;
                    expected.Add(pair.Key, NativePose(avatar, controller, "Face", new Dictionary<string, int> { ["Menu"] = 1 },
                        new Dictionary<string, float> { ["Configured size"] = .25f }).Weights);
                }
            }
            finally { selected.motion = moving; }
            Assert.That(expected[0]["Face size"], Is.EqualTo(35).Within(.01));
            Assert.That(expected[80]["Pupil removal"] - expected[0]["Pupil removal"], Is.GreaterThan(10),
                "Native configured support must leave the selected authored motion functional.");
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, moving, entry);
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1, sourceState: selected);
            Assert.That(entry.Values.Single(value => value.Shape == "Face size").Weight,
                Is.EqualTo(expected[0]["Face size"]).Within(.01), "Merged size settings retain their native upper priority.");
            Assert.That(entry.Animation.Select(value => value.Shape), Is.EquivalentTo(new[] { "Pupil removal" }));
            foreach (var sample in new[] { (Time: 0.0, Input: 0f), (Time: .5, Input: 40f), (Time: 1.0, Input: 80f) })
                Assert.That(entry.Animation.Single().Curve.Evaluate(sample.Time),
                    Is.EqualTo(expected[sample.Input]["Pupil removal"]).Within(.01));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(original));
            Assert.That(controller.parameters.Single(parameter => parameter.name == "Configured size").defaultFloat, Is.EqualTo(.25f));
        }

        [TestCase("none")]
        [TestCase("unused")]
        [TestCase("retained")]
        public void InactiveProvenStateKeepsAuthoredClipUnlessItsCallbacksAffectRetainedGraph(string influence)
        {
            controller.AddParameter("Unused callback", AnimatorControllerParameterType.Int);
            Menu(controller.layers[0].stateMachine);
            var player = AddLayer("Inactive direct player"); var selected = State(player, "Selected", Clip("Inactive selected", ("Face size", 80)));
            player.defaultState = selected; var layers = controller.layers; layers[1].defaultWeight = 0; controller.layers = layers;
            if (influence != "none") ParameterDriverExpressionTests.Driver(selected,
                ParameterDriverExpressionTests.Op("Set", influence == "retained" ? "Menu" : "Unused callback", 1));
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var original = EditorJsonUtility.ToJson(controller);
            if (influence == "retained")
                Assert.That(Assert.Catch<InvalidOperationException>(() =>
                    VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1, sourceState: selected)).Message,
                    Does.Contain("Parameter Driver").And.Contain("Menu"));
            else VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1, sourceState: selected);
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(80), "A dormant slot is never force-activated to replace its authored candidate.");
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(original));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.Zero);
        }

        [TestCase("different clip")]
        [TestCase("different write defaults")]
        [TestCase("changing parameter curve")]
        [TestCase("object curve")]
        [TestCase("event")]
        [TestCase("outside destination")]
        [TestCase("time parameter")]
        public void ConstantRelayProofRejectsOutputOrCallbackDifferences(string difference)
        {
            Menu(controller.layers[0].stateMachine);
            var relay = EquivalentRelay(0, true, out var other); Permanent(AddLayer("Permanent pupil"));
            var clip = (AnimationClip)other.motion;
            switch (difference)
            {
                case "different clip": other.motion = Clip("Different relay clip", ("Face size", 100)); break;
                case "different write defaults": other.writeDefaultValues = false; break;
                case "changing parameter curve":
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "Relay"), AnimationCurve.Linear(0, 1, 30, 0)); break;
                case "object curve":
                    AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve("Face", typeof(SkinnedMeshRenderer), "m_Mesh"),
                        new[] { new ObjectReferenceKeyframe { time = 0, value = mesh } }); break;
                case "event": AnimationUtility.SetAnimationEvents(clip, new[] { new AnimationEvent { time = 20, functionName = "AfterSampling" } }); break;
                case "outside destination": relay.defaultState.transitions[0].destinationState = null; relay.defaultState.transitions[0].isExit = true; break;
                case "time parameter": other.timeParameter = "Relay"; other.timeParameterActive = true; break;
            }
            Assert.That(VrChatExpressionSampler.HasEquivalentConstantStates(controller, 1), Is.False);
            Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0)));
        }

        [Test]
        public void ConstantRelayProofDoesNotAcceptAnSdkBehaviourEvenWhenClipsMatch()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAvatarParameterDriver"))
                .FirstOrDefault(candidate => candidate != null);
            if (type == null) Assert.Ignore("Install the real VRChat SDK for callback equivalence regression.");
            Menu(controller.layers[0].stateMachine);
            EquivalentRelay(0, true, out var other); Permanent(AddLayer("Permanent pupil"));
            other.AddStateMachineBehaviour(type);
            Assert.That(VrChatExpressionSampler.HasEquivalentConstantStates(controller, 1), Is.False);
            Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0)));
        }

        [Test]
        public void InstalledMaConstantParameterRelayKeepsMergedNeutralAndMenuPupilRemoval()
        {
            Type Installed(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(name)).FirstOrDefault(type => type != null);
            var mergeType = Installed("nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator");
            var rootType = Installed("nadena.dev.ndmf.runtime.components.NDMFAvatarRoot");
            var descriptorType = Installed("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            if (mergeType == null || rootType == null || descriptorType == null)
                Assert.Ignore("Install MA, NDMF and the real VRChat SDK for the generated constant-relay regression.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            var source = fixture.Source; source.AddComponent(rootType);
            var zeros = new Vector3[fixture.Mesh.vertexCount]; var delta = new Vector3[fixture.Mesh.vertexCount]; delta[0] = Vector3.up;
            fixture.Mesh.AddBlendShapeFrame("Face size", 100, delta, zeros, zeros);
            fixture.Mesh.AddBlendShapeFrame("Pupil removal", 100, delta, zeros, zeros);
            Menu(controller.layers[0].stateMachine);
            foreach (var clip in controller.animationClips.Distinct())
            {
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, binding); AnimationUtility.SetEditorCurve(clip, binding, null);
                    var mapped = binding; mapped.path = "Front"; AnimationUtility.SetEditorCurve(clip, mapped, curve);
                }
            }
            var permanent = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Merge.controller");
            var pupil = new AnimationClip { name = "Permanent pupil" }; AssetDatabase.AddObjectToAsset(pupil, permanent);
            AnimationUtility.SetEditorCurve(pupil, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"), AnimationCurve.Constant(0, 1, 100));
            permanent.layers[0].stateMachine.defaultState = State(permanent.layers[0].stateMachine, "Always", pupil);
            var descriptor = source.AddComponent(descriptorType);
            using (var data = new SerializedObject(descriptor))
            {
                data.FindProperty("customizeAnimationLayers").boolValue = true;
                var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = 1;
                var item = layers.GetArrayElementAtIndex(0); var type = item.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
                item.FindPropertyRelative("isDefault").boolValue = false; item.FindPropertyRelative("animatorController").objectReferenceValue = controller;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            var tool = new GameObject("Permanent pupil tool"); tool.transform.SetParent(source.transform, false);
            var merge = tool.AddComponent(mergeType); mergeType.GetField("animator").SetValue(merge, permanent);
            var pathMode = mergeType.GetField("pathMode"); pathMode.SetValue(merge, Enum.Parse(pathMode.FieldType, "Absolute"));
            mergeType.GetField("layerPriority").SetValue(merge, 100); mergeType.GetField("matchAvatarWriteDefaults").SetValue(merge, false);
            var sourceGraph = EditorJsonUtility.ToJson(controller); var sourceMesh = EditorJsonUtility.ToJson(fixture.Mesh);
            var clone = Object.Instantiate(source); clone.name = source.name;
            try
            {
                var evaluated = false;
                using (NdmfExportPreparation.Prepare(source, clone, afterTransforming: prepared =>
                {
                    var metadata = VrChatExpressionMenu.Read(clone);
                    var fx = ExpressionDependencies.Controller(metadata.Controller);
                    Assert.That(Enumerable.Range(0, fx.layers.Length).Any(index => fx.layers[index].stateMachine.states.Length >= 3 &&
                        VrChatExpressionSampler.HasEquivalentConstantStates(fx, index)), Is.True, "Real MA must generate the constant parameter relay with its default MMD setting.");
                    var neutralReference = NativePose(clone, fx, "Front", new Dictionary<string, int> { ["Menu"] = 0 });
                    var selectedReference = NativePose(clone, fx, "Front", new Dictionary<string, int> { ["Menu"] = 1 });
                    var neutral = VrChatExpressionSampler.SampleDefaults(clone, metadata.Controller, metadata.Defaults, metadata: metadata);
                    var selected = VrChatExpressionSampler.Sample(clone, metadata.Controller, metadata.Defaults, Parameters(1), metadata: metadata);
                    Assert.That(neutral.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(neutralReference.Weights["Pupil removal"]).Within(.01));
                    Assert.That(selected.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(100).Within(.01));
                    foreach (var value in selected) Assert.That(value.Weight, Is.EqualTo(selectedReference.Weights[value.Shape]).Within(.01), value.Shape);
                    evaluated = true;
                })) { }
                Assert.That(evaluated, Is.True);
                Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(sourceGraph)); Assert.That(EditorJsonUtility.ToJson(fixture.Mesh), Is.EqualTo(sourceMesh));
                Assert.That(source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.Zero);
            }
            finally { Object.DestroyImmediate(clone); }
        }

        // Independent native Animator playback provides the expected result;
        // it does not use the export sampler or its reconstructed probe graph.
        static (Dictionary<string, float> Weights, Vector3[] Vertices) NativePose(GameObject source, AnimatorController fx,
            string path, IDictionary<string, int> parameters, IDictionary<string, float> fixedInputs = null, ISet<int> disabledLayers = null)
        {
            var copy = Object.Instantiate(source);
            var graph = PlayableGraph.Create("Weighted FX regression reference");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            try
            {
                foreach (var behaviour in copy.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                var animator = copy.GetComponent<Animator>(); animator.enabled = true;
                animator.runtimeAnimatorController = null; animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                var playable = AnimatorControllerPlayable.Create(graph, fx);
                for (var index = 0; index < fx.layers.Length; index++)
                    playable.SetLayerWeight(index, disabledLayers?.Contains(index) == true ? 0 : index == 0 ? 1 : fx.layers[index].defaultWeight);
                var output = AnimationPlayableOutput.Create(graph, "Face", animator); output.SetSourcePlayable(playable);
                if (fixedInputs != null)
                    foreach (var input in fixedInputs)
                        switch (fx.parameters.Single(parameter => parameter.name == input.Key).type)
                        {
                            case AnimatorControllerParameterType.Bool: playable.SetBool(input.Key, input.Value != 0); break;
                            case AnimatorControllerParameterType.Int: playable.SetInteger(input.Key, (int)input.Value); break;
                            case AnimatorControllerParameterType.Float: playable.SetFloat(input.Key, input.Value); break;
                            case AnimatorControllerParameterType.Trigger:
                                if (input.Value != 0) playable.SetTrigger(input.Key); else playable.ResetTrigger(input.Key);
                                break;
                            default: throw new InvalidOperationException("The native reference requires a supported input type.");
                        }
                graph.Play(); graph.Evaluate(0);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                foreach (var parameter in parameters) playable.SetInteger(parameter.Key, parameter.Value);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                var renderer = copy.transform.Find(path).GetComponent<SkinnedMeshRenderer>();
                var weights = Enumerable.Range(0, renderer.sharedMesh.blendShapeCount)
                    .ToDictionary(renderer.sharedMesh.GetBlendShapeName, renderer.GetBlendShapeWeight);
                return (weights, WorldVertices(renderer));
            }
            finally { if (graph.IsValid()) graph.Destroy(); Object.DestroyImmediate(copy); }
        }

        // Registered FaceEmo clips are a WD-Off base below the authored
        // permanent FX layers. They do not inherit the selected menu state's
        // WD setting. Replay that authored configuration independently of the
        // export sampler's scalar response probes.
        (Dictionary<string, float> Weights, Vector3[] Vertices) NativeStandalonePose(GameObject source, AnimatorController fx, AnimationClip clip, string path)
        {
            var reference = AnimatorController.CreateAnimatorControllerAtPath(folder + "/StandaloneReference.controller");
            reference.parameters = fx.parameters.Select(parameter => new AnimatorControllerParameter
            {
                name = parameter.name, type = parameter.type, defaultBool = parameter.defaultBool,
                defaultInt = parameter.defaultInt, defaultFloat = parameter.defaultFloat
            }).ToArray();
            var baseMachine = reference.layers[0].stateMachine;
            baseMachine.defaultState = State(baseMachine, "Registered expression", clip);
            foreach (var original in fx.layers.Skip(1))
            {
                Assert.That(original.stateMachine.states.Length, Is.EqualTo(1), "This reference owns only the fixture's stationary upper layers.");
                reference.AddLayer(original.name); var layers = reference.layers; var layer = layers[layers.Length - 1];
                layer.defaultWeight = original.defaultWeight; layer.blendingMode = original.blendingMode; layer.avatarMask = original.avatarMask;
                var state = original.stateMachine.defaultState;
                layer.stateMachine.defaultState = State(layer.stateMachine, "Authored permanent", (AnimationClip)state.motion, state.writeDefaultValues);
                reference.layers = layers;
            }
            return NativePose(source, reference, path, new Dictionary<string, int>());
        }

        static Vector3[] WorldVertices(SkinnedMeshRenderer renderer)
        {
            var baked = new Mesh();
            try { renderer.BakeMesh(baked, false); return baked.vertices.Select(renderer.transform.TransformPoint).ToArray(); }
            finally { Object.DestroyImmediate(baked); }
        }

        static void AssertVertices(Vector3[] expected, Vector3[] actual)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            for (var index = 0; index < expected.Length; index++)
                Assert.That(Vector3.Distance(expected[index], actual[index]), Is.LessThan(.0005f), "Vertex " + index);
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task SourceFrameRangePolicyPreservesNativeClampedGeometryAndUnlimitedAuthoredMath(bool legacyClamp)
        {
            var previous = PlayerSettings.legacyClampBlendShapeWeights;
            try
            {
                PlayerSettings.legacyClampBlendShapeWeights = legacyClamp;
                await BlendShapeSettingsTestSupport.WaitForEditorUpdates();
                using var fixture = new AttachmentConnectionTests.Fixture();
                var originalVertices = fixture.Mesh.vertices;
                var state = State(controller.layers[0].stateMachine, "Native range oracle", null);
                controller.layers[0].stateMachine.defaultState = state;
                foreach (var frames in new[] { new[] { 100f }, new[] { -50f }, new[] { 20f, 80f }, new[] { -20f, 80f },
                    new[] { -100f, -20f }, new[] { 50f, 200f } })
                {
                    fixture.Mesh.ClearBlendShapes();
                    foreach (var frame in frames)
                        fixture.Mesh.AddBlendShapeFrame("Range oracle", frame,
                            Enumerable.Repeat(Vector3.up * (frame / 100f), fixture.Mesh.vertexCount).ToArray(),
                            new Vector3[fixture.Mesh.vertexCount], new Vector3[fixture.Mesh.vertexCount]);
                    var inputs = new[] { -200f, -50f, 0f, 10f, 20f, 50f, 80f, 100f, 102.5f, 150f, 200f, 250f };
                    var native = new Dictionary<float, Vector3[]>();
                    foreach (var input in inputs)
                    {
                        var clip = new AnimationClip { name = "Native range " + input };
                        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Range oracle"),
                            AnimationCurve.Constant(0, 1, input));
                        AssetDatabase.AddObjectToAsset(clip, controller); state.motion = clip;
                        var pose = NativePose(fixture.Source, controller, "Front", new Dictionary<string, int>());
                        Assert.That(pose.Weights["Range oracle"], Is.EqualTo(input).Within(.001), "Native scalar remains unbounded");
                        native.Add(input, pose.Vertices);
                        if (legacyClamp)
                        {
                            // This bound was measured independently with native
                            // Animator playback and direct BakeMesh in Unity 2022.3.
                            // A positive first frame does not exclude zero.
                            // A sole negative frame is inert under legacy
                            // clamp; multiple negative frames use their final
                            // authored frame as the upper bound.
                            var effective = frames.Length == 1 && frames[0] < 0 ? 0
                                : Math.Min(frames.Last(), Math.Max(0f, input));
                            AssertVertices(originalVertices.Select(vertex => vertex + Vector3.up * (effective / 100f)).ToArray(), pose.Vertices);
                        }
                    }
                    foreach (var rest in new[] { -50f, 150f })
                    {
                        var prepared = Object.Instantiate(fixture.Source);
                        var temporary = new List<Mesh>();
                        try
                        {
                            prepared.GetComponent<Animator>().enabled = false;
                            var renderer = prepared.transform.Find("Front").GetComponent<SkinnedMeshRenderer>();
                            renderer.SetBlendShapeWeight(0, rest);
                            AvatarBaseShape.NormalizeSourceWeights(prepared, legacyClamp);
                            var effectiveRest = legacyClamp
                                ? (frames.Length == 1 && frames[0] < 0 ? 0 : Math.Min(frames.Last(), Math.Max(0f, rest)))
                                : rest;
                            Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(effectiveRest));
                            AvatarBaseShape.Preserve(prepared, prepared, temporary, null);
                            // Zero native rest needs no rebase. The baker still
                            // owns a mesh copy before appending expression data.
                            if (ReferenceEquals(renderer.sharedMesh, fixture.Mesh))
                            {
                                renderer.sharedMesh = Object.Instantiate(fixture.Mesh);
                                temporary.Add(renderer.sharedMesh);
                            }
                            Vector3[] Expected(float input) => legacyClamp ? native[input]
                                : originalVertices.Select(vertex => vertex + Vector3.up * (input / 100f)).ToArray();
                            AssertVertices(Expected(rest), WorldVertices(renderer));
                            foreach (var input in inputs)
                            {
                                var target = renderer.sharedMesh;
                                var expression = target.blendShapeCount;
                                AvatarBaseShape.AppendExpression(fixture.Mesh, target, "Static " + input,
                                    new[] { effectiveRest }, new[] { input }, legacyClamp);
                                renderer.SetBlendShapeWeight(expression, 100);
                                AssertVertices(Expected(input), WorldVertices(renderer));
                                renderer.SetBlendShapeWeight(expression, 0);
                                var animated = target.blendShapeCount;
                                AvatarBaseShape.AppendAnimatedShape(fixture.Mesh, target, "Animated " + input,
                                    0, rest, input, legacyClamp);
                                renderer.SetBlendShapeWeight(animated, 100);
                                AssertVertices(Expected(input), WorldVertices(renderer));
                                renderer.SetBlendShapeWeight(animated, 0);
                            }
                        }
                        finally
                        {
                            Object.DestroyImmediate(prepared);
                            foreach (var owned in temporary) Object.DestroyImmediate(owned);
                        }
                    }
                    Assert.That(fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>().sharedMesh, Is.SameAs(fixture.Mesh));
                    Assert.That(fixture.Mesh.vertices, Is.EqualTo(originalVertices));
                }
                // Setting false declares the existing unlimited export math.
                // Its arithmetic remains checked directly alongside the true
                // policy's independent native playback/geometry oracle.
            }
            finally
            {
                PlayerSettings.legacyClampBlendShapeWeights = previous;
                await BlendShapeSettingsTestSupport.WaitForEditorUpdates();
            }
        }

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
        public void FractionalStationaryFxUsesPreparedBaseOnceForMenuGestureAndFaceEmo(bool additive, bool writeDefaults)
        {
            var descriptorType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor"))
                .FirstOrDefault(type => type != null);
            if (descriptorType == null) Assert.Ignore("Install the real VRChat SDK for neutral FX integration.");
            Menu(controller.layers[0].stateMachine, writeDefaults: writeDefaults);
            // Establish the pupil channel in the lower native animation pose.
            // A channel introduced only by a disjoint additive layer may be
            // inert on an Editor backend; this paired fixture must exercise
            // an actual additive contribution on an existing channel.
            foreach (var child in controller.layers[0].stateMachine.states)
                AnimationUtility.SetEditorCurve((AnimationClip)child.state.motion,
                    EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"),
                    AnimationCurve.Constant(0, 1, 0));
            Permanent(AddLayer("Weighted permanent"), writeDefaults);
            if (additive)
            {
                // Author a zero additive reference instead of inheriting the
                // constant clip's first-frame reference, which can cancel its
                // pupil curve completely in native Mecanim evaluation.
                var permanentClip = (AnimationClip)controller.layers[1].stateMachine.defaultState.motion;
                var referenceClip = Clip("Authored zero additive reference", ("Pupil removal", 0));
                var settings = AnimationUtility.GetAnimationClipSettings(permanentClip);
                settings.hasAdditiveReferencePose = true;
                settings.additiveReferencePoseClip = referenceClip;
                settings.additiveReferencePoseTime = 0;
                AnimationUtility.SetAnimationClipSettings(permanentClip, settings);
                AnimationUtility.SetAdditiveReferencePose(permanentClip, referenceClip, 0);
            }
            var layers = controller.layers; layers[1].defaultWeight = .5f;
            layers[1].blendingMode = additive ? AnimatorLayerBlendingMode.Additive : AnimatorLayerBlendingMode.Override; controller.layers = layers;
            var sourceSkin = avatar.GetComponentInChildren<SkinnedMeshRenderer>(); sourceSkin.SetBlendShapeWeight(2, 20);
            var descriptor = avatar.AddComponent(descriptorType);
            using (var data = new SerializedObject(descriptor))
            {
                data.FindProperty("customizeAnimationLayers").boolValue = true;
                var descriptorLayers = data.FindProperty("baseAnimationLayers"); descriptorLayers.arraySize = 1;
                var item = descriptorLayers.GetArrayElementAtIndex(0);
                var type = item.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
                item.FindPropertyRelative("isDefault").boolValue = false;
                item.FindPropertyRelative("animatorController").objectReferenceValue = controller;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            var neutral = NativePose(avatar, controller, "Face", new Dictionary<string, int> { ["Menu"] = 0 });
            if (additive)
            {
                var withoutAdditive = NativePose(avatar, controller, "Face", new Dictionary<string, int> { ["Menu"] = 0 },
                    disabledLayers: new HashSet<int> { 1 });
                Assert.That(Math.Abs(neutral.Weights["Pupil removal"] - withoutAdditive.Weights["Pupil removal"]), Is.GreaterThan(.01),
                    "The authored zero-reference additive fixture must change the independent native pupil pose: full=" +
                    neutral.Weights["Pupil removal"] + ", disabled=" + withoutAdditive.Weights["Pupil removal"]);
            }
            var expected = NativePose(avatar, controller, "Face", new Dictionary<string, int> { ["Menu"] = 1 });
            var clip = (AnimationClip)controller.layers[0].stateMachine.states.Single(child => child.state.name == "Selected").state.motion;
            var registeredClip = Object.Instantiate(clip); registeredClip.name = "Weighted registered face";
            AssetDatabase.CreateAsset(registeredClip, folder + "/WeightedFaceEmo.anim");
            var standaloneExpected = NativeStandalonePose(avatar, controller, registeredClip, "Face");
            var registered = new { Modes = new[] { new { DisplayName = "Weighted registered", ChangeDefaultFace = true,
                Animation = new { GUID = AssetDatabase.AssetPathToGUID(folder + "/WeightedFaceEmo.anim") } } } };
            var clone = Object.Instantiate(avatar);
            try
            {
                var bindings = FaceEmoExpressions.CaptureRegistered(clone, registered);
                var menu = VrChatExpressionSampler.Sample(clone, controller, Parameters(0), Parameters(1));
                var gesture = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(clone, clip, gesture);
                VrChatExpressionSampler.ApplyPermanentOverrides(clone, controller, gesture, 0, writeDefaults);
                var faceEmo = new VrChatExpressionMenu.Source { Controller = controller }; var serial = 0;
                FaceEmoExpressions.ReadRegistered(clone, registered, "FaceEmo", faceEmo, ref serial, new HashSet<object>(), 0, bindings: bindings);
                Assert.That(faceEmo.Entries.Single().Error, Is.Null);
                foreach (var route in new[] { (Name: "Menu", Values: menu, Expected: expected),
                    (Name: "Gesture", Values: gesture.Values, Expected: expected),
                    (Name: "FaceEmo", Values: faceEmo.Entries.Single().Values, Expected: standaloneExpected) })
                {
                    Assert.That(route.Values.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(route.Expected.Weights["Pupil removal"]).Within(.01), route.Name + ": pupil");
                    Assert.That(route.Values.Single(value => value.Shape == "Face size").Weight, Is.EqualTo(route.Expected.Weights["Face size"]).Within(.01), route.Name + ": face size");
                }
                Assert.That(clone.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.EqualTo(20), "Analysis must not replace its own input baseline.");
                VrChatExpressionSampler.ApplyMergedDefaults(avatar, clone);
                Assert.That(clone.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.EqualTo(neutral.Weights["Pupil removal"]).Within(.01));
                Assert.That(sourceSkin.GetBlendShapeWeight(2), Is.EqualTo(20));
                Assert.That(controller.layers[1].defaultWeight, Is.EqualTo(.5f));
            }
            finally { Object.DestroyImmediate(clone); }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void NeutralWeightedUpperLayerKeepsMovingBaseWithoutBakingUnrelatedMorphs(bool additive, bool writeDefaults)
        {
            var descriptorType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor"))
                .FirstOrDefault(type => type != null);
            if (descriptorType == null) Assert.Ignore("Install the real VRChat SDK for neutral FX integration.");
            var baseClip = Clip("Moving base face");
            AnimationUtility.SetEditorCurve(baseClip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                AnimationCurve.Linear(0, 0, 30, 100));
            var baseMachine = controller.layers[0].stateMachine;
            baseMachine.defaultState = State(baseMachine, "Moving face", baseClip);
            Permanent(AddLayer("Fractional pupil"), writeDefaults);
            var layers = controller.layers; layers[1].defaultWeight = .5f;
            layers[1].blendingMode = additive ? AnimatorLayerBlendingMode.Additive : AnimatorLayerBlendingMode.Override;
            controller.layers = layers;
            var sourceSkin = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
            sourceSkin.SetBlendShapeWeight(1, 12); sourceSkin.SetBlendShapeWeight(2, 20);
            var descriptor = avatar.AddComponent(descriptorType);
            using (var data = new SerializedObject(descriptor))
            {
                data.FindProperty("customizeAnimationLayers").boolValue = true;
                var descriptorLayers = data.FindProperty("baseAnimationLayers"); descriptorLayers.arraySize = 1;
                var item = descriptorLayers.GetArrayElementAtIndex(0);
                var type = item.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
                item.FindPropertyRelative("isDefault").boolValue = false;
                item.FindPropertyRelative("animatorController").objectReferenceValue = controller;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            var originalController = EditorJsonUtility.ToJson(controller);
            var reference = NativePose(avatar, controller, "Face", new Dictionary<string, int>());
            Assert.That(Math.Abs(reference.Weights["Face size"] - 12), Is.GreaterThan(.01), "The native base curve must actually move.");
            var neutral = VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0));
            Assert.That(neutral.Select(value => value.Shape), Is.EqualTo(new[] { "Pupil removal" }));
            Assert.That(neutral.Single().Weight, Is.EqualTo(reference.Weights["Pupil removal"]).Within(.01));
            var clone = Object.Instantiate(avatar);
            try
            {
                VrChatExpressionSampler.ApplyMergedDefaults(avatar, clone);
                var skin = clone.GetComponentInChildren<SkinnedMeshRenderer>();
                Assert.That(skin.GetBlendShapeWeight(2), Is.EqualTo(reference.Weights["Pupil removal"]).Within(.01));
                Assert.That(skin.GetBlendShapeWeight(1), Is.EqualTo(12), "The unrelated moving face must not be baked into the neutral mesh.");
                Assert.That(skin.sharedMesh, Is.SameAs(mesh));
                Assert.That(sourceSkin.GetBlendShapeWeight(1), Is.EqualTo(12));
                Assert.That(sourceSkin.GetBlendShapeWeight(2), Is.EqualTo(20));
                Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(originalController));
            }
            finally { Object.DestroyImmediate(clone); }
        }

        [TestCase(.5f, false)]
        [TestCase(.5f, true)]
        [TestCase(1f, false)]
        [TestCase(1f, true)]
        public void NeutralAdditiveDisjointMorphGroupsMatchTheAuthoredNativeController(float weight, bool writeDefaults)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            using var menus = new MenuImportSdkRecoveryTests.SdkFixture(fixture.Source);
            var fx = (AnimatorController)VrChatExpressionMenu.Read(fixture.Source, new VrChatMenuImportPolicy { SkipAll = true }).Controller;
            var delta = new Vector3[fixture.Mesh.vertexCount]; delta[0] = Vector3.up * .1f;
            fixture.Mesh.AddBlendShapeFrame("Pupil removal", 100, delta, new Vector3[delta.Length], new Vector3[delta.Length]);
            var fixedClip = new AnimationClip { name = "Disjoint additive pupil" };
            AnimationUtility.SetEditorCurve(fixedClip, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"),
                AnimationCurve.Constant(0, 1, 100));
            AssetDatabase.AddObjectToAsset(fixedClip, fx);
            fx.AddLayer("Disjoint additive pupil");
            var layers = fx.layers; var layer = layers[layers.Length - 1];
            layer.defaultWeight = weight; layer.blendingMode = AnimatorLayerBlendingMode.Additive;
            layer.stateMachine.defaultState = State(layer.stateMachine, "Always", fixedClip, writeDefaults); fx.layers = layers;
            var sourceSkin = fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>();
            var beforeMesh = EditorJsonUtility.ToJson(fixture.Mesh);
            var beforeController = EditorJsonUtility.ToJson(fx);
            var beforeDescriptor = EditorJsonUtility.ToJson(menus.Descriptor);
            var expected = NativePose(fixture.Source, fx, "Front", new Dictionary<string, int> { ["Face"] = 0, ["GestureRight"] = 0 });
            // A property introduced only by this disjoint additive motion can
            // be inert in native playback. Preserve that result, as well as
            // any implicit contribution to the separate Hair detail group.
            // The paired fractional multi-route fixture above independently
            // proves an additive effect on an established lower channel.
            var neutral = NeutralShapeSampler.Sample(fixture.Source);
            Assert.That(neutral.Select(value => value.Path + "/" + value.Shape),
                Is.EquivalentTo(new[] { "Front/Hair detail", "Front/Pupil removal" }));
            foreach (var value in neutral)
                Assert.That(value.Weight, Is.EqualTo(expected.Weights[value.Shape]).Within(.01), value.Path + "/" + value.Shape);
            Assert.That(sourceSkin.GetBlendShapeWeight(0), Is.EqualTo(35));
            Assert.That(sourceSkin.GetBlendShapeWeight(1), Is.Zero);
            Assert.That(EditorJsonUtility.ToJson(fixture.Mesh), Is.EqualTo(beforeMesh));
            Assert.That(EditorJsonUtility.ToJson(fx), Is.EqualTo(beforeController));
            Assert.That(EditorJsonUtility.ToJson(menus.Descriptor), Is.EqualTo(beforeDescriptor));
        }

        [Test]
        public void NeutralAdditiveAutomaticSupportKeepsNativeDefaultsWithoutBakingMovingBlink()
        {
            controller.layers[0].stateMachine.defaultState = State(controller.layers[0].stateMachine, "Authored face",
                Clip("Authored face", ("Face size", 35)));
            var blink = Clip("Live additive blink");
            var blinkBinding = EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink");
            AnimationUtility.SetEditorCurve(blink, blinkBinding, AnimationCurve.Linear(0, 0, 30, 100));
            var machine = AddLayer("Live additive blink"); machine.defaultState = State(machine, "Live blink", blink);
            var layers = controller.layers; layers[1].defaultWeight = .5f;
            layers[1].blendingMode = AnimatorLayerBlendingMode.Additive; controller.layers = layers;
            var sourceSkin = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
            sourceSkin.SetBlendShapeWeight(0, 7); sourceSkin.SetBlendShapeWeight(1, 35);
            var metadata = new VrChatExpressionMenu.Source { Controller = controller };
            var automatic = new HashSet<EditorCurveBinding> { blinkBinding };
            var roots = ExpressionDependencies.NeutralRoots(controller, null, metadata, automatic).Single();
            var dependencies = ExpressionDependencies.AnalyzeNeutral(controller, roots, null, metadata, automatic);
            Assert.That(dependencies.NativeSupportLayers, Does.Contain(1),
                "Removing an automatic morph from capture must preserve its native additive stream.");
            Assert.That(dependencies.Morphs.Contains(blinkBinding), Is.False);
            var beforeController = EditorJsonUtility.ToJson(controller);
            var expected = NativePose(avatar, controller, "Face", new Dictionary<string, int>());
            var neutral = VrChatExpressionSampler.SampleNeutral(avatar, controller, dependencies, metadata, null);
            Assert.That(neutral.Select(value => value.Shape), Is.EqualTo(new[] { "Face size" }));
            Assert.That(neutral.Single().Weight, Is.EqualTo(expected.Weights["Face size"]).Within(.01));
            Assert.That(sourceSkin.GetBlendShapeWeight(0), Is.EqualTo(7));
            Assert.That(sourceSkin.GetBlendShapeWeight(1), Is.EqualTo(35));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(beforeController));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NeutralCommittedRootsRetainNativeClosureAndCheckOtherPlayableEffects(bool otherTouchesCommittedMorph)
        {
            controller.layers[0].stateMachine.defaultState = State(controller.layers[0].stateMachine, "Authored face",
                Clip("Authored face", ("Face size", 35)));
            Permanent(AddLayer("Implicit default closure"), writeDefaults: true);
            var metadata = new VrChatExpressionMenu.Source { Controller = controller };
            var faceBinding = EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size");
            var pupilBinding = EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Pupil removal");
            var roots = new HashSet<EditorCurveBinding> { faceBinding };
            var legacy = ExpressionDependencies.AnalyzeNeutral(controller, roots, null, metadata);
            Assert.That(legacy.Morphs, Does.Contain(pupilBinding), "Existing callers retain their complete implicit-default capture contract.");
            var other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/OtherPlayable.controller");
            other.layers[0].stateMachine.defaultState = State(other.layers[0].stateMachine, "Other playable",
                Clip("Other playable", (otherTouchesCommittedMorph ? "Face size" : "Pupil removal", 80)));
            metadata.OtherControllers.Add(other);
            if (otherTouchesCommittedMorph)
            {
                var error = Assert.Throws<NeutralShapeSamplingException>(() =>
                    ExpressionDependencies.AnalyzeNeutral(controller, roots, null, metadata, preserveCommittedMorphs: true));
                StringAssert.Contains("FX以外のPlayable Layer", error.Message);
                return;
            }
            var dependencies = ExpressionDependencies.AnalyzeNeutral(controller, roots, null, metadata, preserveCommittedMorphs: true);
            Assert.That(dependencies.Morphs, Is.EquivalentTo(roots));
            Assert.That(dependencies.Layers, Does.Contain(1), "The native WD layer must remain in the evaluation closure.");
            Assert.That(dependencies.NativeSupportLayers, Does.Contain(1));
            var expected = NativePose(avatar, controller, "Face", new Dictionary<string, int>());
            var neutral = VrChatExpressionSampler.SampleNeutral(avatar, controller, dependencies, metadata, null);
            Assert.That(neutral.Select(value => value.Shape), Is.EqualTo(new[] { "Face size" }));
            Assert.That(neutral.Single().Weight, Is.EqualTo(expected.Weights["Face size"]).Within(.01));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NeutralFractionalOverrideDoesNotBakeAnUnrelatedConstantFromAMovingBase(bool writeDefaults)
        {
            var descriptorType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor"))
                .FirstOrDefault(type => type != null);
            if (descriptorType == null) Assert.Ignore("Install the real VRChat SDK for neutral FX integration.");
            var baseClip = Clip("Unrelated constant face", ("Face size", 90));
            AnimationUtility.SetEditorCurve(baseClip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink"),
                AnimationCurve.Linear(0, 0, 30, 100));
            var baseMachine = controller.layers[0].stateMachine;
            baseMachine.defaultState = State(baseMachine, "Face and moving eye", baseClip);
            Permanent(AddLayer("Fractional pupil"), writeDefaults);
            var layers = controller.layers; layers[1].defaultWeight = .5f; controller.layers = layers;
            var sourceSkin = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
            sourceSkin.SetBlendShapeWeight(0, 7); sourceSkin.SetBlendShapeWeight(1, 12); sourceSkin.SetBlendShapeWeight(2, 20);
            Assert.That(ExpressionDependencies.StationaryMorphBindings(controller).Select(binding => binding.propertyName),
                Is.EquivalentTo(new[] { "blendShape.Pupil removal" }), "The mixed moving base clip must not declare its constant face to be stationary.");
            var descriptor = avatar.AddComponent(descriptorType);
            using (var data = new SerializedObject(descriptor))
            {
                data.FindProperty("customizeAnimationLayers").boolValue = true;
                var descriptorLayers = data.FindProperty("baseAnimationLayers"); descriptorLayers.arraySize = 1;
                var item = descriptorLayers.GetArrayElementAtIndex(0);
                var type = item.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
                item.FindPropertyRelative("isDefault").boolValue = false;
                item.FindPropertyRelative("animatorController").objectReferenceValue = controller;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            var originalController = EditorJsonUtility.ToJson(controller);
            var reference = NativePose(avatar, controller, "Face", new Dictionary<string, int>());
            // Match the previous preparation's null-motion placeholder. A
            // real zero-curve clip is a different native Animator graph.
            var prunedReference = NativeStandalonePose(avatar, controller, null, "Face");
            Assert.That(Math.Abs(reference.Weights["Face size"] - 12), Is.GreaterThan(.01), "The unrelated native constant must differ from the authored renderer value.");
            Assert.That(Math.Abs(reference.Weights["Pupil removal"] - 20), Is.GreaterThan(.01), "The fractional native effect must actually move the required morph.");
            Assert.That(Math.Abs(reference.Weights["Pupil removal"] - prunedReference.Weights["Pupil removal"]), Is.GreaterThan(.01),
                "The original native base must affect fractional blending even though its own morphs are outside capture.");
            var neutral = VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0));
            Assert.That(neutral.Select(value => value.Shape), Is.EqualTo(new[] { "Pupil removal" }));
            Assert.That(neutral.Single().Weight, Is.EqualTo(reference.Weights["Pupil removal"]).Within(.01));
            var clone = Object.Instantiate(avatar);
            try
            {
                VrChatExpressionSampler.ApplyMergedDefaults(avatar, clone);
                var skin = clone.GetComponentInChildren<SkinnedMeshRenderer>();
                Assert.That(skin.GetBlendShapeWeight(2), Is.EqualTo(reference.Weights["Pupil removal"]).Within(.01));
                Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(7));
                Assert.That(skin.GetBlendShapeWeight(1), Is.EqualTo(12));
                Assert.That(skin.sharedMesh, Is.SameAs(mesh));
                Assert.That(sourceSkin.GetBlendShapeWeight(0), Is.EqualTo(7));
                Assert.That(sourceSkin.GetBlendShapeWeight(1), Is.EqualTo(12));
                Assert.That(sourceSkin.GetBlendShapeWeight(2), Is.EqualTo(20));
                Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(originalController));
            }
            finally { Object.DestroyImmediate(clone); }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void NativeSupportBaseRejectsUnsafePlayedClipAfterItsTransitionCompletes(bool writeDefaults, bool animationEvent)
        {
            var unsafeClip = Clip("Early unsupported base", ("Face size", 90));
            AnimationUtility.SetEditorCurve(unsafeClip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink"),
                AnimationCurve.Linear(0, 0, 30, 100));
            if (animationEvent) AnimationUtility.SetAnimationEvents(unsafeClip, new[] { new AnimationEvent { time = 30, functionName = "UnsupportedFutureCallback" } });
            else AnimationUtility.SetObjectReferenceCurve(unsafeClip, EditorCurveBinding.PPtrCurve("Face", typeof(SkinnedMeshRenderer), "m_Mesh"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = mesh } });
            var safeClip = Clip("Steady supported base", ("Face size", 90));
            AnimationUtility.SetEditorCurve(safeClip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink"),
                AnimationCurve.Linear(0, 0, 30, 100));
            var machine = controller.layers[0].stateMachine;
            var initial = State(machine, "Early unsupported", unsafeClip); machine.defaultState = initial;
            var steady = State(machine, "Steady supported", safeClip);
            var transition = initial.AddTransition(steady); transition.hasExitTime = false; transition.hasFixedDuration = true; transition.duration = .25f;
            transition.AddCondition(AnimatorConditionMode.Equals, 0, "Menu");
            Permanent(AddLayer("Fractional pupil"), writeDefaults);
            var layers = controller.layers; layers[1].defaultWeight = .5f; controller.layers = layers;
            var before = EditorJsonUtility.ToJson(controller);
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0)));
            Assert.That(error.Message, Is.EqualTo(animationEvent ? "常時適用FXと表情の影響範囲を確定できません。" : "表情への遷移にマテリアル・オブジェクトの差し替えが含まれます。"));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
            var skin = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
            Assert.That(skin.sharedMesh, Is.SameAs(mesh));
            Assert.That(Enumerable.Range(0, mesh.blendShapeCount).Select(skin.GetBlendShapeWeight), Is.All.Zero);
        }

        // Replay the untouched graph independently. This also proves a
        // skipped unsafe motion is absent from the final native clip-info set.
        (int StateHash, bool InTransition, string[] Clips, string[] ObservedClips) NativeBaseStateAfterFourSeconds()
        {
            var copy = Object.Instantiate(avatar);
            var graph = PlayableGraph.Create("Skipped base state reference"); graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            try
            {
                foreach (var behaviour in copy.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                var animator = copy.GetComponent<Animator>(); animator.enabled = true;
                animator.runtimeAnimatorController = null; animator.applyRootMotion = false; animator.fireEvents = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                for (var index = 0; index < controller.layers.Length; index++) playable.SetLayerWeight(index, index == 0 ? 1 : controller.layers[index].defaultWeight);
                var output = AnimationPlayableOutput.Create(graph, "Face", animator); output.SetSourcePlayable(playable);
                var observed = new HashSet<string>();
                playable.SetInteger("Menu", 0); graph.Play();
                for (var frame = 0; frame < 240; frame++)
                {
                    graph.Evaluate(1f / 60f);
                    foreach (var info in playable.GetCurrentAnimatorClipInfo(0).Concat(playable.GetNextAnimatorClipInfo(0)))
                        if (info.clip != null && info.weight > .00001f) observed.Add(info.clip.name);
                }
                return (playable.GetCurrentAnimatorStateInfo(0).shortNameHash, playable.IsInTransition(0),
                    playable.GetCurrentAnimatorClipInfo(0).Where(info => info.weight > .00001f).Select(info => info.clip.name).ToArray(), observed.ToArray());
            }
            finally { if (graph.IsValid()) graph.Destroy(); Object.DestroyImmediate(copy); }
        }

        [TestCase(false, false, false)]
        [TestCase(false, false, true)]
        [TestCase(false, true, false)]
        [TestCase(false, true, true)]
        [TestCase(true, false, false)]
        [TestCase(true, false, true)]
        [TestCase(true, true, false)]
        [TestCase(true, true, true)]
        public void NativeSupportBaseRejectsUnsafeZeroDurationInitialOrIntermediateMotion(bool writeDefaults, bool animationEvent, bool intermediate)
        {
            var unsafeClip = Clip("Skipped unsupported base", ("Face size", 90));
            AnimationUtility.SetEditorCurve(unsafeClip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink"),
                AnimationCurve.Linear(0, 0, 30, 100));
            if (animationEvent) AnimationUtility.SetAnimationEvents(unsafeClip, new[] { new AnimationEvent { time = 30, functionName = "UnsupportedFutureCallback" } });
            else AnimationUtility.SetObjectReferenceCurve(unsafeClip, EditorCurveBinding.PPtrCurve("Face", typeof(SkinnedMeshRenderer), "m_Mesh"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = mesh } });
            var safeClip = Clip("Final supported base", ("Face size", 90));
            AnimationUtility.SetEditorCurve(safeClip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink"),
                AnimationCurve.Linear(0, 0, 30, 100));
            var machine = controller.layers[0].stateMachine;
            var unsafeState = State(machine, "Skipped unsupported", unsafeClip);
            var steady = State(machine, "Steady supported", safeClip);
            machine.defaultState = unsafeState;
            void Immediately(AnimatorState source, AnimatorState target)
            {
                var transition = source.AddTransition(target); transition.hasExitTime = false; transition.hasFixedDuration = true; transition.duration = 0;
                transition.AddCondition(AnimatorConditionMode.Equals, 0, "Menu");
            }
            if (intermediate)
            {
                var initial = State(machine, "Safe initial", safeClip); machine.defaultState = initial;
                Immediately(initial, unsafeState);
            }
            Immediately(unsafeState, steady);
            Permanent(AddLayer("Fractional pupil"), writeDefaults);
            var layers = controller.layers; layers[1].defaultWeight = .5f; controller.layers = layers;
            var before = EditorJsonUtility.ToJson(controller);
            var reference = NativeBaseStateAfterFourSeconds();
            Assert.That(reference.StateHash, Is.EqualTo(Animator.StringToHash(steady.name)));
            Assert.That(reference.InTransition, Is.False);
            Assert.That(reference.Clips, Is.EqualTo(new[] { safeClip.name }), "The unsupported state must have disappeared before final native clip inspection.");
            TestContext.Out.WriteLine("Native post-Evaluate clip history observed unsupported motion: " + reference.ObservedClips.Contains(unsafeClip.name));
            if (!intermediate) Assert.That(reference.ObservedClips, Does.Not.Contain(unsafeClip.name),
                "The initial zero-duration witness must bypass post-Evaluate native clip history.");
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0)));
            Assert.That(error.Message, Is.EqualTo(animationEvent ? "常時適用FXと表情の影響範囲を確定できません。" : "表情への遷移にマテリアル・オブジェクトの差し替えが含まれます。"));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
            var skin = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
            Assert.That(skin.sharedMesh, Is.SameAs(mesh));
            Assert.That(Enumerable.Range(0, mesh.blendShapeCount).Select(skin.GetBlendShapeWeight), Is.All.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NativeSupportBaseValidatesNestedAndBlendTreeMotionsConservatively(bool nestedMachine)
        {
            var safeClip = Clip("Current supported base", ("Face size", 90));
            AnimationUtility.SetEditorCurve(safeClip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink"),
                AnimationCurve.Linear(0, 0, 30, 100));
            var unsafeClip = Clip("Unobserved unsupported base", ("Face size", 90));
            AnimationUtility.SetAnimationEvents(unsafeClip, new[] { new AnimationEvent { time = 30, functionName = "UnsupportedFutureCallback" } });
            var machine = controller.layers[0].stateMachine; var current = State(machine, "Current supported", safeClip); machine.defaultState = current;
            if (nestedMachine)
            {
                var nested = machine.AddStateMachine("Nested unsupported"); nested.defaultState = State(nested, "Unsafe nested", unsafeClip);
            }
            else
            {
                controller.AddParameter("BaseTree", AnimatorControllerParameterType.Float);
                var tree = new BlendTree { name = "Support tree", blendType = BlendTreeType.Simple1D, blendParameter = "BaseTree", useAutomaticThresholds = false };
                AssetDatabase.AddObjectToAsset(tree, controller);
                tree.children = new[] { new ChildMotion { motion = safeClip, threshold = 0, timeScale = 1 }, new ChildMotion { motion = unsafeClip, threshold = 1, timeScale = 1 } };
                current.motion = tree;
            }
            Permanent(AddLayer("Fractional pupil"));
            var layers = controller.layers; layers[1].defaultWeight = .5f; controller.layers = layers;
            var before = EditorJsonUtility.ToJson(controller);
            var reference = NativeBaseStateAfterFourSeconds();
            Assert.That(reference.StateHash, Is.EqualTo(Animator.StringToHash(current.name)));
            Assert.That(reference.Clips, Is.EqualTo(new[] { safeClip.name }));
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0)));
            Assert.That(error.Message, Is.EqualTo("常時適用FXと表情の影響範囲を確定できません。"));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NeutralFractionalOverrideStillValidatesMovingRequiredBaseMorph(bool writeDefaults)
        {
            var baseClip = Clip("Moving required base pupil");
            AnimationUtility.SetEditorCurve(baseClip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"),
                AnimationCurve.Linear(0, 0, 30, 100));
            var machine = controller.layers[0].stateMachine; machine.defaultState = State(machine, "Moving required", baseClip);
            Permanent(AddLayer("Fractional pupil"), writeDefaults);
            var layers = controller.layers; layers[1].defaultWeight = .5f; controller.layers = layers;
            var before = EditorJsonUtility.ToJson(controller);
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0)));
            Assert.That(error.Message, Does.Contain("時間で変わる"));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NeutralFractionalOverrideRejectsDelayedBaseActivityChanges(bool writeDefaultsFlip)
        {
            var baseClip = Clip("Base face", ("Face size", 0));
            var machine = controller.layers[0].stateMachine;
            var idle = State(machine, "Idle", baseClip); machine.defaultState = idle;
            var future = State(machine, "After thirty seconds", writeDefaultsFlip ? baseClip : null, writeDefaults: writeDefaultsFlip);
            var transition = idle.AddTransition(future); transition.hasExitTime = true; transition.exitTime = 30; transition.duration = 0;
            Permanent(AddLayer("Fractional pupil"));
            var layers = controller.layers; layers[1].defaultWeight = .5f; controller.layers = layers;
            avatar.GetComponentInChildren<SkinnedMeshRenderer>().SetBlendShapeWeight(2, 20);
            var before = EditorJsonUtility.ToJson(controller);
            Assert.That(NativePose(avatar, controller, "Face", new Dictionary<string, int>()).Weights["Pupil removal"], Is.GreaterThan(20));
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0)));
            Assert.That(error.Message, Does.Contain("時間で遷移"), "A state change beyond the sampling window must still be rejected.");
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.EqualTo(20));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NeutralFractionalBaseActivityRetainsExternalAndWriterDependencies(bool external)
        {
            controller.AddParameter("BaseGate", AnimatorControllerParameterType.Float);
            var machine = controller.layers[0].stateMachine;
            var idle = State(machine, "Idle", Clip("Base idle", ("Face size", 0))); machine.defaultState = idle;
            var selected = State(machine, "External base", Clip("Base selected", ("Face size", 75)));
            var transition = idle.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Greater, .5f, "BaseGate");
            Permanent(AddLayer("Fractional pupil"));
            var layers = controller.layers; layers[1].defaultWeight = .5f; controller.layers = layers;
            var metadata = new VrChatExpressionMenu.Source();
            if (external) metadata.ExternalParameters.Add("BaseGate");
            else
            {
                var writer = AddLayer("Delayed base gate writer"); var clip = Clip("Changing base gate");
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "BaseGate"), AnimationCurve.Linear(0, 0, 30, 1));
                writer.defaultState = State(writer, "Later gate", clip);
            }
            var before = EditorJsonUtility.ToJson(controller);
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0), metadata: metadata));
            Assert.That(error.Message, Does.Contain(external ? "外部入力" : "時間で変わる"));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.Zero);
        }

        [Test]
        public void NeutralFractionalBaseCannotAssumeBuiltinGestureRemainsAtDefault()
        {
            controller.AddParameter("GestureLeft", AnimatorControllerParameterType.Int);
            var machine = controller.layers[0].stateMachine;
            var idle = State(machine, "Idle", Clip("Base idle", ("Face size", 0))); machine.defaultState = idle;
            var selected = State(machine, "Gesture changes defaults", Clip("Base gesture", ("Face size", 75)), writeDefaults: true);
            var transition = idle.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 2, "GestureLeft");
            Permanent(AddLayer("Fractional pupil"));
            var layers = controller.layers; layers[1].defaultWeight = .5f; controller.layers = layers;
            var original = EditorJsonUtility.ToJson(controller);
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0)));
            Assert.That(error.Message, Does.Contain("外部入力").And.Contain("GestureLeft"));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(original));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task FractionalStationaryFxGeometrySurvivesOneClickExportAndVrmReimport(bool additive)
        {
            var shader = Shader.Find("lilToon");
            if (shader == null) Assert.Ignore("Install lilToon for the real exporter entry point.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            using var menus = new MenuImportSdkRecoveryTests.SdkFixture(fixture.Source);
            var controls = (IList)VrChatExpressionMenu.Member(menus.Root, "controls"); controls.RemoveAt(1);
            var fx = (AnimatorController)VrChatExpressionMenu.Read(fixture.Source).Controller;
            var delta = new Vector3[fixture.Mesh.vertexCount]; delta[0] = Vector3.up * .1f;
            fixture.Mesh.AddBlendShapeFrame("Pupil removal", 100, delta, new Vector3[delta.Length], new Vector3[delta.Length]);
            var fixedClip = new AnimationClip { name = "Weighted permanent pupil" };
            AnimationUtility.SetEditorCurve(fixedClip, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"),
                AnimationCurve.Constant(0, 1, 100));
            AssetDatabase.AddObjectToAsset(fixedClip, fx);
            fx.AddLayer("Weighted permanent pupil");
            var layers = fx.layers; var layer = layers[layers.Length - 1]; layer.defaultWeight = .5f;
            layer.blendingMode = additive ? AnimatorLayerBlendingMode.Additive : AnimatorLayerBlendingMode.Override;
            layer.stateMachine.defaultState = State(layer.stateMachine, "Always", fixedClip); fx.layers = layers;
            var material = new Material(shader);
            foreach (var renderer in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) renderer.sharedMaterial = material;
            var sourceFront = fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>();
            var beforeMesh = EditorJsonUtility.ToJson(fixture.Mesh);
            var beforeController = EditorJsonUtility.ToJson(fx);
            var beforeDescriptor = EditorJsonUtility.ToJson(menus.Descriptor);
            var expectedNeutral = (Weights: Enumerable.Range(0, sourceFront.sharedMesh.blendShapeCount)
                .ToDictionary(index => sourceFront.sharedMesh.GetBlendShapeName(index), sourceFront.GetBlendShapeWeight),
                Vertices: WorldVertices(sourceFront));
            var expectedMenu = NativePose(fixture.Source, fx, "Front", new Dictionary<string, int> { ["Face"] = 1, ["GestureRight"] = 0 });
            var expectedGesture = NativePose(fixture.Source, fx, "Front", new Dictionary<string, int> { ["Face"] = 0, ["GestureRight"] = 2 });
            var sampledNeutral = NeutralShapeSampler.Sample(fixture.Source);
            Vrm10Instance imported = null;
            try
            {
                var warnings = new List<string>();
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Weighted permanent FX", "Tests", warnings,
                    blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported, Is.Not.Null, string.Join("\n", warnings));
                imported.Runtime.Process();
                var importedFront = imported.GetComponentsInChildren<SkinnedMeshRenderer>().Single(renderer => renderer.name == "Front");
                var actualNeutralVertices = WorldVertices(importedFront);
                Assert.That(actualNeutralVertices.Length, Is.EqualTo(expectedNeutral.Vertices.Length), "Neutral");
                for (var vertex = 0; vertex < actualNeutralVertices.Length; vertex++)
                    Assert.That(Vector3.Distance(expectedNeutral.Vertices[vertex], actualNeutralVertices[vertex]), Is.LessThan(.0005f),
                        "Neutral / vertex " + vertex + ": expected=" + expectedNeutral.Vertices[vertex].ToString("G9") +
                        ", actual=" + actualNeutralVertices[vertex].ToString("G9") + "; native=" +
                        string.Join(", ", expectedNeutral.Weights.Select(value => value.Key + "=" + value.Value.ToString("G9"))) +
                        "; sampled=" + string.Join(", ", sampledNeutral.Select(value => value.Path + "/" + value.Shape + "=" + value.Weight.ToString("G9"))) +
                        "; imported=" + string.Join(", ", Enumerable.Range(0, importedFront.sharedMesh.blendShapeCount)
                            .Select(shape => importedFront.sharedMesh.GetBlendShapeName(shape) + "=" + importedFront.GetBlendShapeWeight(shape).ToString("G9"))) +
                        "; warnings=" + string.Join(" | ", warnings));
                foreach (var candidate in new[] { (Name: "Menu face", Pose: expectedMenu), (Name: "Gesture face", Pose: expectedGesture) })
                {
                    foreach (var expression in imported.Vrm.Expression.CustomClips)
                        imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(expression.name), 0);
                    var selected = imported.Vrm.Expression.CustomClips.Single(expression => expression.name.Contains(candidate.Name));
                    imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(selected.name), 1);
                    imported.Runtime.Process();
                    var actualVertices = WorldVertices(importedFront);
                    Assert.That(actualVertices.Length, Is.EqualTo(candidate.Pose.Vertices.Length), candidate.Name);
                    for (var vertex = 0; vertex < actualVertices.Length; vertex++)
                        Assert.That(Vector3.Distance(candidate.Pose.Vertices[vertex], actualVertices[vertex]), Is.LessThan(.0005f),
                            candidate.Name + " / vertex " + vertex + ": expected=" + candidate.Pose.Vertices[vertex].ToString("G9") +
                            ", actual=" + actualVertices[vertex].ToString("G9") + "; native=" +
                            string.Join(", ", candidate.Pose.Weights.Select(value => value.Key + "=" + value.Value.ToString("G9"))) +
                            "; imported=" + string.Join(", ", Enumerable.Range(0, importedFront.sharedMesh.blendShapeCount)
                                .Select(shape => importedFront.sharedMesh.GetBlendShapeName(shape) + "=" + importedFront.GetBlendShapeWeight(shape).ToString("G9"))));
                }
                Assert.That(sourceFront.GetBlendShapeWeight(1), Is.Zero);
                Assert.That(EditorJsonUtility.ToJson(fixture.Mesh), Is.EqualTo(beforeMesh));
                Assert.That(EditorJsonUtility.ToJson(fx), Is.EqualTo(beforeController));
                Assert.That(EditorJsonUtility.ToJson(menus.Descriptor), Is.EqualTo(beforeDescriptor));
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); Object.DestroyImmediate(material); }
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

        [TestCase(false)]
        [TestCase(true)]
        public void RegisteredFaceEmoClipFollowsReparentedTargetAndMergedPermanentFx(bool writeDefaults)
        {
            var clip = Clip("Registered FaceEmo", ("Face size", 75), ("Pupil removal", 0));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"),
                AnimationCurve.Linear(0, 0, 1, 80));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                AnimationCurve.Linear(0, 0, 1, 75));
            // A GUID must address a clip asset, rather than the controller's
            // subasset; real FaceEmo stores standalone registered clips.
            var original = Object.Instantiate(clip); original.name = clip.name;
            AssetDatabase.CreateAsset(original, folder + "/FaceEmo.anim");
            var registered = new { Modes = new[] { new { DisplayName = "Registered", ChangeDefaultFace = true,
                Animation = new { GUID = AssetDatabase.AssetPathToGUID(folder + "/FaceEmo.anim") } } } };
            var clone = Object.Instantiate(avatar); clone.name = avatar.name;
            try
            {
                var auxiliary = new GameObject("Face", typeof(MeshRenderer)); auxiliary.transform.SetParent(clone.transform, false);
                var transformed = false;
                bool Excluded(string path) => transformed && path == "Face";
                var snapshot = FaceEmoExpressions.CaptureRegistered(clone, registered, excludedPath: Excluded);
                var target = clone.GetComponentInChildren<SkinnedMeshRenderer>();
                var parent = new GameObject("Prepared rig"); parent.transform.SetParent(clone.transform, false);
                target.transform.SetParent(parent.transform, false);
                Object.DestroyImmediate(auxiliary);
                transformed = true;
                // Reusing the old hierarchy path must never redirect FaceEmo
                // to an unrelated renderer created by an authoring pass.
                var reused = new GameObject("Face", typeof(SkinnedMeshRenderer)); reused.transform.SetParent(clone.transform, false);
                reused.GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
                var fixedClip = Clip("Merged pupil removal", ("Pupil removal", 100));
                var binding = AnimationUtility.GetCurveBindings(fixedClip).Single();
                AnimationUtility.SetEditorCurve(fixedClip, binding, null); binding.path = "Prepared rig/Face";
                AnimationUtility.SetEditorCurve(fixedClip, binding, AnimationCurve.Constant(0, 1, 100));
                controller.layers[0].stateMachine.defaultState = State(controller.layers[0].stateMachine, "Merged permanent", fixedClip, writeDefaults);
                var source = new VrChatExpressionMenu.Source { Controller = controller }; var serial = 0;
                FaceEmoExpressions.ReadRegistered(clone, registered, "FaceEmo", source, ref serial, new HashSet<object>(), 0, Excluded, snapshot);
                var entry = source.Entries.Single();
                Assert.That(entry.Error, Is.Null);
                Assert.That(entry.Values.All(value => value.Path == "Prepared rig/Face"), Is.True);
                Assert.That(entry.Values.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(100).Within(.01));
                Assert.That(entry.Animation.Select(value => value.Shape), Is.EquivalentTo(new[] { "Face size" }));
                Assert.That(entry.Animation.Single().Curve.Evaluate(.5), Is.EqualTo(37.5).Within(.01));
                Assert.That(entry.Duration, Is.EqualTo(1).Within(.001));
                Assert.That(reused.GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.Zero);
                Assert.That(AnimationUtility.GetCurveBindings(original).All(value => value.path == "Face"), Is.True);
                Assert.That(AnimationUtility.GetEditorCurve(original, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"))
                    .Evaluate(.5f), Is.EqualTo(40).Within(.01));
                Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().transform.parent, Is.SameAs(avatar.transform));
                Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.Zero);
            }
            finally { Object.DestroyImmediate(clone); }
        }

        [Test]
        public void RegisteredFaceEmoTargetRemovalIsReportedEvenIfItsOldPathIsReused()
        {
            var original = new AnimationClip { name = "Registered FaceEmo" };
            AnimationUtility.SetEditorCurve(original, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                AnimationCurve.Constant(0, 1, 75));
            AssetDatabase.CreateAsset(original, folder + "/FaceEmo.anim");
            var registered = new { Modes = new[] { new { DisplayName = "Registered", ChangeDefaultFace = true,
                Animation = new { GUID = AssetDatabase.AssetPathToGUID(folder + "/FaceEmo.anim") } } } };
            var clone = Object.Instantiate(avatar);
            try
            {
                var snapshot = FaceEmoExpressions.CaptureRegistered(clone, registered);
                Object.DestroyImmediate(clone.GetComponentInChildren<SkinnedMeshRenderer>().gameObject);
                var replacement = new GameObject("Face", typeof(SkinnedMeshRenderer)); replacement.transform.SetParent(clone.transform, false);
                replacement.GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
                var source = new VrChatExpressionMenu.Source(); var serial = 0;
                FaceEmoExpressions.ReadRegistered(clone, registered, "FaceEmo", source, ref serial, new HashSet<object>(), 0, bindings: snapshot);
                Assert.That(source.Entries.Single().Error, Does.Contain("FaceEmoの表情対象が残っていません"));
                Assert.That(replacement.GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(1), Is.Zero);
            }
            finally { Object.DestroyImmediate(clone); }
        }

        [Test]
        public void RegisteredFaceEmoOriginalManualOmissionStaysOmittedAfterReparenting()
        {
            var original = new AnimationClip { name = "Registered FaceEmo" };
            AnimationUtility.SetEditorCurve(original, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                AnimationCurve.Constant(0, 1, 75));
            AssetDatabase.CreateAsset(original, folder + "/FaceEmo.anim");
            var registered = new { Modes = new[] { new { DisplayName = "Registered", ChangeDefaultFace = true,
                Animation = new { GUID = AssetDatabase.AssetPathToGUID(folder + "/FaceEmo.anim") } } } };
            var clone = Object.Instantiate(avatar);
            try
            {
                bool Excluded(string path) => path == "Face";
                var snapshot = FaceEmoExpressions.CaptureRegistered(clone, registered, excludedPath: Excluded);
                var target = clone.GetComponentInChildren<SkinnedMeshRenderer>();
                var parent = new GameObject("Prepared rig"); parent.transform.SetParent(clone.transform, false);
                target.transform.SetParent(parent.transform, false);
                var source = new VrChatExpressionMenu.Source(); var serial = 0;
                FaceEmoExpressions.ReadRegistered(clone, registered, "FaceEmo", source, ref serial, new HashSet<object>(), 0, Excluded, snapshot);
                Assert.That(source.Entries.Single().Values, Is.Empty);
                Assert.That(source.Entries.Single().Error, Does.Contain("顔のBlendShapeを含まない"));
                Assert.That(target.GetBlendShapeWeight(1), Is.Zero);
            }
            finally { Object.DestroyImmediate(clone); }
        }

        [Test]
        public void RegisteredFaceEmoMissingOriginalTargetCannotBindAnUnrelatedGeneratedRenderer()
        {
            var original = new AnimationClip { name = "Registered FaceEmo" };
            AnimationUtility.SetEditorCurve(original, EditorCurveBinding.FloatCurve("Generated Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                AnimationCurve.Constant(0, 1, 75));
            AssetDatabase.CreateAsset(original, folder + "/FaceEmo.anim");
            var registered = new { Modes = new[] { new { DisplayName = "Registered", ChangeDefaultFace = true,
                Animation = new { GUID = AssetDatabase.AssetPathToGUID(folder + "/FaceEmo.anim") } } } };
            var clone = Object.Instantiate(avatar);
            try
            {
                var snapshot = FaceEmoExpressions.CaptureRegistered(clone, registered);
                var generated = new GameObject("Generated Face", typeof(SkinnedMeshRenderer)); generated.transform.SetParent(clone.transform, false);
                generated.GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
                var source = new VrChatExpressionMenu.Source(); var serial = 0;
                FaceEmoExpressions.ReadRegistered(clone, registered, "FaceEmo", source, ref serial, new HashSet<object>(), 0, bindings: snapshot);
                Assert.That(source.Entries.Single().Error, Does.Contain("Rendererのパスを一意に解決できません"));
                Assert.That(source.Entries.Single().Values, Is.Empty);
                Assert.That(generated.GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(1), Is.Zero);
                Assert.That(AnimationUtility.GetCurveBindings(original).Single().path, Is.EqualTo("Generated Face"));
            }
            finally { Object.DestroyImmediate(clone); }
        }

        [Test]
        public void RegisteredFaceEmoRetainedTargetCanMoveIntoAnOriginallyManuallyExcludedPath()
        {
            var head = new GameObject("Head"); head.transform.SetParent(avatar.transform, false);
            var omitted = new GameObject("Face", typeof(SkinnedMeshRenderer)); omitted.transform.SetParent(head.transform, false);
            omitted.GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
            var original = new AnimationClip { name = "Registered FaceEmo" };
            AnimationUtility.SetEditorCurve(original, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                AnimationCurve.Linear(0, 0, 1, 75));
            AnimationUtility.SetEditorCurve(original, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"),
                AnimationCurve.Linear(0, 0, 1, 80));
            AssetDatabase.CreateAsset(original, folder + "/FaceEmo.anim");
            var registered = new { Modes = new[] { new { DisplayName = "Registered", ChangeDefaultFace = true,
                Animation = new { GUID = AssetDatabase.AssetPathToGUID(folder + "/FaceEmo.anim") } } } };
            using var exclusions = new ExportObjectExclusions(avatar, new[] { omitted });
            var clone = Object.Instantiate(avatar);
            try
            {
                exclusions.Apply(clone, null);
                bool PreparedExcluded(string path) => VrChatExpressionSampler.IsExcludedPreparedPath(clone, path, exclusions.ContainsPath);
                var snapshot = FaceEmoExpressions.CaptureRegistered(clone, registered, excludedPath: exclusions.ContainsPath,
                    preparedExcludedPath: PreparedExcluded);
                var target = clone.transform.Find("Face").GetComponent<SkinnedMeshRenderer>();
                target.transform.SetParent(clone.transform.Find("Head"), false);
                Assert.That(exclusions.ContainsPath("Head/Face"), Is.True, "This predicate intentionally describes the original source hierarchy.");
                var fixedClip = Clip("Merged pupil removal", ("Pupil removal", 100));
                var binding = AnimationUtility.GetCurveBindings(fixedClip).Single();
                AnimationUtility.SetEditorCurve(fixedClip, binding, null); binding.path = "Head/Face";
                AnimationUtility.SetEditorCurve(fixedClip, binding, AnimationCurve.Constant(0, 1, 100));
                controller.layers[0].stateMachine.defaultState = State(controller.layers[0].stateMachine, "Merged permanent", fixedClip);
                var neutral = VrChatExpressionSampler.SampleDefaults(clone, controller, Parameters(0), PreparedExcluded);
                Assert.That(neutral.Single(value => value.Shape == "Pupil removal").Path, Is.EqualTo("Head/Face"));
                Assert.That(neutral.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(100).Within(.01));
                var source = new VrChatExpressionMenu.Source { Controller = controller }; var serial = 0;
                FaceEmoExpressions.ReadRegistered(clone, registered, "FaceEmo", source, ref serial, new HashSet<object>(), 0, exclusions.ContainsPath, snapshot);
                var entry = source.Entries.Single();
                Assert.That(entry.Error, Is.Null);
                Assert.That(entry.Values.All(value => value.Path == "Head/Face"), Is.True);
                Assert.That(entry.Values.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(100).Within(.01));
                Assert.That(entry.Animation.Select(value => value.Shape), Is.EquivalentTo(new[] { "Face size" }));
                Assert.That(entry.Animation.Single().Curve.Evaluate(.5), Is.EqualTo(37.5).Within(.01));
                Assert.That(target.GetBlendShapeWeight(2), Is.Zero);
                Assert.That(omitted != null && omitted.transform.parent == head.transform, Is.True);
                Assert.That(AnimationUtility.GetCurveBindings(original).All(value => value.path == "Face"), Is.True);
            }
            finally { Object.DestroyImmediate(clone); }
        }

        [Test]
        public void InstalledMaReparentsFaceEmoRendererAndMergesPermanentPupilFxBeforeEvaluation()
        {
            Type Installed(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(name)).FirstOrDefault(type => type != null);
            var proxyType = Installed("nadena.dev.modular_avatar.core.ModularAvatarBoneProxy");
            var mergeType = Installed("nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator");
            var rootType = Installed("nadena.dev.ndmf.runtime.components.NDMFAvatarRoot");
            var descriptorType = Installed("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            if (proxyType == null || mergeType == null || rootType == null || descriptorType == null)
                Assert.Ignore("Install MA, NDMF and the real VRChat SDK for FaceEmo's prepared path integration.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            var source = fixture.Source;
            source.AddComponent(rootType);
            var hair = source.transform.Find("Independent hair");
            var front = source.transform.Find("Front"); front.SetParent(hair, true);
            var proxy = hair.gameObject.AddComponent(proxyType);
            proxyType.GetProperty("target").SetValue(proxy, source.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Head));
            var zeros = new Vector3[fixture.Mesh.vertexCount];
            var deltas = new Vector3[fixture.Mesh.vertexCount]; deltas[0] = Vector3.up;
            fixture.Mesh.AddBlendShapeFrame("FaceEmo smile", 100, deltas, zeros, zeros);
            fixture.Mesh.AddBlendShapeFrame("Pupil removal", 100, deltas, zeros, zeros);
            const string authoredPath = "Independent hair/Front";
            var original = new AnimationClip { name = "Registered FaceEmo" };
            AnimationUtility.SetEditorCurve(original, EditorCurveBinding.FloatCurve(authoredPath, typeof(SkinnedMeshRenderer), "blendShape.FaceEmo smile"),
                AnimationCurve.Linear(0, 0, 1, 75));
            AnimationUtility.SetEditorCurve(original, EditorCurveBinding.FloatCurve(authoredPath, typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"),
                AnimationCurve.Linear(0, 0, 1, 80));
            AssetDatabase.CreateAsset(original, folder + "/FaceEmo.anim");
            var registered = new { Modes = new[] { new { DisplayName = "Registered", ChangeDefaultFace = true,
                Animation = new { GUID = AssetDatabase.AssetPathToGUID(folder + "/FaceEmo.anim") } } } };
            var permanent = new AnimationClip { name = "Always remove pupil" };
            AnimationUtility.SetEditorCurve(permanent, EditorCurveBinding.FloatCurve(authoredPath, typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"),
                AnimationCurve.Constant(0, 1, 100));
            AssetDatabase.AddObjectToAsset(permanent, controller);
            controller.layers[0].stateMachine.defaultState = State(controller.layers[0].stateMachine, "Always", permanent);
            var descriptor = source.AddComponent(descriptorType);
            var emptyFx = AnimatorController.CreateAnimatorControllerAtPath(folder + "/OriginalFX.controller");
            using (var data = new SerializedObject(descriptor))
            {
                data.FindProperty("customizeAnimationLayers").boolValue = true;
                var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = 1;
                var item = layers.GetArrayElementAtIndex(0);
                var type = item.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
                item.FindPropertyRelative("isDefault").boolValue = false;
                item.FindPropertyRelative("animatorController").objectReferenceValue = emptyFx;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            var merge = source.AddComponent(mergeType);
            mergeType.GetField("animator").SetValue(merge, controller);
            var layerType = mergeType.GetField("layerType"); layerType.SetValue(merge, Enum.Parse(layerType.FieldType, "FX"));
            var pathMode = mergeType.GetField("pathMode"); pathMode.SetValue(merge, Enum.Parse(pathMode.FieldType, "Absolute"));
            var clone = Object.Instantiate(source); clone.name = source.name;
            try
            {
                var copiedFront = clone.transform.Find(authoredPath).GetComponent<SkinnedMeshRenderer>();
                var snapshot = FaceEmoExpressions.CaptureRegistered(clone, registered);
                var evaluated = false;
                using (NdmfExportPreparation.Prepare(source, clone, afterTransforming: prepared =>
                {
                    var preparedPath = AnimationUtility.CalculateTransformPath(copiedFront.transform, clone.transform);
                    Assert.That(preparedPath, Is.Not.EqualTo(authoredPath), "The real MA Bone Proxy must reparent the registered clip's target.");
                    var menu = VrChatExpressionMenu.Read(clone);
                    Assert.That(menu.Controller, Is.Not.Null);
                    Assert.That(menu.Controller, Is.Not.SameAs(controller));
                    var serial = 0;
                    FaceEmoExpressions.ReadRegistered(clone, registered, "FaceEmo", menu, ref serial, new HashSet<object>(), 0, bindings: snapshot);
                    var entry = menu.Entries.Single();
                    Assert.That(entry.Error, Is.Null);
                    Assert.That(entry.Values.All(value => value.Path == preparedPath), Is.True);
                    Assert.That(entry.Values.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(100).Within(.01));
                    Assert.That(entry.Animation.Select(value => value.Shape), Is.EquivalentTo(new[] { "FaceEmo smile" }));
                    Assert.That(entry.Animation.Single().Curve.Evaluate(.5), Is.EqualTo(37.5).Within(.01));
                    evaluated = true;
                })) { }
                Assert.That(evaluated, Is.True);
                Assert.That(front.parent, Is.SameAs(hair));
                Assert.That(front.GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.Zero);
                Assert.That(AnimationUtility.GetCurveBindings(original).All(binding => binding.path == authoredPath), Is.True);
                Assert.That(AnimationUtility.GetCurveBindings(permanent).Single().path, Is.EqualTo(authoredPath));
            }
            finally { Object.DestroyImmediate(clone); }
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

        [TestCase(false, 0f)]
        [TestCase(true, 0f)]
        [TestCase(false, 1f)]
        [TestCase(true, 1f)]
        public void DirectClipProbeAppliesOriginalAnimatorLayerControlUsingNativeWeights(bool standalone, float goalWeight)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAnimatorLayerControl"))
                .FirstOrDefault(candidate => candidate != null);
            if (type == null) Assert.Ignore("Install the real VRChat SDK for animator layer-control integration.");
            var machine = controller.layers[0].stateMachine;
            Menu(machine, true);
            Permanent(AddLayer("Permanent pupil"));
            var layers = controller.layers;
            layers[1].defaultWeight = 1 - goalWeight;
            controller.layers = layers;
            var selected = machine.states.Single(child => child.state.name == "Selected").state;
            var controlling = standalone ? machine.defaultState : selected;
            var control = controlling.AddStateMachineBehaviour(type);
            using (var data = new SerializedObject(control))
            {
                var playable = data.FindProperty("playable");
                Assert.That(playable, Is.Not.Null);
                Assert.That(playable.propertyType, Is.EqualTo(SerializedPropertyType.Enum));
                var targetPlayable = Array.IndexOf(playable.enumNames, "FX");
                Assert.That(targetPlayable, Is.GreaterThanOrEqualTo(0));
                playable.enumValueIndex = targetPlayable;
                data.FindProperty("layer").intValue = 1;
                data.FindProperty("goalWeight").floatValue = goalWeight;
                data.FindProperty("blendDuration").floatValue = 0;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            var clip = (AnimationClip)selected.motion;
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"),
                AnimationCurve.Linear(0, 0, 1, 80));
            var entry = new VrChatExpressionMenu.Entry();
            VrChatGestureExpressions.ReadClip(avatar, clip, entry);
            // Replay three authored curve values through an independent native
            // graph with the documented SDK goal weight, not the contradictory
            // serialized default. No SDK callback/exporter adapter is present.
            var expected = new Dictionary<float, float>();
            foreach (var input in new[] { 0f, 40f, 80f })
            {
                var reference = AnimatorController.CreateAnimatorControllerAtPath(folder + "/SdkWeightReference" + input + ".controller");
                var sample = new AnimationClip { name = "Native sample " + input };
                AnimationUtility.SetEditorCurve(sample, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"), AnimationCurve.Constant(0, 1, 75));
                AnimationUtility.SetEditorCurve(sample, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"), AnimationCurve.Constant(0, 1, input));
                AssetDatabase.AddObjectToAsset(sample, reference);
                reference.layers[0].stateMachine.defaultState = State(reference.layers[0].stateMachine, "Direct clip", sample);
                if (standalone)
                {
                    reference.AddLayer("Original base"); var nativeLayers = reference.layers;
                    nativeLayers[1].defaultWeight = 1;
                    nativeLayers[1].stateMachine.defaultState = State(nativeLayers[1].stateMachine, "Original default", (AnimationClip)machine.defaultState.motion);
                    reference.layers = nativeLayers;
                }
                reference.AddLayer("Controlled permanent"); var finalLayers = reference.layers; var upper = finalLayers[finalLayers.Length - 1];
                upper.defaultWeight = goalWeight;
                upper.stateMachine.defaultState = State(upper.stateMachine, "Permanent", (AnimationClip)controller.layers[1].stateMachine.defaultState.motion);
                reference.layers = finalLayers;
                expected.Add(input, NativePose(avatar, reference, "Face", new Dictionary<string, int>()).Weights["Pupil removal"]);
            }
            var before = AssetDatabase.LoadAllAssetsAtPath(folder + "/FX.controller").ToDictionary(value => value, value => EditorJsonUtility.ToJson(value));
            var beforeMesh = EditorJsonUtility.ToJson(mesh);
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, standalone ? (int?)null : 0,
                sourceState: standalone ? null : selected);
            var animation = entry.Animation.SingleOrDefault(value => value.Shape == "Pupil removal");
            foreach (var point in new[] { (Time: 0f, Input: 0f), (Time: .5f, Input: 40f), (Time: 1f, Input: 80f) })
                Assert.That(animation?.Curve.Evaluate(point.Time) ?? entry.Values.Single(value => value.Shape == "Pupil removal").Weight,
                    Is.EqualTo(expected[point.Input]).Within(.01), "Native effective pupil at " + point.Time);
            foreach (var pair in before) Assert.That(EditorJsonUtility.ToJson(pair.Key), Is.EqualTo(pair.Value), pair.Key.name);
            Assert.That(EditorJsonUtility.ToJson(mesh), Is.EqualTo(beforeMesh));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.Zero);
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh, Is.SameAs(mesh));
            Assert.That(controller.layers[1].defaultWeight, Is.EqualTo(1 - goalWeight));
            Assert.That(controlling.behaviours, Does.Contain(control));
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
