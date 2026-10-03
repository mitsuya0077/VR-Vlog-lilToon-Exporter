using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UniGLTF;
using UniVRM10;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
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

        AnimatorStateMachine EquivalentRelay(int transitionMode, bool writeDefaults, out AnimatorState other)
        {
            controller.AddParameter("Relay", AnimatorControllerParameterType.Float);
            var relay = AddLayer("Parameter relay");
            var clip = Clip("Shared constant relay");
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "Relay"), AnimationCurve.Constant(0, 1, 1));
            var initial = State(relay, "Initial", clip, writeDefaults);
            other = State(relay, "Other", clip, writeDefaults); relay.defaultState = initial;
            var transition = initial.AddTransition(other);
            transition.hasExitTime = transitionMode == 0; transition.exitTime = 10;
            transition.hasFixedDuration = true; transition.duration = transitionMode == 2 ? 10 : 0;
            transition.AddCondition(transitionMode == 0 ? AnimatorConditionMode.Less : AnimatorConditionMode.Equals,
                transitionMode == 0 ? .5f : 1, transitionMode == 0 ? "Relay" : "Menu");
            return relay;
        }

        // Match the sampler's default dwell and selection dwell using the
        // original controller, independently of its equivalence proof.
        (Dictionary<string, float> Weights, bool InTransition) NativeRelayPose(int selected)
        {
            var copy = Object.Instantiate(avatar);
            var graph = PlayableGraph.Create("Constant relay reference"); graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            try
            {
                var animator = copy.GetComponent<Animator>(); animator.runtimeAnimatorController = null;
                animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                for (var index = 0; index < controller.layers.Length; index++) playable.SetLayerWeight(index, index == 0 ? 1 : controller.layers[index].defaultWeight);
                var output = AnimationPlayableOutput.Create(graph, "Face", animator); output.SetSourcePlayable(playable);
                playable.SetInteger("Menu", 0); graph.Play();
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                playable.SetInteger("Menu", selected);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                var renderer = copy.GetComponentInChildren<SkinnedMeshRenderer>();
                return (Enumerable.Range(0, mesh.blendShapeCount).ToDictionary(mesh.GetBlendShapeName, renderer.GetBlendShapeWeight), playable.IsInTransition(1));
            }
            finally { if (graph.IsValid()) graph.Destroy(); Object.DestroyImmediate(copy); }
        }

        [TestCase(0, false)]
        [TestCase(0, true)]
        [TestCase(1, false)]
        [TestCase(1, true)]
        [TestCase(2, false)]
        [TestCase(2, true)]
        public void EquivalentConstantRelayPreservesNativeNeutralAndMenuEvenDuringTransitions(int transitionMode, bool writeDefaults)
        {
            Menu(controller.layers[0].stateMachine);
            EquivalentRelay(transitionMode, writeDefaults, out _); Permanent(AddLayer("Permanent pupil"));
            var original = EditorJsonUtility.ToJson(controller);
            var neutralReference = NativeRelayPose(0); var selectedReference = NativeRelayPose(1);
            Assert.That(neutralReference.InTransition, Is.False);
            Assert.That(selectedReference.InTransition, Is.EqualTo(transitionMode == 2), "The long-transition case must actually be sampled mid-transition.");
            var neutral = VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0));
            var selected = VrChatExpressionSampler.Sample(avatar, controller, Parameters(0), Parameters(1));
            foreach (var values in new[] { neutral, selected })
            {
                var expected = ReferenceEquals(values, neutral) ? neutralReference.Weights : selectedReference.Weights;
                foreach (var value in values) Assert.That(value.Weight, Is.EqualTo(expected[value.Shape]).Within(.01), value.Shape);
                Assert.That(values.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(100).Within(.01));
            }
            Assert.That(selected.Any(value => value.Shape == "Face size"), Is.True);
            var selectedClip = (AnimationClip)controller.layers[0].stateMachine.states.Single(child => child.state.name == "Selected").state.motion;
            foreach (var standalone in new[] { false, true })
            {
                var direct = new VrChatExpressionMenu.Entry { Name = standalone ? "Registered FaceEmo" : "Gesture" };
                VrChatGestureExpressions.ReadClip(avatar, selectedClip, direct);
                VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, direct, standalone ? (int?)null : 0);
                foreach (var value in direct.Values) Assert.That(value.Weight, Is.EqualTo(selectedReference.Weights[value.Shape]).Within(.01), direct.Name + ": " + value.Shape);
                Assert.That(direct.Values.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(100).Within(.01));
            }
            Assert.That(VrChatExpressionSampler.HasEquivalentConstantStates(controller, 1), Is.True);
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(original));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ParameterOrEmptyWriteDefaultsLayerPreservesStandaloneClipsWithoutPermanentMorphs(bool parameterCurve)
        {
            Menu(controller.layers[0].stateMachine);
            var clip = Clip(parameterCurve ? "Constant parameter only" : "Empty Write Defaults");
            if (parameterCurve)
            {
                controller.AddParameter("Relay", AnimatorControllerParameterType.Float);
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "Relay"), AnimationCurve.Constant(0, 1, 1));
            }
            var upper = AddLayer("Write Defaults only");
            upper.defaultState = State(upper, "Always", clip, writeDefaults: true);
            Assert.That(ExpressionDependencies.StationaryMorphBindings(controller).Count, Is.Zero,
                "This regression must reach the former early-return path without a permanent morph layer.");
            var selected = (AnimationClip)controller.layers[0].stateMachine.states.Single(child => child.state.name == "Selected").state.motion;
            var reference = NativeStandalonePose(avatar, controller, selected, "Face");
            var entry = new VrChatExpressionMenu.Entry { Name = "Registered face" };
            VrChatGestureExpressions.ReadClip(avatar, selected, entry);
            var authored = entry.Values.Single(value => value.Shape == "Face size").Weight;
            Assert.That(reference.Weights["Face size"], Is.EqualTo(authored).Within(.01),
                "This authored WD layer has no morph curves and must preserve the standalone expression.");
            var beforeController = EditorJsonUtility.ToJson(controller);
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry);
            Assert.That(entry.Error, Is.Null);
            Assert.That(entry.Values.Single(value => value.Shape == "Face size").Weight,
                Is.EqualTo(reference.Weights["Face size"]).Within(.01));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(beforeController));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(1), Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FractionalGestureWithoutPermanentMorphsKeepsEquivalentParameterOrEmptyBaseLayer(bool parameterCurve)
        {
            var baseClip = Clip(parameterCurve ? "Constant base parameter" : "Empty base Write Defaults");
            if (parameterCurve)
            {
                controller.AddParameter("Relay", AnimatorControllerParameterType.Float);
                AnimationUtility.SetEditorCurve(baseClip, EditorCurveBinding.FloatCurve("", typeof(Animator), "Relay"), AnimationCurve.Constant(0, 1, 1));
            }
            var baseMachine = controller.layers[0].stateMachine;
            baseMachine.defaultState = State(baseMachine, "Base", baseClip, writeDefaults: true);
            var gestureMachine = AddLayer("Fractional gesture"); Menu(gestureMachine);
            var layers = controller.layers; layers[1].defaultWeight = .5f; controller.layers = layers;
            Assert.That(ExpressionDependencies.StationaryMorphBindings(controller).Count, Is.Zero);
            Assert.That(VrChatExpressionSampler.HasEquivalentConstantStates(controller, 0), Is.True);
            var selected = (AnimationClip)gestureMachine.states.Single(child => child.state.name == "Selected").state.motion;
            var reference = NativePose(avatar, controller, "Face", new Dictionary<string, int> { ["Menu"] = 1 });
            var entry = new VrChatExpressionMenu.Entry { Name = "Fractional gesture" };
            VrChatGestureExpressions.ReadClip(avatar, selected, entry);
            var authored = entry.Values.Single(value => value.Shape == "Face size").Weight;
            Assert.That(authored, Is.EqualTo(75));
            Assert.That(Math.Abs(reference.Weights["Face size"] - authored), Is.GreaterThan(.01),
                "The authored non-base layer must change its clip weight in native Unity; the old early return would leave it at 75.");
            var beforeController = EditorJsonUtility.ToJson(controller);
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1);
            Assert.That(entry.Error, Is.Null);
            Assert.That(entry.Values.Single(value => value.Shape == "Face size").Weight,
                Is.EqualTo(reference.Weights["Face size"]).Within(.01));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(beforeController));
            Assert.That(controller.layers[1].defaultWeight, Is.EqualTo(.5f));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(1), Is.Zero);
        }

        [TestCase("different clip")]
        [TestCase("different write defaults")]
        [TestCase("changing parameter curve")]
        [TestCase("object curve")]
        [TestCase("event")]
        [TestCase("outside destination")]
        [TestCase("time parameter")]
        public void ConstantRelayProofRejectsOutputOrCallbackDifferences(string difference)
        {
            Menu(controller.layers[0].stateMachine);
            var relay = EquivalentRelay(0, true, out var other); Permanent(AddLayer("Permanent pupil"));
            var clip = (AnimationClip)other.motion;
            switch (difference)
            {
                case "different clip": other.motion = Clip("Different relay clip", ("Face size", 100)); break;
                case "different write defaults": other.writeDefaultValues = false; break;
                case "changing parameter curve":
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "Relay"), AnimationCurve.Linear(0, 1, 30, 0)); break;
                case "object curve":
                    AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve("Face", typeof(SkinnedMeshRenderer), "m_Mesh"),
                        new[] { new ObjectReferenceKeyframe { time = 0, value = mesh } }); break;
                case "event": AnimationUtility.SetAnimationEvents(clip, new[] { new AnimationEvent { time = 20, functionName = "AfterSampling" } }); break;
                case "outside destination": relay.defaultState.transitions[0].destinationState = null; relay.defaultState.transitions[0].isExit = true; break;
                case "time parameter": other.timeParameter = "Relay"; other.timeParameterActive = true; break;
            }
            Assert.That(VrChatExpressionSampler.HasEquivalentConstantStates(controller, 1), Is.False);
            Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0)));
        }

        [Test]
        public void ConstantRelayProofDoesNotAcceptAnSdkBehaviourEvenWhenClipsMatch()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAvatarParameterDriver"))
                .FirstOrDefault(candidate => candidate != null);
            if (type == null) Assert.Ignore("Install the real VRChat SDK for callback equivalence regression.");
            Menu(controller.layers[0].stateMachine);
            EquivalentRelay(0, true, out var other); Permanent(AddLayer("Permanent pupil"));
            other.AddStateMachineBehaviour(type);
            Assert.That(VrChatExpressionSampler.HasEquivalentConstantStates(controller, 1), Is.False);
            Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0)));
        }

        [Test]
        public void InstalledMaConstantParameterRelayKeepsMergedNeutralAndMenuPupilRemoval()
        {
            Type Installed(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(name)).FirstOrDefault(type => type != null);
            var mergeType = Installed("nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator");
            var rootType = Installed("nadena.dev.ndmf.runtime.components.NDMFAvatarRoot");
            var descriptorType = Installed("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            if (mergeType == null || rootType == null || descriptorType == null)
                Assert.Ignore("Install MA, NDMF and the real VRChat SDK for the generated constant-relay regression.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            var source = fixture.Source; source.AddComponent(rootType);
            var zeros = new Vector3[fixture.Mesh.vertexCount]; var delta = new Vector3[fixture.Mesh.vertexCount]; delta[0] = Vector3.up;
            fixture.Mesh.AddBlendShapeFrame("Face size", 100, delta, zeros, zeros);
            fixture.Mesh.AddBlendShapeFrame("Pupil removal", 100, delta, zeros, zeros);
            Menu(controller.layers[0].stateMachine);
            foreach (var clip in controller.animationClips.Distinct())
            {
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, binding); AnimationUtility.SetEditorCurve(clip, binding, null);
                    var mapped = binding; mapped.path = "Front"; AnimationUtility.SetEditorCurve(clip, mapped, curve);
                }
            }
            var permanent = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Merge.controller");
            var pupil = new AnimationClip { name = "Permanent pupil" }; AssetDatabase.AddObjectToAsset(pupil, permanent);
            AnimationUtility.SetEditorCurve(pupil, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"), AnimationCurve.Constant(0, 1, 100));
            permanent.layers[0].stateMachine.defaultState = State(permanent.layers[0].stateMachine, "Always", pupil);
            var descriptor = source.AddComponent(descriptorType);
            using (var data = new SerializedObject(descriptor))
            {
                data.FindProperty("customizeAnimationLayers").boolValue = true;
                var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = 1;
                var item = layers.GetArrayElementAtIndex(0); var type = item.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
                item.FindPropertyRelative("isDefault").boolValue = false; item.FindPropertyRelative("animatorController").objectReferenceValue = controller;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            var tool = new GameObject("Permanent pupil tool"); tool.transform.SetParent(source.transform, false);
            var merge = tool.AddComponent(mergeType); mergeType.GetField("animator").SetValue(merge, permanent);
            var pathMode = mergeType.GetField("pathMode"); pathMode.SetValue(merge, Enum.Parse(pathMode.FieldType, "Absolute"));
            mergeType.GetField("layerPriority").SetValue(merge, 100); mergeType.GetField("matchAvatarWriteDefaults").SetValue(merge, false);
            var sourceGraph = EditorJsonUtility.ToJson(controller); var sourceMesh = EditorJsonUtility.ToJson(fixture.Mesh);
            var clone = Object.Instantiate(source); clone.name = source.name;
            try
            {
                var evaluated = false;
                using (NdmfExportPreparation.Prepare(source, clone, afterTransforming: prepared =>
                {
                    var metadata = VrChatExpressionMenu.Read(clone);
                    var fx = ExpressionDependencies.Controller(metadata.Controller);
                    Assert.That(Enumerable.Range(0, fx.layers.Length).Any(index => fx.layers[index].stateMachine.states.Length >= 3 &&
                        VrChatExpressionSampler.HasEquivalentConstantStates(fx, index)), Is.True, "Real MA must generate the constant parameter relay with its default MMD setting.");
                    var neutralReference = NativePose(clone, fx, "Front", new Dictionary<string, int> { ["Menu"] = 0 });
                    var selectedReference = NativePose(clone, fx, "Front", new Dictionary<string, int> { ["Menu"] = 1 });
                    var neutral = VrChatExpressionSampler.SampleDefaults(clone, metadata.Controller, metadata.Defaults, metadata: metadata);
                    var selected = VrChatExpressionSampler.Sample(clone, metadata.Controller, metadata.Defaults, Parameters(1), metadata: metadata);
                    Assert.That(neutral.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(neutralReference.Weights["Pupil removal"]).Within(.01));
                    Assert.That(selected.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(100).Within(.01));
                    foreach (var value in selected) Assert.That(value.Weight, Is.EqualTo(selectedReference.Weights[value.Shape]).Within(.01), value.Shape);
                    evaluated = true;
                })) { }
                Assert.That(evaluated, Is.True);
                Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(sourceGraph)); Assert.That(EditorJsonUtility.ToJson(fixture.Mesh), Is.EqualTo(sourceMesh));
                Assert.That(source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.Zero);
            }
            finally { Object.DestroyImmediate(clone); }
        }

        // Independent native Animator playback provides the expected result;
        // it does not use the export sampler or its reconstructed probe graph.
        static (Dictionary<string, float> Weights, Vector3[] Vertices) NativePose(GameObject source, AnimatorController fx,
            string path, IDictionary<string, int> parameters)
        {
            var copy = Object.Instantiate(source);
            var graph = PlayableGraph.Create("Weighted FX regression reference");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            try
            {
                foreach (var behaviour in copy.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                var animator = copy.GetComponent<Animator>(); animator.enabled = true;
                animator.runtimeAnimatorController = null; animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                var playable = AnimatorControllerPlayable.Create(graph, fx);
                for (var index = 0; index < fx.layers.Length; index++) playable.SetLayerWeight(index, index == 0 ? 1 : fx.layers[index].defaultWeight);
                var output = AnimationPlayableOutput.Create(graph, "Face", animator); output.SetSourcePlayable(playable);
                graph.Play(); graph.Evaluate(0);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                foreach (var parameter in parameters) playable.SetInteger(parameter.Key, parameter.Value);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                var renderer = copy.transform.Find(path).GetComponent<SkinnedMeshRenderer>();
                var weights = Enumerable.Range(0, renderer.sharedMesh.blendShapeCount)
                    .ToDictionary(renderer.sharedMesh.GetBlendShapeName, renderer.GetBlendShapeWeight);
                return (weights, WorldVertices(renderer));
            }
            finally { if (graph.IsValid()) graph.Destroy(); Object.DestroyImmediate(copy); }
        }

        // Registered FaceEmo clips are a WD-Off base below the authored
        // permanent FX layers. They do not inherit the selected menu state's
        // WD setting. Replay that authored configuration independently of the
        // export sampler's scalar response probes.
        (Dictionary<string, float> Weights, Vector3[] Vertices) NativeStandalonePose(GameObject source, AnimatorController fx, AnimationClip clip, string path)
        {
            var reference = AnimatorController.CreateAnimatorControllerAtPath(folder + "/StandaloneReference.controller");
            reference.parameters = fx.parameters.Select(parameter => new AnimatorControllerParameter
            {
                name = parameter.name, type = parameter.type, defaultBool = parameter.defaultBool,
                defaultInt = parameter.defaultInt, defaultFloat = parameter.defaultFloat
            }).ToArray();
            var baseMachine = reference.layers[0].stateMachine;
            baseMachine.defaultState = State(baseMachine, "Registered expression", clip);
            foreach (var original in fx.layers.Skip(1))
            {
                Assert.That(original.stateMachine.states.Length, Is.EqualTo(1), "This reference owns only the fixture's stationary upper layers.");
                reference.AddLayer(original.name); var layers = reference.layers; var layer = layers[layers.Length - 1];
                layer.defaultWeight = original.defaultWeight; layer.blendingMode = original.blendingMode; layer.avatarMask = original.avatarMask;
                var state = original.stateMachine.defaultState;
                layer.stateMachine.defaultState = State(layer.stateMachine, "Authored permanent", (AnimationClip)state.motion, state.writeDefaultValues);
                reference.layers = layers;
            }
            return NativePose(source, reference, path, new Dictionary<string, int>());
        }

        static Vector3[] WorldVertices(SkinnedMeshRenderer renderer)
        {
            var baked = new Mesh();
            try { renderer.BakeMesh(baked, false); return baked.vertices.Select(renderer.transform.TransformPoint).ToArray(); }
            finally { Object.DestroyImmediate(baked); }
        }

        static void AssertVertices(Vector3[] expected, Vector3[] actual)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            for (var index = 0; index < expected.Length; index++)
                Assert.That(Vector3.Distance(expected[index], actual[index]), Is.LessThan(.0005f), "Vertex " + index);
        }

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
        public void FractionalStationaryFxUsesPreparedBaseOnceForMenuGestureAndFaceEmo(bool additive, bool writeDefaults)
        {
            var descriptorType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor"))
                .FirstOrDefault(type => type != null);
            if (descriptorType == null) Assert.Ignore("Install the real VRChat SDK for neutral FX integration.");
            Menu(controller.layers[0].stateMachine, writeDefaults: writeDefaults);
            Permanent(AddLayer("Weighted permanent"), writeDefaults);
            var layers = controller.layers; layers[1].defaultWeight = .5f;
            layers[1].blendingMode = additive ? AnimatorLayerBlendingMode.Additive : AnimatorLayerBlendingMode.Override; controller.layers = layers;
            var sourceSkin = avatar.GetComponentInChildren<SkinnedMeshRenderer>(); sourceSkin.SetBlendShapeWeight(2, 20);
            var descriptor = avatar.AddComponent(descriptorType);
            using (var data = new SerializedObject(descriptor))
            {
                data.FindProperty("customizeAnimationLayers").boolValue = true;
                var descriptorLayers = data.FindProperty("baseAnimationLayers"); descriptorLayers.arraySize = 1;
                var item = descriptorLayers.GetArrayElementAtIndex(0);
                var type = item.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
                item.FindPropertyRelative("isDefault").boolValue = false;
                item.FindPropertyRelative("animatorController").objectReferenceValue = controller;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            var neutral = NativePose(avatar, controller, "Face", new Dictionary<string, int> { ["Menu"] = 0 });
            var expected = NativePose(avatar, controller, "Face", new Dictionary<string, int> { ["Menu"] = 1 });
            var clip = (AnimationClip)controller.layers[0].stateMachine.states.Single(child => child.state.name == "Selected").state.motion;
            var registeredClip = Object.Instantiate(clip); registeredClip.name = "Weighted registered face";
            AssetDatabase.CreateAsset(registeredClip, folder + "/WeightedFaceEmo.anim");
            var standaloneExpected = NativeStandalonePose(avatar, controller, registeredClip, "Face");
            var registered = new { Modes = new[] { new { DisplayName = "Weighted registered", ChangeDefaultFace = true,
                Animation = new { GUID = AssetDatabase.AssetPathToGUID(folder + "/WeightedFaceEmo.anim") } } } };
            var clone = Object.Instantiate(avatar);
            try
            {
                var bindings = FaceEmoExpressions.CaptureRegistered(clone, registered);
                var menu = VrChatExpressionSampler.Sample(clone, controller, Parameters(0), Parameters(1));
                var gesture = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(clone, clip, gesture);
                VrChatExpressionSampler.ApplyPermanentOverrides(clone, controller, gesture, 0, writeDefaults);
                var faceEmo = new VrChatExpressionMenu.Source { Controller = controller }; var serial = 0;
                FaceEmoExpressions.ReadRegistered(clone, registered, "FaceEmo", faceEmo, ref serial, new HashSet<object>(), 0, bindings: bindings);
                Assert.That(faceEmo.Entries.Single().Error, Is.Null);
                foreach (var route in new[] { (Name: "Menu", Values: menu, Expected: expected),
                    (Name: "Gesture", Values: gesture.Values, Expected: expected),
                    (Name: "FaceEmo", Values: faceEmo.Entries.Single().Values, Expected: standaloneExpected) })
                {
                    Assert.That(route.Values.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(route.Expected.Weights["Pupil removal"]).Within(.01), route.Name + ": pupil");
                    Assert.That(route.Values.Single(value => value.Shape == "Face size").Weight, Is.EqualTo(route.Expected.Weights["Face size"]).Within(.01), route.Name + ": face size");
                }
                Assert.That(clone.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.EqualTo(20), "Analysis must not replace its own input baseline.");
                VrChatExpressionSampler.ApplyMergedDefaults(avatar, clone);
                Assert.That(clone.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.EqualTo(neutral.Weights["Pupil removal"]).Within(.01));
                Assert.That(sourceSkin.GetBlendShapeWeight(2), Is.EqualTo(20));
                Assert.That(controller.layers[1].defaultWeight, Is.EqualTo(.5f));
            }
            finally { Object.DestroyImmediate(clone); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NeutralFractionalOverrideKeepsMovingBaseWithoutBakingUnrelatedMorphs(bool writeDefaults)
        {
            var descriptorType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor"))
                .FirstOrDefault(type => type != null);
            if (descriptorType == null) Assert.Ignore("Install the real VRChat SDK for neutral FX integration.");
            var baseClip = Clip("Moving base face");
            AnimationUtility.SetEditorCurve(baseClip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                AnimationCurve.Linear(0, 0, 30, 100));
            var baseMachine = controller.layers[0].stateMachine;
            baseMachine.defaultState = State(baseMachine, "Moving face", baseClip);
            Permanent(AddLayer("Fractional pupil"), writeDefaults);
            var layers = controller.layers; layers[1].defaultWeight = .5f; controller.layers = layers;
            var sourceSkin = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
            sourceSkin.SetBlendShapeWeight(1, 12); sourceSkin.SetBlendShapeWeight(2, 20);
            var descriptor = avatar.AddComponent(descriptorType);
            using (var data = new SerializedObject(descriptor))
            {
                data.FindProperty("customizeAnimationLayers").boolValue = true;
                var descriptorLayers = data.FindProperty("baseAnimationLayers"); descriptorLayers.arraySize = 1;
                var item = descriptorLayers.GetArrayElementAtIndex(0);
                var type = item.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
                item.FindPropertyRelative("isDefault").boolValue = false;
                item.FindPropertyRelative("animatorController").objectReferenceValue = controller;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            var originalController = EditorJsonUtility.ToJson(controller);
            var reference = NativePose(avatar, controller, "Face", new Dictionary<string, int>());
            Assert.That(Math.Abs(reference.Weights["Face size"] - 12), Is.GreaterThan(.01), "The native base curve must actually move.");
            var neutral = VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0));
            Assert.That(neutral.Select(value => value.Shape), Is.EqualTo(new[] { "Pupil removal" }));
            Assert.That(neutral.Single().Weight, Is.EqualTo(reference.Weights["Pupil removal"]).Within(.01));
            var clone = Object.Instantiate(avatar);
            try
            {
                VrChatExpressionSampler.ApplyMergedDefaults(avatar, clone);
                var skin = clone.GetComponentInChildren<SkinnedMeshRenderer>();
                Assert.That(skin.GetBlendShapeWeight(2), Is.EqualTo(reference.Weights["Pupil removal"]).Within(.01));
                Assert.That(skin.GetBlendShapeWeight(1), Is.EqualTo(12), "The unrelated moving face must not be baked into the neutral mesh.");
                Assert.That(skin.sharedMesh, Is.SameAs(mesh));
                Assert.That(sourceSkin.GetBlendShapeWeight(1), Is.EqualTo(12));
                Assert.That(sourceSkin.GetBlendShapeWeight(2), Is.EqualTo(20));
                Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(originalController));
            }
            finally { Object.DestroyImmediate(clone); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NeutralFractionalOverrideDoesNotBakeAnUnrelatedConstantFromAMovingBase(bool writeDefaults)
        {
            var descriptorType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor"))
                .FirstOrDefault(type => type != null);
            if (descriptorType == null) Assert.Ignore("Install the real VRChat SDK for neutral FX integration.");
            var baseClip = Clip("Unrelated constant face", ("Face size", 90));
            AnimationUtility.SetEditorCurve(baseClip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink"),
                AnimationCurve.Linear(0, 0, 30, 100));
            var baseMachine = controller.layers[0].stateMachine;
            baseMachine.defaultState = State(baseMachine, "Face and moving eye", baseClip);
            Permanent(AddLayer("Fractional pupil"), writeDefaults);
            var layers = controller.layers; layers[1].defaultWeight = .5f; controller.layers = layers;
            var sourceSkin = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
            sourceSkin.SetBlendShapeWeight(0, 7); sourceSkin.SetBlendShapeWeight(1, 12); sourceSkin.SetBlendShapeWeight(2, 20);
            Assert.That(ExpressionDependencies.StationaryMorphBindings(controller).Select(binding => binding.propertyName),
                Is.EquivalentTo(new[] { "blendShape.Pupil removal" }), "The mixed moving base clip must not declare its constant face to be stationary.");
            var descriptor = avatar.AddComponent(descriptorType);
            using (var data = new SerializedObject(descriptor))
            {
                data.FindProperty("customizeAnimationLayers").boolValue = true;
                var descriptorLayers = data.FindProperty("baseAnimationLayers"); descriptorLayers.arraySize = 1;
                var item = descriptorLayers.GetArrayElementAtIndex(0);
                var type = item.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
                item.FindPropertyRelative("isDefault").boolValue = false;
                item.FindPropertyRelative("animatorController").objectReferenceValue = controller;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            var originalController = EditorJsonUtility.ToJson(controller);
            var reference = NativePose(avatar, controller, "Face", new Dictionary<string, int>());
            // Match the previous preparation's null-motion placeholder. A
            // real zero-curve clip is a different native Animator graph.
            var prunedReference = NativeStandalonePose(avatar, controller, null, "Face");
            Assert.That(Math.Abs(reference.Weights["Face size"] - 12), Is.GreaterThan(.01), "The unrelated native constant must differ from the authored renderer value.");
            Assert.That(Math.Abs(reference.Weights["Pupil removal"] - 20), Is.GreaterThan(.01), "The fractional native effect must actually move the required morph.");
            Assert.That(Math.Abs(reference.Weights["Pupil removal"] - prunedReference.Weights["Pupil removal"]), Is.GreaterThan(.01),
                "The original native base must affect fractional blending even though its own morphs are outside capture.");
            var neutral = VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0));
            Assert.That(neutral.Select(value => value.Shape), Is.EqualTo(new[] { "Pupil removal" }));
            Assert.That(neutral.Single().Weight, Is.EqualTo(reference.Weights["Pupil removal"]).Within(.01));
            var clone = Object.Instantiate(avatar);
            try
            {
                VrChatExpressionSampler.ApplyMergedDefaults(avatar, clone);
                var skin = clone.GetComponentInChildren<SkinnedMeshRenderer>();
                Assert.That(skin.GetBlendShapeWeight(2), Is.EqualTo(reference.Weights["Pupil removal"]).Within(.01));
                Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(7));
                Assert.That(skin.GetBlendShapeWeight(1), Is.EqualTo(12));
                Assert.That(skin.sharedMesh, Is.SameAs(mesh));
                Assert.That(sourceSkin.GetBlendShapeWeight(0), Is.EqualTo(7));
                Assert.That(sourceSkin.GetBlendShapeWeight(1), Is.EqualTo(12));
                Assert.That(sourceSkin.GetBlendShapeWeight(2), Is.EqualTo(20));
                Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(originalController));
            }
            finally { Object.DestroyImmediate(clone); }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void NativeSupportBaseRejectsUnsafePlayedClipAfterItsTransitionCompletes(bool writeDefaults, bool animationEvent)
        {
            var unsafeClip = Clip("Early unsupported base", ("Face size", 90));
            AnimationUtility.SetEditorCurve(unsafeClip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink"),
                AnimationCurve.Linear(0, 0, 30, 100));
            if (animationEvent) AnimationUtility.SetAnimationEvents(unsafeClip, new[] { new AnimationEvent { time = 30, functionName = "UnsupportedFutureCallback" } });
            else AnimationUtility.SetObjectReferenceCurve(unsafeClip, EditorCurveBinding.PPtrCurve("Face", typeof(SkinnedMeshRenderer), "m_Mesh"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = mesh } });
            var safeClip = Clip("Steady supported base", ("Face size", 90));
            AnimationUtility.SetEditorCurve(safeClip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink"),
                AnimationCurve.Linear(0, 0, 30, 100));
            var machine = controller.layers[0].stateMachine;
            var initial = State(machine, "Early unsupported", unsafeClip); machine.defaultState = initial;
            var steady = State(machine, "Steady supported", safeClip);
            var transition = initial.AddTransition(steady); transition.hasExitTime = false; transition.hasFixedDuration = true; transition.duration = .25f;
            transition.AddCondition(AnimatorConditionMode.Equals, 0, "Menu");
            Permanent(AddLayer("Fractional pupil"), writeDefaults);
            var layers = controller.layers; layers[1].defaultWeight = .5f; controller.layers = layers;
            var before = EditorJsonUtility.ToJson(controller);
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0)));
            Assert.That(error.Message, Is.EqualTo(animationEvent ? "常時適用FXと表情の影響範囲を確定できません。" : "表情への遷移にマテリアル・オブジェクトの差し替えが含まれます。"));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
            var skin = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
            Assert.That(skin.sharedMesh, Is.SameAs(mesh));
            Assert.That(Enumerable.Range(0, mesh.blendShapeCount).Select(skin.GetBlendShapeWeight), Is.All.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NeutralFractionalOverrideStillValidatesMovingRequiredBaseMorph(bool writeDefaults)
        {
            var baseClip = Clip("Moving required base pupil");
            AnimationUtility.SetEditorCurve(baseClip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"),
                AnimationCurve.Linear(0, 0, 30, 100));
            var machine = controller.layers[0].stateMachine; machine.defaultState = State(machine, "Moving required", baseClip);
            Permanent(AddLayer("Fractional pupil"), writeDefaults);
            var layers = controller.layers; layers[1].defaultWeight = .5f; controller.layers = layers;
            var before = EditorJsonUtility.ToJson(controller);
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0)));
            Assert.That(error.Message, Does.Contain("時間で変わる"));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NeutralFractionalOverrideRejectsDelayedBaseActivityChanges(bool writeDefaultsFlip)
        {
            var baseClip = Clip("Base face", ("Face size", 0));
            var machine = controller.layers[0].stateMachine;
            var idle = State(machine, "Idle", baseClip); machine.defaultState = idle;
            var future = State(machine, "After thirty seconds", writeDefaultsFlip ? baseClip : null, writeDefaults: writeDefaultsFlip);
            var transition = idle.AddTransition(future); transition.hasExitTime = true; transition.exitTime = 30; transition.duration = 0;
            Permanent(AddLayer("Fractional pupil"));
            var layers = controller.layers; layers[1].defaultWeight = .5f; controller.layers = layers;
            avatar.GetComponentInChildren<SkinnedMeshRenderer>().SetBlendShapeWeight(2, 20);
            var before = EditorJsonUtility.ToJson(controller);
            Assert.That(NativePose(avatar, controller, "Face", new Dictionary<string, int>()).Weights["Pupil removal"], Is.GreaterThan(20));
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0)));
            Assert.That(error.Message, Does.Contain("時間で遷移"), "A state change beyond the sampling window must still be rejected.");
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.EqualTo(20));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NeutralFractionalBaseActivityRetainsExternalAndWriterDependencies(bool external)
        {
            controller.AddParameter("BaseGate", AnimatorControllerParameterType.Float);
            var machine = controller.layers[0].stateMachine;
            var idle = State(machine, "Idle", Clip("Base idle", ("Face size", 0))); machine.defaultState = idle;
            var selected = State(machine, "External base", Clip("Base selected", ("Face size", 75)));
            var transition = idle.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Greater, .5f, "BaseGate");
            Permanent(AddLayer("Fractional pupil"));
            var layers = controller.layers; layers[1].defaultWeight = .5f; controller.layers = layers;
            var metadata = new VrChatExpressionMenu.Source();
            if (external) metadata.ExternalParameters.Add("BaseGate");
            else
            {
                var writer = AddLayer("Delayed base gate writer"); var clip = Clip("Changing base gate");
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "BaseGate"), AnimationCurve.Linear(0, 0, 30, 1));
                writer.defaultState = State(writer, "Later gate", clip);
            }
            var before = EditorJsonUtility.ToJson(controller);
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0), metadata: metadata));
            Assert.That(error.Message, Does.Contain(external ? "外部入力" : "時間で変わる"));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.Zero);
        }

        [Test]
        public void NeutralFractionalBaseCannotAssumeBuiltinGestureRemainsAtDefault()
        {
            controller.AddParameter("GestureLeft", AnimatorControllerParameterType.Int);
            var machine = controller.layers[0].stateMachine;
            var idle = State(machine, "Idle", Clip("Base idle", ("Face size", 0))); machine.defaultState = idle;
            var selected = State(machine, "Gesture changes defaults", Clip("Base gesture", ("Face size", 75)), writeDefaults: true);
            var transition = idle.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 2, "GestureLeft");
            Permanent(AddLayer("Fractional pupil"));
            var layers = controller.layers; layers[1].defaultWeight = .5f; controller.layers = layers;
            var original = EditorJsonUtility.ToJson(controller);
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleDefaults(avatar, controller, Parameters(0)));
            Assert.That(error.Message, Does.Contain("外部入力").And.Contain("GestureLeft"));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(original));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task FractionalStationaryFxGeometrySurvivesOneClickExportAndVrmReimport(bool additive)
        {
            var shader = Shader.Find("lilToon");
            if (shader == null) Assert.Ignore("Install lilToon for the real exporter entry point.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            using var menus = new MenuImportSdkRecoveryTests.SdkFixture(fixture.Source);
            var controls = (IList)VrChatExpressionMenu.Member(menus.Root, "controls"); controls.RemoveAt(1);
            var fx = (AnimatorController)VrChatExpressionMenu.Read(fixture.Source).Controller;
            var delta = new Vector3[fixture.Mesh.vertexCount]; delta[0] = Vector3.up * .1f;
            fixture.Mesh.AddBlendShapeFrame("Pupil removal", 100, delta, new Vector3[delta.Length], new Vector3[delta.Length]);
            var fixedClip = new AnimationClip { name = "Weighted permanent pupil" };
            AnimationUtility.SetEditorCurve(fixedClip, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"),
                AnimationCurve.Constant(0, 1, 100));
            AssetDatabase.AddObjectToAsset(fixedClip, fx);
            fx.AddLayer("Weighted permanent pupil");
            var layers = fx.layers; var layer = layers[layers.Length - 1]; layer.defaultWeight = .5f;
            layer.blendingMode = additive ? AnimatorLayerBlendingMode.Additive : AnimatorLayerBlendingMode.Override;
            layer.stateMachine.defaultState = State(layer.stateMachine, "Always", fixedClip); fx.layers = layers;
            var material = new Material(shader);
            foreach (var renderer in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) renderer.sharedMaterial = material;
            var sourceFront = fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>();
            var beforeMesh = EditorJsonUtility.ToJson(fixture.Mesh);
            var beforeController = EditorJsonUtility.ToJson(fx);
            var beforeDescriptor = EditorJsonUtility.ToJson(menus.Descriptor);
            var expectedNeutral = NativePose(fixture.Source, fx, "Front", new Dictionary<string, int> { ["Face"] = 0, ["GestureRight"] = 0 });
            var expectedMenu = NativePose(fixture.Source, fx, "Front", new Dictionary<string, int> { ["Face"] = 1, ["GestureRight"] = 0 });
            var expectedGesture = NativePose(fixture.Source, fx, "Front", new Dictionary<string, int> { ["Face"] = 0, ["GestureRight"] = 2 });
            Vrm10Instance imported = null;
            try
            {
                var warnings = new List<string>();
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Weighted permanent FX", "Tests", warnings,
                    blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported, Is.Not.Null, string.Join("\n", warnings));
                imported.Runtime.Process();
                var importedFront = imported.GetComponentsInChildren<SkinnedMeshRenderer>().Single(renderer => renderer.name == "Front");
                AssertVertices(expectedNeutral.Vertices, WorldVertices(importedFront));
                foreach (var candidate in new[] { (Name: "Menu face", Pose: expectedMenu), (Name: "Gesture face", Pose: expectedGesture) })
                {
                    foreach (var expression in imported.Vrm.Expression.CustomClips)
                        imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(expression.name), 0);
                    var selected = imported.Vrm.Expression.CustomClips.Single(expression => expression.name.Contains(candidate.Name));
                    imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(selected.name), 1);
                    imported.Runtime.Process();
                    AssertVertices(candidate.Pose.Vertices, WorldVertices(importedFront));
                }
                Assert.That(sourceFront.GetBlendShapeWeight(1), Is.Zero);
                Assert.That(EditorJsonUtility.ToJson(fixture.Mesh), Is.EqualTo(beforeMesh));
                Assert.That(EditorJsonUtility.ToJson(fx), Is.EqualTo(beforeController));
                Assert.That(EditorJsonUtility.ToJson(menus.Descriptor), Is.EqualTo(beforeDescriptor));
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); Object.DestroyImmediate(material); }
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

        [TestCase(false, 0f)]
        [TestCase(true, 0f)]
        [TestCase(false, 1f)]
        [TestCase(true, 1f)]
        public void DirectClipProbeRejectsOriginalAnimatorLayerControlInsteadOfUsingDefaultWeight(bool standalone, float goalWeight)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAnimatorLayerControl"))
                .FirstOrDefault(candidate => candidate != null);
            if (type == null) Assert.Ignore("Install the real VRChat SDK for animator layer-control integration.");
            var machine = controller.layers[0].stateMachine;
            Menu(machine, true);
            Permanent(AddLayer("Permanent pupil"));
            var layers = controller.layers;
            layers[1].defaultWeight = 1 - goalWeight;
            controller.layers = layers;
            var selected = machine.states.Single(child => child.state.name == "Selected").state;
            var controlling = standalone ? machine.defaultState : selected;
            var control = controlling.AddStateMachineBehaviour(type);
            using (var data = new SerializedObject(control))
            {
                var playable = data.FindProperty("playable");
                Assert.That(playable, Is.Not.Null);
                Assert.That(playable.propertyType, Is.EqualTo(SerializedPropertyType.Enum));
                var targetPlayable = Array.IndexOf(playable.enumNames, "FX");
                Assert.That(targetPlayable, Is.GreaterThanOrEqualTo(0));
                playable.enumValueIndex = targetPlayable;
                data.FindProperty("layer").intValue = 1;
                data.FindProperty("goalWeight").floatValue = goalWeight;
                data.FindProperty("blendDuration").floatValue = 0;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            var clip = (AnimationClip)selected.motion;
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Pupil removal"),
                AnimationCurve.Linear(0, 0, 1, 80));
            var entry = new VrChatExpressionMenu.Entry();
            VrChatGestureExpressions.ReadClip(avatar, clip, entry);
            var error = Assert.Throws<InvalidOperationException>(() =>
                VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, standalone ? (int?)null : 0));
            Assert.That(error.Message, Does.Contain("影響範囲").And.Contain("VRCAnimatorLayerControl").And.Contain(controlling.name));
            Assert.That(entry.Values.Single(value => value.Shape == "Pupil removal").Weight, Is.Zero,
                "The entry must not be partially rewritten using the wrong stationary layer weight.");
            Assert.That(entry.Animation.Single(value => value.Shape == "Pupil removal").Curve.Evaluate(.5), Is.EqualTo(40).Within(.01));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.Zero);
            Assert.That(controller.layers[1].defaultWeight, Is.EqualTo(1 - goalWeight));
            Assert.That(controlling.behaviours, Does.Contain(control));
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
