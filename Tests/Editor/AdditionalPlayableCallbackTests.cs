using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class AdditionalPlayableCallbackTests
    {
        private string folder;
        private GameObject avatar;
        private Mesh mesh;
        private Component descriptor;
        private SkinnedMeshRenderer skin;
        private AnimatorController fx, action;
        private AnimatorState opening, waiting, command;
        private AnimatorStateTransition gate;
        private VrChatExpressionMenu.Source source;
        private static readonly EditorCurveBinding OpenBinding = EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Open");

        [SetUp]
        public void SetUp()
        {
            var descriptorType = SdkType("VRCAvatarDescriptor");
            if (descriptorType == null || SdkType("VRCAnimatorLayerControl") == null || SdkType("VRCPlayableLayerControl") == null)
                Assert.Ignore("Install the real VRChat SDK.");
            var name = "__AdditionalPlayableCallbacks_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            fx = Controller("FX"); action = Controller("Additional");
            fx.AddParameter("Menu", AnimatorControllerParameterType.Int);
            opening = State(fx.layers[0].stateMachine, "Opening", Morph(75)); fx.layers[0].stateMachine.defaultState = opening;
            var selected = State(fx.layers[0].stateMachine, "Selected", Morph(100));
            Transition(opening, selected).AddCondition(AnimatorConditionMode.Equals, 1, "Menu");
            fx.AddLayer("Unrelated native support");
            var layers = fx.layers; layers[1].defaultWeight = 1; fx.layers = layers;
            var support = State(fx.layers[1].stateMachine, "Native", Empty()); fx.layers[1].stateMachine.defaultState = support;
            // Normal external semantics deliberately disagree with the authored
            // Animator default, proving that no accidental default-only prune
            // makes the dormant branch disappear.
            action.AddParameter(new AnimatorControllerParameter { name = "AFK", type = AnimatorControllerParameterType.Bool, defaultBool = true });
            waiting = State(action.layers[0].stateMachine, "Waiting", Empty()); action.layers[0].stateMachine.defaultState = waiting;
            command = State(action.layers[0].stateMachine, "Command", Empty());
            gate = Transition(waiting, command); gate.AddCondition(AnimatorConditionMode.If, 0, "AFK");
            source = new VrChatExpressionMenu.Source { Controller = fx };
            source.Defaults["IsLocal"] = 1; source.Defaults["Menu"] = 0;
            source.OtherControllers.Add(action);
            avatar = new GameObject("Avatar", typeof(Animator));
            var body = new GameObject("Body", typeof(SkinnedMeshRenderer)); body.transform.SetParent(avatar.transform, false);
            skin = body.GetComponent<SkinnedMeshRenderer>();
            mesh = new Mesh { name = "Prepared appearance" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }; mesh.triangles = new[] { 0, 1, 2 };
            mesh.AddBlendShapeFrame("Open", 100, Enumerable.Repeat(Vector3.up, 3).ToArray(), null, null);
            skin.sharedMesh = mesh; skin.SetBlendShapeWeight(0, 35);
            descriptor = avatar.AddComponent(descriptorType);
            SetDescriptorControllers();
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

        private AnimatorController Controller(string name) => AnimatorController.CreateAnimatorControllerAtPath(folder + "/" + name + ".controller");

        private AnimationClip Empty()
        {
            var clip = new AnimationClip { name = "Empty" }; AssetDatabase.AddObjectToAsset(clip, fx); return clip;
        }

        private AnimationClip Morph(float weight)
        {
            var clip = new AnimationClip { name = "Open " + weight };
            AnimationUtility.SetEditorCurve(clip, OpenBinding, AnimationCurve.Constant(0, 1, weight));
            AssetDatabase.AddObjectToAsset(clip, fx); return clip;
        }

        private static AnimatorState State(AnimatorStateMachine machine, string name, Motion motion = null)
        {
            var state = machine.AddState(name); state.motion = motion; state.writeDefaultValues = false; return state;
        }

        private static AnimatorStateTransition Transition(AnimatorState from, AnimatorState to)
        {
            var transition = from.AddTransition(to); transition.hasExitTime = false; transition.duration = 0; return transition;
        }

        private void SetDescriptorControllers()
        {
            using (var data = new SerializedObject(descriptor))
            {
                data.FindProperty("customizeAnimationLayers").boolValue = true;
                var controllers = new[] { fx }.Concat(source.OtherControllers).ToArray();
                var kinds = new[] { "FX", "Action", "Gesture", "Additive" };
                var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = controllers.Length;
                for (var i = 0; i < controllers.Length; i++)
                {
                    var item = layers.GetArrayElementAtIndex(i); var type = item.FindPropertyRelative("type");
                    type.enumValueIndex = Array.IndexOf(type.enumNames, kinds[i]);
                    item.FindPropertyRelative("isDefault").boolValue = false;
                    item.FindPropertyRelative("animatorController").objectReferenceValue = controllers[i];
                }
                data.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static StateMachineBehaviour AddControl(AnimatorState state, string kind = "animator", float duration = .1f, int index = 1)
        {
            return Configure(state.AddStateMachineBehaviour(SdkType(kind == "animator" ? "VRCAnimatorLayerControl" : "VRCPlayableLayerControl")), kind, duration, index);
        }

        private static StateMachineBehaviour Configure(StateMachineBehaviour control, string kind, float duration, int index)
        {
            using (var data = new SerializedObject(control))
            {
                var target = data.FindProperty(kind == "animator" ? "playable" : "layer");
                Assert.That(Array.IndexOf(target.enumNames, "FX"), Is.GreaterThanOrEqualTo(0));
                target.enumValueIndex = Array.IndexOf(target.enumNames, "FX");
                if (kind == "animator") data.FindProperty("layer").intValue = index;
                data.FindProperty("goalWeight").floatValue = 0; data.FindProperty("blendDuration").floatValue = duration;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            return control;
        }

        private ExpressionDependencies Analyze(IDictionary<string, float> selection = null, bool fixedInputs = true)
        {
            var context = fixedInputs ? FixedExpressionContext.Create(fx, source.Defaults, source) : null;
            if (selection == null)
                return ExpressionDependencies.AnalyzeNeutral(fx, new HashSet<EditorCurveBinding> { OpenBinding }, null, source, fixedContext: context);
            return ExpressionDependencies.Analyze(fx, selection.Keys, null, source, source.Defaults, selection,
                initialMorphs: new[] { OpenBinding }, preserveNativeBasePose: true, fixedContext: context);
        }

        private void AssertAdditionalRejected(string behaviour = "VRCAnimatorLayerControl", IDictionary<string, float> selection = null,
            bool fixedInputs = true, string hardReason = null)
        {
            SetDescriptorControllers();
            var originals = new Object[] { fx, action, skin, mesh }.Concat(source.OtherControllers).Distinct().ToArray();
            var before = originals.Select(value => EditorJsonUtility.ToJson(value)).ToArray();
            if (hardReason == nameof(UnknownStateCallbackProbe))
            {
                var error = Assert.Throws<InvalidOperationException>(() => Analyze(selection, fixedInputs));
                Assert.That(error.Message, Does.Contain("FX以外").And.Contain(hardReason));
                Assert.That(Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar)).Message, Does.Contain(hardReason));
            }
            else if (selection != null)
            {
                var error = Assert.Throws<InvalidOperationException>(() => Analyze(selection, fixedInputs));
                Assert.That(error.Message, Does.Contain(behaviour));
            }
            else
            {
                var error = Assert.Throws<NeutralShapeSamplingException>(() => Analyze(selection, fixedInputs));
                Assert.That(error.Message, Does.Contain(behaviour).And.Contain("重み制御"));
                if (fixedInputs)
                {
                    if (hardReason != null)
                        Assert.That(Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar)).Message, Does.Contain(hardReason));
                    else
                    {
                        var warnings = new List<string>();
                        var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
                        Assert.That(values, Is.Empty);
                        NeutralShapeSnapshot.Apply(avatar, values);
                        Assert.That(warnings.Any(value => value.Contains("Open") && value.Contains(behaviour) && value.Contains("重み制御")), Is.True);
                    }
                }
            }
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(35));
            Assert.That(skin.sharedMesh, Is.SameAs(mesh));
            for (var i = 0; i < originals.Length; i++) Assert.That(EditorJsonUtility.ToJson(originals[i]), Is.EqualTo(before[i]));
        }

        [TestCase("animator", .1f, 1)]
        [TestCase("animator", 0f, 0)]
        [TestCase("playable", 0f, 0)]
        [TestCase("playable", 2f, 0)]
        public void DormantSdkFxWeightCommandsPermitNeutralAndFixedSelectionWithoutSourceMutation(string kind, float duration, int index)
        {
            command.motion = Morph(0);
            var control = AddControl(command, kind, duration, index);
            var beforeFx = EditorJsonUtility.ToJson(fx); var beforeOther = EditorJsonUtility.ToJson(action);
            var beforeControl = EditorJsonUtility.ToJson(control); var beforeGate = EditorJsonUtility.ToJson(gate);
            Assert.That(Analyze().Morphs.Contains(OpenBinding), Is.True);
            Assert.That(Analyze(new Dictionary<string, float> { ["Menu"] = 1 }).Morphs.Contains(OpenBinding), Is.True);
            Assert.That(NeutralShapeSampler.Sample(avatar).Single(value => value.Shape == "Open").Weight, Is.EqualTo(75).Within(.01));
            var values = VrChatExpressionSampler.SampleFixed(avatar, fx, source.Defaults, new Dictionary<string, float> { ["Menu"] = 1 }, null, source);
            Assert.That(values.Single(value => value.Shape == "Open").Weight, Is.EqualTo(100).Within(.01));
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(35));
            Assert.That(EditorJsonUtility.ToJson(fx), Is.EqualTo(beforeFx)); Assert.That(EditorJsonUtility.ToJson(action), Is.EqualTo(beforeOther));
            Assert.That(EditorJsonUtility.ToJson(control), Is.EqualTo(beforeControl)); Assert.That(EditorJsonUtility.ToJson(gate), Is.EqualTo(beforeGate));
            Assert.That(action.parameters.Single(parameter => parameter.name == "AFK").defaultBool, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DormantNestedFxWeightCommandsAndTheirExitRestoresArePruned(bool onMachine)
        {
            var root = action.layers[0].stateMachine;
            root.RemoveState(command); waiting.RemoveTransition(gate);
            var nested = root.AddStateMachine("Nested additional branch");
            var first = State(nested, "Start", Morph(0)); nested.defaultState = first;
            var last = State(nested, "End", Empty()); Transition(first, last);
            var exit = last.AddExitTransition(); exit.hasExitTime = false; exit.duration = 0;
            var enter = waiting.AddTransition(first); enter.hasExitTime = false; enter.duration = 0;
            enter.AddCondition(AnimatorConditionMode.If, 0, "AFK");
            if (onMachine) Configure(nested.AddStateMachineBehaviour(SdkType("VRCAnimatorLayerControl")), "animator", .1f, 1);
            else AddControl(first);
            var restore = State(root, "Exit restore", Empty()); AddControl(restore);
            root.AddStateMachineTransition(nested, restore);
            Assert.That(Analyze().Morphs.Contains(OpenBinding), Is.True);
            Assert.That(NeutralShapeSampler.Sample(avatar).Single(value => value.Shape == "Open").Weight, Is.EqualTo(75).Within(.01));
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(35));
        }

        [TestCase("default")]
        [TestCase("opposite")]
        [TestCase("ancestor")]
        [TestCase("entry")]
        [TestCase("anyState")]
        [TestCase("unconditional")]
        [TestCase("timed")]
        public void ReachableSdkFxWeightCommandsRemainUnsupported(string route)
        {
            AddControl(command);
            var machine = action.layers[0].stateMachine;
            switch (route)
            {
                case "default": machine.defaultState = command; break;
                case "opposite": gate.conditions = Array.Empty<AnimatorCondition>(); gate.AddCondition(AnimatorConditionMode.IfNot, 0, "AFK"); break;
                case "ancestor": Configure(machine.AddStateMachineBehaviour(SdkType("VRCAnimatorLayerControl")), "animator", .1f, 1); break;
                case "entry": machine.AddEntryTransition(command); break;
                case "anyState": machine.AddAnyStateTransition(command); break;
                case "unconditional": Transition(waiting, command); break;
                case "timed": var timed = Transition(waiting, command); timed.hasExitTime = true; timed.exitTime = .5f; break;
            }
            AssertAdditionalRejected();
        }

        [TestCase("animator", 0f)]
        [TestCase("animator", 2f)]
        [TestCase("playable", 0f)]
        [TestCase("playable", 2f)]
        public void ReachableInstantAndTimedSdkCommandsAreNotExecutedOrIgnored(string kind, float duration)
        {
            AddControl(command, kind, duration); action.layers[0].stateMachine.defaultState = command;
            AssertAdditionalRejected(kind == "animator" ? "VRCAnimatorLayerControl" : "VRCPlayableLayerControl");
        }

        [TestCase("fxDriver")]
        [TestCase("fxCurve")]
        [TestCase("otherDriver")]
        [TestCase("otherCurve")]
        [TestCase("thirdDriver")]
        [TestCase("fxRandom")]
        [TestCase("otherRandom")]
        public void EveryRawParameterWriterPreventsDormantCommandProof(string writer)
        {
            AddControl(command); AnimatorState state;
            if (writer.StartsWith("fx", StringComparison.Ordinal)) { fx.AddParameter("AFK", AnimatorControllerParameterType.Bool); state = opening; }
            else if (writer == "thirdDriver")
            {
                var third = Controller("Third"); third.AddParameter("AFK", AnimatorControllerParameterType.Bool);
                state = State(third.layers[0].stateMachine, "Writer", Empty()); third.layers[0].stateMachine.defaultState = state; source.OtherControllers.Add(third);
            }
            else state = command; // Even a writer inside the apparently dormant branch invalidates a circular constant proof.
            if (writer.EndsWith("Curve", StringComparison.Ordinal))
                AnimationUtility.SetEditorCurve((AnimationClip)state.motion, EditorCurveBinding.FloatCurve("", typeof(Animator), "AFK"), AnimationCurve.Constant(0, 1, 1));
            else ParameterDriverExpressionTests.Driver(state, ParameterDriverExpressionTests.Op(writer.EndsWith("Random", StringComparison.Ordinal) ? "Random" : "Set", "AFK", 1));
            AssertAdditionalRejected(hardReason: writer.EndsWith("Curve", StringComparison.Ordinal) ? null : "Parameter Driver");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ConflictingThirdPlayableParameterTypeCannotCertifyAConstant(bool declaredInFx)
        {
            AddControl(command);
            if (declaredInFx) fx.AddParameter("AFK", AnimatorControllerParameterType.Bool);
            var third = Controller("Third"); third.AddParameter("AFK", AnimatorControllerParameterType.Float);
            var state = State(third.layers[0].stateMachine, "Idle", Empty()); third.layers[0].stateMachine.defaultState = state; source.OtherControllers.Add(third);
            AssertAdditionalRejected();
        }

        [Test]
        public void ExplicitChangedNormalInputCannotPruneAnAdditionalCommand()
        {
            AddControl(command); fx.AddParameter("AFK", AnimatorControllerParameterType.Bool);
            AssertAdditionalRejected(selection: new Dictionary<string, float> { ["Menu"] = 1, ["AFK"] = 1 });
        }

        [Test]
        public void LegacySamplingWithoutNormalInputContextRetainsDormantCommands()
        {
            AddControl(command); AssertAdditionalRejected(fixedInputs: false);
        }

        [TestCase("dormant")]
        [TestCase("third")]
        public void GenuineUnknownCallbacksInvalidateTheWholeAdditionalProof(string location)
        {
            AddControl(command);
            if (location == "dormant") command.AddStateMachineBehaviour<UnknownStateCallbackProbe>();
            else
            {
                var third = Controller("Third"); var state = State(third.layers[0].stateMachine, "Unknown", Empty());
                third.layers[0].stateMachine.defaultState = state; state.AddStateMachineBehaviour<UnknownStateCallbackProbe>(); source.OtherControllers.Add(third);
            }
            // A valid earlier weight command must not hide a genuinely unknown
            // callback in this or another controller behind neutral fallback.
            AssertAdditionalRejected(hardReason: nameof(UnknownStateCallbackProbe));
        }

        [TestCase("negativeIndex")]
        [TestCase("outOfRangeIndex")]
        [TestCase("unknownTarget")]
        [TestCase("bodyTarget")]
        [TestCase("negativeWeight")]
        [TestCase("largeWeight")]
        [TestCase("nanWeight")]
        [TestCase("infiniteWeight")]
        [TestCase("negativeDuration")]
        [TestCase("nanDuration")]
        [TestCase("infiniteDuration")]
        public void AnimatorLayerMetadataDistinguishesValidBodyTargetsFromMalformedCommands(string problem)
        {
            var control = AddControl(command);
            using (var data = new SerializedObject(control))
            {
                switch (problem)
                {
                    case "negativeIndex": data.FindProperty("layer").intValue = -1; break;
                    case "outOfRangeIndex": data.FindProperty("layer").intValue = fx.layers.Length; break;
                    case "unknownTarget": data.FindProperty("playable").intValue = int.MaxValue; break;
                    case "bodyTarget": var target = data.FindProperty("playable"); target.enumValueIndex = Array.IndexOf(target.enumNames, "Action");
                        data.FindProperty("layer").intValue = 0; break;
                    case "negativeWeight": data.FindProperty("goalWeight").floatValue = -.1f; break;
                    case "largeWeight": data.FindProperty("goalWeight").floatValue = 1.1f; break;
                    case "nanWeight": data.FindProperty("goalWeight").floatValue = float.NaN; break;
                    case "infiniteWeight": data.FindProperty("goalWeight").floatValue = float.PositiveInfinity; break;
                    case "negativeDuration": data.FindProperty("blendDuration").floatValue = -1; break;
                    case "nanDuration": data.FindProperty("blendDuration").floatValue = float.NaN; break;
                    case "infiniteDuration": data.FindProperty("blendDuration").floatValue = float.PositiveInfinity; break;
                }
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            var beforeControl = EditorJsonUtility.ToJson(control);
            var beforeFx = EditorJsonUtility.ToJson(fx); var beforeOther = EditorJsonUtility.ToJson(action);
            if (problem == "bodyTarget")
            {
                Assert.That(Analyze().Morphs.Contains(OpenBinding), Is.True);
                var warnings = new List<string>();
                Assert.That(NeutralShapeSampler.Sample(avatar, warnings: warnings).Single().Weight, Is.EqualTo(75).Within(.01));
                Assert.That(warnings, Is.Empty);
            }
            else
            {
                var error = Assert.Throws<InvalidOperationException>(() => Analyze());
                Assert.That(error.Message, Does.Contain("FXのレイヤー制御に不正な設定があります").And.Contain("VRCAnimatorLayerControl").And.Contain("Command"));
                Assert.That(Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar)).Message,
                    Does.Contain("FXのレイヤー制御に不正な設定があります"));
            }
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(35));
            Assert.That(skin.sharedMesh, Is.SameAs(mesh));
            Assert.That(EditorJsonUtility.ToJson(control), Is.EqualTo(beforeControl));
            Assert.That(EditorJsonUtility.ToJson(fx), Is.EqualTo(beforeFx));
            Assert.That(EditorJsonUtility.ToJson(action), Is.EqualTo(beforeOther));
        }

        [Test]
        public void ReachableAdditionalMorphWriterKeepsPreparedNeutralDespiteKnownWeightCommand()
        {
            AddControl(command); command.motion = Morph(0); action.layers[0].stateMachine.defaultState = command;
            var before = EditorJsonUtility.ToJson(skin);
            Assert.That(Assert.Throws<NeutralShapeSamplingException>(() => Analyze()).Message, Does.Contain("FX以外").And.Contain("blendShape.Open"));
            var warnings = new List<string>();
            Assert.That(NeutralShapeSampler.Sample(avatar, warnings: warnings), Is.Empty);
            Assert.That(warnings.Any(value => value.Contains("FX以外") && value.Contains("blendShape.Open")), Is.True);
            Assert.That(Assert.Throws<InvalidOperationException>(() => Analyze(new Dictionary<string, float> { ["Menu"] = 1 })).Message,
                Does.Contain("FX以外").And.Contain("blendShape.Open"));
            Assert.That(EditorJsonUtility.ToJson(skin), Is.EqualTo(before));
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(35));
        }

        StateMachineBehaviour AdditionalRandomProgram(VrChatParameterDriver.Operation later = null)
        {
            AddControl(command); action.layers[0].stateMachine.defaultState = command;
            action.AddParameter("Lottery", AnimatorControllerParameterType.Float);
            Transition(command, waiting).AddCondition(AnimatorConditionMode.Greater, .5f, "Lottery");
            var operations = new List<VrChatParameterDriver.Operation> { ParameterDriverExpressionTests.Op("Random", "Lottery") };
            if (later != null) operations.Add(later);
            var driver = ParameterDriverExpressionTests.Driver(command, operations.ToArray());
            using (var data = new SerializedObject(driver))
            {
                foreach (var index in Enumerable.Range(0, operations.Count).Where(index => operations[index].Kind == "Random"))
                {
                    var item = data.FindProperty("parameters").GetArrayElementAtIndex(index);
                    item.FindPropertyRelative("valueMin").floatValue = 0; item.FindPropertyRelative("valueMax").floatValue = 1;
                    item.FindPropertyRelative("chance").floatValue = .5f;
                }
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            SetDescriptorControllers(); return driver;
        }

        [Test]
        public void ValidAdditionalRandomCanRetainPreparedRestForAKnownWeightCapability()
        {
            AdditionalRandomProgram(); var originals = new Object[] { fx, action, skin, mesh }; var before = originals.Select(value => EditorJsonUtility.ToJson(value)).ToArray();
            var warnings = new List<string>(); Assert.That(NeutralShapeSampler.Sample(avatar, warnings: warnings), Is.Empty);
            Assert.That(warnings.Any(value => value.Contains("VRCAnimatorLayerControl")), Is.True);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(35));
            for (var index = 0; index < originals.Length; index++) Assert.That(EditorJsonUtility.ToJson(originals[index]), Is.EqualTo(before[index]));
        }

        [TestCase("Random minimum")]
        [TestCase("Random maximum")]
        [TestCase("Random chance")]
        [TestCase("Random reversed range")]
        [TestCase("missing Copy source")]
        [TestCase("Trigger Copy source")]
        [TestCase("Bool Add")]
        [TestCase("non-finite Set")]
        public void AdditionalWeightCapabilityCannotHideALaterMalformedNeededDriver(string kind)
        {
            var boolean = kind == "Bool Add";
            action.AddParameter("Needed", boolean ? AnimatorControllerParameterType.Bool : AnimatorControllerParameterType.Float);
            Transition(command, waiting).AddCondition(boolean ? AnimatorConditionMode.If : AnimatorConditionMode.Greater, boolean ? 0 : .5f, "Needed");
            VrChatParameterDriver.Operation invalid;
            if (kind.StartsWith("Random", StringComparison.Ordinal)) invalid = ParameterDriverExpressionTests.Op("Random", "Needed");
            else if (kind == "Bool Add") invalid = ParameterDriverExpressionTests.Op("Add", "Needed", 1);
            else if (kind == "non-finite Set") invalid = ParameterDriverExpressionTests.Op("Set", "Needed", float.NaN);
            else
            {
                var copySource = kind == "Trigger Copy source" ? "Trigger source" : "Missing source";
                if (kind == "Trigger Copy source") action.AddParameter(copySource, AnimatorControllerParameterType.Trigger);
                invalid = ParameterDriverExpressionTests.Op("Copy", "Needed", source: copySource);
            }
            var driver = AdditionalRandomProgram(invalid);
            if (kind.StartsWith("Random", StringComparison.Ordinal))
            {
                using var data = new SerializedObject(driver); var item = data.FindProperty("parameters").GetArrayElementAtIndex(1);
                if (kind == "Random minimum") item.FindPropertyRelative("valueMin").floatValue = float.NaN;
                else if (kind == "Random maximum") item.FindPropertyRelative("valueMax").floatValue = float.PositiveInfinity;
                else if (kind == "Random chance") item.FindPropertyRelative("chance").floatValue = 1.1f;
                else { item.FindPropertyRelative("valueMin").floatValue = 2; item.FindPropertyRelative("valueMax").floatValue = 1; }
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            Assert.That(Assert.Throws<NeutralShapeSamplingException>(() => Analyze()).Message, Does.Contain("VRCAnimatorLayerControl"),
                "The known weight capability is reached before neutral data preflight.");
            var originals = new Object[] { fx, action, skin, mesh, driver }; var before = originals.Select(value => EditorJsonUtility.ToJson(value)).ToArray();
            var error = Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar));
            Assert.That(error.Message, Does.Contain("Additional Playable").And.Contain(invalid.Kind));
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(35));
            for (var index = 0; index < originals.Length; index++) Assert.That(EditorJsonUtility.ToJson(originals[index]), Is.EqualTo(before[index]));
        }

        [TestCase(AnimatorControllerParameterType.Trigger)]
        [TestCase(AnimatorControllerParameterType.Float)]
        public void UnprovedTypedGateInputsDoNotHideCommands(AnimatorControllerParameterType type)
        {
            AddControl(command); action.RemoveParameter(0);
            action.AddParameter(type == AnimatorControllerParameterType.Float ? "Unmeasured input" : "AFK", type);
            if (type == AnimatorControllerParameterType.Float)
            {
                gate.conditions = Array.Empty<AnimatorCondition>(); gate.AddCondition(AnimatorConditionMode.Greater, 1, "Unmeasured input");
            }
            AssertAdditionalRejected();
        }
    }
}
