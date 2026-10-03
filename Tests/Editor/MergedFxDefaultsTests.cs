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
    // These tests execute Unity's real layer blending and Write Defaults.
    // A host-only source check cannot replace running them in the Editor.
    public sealed class MergedFxDefaultsTests
    {
        string folder;
        GameObject avatar;
        Mesh mesh;
        AnimatorController controller;

        [SetUp]
        public void SetUp()
        {
            var name = "__MergedFxDefaults_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            controller.AddParameter("Menu", AnimatorControllerParameterType.Int);
            avatar = new GameObject("Avatar", typeof(Animator));
            var face = new GameObject("Face", typeof(SkinnedMeshRenderer)); face.transform.SetParent(avatar.transform, false);
            mesh = BaseShapeFixture.Create();
            var zeros = new Vector3[mesh.vertexCount];
            var delta = new Vector3[mesh.vertexCount]; delta[0] = Vector3.up;
            mesh.AddBlendShapeFrame("Pupil removal", 100, delta, zeros, zeros);
            face.GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(avatar); Object.DestroyImmediate(mesh); AssetDatabase.DeleteAsset(folder);
        }

        AnimationClip Clip(string name, params (string Shape, float Value)[] shapes)
        {
            var clip = new AnimationClip { name = name };
            foreach (var shape in shapes)
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape." + shape.Shape),
                    AnimationCurve.Constant(0, 1, shape.Value));
            AssetDatabase.AddObjectToAsset(clip, controller); return clip;
        }

        AnimatorState State(AnimatorStateMachine machine, string name, AnimationClip clip, bool writeDefaults = false)
        { var state = machine.AddState(name); state.motion = clip; state.writeDefaultValues = writeDefaults; return state; }

        AnimatorStateMachine AddLayer(string name)
        {
            controller.AddLayer(name); var layers = controller.layers;
            layers[layers.Length - 1].defaultWeight = 1; controller.layers = layers;
            return layers[layers.Length - 1].stateMachine;
        }

        void Menu(AnimatorStateMachine machine, bool writesPupil = false, bool writeDefaults = false)
        {
            var idle = State(machine, "Idle", writesPupil ? Clip("Idle", ("Face size", 0), ("Pupil removal", 0)) : Clip("Idle", ("Face size", 0)), writeDefaults);
            var selected = State(machine, "Selected", writesPupil ? Clip("Selected", ("Face size", 75), ("Pupil removal", 0)) : Clip("Selected", ("Face size", 75)), writeDefaults);
            machine.defaultState = idle;
            var transition = idle.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "Menu");
        }

        void Permanent(AnimatorStateMachine machine, bool writeDefaults = false)
        { machine.defaultState = State(machine, "Always", Clip("Always remove pupil", ("Pupil removal", 100)), writeDefaults); }

        static Dictionary<string, float> Parameters(int value) => new Dictionary<string, float> { ["Menu"] = value };

        [TestCase(false)]
        [TestCase(true)]
        public void ConstantIndependentEffectAppliesToNeutralAndSelectedExpression(bool writeDefaults)
        {
            Menu(controller.layers[0].stateMachine, writeDefaults: writeDefaults);
            Permanent(AddLayer("Permanent pupil override"), writeDefaults);
            var neutral = VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0));
            var selected = VrChatExpressionSampler.Sample(avatar, controller, Parameters(0), Parameters(1));
            Assert.That(neutral.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(100).Within(.01));
            Assert.That(selected.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(100).Within(.01));
            Assert.That(selected.Single(value => value.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.Zero);
            Assert.That(mesh.blendShapeCount, Is.EqualTo(3));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void PermanentEffectUsesAuthoredLayerPriority(bool permanentLast, bool writeDefaults)
        {
            if (permanentLast) { Menu(controller.layers[0].stateMachine, true, writeDefaults); Permanent(AddLayer("Permanent"), writeDefaults); }
            else { Permanent(controller.layers[0].stateMachine, writeDefaults); Menu(AddLayer("Menu"), true, writeDefaults); }
            var selected = VrChatExpressionSampler.Sample(avatar, controller, Parameters(0), Parameters(1));
            Assert.That(selected.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(permanentLast ? 100 : 0).Within(.01));
        }

        [Test]
        public void UnrelatedGestureLayerDoesNotBecomeANeutralSamplingRoot()
        {
            Menu(controller.layers[0].stateMachine);
            Permanent(AddLayer("Permanent"));
            controller.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
            var machine = AddLayer("Unrelated gesture");
            var idle = State(machine, "Idle", Clip("Gesture neutral", ("Blink", 0))); machine.defaultState = idle;
            var gesture = State(machine, "Gesture", Clip("Gesture eye", ("Blink", 100)));
            var transition = idle.AddTransition(gesture); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 2, "GestureRight");
            var seeds = ExpressionDependencies.StationaryMorphBindings(controller);
            Assert.That(seeds.Select(binding => binding.propertyName), Is.EquivalentTo(new[] { "blendShape.Pupil removal" }));
            var neutral = VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0));
            Assert.That(neutral.Select(value => value.Shape), Is.EquivalentTo(new[] { "Pupil removal" }));
            var selected = VrChatExpressionSampler.Sample(avatar, controller, Parameters(0), Parameters(1));
            Assert.That(selected.Any(value => value.Shape == "Blink"), Is.False);
        }

        [Test]
        public void TimeVaryingDefaultLayerIsNotDeclaredPermanent()
        {
            Permanent(controller.layers[0].stateMachine);
            var clip = (AnimationClip)controller.layers[0].stateMachine.defaultState.motion;
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"),
                AnimationCurve.Linear(0, 0, 1, 100));
            Assert.That(VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0)), Is.Empty);
        }

        [Test]
        public void OrdinaryMenuDoesNotNeedAStationaryLayer()
        {
            Menu(controller.layers[0].stateMachine);
            Assert.That(VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0)), Is.Empty);
            var selected = VrChatExpressionSampler.Sample(avatar, controller, Parameters(0), Parameters(1));
            Assert.That(selected.Single().Shape, Is.EqualTo("Face size"));
            Assert.That(selected.Single().Weight, Is.EqualTo(75).Within(.01));
        }

        [Test]
        public void PermanentOverrideSuppressesPupilAnimationWithoutLosingOtherAnimatedChannels()
        {
            Menu(controller.layers[0].stateMachine, true);
            Permanent(AddLayer("Permanent"));
            var clip = (AnimationClip)controller.layers[0].stateMachine.states.Single(item => item.state.name == "Selected").state.motion;
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"), AnimationCurve.Linear(0, 0, 1, 80));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"), AnimationCurve.Linear(0, 0, 1, 75));
            var entry = new VrChatExpressionMenu.Entry();
            VrChatGestureExpressions.ReadClip(avatar, clip, entry);
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 0);
            Assert.That(entry.Values.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(100).Within(.01));
            Assert.That(entry.Animation.Select(animation => animation.Shape), Is.EquivalentTo(new[] { "Face size" }));
            Assert.That(entry.Animation.Single().Curve.Evaluate(.5), Is.EqualTo(37.5).Within(.01));
            Assert.That(AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Pupil removal")).Evaluate(.5f), Is.EqualTo(40).Within(.01),
                "The source clip is never rewritten by the overlay probes.");
        }

        [Test]
        public void DirectAnimatedClipRetainsNativeLayerWeightAndPriority()
        {
            Permanent(controller.layers[0].stateMachine);
            var machine = AddLayer("Weighted expression"); Menu(machine, true);
            var layers = controller.layers; layers[1].defaultWeight = .5f; controller.layers = layers;
            var clip = (AnimationClip)machine.states.Single(item => item.state.name == "Selected").state.motion;
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"), AnimationCurve.Linear(0, 0, 1, 80));
            var entry = new VrChatExpressionMenu.Entry();
            VrChatGestureExpressions.ReadClip(avatar, clip, entry);
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1);
            Assert.That(entry.Values.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(50).Within(.01));
            var curve = entry.Animation.Single(animation => animation.Shape == "Pupil removal").Curve;
            Assert.That(curve.Evaluate(0), Is.EqualTo(50).Within(.01));
            Assert.That(curve.Evaluate(1), Is.EqualTo(90).Within(.01));
        }

        [Test]
        public void StandaloneRegisteredClipKeepsPermanentFxAboveItsBasePose()
        {
            Permanent(controller.layers[0].stateMachine);
            var clip = Clip("Registered expression", ("Face size", 75), ("Pupil removal", 0));
            var entry = new VrChatExpressionMenu.Entry();
            VrChatGestureExpressions.ReadClip(avatar, clip, entry);
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry);
            Assert.That(entry.Values.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(100).Within(.01));
            Assert.That(entry.Values.Single(value => value.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RegisteredFaceEmoClipFollowsReparentedTargetAndMergedPermanentFx(bool writeDefaults)
        {
            var clip = Clip("Registered FaceEmo", ("Face size", 75), ("Pupil removal", 0));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"),
                AnimationCurve.Linear(0, 0, 1, 80));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                AnimationCurve.Linear(0, 0, 1, 75));
            // A GUID must address a clip asset, rather than the controller's
            // subasset; real FaceEmo stores standalone registered clips.
            var original = Object.Instantiate(clip); original.name = clip.name;
            AssetDatabase.CreateAsset(original, folder + "/FaceEmo.anim");
            var registered = new { Modes = new[] { new { DisplayName = "Registered", ChangeDefaultFace = true,
                Animation = new { GUID = AssetDatabase.AssetPathToGUID(folder + "/FaceEmo.anim") } } } };
            var clone = Object.Instantiate(avatar); clone.name = avatar.name;
            try
            {
                var auxiliary = new GameObject("Face", typeof(MeshRenderer)); auxiliary.transform.SetParent(clone.transform, false);
                var transformed = false;
                bool Excluded(string path) => transformed && path == "Face";
                var snapshot = FaceEmoExpressions.CaptureRegistered(clone, registered, excludedPath: Excluded);
                var target = clone.GetComponentInChildren<SkinnedMeshRenderer>();
                var parent = new GameObject("Prepared rig"); parent.transform.SetParent(clone.transform, false);
                target.transform.SetParent(parent.transform, false);
                Object.DestroyImmediate(auxiliary);
                transformed = true;
                // Reusing the old hierarchy path must never redirect FaceEmo
                // to an unrelated renderer created by an authoring pass.
                var reused = new GameObject("Face", typeof(SkinnedMeshRenderer)); reused.transform.SetParent(clone.transform, false);
                reused.GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
                var fixedClip = Clip("Merged pupil removal", ("Pupil removal", 100));
                var binding = AnimationUtility.GetCurveBindings(fixedClip).Single();
                AnimationUtility.SetEditorCurve(fixedClip, binding, null); binding.path = "Prepared rig/Face";
                AnimationUtility.SetEditorCurve(fixedClip, binding, AnimationCurve.Constant(0, 1, 100));
                controller.layers[0].stateMachine.defaultState = State(controller.layers[0].stateMachine, "Merged permanent", fixedClip, writeDefaults);
                var source = new VrChatExpressionMenu.Source { Controller = controller }; var serial = 0;
                FaceEmoExpressions.ReadRegistered(clone, registered, "FaceEmo", source, ref serial, new HashSet<object>(), 0, Excluded, snapshot);
                var entry = source.Entries.Single();
                Assert.That(entry.Error, Is.Null);
                Assert.That(entry.Values.All(value => value.Path == "Prepared rig/Face"), Is.True);
                Assert.That(entry.Values.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(100).Within(.01));
                Assert.That(entry.Animation.Select(value => value.Shape), Is.EquivalentTo(new[] { "Face size" }));
                Assert.That(entry.Animation.Single().Curve.Evaluate(.5), Is.EqualTo(37.5).Within(.01));
                Assert.That(entry.Duration, Is.EqualTo(1).Within(.001));
                Assert.That(reused.GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.Zero);
                Assert.That(AnimationUtility.GetCurveBindings(original).All(value => value.path == "Face"), Is.True);
                Assert.That(AnimationUtility.GetEditorCurve(original, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"))
                    .Evaluate(.5f), Is.EqualTo(40).Within(.01));
                Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().transform.parent, Is.SameAs(avatar.transform));
                Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.Zero);
            }
            finally { Object.DestroyImmediate(clone); }
        }

        [Test]
        public void RegisteredFaceEmoTargetRemovalIsReportedEvenIfItsOldPathIsReused()
        {
            var original = new AnimationClip { name = "Registered FaceEmo" };
            AnimationUtility.SetEditorCurve(original, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                AnimationCurve.Constant(0, 1, 75));
            AssetDatabase.CreateAsset(original, folder + "/FaceEmo.anim");
            var registered = new { Modes = new[] { new { DisplayName = "Registered", ChangeDefaultFace = true,
                Animation = new { GUID = AssetDatabase.AssetPathToGUID(folder + "/FaceEmo.anim") } } } };
            var clone = Object.Instantiate(avatar);
            try
            {
                var snapshot = FaceEmoExpressions.CaptureRegistered(clone, registered);
                Object.DestroyImmediate(clone.GetComponentInChildren<SkinnedMeshRenderer>().gameObject);
                var replacement = new GameObject("Face", typeof(SkinnedMeshRenderer)); replacement.transform.SetParent(clone.transform, false);
                replacement.GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
                var source = new VrChatExpressionMenu.Source(); var serial = 0;
                FaceEmoExpressions.ReadRegistered(clone, registered, "FaceEmo", source, ref serial, new HashSet<object>(), 0, bindings: snapshot);
                Assert.That(source.Entries.Single().Error, Does.Contain("FaceEmoの表情対象が残っていません"));
                Assert.That(replacement.GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(1), Is.Zero);
            }
            finally { Object.DestroyImmediate(clone); }
        }

        [Test]
        public void RegisteredFaceEmoOriginalManualOmissionStaysOmittedAfterReparenting()
        {
            var original = new AnimationClip { name = "Registered FaceEmo" };
            AnimationUtility.SetEditorCurve(original, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                AnimationCurve.Constant(0, 1, 75));
            AssetDatabase.CreateAsset(original, folder + "/FaceEmo.anim");
            var registered = new { Modes = new[] { new { DisplayName = "Registered", ChangeDefaultFace = true,
                Animation = new { GUID = AssetDatabase.AssetPathToGUID(folder + "/FaceEmo.anim") } } } };
            var clone = Object.Instantiate(avatar);
            try
            {
                bool Excluded(string path) => path == "Face";
                var snapshot = FaceEmoExpressions.CaptureRegistered(clone, registered, excludedPath: Excluded);
                var target = clone.GetComponentInChildren<SkinnedMeshRenderer>();
                var parent = new GameObject("Prepared rig"); parent.transform.SetParent(clone.transform, false);
                target.transform.SetParent(parent.transform, false);
                var source = new VrChatExpressionMenu.Source(); var serial = 0;
                FaceEmoExpressions.ReadRegistered(clone, registered, "FaceEmo", source, ref serial, new HashSet<object>(), 0, Excluded, snapshot);
                Assert.That(source.Entries.Single().Values, Is.Empty);
                Assert.That(source.Entries.Single().Error, Does.Contain("顔のBlendShapeを含まない"));
                Assert.That(target.GetBlendShapeWeight(1), Is.Zero);
            }
            finally { Object.DestroyImmediate(clone); }
        }

        [Test]
        public void RegisteredFaceEmoMissingOriginalTargetCannotBindAnUnrelatedGeneratedRenderer()
        {
            var original = new AnimationClip { name = "Registered FaceEmo" };
            AnimationUtility.SetEditorCurve(original, EditorCurveBinding.FloatCurve("Generated Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                AnimationCurve.Constant(0, 1, 75));
            AssetDatabase.CreateAsset(original, folder + "/FaceEmo.anim");
            var registered = new { Modes = new[] { new { DisplayName = "Registered", ChangeDefaultFace = true,
                Animation = new { GUID = AssetDatabase.AssetPathToGUID(folder + "/FaceEmo.anim") } } } };
            var clone = Object.Instantiate(avatar);
            try
            {
                var snapshot = FaceEmoExpressions.CaptureRegistered(clone, registered);
                var generated = new GameObject("Generated Face", typeof(SkinnedMeshRenderer)); generated.transform.SetParent(clone.transform, false);
                generated.GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
                var source = new VrChatExpressionMenu.Source(); var serial = 0;
                FaceEmoExpressions.ReadRegistered(clone, registered, "FaceEmo", source, ref serial, new HashSet<object>(), 0, bindings: snapshot);
                Assert.That(source.Entries.Single().Error, Does.Contain("Rendererのパスを一意に解決できません"));
                Assert.That(source.Entries.Single().Values, Is.Empty);
                Assert.That(generated.GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(1), Is.Zero);
                Assert.That(AnimationUtility.GetCurveBindings(original).Single().path, Is.EqualTo("Generated Face"));
            }
            finally { Object.DestroyImmediate(clone); }
        }

        [Test]
        public void RegisteredFaceEmoRetainedTargetCanMoveIntoAnOriginallyManuallyExcludedPath()
        {
            var head = new GameObject("Head"); head.transform.SetParent(avatar.transform, false);
            var omitted = new GameObject("Face", typeof(SkinnedMeshRenderer)); omitted.transform.SetParent(head.transform, false);
            omitted.GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
            var original = new AnimationClip { name = "Registered FaceEmo" };
            AnimationUtility.SetEditorCurve(original, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                AnimationCurve.Linear(0, 0, 1, 75));
            AnimationUtility.SetEditorCurve(original, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"),
                AnimationCurve.Linear(0, 0, 1, 80));
            AssetDatabase.CreateAsset(original, folder + "/FaceEmo.anim");
            var registered = new { Modes = new[] { new { DisplayName = "Registered", ChangeDefaultFace = true,
                Animation = new { GUID = AssetDatabase.AssetPathToGUID(folder + "/FaceEmo.anim") } } } };
            using var exclusions = new ExportObjectExclusions(avatar, new[] { omitted });
            var clone = Object.Instantiate(avatar);
            try
            {
                exclusions.Apply(clone, null);
                bool PreparedExcluded(string path) => VrChatExpressionSampler.IsExcludedPreparedPath(clone, path, exclusions.ContainsPath);
                var snapshot = FaceEmoExpressions.CaptureRegistered(clone, registered, excludedPath: exclusions.ContainsPath,
                    preparedExcludedPath: PreparedExcluded);
                var target = clone.transform.Find("Face").GetComponent<SkinnedMeshRenderer>();
                target.transform.SetParent(clone.transform.Find("Head"), false);
                Assert.That(exclusions.ContainsPath("Head/Face"), Is.True, "This predicate intentionally describes the original source hierarchy.");
                var fixedClip = Clip("Merged pupil removal", ("Pupil removal", 100));
                var binding = AnimationUtility.GetCurveBindings(fixedClip).Single();
                AnimationUtility.SetEditorCurve(fixedClip, binding, null); binding.path = "Head/Face";
                AnimationUtility.SetEditorCurve(fixedClip, binding, AnimationCurve.Constant(0, 1, 100));
                controller.layers[0].stateMachine.defaultState = State(controller.layers[0].stateMachine, "Merged permanent", fixedClip);
                var neutral = VrChatExpressionSampler.SampleDefaults(clone, controller, Parameters(0), PreparedExcluded);
                Assert.That(neutral.Single(value => value.Shape == "Pupil removal").Path, Is.EqualTo("Head/Face"));
                Assert.That(neutral.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(100).Within(.01));
                var source = new VrChatExpressionMenu.Source { Controller = controller }; var serial = 0;
                FaceEmoExpressions.ReadRegistered(clone, registered, "FaceEmo", source, ref serial, new HashSet<object>(), 0, exclusions.ContainsPath, snapshot);
                var entry = source.Entries.Single();
                Assert.That(entry.Error, Is.Null);
                Assert.That(entry.Values.All(value => value.Path == "Head/Face"), Is.True);
                Assert.That(entry.Values.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(100).Within(.01));
                Assert.That(entry.Animation.Select(value => value.Shape), Is.EquivalentTo(new[] { "Face size" }));
                Assert.That(entry.Animation.Single().Curve.Evaluate(.5), Is.EqualTo(37.5).Within(.01));
                Assert.That(target.GetBlendShapeWeight(2), Is.Zero);
                Assert.That(omitted != null && omitted.transform.parent == head.transform, Is.True);
                Assert.That(AnimationUtility.GetCurveBindings(original).All(value => value.path == "Face"), Is.True);
            }
            finally { Object.DestroyImmediate(clone); }
        }

        [Test]
        public void InstalledMaReparentsFaceEmoRendererAndMergesPermanentPupilFxBeforeEvaluation()
        {
            Type Installed(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(name)).FirstOrDefault(type => type != null);
            var proxyType = Installed("nadena.dev.modular_avatar.core.ModularAvatarBoneProxy");
            var mergeType = Installed("nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator");
            var rootType = Installed("nadena.dev.ndmf.runtime.components.NDMFAvatarRoot");
            var descriptorType = Installed("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            if (proxyType == null || mergeType == null || rootType == null || descriptorType == null)
                Assert.Ignore("Install MA, NDMF and the real VRChat SDK for FaceEmo's prepared path integration.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            var source = fixture.Source;
            source.AddComponent(rootType);
            var hair = source.transform.Find("Independent hair");
            var front = source.transform.Find("Front"); front.SetParent(hair, true);
            var proxy = hair.gameObject.AddComponent(proxyType);
            proxyType.GetProperty("target").SetValue(proxy, source.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Head));
            var zeros = new Vector3[fixture.Mesh.vertexCount];
            var deltas = new Vector3[fixture.Mesh.vertexCount]; deltas[0] = Vector3.up;
            fixture.Mesh.AddBlendShapeFrame("FaceEmo smile", 100, deltas, zeros, zeros);
            fixture.Mesh.AddBlendShapeFrame("Pupil removal", 100, deltas, zeros, zeros);
            const string authoredPath = "Independent hair/Front";
            var original = new AnimationClip { name = "Registered FaceEmo" };
            AnimationUtility.SetEditorCurve(original, EditorCurveBinding.FloatCurve(authoredPath, typeof(SkinnedMeshRenderer), "blendShape.FaceEmo smile"),
                AnimationCurve.Linear(0, 0, 1, 75));
            AnimationUtility.SetEditorCurve(original, EditorCurveBinding.FloatCurve(authoredPath, typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"),
                AnimationCurve.Linear(0, 0, 1, 80));
            AssetDatabase.CreateAsset(original, folder + "/FaceEmo.anim");
            var registered = new { Modes = new[] { new { DisplayName = "Registered", ChangeDefaultFace = true,
                Animation = new { GUID = AssetDatabase.AssetPathToGUID(folder + "/FaceEmo.anim") } } } };
            var permanent = new AnimationClip { name = "Always remove pupil" };
            AnimationUtility.SetEditorCurve(permanent, EditorCurveBinding.FloatCurve(authoredPath, typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"),
                AnimationCurve.Constant(0, 1, 100));
            AssetDatabase.AddObjectToAsset(permanent, controller);
            controller.layers[0].stateMachine.defaultState = State(controller.layers[0].stateMachine, "Always", permanent);
            var descriptor = source.AddComponent(descriptorType);
            var emptyFx = AnimatorController.CreateAnimatorControllerAtPath(folder + "/OriginalFX.controller");
            using (var data = new SerializedObject(descriptor))
            {
                data.FindProperty("customizeAnimationLayers").boolValue = true;
                var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = 1;
                var item = layers.GetArrayElementAtIndex(0);
                var type = item.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
                item.FindPropertyRelative("isDefault").boolValue = false;
                item.FindPropertyRelative("animatorController").objectReferenceValue = emptyFx;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            var merge = source.AddComponent(mergeType);
            mergeType.GetField("animator").SetValue(merge, controller);
            var layerType = mergeType.GetField("layerType"); layerType.SetValue(merge, Enum.Parse(layerType.FieldType, "FX"));
            var pathMode = mergeType.GetField("pathMode"); pathMode.SetValue(merge, Enum.Parse(pathMode.FieldType, "Absolute"));
            var clone = Object.Instantiate(source); clone.name = source.name;
            try
            {
                var copiedFront = clone.transform.Find(authoredPath).GetComponent<SkinnedMeshRenderer>();
                var snapshot = FaceEmoExpressions.CaptureRegistered(clone, registered);
                var evaluated = false;
                using (NdmfExportPreparation.Prepare(source, clone, afterTransforming: prepared =>
                {
                    var preparedPath = AnimationUtility.CalculateTransformPath(copiedFront.transform, clone.transform);
                    Assert.That(preparedPath, Is.Not.EqualTo(authoredPath), "The real MA Bone Proxy must reparent the registered clip's target.");
                    var menu = VrChatExpressionMenu.Read(clone);
                    Assert.That(menu.Controller, Is.Not.Null);
                    Assert.That(menu.Controller, Is.Not.SameAs(controller));
                    var serial = 0;
                    FaceEmoExpressions.ReadRegistered(clone, registered, "FaceEmo", menu, ref serial, new HashSet<object>(), 0, bindings: snapshot);
                    var entry = menu.Entries.Single();
                    Assert.That(entry.Error, Is.Null);
                    Assert.That(entry.Values.All(value => value.Path == preparedPath), Is.True);
                    Assert.That(entry.Values.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(100).Within(.01));
                    Assert.That(entry.Animation.Select(value => value.Shape), Is.EquivalentTo(new[] { "FaceEmo smile" }));
                    Assert.That(entry.Animation.Single().Curve.Evaluate(.5), Is.EqualTo(37.5).Within(.01));
                    evaluated = true;
                })) { }
                Assert.That(evaluated, Is.True);
                Assert.That(front.parent, Is.SameAs(hair));
                Assert.That(front.GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.Zero);
                Assert.That(AnimationUtility.GetCurveBindings(original).All(binding => binding.path == authoredPath), Is.True);
                Assert.That(AnimationUtility.GetCurveBindings(permanent).Single().path, Is.EqualTo(authoredPath));
            }
            finally { Object.DestroyImmediate(clone); }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void DirectClipUsesLayerPriorityWithWriteDefaults(bool permanentLast, bool writeDefaults)
        {
            AnimatorStateMachine selectedMachine;
            var selectedLayer = permanentLast ? 0 : 1;
            if (permanentLast) { selectedMachine = controller.layers[0].stateMachine; Menu(selectedMachine, true, writeDefaults); Permanent(AddLayer("Permanent"), writeDefaults); }
            else { Permanent(controller.layers[0].stateMachine, writeDefaults); selectedMachine = AddLayer("Selected"); Menu(selectedMachine, true, writeDefaults); }
            var clip = (AnimationClip)selectedMachine.states.Single(item => item.state.name == "Selected").state.motion;
            var entry = new VrChatExpressionMenu.Entry();
            VrChatGestureExpressions.ReadClip(avatar, clip, entry);
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, selectedLayer, writeDefaults);
            Assert.That(entry.Values.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(permanentLast ? 100 : 0).Within(.01));
        }

        [Test]
        public void AdditiveDirectClipIsRejectedWithoutGuessingItsReferencePose()
        {
            Permanent(controller.layers[0].stateMachine);
            var machine = AddLayer("Additive expression"); Menu(machine, true);
            var layers = controller.layers; layers[1].blendingMode = AnimatorLayerBlendingMode.Additive; controller.layers = layers;
            var clip = (AnimationClip)machine.states.Single(item => item.state.name == "Selected").state.motion;
            var entry = new VrChatExpressionMenu.Entry();
            VrChatGestureExpressions.ReadClip(avatar, clip, entry);
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1));
            StringAssert.Contains("Additive", error.Message);
            Assert.That(entry.Values.Single(value => value.Shape == "Pupil removal").Weight, Is.Zero);
        }

        [Test]
        public void ExcludedPermanentTargetDoesNotBecomeANeutralSamplingRoot()
        {
            Permanent(controller.layers[0].stateMachine);
            Assert.That(VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0), path => path == "Face"), Is.Empty);
        }

        [Test]
        public void ApplyingDefaultsRequiresAnIndependentExportCopy()
        { Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.ApplyMergedDefaults(avatar, avatar)); }

        [Test]
        public void ApplyingCommittedFxDefaultsChangesOnlyTheCopy()
        {
            var descriptorType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor"))
                .FirstOrDefault(type => type != null);
            if (descriptorType == null) Assert.Ignore("Install the real VRChat SDK for committed descriptor integration.");
            Permanent(controller.layers[0].stateMachine);
            var descriptor = avatar.AddComponent(descriptorType);
            using (var data = new SerializedObject(descriptor))
            {
                data.FindProperty("customizeAnimationLayers").boolValue = true;
                var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = 1;
                var item = layers.GetArrayElementAtIndex(0);
                var type = item.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
                item.FindPropertyRelative("isDefault").boolValue = false;
                item.FindPropertyRelative("animatorController").objectReferenceValue = controller;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            var clone = Object.Instantiate(avatar);
            try
            {
                VrChatExpressionSampler.ApplyMergedDefaults(avatar, clone);
                Assert.That(clone.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.EqualTo(100).Within(.01));
                Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.Zero);
                Assert.That(clone.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh, Is.SameAs(mesh));
                Assert.That(controller.layers[0].stateMachine.defaultState.motion, Is.Not.Null);
            }
            finally { Object.DestroyImmediate(clone); }
        }
    }
}
