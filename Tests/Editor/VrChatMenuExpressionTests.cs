using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
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
                    if (kind == "alias") Assert.That(expressions[row].Animation.Channels.Single().Points.Select(point => point.Value), Is.EqualTo(new[] { -100d, 0, 50, 100 }));
                    foreach (var time in new[] { 0d, menu.Entries[row].Duration * .25, menu.Entries[row].Duration })
                    {
                        original.SetBlendShapeWeight(0, 0); original.SetBlendShapeWeight(1, 0);
                        original.SetBlendShapeWeight(row, (float)menu.Entries[row].Animation.Single().Curve.Evaluate(time)); original.BakeMesh(native);
                        if (kind == "alias" && time == menu.Entries[row].Duration * .25)
                            Assert.That(native.vertices[0].x, Is.LessThan(-.001f), "The original signed source frame actually moves below neutral; zero-clamping is not a signed oracle.");
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
            var menu = new VrChatExpressionMenu.Source(); for (var row = 0; row < rows; row++) menu.Entries.Add(BasisEntry("Row" + row, "Basis" + row));
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
