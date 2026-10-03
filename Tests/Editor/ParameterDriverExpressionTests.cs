using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class ParameterDriverExpressionTests
    {
        private string folder;
        private GameObject avatar;
        private Mesh mesh;
        private AnimatorController controller;
        private VrChatExpressionMenu.Source metadata;

        [SetUp]
        public void SetUp()
        {
            var name = "__ExpressionDrivers_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            controller.AddParameter("Menu", AnimatorControllerParameterType.Int);
            controller.AddParameter("Face", AnimatorControllerParameterType.Int);
            metadata = new VrChatExpressionMenu.Source { Controller = controller };
            metadata.Defaults["IsLocal"] = 1;
            avatar = new GameObject("Avatar", typeof(Animator));
            var body = new GameObject("Body", typeof(SkinnedMeshRenderer)); body.transform.SetParent(avatar.transform, false);
            mesh = BaseShapeFixture.Create(); body.GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
            body.GetComponent<SkinnedMeshRenderer>().SetBlendShapeWeight(0, 25);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(avatar); Object.DestroyImmediate(mesh); AssetDatabase.DeleteAsset(folder);
        }

        private AnimatorState State(AnimatorStateMachine machine, string name, AnimationClip motion = null)
        {
            var state = machine.AddState(name); state.motion = motion; state.writeDefaultValues = false; return state;
        }

        private AnimatorStateMachine Layer(string name)
        {
            controller.AddLayer(name); var layers = controller.layers;
            layers[layers.Length - 1].defaultWeight = 1; controller.layers = layers;
            return layers[layers.Length - 1].stateMachine;
        }

        private static void Transition(AnimatorState from, AnimatorState to, string parameter, float value, AnimatorConditionMode mode = AnimatorConditionMode.Equals)
        {
            var t = from.AddTransition(to); t.hasExitTime = false; t.duration = 0; t.canTransitionToSelf = true;
            t.AddCondition(mode, value, parameter);
        }

        private AnimationClip Clip(string name, float value, bool missing = false)
        {
            var clip = new AnimationClip { name = name };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Face size"), AnimationCurve.Constant(0, 1, value));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Blink"), AnimationCurve.Constant(0, 1, 0));
            if (missing) AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.extra_cheek5"), AnimationCurve.Constant(0, 1, 100));
            AssetDatabase.AddObjectToAsset(clip, controller); return clip;
        }

        private AnimatorState Gate()
        {
            var machine = controller.layers[0].stateMachine;
            var idle = State(machine, "Idle"); machine.defaultState = idle;
            var gate = State(machine, "Select face"); Transition(idle, gate, "Menu", 1); return gate;
        }

        private void FaceLayer(int selected = 1, bool missing = false)
        {
            var machine = Layer("Face layer");
            var idle = State(machine, "Neutral", Clip("Neutral", 0, missing)); machine.defaultState = idle;
            var smile = State(machine, "Smile", Clip("Smile", 75, missing));
            Transition(idle, smile, "Face", selected);
        }

        private List<VrChatExpressionMenu.MorphValue> Sample(IList<VrChatExpressionMenu.MorphValue> unresolved = null) =>
            VrChatExpressionSampler.Sample(avatar, controller, metadata.Defaults,
                new Dictionary<string, float> { ["Menu"] = 1 }, null, metadata, unresolved);

        private List<VrChatExpressionMenu.MorphValue> SampleFixed(FixedExpressionContext context = null,
            IDictionary<string, float> selected = null) =>
            VrChatExpressionSampler.SampleFixed(avatar, controller, metadata.Defaults,
                selected ?? new Dictionary<string, float> { ["Menu"] = 1 }, null, metadata, fixedContext: context);

        internal static VrChatParameterDriver.Operation Op(string kind, string destination, float value = 0, string source = null) =>
            new VrChatParameterDriver.Operation { Kind = kind, Destination = destination, Value = value, Source = source };

        internal static StateMachineBehaviour Driver(AnimatorState state, params VrChatParameterDriver.Operation[] operations)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("VRC.SDK3.Avatars.Components.VRCAvatarParameterDriver")).FirstOrDefault(t => t != null);
            if (type == null) Assert.Ignore("Install the real VRChat SDK to run state-entry integration tests.");
            var driver = state.AddStateMachineBehaviour(type);
            using (var data = new SerializedObject(driver))
            {
                var parameters = data.FindProperty("parameters"); parameters.arraySize = operations.Length;
                for (var i = 0; i < operations.Length; i++)
                {
                    var item = parameters.GetArrayElementAtIndex(i); var op = operations[i];
                    item.FindPropertyRelative("name").stringValue = op.Destination;
                    var kind = item.FindPropertyRelative("type"); kind.enumValueIndex = Array.IndexOf(kind.enumNames, op.Kind);
                    item.FindPropertyRelative("value").floatValue = op.Value;
                    item.FindPropertyRelative("source").stringValue = op.Source ?? "";
                    item.FindPropertyRelative("convertRange").boolValue = op.ConvertRange;
                    item.FindPropertyRelative("sourceMin").floatValue = op.SourceMin;
                    item.FindPropertyRelative("sourceMax").floatValue = op.SourceMax;
                    item.FindPropertyRelative("destMin").floatValue = op.DestinationMin;
                    item.FindPropertyRelative("destMax").floatValue = op.DestinationMax;
                }
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            return driver;
        }

        private static StateMachineBehaviour SdkBehaviour(AnimatorState state, string name)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);
            if (type == null) Assert.Ignore("Install the real VRChat SDK to run playable-control integration tests.");
            Assert.That(type.IsAbstract, Is.False, "Only concrete SDK behaviours can be attached: " + name);
            var behaviour = state.AddStateMachineBehaviour(type);
            Assert.That(behaviour, Is.Not.Null, "The SDK behaviour could not be attached: " + name);
            return behaviour;
        }

        private static StateMachineBehaviour PlayableControl(AnimatorState state, string target, float weight = .5f, float duration = 2,
            string type = "VRC.SDK3.Avatars.Components.VRCPlayableLayerControl")
        {
            var control = SdkBehaviour(state, type);
            using (var data = new SerializedObject(control))
            {
                var layer = data.FindProperty("layer");
                layer.enumValueIndex = Array.IndexOf(layer.enumNames, target);
                data.FindProperty("goalWeight").floatValue = weight;
                data.FindProperty("blendDuration").floatValue = duration;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            return control;
        }

        [TestCase("Action", "VRC.SDK3.Avatars.Components.VRCPlayableLayerControl")]
        [TestCase("Gesture", "VRC.SDK3.Avatars.Components.VRCPlayableLayerControl")]
        [TestCase("Additive", "VRC.SDK3.Avatars.Components.VRCPlayableLayerControl")]
        public void BodyPlayableControlAndTrackedHandDoNotBlockFixedFacialSampler(string target, string type)
        {
            var face = Gate(); face.motion = Clip("Menu face", 75);
            var dance = Layer("Dance"); var idle = State(dance, "0"); dance.defaultState = idle;
            var selected = State(dance, "1"); Transition(idle, selected, "Menu", 1);
            var off = PlayableControl(idle, target, 0, type: type);
            var on = PlayableControl(selected, target, 1, type: type);
            var tracking = SdkBehaviour(selected, "VRC.SDK3.Avatars.Components.VRCAnimatorTrackingControl");
            using (var data = new SerializedObject(tracking))
            {
                var hand = data.FindProperty("trackingLeftHand");
                hand.enumValueIndex = Array.IndexOf(hand.enumNames, "Tracking");
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            var assets = AssetDatabase.LoadAllAssetsAtPath(folder + "/FX.controller");
            var before = assets.ToDictionary(a => a, a => EditorJsonUtility.ToJson(a));
            Assert.That(VrChatParameterDriver.IsNonFxPlayableControl(off), Is.True);
            Assert.That(VrChatParameterDriver.IsNonFxPlayableControl(on), Is.True);
            Assert.That(Sample().Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
            foreach (var asset in assets) Assert.That(EditorJsonUtility.ToJson(asset), Is.EqualTo(before[asset]), asset.name);
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(25));
        }

        private AnimatorState NeutralFxGate(string parameter = "Dance")
        {
            controller.AddParameter(parameter, AnimatorControllerParameterType.Int);
            var machine = Layer("Dance gate"); var neutral = State(machine, "DISABLE dance"); machine.defaultState = neutral;
            var disabled = State(machine, "ENABLE dance");
            PlayableControl(neutral, "FX", 1, 0); PlayableControl(disabled, "FX", 0, 0);
            Transition(neutral, disabled, parameter, 1);
            return neutral;
        }

        private AnimatorState AndGuardedFxGate()
        {
            controller.AddParameter("Dance", AnimatorControllerParameterType.Bool);
            controller.AddParameter("InStation", AnimatorControllerParameterType.Bool);
            var machine = Layer("Station dance gate"); var neutral = State(machine, "Neutral FX"); machine.defaultState = neutral;
            var disabled = State(machine, "Station dance disables FX");
            PlayableControl(neutral, "FX", 1, 0); PlayableControl(disabled, "FX", 0, 0);
            Transition(neutral, disabled, "Dance", 0, AnimatorConditionMode.If);
            neutral.transitions[0].AddCondition(AnimatorConditionMode.If, 0, "InStation");
            Transition(disabled, neutral, "Dance", 0, AnimatorConditionMode.IfNot);
            Transition(disabled, neutral, "InStation", 0, AnimatorConditionMode.IfNot);
            return neutral;
        }

        private void StationFxGate(string structure = "Flat")
        {
            controller.AddParameter("InStation", AnimatorControllerParameterType.Bool);
            var machine = Layer("Station FX gate");
            var neutral = State(machine, "Neutral FX"); machine.defaultState = neutral;
            var disabledMachine = structure == "Nested" ? machine.AddStateMachine("Station") : machine;
            var disabled = State(disabledMachine, "Station disables FX");
            if (disabledMachine != machine) disabledMachine.defaultState = disabled;
            PlayableControl(neutral, "FX", 1, 0); PlayableControl(disabled, "FX", 0, 0);
            if (structure == "AnyState")
            {
                var transition = machine.AddAnyStateTransition(disabled);
                transition.hasExitTime = false; transition.duration = 0; transition.canTransitionToSelf = false;
                transition.AddCondition(AnimatorConditionMode.If, 0, "InStation");
            }
            else if (structure == "Nested")
            {
                var transition = neutral.AddTransition(disabledMachine);
                transition.hasExitTime = false; transition.duration = 0;
                transition.AddCondition(AnimatorConditionMode.If, 0, "InStation");
            }
            else Transition(neutral, disabled, "InStation", 0, AnimatorConditionMode.If);
            if (structure == "MotionWriteDefaults")
            {
                disabled.motion = Clip("Unreached station face", 0);
                disabled.writeDefaultValues = true;
            }
        }

        [Test]
        public void FixedNormalStationContextPreservesAuthoredAssetsDefaultsAndAvatarWeights()
        {
            Gate().motion = Clip("Menu face", 75); StationFxGate();
            controller.AddParameter("IsLocal", AnimatorControllerParameterType.Bool);
            var assets = AssetDatabase.LoadAllAssetsAtPath(folder + "/FX.controller");
            var before = assets.ToDictionary(a => a, a => EditorJsonUtility.ToJson(a));
            var defaultsBefore = new Dictionary<string, float>(metadata.Defaults);
            var context = FixedExpressionContext.Create(controller, metadata.Defaults, metadata);
            Assert.That(context.Values["InStation"], Is.Zero);
            Assert.That(context.Values["IsLocal"], Is.EqualTo(1));
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message,
                Does.Contain("外部入力").And.Contain("InStation"));
            Assert.That(SampleFixed(context).Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
            foreach (var asset in assets) Assert.That(EditorJsonUtility.ToJson(asset), Is.EqualTo(before[asset]), asset.name);
            CollectionAssert.AreEquivalent(defaultsBefore, metadata.Defaults);
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(25));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh, Is.SameAs(mesh));
        }

        [TestCase("AnyState")]
        [TestCase("Nested")]
        [TestCase("MotionWriteDefaults")]
        public void FixedFalseStationContextProvesComplexDisabledBranchUnreachable(string structure)
        {
            Gate().motion = Clip("Menu face", 75); StationFxGate(structure);
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message,
                Does.Contain("外部入力").And.Contain("InStation"));
            Assert.That(SampleFixed().Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
        }

        [Test]
        public void FixedTrueStationContextStillRejectsAReachableFxDisable()
        {
            Gate().motion = Clip("Menu face", 75); StationFxGate();
            var context = FixedExpressionContext.Create(controller, metadata.Defaults, metadata);
            context.Values["InStation"] = 1;
            Assert.That(Assert.Throws<InvalidOperationException>(() => SampleFixed(context)).Message,
                Does.Contain("Station disables FX").And.Contain("FXの重み"));
            Assert.That(SampleFixed().Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
        }

        [Test]
        public void FixedBuiltinCopyUsesNormalAndExplicitStationValuesWhileStrictSamplingRejectsIt()
        {
            controller.AddParameter("InStation", AnimatorControllerParameterType.Bool);
            Driver(Gate(), Op("Copy", "Face", source: "InStation")); FaceLayer();
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message,
                Does.Contain("組み込みパラメーター").And.Contain("Copy元"));
            var context = FixedExpressionContext.Create(controller, metadata.Defaults, metadata);
            Assert.That(SampleFixed(context).Single(v => v.Shape == "Face size").Weight, Is.EqualTo(0).Within(.01));
            context.Values["InStation"] = 1;
            Assert.That(SampleFixed(context).Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
        }

        [TestCase("EyeHeightAsMeters", 0f, false)]
        [TestCase("EyeHeightAsMeters", 1f, false)]
        [TestCase("EyeHeightAsMeters", 0f, true)]
        [TestCase("EyeHeightAsMeters", 1f, true)]
        [TestCase("EyeHeightAsPercent", 0f, false)]
        [TestCase("EyeHeightAsPercent", 1f, false)]
        [TestCase("EyeHeightAsPercent", 0f, true)]
        [TestCase("EyeHeightAsPercent", 1f, true)]
        public void FixedEyeHeightFaceTransitionDoesNotTrustAuthoredOrSuppliedDefaults(string parameter, float value, bool suppliedDefault)
        {
            controller.AddParameter(new AnimatorControllerParameter
            {
                name = parameter, type = AnimatorControllerParameterType.Float, defaultFloat = suppliedDefault ? 0 : value
            });
            if (suppliedDefault) metadata.Defaults[parameter] = value;
            Gate().motion = Clip("Eye height face", 75);
            var idle = controller.layers[0].stateMachine.defaultState;
            idle.motion = Clip("Neutral face", 0);
            idle.transitions[0].AddCondition(AnimatorConditionMode.Greater, .5f, parameter);
            var context = FixedExpressionContext.Create(controller, metadata.Defaults, metadata);
            Assert.That(context.Values.ContainsKey(parameter), Is.False, "An authored default is not a measured eye height.");
            Assert.That(Assert.Throws<InvalidOperationException>(() => SampleFixed(context)).Message,
                Does.Contain("外部入力").And.Contain(parameter));
        }

        [TestCase("EyeHeightAsMeters", 0f)]
        [TestCase("EyeHeightAsMeters", 1f)]
        [TestCase("EyeHeightAsPercent", 0f)]
        [TestCase("EyeHeightAsPercent", 1f)]
        public void FixedEyeHeightDriverCopyRequiresExplicitInput(string parameter, float authoredValue)
        {
            controller.AddParameter(new AnimatorControllerParameter
            {
                name = parameter, type = AnimatorControllerParameterType.Float, defaultFloat = authoredValue
            });
            metadata.Defaults[parameter] = authoredValue;
            Driver(Gate(), Op("Copy", "Face", source: parameter)); FaceLayer();
            var context = FixedExpressionContext.Create(controller, metadata.Defaults, metadata);
            Assert.That(context.Values.ContainsKey(parameter), Is.False);
            Assert.That(Assert.Throws<InvalidOperationException>(() => SampleFixed(context)).Message,
                Does.Contain("外部入力").And.Contain(parameter));
            context.Values[parameter] = 1;
            Assert.That(SampleFixed(context).Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
            Assert.That(metadata.Defaults[parameter], Is.EqualTo(authoredValue));
        }

        [TestCase("EyeHeightAsMeters", 0f)]
        [TestCase("EyeHeightAsMeters", 1f)]
        [TestCase("EyeHeightAsPercent", 0f)]
        [TestCase("EyeHeightAsPercent", 1f)]
        public void FixedUnusedEyeHeightDeclarationDoesNotBlockFace(string parameter, float authoredValue)
        {
            controller.AddParameter(new AnimatorControllerParameter
            {
                name = parameter, type = AnimatorControllerParameterType.Float, defaultFloat = authoredValue
            });
            metadata.Defaults[parameter] = authoredValue;
            Gate().motion = Clip("Menu face", 75);
            var context = FixedExpressionContext.Create(controller, metadata.Defaults, metadata);
            Assert.That(context.Values.ContainsKey(parameter), Is.False);
            Assert.That(SampleFixed(context).Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
            Assert.That(context.UsedParameters.Contains(parameter), Is.False);
        }

        [TestCase("EyeHeightAsMeters", 0f, 0f)]
        [TestCase("EyeHeightAsMeters", 1f, 75f)]
        [TestCase("EyeHeightAsPercent", 0f, 0f)]
        [TestCase("EyeHeightAsPercent", 1f, 75f)]
        public void FixedExplicitEyeHeightSelectsCorrectFace(string parameter, float suppliedValue, float expectedWeight)
        {
            controller.AddParameter(new AnimatorControllerParameter
            {
                name = parameter, type = AnimatorControllerParameterType.Float, defaultFloat = 1 - suppliedValue
            });
            metadata.Defaults[parameter] = 1 - suppliedValue;
            Gate().motion = Clip("Eye height face", 75);
            var idle = controller.layers[0].stateMachine.defaultState;
            idle.motion = Clip("Neutral face", 0);
            idle.transitions[0].AddCondition(AnimatorConditionMode.Greater, .5f, parameter);
            var context = FixedExpressionContext.Create(controller, metadata.Defaults, metadata);
            context.Values[parameter] = suppliedValue;
            Assert.That(SampleFixed(context).Single(v => v.Shape == "Face size").Weight, Is.EqualTo(expectedWeight).Within(.01));
            Assert.That(context.UsedParameters.Contains(parameter), Is.True);
        }

        [TestCase("EyeHeightAsMeters", 0f)]
        [TestCase("EyeHeightAsMeters", 1f)]
        [TestCase("EyeHeightAsPercent", 0f)]
        [TestCase("EyeHeightAsPercent", 1f)]
        public void FixedEyeHeightFxGateRequiresExplicitInput(string parameter, float authoredValue)
        {
            Gate().motion = Clip("Menu face", 75);
            controller.AddParameter(new AnimatorControllerParameter
            {
                name = parameter, type = AnimatorControllerParameterType.Float, defaultFloat = authoredValue
            });
            metadata.Defaults[parameter] = authoredValue;
            var machine = Layer("Eye height FX gate");
            var neutral = State(machine, "Neutral FX"); machine.defaultState = neutral;
            var disabled = State(machine, "Eye height disables FX");
            PlayableControl(neutral, "FX", 1, 0); PlayableControl(disabled, "FX", 0, 0);
            Transition(neutral, disabled, parameter, .5f, AnimatorConditionMode.Greater);
            Assert.That(Assert.Throws<InvalidOperationException>(() => SampleFixed()).Message,
                Does.Contain("外部入力").And.Contain(parameter));
        }

        [Test]
        public void FixedStationReachabilityFollowsNestedEntryExitAndStateMachineTransitions()
        {
            Gate().motion = Clip("Menu face", 75);
            controller.AddParameter("InStation", AnimatorControllerParameterType.Bool);
            var root = Layer("Entry exit FX gate");
            var neutral = State(root, "Neutral FX"); root.defaultState = neutral;
            PlayableControl(neutral, "FX", 1, 0);
            var nested = root.AddStateMachine("Nested gate");
            var fallback = State(nested, "Unreached entry fallback"); nested.defaultState = fallback;
            var fallbackCommand = PlayableControl(fallback, "FX", 0, 0);
            var entry = State(nested, "Entry command");
            var entryCommand = PlayableControl(entry, "FX", 1, 0);
            nested.AddEntryTransition(entry);
            var enter = neutral.AddTransition(nested); enter.hasExitTime = false; enter.duration = 0;
            enter.AddCondition(AnimatorConditionMode.If, 0, "InStation");
            var exit = entry.AddExitTransition(); exit.hasExitTime = false; exit.duration = 0;
            exit.AddCondition(AnimatorConditionMode.If, 0, "InStation");
            var disabled = State(root, "Exit disables FX");
            var disabledCommand = PlayableControl(disabled, "FX", 0, 0);
            root.AddStateMachineTransition(nested, disabled);
            var context = FixedExpressionContext.Create(controller, metadata.Defaults, metadata);
            Assert.That(SampleFixed(context).Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
            Assert.That(FixedExpressionContext.TryReachableFxCommands(root, context.Values, controller.parameters, out var normalCommands), Is.True);
            Assert.That(normalCommands.Contains(entryCommand), Is.False);
            Assert.That(normalCommands.Contains(disabledCommand), Is.False);
            Assert.That(normalCommands.Contains(fallbackCommand), Is.False);
            context.Values["InStation"] = 1;
            Assert.That(FixedExpressionContext.TryReachableFxCommands(root, context.Values, controller.parameters, out var stationCommands), Is.True);
            Assert.That(stationCommands.Contains(entryCommand), Is.True);
            Assert.That(stationCommands.Contains(disabledCommand), Is.True);
            Assert.That(stationCommands.Contains(fallbackCommand), Is.False);
            Assert.That(Assert.Throws<InvalidOperationException>(() => SampleFixed(context)).Message,
                Does.Contain("Exit disables FX").And.Contain("FXの重み"));
        }

        [Test]
        public void FixedStationReachabilityKeepsParentAnyStateTransitionsWhenNestedStateIsActive()
        {
            Gate().motion = Clip("Menu face", 75);
            controller.AddParameter("InStation", AnimatorControllerParameterType.Bool);
            var root = Layer("Parent AnyState FX gate");
            var neutral = State(root, "Neutral FX"); root.defaultState = neutral;
            PlayableControl(neutral, "FX", 1, 0);
            var nested = root.AddStateMachine("Nested active state");
            var active = State(nested, "Active nested FX"); nested.defaultState = active;
            var activeCommand = PlayableControl(active, "FX", 1, 0);
            var enter = neutral.AddTransition(nested); enter.hasExitTime = false; enter.duration = 0;
            enter.AddCondition(AnimatorConditionMode.IfNot, 0, "InStation");
            var disabled = State(root, "Parent AnyState disables FX");
            var disabledCommand = PlayableControl(disabled, "FX", 0, 0);
            var any = root.AddAnyStateTransition(disabled); any.hasExitTime = false; any.duration = 0; any.canTransitionToSelf = false;
            any.AddCondition(AnimatorConditionMode.If, 0, "InStation");
            var context = FixedExpressionContext.Create(controller, metadata.Defaults, metadata);
            Assert.That(SampleFixed(context).Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
            Assert.That(FixedExpressionContext.TryReachableFxCommands(root, context.Values, controller.parameters, out var normalCommands), Is.True);
            Assert.That(normalCommands.Contains(activeCommand), Is.True);
            Assert.That(normalCommands.Contains(disabledCommand), Is.False);
            context.Values["InStation"] = 1;
            Assert.That(FixedExpressionContext.TryReachableFxCommands(root, context.Values, controller.parameters, out var stationCommands), Is.True);
            Assert.That(stationCommands, Does.Contain(disabledCommand));
            Assert.That(Assert.Throws<InvalidOperationException>(() => SampleFixed(context)).Message,
                Does.Contain("Parent AnyState disables FX").And.Contain("FXの重み"));
        }

        [Test]
        public void FixedContactInputUsesAuthoredDefaultAndAllowsExplicitSelectionOverride()
        {
            controller.AddParameter("Contact", AnimatorControllerParameterType.Float);
            metadata.ExternalParameters.Add("Contact"); metadata.Defaults["Contact"] = 1;
            Driver(Gate(), Op("Copy", "Face", source: "Contact")); FaceLayer();
            var context = FixedExpressionContext.Create(controller, metadata.Defaults, metadata);
            Assert.That(context.Values["Contact"], Is.EqualTo(1));
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message,
                Does.Contain("外部入力").And.Contain("Contact"));
            Assert.That(SampleFixed(context).Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
            var selected = new Dictionary<string, float> { ["Menu"] = 1, ["Contact"] = 0 };
            Assert.That(SampleFixed(context, selected).Single(v => v.Shape == "Face size").Weight, Is.EqualTo(0).Within(.01));
            Assert.That(context.Values["Contact"], Is.EqualTo(1));
            Assert.That(metadata.Defaults["Contact"], Is.EqualTo(1));
            Assert.That(selected["Contact"], Is.Zero);
        }

        [Test]
        public void FixedExternalContextDoesNotReapplyMenuAfterAParameterDriverReset()
        {
            var gate = Gate(); Driver(gate, Op("Add", "Face", 1), Op("Set", "Menu", 0));
            Transition(gate, controller.layers[0].stateMachine.defaultState, "Menu", 0);
            FaceLayer(); StationFxGate();
            var face = controller.layers[1].stateMachine;
            var smile = face.states.Single(s => s.state.name == "Smile").state;
            Transition(smile, face.defaultState, "Face", 1, AnimatorConditionMode.NotEqual);
            metadata.ExternalParameters.Add("Menu");
            var selected = new Dictionary<string, float> { ["Menu"] = 1 };
            Assert.That(SampleFixed(selected: selected).Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
            Assert.That(selected["Menu"], Is.EqualTo(1));
        }

        [Test]
        public void MenuAnalysisImportsStationDependentFaceUsingTheFixedNormalContext()
        {
            Gate().motion = Clip("Menu face", 75); StationFxGate();
            Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);
            var menuType = Find("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu");
            var descriptorType = Find("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            if (menuType == null || descriptorType == null) Assert.Ignore("Install the real VRChat SDK to run menu-analysis integration tests.");
            var menu = ScriptableObject.CreateInstance(menuType);
            AssetDatabase.CreateAsset(menu, folder + "/Menu.asset");
            using (var data = new SerializedObject(menu))
            {
                var controls = data.FindProperty("controls"); controls.arraySize = 1;
                var item = controls.GetArrayElementAtIndex(0);
                item.FindPropertyRelative("name").stringValue = "Station-independent smile";
                var type = item.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "Toggle");
                item.FindPropertyRelative("parameter").FindPropertyRelative("name").stringValue = "Menu";
                item.FindPropertyRelative("value").floatValue = 1;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            var descriptor = avatar.AddComponent(descriptorType);
            using (var data = new SerializedObject(descriptor))
            {
                data.FindProperty("customExpressions").boolValue = true;
                data.FindProperty("expressionsMenu").objectReferenceValue = menu;
                data.FindProperty("customizeAnimationLayers").boolValue = true;
                var list = data.FindProperty("baseAnimationLayers"); list.arraySize = 1;
                var layer = list.GetArrayElementAtIndex(0); var type = layer.FindPropertyRelative("type");
                type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
                layer.FindPropertyRelative("isDefault").boolValue = false;
                layer.FindPropertyRelative("animatorController").objectReferenceValue = controller;
                data.FindProperty("specialAnimationLayers").arraySize = 0;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            var source = VrChatExpressionSampler.Analyze(avatar);
            var entry = source.Entries.Single(e => e.Name == "Station-independent smile");
            Assert.That(entry.Error, Is.Null, string.Join("\n", source.Messages));
            Assert.That(entry.Values.Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
            Assert.That(source.Controller, Is.SameAs(controller));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnwrittenFalseAndGuardProvesNeutralFxIndependentOfStationInput(bool listedExternal)
        {
            Gate().motion = Clip("Menu face", 75); AndGuardedFxGate();
            if (listedExternal) metadata.ExternalParameters.Add("InStation");
            Assert.That(Sample().Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
        }

        [Test]
        public void SelectedTrueAndGuardCannotAssumeStationInputFalse()
        {
            Gate().motion = Clip("Menu face", 75); AndGuardedFxGate();
            var selection = new Dictionary<string, float> { ["Menu"] = 1, ["Dance"] = 1 };
            Assert.That(Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.Sample(
                avatar, controller, metadata.Defaults, selection, null, metadata)).Message,
                Does.Contain("外部入力").And.Contain("InStation"));
        }

        [Test]
        public void TrueDefaultAndGuardCannotAssumeStationInputFalse()
        {
            Gate().motion = Clip("Menu face", 75); AndGuardedFxGate(); metadata.Defaults["Dance"] = 1;
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message,
                Does.Contain("外部入力").And.Contain("InStation"));
        }

        [Test]
        public void DriverWrittenFalseAndGuardIsNotAssumedInvariant()
        {
            var face = Gate(); face.motion = Clip("Menu face", 75); AndGuardedFxGate();
            Driver(face, Op("Set", "Dance", 0));
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message,
                Does.Contain("外部入力").And.Contain("InStation"));
        }

        [Test]
        public void AnotherPlayableWriterInvalidatesFalseAndGuardProof()
        {
            Gate().motion = Clip("Menu face", 75); AndGuardedFxGate();
            var other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Other.controller");
            other.AddParameter("Dance", AnimatorControllerParameterType.Bool);
            var state = State(other.layers[0].stateMachine, "Writes guard"); other.layers[0].stateMachine.defaultState = state;
            Driver(state, Op("Set", "Dance", 0)); metadata.OtherControllers.Add(other);
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message,
                Does.Contain("外部入力").And.Contain("InStation"));
        }

        [Test]
        public void AnimatorCurveWriterInvalidatesFalseAndGuardProof()
        {
            Gate().motion = Clip("Menu face", 75); AndGuardedFxGate();
            var machine = Layer("Writes guard curve"); var clip = new AnimationClip { name = "Guard curve" };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "Dance"), AnimationCurve.Constant(0, 1, 0));
            AssetDatabase.AddObjectToAsset(clip, controller); machine.defaultState = State(machine, "Writes guard", clip);
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message,
                Does.Contain("外部入力").And.Contain("InStation"));
        }

        [Test]
        public void UnguardedReachableStationBranchIsNotHiddenByAnotherFalseAndGuard()
        {
            Gate().motion = Clip("Menu face", 75); var neutral = AndGuardedFxGate();
            var disabled = controller.layers[controller.layers.Length - 1].stateMachine.states.Single(s => s.state.name == "Station dance disables FX").state;
            Transition(neutral, disabled, "InStation", 0, AnimatorConditionMode.If);
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message,
                Does.Contain("外部入力").And.Contain("InStation"));
        }

        [Test]
        public void WriteDefaultsCommandLayerDoesNotQualifyForFalseAndGuardProof()
        {
            Gate().motion = Clip("Menu face", 75); var neutral = AndGuardedFxGate(); neutral.writeDefaultValues = true;
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message,
                Does.Contain("外部入力").And.Contain("InStation"));
        }
        [Test]
        public void ExplicitNeutralFxGateAllowsUnreachedDisableWithoutEditingSources()
        {
            Gate().motion = Clip("Menu face", 75); NeutralFxGate();
            var assets = AssetDatabase.LoadAllAssetsAtPath(folder + "/FX.controller");
            var before = assets.ToDictionary(a => a, a => EditorJsonUtility.ToJson(a));
            Assert.That(Sample().Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
            foreach (var asset in assets) Assert.That(EditorJsonUtility.ToJson(asset), Is.EqualTo(before[asset]), asset.name);
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(25));
        }

        [TestCase(0)]
        [TestCase(1)]
        public void UninitializedFxGateDoesNotAssumeFullWeight(float weight)
        {
            Gate().motion = Clip("Menu face", 75);
            var dance = Layer("Dance"); dance.defaultState = State(dance, "No command");
            var selected = State(dance, "Unreached command");
            PlayableControl(selected, "FX", weight, 0);
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message,
                Does.Contain("FXの初期状態").And.Contain("VRCPlayableLayerControl"));
        }

        [Test]
        public void FxOffSelectionIsRejectedAndLaterSamplesStartFromFreshDefaults()
        {
            Gate().motion = Clip("Menu face", 75); NeutralFxGate();
            var selection = new Dictionary<string, float> { ["Menu"] = 1, ["Dance"] = 1 };
            Assert.That(Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.Sample(
                avatar, controller, metadata.Defaults, selection, null, metadata)).Message,
                Does.Contain("ENABLE dance").And.Contain("FXの重み"));
            Assert.That(Sample().Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
        }

        [Test]
        public void TransientFxDisableDuringDefaultsIsNotResetIntoAnInventedFace()
        {
            Gate().motion = Clip("Menu face", 75);
            var machine = Layer("Transient FX gate"); var disable = State(machine, "Disable first"); machine.defaultState = disable;
            var restore = State(machine, "Restore");
            PlayableControl(disable, "FX", 0, 0); PlayableControl(restore, "FX", 1, 0);
            Transition(disable, restore, "Menu", 0);
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message,
                Does.Contain("Disable first").And.Contain("FXの重み"));
        }

        [Test]
        public void ImmediateFxDisableAndRestoreAfterSelectionCannotDisappearBetweenCallbacks()
        {
            Gate().motion = Clip("Menu face", 75);
            var machine = Layer("Transient selected FX gate"); var neutral = State(machine, "Neutral"); machine.defaultState = neutral;
            var disable = State(machine, "Immediate disable"); var restore = State(machine, "Immediate restore");
            PlayableControl(neutral, "FX", 1, 0); PlayableControl(disable, "FX", 0, 0); PlayableControl(restore, "FX", 1, 0);
            Transition(neutral, disable, "Menu", 1); Transition(disable, restore, "Menu", 1);
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message,
                Does.Contain("Immediate disable").And.Contain("FXの重み"));
        }

        [Test]
        public void NonUnitFxCommandCannotBeCancelledByAnotherCommandOnTheSameState()
        {
            Gate().motion = Clip("Menu face", 75);
            var machine = Layer("Multiple FX commands"); var state = State(machine, "Disable then restore"); machine.defaultState = state;
            PlayableControl(state, "FX", 0, 0); PlayableControl(state, "FX", 1, 0);
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message,
                Does.Contain("Disable then restore").And.Contain("FXの重み"));
        }
        [Test]
        public void ConflictingReachedFxCommandsCannotDependOnCallbackOrder()
        {
            Gate().motion = Clip("Menu face", 75); NeutralFxGate();
            var other = Layer("Conflicting FX gate"); other.defaultState = State(other, "Disable FX");
            PlayableControl(other.defaultState, "FX", 0, 0);
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message,
                Does.Contain("Disable FX").And.Contain("FXの重み"));
        }

        [TestCase(.5f)]
        [TestCase(10)]
        public void TemporalFxBlendRemainsUnsupportedEvenAtFullGoalWeight(float duration)
        {
            Gate().motion = Clip("Menu face", 75);
            var machine = Layer("Temporal FX gate"); machine.defaultState = State(machine, "Blend FX");
            PlayableControl(machine.defaultState, "FX", 1, duration);
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message,
                Does.Contain("Blend FX").And.Contain("影響範囲"));
        }

        [Test]
        public void FxGateExternalInputIsNotAssumedConstant()
        {
            Gate().motion = Clip("Menu face", 75); NeutralFxGate();
            metadata.ExternalParameters.Add("Dance");
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message,
                Does.Contain("外部入力").And.Contain("Dance"));
        }

        [Test]
        public void AnotherPlayableWritingAnFxGateRemainsUnsupported()
        {
            Gate().motion = Clip("Menu face", 75); NeutralFxGate();
            var other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Other.controller");
            other.AddParameter("Dance", AnimatorControllerParameterType.Int);
            var state = State(other.layers[0].stateMachine, "External gate writer"); other.layers[0].stateMachine.defaultState = state;
            Driver(state, Op("Set", "Dance", 1)); metadata.OtherControllers.Add(other);
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message,
                Does.Contain("FX以外").And.Contain("Dance"));
        }

        [Test]
        public void MenuDriverCanReachAndDisableAnOtherwiseUnrelatedFxGate()
        {
            var face = Gate(); face.motion = Clip("Menu face", 75); NeutralFxGate();
            Driver(face, Op("Set", "Dance", 1));
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message,
                Does.Contain("ENABLE dance").And.Contain("FXの重み"));
        }

        [Test]
        public void DelayedFxDisableOutsideTheSampleWindowRemainsUnsupported()
        {
            Gate().motion = Clip("Menu face", 75); var neutral = NeutralFxGate();
            neutral.motion = Clip("Neutral gate timing", 0);
            var timed = neutral.AddTransition(controller.layers[controller.layers.Length - 1].stateMachine.states
                .Single(s => s.state.name == "ENABLE dance").state);
            timed.hasExitTime = true; timed.exitTime = 10; timed.duration = 0;
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message, Does.Contain("FXの重み").Or.Contain("時間で遷移"));
        }

        [Test]
        public void ChangingParameterCurveCannotDisableFxAfterTheSampleWindow()
        {
            Gate().motion = Clip("Menu face", 75); var neutral = NeutralFxGate();
            var clip = new AnimationClip { name = "Delayed gate input" };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "Dance"), AnimationCurve.Linear(0, 0, 20, 1));
            AssetDatabase.AddObjectToAsset(clip, controller); neutral.motion = clip;
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message,
                Does.Contain("時間で変わる").Or.Contain("FXの重み"));
        }
        [Test]
        public void FxCommandsOnStateMachinesAreNotSilentlySkippedByStateCallbacks()
        {
            Gate().motion = Clip("Menu face", 75); NeutralFxGate();
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("VRC.SDK3.Avatars.Components.VRCPlayableLayerControl")).First(t => t != null);
            var control = controller.layers[controller.layers.Length - 1].stateMachine.AddStateMachineBehaviour(type);
            using (var data = new SerializedObject(control))
            {
                var layer = data.FindProperty("layer");
                layer.enumValueIndex = Array.IndexOf(layer.enumNames, "FX");
                data.FindProperty("goalWeight").floatValue = 1;
                data.FindProperty("blendDuration").floatValue = 0;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message,
                Does.Contain("Dance gate").And.Contain("影響範囲"));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void LocomotionControlDoesNotBlockFixedFacialSampling(bool disabled, bool otherPlayable)
        {
            var face = Gate(); face.motion = Clip("Menu face", 75);
            var state = face;
            if (otherPlayable)
            {
                var other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Other.controller");
                state = State(other.layers[0].stateMachine, "Locomotion control"); other.layers[0].stateMachine.defaultState = state;
                metadata.OtherControllers.Add(other);
            }
            var control = SdkBehaviour(state, "VRC.SDK3.Avatars.Components.VRCAnimatorLocomotionControl");
            using (var data = new SerializedObject(control))
            {
                data.FindProperty("disableLocomotion").boolValue = disabled;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            Assert.That(VrChatParameterDriver.IsLocomotionControl(control), Is.True);
            Assert.That(Sample().Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
        }

        [Test]
        public void LocomotionControlDoesNotHideOtherPlayableParameterWrites()
        {
            Driver(Gate(), Op("Set", "Face", 1)); FaceLayer();
            var other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Other.controller");
            other.AddParameter("Face", AnimatorControllerParameterType.Int);
            var state = State(other.layers[0].stateMachine, "Locomotion plus face writer"); other.layers[0].stateMachine.defaultState = state;
            SdkBehaviour(state, "VRC.SDK3.Avatars.Components.VRCAnimatorLocomotionControl");
            Driver(state, Op("Set", "Face", 2)); metadata.OtherControllers.Add(other);
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message,
                Does.Contain("FX以外").And.Contain("Face"));
        }
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void TemporaryPoseSpaceDoesNotBlockFixedFacialSampling(bool enter, bool fixedDelay)
        {
            var face = Gate(); face.motion = Clip("Menu face", 75);
            var control = SdkBehaviour(face, "VRC.SDK3.Avatars.Components.VRCAnimatorTemporaryPoseSpace");
            using (var data = new SerializedObject(control))
            {
                data.FindProperty("enterPoseSpace").boolValue = enter;
                data.FindProperty("fixedDelay").boolValue = fixedDelay;
                data.FindProperty("delayTime").floatValue = .5f;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            Assert.That(VrChatParameterDriver.IsTemporaryPoseSpace(control), Is.True);
            Assert.That(Sample().Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
        }

        [TestCase(-1)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        public void MalformedTemporaryPoseSpaceIsNotIgnored(float delay)
        {
            var face = Gate(); face.motion = Clip("Menu face", 75);
            var control = SdkBehaviour(face, "VRC.SDK3.Avatars.Components.VRCAnimatorTemporaryPoseSpace");
            using (var data = new SerializedObject(control))
            {
                data.FindProperty("delayTime").floatValue = delay;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            Assert.That(VrChatParameterDriver.IsTemporaryPoseSpace(control), Is.False);
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message,
                Does.Contain("VRCAnimatorTemporaryPoseSpace").And.Contain("影響範囲"));
        }
        [TestCase(-.1f, 0)]
        [TestCase(1.1f, 0)]
        [TestCase(float.NaN, 0)]
        [TestCase(float.PositiveInfinity, 0)]
        [TestCase(.5f, -1)]
        [TestCase(.5f, float.NaN)]
        [TestCase(.5f, float.PositiveInfinity)]
        public void MalformedBodyPlayableControlIsNotIgnored(float weight, float duration)
        {
            var gate = Gate(); gate.motion = Clip("Menu face", 75);
            var control = PlayableControl(gate, "Action", weight, duration);
            Assert.That(VrChatParameterDriver.IsNonFxPlayableControl(control), Is.False);
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message,
                Does.Contain("Select face").And.Contain("VRCPlayableLayerControl"));
        }

        [Test]
        public void UnknownPlayableTargetIsNotIgnored()
        {
            var gate = Gate(); gate.motion = Clip("Menu face", 75);
            var control = PlayableControl(gate, "Action");
            using (var data = new SerializedObject(control))
            {
                data.FindProperty("layer").intValue = int.MaxValue;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            Assert.That(VrChatParameterDriver.IsNonFxPlayableControl(control), Is.False);
            Assert.Throws<InvalidOperationException>(() => Sample());
        }

        [TestCase("Action", true)]
        [TestCase("FX", false)]
        public void OtherPlayableControlUsesTheSameFxBoundary(string target, bool supported)
        {
            Gate().motion = Clip("Menu face", 75);
            var other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Other.controller");
            var state = State(other.layers[0].stateMachine, "Other control"); other.layers[0].stateMachine.defaultState = state;
            PlayableControl(state, target); metadata.OtherControllers.Add(other);
            if (supported) Assert.That(Sample().Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
            else Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message,
                Does.Contain("FX以外").And.Contain("Other control").And.Contain("VRCPlayableLayerControl"));
        }

        [Test]
        public void BodyControlDoesNotMakeAnExcludedOnlyLayerLookLikeAFace()
        {
            var gate = Gate(); gate.motion = Clip("Menu face", 75);
            var machine = Layer("Excluded decoration");
            var clip = new AnimationClip { name = "Decoration" };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Decoration", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1));
            AssetDatabase.AddObjectToAsset(clip, controller);
            var state = State(machine, "Decoration", clip); machine.defaultState = state;
            PlayableControl(state, "Action");
            var excluded = VrChatExpressionSampler.FindExcludedLayers(controller, controller, path => path == "Decoration");
            Assert.That(excluded, Does.Contain(controller.layers.Length - 1));
            var values = VrChatExpressionSampler.Sample(avatar, controller, metadata.Defaults,
                new Dictionary<string, float> { ["Menu"] = 1 }, path => path == "Decoration", metadata);
            Assert.That(values.Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
        }

        [Test]
        public void MenuDriverChainReachesAnotherLayerWithoutEditingAnySourceAsset()
        {
            controller.AddParameter("Relay", AnimatorControllerParameterType.Int);
            Driver(Gate(), Op("Set", "Relay", 1));
            var relay = Layer("Relay layer"); var idle = State(relay, "Idle"); relay.defaultState = idle;
            var copy = State(relay, "Copy selection"); Transition(idle, copy, "Relay", 1);
            Driver(copy, Op("Copy", "Face", source: "Relay")); FaceLayer();
            var assets = AssetDatabase.LoadAllAssetsAtPath(folder + "/FX.controller");
            var before = assets.ToDictionary(a => a, a => EditorJsonUtility.ToJson(a));
            var values = Sample();
            Assert.That(values.Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
            foreach (var asset in assets) Assert.That(EditorJsonUtility.ToJson(asset), Is.EqualTo(before[asset]), asset.name);
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(25));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh, Is.SameAs(mesh));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AddRunsOncePerEntryIncludingSelfTransitions(bool selfTransition)
        {
            var gate = Gate(); Driver(gate, Op("Add", "Face", 1));
            if (selfTransition) Transition(gate, gate, "Face", 2, AnimatorConditionMode.Less);
            FaceLayer(selfTransition ? 2 : 1);
            Assert.That(Sample().Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
        }

        [Test]
        public void ShortIntermediateDriverAndResetAreObservedWithoutHoldingMenuParameter()
        {
            var gate = Gate(); Driver(gate, Op("Set", "Face", 1), Op("Set", "Menu", 0));
            Transition(gate, controller.layers[0].stateMachine.defaultState, "Menu", 0);
            FaceLayer();
            Assert.That(Sample().Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LocalOnlyUsesTheEvaluatedInstance(bool local)
        {
            var driver = Driver(Gate(), Op("Set", "Face", 1));
            using (var data = new SerializedObject(driver)) { data.FindProperty("localOnly").boolValue = true; data.ApplyModifiedPropertiesWithoutUndo(); }
            metadata.Defaults["IsLocal"] = local ? 1 : 0;
            FaceLayer();
            Assert.That(Sample().Single(v => v.Shape == "Face size").Weight, Is.EqualTo(local ? 75 : 0).Within(.01));
        }

        [Test]
        public void DriverOperationsUseAuthoredOrderAndCopyConversion()
        {
            controller.AddParameter("Scratch", AnimatorControllerParameterType.Float);
            var copy = Op("Copy", "Face", source: "Scratch"); copy.ConvertRange = true;
            copy.SourceMin = 0; copy.SourceMax = 1; copy.DestinationMin = 0; copy.DestinationMax = 3;
            Driver(Gate(), Op("Set", "Scratch", .5f), copy); FaceLayer();
            Assert.That(Sample().Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
        }

        [Test]
        public void UnrelatedRandomDriverDoesNotBlockTheMenu()
        {
            Driver(Gate(), Op("Set", "Face", 1)); FaceLayer();
            controller.AddParameter("Clothes", AnimatorControllerParameterType.Int);
            var other = Layer("Clothes only"); var idle = State(other, "Random outfit"); other.defaultState = idle;
            Driver(idle, Op("Random", "Clothes"));
            Assert.That(Sample().Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
        }

        [Test]
        public void RelevantRandomReportsStateAndOperationInsteadOfInventingAFace()
        {
            Driver(Gate(), Op("Random", "Face")); FaceLayer();
            var error = Assert.Throws<InvalidOperationException>(() => Sample());
            Assert.That(error.Message, Does.Contain("Select face").And.Contain("Random").And.Contain("Face"));
        }

        [Test]
        public void ExternalInputsAndOtherPlayableWritersAreNotAssumedConstant()
        {
            controller.AddParameter("Input", AnimatorControllerParameterType.Float);
            Driver(Gate(), Op("Copy", "Face", source: "Input")); FaceLayer();
            metadata.ExternalParameters.Add("Input");
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message, Does.Contain("外部入力"));
            metadata.ExternalParameters.Clear();
            var other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Other.controller");
            other.AddParameter("Input", AnimatorControllerParameterType.Float);
            var state = State(other.layers[0].stateMachine, "External writer"); other.layers[0].stateMachine.defaultState = state;
            Driver(state, Op("Set", "Input", 1)); metadata.OtherControllers.Add(other);
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message, Does.Contain("FX以外"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MissingMenuMorphsAreDeferredUntilPreparedMeshIsKnown(bool animated)
        {
            Driver(Gate(), Op("Set", "Face", 1)); FaceLayer(missing: true);
            if (animated)
                foreach (var clip in controller.animationClips)
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.extra_cheek5"), AnimationCurve.Linear(0, 0, 10, 100));
            var entry = new VrChatExpressionMenu.Entry { Name = "Face / Smile" };
            entry.Values.AddRange(Sample(entry.Unevaluated));
            Assert.That(entry.Unevaluated.Single().Shape, Is.EqualTo("extra_cheek5"));
            var menu = new VrChatExpressionMenu.Source(); menu.Entries.Add(entry);
            var clone = Object.Instantiate(avatar);
            try
            {
                var bindings = new PreparedExpressionBindings(clone, menu); bindings.Capture(menu);
                Assert.That(entry.Error, Is.Null); Assert.That(entry.Unevaluated, Is.Empty);
                Assert.That(entry.Values.Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
            }
            finally { Object.DestroyImmediate(clone); }
        }

        [Test]
        public void DriverOnExcludedClothingLayerStillReachesTheFace()
        {
            var gate = Gate(); Driver(gate, Op("Set", "Face", 1)); FaceLayer();
            foreach (var child in controller.layers[0].stateMachine.states)
            {
                var clip = new AnimationClip { name = "Excluded clothing" };
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Clothes", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1));
                AssetDatabase.AddObjectToAsset(clip, controller); child.state.motion = clip;
            }
            var values = VrChatExpressionSampler.Sample(avatar, controller, metadata.Defaults,
                new Dictionary<string, float> { ["Menu"] = 1 }, path => path == "Clothes", metadata);
            Assert.That(values.Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
        }

        [Test]
        public void UnknownBehaviourIsRejectedWithItsLocation()
        {
            var gate = Gate(); Driver(gate, Op("Set", "Face", 1)); FaceLayer();
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("VRC.SDK3.Avatars.Components.VRCAnimatorLayerControl")).FirstOrDefault(t => t != null);
            Assert.That(type, Is.Not.Null); gate.AddStateMachineBehaviour(type);
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message,
                Does.Contain("Select face").And.Contain("VRCAnimatorLayerControl").And.Contain("影響範囲"));
        }

        [Test]
        public void NestedStateMachineEntryAndExitRetainNativeDriverEvents()
        {
            var root = controller.layers[0].stateMachine;
            var idle = State(root, "Idle"); root.defaultState = idle;
            var nested = root.AddStateMachine("Nested face");
            var enter = idle.AddTransition(nested); enter.hasExitTime = false; enter.duration = 0;
            enter.AddCondition(AnimatorConditionMode.Equals, 1, "Menu");
            var step = State(nested, "One frame"); nested.defaultState = step;
            Driver(step, Op("Add", "Face", 1), Op("Set", "Menu", 0));
            var leave = step.AddExitTransition(); leave.hasExitTime = false; leave.duration = 0;
            leave.AddCondition(AnimatorConditionMode.Equals, 0, "Menu");
            root.AddStateMachineTransition(nested, idle);
            FaceLayer();
            Assert.That(Sample().Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
        }

        [Test]
        public void AnotherLayerWritingTheSameMorphMustPassStabilityChecks()
        {
            Driver(Gate(), Op("Set", "Face", 1)); FaceLayer();
            var other = Layer("Conflicting blink");
            var clip = Clip("Moving face", 0);
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Face size"), AnimationCurve.Linear(0, 0, 10, 100));
            other.defaultState = State(other, "Moving face", clip);
            Assert.That(Assert.Throws<InvalidOperationException>(() => Sample()).Message, Does.Contain("時間で変わる"));
        }

        [Test]
        public void SupportedNumbersClampOnlyExpressionParametersAndFloorCopiedIntegers()
        {
            var types = new Dictionary<string, AnimatorControllerParameterType>
            {
                ["Synced"] = AnimatorControllerParameterType.Float, ["Local"] = AnimatorControllerParameterType.Float,
                ["Int"] = AnimatorControllerParameterType.Int, ["Bool"] = AnimatorControllerParameterType.Bool
            };
            var values = types.Keys.ToDictionary(n => n, _ => 0.0);
            var program = new VrChatParameterDriver.Program { Location = "Test" };
            program.Operations.AddRange(new[] { Op("Set", "Synced", 4), Op("Set", "Local", -1.2f), Op("Copy", "Int", source: "Local"), Op("Copy", "Bool", source: "Local") });
            VrChatParameterDriver.Execute(program, types, new HashSet<string> { "Synced" }, new HashSet<string>(types.Keys), true, n => values[n], (n, v) => values[n] = v);
            Assert.That(values["Synced"], Is.EqualTo(1)); Assert.That(values["Local"], Is.EqualTo(-1.2).Within(.0001));
            Assert.That(values["Int"], Is.EqualTo(-2)); Assert.That(values["Bool"], Is.EqualTo(1));
        }
    }
}
