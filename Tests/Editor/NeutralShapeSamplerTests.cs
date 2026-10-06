using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UniVRM10;
using Unity.Collections;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class NeutralShapeSamplerTests
    {
        private string folder;
        private GameObject avatar;
        private Mesh mesh;
        private AnimatorController controller;
        private Component descriptor;
        private SkinnedMeshRenderer skin;

        [SetUp]
        public void SetUp()
        {
            var descriptorType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor")).FirstOrDefault(type => type != null);
            if (descriptorType == null) Assert.Ignore("Install the real VRChat SDK.");
            var name = "__NeutralShapeSampler_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            avatar = new GameObject("Avatar", typeof(Animator));
            var body = new GameObject("Body", typeof(SkinnedMeshRenderer)); body.transform.SetParent(avatar.transform, false);
            skin = body.GetComponent<SkinnedMeshRenderer>();
            mesh = new Mesh { name = "Closed base" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            foreach (var shape in new[] { "Open", "Blink", "vrc.v.aa", "Untouched" })
                mesh.AddBlendShapeFrame(shape, 100, Enumerable.Repeat(Vector3.up, 3).ToArray(), null, null);
            skin.sharedMesh = mesh;
            skin.SetBlendShapeWeight(3, 35);
            descriptor = avatar.AddComponent(descriptorType);
            SetFx(controller);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(avatar); Object.DestroyImmediate(mesh);
            if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        private void SetFx(RuntimeAnimatorController runtime)
        {
            using (var data = new SerializedObject(descriptor))
            {
                data.FindProperty("customizeAnimationLayers").boolValue = true;
                var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = 1;
                var layer = layers.GetArrayElementAtIndex(0);
                var type = layer.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
                layer.FindPropertyRelative("isDefault").boolValue = false;
                layer.FindPropertyRelative("animatorController").objectReferenceValue = runtime;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private void SetAction(RuntimeAnimatorController runtime)
        {
            using var data = new SerializedObject(descriptor);
            var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = 2;
            var layer = layers.GetArrayElementAtIndex(1);
            var type = layer.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "Action");
            layer.FindPropertyRelative("isDefault").boolValue = false;
            layer.FindPropertyRelative("animatorController").objectReferenceValue = runtime;
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        private AnimationClip Clip(string shape, AnimationCurve curve)
        {
            var clip = new AnimationClip { name = shape + " pose" };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape." + shape), curve);
            AssetDatabase.AddObjectToAsset(clip, controller); return clip;
        }

        private AnimatorState State(AnimatorStateMachine machine, AnimationClip clip = null, bool writeDefaults = false)
        {
            var state = machine.AddState("State " + machine.states.Length); state.motion = clip; state.writeDefaultValues = writeDefaults;
            return state;
        }

        private AnimatorStateMachine Layer(string name)
        {
            controller.AddLayer(name); var layers = controller.layers;
            layers[layers.Length - 1].defaultWeight = 1; controller.layers = layers;
            return layers[layers.Length - 1].stateMachine;
        }

        private AnimatorState Open(float weight = 100, bool writeDefaults = false)
        {
            var machine = controller.layers[0].stateMachine;
            return machine.defaultState = State(machine, Clip("Open", AnimationCurve.Constant(0, 1, weight)), writeDefaults);
        }

        [Test]
        public void ConstantStartupWritesIncludeZeroAndLeaveUnwrittenChannelsAlone()
        {
            var state = Open(); skin.SetBlendShapeWeight(1, 80);
            AnimationUtility.SetEditorCurve((AnimationClip)state.motion,
                EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Blink"), AnimationCurve.Constant(0, 1, 0));
            var values = NeutralShapeSampler.Sample(avatar);
            Assert.That(values.Select(value => value.Shape), Is.EquivalentTo(new[] { "Open", "Blink" }));
            Assert.That(values.Single(value => value.Shape == "Open").Weight, Is.EqualTo(100).Within(.01));
            Assert.That(values.Single(value => value.Shape == "Blink").Weight, Is.Zero.Within(.01));
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
            Assert.That(skin.GetBlendShapeWeight(1), Is.EqualTo(80));
            Assert.That(skin.GetBlendShapeWeight(3), Is.EqualTo(35));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NeutralAcceptsConstantActivationThatPreservesThePreparedObject(bool active)
        {
            var target = new GameObject("Prepared clothing"); target.transform.SetParent(avatar.transform, false); target.SetActive(active);
            var clip = (AnimationClip)Open(75).motion;
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(target.name, typeof(GameObject), "m_IsActive"),
                AnimationCurve.Constant(0, 1, active ? 1 : 0));
            var before = EditorJsonUtility.ToJson(clip);
            Assert.That(NeutralShapeSampler.Sample(avatar).Single(value => value.Shape == "Open").Weight, Is.EqualTo(75).Within(.01));
            Assert.That(target.activeSelf, Is.EqualTo(active));
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
            Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(before));
        }

        [Test]
        public void NeutralAcceptsAnAbsentConstantDummyActivationTarget()
        {
            var face = (AnimationClip)Open(75).motion;
            var clip = new AnimationClip { name = "Generated empty pose" };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("_ignored", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 0));
            AssetDatabase.AddObjectToAsset(clip, controller);
            var machine = controller.layers[0].stateMachine;
            machine.defaultState.motion = clip; machine.defaultState.writeDefaultValues = true;
            var faceLayer = Layer("Authored neutral face"); faceLayer.defaultState = State(faceLayer, face);
            Assert.That(avatar.transform.Find("_ignored"), Is.Null);
            Assert.That(NeutralShapeSampler.Sample(avatar).Single(value => value.Shape == "Open").Weight, Is.EqualTo(75).Within(.01));
            Assert.That(avatar.transform.Find("_ignored"), Is.Null);
        }

        [Test]
        public void MissingAlternateHairLayoutCannotBindOrClaimTheAccompanyingMorph()
        {
            var clip = (AnimationClip)Open(75).motion;
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Hair_Root/Hair_Ahoge/Ahoge.A", typeof(Transform), "localEulerAnglesRaw.x"),
                AnimationCurve.Constant(0, 1, 31));
            var before = EditorJsonUtility.ToJson(clip);
            Assert.That(NeutralShapeSampler.Sample(avatar).Single(value => value.Shape == "Open").Weight, Is.EqualTo(75).Within(.01));
            Assert.That(avatar.transform.Find("Hair_Root"), Is.Null);
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
            Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(before));
        }

        [TestCase("material._Color.r")]
        [TestCase("m_Enabled")]
        public void MissingTypedRendererComponentCannotClaimOrBlockARequiredMorph(string property)
        {
            var clip = (AnimationClip)Open(75).motion;
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(MeshRenderer), property), AnimationCurve.Constant(0, 1, 0));
            var required = new[] { EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Open") };
            var before = EditorJsonUtility.ToJson(clip);
            Assert.That(skin.GetComponent<MeshRenderer>(), Is.Null);
            Assert.That(NeutralShapeSampler.Sample(avatar, requiredMorphs: required).Single(value => value.Shape == "Open").Weight, Is.EqualTo(75).Within(.01));
            Assert.That(skin.enabled, Is.True);
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
            Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(before));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AppearanceCoupledStartupKeepsItsEntirePreparedConfiguration(bool affectsRendererAncestor)
        {
            var target = affectsRendererAncestor ? skin.gameObject : new GameObject("Prepared clothing");
            if (!affectsRendererAncestor) target.transform.SetParent(avatar.transform, false);
            var clip = (AnimationClip)Open(75).motion;
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(target.name, typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 0));
            var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values, Is.Empty);
            Assert.That(warnings.Single(), Does.Contain(clip.name).And.Contain(target.name).And.Contain("m_IsActive"));
            Assert.That(target.activeSelf, Is.True);
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
        }

        [Test]
        public void DelayedAppearanceConfigurationKeepsItsPreparedMorphAndActivationTogether()
        {
            var target = new GameObject("Prepared clothing"); target.transform.SetParent(avatar.transform, false);
            var clip = (AnimationClip)Open(75).motion;
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(target.name, typeof(GameObject), "m_IsActive"),
                new AnimationCurve(new Keyframe(0, 1), new Keyframe(10, 1), new Keyframe(11, 0)));
            var warnings = new List<string>();
            Assert.That(NeutralShapeSampler.Sample(avatar, warnings: warnings), Is.Empty);
            Assert.That(warnings.Single(), Does.Contain("m_IsActive"));
            Assert.That(target.activeSelf, Is.True);
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CurrentCapHairAndMaskStayTogetherWhileIndependentFaceAndPermanentMorphReconstruct(bool writeDefaults)
        {
            mesh.AddBlendShapeFrame("Cap mask", 100, Enumerable.Repeat(Vector3.right * .2f, 3).ToArray(), null, null);
            skin.SetBlendShapeWeight(4, 25);
            var cap = new GameObject("Cap"); cap.transform.SetParent(avatar.transform, false); cap.SetActive(false);
            var hair = new GameObject("Hair"); hair.transform.SetParent(avatar.transform, false);
            hair.transform.localEulerAngles = new Vector3(345, 9, 328);
            var sourceHair = hair.transform.localRotation;
            var startup = (AnimationClip)Open(75).motion;
            AnimationUtility.SetEditorCurve(startup, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Cap mask"),
                AnimationCurve.Constant(0, 1, 0));
            var clothing = Layer("Current scene wardrobe differs from authored defaults");
            var capOn = Clip("Cap mask", AnimationCurve.Constant(0, 1, 100)); capOn.name = "Authored Cap ON";
            AnimationUtility.SetEditorCurve(capOn, EditorCurveBinding.FloatCurve("Outfit_Mobile", typeof(SkinnedMeshRenderer), "blendShape.Cap_OFF"),
                AnimationCurve.Constant(0, 1, 100));
            AnimationUtility.SetEditorCurve(capOn, EditorCurveBinding.FloatCurve("Cap", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1));
            foreach (var axis in new[] { (Name: "x", Value: 31f), (Name: "y", Value: -13f), (Name: "z", Value: -144f) })
                AnimationUtility.SetEditorCurve(capOn, EditorCurveBinding.FloatCurve("Hair", typeof(Transform), "localEulerAnglesRaw." + axis.Name),
                    AnimationCurve.Constant(0, 1, axis.Value));
            clothing.defaultState = State(clothing, capOn, writeDefaults);
            // A generic FaceEmo common reset may mention the mask too. It does
            // not transfer ownership away from the prepared wardrobe group.
            var common = Layer("Common face above wardrobe"); common.defaultState = State(common, startup);
            var pupil = Layer("Permanent pupil setting"); pupil.defaultState = State(pupil, Clip("Untouched", AnimationCurve.Constant(0, 1, 80)));
            var sourceController = EditorJsonUtility.ToJson(controller);
            var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values.Select(value => value.Shape), Is.EquivalentTo(new[] { "Open", "Untouched" }));
            Assert.That(values.Single(value => value.Shape == "Open").Weight, Is.EqualTo(75).Within(.01));
            Assert.That(values.Single(value => value.Shape == "Untouched").Weight, Is.EqualTo(80).Within(.01));
            Assert.That(warnings.Any(warning => warning.Contains("Cap mask") && warning.Contains("Authored Cap ON")), Is.True);
            Assert.That(warnings.Any(warning => warning.Contains("Outfit_Mobile") && warning.Contains("Cap_OFF")), Is.True);
            var prepared = Object.Instantiate(avatar);
            try
            {
                NeutralShapeSnapshot.Apply(prepared, values);
                var preparedSkin = prepared.transform.Find("Body").GetComponent<SkinnedMeshRenderer>();
                Assert.That(preparedSkin.GetBlendShapeWeight(0), Is.EqualTo(75).Within(.01));
                Assert.That(preparedSkin.GetBlendShapeWeight(3), Is.EqualTo(80).Within(.01));
                Assert.That(preparedSkin.GetBlendShapeWeight(4), Is.EqualTo(25));
                Assert.That(prepared.transform.Find("Cap").gameObject.activeSelf, Is.False);
                Assert.That(Quaternion.Angle(prepared.transform.Find("Hair").localRotation, sourceHair), Is.LessThan(.001));
            }
            finally { Object.DestroyImmediate(prepared); }
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
            Assert.That(skin.GetBlendShapeWeight(3), Is.EqualTo(35));
            Assert.That(skin.GetBlendShapeWeight(4), Is.EqualTo(25));
            Assert.That(cap.activeSelf, Is.False);
            Assert.That(Quaternion.Angle(hair.transform.localRotation, sourceHair), Is.LessThan(.001));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(sourceController));
        }

        [Test]
        public void ExplicitEndpointRequirementKeepsItsCoupledPreparedNeutral()
        {
            var clothing = new GameObject("Clothing"); clothing.transform.SetParent(avatar.transform, false);
            var clip = (AnimationClip)Open(75).motion;
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Clothing", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 0));
            var required = new[] { EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Open") };
            skin.SetBlendShapeWeight(0, 17);
            var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, requiredMorphs: required, warnings: warnings);
            Assert.That(values, Is.Empty);
            Assert.That(warnings.Any(value => value.Contains("Open") && value.Contains("Clothing")), Is.True);
            NeutralShapeSnapshot.Apply(avatar, values); Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17));
            Assert.That(clothing.activeSelf, Is.True);
        }

        [TestCase(false, 0, false)]
        [TestCase(true, 0, false)]
        [TestCase(false, 1, false)]
        [TestCase(true, 1, false)]
        [TestCase(false, 2, true)]
        [TestCase(true, 2, true)]
        public void SharedCustomAppearanceControlKeepsSeparateVisibilityAndMaskTogetherWithoutClaimingGenericResetFace(bool requiredMask,
            int copyHops, bool convertRange)
        {
            mesh.AddBlendShapeFrame("Cap mask", 100, Enumerable.Repeat(Vector3.right * .2f, 3).ToArray(), null, null);
            skin.SetBlendShapeWeight(4, 25);
            controller.AddParameter("Cap selected", AnimatorControllerParameterType.Bool);
            var maskInput = "Cap selected";
            var copyOperations = new List<VrChatParameterDriver.Operation>();
            for (var hop = 1; hop <= copyHops; hop++)
            {
                var copied = "Cap alias " + hop;
                controller.AddParameter(copied, AnimatorControllerParameterType.Bool);
                var copy = ParameterDriverExpressionTests.Op("Copy", copied, source: maskInput);
                copy.ConvertRange = convertRange; copy.SourceMin = 0; copy.SourceMax = 1; copy.DestinationMin = 0; copy.DestinationMax = 2;
                copyOperations.Add(copy); maskInput = copied;
            }
            var cap = new GameObject("Cap"); cap.transform.SetParent(avatar.transform, false); cap.SetActive(false);
            var face = (AnimationClip)Open(75).motion;
            AnimationUtility.SetEditorCurve(face, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Cap mask"),
                AnimationCurve.Constant(0, 1, 0));
            var visibility = Layer("Cap visibility route");
            var off = new AnimationClip { name = "Cap OFF visibility" };
            var on = new AnimationClip { name = "Cap ON visibility" };
            AnimationUtility.SetEditorCurve(off, EditorCurveBinding.FloatCurve("Cap", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 0));
            AnimationUtility.SetEditorCurve(on, EditorCurveBinding.FloatCurve("Cap", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1));
            AssetDatabase.AddObjectToAsset(off, controller); AssetDatabase.AddObjectToAsset(on, controller);
            visibility.defaultState = State(visibility, off);
            var shown = State(visibility, on);
            var show = visibility.defaultState.AddTransition(shown); show.hasExitTime = false; show.duration = 0;
            show.AddCondition(AnimatorConditionMode.If, 0, "Cap selected");
            var mask = Layer("Separate cap mask route"); mask.defaultState = State(mask, Clip("Cap mask", AnimationCurve.Constant(0, 1, 0)));
            var covered = State(mask, Clip("Cap mask", AnimationCurve.Constant(0, 1, 100)));
            var cover = mask.defaultState.AddTransition(covered); cover.hasExitTime = false; cover.duration = 0;
            cover.AddCondition(AnimatorConditionMode.If, 0, maskInput);
            var common = Layer("Generic common reset"); common.defaultState = State(common, face);
            if (copyOperations.Count > 0)
            {
                var aliases = Layer("Copy appearance inputs"); aliases.defaultState = State(aliases, null);
                ParameterDriverExpressionTests.Driver(aliases.defaultState, copyOperations.ToArray());
            }
            var before = EditorJsonUtility.ToJson(controller);
            var warnings = new List<string>();
            var required = requiredMask ? new[] { EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Cap mask") } : null;
            {
                var values = NeutralShapeSampler.Sample(avatar, requiredMorphs: required, warnings: warnings);
                Assert.That(values.Select(value => value.Shape), Is.EqualTo(new[] { "Open" }));
                Assert.That(values.Single().Weight, Is.EqualTo(75).Within(.01));
                Assert.That(warnings.Single(), Does.Contain("Cap mask").And.Contain("Cap selected"));
                if (copyHops > 0) Assert.That(warnings.Single(), Does.Contain(maskInput).And.Contain("Parameter Driver Copy"));
                NeutralShapeSnapshot.Apply(avatar, values);
                Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(75).Within(.01));
            }
            Assert.That(skin.GetBlendShapeWeight(4), Is.EqualTo(25));
            Assert.That(cap.activeSelf, Is.False);
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
        }

        [TestCase("dormant")]
        [TestCase("reachable")]
        [TestCase("custom")]
        [TestCase("height")]
        [TestCase("curve writer")]
        [TestCase("driver writer")]
        [TestCase("other writer")]
        public void AppearanceOwnershipPrunesOnlyProvedNormalExternalBranches(string scenario)
        {
            var face = (AnimationClip)Open(75).motion;
            var input = scenario == "custom" ? "Wardrobe mode" : scenario == "height" ? "EyeHeightAsMeters" : "AFK";
            var type = scenario == "height" ? AnimatorControllerParameterType.Float : AnimatorControllerParameterType.Bool;
            controller.AddParameter(new AnimatorControllerParameter { name = input, type = type, defaultBool = true });
            var support = Layer("Normal or mixed appearance support");
            var idleClip = new AnimationClip { name = "Waiting" }; AssetDatabase.AddObjectToAsset(idleClip, controller);
            support.defaultState = State(support, idleClip, writeDefaults: true);
            var mixed = Clip("Open", AnimationCurve.Constant(0, 1, 10)); mixed.name = "Mixed alternate configuration";
            AnimationUtility.SetEditorCurve(mixed, EditorCurveBinding.FloatCurve("Body", typeof(Transform), "m_LocalPosition.x"),
                AnimationCurve.Constant(0, 1, 1));
            var alternate = State(support, mixed);
            var transition = support.defaultState.AddTransition(alternate); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(scenario == "height" ? AnimatorConditionMode.Greater : scenario == "reachable" ?
                AnimatorConditionMode.IfNot : AnimatorConditionMode.If, 0, input);
            var common = Layer("Face above native support"); common.defaultState = State(common, face);
            if (scenario == "curve writer")
            {
                var writer = Layer("Authored AFK curve");
                var clip = new AnimationClip { name = "AFK input writer" };
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "AFK"), AnimationCurve.Constant(0, 1, 1));
                AssetDatabase.AddObjectToAsset(clip, controller); writer.defaultState = State(writer, clip);
            }
            if (scenario == "driver writer")
                ParameterDriverExpressionTests.Driver(common.defaultState, ParameterDriverExpressionTests.Op("Set", "AFK", 1));
            if (scenario == "other writer")
            {
                var other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Action.controller");
                other.AddParameter("AFK", AnimatorControllerParameterType.Bool);
                var state = other.layers[0].stateMachine.AddState("Writes shared external input");
                other.layers[0].stateMachine.defaultState = state; state.writeDefaultValues = false;
                ParameterDriverExpressionTests.Driver(state, ParameterDriverExpressionTests.Op("Set", "AFK", 1));
                SetAction(other);
            }
            var required = new[] { EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Open") };
            var warnings = new List<string>();
            if (scenario == "dormant")
            {
                var values = NeutralShapeSampler.Sample(avatar, requiredMorphs: required, warnings: warnings);
                Assert.That(values.Single().Shape, Is.EqualTo("Open"));
                Assert.That(values.Single().Weight, Is.EqualTo(75).Within(.01));
                Assert.That(warnings, Is.Empty);
            }
            else if (scenario == "other writer")
            {
                var error = Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar, requiredMorphs: required, warnings: warnings));
                Assert.That(error.Message, Does.Contain("Action").And.Contain("AFK").And.Contain("Playable Layer"));
                Assert.That(warnings, Is.Empty);
            }
            else
            {
                Assert.That(NeutralShapeSampler.Sample(avatar, requiredMorphs: required, warnings: warnings), Is.Empty);
                Assert.That(warnings.Any(value => value.Contains("Open") && value.Contains("Mixed alternate configuration")), Is.True);
            }
            Assert.That(skin.transform.localPosition, Is.EqualTo(Vector3.zero));
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
        }

        [Test]
        public void DeclaredTrackingProfileRetainsCoupledPreparedNeutralWithWarning()
        {
            var clothing = new GameObject("Clothing"); clothing.transform.SetParent(avatar.transform, false);
            var clip = (AnimationClip)Open(75).motion;
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Clothing", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 0));
            var profile = ScriptableObject.CreateInstance<VrmTrackingProfile>();
            profile.expressions = VrmTrackingExpressions.Names.Select(name => new TrackingExpression
                { name = name, morphs = new[] { new TrackingMorph { shape = "Open", weight = 1 } } }).ToArray();
            avatar.AddComponent<VrmTrackingMarker>().profile = profile;
            try
            {
                var warnings = new List<string>();
                Assert.That(NeutralShapeSampler.Sample(avatar, warnings: warnings), Is.Empty);
                Assert.That(warnings.Any(value => value.Contains("Open") && value.Contains("Clothing")), Is.True);
                Assert.That(clothing.activeSelf, Is.True);
                Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
            }
            finally { Object.DestroyImmediate(profile); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AuthoredEndpointObligationUsesItsFirstBindingAndRetainsDisabledZeroCoverage(bool firstZero)
        {
            var clothing = new GameObject("Clothing"); clothing.transform.SetParent(avatar.transform, false);
            var clip = (AnimationClip)Open(75).motion;
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Clothing", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 0));
            var vrm = ScriptableObject.CreateInstance<VRM10Object>();
            var expression = ScriptableObject.CreateInstance<VRM10Expression>();
            expression.MorphTargetBindings = new[] { new MorphTargetBinding("Body", 0, firstZero ? 0 : .5f), new MorphTargetBinding("Body", 0, 1) };
            vrm.Expression.Happy = expression;
            avatar.AddComponent<Vrm10Instance>().Vrm = vrm;
            try
            {
                var warnings = new List<string>();
                Assert.That(NeutralShapeSampler.Sample(avatar, warnings: warnings), Is.Empty);
                Assert.That(warnings.Any(value => value.Contains("Open") && value.Contains("Clothing")), Is.True);
                Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
                Assert.That(clothing.activeSelf, Is.True);
                Assert.That(vrm.Expression.Happy, Is.SameAs(expression));
                Assert.That(expression.MorphTargetBindings[0].Weight, Is.EqualTo(firstZero ? 0 : .5f));
            }
            finally { Object.DestroyImmediate(expression); Object.DestroyImmediate(vrm); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AppearanceSupportThatDisablesTheRendererKeepsPreparedNeutralAndActivation(bool ancestor)
        {
            Open(75); skin.SetBlendShapeWeight(0, 17);
            var path = ancestor ? "" : "Body";
            var support = Layer("Appearance support cannot disable the face");
            var clip = new AnimationClip { name = "Unsafe activation support" };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(GameObject), "m_IsActive"),
                AnimationCurve.Constant(0, 1, 0));
            AssetDatabase.AddObjectToAsset(clip, controller);
            support.defaultState = State(support, clip, writeDefaults: true);
            var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values, Is.Empty);
            Assert.That(warnings.Any(value => value.Contains("Unsafe activation support") && value.Contains("Body")), Is.True);
            NeutralShapeSnapshot.Apply(avatar, values);
            Assert.That(avatar.activeSelf, Is.True);
            Assert.That(skin.gameObject.activeSelf, Is.True);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17));
        }

        [TestCase("bone")]
        [TestCase("renderer")]
        [TestCase("ancestor")]
        public void IndependentTransformSupportCapturesNativeMorphScalarsAndKeepsPreparedPose(string targetKind)
        {
            var face = (AnimationClip)Open(75).motion;
            var bone = new GameObject("Face bone").transform; bone.SetParent(avatar.transform, false);
            skin.bones = new[] { bone }; skin.rootBone = bone;
            mesh.bindposes = new[] { Matrix4x4.identity };
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, mesh.vertexCount).ToArray();
            var target = targetKind == "bone" ? bone : targetKind == "renderer" ? skin.transform : avatar.transform;
            var preparedPosition = new Vector3(.2f, .3f, .4f); target.localPosition = preparedPosition;
            var support = Layer("Independent transform support");
            var clip = new AnimationClip { name = "Probe-only transform" };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(AnimationUtility.CalculateTransformPath(target, avatar.transform),
                typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Constant(0, 1, 1));
            AssetDatabase.AddObjectToAsset(clip, controller);
            support.defaultState = State(support, clip, writeDefaults: true);
            var common = Layer("Face above native support"); common.defaultState = State(common, face);
            var before = EditorJsonUtility.ToJson(controller);
            var expected = NativeOpeningWeight();
            var values = NeutralShapeSampler.Sample(avatar);
            Assert.That(expected, Is.EqualTo(75).Within(.01));
            Assert.That(values.Single(value => value.Shape == "Open").Weight, Is.EqualTo(expected).Within(.01));
            Assert.That(target.localPosition, Is.EqualTo(preparedPosition));
            Assert.That(skin.sharedMesh, Is.SameAs(mesh));
            Assert.That(skin.enabled, Is.True);
            Assert.That(skin.gameObject.activeSelf, Is.True);
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
            NeutralShapeSnapshot.Apply(avatar, values);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(expected).Within(.01));
            Assert.That(target.localPosition, Is.EqualTo(preparedPosition));
        }

        [Test]
        public void IndependentTransformOnSixthSkinInfluenceKeepsNativeScalarAndPreparedBone()
        {
            var face = (AnimationClip)Open(75).motion;
            var bones = Enumerable.Range(0, 6).Select(index =>
            {
                var bone = new GameObject("Bone " + index).transform; bone.SetParent(avatar.transform, false); return bone;
            }).ToArray();
            skin.bones = bones; skin.rootBone = bones[0];
            mesh.bindposes = Enumerable.Repeat(Matrix4x4.identity, bones.Length).ToArray();
            using (var counts = new NativeArray<byte>(Enumerable.Repeat((byte)6, mesh.vertexCount).ToArray(), Allocator.Temp))
            using (var weights = new NativeArray<BoneWeight1>(Enumerable.Range(0, mesh.vertexCount)
                .SelectMany(_ => Enumerable.Range(0, 6).Select(index => new BoneWeight1 { boneIndex = index, weight = (6 - index) / 21f })).ToArray(), Allocator.Temp))
                mesh.SetBoneWeights(counts, weights);
            var support = Layer("Sixth influence support");
            var clip = new AnimationClip { name = "Move sixth face influence" };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Bone 5", typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Constant(0, 1, 1));
            AssetDatabase.AddObjectToAsset(clip, controller);
            support.defaultState = State(support, clip, writeDefaults: true);
            var common = Layer("Face above native support"); common.defaultState = State(common, face);
            var expected = NativeOpeningWeight();
            var values = NeutralShapeSampler.Sample(avatar);
            Assert.That(expected, Is.EqualTo(75).Within(.01));
            Assert.That(values.Single(value => value.Shape == "Open").Weight, Is.EqualTo(expected).Within(.01));
            Assert.That(bones[5].localPosition, Is.EqualTo(Vector3.zero));
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
            NeutralShapeSnapshot.Apply(avatar, values);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(expected).Within(.01));
            Assert.That(bones[5].localPosition, Is.EqualTo(Vector3.zero));
        }

        [TestCase("positions", false)]
        [TestCase("normals", false)]
        [TestCase("tangents", false)]
        [TestCase("positions", true)]
        [TestCase("normals", true)]
        [TestCase("tangents", true)]
        public void IndependentTransformSupportKeepsNativeScalarAcrossOverlappingMorphFrames(string kind, bool overlaps)
        {
            mesh.ClearBlendShapes();
            mesh.normals = Enumerable.Repeat(Vector3.forward, mesh.vertexCount).ToArray();
            mesh.tangents = Enumerable.Repeat(new Vector4(1, 0, 0, 1), mesh.vertexCount).ToArray();
            var zero = new Vector3[mesh.vertexCount];
            var positions = new Vector3[mesh.vertexCount];
            var normals = new Vector3[mesh.vertexCount];
            var tangents = new Vector3[mesh.vertexCount];
            var changed = kind == "positions" ? positions : kind == "normals" ? normals : tangents;
            // Physical overlap in a later position or shading frame is not
            // scalar coupling: only the native morph weight leaves the probe.
            changed[overlaps ? 1 : 0] = Vector3.up * .1f;
            mesh.AddBlendShapeFrame("Open", 50, zero, zero, zero);
            mesh.AddBlendShapeFrame("Open", 100, positions, normals, tangents);
            var faceBone = new GameObject("Face bone").transform; faceBone.SetParent(avatar.transform, false);
            var hairBone = new GameObject("Hair bone").transform; hairBone.SetParent(avatar.transform, false);
            skin.bones = new[] { faceBone, hairBone }; skin.rootBone = faceBone;
            mesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity };
            mesh.boneWeights = new[] { new BoneWeight { boneIndex0 = 0, weight0 = 1 },
                new BoneWeight { boneIndex0 = 1, weight0 = 1 }, new BoneWeight { boneIndex0 = 0, weight0 = 1 } };
            var face = (AnimationClip)Open(75).motion;
            var support = Layer("Integrated hair appearance support");
            var clip = new AnimationClip { name = "Hair configuration" };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Hair bone", typeof(Transform), "m_LocalPosition.x"),
                AnimationCurve.Constant(0, 1, 1));
            AssetDatabase.AddObjectToAsset(clip, controller);
            support.defaultState = State(support, clip, writeDefaults: true);
            var common = Layer("Face above native support"); common.defaultState = State(common, face);
            var before = EditorJsonUtility.ToJson(mesh);
            var expected = NativeOpeningWeight();
            var values = NeutralShapeSampler.Sample(avatar);
            Assert.That(expected, Is.EqualTo(75).Within(.01));
            Assert.That(values.Single(value => value.Shape == "Open").Weight, Is.EqualTo(expected).Within(.01));
            Assert.That(hairBone.localPosition, Is.EqualTo(Vector3.zero));
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
            Assert.That(EditorJsonUtility.ToJson(mesh), Is.EqualTo(before));
            NeutralShapeSnapshot.Apply(avatar, values);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(expected).Within(.01));
            Assert.That(hairBone.localPosition, Is.EqualTo(Vector3.zero));
            Assert.That(skin.sharedMesh, Is.SameAs(mesh));
        }

        [TestCase("path")]
        [TestCase("component")]
        [TestCase("mesh")]
        [TestCase("shape")]
        [TestCase("inactive")]
        [TestCase("parent")]
        [TestCase("disabled")]
        public void OptionalUnboundOrUnexportedMorphCannotPoisonIndependentFaceReconstruction(string missing)
        {
            var face = (AnimationClip)Open(75).motion;
            var path = "Optional wardrobe";
            var shape = "Untouched";
            GameObject wardrobe = null;
            SkinnedMeshRenderer wardrobeRenderer = null;
            if (missing == "shape") { path = "Body"; shape = "Absent shape"; }
            else if (missing != "path")
            {
                wardrobe = new GameObject(path); wardrobe.transform.SetParent(avatar.transform, false);
                if (missing != "component")
                {
                    wardrobeRenderer = wardrobe.AddComponent<SkinnedMeshRenderer>();
                    if (missing != "mesh")
                    { wardrobeRenderer.sharedMesh = mesh; wardrobeRenderer.SetBlendShapeWeight(3, 37); }
                }
                if (missing == "inactive") wardrobe.SetActive(false);
                if (missing == "disabled") wardrobeRenderer.enabled = false;
                if (missing == "parent")
                {
                    var parent = new GameObject("Hidden wardrobe"); parent.transform.SetParent(avatar.transform, false);
                    wardrobe.transform.SetParent(parent.transform, false); parent.SetActive(false);
                    path = "Hidden wardrobe/Optional wardrobe";
                }
            }
            AnimationUtility.SetEditorCurve(face, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + shape),
                AnimationCurve.Constant(0, 1, 100));
            var target = new GameObject("Visual support"); target.transform.SetParent(avatar.transform, false);
            var support = Layer("Native visual support");
            var clip = new AnimationClip { name = "Independent visual configuration" };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(target.name, typeof(Transform), "m_LocalPosition.x"),
                AnimationCurve.Constant(0, 1, 1));
            AssetDatabase.AddObjectToAsset(clip, controller);
            support.defaultState = State(support, clip, writeDefaults: true);
            var common = Layer("Face above native support"); common.defaultState = State(common, face);
            var before = EditorJsonUtility.ToJson(face);
            var warnings = new List<string>();
            var expected = NativeOpeningWeight();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values.Select(value => value.Shape), Is.EqualTo(new[] { "Open" }));
            Assert.That(values.Single().Weight, Is.EqualTo(expected).Within(.01));
            Assert.That(expected, Is.EqualTo(75).Within(.01));
            Assert.That(warnings.Single(), Does.Contain(path).And.Contain(shape));
            Assert.That(target.transform.localPosition, Is.EqualTo(Vector3.zero));
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
            if (wardrobeRenderer != null && wardrobeRenderer.sharedMesh != null)
                Assert.That(wardrobeRenderer.GetBlendShapeWeight(3), Is.EqualTo(37));
            Assert.That(EditorJsonUtility.ToJson(face), Is.EqualTo(before));
            NeutralShapeSnapshot.Apply(avatar, values);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(75).Within(.01));
            if (wardrobe != null && missing == "inactive") Assert.That(wardrobe.activeSelf, Is.False);
            if (wardrobeRenderer != null && missing == "disabled") Assert.That(wardrobeRenderer.enabled, Is.False);
        }

        [Test]
        public void AmbiguousOptionalMorphTargetIsRejectedBeforeIgnoringItsMissingShape()
        {
            var face = (AnimationClip)Open(75).motion;
            foreach (var _ in new[] { 0, 1 })
            {
                var wardrobe = new GameObject("Optional wardrobe"); wardrobe.transform.SetParent(avatar.transform, false);
            }
            AnimationUtility.SetEditorCurve(face, EditorCurveBinding.FloatCurve("Optional wardrobe", typeof(SkinnedMeshRenderer), "blendShape.Absent"),
                AnimationCurve.Constant(0, 1, 100));
            Assert.That(Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar)).Message,
                Does.Contain("一意").And.Contain("Optional wardrobe").And.Contain("Absent"));
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DisjointAdditiveVisibilityOrMaterialSupportKeepsNativeMorphMathAndPreparedAppearance(bool materialCurve)
        {
            Open(75);
            var clothing = new GameObject("Prepared clothing", typeof(MeshRenderer)); clothing.transform.SetParent(avatar.transform, false);
            var renderer = clothing.GetComponent<MeshRenderer>();
            var material = new Material(Shader.Find("Unlit/Color")); renderer.sharedMaterial = material;
            var beforeMaterial = EditorJsonUtility.ToJson(material);
            var support = Layer("Additive visual support");
            var clip = new AnimationClip { name = materialCurve ? "Material support" : "Visibility support" };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(clothing.name, typeof(MeshRenderer),
                materialCurve ? "material._Color.r" : "m_Enabled"), AnimationCurve.Constant(0, 1, 0));
            AssetDatabase.AddObjectToAsset(clip, controller); support.defaultState = State(support, clip);
            var layers = controller.layers; layers[1].defaultWeight = .5f; layers[1].blendingMode = AnimatorLayerBlendingMode.Additive; controller.layers = layers;
            var beforeController = EditorJsonUtility.ToJson(controller);
            try
            {
                var expected = NativeOpeningWeight();
                var values = NeutralShapeSampler.Sample(avatar);
                Assert.That(values.Select(value => value.Shape), Is.EqualTo(new[] { "Open" }));
                Assert.That(values.Single().Weight, Is.EqualTo(expected).Within(.01));
                Assert.That(renderer.enabled, Is.True);
                Assert.That(renderer.sharedMaterial, Is.SameAs(material));
                Assert.That(EditorJsonUtility.ToJson(material), Is.EqualTo(beforeMaterial));
                Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(beforeController));
                Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
            }
            finally { Object.DestroyImmediate(material); }
        }

        private float NativeOpeningWeight()
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            GameObject copy = null;
            var graph = default(PlayableGraph);
            try
            {
                copy = Object.Instantiate(avatar); copy.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(copy, scene);
                foreach (var component in copy.GetComponentsInChildren<Behaviour>(true)) component.enabled = false;
                var animator = copy.GetComponent<Animator>(); animator.runtimeAnimatorController = null; animator.enabled = true;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.applyRootMotion = false; animator.fireEvents = false;
                graph = PlayableGraph.Create("Original additive visual support oracle"); graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                AnimationPlayableOutput.Create(graph, "Original full FX", animator).SetSourcePlayable(playable);
                graph.Play(); graph.Evaluate(0f);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                return copy.transform.Find("Body").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(0);
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                if (copy != null) Object.DestroyImmediate(copy);
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [Test]
        public void UnknownComponentPropertyPreservesPreparedNeutralAndReportsTarget()
        {
            Open(75); skin.SetBlendShapeWeight(0, 17);
            var support = Layer("Unknown support");
            var clip = new AnimationClip { name = "Unknown collider change" };
            var collider = skin.gameObject.AddComponent<BoxCollider>();
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(BoxCollider), "m_Size.x"), AnimationCurve.Constant(0, 1, 4));
            AssetDatabase.AddObjectToAsset(clip, controller);
            support.defaultState = State(support, clip, writeDefaults: true);
            var before = EditorJsonUtility.ToJson(controller); var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values, Is.Empty);
            Assert.That(warnings.Any(value => value.Contains("Body") && value.Contains("Open") &&
                value.Contains("Unknown collider change") && value.Contains("m_Size.x")), Is.True);
            NeutralShapeSnapshot.Apply(avatar, values);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17));
            Assert.That(collider.size.x, Is.EqualTo(1));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
        }

        [TestCase(false, 0f)] [TestCase(false, 1f)]
        [TestCase(true, 0f)] [TestCase(true, 1f)]
        public void KnownConstraintSupportKeepsNativeWriteDefaultsAndIndependentMorphRest(bool vrcConstraint, float value)
        {
            var face = (AnimationClip)Open(75).motion;
            var target = new GameObject("IceMilkSummerSet_ussk"); target.transform.SetParent(avatar.transform, false);
            var child = new GameObject("IMS_UmbrellaOpen_ussk"); child.transform.SetParent(target.transform, false);
            var type = vrcConstraint ? VrcParentConstraintType() : typeof(ParentConstraint);
            var constraint = child.AddComponent(type);
            var property = vrcConstraint ? "FreezeToWorld" : "m_Weight";
            var path = AnimationUtility.CalculateTransformPath(child.transform, avatar.transform);
            var clip = new AnimationClip { name = "U_WorldFix_off" };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, type, property), AnimationCurve.Constant(0, 1, value));
            AssetDatabase.AddObjectToAsset(clip, controller);
            var support = Layer("Native constraint support"); support.defaultState = State(support, clip, writeDefaults: true);
            var upper = Layer("Face above native constraint support"); upper.defaultState = State(upper, face);
            var layers = controller.layers; layers[2].defaultWeight = .5f; controller.layers = layers;
            var beforeController = EditorJsonUtility.ToJson(controller); var beforeClip = EditorJsonUtility.ToJson(clip);
            using var data = new SerializedObject(constraint);
            var beforeValue = data.FindProperty(property).propertyType == SerializedPropertyType.Boolean
                ? data.FindProperty(property).boolValue ? 1f : 0f : data.FindProperty(property).floatValue;
            var expected = NativeOpeningWeight();
            var values = NeutralShapeSampler.Sample(avatar);
            Assert.That(values.Select(item => item.Shape), Is.EqualTo(new[] { "Open" }));
            Assert.That(values.Single().Weight, Is.EqualTo(expected).Within(.01));
            Assert.That(expected, Is.GreaterThan(0));
            data.Update();
            var afterValue = data.FindProperty(property).propertyType == SerializedPropertyType.Boolean
                ? data.FindProperty(property).boolValue ? 1f : 0f : data.FindProperty(property).floatValue;
            Assert.That(afterValue, Is.EqualTo(beforeValue));
            Assert.That(child.transform.localPosition, Is.EqualTo(Vector3.zero));
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(beforeController));
            Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(beforeClip));
        }

        [TestCase(false)] [TestCase(true)]
        public void FractionalOrMaskedTopOverrideCannotProveIndependenceFromUnmeasuredLowerInputs(bool masked)
        {
            Open(75); skin.SetBlendShapeWeight(0, 17);
            controller.AddParameter("EyeHeightAsMeters", AnimatorControllerParameterType.Float);
            var body = Layer("Unmeasured body rest"); body.defaultState = State(body, Clip("Untouched", AnimationCurve.Constant(0, 1, 63)));
            var alternate = State(body, Clip("Untouched", AnimationCurve.Constant(0, 1, 91)));
            var change = body.defaultState.AddTransition(alternate); change.hasExitTime = false; change.duration = 0;
            change.AddCondition(AnimatorConditionMode.Greater, .5f, "EyeHeightAsMeters");
            var type = VrcParentConstraintType();
            var target = new GameObject("World fix support"); target.transform.SetParent(avatar.transform, false); target.AddComponent(type);
            var clip = new AnimationClip { name = "World fix with write defaults" };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(target.name, type, "FreezeToWorld"), AnimationCurve.Constant(0, 1, 1));
            AssetDatabase.AddObjectToAsset(clip, controller);
            var support = Layer("Native WD closure"); support.defaultState = State(support, clip, writeDefaults: true);
            var top = Layer("Incomplete highest override"); top.defaultState = State(top, Clip("Open", AnimationCurve.Constant(0, 1, 100)));
            var layers = controller.layers; layers[3].defaultWeight = masked ? 1 : .5f;
            if (masked)
            {
                var mask = new AvatarMask { name = "Explicit face mask", transformCount = 1 };
                mask.SetTransformPath(0, "Body"); mask.SetTransformActive(0, true);
                AssetDatabase.AddObjectToAsset(mask, controller); layers[3].avatarMask = mask;
            }
            controller.layers = layers;
            var before = EditorJsonUtility.ToJson(controller); var beforeMesh = EditorJsonUtility.ToJson(mesh);
            var warnings = new List<string>(); var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values.Any(value => value.Shape == "Open"), Is.False);
            Assert.That(warnings.Any(value => value.Contains("Open") && value.Contains("EyeHeightAsMeters")), Is.True);
            NeutralShapeSnapshot.Apply(avatar, values);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17)); Assert.That(skin.GetBlendShapeWeight(3), Is.EqualTo(35));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
            Assert.That(EditorJsonUtility.ToJson(mesh), Is.EqualTo(beforeMesh));
        }

        [TestCase("activation")] [TestCase("mesh")] [TestCase("unknown")]
        public void FutureNonMorphChangeBelowTopOverrideRetainsPreparedNeutral(string kind)
        {
            var initial = Open(75); skin.SetBlendShapeWeight(0, 17);
            var future = new AnimationClip { name = "Future non-morph " + kind };
            string property;
            if (kind == "mesh")
            {
                property = "m_Mesh";
                var replacement = Object.Instantiate(mesh); replacement.name = "Future replacement mesh";
                AssetDatabase.AddObjectToAsset(replacement, controller);
                AnimationUtility.SetObjectReferenceCurve(future, EditorCurveBinding.PPtrCurve("Body", typeof(SkinnedMeshRenderer), property),
                    new[] { new ObjectReferenceKeyframe { time = 0, value = replacement } });
            }
            else
            {
                property = kind == "activation" ? "m_IsActive" : "m_Size.x";
                if (kind == "unknown") skin.gameObject.AddComponent<BoxCollider>().size = Vector3.one;
                AnimationUtility.SetEditorCurve(future, EditorCurveBinding.FloatCurve("Body", kind == "activation" ? typeof(GameObject) : typeof(BoxCollider), property),
                    AnimationCurve.Constant(0, 1, kind == "activation" ? 0 : 4));
            }
            AssetDatabase.AddObjectToAsset(future, controller);
            var changed = State(controller.layers[0].stateMachine, future);
            var transition = initial.AddTransition(changed); transition.hasExitTime = true; transition.exitTime = 10; transition.duration = 0;
            var top = Layer("Highest constant face"); top.defaultState = State(top, Clip("Open", AnimationCurve.Constant(0, 1, 100)));
            var before = EditorJsonUtility.ToJson(controller); var beforeMesh = EditorJsonUtility.ToJson(mesh);
            var beforeClip = EditorJsonUtility.ToJson(future); var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values.Any(value => value.Shape == "Open"), Is.False,
                "The top scalar override cannot prove that a future visibility or mesh/property change is harmless.");
            Assert.That(warnings.Any(value => value.Contains("Open") && value.Contains(property)), Is.True,
                string.Join("\n", warnings));
            NeutralShapeSnapshot.Apply(avatar, values);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17));
            Assert.That(skin.sharedMesh, Is.SameAs(mesh)); Assert.That(skin.gameObject.activeSelf, Is.True);
            if (kind == "unknown") Assert.That(skin.GetComponent<BoxCollider>().size, Is.EqualTo(Vector3.one));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
            Assert.That(EditorJsonUtility.ToJson(mesh), Is.EqualTo(beforeMesh));
            Assert.That(EditorJsonUtility.ToJson(future), Is.EqualTo(beforeClip));
        }

        [Test]
        public void FutureInvalidParameterDriverBelowTopOverrideStillFailsDataChecks()
        {
            var initial = Open(75); skin.SetBlendShapeWeight(0, 17);
            controller.AddParameter("Face", AnimatorControllerParameterType.Float);
            var machine = controller.layers[0].stateMachine;
            var late = State(machine, Clip("Open", AnimationCurve.Constant(0, 1, 75)));
            var transition = initial.AddTransition(late); transition.hasExitTime = true; transition.exitTime = 10; transition.duration = 0;
            var driver = ParameterDriverExpressionTests.Driver(late, ParameterDriverExpressionTests.Op("Set", "Face", float.NaN));
            var selected = State(machine, Clip("Open", AnimationCurve.Constant(0, 1, 90)));
            var read = late.AddTransition(selected); read.hasExitTime = false; read.duration = 0;
            read.AddCondition(AnimatorConditionMode.Greater, .5f, "Face");
            var top = Layer("Highest constant face"); top.defaultState = State(top, Clip("Open", AnimationCurve.Constant(0, 1, 100)));
            using (var data = new SerializedObject(driver))
                Assert.That(float.IsNaN(data.FindProperty("parameters").GetArrayElementAtIndex(0).FindPropertyRelative("value").floatValue), Is.True);
            var before = EditorJsonUtility.ToJson(controller); var beforeDriver = EditorJsonUtility.ToJson(driver);
            var beforeMesh = EditorJsonUtility.ToJson(mesh); var warnings = new List<string>();
            var error = Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar, warnings: warnings));
            Assert.That(error.Message, Does.Contain("Parameter Driver").And.Contain("Face").And.Contain("value"));
            Assert.That(warnings, Is.Empty);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17)); Assert.That(skin.sharedMesh, Is.SameAs(mesh));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
            Assert.That(EditorJsonUtility.ToJson(driver), Is.EqualTo(beforeDriver));
            Assert.That(EditorJsonUtility.ToJson(mesh), Is.EqualTo(beforeMesh));
        }

        [TestCase(false)] [TestCase(true)]
        public void ConstraintCoupledMorphKeepsPreparedWeightWhileIndependentFaceStillSamples(bool sharedInput)
        {
            Open(100); skin.SetBlendShapeWeight(3, 17);
            var type = VrcParentConstraintType();
            var target = new GameObject("World fixed accessory"); target.transform.SetParent(avatar.transform, false);
            var constraint = target.AddComponent(type);
            var binding = EditorCurveBinding.FloatCurve(target.name, type, "FreezeToWorld");
            var clip = new AnimationClip { name = "Constraint and body option" };
            AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, 1, 1));
            AssetDatabase.AddObjectToAsset(clip, controller);
            var support = Layer("World fixation"); support.defaultState = State(support, clip);
            if (sharedInput)
            {
                controller.AddParameter("Appearance option", AnimatorControllerParameterType.Int);
                var otherFix = new AnimationClip { name = "Alternate world fixation" };
                AnimationUtility.SetEditorCurve(otherFix, binding, AnimationCurve.Constant(0, 1, 0));
                AssetDatabase.AddObjectToAsset(otherFix, controller);
                var alternate = State(support, otherFix);
                var transition = support.defaultState.AddTransition(alternate); transition.hasExitTime = false; transition.duration = 0;
                transition.AddCondition(AnimatorConditionMode.Equals, 1, "Appearance option");
                var body = Layer("Coupled body option"); body.defaultState = State(body, Clip("Untouched", AnimationCurve.Constant(0, 1, 63)));
                var other = State(body, Clip("Untouched", AnimationCurve.Constant(0, 1, 91)));
                var changed = body.defaultState.AddTransition(other); changed.hasExitTime = false; changed.duration = 0;
                changed.AddCondition(AnimatorConditionMode.Equals, 1, "Appearance option");
            }
            else AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Untouched"),
                AnimationCurve.Constant(0, 1, 63));
            var before = EditorJsonUtility.ToJson(controller); var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values.Select(value => value.Shape), Is.EqualTo(new[] { "Open" }));
            Assert.That(values.Single().Weight, Is.EqualTo(100).Within(.01));
            Assert.That(warnings.Any(value => value.Contains("Untouched") && value.Contains("FreezeToWorld")), Is.True);
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero); Assert.That(skin.GetBlendShapeWeight(3), Is.EqualTo(17));
            using (var data = new SerializedObject(constraint)) Assert.That(data.FindProperty("FreezeToWorld").boolValue, Is.False);
            NeutralShapeSnapshot.Apply(avatar, values);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(100).Within(.01)); Assert.That(skin.GetBlendShapeWeight(3), Is.EqualTo(17));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
        }

        static Type VrcParentConstraintType()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly =>
                assembly.GetType("VRC.SDK3.Dynamics.Constraint.Components.VRCParentConstraint")).FirstOrDefault(value => value != null);
            if (type == null) Assert.Ignore("Install the real VRChat SDK with VRCParentConstraint.");
            return type;
        }

        [TestCase(0f)] [TestCase(1f)]
        public void ConstraintEnabledSupportKeepsPreparedRestWithoutExecutingConstraint(float enabled)
        {
            Open(75); skin.SetBlendShapeWeight(0, 17);
            var type = VrcParentConstraintType();
            var target = new GameObject("Disabled world constraint"); target.transform.SetParent(avatar.transform, false);
            var constraint = (Behaviour)target.AddComponent(type); constraint.enabled = false;
            var clip = new AnimationClip { name = "Constraint execution switch" };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(target.name, type, "m_Enabled"), AnimationCurve.Constant(0, 1, enabled));
            AssetDatabase.AddObjectToAsset(clip, controller);
            var support = Layer("Constraint execution support"); support.defaultState = State(support, clip, writeDefaults: true);
            var before = EditorJsonUtility.ToJson(controller); var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values, Is.Empty);
            Assert.That(warnings.Any(value => value.Contains("Open") && value.Contains("m_Enabled")), Is.True);
            NeutralShapeSnapshot.Apply(avatar, values);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17)); Assert.That(constraint.enabled, Is.False);
            Assert.That(target.transform.localPosition, Is.EqualTo(Vector3.zero));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
        }

        [Test]
        public void ConstraintCoupledExplicitEndpointKeepsPreparedNeutralForLaterEndpointBaking()
        {
            Open(100); skin.SetBlendShapeWeight(3, 17);
            var type = VrcParentConstraintType();
            var target = new GameObject("Required world fix"); target.transform.SetParent(avatar.transform, false); target.AddComponent(type);
            var clip = Clip("Untouched", AnimationCurve.Constant(0, 1, 63));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(target.name, type, "FreezeToWorld"), AnimationCurve.Constant(0, 1, 1));
            var support = Layer("Required coupled option"); support.defaultState = State(support, clip);
            var before = EditorJsonUtility.ToJson(controller); var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings,
                requiredMorphs: new[] { EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Untouched") });
            Assert.That(values.Select(value => value.Shape), Is.EqualTo(new[] { "Open" }));
            Assert.That(values.Single().Weight, Is.EqualTo(100).Within(.01));
            Assert.That(warnings.Any(value => value.Contains("Untouched") && value.Contains("FreezeToWorld")), Is.True);
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero); Assert.That(skin.GetBlendShapeWeight(3), Is.EqualTo(17));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
        }

        [TestCase(false)] [TestCase(true)]
        public void UnknownPropertyFallbackCannotHideLaterActiveNaNOrAnimationEvent(bool animationEvent)
        {
            Open(75); skin.SetBlendShapeWeight(0, 17);
            skin.gameObject.AddComponent<BoxCollider>();
            var unknown = new AnimationClip { name = "Unknown collider fallback" };
            AnimationUtility.SetEditorCurve(unknown, EditorCurveBinding.FloatCurve("Body", typeof(BoxCollider), "m_Size.x"), AnimationCurve.Constant(0, 1, 4));
            AssetDatabase.AddObjectToAsset(unknown, controller);
            var first = Layer("First unknown support"); first.defaultState = State(first, unknown, writeDefaults: true);
            var malformed = new AnimationClip { name = "Later invalid clip" };
            if (animationEvent) AnimationUtility.SetAnimationEvents(malformed, new[] { new AnimationEvent { time = .3f, functionName = "ShouldNeverRun" } });
            else
            {
                controller.AddParameter("Invalid later curve", AnimatorControllerParameterType.Float);
                AnimationUtility.SetEditorCurve(malformed, EditorCurveBinding.FloatCurve("", typeof(Animator), "Invalid later curve"),
                    new AnimationCurve(new Keyframe(0, 0, 0, float.NaN), new Keyframe(1, 1, 1, 0)));
            }
            AssetDatabase.AddObjectToAsset(malformed, controller);
            var later = Layer("Later malformed support"); later.defaultState = State(later, malformed, writeDefaults: true);
            var before = EditorJsonUtility.ToJson(controller); var beforeClip = EditorJsonUtility.ToJson(malformed);
            var warnings = new List<string>();
            var error = Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar, warnings: warnings));
            Assert.That(error.Message, Does.Not.Contain("Unknown collider fallback"), "The independent invalid clip must be checked before the recoverable property.");
            Assert.That(warnings, Is.Empty);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
            Assert.That(EditorJsonUtility.ToJson(malformed), Is.EqualTo(beforeClip));
        }

        [TestCase(false)] [TestCase(true)]
        public void AppearanceOnlyPreparedRestCannotHideUnknownPlayableOrFxWeight(bool fxWeight)
        {
            var state = Open(75); skin.SetBlendShapeWeight(0, 17);
            var type = VrcParentConstraintType();
            var target = new GameObject("Only appearance configuration"); target.transform.SetParent(avatar.transform, false);
            target.AddComponent(type);
            AnimationUtility.SetEditorCurve((AnimationClip)state.motion, EditorCurveBinding.FloatCurve(target.name, type, "FreezeToWorld"),
                AnimationCurve.Constant(0, 1, 1));
            string diagnostic;
            if (fxWeight)
            {
                diagnostic = "VRCPlayableLayerControl";
                var controlType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly =>
                    assembly.GetType("VRC.SDK3.Avatars.Components.VRCPlayableLayerControl")).FirstOrDefault(value => value != null);
                Assert.That(controlType, Is.Not.Null);
                var control = state.AddStateMachineBehaviour(controlType);
                using var data = new SerializedObject(control);
                var layer = data.FindProperty("layer"); layer.enumValueIndex = Array.IndexOf(layer.enumNames, "FX");
                data.FindProperty("goalWeight").floatValue = .5f; data.FindProperty("blendDuration").floatValue = 0;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            else
            {
                AfkAction(false, false).AddStateMachineBehaviour<UnknownStateCallbackProbe>();
                diagnostic = nameof(UnknownStateCallbackProbe);
            }
            var before = EditorJsonUtility.ToJson(controller); var warnings = new List<string>();
            var error = Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar, warnings: warnings));
            Assert.That(error.Message, Does.Contain(diagnostic)); Assert.That(warnings, Is.Empty);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
        }

        [TestCase("m_LocalPosition.anything")]
        [TestCase("m_LocalRotation.x.extra")]
        [TestCase("localEulerAnglesRaw.w")]
        [TestCase("m_LocalScale.w")]
        public void IndependentTransformSupportStillRejectsUnknownPropertyComponents(string property)
        {
            Open(75);
            var target = new GameObject("Unsupported transform"); target.transform.SetParent(avatar.transform, false);
            var support = Layer("Unknown transform support");
            var clip = new AnimationClip { name = "Known transform support" };
            var valid = EditorCurveBinding.FloatCurve(target.name, typeof(Transform), "m_LocalPosition.x");
            AnimationUtility.SetEditorCurve(clip, valid, AnimationCurve.Constant(0, 1, 1));
            AssetDatabase.AddObjectToAsset(clip, controller);
            support.defaultState = State(support, clip, writeDefaults: true);
            var metadata = VrChatExpressionMenu.Read(avatar, new VrChatMenuImportPolicy { SkipAll = true });
            var plan = NeutralShapePlan.Create(avatar, controller, new[] { new[] {
                EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Open") } },
                source: metadata, fixedContext: FixedExpressionContext.Create(controller, metadata.Defaults, metadata));
            Assert.That(plan.AllowsEvaluationBinding(clip, valid), Is.True);
            // Unity refuses to create these invalid Transform curves. Exercise
            // the admission boundary directly instead of relying on an
            // impossible clip or suppressing AnimationUtility error logs.
            var unknown = EditorCurveBinding.FloatCurve(target.name, typeof(Transform), property);
            Assert.That(plan.AllowsEvaluationBinding(clip, unknown), Is.False);
            Assert.That(plan.EvaluationBindingRejection(clip, unknown), Does.Contain("未対応"));
            Assert.That(target.transform.localPosition, Is.EqualTo(Vector3.zero));
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
        }

        [Test]
        public void NeutralRejectsAnAmbiguousActivationPath()
        {
            foreach (var index in Enumerable.Range(0, 2)) new GameObject("Ambiguous clothing").transform.SetParent(avatar.transform, false);
            var clip = (AnimationClip)Open(75).motion;
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Ambiguous clothing", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1));
            Assert.That(Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar)).Message,
                Does.Contain("Ambiguous clothing").And.Contain("m_IsActive"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FixedMenuStillRejectsActivationIncludingHarmlessNeutralBindings(bool targetExists)
        {
            if (targetExists) new GameObject("Prepared clothing").transform.SetParent(avatar.transform, false);
            controller.AddParameter("Menu", AnimatorControllerParameterType.Int);
            var idle = Open(0);
            var clip = Clip("Open", AnimationCurve.Constant(0, 1, 75));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Prepared clothing", typeof(GameObject), "m_IsActive"),
                AnimationCurve.Constant(0, 1, targetExists ? 1 : 0));
            var selected = State(controller.layers[0].stateMachine, clip);
            var transition = idle.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "Menu");
            var metadata = VrChatExpressionMenu.Read(avatar, new VrChatMenuImportPolicy { SkipAll = true });
            Assert.That(Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleFixed(avatar, controller,
                metadata.Defaults, new Dictionary<string, float> { ["Menu"] = 1 }, metadata: metadata)).Message,
                Does.Contain("Prepared clothing").And.Contain("m_IsActive"));
        }

        [Test]
        public void WriteDefaultsOffInitializerRetainsItsStartupPoseAfterLeavingTheClip()
        {
            var initial = Open(); var machine = controller.layers[0].stateMachine;
            var held = State(machine);
            var transition = initial.AddTransition(held); transition.hasExitTime = true;
            transition.exitTime = .01f; transition.duration = 0;
            Assert.That(NeutralShapeSampler.Sample(avatar).Single(value => value.Shape == "Open").Weight, Is.EqualTo(100).Within(.01));
        }

        [Test]
        public void DelayedGenericMorphAnimationKeepsPreparedRestInsteadOfASampledPhase()
        {
            var state = Open(); skin.SetBlendShapeWeight(0, 17);
            AnimationUtility.SetEditorCurve((AnimationClip)state.motion,
                EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Open"),
                AnimationCurve.Linear(0, 0, 10, 100));
            var warnings = new List<string>();
            Assert.That(NeutralShapeSampler.Sample(avatar, warnings: warnings), Is.Empty);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17));
            Assert.That(warnings.Any(warning => warning.Contains("時間で変わる") && warning.Contains("blendShape.Open")), Is.True);
        }

        [Test]
        public void IndependentAutomaticBlinkAndVisemeDoNotBecomeTheRestFace()
        {
            Open();
            var blink = Layer("Automatic blinking");
            blink.defaultState = State(blink, Clip("Blink", AnimationCurve.Linear(0, 0, 10, 100)));
            controller.AddParameter("Viseme", AnimatorControllerParameterType.Int);
            var mouth = Layer("Lip sync");
            var idle = State(mouth, Clip("vrc.v.aa", AnimationCurve.Constant(0, 1, 0))); mouth.defaultState = idle;
            var spoken = State(mouth, Clip("vrc.v.aa", AnimationCurve.Constant(0, 1, 100)));
            var transition = idle.AddTransition(spoken); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "Viseme");
            var values = NeutralShapeSampler.Sample(avatar);
            Assert.That(values.Select(value => value.Shape), Is.EquivalentTo(new[] { "Open" }));
            Assert.That(values[0].Weight, Is.EqualTo(100).Within(.01));
        }

        [Test]
        public void AutomaticAnimationSharingTheOpeningChannelKeepsItsPreparedRest()
        {
            Open(); skin.SetBlendShapeWeight(0, 17); var blink = Layer("Blink also changes opening");
            var clip = Clip("Blink", AnimationCurve.Linear(0, 0, 10, 100));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Open"),
                AnimationCurve.Linear(0, 100, 10, 0));
            blink.defaultState = State(blink, clip);
            var values = NeutralShapeSampler.Sample(avatar);
            Assert.That(values.Any(value => value.Shape == "Open"), Is.False);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17));
        }

        [Test]
        public void QuietVoiceContextChoosesTheNeutralOpeningPose()
        {
            var idle = Open(0); controller.AddParameter("Voice", AnimatorControllerParameterType.Float);
            var selected = State(controller.layers[0].stateMachine, Clip("Open", AnimationCurve.Constant(0, 1, 100)));
            var transition = idle.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Greater, .5f, "Voice");
            Assert.That(NeutralShapeSampler.Sample(avatar).Single(value => value.Shape == "Open").Weight, Is.Zero.Within(.01));
        }

        [TestCase("Upright", AnimatorControllerParameterType.Float, 0, 1)]
        [TestCase("VRMode", AnimatorControllerParameterType.Int, 0, 1)]
        [TestCase("Viseme", AnimatorControllerParameterType.Int, 1, 0)]
        [TestCase("Voice", AnimatorControllerParameterType.Float, 1, 0)]
        public void NeutralUsesTheSameNormalVrChatInputsAsMenuSampling(string name, AnimatorControllerParameterType type,
            float authoredDefault, float normalValue)
        {
            var idle = Open(0);
            controller.AddParameter(new AnimatorControllerParameter {
                name = name, type = type, defaultFloat = authoredDefault, defaultInt = (int)authoredDefault });
            var selected = State(controller.layers[0].stateMachine, Clip("Open", AnimationCurve.Constant(0, 1, 100)));
            var transition = idle.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(type == AnimatorControllerParameterType.Int ? AnimatorConditionMode.Equals : AnimatorConditionMode.Greater,
                type == AnimatorControllerParameterType.Int ? 1 : .5f, name);
            var before = EditorJsonUtility.ToJson(controller);
            Assert.That(NeutralShapeSampler.Sample(avatar).Single(value => value.Shape == "Open").Weight,
                Is.EqualTo(normalValue == 0 ? 0 : 100).Within(.01));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
        }

        private void Contact(string parameter)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly =>
                assembly.GetType("VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver")).FirstOrDefault(value => value != null);
            if (type == null) Assert.Ignore("Install the real VRChat SDK contact components.");
            using var data = new SerializedObject(avatar.AddComponent(type));
            data.FindProperty("parameter").stringValue = parameter;
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        private void ExpressionDefault(string parameter, float value)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly =>
                assembly.GetType("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters")).FirstOrDefault(candidate => candidate != null);
            if (type == null) Assert.Ignore("Install the real VRChat SDK expression parameters.");
            var asset = ScriptableObject.CreateInstance(type); AssetDatabase.CreateAsset(asset, folder + "/Parameters.asset");
            using (var data = new SerializedObject(asset))
            {
                var parameters = data.FindProperty("parameters"); parameters.arraySize = 1;
                var item = parameters.GetArrayElementAtIndex(0);
                item.FindPropertyRelative("name").stringValue = parameter;
                var valueType = item.FindPropertyRelative("valueType"); valueType.enumValueIndex = Array.IndexOf(valueType.enumNames, "Float");
                item.FindPropertyRelative("defaultValue").floatValue = value;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            using (var data = new SerializedObject(descriptor))
            {
                data.FindProperty("expressionParameters").objectReferenceValue = asset;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        [TestCase(false, 0)]
        [TestCase(false, 1)]
        [TestCase(true, 0)]
        [TestCase(true, 1)]
        public void NeutralContactUsesTheAuthoredDefaultWithoutReceivingLiveInput(bool expressionDefault, float value)
        {
            controller.AddParameter(new AnimatorControllerParameter {
                name = "Pet", type = AnimatorControllerParameterType.Float, defaultFloat = expressionDefault ? 1 - value : value });
            Contact("Pet");
            if (expressionDefault) ExpressionDefault("Pet", value);
            var idle = Open(0);
            var selected = State(controller.layers[0].stateMachine, Clip("Open", AnimationCurve.Constant(0, 1, 100)));
            var transition = idle.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Greater, .25f, "Pet");
            Assert.That(VrChatExpressionMenu.Read(avatar).ExternalParameters, Does.Contain("Pet"));
            Assert.That(NeutralShapeSampler.Sample(avatar).Single(result => result.Shape == "Open").Weight,
                Is.EqualTo(value == 0 ? 0 : 100).Within(.01));
            Assert.That(controller.parameters.Single(parameter => parameter.name == "Pet").defaultFloat,
                Is.EqualTo(expressionDefault ? 1 - value : value));
        }

        [Test]
        public void SuppliedBuiltinCanBeCopiedByTheNeutralDriver()
        {
            controller.AddParameter("Upright", AnimatorControllerParameterType.Float);
            controller.AddParameter("Face", AnimatorControllerParameterType.Float);
            var idle = Open(0);
            ParameterDriverExpressionTests.Driver(idle, ParameterDriverExpressionTests.Op("Copy", "Face", source: "Upright"));
            var selected = State(controller.layers[0].stateMachine, Clip("Open", AnimationCurve.Constant(0, 1, 100)));
            var transition = idle.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Greater, .5f, "Face");
            Assert.That(NeutralShapeSampler.Sample(avatar).Single(value => value.Shape == "Open").Weight, Is.EqualTo(100).Within(.01));
        }

        [Test]
        public void FixedContactIsOnlyAnInitialValueAndDoesNotSuppressAuthoredDriverWrites()
        {
            controller.AddParameter("Pet", AnimatorControllerParameterType.Float); Contact("Pet");
            var idle = Open(0);
            ParameterDriverExpressionTests.Driver(idle, ParameterDriverExpressionTests.Op("Set", "Pet", 1));
            var selected = State(controller.layers[0].stateMachine, Clip("Open", AnimationCurve.Constant(0, 1, 100)));
            var transition = idle.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Greater, .25f, "Pet");
            Assert.That(NeutralShapeSampler.Sample(avatar).Single(value => value.Shape == "Open").Weight, Is.EqualTo(100).Within(.01));
            Assert.That(controller.parameters.Single(parameter => parameter.name == "Pet").defaultFloat, Is.Zero);
        }

        [Test]
        public void FixedContactCannotHideAnotherPlayableLayerWriter()
        {
            controller.AddParameter("Pet", AnimatorControllerParameterType.Float); Contact("Pet");
            var idle = Open(0);
            var selected = State(controller.layers[0].stateMachine, Clip("Open", AnimationCurve.Constant(0, 1, 100)));
            var transition = idle.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Greater, .25f, "Pet");
            var other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Action.controller");
            other.AddParameter("Pet", AnimatorControllerParameterType.Float);
            var state = State(other.layers[0].stateMachine); other.layers[0].stateMachine.defaultState = state;
            ParameterDriverExpressionTests.Driver(state, ParameterDriverExpressionTests.Op("Set", "Pet", 1));
            using (var data = new SerializedObject(descriptor))
            {
                var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = 2;
                var layer = layers.GetArrayElementAtIndex(1);
                var type = layer.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "Action");
                layer.FindPropertyRelative("isDefault").boolValue = false;
                layer.FindPropertyRelative("animatorController").objectReferenceValue = other;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            Assert.That(Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar)).Message,
                Does.Contain("FX以外").And.Contain("Pet"));
        }

        private AnimatorState AfkAction(bool reachable, bool driver = true)
        {
            var other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/AfkAction.controller");
            other.AddParameter(new AnimatorControllerParameter {
                name = "AFK", type = AnimatorControllerParameterType.Bool, defaultBool = true });
            other.AddParameter("AFK_Step", AnimatorControllerParameterType.Int);
            var machine = other.layers[0].stateMachine;
            var idle = State(machine); machine.defaultState = idle;
            var afk = State(machine, Clip("Open", AnimationCurve.Constant(0, 1, 0)));
            var transition = idle.AddTransition(afk); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(reachable ? AnimatorConditionMode.IfNot : AnimatorConditionMode.If, 0, "AFK");
            if (driver) ParameterDriverExpressionTests.Driver(afk, ParameterDriverExpressionTests.Op("Set", "AFK_Step", 1));
            SetAction(other); return afk;
        }

        private AnimationClip TransientOwnershipFixture(string scenario, bool networkSynced = true)
        {
            const string signal = "Transition signal";
            controller.AddParameter(signal, AnimatorControllerParameterType.Int);
            var idle = Open(75);
            var clothing = new GameObject("Dormant appearance"); clothing.transform.SetParent(avatar.transform, false);
            var visual = Clip("Open", AnimationCurve.Constant(0, 1, 0)); visual.name = "Signal appearance branch";
            AnimationUtility.SetEditorCurve(visual, EditorCurveBinding.FloatCurve(clothing.name, typeof(Transform), "m_LocalPosition.x"),
                AnimationCurve.Constant(0, 1, 1));
            var changed = State(controller.layers[0].stateMachine, visual);
            var transition = idle.AddTransition(changed); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, signal);
            var other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/TransientAction.controller");
            other.AddParameter("AFK", AnimatorControllerParameterType.Bool);
            other.AddParameter(new AnimatorControllerParameter { name = signal,
                type = scenario == "type" ? AnimatorControllerParameterType.Float : AnimatorControllerParameterType.Int,
                defaultInt = scenario == "default" ? 1 : 0 });
            var machine = other.layers[0].stateMachine;
            var rest = State(machine); machine.defaultState = rest;
            var active = State(machine);
            var entrance = rest.AddTransition(active); entrance.hasExitTime = false; entrance.duration = 0;
            entrance.AddCondition(scenario == "reachable" || scenario == "random" ? AnimatorConditionMode.IfNot : AnimatorConditionMode.If, 0, "AFK");
            if (scenario != "unwritten") ParameterDriverExpressionTests.Driver(active, ParameterDriverExpressionTests.Op(
                scenario == "random" ? "Random" : "Set", signal, 1));
            if (scenario == "unknown")
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly =>
                    assembly.GetType("VRC.SDK3.Avatars.Components.VRCAnimatorLayerControl")).FirstOrDefault(value => value != null);
                Assert.That(type, Is.Not.Null); active.AddStateMachineBehaviour(type);
            }
            if (scenario == "fxwriter") ParameterDriverExpressionTests.Driver(idle, ParameterDriverExpressionTests.Op("Set", signal, 0));
            if (scenario == "fxcurve") AnimationUtility.SetEditorCurve((AnimationClip)idle.motion,
                EditorCurveBinding.FloatCurve("", typeof(Animator), signal), AnimationCurve.Constant(0, 1, 0));
            if (scenario == "external") Contact(signal);
            SetAction(other);
            ConfigureTransientInputMetadata(signal, scenario, networkSynced);
            return visual;
        }

        private void ConfigureTransientInputMetadata(string signal, string scenario, bool networkSynced)
        {
            Type Sdk(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(name)).FirstOrDefault(value => value != null);
            var parametersType = Sdk("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters");
            var menuType = Sdk("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu");
            Assert.That(parametersType, Is.Not.Null); Assert.That(menuType, Is.Not.Null);
            var parametersAsset = ScriptableObject.CreateInstance(parametersType);
            AssetDatabase.CreateAsset(parametersAsset, folder + "/TransientParameters.asset");
            using (var data = new SerializedObject(parametersAsset))
            {
                var parameters = data.FindProperty("parameters"); parameters.arraySize = 1;
                var item = parameters.GetArrayElementAtIndex(0);
                item.FindPropertyRelative("name").stringValue = signal;
                var valueType = item.FindPropertyRelative("valueType"); valueType.enumValueIndex = Array.IndexOf(valueType.enumNames, "Int");
                item.FindPropertyRelative("defaultValue").floatValue = scenario == "expressionDefault" ? 1 : scenario == "nonfinite" ? float.NaN : 0;
                item.FindPropertyRelative("saved").boolValue = scenario == "saved";
                item.FindPropertyRelative("networkSynced").boolValue = networkSynced;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            var menu = ScriptableObject.CreateInstance(menuType); AssetDatabase.CreateAsset(menu, folder + "/TransientMenu.asset");
            using (var data = new SerializedObject(menu))
            {
                var controls = data.FindProperty("controls"); controls.arraySize =
                    scenario == "menu" || scenario == "subparameter" || scenario == "cycle" || scenario == "unknownMenu" || scenario == "missingSubmenu" || scenario == "optionalNull" ? 1 : scenario == "shared" ? 2 : 0;
                if (controls.arraySize > 0)
                {
                    var control = controls.GetArrayElementAtIndex(0);
                    var type = control.FindPropertyRelative("type");
                    var kind = scenario == "cycle" || scenario == "missingSubmenu" ? "SubMenu" : scenario == "subparameter" ? "RadialPuppet" : "Toggle";
                    type.enumValueIndex = Array.IndexOf(type.enumNames, kind);
                    if (scenario == "unknownMenu") type.intValue = 999;
                    control.FindPropertyRelative("parameter").FindPropertyRelative("name").stringValue = scenario == "menu" ? signal : "";
                    var subParameters = control.FindPropertyRelative("subParameters"); subParameters.arraySize = scenario == "subparameter" ? 1 : 0;
                    if (subParameters.arraySize > 0) subParameters.GetArrayElementAtIndex(0).FindPropertyRelative("name").stringValue = signal;
                    control.FindPropertyRelative("subMenu").objectReferenceValue = scenario == "cycle" ? menu : null;
                }
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            if (scenario == "shared")
            {
                var child = ScriptableObject.CreateInstance(menuType); AssetDatabase.CreateAsset(child, folder + "/SharedTransientMenu.asset");
                using (var data = new SerializedObject(child))
                {
                    var controls = data.FindProperty("controls"); controls.arraySize = 1;
                    var control = controls.GetArrayElementAtIndex(0);
                    var type = control.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "Toggle");
                    control.FindPropertyRelative("parameter").FindPropertyRelative("name").stringValue = "Unrelated control";
                    control.FindPropertyRelative("subParameters").arraySize = 0;
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
                using (var data = new SerializedObject(menu))
                {
                    var controls = data.FindProperty("controls");
                    for (var index = 0; index < controls.arraySize; index++)
                    {
                        var control = controls.GetArrayElementAtIndex(index);
                        var type = control.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "SubMenu");
                        control.FindPropertyRelative("parameter").FindPropertyRelative("name").stringValue = "";
                        control.FindPropertyRelative("subParameters").arraySize = 0;
                        control.FindPropertyRelative("subMenu").objectReferenceValue = child;
                    }
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            if (scenario == "optionalNull")
            {
                // Use actual SDK fields to create a null optional inline
                // Parameter and null subParameter array, then inspect their
                // real Unity serialized representation through the helper.
                var controls = (System.Collections.IList)menuType.GetField("controls").GetValue(menu);
                var control = controls[0]; var controlType = control.GetType();
                controlType.GetField("parameter").SetValue(control, null);
                controlType.GetField("subParameters").SetValue(control, null);
            }
            using (var data = new SerializedObject(descriptor))
            {
                data.FindProperty("customExpressions").boolValue = true;
                data.FindProperty("expressionParameters").objectReferenceValue = parametersAsset;
                data.FindProperty("expressionsMenu").objectReferenceValue = scenario == "missingMenu" ? null : menu;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnsavedNonMenuSignalWithOnlyDormantOtherPlayableWritersDoesNotOwnTheNormalFace(bool networkSynced)
        {
            var visual = TransientOwnershipFixture("normal", networkSynced);
            var metadata = VrChatExpressionMenu.Read(avatar, new VrChatMenuImportPolicy { SkipAll = true });
            NeutralInputProof.Read(avatar, metadata);
            Assert.That(metadata.NeutralInputInventoryComplete, Is.True);
            Assert.That(metadata.ParameterPersistence["Transition signal"].Saved, Is.False);
            Assert.That(metadata.MenuInputs, Does.Not.Contain("Transition signal"));
            var layers = ExpressionDependencies.NormalInputLayers(controller, metadata,
                FixedExpressionContext.Create(controller, metadata.Defaults, metadata), null);
            Assert.That(layers.SelectMany(layer => layer.Clips).Contains(visual), Is.False);
            var before = EditorJsonUtility.ToJson(controller);
            var required = new[] { EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Open") };
            var values = NeutralShapeSampler.Sample(avatar, requiredMorphs: required);
            Assert.That(values.Single(value => value.Shape == "Open").Weight, Is.EqualTo(75).Within(.01));
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
            Assert.That(avatar.transform.Find("Dormant appearance").localPosition, Is.EqualTo(Vector3.zero));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
        }

        [TestCase("saved")]
        [TestCase("menu")]
        [TestCase("subparameter")]
        [TestCase("missingMenu")]
        [TestCase("missingSubmenu")]
        [TestCase("unknownMenu")]
        [TestCase("reachable")]
        [TestCase("random")]
        [TestCase("unknown")]
        [TestCase("unwritten")]
        [TestCase("fxwriter")]
        [TestCase("fxcurve")]
        [TestCase("external")]
        [TestCase("type")]
        [TestCase("default")]
        [TestCase("expressionDefault")]
        [TestCase("nonfinite")]
        public void SavedExposedUnprovedOrWrittenCustomSignalRetainsItsAppearanceAlternatives(string scenario)
        {
            var visual = TransientOwnershipFixture(scenario);
            if (scenario == "nonfinite")
            {
                Assert.That(Assert.Throws<InvalidOperationException>(() => VrChatExpressionMenu.Read(avatar,
                    new VrChatMenuImportPolicy { SkipAll = true })).Message, Does.Contain("不正な数値"));
                Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
                return;
            }
            var metadata = VrChatExpressionMenu.Read(avatar, new VrChatMenuImportPolicy { SkipAll = true });
            NeutralInputProof.Read(avatar, metadata);
            var layers = ExpressionDependencies.NormalInputLayers(controller, metadata,
                FixedExpressionContext.Create(controller, metadata.Defaults, metadata), null);
            Assert.That(layers.SelectMany(layer => layer.Clips).Contains(visual), Is.True, scenario);
            if (scenario != "external")
            {
                var warnings = new List<string>();
                var required = new[] { EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Open") };
                if (scenario == "reachable" || scenario == "random" || scenario == "unknown")
                {
                    var error = Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar, requiredMorphs: required, warnings: warnings));
                    Assert.That(error.Message, Does.Contain("TransientAction").And.Contain("Transition signal").And.Contain("Playable Layer"));
                    if (scenario == "unknown") Assert.That(error.Message, Does.Contain("VRCAnimatorLayerControl"));
                    Assert.That(warnings, Is.Empty);
                }
                else
                {
                    Assert.That(NeutralShapeSampler.Sample(avatar, requiredMorphs: required, warnings: warnings), Is.Empty);
                    Assert.That(warnings.Any(value => value.Contains("Open") && value.Contains("Signal appearance branch")), Is.True);
                }
            }
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
            Assert.That(avatar.transform.Find("Dormant appearance").localPosition, Is.EqualTo(Vector3.zero));
        }

        [TestCase("selected")]
        [TestCase("remote")]
        [TestCase("missingPersistence")]
        [TestCase("missingType")]
        [TestCase("nonfinite")]
        public void TransientOwnershipProofRetainsUnknownMetadataAndExplicitOrRemoteInputs(string scenario)
        {
            var visual = TransientOwnershipFixture("normal");
            var metadata = VrChatExpressionMenu.Read(avatar, new VrChatMenuImportPolicy { SkipAll = true });
            NeutralInputProof.Read(avatar, metadata);
            if (scenario == "remote") metadata.Defaults["IsLocal"] = 0;
            if (scenario == "missingPersistence") metadata.ParameterPersistence.Remove("Transition signal");
            if (scenario == "missingType") metadata.ExpressionParameterTypes.Remove("Transition signal");
            if (scenario == "nonfinite") metadata.Defaults["Transition signal"] = float.NaN;
            var selected = scenario == "selected" ? new Dictionary<string, float> { ["Transition signal"] = 1 } : null;
            var layers = ExpressionDependencies.NormalInputLayers(controller, metadata,
                FixedExpressionContext.Create(controller, metadata.Defaults, metadata), null, selected);
            Assert.That(layers.SelectMany(layer => layer.Clips).Contains(visual), Is.True);
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
        }

        [TestCase("cycle")]
        [TestCase("shared")]
        [TestCase("optionalNull")]
        public void CompleteMenuInputInventoryHandlesSharedCyclicAndEmptyOptionalSdkInputs(string scenario)
        {
            var visual = TransientOwnershipFixture(scenario);
            var metadata = VrChatExpressionMenu.Read(avatar, new VrChatMenuImportPolicy { SkipAll = true });
            NeutralInputProof.Read(avatar, metadata);
            Assert.That(metadata.NeutralInputInventoryComplete, Is.True);
            Assert.That(metadata.MenuInputs, Does.Not.Contain("Transition signal"));
            Assert.That(ExpressionDependencies.NormalInputLayers(controller, metadata,
                FixedExpressionContext.Create(controller, metadata.Defaults, metadata), null).SelectMany(layer => layer.Clips).Contains(visual), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DormantAfkActionCannotChangeTheNormalNeutralFace(bool declaredInFx)
        {
            if (declaredInFx) controller.AddParameter("AFK", AnimatorControllerParameterType.Bool);
            controller.AddParameter("AFK_Step", AnimatorControllerParameterType.Int);
            var idle = Open(75);
            var moving = State(controller.layers[0].stateMachine, Clip("Open", AnimationCurve.Linear(0, 0, 10, 100)));
            var transition = idle.AddTransition(moving); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "AFK_Step");
            AfkAction(false);
            Assert.That(NeutralShapeSampler.Sample(avatar).Single(value => value.Shape == "Open").Weight, Is.EqualTo(75).Within(.01));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReachableActionMorphOrParameterWriterIsStillRejected(bool driver)
        {
            controller.AddParameter("AFK_Step", AnimatorControllerParameterType.Int);
            var idle = Open(75);
            var changed = State(controller.layers[0].stateMachine, Clip("Open", AnimationCurve.Constant(0, 1, 100)));
            var transition = idle.AddTransition(changed); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "AFK_Step");
            AfkAction(true, driver);
            Assert.That(Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar)).Message,
                Does.Contain("FX以外").And.Contain(driver ? "AFK_Step" : "Body/blendShape.Open"));
        }

        [Test]
        public void DormantActionDoesNotHideAnUnknownBehaviour()
        {
            Open(); var afk = AfkAction(false, false);
            afk.AddStateMachineBehaviour<UnknownStateCallbackProbe>();
            Assert.That(Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar)).Message,
                Does.Contain("FX以外").And.Contain(nameof(UnknownStateCallbackProbe)));
        }

        [Test]
        public void FxDriverWritingTheFixedContactCannotHideAnActionBranch()
        {
            controller.AddParameter("Pet", AnimatorControllerParameterType.Float); Contact("Pet");
            var idle = Open(75); ParameterDriverExpressionTests.Driver(idle, ParameterDriverExpressionTests.Op("Set", "Pet", 1));
            var other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/PetAction.controller");
            other.AddParameter("Pet", AnimatorControllerParameterType.Float);
            var machine = other.layers[0].stateMachine;
            var rest = State(machine); machine.defaultState = rest;
            var active = State(machine, Clip("Open", AnimationCurve.Constant(0, 1, 0)));
            var transition = rest.AddTransition(active); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Greater, .25f, "Pet"); SetAction(other);
            Assert.That(Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar)).Message,
                Does.Contain("FX以外").And.Contain("Body/blendShape.Open"));
        }

        [TestCase("EyeHeightAsMeters")]
        [TestCase("EyeHeightAsPercent")]
        public void UnmeasuredBuiltinInputKeepsPreparedNeutralInsteadOfChoosingAFace(string name)
        {
            var idle = Open(0);
            controller.AddParameter(new AnimatorControllerParameter { name = name, type = AnimatorControllerParameterType.Float, defaultFloat = 1 });
            var selected = State(controller.layers[0].stateMachine, Clip("Open", AnimationCurve.Constant(0, 1, 100)));
            var transition = idle.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Greater, .5f, name);
            skin.SetBlendShapeWeight(0, 31);
            var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values, Is.Empty);
            NeutralShapeSnapshot.Apply(avatar, values);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(31));
            Assert.That(warnings.Any(value => value.Contains(name) && value.Contains("Open")), Is.True);
        }

        [TestCase("UnknownAction")]
        [TestCase("ActionMorph")]
        [TestCase("FxWeight")]
        public void RecoverableExternalInputCannotHideAnIndependentUnsupportedGraph(string kind)
        {
            controller.AddParameter("EyeHeightAsMeters", AnimatorControllerParameterType.Float);
            var idle = Open(75);
            var selected = State(controller.layers[0].stateMachine, Clip("Open", AnimationCurve.Constant(0, 1, 100)));
            var transition = idle.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Greater, .5f, "EyeHeightAsMeters");
            string diagnostic;
            if (kind == "FxWeight")
            {
                diagnostic = "VRCPlayableLayerControl";
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly =>
                    assembly.GetType("VRC.SDK3.Avatars.Components.VRCPlayableLayerControl")).FirstOrDefault(value => value != null);
                Assert.That(type, Is.Not.Null);
                var control = idle.AddStateMachineBehaviour(type);
                using var data = new SerializedObject(control);
                var layer = data.FindProperty("layer"); layer.enumValueIndex = Array.IndexOf(layer.enumNames, "FX");
                data.FindProperty("goalWeight").floatValue = .5f;
                data.FindProperty("blendDuration").floatValue = 0;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            else
            {
                var action = AfkAction(kind == "ActionMorph", false);
                if (kind == "UnknownAction") action.AddStateMachineBehaviour<UnknownStateCallbackProbe>();
                diagnostic = kind == "UnknownAction" ? nameof(UnknownStateCallbackProbe) : "Body/blendShape.Open";
            }
            var before = EditorJsonUtility.ToJson(controller);
            var warnings = new List<string>();
            var error = Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar, warnings: warnings));
            Assert.That(error.Message, Does.Contain(diagnostic));
            Assert.That(warnings, Is.Empty, "A recoverable input must not conceal the independent hard failure.");
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
        }

        [Test]
        public void KnownNeutralGestureZeroPrunesAnUnreachableAnimatedExpression()
        {
            var idle = Open(); controller.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
            var selected = State(controller.layers[0].stateMachine, Clip("Open", AnimationCurve.Linear(0, 0, 10, 100)));
            var transition = idle.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 2, "GestureRight");
            Assert.That(NeutralShapeSampler.Sample(avatar).Single(value => value.Shape == "Open").Weight, Is.EqualTo(100).Within(.01));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnresolvedGroupPreservesAuthoredWeightsWithoutDiscardingIndependentRest(bool unresolvedFirst)
        {
            controller.AddParameter("EyeHeightAsMeters", AnimatorControllerParameterType.Float);
            var first = controller.layers[0].stateMachine;
            var second = Layer("Independent group");
            var unresolved = unresolvedFirst ? first : second;
            var resolved = unresolvedFirst ? second : first;
            resolved.defaultState = State(resolved, Clip("Open", AnimationCurve.Constant(0, 1, 100)));
            unresolved.defaultState = State(unresolved, Clip("Untouched", AnimationCurve.Constant(0, 1, 63)));
            var alternate = State(unresolved, Clip("Untouched", AnimationCurve.Constant(0, 1, 91)));
            var transition = unresolved.defaultState.AddTransition(alternate); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Greater, .5f, "EyeHeightAsMeters");
            var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings,
                requiredMorphs: new[] { EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Untouched") });
            Assert.That(values.Select(value => value.Shape), Is.EquivalentTo(new[] { "Open" }));
            Assert.That(values.Single().Weight, Is.EqualTo(100).Within(.01));
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero, "Sampling must not change the prepared source.");
            NeutralShapeSnapshot.Apply(avatar, values);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(100).Within(.01));
            Assert.That(skin.GetBlendShapeWeight(3), Is.EqualTo(35));
            Assert.That(warnings.Any(value => value.Contains("Untouched") && value.Contains("EyeHeightAsMeters")), Is.True);
        }

        [Test]
        public void CommonDefaultFaceCanResetBlinkWhileIndependentBlinkStaysLive()
        {
            var state = Open(); skin.SetBlendShapeWeight(1, 100);
            AnimationUtility.SetEditorCurve((AnimationClip)state.motion,
                EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Blink"), AnimationCurve.Constant(0, 1, 0));
            var blink = Layer("BLINK"); blink.defaultState = State(blink, Clip("Blink", AnimationCurve.Linear(0, 0, 10, 100)));
            var values = NeutralShapeSampler.Sample(avatar);
            Assert.That(values.Single(value => value.Shape == "Open").Weight, Is.EqualTo(100).Within(.01));
            Assert.That(values.Single(value => value.Shape == "Blink").Weight, Is.Zero.Within(.01));
        }

        [Test]
        public void AutomaticChannelTimedInitializerStillResetsItsStaticClosingWeight()
        {
            Open(); skin.SetBlendShapeWeight(1, 100);
            var blink = Layer("Blink initializer");
            var initial = State(blink, Clip("Blink", AnimationCurve.Constant(0, 1, 0))); blink.defaultState = initial;
            var held = State(blink);
            var transition = initial.AddTransition(held); transition.hasExitTime = true; transition.exitTime = .01f; transition.duration = 0;
            var values = NeutralShapeSampler.Sample(avatar);
            Assert.That(values.Single(value => value.Shape == "Open").Weight, Is.EqualTo(100).Within(.01));
            Assert.That(values.Single(value => value.Shape == "Blink").Weight, Is.Zero.Within(.01));
        }

        // The source generator uses a separate DEFAULT FACE underlay, a local
        // SetControl driver, nested Not AFK/AFK machines and alternative mode /
        // branch states on FACE EMOTE PLAYER. Voice gates are AND transitions.
        [TestCase(false)]
        [TestCase(true)]
        public void GeneratedFaceEmoStyleNestedFxSettlesWithQuietVoiceInEitherWaitMode(bool waitByVoice)
        {
            Open();
            controller.AddParameter("AFK", AnimatorControllerParameterType.Bool);
            controller.AddParameter("FaceEmo_CN_EMOTE_OVERRIDE", AnimatorControllerParameterType.Bool);
            controller.AddParameter("FaceEmo_CN_BYPASS", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Voice", AnimatorControllerParameterType.Float);
            controller.AddParameter("GestureLeft", AnimatorControllerParameterType.Int);
            controller.AddParameter("FaceEmo_EM_EMOTE_PRESELECT", AnimatorControllerParameterType.Int);
            controller.AddParameter("FaceEmo_SYNC_EM_EMOTE", AnimatorControllerParameterType.Int);
            controller.AddParameter("FaceEmo_SYNC_CN_WAIT_FACE_EMOTE_BY_VOICE", AnimatorControllerParameterType.Bool);
            var parameters = controller.parameters;
            parameters.Single(parameter => parameter.name == "FaceEmo_SYNC_CN_WAIT_FACE_EMOTE_BY_VOICE").defaultBool = waitByVoice;
            controller.parameters = parameters;
            var input = Layer("INPUT CONVERTER L");
            var neutral = State(input); input.defaultState = neutral;
            ParameterDriverExpressionTests.Driver(neutral, ParameterDriverExpressionTests.Op("Set", "FaceEmo_EM_EMOTE_PRESELECT", 0));
            input.AddEntryTransition(neutral).AddCondition(AnimatorConditionMode.Equals, 0, "GestureLeft");
            var gesture = State(input);
            ParameterDriverExpressionTests.Driver(gesture, ParameterDriverExpressionTests.Op("Set", "FaceEmo_EM_EMOTE_PRESELECT", 1));
            input.AddEntryTransition(gesture).AddCondition(AnimatorConditionMode.Equals, 1, "GestureLeft");
            var control = Layer("FACE EMOTE SET CONTROL"); control.defaultState = State(control);
            ParameterDriverExpressionTests.Driver(control.defaultState,
                ParameterDriverExpressionTests.Op("Set", "FaceEmo_SYNC_EM_EMOTE", 1),
                ParameterDriverExpressionTests.Op("Set", "FaceEmo_SYNC_CN_WAIT_FACE_EMOTE_BY_VOICE", waitByVoice ? 1 : 0));
            var player = Layer("[ USER EDIT ] FACE EMOTE PLAYER");
            var normal = player.AddStateMachine("Not AFK");
            player.AddEntryTransition(normal).AddCondition(AnimatorConditionMode.IfNot, 0, "AFK");
            var afk = player.AddStateMachine("AFK");
            player.AddEntryTransition(afk).AddCondition(AnimatorConditionMode.If, 0, "AFK");
            afk.defaultState = State(afk, Clip("Open", AnimationCurve.Linear(0, 0, 10, 100)));
            afk.defaultState.motion.name = "AFK dynamic opening";
            var mode = State(normal, Clip("Open", AnimationCurve.Constant(0, 1, 70))); normal.defaultState = mode;
            var branch = State(normal, Clip("Open", AnimationCurve.Constant(0, 1, 20)));
            mode.motion.name = "Mode default opening70"; branch.motion.name = "Branch opening20";
            normal.AddEntryTransition(mode).AddCondition(AnimatorConditionMode.Equals, 1, "FaceEmo_SYNC_EM_EMOTE");
            normal.AddEntryTransition(branch).AddCondition(AnimatorConditionMode.Equals, 2, "FaceEmo_SYNC_EM_EMOTE");
            // FaceEmo FxGenerator.GenerateFaceEmotePlayerLayer creates both
            // direct root states with conditional exits. The exit is necessary:
            // a root fallback without it never enters the selected nested mode.
            var overriding = State(player); overriding.name = "in OVERRIDE"; player.defaultState = overriding;
            var bypass = State(player); bypass.name = "BYPASS";
            var enterOverride = player.AddAnyStateTransition(overriding); enterOverride.hasExitTime = false; enterOverride.duration = 0;
            enterOverride.AddCondition(AnimatorConditionMode.If, 0, "FaceEmo_CN_EMOTE_OVERRIDE");
            enterOverride.AddCondition(AnimatorConditionMode.IfNot, 0, "FaceEmo_CN_BYPASS");
            var leaveOverride = overriding.AddExitTransition(); leaveOverride.hasExitTime = false; leaveOverride.duration = 0;
            leaveOverride.AddCondition(AnimatorConditionMode.IfNot, 0, "FaceEmo_CN_EMOTE_OVERRIDE");
            var enterBypass = player.AddAnyStateTransition(bypass); enterBypass.hasExitTime = false; enterBypass.duration = 0;
            enterBypass.AddCondition(AnimatorConditionMode.If, 0, "FaceEmo_CN_BYPASS");
            var leaveBypass = bypass.AddExitTransition(); leaveBypass.hasExitTime = false; leaveBypass.duration = 0;
            leaveBypass.AddCondition(AnimatorConditionMode.IfNot, 0, "FaceEmo_CN_BYPASS");
            player.AddStateMachineExitTransition(normal); player.AddStateMachineExitTransition(afk);
            var changed = mode.AddExitTransition(); changed.hasExitTime = false; changed.duration = 0;
            changed.AddCondition(AnimatorConditionMode.NotEqual, 1, "FaceEmo_SYNC_EM_EMOTE");
            changed.AddCondition(AnimatorConditionMode.If, 0, "FaceEmo_SYNC_CN_WAIT_FACE_EMOTE_BY_VOICE");
            changed.AddCondition(AnimatorConditionMode.Less, .01f, "Voice");
            if (!waitByVoice) AssertOriginalNestedModePose(mode);
            Assert.That(NeutralShapeSampler.Sample(avatar).Single(value => value.Shape == "Open").Weight, Is.EqualTo(70).Within(.01));
        }

        // Native routing is checked independently of the exporter's controller
        // clone. Original VRC drivers do not execute in EditMode, so seed the
        // same known post-driver values before evaluating the original graph.
        private void AssertOriginalNestedModePose(AnimatorState mode)
        {
            var preview = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            GameObject copy = null;
            var graph = default(PlayableGraph);
            try
            {
                copy = Object.Instantiate(avatar); copy.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(copy, preview);
                foreach (var component in copy.GetComponentsInChildren<Behaviour>(true)) component.enabled = false;
                var animator = copy.GetComponent<Animator>(); animator.runtimeAnimatorController = null; animator.enabled = true;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.applyRootMotion = false; animator.fireEvents = false;
                copy.SetActive(true);
                graph = PlayableGraph.Create("Generated FaceEmo fixture native routing");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                AnimationPlayableOutput.Create(graph, "Original FaceEmo fixture", animator).SetSourcePlayable(playable);
                playable.SetBool("AFK", false); playable.SetFloat("Voice", 0); playable.SetInteger("GestureLeft", 0);
                playable.SetInteger("FaceEmo_EM_EMOTE_PRESELECT", 0); playable.SetInteger("FaceEmo_SYNC_EM_EMOTE", 1);
                playable.SetBool("FaceEmo_SYNC_CN_WAIT_FACE_EMOTE_BY_VOICE", false);
                playable.SetBool("FaceEmo_CN_EMOTE_OVERRIDE", false); playable.SetBool("FaceEmo_CN_BYPASS", false);
                graph.Play(); graph.Evaluate(0f);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                var layer = Array.FindIndex(controller.layers, value => value.name == "[ USER EDIT ] FACE EMOTE PLAYER");
                Assert.That(playable.IsInTransition(layer), Is.False, "The original generated topology must settle.");
                Assert.That(playable.GetCurrentAnimatorStateInfo(layer).fullPathHash,
                    Is.EqualTo(Animator.StringToHash(controller.layers[layer].name + ".Not AFK." + mode.name)),
                    "The original graph must select the Mode state before testing the exporter's private clone.");
                Assert.That(copy.transform.Find("Body").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(0),
                    Is.EqualTo(70).Within(.01f), "The original Mode clip controls opening above the common default.");
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                if (copy != null) Object.DestroyImmediate(copy);
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(preview);
            }
        }

        [Test]
        public void StartupParameterDriverReachesTheMorphLayer()
        {
            controller.AddParameter("Face", AnimatorControllerParameterType.Int);
            var gate = controller.layers[0].stateMachine;
            gate.defaultState = State(gate);
            ParameterDriverExpressionTests.Driver(gate.defaultState, ParameterDriverExpressionTests.Op("Set", "Face", 1));
            var face = Layer("Face"); var idle = State(face, Clip("Open", AnimationCurve.Constant(0, 1, 0))); face.defaultState = idle;
            var open = State(face, Clip("Open", AnimationCurve.Constant(0, 1, 100)));
            var transition = idle.AddTransition(open); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "Face");
            Assert.That(NeutralShapeSampler.Sample(avatar).Single(value => value.Shape == "Open").Weight, Is.EqualTo(100).Within(.01));
        }

        [Test]
        public void FractionalIntSetUsesDriverTruncationAndCannotPruneItsZeroEndpoint()
        {
            var idle = Open(0); controller.AddParameter("Face", AnimatorControllerParameterType.Int);
            var parameters = controller.parameters; parameters.Single(parameter => parameter.name == "Face").defaultInt = 1;
            controller.parameters = parameters;
            ParameterDriverExpressionTests.Driver(idle, ParameterDriverExpressionTests.Op("Set", "Face", .9f));
            var opened = State(controller.layers[0].stateMachine, Clip("Open", AnimationCurve.Constant(0, 1, 100)));
            var transition = idle.AddTransition(opened); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 0, "Face");
            Assert.That(NeutralShapeSampler.Sample(avatar).Single(value => value.Shape == "Open").Weight, Is.EqualTo(100).Within(.01));
        }

        [Test]
        public void NestedExitReachesItsParentDestinationAndPrunesFalseAncestorAnyState()
        {
            var root = controller.layers[0].stateMachine;
            var startup = root.AddStateMachine("Startup");
            startup.defaultState = State(startup, Clip("Open", AnimationCurve.Constant(0, 1, 0)));
            var exit = startup.defaultState.AddExitTransition(); exit.hasExitTime = true; exit.exitTime = .01f; exit.duration = 0;
            var held = State(root, Clip("Open", AnimationCurve.Constant(0, 1, 100))); root.defaultState = held;
            root.AddEntryTransition(startup); root.AddStateMachineTransition(startup, held);
            controller.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
            var unreachable = State(root, Clip("Open", AnimationCurve.Linear(0, 0, 10, 100)));
            var gesture = root.AddAnyStateTransition(unreachable); gesture.hasExitTime = false; gesture.duration = 0;
            gesture.AddCondition(AnimatorConditionMode.Equals, 2, "GestureRight");
            Assert.That(NeutralShapeSampler.Sample(avatar).Single(value => value.Shape == "Open").Weight, Is.EqualTo(100).Within(.01));
        }

        [Test]
        public void QuietVoicePrunesAncestorAnyStateWhileKeepingTheNestedNeutralState()
        {
            var root = controller.layers[0].stateMachine;
            var child = root.AddStateMachine("Nested face");
            child.defaultState = State(child, Clip("Open", AnimationCurve.Constant(0, 1, 100))); root.AddEntryTransition(child);
            controller.AddParameter("Voice", AnimatorControllerParameterType.Float);
            var externallyChosen = State(root, Clip("Open", AnimationCurve.Constant(0, 1, 0)));
            var voice = root.AddAnyStateTransition(externallyChosen); voice.hasExitTime = false; voice.duration = 0;
            voice.AddCondition(AnimatorConditionMode.Greater, .5f, "Voice");
            Assert.That(NeutralShapeSampler.Sample(avatar).Single(value => value.Shape == "Open").Weight, Is.EqualTo(100).Within(.01));
        }

        [Test]
        public void AutomaticBlinkWithWriteDefaultsKeepsTheFullNativeClosureAndConstantRest()
        {
            Open(); var blink = Layer("Blink with implicit writes");
            blink.defaultState = State(blink, Clip("Blink", AnimationCurve.Linear(0, 0, 10, 100)), writeDefaults: true);
            Assert.That(NativeOpeningWeight(), Is.EqualTo(100).Within(.01),
                "This original graph keeps Open at 100; Write Defaults alone does not establish a reset value or prove independence.");
            var metadata = VrChatExpressionMenu.Read(avatar, new VrChatMenuImportPolicy { SkipAll = true });
            var automaticRoot = new HashSet<EditorCurveBinding> { EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Blink") };
            var dependencies = ExpressionDependencies.AnalyzeNeutral(controller, automaticRoot, null, metadata,
                automaticRoot, FixedExpressionContext.Create(controller, metadata.Defaults, metadata), preserveCommittedMorphs: true);
            Assert.That(dependencies.Morphs, Is.EquivalentTo(automaticRoot), "The output capture itself is deliberately narrow.");
            Assert.That(dependencies.NeutralDependencyMorphs,
                Does.Contain(EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Open")),
                "Narrow capture must not erase the coupled automatic layer's full dependency closure.");
            var values = NeutralShapeSampler.Sample(avatar);
            Assert.That(values.Single(value => value.Shape == "Open").Weight, Is.EqualTo(100).Within(.01));
            Assert.That(values.Any(value => value.Shape == "Blink"), Is.False);
            Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
        }

        [Test]
        public void RelevantRandomDriverCannotInventANeutralFace()
        {
            controller.AddParameter("Face", AnimatorControllerParameterType.Int);
            var open = Open(); ParameterDriverExpressionTests.Driver(open, ParameterDriverExpressionTests.Op("Random", "Face"));
            var other = State(controller.layers[0].stateMachine, Clip("Open", AnimationCurve.Constant(0, 1, 0)));
            var transition = open.AddTransition(other); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "Face");
            Assert.That(Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar)).Message, Does.Contain("Random"));
        }

        [Test]
        public void OverrideClipAndWriteDefaultsUseThePreparedFxPose()
        {
            var original = (AnimationClip)Open(writeDefaults: true).motion;
            var replacement = Clip("Open", AnimationCurve.Constant(0, 1, 75));
            var overrides = new AnimatorOverrideController(controller);
            try
            {
                overrides.ApplyOverrides(new[] { new KeyValuePair<AnimationClip, AnimationClip>(original, replacement) });
                SetFx(overrides);
                Assert.That(NeutralShapeSampler.Sample(avatar).Single(value => value.Shape == "Open").Weight, Is.EqualTo(75).Within(.01));
                Assert.That(controller.layers[0].stateMachine.defaultState.motion, Is.SameAs(original));
            }
            finally { Object.DestroyImmediate(overrides); }
        }

        [Test]
        public void ExcludedRendererDoesNotContributeANeutralPose()
        {
            Open(); Assert.That(NeutralShapeSampler.Sample(avatar, path => path == "Body"), Is.Empty);
        }
    }
}
