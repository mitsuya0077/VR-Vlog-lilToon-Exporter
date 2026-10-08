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
    public sealed class AdditionalParameterConstantTests
    {
        private const string Axis = "ProceduralAxis";
        private static readonly EditorCurveBinding OpenBinding = EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Open");
        private string folder;
        private GameObject avatar;
        private Mesh mesh;
        private SkinnedMeshRenderer skin;
        private Component descriptor;
        private AnimatorController fx, additional;
        private AnimatorState neutral, reset, tracking;
        private StateMachineBehaviour driver;
        private VrChatExpressionMenu.Source source;

        [SetUp]
        public void SetUp()
        {
            var descriptorType = SdkType("VRCAvatarDescriptor");
            if (descriptorType == null || SdkType("VRCAnimatorTrackingControl") == null) Assert.Ignore("Install the real VRChat SDK.");
            var name = "__AdditionalParameterConstants_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            fx = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            additional = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Gesture.controller");
            fx.AddParameter(Axis, AnimatorControllerParameterType.Float); additional.AddParameter(Axis, AnimatorControllerParameterType.Float);
            fx.AddParameter("Menu", AnimatorControllerParameterType.Int);
            neutral = State(fx.layers[0].stateMachine, "Neutral", Clip("Face75", 75));
            fx.layers[0].stateMachine.defaultState = neutral;
            var selected = State(fx.layers[0].stateMachine, "Selected", Clip("Face100", 100));
            Transition(neutral, selected).AddCondition(AnimatorConditionMode.Equals, 1, "Menu");
            fx.AddLayer("Tracking only"); var layers = fx.layers; layers[1].defaultWeight = 1; fx.layers = layers;
            tracking = State(layers[1].stateMachine, "Tracking", Clip("Empty")); layers[1].stateMachine.defaultState = tracking;
            var off = State(layers[1].stateMachine, "Animation", Clip("Empty2"));
            Transition(tracking, off).AddCondition(AnimatorConditionMode.Greater, .5f, Axis);
            Tracking(tracking, "Tracking"); Tracking(off, "Animation");
            reset = State(additional.layers[0].stateMachine, "Reset", Clip("Additional empty"));
            additional.layers[0].stateMachine.defaultState = reset;
            driver = ParameterDriverExpressionTests.Driver(reset, ParameterDriverExpressionTests.Op("Set", Axis));
            source = new VrChatExpressionMenu.Source { Controller = fx };
            source.OtherControllers.Add(additional); source.Defaults["Menu"] = 0; source.Defaults[Axis] = 0;
            avatar = new GameObject("Avatar", typeof(Animator));
            var body = new GameObject("Body", typeof(SkinnedMeshRenderer)); body.transform.SetParent(avatar.transform, false);
            skin = body.GetComponent<SkinnedMeshRenderer>(); mesh = new Mesh { name = "Prepared face" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }; mesh.triangles = new[] { 0, 1, 2 };
            mesh.AddBlendShapeFrame("Open", 100, Enumerable.Repeat(Vector3.up, 3).ToArray(), null, null);
            skin.sharedMesh = mesh; skin.SetBlendShapeWeight(0, 35);
            descriptor = avatar.AddComponent(descriptorType); SetControllers();
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

        private AnimationClip Clip(string name, float? value = null)
        {
            var clip = new AnimationClip { name = name };
            if (value.HasValue) AnimationUtility.SetEditorCurve(clip, OpenBinding, AnimationCurve.Constant(0, 1, value.Value));
            AssetDatabase.AddObjectToAsset(clip, fx); return clip;
        }

        private static AnimatorState State(AnimatorStateMachine machine, string name, Motion motion)
        {
            var state = machine.AddState(name); state.motion = motion; state.writeDefaultValues = false; return state;
        }

        private static AnimatorStateTransition Transition(AnimatorState from, AnimatorState to)
        {
            var transition = from.AddTransition(to); transition.hasExitTime = false; transition.duration = 0; return transition;
        }

        private static void Tracking(AnimatorState state, string mode)
        {
            var behaviour = state.AddStateMachineBehaviour(SdkType("VRCAnimatorTrackingControl"));
            using var data = new SerializedObject(behaviour);
            var eyes = data.FindProperty("trackingEyes"); eyes.enumValueIndex = Array.IndexOf(eyes.enumNames, mode);
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        private void SetControllers()
        {
            using var data = new SerializedObject(descriptor);
            data.FindProperty("customizeAnimationLayers").boolValue = true;
            var runtimes = new[] { fx }.Concat(source.OtherControllers).ToArray();
            var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = runtimes.Length;
            for (var index = 0; index < runtimes.Length; index++)
            {
                var item = layers.GetArrayElementAtIndex(index); var type = item.FindPropertyRelative("type");
                type.enumValueIndex = Array.IndexOf(type.enumNames, index == 0 ? "FX" : index == 1 ? "Gesture" : "Action");
                item.FindPropertyRelative("isDefault").boolValue = false;
                item.FindPropertyRelative("animatorController").objectReferenceValue = runtimes[index];
            }
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        private FixedExpressionContext Context() => FixedExpressionContext.Create(fx, source.Defaults, source);
        private ExpressionDependencies Dependencies(IDictionary<string, float> selected = null) => selected == null
            ? ExpressionDependencies.AnalyzeNeutral(fx, new HashSet<EditorCurveBinding> { OpenBinding }, null, source,
                fixedContext: Context(), preserveCommittedMorphs: true)
            : ExpressionDependencies.Analyze(fx, selected.Keys, null, source, source.Defaults, selected,
                new[] { OpenBinding }, fixedContext: Context());

        private List<VrChatExpressionMenu.MorphValue> Selected(IDictionary<string, float> selected = null) =>
            VrChatExpressionSampler.SampleFixed(avatar, fx, source.Defaults, selected ?? new Dictionary<string, float> { ["Menu"] = 1 },
                null, source, fixedContext: Context());

        private void SetParameter(AnimatorController controller, AnimatorControllerParameterType type, float value)
        {
            var parameters = controller.parameters; var parameter = parameters.Single(item => item.name == Axis);
            parameter.type = type; parameter.defaultFloat = value; parameter.defaultInt = (int)value; parameter.defaultBool = value != 0;
            controller.parameters = parameters;
        }

        private void SetDriverValue(float value)
        {
            using var data = new SerializedObject(driver);
            data.FindProperty("parameters").GetArrayElementAtIndex(0).FindPropertyRelative("value").floatValue = value;
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        // Independent original controller evaluation, without the dependency
        // clone/proof under test. The additional SDK Set assigns this same
        // typed value, so executing it cannot change this reference stream.
        private float Native(bool selected)
        {
            var clone = Object.Instantiate(avatar); var animator = clone.GetComponent<Animator>();
            animator.runtimeAnimatorController = null; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.fireEvents = false;
            var graph = PlayableGraph.Create("Original additional reset reference"); graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            try
            {
                var playable = AnimatorControllerPlayable.Create(graph, fx);
                AnimationPlayableOutput.Create(graph, "Original", animator).SetSourcePlayable(playable);
                foreach (var parameter in fx.parameters)
                {
                    var value = source.Defaults.TryGetValue(parameter.name, out var supplied) ? supplied :
                        parameter.type == AnimatorControllerParameterType.Bool ? (parameter.defaultBool ? 1 : 0) :
                        parameter.type == AnimatorControllerParameterType.Int ? parameter.defaultInt : parameter.defaultFloat;
                    if (parameter.type == AnimatorControllerParameterType.Bool) playable.SetBool(parameter.name, value != 0);
                    else if (parameter.type == AnimatorControllerParameterType.Int) playable.SetInteger(parameter.name, (int)value);
                    else playable.SetFloat(parameter.name, value);
                }
                graph.Play(); graph.Evaluate(0);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60);
                if (selected) playable.SetInteger("Menu", 1);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60);
                return clone.transform.Find("Body").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(0);
            }
            finally { graph.Destroy(); Object.DestroyImmediate(clone); }
        }

        [TestCase(AnimatorControllerParameterType.Float, 0f)]
        [TestCase(AnimatorControllerParameterType.Float, .25f)]
        [TestCase(AnimatorControllerParameterType.Bool, 1f)]
        [TestCase(AnimatorControllerParameterType.Int, 2f)]
        public void IdempotentAdditionalResetsKeepTheOriginalNativeNeutralAndSelection(AnimatorControllerParameterType type, float initial)
        {
            SetParameter(fx, type, initial); SetParameter(additional, type, initial); source.Defaults[Axis] = initial; SetDriverValue(initial);
            var transition = tracking.transitions.Single(); transition.conditions = Array.Empty<AnimatorCondition>();
            transition.AddCondition(type == AnimatorControllerParameterType.Bool ? AnimatorConditionMode.IfNot :
                type == AnimatorControllerParameterType.Int ? AnimatorConditionMode.Equals : AnimatorConditionMode.Greater,
                type == AnimatorControllerParameterType.Int ? 99 : 10, Axis);
            var before = new Object[] { fx, additional, driver, skin, mesh }.Select(value => EditorJsonUtility.ToJson(value)).ToArray();
            var nativeNeutral = Native(false); var nativeSelected = Native(true);
            Assert.That(nativeNeutral, Is.EqualTo(75).Within(.01)); Assert.That(nativeSelected, Is.EqualTo(100).Within(.01));
            Assert.That(NeutralShapeSampler.Sample(avatar).Single(value => value.Shape == "Open").Weight, Is.EqualTo(nativeNeutral).Within(.01));
            Assert.That(Selected().Single(value => value.Shape == "Open").Weight, Is.EqualTo(nativeSelected).Within(.01));
            var dependencies = Dependencies();
            Assert.That(dependencies.NeutralFixedValues[Axis], Is.EqualTo(initial));
            Assert.That(dependencies.Layers.Contains(1), Is.True, "Retain the native empty/tracking support layer.");
            var after = new Object[] { fx, additional, driver, skin, mesh }.Select(value => EditorJsonUtility.ToJson(value)).ToArray();
            Assert.That(after, Is.EqualTo(before)); Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(35));
        }

        [TestCase("changed Set")]
        [TestCase("third writer")]
        [TestCase("ancestor writer")]
        [TestCase("FX writer")]
        [TestCase("Add")]
        [TestCase("Copy")]
        [TestCase("Random")]
        [TestCase("constant Animator curve")]
        [TestCase("moving Animator curve")]
        [TestCase("override Animator curve")]
        [TestCase("nonfinite Set")]
        [TestCase("conflicting type")]
        [TestCase("conflicting default")]
        [TestCase("conflicting expression type")]
        [TestCase("missing FX declaration")]
        [TestCase("Trigger")]
        [TestCase("unknown")]
        [TestCase("unknown ancestor")]
        [TestCase("event")]
        [TestCase("circular gate")]
        public void AResetCannotCertifyAChangingOrAmbiguousProducer(string kind)
        {
            if (kind == "changed Set") SetDriverValue(1);
            else if (kind == "FX writer") ParameterDriverExpressionTests.Driver(neutral, ParameterDriverExpressionTests.Op("Set", Axis, 1));
            else if (kind == "ancestor writer")
            {
                var ancestor = additional.layers[0].stateMachine.AddStateMachineBehaviour(driver.GetType());
                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(driver), ancestor);
                using var data = new SerializedObject(ancestor);
                data.FindProperty("parameters").GetArrayElementAtIndex(0).FindPropertyRelative("value").floatValue = 1;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            else if (kind == "third writer" || kind == "circular gate")
            {
                var third = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Third.controller"); third.AddParameter(Axis, AnimatorControllerParameterType.Float);
                var start = State(third.layers[0].stateMachine, "Initial", Clip("Third empty")); third.layers[0].stateMachine.defaultState = start;
                var writer = kind == "third writer" ? start : State(third.layers[0].stateMachine, "Possible", Clip("Third possible"));
                if (kind == "circular gate") Transition(start, writer).AddCondition(AnimatorConditionMode.Greater, .5f, Axis);
                ParameterDriverExpressionTests.Driver(writer, ParameterDriverExpressionTests.Op("Set", Axis, 1)); source.OtherControllers.Add(third);
            }
            else if (kind == "Add" || kind == "Copy" || kind == "Random")
            {
                if (kind == "Copy") additional.AddParameter("CopySource", AnimatorControllerParameterType.Float);
                // Keep the harmless reset and add another producer. The proof
                // must inspect both programs rather than accepting the first.
                driver = ParameterDriverExpressionTests.Driver(reset,
                    ParameterDriverExpressionTests.Op(kind, Axis, source: kind == "Copy" ? "CopySource" : null));
            }
            else if (kind == "override Animator curve")
            {
                var replacement = Clip("Actual parameter writer");
                AnimationUtility.SetEditorCurve(replacement, EditorCurveBinding.FloatCurve("", typeof(Animator), Axis), AnimationCurve.Constant(0, 1, 0));
                var runtime = new AnimatorOverrideController(additional); runtime[(AnimationClip)reset.motion] = replacement;
                AssetDatabase.CreateAsset(runtime, folder + "/Effective.overrideController"); source.OtherControllers[0] = runtime;
            }
            else if (kind.EndsWith("Animator curve", StringComparison.Ordinal))
                AnimationUtility.SetEditorCurve((AnimationClip)reset.motion, EditorCurveBinding.FloatCurve("", typeof(Animator), Axis),
                    kind.StartsWith("moving", StringComparison.Ordinal) ? AnimationCurve.Linear(0, 0, 10, 1) : AnimationCurve.Constant(0, 1, 0));
            else if (kind == "nonfinite Set")
            {
                SetDriverValue(float.NaN);
                Assert.That(VrChatParameterDriver.Read(driver, reset.name).Operations.Single().Error, Is.Not.Null);
            }
            else if (kind == "conflicting type") SetParameter(additional, AnimatorControllerParameterType.Bool, 0);
            else if (kind == "conflicting default") SetParameter(additional, AnimatorControllerParameterType.Float, .5f);
            else if (kind == "conflicting expression type") source.ExpressionParameterTypes[Axis] = "Int";
            else if (kind == "missing FX declaration") fx.parameters = fx.parameters.Where(parameter => parameter.name != Axis).ToArray();
            else if (kind == "Trigger") SetParameter(additional, AnimatorControllerParameterType.Trigger, 0);
            else if (kind == "unknown") reset.AddStateMachineBehaviour<UnknownStateCallbackProbe>();
            else if (kind == "unknown ancestor") additional.layers[0].stateMachine.AddStateMachineBehaviour<UnknownStateCallbackProbe>();
            else if (kind == "event") AnimationUtility.SetAnimationEvents((AnimationClip)reset.motion,
                new[] { new AnimationEvent { time = .1f, functionName = "UnmodelledProducer" } });
            SetControllers();
            Assert.That(AdditionalParameterConstants.Prove(fx, source, Context()), Is.Empty);
            Assert.Throws<InvalidOperationException>(() => Dependencies());
            Assert.Throws<InvalidOperationException>(() => Selected());
            // The manually supplied expression type is checked by this source
            // context; the descriptor has no expression-parameter asset.
            if (kind != "conflicting expression type") Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar));
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(35));
        }

        [Test]
        public void SelectedDifferentInputCannotBeErasedByAnAdditionalReset()
        {
            var selected = new Dictionary<string, float> { ["Menu"] = 1, [Axis] = 1 };
            Assert.That(AdditionalParameterConstants.Prove(fx, source, Context(), source.Defaults, selected), Is.Empty);
            Assert.That(Assert.Throws<InvalidOperationException>(() => Selected(selected)).Message, Does.Contain(Axis));
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(35));
        }

        [Test]
        public void AnIdempotentResetCannotConcealAnAdditionalFaceWriter()
        {
            reset.motion = Clip("Additional face", 0);
            Assert.That(AdditionalParameterConstants.Prove(fx, source, Context()).ContainsKey(Axis), Is.True);
            Assert.That(Assert.Throws<InvalidOperationException>(() => Dependencies()).Message, Does.Contain("blendShape.Open"));
            Assert.That(Assert.Throws<InvalidOperationException>(() => Selected()).Message, Does.Contain("blendShape.Open"));
            Assert.That(Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar)).Message, Does.Contain("blendShape.Open"));
        }

        [TestCase("source", false)]
        [TestCase("source", true)]
        [TestCase("probe", false)]
        [TestCase("probe", true)]
        public void SuppliedInitialValuesAlsoRequireAnUnchangedReset(string location, bool matching)
        {
            var supplied = new Dictionary<string, float>(source.Defaults) { [Axis] = .25f };
            if (location == "source") source.Defaults[Axis] = .25f;
            SetDriverValue(matching ? .25f : 0);
            var defaults = location == "source" ? source.Defaults : supplied;
            var constants = AdditionalParameterConstants.Prove(fx, source, Context(), defaults, new Dictionary<string, float> { ["Menu"] = 1 });
            if (matching)
            {
                Assert.That(constants[Axis], Is.EqualTo(.25f));
                var values = VrChatExpressionSampler.SampleFixed(avatar, fx, defaults, new Dictionary<string, float> { ["Menu"] = 1 },
                    null, source, fixedContext: Context());
                Assert.That(values.Single(value => value.Shape == "Open").Weight, Is.EqualTo(100).Within(.01));
            }
            else
            {
                Assert.That(constants, Is.Empty);
                Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleFixed(avatar, fx, defaults,
                    new Dictionary<string, float> { ["Menu"] = 1 }, null, source, fixedContext: Context()));
            }
        }

        [TestCase("nonfinite Set")]
        [TestCase("missing Copy source")]
        [TestCase("Bool Add")]
        public void AnIdempotentResetDoesNotAuthorizeALaterMalformedNeededOperation(string kind)
        {
            var boolean = kind == "Bool Add";
            var type = boolean ? AnimatorControllerParameterType.Bool : AnimatorControllerParameterType.Float;
            fx.AddParameter("Needed", type); additional.AddParameter("Needed", type);
            var possible = State(fx.layers[0].stateMachine, "Needed branch", Clip("Needed face", 25));
            Transition(neutral, possible).AddCondition(boolean ? AnimatorConditionMode.If : AnimatorConditionMode.Greater, .5f, "Needed");
            // Preserve the valid first callback; malformed needed operations
            // in a later callback must still prevent sampling.
            driver = ParameterDriverExpressionTests.Driver(reset, ParameterDriverExpressionTests.Op("Set", Axis),
                ParameterDriverExpressionTests.Op(kind == "Bool Add" ? "Add" : kind == "missing Copy source" ? "Copy" : "Set", "Needed",
                    kind == "nonfinite Set" ? float.NaN : 1, kind == "missing Copy source" ? "NotDeclared" : null));
            Assert.That(AdditionalParameterConstants.Prove(fx, source, Context()).ContainsKey(Axis), Is.True);
            Assert.Throws<InvalidOperationException>(() => Selected());
            Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar));
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(35));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OnlyAnIndependentUnchangedInputCanPruneAConflictingProducer(bool selectedGate)
        {
            fx.AddParameter("FaceMode", AnimatorControllerParameterType.Int); additional.AddParameter("FaceMode", AnimatorControllerParameterType.Int);
            var other = State(additional.layers[0].stateMachine, "Different option", Clip("Option empty"));
            Transition(reset, other).AddCondition(AnimatorConditionMode.Equals, 1, "FaceMode");
            ParameterDriverExpressionTests.Driver(other, ParameterDriverExpressionTests.Op("Set", Axis, 1));
            var selected = new Dictionary<string, float> { ["Menu"] = 1, ["FaceMode"] = selectedGate ? 1 : 0 };
            if (selectedGate) Assert.Throws<InvalidOperationException>(() => Selected(selected));
            else Assert.That(Selected(selected).Single(value => value.Shape == "Open").Weight, Is.EqualTo(Native(true)).Within(.01));
            // Appearance ownership still covers authored menu alternatives.
            source.MenuInputs.Add("FaceMode");
            var alternate = State(fx.layers[0].stateMachine, "Alternative appearance", Clip("Alternative", 25));
            Transition(neutral, alternate).AddCondition(AnimatorConditionMode.Greater, .5f, Axis);
            Assert.That(ExpressionDependencies.NormalInputLayers(fx, source, Context(), null)[0].Clips.Contains((AnimationClip)alternate.motion), Is.True);
        }

        [TestCase(AnimatorControllerParameterType.Bool, 1f, 2f)]
        [TestCase(AnimatorControllerParameterType.Int, 2f, 2.9f)]
        [TestCase(AnimatorControllerParameterType.Float, 1f, 2f)]
        public void IdempotenceUsesTheValidatedSdkTypedAndExpressionParameterValue(AnimatorControllerParameterType type, float initial, float set)
        {
            SetParameter(fx, type, initial); SetParameter(additional, type, initial); source.Defaults[Axis] = initial; SetDriverValue(set);
            source.ExpressionParameters.Add(Axis);
            var constants = AdditionalParameterConstants.Prove(fx, source, Context());
            Assert.That(constants[Axis], Is.EqualTo(initial));
            var types = new Dictionary<string, AnimatorControllerParameterType> { [Axis] = type };
            var actual = (double)initial; var program = VrChatParameterDriver.Read(driver, reset.name);
            VrChatParameterDriver.ValidateTargets(program, types, new HashSet<string> { Axis });
            VrChatParameterDriver.Execute(program, types, source.ExpressionParameters, new HashSet<string> { Axis }, true,
                _ => actual, (_, value) => actual = value);
            Assert.That(actual, Is.EqualTo(initial), "Compare against the actual SDK-data adapter semantics.");
        }
    }
}
