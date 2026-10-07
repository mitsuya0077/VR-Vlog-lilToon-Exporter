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
    public sealed class NativeTreeTrajectoryTests
    {
        string folder;
        GameObject avatar;
        Mesh mesh;
        AnimatorController controller, other;
        AnimatorState selected;
        VrChatExpressionMenu.Source source;
        static readonly EditorCurveBinding Morph = EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size");

        [SetUp]
        public void SetUp()
        {
            var name = "__NativeTreeHistory_" + Guid.NewGuid().ToString("N"); AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            controller.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
            controller.AddParameter("Mix", AnimatorControllerParameterType.Float);
            avatar = new GameObject("Prepared reference", typeof(Animator)); var skin = new GameObject("Face", typeof(SkinnedMeshRenderer)).GetComponent<SkinnedMeshRenderer>();
            skin.transform.SetParent(avatar.transform, false); mesh = BaseShapeFixture.Create(); skin.sharedMesh = mesh; skin.SetBlendShapeWeight(0, 25);
            var machine = controller.layers[0].stateMachine; var normal = machine.AddState("Normal"); normal.motion = Clip(controller, "Normal face", AnimationCurve.Constant(0, 1, 0));
            normal.writeDefaultValues = false; machine.defaultState = normal; selected = machine.AddState("Selected tree"); selected.writeDefaultValues = false;
            var tree = new BlendTree { name = "Animated tree", blendType = BlendTreeType.Simple1D, blendParameter = "Mix", useAutomaticThresholds = false };
            AssetDatabase.AddObjectToAsset(tree, controller); tree.children = new[] {
                new ChildMotion { motion = Clip(controller, "Animated low", AnimationCurve.Linear(0, 20, 1, 80)), threshold = 0, timeScale = 1 },
                new ChildMotion { motion = Clip(controller, "Animated high", AnimationCurve.Linear(0, 40, 1, 100)), threshold = 1, timeScale = 1 }
            }; selected.motion = tree;
            var enter = normal.AddTransition(selected); enter.hasExitTime = false; enter.duration = 0; enter.AddCondition(AnimatorConditionMode.Equals, 1, "GestureRight");
            other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Normal-only playable.controller"); other.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
            other.AddParameter("Mix", AnimatorControllerParameterType.Float); source = new VrChatExpressionMenu.Source { Controller = controller }; source.OtherControllers.Add(other);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(avatar); Object.DestroyImmediate(mesh); if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        static AnimationClip Clip(AnimatorController owner, string name, AnimationCurve curve)
        {
            var clip = new AnimationClip { name = name }; AnimationUtility.SetEditorCurve(clip, Morph, curve);
            var settings = AnimationUtility.GetAnimationClipSettings(clip); settings.loopTime = true; AnimationUtility.SetAnimationClipSettings(clip, settings);
            AssetDatabase.AddObjectToAsset(clip, owner); return clip;
        }

        [TestCase("morph")]
        [TestCase("parameter")]
        public void NormalPlayableWriterCannotDisappearWhenTheHandIsSelected(string effect)
        {
            var machine = other.layers[0].stateMachine; var quiet = machine.AddState("Selected quiet branch"); quiet.writeDefaultValues = false;
            quiet.motion = new AnimationClip { name = "Empty selected branch" }; AssetDatabase.AddObjectToAsset(quiet.motion, other); machine.defaultState = quiet;
            var initializer = machine.AddState("Normal-only initializer"); initializer.writeDefaultValues = false;
            var clip = new AnimationClip { name = "Normal-only effect" }; AssetDatabase.AddObjectToAsset(clip, other); initializer.motion = clip;
            AnimationUtility.SetEditorCurve(clip, effect == "morph" ? Morph : EditorCurveBinding.FloatCurve("", typeof(Animator), "Mix"),
                AnimationCurve.Constant(0, 1, effect == "morph" ? 40 : .75f));
            var enter = machine.AddEntryTransition(initializer); enter.AddCondition(AnimatorConditionMode.Equals, 0, "GestureRight");
            var before = EditorJsonUtility.ToJson(controller); var otherBefore = EditorJsonUtility.ToJson(other);
            var error = Assert.Catch<InvalidOperationException>(() => NativeTreeTrajectory.Probe(avatar, source, 0, selected,
                new Dictionary<string, float> { ["GestureRight"] = 1 }, new HashSet<EditorCurveBinding> { Morph }, new[] { 0f, .5f, 1f }));
            Assert.That(error.Message, Does.Contain(other.name));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before)); Assert.That(EditorJsonUtility.ToJson(other), Is.EqualTo(otherBefore));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(25));
        }

        BlendTree MixedClock(bool loop = true)
        {
            source.OtherControllers.Clear();
            var still = Clip(controller, "Zero-duration constant", new AnimationCurve(new Keyframe(0, 0)));
            var moving = Clip(controller, "Periodic leaf", new AnimationCurve(new Keyframe(0, 20), new Keyframe(1, 80), new Keyframe(2, 20)));
            foreach (var clip in new[] { still, moving })
            {
                var settings = AnimationUtility.GetAnimationClipSettings(clip); settings.loopTime = clip == moving && loop;
                AnimationUtility.SetAnimationClipSettings(clip, settings);
            }
            Assert.That(still.length, Is.Zero); Assert.That(still.isLooping, Is.False);
            var tree = (BlendTree)selected.motion; tree.children = new[] {
                new ChildMotion { motion = still, threshold = 0, timeScale = 1 },
                new ChildMotion { motion = moving, threshold = 1, timeScale = 1 }
            };
            return tree;
        }

        NativeTreeTrajectory.Result ProbeMixed() => NativeTreeTrajectory.Probe(avatar, source, 0, selected,
            new Dictionary<string, float> { ["GestureRight"] = 1, ["Mix"] = .75f }, new HashSet<EditorCurveBinding> { Morph }, new[] { 0f, .25f, .5f, .75f, 1f });

        [TestCase(true)]
        [TestCase(false)]
        public void ZeroDurationConstantDoesNotDecideTheChangingLeafsLoop(bool loop)
        {
            MixedClock(loop); var before = EditorJsonUtility.ToJson(controller); var sourceBefore = EditorJsonUtility.ToJson(mesh);
            var result = ProbeMixed();
            Assert.That(result.StateLoop, Is.False, "The fixture must reproduce Unity's mixed nonloop state metadata.");
            Assert.That(result.Loop, Is.EqualTo(loop)); Assert.That(result.Duration, Is.EqualTo(1.5f).Within(.0001));
            Assert.That(result.Coefficients.Values.OrderBy(value => value).ToArray(), Is.EqualTo(new[] { .25f, .75f }).Within(.0001));
            Assert.That(result.Samples[.5f][Morph], Is.GreaterThan(result.Samples[0][Morph] + 40));
            var entry = new VrChatExpressionMenu.Entry { Name = "Mixed native clock" };
            entry.Parameters["GestureRight"] = 1; entry.Parameters["Mix"] = .75f;
            AnimatedGestureTree.Read(avatar, source, 0, selected, entry, null);
            Assert.That(entry.Loop, Is.EqualTo(loop)); Assert.That(entry.Duration, Is.EqualTo(result.Duration).Within(.0001));
            var exported = entry.Animation.Single(channel => channel.Shape == "Face size").Curve;
            foreach (var sample in result.Samples) Assert.That(exported.Evaluate(result.SamplePhases[sample.Key] * entry.Duration), Is.EqualTo(sample.Value[Morph]).Within(.02));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before)); Assert.That(EditorJsonUtility.ToJson(mesh), Is.EqualTo(sourceBefore));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(25));
        }

        [TestCase("loop")]
        [TestCase("duration")]
        public void NativeTrajectoryRejectsDisagreementBetweenActualChangingLeaves(string damage)
        {
            var tree = MixedClock();
            var second = Clip(controller, "Second changing clock", new AnimationCurve(new Keyframe(0, 40), new Keyframe(1, 90), new Keyframe(damage == "duration" ? 4 : 2, 40)));
            if (damage == "loop") { var settings = AnimationUtility.GetAnimationClipSettings(second); settings.loopTime = false; AnimationUtility.SetAnimationClipSettings(second, settings); }
            var children = tree.children; children[0].motion = second; tree.children = children;
            Assert.Catch<InvalidOperationException>(() => ProbeMixed());
        }

        [TestCase("selected exit")]
        [TestCase("ancestor AnyState")]
        [TestCase("future input callback")]
        public void PeriodicLeavesCannotHideFutureNativeTimedEffects(string effect)
        {
            MixedClock(); var machine = controller.layers[0].stateMachine;
            if (effect == "future input callback")
            {
                controller.AddLayer("Future input program"); var layers = controller.layers; layers[1].defaultWeight = 1; controller.layers = layers;
                machine = controller.layers[1].stateMachine; var waiting = machine.AddState("Wait far beyond the witnesses"); waiting.writeDefaultValues = false; machine.defaultState = waiting;
                var change = machine.AddState("Change tree mixture"); change.writeDefaultValues = false;
                ParameterDriverExpressionTests.Driver(change, ParameterDriverExpressionTests.Op("Set", "Mix", 0));
                var timed = waiting.AddTransition(change); timed.hasExitTime = true; timed.exitTime = 100; timed.duration = 0;
            }
            else
            {
                var escape = machine.AddState("Future different face"); escape.motion = Clip(controller, "Future face", AnimationCurve.Constant(0, 1, 40)); escape.writeDefaultValues = false;
                var timed = effect == "selected exit" ? selected.AddTransition(escape) : machine.AddAnyStateTransition(escape);
                timed.hasExitTime = true; timed.exitTime = 100; timed.duration = 0;
            }
            var error = Assert.Catch<InvalidOperationException>(() => ProbeMixed()); Assert.That(error.Message, Does.Contain("時間遷移"));
        }

        [Test]
        public void AProvablyFalseTimedEdgeCannotInvalidateTheHeldLoop()
        {
            MixedClock(); var machine = controller.layers[0].stateMachine; var escape = machine.AddState("Other hand face");
            escape.motion = Clip(controller, "Other hand", AnimationCurve.Constant(0, 1, 40)); escape.writeDefaultValues = false;
            var timed = selected.AddTransition(escape); timed.hasExitTime = true; timed.exitTime = 100; timed.duration = 0; timed.AddCondition(AnimatorConditionMode.Equals, 0, "GestureRight");
            Assert.That(ProbeMixed().Loop, Is.True);
        }

        [Test]
        public void DelayedNativeParameterWriterCannotMasqueradeAsStableTreeCoefficients()
        {
            MixedClock(); controller.AddLayer("Delayed mixture writer"); var layers = controller.layers; layers[1].defaultWeight = 1; controller.layers = layers;
            var machine = controller.layers[1].stateMachine; var state = machine.AddState("Initially unchanged mixture"); machine.defaultState = state; state.writeDefaultValues = false;
            var clip = new AnimationClip { name = "Delayed native Mix" }; AssetDatabase.AddObjectToAsset(clip, controller); state.motion = clip;
            var binding = EditorCurveBinding.FloatCurve("", typeof(Animator), "Mix");
            AnimationUtility.SetEditorCurve(clip, binding, new AnimationCurve(new Keyframe(0, .75f), new Keyframe(10, .75f), new Keyframe(12, 1)));
            var stored = AnimationUtility.GetEditorCurve(clip, binding);
            Assert.That(stored.Evaluate(4), Is.EqualTo(.75f)); Assert.That(stored.Evaluate(11), Is.GreaterThan(.75f));
            var before = EditorJsonUtility.ToJson(controller); var error = Assert.Catch<InvalidOperationException>(() => ProbeMixed());
            Assert.That(error.Message, Does.Contain(clip.name).And.Contain("Mix"));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
        }

        [TestCase("Mix", .5f)]
        [TestCase("GestureRight", .5f)]
        [TestCase("Missing arrival input", 1)]
        public void ArrivalCannotIntroduceAnUninventoriedOrInvalidInput(string name, float value)
        {
            source.OtherControllers.Clear(); var before = EditorJsonUtility.ToJson(controller);
            var error = Assert.Catch<InvalidOperationException>(() => NativeTreeTrajectory.Probe(avatar, source, 0, selected,
                new Dictionary<string, float> { ["GestureRight"] = 1, ["Mix"] = 1 }, new HashSet<EditorCurveBinding> { Morph }, new[] { 0f, .5f, 1f },
                arrivalInputs: new Dictionary<string, float> { [name] = value }));
            Assert.That(error.Message, Does.Contain(name)); Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
        }

        [Test]
        public void OriginalArrivalThenStaticFinalKnotHasNoTemporalProgram()
        {
            source.OtherControllers.Clear();
            var still = Clip(controller, "Only active final static leaf", new AnimationCurve(new Keyframe(0, 77)));
            var settings = AnimationUtility.GetAnimationClipSettings(still); settings.loopTime = false; AnimationUtility.SetAnimationClipSettings(still, settings);
            var tree = (BlendTree)selected.motion; var children = tree.children; children[1].motion = still; tree.children = children;
            Assert.That(still.length, Is.Zero); var before = EditorJsonUtility.ToJson(controller);
            var result = NativeTreeTrajectory.Probe(avatar, source, 0, selected,
                new Dictionary<string, float> { ["GestureRight"] = 1, ["Mix"] = 1 }, new HashSet<EditorCurveBinding> { Morph }, new[] { 0f, .5f, 1f },
                arrivalInputs: new Dictionary<string, float> { ["GestureRight"] = 1, ["Mix"] = 0 });
            Assert.That(result.Coefficients.Keys, Is.EquivalentTo(new[] { still }));
            Assert.That(result.Duration, Is.Zero); Assert.That(result.Loop, Is.False);
            Assert.That(result.Initial[Morph], Is.EqualTo(77).Within(.01));
            Assert.That(result.Samples.Values.All(sample => Mathf.Abs(sample[Morph] - 77) < .01), Is.True);
            Assert.That(result.SamplePhases.Values.All(phase => phase == 0), Is.True);
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(25));
        }

        [Test]
        public void StaticFinalKnotCannotConcealAFutureNativeExit()
        {
            source.OtherControllers.Clear(); var tree = (BlendTree)selected.motion;
            var children = tree.children; children[1].motion = Clip(controller, "Static final leaf", AnimationCurve.Constant(0, 1, 77)); tree.children = children;
            var exit = selected.AddTransition(controller.layers[0].stateMachine.defaultState); exit.hasExitTime = true; exit.exitTime = 100; exit.duration = 0;
            var error = Assert.Catch<InvalidOperationException>(() => NativeTreeTrajectory.Probe(avatar, source, 0, selected,
                new Dictionary<string, float> { ["GestureRight"] = 1, ["Mix"] = 1 }, new HashSet<EditorCurveBinding> { Morph }, new[] { 0f, 1f },
                arrivalInputs: new Dictionary<string, float> { ["GestureRight"] = 1, ["Mix"] = 0 }));
            Assert.That(error.Message, Does.Contain("時間遷移"));
        }
    }
}
