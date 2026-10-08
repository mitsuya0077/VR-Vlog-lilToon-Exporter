using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using VRVlog.Expressions;

namespace VRVlog.LilToonExporter.Tests
{
    // Real Unity graph/animation tests. The command-line mesh doubles do not
    // execute these tests and must not be reported as Animator validation.
    public class VrChatMenuExpressionTests
    {
        private string directory;
        private GameObject avatar;
        private Mesh mesh;
        private AnimatorController controller;

        [SetUp]
        public void SetUp()
        {
            var folder = "__VRVlogMenuTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder);
            directory = "Assets/" + folder;
            controller = AnimatorController.CreateAnimatorControllerAtPath(directory + "/FX.controller");
            avatar = new GameObject("Avatar", typeof(Animator));
            var face = new GameObject("Face", typeof(SkinnedMeshRenderer));
            face.transform.SetParent(avatar.transform, false);
            mesh = BaseShapeFixture.Create();
            var renderer = face.GetComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.SetBlendShapeWeight(0, 25);
            renderer.SetBlendShapeWeight(1, 50);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(avatar);
            UnityEngine.Object.DestroyImmediate(mesh);
            AssetDatabase.DeleteAsset(directory);
        }

        [Test]
        public void MenuSelectionCapturesACombinationAndLeavesSourceUntouched()
        {
            DiscreteController();
            var values = VrChatExpressionSampler.Sample(avatar, controller, Params("Face", 0), Params("Face", 1));
            Assert.That(values, Has.Count.EqualTo(2));
            Assert.That(values.Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
            Assert.That(values.Single(v => v.Shape == "Blink").Weight, Is.EqualTo(0).Within(.01));
            var source = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
            Assert.That(source.GetBlendShapeWeight(0), Is.EqualTo(25));
            Assert.That(source.GetBlendShapeWeight(1), Is.EqualTo(50));
            Assert.That(source.sharedMesh, Is.SameAs(mesh));

            var clone = UnityEngine.Object.Instantiate(avatar);
            var temporary = new List<Mesh>();
            try
            {
                AvatarBaseShape.Preserve(avatar, clone, temporary, null);
                var menu = new VrChatExpressionMenu.Source();
                var entry = new VrChatExpressionMenu.Entry { Id = "face-smile", Name = "顔 / 笑顔" };
                entry.Values.AddRange(values);
                menu.Entries.Add(entry);
                var expressions = VrChatExpressionBaker.Bake(avatar, clone, menu, temporary, null);
                Assert.That(expressions, Has.Count.EqualTo(1));
                var baked = clone.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh;
                var v = new Vector3[3]; var n = new Vector3[3]; var t = new Vector3[3];
                baked.GetBlendShapeFrameVertices(2, 0, v, n, t);
                Assert.That(baked.vertices[0] + v[0], Is.EqualTo(new Vector3(4, 0, 0)));
                Assert.That(mesh.blendShapeCount, Is.EqualTo(2));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clone);
                foreach (var item in temporary) UnityEngine.Object.DestroyImmediate(item);
            }
        }

        [Test]
        public void WriteDefaultsOffRetainsTheWholeEvaluatedFace()
        {
            DiscreteController();
            var states = controller.layers[0].stateMachine.states;
            var idle = (AnimationClip)states.Single(s => s.state.name == "Idle").state.motion;
            var smile = (AnimationClip)states.Single(s => s.state.name == "Smile").state.motion;
            var binding = EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size");
            AnimationUtility.SetEditorCurve(idle, binding, AnimationCurve.Constant(0, 1, 60));
            AnimationUtility.SetEditorCurve(smile, binding, null);
            var values = VrChatExpressionSampler.Sample(avatar, controller, Params("Face", 0), Params("Face", 1));
            Assert.That(values.Single(v => v.Shape == "Face size").Weight, Is.EqualTo(60).Within(.01),
                "The previous state owns the retained value, even though the current clip does not bind it.");
        }

        [Test]
        public void DelayedExitCannotMasqueradeAsAFixedExpression()
        {
            DiscreteController();
            var states = controller.layers[0].stateMachine.states;
            var idle = states.Single(s => s.state.name == "Idle").state;
            var smile = states.Single(s => s.state.name == "Smile").state;
            var transition = smile.AddTransition(idle);
            transition.hasExitTime = true;
            transition.exitTime = 10;
            transition.duration = 0;
            Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.Sample(avatar, controller, Params("Face", 0), Params("Face", 1)));
        }

