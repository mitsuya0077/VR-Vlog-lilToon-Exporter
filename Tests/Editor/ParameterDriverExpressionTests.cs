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
