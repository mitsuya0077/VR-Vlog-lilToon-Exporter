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
    // Regression witnesses for combining the permanent-morph exporter with
    // fixed menu inputs and the broader native neutral-shape sampler.
    public sealed class MergedFixedNeutralSamplingTests
    {
        private string folder;
        private GameObject avatar;
        private Mesh mesh;
        private AnimatorController controller;

        [SetUp]
        public void SetUp()
        {
            var name = "__MergedFixedNeutral_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            controller.AddParameter("Menu", AnimatorControllerParameterType.Int);
            controller.AddParameter("Voice", AnimatorControllerParameterType.Float);
            controller.AddParameter("Relay", AnimatorControllerParameterType.Float);
            avatar = new GameObject("Avatar", typeof(Animator));
            var face = new GameObject("Face", typeof(SkinnedMeshRenderer)); face.transform.SetParent(avatar.transform, false);
            mesh = BaseShapeFixture.Create();
            mesh.AddBlendShapeFrame("Pupil removal", 100, new[] { Vector3.up, Vector3.zero, Vector3.zero }, null, null);
            face.GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(avatar); Object.DestroyImmediate(mesh);
            if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        private AnimationClip Clip(string name, string shape = null, float weight = 0)
        {
            var clip = new AnimationClip { name = name };
            if (shape != null) AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape." + shape),
                AnimationCurve.Constant(0, 1, weight));
            AssetDatabase.AddObjectToAsset(clip, controller); return clip;
        }

        private static AnimatorState State(AnimatorStateMachine machine, string name, AnimationClip clip, bool defaults = false)
        {
            var state = machine.AddState(name); state.motion = clip; state.writeDefaultValues = defaults; return state;
        }

        private AnimatorStateMachine Layer(string name)
        {
            controller.AddLayer(name); var layers = controller.layers; layers[layers.Length - 1].defaultWeight = 1;
            controller.layers = layers; return layers[layers.Length - 1].stateMachine;
        }

        private void RelayAndPupil(bool defaults, bool timed)
        {
            var relay = Layer("Equivalent parameter relay"); var clip = Clip("Shared relay");
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "Relay"), AnimationCurve.Constant(0, 1, 1));
            var initial = State(relay, "Initial", clip, defaults); var next = State(relay, "Equivalent", clip, defaults);
            relay.defaultState = initial;
            var transition = initial.AddTransition(next); transition.hasExitTime = timed; transition.exitTime = .01f;
            transition.hasFixedDuration = true; transition.duration = 10;
            if (!timed) transition.AddCondition(AnimatorConditionMode.Equals, 1, "Menu");
            var permanent = Layer("Permanent pupil"); permanent.defaultState = State(permanent, "Always", Clip("Pupil", "Pupil removal", 100));
        }

        private Dictionary<string, float> Native(bool select, bool requireRelay = true)
        {
            var copy = Object.Instantiate(avatar); var graph = PlayableGraph.Create("Merged neutral reference");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            try
            {
                var animator = copy.GetComponent<Animator>(); animator.runtimeAnimatorController = null;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.applyRootMotion = false; animator.fireEvents = false;
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                for (var layer = 0; layer < controller.layers.Length; layer++)
                    playable.SetLayerWeight(layer, layer == 0 ? 1 : controller.layers[layer].defaultWeight);
                AnimationPlayableOutput.Create(graph, "Face", animator).SetSourcePlayable(playable);
                playable.SetInteger("Menu", 0); playable.SetFloat("Voice", 0); graph.Play(); graph.Evaluate(0);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                if (select)
                {
                    playable.SetInteger("Menu", 1);
                    for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                }
                if (requireRelay)
                    Assert.That(playable.IsInTransition(1), Is.True, "Exercise the equivalent-state proof during a real native transition.");
                var skin = copy.GetComponentInChildren<SkinnedMeshRenderer>();
                return Enumerable.Range(0, mesh.blendShapeCount).ToDictionary(mesh.GetBlendShapeName, skin.GetBlendShapeWeight);
            }
            finally { if (graph.IsValid()) graph.Destroy(); Object.DestroyImmediate(copy); }
        }

        private void SetDescriptorFx()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly =>
                assembly.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor")).FirstOrDefault(candidate => candidate != null);
            if (type == null) Assert.Ignore("Install the real VRChat SDK.");
            var descriptor = avatar.AddComponent(type);
            using var data = new SerializedObject(descriptor);
            data.FindProperty("customizeAnimationLayers").boolValue = true;
            var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = 1;
            var layer = layers.GetArrayElementAtIndex(0); var kind = layer.FindPropertyRelative("type");
            kind.enumValueIndex = Array.IndexOf(kind.enumNames, "FX");
            layer.FindPropertyRelative("isDefault").boolValue = false;
            layer.FindPropertyRelative("animatorController").objectReferenceValue = controller;
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FixedExternalMenuKeepsPermanentMorphAndNativeWriteDefaults(bool defaults)
        {
            var machine = controller.layers[0].stateMachine;
            var idle = State(machine, "Idle", Clip("Idle", "Face size", 0)); machine.defaultState = idle;
            var selected = State(machine, "Selected", Clip("Selected", "Face size", 75));
            var transition = idle.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "Menu");
            transition.AddCondition(AnimatorConditionMode.Less, .5f, "Voice");
            RelayAndPupil(defaults, false);
            var sourceJson = EditorJsonUtility.ToJson(controller); var expected = Native(true);
            var initial = new Dictionary<string, float> { ["Menu"] = 0 };
            var context = FixedExpressionContext.Create(controller, initial);
            var values = VrChatExpressionSampler.SampleFixed(avatar, controller, initial,
                new Dictionary<string, float> { ["Menu"] = 1 }, fixedContext: context);
            Assert.That(values.Select(value => value.Shape), Does.Contain("Face size").And.Contain("Pupil removal"));
            foreach (var value in values) Assert.That(value.Weight, Is.EqualTo(expected[value.Shape]).Within(.01), value.Shape);
            Assert.That(values.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(100).Within(.01));
            Assert.That(context.UsedParameters, Does.Contain("Voice"));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(sourceJson));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(2), Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NeutralStartupWritesAndPermanentMorphSurviveEquivalentTimedRelay(bool defaults)
        {
            var machine = controller.layers[0].stateMachine;
            var startup = State(machine, "Startup", Clip("Startup", "Face size", 75)); machine.defaultState = startup;
            var held = State(machine, "Held", null);
            var transition = startup.AddTransition(held); transition.hasExitTime = true; transition.exitTime = .01f; transition.duration = 0;
            RelayAndPupil(defaults, true); SetDescriptorFx();
            var sourceJson = EditorJsonUtility.ToJson(controller); var expected = Native(false);
            var values = NeutralShapeSampler.Sample(avatar);
            Assert.That(values.Select(value => value.Shape), Does.Contain("Face size").And.Contain("Pupil removal"));
            foreach (var value in values) Assert.That(value.Weight, Is.EqualTo(expected[value.Shape]).Within(.01), value.Shape);
            Assert.That(values.Single(value => value.Shape == "Pupil removal").Weight, Is.EqualTo(100).Within(.01));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(sourceJson));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.Zero);
        }

        private void FractionalPupil(bool defaults = false)
        {
            var idle = Layer("Empty gesture"); idle.defaultState = State(idle, "Neutral", null);
            var permanent = Layer("Fractional pupil");
            permanent.defaultState = State(permanent, "Always", Clip("Pupil", "Pupil removal", 100), defaults);
            var layers = controller.layers; layers[layers.Length - 1].defaultWeight = .5f; controller.layers = layers;
            SetDescriptorFx();
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void FractionalNeutralRetainsNativeBaseActivityAndLeavesAutomaticBlinkLive(bool automatic, bool defaults)
        {
            var baseClip = Clip("Native base", automatic ? "Blink" : "Face size", 35);
            if (automatic) AnimationUtility.SetEditorCurve(baseClip,
                EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink"), AnimationCurve.Linear(0, 0, 30, 100));
            var machine = controller.layers[0].stateMachine;
            machine.defaultState = State(machine, "Base", baseClip);
            FractionalPupil(defaults);
            var skin = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
            skin.SetBlendShapeWeight(mesh.GetBlendShapeIndex("Pupil removal"), 20);
            skin.SetBlendShapeWeight(mesh.GetBlendShapeIndex("Face size"), 12);
            var sourceJson = EditorJsonUtility.ToJson(controller);
            var expected = Native(false, requireRelay: false);
            var values = NeutralShapeSampler.Sample(avatar);
            Assert.That(values.Single(value => value.Shape == "Pupil removal").Weight,
                Is.EqualTo(expected["Pupil removal"]).Within(.01), "The unrelated native base must preserve the effective fractional override weight.");
            Assert.That(values.Select(value => value.Shape), Is.EquivalentTo(automatic ?
                new[] { "Pupil removal" } : new[] { "Face size", "Pupil removal" }));
            if (!automatic) Assert.That(values.Single(value => value.Shape == "Face size").Weight, Is.EqualTo(expected["Face size"]).Within(.01));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(sourceJson));
            Assert.That(skin.GetBlendShapeWeight(mesh.GetBlendShapeIndex("Pupil removal")), Is.EqualTo(20));
            Assert.That(skin.GetBlendShapeWeight(mesh.GetBlendShapeIndex("Face size")), Is.EqualTo(12));
            Assert.That(skin.GetBlendShapeWeight(mesh.GetBlendShapeIndex("Blink")), Is.Zero);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void FractionalNeutralDistinguishesDisconnectedAndReachableUnsupportedSupportMotions(bool animationEvent, bool reachable)
        {
            var baseClip = Clip("Automatic base", "Blink");
            AnimationUtility.SetEditorCurve(baseClip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink"),
                AnimationCurve.Linear(0, 0, 30, 100));
            var machine = controller.layers[0].stateMachine; machine.defaultState = State(machine, "Live blink", baseClip);
            var unsafeClip = Clip("Unobserved callback or object", "Blink");
            if (animationEvent) AnimationUtility.SetAnimationEvents(unsafeClip,
                new[] { new AnimationEvent { time = 30, functionName = "FutureCallback" } });
            else
            {
                var replacement = Object.Instantiate(mesh); replacement.name = "Unsupported replacement mesh";
                AssetDatabase.AddObjectToAsset(replacement, controller);
                AnimationUtility.SetObjectReferenceCurve(unsafeClip, EditorCurveBinding.PPtrCurve("Face", typeof(SkinnedMeshRenderer), "m_Mesh"),
                    new[] { new ObjectReferenceKeyframe { time = 0, value = replacement } });
            }
            var alternative = State(machine, "Alternative unsupported motion", unsafeClip);
            if (reachable)
            {
                var transition = machine.defaultState.AddTransition(alternative);
                transition.hasExitTime = true; transition.exitTime = 30; transition.duration = 0;
            }
            FractionalPupil();
            var skin = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
            var pupil = mesh.GetBlendShapeIndex("Pupil removal");
            skin.SetBlendShapeWeight(pupil, 17);
            var sourceJson = EditorJsonUtility.ToJson(controller);
            var meshJson = EditorJsonUtility.ToJson(mesh);
            var skinJson = EditorJsonUtility.ToJson(skin);
            var warnings = new List<string>();
            if (reachable && animationEvent)
            {
                // A reachable callback still requires a hard stop, even when
                // it lies beyond the native sampling window.
                Assert.Catch<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar, warnings: warnings));
            }
            else if (reachable)
            {
                // A fractional override depends on the lower layer's activity.
                // Retain the prepared rest when a future object swap prevents
                // proving that support, without applying the replacement mesh.
                var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
                Assert.That(values, Is.Empty);
                NeutralShapeSnapshot.Apply(avatar, values);
                Assert.That(warnings.Any(value => value.Contains("Pupil removal") && value.Contains("FX") && value.Contains("m_Mesh")), Is.True);
            }
            else
            {
                var expected = Native(false, requireRelay: false);
                var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
                Assert.That(values.Select(value => value.Shape), Is.EquivalentTo(new[] { "Pupil removal" }));
                Assert.That(values.Single().Weight, Is.EqualTo(expected["Pupil removal"]).Within(.01));
                Assert.That(Mathf.Abs(values.Single().Weight - 17), Is.GreaterThan(1), "Disconnected motions must not force prepared-rest fallback.");
            }
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(sourceJson));
            Assert.That(EditorJsonUtility.ToJson(mesh), Is.EqualTo(meshJson));
            Assert.That(EditorJsonUtility.ToJson(skin), Is.EqualTo(skinJson));
            Assert.That(skin.sharedMesh, Is.SameAs(mesh));
            Assert.That(skin.GetBlendShapeWeight(pupil), Is.EqualTo(17));
        }

        [Test]
        public void FractionalNeutralKeepsPreparedRestForDelayedAutomaticBaseActivityChanges()
        {
            var baseClip = Clip("Automatic base", "Blink");
            AnimationUtility.SetEditorCurve(baseClip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink"),
                AnimationCurve.Linear(0, 0, 30, 100));
            var machine = controller.layers[0].stateMachine;
            var initial = State(machine, "Live blink", baseClip); machine.defaultState = initial;
            var held = State(machine, "Future empty base", null);
            var transition = initial.AddTransition(held); transition.hasExitTime = true; transition.exitTime = 30; transition.duration = 0;
            FractionalPupil();
            var sourceJson = EditorJsonUtility.ToJson(controller);
            var skin = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
            var preparedPupil = skin.GetBlendShapeWeight(mesh.GetBlendShapeIndex("Pupil removal"));
            var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values, Is.Empty);
            NeutralShapeSnapshot.Apply(avatar, values);
            Assert.That(skin.GetBlendShapeWeight(mesh.GetBlendShapeIndex("Pupil removal")), Is.EqualTo(preparedPupil));
            Assert.That(warnings.Any(value => value.Contains("Pupil removal") && value.Contains("FX")), Is.True);
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(sourceJson));
        }
    }
}
