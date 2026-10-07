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
    }
}
