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
    public sealed class HeldAutomaticExpressionTests
    {
        string folder;
        GameObject avatar;
        Mesh mesh;
        AnimatorController controller;
        AnimatorState selected, condition, closed;
        AnimationClip faceClip, closeClip;
        StateMachineBehaviour random;
        VrChatExpressionMenu.Source source;
        static EditorCurveBinding Binding(string shape) => EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape." + shape);
        static readonly HashSet<EditorCurveBinding> Automatic = new HashSet<EditorCurveBinding> { Binding("Blink") };
        static readonly HashSet<EditorCurveBinding> Selected = new HashSet<EditorCurveBinding> { Binding("Authored"), Binding("Blink") };

        [SetUp]
        public void SetUp()
        {
            var name = "__HeldAutomatic_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            controller.AddParameter("Enable", AnimatorControllerParameterType.Bool);
            var parameters = controller.parameters; parameters.Single(parameter => parameter.name == "Enable").defaultBool = true; controller.parameters = parameters;
            controller.AddParameter("Lottery", AnimatorControllerParameterType.Float);
            avatar = new GameObject("Authored held expression", typeof(Animator));
            var skin = new GameObject("Body", typeof(SkinnedMeshRenderer)).GetComponent<SkinnedMeshRenderer>(); skin.transform.SetParent(avatar.transform, false);
            mesh = new Mesh { name = "Independent automatic test" }; mesh.vertices = new[] { Vector3.zero, Vector3.up, Vector3.right }; mesh.triangles = new[] { 0, 1, 2 };
            foreach (var shape in new[] { "Authored", "Blink", "Rest" }) mesh.AddBlendShapeFrame(shape, 100, Enumerable.Repeat(Vector3.up * .01f, 3).ToArray(), null, null);
            skin.sharedMesh = mesh;
            State(0, "Permanent underlay", Clip("Permanent", ("Rest", 12)));
            AddLayer("Selected face"); faceClip = Clip("Authored selected face", ("Authored", 75), ("Blink", 0)); selected = State(1, "Selected", faceClip);
            ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "Enable", 1));
            AddLayer("Automatic blink"); condition = State(2, "Condition", Clip("Empty"));
            random = ParameterDriverExpressionTests.Driver(condition, ParameterDriverExpressionTests.Op("Random", "Lottery"));
            using (var data = new SerializedObject(random))
            {
                var operation = data.FindProperty("parameters").GetArrayElementAtIndex(0);
                operation.FindPropertyRelative("valueMin").floatValue = 0; operation.FindPropertyRelative("valueMax").floatValue = 4;
                operation.FindPropertyRelative("chance").floatValue = .5f; data.ApplyModifiedPropertiesWithoutUndo();
            }
            closeClip = Clip("Blink close and sparse eye resets", ("Authored", 0), ("Blink", 100)); closed = State(2, "Closed", closeClip);
            var open = State(2, "Open", Clip("Blink open", ("Blink", 0)));
            var enter = condition.AddTransition(closed); enter.hasExitTime = true; enter.exitTime = .1f; enter.duration = 0;
            enter.AddCondition(AnimatorConditionMode.If, 0, "Enable"); enter.AddCondition(AnimatorConditionMode.Greater, -1, "Lottery");
            var leave = closed.AddTransition(open); leave.hasExitTime = true; leave.exitTime = .5f; leave.duration = 0;
            source = new VrChatExpressionMenu.Source { Controller = controller };
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(avatar); Object.DestroyImmediate(mesh); if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        void AddLayer(string name)
        {
            controller.AddLayer(name); var layers = controller.layers; layers[layers.Length - 1].defaultWeight = 1; controller.layers = layers;
        }

        AnimationClip Clip(string name, params (string Shape, float Weight)[] weights)
        {
            var clip = new AnimationClip { name = name }; AssetDatabase.AddObjectToAsset(clip, controller);
            foreach (var weight in weights) AnimationUtility.SetEditorCurve(clip, Binding(weight.Shape), AnimationCurve.Constant(0, 1, weight.Weight));
            return clip;
        }

        AnimatorState State(int index, string name, Motion motion)
        {
            var machine = controller.layers[index].stateMachine; var state = machine.AddState(name); state.motion = motion; state.writeDefaultValues = false;
            if (machine.states.Length == 1) machine.defaultState = state; return state;
        }

        HashSet<int> Find() => HeldAutomaticExpressionLayers.Find(controller, source, Automatic, 1, Selected);

        VrChatExpressionMenu.Entry Hold()
        {
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, faceClip, entry);
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1, false, metadata: source, sourceState: selected);
            return entry;
        }

        [Test]
        public void HeldAutomaticComponentKeepsExactAuthoredFaceWithSparseBlinkResets()
        {
            // Independent native witness: the complete live controller blinks,
            // including the eye reset while closed. A held authored expression
            // has a separate explicit policy and retains its open-eye face.
            var clone = Object.Instantiate(avatar); var graph = PlayableGraph.Create("Live automatic oracle");
            try
            {
                var animator = clone.GetComponent<Animator>(); animator.runtimeAnimatorController = null; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual); var playable = AnimatorControllerPlayable.Create(graph, controller);
                AnimationPlayableOutput.Create(graph, "Live", animator).SetSourcePlayable(playable); graph.Play(); graph.Evaluate(0);
                var skin = clone.transform.Find("Body").GetComponent<SkinnedMeshRenderer>();
                for (var frame = 0; frame < 20; frame++) graph.Evaluate(1f / 60f);
                Assert.That(skin.GetBlendShapeWeight(1), Is.EqualTo(100).Within(.01));
                for (var frame = 0; frame < 60; frame++) graph.Evaluate(1f / 60f);
                Assert.That(skin.GetBlendShapeWeight(1), Is.Zero.Within(.01));
            }
            finally { graph.Destroy(); Object.DestroyImmediate(clone); }
            var before = ExportSourceFingerprint.Compute(avatar); var serialized = EditorJsonUtility.ToJson(controller);
            Assert.That(Find(), Is.EquivalentTo(new[] { 2 })); var entry = Hold();
            Assert.That(entry.Values.Single(value => value.Shape == "Authored").Weight, Is.EqualTo(75).Within(.01));
            Assert.That(entry.Values.Single(value => value.Shape == "Blink").Weight, Is.Zero.Within(.01));
            Assert.That(entry.Values.Single(value => value.Shape == "Rest").Weight, Is.EqualTo(12).Within(.01));
            Assert.That(entry.Messages.Any(value => value.Contains("Automatic blink")), Is.True);
            Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before)); Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(serialized));
        }

        [TestCase("varied nonautomatic")]
        [TestCase("sparse nonzero reset")]
        [TestCase("appearance")]
        [TestCase("outer face reader")]
        [TestCase("other playable reader")]
        [TestCase("external destination")]
        [TestCase("additive")]
        [TestCase("weight control")]
        [TestCase("unselected permanent morph")]
        public void AutomaticSuspensionRequiresCompleteIsolation(string kind)
        {
            if (kind == "varied nonautomatic") AnimationUtility.SetEditorCurve(closeClip, Binding("Authored"), AnimationCurve.Linear(0, 0, 1, 50));
            else if (kind == "sparse nonzero reset") AnimationUtility.SetEditorCurve(closeClip, Binding("Authored"), AnimationCurve.Constant(0, 1, 30));
            else if (kind == "appearance") AnimationUtility.SetEditorCurve(closeClip, EditorCurveBinding.FloatCurve("Body", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1));
            else if (kind == "external destination") source.ExternalParameters.Add("Lottery");
            else if (kind == "additive") { var layers = controller.layers; layers[2].blendingMode = AnimatorLayerBlendingMode.Additive; controller.layers = layers; }
            else if (kind == "unselected permanent morph") AnimationUtility.SetEditorCurve(closeClip, Binding("Rest"), AnimationCurve.Constant(0, 1, 0));
            else if (kind == "weight control")
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAnimatorLayerControl")).FirstOrDefault(value => value != null);
                if (type == null) Assert.Ignore("Install the real VRChat SDK.");
                var control = selected.AddStateMachineBehaviour(type);
                using var data = new SerializedObject(control); var playable = data.FindProperty("playable"); playable.enumValueIndex = Array.IndexOf(playable.enumNames, "FX");
                data.FindProperty("layer").intValue = 2; data.FindProperty("goalWeight").floatValue = .5f; data.FindProperty("blendDuration").floatValue = 0; data.ApplyModifiedPropertiesWithoutUndo();
            }
            else
            {
                var target = controller;
                if (kind == "other playable reader") { target = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Other.controller"); target.AddParameter("Lottery", AnimatorControllerParameterType.Float); source.OtherControllers.Add(target); }
                else { AddLayer("Coupled face reader"); }
                var machine = target.layers[kind == "other playable reader" ? 0 : 3].stateMachine;
                var state = machine.AddState("Reader"); state.motion = faceClip; state.writeDefaultValues = false; machine.defaultState = state;
                var next = machine.AddState("Other face"); next.motion = Clip("Other face", ("Authored", 20)); next.writeDefaultValues = false;
                var transition = state.AddTransition(next); transition.hasExitTime = false; transition.duration = 0; transition.AddCondition(AnimatorConditionMode.Greater, 1, "Lottery");
            }
            Assert.That(Find(), Is.Empty);
        }

        [TestCase("malformed Random")]
        [TestCase("later Bool Add")]
        [TestCase("unknown callback")]
        [TestCase("incoming malformed Set")]
        public void AutomaticSuspensionCannotHideMalformedOrUnknownCallbacks(string kind)
        {
            if (kind == "unknown callback") Assert.That(condition.AddStateMachineBehaviour<UnknownStateCallbackProbe>(), Is.Not.Null);
            else if (kind == "later Bool Add") ParameterDriverExpressionTests.Driver(condition, ParameterDriverExpressionTests.Op("Add", "Enable", 1));
            else if (kind == "incoming malformed Set") ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "Enable", float.NaN));
            else
            {
                using var data = new SerializedObject(random); data.FindProperty("parameters").GetArrayElementAtIndex(0).FindPropertyRelative("valueMax").floatValue = float.NaN; data.ApplyModifiedPropertiesWithoutUndo();
            }
            var before = EditorJsonUtility.ToJson(controller);
            Assert.Catch<InvalidOperationException>(() => Hold()); Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
        }

        [Test]
        public void AParameterOnlyForwardRelayIsIncludedInTheSuspendedComponent()
        {
            controller.AddParameter("Relay", AnimatorControllerParameterType.Bool); AddLayer("Automatic relay"); var relay = State(3, "Relay", Clip("Empty relay"));
            ParameterDriverExpressionTests.Driver(relay, ParameterDriverExpressionTests.Op("Set", "Relay", 1));
            var other = State(3, "Other relay", Clip("Other empty relay")); var transition = relay.AddTransition(other); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Greater, 1, "Lottery"); Assert.That(Find(), Is.EquivalentTo(new[] { 2, 3 }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AutomaticSuspensionCannotDiscardIncomingOtherPlayableWriters(bool malformed)
        {
            var other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/OtherInitializer.controller");
            other.AddParameter("Lottery", AnimatorControllerParameterType.Float); source.OtherControllers.Add(other);
            var machine = other.layers[0].stateMachine; var state = machine.AddState("Initialize automatic interval"); machine.defaultState = state;
            state.motion = Clip("Empty incoming initializer"); state.writeDefaultValues = false;
            ParameterDriverExpressionTests.Driver(state, ParameterDriverExpressionTests.Op("Set", "Lottery", malformed ? float.NaN : 2));
            Assert.That(Find(), Is.Empty, "An incoming shared parameter writer makes the automatic component dependent on another playable.");
            Assert.Catch<InvalidOperationException>(() => Hold());
        }

        [Test]
        public void GeneralFixedSamplingStillRejectsTheRelevantAutomaticRandomOperation()
        {
            var error = Assert.Catch<InvalidOperationException>(() => VrChatExpressionSampler.SampleFixed(avatar, controller,
                new Dictionary<string, float>(), new Dictionary<string, float> { ["Enable"] = 1 }, null, source));
            Assert.That(error.Message, Does.Contain("Random"));
        }

        void UnownedRandomFixture(float preparedRecovery = 0)
        {
            // Neither output has an authored binding in the selected face.
            // The recovery channel deliberately has no semantic blink name.
            mesh.AddBlendShapeFrame("Recovery channel", 100, Enumerable.Repeat(Vector3.right * .02f, 3).ToArray(), null, null);
            avatar.transform.Find("Body").GetComponent<SkinnedMeshRenderer>().SetBlendShapeWeight(3, preparedRecovery);
            // A separate permanent underlay owns the same prepared rest. Its
            // single constant clip genuinely seeds a stationary scalar root;
            // the multi-state automatic layer itself cannot establish one.
            AnimationUtility.SetEditorCurve((AnimationClip)controller.layers[0].stateMachine.defaultState.motion,
                Binding("Recovery channel"), AnimationCurve.Constant(0, 1, preparedRecovery));
            condition.motion = Clip("Random idle reset", ("Blink", 0), ("Recovery channel", 0));
            AnimationUtility.SetEditorCurve(faceClip, Binding("Blink"), null);
            AnimationUtility.SetEditorCurve(closeClip, Binding("Authored"), null);
            AnimationUtility.SetEditorCurve(closeClip, Binding("Blink"), new AnimationCurve(
                new Keyframe(0, 0), new Keyframe(.2f, 100), new Keyframe(.4f, 0), new Keyframe(1, 0)));
            AnimationUtility.SetEditorCurve(closeClip, Binding("Recovery channel"), new AnimationCurve(
                new Keyframe(0, 0), new Keyframe(.4f, 0), new Keyframe(.7f, 80), new Keyframe(1, 0)));
            var openState = controller.layers[2].stateMachine.states.Single(child => child.state.name == "Open").state;
            var open = openState.motion as AnimationClip;
            AnimationUtility.SetEditorCurve(open, Binding("Recovery channel"), AnimationCurve.Constant(0, 1, 0));
            // The source selected driver is not a producer of this idle gate.
            condition.transitions.Single().conditions = condition.transitions.Single().conditions.Where(value => value.parameter != "Enable").ToArray();
            closed.transitions.Single().exitTime = 1;
            // Keep this an ongoing autonomous rest. A one-shot animation which
            // settles forever in Open can independently yield a fixed zero.
            var repeat = openState.AddTransition(condition); repeat.hasExitTime = true; repeat.exitTime = 1; repeat.duration = 0;
        }

        HashSet<int> UnownedRest(out HashSet<EditorCurveBinding> preserved)
            => HeldAutomaticExpressionLayers.FindUnownedRandomRest(avatar, controller, source,
                new HashSet<EditorCurveBinding>(AnimationUtility.GetCurveBindings(faceClip)),
                FixedExpressionContext.Create(controller, source.Defaults, source), out preserved);

        [TestCase(0)]
        [TestCase(27)]
        public void UnownedRandomRecoveryIsPreservedWithoutRecapturingItsStationaryReset(int preparedRecovery)
        {
            UnownedRandomFixture(preparedRecovery);
            var clone = Object.Instantiate(avatar); var graph = PlayableGraph.Create("Unowned live recovery oracle");
            try
            {
                var animator = clone.GetComponent<Animator>(); animator.runtimeAnimatorController = null; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual); var playable = AnimatorControllerPlayable.Create(graph, controller);
                AnimationPlayableOutput.Create(graph, "Live", animator).SetSourcePlayable(playable); graph.Play(); graph.Evaluate(0);
                var skin = clone.transform.Find("Body").GetComponent<SkinnedMeshRenderer>();
                float blinkPeak = 0, recoveryPeak = 0; var blinkFrame = -1; var recoveryFrame = -1;
                for (var frame = 0; frame < 100; frame++)
                {
                    graph.Evaluate(1f / 60f);
                    if (skin.GetBlendShapeWeight(1) > blinkPeak) { blinkPeak = skin.GetBlendShapeWeight(1); blinkFrame = frame; }
                    if (skin.GetBlendShapeWeight(3) > recoveryPeak) { recoveryPeak = skin.GetBlendShapeWeight(3); recoveryFrame = frame; }
                }
                Assert.That(blinkPeak, Is.GreaterThan(90)); Assert.That(recoveryPeak, Is.GreaterThan(70));
                Assert.That(recoveryFrame, Is.GreaterThan(blinkFrame), "The companion has a separate delayed temporal curve.");
            }
            finally { graph.Destroy(); Object.DestroyImmediate(clone); }
            var before = ExportSourceFingerprint.Compute(avatar); var serialized = EditorJsonUtility.ToJson(controller);
            Assert.That(ExpressionDependencies.StationaryMorphBindings(controller, null).Contains(Binding("Recovery channel")), Is.True,
                "The constant reset would otherwise promote the automatic channel to a scalar root.");
            Assert.That(UnownedRest(out var preserved), Is.EquivalentTo(new[] { 2 }));
            Assert.That(preserved, Is.EquivalentTo(new[] { Binding("Blink"), Binding("Recovery channel") }));
            var entry = Hold();
            Assert.That(entry.Values.Single(value => value.Shape == "Authored").Weight, Is.EqualTo(75).Within(.01));
            Assert.That(entry.Values.Single(value => value.Shape == "Rest").Weight, Is.EqualTo(12).Within(.01));
            Assert.That(entry.Values.Any(value => value.Shape == "Blink" || value.Shape == "Recovery channel"), Is.False);
            Assert.That(entry.Messages.Any(value => value.Contains("Random") && value.Contains("Automatic blink")), Is.True);
            Assert.That(avatar.transform.Find("Body").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(3), Is.EqualTo(preparedRecovery));
            Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before)); Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(serialized));
        }

        void IncomingRandomGate()
        {
            condition.transitions.Single().AddCondition(AnimatorConditionMode.If, 0, "Enable");
        }

        [TestCase("Set")]
        [TestCase("Copy")]
        public void ReadOnlyRandomGateProducersKeepTheirNativeSelectedCallbacks(string operation)
        {
            UnownedRandomFixture(); IncomingRandomGate();
            controller.AddParameter("Requested", AnimatorControllerParameterType.Bool);
            selected.behaviours = Array.Empty<StateMachineBehaviour>();
            ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op(operation, "Enable", 0, "Requested"));
            var rest = controller.layers[2].stateMachine.states.Single(child => child.state.name == "Open").state;
            var disable = closed.AddTransition(rest); disable.hasExitTime = false; disable.duration = 0;
            disable.AddCondition(AnimatorConditionMode.IfNot, 0, "Enable");
            var underlay = controller.layers[0].stateMachine.defaultState;
            AnimationUtility.SetEditorCurve((AnimationClip)underlay.motion, Binding("Authored"), AnimationCurve.Constant(0, 1, 20));
            var enabled = State(0, "Selected gate underlay", Clip("Gate changes the lower face", ("Authored", 40)));
            var transition = underlay.AddTransition(enabled); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.IfNot, 0, "Enable");
            var definitions = controller.layers; definitions[1].defaultWeight = .5f; controller.layers = definitions;
            var before = ExportSourceFingerprint.Compute(avatar); var serialized = EditorJsonUtility.ToJson(controller);
            Assert.That(UnownedRest(out var preserved), Is.EquivalentTo(new[] { 2 }));
            Assert.That(preserved, Is.EquivalentTo(new[] { Binding("Blink"), Binding("Recovery channel") }));

            // The original native graph and actual SDK Set/Copy adapter provide
            // the selected callback witness. The Random result is irrelevant to
            // this face and is not invented by the witness or by the exporter.
            var clone = Object.Instantiate(avatar); var graph = PlayableGraph.Create("Native incoming gate oracle");
            ExpressionEvaluationSession evaluation = null;
            float expected;
            try
            {
                var dependencies = new ExpressionDependencies();
                var unknown = new List<string>();
                ExpressionDependencies.Inspect(controller, null, dependencies.Drivers, unknown, true);
                Assert.That(unknown, Is.Empty); dependencies.Layers.UnionWith(new[] { 0, 1, 2 });
                dependencies.Parameters.UnionWith(new[] { "Enable", "Requested" });
                evaluation = new ExpressionEvaluationSession(controller, dependencies, source.ExpressionParameters);
                var animator = clone.GetComponent<Animator>(); animator.runtimeAnimatorController = null; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                evaluation.Animator = animator; graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, evaluation.Controller);
                AnimationPlayableOutput.Create(graph, "Native", animator).SetSourcePlayable(playable); graph.Play(); graph.Evaluate(0); evaluation.Check();
                for (var frame = 0; frame < 120; frame++) { graph.Evaluate(1f / 60f); evaluation.Check(); }
                Assert.That(playable.GetBool("Enable"), Is.False, "The source callback changes its authored true default.");
                expected = clone.transform.Find("Body").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(0);
                Assert.That(expected, Is.EqualTo(57.5f).Within(.01), "The selected callback retains the native lower face at the fractional slot.");
            }
            finally { graph.Destroy(); evaluation?.Dispose(); Object.DestroyImmediate(clone); }
            var entry = Hold();
            Assert.That(entry.Values.Single(value => value.Shape == "Authored").Weight, Is.EqualTo(expected).Within(.01));
            Assert.That(entry.Values.Any(value => value.Shape == "Blink" || value.Shape == "Recovery channel"), Is.False);
            Assert.That(controller.parameters.Single(parameter => parameter.name == "Enable").defaultBool, Is.True);
            Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before)); Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(serialized));
        }

        [TestCase("nonfinite Set")]
        [TestCase("Bool Add")]
        [TestCase("missing Copy source")]
        public void ReadOnlyRandomGateCannotConcealMalformedTypedProducerOperations(string kind)
        {
            UnownedRandomFixture(); IncomingRandomGate();
            var operation = kind == "nonfinite Set" ? ParameterDriverExpressionTests.Op("Set", "Enable", float.NaN) :
                kind == "Bool Add" ? ParameterDriverExpressionTests.Op("Add", "Enable", 1) :
                ParameterDriverExpressionTests.Op("Copy", "Enable", source: "Missing source");
            ParameterDriverExpressionTests.Driver(selected, operation);
            var before = ExportSourceFingerprint.Compute(avatar); var serialized = EditorJsonUtility.ToJson(controller);
            Assert.Catch<InvalidOperationException>(() => UnownedRest(out _));
            Assert.Catch<InvalidOperationException>(() => Hold());
            Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before)); Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(serialized));
        }

        [TestCase("Set")]
        [TestCase("Add")]
        [TestCase("Copy")]
        public void SelectedRandomGateCannotDropADeterministicUnownedFace(string operation)
        {
            UnownedRandomFixture(); IncomingRandomGate();
            controller.AddParameter("Forced face", AnimatorControllerParameterType.Int);
            controller.AddParameter("Requested face", AnimatorControllerParameterType.Int);
            var parameters = controller.parameters;
            parameters.Single(parameter => parameter.name == "Requested face").defaultInt = 1; controller.parameters = parameters;
            selected.behaviours = Array.Empty<StateMachineBehaviour>();
            ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op(operation, "Forced face", 1, "Requested face"));
            var terminal = State(2, "Selected deterministic alternative", Clip("Unowned selected face", ("Blink", 0), ("Recovery channel", 60)));
            var enter = controller.layers[2].stateMachine.AddAnyStateTransition(terminal); enter.hasExitTime = false; enter.duration = 0;
            enter.canTransitionToSelf = false; enter.AddCondition(AnimatorConditionMode.Greater, 0, "Forced face");
            var before = ExportSourceFingerprint.Compute(avatar); var serialized = EditorJsonUtility.ToJson(controller);

            // The original graph and real SDK callback reach a nonzero face
            // which the selected clip does not explicitly bind. It is not an
            // independent idle just because the same layer also has Random.
            var clone = Object.Instantiate(avatar); var graph = PlayableGraph.Create("Selected deterministic gate oracle");
            ExpressionEvaluationSession evaluation = null;
            try
            {
                var dependencies = new ExpressionDependencies(); var unknown = new List<string>();
                ExpressionDependencies.Inspect(controller, null, dependencies.Drivers, unknown, true); Assert.That(unknown, Is.Empty);
                dependencies.Layers.UnionWith(new[] { 0, 1, 2 });
                dependencies.Parameters.UnionWith(new[] { "Enable", "Forced face", "Requested face" });
                evaluation = new ExpressionEvaluationSession(controller, dependencies, source.ExpressionParameters);
                var animator = clone.GetComponent<Animator>(); animator.runtimeAnimatorController = null; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                evaluation.Animator = animator; graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, evaluation.Controller);
                AnimationPlayableOutput.Create(graph, "Native", animator).SetSourcePlayable(playable); graph.Play(); graph.Evaluate(0); evaluation.Check();
                for (var frame = 0; frame < 120; frame++) { graph.Evaluate(1f / 60f); evaluation.Check(); }
                Assert.That(playable.GetInteger("Forced face"), Is.EqualTo(1));
                Assert.That(playable.GetCurrentAnimatorStateInfo(2).shortNameHash, Is.EqualTo(Animator.StringToHash(terminal.name)));
                Assert.That(clone.transform.Find("Body").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(3), Is.EqualTo(60).Within(.01));
            }
            finally { graph.Destroy(); evaluation?.Dispose(); Object.DestroyImmediate(clone); }
            Assert.That(UnownedRest(out var preserved), Is.Empty); Assert.That(preserved, Is.Empty);
            try
            {
                var entry = Hold();
                Assert.That(entry.Values.Single(value => value.Shape == "Recovery channel").Weight, Is.EqualTo(60).Within(.01),
                    "Ordinary evaluation may reproduce the selected contribution, but suspension must not remove it.");
            }
            catch (InvalidOperationException error) { Assert.That(error.Message, Does.Contain("Random")); }
            Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before)); Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(serialized));
        }

        [TestCase("parameter clock")]
        [TestCase("conditional exit")]
        [TestCase("AnyState cycle")]
        public void RandomGateRestNeedsAFiniteGateIndependentReturn(string kind)
        {
            UnownedRandomFixture(); IncomingRandomGate();
            if (kind == "parameter clock")
            {
                controller.AddParameter("Clock", AnimatorControllerParameterType.Float); closed.timeParameterActive = true; closed.timeParameter = "Clock";
                ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "Clock", .25f));
            }
            else if (kind == "conditional exit") closed.transitions.Single().AddCondition(AnimatorConditionMode.IfNot, 0, "Enable");
            else
            {
                AnimationUtility.SetEditorCurve(closeClip, Binding("Recovery channel"), AnimationCurve.Constant(0, 1, 60));
                var reset = controller.layers[2].stateMachine.AddAnyStateTransition(closed); reset.hasExitTime = false; reset.duration = 0;
                reset.canTransitionToSelf = true; reset.AddCondition(AnimatorConditionMode.If, 0, "Enable");
            }
            var before = ExportSourceFingerprint.Compute(avatar); var serialized = EditorJsonUtility.ToJson(controller);
            Assert.That(UnownedRest(out var preserved), Is.Empty); Assert.That(preserved, Is.Empty);
            Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before)); Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(serialized));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NestedRandomGateRestMustReturnThroughAnUnconditionalParentExit(bool conditionalParent)
        {
            UnownedRandomFixture(); IncomingRandomGate();
            var root = controller.layers[2].stateMachine;
            var open = root.states.Single(child => child.state.name == "Open").state;
            var child = root.AddStateMachine("Automatic nested cycle");
            var blink = child.AddState("Nested automatic curve"); blink.motion = closeClip; blink.writeDefaultValues = false; child.defaultState = blink;
            condition.RemoveTransition(condition.transitions.Single()); root.RemoveState(closed);
            var enter = condition.AddTransition(child); enter.hasExitTime = true; enter.exitTime = .1f; enter.duration = 0;
            enter.AddCondition(AnimatorConditionMode.If, 0, "Enable"); enter.AddCondition(AnimatorConditionMode.Greater, -1, "Lottery");
            var exit = blink.AddExitTransition(); exit.hasExitTime = true; exit.exitTime = 1; exit.duration = 0;
            var reset = root.AddStateMachineTransition(child, open);
            if (conditionalParent) reset.AddCondition(AnimatorConditionMode.IfNot, 0, "Enable");
            var before = ExportSourceFingerprint.Compute(avatar); var serialized = EditorJsonUtility.ToJson(controller);
            var omitted = UnownedRest(out var preserved);
            if (conditionalParent) Assert.That(omitted, Is.Empty);
            else Assert.That(omitted, Is.EquivalentTo(new[] { 2 }));
            Assert.That(preserved.Count, Is.EqualTo(conditionalParent ? 0 : 2));
            if (!conditionalParent)
            {
                var entry = Hold(); Assert.That(entry.Values.Single(value => value.Shape == "Authored").Weight, Is.EqualTo(75).Within(.01));
                Assert.That(entry.Values.Any(value => value.Shape == "Recovery channel"), Is.False);
            }
            Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before)); Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(serialized));
        }

        [TestCase("complete partition")]
        [TestCase("missing partition")]
        [TestCase("nonzero terminal")]
        public void NestedRandomRestartNeedsCompleteTypedRestRouting(string kind)
        {
            UnownedRandomFixture(); IncomingRandomGate();
            controller.AddParameter("Mode", AnimatorControllerParameterType.Int);
            var root = controller.layers[2].stateMachine;
            var open = root.states.Single(child => child.state.name == "Open").state;
            var child = root.AddStateMachine("Random restart boundary");
            var childRandom = child.AddStateMachineBehaviour(random.GetType()); EditorUtility.CopySerialized(random, childRandom);
            var blink = child.AddState("Nested automatic curve"); blink.motion = closeClip; blink.writeDefaultValues = false; child.defaultState = blink;
            condition.RemoveTransition(condition.transitions.Single()); root.RemoveState(closed);
            var enter = condition.AddTransition(child); enter.hasExitTime = true; enter.exitTime = .1f; enter.duration = 0;
            enter.AddCondition(AnimatorConditionMode.If, 0, "Enable"); enter.AddCondition(AnimatorConditionMode.Greater, -1, "Lottery");
            var exit = blink.AddExitTransition(); exit.hasExitTime = true; exit.exitTime = 1; exit.duration = 0;
            var disabled = root.AddStateMachineTransition(child, open); disabled.AddCondition(AnimatorConditionMode.IfNot, 0, "Enable");
            if (kind != "missing partition")
            {
                var otherMode = root.AddStateMachineTransition(child, open); otherMode.AddCondition(AnimatorConditionMode.If, 0, "Enable");
                otherMode.AddCondition(AnimatorConditionMode.NotEqual, 0, "Mode");
            }
            var restart = root.AddStateMachineTransition(child, child); restart.AddCondition(AnimatorConditionMode.If, 0, "Enable");
            restart.AddCondition(AnimatorConditionMode.Equals, 0, "Mode");
            if (kind == "nonzero terminal")
            {
                var terminal = child.AddState("Authored deterministic alternative"); terminal.writeDefaultValues = false;
                terminal.motion = Clip("Nonzero child face", ("Blink", 0), ("Recovery channel", 60));
                var entry = child.AddEntryTransition(terminal); entry.AddCondition(AnimatorConditionMode.Equals, 1, "Mode");
            }
            var before = ExportSourceFingerprint.Compute(avatar); var serialized = EditorJsonUtility.ToJson(controller);
            var omitted = UnownedRest(out var preserved);
            if (kind == "complete partition")
            {
                Assert.That(omitted, Is.EquivalentTo(new[] { 2 })); Assert.That(preserved.Count, Is.EqualTo(2));
                var entry = Hold(); Assert.That(entry.Values.Single(value => value.Shape == "Authored").Weight, Is.EqualTo(75).Within(.01));
                Assert.That(entry.Values.Any(value => value.Shape == "Recovery channel"), Is.False);
            }
            else { Assert.That(omitted, Is.Empty); Assert.That(preserved, Is.Empty); }
            Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before)); Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(serialized));
        }

        [Test]
        public void SparseWriteDefaultsOffRestCannotLatchASelectedUnownedFace()
        {
            UnownedRandomFixture(); IncomingRandomGate();
            // A lower-layer explicit zero masks a sparse upper-layer value.
            // Remove that support so this oracle tests the actual WD Off
            // predecessor history rather than a lower-layer reset.
            AnimationUtility.SetEditorCurve((AnimationClip)controller.layers[0].stateMachine.defaultState.motion,
                Binding("Recovery channel"), null);
            controller.AddParameter("Forced face", AnimatorControllerParameterType.Int);
            ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "Forced face", 1));
            var terminal = State(2, "Selected transient face", Clip("Transient selected face", ("Blink", 0), ("Recovery channel", 60)));
            var empty = State(2, "Sparse held rest", Clip("No recovery reset", ("Blink", 0)));
            var enter = condition.AddTransition(terminal); enter.hasExitTime = false; enter.duration = 0;
            enter.AddCondition(AnimatorConditionMode.Greater, 0, "Forced face");
            var leave = terminal.AddTransition(empty); leave.hasExitTime = true; leave.exitTime = .5f; leave.duration = 0;
            var before = ExportSourceFingerprint.Compute(avatar); var serialized = EditorJsonUtility.ToJson(controller);
            Assert.That(ExpressionDependencies.StationaryMorphBindings(controller, null).Contains(Binding("Recovery channel")), Is.False,
                "No independent lower-layer constant can reset or conceal the previous authored value.");
            var clone = Object.Instantiate(avatar); var graph = PlayableGraph.Create("Native sparse held face oracle");
            ExpressionEvaluationSession evaluation = null;
            try
            {
                var dependencies = new ExpressionDependencies(); var unknown = new List<string>();
                ExpressionDependencies.Inspect(controller, null, dependencies.Drivers, unknown, true); Assert.That(unknown, Is.Empty);
                dependencies.Layers.UnionWith(new[] { 0, 1, 2 }); dependencies.Parameters.UnionWith(new[] { "Enable", "Forced face" });
                evaluation = new ExpressionEvaluationSession(controller, dependencies, source.ExpressionParameters);
                var animator = clone.GetComponent<Animator>(); animator.runtimeAnimatorController = null; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                evaluation.Animator = animator; graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, evaluation.Controller);
                AnimationPlayableOutput.Create(graph, "Native", animator).SetSourcePlayable(playable); graph.Play(); graph.Evaluate(0); evaluation.Check();
                for (var frame = 0; frame < 120; frame++) { graph.Evaluate(1f / 60f); evaluation.Check(); }
                Assert.That(playable.GetInteger("Forced face"), Is.EqualTo(1));
                Assert.That(playable.GetCurrentAnimatorStateInfo(2).shortNameHash, Is.EqualTo(Animator.StringToHash(empty.name)));
                Assert.That(clone.transform.Find("Body").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(3), Is.EqualTo(60).Within(.01),
                    "The original WD Off sparse state keeps the omitted authored value; it does not reset to prepared zero.");
            }
            finally { graph.Destroy(); evaluation?.Dispose(); Object.DestroyImmediate(clone); }
            Assert.That(UnownedRest(out var preserved), Is.Empty); Assert.That(preserved, Is.Empty);
            // This predecessor channel deliberately has no stationary scalar
            // root. The native latch proves why the automatic omission proof
            // must reject the sparse reset, independently of ReadClip's domain.
            Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before)); Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(serialized));
        }

        StateMachineBehaviour GlobalFxControl(AnimatorState state, float weight, float duration = 0)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCPlayableLayerControl"))
                .FirstOrDefault(value => value != null);
            if (type == null) Assert.Ignore("Install the real VRChat SDK.");
            var control = state.AddStateMachineBehaviour(type); using var data = new SerializedObject(control);
            var target = data.FindProperty("layer"); target.enumValueIndex = Array.IndexOf(target.enumNames, "FX");
            data.FindProperty("goalWeight").floatValue = weight; data.FindProperty("blendDuration").floatValue = duration;
            data.ApplyModifiedPropertiesWithoutUndo(); return control;
        }

        (AnimatorState Unit, AnimatorState Disabled) GlobalFxGate(string gate = "InStation", float weight = 1, float duration = 0)
        {
            controller.AddParameter(gate, AnimatorControllerParameterType.Bool); AddLayer("Global FX gate");
            var unit = State(3, "FX enabled", Clip("FX enabled support")); GlobalFxControl(unit, weight, duration);
            var disabled = State(3, "FX disabled", Clip("FX disabled support")); GlobalFxControl(disabled, 0);
            var enter = unit.AddTransition(disabled); enter.hasExitTime = false; enter.duration = 0; enter.AddCondition(AnimatorConditionMode.If, 0, gate);
            var leave = disabled.AddTransition(unit); leave.hasExitTime = false; leave.duration = 0; leave.AddCondition(AnimatorConditionMode.IfNot, 0, gate);
            return (unit, disabled);
        }

        [Test]
        public void UnownedRandomRestRetainsNativeUnitFxCallbacksBehindAnUnchangedNormalGate()
        {
            UnownedRandomFixture(); IncomingRandomGate(); var control = GlobalFxGate();
            controller.AddParameter("Native relay", AnimatorControllerParameterType.Int);
            ParameterDriverExpressionTests.Driver(control.Unit, ParameterDriverExpressionTests.Op("Set", "Native relay", 1));
            var underlay = controller.layers[0].stateMachine.defaultState;
            AnimationUtility.SetEditorCurve((AnimationClip)underlay.motion, Binding("Authored"), AnimationCurve.Constant(0, 1, 20));
            var enabled = State(0, "Native relay face", Clip("Native relay lower face", ("Authored", 40)));
            var transition = underlay.AddTransition(enabled); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Greater, 0, "Native relay");
            var definitions = controller.layers; definitions[1].defaultWeight = .5f; controller.layers = definitions;
            var before = ExportSourceFingerprint.Compute(avatar); var serialized = EditorJsonUtility.ToJson(controller);
            Assert.That(UnownedRest(out var preserved), Is.EquivalentTo(new[] { 2 })); Assert.That(preserved.Count, Is.EqualTo(2));
            var clone = Object.Instantiate(avatar); var graph = PlayableGraph.Create("Original unit FX callback oracle");
            ExpressionEvaluationSession evaluation = null; float expected;
            try
            {
                var dependencies = new ExpressionDependencies(); var unknown = new List<string>();
                var inspected = ExpressionDependencies.Inspect(controller, null, dependencies.Drivers, unknown, true); Assert.That(unknown, Is.Empty);
                dependencies.HasFxControls = inspected.Any(layer => layer.FxControl);
                dependencies.Layers.UnionWith(new[] { 0, 1, 2, 3 }); dependencies.Parameters.UnionWith(new[] { "Enable", "Native relay", "InStation" });
                evaluation = new ExpressionEvaluationSession(controller, dependencies, source.ExpressionParameters);
                var animator = clone.GetComponent<Animator>(); animator.runtimeAnimatorController = null; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                evaluation.Animator = animator; graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, evaluation.Controller);
                AnimationPlayableOutput.Create(graph, "Native", animator).SetSourcePlayable(playable); graph.Play(); graph.Evaluate(0); evaluation.Check();
                for (var frame = 0; frame < 120; frame++) { graph.Evaluate(1f / 60f); evaluation.Check(); }
                evaluation.CheckNeutralFx(); Assert.That(playable.GetBool("InStation"), Is.False);
                Assert.That(playable.GetInteger("Native relay"), Is.EqualTo(1));
                Assert.That(playable.GetCurrentAnimatorStateInfo(3).shortNameHash, Is.EqualTo(Animator.StringToHash(control.Unit.name)));
                expected = clone.transform.Find("Body").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(0);
                Assert.That(expected, Is.EqualTo(57.5f).Within(.01));
            }
            finally { graph.Destroy(); evaluation?.Dispose(); Object.DestroyImmediate(clone); }
            var entry = Hold(); Assert.That(entry.Values.Single(value => value.Shape == "Authored").Weight, Is.EqualTo(expected).Within(.01));
            Assert.That(entry.Values.Any(value => value.Shape == "Recovery channel"), Is.False);
            Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before)); Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(serialized));
        }

        [TestCase("selected input")]
        [TestCase("live input")]
        [TestCase("selected writer")]
        [TestCase("dormant curve writer")]
        [TestCase("fractional global weight")]
        [TestCase("zero global weight")]
        [TestCase("noninstant global weight")]
        [TestCase("other playable control")]
        [TestCase("unknown callback")]
        [TestCase("invalid routing")]
        public void UnownedRandomRestCannotHideAnUnprovenGlobalFxCommand(string kind)
        {
            UnownedRandomFixture(); IncomingRandomGate();
            var custom = kind == "selected writer" || kind == "dormant curve writer";
            var gate = custom ? "Custom global gate" : "InStation";
            if (custom) source.ExternalParameters.Add(gate);
            var control = GlobalFxGate(gate, kind == "fractional global weight" ? .5f : kind == "zero global weight" ? 0 : 1,
                kind == "noninstant global weight" ? .5f : 0);
            var context = FixedExpressionContext.Create(controller, source.Defaults, source);
            if (kind == "selected input") context.Values[gate] = 1;
            else if (kind == "live input") context.Values.Remove(gate);
            else if (kind == "selected writer") ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", gate, 1));
            else if (kind == "dormant curve writer") AnimationUtility.SetEditorCurve((AnimationClip)control.Disabled.motion,
                EditorCurveBinding.FloatCurve("", typeof(Animator), gate), AnimationCurve.Constant(0, 1, 1));
            else if (kind == "other playable control")
            {
                var other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/OtherGlobalControl.controller"); source.OtherControllers.Add(other);
                var state = other.layers[0].stateMachine.AddState("Additional global FX writer"); other.layers[0].stateMachine.defaultState = state;
                state.motion = Clip("Additional empty control"); state.writeDefaultValues = false; GlobalFxControl(state, 1);
            }
            else if (kind == "unknown callback") control.Unit.AddStateMachineBehaviour<UnknownStateCallbackProbe>();
            else if (kind == "invalid routing") control.Unit.transitions.Single().destinationState = selected;
            var before = ExportSourceFingerprint.Compute(avatar); var serialized = EditorJsonUtility.ToJson(controller);
            if (kind == "selected input")
            {
                var normal = FixedExpressionContext.Create(controller, source.Defaults, source);
                Assert.That(HeldAutomaticExpressionLayers.FindUnownedRandomRest(avatar, controller, source,
                    new HashSet<EditorCurveBinding>(AnimationUtility.GetCurveBindings(faceClip)), normal, out var selectedPreserved,
                    new HashSet<string> { gate }), Is.Empty,
                    "A final input can enable the dormant whole-FX zero command even when the normal context remains unchanged.");
                Assert.That(selectedPreserved, Is.Empty);
            }
            Assert.That(HeldAutomaticExpressionLayers.FindUnownedRandomRest(avatar, controller, source,
                new HashSet<EditorCurveBinding>(AnimationUtility.GetCurveBindings(faceClip)), context, out var preserved), Is.Empty);
            Assert.That(preserved, Is.Empty);
            Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before)); Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(serialized));
        }

        [TestCase("parameter curve")]
        [TestCase("Random gate")]
        public void ReadOnlyRandomGateRequiresReproducibleNativeProducerOperations(string kind)
        {
            UnownedRandomFixture(); IncomingRandomGate();
            if (kind == "parameter curve") AnimationUtility.SetEditorCurve(faceClip,
                EditorCurveBinding.FloatCurve("", typeof(Animator), "Enable"), AnimationCurve.Linear(0, 0, 1, 1));
            else ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Random", "Enable"));
            Assert.That(UnownedRest(out var preserved), Is.Empty); Assert.That(preserved, Is.Empty);
        }

        [TestCase("selected output")]
        [TestCase("incoming FX writer")]
        [TestCase("incoming other playable writer")]
        [TestCase("escaping face reader")]
        [TestCase("appearance")]
        [TestCase("animation event")]
        [TestCase("parameter curve")]
        [TestCase("malformed Random")]
        [TestCase("unknown callback")]
        [TestCase("target layer control")]
        public void UnownedRandomRestStillRequiresEveryOriginalIsolationAndDataGuard(string kind)
        {
            UnownedRandomFixture();
            if (kind == "selected output")
            {
                AnimationUtility.SetEditorCurve(faceClip, Binding("Recovery channel"), AnimationCurve.Constant(0, 1, 0));
                // The selected SDK callback actually drives this upper layer.
                // It must stay in support rather than using the pre-existing
                // direct-clip policy for an unrelated competing face selector.
                IncomingRandomGate();
            }
            else if (kind == "incoming FX writer") ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "Lottery", 2));
            else if (kind == "incoming other playable writer")
            {
                var other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/OtherRestWriter.controller");
                other.AddParameter("Lottery", AnimatorControllerParameterType.Float); source.OtherControllers.Add(other);
                var machine = other.layers[0].stateMachine; var state = machine.AddState("Initialize interval"); machine.defaultState = state;
                state.motion = Clip("Empty additional initializer"); state.writeDefaultValues = false;
                ParameterDriverExpressionTests.Driver(state, ParameterDriverExpressionTests.Op("Set", "Lottery", 2));
            }
            else if (kind == "escaping face reader")
            {
                AddLayer("Dependent face"); var start = State(3, "Face", Clip("Face rest", ("Authored", 0)));
                var next = State(3, "Other face", Clip("Face selected by interval", ("Authored", 20)));
                var transition = start.AddTransition(next); transition.hasExitTime = false; transition.duration = 0;
                transition.AddCondition(AnimatorConditionMode.Greater, 1, "Lottery");
            }
            else if (kind == "appearance") AnimationUtility.SetEditorCurve(closeClip, EditorCurveBinding.FloatCurve("Body", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1));
            else if (kind == "animation event") AnimationUtility.SetAnimationEvents(closeClip, new[] { new AnimationEvent { time = .2f, functionName = "UnknownEffect" } });
            else if (kind == "parameter curve") AnimationUtility.SetEditorCurve(closeClip, EditorCurveBinding.FloatCurve("", typeof(Animator), "Lottery"), AnimationCurve.Linear(0, 0, 1, 2));
            else if (kind == "unknown callback") condition.AddStateMachineBehaviour<UnknownStateCallbackProbe>();
            else if (kind == "malformed Random")
            {
                using var data = new SerializedObject(random); data.FindProperty("parameters").GetArrayElementAtIndex(0).FindPropertyRelative("valueMax").floatValue = float.NaN; data.ApplyModifiedPropertiesWithoutUndo();
            }
            else
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAnimatorLayerControl")).FirstOrDefault(value => value != null);
                if (type == null) Assert.Ignore("Install the real VRChat SDK.");
                var control = selected.AddStateMachineBehaviour(type); using var data = new SerializedObject(control);
                var playable = data.FindProperty("playable"); playable.enumValueIndex = Array.IndexOf(playable.enumNames, "FX");
                data.FindProperty("layer").intValue = 2; data.FindProperty("goalWeight").floatValue = .5f; data.FindProperty("blendDuration").floatValue = 0; data.ApplyModifiedPropertiesWithoutUndo();
            }
            var before = ExportSourceFingerprint.Compute(avatar); var serialized = EditorJsonUtility.ToJson(controller);
            if (kind == "malformed Random") Assert.Catch<InvalidOperationException>(() => UnownedRest(out _));
            else { Assert.That(UnownedRest(out var preserved), Is.Empty); Assert.That(preserved, Is.Empty); }
            if (kind == "selected output" || kind == "malformed Random" || kind == "unknown callback")
                Assert.Catch<InvalidOperationException>(() => Hold());
            Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before)); Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(serialized));
        }

        void AutomaticAliasFixture(string alias, string mismatch = null)
        {
            mesh.ClearBlendShapes();
            var positions = Enumerable.Repeat(Vector3.up * .01f, 3).ToArray();
            var normals = Enumerable.Repeat(Vector3.right * .02f, 3).ToArray();
            var tangents = Enumerable.Repeat(Vector3.forward * .03f, 3).ToArray();
            if (mismatch == "inert") positions = normals = tangents = new Vector3[3];
            if (mismatch == "tiny") { positions = Enumerable.Repeat(Vector3.up * .0000001f, 3).ToArray(); normals = new Vector3[3]; tangents = new Vector3[3]; }
            foreach (var shape in new[] { "Authored", "まばたき", alias, "Rest" })
            {
                var candidate = shape == alias;
                var p = positions.ToArray(); var n = normals.ToArray(); var t = tangents.ToArray();
                if (candidate && mismatch == "position") p[1].x = .00001f;
                if (candidate && mismatch == "normal") n[1].y = .00001f;
                if (candidate && mismatch == "tangent") t[1].x = .00001f;
                mesh.AddBlendShapeFrame(shape, candidate && mismatch == "frame weight" ? 50 : 100, p, n, t);
                if (candidate && mismatch == "frame count") mesh.AddBlendShapeFrame(shape, 200, p, n, t);
            }
            AnimationUtility.SetEditorCurve(faceClip, Binding("Blink"), null);
            AnimationUtility.SetEditorCurve(faceClip, Binding("まばたき"), AnimationCurve.Constant(0, 1, 3));
            AnimationUtility.SetEditorCurve(faceClip, Binding(alias), AnimationCurve.Constant(0, 1, 0));
            AnimationUtility.SetEditorCurve(closeClip, Binding("Blink"), null);
            AnimationUtility.SetEditorCurve(closeClip, Binding("まばたき"), AnimationCurve.Constant(0, 1, 0));
            AnimationUtility.SetEditorCurve(closeClip, Binding(alias), AnimationCurve.Constant(0, 1, 100));
            var openClip = controller.layers[2].stateMachine.states.Single(child => child.state.name == "Open").state.motion as AnimationClip;
            AnimationUtility.SetEditorCurve(openClip, Binding("Blink"), null);
            AnimationUtility.SetEditorCurve(openClip, Binding(alias), AnimationCurve.Constant(0, 1, 0));
        }

        [TestCase("Auto_Blink", false)]
        [TestCase("AutoBlink", false)]
        [TestCase("auto_blink", false)]
        public void GeometryEquivalentAutomaticAliasCanCoexistWithTheManualBlink(string alias, bool tiny)
        {
            AutomaticAliasFixture(alias, tiny ? "tiny" : null);
            var automatic = NeutralShapeSampler.AutomaticChannels(avatar);
            Assert.That(automatic, Does.Contain(Binding("まばたき")));
            Assert.That(automatic, Does.Contain(Binding(alias)));
            Assert.That(BlinkShapeNames.Resolve(new[] { "まばたき", alias })[0], Is.Zero,
                "Standard VRM blink priority remains the manual semantic family.");
            var entry = Hold();
            Assert.That(entry.Values.Single(value => value.Shape == "Authored").Weight, Is.EqualTo(75).Within(.01));
            Assert.That(entry.Values.Single(value => value.Shape == "まばたき").Weight, Is.EqualTo(3).Within(.01));
            Assert.That(entry.Values.Single(value => value.Shape == alias).Weight, Is.Zero.Within(.01));
            Assert.That(entry.Messages.Any(value => value.Contains("Automatic blink")), Is.True);
        }

        [TestCase("position")]
        [TestCase("normal")]
        [TestCase("tangent")]
        [TestCase("frame count")]
        [TestCase("frame weight")]
        [TestCase("inert")]
        [TestCase("substring")]
        [TestCase("collision")]
        [TestCase("quantized tiny")]
        public void AutomaticAliasRequiresUniqueExactMeaningAndCompleteNonInertGeometry(string kind)
        {
            var alias = kind == "substring" ? "auto_blink_size" : "Auto_Blink";
            AutomaticAliasFixture(alias, kind == "quantized tiny" ? "tiny" : kind);
            if (kind == "collision") mesh.AddBlendShapeFrame("auto_blink", 100, Enumerable.Repeat(Vector3.up * .01f, 3).ToArray(), null, null);
            if (kind == "quantized tiny")
            {
                var positions = new Vector3[mesh.vertexCount]; var normals = new Vector3[mesh.vertexCount]; var tangents = new Vector3[mesh.vertexCount];
                mesh.GetBlendShapeFrameVertices(mesh.GetBlendShapeIndex("まばたき"), 0, positions, normals, tangents);
                Assert.That(positions.Concat(normals).Concat(tangents).All(value => value.Equals(Vector3.zero)), Is.True,
                    "Unity discarded the requested tiny deltas; stored source geometry, rather than the requested array, decides automatic equivalence.");
            }
            Assert.That(NeutralShapeSampler.AutomaticChannels(avatar).Contains(Binding(alias)), Is.False);
            Assert.Catch<InvalidOperationException>(() => Hold());
        }

        [Test]
        public void CoexistingKnownBlinkFamiliesAllKeepTheirAutomaticOwnership()
        {
            foreach (var shape in new[] { "まばたき", "Fcl_EYE_Close", "Blink_L", "Blink_R", "EyeClosedLeft", "EyeClosedRight" })
                mesh.AddBlendShapeFrame(shape, 100, Enumerable.Repeat(Vector3.up * .01f, 3).ToArray(), null, null);
            var automatic = NeutralShapeSampler.AutomaticChannels(avatar);
            foreach (var shape in new[] { "Blink", "まばたき", "Fcl_EYE_Close", "Blink_L", "Blink_R", "EyeClosedLeft", "EyeClosedRight" })
                Assert.That(automatic.Contains(Binding(shape)), Is.True);
            Assert.That(automatic.Contains(Binding("Authored")), Is.False);
            Assert.That(automatic.Contains(Binding("Rest")), Is.False);
        }

        float NativeDelayedGateRecoveryAtTheCaptureHorizon(bool expectedBlend)
        {
            var clone = Object.Instantiate(avatar); var graph = PlayableGraph.Create("Original delayed gate horizon oracle");
            ExpressionEvaluationSession evaluation = null;
            try
            {
                var dependencies = new ExpressionDependencies(); var unknown = new List<string>();
                ExpressionDependencies.Inspect(controller, null, dependencies.Drivers, unknown, true);
                Assert.That(unknown, Is.Empty);
                dependencies.Layers.UnionWith(new[] { 0, 1, 2 });
                dependencies.Parameters.UnionWith(new[] { "Enable", "Forced face" });
                evaluation = new ExpressionEvaluationSession(controller, dependencies, source.ExpressionParameters);
                var animator = clone.GetComponent<Animator>(); animator.runtimeAnimatorController = null;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; evaluation.Animator = animator;
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, evaluation.Controller);
                AnimationPlayableOutput.Create(graph, "Native original controller", animator).SetSourcePlayable(playable);
                graph.Play(); graph.Evaluate(0); evaluation.Check();
                for (var frame = 0; frame < 120; frame++) { graph.Evaluate(1f / 60f); evaluation.Check(); }
                Assert.That(playable.GetInteger("Forced face"), Is.EqualTo(1),
                    "The original selected SDK callback, rather than a manually forced oracle input, selects the deterministic alternative.");
                if (expectedBlend) Assert.That(playable.IsInTransition(2), Is.True,
                    "The native ten-second blend must actually be running; an ignored transition is not a clock counterexample.");
                return clone.transform.Find("Body").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(3);
            }
            finally { graph.Destroy(); evaluation?.Dispose(); Object.DestroyImmediate(clone); }
        }

        [TestCase("long exit")]
        [TestCase("slow state")]
        [TestCase("long blend")]
        [TestCase("long chain")]
        [TestCase("unused Random marker")]
        public void SelectedGateCannotHideAResetBeyondTheNativeCaptureHorizon(string kind)
        {
            UnownedRandomFixture(); IncomingRandomGate();
            controller.AddParameter("Forced face", AnimatorControllerParameterType.Int);
            ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "Forced face", 1));
            var open = controller.layers[2].stateMachine.states.Single(child => child.state.name == "Open").state;
            var alternativeClip = Clip("Delayed deterministic recovery", ("Blink", 0), ("Recovery channel", 60));
            var alternative = State(2, "Selected delayed recovery", alternativeClip);
            var enter = condition.AddTransition(alternative); enter.hasExitTime = false; enter.duration = 0;
            enter.canTransitionToSelf = false; enter.AddCondition(AnimatorConditionMode.Greater, 0, "Forced face");
            var reset = alternative.AddTransition(open); reset.hasExitTime = true; reset.exitTime = 10; reset.duration = 0;
            if (kind == "slow state") { alternative.speed = .05f; reset.exitTime = 1; }
            else if (kind == "long blend")
            {
                reset.hasExitTime = true; reset.exitTime = .01f; reset.hasFixedDuration = true; reset.duration = 10;
            }
            else if (kind == "long chain")
            {
                AnimationUtility.SetEditorCurve(alternativeClip, Binding("Blink"), AnimationCurve.Constant(0, 1.5f, 0));
                AnimationUtility.SetEditorCurve(alternativeClip, Binding("Recovery channel"), AnimationCurve.Constant(0, 1.5f, 60));
                var nextClip = Clip("Second delayed deterministic recovery", ("Blink", 0), ("Recovery channel", 60));
                AnimationUtility.SetEditorCurve(nextClip, Binding("Blink"), AnimationCurve.Constant(0, 1.5f, 0));
                AnimationUtility.SetEditorCurve(nextClip, Binding("Recovery channel"), AnimationCurve.Constant(0, 1.5f, 60));
                var next = State(2, "Second selected delayed recovery", nextClip);
                reset.destinationState = next; reset.exitTime = 1;
                var finish = next.AddTransition(open); finish.hasExitTime = true; finish.exitTime = 1; finish.duration = 0;
            }
            else if (kind == "unused Random marker")
            {
                controller.AddParameter("Unused random marker", AnimatorControllerParameterType.Float);
                var marker = alternative.AddStateMachineBehaviour(random.GetType()); EditorUtility.CopySerialized(random, marker);
                using var data = new SerializedObject(marker);
                data.FindProperty("parameters").GetArrayElementAtIndex(0).FindPropertyRelative("name").stringValue = "Unused random marker";
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            var before = ExportSourceFingerprint.Compute(avatar); var serialized = EditorJsonUtility.ToJson(controller);
            var native = NativeDelayedGateRecoveryAtTheCaptureHorizon(kind == "long blend");
            Assert.That(native, Is.GreaterThan(10), "A merely finite future reset is not the selected pose at the actual two-second native capture horizon.");
            if (kind != "long blend") Assert.That(native, Is.EqualTo(60).Within(.01));
            Assert.That(UnownedRest(out var preserved), Is.Empty); Assert.That(preserved, Is.Empty);
            try
            {
                var entry = Hold();
                Assert.That(entry.Values.Single(value => value.Shape == "Recovery channel").Weight, Is.EqualTo(native).Within(.02),
                    "The exporter must retain the independently observed selected recovery or refuse its relevant Random dependency.");
            }
            catch (InvalidOperationException error) { Assert.That(error.Message, Does.Contain("Random")); }
            Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(serialized));
        }
    }
}
