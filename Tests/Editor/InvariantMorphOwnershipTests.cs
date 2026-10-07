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
    public sealed class InvariantMorphOwnershipTests
    {
        private string folder;
        private GameObject avatar, coat;
        private Mesh mesh;
        private AnimatorController controller;
        private AnimatorState off, on, face;
        private AnimationClip offClip, onClip, faceClip;
        private static readonly EditorCurveBinding Morph = EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size");

        [SetUp]
        public void SetUp()
        {
            var name = "__InvariantOwnership_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            avatar = new GameObject("Avatar", typeof(Animator));
            var renderer = new GameObject("Face", typeof(SkinnedMeshRenderer)).GetComponent<SkinnedMeshRenderer>();
            renderer.transform.SetParent(avatar.transform, false); mesh = BaseShapeFixture.Create(); renderer.sharedMesh = mesh;
            renderer.SetBlendShapeWeight(0, 20);
            coat = new GameObject("Coat"); coat.transform.SetParent(avatar.transform, false); coat.SetActive(false);
            controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            controller.AddParameter("Outfit", AnimatorControllerParameterType.Int);
            controller.AddParameter("DirectWeight", AnimatorControllerParameterType.Float);
            offClip = Clip("Outfit off and face reset", 30, false);
            onClip = Clip("Outfit on and same face reset", 30, true);
            var machine = controller.layers[0].stateMachine;
            off = State(machine, "Off", offClip); on = State(machine, "On", onClip); machine.defaultState = off;
            var transition = off.AddTransition(on); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "Outfit");
            controller.AddLayer("Selected authored face"); var layers = controller.layers; layers[1].defaultWeight = 1;
            faceClip = Clip("Selected face", 75); face = State(layers[1].stateMachine, "Face", faceClip);
            layers[1].stateMachine.defaultState = face; controller.layers = layers;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(avatar); Object.DestroyImmediate(mesh);
            if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        private AnimationClip Clip(string name, float weight, bool? active = null)
        {
            var clip = new AnimationClip { name = name }; AssetDatabase.AddObjectToAsset(clip, controller);
            AnimationUtility.SetEditorCurve(clip, Morph, AnimationCurve.Constant(0, 1, weight));
            if (active.HasValue) AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("Coat", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, active.Value ? 1 : 0));
            return clip;
        }

        private static AnimatorState State(AnimatorStateMachine machine, string name, Motion motion)
        {
            var state = machine.AddState(name); state.motion = motion; state.writeDefaultValues = false; return state;
        }

        private NeutralShapePlan Plan() => NeutralShapePlan.Create(avatar, controller, new[] { new[] { Morph } },
            source: new VrChatExpressionMenu.Source { Controller = controller },
            allowUnchangedAppearance: true);

        [Test]
        public void ExplicitIdenticalFaceResetsDoNotOwnTheChangingWardrobeOption()
        {
            var plan = Plan();
            Assert.That(plan.CommittedMorphs.Contains(Morph), Is.True);
            Assert.That(plan.PreservedMorphs.Contains(Morph), Is.False);
            Assert.That(plan.AllowsEvaluationBinding(onClip, EditorCurveBinding.FloatCurve("Coat", typeof(GameObject), "m_IsActive")), Is.True,
                "The native wardrobe layer must remain evaluation support.");
        }

        [Test]
        public void AuthoredPureMorphSurvivesAnInvariantWardrobeResetWithoutAFacialDescriptor()
        {
            // Independent witness: run the complete authored graph, rather
            // than the exporter's dependency/probe controller.
            var clone = Object.Instantiate(avatar); var graph = PlayableGraph.Create("Invariant reset native oracle");
            float expected;
            try
            {
                var animator = clone.GetComponent<Animator>(); animator.runtimeAnimatorController = null;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                AnimationPlayableOutput.Create(graph, "Face", animator).SetSourcePlayable(playable);
                graph.Play(); graph.Evaluate(0);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                expected = clone.transform.Find("Face").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(0);
                Assert.That(expected, Is.EqualTo(75).Within(.01));
            }
            finally { graph.Destroy(); Object.DestroyImmediate(clone); }
            var before = ExportSourceFingerprint.Compute(avatar); var controllerBefore = EditorJsonUtility.ToJson(controller);
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, faceClip, entry);
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1, false,
                metadata: new VrChatExpressionMenu.Source { Controller = controller }, sourceState: face);
            Assert.That(entry.Values.Single(value => value.Shape == "Face size").Weight, Is.EqualTo(expected).Within(.01));
            Assert.That(coat.activeSelf, Is.False); Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(controllerBefore));
        }

        [TestCase("varying")]
        [TestCase("missing with WD Off")]
        [TestCase("missing with WD On")]
        [TestCase("empty motion")]
        [TestCase("animated")]
        [TestCase("additive")]
        public void UnprovenResetAlternativesKeepTheirAppearanceOwnership(string kind)
        {
            if (kind == "varying") AnimationUtility.SetEditorCurve(onClip, Morph, AnimationCurve.Constant(0, 1, 55));
            else if (kind.StartsWith("missing", StringComparison.Ordinal))
            {
                AnimationUtility.SetEditorCurve(offClip, Morph, null); off.writeDefaultValues = kind == "missing with WD On";
            }
            else if (kind == "empty motion") off.motion = null;
            else if (kind == "animated") AnimationUtility.SetEditorCurve(offClip, Morph, AnimationCurve.Linear(0, 30, 1, 31));
            else if (kind == "additive")
            {
                var layers = controller.layers; layers[1].blendingMode = AnimatorLayerBlendingMode.Additive; controller.layers = layers;
            }
            Assert.That(Plan().PreservedMorphs.Contains(Morph), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SharedInputPreservesOnlyMorphsThatVaryAcrossTheirOwnAlternatives(bool varying)
        {
            // The appearance clips do not bind the face. Ownership here must
            // come through their shared custom input, not clip co-location.
            AnimationUtility.SetEditorCurve(offClip, Morph, null); AnimationUtility.SetEditorCurve(onClip, Morph, null);
            var machine = controller.layers[1].stateMachine;
            var alternate = State(machine, "Other face", Clip("Other face", varying ? 50 : 75));
            var transition = face.AddTransition(alternate); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "Outfit");
            Assert.That(Plan().PreservedMorphs.Contains(Morph), Is.EqualTo(varying));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BlendTreesNeedAConvexProofBeforeIdenticalLeavesAreInvariant(bool direct)
        {
            var tree = new BlendTree { name = "Wardrobe reset tree", blendType = direct ? BlendTreeType.Direct : BlendTreeType.Simple1D,
                blendParameter = "DirectWeight", useAutomaticThresholds = false };
            AssetDatabase.AddObjectToAsset(tree, controller);
            tree.children = new[] {
                new ChildMotion { motion = offClip, threshold = 0, directBlendParameter = "DirectWeight", timeScale = 1 },
                new ChildMotion { motion = onClip, threshold = 1, directBlendParameter = "DirectWeight", timeScale = 1 }
            };
            off.motion = tree; on.motion = tree;
            Assert.That(Plan().PreservedMorphs.Contains(Morph), Is.EqualTo(direct));
        }

        [Test]
        public void IdenticalKeysWithCurvedInteriorsAreNotInvariantResets()
        {
            AnimationUtility.SetEditorCurve(offClip, Morph, new AnimationCurve(new Keyframe(0, 30, 0, 3), new Keyframe(1, 30, -3, 0)));
            Assert.That(Plan().PreservedMorphs.Contains(Morph), Is.True);
        }

        [Test]
        public void ASingleMixedConfigurationRetainsItsPairedMorphOwnership()
        {
            var machine = controller.layers[0].stateMachine;
            machine.RemoveState(off); machine.defaultState = on;
            Assert.That(Plan().PreservedMorphs.Contains(Morph), Is.True,
                "A static outfit configuration does not prove that its paired morph is a generic reset.");
        }

        [Test]
        public void NativeLayerWeightCommandsKeepIdenticalResetsConservative()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly =>
                assembly.GetType("VRC.SDK3.Avatars.Components.VRCAnimatorLayerControl")).FirstOrDefault(value => value != null);
            if (type == null) Assert.Ignore("Install the real VRChat SDK to validate native weight controls.");
            var control = face.AddStateMachineBehaviour(type);
            using (var data = new SerializedObject(control))
            {
                var playable = data.FindProperty("playable"); playable.enumValueIndex = Array.IndexOf(playable.enumNames, "FX");
                data.FindProperty("layer").intValue = 0; data.FindProperty("goalWeight").floatValue = .5f;
                data.FindProperty("blendDuration").floatValue = 0; data.ApplyModifiedPropertiesWithoutUndo();
            }
            Assert.That(Plan().PreservedMorphs.Contains(Morph), Is.True);
        }
    }
}
