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
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class NeutralLayerControlTests
    {
        private string folder;
        private GameObject avatar;
        private Mesh mesh;
        private Component descriptor;
        private SkinnedMeshRenderer skin;
        private AnimatorController controller;
        private AnimatorState bodyState, controlState;
        private const int BodyLayer = 1, ControlLayer = 2, TopLayer = 3;

        [SetUp]
        public void SetUp()
        {
            if (SdkType("VRCAvatarDescriptor") == null || SdkType("VRCAnimatorLayerControl") == null || SdkType("VRCPlayableLayerControl") == null)
                Assert.Ignore("Install the real VRChat SDK.");
            var name = "__NeutralLayerControl_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            avatar = new GameObject("Avatar", typeof(Animator));
            var body = new GameObject("Body", typeof(SkinnedMeshRenderer)); body.transform.SetParent(avatar.transform, false);
            skin = body.GetComponent<SkinnedMeshRenderer>();
            mesh = new Mesh { name = "Prepared layer-controlled appearance" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }; mesh.triangles = new[] { 0, 1, 2 };
            mesh.AddBlendShapeFrame("Body size", 100, Enumerable.Repeat(Vector3.right * .02f, 3).ToArray(), null, null);
            mesh.AddBlendShapeFrame("Open", 100, Enumerable.Repeat(Vector3.up * .015f, 3).ToArray(), null, null);
            skin.sharedMesh = mesh; skin.SetBlendShapeWeight(0, 17); skin.SetBlendShapeWeight(1, 23);
            descriptor = avatar.AddComponent(SdkType("VRCAvatarDescriptor"));
            BuildGraph(controller, new[] { "Body" }, out bodyState, out controlState);
            SetControllers(descriptor, controller);
        }

        [TearDown]
        public void TearDown()
        {
            if (avatar != null) Object.DestroyImmediate(avatar);
            if (mesh != null) Object.DestroyImmediate(mesh);
            if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        private static Type SdkType(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components." + name)).FirstOrDefault(type => type != null);

        private static AnimatorState State(AnimatorStateMachine machine, string name, Motion motion)
        {
            var state = machine.AddState(name); state.motion = motion; state.writeDefaultValues = false;
            if (machine.states.Length == 1) machine.defaultState = state;
            return state;
        }

        private static AnimatorStateMachine Layer(AnimatorController target, string name)
        {
            target.AddLayer(name); var layers = target.layers; layers[layers.Length - 1].defaultWeight = 1; target.layers = layers;
            return layers[layers.Length - 1].stateMachine;
        }

        private static AnimationClip Clip(AnimatorController target, string name, IEnumerable<string> paths = null, string shape = null, float weight = 0)
        {
            var clip = new AnimationClip { name = name };
            foreach (var path in paths ?? Enumerable.Empty<string>())
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + shape),
                    AnimationCurve.Constant(0, 1, weight));
            AssetDatabase.AddObjectToAsset(clip, target); return clip;
        }

        private static void BuildGraph(AnimatorController target, string[] paths, out AnimatorState body, out AnimatorState control)
        {
            State(target.layers[0].stateMachine, "Uncontrolled base", Clip(target, "Base"));
            body = State(Layer(target, "Controlled body"), "Body rest", Clip(target, "Body rest", paths, "Body size", 63));
            control = State(Layer(target, "Layer weight control"), "Control", Clip(target, "Control"));
            State(Layer(target, "Independent upper face"), "Always open", Clip(target, "Always open", paths, "Open", 100));
        }

        private static AnimatorStateTransition Transition(AnimatorState from, AnimatorState to, string parameter, AnimatorConditionMode mode, float threshold)
        {
            var transition = from.AddTransition(to); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(mode, threshold, parameter); return transition;
        }

        private static StateMachineBehaviour Control(AnimatorState state, string target = "FX", int layer = BodyLayer,
            float weight = 0, float duration = 0, bool global = false)
        {
            var control = state.AddStateMachineBehaviour(SdkType(global ? "VRCPlayableLayerControl" : "VRCAnimatorLayerControl"));
            using var data = new SerializedObject(control);
            var playable = data.FindProperty(global ? "layer" : "playable");
            var index = Array.IndexOf(playable.enumNames, target); Assert.That(index, Is.GreaterThanOrEqualTo(0), target);
            playable.enumValueIndex = index;
            if (!global) data.FindProperty("layer").intValue = layer;
            data.FindProperty("goalWeight").floatValue = weight; data.FindProperty("blendDuration").floatValue = duration;
            data.ApplyModifiedPropertiesWithoutUndo(); return control;
        }

        private static void SetControllers(Component target, AnimatorController fx, AnimatorController other = null)
        {
            using var data = new SerializedObject(target);
            data.FindProperty("customizeAnimationLayers").boolValue = true;
            var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = other == null ? 1 : 2;
            for (var i = 0; i < layers.arraySize; i++)
            {
                var layer = layers.GetArrayElementAtIndex(i); var type = layer.FindPropertyRelative("type");
                type.enumValueIndex = Array.IndexOf(type.enumNames, i == 0 ? "FX" : "Action");
                layer.FindPropertyRelative("isDefault").boolValue = false;
                layer.FindPropertyRelative("animatorController").objectReferenceValue = i == 0 ? fx : other;
            }
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        // This is the generated MA relay's data graph, without invoking its
        // callbacks. Renaming the states must not change exporter policy.
        private static void MmdRelay(AnimatorController target, bool maNames)
        {
            var parameter = maNames ? "__MA/Internal/MMDNotActive" : "Generic layer relay";
            target.AddParameter(new AnimatorControllerParameter { name = parameter, type = AnimatorControllerParameterType.Float, defaultFloat = 0 });
            var machine = target.layers[ControlLayer].stateMachine;
            foreach (var item in machine.states) machine.RemoveState(item.state);
            var layers = target.layers; layers[ControlLayer].name = maNames ? "Modular Avatar: MMD Control" : "Generic relay controller"; target.layers = layers;
            var clip = Clip(target, "Relay signal");
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), parameter), AnimationCurve.Constant(0, 1, 1));
            var initial = State(machine, maNames ? "Initial" : "Starting", clip);
            var normal = State(machine, maNames ? "NotMMD" : "Enabled", clip);
            var mmd = State(machine, maNames ? "MMD" : "Disabled", clip);
            machine.defaultState = initial;
            Transition(initial, mmd, parameter, AnimatorConditionMode.Less, .5f);
            Transition(mmd, normal, parameter, AnimatorConditionMode.Greater, .5f);
            Transition(normal, mmd, parameter, AnimatorConditionMode.Less, .5f);
            Control(normal, weight: 1); Control(mmd, weight: 0);
        }

        private Dictionary<Object, string> CaptureAssets() => AssetDatabase.LoadAllAssetsAtPath(folder + "/FX.controller")
            .Concat(new Object[] { mesh, descriptor }).ToDictionary(value => value, value => EditorJsonUtility.ToJson(value));

        private void AssertSourceUnchanged(Dictionary<Object, string> before)
        {
            foreach (var pair in before) Assert.That(EditorJsonUtility.ToJson(pair.Key), Is.EqualTo(pair.Value), pair.Key.name);
            Assert.That(skin.sharedMesh, Is.SameAs(mesh));
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17)); Assert.That(skin.GetBlendShapeWeight(1), Is.EqualTo(23));
        }

        private void AssertRetainedBodyAndIndependentFace(List<VrChatExpressionMenu.MorphValue> values, List<string> warnings)
        {
            Assert.That(values.Any(value => value.Shape == "Body size"), Is.False, "SDK layer weights must not be silently ignored or simulated.");
            Assert.That(values.Single(value => value.Shape == "Open").Weight, Is.EqualTo(100).Within(.01));
            Assert.That(warnings.Any(value => value.Contains("Body size") && value.Contains("VRCAnimatorLayerControl")), Is.True,
                string.Join("\n", warnings));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void MmdRelayRetainsPreparedTargetAndIndependentTopOverrideWithoutNameExceptions(bool maNames)
        {
            MmdRelay(controller, maNames); var before = CaptureAssets(); var warnings = new List<string>();
            AssertRetainedBodyAndIndependentFace(NeutralShapeSampler.Sample(avatar, warnings: warnings), warnings);
            AssertSourceUnchanged(before);
        }

        [TestCase(0f, 0f, 1f)]
        [TestCase(1f, 0f, 0f)]
        [TestCase(.5f, 0f, 1f)]
        [TestCase(1f, 2f, .5f)]
        public void IndividualLayerControlKeepsPreparedRestWithoutAssumingItsGoalIsAlreadyApplied(float goal, float duration, float defaultWeight)
        {
            var layers = controller.layers; layers[BodyLayer].defaultWeight = defaultWeight; controller.layers = layers;
            Control(controlState, weight: goal, duration: duration); var before = CaptureAssets(); var warnings = new List<string>();
            AssertRetainedBodyAndIndependentFace(NeutralShapeSampler.Sample(avatar, warnings: warnings), warnings);
            AssertSourceUnchanged(before);
        }

        [Test]
        public void FxBaseLayerIndexZeroIsValidMetadataWithoutInventingItsRuntimeEffect()
        {
            var clip = (AnimationClip)bodyState.motion;
            controller.layers[0].stateMachine.defaultState.motion = clip;
            bodyState.motion = Clip(controller, "Empty body layer");
            Control(controlState, layer: 0); var before = CaptureAssets(); var warnings = new List<string>();
            AssertRetainedBodyAndIndependentFace(NeutralShapeSampler.Sample(avatar, warnings: warnings), warnings);
            AssertSourceUnchanged(before);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AControlOfTheTopLayerOrWholeFxCannotCertifyIndependentNeutral(bool global)
        {
            Control(controlState, layer: TopLayer, global: global); var before = CaptureAssets(); var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values.Any(value => value.Shape == "Open"), Is.False);
            Assert.That(warnings.Any(value => value.Contains("Open") && value.Contains(global ? "VRCPlayableLayerControl" : "VRCAnimatorLayerControl")), Is.True,
                string.Join("\n", warnings));
            AssertSourceUnchanged(before);
        }

        [TestCase("Action", false)]
        [TestCase("Gesture", false)]
        [TestCase("Additive", false)]
        [TestCase("Action", true)]
        [TestCase("Gesture", true)]
        [TestCase("Additive", true)]
        public void NonFxIndividualLayerControlsDoNotBlockNeutralMorphs(string target, bool otherController)
        {
            AnimatorController other = null; var state = controlState;
            if (otherController)
            {
                other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Action.controller");
                state = State(other.layers[0].stateMachine, "Body playable command", Clip(other, "Other empty"));
                SetControllers(descriptor, controller, other);
            }
            var command = Control(state, target, layer: 0, weight: .5f, duration: 2);
            var commandBefore = EditorJsonUtility.ToJson(command); var before = CaptureAssets(); var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values.Single(value => value.Shape == "Body size").Weight, Is.EqualTo(63).Within(.01));
            Assert.That(values.Single(value => value.Shape == "Open").Weight, Is.EqualTo(100).Within(.01));
            Assert.That(warnings, Is.Empty); AssertSourceUnchanged(before);
            Assert.That(EditorJsonUtility.ToJson(command), Is.EqualTo(commandBefore));
        }

        [TestCase("Bool")]
        [TestCase("Int")]
        [TestCase("Float")]
        public void NormalTypedInputsCanProveAValidLayerControlDormant(string type)
        {
            var parameter = type == "Bool" ? "AFK" : type == "Int" ? "TrackingType" : "GestureLeftWeight";
            controller.AddParameter(new AnimatorControllerParameter { name = parameter,
                type = (AnimatorControllerParameterType)Enum.Parse(typeof(AnimatorControllerParameterType), type), defaultBool = true, defaultInt = 0, defaultFloat = 1 });
            var dormant = State(controller.layers[ControlLayer].stateMachine, "Dormant SDK control", Clip(controller, "Dormant"));
            Transition(controlState, dormant, parameter, type == "Bool" ? AnimatorConditionMode.If : type == "Int" ? AnimatorConditionMode.Equals : AnimatorConditionMode.Greater,
                type == "Float" ? .5f : 0);
            Control(dormant, duration: 2); var before = CaptureAssets(); var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values.Single(value => value.Shape == "Body size").Weight, Is.EqualTo(63).Within(.01));
            Assert.That(values.Single(value => value.Shape == "Open").Weight, Is.EqualTo(100).Within(.01));
            Assert.That(warnings, Is.Empty); AssertSourceUnchanged(before);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AReachableAdditionalPlayableFxCommandKeepsPreparedNeutralWithoutRunningCallbacks(bool global)
        {
            var other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Action.controller");
            var state = State(other.layers[0].stateMachine, "Additional SDK control", Clip(other, "Additional empty"));
            var command = Control(state, global: global); SetControllers(descriptor, controller, other);
            var commandBefore = EditorJsonUtility.ToJson(command); var before = CaptureAssets(); var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            if (!global) AssertRetainedBodyAndIndependentFace(values, warnings);
            else
            {
                Assert.That(values, Is.Empty);
                Assert.That(warnings.Any(value => value.Contains("Body size") && value.Contains("VRCPlayableLayerControl")), Is.True, string.Join("\n", warnings));
            }
            AssertSourceUnchanged(before); Assert.That(EditorJsonUtility.ToJson(command), Is.EqualTo(commandBefore));
        }

        [TestCase("nanWeight")]
        [TestCase("infiniteWeight")]
        [TestCase("negativeWeight")]
        [TestCase("largeWeight")]
        [TestCase("nanDuration")]
        [TestCase("infiniteDuration")]
        [TestCase("negativeDuration")]
        [TestCase("negativeIndex")]
        [TestCase("outOfRangeIndex")]
        [TestCase("invalidPlayable")]
        public void MalformedSdkLayerControlDataRemainsHardEvenOnADormantBranch(string corruption)
        {
            controller.AddParameter("AFK", AnimatorControllerParameterType.Bool);
            var dormant = State(controller.layers[ControlLayer].stateMachine, "Invalid dormant control", Clip(controller, "Invalid dormant"));
            Transition(controlState, dormant, "AFK", AnimatorConditionMode.If, 0);
            var command = Control(dormant);
            using (var data = new SerializedObject(command))
            {
                switch (corruption)
                {
                    case "nanWeight": data.FindProperty("goalWeight").floatValue = float.NaN; break;
                    case "infiniteWeight": data.FindProperty("goalWeight").floatValue = float.PositiveInfinity; break;
                    case "negativeWeight": data.FindProperty("goalWeight").floatValue = -.1f; break;
                    case "largeWeight": data.FindProperty("goalWeight").floatValue = 1.1f; break;
                    case "nanDuration": data.FindProperty("blendDuration").floatValue = float.NaN; break;
                    case "infiniteDuration": data.FindProperty("blendDuration").floatValue = float.PositiveInfinity; break;
                    case "negativeDuration": data.FindProperty("blendDuration").floatValue = -1; break;
                    case "negativeIndex": data.FindProperty("layer").intValue = -1; break;
                    case "outOfRangeIndex": data.FindProperty("layer").intValue = controller.layers.Length; break;
                    case "invalidPlayable": data.FindProperty("playable").intValue = int.MaxValue; break;
                }
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            var before = CaptureAssets(); var warnings = new List<string>();
            var error = Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar, warnings: warnings));
            Assert.That(error.Message, Does.Contain("Invalid dormant control").And.Contain("VRCAnimatorLayerControl"));
            Assert.That(warnings, Is.Empty); AssertSourceUnchanged(before);
        }

        [TestCase("unknown")]
        [TestCase("nan")]
        [TestCase("event")]
        public void KnownLayerControlFallbackCannotHideUnknownCallbacksOrBadAnimationData(string damage)
        {
            Control(controlState);
            var later = State(controller.layers[ControlLayer].stateMachine, "Later unsafe data", Clip(controller, "Later unsafe clip"));
            var transition = controlState.AddTransition(later); transition.hasExitTime = true; transition.exitTime = 10; transition.duration = 0;
            var clip = (AnimationClip)later.motion;
            if (damage == "unknown") Assert.That(later.AddStateMachineBehaviour<UnknownStateCallbackProbe>(), Is.Not.Null);
            else if (damage == "nan")
            {
                controller.AddParameter("Unsafe parameter", AnimatorControllerParameterType.Float);
                SetMalformedParameterCurve(clip, "Unsafe parameter");
            }
            else AnimationUtility.SetAnimationEvents(clip, new[] { new AnimationEvent { time = 0, functionName = "MustNotRun" } });
            var before = CaptureAssets(); var warnings = new List<string>();
            var error = Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar, warnings: warnings));
            if (damage == "unknown") Assert.That(error.Message, Does.Contain(nameof(UnknownStateCallbackProbe)));
            if (damage == "event") Assert.That(error.Message, Does.Contain("AnimationEvent"));
            Assert.That(warnings, Is.Empty); AssertSourceUnchanged(before);
        }

        [TestCase("nan")]
        [TestCase("event")]
        [TestCase("driver")]
        public void AdditionalFxControlCannotHideReachableBadDataEvenWhenEveryNeutralIsRetained(string damage)
        {
            var other = AdditionalBadData(damage, dormant: false);
            var before = CaptureAssets(); var otherBefore = EditorJsonUtility.ToJson(other); var warnings = new List<string>();
            var error = Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar, warnings: warnings));
            Assert.That(error.Message, Does.Contain("Additional"));
            if (damage == "event") Assert.That(error.Message, Does.Contain("AnimationEvent"));
            if (damage == "driver") Assert.That(error.Message, Does.Contain("Parameter Driver").And.Contain("Driver read"));
            Assert.That(warnings, Is.Empty); AssertSourceUnchanged(before);
            Assert.That(EditorJsonUtility.ToJson(other), Is.EqualTo(otherBefore));
        }

        [TestCase("nan")]
        [TestCase("event")]
        public void ProvenDormantAdditionalBadDataDoesNotBlockPreparedNeutralFallback(string damage)
        {
            var other = AdditionalBadData(damage, dormant: true);
            var before = CaptureAssets(); var otherBefore = EditorJsonUtility.ToJson(other); var warnings = new List<string>();
            Assert.That(NeutralShapeSampler.Sample(avatar, warnings: warnings), Is.Empty);
            Assert.That(warnings.Any(value => value.Contains("Body size") && value.Contains("VRCPlayableLayerControl")), Is.True, string.Join("\n", warnings));
            AssertSourceUnchanged(before); Assert.That(EditorJsonUtility.ToJson(other), Is.EqualTo(otherBefore));
        }

        private AnimatorController AdditionalBadData(string damage, bool dormant)
        {
            var other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Additional.controller");
            var machine = other.layers[0].stateMachine;
            var idle = State(machine, "Additional default", Clip(other, "Additional empty"));
            Control(idle, global: true); // All captured FX roots require prepared-rest fallback.
            var bad = State(machine, "Additional unsafe state", Clip(other, "Additional unsafe clip"));
            if (dormant)
            {
                other.AddParameter(new AnimatorControllerParameter { name = "AFK", type = AnimatorControllerParameterType.Bool, defaultBool = true });
                Transition(idle, bad, "AFK", AnimatorConditionMode.If, 0);
            }
            else
            {
                var transition = idle.AddTransition(bad); transition.hasExitTime = true; transition.exitTime = 10; transition.duration = 0;
            }
            var clip = (AnimationClip)bad.motion;
            if (damage == "event") AnimationUtility.SetAnimationEvents(clip, new[] { new AnimationEvent { time = 0, functionName = "MustNotRun" } });
            else if (damage == "nan")
            {
                other.AddParameter("Unconsumed numeric curve", AnimatorControllerParameterType.Float);
                SetMalformedParameterCurve(clip, "Unconsumed numeric curve");
            }
            else
            {
                other.AddParameter("Driver read", AnimatorControllerParameterType.Float);
                ParameterDriverExpressionTests.Driver(bad, ParameterDriverExpressionTests.Op("Set", "Driver read", float.NaN));
                var after = State(machine, "Reads malformed destination", Clip(other, "Additional end"));
                Transition(bad, after, "Driver read", AnimatorConditionMode.Greater, .5f);
            }
            SetControllers(descriptor, controller, other); return other;
        }

        private static void SetMalformedParameterCurve(AnimationClip clip, string parameter)
        {
            var binding = EditorCurveBinding.FloatCurve("", typeof(Animator), parameter);
            // Unity may discard a key with a NaN value during SetEditorCurve.
            // An active NaN tangent survives serialization and is malformed
            // interpolation data, unlike an unused first incoming tangent.
            AnimationUtility.SetEditorCurve(clip, binding,
                new AnimationCurve(new Keyframe(0, 0, 0, float.NaN), new Keyframe(1, 1, 1, 0)));
            var persisted = AnimationUtility.GetEditorCurve(clip, binding);
            Assert.That(persisted, Is.Not.Null); Assert.That(persisted.length, Is.EqualTo(2));
            Assert.That(float.IsNaN(persisted.keys[0].outTangent), Is.True, "The test must retain malformed active data in the actual Unity clip.");
        }

        [Test]
        public void InvalidFxDriverReadOnlyByAnotherPlayableRemainsHardBeforeLayerControlFallback()
        {
            const string input = "Read only by Action";
            controller.AddParameter(input, AnimatorControllerParameterType.Float);
            var driver = ParameterDriverExpressionTests.Driver(controlState, ParameterDriverExpressionTests.Op("Set", input, float.NaN));
            var other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Action.controller");
            other.AddParameter(input, AnimatorControllerParameterType.Float);
            var machine = other.layers[0].stateMachine;
            var idle = State(machine, "Action reads FX driver", Clip(other, "Action idle"));
            var later = State(machine, "Action later", Clip(other, "Action later"));
            Transition(idle, later, input, AnimatorConditionMode.Greater, .5f);
            var control = Control(idle, global: true); SetControllers(descriptor, controller, other);
            var before = CaptureAssets(); var beforeOther = EditorJsonUtility.ToJson(other);
            var beforeControl = EditorJsonUtility.ToJson(control); var beforeDriver = EditorJsonUtility.ToJson(driver);
            var warnings = new List<string>();
            var error = Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar, warnings: warnings));
            Assert.That(error.Message, Does.Contain("Parameter Driver").And.Contain(input));
            Assert.That(warnings, Is.Empty); AssertSourceUnchanged(before);
            Assert.That(EditorJsonUtility.ToJson(other), Is.EqualTo(beforeOther));
            Assert.That(EditorJsonUtility.ToJson(control), Is.EqualTo(beforeControl));
            Assert.That(EditorJsonUtility.ToJson(driver), Is.EqualTo(beforeDriver));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void DirectClipEarlyReturnsStillValidateAdditionalFxEffects(bool inactiveProvenance, bool dormant)
        {
            var fx = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Direct.controller");
            // No stationary scalar or constant-parameter support: without a
            // source slot this reaches the standalone direct-clip early exit.
            var moving = Clip(fx, "Unresolved lower native motion", new[] { "Body" }, "Body size", 63);
            AnimationUtility.SetEditorCurve(moving, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Body size"),
                AnimationCurve.Linear(0, 63, 1, 91));
            State(fx.layers[0].stateMachine, "Moving lower", moving);
            var selectedClip = Clip(fx, "Authored direct endpoint", new[] { "Body" }, "Body size", 80);
            AnimatorState selected = null;
            if (inactiveProvenance)
            {
                selected = State(Layer(fx, "Inactive authored slot"), "Authored candidate", selectedClip);
                var layers = fx.layers; layers[1].defaultWeight = 0; fx.layers = layers;
            }
            var other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/DirectAction.controller");
            other.AddParameter(new AnimatorControllerParameter { name = "AFK", type = AnimatorControllerParameterType.Bool, defaultBool = true });
            var machine = other.layers[0].stateMachine;
            var idle = State(machine, "Normal Action", Clip(other, "Action idle"));
            var command = State(machine, "Additional direct-clip control", Clip(other, "Action command"));
            Control(command, layer: 0);
            Transition(idle, command, "AFK", AnimatorConditionMode.If, 0);
            if (!dormant) machine.defaultState = command;
            SetControllers(descriptor, fx, other);
            var metadata = VrChatExpressionMenu.Read(avatar, new VrChatMenuImportPolicy { SkipAll = true });
            Assert.That(metadata.OtherControllers, Does.Contain(other));
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, selectedClip, entry);
            var originalValues = entry.Values.Select(value => (value.Path, value.Shape, value.Weight)).ToArray();
            var before = CaptureAssets();
            var otherAssets = AssetDatabase.LoadAllAssetsAtPath(folder + "/Direct.controller")
                .Concat(AssetDatabase.LoadAllAssetsAtPath(folder + "/DirectAction.controller"))
                .ToDictionary(value => value, value => EditorJsonUtility.ToJson(value));
            void Apply() => VrChatExpressionSampler.ApplyPermanentOverrides(avatar, fx, entry,
                inactiveProvenance ? (int?)1 : null, metadata: metadata, sourceState: selected);
            if (dormant) Apply();
            else Assert.That(Assert.Throws<InvalidOperationException>(Apply).Message, Does.Contain("VRCAnimatorLayerControl"));
            Assert.That(entry.Values.Select(value => (value.Path, value.Shape, value.Weight)).ToArray(), Is.EqualTo(originalValues));
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(80)); Assert.That(entry.Error, Is.Null);
            AssertSourceUnchanged(before);
            foreach (var pair in otherAssets) Assert.That(EditorJsonUtility.ToJson(pair.Key), Is.EqualTo(pair.Value), pair.Key.name);
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void ExplicitSelectedExpressionsCannotIgnoreReachableSdkFxWeightEffects(bool otherController, bool global)
        {
            controller.AddParameter("Menu", AnimatorControllerParameterType.Int);
            var selected = State(controller.layers[BodyLayer].stateMachine, "Selected face", Clip(controller, "Selected face", new[] { "Body" }, "Body size", 80));
            Transition(bodyState, selected, "Menu", AnimatorConditionMode.Equals, 1);
            var state = controlState;
            if (otherController)
            {
                var other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Action.controller");
                state = State(other.layers[0].stateMachine, "Other reached control", Clip(other, "Other empty"));
                SetControllers(descriptor, controller, other);
            }
            Control(state, global: global); var before = CaptureAssets();
            var source = VrChatExpressionMenu.Read(avatar, new VrChatMenuImportPolicy { SkipAll = true });
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleFixed(avatar, controller, source.Defaults,
                new Dictionary<string, float> { ["Menu"] = 1 }, null, source));
            Assert.That(error.Message, Does.Contain(global ? "VRCPlayableLayerControl" : "VRCAnimatorLayerControl"));
            AssertSourceUnchanged(before);
        }

        [Test]
        public async Task MmdControlledPreparedRestAndAuthoredEndpointSurviveFullExportAndVrmRoundTrip()
        {
            var shader = Shader.Find("lilToon"); if (shader == null) Assert.Ignore("Install lilToon for the real exporter entry point.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            Vrm10Instance imported = null; GameObject expected = null;
            try
            {
                fixture.Mesh.ClearBlendShapes();
                fixture.Mesh.AddBlendShapeFrame("Body size", 100, Enumerable.Repeat(Vector3.right * .02f, fixture.Mesh.vertexCount).ToArray(), null, null);
                fixture.Mesh.AddBlendShapeFrame("Open", 100, Enumerable.Repeat(Vector3.up * .015f, fixture.Mesh.vertexCount).ToArray(), null, null);
                foreach (var renderer in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>())
                { renderer.sharedMaterial.shader = shader; renderer.SetBlendShapeWeight(0, 17); renderer.SetBlendShapeWeight(1, 23); }
                var fx = AnimatorController.CreateAnimatorControllerAtPath(folder + "/RoundTrip.controller");
                BuildGraph(fx, new[] { "Front", "Back" }, out _, out _); MmdRelay(fx, true);
                SetControllers(fixture.Source.AddComponent(SdkType("VRCAvatarDescriptor")), fx);
                var vrm = ScriptableObject.CreateInstance<VRM10Object>();
                var endpoint = ScriptableObject.CreateInstance<VRM10Expression>(); endpoint.name = "Absolute body endpoint";
                endpoint.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 0, .8f), new MorphTargetBinding("Back", 0, .8f) };
                vrm.Expression.CustomClips.Add(endpoint);
                AssetDatabase.CreateAsset(vrm, folder + "/Vrm.asset"); AssetDatabase.CreateAsset(endpoint, folder + "/Endpoint.asset");
                fixture.Source.AddComponent<Vrm10Instance>().Vrm = vrm;
                var assetBefore = AssetDatabase.LoadAllAssetsAtPath(folder + "/RoundTrip.controller")
                    .Concat(new Object[] { fixture.Mesh, endpoint }).ToDictionary(value => value, value => EditorJsonUtility.ToJson(value));
                expected = Object.Instantiate(fixture.Source);
                foreach (var behaviour in expected.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                var warnings = new List<string>();
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "SDK layer control regression", "Tests", warnings,
                    blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(warnings.Any(value => value.Contains("Body size") && value.Contains("VRCAnimatorLayerControl")), Is.True, string.Join("\n", warnings));
                Assert.That(imported.Vrm.Expression.CustomClips.Any(clip => clip.name == endpoint.name), Is.True);
                var key = ExpressionKey.CreateCustom(endpoint.name);
                foreach (var input in new[] { 0f, 1f, 0f })
                {
                    imported.Runtime.Expression.SetWeight(key, input); imported.Runtime.Process();
                    foreach (var path in new[] { "Front", "Back" })
                    {
                        var reference = expected.transform.Find(path).GetComponent<SkinnedMeshRenderer>();
                        reference.SetBlendShapeWeight(0, input == 0 ? 17 : 80); reference.SetBlendShapeWeight(1, 100);
                        var actual = imported.GetComponentsInChildren<SkinnedMeshRenderer>().Single(renderer => renderer.name == path);
                        var a = Vertices(reference); var b = Vertices(actual); Assert.That(b.Length, Is.EqualTo(a.Length));
                        for (var i = 0; i < a.Length; i++) Assert.That(Vector3.Distance(a[i], b[i]), Is.LessThan(.0005f),
                            path + " / " + input + "\n" + string.Join("\n", warnings));
                    }
                }
                foreach (var pair in assetBefore) Assert.That(EditorJsonUtility.ToJson(pair.Key), Is.EqualTo(pair.Value), pair.Key.name);
                Assert.That(fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>().All(renderer => renderer.sharedMesh == fixture.Mesh &&
                    renderer.GetBlendShapeWeight(0) == 17 && renderer.GetBlendShapeWeight(1) == 23), Is.True);
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                if (expected != null) Object.DestroyImmediate(expected);
            }
        }

        private static Vector3[] Vertices(SkinnedMeshRenderer renderer)
        {
            var baked = new Mesh();
            try { renderer.BakeMesh(baked, false); return baked.vertices.Select(renderer.transform.TransformPoint).ToArray(); }
            finally { Object.DestroyImmediate(baked); }
        }
    }
}
