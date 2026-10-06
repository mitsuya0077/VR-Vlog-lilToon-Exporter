using System;
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
    public sealed class SelectedLayerControlTests
    {
        private string folder;
        private GameObject avatar;
        private Mesh mesh;
        private SkinnedMeshRenderer skin;
        private Component descriptor;
        private AnimatorController controller;
        private AnimatorState command, neutral, selected;
        private const int TargetLayer = 4;

        private static Type Sdk(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(name)).FirstOrDefault(type => type != null);

        [SetUp]
        public void SetUp()
        {
            if (Sdk("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor") == null || Sdk("VRC.SDK3.Avatars.Components.VRCAnimatorLayerControl") == null)
                Assert.Ignore("Install the real VRChat SDK.");
            var name = "__SelectedLayerControl_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            avatar = new GameObject("Avatar", typeof(Animator));
            var body = new GameObject("Body", typeof(SkinnedMeshRenderer)); body.transform.SetParent(avatar.transform, false);
            skin = body.GetComponent<SkinnedMeshRenderer>();
            mesh = new Mesh { name = "Unchanged authored body" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }; mesh.triangles = new[] { 0, 1, 2 };
            mesh.AddBlendShapeFrame("Body size", 100, Enumerable.Repeat(Vector3.right * .02f, 3).ToArray(), null, null);
            skin.sharedMesh = mesh; skin.SetBlendShapeWeight(0, 17);
            controller = Graph("FX", new[] { "Body" }, "Body size", 1, out command, out neutral, out selected);
            descriptor = avatar.AddComponent(Sdk("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor")); SetFx(descriptor, controller);
        }

        [TearDown]
        public void TearDown()
        {
            if (avatar != null) Object.DestroyImmediate(avatar);
            if (mesh != null) Object.DestroyImmediate(mesh);
            if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        private static AnimatorState State(AnimatorStateMachine machine, string name, AnimationClip clip)
        {
            var state = machine.AddState(name); state.motion = clip; state.writeDefaultValues = false;
            if (machine.states.Length == 1) machine.defaultState = state; return state;
        }

        private static AnimatorStateMachine Layer(AnimatorController fx, string name)
        {
            fx.AddLayer(name); var layers = fx.layers; layers[layers.Length - 1].defaultWeight = 1; fx.layers = layers;
            return layers[layers.Length - 1].stateMachine;
        }

        private static void To(AnimatorState from, AnimatorState to, float value)
        {
            var transition = from.AddTransition(to); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, value, "Menu");
        }

        private static AnimationClip Clip(AnimatorController fx, string name, IEnumerable<string> paths = null, string shape = "Body size", float value = 0)
        {
            var clip = new AnimationClip { name = name };
            foreach (var path in paths ?? Enumerable.Empty<string>())
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + shape), AnimationCurve.Constant(0, 1, value));
            AssetDatabase.AddObjectToAsset(clip, fx); return clip;
        }

        private AnimatorController Graph(string name, string[] paths, string shape, float weight,
            out AnimatorState control, out AnimatorState rest, out AnimatorState endpoint, float baseWeight = 30)
        {
            var fx = AnimatorController.CreateAnimatorControllerAtPath(folder + "/" + name + ".controller");
            fx.AddParameter("Menu", AnimatorControllerParameterType.Int);
            State(fx.layers[0].stateMachine, "Base rest", Clip(fx, "Base rest", paths, shape, baseWeight));
            control = State(Layer(fx, "SDK command source"), "Command", Clip(fx, "Empty command motion"));
            State(Layer(fx, "Unrelated slot 2"), "Empty", Clip(fx, "Empty 2"));
            State(Layer(fx, "Unrelated slot 3"), "Empty", Clip(fx, "Empty 3"));
            var target = Layer(fx, "Controlled face at physical index 4");
            rest = State(target, "Neutral", Clip(fx, "Neutral", paths, shape, 60));
            endpoint = State(target, "Selected endpoint", Clip(fx, "Selected endpoint", paths, shape, 80));
            To(rest, endpoint, 1); To(endpoint, rest, 0);
            var layers = fx.layers; layers[TargetLayer].defaultWeight = weight; fx.layers = layers; return fx;
        }

        private static StateMachineBehaviour Control(AnimatorState state, float goal, int target = TargetLayer, float duration = 0, string playable = "FX")
            => Configure(state.AddStateMachineBehaviour(Sdk("VRC.SDK3.Avatars.Components.VRCAnimatorLayerControl")), goal, target, duration, playable);

        private static StateMachineBehaviour Configure(StateMachineBehaviour value, float goal, int target, float duration, string playable)
        {
            Assert.That(value, Is.Not.Null);
            using var data = new SerializedObject(value);
            var property = data.FindProperty("playable"); property.enumValueIndex = Array.IndexOf(property.enumNames, playable);
            data.FindProperty("layer").intValue = target; data.FindProperty("goalWeight").floatValue = goal;
            data.FindProperty("blendDuration").floatValue = duration; data.ApplyModifiedPropertiesWithoutUndo(); return value;
        }

        private static void SetFx(Component target, AnimatorController fx)
        {
            using var data = new SerializedObject(target);
            data.FindProperty("customizeAnimationLayers").boolValue = true;
            var list = data.FindProperty("baseAnimationLayers"); list.arraySize = 1;
            var layer = list.GetArrayElementAtIndex(0); var type = layer.FindPropertyRelative("type");
            type.enumValueIndex = Array.IndexOf(type.enumNames, "FX"); layer.FindPropertyRelative("isDefault").boolValue = false;
            layer.FindPropertyRelative("animatorController").objectReferenceValue = fx; data.ApplyModifiedPropertiesWithoutUndo();
        }

        private List<VrChatExpressionMenu.MorphValue> Sample(float menu = 1)
        {
            var metadata = VrChatExpressionMenu.Read(avatar, new VrChatMenuImportPolicy { SkipAll = true });
            return VrChatExpressionSampler.SampleFixed(avatar, controller, metadata.Defaults, new Dictionary<string, float> { ["Menu"] = menu }, metadata: metadata);
        }

        private Dictionary<Object, string> Before() => AssetDatabase.LoadAllAssetsAtPath(folder + "/FX.controller")
            .Concat(new Object[] { mesh, descriptor }).ToDictionary(value => value, value => EditorJsonUtility.ToJson(value));

        private void Unchanged(Dictionary<Object, string> before)
        {
            foreach (var pair in before) Assert.That(EditorJsonUtility.ToJson(pair.Key), Is.EqualTo(pair.Value), pair.Key.name);
            Assert.That(skin.sharedMesh, Is.SameAs(mesh)); Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17));
        }

        // This graph contains no SDK callback or exporter adapter. Apply the
        // documented final layer weight through Unity's native API instead.
        private float Native(AnimatorController reference, float goal, int target = TargetLayer, float menu = 1, int shapeIndex = 0)
        {
            var copy = Object.Instantiate(avatar); var graph = PlayableGraph.Create("SDK layer weight native reference");
            try
            {
                foreach (var behaviour in copy.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                var animator = copy.GetComponent<Animator>(); animator.enabled = true; animator.runtimeAnimatorController = null;
                animator.applyRootMotion = false; animator.fireEvents = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, reference);
                for (var index = 0; index < reference.layers.Length; index++) playable.SetLayerWeight(index, index == 0 ? 1 : reference.layers[index].defaultWeight);
                playable.SetLayerWeight(target, target == 0 ? 1 : goal); playable.SetInteger("Menu", Mathf.RoundToInt(menu));
                AnimationPlayableOutput.Create(graph, "Native reference", animator).SetSourcePlayable(playable);
                graph.Play(); graph.Evaluate(0);
                for (var frame = 0; frame < 240; frame++) graph.Evaluate(1f / 60);
                return copy.transform.Find("Body").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(shapeIndex);
            }
            finally { if (graph.IsValid()) graph.Destroy(); Object.DestroyImmediate(copy); }
        }

        [TestCase(0f, 0f)] [TestCase(0f, .5f)] [TestCase(0f, 1f)]
        [TestCase(.5f, 0f)] [TestCase(.5f, .5f)] [TestCase(.5f, 1f)]
        [TestCase(1f, 0f)] [TestCase(1f, .5f)] [TestCase(1f, 1f)]
        public void InstantIndividualFxControlMatchesNativeSelectedWeight(float goal, float initialWeight)
        {
            var layers = controller.layers; layers[TargetLayer].defaultWeight = initialWeight; controller.layers = layers;
            Control(command, goal); var before = Before();
            var reference = Graph("Reference", new[] { "Body" }, "Body size", initialWeight, out _, out _, out _);
            var expected = Native(reference, goal);
            Assert.That(expected, Is.EqualTo(30 + 50 * goal).Within(.01));
            Assert.That(Sample().Single(value => value.Shape == "Body size").Weight, Is.EqualTo(expected).Within(.01));
            Unchanged(before);
        }

        [Test]
        public void SelectedControlCanEnableAStaticInitiallyZeroTarget()
        {
            var machine = controller.layers[TargetLayer].stateMachine;
            neutral.transitions = Array.Empty<AnimatorStateTransition>(); machine.RemoveState(selected);
            AnimationUtility.SetEditorCurve((AnimationClip)neutral.motion, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Body size"), AnimationCurve.Constant(0, 1, 80));
            var layers = controller.layers; layers[TargetLayer].defaultWeight = 0; controller.layers = layers;
            var on = State(controller.layers[1].stateMachine, "Menu enables target", Clip(controller, "On"));
            Control(on, 1); To(command, on, 1);
            var before = Before();
            Assert.That(Sample().Single(value => value.Shape == "Body size").Weight, Is.EqualTo(80).Within(.01));
            Unchanged(before);
        }

        [Test]
        public void LayerRelaySelectionUsesStateEntryOrderAndFreshProbes()
        {
            Control(command, 0); var on = State(controller.layers[1].stateMachine, "Relay enables layer", Clip(controller, "On"));
            Control(on, 1); To(command, on, 1); To(on, command, 0);
            var before = Before();
            foreach (var menu in new[] { 1f, 0f, 1f })
                Assert.That(Sample(menu).Single(value => value.Shape == "Body size").Weight, Is.EqualTo(menu == 0 ? 30 : 80).Within(.01));
            Unchanged(before);
        }

        [TestCase(false)] [TestCase(true)]
        public void GeneratedConstantSignalRelayDoesNotDiscardSelectedExpressions(bool maNames)
        {
            var parameter = maNames ? "__MA/Internal/MMDNotActive" : "Internal relay signal";
            controller.AddParameter(new AnimatorControllerParameter { name = parameter, type = AnimatorControllerParameterType.Float, defaultFloat = 0 });
            var layers = controller.layers; layers[1].name = maNames ? "Modular Avatar: MMD Control" : "Generic relay"; controller.layers = layers;
            var machine = layers[1].stateMachine;
            command.name = "Initial";
            var clip = (AnimationClip)command.motion;
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), parameter), AnimationCurve.Constant(0, 1, 1));
            var off = State(machine, maNames ? "MMD" : "Disabled", clip); Control(off, 0);
            var on = State(machine, maNames ? "NotMMD" : "Enabled", clip); Control(on, 1);
            void Relay(AnimatorState from, AnimatorState to, AnimatorConditionMode mode)
            {
                var transition = from.AddTransition(to); transition.hasExitTime = false; transition.duration = 0;
                transition.AddCondition(mode, .5f, parameter);
            }
            Relay(command, off, AnimatorConditionMode.Less); Relay(off, on, AnimatorConditionMode.Greater); Relay(on, off, AnimatorConditionMode.Less);
            var before = Before();
            Assert.That(Sample().Single(value => value.Shape == "Body size").Weight, Is.EqualTo(80).Within(.01)); Unchanged(before);
        }

        [TestCase("native", 0f)] [TestCase("native", .5f)] [TestCase("native", 1f)]
        [TestCase("inactive", .5f)]
        [TestCase("standalone", 0f)] [TestCase("standalone", .5f)] [TestCase("standalone", 1f)]
        public void DirectClipControlsPreserveNativeSlotAndStandaloneIndexMapping(string mode, float goal)
        {
            Control(command, goal);
            var endpoint = (AnimationClip)selected.motion;
            var reference = Graph("Direct reference", new[] { "Body" }, "Body size", 1, out _, out var referenceRest, out var referenceSelected,
                baseWeight: mode == "standalone" ? 80 : 30);
            if (mode == "inactive")
            { var layers = controller.layers; layers[TargetLayer].defaultWeight = 0; controller.layers = layers; }
            if (mode == "standalone")
            {
                controller.layers[0].stateMachine.defaultState.motion = Clip(controller, "Empty original base");
                neutral.transitions = Array.Empty<AnimatorStateTransition>(); controller.layers[TargetLayer].stateMachine.RemoveState(selected);
                referenceRest.transitions = Array.Empty<AnimatorStateTransition>(); reference.layers[TargetLayer].stateMachine.RemoveState(referenceSelected);
            }
            var expected = Native(reference, goal, menu: mode == "standalone" ? 0 : 1);
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, endpoint, entry);
            var before = Before(); var metadata = VrChatExpressionMenu.Read(avatar, new VrChatMenuImportPolicy { SkipAll = true });
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry,
                mode == "standalone" ? (int?)null : TargetLayer, metadata: metadata, sourceState: mode == "standalone" ? null : selected);
            Assert.That(entry.Values.Single(value => value.Shape == "Body size").Weight, Is.EqualTo(expected).Within(.01));
            Unchanged(before);
        }

        [TestCase(false)] [TestCase(true)]
        public void InactiveDirectClipDistinguishesDormantEnableFromReachedExplicitDisable(bool dormantEnable)
        {
            var layers = controller.layers; layers[TargetLayer].defaultWeight = 0; controller.layers = layers;
            if (dormantEnable)
            {
                controller.AddParameter(new AnimatorControllerParameter { name = "AFK", type = AnimatorControllerParameterType.Bool, defaultBool = true });
                var dormant = State(controller.layers[1].stateMachine, "Dormant enable", Clip(controller, "Dormant enable"));
                Control(dormant, 1);
                var transition = command.AddTransition(dormant); transition.hasExitTime = false; transition.duration = 0;
                transition.AddCondition(AnimatorConditionMode.If, 0, "AFK");
            }
            else Control(command, 0);
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var metadata = VrChatExpressionMenu.Read(avatar, new VrChatMenuImportPolicy { SkipAll = true }); var before = Before();
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, TargetLayer, metadata: metadata, sourceState: selected);
            Assert.That(entry.Values.Single(value => value.Shape == "Body size").Weight, Is.EqualTo(dormantEnable ? 80 : 30).Within(.01),
                dormantEnable ? "A normally unreachable enable command cannot erase the legacy inactive authored clip." :
                    "A reached SDK disable is an explicit effect: preserve the native underlay instead of exposing the inactive candidate.");
            Unchanged(before);
        }

        [Test]
        public void ZeroWeightControlSourceCannotHideALaterParameterDrivenWeightChange()
        {
            controller.AddParameter("Future control input", AnimatorControllerParameterType.Float);
            var layers = controller.layers; layers[1].defaultWeight = 0; controller.layers = layers;
            Control(command, 1);
            AnimationUtility.SetEditorCurve((AnimationClip)command.motion, EditorCurveBinding.FloatCurve("", typeof(Animator), "Future control input"),
                AnimationCurve.Linear(0, 0, 10, 1));
            var later = State(controller.layers[1].stateMachine, "Delayed disable", Clip(controller, "Delayed disable")); Control(later, 0);
            var transition = command.AddTransition(later); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Greater, .5f, "Future control input");
            var before = Before(); var error = Assert.Throws<InvalidOperationException>(() => Sample());
            Assert.That(error.Message, Does.Contain("Future control input")); Unchanged(before);
        }

        [Test]
        public void StandaloneClipKeepsOriginalSdkBaseLayerAtUnitWeightAfterIndexRemap()
        {
            foreach (var state in controller.layers[TargetLayer].stateMachine.states) state.state.motion = Clip(controller, "Unwritten target");
            Control(command, 0, target: 0);
            var endpoint = Clip(controller, "Standalone candidate", new[] { "Body" }, value: 80);
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, endpoint, entry);
            var metadata = VrChatExpressionMenu.Read(avatar, new VrChatMenuImportPolicy { SkipAll = true }); var before = Before();
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, metadata: metadata);
            Assert.That(entry.Values.Single(value => value.Shape == "Body size").Weight, Is.EqualTo(30).Within(.01),
                "The original SDK base is moved to private slot 1 but must remain fully weighted above the standalone 80 clip.");
            Unchanged(before);
        }

        [Test]
        public void DirectBaseClipWithoutStateProvenanceCannotDiscardItsLayerControl()
        {
            var candidate = State(controller.layers[0].stateMachine, "Base callback candidate",
                Clip(controller, "Base callback candidate", new[] { "Body" }, value: 80));
            Control(candidate, 1);
            var layers = controller.layers; layers[TargetLayer].defaultWeight = 0; controller.layers = layers;
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)candidate.motion, entry);
            var metadata = VrChatExpressionMenu.Read(avatar, new VrChatMenuImportPolicy { SkipAll = true }); var before = Before();
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 0, metadata: metadata));
            Assert.That(error.Message, Does.Contain(candidate.name));
            Assert.That(entry.Values.Single(value => value.Shape == "Body size").Weight, Is.EqualTo(80)); Unchanged(before);
        }

        [Test]
        public void InactiveSelectedStateKeepsItsOwnWeightButExecutesAControlOfAnotherLayer()
        {
            mesh.AddBlendShapeFrame("Auxiliary", 100, Enumerable.Repeat(Vector3.up * .03f, mesh.vertexCount).ToArray(), null, null);
            skin.SetBlendShapeWeight(1, 7);
            var auxiliary = EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Auxiliary");
            AnimationUtility.SetEditorCurve((AnimationClip)selected.motion, auxiliary, AnimationCurve.Constant(0, 1, 50));
            controller.layers[2].stateMachine.defaultState.motion = Clip(controller, "Controlled auxiliary", new[] { "Body" }, "Auxiliary", 90);
            var layers = controller.layers; layers[2].defaultWeight = 0; layers[TargetLayer].defaultWeight = 0; controller.layers = layers;
            var sdkControl = Control(selected, 1, target: 2);
            var reference = Graph("Inactive other-target reference", new[] { "Body" }, "Body size", 0, out _, out _, out var referenceSelected);
            AnimationUtility.SetEditorCurve((AnimationClip)referenceSelected.motion, auxiliary, AnimationCurve.Constant(0, 1, 50));
            reference.layers[2].stateMachine.defaultState.motion = Clip(reference, "Native auxiliary", new[] { "Body" }, "Auxiliary", 90);
            var nativeBody = Native(reference, 1, target: 2);
            var nativeAuxiliary = Native(reference, 1, target: 2, shapeIndex: 1);
            Assert.That(nativeBody, Is.EqualTo(30).Within(.01)); Assert.That(nativeAuxiliary, Is.EqualTo(90).Within(.01));
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var before = Before(); var metadata = VrChatExpressionMenu.Read(avatar, new VrChatMenuImportPolicy { SkipAll = true });
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, TargetLayer, metadata: metadata, sourceState: selected);
            Assert.That(entry.Values.Single(value => value.Shape == "Body size").Weight, Is.EqualTo(nativeBody).Within(.01),
                "Entering an inactive source state must not apply its authored Body=80 pose.");
            Assert.That(entry.Values.Single(value => value.Shape == "Auxiliary").Weight, Is.EqualTo(nativeAuxiliary).Within(.01),
                "The same zero-weight state's SDK callback still enables the other native layer.");
            Assert.That(controller.layers[TargetLayer].defaultWeight, Is.Zero); Assert.That(selected.behaviours, Does.Contain(sdkControl));
            Assert.That(skin.GetBlendShapeWeight(1), Is.EqualTo(7)); Unchanged(before);
        }

        [TestCase(false)] [TestCase(true)]
        public void SelectedCallbackRetainsItsUpperNonEquivalentSelectorTarget(bool alternate)
        {
            command.motion = Clip(controller, "Lower selected candidate", new[] { "Body" }, value: 90);
            var layers = controller.layers; layers[TargetLayer].defaultWeight = 0; controller.layers = layers;
            var sdkControl = Control(command, 1);
            var reference = Graph("Upper selector native reference", new[] { "Body" }, "Body size", 0,
                out var referenceCommand, out _, out _);
            referenceCommand.motion = Clip(reference, "Native lower selected candidate", new[] { "Body" }, value: 90);
            var expected = Native(reference, 1, menu: alternate ? 1 : 0);
            Assert.That(expected, Is.EqualTo(alternate ? 80 : 60).Within(.01));

            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)command.motion, entry);
            var metadata = VrChatExpressionMenu.Read(avatar, new VrChatMenuImportPolicy { SkipAll = true });
            if (alternate) metadata.Defaults["Menu"] = 1;
            var before = Before();
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1, metadata: metadata, sourceState: command);
            Assert.That(entry.Values.Single(value => value.Shape == "Body size").Weight, Is.EqualTo(expected).Within(.01),
                "An explicitly controlled upper selector must retain its state graph instead of being erased as a competing direct expression.");
            Assert.That(controller.layers[TargetLayer].defaultWeight, Is.Zero);
            Assert.That(controller.layers[TargetLayer].stateMachine.states.Length, Is.EqualTo(2));
            Assert.That(command.behaviours, Does.Contain(sdkControl)); Unchanged(before);
        }

        [TestCase(false)] [TestCase(true)]
        public void ConflictingIndividualCommandsCannotChooseCallbackOrder(bool sameState)
        {
            Control(command, 0);
            Control(sameState ? command : controller.layers[2].stateMachine.defaultState, 1);
            var before = Before(); var error = Assert.Throws<InvalidOperationException>(() => Sample());
            Assert.That(error.Message, Does.Contain("VRCAnimatorLayerControl")); Unchanged(before);
        }

        [TestCase("duration")] [TestCase("machine")] [TestCase("moving")] [TestCase("lateControl")]
        public void UnsupportedIndividualControlTimingAndCallbacksStayStrict(string kind)
        {
            if (kind == "machine") Configure(controller.layers[1].stateMachine.AddStateMachineBehaviour(Sdk("VRC.SDK3.Avatars.Components.VRCAnimatorLayerControl")), 1, TargetLayer, 0, "FX");
            else Control(command, 1, duration: kind == "duration" ? 1 : 0);
            if (kind == "moving")
            {
                AnimationUtility.SetEditorCurve((AnimationClip)selected.motion, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Body size"), AnimationCurve.Linear(0, 0, 1, 100));
                var settings = AnimationUtility.GetAnimationClipSettings((AnimationClip)selected.motion); settings.loopTime = true;
                AnimationUtility.SetAnimationClipSettings((AnimationClip)selected.motion, settings);
            }
            if (kind == "lateControl")
            {
                var later = State(controller.layers[1].stateMachine, "Future layer disable", Clip(controller, "Later")); Control(later, 0);
                var transition = command.AddTransition(later); transition.hasExitTime = true; transition.exitTime = 10; transition.duration = 0;
            }
            var before = Before(); Assert.Throws<InvalidOperationException>(() => Sample()); Unchanged(before);
        }

        [TestCase("Action")] [TestCase("Gesture")] [TestCase("Additive")]
        public void NonFxIndividualControlsRemainOutsideFacialWeightEvaluation(string playable)
        {
            Control(command, .5f, target: 0, duration: 2, playable: playable);
            var before = Before(); Assert.That(Sample().Single(value => value.Shape == "Body size").Weight, Is.EqualTo(80).Within(.01)); Unchanged(before);
        }

        [TestCase(0f)] [TestCase(.5f)]
        public void BaseLayerControlUsesTheNativeUnitWeightContract(float goal)
        {
            var baseMachine = controller.layers[0].stateMachine;
            var baseRest = baseMachine.defaultState;
            var baseSelected = State(baseMachine, "Selected base", Clip(controller, "Base selected", new[] { "Body" }, value: 80)); To(baseRest, baseSelected, 1);
            foreach (var state in controller.layers[TargetLayer].stateMachine.states) state.state.motion = Clip(controller, "Unused target");
            Control(command, goal, target: 0); var before = Before();
            Assert.That(Sample().Single(value => value.Shape == "Body size").Weight, Is.EqualTo(80).Within(.01)); Unchanged(before);
        }

        [Test]
        public async Task SelectedLayerControlMenuSurvivesFullVrmRoundTrip()
        {
            var shader = Shader.Find("lilToon"); if (shader == null) Assert.Ignore("Install lilToon for full export.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            Vrm10Instance imported = null; GameObject expected = null;
            try
            {
                foreach (var renderer in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) renderer.sharedMaterial.shader = shader;
                var fx = Graph("Round trip", new[] { "Front", "Back" }, "Hair detail", 0, out var control, out _, out _);
                Control(control, .5f);
                var target = fixture.Source.AddComponent(Sdk("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor")); SetFx(target, fx);
                var menu = ScriptableObject.CreateInstance(Sdk("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu"));
                var parameters = ScriptableObject.CreateInstance(Sdk("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters"));
                AssetDatabase.CreateAsset(menu, folder + "/Menu.asset"); AssetDatabase.CreateAsset(parameters, folder + "/Parameters.asset");
                using (var data = new SerializedObject(menu))
                {
                    var controls = data.FindProperty("controls"); controls.arraySize = 1;
                    var item = controls.GetArrayElementAtIndex(0); item.FindPropertyRelative("name").stringValue = "Controlled face";
                    var type = item.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "Toggle");
                    item.FindPropertyRelative("parameter").FindPropertyRelative("name").stringValue = "Menu";
                    item.FindPropertyRelative("value").floatValue = 1; data.ApplyModifiedPropertiesWithoutUndo();
                }
                using (var data = new SerializedObject(parameters))
                {
                    var list = data.FindProperty("parameters"); list.arraySize = 1; var item = list.GetArrayElementAtIndex(0);
                    item.FindPropertyRelative("name").stringValue = "Menu"; var type = item.FindPropertyRelative("valueType");
                    type.enumValueIndex = Array.IndexOf(type.enumNames, "Int"); item.FindPropertyRelative("defaultValue").floatValue = 0;
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
                using (var data = new SerializedObject(target))
                {
                    data.FindProperty("customExpressions").boolValue = true; data.FindProperty("expressionsMenu").objectReferenceValue = menu;
                    data.FindProperty("expressionParameters").objectReferenceValue = parameters; data.ApplyModifiedPropertiesWithoutUndo();
                }
                var before = AssetDatabase.LoadAllAssetsAtPath(folder + "/Round trip.controller")
                    .Concat(new Object[] { fixture.Mesh, menu, parameters }).ToDictionary(value => value, value => EditorJsonUtility.ToJson(value));
                expected = Object.Instantiate(fixture.Source); foreach (var behaviour in expected.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                var warnings = new List<string>();
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Selected SDK layer control", "Tests", warnings,
                    blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                Assert.That(VrmMenuExpressions.CountRegistered(bytes), Is.EqualTo(1), string.Join("\n", warnings));
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                const string name = "VRChat / Controlled face";
                Assert.That(imported.Vrm.Expression.CustomClips.Any(clip => clip.name == name), Is.True);
                foreach (var input in new[] { 0f, 1f, 0f })
                {
                    imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(name), input); imported.Runtime.Process();
                    foreach (var path in new[] { "Front", "Back" })
                    {
                        var reference = expected.transform.Find(path).GetComponent<SkinnedMeshRenderer>(); reference.SetBlendShapeWeight(0, input == 0 ? 35 : 55);
                        var actual = imported.GetComponentsInChildren<SkinnedMeshRenderer>().Single(renderer => renderer.name == path);
                        var a = Vertices(reference); var b = Vertices(actual); Assert.That(b.Length, Is.EqualTo(a.Length));
                        for (var index = 0; index < a.Length; index++) Assert.That(Vector3.Distance(a[index], b[index]), Is.LessThan(.0005f), path + " / " + input + "\n" + string.Join("\n", warnings));
                    }
                }
                foreach (var pair in before) Assert.That(EditorJsonUtility.ToJson(pair.Key), Is.EqualTo(pair.Value), pair.Key.name);
                Assert.That(fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>().All(renderer => renderer.sharedMesh == fixture.Mesh && renderer.GetBlendShapeWeight(0) == 35), Is.True);
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); if (expected != null) Object.DestroyImmediate(expected); }
        }

        private static Vector3[] Vertices(SkinnedMeshRenderer renderer)
        {
            var value = new Mesh(); try { renderer.BakeMesh(value, false); return value.vertices.Select(renderer.transform.TransformPoint).ToArray(); }
            finally { Object.DestroyImmediate(value); }
        }
    }
}