        [Test]
        public void DelayedNonLoopingStepCannotMasqueradeAsAFixedExpression()
        {
            DiscreteController();
            var clip = (AnimationClip)controller.layers[0].stateMachine.states.Single(s => s.state.name == "Smile").state.motion;
            var curve = new AnimationCurve(new Keyframe(0, 75, 0, float.PositiveInfinity), new Keyframe(10, 100, float.PositiveInfinity, 0));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"), curve);
            Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.Sample(avatar, controller, Params("Face", 0), Params("Face", 1)));
        }

        private (float Face, float Blink, AnimatorStateInfo State) OriginalNativeSelectedPose(RuntimeAnimatorController runtime)
        {
            // Independent original-controller witness: no exporter dependency,
            // graph rewrite, sampler, or synthesized expected expression.
            var clone = UnityEngine.Object.Instantiate(avatar);
            var graph = PlayableGraph.Create("Original selected terminal witness");
            try
            {
                var animator = clone.GetComponent<Animator>(); animator.runtimeAnimatorController = null;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, runtime);
                AnimationPlayableOutput.Create(graph, "Original", animator).SetSourcePlayable(playable);
                playable.SetInteger("Face", 0); graph.Play(); graph.Evaluate(0);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                playable.SetInteger("Face", 1);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                var renderer = clone.GetComponentInChildren<SkinnedMeshRenderer>();
                return (renderer.GetBlendShapeWeight(0), renderer.GetBlendShapeWeight(1), playable.GetCurrentAnimatorStateInfo(0));
            }
            finally { graph.Destroy(); UnityEngine.Object.DestroyImmediate(clone); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReachedNonLoopingMorphTerminalsKeepTheOriginalNativeHeldPose(bool effectiveOverride)
        {
            DiscreteController();
            var state = controller.layers[0].stateMachine.states.Single(child => child.state.name == "Smile").state;
            var clip = (AnimationClip)state.motion;
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                AnimationCurve.Linear(0, 0, .5f, 75));
            RuntimeAnimatorController runtime = controller; AnimatorOverrideController overrides = null;
            if (effectiveOverride)
            {
                var replacement = Clip("Original effective terminal", 90, 0);
                AnimationUtility.SetEditorCurve(replacement, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                    AnimationCurve.Linear(0, 0, .5f, 90));
                overrides = new AnimatorOverrideController(controller); overrides[clip] = replacement; runtime = overrides;
            }
            var before = ExportSourceFingerprint.Compute(avatar); var serialized = EditorJsonUtility.ToJson(controller);
            try
            {
                var native = OriginalNativeSelectedPose(runtime);
                Assert.That(native.State.loop, Is.False); Assert.That(native.State.normalizedTime, Is.GreaterThan(1));
                Assert.That(native.Face, Is.EqualTo(effectiveOverride ? 90 : 75).Within(.01));
                var values = VrChatExpressionSampler.Sample(avatar, runtime, Params("Face", 0), Params("Face", 1));
                Assert.That(values.Single(value => value.Shape == "Face size").Weight, Is.EqualTo(native.Face).Within(.01));
                Assert.That(values.Single(value => value.Shape == "Blink").Weight, Is.EqualTo(native.Blink).Within(.01));
                Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before));
                Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(serialized));
            }
            finally { if (overrides != null) UnityEngine.Object.DestroyImmediate(overrides); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReachedConstantMorphTailsKeepTheNativePoseBeforeTheClipEnd(bool weighted)
        {
            DiscreteController();
            var state = controller.layers[0].stateMachine.states.Single(child => child.state.name == "Smile").state;
            var clip = (AnimationClip)state.motion;
            var start = new Keyframe(0, 0, 0, 0);
            var tail = new Keyframe(.5f, 75, 0, 0);
            var end = new Keyframe(5, 75, 0, 0);
            if (weighted)
            {
                tail.weightedMode = WeightedMode.Out; tail.outWeight = .8f;
                end.weightedMode = WeightedMode.In; end.inWeight = .2f;
            }
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                new AnimationCurve(start, tail, end));
            var before = ExportSourceFingerprint.Compute(avatar); var serialized = EditorJsonUtility.ToJson(controller);
            var native = OriginalNativeSelectedPose(controller);
            Assert.That(native.State.loop, Is.False);
            Assert.That(native.State.normalizedTime, Is.InRange(.1f, .9f), "The original native clock must still be before the clip end.");
            Assert.That(native.Face, Is.EqualTo(75).Within(.01));
            var values = VrChatExpressionSampler.Sample(avatar, controller, Params("Face", 0), Params("Face", 1));
            Assert.That(values.Single(value => value.Shape == "Face size").Weight, Is.EqualTo(native.Face).Within(.01));
            Assert.That(values.Single(value => value.Shape == "Blink").Weight, Is.EqualTo(native.Blink).Within(.01));
            Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(serialized));
        }

        [TestCase("ongoing segment")]
        [TestCase("loop")]
        [TestCase("future bend")]
        [TestCase("weighted tail bend")]
        [TestCase("wrapped short curve")]
        [TestCase("future SDK timer")]
        public void AReachedMorphTailCannotHideAFutureCurveOrCallback(string kind)
        {
            DiscreteController();
            var machine = controller.layers[0].stateMachine;
            var state = machine.states.Single(child => child.state.name == "Smile").state;
            var clip = (AnimationClip)state.motion;
            var curve = new AnimationCurve(new Keyframe(0, 0, 0, 0), new Keyframe(.5f, 75, 0, 0), new Keyframe(5, 75, 0, 0));
            if (kind == "ongoing segment") curve = AnimationCurve.Linear(0, 0, 5, 75);
            if (kind == "loop") { var settings = AnimationUtility.GetAnimationClipSettings(clip); settings.loopTime = true; AnimationUtility.SetAnimationClipSettings(clip, settings); }
            if (kind == "future bend") curve = new AnimationCurve(new Keyframe(0, 0, 0, 0), new Keyframe(.5f, 75, 0, 0),
                new Keyframe(2.5f, 75, 0, 0), new Keyframe(3, 30, 0, 0), new Keyframe(5, 30, 0, 0));
            if (kind == "weighted tail bend")
            {
                var keys = curve.keys; var tail = keys[1]; tail.outTangent = 2; tail.weightedMode = WeightedMode.Out; tail.outWeight = .8f;
                keys[1] = tail; curve.keys = keys;
            }
            if (kind == "wrapped short curve")
            {
                curve = new AnimationCurve(new Keyframe(0, 0, 0, 0), new Keyframe(.5f, 75, 0, 0), new Keyframe(1, 75, 0, 0));
                curve.postWrapMode = WrapMode.Loop;
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink"),
                    AnimationCurve.Constant(0, 5, 0));
            }
            if (kind == "future SDK timer")
            {
                var next = machine.AddState("Future SDK owner"); next.motion = Clip("Future SDK owner", 10, 0); next.writeDefaultValues = false;
                ParameterDriverExpressionTests.Driver(next, ParameterDriverExpressionTests.Op("Set", "Face", 0));
                var transition = state.AddTransition(next); transition.hasExitTime = true; transition.exitTime = 2; transition.duration = 0;
            }
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"), curve);
            var native = OriginalNativeSelectedPose(controller);
            Assert.That(native.State.normalizedTime, Is.InRange(.1f, .9f));
            if (kind == "future bend" || kind == "loop" || kind == "future SDK timer")
                Assert.That(native.Face, Is.EqualTo(75).Within(.01), "A currently constant native pose alone must not certify its future.");
            Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.Sample(avatar, controller, Params("Face", 0), Params("Face", 1)));
        }

        [TestCase("loop")]
        [TestCase("late curve")]
        [TestCase("speed input")]
        [TestCase("motion time")]
        [TestCase("cycle offset")]
        [TestCase("parameter curve")]
        [TestCase("future exit")]
        public void TerminalMorphProofCannotReplaceAnUnprovenNativeClockOrFutureState(string kind)
        {
            DiscreteController();
            var machine = controller.layers[0].stateMachine;
            var state = machine.states.Single(child => child.state.name == "Smile").state;
            var clip = (AnimationClip)state.motion;
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                AnimationCurve.Linear(0, 0, kind == "late curve" ? 10 : .5f, 75));
            if (kind == "loop") { var settings = AnimationUtility.GetAnimationClipSettings(clip); settings.loopTime = true; AnimationUtility.SetAnimationClipSettings(clip, settings); }
            if (kind == "speed input" || kind == "motion time" || kind == "parameter curve") controller.AddParameter("Clock", AnimatorControllerParameterType.Float);
            if (kind == "speed input") { state.speedParameterActive = true; state.speedParameter = "Clock"; }
            if (kind == "motion time") { state.timeParameterActive = true; state.timeParameter = "Clock"; }
            if (kind == "cycle offset") state.cycleOffset = .25f;
            if (kind == "parameter curve") AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "Clock"), AnimationCurve.Linear(0, 0, .5f, 1));
            if (kind == "future exit") { var exit = state.AddTransition(machine.defaultState); exit.hasExitTime = true; exit.exitTime = 10; exit.duration = 0; }
            Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.Sample(avatar, controller, Params("Face", 0), Params("Face", 1)));
        }

        [TestCase("constant override", true)]
        [TestCase("terminal override", true)]
        [TestCase("fractional override", false)]
        [TestCase("additive", false)]
        [TestCase("unbound channel", false)]
        public void SelectedNativeUpperWritersDominateOnlyTheirExactLowerMorphStream(string kind, bool supported)
        {
            DiscreteController();
            var layers = controller.layers;
            var moving = Clip("Original lower loop", 0, 0);
            AnimationUtility.SetEditorCurve(moving, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"), AnimationCurve.Linear(0, 0, .8f, 40));
            if (kind == "unbound channel") AnimationUtility.SetEditorCurve(moving, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink"), AnimationCurve.Linear(0, 0, .8f, 60));
            var settings = AnimationUtility.GetAnimationClipSettings(moving); settings.loopTime = true; AnimationUtility.SetAnimationClipSettings(moving, settings);
            var lower = new AnimatorStateMachine { name = "Original lower moving support" }; AssetDatabase.AddObjectToAsset(lower, controller);
            var idle = lower.AddState("Original moving rest"); idle.motion = moving; idle.writeDefaultValues = false; lower.defaultState = idle;
            var upper = layers[0]; upper.name = "Selected upper face"; upper.defaultWeight = kind == "fractional override" ? .5f : 1;
            upper.blendingMode = kind == "additive" ? AnimatorLayerBlendingMode.Additive : AnimatorLayerBlendingMode.Override;
            if (kind == "terminal override")
                AnimationUtility.SetEditorCurve((AnimationClip)upper.stateMachine.states.Single(child => child.state.name == "Smile").state.motion,
                    EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"), AnimationCurve.Linear(0, 0, .5f, 75));
            if (kind == "unbound channel")
                AnimationUtility.SetEditorCurve((AnimationClip)upper.stateMachine.states.Single(child => child.state.name == "Smile").state.motion,
                    EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink"), null);
            controller.layers = new[] { new AnimatorControllerLayer { name = lower.name, stateMachine = lower, defaultWeight = 1 }, upper };
            if (!supported)
            { Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.Sample(avatar, controller, Params("Face", 0), Params("Face", 1))); return; }
            var native = OriginalNativeSelectedPose(controller);
            Assert.That(native.Face, Is.EqualTo(75).Within(.01));
            var values = VrChatExpressionSampler.Sample(avatar, controller, Params("Face", 0), Params("Face", 1));
            Assert.That(values.Single(value => value.Shape == "Face size").Weight, Is.EqualTo(native.Face).Within(.01));
        }

        [Test]
        public void BlendTreeUsesTheMenuValueInsteadOfPublishingItsIndividualShapes()
        {
            controller.AddParameter("Strength", AnimatorControllerParameterType.Float);
            var tree = new BlendTree { name = "Composed smile", blendType = BlendTreeType.Simple1D, blendParameter = "Strength", useAutomaticThresholds = false };
            AssetDatabase.AddObjectToAsset(tree, controller);
            tree.AddChild(Clip("Off", 0, 0), 0);
            tree.AddChild(Clip("On", 80, 100), 1);
            var state = controller.layers[0].stateMachine.AddState("Smile");
            state.motion = tree;
            controller.layers[0].stateMachine.defaultState = state;
            var values = VrChatExpressionSampler.Sample(avatar, controller, Params("Strength", 0), Params("Strength", .5f));
            Assert.That(values.Single(v => v.Shape == "Face size").Weight, Is.EqualTo(40).Within(.01));
            Assert.That(values.Single(v => v.Shape == "Blink").Weight, Is.EqualTo(50).Within(.01));
        }

        [Test]
        public void UnsupportedVisibilityIsReportedInsteadOfDroppingHalfAnExpression()
        {
            DiscreteController();
            var clip = (AnimationClip)controller.layers[0].stateMachine.states.Single(s => s.state.name == "Smile").state.motion;
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 0));
            Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.Sample(avatar, controller, Params("Face", 0), Params("Face", 1)));
        }

        [Test]
        public void DuplicateHierarchyPathsCannotCaptureTheWrongFace()
        {
            DiscreteController();
            var duplicate = new GameObject("Face");
            duplicate.transform.SetParent(avatar.transform, false);
            Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.Sample(avatar, controller, Params("Face", 0), Params("Face", 1)));
        }

        [Test]
        public void MenuTraversalKeepsLabelsAncestorGatesAndReportsPuppets()
        {
            MenuTraversalFixture.Run((condition, description) => Assert.That(condition, Is.True, description));
        }

        [Test]
        public void GestureClipKeepsTheAuthoredCombinationDespiteAutomaticBlink()
        {
            DiscreteController();
            controller.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
            var machine = controller.layers[0].stateMachine;
            var idle = machine.states.Single(s => s.state.name == "Idle").state;
            var smile = machine.states.Single(s => s.state.name == "Smile").state;
            var gesture = idle.AddTransition(smile);
            gesture.AddCondition(AnimatorConditionMode.Equals, 2, "GestureRight");
            var blink = machine.AddState("EyeClose");
            blink.motion = Clip("AutomaticBlink", 0, 100);
            var timed = idle.AddTransition(blink);
            timed.hasExitTime = true;
            timed.exitTime = 10;
            var source = new VrChatExpressionMenu.Source { Controller = controller };
            VrChatGestureExpressions.Add(avatar, source);
            Assert.That(source.Entries, Has.Count.EqualTo(1));
            var expression = source.Entries.Single();
            Assert.That(expression.Error, Is.Null);
            Assert.That(expression.Name, Is.EqualTo("ジェスチャー / Smile"));
            Assert.That(expression.Values.Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75));
            Assert.That(expression.Values.Single(v => v.Shape == "Blink").Weight, Is.EqualTo(0));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(25));
        }

        [Test]
        public void GestureClipReadsOverridesAndDeduplicatesSharedAnimations()
        {
            DiscreteController();
            controller.AddParameter("GestureLeft", AnimatorControllerParameterType.Int);
            var machine = controller.layers[0].stateMachine;
            var smile = machine.states.Single(s => s.state.name == "Smile").state;
            var same = machine.AddState("Same smile");
            same.motion = smile.motion;
            foreach (var state in new[] { smile, same })
                machine.AddAnyStateTransition(state).AddCondition(AnimatorConditionMode.Equals, 3, "GestureLeft");
            var overrides = new AnimatorOverrideController(controller);
            try
            {
                overrides[(AnimationClip)smile.motion] = Clip("My smile", 42, 80);
                var source = new VrChatExpressionMenu.Source { Controller = overrides };
                VrChatGestureExpressions.Add(avatar, source);
                Assert.That(source.Entries, Has.Count.EqualTo(1));
                Assert.That(source.Entries.Single().Name, Is.EqualTo("ジェスチャー / My smile"));
                Assert.That(source.Entries.Single().Values.Single(v => v.Shape == "Face size").Weight, Is.EqualTo(42));
            }
            finally { UnityEngine.Object.DestroyImmediate(overrides); }
        }

        [Test]
        public void GestureClipKeepsChangingCurvesButRejectsPartialMaterialFaces()
        {
            var clip = Clip("Smile", 75, 0);
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink"),
                AnimationCurve.Linear(0, 0, 10, 100));
            Assert.That(VrChatGestureExpressions.ReadPose(avatar, clip).Single(v => v.Shape == "Blink").Weight, Is.Zero);
            DiscreteController();
            controller.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
            var machine = controller.layers[0].stateMachine;
            var state = machine.AddState("Animated cry"); state.motion = clip;
            machine.AddAnyStateTransition(state).AddCondition(AnimatorConditionMode.Equals, 2, "GestureRight");
            var source = new VrChatExpressionMenu.Source { Controller = controller };
            VrChatGestureExpressions.Add(avatar, source);
            var expression = source.Entries.Single();
            Assert.That(expression.Error, Is.Null);
            Assert.That(expression.Animation, Has.Count.EqualTo(1));
            Assert.That(expression.Animation[0].Curve.Evaluate(5), Is.EqualTo(50).Within(.0001));
            Assert.That(expression.Duration, Is.EqualTo(10));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink"),
                AnimationCurve.Constant(0, 1, 0));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(GameObject), "m_IsActive"),
                AnimationCurve.Constant(0, 1, 0));
            Assert.Throws<InvalidOperationException>(() => VrChatGestureExpressions.ReadPose(avatar, clip));
        }

        [Test]
        public void PortableCurveMatchesUnityWeightedAndSteppedAnimation()
        {
            var a = new Keyframe(0, -20, 0, 180, .25f, .8f) { weightedMode = WeightedMode.Both };
            var b = new Keyframe(1, 80, -30, 0, .1f, .25f) { weightedMode = WeightedMode.Both };
            var curve = new AnimationCurve(a, b) { preWrapMode = WrapMode.Loop, postWrapMode = WrapMode.PingPong };
            var portable = VrChatGestureExpressions.ReadCurve(curve);
            for (var i = -50; i <= 250; i++)
                Assert.That(portable.Evaluate(i / 100f), Is.EqualTo(curve.Evaluate(i / 100f)).Within(.01), "time=" + i / 100f);
            a.outTangent = float.PositiveInfinity;
            curve = new AnimationCurve(a, b);
            portable = VrChatGestureExpressions.ReadCurve(curve);
            foreach (var time in new[] { 0f, .5f, .999f, 1f })
                Assert.That(portable.Evaluate(time), Is.EqualTo(curve.Evaluate(time)).Within(.01));
        }

        [Test] public void AnimatedExpressionDataContracts() => AnimatedExpressionFixture.Run((ok, message) => Assert.IsTrue(ok, message));

        [TestCase(false)]
        [TestCase(true)]
        public void GestureSubmachineUsesEntryPathsWithoutIncludingUnrelatedBlink(bool overrideEntry)
        {
            DiscreteController();
            controller.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
            var machine = controller.layers[0].stateMachine;
            var idle = machine.states.Single(s => s.state.name == "Idle").state;
            var sub = machine.AddStateMachine("Gesture faces");
            var smile = sub.AddState("Default smile");
            smile.motion = Clip("Default smile", 75, 0);
            sub.defaultState = smile;
            var blink = sub.AddState("Automatic blink");
            blink.motion = Clip("Unrelated blink", 0, 100);
            smile.AddTransition(blink).hasExitTime = true;
            if (overrideEntry)
            {
                var angry = sub.AddState("Entry face");
                angry.motion = Clip("Entry face", 42, 0);
                sub.AddEntryTransition(angry); // unconditional, takes precedence over default
            }
            idle.AddTransition(sub).AddCondition(AnimatorConditionMode.Equals, 3, "GestureRight");
            var source = new VrChatExpressionMenu.Source { Controller = controller };
            VrChatGestureExpressions.Add(avatar, source);
            Assert.That(source.Entries, Has.Count.EqualTo(1));
            Assert.That(source.Entries[0].Name, Is.EqualTo("ジェスチャー / " + (overrideEntry ? "Entry face" : "Default smile")));
        }

        [Test]
        public void GestureSyncedLayerUsesItsOverrideBeforeControllerReplacements()
        {
            DiscreteController();
            controller.AddParameter("GestureLeft", AnimatorControllerParameterType.Int);
            var machine = controller.layers[0].stateMachine;
            var smile = machine.states.Single(s => s.state.name == "Smile").state;
            machine.AddAnyStateTransition(smile).AddCondition(AnimatorConditionMode.Equals, 2, "GestureLeft");
            controller.AddLayer("Alternate face");
            var layers = controller.layers;
            layers[1].syncedLayerIndex = 0;
            controller.layers = layers;
            var alternate = Clip("Synced smile", 42, 80);
            controller.SetStateEffectiveMotion(smile, alternate, 1);
            var overrides = new AnimatorOverrideController(controller);
            try
            {
                overrides[alternate] = Clip("Final smile", 60, 30);
                var source = new VrChatExpressionMenu.Source { Controller = overrides };
                VrChatGestureExpressions.Add(avatar, source);
                Assert.That(source.Entries, Has.Count.EqualTo(2));
                var expression = source.Entries.Single(e => e.Name == "ジェスチャー / Final smile");
                Assert.That(expression.Values.Single(v => v.Shape == "Face size").Weight, Is.EqualTo(60));
                Assert.That(expression.Values.Single(v => v.Shape == "Blink").Weight, Is.EqualTo(30));
            }
            finally { UnityEngine.Object.DestroyImmediate(overrides); }
        }

        [TestCase("state")]
        [TestCase("any")]
        [TestCase("entry")]
        [TestCase("machine")]
        public void GestureDiscoveryExcludesNonSoloSiblings(string kind)
        {
            DiscreteController();
            controller.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
            var machine = controller.layers[0].stateMachine;
            var idle = machine.states.Single(s => s.state.name == "Idle").state;
            var smile = machine.states.Single(s => s.state.name == "Smile").state;
            AnimatorTransitionBase inactive, solo;
            if (kind == "any")
            {
                inactive = machine.AddAnyStateTransition(smile);
                solo = machine.AddAnyStateTransition(idle);
            }
            else if (kind == "entry")
            {
                inactive = machine.AddEntryTransition(smile);
                solo = machine.AddEntryTransition(idle);
            }
            else if (kind == "machine")
            {
                var sub = machine.AddStateMachine("Source");
                sub.AddState("Default");
                inactive = machine.AddStateMachineTransition(sub, smile);
                solo = machine.AddStateMachineTransition(sub, idle);
            }
            else
            {
                inactive = idle.AddTransition(smile);
                solo = idle.AddTransition(idle);
            }
            inactive.AddCondition(AnimatorConditionMode.Equals, 3, "GestureRight");
            solo.solo = true;
            var source = new VrChatExpressionMenu.Source { Controller = controller };
            VrChatGestureExpressions.Add(avatar, source);
            Assert.That(source.Entries, Is.Empty);
            solo.mute = true;
            source = new VrChatExpressionMenu.Source { Controller = controller };
            VrChatGestureExpressions.Add(avatar, source);
            Assert.That(source.Entries, Is.Empty, "Muting the solo transition does not enable non-solo siblings.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void GestureExitReachesTheParentExpression(bool nestedExit)
        {
            DiscreteController();
            controller.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
            var root = controller.layers[0].stateMachine;
            var smile = root.states.Single(s => s.state.name == "Smile").state;
            var outer = root.AddStateMachine("Outer");
            var sourceMachine = outer;
            if (nestedExit)
            {
                sourceMachine = outer.AddStateMachine("Inner");
                outer.AddStateMachineExitTransition(sourceMachine);
            }
            var gate = sourceMachine.AddState("Gesture gate");
            sourceMachine.defaultState = gate;
            gate.AddExitTransition().AddCondition(AnimatorConditionMode.Equals, 3, "GestureRight");
            root.AddStateMachineTransition(outer, smile);
            var source = new VrChatExpressionMenu.Source { Controller = controller };
            VrChatGestureExpressions.Add(avatar, source);
            Assert.That(source.Entries, Has.Count.EqualTo(1));
            Assert.That(source.Entries[0].Name, Is.EqualTo("ジェスチャー / Smile"));
            Assert.That(source.Entries[0].Values.Single(v => v.Shape == "Face size").Weight, Is.EqualTo(75));
        }

        [Test]
        public void GestureBlendTreeLeavesAreNotRegisteredAsSeparateExpressions()
        {
            controller.AddParameter("GestureLeft", AnimatorControllerParameterType.Int);
            var tree = new BlendTree { name = "Composed gesture" };
            AssetDatabase.AddObjectToAsset(tree, controller);
            tree.AddChild(Clip("Part A", 75, 0));
            tree.AddChild(Clip("Part B", 0, 50));
            var machine = controller.layers[0].stateMachine;
            var state = machine.AddState("Composed");
            state.motion = tree;
            machine.AddAnyStateTransition(state).AddCondition(AnimatorConditionMode.Equals, 3, "GestureLeft");
            var source = new VrChatExpressionMenu.Source { Controller = controller };
            VrChatGestureExpressions.Add(avatar, source);
            Assert.That(source.Entries, Has.Count.EqualTo(1));
            Assert.That(source.Entries[0].Error, Does.Contain("BlendTree"));
            Assert.That(source.Entries[0].Values, Is.Empty);
        }

        [Test]
        public void GlbRegistrationKeepsComposedExpressions()
        {
            MenuExpressionFixture.Run((condition, description) => Assert.That(condition, Is.True, description));
        }

        private void BasisMesh(int count, params (string Name, Vector3 Position, Vector3 Normal, Vector3 Tangent)[] shapes)
        {
            UnityEngine.Object.DestroyImmediate(mesh);
            mesh = new Mesh { name = "Exact basis fixture", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            var positions = new Vector3[count]; positions[1] = Vector3.right; positions[2] = Vector3.up;
            mesh.vertices = positions; mesh.normals = Enumerable.Repeat(Vector3.up, count).ToArray();
            mesh.tangents = Enumerable.Repeat(new Vector4(1, 0, 0, 1), count).ToArray(); mesh.triangles = new[] { 0, 1, 2 };
            foreach (var shape in shapes)
                mesh.AddBlendShapeFrame(shape.Name, 100, Enumerable.Repeat(shape.Position, count).ToArray(),
                    Enumerable.Repeat(shape.Normal, count).ToArray(), Enumerable.Repeat(shape.Tangent, count).ToArray());
            var skin = avatar.GetComponentInChildren<SkinnedMeshRenderer>(); skin.sharedMesh = mesh;
            for (var shape = 0; shape < mesh.blendShapeCount; shape++) skin.SetBlendShapeWeight(shape, 0);
        }

        private static VrChatExpressionMenu.Entry BasisEntry(string name, string shape, double duration = 1, bool animation = true, string path = "Face", double maximum = 100)
        {
            var entry = new VrChatExpressionMenu.Entry { Id = name, Name = name, Duration = duration, Loop = duration == 1 };
            entry.Values.Add(new VrChatExpressionMenu.MorphValue { Path = path, Shape = shape, Weight = animation ? 0 : 100 });
            if (animation)
            {
                var curve = new ExpressionAnimationData.Curve();
                curve.Keys.Add(new ExpressionAnimationData.Key { Time = 0, Value = 0, InTangent = maximum / duration, OutTangent = maximum / duration });
                curve.Keys.Add(new ExpressionAnimationData.Key { Time = duration, Value = maximum, InTangent = maximum / duration, OutTangent = maximum / duration });
                entry.Animation.Add(new VrChatExpressionMenu.AnimatedMorph { Path = path, Shape = shape, Curve = curve });
            }
            return entry;
        }

        private static string BasisTarget(VrmMenuExpressions.Expression expression) =>
            expression.Animation.Channels.SelectMany(channel => channel.Points).First(point => point.Target != null).Target;

        private static void ApplyBasis(SkinnedMeshRenderer skin, VrmMenuExpressions.Expression expression, double time)
        {
            for (var shape = 0; shape < skin.sharedMesh.blendShapeCount; shape++) skin.SetBlendShapeWeight(shape, 0);
            foreach (var name in expression.Targets) skin.SetBlendShapeWeight(skin.sharedMesh.GetBlendShapeIndex(name), 100);
            foreach (var channel in expression.Animation.Channels)
            {
                channel.Weights(time, out var left, out var right, out var blend);
                if (channel.Points[left].Target != null) skin.SetBlendShapeWeight(skin.sharedMesh.GetBlendShapeIndex(channel.Points[left].Target), 100 * (1 - blend));
                if (channel.Points[right].Target != null) skin.SetBlendShapeWeight(skin.sharedMesh.GetBlendShapeIndex(channel.Points[right].Target), 100 * blend);
            }
        }

        private static void BakeAuthoredNegativeBracket(Mesh source, int shape, double value, Mesh baked)
        {
            // EditMode can retain native clamping after the declared policy is
            // changed. Read the original authored bracket independently, then
            // use a fresh positive-100 frame as the native geometry witness.
            // Its coefficient is in range under either native policy.
            Assert.That(source.GetBlendShapeFrameWeight(shape, 0), Is.EqualTo(-100));
            Assert.That(source.GetBlendShapeFrameWeight(shape, 1), Is.EqualTo(50));
            Assert.That(value, Is.InRange(-100d, 0d));
            var lowV = new Vector3[source.vertexCount]; var lowN = new Vector3[source.vertexCount]; var lowT = new Vector3[source.vertexCount];
            var highV = new Vector3[source.vertexCount]; var highN = new Vector3[source.vertexCount]; var highT = new Vector3[source.vertexCount];
            source.GetBlendShapeFrameVertices(shape, 0, lowV, lowN, lowT);
            source.GetBlendShapeFrameVertices(shape, 1, highV, highN, highT);
            var alpha = (float)((value + 100) / 150);
            for (var vertex = 0; vertex < source.vertexCount; vertex++)
            {
                lowV[vertex] = Vector3.LerpUnclamped(lowV[vertex], highV[vertex], alpha);
                lowN[vertex] = Vector3.LerpUnclamped(lowN[vertex], highN[vertex], alpha);
                lowT[vertex] = Vector3.LerpUnclamped(lowT[vertex], highT[vertex], alpha);
            }
            Assert.That(lowV[0].x, Is.LessThan(-.001f), "The independently interpolated original authored frames must have a nonzero negative displacement.");
            var witness = new Mesh { vertices = source.vertices, normals = source.normals, tangents = source.tangents, triangles = source.triangles };
            var root = new GameObject("Original authored bracket witness", typeof(SkinnedMeshRenderer));
            try
            {
                witness.AddBlendShapeFrame("Authored sample", 100, lowV, lowN, lowT);
                var skin = root.GetComponent<SkinnedMeshRenderer>(); skin.sharedMesh = witness; skin.SetBlendShapeWeight(0, 100);
                skin.BakeMesh(baked);
                Assert.That(baked.vertices[0].x, Is.LessThan(-.001f), "The independent in-range native witness must retain the signed authored displacement.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(witness); }
        }

        private static void AssertSignedBasisAgainstAuthoredFrames(Mesh source, int shape, Mesh target, ExpressionAnimationData.Channel channel)
        {
            var restV = new Vector3[source.vertexCount]; var restN = new Vector3[source.vertexCount]; var restT = new Vector3[source.vertexCount];
            var middleV = new Vector3[source.vertexCount]; var middleN = new Vector3[source.vertexCount]; var middleT = new Vector3[source.vertexCount];
            source.GetBlendShapeFrameVertices(shape, 0, restV, restN, restT);
            source.GetBlendShapeFrameVertices(shape, 1, middleV, middleN, middleT);
            for (var vertex = 0; vertex < source.vertexCount; vertex++)
            {
                restV[vertex] = Vector3.LerpUnclamped(restV[vertex], middleV[vertex], 2f / 3f);
                restN[vertex] = Vector3.LerpUnclamped(restN[vertex], middleN[vertex], 2f / 3f);
                restT[vertex] = Vector3.LerpUnclamped(restT[vertex], middleT[vertex], 2f / 3f);
            }
            foreach (var point in channel.Points)
            {
                if (point.Value == 0) { Assert.That(point.Target, Is.Null); continue; }
                var frame = Enumerable.Range(0, source.GetBlendShapeFrameCount(shape)).Single(index => source.GetBlendShapeFrameWeight(shape, index) == point.Value);
                var expectedV = new Vector3[source.vertexCount]; var expectedN = new Vector3[source.vertexCount]; var expectedT = new Vector3[source.vertexCount];
                var actualV = new Vector3[source.vertexCount]; var actualN = new Vector3[source.vertexCount]; var actualT = new Vector3[source.vertexCount];
                source.GetBlendShapeFrameVertices(shape, frame, expectedV, expectedN, expectedT);
                target.GetBlendShapeFrameVertices(target.GetBlendShapeIndex(point.Target), 0, actualV, actualN, actualT);
                for (var vertex = 0; vertex < source.vertexCount; vertex++)
                {
                    Assert.That(Vector3.Distance(actualV[vertex], expectedV[vertex] - restV[vertex]), Is.LessThan(.0000001f));
                    Assert.That(Vector3.Distance(actualN[vertex], expectedN[vertex] - restN[vertex]), Is.LessThan(.0000001f));
                    Assert.That(Vector3.Distance(actualT[vertex], expectedT[vertex] - restT[vertex]), Is.LessThan(.0000001f));
                }
                if (point.Value < 0) Assert.That(actualV[0].x, Is.LessThan(-.009f), "The stored signed basis must retain the original negative frame's nonzero residual.");
            }
        }

        [TestCase("alias")]
        [TestCase("position")]
        [TestCase("normal")]
        [TestCase("tangent")]
        [TestCase("near position")]
        public void MutuallyExclusiveAnimationsReuseOnlyExactStoredPnt(string kind)
        {
            var position = Vector3.right * .01f; var normal = Vector3.up * .02f; var tangent = Vector3.forward * .03f;
            BasisMesh(3, ("First", position, normal, tangent), ("Other", position + (kind == "position" ? Vector3.up * .01f :
                kind == "near position" ? Vector3.right * .000001f : Vector3.zero),
                normal + (kind == "normal" ? Vector3.right * .01f : Vector3.zero), tangent + (kind == "tangent" ? Vector3.up * .01f : Vector3.zero)));
            if (kind == "alias")
            {
                mesh.ClearBlendShapes();
                foreach (var name in new[] { "First", "Other" })
                    foreach (var weight in new[] { -100f, 50f, 100f })
                        mesh.AddBlendShapeFrame(name, weight, Enumerable.Repeat(position * (weight / 100), 3).ToArray(),
                            Enumerable.Repeat(normal * (weight / 100), 3).ToArray(), Enumerable.Repeat(tangent * (weight / 100), 3).ToArray());
            }
            var menu = new VrChatExpressionMenu.Source(); menu.Entries.Add(BasisEntry("First face", "First")); menu.Entries.Add(BasisEntry("Other face", "Other", 2));
            if (kind == "alias")
                foreach (var entry in menu.Entries)
                {
                    var curve = entry.Animation.Single().Curve;
                    curve.Keys.ForEach(key => { key.InTangent = 0; key.OutTangent = 0; });
                    curve.Keys.Insert(1, new ExpressionAnimationData.Key { Time = entry.Duration / 2, Value = -100, InTangent = 0, OutTangent = 0 });
                }
            var before = ExportSourceFingerprint.Compute(avatar); var clone = UnityEngine.Object.Instantiate(avatar); var owned = new List<Mesh>();
            var native = new Mesh(); var baked = new Mesh();
            var clamp = PlayerSettings.legacyClampBlendShapeWeights;
            try
            {
                PlayerSettings.legacyClampBlendShapeWeights = false;
                var expressions = VrChatExpressionBaker.Bake(avatar, clone, menu, owned, null);
                Assert.That(expressions.Count, Is.EqualTo(2));
                Assert.That(BasisTarget(expressions[0]) == BasisTarget(expressions[1]), Is.EqualTo(kind == "alias"));
                var original = avatar.GetComponentInChildren<SkinnedMeshRenderer>(); var copy = clone.GetComponentInChildren<SkinnedMeshRenderer>();
                for (var row = 0; row < 2; row++)
                {
                    Assert.That(expressions[row].Animation.Duration, Is.EqualTo(menu.Entries[row].Duration));
                    Assert.That(expressions[row].Animation.Loop, Is.EqualTo(menu.Entries[row].Loop));
                    Assert.That(expressions[row].Animation.Channels.Single().Curve, Is.SameAs(menu.Entries[row].Animation.Single().Curve));
                    if (kind == "alias")
                    {
                        Assert.That(expressions[row].Animation.Channels.Single().Points.Select(point => point.Value), Is.EqualTo(new[] { -100d, 0, 50, 100 }));
                        AssertSignedBasisAgainstAuthoredFrames(mesh, row, copy.sharedMesh, expressions[row].Animation.Channels.Single());
                    }
                    foreach (var time in new[] { 0d, menu.Entries[row].Duration * .25, menu.Entries[row].Duration })
                    {
                        original.SetBlendShapeWeight(0, 0); original.SetBlendShapeWeight(1, 0);
                        var authoredWeight = menu.Entries[row].Animation.Single().Curve.Evaluate(time);
                        original.SetBlendShapeWeight(row, (float)authoredWeight); original.BakeMesh(native);
                        if (kind == "alias" && authoredWeight < 0)
                        {
                            Assert.That(original.GetBlendShapeWeight(row), Is.LessThan(-1), "The original renderer retains the signed authored scalar.");
                            BakeAuthoredNegativeBracket(mesh, row, authoredWeight, native);
                        }
                        ApplyBasis(copy, expressions[row], time); copy.BakeMesh(baked);
                        for (var vertex = 0; vertex < native.vertexCount; vertex++)
                        {
                            Assert.That(Vector3.Distance(native.vertices[vertex], baked.vertices[vertex]), Is.LessThan(.000001f));
                            Assert.That(Vector3.Distance(native.normals[vertex], baked.normals[vertex]), Is.LessThan(.000001f));
                            Assert.That(Vector4.Distance(native.tangents[vertex], baked.tangents[vertex]), Is.LessThan(.000001f));
                        }
                    }
                    expressions[row].Animation.Expression = expressions[row].Name;
                }
                Assert.That(ExpressionAnimationData.Read(ExpressionAnimationData.Write(expressions.Select(value => value.Animation).ToArray())).Count, Is.EqualTo(2));
                original.SetBlendShapeWeight(0, 0); original.SetBlendShapeWeight(1, 0);
                Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before)); Assert.That(mesh.blendShapeCount, Is.EqualTo(2));
            }
            finally { PlayerSettings.legacyClampBlendShapeWeights = clamp; UnityEngine.Object.DestroyImmediate(native); UnityEngine.Object.DestroyImmediate(baked); UnityEngine.Object.DestroyImmediate(clone); foreach (var item in owned) UnityEngine.Object.DestroyImmediate(item); }
        }

        [TestCase("channels")]
        [TestCase("knots")]
        public void AnimationProgramsKeepUniqueBasisNamesWithinEachFace(string kind)
        {
            BasisMesh(3, ("First", Vector3.right * .01f, Vector3.up * .02f, Vector3.forward * .03f),
                ("Alias", Vector3.right * .01f, Vector3.up * .02f, Vector3.forward * .03f));
            var menu = new VrChatExpressionMenu.Source(); var entry = BasisEntry("Combined face", "First", maximum: kind == "knots" ? 200 : 100);
            if (kind == "channels") entry.Animation.Add(BasisEntry("Alias", "Alias").Animation.Single()); menu.Entries.Add(entry);
            var clone = UnityEngine.Object.Instantiate(avatar); var owned = new List<Mesh>(); var clamp = PlayerSettings.legacyClampBlendShapeWeights;
            try
            {
                PlayerSettings.legacyClampBlendShapeWeights = true;
                var expression = VrChatExpressionBaker.Bake(avatar, clone, menu, owned, null).Single();
                var targets = expression.Animation.Channels.SelectMany(channel => channel.Points).Where(point => point.Target != null).Select(point => point.Target).ToArray();
                Assert.That(targets.Length, Is.EqualTo(2)); Assert.That(targets.Distinct().Count(), Is.EqualTo(2));
                expression.Animation.Expression = expression.Name;
                Assert.That(ExpressionAnimationData.Read(ExpressionAnimationData.Write(new[] { expression.Animation })).Count, Is.EqualTo(1));
                var target = clone.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh;
                var first = new Vector3[3]; var second = new Vector3[3]; var n = new Vector3[3]; var t = new Vector3[3];
                target.GetBlendShapeFrameVertices(target.GetBlendShapeIndex(targets[0]), 0, first, n, t);
                target.GetBlendShapeFrameVertices(target.GetBlendShapeIndex(targets[1]), 0, second, n, t);
                Assert.That(first, Is.EqualTo(second), "Even identical stored basis deltas must retain separate identities inside one face.");
            }
            finally { PlayerSettings.legacyClampBlendShapeWeights = clamp; UnityEngine.Object.DestroyImmediate(clone); foreach (var item in owned) UnityEngine.Object.DestroyImmediate(item); }
        }

        [Test]
        public void EqualScalarPosesRetainIndependentSimultaneousTargets()
        {
            BasisMesh(3, ("First", Vector3.right * .01f, Vector3.zero, Vector3.zero));
            var menu = new VrChatExpressionMenu.Source(); menu.Entries.Add(BasisEntry("First row", "First", animation: false)); menu.Entries.Add(BasisEntry("Second row", "First", animation: false));
            var clone = UnityEngine.Object.Instantiate(avatar); var owned = new List<Mesh>(); var native = new Mesh();
            try
            {
                var expressions = VrChatExpressionBaker.Bake(avatar, clone, menu, owned, null);
                Assert.That(expressions.SelectMany(expression => expression.Targets).Distinct().Count(), Is.EqualTo(2));
                var skin = clone.GetComponentInChildren<SkinnedMeshRenderer>();
                foreach (var target in expressions.SelectMany(expression => expression.Targets)) skin.SetBlendShapeWeight(skin.sharedMesh.GetBlendShapeIndex(target), 100);
                skin.BakeMesh(native); Assert.That(native.vertices[0].x, Is.EqualTo(.02f).Within(.000001f));
                Assert.That(expressions.All(expression => expression.Animation == null), Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(native); UnityEngine.Object.DestroyImmediate(clone); foreach (var item in owned) UnityEngine.Object.DestroyImmediate(item); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ExactZeroScalarMarkersKeepAllExpressionSettingsAndNativeCoactivation(bool clamp)
        {
            BasisMesh(3, ("Face", Vector3.right * .01f, Vector3.zero, Vector3.zero));
            var menu = new VrChatExpressionMenu.Source();
            for (var row = 0; row < 4; row++)
            {
                var entry = BasisEntry("Face " + row, "Face", animation: false);
                if (row < 2) entry.Values.Single().Weight = 0;
                menu.Entries.Add(entry);
            }
            var before = ExportSourceFingerprint.Compute(avatar); var clone = UnityEngine.Object.Instantiate(avatar);
            var owned = new List<Mesh>(); var native = new Mesh(); var oldClamp = PlayerSettings.legacyClampBlendShapeWeights;
            try
            {
                PlayerSettings.legacyClampBlendShapeWeights = clamp;
                var expressions = VrChatExpressionBaker.Bake(avatar, clone, menu, owned, null);
                Assert.That(expressions.Count, Is.EqualTo(4));
                Assert.That(expressions[0].Targets.Single(), Is.EqualTo(expressions[1].Targets.Single()));
                Assert.That(expressions[2].Targets.Single(), Is.Not.EqualTo(expressions[3].Targets.Single()));
                Assert.That(expressions.SelectMany(expression => expression.Targets).Distinct().Count(), Is.EqualTo(3));
                var skin = clone.GetComponentInChildren<SkinnedMeshRenderer>();
                var marker = skin.sharedMesh.GetBlendShapeIndex(expressions[0].Targets.Single());
                var vertices = new Vector3[3]; var normals = new Vector3[3]; var tangents = new Vector3[3];
                skin.sharedMesh.GetBlendShapeFrameVertices(marker, 0, vertices, normals, tangents);
                Assert.That(vertices.Concat(normals).Concat(tangents).All(value => value.x == 0 && value.y == 0 && value.z == 0), Is.True);
                // The pinned UniVRM merger sums coactivated bindings on the
                // shared index. A zero marker is unchanged even above100%.
                foreach (var weight in new[] { 200f, 1000000f })
                {
                    skin.SetBlendShapeWeight(marker, weight); skin.BakeMesh(native);
                    Assert.That(native.vertices, Is.EqualTo(mesh.vertices));
                    Assert.That(native.normals, Is.EqualTo(mesh.normals));
                    Assert.That(native.tangents, Is.EqualTo(mesh.tangents));
                }
                foreach (var expression in expressions.Skip(2))
                    skin.SetBlendShapeWeight(skin.sharedMesh.GetBlendShapeIndex(expression.Targets.Single()), 100);
                skin.BakeMesh(native); Assert.That(native.vertices[0].x, Is.EqualTo(.02f).Within(.000001f));

                var names = Enumerable.Range(0, skin.sharedMesh.blendShapeCount).Select(skin.sharedMesh.GetBlendShapeName).Cast<object>().ToList();
                var custom = new Dictionary<string, object>();
                var root = new Dictionary<string, object>
                {
                    ["asset"] = new Dictionary<string, object> { ["version"] = "2.0" },
                    ["nodes"] = new List<object> { new Dictionary<string, object> { ["mesh"] = 0L } },
                    ["meshes"] = new List<object> { new Dictionary<string, object>
                        { ["extras"] = new Dictionary<string, object> { ["targetNames"] = names },
                          ["primitives"] = new List<object> { new Dictionary<string, object>
                              { ["targets"] = names.Select(_ => (object)new Dictionary<string, object>()).ToList() } } } },
                    ["extensions"] = new Dictionary<string, object> { ["VRMC_vrm"] = new Dictionary<string, object>
                        { ["expressions"] = new Dictionary<string, object> { ["custom"] = custom } } }
                };
                var bytes = VrmMenuExpressions.Add(GlbDocument.Create(root, Array.Empty<byte>()).Write(), expressions);
                Assert.That(VrmMenuExpressions.CountRegistered(bytes), Is.EqualTo(4));
                var document = GlbDocument.Read(bytes).Json;
                custom = (Dictionary<string, object>)((Dictionary<string, object>)((Dictionary<string, object>)((Dictionary<string, object>)document["extensions"])["VRMC_vrm"])["expressions"])["custom"];
                Assert.That(custom.Count, Is.EqualTo(4));
                foreach (Dictionary<string, object> expression in custom.Values)
                {
                    Assert.That(expression["isBinary"], Is.True);
                    foreach (var key in new[] { "overrideBlink", "overrideMouth", "overrideLookAt" }) Assert.That(expression[key], Is.EqualTo("block"));
                    Assert.That((List<object>)expression["morphTargetBinds"], Has.Count.EqualTo(1));
                }
                Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before));
            }
            finally { PlayerSettings.legacyClampBlendShapeWeights = oldClamp; UnityEngine.Object.DestroyImmediate(native); UnityEngine.Object.DestroyImmediate(clone); foreach (var item in owned) UnityEngine.Object.DestroyImmediate(item); }
        }

        [TestCase("position")]
        [TestCase("normal")]
        [TestCase("tangent")]
        [TestCase("tiny position")]
        public void OnlyAnExactlyZeroScalarPntCanReuseAMarker(string kind)
        {
            BasisMesh(3, ("Zero", Vector3.zero, Vector3.zero, Vector3.zero),
                ("Changed", Vector3.right * (kind == "position" || kind == "tiny position" ? .01f : 0),
                    kind == "normal" ? Vector3.up * .02f : Vector3.zero, kind == "tangent" ? Vector3.forward * .03f : Vector3.zero));
            var menu = new VrChatExpressionMenu.Source(); menu.Entries.Add(BasisEntry("Neutral", "Zero", animation: false));
            menu.Entries.Add(BasisEntry("Changed one", "Changed", animation: false)); menu.Entries.Add(BasisEntry("Changed two", "Changed", animation: false));
            if (kind == "tiny position") foreach (var entry in menu.Entries.Skip(1)) entry.Values.Single().Weight = .09f;
            var clone = UnityEngine.Object.Instantiate(avatar); var owned = new List<Mesh>(); Mesh reference = null;
            try
            {
                var expected = new Vector3[3]; var expectedN = new Vector3[3]; var expectedT = new Vector3[3];
                mesh.GetBlendShapeFrameVertices(1, 0, expected, expectedN, expectedT);
                if (kind == "tiny position")
                {
                    // Native storage drops authored sub-epsilon frames. Derive
                    // a small residual from an independently retained frame.
                    Assert.That(expected[0].x, Is.EqualTo(.01f));
                    var weight = menu.Entries[1].Values.Single().Weight;
                    var rest = new float[mesh.blendShapeCount]; var pose = (float[])rest.Clone(); pose[1] = weight;
                    var composed = AvatarBaseShape.ExpressionDeltas(mesh, rest, pose);
                    for (var vertex = 0; vertex < expected.Length; vertex++) expected[vertex] *= weight / 100f;
                    Assert.That(composed.Vertices, Is.EqualTo(expected));
                    Assert.That(composed.Vertices[0].x, Is.GreaterThan(0));
                    Assert.That(composed.Vertices[0] == Vector3.zero, Is.True);
                    // Compare native storage with a separate single-expression
                    // frame. Its own small-delta quantization is not reuse.
                    reference = UnityEngine.Object.Instantiate(mesh);
                    reference.AddBlendShapeFrame("Independent scalar reference", 100, expected, expectedN, expectedT);
                    reference.GetBlendShapeFrameVertices(reference.blendShapeCount - 1, 0, expected, expectedN, expectedT);
                }
                var expressions = VrChatExpressionBaker.Bake(avatar, clone, menu, owned, null);
                Assert.That(expressions.SelectMany(expression => expression.Targets).Distinct().Count(), Is.EqualTo(3),
                    "Neither approximate Vector3 equality nor position-only equality can authorize sharing.");
                var target = clone.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh;
                var actual = new Vector3[3]; var actualN = new Vector3[3]; var actualT = new Vector3[3];
                foreach (var expression in expressions.Skip(1))
                {
                    target.GetBlendShapeFrameVertices(target.GetBlendShapeIndex(expression.Targets.Single()), 0, actual, actualN, actualT);
                    Assert.That(actual, Is.EqualTo(expected)); Assert.That(actualN, Is.EqualTo(expectedN)); Assert.That(actualT, Is.EqualTo(expectedT));
                }
            }
            finally { if (reference != null) UnityEngine.Object.DestroyImmediate(reference); UnityEngine.Object.DestroyImmediate(clone); foreach (var item in owned) UnityEngine.Object.DestroyImmediate(item); }
        }

        [Test]
        public void ZeroScalarMarkersRemainUniqueToTheActualRenderer()
        {
            BasisMesh(3, ("Zero", Vector3.zero, Vector3.zero, Vector3.zero));
            var other = new GameObject("Other", typeof(SkinnedMeshRenderer)); other.transform.SetParent(avatar.transform, false);
            other.GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
            var menu = new VrChatExpressionMenu.Source(); menu.Entries.Add(BasisEntry("First", "Zero", animation: false));
            menu.Entries.Add(BasisEntry("Second", "Zero", animation: false, path: "Other"));
            var clone = UnityEngine.Object.Instantiate(avatar); var owned = new List<Mesh>();
            try
            {
                var expressions = VrChatExpressionBaker.Bake(avatar, clone, menu, owned, null);
                Assert.That(expressions[0].Targets.Single(), Is.Not.EqualTo(expressions[1].Targets.Single()));
                foreach (var target in expressions.SelectMany(expression => expression.Targets))
                    Assert.That(clone.GetComponentsInChildren<SkinnedMeshRenderer>().Count(skin => skin.sharedMesh.GetBlendShapeIndex(target) >= 0), Is.EqualTo(1));
            }
            finally { UnityEngine.Object.DestroyImmediate(clone); foreach (var item in owned) UnityEngine.Object.DestroyImmediate(item); }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ExactZeroScalarReuseRespectsTheOriginalMemoryBudget(bool zero)
        {
            const int vertices = 65536, rows = 58;
            BasisMesh(vertices, ("Face", zero ? Vector3.zero : Vector3.right * .01f, Vector3.zero, Vector3.zero));
            var menu = new VrChatExpressionMenu.Source(); for (var row = 0; row < rows; row++) menu.Entries.Add(BasisEntry("Row " + row, "Face", animation: false));
            var before = ExportSourceFingerprint.Compute(avatar); var clone = UnityEngine.Object.Instantiate(avatar); var owned = new List<Mesh>();
            try
            {
                Assert.That(rows * (long)vertices * 36, Is.GreaterThan(128L * 1024 * 1024));
                if (!zero)
                    Assert.That(Assert.Throws<InvalidOperationException>(() => VrChatExpressionBaker.Bake(avatar, clone, menu, owned, null)).Message, Does.Contain("128 MiB"));
                else
                {
                    var expressions = VrChatExpressionBaker.Bake(avatar, clone, menu, owned, null);
                    Assert.That(expressions.Count, Is.EqualTo(rows)); Assert.That(expressions.All(expression => expression.Animation == null), Is.True);
                    Assert.That(expressions.SelectMany(expression => expression.Targets).Distinct().Count(), Is.EqualTo(1));
                    Assert.That(clone.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh.blendShapeCount, Is.EqualTo(mesh.blendShapeCount + 1));
                }
                Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before));
            }
            finally { UnityEngine.Object.DestroyImmediate(clone); foreach (var item in owned) UnityEngine.Object.DestroyImmediate(item); }
        }

        [Test]
        public void AnimationGeometryCannotShareTargetsAcrossRenderers()
        {
            BasisMesh(3, ("First", Vector3.right * .01f, Vector3.zero, Vector3.zero));
            var second = new GameObject("Second", typeof(SkinnedMeshRenderer)); second.transform.SetParent(avatar.transform, false); second.GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
            var menu = new VrChatExpressionMenu.Source(); menu.Entries.Add(BasisEntry("First row", "First")); menu.Entries.Add(BasisEntry("Second row", "First", path: "Second"));
            var clone = UnityEngine.Object.Instantiate(avatar); var owned = new List<Mesh>();
            try
            {
                var expressions = VrChatExpressionBaker.Bake(avatar, clone, menu, owned, null);
                Assert.That(BasisTarget(expressions[0]), Is.Not.EqualTo(BasisTarget(expressions[1])));
                foreach (var target in expressions.Select(BasisTarget))
                    Assert.That(clone.GetComponentsInChildren<SkinnedMeshRenderer>().Count(skin => skin.sharedMesh.GetBlendShapeIndex(target) >= 0), Is.EqualTo(1));
            }
            finally { UnityEngine.Object.DestroyImmediate(clone); foreach (var item in owned) UnityEngine.Object.DestroyImmediate(item); }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ExactAnimationBasisReuseRespectsTheOriginalMemoryBudget(bool duplicate)
        {
            const int vertices = 65536, rows = 32;
            BasisMesh(vertices, Enumerable.Range(0, rows).Select(index => ("Basis" + index,
                Vector3.right * .01f * (duplicate ? 1 : index + 1), Vector3.up * .02f, Vector3.forward * .03f)).ToArray());
            var menu = new VrChatExpressionMenu.Source();
            for (var row = 0; row < rows; row++)
            {
                var entry = BasisEntry("Row" + row, "Basis" + row);
                // Keep this budget test's independent scalar payload nonzero;
                // the separate exact-zero test covers inert marker pooling.
                entry.Values.Single().Weight = 25; entry.Animation.Single().Curve.Keys[0].Value = 25;
                foreach (var key in entry.Animation.Single().Curve.Keys) key.InTangent = key.OutTangent = 75;
                menu.Entries.Add(entry);
            }
            var before = ExportSourceFingerprint.Compute(avatar); var clone = UnityEngine.Object.Instantiate(avatar); var owned = new List<Mesh>();
            try
            {
                Assert.That(rows * 2L * vertices * 36, Is.GreaterThan(128L * 1024 * 1024), "Unpooled scalar and animation frames exceed the unchanged cap.");
                if (!duplicate)
                    Assert.That(Assert.Throws<InvalidOperationException>(() => VrChatExpressionBaker.Bake(avatar, clone, menu, owned, null)).Message, Does.Contain("128 MiB"));
                else
                {
                    var expressions = VrChatExpressionBaker.Bake(avatar, clone, menu, owned, null);
                    Assert.That(expressions.Count, Is.EqualTo(rows));
                    Assert.That(expressions.SelectMany(expression => expression.Targets).Distinct().Count(), Is.EqualTo(rows));
                    Assert.That(expressions.Select(BasisTarget).Distinct().Count(), Is.EqualTo(1));
                    var generated = clone.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh.blendShapeCount - mesh.blendShapeCount;
                    Assert.That(generated, Is.EqualTo(rows + 1)); Assert.That(generated * (long)vertices * 36, Is.LessThan(128L * 1024 * 1024));
                }
                Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before)); Assert.That(mesh.blendShapeCount, Is.EqualTo(rows));
            }
            finally { UnityEngine.Object.DestroyImmediate(clone); foreach (var item in owned) UnityEngine.Object.DestroyImmediate(item); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CanonicalAnimationKnotsReuseAuthoredEndpointsAcrossCurveRanges(bool startingAtEnd)
        {
            BasisMesh(3, ("Face", Vector3.right * .01f, Vector3.up * .02f, Vector3.forward * .03f));
            var menu = new VrChatExpressionMenu.Source();
            foreach (var maximum in new[] { 35d, 70d })
            {
                var entry = BasisEntry("Range " + maximum, "Face", duration: 2, maximum: maximum);
                var curve = entry.Animation.Single().Curve;
                if (startingAtEnd) { entry.Values.Single().Weight = 100; curve.Keys[0].Value = 100; }
                foreach (var key in curve.Keys) key.InTangent = key.OutTangent = (maximum - curve.Keys[0].Value) / 2;
                menu.Entries.Add(entry);
            }
            var clone = UnityEngine.Object.Instantiate(avatar); var owned = new List<Mesh>(); var original = new Mesh(); var actual = new Mesh();
            try
            {
                var expressions = VrChatExpressionBaker.Bake(avatar, clone, menu, owned, null);
                Assert.That(expressions.Select(BasisTarget).Distinct().Count(), Is.EqualTo(1));
                var skin = avatar.GetComponentInChildren<SkinnedMeshRenderer>(); var copy = clone.GetComponentInChildren<SkinnedMeshRenderer>();
                foreach (var expression in expressions)
                {
                    var channel = expression.Animation.Channels.Single();
                    Assert.That(channel.Points.Select(point => point.Value), Is.EqualTo(new[] { 0d, 100d }));
                    Assert.That(channel.Points.Single(point => point.Target == null).Value, Is.EqualTo(startingAtEnd ? 100d : 0d));
                    Assert.That(expression.Animation.Duration, Is.EqualTo(2)); Assert.That(expression.Animation.Loop, Is.False);
                    for (var sample = 0; sample <= 8; sample++)
                    {
                        var time = sample / 4d; skin.SetBlendShapeWeight(0, (float)channel.Curve.Evaluate(time)); skin.BakeMesh(original);
                        ApplyBasis(copy, expression, time); copy.BakeMesh(actual); AssertNativePnt(original, actual);
                    }
                    expression.Animation.Expression = expression.Name;
                }
                Assert.That(ExpressionAnimationData.Read(ExpressionAnimationData.Write(expressions.Select(expression => expression.Animation).ToList())).Count, Is.EqualTo(2));
                if (startingAtEnd) Assert.That(expressions.SelectMany(expression => expression.Targets).Distinct().Count(), Is.EqualTo(2));
            }
            finally { UnityEngine.Object.DestroyImmediate(original); UnityEngine.Object.DestroyImmediate(actual); UnityEngine.Object.DestroyImmediate(clone); foreach (var item in owned) UnityEngine.Object.DestroyImmediate(item); }
        }

        private static void AssertNativePnt(Mesh expected, Mesh actual)
        {
            for (var vertex = 0; vertex < expected.vertexCount; vertex++)
            {
                Assert.That(Vector3.Distance(actual.vertices[vertex], expected.vertices[vertex]), Is.LessThan(.000001f));
                Assert.That(Vector3.Distance(actual.normals[vertex], expected.normals[vertex]), Is.LessThan(.000001f));
                Assert.That(Vector4.Distance(actual.tangents[vertex], expected.tangents[vertex]), Is.LessThan(.000001f));
            }
        }

        [TestCase("multiple frames")]
        [TestCase("signed clamped")]
        [TestCase("signed unbounded")]
        [TestCase("extrapolation")]
        [TestCase("large authored endpoint")]
        [TestCase("interior initial")]
        public void CanonicalAnimationKnotsPreserveSourceFrameGeometryAndRangeEdges(string kind)
        {
            BasisMesh(3, ("Face", Vector3.right * .01f, Vector3.up * .02f, Vector3.forward * .03f));
            var signed = kind.StartsWith("signed", StringComparison.Ordinal); var multiple = signed || kind == "multiple frames";
            if (multiple || kind == "large authored endpoint")
            {
                mesh.ClearBlendShapes();
                if (signed) mesh.AddBlendShapeFrame("Face", -100, Enumerable.Repeat(Vector3.right * -.02f, 3).ToArray(),
                    Enumerable.Repeat(Vector3.up * -.04f, 3).ToArray(), Enumerable.Repeat(Vector3.forward * -.06f, 3).ToArray());
                if (multiple) mesh.AddBlendShapeFrame("Face", 50, Enumerable.Repeat(Vector3.right * .01f, 3).ToArray(),
                    Enumerable.Repeat(Vector3.up * .02f, 3).ToArray(), Enumerable.Repeat(Vector3.forward * .03f, 3).ToArray());
                mesh.AddBlendShapeFrame("Face", kind == "large authored endpoint" ? 100000 : 100,
                    Enumerable.Repeat(Vector3.right * (multiple ? .08f : 100f), 3).ToArray(),
                    Enumerable.Repeat(Vector3.up * (multiple ? .07f : 200f), 3).ToArray(),
                    Enumerable.Repeat(Vector3.forward * (multiple ? .05f : 300f), 3).ToArray());
            }
            var entry = BasisEntry(kind, "Face", maximum: kind == "extrapolation" ? 150 : 70);
            var curve = entry.Animation.Single().Curve;
            if (signed || kind == "extrapolation")
            {
                foreach (var key in curve.Keys) key.InTangent = key.OutTangent = 0;
                curve.Keys.Insert(1, new ExpressionAnimationData.Key { Time = .5, Value = -50, InTangent = 0, OutTangent = 0 });
            }
            if (kind == "interior initial")
            {
                entry.Values.Single().Weight = 35; curve.Keys[0].Value = 35;
                foreach (var key in curve.Keys) key.InTangent = key.OutTangent = 0;
                curve.Keys.Insert(1, new ExpressionAnimationData.Key { Time = .5, Value = 10, InTangent = 0, OutTangent = 0 });
            }
            var menu = new VrChatExpressionMenu.Source(); menu.Entries.Add(entry);
            var clamp = PlayerSettings.legacyClampBlendShapeWeights;
            var clone = UnityEngine.Object.Instantiate(avatar); var owned = new List<Mesh>(); var expected = new Mesh(); var actual = new Mesh();
            var witness = UnityEngine.Object.Instantiate(mesh); var witnessRoot = new GameObject("Independent authored frame sample", typeof(SkinnedMeshRenderer));
            try
            {
                PlayerSettings.legacyClampBlendShapeWeights = kind != "signed unbounded";
                var expression = VrChatExpressionBaker.Bake(avatar, clone, menu, owned, null).Single();
                var channel = expression.Animation.Channels.Single(); var copy = clone.GetComponentInChildren<SkinnedMeshRenderer>();
                var wantedPoints = signed ? new[] { -100d, 0d, 50d, 100d } : kind == "multiple frames" ? new[] { 0d, 50d, 100d } :
                    kind == "extrapolation" ? new[] { -50d, 0d, 100d, 150d } : kind == "large authored endpoint" ? new[] { 0d, 70d } : new[] { 0d, 100d };
                Assert.That(channel.Points.Select(point => point.Value), Is.EqualTo(wantedPoints));
                Assert.That(VrChatExpressionBaker.SameCurve(channel.Curve, curve), Is.True);
                expression.Animation.Expression = expression.Name;
                Assert.That(ExpressionAnimationData.Read(ExpressionAnimationData.Write(new[] { expression.Animation })).Count, Is.EqualTo(1));
                var frameV = new List<Vector3[]>(); var frameN = new List<Vector3[]>(); var frameT = new List<Vector3[]>();
                for (var frame = 0; frame < mesh.GetBlendShapeFrameCount(0); frame++)
                {
                    var v = new Vector3[3]; var n = new Vector3[3]; var t = new Vector3[3]; mesh.GetBlendShapeFrameVertices(0, frame, v, n, t);
                    frameV.Add(v); frameN.Add(n); frameT.Add(t);
                }
                var sourceSkin = witnessRoot.GetComponent<SkinnedMeshRenderer>(); sourceSkin.sharedMesh = witness;
                for (var sample = 0; sample <= 16; sample++)
                {
                    var time = sample / 16d; var value = curve.Evaluate(time);
                    if (PlayerSettings.legacyClampBlendShapeWeights) value = Math.Max(0, Math.Min(kind == "large authored endpoint" ? 100000 : 100, value));
                    var v = new Vector3[3]; var n = new Vector3[3]; var t = new Vector3[3];
                    for (var vertex = 0; vertex < 3; vertex++)
                    {
                        if (!multiple)
                        {
                            var factor = (float)(value / (kind == "large authored endpoint" ? 100000 : 100));
                            v[vertex] = frameV[0][vertex] * factor; n[vertex] = frameN[0][vertex] * factor; t[vertex] = frameT[0][vertex] * factor;
                        }
                        else if (value <= 50)
                        {
                            var factor = (float)(signed ? (value + 100) / 150 : value / 50);
                            var originV = signed ? frameV[0][vertex] : Vector3.zero;
                            var originN = signed ? frameN[0][vertex] : Vector3.zero;
                            var originT = signed ? frameT[0][vertex] : Vector3.zero;
                            var upper = signed ? 1 : 0;
                            v[vertex] = Vector3.LerpUnclamped(originV, frameV[upper][vertex], factor);
                            n[vertex] = Vector3.LerpUnclamped(originN, frameN[upper][vertex], factor);
                            t[vertex] = Vector3.LerpUnclamped(originT, frameT[upper][vertex], factor);
                        }
                        else
                        {
                            var lower = signed ? 1 : 0; var upper = lower + 1; var factor = (float)((value - 50) / 50);
                            v[vertex] = Vector3.LerpUnclamped(frameV[lower][vertex], frameV[upper][vertex], factor);
                            n[vertex] = Vector3.LerpUnclamped(frameN[lower][vertex], frameN[upper][vertex], factor);
                            t[vertex] = Vector3.LerpUnclamped(frameT[lower][vertex], frameT[upper][vertex], factor);
                        }
                    }
                    if (kind == "signed unbounded" && sample == 8) Assert.That(v[0].x, Is.LessThan(-.001f));
                    // Fresh positive100 witnesses avoid EditMode's stale native
                    // unclamped setting while retaining original signed P/N/T.
                    witness.ClearBlendShapes(); witness.AddBlendShapeFrame("Original authored sample", 100, v, n, t);
                    sourceSkin.SetBlendShapeWeight(0, 100); sourceSkin.BakeMesh(expected);
                    ApplyBasis(copy, expression, time); copy.BakeMesh(actual); AssertNativePnt(expected, actual);
                }
            }
            finally { PlayerSettings.legacyClampBlendShapeWeights = clamp; UnityEngine.Object.DestroyImmediate(witnessRoot); UnityEngine.Object.DestroyImmediate(witness); UnityEngine.Object.DestroyImmediate(expected); UnityEngine.Object.DestroyImmediate(actual); UnityEngine.Object.DestroyImmediate(clone); foreach (var item in owned) UnityEngine.Object.DestroyImmediate(item); }
        }

        [Test]
        public void CanonicalAnimationKnotsRetainIndependentChannelsInsideOneFace()
        {
            BasisMesh(3, ("First", Vector3.right * .01f, Vector3.up * .02f, Vector3.forward * .03f),
                ("Other", Vector3.right * .01f, Vector3.up * .02f, Vector3.forward * .03f));
            var entry = BasisEntry("Two channels", "First", maximum: 35);
            entry.Values.Add(BasisEntry("Other", "Other", maximum: 70).Values.Single());
            entry.Animation.Add(BasisEntry("Other", "Other", maximum: 70).Animation.Single());
            var menu = new VrChatExpressionMenu.Source(); menu.Entries.Add(entry);
            var clone = UnityEngine.Object.Instantiate(avatar); var owned = new List<Mesh>(); var expected = new Mesh(); var actual = new Mesh();
            try
            {
                var expression = VrChatExpressionBaker.Bake(avatar, clone, menu, owned, null).Single();
                var targets = expression.Animation.Channels.SelectMany(channel => channel.Points).Where(point => point.Target != null).Select(point => point.Target).ToArray();
                Assert.That(targets.Length, Is.EqualTo(2)); Assert.That(targets.Distinct().Count(), Is.EqualTo(2));
                foreach (var channel in expression.Animation.Channels) Assert.That(channel.Points.Select(point => point.Value), Is.EqualTo(new[] { 0d, 100d }));
                var original = avatar.GetComponentInChildren<SkinnedMeshRenderer>(); var copy = clone.GetComponentInChildren<SkinnedMeshRenderer>();
                for (var sample = 0; sample <= 8; sample++)
                {
                    var time = sample / 8d;
                    for (var channel = 0; channel < 2; channel++) original.SetBlendShapeWeight(channel, (float)entry.Animation[channel].Curve.Evaluate(time));
                    original.BakeMesh(expected); ApplyBasis(copy, expression, time); copy.BakeMesh(actual); AssertNativePnt(expected, actual);
                }
                expression.Animation.Expression = expression.Name;
                Assert.That(ExpressionAnimationData.Read(ExpressionAnimationData.Write(new[] { expression.Animation })).Count, Is.EqualTo(1));
            }
            finally { UnityEngine.Object.DestroyImmediate(expected); UnityEngine.Object.DestroyImmediate(actual); UnityEngine.Object.DestroyImmediate(clone); foreach (var item in owned) UnityEngine.Object.DestroyImmediate(item); }
        }

        private void DiscreteController()
        {
            controller.AddParameter("Face", AnimatorControllerParameterType.Int);
            var machine = controller.layers[0].stateMachine;
            var idle = machine.AddState("Idle");
            idle.motion = Clip("Idle", 0, 0);
            var smile = machine.AddState("Smile");
            smile.motion = Clip("Smile", 75, 0);
            idle.writeDefaultValues = smile.writeDefaultValues = false;
            machine.defaultState = idle;
            var transition = idle.AddTransition(smile);
            transition.hasExitTime = false;
            transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "Face");
        }

        private AnimationClip Clip(string name, float size, float blink)
        {
            var clip = new AnimationClip { name = name };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"), AnimationCurve.Constant(0, 1, size));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink"), AnimationCurve.Constant(0, 1, blink));
            AssetDatabase.AddObjectToAsset(clip, controller);
            return clip;
        }

        private static Dictionary<string, float> Params(string name, float value) => new Dictionary<string, float> { [name] = value };
    }
}
