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
    public sealed class NeutralCurveConditionTests
    {
        string folder;
        GameObject avatar;
        Mesh mesh;
        AnimatorController controller;

        [SetUp]
        public void SetUp()
        {
            var descriptorType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly =>
                assembly.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor")).FirstOrDefault(type => type != null);
            if (descriptorType == null) Assert.Ignore("Install the real VRChat SDK.");
            var name = "__NeutralCurveConditions_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            avatar = new GameObject("Avatar", typeof(Animator));
            var body = new GameObject("Body", typeof(SkinnedMeshRenderer)); body.transform.SetParent(avatar.transform, false);
            mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            mesh.AddBlendShapeFrame("Opening", 100, Enumerable.Repeat(Vector3.up, 3).ToArray(), null, null);
            body.GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
            var open = Clip("Opening", "Body", typeof(SkinnedMeshRenderer), "blendShape.Opening", AnimationCurve.Constant(0, 1, 100));
            State(controller.layers[0].stateMachine, "Neutral face", open, false);
            var descriptor = avatar.AddComponent(descriptorType);
            using var data = new SerializedObject(descriptor);
            data.FindProperty("customizeAnimationLayers").boolValue = true;
            var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = 1;
            var layer = layers.GetArrayElementAtIndex(0);
            var type = layer.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
            layer.FindPropertyRelative("isDefault").boolValue = false;
            layer.FindPropertyRelative("animatorController").objectReferenceValue = controller;
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(avatar); Object.DestroyImmediate(mesh);
            if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        AnimationClip Clip(string name, string path, Type type, string property, AnimationCurve curve)
        {
            var clip = new AnimationClip { name = name };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, type, property), curve);
            AssetDatabase.AddObjectToAsset(clip, controller); return clip;
        }

        AnimatorStateMachine Layer(string name)
        {
            controller.AddLayer(name); var layers = controller.layers;
            layers[layers.Length - 1].defaultWeight = 1; controller.layers = layers;
            return layers[layers.Length - 1].stateMachine;
        }

        static AnimatorState State(AnimatorStateMachine machine, string name, AnimationClip clip, bool writeDefaults)
        {
            var state = machine.AddState(name); state.motion = clip; state.writeDefaultValues = writeDefaults;
            if (machine.states.Length == 1) machine.defaultState = state;
            return state;
        }

        AnimationClip Relay(AnimationCurve curve, string parameter = "Relay value", float threshold = .5f)
        {
            controller.AddParameter(parameter, AnimatorControllerParameterType.Float);
            var clip = Clip(parameter, "", typeof(Animator), parameter, curve);
            var machine = Layer("Parameter lifecycle");
            var first = State(machine, "Startup", clip, true);
            var second = State(machine, "Different state", clip, true);
            var transition = first.AddTransition(second);
            transition.hasExitTime = true; transition.exitTime = .75f; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Less, threshold, parameter);
            return clip;
        }

        void AssertPreparedPoseFallback()
        {
            var skin = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
            skin.SetBlendShapeWeight(0, 28);
            var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values, Is.Empty, "An unproved relay must not freeze a possibly changing face.");
            Assert.That(warnings, Is.Not.Empty);
            NeutralShapeSnapshot.Apply(avatar, values);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(28));
        }

        float NativeRelayValue(string parameter)
        {
            var nativeAvatar = new GameObject("Independent native relay baseline", typeof(Animator));
            var nativeMesh = Object.Instantiate(mesh);
            var graph = default(PlayableGraph);
            try
            {
                var body = new GameObject("Body", typeof(SkinnedMeshRenderer));
                body.transform.SetParent(nativeAvatar.transform, false);
                body.GetComponent<SkinnedMeshRenderer>().sharedMesh = nativeMesh;
                var animator = nativeAvatar.GetComponent<Animator>();
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.applyRootMotion = false; animator.fireEvents = false;
                graph = PlayableGraph.Create("Independent native relay baseline");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                // Use the original controller, without exporter inspection or
                // controller copying, to establish the actual parameter value.
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                AnimationPlayableOutput.Create(graph, "Native relay", animator).SetSourcePlayable(playable);
                graph.Play(); graph.Evaluate(0);
                var values = new List<float>();
                for (var frame = 1; frame <= 144; frame++)
                {
                    graph.Evaluate(1f / 60);
                    var value = playable.GetFloat(parameter);
                    Assert.That(float.IsNaN(value) || float.IsInfinity(value), Is.False);
                    if (frame == 120 || frame == 127 || frame == 135 || frame == 144) values.Add(value);
                }
                Assert.That(values, Has.Count.EqualTo(4));
                Assert.That(values.All(value => Math.Abs(value - values[0]) <= .00001f), Is.True,
                    "The original native relay must be stationary at the neutral sampling checkpoints.");
                return values[0];
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                Object.DestroyImmediate(nativeAvatar); Object.DestroyImmediate(nativeMesh);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NativeConstantParameterRelayCanProveItsTimedExitFalseWithoutNames(bool staticWriteDefaultsLayer)
        {
            Relay(AnimationCurve.Constant(0, 1, 1));
            if (staticWriteDefaultsLayer)
            {
                var machine = Layer("Static empty layer");
                State(machine, "Held", new AnimationClip(), true);
                AssetDatabase.AddObjectToAsset(machine.defaultState.motion, controller);
            }
            var values = NeutralShapeSampler.Sample(avatar);
            Assert.That(values.Single(value => value.Shape == "Opening").Weight, Is.EqualTo(100).Within(.01));
            Assert.That(controller.parameters.Single(value => value.name == "Relay value").defaultFloat, Is.Zero,
                "The post-native proof must not overwrite the authoring startup default.");
        }

        [TestCase("firstIn")]
        [TestCase("lastOut")]
        [TestCase("singleKey")]
        [TestCase("disabledWeights")]
        [TestCase("endpointWeights")]
        [TestCase("stepPairedIn")]
        [TestCase("stepOutWeight")]
        [TestCase("stepInWeight")]
        public void NativeGeneratedRelayMetadataCanProveItsTimedExitFalse(string metadata)
        {
            var parameter = NeutralShapeSamplerTests.GestureWeightProxy;
            var clip = Relay(NeutralShapeSamplerTests.GeneratedProxyCurve(metadata), parameter, .25f);
            NeutralShapeSamplerTests.AssertNativeProxyMetadata(clip, metadata);
            var original = EditorJsonUtility.ToJson(clip);
            var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values.Single(value => value.Shape == "Opening").Weight, Is.EqualTo(100).Within(.01));
            Assert.That(warnings, Is.Empty, "A natively constant relay must not trigger the timed-pose fallback.");
            Assert.That(controller.parameters.Single(value => value.name == parameter).defaultFloat, Is.Zero,
                "The native relay value must be observed without replacing its startup default.");
            NeutralShapeSamplerTests.AssertNativeProxyMetadata(clip, metadata);
            Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(original), "The source clip metadata must remain intact.");
        }

        [Test]
        public void NativeFractionalGeneratedRelayCannotProveTimedExitFalse()
        {
            const string metadata = "firstIn";
            var parameter = NeutralShapeSamplerTests.GestureWeightProxy;
            const float threshold = .375f;
            var clip = Relay(NeutralShapeSamplerTests.GeneratedProxyCurve(metadata), parameter, threshold);
            var layers = controller.layers;
            layers[1].defaultWeight = .5f; controller.layers = layers;
            var machine = layers[1].stateMachine;
            var first = machine.defaultState;
            var second = machine.states.Select(value => value.state).Single(value => value != first);
            var back = second.AddTransition(first);
            back.hasExitTime = true; back.exitTime = .75f; back.duration = 0;
            back.AddCondition(AnimatorConditionMode.Less, threshold, parameter);
            NeutralShapeSamplerTests.AssertNativeProxyMetadata(clip, metadata);
            var curve = AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), parameter));
            var native = NativeRelayValue(parameter);
            Assert.That(Math.Abs(native - curve.Evaluate(.5f)), Is.GreaterThan(.00001f),
                "The negative fixture must establish a real native parameter mismatch before testing fallback.");
            Assert.That(native, Is.LessThan(threshold), "Both native timed gates must remain reachable.");
            var original = EditorJsonUtility.ToJson(clip);
            AssertPreparedPoseFallback();
            Assert.That(controller.parameters.Single(value => value.name == parameter).defaultFloat, Is.Zero,
                "The native mismatch must not be hidden by changing the startup default.");
            NeutralShapeSamplerTests.AssertNativeProxyMetadata(clip, metadata);
            Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(original), "The source clip metadata must remain intact.");
        }

        [TestCase("firstOut")]
        [TestCase("lastIn")]
        [TestCase("firstOutWeight")]
        [TestCase("lastInWeight")]
        public void UsedInvalidRelayMetadataCannotProveItsTimedExitFalse(string metadata)
        {
            var clip = Relay(NeutralShapeSamplerTests.GeneratedProxyCurve(metadata),
                NeutralShapeSamplerTests.GestureWeightProxy, .25f);
            NeutralShapeSamplerTests.AssertNativeProxyMetadata(clip, metadata, evaluate: false);
            var skin = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
            skin.SetBlendShapeWeight(0, 28);
            var error = Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar));
            Assert.That(error.Message, Does.Contain("曲線に不正な値"));
            Assert.That(error, Is.Not.InstanceOf<NeutralShapeSamplingException>(),
                "Invalid used metadata must remain an error rather than the optional prepared-pose fallback.");
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(28));
            NeutralShapeSamplerTests.AssertNativeProxyMetadata(clip, metadata, evaluate: false);
        }

        [Test]
        public void DynamicParameterCurveCannotHideAFutureTimedExit()
        {
            Relay(AnimationCurve.Linear(0, 1, 10, 0));
            AssertPreparedPoseFallback();
        }

        [Test]
        public void CompetingParameterDriverPreventsTheRelayProof()
        {
            Relay(AnimationCurve.Constant(0, 1, 1));
            var machine = Layer("Competing writer");
            var state = State(machine, "Set relay", null, false);
            ParameterDriverExpressionTests.Driver(state, ParameterDriverExpressionTests.Op("Set", "Relay value", 0));
            AssertPreparedPoseFallback();
        }

        [Test]
        public void FractionalRelayWeightNearExitThresholdCannotCountAsAFullConstantWriter()
        {
            Relay(AnimationCurve.Constant(0, 1, 1));
            var layers = controller.layers;
            layers[1].defaultWeight = .999995f; controller.layers = layers;
            var machine = layers[1].stateMachine;
            var first = machine.defaultState;
            var second = machine.states.Select(value => value.state).Single(value => value != first);
            var conditions = first.transitions.Single().conditions;
            conditions[0].threshold = .999997f; first.transitions.Single().conditions = conditions;
            var back = second.AddTransition(first);
            back.hasExitTime = true; back.exitTime = .75f; back.duration = 0;
            back.AddCondition(AnimatorConditionMode.Less, .999997f, "Relay value");
            AssertPreparedPoseFallback();
        }
    }
}
