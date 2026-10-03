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
    public sealed class NeutralShapeSamplerTests
    {
        private string folder;
        private GameObject avatar;
        private Mesh mesh;
        private AnimatorController controller;
        private Component descriptor;
        private SkinnedMeshRenderer skin;

        [SetUp]
        public void SetUp()
        {
            var descriptorType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor")).FirstOrDefault(type => type != null);
            if (descriptorType == null) Assert.Ignore("Install the real VRChat SDK.");
            var name = "__NeutralShapeSampler_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            avatar = new GameObject("Avatar", typeof(Animator));
            var body = new GameObject("Body", typeof(SkinnedMeshRenderer)); body.transform.SetParent(avatar.transform, false);
            skin = body.GetComponent<SkinnedMeshRenderer>();
            mesh = new Mesh { name = "Closed base" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            foreach (var shape in new[] { "Open", "Blink", "vrc.v.aa", "Untouched" })
                mesh.AddBlendShapeFrame(shape, 100, Enumerable.Repeat(Vector3.up, 3).ToArray(), null, null);
            skin.sharedMesh = mesh;
            skin.SetBlendShapeWeight(3, 35);
            descriptor = avatar.AddComponent(descriptorType);
            SetFx(controller);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(avatar); Object.DestroyImmediate(mesh);
            if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        private void SetFx(RuntimeAnimatorController runtime)
        {
            using (var data = new SerializedObject(descriptor))
            {
                data.FindProperty("customizeAnimationLayers").boolValue = true;
                var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = 1;
                var layer = layers.GetArrayElementAtIndex(0);
                var type = layer.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
                layer.FindPropertyRelative("isDefault").boolValue = false;
                layer.FindPropertyRelative("animatorController").objectReferenceValue = runtime;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private AnimationClip Clip(string shape, AnimationCurve curve)
        {
            var clip = new AnimationClip { name = shape + " pose" };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape." + shape), curve);
            AssetDatabase.AddObjectToAsset(clip, controller); return clip;
        }

        private AnimatorState State(AnimatorStateMachine machine, AnimationClip clip = null, bool writeDefaults = false)
        {
            var state = machine.AddState("State " + machine.states.Length); state.motion = clip; state.writeDefaultValues = writeDefaults;
            return state;
        }

        private AnimatorStateMachine Layer(string name)
        {
            controller.AddLayer(name); var layers = controller.layers;
            layers[layers.Length - 1].defaultWeight = 1; controller.layers = layers;
            return layers[layers.Length - 1].stateMachine;
        }

        private AnimatorState Open(float weight = 100, bool writeDefaults = false)
        {
            var machine = controller.layers[0].stateMachine;
            return machine.defaultState = State(machine, Clip("Open", AnimationCurve.Constant(0, 1, weight)), writeDefaults);
        }

        [Test]
        public void ConstantStartupWritesIncludeZeroAndLeaveUnwrittenChannelsAlone()
        {
            var state = Open(); skin.SetBlendShapeWeight(1, 80);
            AnimationUtility.SetEditorCurve((AnimationClip)state.motion,
                EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Blink"), AnimationCurve.Constant(0, 1, 0));
            var values = NeutralShapeSampler.Sample(avatar);
            Assert.That(values.Select(value => value.Shape), Is.EquivalentTo(new[] { "Open", "Blink" }));
            Assert.That(values.Single(value => value.Shape == "Open").Weight, Is.EqualTo(100).Within(.01));
            Assert.That(values.Single(value => value.Shape == "Blink").Weight, Is.Zero.Within(.01));
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
            Assert.That(skin.GetBlendShapeWeight(1), Is.EqualTo(80));
            Assert.That(skin.GetBlendShapeWeight(3), Is.EqualTo(35));
        }

        [Test]
        public void WriteDefaultsOffInitializerRetainsItsStartupPoseAfterLeavingTheClip()
        {
            var initial = Open(); var machine = controller.layers[0].stateMachine;
            var held = State(machine);
            var transition = initial.AddTransition(held); transition.hasExitTime = true;
            transition.exitTime = .01f; transition.duration = 0;
            Assert.That(NeutralShapeSampler.Sample(avatar).Single(value => value.Shape == "Open").Weight, Is.EqualTo(100).Within(.01));
        }

        [Test]
        public void DelayedGenericMorphAnimationCannotBeFrozenAsNeutral()
        {
            var state = Open();
            AnimationUtility.SetEditorCurve((AnimationClip)state.motion,
                EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Open"),
                AnimationCurve.Linear(0, 0, 10, 100));
            Assert.That(Assert.Throws<NeutralShapeSamplingException>(() => NeutralShapeSampler.Sample(avatar)).Message, Does.Contain("時間で変わる"));
        }

        [Test]
        public void IndependentAutomaticBlinkAndVisemeDoNotBecomeTheRestFace()
        {
            Open();
            var blink = Layer("Automatic blinking");
            blink.defaultState = State(blink, Clip("Blink", AnimationCurve.Linear(0, 0, 10, 100)));
            controller.AddParameter("Viseme", AnimatorControllerParameterType.Int);
            var mouth = Layer("Lip sync");
            var idle = State(mouth, Clip("vrc.v.aa", AnimationCurve.Constant(0, 1, 0))); mouth.defaultState = idle;
            var spoken = State(mouth, Clip("vrc.v.aa", AnimationCurve.Constant(0, 1, 100)));
            var transition = idle.AddTransition(spoken); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "Viseme");
            var values = NeutralShapeSampler.Sample(avatar);
            Assert.That(values.Select(value => value.Shape), Is.EquivalentTo(new[] { "Open" }));
            Assert.That(values[0].Weight, Is.EqualTo(100).Within(.01));
        }

        [Test]
        public void AutomaticAnimationSharingTheOpeningChannelIsRejected()
        {
            Open(); var blink = Layer("Blink also changes opening");
            var clip = Clip("Blink", AnimationCurve.Linear(0, 0, 10, 100));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Open"),
                AnimationCurve.Linear(0, 100, 10, 0));
            blink.defaultState = State(blink, clip);
            Assert.Throws<NeutralShapeSamplingException>(() => NeutralShapeSampler.Sample(avatar));
        }

        [Test]
        public void ExternalInputCannotChooseTheNeutralOpeningPose()
        {
            var idle = Open(0); controller.AddParameter("Voice", AnimatorControllerParameterType.Float);
            var selected = State(controller.layers[0].stateMachine, Clip("Open", AnimationCurve.Constant(0, 1, 100)));
            var transition = idle.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Greater, .5f, "Voice");
            var message = Assert.Throws<NeutralShapeSamplingException>(() => NeutralShapeSampler.Sample(avatar)).Message;
            Assert.That(message, Does.Contain("外部入力").And.Contain("Body / Open"));
        }

        [Test]
        public void KnownNeutralGestureZeroPrunesAnUnreachableAnimatedExpression()
        {
            var idle = Open(); controller.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
            var selected = State(controller.layers[0].stateMachine, Clip("Open", AnimationCurve.Linear(0, 0, 10, 100)));
            var transition = idle.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 2, "GestureRight");
            Assert.That(NeutralShapeSampler.Sample(avatar).Single(value => value.Shape == "Open").Weight, Is.EqualTo(100).Within(.01));
        }

        [Test]
        public void CommonDefaultFaceCanResetBlinkWhileIndependentBlinkStaysLive()
        {
            var state = Open(); skin.SetBlendShapeWeight(1, 100);
            AnimationUtility.SetEditorCurve((AnimationClip)state.motion,
                EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Blink"), AnimationCurve.Constant(0, 1, 0));
            var blink = Layer("BLINK"); blink.defaultState = State(blink, Clip("Blink", AnimationCurve.Linear(0, 0, 10, 100)));
            var values = NeutralShapeSampler.Sample(avatar);
            Assert.That(values.Single(value => value.Shape == "Open").Weight, Is.EqualTo(100).Within(.01));
            Assert.That(values.Single(value => value.Shape == "Blink").Weight, Is.Zero.Within(.01));
        }

        [Test]
        public void AutomaticChannelTimedInitializerStillResetsItsStaticClosingWeight()
        {
            Open(); skin.SetBlendShapeWeight(1, 100);
            var blink = Layer("Blink initializer");
            var initial = State(blink, Clip("Blink", AnimationCurve.Constant(0, 1, 0))); blink.defaultState = initial;
            var held = State(blink);
            var transition = initial.AddTransition(held); transition.hasExitTime = true; transition.exitTime = .01f; transition.duration = 0;
            var values = NeutralShapeSampler.Sample(avatar);
            Assert.That(values.Single(value => value.Shape == "Open").Weight, Is.EqualTo(100).Within(.01));
            Assert.That(values.Single(value => value.Shape == "Blink").Weight, Is.Zero.Within(.01));
        }

        // The source generator uses a separate DEFAULT FACE underlay, a local
        // SetControl driver, nested Not AFK/AFK machines and alternative mode /
        // branch states on FACE EMOTE PLAYER. Voice gates are AND transitions.
        [TestCase(false)]
        [TestCase(true)]
        public void GeneratedFaceEmoStyleNestedFxPrunesProvedInactiveVoiceGatesAndRejectsUnprovedOnes(bool waitByVoice)
        {
            Open();
            controller.AddParameter("AFK", AnimatorControllerParameterType.Bool);
            controller.AddParameter("FaceEmo_CN_EMOTE_OVERRIDE", AnimatorControllerParameterType.Bool);
            controller.AddParameter("FaceEmo_CN_BYPASS", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Voice", AnimatorControllerParameterType.Float);
            controller.AddParameter("GestureLeft", AnimatorControllerParameterType.Int);
            controller.AddParameter("FaceEmo_EM_EMOTE_PRESELECT", AnimatorControllerParameterType.Int);
            controller.AddParameter("FaceEmo_SYNC_EM_EMOTE", AnimatorControllerParameterType.Int);
            controller.AddParameter("FaceEmo_SYNC_CN_WAIT_FACE_EMOTE_BY_VOICE", AnimatorControllerParameterType.Bool);
            var parameters = controller.parameters;
            parameters.Single(parameter => parameter.name == "FaceEmo_SYNC_CN_WAIT_FACE_EMOTE_BY_VOICE").defaultBool = waitByVoice;
            controller.parameters = parameters;
            var input = Layer("INPUT CONVERTER L");
            var neutral = State(input); input.defaultState = neutral;
            ParameterDriverExpressionTests.Driver(neutral, ParameterDriverExpressionTests.Op("Set", "FaceEmo_EM_EMOTE_PRESELECT", 0));
            input.AddEntryTransition(neutral).AddCondition(AnimatorConditionMode.Equals, 0, "GestureLeft");
            var gesture = State(input);
            ParameterDriverExpressionTests.Driver(gesture, ParameterDriverExpressionTests.Op("Set", "FaceEmo_EM_EMOTE_PRESELECT", 1));
            input.AddEntryTransition(gesture).AddCondition(AnimatorConditionMode.Equals, 1, "GestureLeft");
            var control = Layer("FACE EMOTE SET CONTROL"); control.defaultState = State(control);
            ParameterDriverExpressionTests.Driver(control.defaultState,
                ParameterDriverExpressionTests.Op("Set", "FaceEmo_SYNC_EM_EMOTE", 1),
                ParameterDriverExpressionTests.Op("Set", "FaceEmo_SYNC_CN_WAIT_FACE_EMOTE_BY_VOICE", waitByVoice ? 1 : 0));
            var player = Layer("[ USER EDIT ] FACE EMOTE PLAYER");
            var normal = player.AddStateMachine("Not AFK");
            player.AddEntryTransition(normal).AddCondition(AnimatorConditionMode.IfNot, 0, "AFK");
            var afk = player.AddStateMachine("AFK");
            player.AddEntryTransition(afk).AddCondition(AnimatorConditionMode.If, 0, "AFK");
            afk.defaultState = State(afk, Clip("Open", AnimationCurve.Linear(0, 0, 10, 100)));
            afk.defaultState.motion.name = "AFK dynamic opening";
            var mode = State(normal, Clip("Open", AnimationCurve.Constant(0, 1, 70))); normal.defaultState = mode;
            var branch = State(normal, Clip("Open", AnimationCurve.Constant(0, 1, 20)));
            mode.motion.name = "Mode default opening70"; branch.motion.name = "Branch opening20";
            normal.AddEntryTransition(mode).AddCondition(AnimatorConditionMode.Equals, 1, "FaceEmo_SYNC_EM_EMOTE");
            normal.AddEntryTransition(branch).AddCondition(AnimatorConditionMode.Equals, 2, "FaceEmo_SYNC_EM_EMOTE");
            // FaceEmo FxGenerator.GenerateFaceEmotePlayerLayer creates both
            // direct root states with conditional exits. The exit is necessary:
            // a root fallback without it never enters the selected nested mode.
            var overriding = State(player); overriding.name = "in OVERRIDE"; player.defaultState = overriding;
            var bypass = State(player); bypass.name = "BYPASS";
            var enterOverride = player.AddAnyStateTransition(overriding); enterOverride.hasExitTime = false; enterOverride.duration = 0;
            enterOverride.AddCondition(AnimatorConditionMode.If, 0, "FaceEmo_CN_EMOTE_OVERRIDE");
            enterOverride.AddCondition(AnimatorConditionMode.IfNot, 0, "FaceEmo_CN_BYPASS");
            var leaveOverride = overriding.AddExitTransition(); leaveOverride.hasExitTime = false; leaveOverride.duration = 0;
            leaveOverride.AddCondition(AnimatorConditionMode.IfNot, 0, "FaceEmo_CN_EMOTE_OVERRIDE");
            var enterBypass = player.AddAnyStateTransition(bypass); enterBypass.hasExitTime = false; enterBypass.duration = 0;
            enterBypass.AddCondition(AnimatorConditionMode.If, 0, "FaceEmo_CN_BYPASS");
            var leaveBypass = bypass.AddExitTransition(); leaveBypass.hasExitTime = false; leaveBypass.duration = 0;
            leaveBypass.AddCondition(AnimatorConditionMode.IfNot, 0, "FaceEmo_CN_BYPASS");
            player.AddStateMachineExitTransition(normal); player.AddStateMachineExitTransition(afk);
            var changed = mode.AddExitTransition(); changed.hasExitTime = false; changed.duration = 0;
            changed.AddCondition(AnimatorConditionMode.NotEqual, 1, "FaceEmo_SYNC_EM_EMOTE");
            changed.AddCondition(AnimatorConditionMode.If, 0, "FaceEmo_SYNC_CN_WAIT_FACE_EMOTE_BY_VOICE");
            changed.AddCondition(AnimatorConditionMode.Less, .01f, "Voice");
            if (!waitByVoice) AssertOriginalNestedModePose(mode);
            if (waitByVoice)
                Assert.That(Assert.Throws<NeutralShapeSamplingException>(() => NeutralShapeSampler.Sample(avatar)).Message, Does.Contain("Voice"));
            else
                Assert.That(NeutralShapeSampler.Sample(avatar).Single(value => value.Shape == "Open").Weight, Is.EqualTo(70).Within(.01));
        }

        // Native routing is checked independently of the exporter's controller
        // clone. Original VRC drivers do not execute in EditMode, so seed the
        // same known post-driver values before evaluating the original graph.
        private void AssertOriginalNestedModePose(AnimatorState mode)
        {
            var preview = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            GameObject copy = null;
            var graph = default(PlayableGraph);
            try
            {
                copy = Object.Instantiate(avatar); copy.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(copy, preview);
                foreach (var component in copy.GetComponentsInChildren<Behaviour>(true)) component.enabled = false;
                var animator = copy.GetComponent<Animator>(); animator.runtimeAnimatorController = null; animator.enabled = true;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.applyRootMotion = false; animator.fireEvents = false;
                copy.SetActive(true);
                graph = PlayableGraph.Create("Generated FaceEmo fixture native routing");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                AnimationPlayableOutput.Create(graph, "Original FaceEmo fixture", animator).SetSourcePlayable(playable);
                playable.SetBool("AFK", false); playable.SetFloat("Voice", 0); playable.SetInteger("GestureLeft", 0);
                playable.SetInteger("FaceEmo_EM_EMOTE_PRESELECT", 0); playable.SetInteger("FaceEmo_SYNC_EM_EMOTE", 1);
                playable.SetBool("FaceEmo_SYNC_CN_WAIT_FACE_EMOTE_BY_VOICE", false);
                playable.SetBool("FaceEmo_CN_EMOTE_OVERRIDE", false); playable.SetBool("FaceEmo_CN_BYPASS", false);
                graph.Play(); graph.Evaluate(0f);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                var layer = Array.FindIndex(controller.layers, value => value.name == "[ USER EDIT ] FACE EMOTE PLAYER");
                Assert.That(playable.IsInTransition(layer), Is.False, "The original generated topology must settle.");
                Assert.That(playable.GetCurrentAnimatorStateInfo(layer).fullPathHash,
                    Is.EqualTo(Animator.StringToHash(controller.layers[layer].name + ".Not AFK." + mode.name)),
                    "The original graph must select the Mode state before testing the exporter's private clone.");
                Assert.That(copy.transform.Find("Body").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(0),
                    Is.EqualTo(70).Within(.01f), "The original Mode clip controls opening above the common default.");
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                if (copy != null) Object.DestroyImmediate(copy);
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(preview);
            }
        }

        [Test]
        public void StartupParameterDriverReachesTheMorphLayer()
        {
            controller.AddParameter("Face", AnimatorControllerParameterType.Int);
            var gate = controller.layers[0].stateMachine;
            gate.defaultState = State(gate);
            ParameterDriverExpressionTests.Driver(gate.defaultState, ParameterDriverExpressionTests.Op("Set", "Face", 1));
            var face = Layer("Face"); var idle = State(face, Clip("Open", AnimationCurve.Constant(0, 1, 0))); face.defaultState = idle;
            var open = State(face, Clip("Open", AnimationCurve.Constant(0, 1, 100)));
            var transition = idle.AddTransition(open); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "Face");
            Assert.That(NeutralShapeSampler.Sample(avatar).Single(value => value.Shape == "Open").Weight, Is.EqualTo(100).Within(.01));
        }

        [Test]
        public void FractionalIntSetUsesDriverTruncationAndCannotPruneItsZeroEndpoint()
        {
            var idle = Open(0); controller.AddParameter("Face", AnimatorControllerParameterType.Int);
            var parameters = controller.parameters; parameters.Single(parameter => parameter.name == "Face").defaultInt = 1;
            controller.parameters = parameters;
            ParameterDriverExpressionTests.Driver(idle, ParameterDriverExpressionTests.Op("Set", "Face", .9f));
            var opened = State(controller.layers[0].stateMachine, Clip("Open", AnimationCurve.Constant(0, 1, 100)));
            var transition = idle.AddTransition(opened); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 0, "Face");
            Assert.That(NeutralShapeSampler.Sample(avatar).Single(value => value.Shape == "Open").Weight, Is.EqualTo(100).Within(.01));
        }

        [Test]
        public void NestedExitReachesItsParentDestinationAndPrunesFalseAncestorAnyState()
        {
            var root = controller.layers[0].stateMachine;
            var startup = root.AddStateMachine("Startup");
            startup.defaultState = State(startup, Clip("Open", AnimationCurve.Constant(0, 1, 0)));
            var exit = startup.defaultState.AddExitTransition(); exit.hasExitTime = true; exit.exitTime = .01f; exit.duration = 0;
            var held = State(root, Clip("Open", AnimationCurve.Constant(0, 1, 100))); root.defaultState = held;
            root.AddEntryTransition(startup); root.AddStateMachineTransition(startup, held);
            controller.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
            var unreachable = State(root, Clip("Open", AnimationCurve.Linear(0, 0, 10, 100)));
            var gesture = root.AddAnyStateTransition(unreachable); gesture.hasExitTime = false; gesture.duration = 0;
            gesture.AddCondition(AnimatorConditionMode.Equals, 2, "GestureRight");
            Assert.That(NeutralShapeSampler.Sample(avatar).Single(value => value.Shape == "Open").Weight, Is.EqualTo(100).Within(.01));
        }

        [Test]
        public void ActiveAncestorAnyStateExternalInputIsRetainedForNestedNeutralStates()
        {
            var root = controller.layers[0].stateMachine;
            var child = root.AddStateMachine("Nested face");
            child.defaultState = State(child, Clip("Open", AnimationCurve.Constant(0, 1, 100))); root.AddEntryTransition(child);
            controller.AddParameter("Voice", AnimatorControllerParameterType.Float);
            var externallyChosen = State(root, Clip("Open", AnimationCurve.Constant(0, 1, 0)));
            var voice = root.AddAnyStateTransition(externallyChosen); voice.hasExitTime = false; voice.duration = 0;
            voice.AddCondition(AnimatorConditionMode.Greater, .5f, "Voice");
            Assert.That(Assert.Throws<NeutralShapeSamplingException>(() => NeutralShapeSampler.Sample(avatar)).Message, Does.Contain("Voice"));
        }

        [Test]
        public void AutomaticBlinkWithWriteDefaultsCanAffectTheOpeningAndIsNotExcluded()
        {
            Open(); var blink = Layer("Blink with implicit writes");
            blink.defaultState = State(blink, Clip("Blink", AnimationCurve.Linear(0, 0, 10, 100)), writeDefaults: true);
            Assert.Throws<NeutralShapeSamplingException>(() => NeutralShapeSampler.Sample(avatar));
        }

        [Test]
        public void RelevantRandomDriverCannotInventANeutralFace()
        {
            controller.AddParameter("Face", AnimatorControllerParameterType.Int);
            var open = Open(); ParameterDriverExpressionTests.Driver(open, ParameterDriverExpressionTests.Op("Random", "Face"));
            var other = State(controller.layers[0].stateMachine, Clip("Open", AnimationCurve.Constant(0, 1, 0)));
            var transition = open.AddTransition(other); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "Face");
            Assert.That(Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar)).Message, Does.Contain("Random"));
        }

        [Test]
        public void OverrideClipAndWriteDefaultsUseThePreparedFxPose()
        {
            var original = (AnimationClip)Open(writeDefaults: true).motion;
            var replacement = Clip("Open", AnimationCurve.Constant(0, 1, 75));
            var overrides = new AnimatorOverrideController(controller);
            try
            {
                overrides.ApplyOverrides(new[] { new KeyValuePair<AnimationClip, AnimationClip>(original, replacement) });
                SetFx(overrides);
                Assert.That(NeutralShapeSampler.Sample(avatar).Single(value => value.Shape == "Open").Weight, Is.EqualTo(75).Within(.01));
                Assert.That(controller.layers[0].stateMachine.defaultState.motion, Is.SameAs(original));
            }
            finally { Object.DestroyImmediate(overrides); }
        }

        [Test]
        public void ExcludedRendererDoesNotContributeANeutralPose()
        {
            Open(); Assert.That(NeutralShapeSampler.Sample(avatar, path => path == "Body"), Is.Empty);
        }
    }
}
