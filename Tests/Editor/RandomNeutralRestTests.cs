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
    public sealed class RandomNeutralRestTests
    {
        string folder;
        GameObject avatar;
        Mesh mesh;
        SkinnedMeshRenderer skin;
        AnimatorController controller;
        AnimatorState randomState;
        AnimationClip randomClip;
        StateMachineBehaviour randomDriver;

        static EditorCurveBinding Binding(string shape) =>
            EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape." + shape);

        [SetUp]
        public void SetUp()
        {
            var descriptorType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly =>
                assembly.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor")).FirstOrDefault(type => type != null);
            if (descriptorType == null) Assert.Ignore("Install the real VRChat SDK.");
            var name = "__RandomNeutral_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            controller.AddParameter("Choice", AnimatorControllerParameterType.Bool);
            avatar = new GameObject("Prepared appearance", typeof(Animator));
            var body = new GameObject("Body", typeof(SkinnedMeshRenderer)); body.transform.SetParent(avatar.transform, false);
            skin = body.GetComponent<SkinnedMeshRenderer>();
            mesh = new Mesh { name = "Synthetic random rest" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }; mesh.triangles = new[] { 0, 1, 2 };
            foreach (var shape in new[] { "Transient", "Rest", "Unwritten" })
                mesh.AddBlendShapeFrame(shape, 100, Enumerable.Repeat(Vector3.up * .01f, 3).ToArray(), null, null);
            skin.sharedMesh = mesh; skin.SetBlendShapeWeight(0, 17); skin.SetBlendShapeWeight(1, 35); skin.SetBlendShapeWeight(2, 27);
            var descriptor = avatar.AddComponent(descriptorType);
            using (var data = new SerializedObject(descriptor))
            {
                data.FindProperty("customizeAnimationLayers").boolValue = true;
                var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = 1;
                var layer = layers.GetArrayElementAtIndex(0); var type = layer.FindPropertyRelative("type");
                type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
                layer.FindPropertyRelative("isDefault").boolValue = false;
                layer.FindPropertyRelative("animatorController").objectReferenceValue = controller;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            var rest = Clip("Authored fixed rest", ("Rest", AnimationCurve.Constant(0, 1, 63)),
                ("Transient", AnimationCurve.Constant(0, 1, 0)));
            State(0, rest);
            controller.AddLayer("Self-contained random idle");
            var definitions = controller.layers; definitions[1].defaultWeight = 1; controller.layers = definitions;
            randomClip = Clip("Live idle phase", ("Transient", AnimationCurve.Linear(0, 1, 1, 80)));
            randomState = State(1, randomClip);
            randomDriver = ParameterDriverExpressionTests.Driver(randomState, ParameterDriverExpressionTests.Op("Random", "Choice"));
            RandomField("valueMin", 0); RandomField("valueMax", 1); RandomField("chance", .5f);
            var other = State(1, Clip("Other idle phase", ("Transient", AnimationCurve.Linear(0, 80, 1, 1))));
            var enter = randomState.AddTransition(other); enter.hasExitTime = true; enter.exitTime = 1; enter.duration = 0;
            enter.AddCondition(AnimatorConditionMode.If, 0, "Choice");
            var repeat = other.AddTransition(randomState); repeat.hasExitTime = true; repeat.exitTime = 1; repeat.duration = 0;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(avatar); Object.DestroyImmediate(mesh);
            if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        AnimationClip Clip(string name, params (string Shape, AnimationCurve Curve)[] curves)
        {
            var clip = new AnimationClip { name = name };
            foreach (var curve in curves) AnimationUtility.SetEditorCurve(clip, Binding(curve.Shape), curve.Curve);
            AssetDatabase.AddObjectToAsset(clip, controller); return clip;
        }

        AnimatorState State(int layer, AnimationClip clip)
        {
            var machine = controller.layers[layer].stateMachine;
            var state = machine.AddState("State " + machine.states.Length); state.motion = clip; state.writeDefaultValues = false;
            if (machine.states.Length == 1) machine.defaultState = state;
            return state;
        }

        void RandomField(string name, float value)
        {
            using var data = new SerializedObject(randomDriver);
            data.FindProperty("parameters").GetArrayElementAtIndex(0).FindPropertyRelative(name).floatValue = value;
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        void ReplaceOperations(params VrChatParameterDriver.Operation[] operations)
        {
            randomState.behaviours = Array.Empty<StateMachineBehaviour>();
            randomDriver = ParameterDriverExpressionTests.Driver(randomState, operations);
            RandomField("valueMin", 0); RandomField("valueMax", 1); RandomField("chance", .5f);
        }

        InvalidOperationException AssertFatal()
        {
            var before = ExportSourceFingerprint.Compute(avatar); var warnings = new List<string>();
            var error = Assert.Catch<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar, warnings: warnings));
            Assert.That(VrChatParameterDriver.IsRandomCapability(error), Is.False,
                "Malformed data and unknown effects must never be mislabeled as a recoverable Random capability.");
            Assert.That(warnings.Any(value => value.Contains("Random")), Is.False);
            Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before));
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17));
            Assert.That(skin.GetBlendShapeWeight(1), Is.EqualTo(35));
            Assert.That(skin.GetBlendShapeWeight(2), Is.EqualTo(27));
            return error;
        }

        [Test]
        public void IsolatedRandomIdleKeepsPreparedChannelsAndIndependentFixedRest()
        {
            var before = EditorJsonUtility.ToJson(controller); var clipBefore = EditorJsonUtility.ToJson(randomClip);
            var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings, requiredMorphs: new[] { Binding("Transient") });
            Assert.That(values.Select(value => value.Shape), Is.EquivalentTo(new[] { "Rest" }));
            Assert.That(values.Single().Weight, Is.EqualTo(63).Within(.001));
            NeutralShapeSnapshot.Apply(avatar, values);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17));
            Assert.That(skin.GetBlendShapeWeight(1), Is.EqualTo(63).Within(.001));
            Assert.That(skin.GetBlendShapeWeight(2), Is.EqualTo(27));
            Assert.That(warnings.Any(value => value.Contains("時間で変わる") && value.Contains("blendShape.Transient")), Is.True);
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
            Assert.That(EditorJsonUtility.ToJson(randomClip), Is.EqualTo(clipBefore));
            Assert.That(warnings.Any(value => value.Contains("Random")), Is.True, string.Join("\n", warnings));
        }

        [TestCase("WriteDefaults")]
        [TestCase("Fractional")]
        [TestCase("Additive")]
        [TestCase("NonMorph")]
        [TestCase("AnimatorCurve")]
        [TestCase("EscapingRead")]
        [TestCase("EscapingCopy")]
        [TestCase("OtherPlayableRead")]
        public void ValidRandomCapabilityKeepsTheCompletePreparedDependencyComponent(string kind)
        {
            if (kind == "WriteDefaults") randomState.writeDefaultValues = true;
            if (kind == "Fractional" || kind == "Additive")
            {
                var layers = controller.layers;
                if (kind == "Fractional") layers[1].defaultWeight = .5f;
                else layers[1].blendingMode = AnimatorLayerBlendingMode.Additive;
                controller.layers = layers;
            }
            if (kind == "NonMorph") AnimationUtility.SetEditorCurve(randomClip,
                EditorCurveBinding.FloatCurve("Body", typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Constant(0, 1, 1));
            if (kind == "AnimatorCurve") AnimationUtility.SetEditorCurve(randomClip,
                EditorCurveBinding.FloatCurve("", typeof(Animator), "Choice"), AnimationCurve.Constant(0, 1, 1));
            if (kind == "EscapingRead" || kind == "OtherPlayableRead")
            {
                var target = kind == "EscapingRead" ? controller : AnimatorController.CreateAnimatorControllerAtPath(folder + "/Other.controller");
                if (target != controller) target.AddParameter("Choice", AnimatorControllerParameterType.Bool);
                var machine = target.layers[0].stateMachine;
                if (machine.defaultState == null) machine.defaultState = machine.AddState("Idle");
                var next = machine.AddState("Parameter consumer");
                var transition = machine.defaultState.AddTransition(next); transition.hasExitTime = false;
                transition.AddCondition(AnimatorConditionMode.If, 0, "Choice");
                if (target != controller)
                {
                    var descriptor = avatar.GetComponents<Component>().First(value => value.GetType().Name == "VRCAvatarDescriptor");
                    using var data = new SerializedObject(descriptor);
                    var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = 2;
                    var layer = layers.GetArrayElementAtIndex(1); var type = layer.FindPropertyRelative("type");
                    type.enumValueIndex = Array.IndexOf(type.enumNames, "Action");
                    layer.FindPropertyRelative("isDefault").boolValue = false;
                    layer.FindPropertyRelative("animatorController").objectReferenceValue = target;
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            if (kind == "EscapingCopy")
            {
                controller.AddParameter("Alias", AnimatorControllerParameterType.Bool);
                ParameterDriverExpressionTests.Driver(controller.layers[0].stateMachine.defaultState,
                    ParameterDriverExpressionTests.Op("Copy", "Alias", source: "Choice"));
            }
            var before = ExportSourceFingerprint.Compute(avatar);
            var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values, Is.Empty, "A valid unresolved Random cannot authorize a sampled phase or a partial value from this dependent component.");
            Assert.That(warnings.Any(value => value.Contains("Random")), Is.True, string.Join("\n", warnings));
            NeutralShapeSnapshot.Apply(avatar, values);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17));
            Assert.That(skin.GetBlendShapeWeight(1), Is.EqualTo(35));
            Assert.That(skin.GetBlendShapeWeight(2), Is.EqualTo(27));
            Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before));
        }

        [TestCase("Full")]
        [TestCase("Fractional")]
        [TestCase("Additive")]
        public void OnlyAPermanentFullOverrideKeepsItsAuthoredConstantAboveTheRandomIdle(string kind)
        {
            controller.AddLayer("Permanent upper rest");
            var layers = controller.layers;
            layers[2].defaultWeight = kind == "Fractional" ? .5f : 1;
            layers[2].blendingMode = kind == "Additive" ? AnimatorLayerBlendingMode.Additive : AnimatorLayerBlendingMode.Override;
            controller.layers = layers;
            var clip = Clip("Upper rest", ("Transient", AnimationCurve.Constant(0, 1, 41)));
            if (kind == "Additive")
                AnimationUtility.SetAdditiveReferencePose(clip, Clip("Zero reference", ("Transient", AnimationCurve.Constant(0, 1, 0))), 0);
            State(2, clip);
            var values = NeutralShapeSampler.Sample(avatar);
            Assert.That(values.Any(value => value.Shape == "Transient"), Is.EqualTo(kind == "Full"));
            if (kind == "Full") Assert.That(values.Single(value => value.Shape == "Transient").Weight, Is.EqualTo(41).Within(.001));
            Assert.That(values.Any(value => value.Shape == "Rest"), Is.True);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17));
        }

        [Test]
        public void AnOverlappingUpperGraphRetainsPreparedRestWhenItsNativeDependencyIsRandom()
        {
            controller.AddParameter("Upper selection", AnimatorControllerParameterType.Bool);
            controller.AddLayer("Upper selection graph");
            var layers = controller.layers; layers[2].defaultWeight = 1; controller.layers = layers;
            var state = State(2, Clip("Initial upper constant", ("Transient", AnimationCurve.Constant(0, 1, 49))));
            var alternate = State(2, Clip("Alternate upper constant", ("Transient", AnimationCurve.Constant(0, 1, 70))));
            var transition = state.AddTransition(alternate); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.If, 0, "Upper selection");
            var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values, Is.Empty);
            Assert.That(warnings.Any(value => value.Contains("Random")), Is.True, string.Join("\n", warnings));
            NeutralShapeSnapshot.Apply(avatar, values);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17));
            Assert.That(skin.GetBlendShapeWeight(1), Is.EqualTo(35));
        }

        [Test]
        public void AnUnreachableRandomBranchDoesNotReplaceTheAuthoredConstantRest()
        {
            controller.AddParameter("Enable idle", AnimatorControllerParameterType.Bool);
            var idle = State(1, Clip("Selected constant rest", ("Transient", AnimationCurve.Constant(0, 1, 49))));
            controller.layers[1].stateMachine.defaultState = idle;
            var transition = idle.AddTransition(randomState); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.If, 0, "Enable idle");
            var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values.Single(value => value.Shape == "Transient").Weight, Is.EqualTo(49).Within(.001));
            Assert.That(values.Single(value => value.Shape == "Rest").Weight, Is.EqualTo(63).Within(.001));
            Assert.That(warnings.Any(value => value.Contains("時間で変わる")), Is.False);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17));
        }

        [Test]
        public void ExplicitFixedExpressionStillRejectsRelevantRandomness()
        {
            var metadata = VrChatExpressionMenu.Read(avatar, new VrChatMenuImportPolicy { SkipAll = true });
            var context = FixedExpressionContext.Create(controller, metadata.Defaults, metadata);
            var dependencies = ExpressionDependencies.AnalyzeNeutral(controller, new HashSet<EditorCurveBinding> { Binding("Transient") },
                null, metadata, fixedContext: context, preserveCommittedMorphs: true);
            Assert.Catch<InvalidOperationException>(() => VrChatExpressionSampler.SampleNeutral(avatar, controller,
                dependencies, metadata, null, context));
        }

        [Test]
        public void ASeparateDeterministicComponentStillReconstructsWhenTheRandomComponentFallsBack()
        {
            var machine = controller.layers[0].stateMachine;
            var next = machine.AddState("Consumer of unresolved choice"); next.motion = machine.defaultState.motion; next.writeDefaultValues = false;
            var transition = machine.defaultState.AddTransition(next); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.If, 0, "Choice");
            controller.AddLayer("Independent fixed component");
            var layers = controller.layers; layers[2].defaultWeight = 1; controller.layers = layers;
            State(2, Clip("Independent authored fixed value", ("Unwritten", AnimationCurve.Constant(0, 1, 88))));
            var warnings = new List<string>(); var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values.Select(value => value.Shape), Is.EqualTo(new[] { "Unwritten" }));
            Assert.That(values.Single().Weight, Is.EqualTo(88).Within(.001));
            Assert.That(warnings.Any(value => value.Contains("Random") && value.Contains("Transient")), Is.True);
            NeutralShapeSnapshot.Apply(avatar, values);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17));
            Assert.That(skin.GetBlendShapeWeight(1), Is.EqualTo(35));
            Assert.That(skin.GetBlendShapeWeight(2), Is.EqualTo(88).Within(.001));
        }

        [TestCase("non-finite minimum")]
        [TestCase("non-finite maximum")]
        [TestCase("non-finite chance")]
        [TestCase("reversed range")]
        [TestCase("negative chance")]
        [TestCase("chance above one")]
        public void MalformedRandomNumbersCannotUsePreparedRestFallback(string kind)
        {
            if (kind == "non-finite minimum") RandomField("valueMin", float.NaN);
            else if (kind == "non-finite maximum") RandomField("valueMax", float.PositiveInfinity);
            else if (kind == "non-finite chance") RandomField("chance", float.NaN);
            else if (kind == "reversed range") RandomField("valueMin", 2);
            else if (kind == "negative chance") RandomField("chance", -.1f);
            else RandomField("chance", 1.1f);
            var operation = VrChatParameterDriver.Read(randomDriver, "Invalid Random data").Operations.Single();
            Assert.That(operation.UnresolvedRandom, Is.False); Assert.That(operation.Error, Is.Not.Null);
            Assert.That(AssertFatal().Message, Does.Contain("Random"));
        }

        [TestCase("empty")]
        [TestCase("missing")]
        [TestCase("Trigger")]
        [TestCase("built-in")]
        public void InvalidRandomDestinationsRemainFatal(string kind)
        {
            if (kind == "empty")
            {
                using var data = new SerializedObject(randomDriver);
                data.FindProperty("parameters").GetArrayElementAtIndex(0).FindPropertyRelative("name").stringValue = "";
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            else if (kind == "missing") controller.parameters = controller.parameters.Where(value => value.name != "Choice").ToArray();
            else if (kind == "Trigger") controller.parameters = new[] {
                new AnimatorControllerParameter { name = "Choice", type = AnimatorControllerParameterType.Trigger }
            };
            else
            {
                controller.AddParameter("Voice", AnimatorControllerParameterType.Float);
                using var data = new SerializedObject(randomDriver);
                data.FindProperty("parameters").GetArrayElementAtIndex(0).FindPropertyRelative("name").stringValue = "Voice";
                data.ApplyModifiedPropertiesWithoutUndo();
                foreach (var transition in randomState.transitions)
                    transition.conditions = transition.conditions.Select(condition => new AnimatorCondition {
                        parameter = condition.parameter == "Choice" ? "Voice" : condition.parameter,
                        mode = condition.parameter == "Choice" ? AnimatorConditionMode.Greater : condition.mode,
                        threshold = condition.parameter == "Choice" ? .5f : condition.threshold
                    }).ToArray();
            }
            AssertFatal();
        }

        [TestCase("non-finite Set")]
        [TestCase("empty Copy source")]
        [TestCase("zero Copy range")]
        [TestCase("missing Copy source")]
        [TestCase("Trigger Copy source")]
        [TestCase("Bool Add")]
        public void AValidRandomOperationCannotConcealALaterMalformedNeededOperation(string kind)
        {
            VrChatParameterDriver.Operation invalid;
            if (kind == "non-finite Set") invalid = ParameterDriverExpressionTests.Op("Set", "Choice", float.NaN);
            else if (kind == "Bool Add") invalid = ParameterDriverExpressionTests.Op("Add", "Choice", 1);
            else
            {
                var source = kind == "empty Copy source" ? "" : kind == "missing Copy source" ? "Missing source" :
                    kind == "Trigger Copy source" ? "Trigger source" : "Choice";
                if (kind == "Trigger Copy source") controller.AddParameter(source, AnimatorControllerParameterType.Trigger);
                invalid = ParameterDriverExpressionTests.Op("Copy", "Choice", source: source);
                if (kind == "zero Copy range") { invalid.ConvertRange = true; invalid.SourceMin = 1; invalid.SourceMax = 1; }
            }
            ReplaceOperations(ParameterDriverExpressionTests.Op("Random", "Choice"), invalid);
            var error = AssertFatal();
            Assert.That(error.Message, Does.Contain(kind == "Bool Add" ? "Add" : kind == "non-finite Set" ? "Set" : "Copy"));
        }

        [TestCase("AnimationEvent")]
        [TestCase("unknown behaviour")]
        [TestCase("corrupt curve")]
        public void PreparedRandomRestDoesNotHideCorruptDataOrUnknownEffects(string kind)
        {
            if (kind == "AnimationEvent") AnimationUtility.SetAnimationEvents(randomClip,
                new[] { new AnimationEvent { functionName = "UnsupportedEvent" } });
            else if (kind == "unknown behaviour")
            {
                var unknown = randomState.AddStateMachineBehaviour<UnknownStateCallbackProbe>();
                Assert.That(unknown, Is.Not.Null);
                unknown.Parameter = "Choice";
                Assert.That(randomState.behaviours.Contains(unknown), Is.True,
                    "The unknown behavior must be attached before testing the fallback guard.");
            }
            else AnimationUtility.SetEditorCurve(randomClip, Binding("Transient"), new AnimationCurve(new Keyframe(0, float.NaN)));
            var error = AssertFatal();
            if (kind == "unknown behaviour") Assert.That(error.Message, Does.Contain(nameof(UnknownStateCallbackProbe)));
        }
    }
}
