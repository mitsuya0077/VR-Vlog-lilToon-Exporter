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

        internal const string GestureWeightProxy = "FaceEmo_Hai_GestureLWProxy";

        internal static AnimationCurve GeneratedProxyCurve(string metadata, bool linear = false)
        {
            var first = new Keyframe(0, linear ? 0 : .5f, 0, linear ? 1 : 0)
                { weightedMode = WeightedMode.None, inWeight = 0, outWeight = 0 };
            var last = new Keyframe(1, linear ? 1 : .5f, linear ? 1 : 0, 0)
                { weightedMode = WeightedMode.None, inWeight = 0, outWeight = 0 };
            switch (metadata)
            {
                case "firstIn": first.inTangent = float.NaN; break;
                case "lastOut": last.outTangent = float.NaN; break;
                case "singleKey":
                    first.inTangent = first.outTangent = float.NaN;
                    return new AnimationCurve(first);
                case "disabledWeights":
                    first.inWeight = last.inWeight = float.NaN;
                    first.outWeight = last.outWeight = float.PositiveInfinity;
                    break;
                case "endpointWeights":
                    first.weightedMode = WeightedMode.In; first.inWeight = float.NaN;
                    last.weightedMode = WeightedMode.Out; last.outWeight = float.PositiveInfinity;
                    break;
                case "firstOut": first.outTangent = float.NaN; break;
                case "lastIn": last.inTangent = float.NaN; break;
                case "firstOutWeight": first.weightedMode = WeightedMode.Out; first.outWeight = float.NaN; break;
                case "lastInWeight": last.weightedMode = WeightedMode.In; last.inWeight = float.NaN; break;
                case "stepPairedIn": first.outTangent = float.PositiveInfinity; last.inTangent = float.NaN; break;
                case "stepOutWeight":
                    first.outTangent = float.PositiveInfinity; first.weightedMode = WeightedMode.Out; first.outWeight = float.NaN;
                    break;
                case "stepInWeight":
                    first.outTangent = float.PositiveInfinity; last.weightedMode = WeightedMode.In; last.inWeight = float.NaN;
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(metadata));
            }
            return new AnimationCurve(first, last);
        }

        internal static void AssertNativeProxyMetadata(AnimationClip clip, string metadata, bool linear = false, bool evaluate = true)
        {
            var curve = AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), GestureWeightProxy));
            Assert.That(curve, Is.Not.Null);
            var keys = curve.keys;
            switch (metadata)
            {
                case "firstIn": Assert.That(float.IsNaN(keys[0].inTangent), Is.True); break;
                case "lastOut": Assert.That(float.IsNaN(keys[keys.Length - 1].outTangent), Is.True); break;
                case "singleKey":
                    Assert.That(keys, Has.Length.EqualTo(1));
                    Assert.That(float.IsNaN(keys[0].inTangent) && float.IsNaN(keys[0].outTangent), Is.True);
                    break;
                case "disabledWeights":
                    Assert.That(keys.All(key => key.weightedMode == WeightedMode.None && float.IsNaN(key.inWeight) &&
                        float.IsPositiveInfinity(key.outWeight)), Is.True);
                    break;
                case "endpointWeights":
                    Assert.That(keys[0].weightedMode, Is.EqualTo(WeightedMode.In));
                    Assert.That(float.IsNaN(keys[0].inWeight), Is.True);
                    Assert.That(keys[1].weightedMode, Is.EqualTo(WeightedMode.Out));
                    Assert.That(float.IsPositiveInfinity(keys[1].outWeight), Is.True);
                    break;
                case "firstOut": Assert.That(float.IsNaN(keys[0].outTangent), Is.True); break;
                case "lastIn": Assert.That(float.IsNaN(keys[1].inTangent), Is.True); break;
                case "firstOutWeight":
                    Assert.That(keys[0].weightedMode, Is.EqualTo(WeightedMode.Out));
                    Assert.That(float.IsNaN(keys[0].outWeight), Is.True); break;
                case "lastInWeight":
                    Assert.That(keys[1].weightedMode, Is.EqualTo(WeightedMode.In));
                    Assert.That(float.IsNaN(keys[1].inWeight), Is.True); break;
                case "stepPairedIn":
                    Assert.That(float.IsPositiveInfinity(keys[0].outTangent) && float.IsNaN(keys[1].inTangent), Is.True);
                    break;
                case "stepOutWeight":
                    Assert.That(float.IsPositiveInfinity(keys[0].outTangent), Is.True);
                    Assert.That(keys[0].weightedMode, Is.EqualTo(WeightedMode.Out));
                    Assert.That(float.IsNaN(keys[0].outWeight), Is.True); break;
                case "stepInWeight":
                    Assert.That(float.IsPositiveInfinity(keys[0].outTangent), Is.True);
                    Assert.That(keys[1].weightedMode, Is.EqualTo(WeightedMode.In));
                    Assert.That(float.IsNaN(keys[1].inWeight), Is.True); break;
            }
            if (!evaluate) return;
            foreach (var time in new[] { 0f, .5f, 1f })
            {
                var value = curve.Evaluate(time);
                Assert.That(float.IsNaN(value) || float.IsInfinity(value), Is.False,
                    "Unity must evaluate the retained native metadata before exporter compatibility is tested.");
                var expected = linear ? (metadata.StartsWith("step", StringComparison.Ordinal) && time < 1 ? 0 : time) : .5f;
                Assert.That(value, Is.EqualTo(expected).Within(.00001));
            }
        }

        private AnimationClip ProxyRelay(AnimationCurve curve)
        {
            controller.AddParameter(GestureWeightProxy, AnimatorControllerParameterType.Float);
            var tree = new BlendTree { name = "Face controlled by generated proxy", blendType = BlendTreeType.Simple1D,
                blendParameter = GestureWeightProxy, useAutomaticThresholds = false };
            tree.AddChild(Clip("Open", AnimationCurve.Constant(0, 1, 0)), 0);
            tree.AddChild(Clip("Open", AnimationCurve.Constant(0, 1, 100)), 1);
            AssetDatabase.AddObjectToAsset(tree, controller);
            var machine = controller.layers[0].stateMachine;
            var state = State(machine); state.motion = tree; machine.defaultState = state;
            var clip = new AnimationClip { name = "Generated gesture weight proxy" };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), GestureWeightProxy), curve);
            AssetDatabase.AddObjectToAsset(clip, controller);
            machine = Layer("Generated proxy writer"); machine.defaultState = State(machine, clip);
            return clip;
        }

        private float NativeMorphBaseline(string parameterName = null)
        {
            var nativeAvatar = new GameObject("Independent native proxy baseline", typeof(Animator));
            var nativeMesh = Object.Instantiate(mesh);
            var graph = default(PlayableGraph);
            try
            {
                var body = new GameObject("Body", typeof(SkinnedMeshRenderer));
                body.transform.SetParent(nativeAvatar.transform, false);
                var nativeSkin = body.GetComponent<SkinnedMeshRenderer>(); nativeSkin.sharedMesh = nativeMesh;
                for (var index = 0; index < mesh.blendShapeCount; index++)
                    nativeSkin.SetBlendShapeWeight(index, skin.GetBlendShapeWeight(index));
                var animator = nativeAvatar.GetComponent<Animator>();
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.applyRootMotion = false; animator.fireEvents = false;
                graph = PlayableGraph.Create("Independent native generated proxy baseline");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                // Evaluate the original controller directly. This baseline must
                // not depend on the exporter's controller copy or curve reader.
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                AnimationPlayableOutput.Create(graph, "Native proxy", animator).SetSourcePlayable(playable);
                graph.Play(); graph.Evaluate(0);
                var weights = new List<float>(); var parameters = new List<float>();
                for (var frame = 1; frame <= 144; frame++)
                {
                    graph.Evaluate(1f / 60);
                    var weight = nativeSkin.GetBlendShapeWeight(0);
                    Assert.That(float.IsNaN(weight) || float.IsInfinity(weight), Is.False,
                        "The source's native morph must remain finite, including during initialization.");
                    var parameter = parameterName == null ? 0 : playable.GetFloat(parameterName);
                    if (parameterName != null)
                        Assert.That(float.IsNaN(parameter) || float.IsInfinity(parameter), Is.False,
                            "A BlendTree coercing a nonfinite proxy to zero is not a valid native baseline.");
                    if (frame != 120 && frame != 127 && frame != 135 && frame != 144) continue;
                    weights.Add(weight); parameters.Add(parameter);
                }
                Assert.That(weights, Has.Count.EqualTo(4));
                Assert.That(weights.All(value => Math.Abs(value - weights[0]) <= .01f), Is.True,
                    "The source's native morph must be stationary at the exporter's sample checkpoints.");
                Assert.That(parameters.All(value => Math.Abs(value - parameters[0]) <= .00001f), Is.True,
                    "The source's native parameter must be stationary at the same checkpoints.");
                return weights[0];
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                Object.DestroyImmediate(nativeAvatar); Object.DestroyImmediate(nativeMesh);
            }
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

        private string AssertPreparedPoseFallback()
        {
            skin.SetBlendShapeWeight(0, 42);
            skin.SetBlendShapeWeight(1, 18);
            var sourceJson = EditorJsonUtility.ToJson(controller);
            var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values, Is.Empty, "An uncertain group cannot invent a neutral endpoint.");
            Assert.That(warnings, Is.Not.Empty, "Retaining the prepared pose must be reported.");
            NeutralShapeSnapshot.Apply(avatar, values);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(42));
            Assert.That(skin.GetBlendShapeWeight(1), Is.EqualTo(18));
            Assert.That(skin.GetBlendShapeWeight(3), Is.EqualTo(35));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(sourceJson));
            return string.Join("\n", warnings);
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
        public void DelayedGenericMorphAnimationKeepsPreparedPoseAndWarns()
        {
            var state = Open();
            AnimationUtility.SetEditorCurve((AnimationClip)state.motion,
                EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Open"),
                AnimationCurve.Linear(0, 0, 10, 100));
            Assert.That(AssertPreparedPoseFallback(), Does.Contain("時間で変わる").And.Contain("Body / Open"));
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
        public void AutomaticAnimationSharingTheOpeningChannelKeepsPreparedPose()
        {
            Open(); var blink = Layer("Blink also changes opening");
            var clip = Clip("Blink", AnimationCurve.Linear(0, 0, 10, 100));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Open"),
                AnimationCurve.Linear(0, 100, 10, 0));
            blink.defaultState = State(blink, clip);
            AssertPreparedPoseFallback();
        }

        [Test]
        public void ExternalInputCannotChooseTheNeutralOpeningPose()
        {
            var idle = Open(0); controller.AddParameter("Voice", AnimatorControllerParameterType.Float);
            var selected = State(controller.layers[0].stateMachine, Clip("Open", AnimationCurve.Constant(0, 1, 100)));
            var transition = idle.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Greater, .5f, "Voice");
            var message = AssertPreparedPoseFallback();
            Assert.That(message, Does.Contain("外部入力").And.Contain("Body / Open"));
        }

        [Test]
        public void ContactAndVrInputsRetainGenericMorphWithoutLosingIndependentStationaryPose()
        {
            Open();
            var contactType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly =>
                assembly.GetType("VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver")).FirstOrDefault(type => type != null);
            if (contactType == null) Assert.Ignore("Install the real VRChat Contacts SDK.");
            var contact = avatar.AddComponent(contactType);
            using (var data = new SerializedObject(contact))
            {
                data.FindProperty("parameter").stringValue = "Pet";
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            controller.AddParameter("Pet", AnimatorControllerParameterType.Float);
            controller.AddParameter("Upright", AnimatorControllerParameterType.Float);
            controller.AddParameter("VRMode", AnimatorControllerParameterType.Int);
            controller.AddParameter("Viseme", AnimatorControllerParameterType.Int);
            controller.AddParameter("Voice", AnimatorControllerParameterType.Float);
            var machine = Layer("External facial inputs");
            var idle = State(machine, Clip("Untouched", AnimationCurve.Constant(0, 1, 0))); machine.defaultState = idle;
            var selected = State(machine, Clip("Untouched", AnimationCurve.Constant(0, 1, 100)));
            var transition = idle.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
            foreach (var parameter in new[] { "Pet", "Upright", "Voice" })
                transition.AddCondition(AnimatorConditionMode.Greater, .5f, parameter);
            foreach (var parameter in new[] { "VRMode", "Viseme" })
                transition.AddCondition(AnimatorConditionMode.Equals, 1, parameter);
            var sourceJson = EditorJsonUtility.ToJson(controller);
            var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values.Select(value => value.Shape), Is.EquivalentTo(new[] { "Open" }));
            Assert.That(values.Single().Weight, Is.EqualTo(100).Within(.01));
            var message = string.Join("\n", warnings);
            Assert.That(message, Does.Contain("Body / Untouched").And.Contain("外部入力"));
            foreach (var parameter in new[] { "Pet", "Upright", "VRMode", "Viseme", "Voice" })
                Assert.That(message, Does.Contain(parameter));
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero, "Sampling must not alter the prepared object.");
            NeutralShapeSnapshot.Apply(avatar, values);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(100).Within(.01));
            Assert.That(skin.GetBlendShapeWeight(3), Is.EqualTo(35), "The authored generic channel survives external inputs.");
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(sourceJson));
        }

        [Test]
        public void UnsupportedSyncedNeutralLayerKeepsPreparedPoseAndWarns()
        {
            Open(); Layer("Synced neutral");
            var layers = controller.layers;
            layers[1].syncedLayerIndex = 0; controller.layers = layers;
            Assert.That(AssertPreparedPoseFallback(), Does.Contain("同期").And.Contain("Body / Open"));
        }

        [Test]
        public void OtherPlayableMorphWriterRetainsPreparedPoseRatherThanBlockingExport()
        {
            Open();
            var other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Gesture.controller");
            var state = other.layers[0].stateMachine.AddState("Other playable face");
            state.motion = Clip("Open", AnimationCurve.Constant(0, 1, 0)); state.writeDefaultValues = false;
            other.layers[0].stateMachine.defaultState = state;
            using (var data = new SerializedObject(descriptor))
            {
                var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = 2;
                var layer = layers.GetArrayElementAtIndex(1);
                var type = layer.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "Gesture");
                layer.FindPropertyRelative("isDefault").boolValue = false;
                layer.FindPropertyRelative("animatorController").objectReferenceValue = other;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            Assert.That(AssertPreparedPoseFallback(), Does.Contain("FX以外").And.Contain("Body / Open"));
        }

        [Test]
        public void UnsupportedStateBehaviourRetainsPreparedPoseAndReportsItsType()
        {
            var state = Open();
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly =>
                assembly.GetType("VRC.SDK3.Avatars.Components.VRCAnimatorLayerControl")).FirstOrDefault(candidate => candidate != null);
            Assert.That(type, Is.Not.Null);
            state.AddStateMachineBehaviour(type);
            Assert.That(AssertPreparedPoseFallback(), Does.Contain("VRCAnimatorLayerControl"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void HiddenMorphRendererKeepsPreparedPoseAndWarns(bool inactive)
        {
            Open();
            if (inactive) skin.gameObject.SetActive(false);
            else skin.enabled = false;
            Assert.That(AssertPreparedPoseFallback(), Does.Contain("非表示").And.Contain("Body / Open"));
            Assert.That(skin.enabled, Is.EqualTo(inactive));
            Assert.That(skin.gameObject.activeSelf, Is.EqualTo(!inactive));
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void MalformedDriverNumberRemainsFatalRatherThanBecomingPoseFallback(float value)
        {
            controller.AddParameter("Face", AnimatorControllerParameterType.Float);
            var initial = Open();
            ParameterDriverExpressionTests.Driver(initial, ParameterDriverExpressionTests.Op("Set", "Face", value));
            var selected = State(controller.layers[0].stateMachine, Clip("Open", AnimationCurve.Constant(0, 1, 0)));
            var transition = initial.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Greater, .5f, "Face");
            var warnings = new List<string>();
            var error = Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar, warnings: warnings));
            Assert.That(error.Message, Does.Contain("数値が不正"));
            Assert.That(warnings, Is.Empty);
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void MalformedCurveNumberCannotBeHiddenByExternalInputFallback(float value)
        {
            var state = Open();
            var clip = (AnimationClip)state.motion;
            var binding = EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Open");
            AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, 1, value));
            Assert.That(AnimationUtility.GetCurveBindings(clip).Contains(binding), Is.True,
                "Unity must retain the declared float binding so this test exercises malformed source data.");
            var native = AnimationUtility.GetEditorCurve(clip, binding);
            Assert.That(native == null || native.length == 0 || native.keys.Any(key =>
                float.IsNaN(key.value) || float.IsInfinity(key.value)), Is.True,
                "The declared native float binding must have a missing/empty curve or retain a nonfinite key value.");
            controller.AddParameter("Voice", AnimatorControllerParameterType.Float);
            var selected = State(controller.layers[0].stateMachine, Clip("Open", AnimationCurve.Constant(0, 1, 0)));
            var transition = state.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Greater, .5f, "Voice");
            var warnings = new List<string>();
            Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar, warnings: warnings));
            Assert.That(warnings, Is.Empty, "Invalid numbers must be validated before classifying external inputs.");
        }

        [TestCase("firstIn")]
        [TestCase("lastOut")]
        [TestCase("singleKey")]
        [TestCase("disabledWeights")]
        [TestCase("endpointWeights")]
        public void NativeUnusedProxyMetadataStillDrivesTheStationaryMorph(string metadata)
        {
            var proxy = ProxyRelay(GeneratedProxyCurve(metadata));
            AssertNativeProxyMetadata(proxy, metadata);
            var controllerJson = EditorJsonUtility.ToJson(controller);
            var expectedWeight = 50f;
            if (metadata == "endpointWeights")
            {
                // AnimationCurve and Animator can handle weighted endpoint
                // metadata differently. Preserve the actual source appearance.
                expectedWeight = NativeMorphBaseline(GestureWeightProxy);
                var native = AnimationUtility.GetEditorCurve(proxy, EditorCurveBinding.FloatCurve("", typeof(Animator), GestureWeightProxy));
                var exported = VrChatGestureExpressions.ReadCurve(native);
                foreach (var time in new[] { 0f, .5f, 1f })
                    Assert.That(exported.Evaluate(time), Is.EqualTo(native.Evaluate(time)).Within(.00001));
            }
            var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values.Select(value => value.Shape), Is.EquivalentTo(new[] { "Open" }));
            Assert.That(values.Single().Weight, Is.EqualTo(expectedWeight).Within(.01),
                "The native Animator curve must enter the dependency closure and preserve the source's actual BlendTree pose.");
            Assert.That(warnings, Is.Empty);
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
            Assert.That(skin.GetBlendShapeWeight(3), Is.EqualTo(35));
            AssertNativeProxyMetadata(proxy, metadata);
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(controllerJson));
        }

        [TestCase("longTime")]
        [TestCase("manyKeys")]
        [TestCase("largeValue")]
        [TestCase("activeWeight")]
        public void NativeStationaryMorphDoesNotInheritPortableAnimationFormatCaps(string limit)
        {
            var weight = limit == "largeValue" ? 10001f : 50f;
            var curve = AnimationCurve.Constant(0, limit == "longTime" ? 3601 : 1, weight);
            if (limit == "manyKeys")
                curve = new AnimationCurve(Enumerable.Range(0, 16385)
                    .Select(index => new Keyframe(index / 16384f, weight, 0, 0)).ToArray());
            if (limit == "activeWeight")
            {
                var keys = curve.keys;
                keys[0].weightedMode = WeightedMode.Out; keys[0].outWeight = 1.5f;
                keys[1].weightedMode = WeightedMode.In; keys[1].inWeight = 1.5f;
                curve.keys = keys;
            }
            var state = Open(); var clip = (AnimationClip)state.motion;
            var binding = EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Open");
            AnimationUtility.SetEditorCurve(clip, binding, curve);
            var native = AnimationUtility.GetEditorCurve(clip, binding);
            if (limit == "longTime") Assert.That(native.keys.Last().time, Is.GreaterThan(3600));
            if (limit == "manyKeys") Assert.That(native.length, Is.GreaterThan(16384));
            if (limit == "largeValue") Assert.That(native.keys[0].value, Is.GreaterThan(10000));
            if (limit == "activeWeight")
            {
                Assert.That(native.keys[0].weightedMode, Is.EqualTo(WeightedMode.Out));
                Assert.That(native.keys[1].weightedMode, Is.EqualTo(WeightedMode.In));
                Assert.That(native.keys[0].outWeight, Is.GreaterThan(1));
                Assert.That(native.keys[1].inWeight, Is.GreaterThan(1));
            }
            foreach (var time in new[] { 0f, .5f, 1f })
                Assert.That(native.Evaluate(time), Is.EqualTo(weight).Within(.01),
                    "The native curve must be valid before testing the portable-format restriction.");
            Assert.That(NativeMorphBaseline(), Is.EqualTo(weight).Within(.01),
                "The original controller must actually produce a finite stationary morph outside the portable animation limits.");
            Assert.Throws<InvalidOperationException>(() => VrChatGestureExpressions.ReadCurve(native),
                "The portable animation format must retain its separate size and range limits.");
            var curveJson = EditorJsonUtility.ToJson(clip);
            var controllerJson = EditorJsonUtility.ToJson(controller);
            var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values.Select(value => value.Shape), Is.EquivalentTo(new[] { "Open" }));
            Assert.That(values.Single().Weight, Is.EqualTo(weight).Within(.01));
            Assert.That(warnings, Is.Empty);
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
            Assert.That(skin.GetBlendShapeWeight(3), Is.EqualTo(35));
            Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(curveJson));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(controllerJson));
        }

        [TestCase("stepPairedIn")]
        [TestCase("stepOutWeight")]
        [TestCase("stepInWeight")]
        public void NativeStepProxyIgnoresUnusedPairedHandleAndWeights(string metadata)
        {
            var proxy = ProxyRelay(GeneratedProxyCurve(metadata));
            AssertNativeProxyMetadata(proxy, metadata);
            var stepped = new AnimationClip { name = "Generated stepped proxy" };
            AnimationUtility.SetEditorCurve(stepped, EditorCurveBinding.FloatCurve("", typeof(Animator), GestureWeightProxy),
                GeneratedProxyCurve(metadata, linear: true));
            AssetDatabase.AddObjectToAsset(stepped, controller);
            AssertNativeProxyMetadata(stepped, metadata, linear: true);
            var native = AnimationUtility.GetEditorCurve(stepped, EditorCurveBinding.FloatCurve("", typeof(Animator), GestureWeightProxy));
            var exported = VrChatGestureExpressions.ReadCurve(native);
            foreach (var time in new[] { 0f, .25f, .5f, 1f })
                Assert.That(exported.Evaluate(time), Is.EqualTo(native.Evaluate(time)).Within(.00001),
                    "The exported curve must preserve Unity's step-segment interpretation.");
            var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values.Select(value => value.Shape), Is.EquivalentTo(new[] { "Open" }));
            Assert.That(values.Single().Weight, Is.EqualTo(50).Within(.01));
            Assert.That(warnings, Is.Empty);
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
            Assert.That(skin.GetBlendShapeWeight(3), Is.EqualTo(35));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NativeGeneratedLinearProxyAllowsRuntimeFallbackAndIndependentStationaryPose(bool externalInput)
        {
            var proxy = ProxyRelay(GeneratedProxyCurve("firstIn", linear: true));
            AssertNativeProxyMetadata(proxy, "firstIn", linear: true);
            if (externalInput)
            {
                controller.AddParameter("Voice", AnimatorControllerParameterType.Float);
                var machine = controller.layers[0].stateMachine;
                var selected = State(machine, Clip("Open", AnimationCurve.Constant(0, 1, 100)));
                var transition = machine.defaultState.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
                transition.AddCondition(AnimatorConditionMode.Greater, .5f, "Voice");
            }
            var independent = Layer("Independent held face");
            independent.defaultState = State(independent, Clip("Untouched", AnimationCurve.Constant(0, 1, 80)));
            skin.SetBlendShapeWeight(0, 42);
            var controllerJson = EditorJsonUtility.ToJson(controller);
            var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values.Select(value => value.Shape), Is.EquivalentTo(new[] { "Untouched" }));
            Assert.That(values.Single().Weight, Is.EqualTo(80).Within(.01));
            Assert.That(string.Join("\n", warnings), Does.Contain("Body / Open").And.Contain(externalInput ? "Voice" : "時間で変わる"));
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(42));
            NeutralShapeSnapshot.Apply(avatar, values);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(42));
            Assert.That(skin.GetBlendShapeWeight(3), Is.EqualTo(80).Within(.01));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(controllerJson));
        }

        [TestCase("firstOut")]
        [TestCase("lastIn")]
        [TestCase("firstOutWeight")]
        [TestCase("lastInWeight")]
        public void MalformedUsedProxyMetadataRemainsFatalBeforeRuntimeFallback(string metadata)
        {
            var proxy = ProxyRelay(GeneratedProxyCurve(metadata));
            AssertNativeProxyMetadata(proxy, metadata, evaluate: false);
            controller.AddParameter("Voice", AnimatorControllerParameterType.Float);
            var machine = controller.layers[0].stateMachine;
            var selected = State(machine, Clip("Open", AnimationCurve.Constant(0, 1, 100)));
            var transition = machine.defaultState.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Greater, .5f, "Voice");
            var warnings = new List<string>();
            Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar, warnings: warnings));
            Assert.That(warnings, Is.Empty, "Malformed active segment data must be rejected before external-input fallback.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NativeStepTangentsKeepStationaryPoseOrReportTemporalFallback(bool changing)
        {
            var state = Open();
            var binding = EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Open");
            var curve = new AnimationCurve(new Keyframe(0, changing ? 0 : 100), new Keyframe(1, 100));
            AnimationUtility.SetKeyRightTangentMode(curve, 0, AnimationUtility.TangentMode.Constant);
            AnimationUtility.SetKeyLeftTangentMode(curve, 1, AnimationUtility.TangentMode.Constant);
            AnimationUtility.SetEditorCurve((AnimationClip)state.motion, binding, curve);
            var native = AnimationUtility.GetEditorCurve((AnimationClip)state.motion, binding);
            Assert.That(float.IsInfinity(native.keys[0].outTangent) && float.IsInfinity(native.keys[1].inTangent), Is.True);
            Assert.That(native.Evaluate(.5f), Is.EqualTo(changing ? 0 : 100));
            if (changing) Assert.That(AssertPreparedPoseFallback(), Does.Contain("時間で変わる"));
            else Assert.That(NeutralShapeSampler.Sample(avatar).Single(value => value.Shape == "Open").Weight, Is.EqualTo(100).Within(.01));
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
        public void GeneratedFaceEmoStyleNestedFxPrunesInactiveVoiceGatesAndPreservesUnprovedPose(bool waitByVoice)
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
                Assert.That(AssertPreparedPoseFallback(), Does.Contain("Voice"));
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
            Assert.That(AssertPreparedPoseFallback(), Does.Contain("Voice"));
        }

        [Test]
        public void AutomaticBlinkWithWriteDefaultsRetainsTheEntireCoupledPreparedPose()
        {
            Open(); var blink = Layer("Blink with implicit writes");
            blink.defaultState = State(blink, Clip("Blink", AnimationCurve.Linear(0, 0, 10, 100)), writeDefaults: true);
            AssertPreparedPoseFallback();
        }

        [Test]
        public void RelevantRandomDriverCannotInventANeutralFace()
        {
            controller.AddParameter("Face", AnimatorControllerParameterType.Int);
            var open = Open(); ParameterDriverExpressionTests.Driver(open, ParameterDriverExpressionTests.Op("Random", "Face"));
            var other = State(controller.layers[0].stateMachine, Clip("Open", AnimationCurve.Constant(0, 1, 0)));
            var transition = open.AddTransition(other); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "Face");
            Assert.That(AssertPreparedPoseFallback(), Does.Contain("Random"));
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
