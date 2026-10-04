using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class DirectExpressionNativeLayersTests
    {
        private string folder;
        private GameObject avatar;
        private Mesh mesh;
        private AnimatorController controller;
        private AnimatorState lower, alternate, selected;

        [SetUp]
        public void SetUp()
        {
            var name = "__DirectNativeLayers_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            controller.AddParameter("Face", AnimatorControllerParameterType.Int);
            avatar = new GameObject("Avatar", typeof(Animator));
            var face = new GameObject("Face", typeof(SkinnedMeshRenderer)); face.transform.SetParent(avatar.transform, false);
            mesh = BaseShapeFixture.Create(); face.GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
            face.GetComponent<SkinnedMeshRenderer>().SetBlendShapeWeight(0, 20);
            var machine = controller.layers[0].stateMachine;
            lower = State(machine, "Default lower face", Clip("Lower default", 30)); machine.defaultState = lower;
            alternate = State(machine, "Alternate lower face", Clip("Lower alternate", 60));
            var transition = lower.AddTransition(alternate); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "Face");
            controller.AddLayer("Authored player"); var layers = controller.layers; layers[1].defaultWeight = .5f;
            selected = State(layers[1].stateMachine, "Authored expression", Clip("Selected", 80));
            layers[1].stateMachine.defaultState = selected; controller.layers = layers;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(avatar); Object.DestroyImmediate(mesh);
            if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        private AnimationClip Clip(string name, float weight)
        {
            var clip = new AnimationClip { name = name };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                AnimationCurve.Constant(0, 1, weight));
            AssetDatabase.AddObjectToAsset(clip, controller); return clip;
        }

        private static AnimatorState State(AnimatorStateMachine machine, string name, AnimationClip clip)
        {
            var state = machine.AddState(name); state.motion = clip; state.writeDefaultValues = false; return state;
        }

        // This reference plays the complete authored graph and its actual clips.
        // It does not reconstruct an exporter probe or use dependency analysis.
        private float Native(RuntimeAnimatorController runtime, IDictionary<string, float> defaults = null,
            Action<AnimatorControllerPlayable> observe = null)
        {
            var copy = Object.Instantiate(avatar); var graph = PlayableGraph.Create("Direct expression native reference");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            try
            {
                var animator = copy.GetComponent<Animator>(); animator.runtimeAnimatorController = null;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.applyRootMotion = false; animator.fireEvents = false;
                var playable = AnimatorControllerPlayable.Create(graph, runtime);
                var authored = runtime as AnimatorController ?? (runtime as AnimatorOverrideController)?.runtimeAnimatorController as AnimatorController;
                Assert.That(authored, Is.Not.Null);
                var nativeLayers = authored.layers;
                for (var index = 0; index < nativeLayers.Length; index++)
                    playable.SetLayerWeight(index, index == 0 ? 1 : nativeLayers[index].defaultWeight);
                AnimationPlayableOutput.Create(graph, "Face", animator).SetSourcePlayable(playable);
                if (defaults != null)
                    foreach (var pair in defaults)
                    {
                        var type = authored.parameters.Single(parameter => parameter.name == pair.Key).type;
                        if (type == AnimatorControllerParameterType.Bool) playable.SetBool(pair.Key, pair.Value != 0);
                        else if (type == AnimatorControllerParameterType.Int) playable.SetInteger(pair.Key, Mathf.RoundToInt(pair.Value));
                        else if (type == AnimatorControllerParameterType.Float) playable.SetFloat(pair.Key, pair.Value);
                        else Assert.Fail("The native counterfactual cannot set a trigger.");
                    }
                graph.Play(); graph.Evaluate(0);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                Assert.That(playable.IsInTransition(0), Is.False);
                observe?.Invoke(playable);
                return copy.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0);
            }
            finally { if (graph.IsValid()) graph.Destroy(); Object.DestroyImmediate(copy); }
        }

        private static int ProbeObjects() => Resources.FindObjectsOfTypeAll<Object>().Count(value => value != null &&
            value.hideFlags == HideFlags.HideAndDontSave &&
            (value.name == "VRVlog expression override probe" || value.name == "VRVlog expression effective override probe"));

        private void AssertUnchanged(string sourceJson, string meshJson, int probes)
        {
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(sourceJson));
            Assert.That(EditorJsonUtility.ToJson(mesh), Is.EqualTo(meshJson));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(20));
            Assert.That(ProbeObjects(), Is.EqualTo(probes), "Both successful and rejected probes must dispose their private controllers and clips.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FractionalPlayerBlendsWithTheEvaluatedLowerDefaultAcrossPossibleTransitions(bool writeDefaults)
        {
            selected.writeDefaultValues = writeDefaults;
            var expected = Native(controller);
            Assert.That(expected, Is.EqualTo(55).Within(.01), "The authored lower 30 and selected 80 at weight .5 must blend natively.");
            Assert.That(ExpressionDependencies.StationaryLayers(controller).ContainsKey(0), Is.False,
                "The lower graph has multiple states and a transition, so a stationary-clip-only reconstruction loses it.");
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1, writeDefaults);
            Assert.That(entry.Values.Single(value => value.Shape == "Face size").Weight, Is.EqualTo(expected).Within(.01));
            AssertUnchanged(sourceJson, meshJson, probes);
        }

        [Test]
        public void EffectiveOverrideClipsSurviveThePrivateFullGraphProbe()
        {
            var replacementLower = Clip("Effective lower", 50); var replacementSelected = Clip("Effective selected", 90);
            var runtime = new AnimatorOverrideController(controller);
            runtime[(AnimationClip)lower.motion] = replacementLower; runtime[(AnimationClip)selected.motion] = replacementSelected;
            AssetDatabase.CreateAsset(runtime, folder + "/Effective.overrideController");
            var expected = Native(runtime); Assert.That(expected, Is.EqualTo(70).Within(.01));
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, replacementSelected, entry);
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var overrideJson = EditorJsonUtility.ToJson(runtime);
            var probes = ProbeObjects();
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, runtime, entry, 1);
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(expected).Within(.01));
            Assert.That(EditorJsonUtility.ToJson(runtime), Is.EqualTo(overrideJson));
            AssertUnchanged(sourceJson, meshJson, probes);
        }

        [Test]
        public void ExpressionParameterDefaultsSelectTheDeclaredLowerPose()
        {
            var metadata = new VrChatExpressionMenu.Source { Controller = controller }; metadata.Defaults.Add("Face", 1);
            var expected = Native(controller, metadata.Defaults); Assert.That(expected, Is.EqualTo(70).Within(.01));
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1, metadata: metadata);
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(expected).Within(.01));
            AssertUnchanged(sourceJson, meshJson, probes);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InactiveAuthoredSlotsKeepTheirDirectClipCandidate(bool synced)
        {
            var layers = controller.layers; layers[1].defaultWeight = 0;
            if (synced) layers[1].syncedLayerIndex = 0;
            controller.layers = layers;
            if (synced) controller.SetStateEffectiveMotion(lower, selected.motion, 1);
            Assert.That(Native(controller), Is.EqualTo(30).Within(.01), "The candidate slot is inactive in the native default graph.");
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1);
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(80), "A direct clip candidate keeps its authored pose even when its FX slot is initially inactive.");
            AssertUnchanged(sourceJson, meshJson, probes);
        }

        [Test]
        public void ActiveFractionalSyncedSlotKeepsItsEffectiveMotionAndControllerReplacement()
        {
            var layers = controller.layers; layers[1].syncedLayerIndex = 0; controller.layers = layers;
            controller.SetStateEffectiveMotion(lower, selected.motion, 1);
            var replacementLower = Clip("Effective synced lower", 50); var replacementSelected = Clip("Effective synced expression", 90);
            var runtime = new AnimatorOverrideController(controller);
            runtime[(AnimationClip)lower.motion] = replacementLower; runtime[(AnimationClip)selected.motion] = replacementSelected;
            AssetDatabase.CreateAsset(runtime, folder + "/EffectiveSynced.overrideController");
            var expected = Native(runtime); Assert.That(expected, Is.EqualTo(70).Within(.01));
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, replacementSelected, entry);
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var overrideJson = EditorJsonUtility.ToJson(runtime);
            var probes = ProbeObjects();
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, runtime, entry, 1);
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(expected).Within(.01));
            Assert.That(EditorJsonUtility.ToJson(runtime), Is.EqualTo(overrideJson));
            AssertUnchanged(sourceJson, meshJson, probes);
        }

        [TestCase("state")]
        [TestCase("machine")]
        [TestCase("nested machine")]
        public void DroppedSelectedDriversCannotSilentlyChooseARetainedLowerPose(string placement)
        {
            var driver = ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "Face", 1));
            var machine = controller.layers[1].stateMachine;
            if (placement != "state")
            {
                selected.behaviours = Array.Empty<StateMachineBehaviour>();
                if (placement == "nested machine") machine = machine.AddStateMachine("Registered alternatives");
                machine.behaviours = new[] { driver };
            }
            Assert.That(VrChatParameterDriver.Read(driver, "Authored player").Operations.Single().Destination, Is.EqualTo("Face"));
            // Native playback at the driver's declared target value shows that
            // this destination changes the retained lower pose. SDK callbacks
            // are not simulated or installed on their process-wide delegates.
            Assert.That(Native(controller, new Dictionary<string, float> { ["Face"] = 1 }), Is.EqualTo(70).Within(.01));
            var sourceAssets = AssetDatabase.LoadAllAssetsAtPath(folder + "/FX.controller").ToDictionary(asset => asset, asset => EditorJsonUtility.ToJson(asset));
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1));
            Assert.That(error.Message, Does.Contain("Parameter Driver").And.Contain("Face"));
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(80), "Rejected selected callbacks cannot partially rewrite a registered clip.");
            foreach (var pair in sourceAssets) Assert.That(EditorJsonUtility.ToJson(pair.Key), Is.EqualTo(pair.Value), pair.Key.name);
            AssertUnchanged(sourceJson, meshJson, probes);
        }

        [Test]
        public void UnrelatedSelectedDriverDestinationDoesNotBlockTheNativeProbe()
        {
            controller.AddParameter("Unused driver destination", AnimatorControllerParameterType.Int);
            var driver = ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "Unused driver destination", 1));
            var sourceAssets = AssetDatabase.LoadAllAssetsAtPath(folder + "/FX.controller").ToDictionary(asset => asset, asset => EditorJsonUtility.ToJson(asset));
            var expected = Native(controller); Assert.That(expected, Is.EqualTo(55).Within(.01));
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1);
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(expected).Within(.01));
            Assert.That(selected.behaviours, Does.Contain(driver));
            foreach (var pair in sourceAssets) Assert.That(EditorJsonUtility.ToJson(pair.Key), Is.EqualTo(pair.Value), pair.Key.name);
            AssertUnchanged(sourceJson, meshJson, probes);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SelectedSyncedSlotChecksEffectiveOverrideDriversWithoutChangingSourceBehaviours(bool relevant)
        {
            controller.AddParameter("Unused driver destination", AnimatorControllerParameterType.Int);
            var driver = ParameterDriverExpressionTests.Driver(selected,
                ParameterDriverExpressionTests.Op("Set", relevant ? "Face" : "Unused driver destination", 1));
            selected.behaviours = Array.Empty<StateMachineBehaviour>();
            var layers = controller.layers; layers[1].syncedLayerIndex = 0; controller.layers = layers;
            controller.SetStateEffectiveMotion(lower, selected.motion, 1);
            controller.SetStateEffectiveMotion(alternate, selected.motion, 1);
            controller.SetStateEffectiveBehaviours(lower, 1, new[] { driver });
            Assert.That(lower.behaviours, Is.Empty, "The driver exists only in the selected synced slot's effective override.");
            Assert.That(controller.GetStateEffectiveBehaviours(lower, 1), Is.EqualTo(new[] { driver }));
            var expected = relevant ? Native(controller, new Dictionary<string, float> { ["Face"] = 1 }) : Native(controller);
            Assert.That(expected, Is.EqualTo(relevant ? 70 : 55).Within(.01),
                "Relevant callbacks are checked through their declared target-value counterfactual, without invoking SDK client delegates.");
            var sourceAssets = AssetDatabase.LoadAllAssetsAtPath(folder + "/FX.controller").ToDictionary(asset => asset, asset => EditorJsonUtility.ToJson(asset));
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            if (relevant)
            {
                var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1));
                Assert.That(error.Message, Does.Contain("Parameter Driver").And.Contain("Face"));
                Assert.That(entry.Values.Single().Weight, Is.EqualTo(80));
            }
            else
            {
                VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1);
                Assert.That(entry.Values.Single().Weight, Is.EqualTo(expected).Within(.01));
            }
            Assert.That(lower.behaviours, Is.Empty);
            Assert.That(controller.GetStateEffectiveBehaviours(lower, 1), Is.EqualTo(new[] { driver }));
            foreach (var pair in sourceAssets) Assert.That(EditorJsonUtility.ToJson(pair.Key), Is.EqualTo(pair.Value), pair.Key.name);
            AssertUnchanged(sourceJson, meshJson, probes);
        }

        [Test]
        public void RetainedSyncedOverrideDriverCannotBePrunedWithItsUnrelatedMotion()
        {
            var driver = ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "Face", 1));
            selected.behaviours = Array.Empty<StateMachineBehaviour>();
            var blink = new AnimationClip { name = "Unrelated retained blink" };
            AnimationUtility.SetEditorCurve(blink, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink"),
                AnimationCurve.Constant(0, 1, 100));
            AssetDatabase.AddObjectToAsset(blink, controller);
            controller.AddLayer("Retained synced blink"); var layers = controller.layers;
            layers[2].defaultWeight = 1; layers[2].syncedLayerIndex = 0; controller.layers = layers;
            controller.SetStateEffectiveMotion(lower, blink, 2); controller.SetStateEffectiveMotion(alternate, blink, 2);
            controller.SetStateEffectiveBehaviours(lower, 2, new[] { driver });
            Assert.That(lower.behaviours, Is.Empty);
            Assert.That(controller.GetStateEffectiveBehaviours(lower, 2), Is.EqualTo(new[] { driver }));
            // Face=1 is the declared callback result. Its native lower pose
            // changes the fractional player's output even though the retained
            // synced layer's own motion writes only the independent blink.
            Assert.That(Native(controller, new Dictionary<string, float> { ["Face"] = 1 }), Is.EqualTo(70).Within(.01));
            var sourceAssets = AssetDatabase.LoadAllAssetsAtPath(folder + "/FX.controller").ToDictionary(asset => asset, asset => EditorJsonUtility.ToJson(asset));
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1));
            Assert.That(error.Message, Does.Contain("同期Animatorレイヤー"), "Effective driver dependencies must reach the existing unsupported-sync guard before playback.");
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(80));
            Assert.That(lower.behaviours, Is.Empty);
            Assert.That(controller.GetStateEffectiveBehaviours(lower, 2), Is.EqualTo(new[] { driver }));
            foreach (var pair in sourceAssets) Assert.That(EditorJsonUtility.ToJson(pair.Key), Is.EqualTo(pair.Value), pair.Key.name);
            AssertUnchanged(sourceJson, meshJson, probes);
        }

        [Test]
        public void FixedDirectProbeCannotPinAGateWrittenByItsDroppedSelectedDriver()
        {
            var driver = ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "Face", 1));
            Assert.That(VrChatParameterDriver.Read(driver, "Authored player").Operations.Single().Destination, Is.EqualTo("Face"));
            Assert.That(Native(controller), Is.EqualTo(55).Within(.01));
            Assert.That(Native(controller, new Dictionary<string, float> { ["Face"] = 1 }), Is.EqualTo(70).Within(.01),
                "The original driver's declared value changes the actual retained native lower pose.");
            var metadata = new VrChatExpressionMenu.Source { Controller = controller };
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var sourceAssets = DirectProbeSourceAssets();
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            var error = Assert.Throws<InvalidOperationException>(() =>
                VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar, controller, entry, 1, metadata: metadata));
            Assert.That(error.Message, Does.Contain("Parameter Driver").And.Contain("Face"));
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(80), "A dropped callback must be diagnosed before rewriting the direct expression.");
            Assert.That(selected.behaviours, Does.Contain(driver));
            AssertDirectProbeSourceAssets(sourceAssets); AssertUnchanged(sourceJson, meshJson, probes);
        }

        [Test]
        public void FixedDirectProbeStillAllowsAnUnrelatedDroppedDriverDestination()
        {
            controller.AddParameter("Unused driver destination", AnimatorControllerParameterType.Int);
            var driver = ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "Unused driver destination", 1));
            var expected = Native(controller); Assert.That(expected, Is.EqualTo(55).Within(.01));
            var metadata = new VrChatExpressionMenu.Source { Controller = controller };
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var sourceAssets = DirectProbeSourceAssets();
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar, controller, entry, 1, metadata: metadata);
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(expected).Within(.01));
            Assert.That(selected.behaviours, Does.Contain(driver));
            AssertDirectProbeSourceAssets(sourceAssets); AssertUnchanged(sourceJson, meshJson, probes);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FixedNativeSlotValidatesRetainedFxDisableAgainstItsActualNormalGate(bool reachable)
        {
            var control = RetainedFxDisable(reachable);
            Assert.That(VrChatParameterDriver.ReadInstantFxControl(control, "Retained control", out var program), Is.True);
            Assert.That(program.FxWeight, Is.Zero);
            var metadata = new VrChatExpressionMenu.Source { Controller = controller };
            metadata.Defaults["AFK"] = 1;
            var context = FixedExpressionContext.Create(controller, metadata.Defaults, metadata);
            Assert.That(context.Values["AFK"], Is.Zero, "The explicit normal environment wins over an authored AFK default.");
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var expected = reachable ? 0 : Native(controller);
            if (!reachable) Assert.That(expected, Is.EqualTo(55).Within(.01));
            var sourceAssets = DirectProbeSourceAssets();
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            if (reachable)
            {
                var error = Assert.Throws<InvalidOperationException>(() =>
                    VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar, controller, entry, 1, metadata: metadata));
                Assert.That(error.Message, Does.Contain("FX"));
                Assert.That(entry.Values.Single().Weight, Is.EqualTo(80));
            }
            else
            {
                VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar, controller, entry, 1, metadata: metadata);
                Assert.That(entry.Values.Single().Weight, Is.EqualTo(expected).Within(.01),
                    "A retained command behind the false normal AFK gate cannot erase the selectable native expression.");
            }
            AssertDirectProbeSourceAssets(sourceAssets); AssertUnchanged(sourceJson, meshJson, probes);
        }

        [Test]
        public void FixedNativeSlotCannotDropItsSelectedNonUnitFxCommand()
        {
            var control = FxDisable(selected);
            Assert.That(VrChatParameterDriver.ReadInstantFxControl(control, "Selected slot", out var program), Is.True);
            Assert.That(program.FxWeight, Is.Zero);
            var metadata = new VrChatExpressionMenu.Source { Controller = controller };
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var sourceAssets = DirectProbeSourceAssets();
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            var error = Assert.Throws<InvalidOperationException>(() =>
                VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar, controller, entry, 1, metadata: metadata));
            Assert.That(error.Message, Does.Contain("FX"));
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(80));
            Assert.That(selected.behaviours, Does.Contain(control));
            AssertDirectProbeSourceAssets(sourceAssets); AssertUnchanged(sourceJson, meshJson, probes);
        }

        [TestCase("raw")]
        [TestCase("standalone")]
        [TestCase("inactive slot")]
        public void ReducedOrRawDirectRoutesKeepTheirStrictFxCommandContract(string route)
        {
            RetainedFxDisable(false);
            if (route == "inactive slot")
            { var layers = controller.layers; layers[1].defaultWeight = 0; controller.layers = layers; }
            var metadata = new VrChatExpressionMenu.Source { Controller = controller };
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var sourceAssets = DirectProbeSourceAssets();
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            var error = Assert.Throws<InvalidOperationException>(() => {
                if (route == "raw") VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1, metadata: metadata);
                else VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar, controller, entry,
                    route == "standalone" ? (int?)null : 1, metadata: metadata);
            });
            Assert.That(error.Message, Does.Contain("FX"));
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(80));
            AssertDirectProbeSourceAssets(sourceAssets); AssertUnchanged(sourceJson, meshJson, probes);
        }

        [TestCase(0)]
        [TestCase(1)]
        public void KnownSelectedStateSetUsesItsNativeLowerPoseInsteadOfAnotherStatesDriver(int value)
        {
            var player = controller.layers[1].stateMachine;
            var unselected = State(player, "Different default callback", (AnimationClip)selected.motion);
            player.defaultState = unselected;
            ParameterDriverExpressionTests.Driver(unselected, ParameterDriverExpressionTests.Op("Set", "Face", 1 - value));
            var driver = ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "Face", value));
            var expected = Native(controller, new Dictionary<string, float> { ["Face"] = value });
            Assert.That(expected, Is.EqualTo(value == 0 ? 55 : 70).Within(.01));
            var sourceAssets = DirectProbeSourceAssets();
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar, controller, entry, 1,
                metadata: new VrChatExpressionMenu.Source { Controller = controller }, selectedState: selected, selectedClip: (AnimationClip)selected.motion);
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(expected).Within(.01));
            Assert.That(selected.behaviours, Does.Contain(driver));
            Assert.That(player.defaultState, Is.SameAs(unselected));
            AssertDirectProbeSourceAssets(sourceAssets); AssertUnchanged(sourceJson, meshJson, probes);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void KnownSelectedBoolSetIsEvaluatedThroughTheNativeLowerGate(bool enabled)
        {
            var parameters = controller.parameters;
            parameters.Single(parameter => parameter.name == "Face").type = AnimatorControllerParameterType.Bool;
            controller.parameters = parameters;
            var transition = lower.transitions.Single();
            transition.conditions = Array.Empty<AnimatorCondition>(); transition.AddCondition(AnimatorConditionMode.If, 0, "Face");
            ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "Face", enabled ? 1 : 0));
            var expected = Native(controller, new Dictionary<string, float> { ["Face"] = enabled ? 1 : 0 });
            Assert.That(expected, Is.EqualTo(enabled ? 70 : 55).Within(.01));
            var sourceAssets = DirectProbeSourceAssets();
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar, controller, entry, 1,
                metadata: new VrChatExpressionMenu.Source { Controller = controller }, selectedState: selected);
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(expected).Within(.01));
            AssertDirectProbeSourceAssets(sourceAssets); AssertUnchanged(sourceJson, meshJson, probes);
        }

        [TestCase("equivalent")]
        [TestCase("conflicting")]
        [TestCase("same-name-copy")]
        public void RegisteredClipIdentityRequiresEquivalentNativeStateSetPrograms(string kind)
        {
            var clip = (AnimationClip)selected.motion;
            ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "Face", 1));
            var other = State(controller.layers[1].stateMachine, "Another registered state",
                kind == "same-name-copy" ? Clip(clip.name, 80) : clip);
            ParameterDriverExpressionTests.Driver(other, ParameterDriverExpressionTests.Op("Set", "Face", kind == "equivalent" ? 1 : 0));
            Assert.That(other.motion.name, Is.EqualTo(clip.name));
            Assert.That(other.motion == clip, Is.EqualTo(kind != "same-name-copy"));
            var expected = Native(controller, new Dictionary<string, float> { ["Face"] = 1 });
            Assert.That(expected, Is.EqualTo(70).Within(.01));
            var sourceAssets = DirectProbeSourceAssets();
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, clip, entry);
            void Apply() => VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar, controller, entry, 1,
                metadata: new VrChatExpressionMenu.Source { Controller = controller }, selectedClip: clip);
            if (kind == "conflicting")
            {
                Assert.That(Assert.Throws<InvalidOperationException>(Apply).Message, Does.Contain("Parameter Driver").And.Contain("Face"));
                Assert.That(entry.Values.Single().Weight, Is.EqualTo(80), "Ambiguous callbacks cannot partially rewrite the registered clip.");
            }
            else
            {
                Apply(); Assert.That(entry.Values.Single().Weight, Is.EqualTo(expected).Within(.01));
            }
            AssertDirectProbeSourceAssets(sourceAssets); AssertUnchanged(sourceJson, meshJson, probes);
        }

        [TestCase("Add")]
        [TestCase("Copy")]
        [TestCase("Random")]
        public void KnownSelectedStateDoesNotTurnOtherDriverOperationsIntoSetProof(string operation)
        {
            ParameterDriverExpressionTests.Driver(selected,
                ParameterDriverExpressionTests.Op(operation, "Face", 1, source: "Face"));
            var sourceAssets = DirectProbeSourceAssets();
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar, controller,
                entry, 1, metadata: new VrChatExpressionMenu.Source { Controller = controller }, selectedState: selected));
            Assert.That(error.Message, Does.Contain("Parameter Driver").And.Contain("Face"));
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(80));
            AssertDirectProbeSourceAssets(sourceAssets); AssertUnchanged(sourceJson, meshJson, probes);
        }

        [Test]
        public void KnownSelectedStateMalformedSetCannotBecomeAValidNativeProbe()
        {
            var driver = ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "Face", float.NaN));
            Assert.That(VrChatParameterDriver.Read(driver, selected.name).Operations.Single().Error, Is.Not.Null);
            var sourceAssets = DirectProbeSourceAssets();
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar, controller,
                entry, 1, metadata: new VrChatExpressionMenu.Source { Controller = controller }, selectedState: selected));
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(80));
            AssertDirectProbeSourceAssets(sourceAssets); AssertUnchanged(sourceJson, meshJson, probes);
        }

        [Test]
        public void KnownSelectedStateCannotHideAnOmittedAncestorDriverToTheSameParameter()
        {
            ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "Face", 1));
            var carrier = State(controller.layers[1].stateMachine, "Ancestor callback data", (AnimationClip)selected.motion);
            var ancestor = ParameterDriverExpressionTests.Driver(carrier, ParameterDriverExpressionTests.Op("Set", "Face", 0));
            carrier.behaviours = Array.Empty<StateMachineBehaviour>();
            controller.layers[1].stateMachine.behaviours = new[] { ancestor };
            var sourceAssets = DirectProbeSourceAssets();
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            Assert.That(Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar, controller,
                entry, 1, metadata: new VrChatExpressionMenu.Source { Controller = controller }, selectedState: selected)).Message,
                Does.Contain("Parameter Driver").And.Contain("Face"));
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(80));
            AssertDirectProbeSourceAssets(sourceAssets); AssertUnchanged(sourceJson, meshJson, probes);
        }

        [Test]
        public void ExplicitStateIdentityDoesNotChangeTheRawDirectProbeContract()
        {
            ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "Face", 1));
            var sourceAssets = DirectProbeSourceAssets();
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            Assert.That(Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller,
                entry, 1, selectedState: selected)).Message, Does.Contain("Parameter Driver").And.Contain("Face"));
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(80));
            AssertDirectProbeSourceAssets(sourceAssets); AssertUnchanged(sourceJson, meshJson, probes);
        }

        private StateMachineBehaviour RetainedFxDisable(bool reachable)
        {
            controller.AddParameter("AFK", AnimatorControllerParameterType.Bool);
            controller.AddLayer("Retained FX control"); var layers = controller.layers;
            layers[2].defaultWeight = 1; controller.layers = layers;
            var empty = new AnimationClip { name = "Retained control idle" }; AssetDatabase.AddObjectToAsset(empty, controller);
            var machine = layers[2].stateMachine;
            var idle = State(machine, "No FX change", empty); machine.defaultState = idle;
            var disabled = State(machine, "Disable FX", empty);
            var control = FxDisable(disabled);
            var transition = idle.AddTransition(disabled); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(reachable ? AnimatorConditionMode.IfNot : AnimatorConditionMode.If, 0, "AFK");
            return control;
        }

        private static StateMachineBehaviour FxDisable(AnimatorState state)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly =>
                assembly.GetType("VRC.SDK3.Avatars.Components.VRCPlayableLayerControl")).FirstOrDefault(value => value != null);
            if (type == null) Assert.Ignore("Install the real VRChat SDK.");
            var control = state.AddStateMachineBehaviour(type);
            using (var data = new SerializedObject(control))
            {
                var layer = data.FindProperty("layer"); layer.enumValueIndex = Array.IndexOf(layer.enumNames, "FX");
                data.FindProperty("goalWeight").floatValue = 0;
                data.FindProperty("blendDuration").floatValue = 0;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            return control;
        }

        [TestCase("transform")]
        [TestCase("object")]
        [TestCase("event")]
        public void FixedNativeSupportIgnoresOnlyProvenUnreachableUnsupportedMotion(string kind)
        {
            SupportGraph(kind);
            var expected = Native(controller);
            Assert.That(float.IsNaN(expected) || float.IsInfinity(expected), Is.False);
            var metadata = new VrChatExpressionMenu.Source { Controller = controller };
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var sourceAssets = DirectProbeSourceAssets();
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar, controller, entry, 1, metadata: metadata);
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(expected).Within(.01),
                "The independent native base is retained; only the false normal AFK branch is outside support validation.");
            AssertDirectProbeSourceAssets(sourceAssets); AssertUnchanged(sourceJson, meshJson, probes);
        }

        [TestCase("raw")]
        [TestCase("mutable")]
        [TestCase("reachable")]
        [TestCase("transient")]
        [TestCase("unknown")]
        [TestCase("uncertain-input")]
        public void NativeSupportRetainsUnsupportedMotionAndCallbackSafeguards(string kind)
        {
            SupportGraph("transform");
            if (kind == "raw" || kind == "mutable")
            {
                controller.AddParameter("SupportGate", AnimatorControllerParameterType.Bool);
                lower.transitions[0].conditions = new[] { new AnimatorCondition { parameter = "SupportGate", mode = AnimatorConditionMode.If } };
            }
            if (kind == "mutable")
                ParameterDriverExpressionTests.Driver(lower, ParameterDriverExpressionTests.Op("Set", "SupportGate", 1));
            if (kind == "reachable")
                lower.transitions[0].conditions = new[] { new AnimatorCondition { parameter = "AFK", mode = AnimatorConditionMode.IfNot } };
            if (kind == "transient")
            {
                lower.transitions[0].conditions = Array.Empty<AnimatorCondition>();
                var returned = State(controller.layers[0].stateMachine, "Returned support", (AnimationClip)lower.motion);
                var exit = alternate.AddTransition(returned); exit.hasExitTime = false; exit.duration = 0;
                Assert.That(lower.transitions[0].duration, Is.Zero);
                Assert.That(exit.conditions, Is.Empty);
            }
            if (kind == "unknown")
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAnimatorLayerControl"))
                    .FirstOrDefault(value => value != null);
                if (type == null) Assert.Ignore("Install the real VRChat SDK.");
                lower.AddStateMachineBehaviour(type);
            }
            if (kind == "uncertain-input")
            {
                controller.AddParameter("EyeHeightAsMeters", AnimatorControllerParameterType.Float);
                lower.transitions[0].conditions = new[] { new AnimatorCondition { parameter = "EyeHeightAsMeters",
                    mode = AnimatorConditionMode.Greater, threshold = .5f } };
            }
            var metadata = new VrChatExpressionMenu.Source { Controller = controller };
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var sourceAssets = DirectProbeSourceAssets();
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            var error = Assert.Throws<InvalidOperationException>(() =>
            {
                if (kind == "raw") VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1, metadata: metadata);
                else VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar, controller, entry, 1, metadata: metadata);
            });
            Assert.That(error.Message, Does.Contain(kind == "unknown" ? "VRCAnimatorLayerControl" :
                kind == "uncertain-input" ? "EyeHeightAsMeters" : "BlendShape以外"));
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(80));
            AssertDirectProbeSourceAssets(sourceAssets); AssertUnchanged(sourceJson, meshJson, probes);
        }

        private void SupportGraph(string unsupported)
        {
            controller.AddParameter("AFK", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Support constant", AnimatorControllerParameterType.Float);
            var clip = new AnimationClip { name = "Native parameter base" };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "Support constant"), AnimationCurve.Constant(0, 1, 0));
            AssetDatabase.AddObjectToAsset(clip, controller); lower.motion = clip;
            var blocked = new AnimationClip { name = "Unreachable support motion" };
            if (unsupported == "object")
                AnimationUtility.SetObjectReferenceCurve(blocked, EditorCurveBinding.PPtrCurve("Face", typeof(SkinnedMeshRenderer), "m_Mesh"),
                    new[] { new ObjectReferenceKeyframe { time = 0, value = mesh } });
            else if (unsupported == "event")
                AnimationUtility.SetAnimationEvents(blocked, new[] { new AnimationEvent { time = 0, functionName = "FutureCallback" } });
            else AnimationUtility.SetEditorCurve(blocked, EditorCurveBinding.FloatCurve("Face", typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Constant(0, 1, .25f));
            AssetDatabase.AddObjectToAsset(blocked, controller); alternate.motion = blocked;
            lower.transitions = Array.Empty<AnimatorStateTransition>();
            var transition = lower.AddTransition(alternate); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.If, 0, "AFK");
            Assert.That(ExpressionDependencies.StationaryLayers(controller).ContainsKey(0), Is.False);
            Assert.That(AnimationUtility.GetCurveBindings(clip).All(binding => binding.type == typeof(Animator)), Is.True);
        }

        [TestCase("menu")]
        [TestCase("direct")]
        public void FixedFaceRetainsNativeMathWithAnImplicitAncillaryEmptyClipLayer(string route)
        {
            AncillaryGraph();
            var metadata = new VrChatExpressionMenu.Source { Controller = controller }; metadata.Defaults["Face"] = 0;
            var assets = DirectProbeSourceAssets(); var sourceJson = EditorJsonUtility.ToJson(controller);
            var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            var expected = Native(controller, new Dictionary<string, float> { ["Face"] = route == "menu" ? 1 : 0 });
            Assert.That(float.IsNaN(expected) || float.IsInfinity(expected), Is.False);
            if (route == "menu")
            {
                var values = VrChatExpressionSampler.SampleFixed(avatar, controller, metadata.Defaults,
                    new Dictionary<string, float> { ["Face"] = 1 }, metadata: metadata);
                Assert.That(values.Single().Weight, Is.EqualTo(expected).Within(.01));
            }
            else
            {
                var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
                VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar, controller, entry, 1, metadata: metadata);
                Assert.That(entry.Values.Single().Weight, Is.EqualTo(expected).Within(.01));
            }
            Assert.That(metadata.Messages.Any(message => message.Contains("表示・Transform")), Is.True);
            Assert.That(avatar.transform.Find("Ear").gameObject.activeSelf, Is.True);
            Assert.That(avatar.transform.Find("Ear").localPosition, Is.EqualTo(Vector3.zero));
            AssertDirectProbeSourceAssets(assets); AssertUnchanged(sourceJson, meshJson, probes);
        }

        [TestCase("raw")]
        [TestCase("captured")]
        [TestCase("ancestor")]
        [TestCase("selected-root")]
        [TestCase("callback")]
        public void AncillaryExceptionRetainsRawCapturedSelectedAndCallbackBoundaries(string kind)
        {
            var clip = AncillaryGraph(kind == "captured" ? "Face" : kind == "ancestor" ? "" : "Ear");
            if (kind == "selected-root")
            {
                var transition = controller.layers[2].stateMachine.defaultState.transitions.Single();
                transition.AddCondition(AnimatorConditionMode.Equals, 1, "Face");
            }
            if (kind == "callback")
                AnimationUtility.SetAnimationEvents(clip, new[] { new AnimationEvent { time = 0, functionName = "FutureCallback" } });
            var metadata = new VrChatExpressionMenu.Source { Controller = controller }; metadata.Defaults["Face"] = 0;
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var assets = DirectProbeSourceAssets(); var sourceJson = EditorJsonUtility.ToJson(controller);
            var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            var error = Assert.Throws<InvalidOperationException>(() =>
            {
                if (kind == "raw") VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1, metadata: metadata);
                else if (kind == "selected-root") VrChatExpressionSampler.SampleFixed(avatar, controller, metadata.Defaults,
                    new Dictionary<string, float> { ["Face"] = 1 }, metadata: metadata);
                else VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar, controller, entry, 1, metadata: metadata);
            });
            Assert.That(error.Message, Does.Contain(kind == "callback" ? "影響範囲" : "BlendShape以外"));
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(80));
            AssertDirectProbeSourceAssets(assets); AssertUnchanged(sourceJson, meshJson, probes);
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void AncillaryNativeCurveNumbersRemainFatalBeforeNativeEvaluation(float number)
        {
            var clip = AncillaryGraph();
            var binding = EditorCurveBinding.FloatCurve("Ear", typeof(Transform), "m_LocalPosition.x");
            AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, 1, number));
            Assert.That(AnimationUtility.GetCurveBindings(clip).Contains(binding), Is.True,
                "The native clip must retain the declared float binding.");
            var retained = AnimationUtility.GetEditorCurve(clip, binding);
            Assert.That(retained == null || retained.length == 0 || retained.keys.Any(key => float.IsNaN(key.value) || float.IsInfinity(key.value)), Is.True);
            var metadata = new VrChatExpressionMenu.Source { Controller = controller };
            var assets = DirectProbeSourceAssets(); var sourceJson = EditorJsonUtility.ToJson(controller);
            var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleFixed(avatar, controller,
                new Dictionary<string, float> { ["Face"] = 0 }, new Dictionary<string, float> { ["Face"] = 1 }, metadata: metadata));
            Assert.That(error.Message, Does.Contain("不正な値"));
            Assert.That(metadata.Messages, Is.Empty);
            AssertDirectProbeSourceAssets(assets); AssertUnchanged(sourceJson, meshJson, probes);
        }

        [TestCase("unrelated")]
        [TestCase("unreachable")]
        [TestCase("excluded")]
        public void FixedFaceExcludesSafelyOmittedMalformedNativeClipData(string kind)
        {
            AncillaryGraph();
            var expected = Native(controller, new Dictionary<string, float> { ["Face"] = 1 });
            var bad = new AnimationClip { name = "Malformed omitted motion" };
            var binding = EditorCurveBinding.FloatCurve("Ear", typeof(Transform), "m_LocalPosition.x");
            AnimationUtility.SetEditorCurve(bad, binding, AnimationCurve.Constant(0, 1, float.NaN));
            AnimationUtility.SetEditorCurve(bad, EditorCurveBinding.FloatCurve("Ear", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1));
            AssetDatabase.AddObjectToAsset(bad, controller);
            Assert.That(AnimationUtility.GetCurveBindings(bad).Contains(binding), Is.True);
            var retained = AnimationUtility.GetEditorCurve(bad, binding);
            Assert.That(retained == null || retained.length == 0 || retained.keys.Any(key => float.IsNaN(key.value)), Is.True);
            if (kind == "unrelated" || kind == "excluded")
            {
                controller.AddLayer("Fully omitted malformed layer"); var layers = controller.layers; layers[3].defaultWeight = 1;
                layers[3].stateMachine.defaultState = State(layers[3].stateMachine, "Unrelated nonempty motion", bad);
                controller.layers = layers;
                if (kind == "excluded")
                {
                    controller.AddParameter("ExcludedControl", AnimatorControllerParameterType.Bool);
                    var idle = layers[3].stateMachine.defaultState;
                    var transition = idle.AddTransition(idle); transition.hasExitTime = false; transition.duration = 0;
                    transition.AddCondition(AnimatorConditionMode.If, 0, "ExcludedControl");
                }
            }
            else
            {
                controller.AddParameter("AFK", AnimatorControllerParameterType.Bool);
                var machine = controller.layers[2].stateMachine;
                var dormant = State(machine, "Fixed false malformed state", bad);
                var active = machine.states.Single(child => child.state.name == "Ancillary active").state;
                var transition = active.AddTransition(dormant); transition.hasExitTime = false; transition.duration = 0;
                transition.AddCondition(AnimatorConditionMode.If, 0, "AFK");
            }
            var metadata = new VrChatExpressionMenu.Source { Controller = controller };
            var defaults = new Dictionary<string, float> { ["Face"] = 0 };
            var selection = new Dictionary<string, float> { ["Face"] = 1 };
            Func<string, bool> excludedPath = null;
            if (kind == "excluded")
            {
                selection["ExcludedControl"] = 1;
                excludedPath = path => path == "Ear";
                var context = FixedExpressionContext.Create(controller, defaults, metadata);
                var dependencies = ExpressionDependencies.Analyze(controller, selection.Keys, excludedPath, metadata, defaults, selection,
                    AnimationUtility.GetCurveBindings((AnimationClip)selected.motion), fixedContext: context);
                Assert.That(dependencies.Layers, Does.Contain(3), "The selected raw gate must first retain this layer.");
                var omitted = VrChatExpressionSampler.FindExcludedLayers(controller, controller, excludedPath, dependencies.Parameters);
                Assert.That(omitted, Does.Contain(3), "The layer's complete non-parameter binding scope is excluded.");
                dependencies.Layers.ExceptWith(omitted); dependencies.NativeSupportLayers.ExceptWith(omitted);
                using (var evaluation = new ExpressionEvaluationSession(controller, dependencies, null, true, context))
                {
                    Assert.That(evaluation.Controller.layers[3].defaultWeight, Is.Zero);
                    Assert.That(evaluation.Controller.layers[3].stateMachine.defaultState.motion, Is.Null,
                        "The actual owned native controller must omit the malformed clip.");
                }
            }
            var assets = DirectProbeSourceAssets(); var sourceJson = EditorJsonUtility.ToJson(controller);
            var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            var values = VrChatExpressionSampler.SampleFixed(avatar, controller, defaults, selection, excludedPath, metadata: metadata);
            Assert.That(values.Single().Weight, Is.EqualTo(expected).Within(.01));
            AssertDirectProbeSourceAssets(assets); AssertUnchanged(sourceJson, meshJson, probes);
        }

        [TestCase("menu", false)]
        [TestCase("direct", false)]
        [TestCase("selected-driver", false)]
        [TestCase("menu", true)]
        [TestCase("selected-driver", true)]
        public void AncillaryBonesAreJudgedByCapturedMorphVertexInfluence(string route, bool affectsFace)
        {
            ConfigureCapturedMorphSkin(affectsFace);
            var nativeDefaults = new Dictionary<string, float> { ["Face"] = route == "menu" ? 1 : 0 };
            var baseline = NativeSkinnedVertices(nativeDefaults);
            var clip = AncillaryGraph();
            if (route == "selected-driver")
            {
                controller.AddParameter("EarControl", AnimatorControllerParameterType.Bool);
                ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "EarControl", 1));
                controller.layers[2].stateMachine.defaultState.transitions.Single().AddCondition(AnimatorConditionMode.If, 0, "EarControl");
                // The independent original graph supplies the same selected Set
                // result, without using the exporter controller or adapter.
                nativeDefaults["EarControl"] = 1;
            }
            var evaluated = NativeSkinnedVertices(nativeDefaults);
            if (affectsFace) Assert.That(Math.Abs(evaluated[0].x - baseline[0].x), Is.GreaterThan(.1f),
                "The actual baked morph vertex must change when its positively weighted bone moves.");
            else Assert.That(Math.Abs(evaluated[0].x - baseline[0].x), Is.LessThan(.0001f),
                "The actual baked morph vertex must stay fixed when only the disjoint listed bone moves.");
            Assert.That(Math.Abs(evaluated[1].x - baseline[1].x), Is.GreaterThan(.1f),
                "The ancillary animation must move an actual weighted vertex.");
            var expected = Native(controller, nativeDefaults);
            var metadata = new VrChatExpressionMenu.Source { Controller = controller }; metadata.Defaults["Face"] = 0;
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var assets = DirectProbeSourceAssets(); var sourceJson = EditorJsonUtility.ToJson(controller);
            var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            Action sample = () =>
            {
                if (route == "menu")
                {
                    var values = VrChatExpressionSampler.SampleFixed(avatar, controller, metadata.Defaults,
                        new Dictionary<string, float> { ["Face"] = 1 }, metadata: metadata);
                    entry.Values.Clear(); entry.Values.AddRange(values);
                }
                else VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar, controller, entry, 1, metadata: metadata,
                    selectedState: route == "selected-driver" ? selected : null);
            };
            if (affectsFace)
            {
                Assert.That(Assert.Throws<InvalidOperationException>(() => sample()).Message, Does.Contain("BlendShape以外").And.Contain("Ear"));
                Assert.That(entry.Values.Single().Weight, Is.EqualTo(80));
            }
            else
            {
                sample(); Assert.That(entry.Values.Single().Weight, Is.EqualTo(expected).Within(.01));
                Assert.That(metadata.Messages.Any(message => message.Contains("表示・Transform")), Is.True);
            }
            AssertDirectProbeSourceAssets(assets); AssertUnchanged(sourceJson, meshJson, probes);
        }

        [TestCase("menu")]
        [TestCase("direct")]
        public void DeferredAncillaryScopeUsesActualEndpointAndKeepsZeroAuthoredChannels(string route)
        {
            var metadata = DeferredEndpointGraph(route, out var entry, out var expected);
            Assert.That(entry.Values.Single(value => value.Shape == "Unused appendage").Weight, Is.Zero,
                "The captured zero channel is preserved, including the direct probe's counterfactual full input.");
            var assets = DirectProbeSourceAssets(); var sourceJson = EditorJsonUtility.ToJson(controller);
            var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            var skin = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
            var owned = new List<Mesh>();
            try
            {
                using (var prepared = new PreparedExpressionBindings(avatar, metadata))
                {
                    prepared.Capture(metadata);
                    Assert.That(entry.Error, Is.Null);
                    Assert.That(entry.AncillaryGeometry.IsValidatedFor(entry), Is.True);
                    Assert.That(entry.Messages.Any(message => message.Contains("表示・Transform")), Is.True);
                    var result = VrChatExpressionBaker.Bake(avatar, avatar, metadata, owned, new List<string>(), prepared);
                    Assert.That(result.Count, Is.EqualTo(1));
                    Assert.That(entry.Values.Single(value => value.Shape == "Face size").Weight, Is.EqualTo(expected).Within(.01));
                    var vertices = new Vector3[mesh.vertexCount];
                    var target = skin.sharedMesh.GetBlendShapeIndex(result.Single().Targets.Single());
                    skin.sharedMesh.GetBlendShapeFrameVertices(target, 0, vertices, null, null);
                    Assert.That(Math.Abs(vertices[0].x), Is.GreaterThan(.1f));
                    Assert.That(vertices[1], Is.EqualTo(Vector3.zero), "The emitted facial target does not serialize the ancillary bone motion.");
                }
            }
            finally { skin.sharedMesh = mesh; foreach (var value in owned) Object.DestroyImmediate(value); }
            AssertDirectProbeSourceAssets(assets); AssertUnchanged(sourceJson, meshJson, probes);
        }

        [TestCase("menu")]
        [TestCase("direct")]
        public void DeferredAncillaryScopeRejectsAZeroEndpointThatResetsTheFinalNeutral(string route)
        {
            var metadata = DeferredEndpointGraph(route, out var entry, out _);
            var skin = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
            var shape = mesh.GetBlendShapeIndex("Unused appendage");
            // This represents a later prepared-neutral pass, after the probes.
            skin.SetBlendShapeWeight(shape, 25);
            var owned = new List<Mesh>();
            using (var prepared = new PreparedExpressionBindings(avatar, metadata))
            {
                prepared.Capture(metadata);
                Assert.That(prepared.Get("Face").Weights[shape], Is.EqualTo(25));
                Assert.That(entry.Values.Single(value => value.Shape == "Unused appendage").Weight, Is.Zero);
                Assert.That(entry.Error, Does.Contain("BlendShape以外").And.Contain("Ear"));
                Assert.That(entry.AncillaryGeometry.IsValidatedFor(entry), Is.False);
                Assert.That(VrChatExpressionBaker.Bake(avatar, avatar, metadata, owned, new List<string>(), prepared), Is.Empty);
            }
            Assert.That(owned, Is.Empty); Assert.That(skin.sharedMesh, Is.SameAs(mesh));
            Assert.That(skin.GetBlendShapeWeight(shape), Is.EqualTo(25));
        }

        [Test]
        public void DeferredAncillaryScopeProtectsIntermediateAnimationGeometryEvenWhenItsEndpointsCancel()
        {
            var metadata = DeferredEndpointGraph("direct", out var entry, out _, intermediate: true);
            var shape = mesh.GetBlendShapeIndex("Unused appendage");
            var endpoint = new Vector3[mesh.vertexCount];
            mesh.GetBlendShapeFrameVertices(shape, 1, endpoint, null, null);
            Assert.That(endpoint.All(value => value == Vector3.zero), Is.True);
            var curve = VrChatGestureExpressions.ReadCurve(AnimationCurve.Linear(0, 0, 1, 100));
            entry.Animation.Add(new VrChatExpressionMenu.AnimatedMorph { Path = "Face", Shape = "Unused appendage", Curve = curve }); entry.Duration = 1;
            using (var prepared = new PreparedExpressionBindings(avatar, metadata))
            {
                prepared.Capture(metadata);
                Assert.That(entry.Values.Single(value => value.Shape == "Unused appendage").Weight, Is.Zero);
                Assert.That(entry.Error, Does.Contain("BlendShape以外").And.Contain("Ear"),
                    "A nonzero intermediate source frame is an emitted animation basis, even if both end poses coincide.");
            }
        }

        [TestCase("pending")]
        [TestCase("changed-value")]
        [TestCase("changed-curve")]
        public void DeferredAncillaryEvidenceMustValidateTheExactEntryBeforeAnyBake(string kind)
        {
            var metadata = DeferredEndpointGraph("direct", out var entry, out _);
            var skin = avatar.GetComponentInChildren<SkinnedMeshRenderer>(); var owned = new List<Mesh>();
            using (var prepared = new PreparedExpressionBindings(avatar, metadata))
            {
                if (kind != "pending")
                {
                    prepared.Capture(metadata); Assert.That(entry.Error, Is.Null);
                    Assert.That(entry.AncillaryGeometry.IsValidatedFor(entry), Is.True);
                    if (kind == "changed-value")
                    {
                        entry.Values[0].Weight += 1;
                    }
                    else
                    {
                        var old = entry.Values.Single(value => value.Shape == "Face size");
                        entry.Animation.Add(new VrChatExpressionMenu.AnimatedMorph { Path = old.Path, Shape = old.Shape,
                            Curve = VrChatGestureExpressions.ReadCurve(AnimationCurve.Linear(0, old.Weight, 1, old.Weight + 1)) });
                    }
                }
                Assert.That(entry.AncillaryGeometry.IsValidatedFor(entry), Is.False);
                var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionBaker.Bake(avatar, avatar, metadata, owned, new List<string>(), prepared));
                Assert.That(error.Message, Does.Contain("未検証"));
            }
            Assert.That(owned, Is.Empty); Assert.That(skin.sharedMesh, Is.SameAs(mesh));
        }

        [Test]
        public void DeferredAncillaryScopeRejectsUnresolvedWeightedFacialBones()
        {
            var metadata = DeferredEndpointGraph("direct", out var entry, out _);
            var skin = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
            var bones = skin.bones; var invalid = (Transform[])bones.Clone(); invalid[0] = null; skin.bones = invalid;
            try
            {
                using (var prepared = new PreparedExpressionBindings(avatar, metadata))
                {
                    prepared.Capture(metadata);
                    Assert.That(entry.Error, Does.Contain("影響範囲を確定できません"));
                    Assert.That(entry.AncillaryGeometry.IsValidatedFor(entry), Is.False);
                }
            }
            finally { skin.bones = bones; }
        }

        [TestCase("captured")]
        [TestCase("callback")]
        public void DeferredAncillaryScopeKeepsPreEvaluationRendererAndCallbackSafeguards(string kind)
        {
            ConfigureCapturedMorphSkin(false);
            var clip = AncillaryGraph(kind == "captured" ? "Face" : "Ear");
            if (kind == "callback") AnimationUtility.SetAnimationEvents(clip, new[] { new AnimationEvent { time = 0, functionName = "FutureCallback" } });
            var metadata = new VrChatExpressionMenu.Source { Controller = controller, DeferAncillaryGeometryValidation = true };
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var assets = DirectProbeSourceAssets(); var sourceJson = EditorJsonUtility.ToJson(controller);
            var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar, controller, entry, 1, metadata: metadata));
            Assert.That(error.Message, Does.Contain(kind == "callback" ? "影響範囲" : "BlendShape以外"));
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(80));
            AssertDirectProbeSourceAssets(assets); AssertUnchanged(sourceJson, meshJson, probes);
        }

        private VrChatExpressionMenu.Source DeferredEndpointGraph(string route, out VrChatExpressionMenu.Entry entry,
            out float expected, bool intermediate = false)
        {
            ConfigureCapturedMorphSkin(false);
            var delta = new[] { Vector3.zero, new Vector3(1, 0, 0), Vector3.zero };
            if (intermediate) mesh.AddBlendShapeFrame("Unused appendage", 50, delta, null, null);
            mesh.AddBlendShapeFrame("Unused appendage", 100, intermediate ? new Vector3[3] : delta, null, null);
            var binding = EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Unused appendage");
            foreach (var state in new[] { lower, alternate, selected })
                AnimationUtility.SetEditorCurve((AnimationClip)state.motion, binding, AnimationCurve.Constant(0, 1, 0));
            var defaults = new Dictionary<string, float> { ["Face"] = route == "menu" ? 1 : 0 };
            var baseline = NativeSkinnedVertices(defaults);
            AncillaryGraph();
            var evaluated = NativeSkinnedVertices(defaults);
            Assert.That(Math.Abs(evaluated[0].x - baseline[0].x), Is.LessThan(.0001f));
            Assert.That(Math.Abs(evaluated[1].x - baseline[1].x), Is.GreaterThan(.1f),
                "The independent original graph must genuinely move the appendage bone before testing serialization scope.");
            expected = Native(controller, defaults);
            var metadata = new VrChatExpressionMenu.Source { Controller = controller, DeferAncillaryGeometryValidation = true };
            metadata.Defaults["Face"] = 0;
            entry = new VrChatExpressionMenu.Entry { Name = "Captured facial endpoint" }; metadata.Entries.Add(entry);
            if (route == "menu") entry.Values.AddRange(VrChatExpressionSampler.SampleFixed(avatar, controller, metadata.Defaults,
                new Dictionary<string, float> { ["Face"] = 1 }, metadata: metadata, geometryEntry: entry));
            else
            {
                VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
                VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar, controller, entry, 1, metadata: metadata);
            }
            Assert.That(entry.Values.Single(value => value.Shape == "Face size").Weight, Is.EqualTo(expected).Within(.01));
            Assert.That(entry.AncillaryGeometry, Is.Not.Null);
            Assert.That(entry.AncillaryGeometry.IsValidatedFor(entry), Is.False);
            Assert.That(entry.Messages.Any(message => message.Contains("表示・Transform")), Is.False,
                "Deferred ancillary scope must not be advertised as safe before final-neutral capture.");
            return metadata;
        }

        [TestCase("menu", false)]
        [TestCase("direct", false)]
        [TestCase("menu", true)]
        [TestCase("direct", true)]
        public void NativeSupportParameterWriterKeepsItsGraphAndProvesItsAncillaryGeometry(string route, bool affectsFace)
        {
            var writer = SupportParameterWriterGraph(affectsFace, out var nativeDefaults, route);
            var sourceAssets = DirectProbeSourceAssets(); var sourceJson = EditorJsonUtility.ToJson(controller);
            var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            var expected = Native(controller, nativeDefaults);
            var metadata = new VrChatExpressionMenu.Source { Controller = controller, DeferAncillaryGeometryValidation = true };
            metadata.Defaults["Face"] = 0;
            var entry = new VrChatExpressionMenu.Entry { Name = "Facial native endpoint" }; metadata.Entries.Add(entry);
            var seeds = AnimationUtility.GetCurveBindings((AnimationClip)selected.motion);
            var selection = route == "menu" ? new Dictionary<string, float> { ["Face"] = 1 } : new Dictionary<string, float>();
            var context = FixedExpressionContext.Create(controller, metadata.Defaults, metadata);
            var dependencies = ExpressionDependencies.Analyze(controller, selection.Keys, null, metadata, metadata.Defaults, selection,
                seeds, fixedContext: context);
            Assert.That(dependencies.Layers, Does.Contain(3), "The support writer must be retained, not discarded as unrelated.");
            Assert.That(dependencies.Parameters, Does.Contain("EarControl"));
            Assert.That(dependencies.Drivers.ContainsKey(writer.behaviours.Single()), Is.True);
            Assert.That(dependencies.StrictNonMorphLayers.Contains(3), Is.False);
            if (route == "menu") entry.Values.AddRange(VrChatExpressionSampler.SampleFixed(avatar, controller, metadata.Defaults,
                selection, metadata: metadata, geometryEntry: entry));
            else
            {
                VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
                VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar, controller, entry, 1, metadata: metadata);
            }
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(expected).Within(.01));
            var owned = new List<Mesh>(); var skin = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
            try
            {
                using (var prepared = new PreparedExpressionBindings(avatar, metadata))
                {
                    prepared.Capture(metadata);
                    if (affectsFace)
                    {
                        Assert.That(entry.Error, Does.Contain("BlendShape以外").And.Contain("Ear"));
                        Assert.That(VrChatExpressionBaker.Bake(avatar, avatar, metadata, owned, new List<string>(), prepared), Is.Empty);
                        Assert.That(owned, Is.Empty);
                    }
                    else
                    {
                        Assert.That(entry.Error, Is.Null); Assert.That(entry.AncillaryGeometry.IsValidatedFor(entry), Is.True);
                        Assert.That(entry.Messages.Any(message => message.Contains("表示・Transform")), Is.True);
                        Assert.That(VrChatExpressionBaker.Bake(avatar, avatar, metadata, owned, new List<string>(), prepared).Count, Is.EqualTo(1));
                    }
                }
            }
            finally { skin.sharedMesh = mesh; foreach (var value in owned) Object.DestroyImmediate(value); }
            Assert.That(avatar.transform.Find("Wardrobe").gameObject.activeSelf, Is.False);
            Assert.That(avatar.transform.Find("Ear").localPosition, Is.EqualTo(Vector3.zero));
            AssertDirectProbeSourceAssets(sourceAssets); AssertUnchanged(sourceJson, meshJson, probes);
        }

        [Test]
        public void SelectingTheSupportWritersOwnVisibilityStillRejectsItAsAnIntrinsicEntry()
        {
            var writer = SupportParameterWriterGraph(false, out _, "menu");
            controller.AddParameter("WardrobeVisible", AnimatorControllerParameterType.Bool);
            var machine = controller.layers[3].stateMachine;
            var initial = State(machine, "Unselected visibility", (AnimationClip)writer.motion); machine.defaultState = initial;
            var transition = initial.AddTransition(writer); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.If, 0, "WardrobeVisible");
            var metadata = new VrChatExpressionMenu.Source { Controller = controller, DeferAncillaryGeometryValidation = true };
            var entry = new VrChatExpressionMenu.Entry(); var sourceJson = EditorJsonUtility.ToJson(controller);
            var assets = DirectProbeSourceAssets(); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleFixed(avatar, controller,
                new Dictionary<string, float> { ["Face"] = 0 }, new Dictionary<string, float> { ["WardrobeVisible"] = 1 },
                metadata: metadata, geometryEntry: entry));
            Assert.That(error.Message, Does.Contain("BlendShape以外").And.Contain("Wardrobe"));
            Assert.That(entry.Values, Is.Empty);
            AssertDirectProbeSourceAssets(assets); AssertUnchanged(sourceJson, meshJson, probes);
        }

        [Test]
        public void SupportParameterWriterCannotHideACallbackWithUnknownSceneEffects()
        {
            var writer = SupportParameterWriterGraph(false, out _, "direct");
            AnimationUtility.SetAnimationEvents((AnimationClip)writer.motion,
                new[] { new AnimationEvent { time = 0, functionName = "FutureCallback" } });
            var metadata = new VrChatExpressionMenu.Source { Controller = controller, DeferAncillaryGeometryValidation = true };
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var assets = DirectProbeSourceAssets(); var sourceJson = EditorJsonUtility.ToJson(controller);
            var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar,
                controller, entry, 1, metadata: metadata));
            Assert.That(error.Message, Does.Contain("影響範囲"));
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(80));
            AssertDirectProbeSourceAssets(assets); AssertUnchanged(sourceJson, meshJson, probes);
        }

        [TestCase("event", "fixed-false")]
        [TestCase("object", "fixed-false")]
        [TestCase("event", "fixed-true")]
        [TestCase("object", "fixed-true")]
        [TestCase("event", "mutable")]
        [TestCase("object", "mutable")]
        [TestCase("event", "unknown")]
        [TestCase("object", "unknown")]
        public void RetainedAffectedWriterChecksTransientCapabilitiesUnderTheSameFixedReachabilityProof(string capability, string gate)
        {
            var writer = SupportParameterWriterGraph(false, out var nativeInputs, "direct");
            var parameter = gate == "unknown" ? "EyeHeightAsMeters" : "CapabilityGate";
            controller.AddParameter(parameter, gate == "unknown" ? AnimatorControllerParameterType.Float : AnimatorControllerParameterType.Bool);
            var transient = new AnimationClip { name = "Instant retained support motion" };
            AnimationUtility.SetEditorCurve(transient, EditorCurveBinding.FloatCurve("Wardrobe", typeof(Transform), "m_LocalPosition.x"),
                AnimationCurve.Constant(0, 1, 1));
            if (capability == "event")
                AnimationUtility.SetAnimationEvents(transient, new[] { new AnimationEvent { time = 0, functionName = "FutureCallback" } });
            else
                AnimationUtility.SetObjectReferenceCurve(transient, EditorCurveBinding.PPtrCurve("Face", typeof(SkinnedMeshRenderer), "m_Mesh"),
                    new[] { new ObjectReferenceKeyframe { time = 0, value = mesh } });
            AssetDatabase.AddObjectToAsset(transient, controller);
            var machine = controller.layers[3].stateMachine;
            var intermediate = State(machine, "Instant retained state", transient);
            var returned = State(machine, "Returned retained support", (AnimationClip)writer.motion);
            var enter = writer.AddTransition(intermediate); enter.hasExitTime = false; enter.duration = 0;
            enter.AddCondition(gate == "unknown" ? AnimatorConditionMode.Greater : AnimatorConditionMode.If,
                gate == "unknown" ? .5f : 0, parameter);
            var leave = intermediate.AddTransition(returned); leave.hasExitTime = false; leave.duration = 0;
            // Native Unity does not traverse a conditionless transition with
            // no exit time. Use the same stable true input so the intermediate
            // motion actually enters, leaves and disappears from clip-info.
            leave.AddCondition(gate == "unknown" ? AnimatorConditionMode.Greater : AnimatorConditionMode.If,
                gate == "unknown" ? .5f : 0, parameter);
            Assert.That(enter.duration, Is.Zero); Assert.That(leave.duration, Is.Zero);
            Assert.That(leave.conditions.Single().parameter, Is.EqualTo(parameter));
            if (gate == "mutable")
                ParameterDriverExpressionTests.Driver(writer, ParameterDriverExpressionTests.Op("Set", parameter, 1));
            nativeInputs[parameter] = gate == "fixed-false" ? 0 : 1;
            // The real Editor graph does not implement SDK scalar Set commands.
            // Its counterfactual receives their known values once; it still
            // evaluates the original native motions and Write Defaults.
            var native = RetainedTransientNativeReference(nativeInputs, transient);
            Assert.That(native.Marker, Is.EqualTo(gate == "fixed-false" ? 0 : 1).Within(.0001f),
                "The retained native Transform write proves whether the instantaneous state actually ran.");
            Assert.That(native.State, Is.EqualTo(Animator.StringToHash(gate == "fixed-false" ? writer.name : returned.name)));
            Assert.That(native.TransientVisible, Is.False,
                "The final native clip-info no longer exposes the instantaneous motion despite its retained property write.");
            var assets = DirectProbeSourceAssets(); var sourceJson = EditorJsonUtility.ToJson(controller);
            var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            var repeated = RetainedTransientNativeReference(nativeInputs, transient);
            Assert.That(repeated.Marker, Is.EqualTo(native.Marker).Within(.0001f));
            AssertDirectProbeSourceAssets(assets); AssertUnchanged(sourceJson, meshJson, probes);
            var metadata = new VrChatExpressionMenu.Source { Controller = controller, DeferAncillaryGeometryValidation = true };
            metadata.Defaults["Face"] = 0;
            if (gate != "unknown") metadata.Defaults[parameter] = gate == "fixed-true" ? 1 : 0;
            var entry = new VrChatExpressionMenu.Entry { Name = "Retained facial expression" }; metadata.Entries.Add(entry);
            VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            Action sample = () => VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar, controller, entry, 1, metadata: metadata);
            if (gate == "fixed-false")
            {
                sample(); Assert.That(entry.Values.Single().Weight, Is.EqualTo(native.Face).Within(.01f));
                var owned = new List<Mesh>(); var skin = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
                try
                {
                    using (var prepared = new PreparedExpressionBindings(avatar, metadata))
                    {
                        prepared.Capture(metadata); Assert.That(entry.Error, Is.Null);
                        Assert.That(entry.AncillaryGeometry.IsValidatedFor(entry), Is.True);
                        Assert.That(VrChatExpressionBaker.Bake(avatar, avatar, metadata, owned, new List<string>(), prepared), Has.Count.EqualTo(1));
                    }
                }
                finally { skin.sharedMesh = mesh; foreach (var value in owned) Object.DestroyImmediate(value); }
            }
            else
            {
                var error = Assert.Throws<InvalidOperationException>(() => sample());
                Assert.That(error.Message, Does.Contain(gate == "unknown" ? parameter : capability == "event" ? "影響範囲" : "差し替え"));
                Assert.That(entry.Values.Single().Weight, Is.EqualTo(80), "A rejected transient must not partially replace the authored clip values.");
            }
            Assert.That(avatar.transform.Find("Wardrobe").localPosition, Is.EqualTo(Vector3.zero));
            Assert.That(avatar.transform.Find("Wardrobe").gameObject.activeSelf, Is.False);
            AssertDirectProbeSourceAssets(assets); AssertUnchanged(sourceJson, meshJson, probes);
        }

        private (float Marker, int State, bool TransientVisible, float Face) RetainedTransientNativeReference(
            IDictionary<string, float> inputs, AnimationClip transient)
        {
            var copy = Object.Instantiate(avatar); var graph = PlayableGraph.Create("Independent retained transient native reference");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            try
            {
                var animator = copy.GetComponent<Animator>(); animator.runtimeAnimatorController = null;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.applyRootMotion = false; animator.fireEvents = false;
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                for (var index = 0; index < controller.layers.Length; index++)
                    playable.SetLayerWeight(index, index == 0 ? 1 : controller.layers[index].defaultWeight);
                foreach (var pair in inputs)
                {
                    var type = controller.parameters.Single(parameter => parameter.name == pair.Key).type;
                    if (type == AnimatorControllerParameterType.Bool) playable.SetBool(pair.Key, pair.Value != 0);
                    else if (type == AnimatorControllerParameterType.Int) playable.SetInteger(pair.Key, Mathf.RoundToInt(pair.Value));
                    else if (type == AnimatorControllerParameterType.Float) playable.SetFloat(pair.Key, pair.Value);
                    else Assert.Fail("The native counterfactual cannot set a trigger.");
                }
                AnimationPlayableOutput.Create(graph, "Original retained graph", animator).SetSourcePlayable(playable);
                graph.Play(); graph.Evaluate(0);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                Assert.That(playable.IsInTransition(3), Is.False);
                var visible = playable.GetCurrentAnimatorClipInfo(3).Concat(playable.GetNextAnimatorClipInfo(3))
                    .Any(info => info.clip == transient && info.weight > .00001f);
                return (copy.transform.Find("Wardrobe").localPosition.x, playable.GetCurrentAnimatorStateInfo(3).shortNameHash,
                    visible, copy.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0));
            }
            finally { if (graph.IsValid()) graph.Destroy(); Object.DestroyImmediate(copy); }
        }

        private AnimatorState SupportParameterWriterGraph(bool affectsFace, out Dictionary<string, float> nativeDefaults, string route)
        {
            ConfigureCapturedMorphSkin(affectsFace);
            var defaults = new Dictionary<string, float> { ["Face"] = route == "menu" ? 1 : 0 };
            var before = NativeSkinnedVertices(defaults);
            AncillaryGraph(); controller.AddParameter("EarControl", AnimatorControllerParameterType.Bool);
            controller.layers[2].stateMachine.defaultState.transitions.Single().AddCondition(AnimatorConditionMode.If, 0, "EarControl");
            var wardrobe = new GameObject("Wardrobe"); wardrobe.transform.SetParent(avatar.transform, false); wardrobe.SetActive(false);
            var clip = new AnimationClip { name = "Support wardrobe visibility" };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Wardrobe", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1));
            AssetDatabase.AddObjectToAsset(clip, controller);
            controller.AddLayer("Support parameter writer"); var layers = controller.layers; layers[3].defaultWeight = 1;
            var writer = State(layers[3].stateMachine, "Known ancillary input", clip); layers[3].stateMachine.defaultState = writer;
            ParameterDriverExpressionTests.Driver(writer, ParameterDriverExpressionTests.Op("Set", "EarControl", 1));
            controller.layers = layers;
            // SDK Set programs have no native Editor implementation. Supply
            // their one validated scalar result to the independent original
            // graph; it still evaluates the actual clips, weights and skin.
            defaults["EarControl"] = 1;
            var after = NativeSkinnedVertices(defaults, copy => Assert.That(copy.transform.Find("Wardrobe").gameObject.activeSelf, Is.True));
            Assert.That(Math.Abs(after[1].x - before[1].x), Is.GreaterThan(.1f));
            if (affectsFace) Assert.That(Math.Abs(after[0].x - before[0].x), Is.GreaterThan(.1f));
            else Assert.That(Math.Abs(after[0].x - before[0].x), Is.LessThan(.0001f));
            nativeDefaults = defaults; return writer;
        }

        [TestCase("menu", false)]
        [TestCase("fixed-direct", false)]
        [TestCase("raw-direct", false)]
        [TestCase("menu", true)]
        [TestCase("frozen-time", false)]
        [TestCase("startup-clock", false)]
        public void TemporaryExpressionGraphsPreserveTheSavedAuthoredScene(string route, bool rejected)
        {
            var unrelated = new GameObject("Unrelated"); unrelated.transform.SetParent(avatar.transform, false);
            var clip = new AnimationClip { name = "Unrelated owned motion" };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Unrelated", typeof(Transform), "m_LocalPosition.x"),
                AnimationCurve.Constant(0, 1, .25f));
            AssetDatabase.AddObjectToAsset(clip, controller);
            controller.AddLayer("Unrelated nonempty layer"); var layers = controller.layers; layers[2].defaultWeight = 1;
            layers[2].stateMachine.defaultState = State(layers[2].stateMachine, "Unrelated state", clip); controller.layers = layers;
            if (rejected) AnimationUtility.SetEditorCurve((AnimationClip)alternate.motion,
                EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"), AnimationCurve.Linear(0, 60, 30, 80));
            if (route == "frozen-time") FrozenFacialMotion(.5f, out _);
            if (route == "startup-clock")
            {
                var clockState = FrozenFacialMotion(.2f, out var ramp);
                ParameterDriverExpressionTests.Driver(clockState, ParameterDriverExpressionTests.Op("Set", FrozenClock, .4f));
                foreach (var value in NativeFrozenClockHistory(clockState, ramp, .4f, 0)) Assert.That(value, Is.EqualTo(47.5f).Within(.01f));
            }
            var metadata = new VrChatExpressionMenu.Source { Controller = controller, DeferAncillaryGeometryValidation = route == "frozen-time" || route == "startup-clock" }; metadata.Defaults["Face"] = 0;
            if (route == "startup-clock") metadata.Defaults[FrozenClock] = .2f;
            var selection = new Dictionary<string, float> { ["Face"] = route == "menu" ? 1 : 0 };
            var seeds = AnimationUtility.GetCurveBindings((AnimationClip)selected.motion);
            var dependencies = ExpressionDependencies.Analyze(controller, selection.Keys, null, metadata, metadata.Defaults,
                selection, seeds, fixedContext: FixedExpressionContext.Create(controller, metadata.Defaults, metadata));
            Assert.That(dependencies.Layers.Contains(2) || dependencies.NativeSupportLayers.Contains(2), Is.False,
                "A genuinely omitted layer must force the temporary fallback state that previously registered Undo.");
            var referenceInputs = new Dictionary<string, float>(selection);
            if (route == "startup-clock") referenceInputs[FrozenClock] = .4f;
            var expected = rejected ? 0 : Native(controller, referenceInputs);
            if (route == "frozen-time") Assert.That(expected, Is.EqualTo(52.5f).Within(.01f));
            if (route == "startup-clock") Assert.That(expected, Is.EqualTo(47.5f).Within(.01f));
            // Closing the runner scene also destroys its transient Mesh. Make
            // our fixture's native copy a persistent owned asset before cloning
            // the template or replacing that scene, so neither holds a dead ref.
            var persistedMesh = Object.Instantiate(mesh); persistedMesh.hideFlags = HideFlags.None;
            AssetDatabase.CreateAsset(persistedMesh, folder + "/Authored mesh.asset");
            avatar.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh = persistedMesh;
            // The runner's isolated untitled scene cannot host NewScene(Additive).
            // Retain our template in an owned preview, then use the saved normal
            // scene ownership/cleanup pattern of the other native scene tests.
            using var template = new ExportCopyScene(avatar);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var path = folder + "/Authored source.unity";
            try
            {
                Assert.That(scene.IsValid() && scene.isLoaded, Is.True);
                Assert.That(EditorSceneManager.IsPreviewScene(scene), Is.False);
                if (SceneManager.GetActiveScene() != scene) Assert.That(SceneManager.SetActiveScene(scene), Is.True);
                Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(scene));
                var sourceAvatar = Object.Instantiate(template.Copy); sourceAvatar.name = template.Copy.name;
                foreach (var component in sourceAvatar.GetComponentsInChildren<Component>(true))
                    if (component != null) { component.gameObject.hideFlags = HideFlags.None; component.hideFlags = HideFlags.None; }
                Assert.That(sourceAvatar.scene, Is.EqualTo(scene));
                Assert.That(sourceAvatar.activeInHierarchy, Is.True);
                Assert.That(sourceAvatar.hideFlags, Is.EqualTo(HideFlags.None));
                var skin = sourceAvatar.GetComponentInChildren<SkinnedMeshRenderer>();
                Assert.That(skin.sharedMesh, Is.SameAs(persistedMesh));
                sourceAvatar.GetComponent<Animator>().runtimeAnimatorController = controller;
                DirectProbeSourceAssets(); AssetDatabase.SaveAssets();
                Assert.That(EditorSceneManager.SaveScene(scene, path), Is.True);
                Assert.That(scene.isDirty, Is.False, "Start from a genuinely saved owned scene; never clear the dirty flag for the assertion.");
                var sourceName = sourceAvatar.name;
                scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                sourceAvatar = scene.GetRootGameObjects().Single(value => value.name == sourceName);
                skin = sourceAvatar.GetComponentInChildren<SkinnedMeshRenderer>();
                Assert.That(EditorSceneManager.IsPreviewScene(scene), Is.False);
                Assert.That(scene.isDirty, Is.False);
                Assert.That(sourceAvatar.activeInHierarchy, Is.True);
                Assert.That(sourceAvatar.hideFlags, Is.EqualTo(HideFlags.None));
                Assert.That(skin.sharedMesh, Is.SameAs(persistedMesh), "The native scene file must retain the source mesh asset reference.");
                Assert.That(sourceAvatar.GetComponent<Animator>().runtimeAnimatorController, Is.SameAs(controller));
                var assets = DirectProbeSourceAssets();
                assets.Add(persistedMesh, EditorJsonUtility.ToJson(persistedMesh));
                var file = File.ReadAllBytes(path); var roots = scene.GetRootGameObjects();
                var sourceJson = sourceAvatar.GetComponentsInChildren<Transform>(true).SelectMany(value =>
                    new Object[] { value.gameObject }.Concat(value.GetComponents<Component>().Cast<Object>()))
                    .Where(value => value != null).Select(value => EditorJsonUtility.ToJson(value)).ToArray();
                var weights = Enumerable.Range(0, persistedMesh.blendShapeCount).Select(skin.GetBlendShapeWeight).ToArray();
                var probes = ProbeObjects();
                Action sample = () =>
                {
                    if (route == "menu" || route == "frozen-time" || route == "startup-clock")
                    {
                        var entry = route == "frozen-time" || route == "startup-clock" ? new VrChatExpressionMenu.Entry { Name = "Saved authored fixed-time face" } : null;
                        if (entry != null) metadata.Entries.Add(entry);
                        var values = VrChatExpressionSampler.SampleFixed(sourceAvatar, controller, metadata.Defaults, selection, metadata: metadata, geometryEntry: entry);
                        Assert.That(values.Single().Weight, Is.EqualTo(expected).Within(.01));
                        if (route == "startup-clock") Assert.That(metadata.Defaults[FrozenClock], Is.EqualTo(.2f));
                    }
                    else
                    {
                        var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(sourceAvatar, (AnimationClip)selected.motion, entry);
                        if (route == "fixed-direct") VrChatExpressionSampler.ApplyFixedPermanentOverrides(sourceAvatar, controller, entry, 1, metadata: metadata);
                        else VrChatExpressionSampler.ApplyPermanentOverrides(sourceAvatar, controller, entry, 1, metadata: metadata);
                        Assert.That(entry.Values.Single().Weight, Is.EqualTo(expected).Within(.01));
                    }
                };
                if (rejected) Assert.That(Assert.Throws<InvalidOperationException>(() => sample()).Message, Does.Contain("時間で変わる"));
                else sample();
                Assert.That(scene.isDirty, Is.False, "Temporary Animator graphs must not create global Undo entries that dirty the authored scene.");
                Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(scene));
                Assert.That(scene.GetRootGameObjects(), Is.EquivalentTo(roots));
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(file));
                Assert.That(sourceAvatar.GetComponentsInChildren<Transform>(true).SelectMany(value =>
                    new Object[] { value.gameObject }.Concat(value.GetComponents<Component>().Cast<Object>()))
                    .Where(value => value != null).Select(value => EditorJsonUtility.ToJson(value)).ToArray(), Is.EqualTo(sourceJson));
                Assert.That(skin.sharedMesh, Is.SameAs(persistedMesh));
                Assert.That(Enumerable.Range(0, persistedMesh.blendShapeCount).Select(skin.GetBlendShapeWeight).ToArray(), Is.EqualTo(weights));
                Assert.That(ProbeObjects(), Is.EqualTo(probes)); AssertDirectProbeSourceAssets(assets);
            }
            finally
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        [TestCase("menu", "duplicate", "unbound")]
        [TestCase("direct", "duplicate", "unbound")]
        [TestCase("menu", "duplicate", "implicit")]
        [TestCase("direct", "duplicate", "implicit")]
        [TestCase("menu", "duplicate", "stationary")]
        [TestCase("direct", "duplicate", "stationary")]
        [TestCase("menu", "missing", "implicit")]
        [TestCase("direct", "missing", "implicit")]
        [TestCase("menu", "missing", "stationary")]
        [TestCase("direct", "missing", "stationary")]
        public void FixedFacialFootprintDoesNotResolveUnrelatedDuplicateOrStaleSupportTargets(string route, string target, string support)
        {
            ConfigureCapturedMorphSkin(false);
            var inputs = new Dictionary<string, float> { ["Face"] = route == "menu" ? 1 : 0 };
            var before = NativeSkinnedVertices(inputs);
            AncillaryGraph();
            var unrelated = BaseShapeFixture.Create();
            try
            {
                if (target == "duplicate")
                {
                    FootprintRenderer("UnrelatedWardrobe", unrelated, 12);
                    FootprintRenderer("UnrelatedWardrobe", unrelated, 12);
                    Assert.That(avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).Count(renderer => renderer.name == "UnrelatedWardrobe"), Is.EqualTo(2));
                }
                else Assert.That(avatar.transform.Find("UnrelatedWardrobe"), Is.Null);
                AnimatorState supportEndpoint = null;
                AnimationClip supportClip = null;
                if (support != "unbound")
                {
                    var clip = new AnimationClip { name = "Unrelated native morph support" };
                    supportClip = clip;
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("UnrelatedWardrobe", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                        AnimationCurve.Constant(0, 1, 65));
                    AssetDatabase.AddObjectToAsset(clip, controller);
                    controller.AddLayer("Unrelated retained support"); var layers = controller.layers; layers[3].defaultWeight = 1;
                    var machine = layers[3].stateMachine;
                    var active = State(machine, "Unrelated support endpoint", clip); machine.defaultState = active;
                    supportEndpoint = active;
                    if (support == "implicit")
                    {
                        var empty = new AnimationClip { name = "Unrelated zero-curve initialization" }; AssetDatabase.AddObjectToAsset(empty, controller);
                        var initial = State(machine, "Implicit empty initialization", empty); machine.defaultState = initial;
                        controller.AddParameter("FootprintSupportEnabled", AnimatorControllerParameterType.Bool);
                        var parameters = controller.parameters;
                        parameters.Single(parameter => parameter.name == "FootprintSupportEnabled").defaultBool = true;
                        controller.parameters = parameters;
                        inputs["FootprintSupportEnabled"] = 1;
                        var enter = initial.AddTransition(active); enter.hasExitTime = false; enter.duration = 0;
                        enter.AddCondition(AnimatorConditionMode.If, 0, "FootprintSupportEnabled");
                        Assert.That(initial.writeDefaultValues || active.writeDefaultValues, Is.False);
                        Assert.That(AnimationUtility.GetCurveBindings(empty), Is.Empty);
                    }
                    controller.layers = layers;
                }
                var after = NativeSkinnedVertices(inputs);
                Assert.That(Math.Abs(after[0].x - before[0].x), Is.LessThan(.0001f));
                Assert.That(Math.Abs(after[1].x - before[1].x), Is.GreaterThan(.1f),
                    "The separate native appendage motion must remain active while its facial delta stays disjoint.");
                var expected = Native(controller, inputs, playable =>
                {
                    if (supportEndpoint == null) return;
                    Assert.That(playable.IsInTransition(3), Is.False);
                    Assert.That(playable.GetCurrentAnimatorStateInfo(3).shortNameHash, Is.EqualTo(Animator.StringToHash(supportEndpoint.name)));
                    Assert.That(playable.GetCurrentAnimatorClipInfo(3).Any(info => info.clip == supportClip && info.weight > .00001f), Is.True,
                        "The ignored stale or ambiguous binding must belong to an actually active native support motion.");
                });
                Assert.That(expected, Is.EqualTo(route == "menu" ? 70 : 55).Within(.01f));
                var assets = DirectProbeSourceAssets(); var sourceJson = EditorJsonUtility.ToJson(controller);
                var meshJson = EditorJsonUtility.ToJson(mesh); var unrelatedJson = EditorJsonUtility.ToJson(unrelated); var probes = ProbeObjects();
                var hierarchy = FootprintSourceHierarchy();
                var metadata = new VrChatExpressionMenu.Source { Controller = controller, DeferAncillaryGeometryValidation = true };
                metadata.Defaults["Face"] = 0;
                var entry = new VrChatExpressionMenu.Entry { Name = "Selectable facial endpoint" }; metadata.Entries.Add(entry);
                if (route == "menu") entry.Values.AddRange(VrChatExpressionSampler.SampleFixed(avatar, controller, metadata.Defaults,
                    new Dictionary<string, float> { ["Face"] = 1 }, metadata: metadata, geometryEntry: entry));
                else
                {
                    VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
                    VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar, controller, entry, 1, metadata: metadata);
                }
                Assert.That(entry.Values, Has.Count.EqualTo(1));
                Assert.That(entry.Values.Single().Path, Is.EqualTo("Face"));
                Assert.That(entry.Values.Single().Weight, Is.EqualTo(expected).Within(.01f));
                Assert.That(entry.Unevaluated, Is.Empty, "Stale unrelated support bindings do not become selectable expression targets.");
                var owned = new List<Mesh>(); var skin = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
                try
                {
                    using (var prepared = new PreparedExpressionBindings(avatar, metadata))
                    {
                        prepared.Capture(metadata); Assert.That(entry.Error, Is.Null);
                        Assert.That(entry.AncillaryGeometry.IsValidatedFor(entry), Is.True);
                        var result = VrChatExpressionBaker.Bake(avatar, avatar, metadata, owned, new List<string>(), prepared);
                        Assert.That(result, Has.Count.EqualTo(1)); Assert.That(result.Single().Targets, Has.Count.EqualTo(1));
                        var values = new Vector3[mesh.vertexCount];
                        skin.sharedMesh.GetBlendShapeFrameVertices(skin.sharedMesh.GetBlendShapeIndex(result.Single().Targets.Single()), 0, values, null, null);
                        Assert.That(values.Any(value => value.magnitude > .1f), Is.True);
                    }
                }
                finally { skin.sharedMesh = mesh; foreach (var value in owned) Object.DestroyImmediate(value); }
                Assert.That(EditorJsonUtility.ToJson(unrelated), Is.EqualTo(unrelatedJson));
                foreach (var value in hierarchy) Assert.That(EditorJsonUtility.ToJson(value.Key), Is.EqualTo(value.Value));
                AssertDirectProbeSourceAssets(assets); AssertUnchanged(sourceJson, meshJson, probes);
            }
            finally { Object.DestroyImmediate(unrelated); }
        }

        [TestCase("menu", "duplicate", "Face")]
        [TestCase("direct", "duplicate", "Face")]
        [TestCase("menu", "duplicate", "Eyes")]
        [TestCase("direct", "duplicate", "Eyes")]
        [TestCase("menu", "missing", "Face")]
        [TestCase("direct", "missing", "Face")]
        [TestCase("menu", "missing", "Eyes")]
        [TestCase("direct", "missing", "Eyes")]
        public void FixedFacialFootprintStillRejectsAmbiguousOrMissingSelectedAndInfluencingTargets(string route, string target, string path)
        {
            var extraMesh = BaseShapeFixture.Create();
            try
            {
                if (path == "Eyes")
                {
                    FootprintRenderer(path, extraMesh, 20);
                    foreach (var state in new[] { alternate, selected })
                        AnimationUtility.SetEditorCurve((AnimationClip)state.motion,
                            EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape.Face size"), AnimationCurve.Constant(0, 1, 80));
                }
                var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
                Assert.That(entry.Values.Any(value => value.Path == path), Is.True,
                    "The failing path is an explicit selected facial binding, not collateral support.");
                // Prime our own native reference before invalidating the scene
                // target. This does not guess how an ambiguous binding plays;
                // it establishes a stable source clip cache for the guard.
                Assert.That(float.IsNaN(Native(controller)), Is.False);
                if (target == "duplicate") FootprintRenderer(path, extraMesh, 20);
                else Object.DestroyImmediate(avatar.transform.Find(path).gameObject);
                var count = avatar.GetComponentsInChildren<Transform>(true).Count(value => AnimationUtility.CalculateTransformPath(value, avatar.transform) == path);
                Assert.That(count, Is.EqualTo(target == "duplicate" ? 2 : 0));
                var assets = DirectProbeSourceAssets(); var sourceJson = EditorJsonUtility.ToJson(controller);
                var meshJson = EditorJsonUtility.ToJson(mesh); var extraJson = EditorJsonUtility.ToJson(extraMesh); var probes = ProbeObjects();
                var hierarchy = FootprintSourceHierarchy();
                var metadata = new VrChatExpressionMenu.Source { Controller = controller, DeferAncillaryGeometryValidation = true };
                metadata.Defaults["Face"] = 0;
                var error = Assert.Throws<InvalidOperationException>(() =>
                {
                    if (route == "menu") VrChatExpressionSampler.SampleFixed(avatar, controller, metadata.Defaults,
                        new Dictionary<string, float> { ["Face"] = 1 }, metadata: metadata, geometryEntry: entry);
                    else VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar, controller, entry, 1, metadata: metadata);
                });
                Assert.That(error.Message, Does.Contain(path));
                Assert.That(entry.Values.Select(value => value.Weight), Is.All.EqualTo(80), "A missing or ambiguous selected target must not partially replace authored values.");
                foreach (var value in hierarchy) Assert.That(EditorJsonUtility.ToJson(value.Key), Is.EqualTo(value.Value));
                Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(sourceJson));
                Assert.That(EditorJsonUtility.ToJson(mesh), Is.EqualTo(meshJson));
                Assert.That(EditorJsonUtility.ToJson(extraMesh), Is.EqualTo(extraJson));
                Assert.That(ProbeObjects(), Is.EqualTo(probes)); AssertDirectProbeSourceAssets(assets);
            }
            finally { Object.DestroyImmediate(extraMesh); }
        }

        [TestCase("duplicate")]
        [TestCase("missing")]
        public void FixedFacialFootprintKeepsTargetsCausallyEnabledByTheKnownSelectedStateSet(string target)
        {
            ConfigureCapturedMorphSkin(false);
            var extraMesh = BaseShapeFixture.Create();
            try
            {
                FootprintRenderer("Eyes", extraMesh, 20);
                controller.AddParameter("EyeControl", AnimatorControllerParameterType.Bool);
                var rest = new AnimationClip { name = "Driven facial rest" }; var active = new AnimationClip { name = "Driven facial endpoint" };
                AnimationUtility.SetEditorCurve(rest, EditorCurveBinding.FloatCurve("Eyes", typeof(SkinnedMeshRenderer), "blendShape.Face size"), AnimationCurve.Constant(0, 1, 40));
                AnimationUtility.SetEditorCurve(active, EditorCurveBinding.FloatCurve("Eyes", typeof(SkinnedMeshRenderer), "blendShape.Face size"), AnimationCurve.Constant(0, 1, 70));
                AssetDatabase.AddObjectToAsset(rest, controller); AssetDatabase.AddObjectToAsset(active, controller);
                controller.AddLayer("Selected Set facial reader"); var layers = controller.layers; layers[2].defaultWeight = 1;
                var machine = layers[2].stateMachine; var initial = State(machine, "Driven default", rest); machine.defaultState = initial;
                var endpoint = State(machine, "Driven selected", active);
                var enter = initial.AddTransition(endpoint); enter.hasExitTime = false; enter.duration = 0;
                enter.AddCondition(AnimatorConditionMode.If, 0, "EyeControl"); controller.layers = layers;
                ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "EyeControl", 1));
                // Supply the known SDK Set value to the original native graph.
                // Its source face keeps the same fractional 55 while the
                // additional renderer genuinely changes from 40 to 70.
                NativeSkinnedVertices(new Dictionary<string, float> { ["EyeControl"] = 0 }, copy =>
                    Assert.That(copy.transform.Find("Eyes").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(40).Within(.01f)));
                NativeSkinnedVertices(new Dictionary<string, float> { ["EyeControl"] = 1 }, copy =>
                    Assert.That(copy.transform.Find("Eyes").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(70).Within(.01f)));
                Assert.That(Native(controller, new Dictionary<string, float> { ["EyeControl"] = 1 }), Is.EqualTo(55).Within(.01f));
                var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
                Assert.That(entry.Values.Single().Path, Is.EqualTo("Face"), "The driven renderer is causal, not an explicit direct-clip seed.");
                if (target == "duplicate") FootprintRenderer("Eyes", extraMesh, 20);
                else Object.DestroyImmediate(avatar.transform.Find("Eyes").gameObject);
                var assets = DirectProbeSourceAssets(); var sourceJson = EditorJsonUtility.ToJson(controller);
                var meshJson = EditorJsonUtility.ToJson(mesh); var extraJson = EditorJsonUtility.ToJson(extraMesh); var probes = ProbeObjects();
                var hierarchy = FootprintSourceHierarchy();
                var metadata = new VrChatExpressionMenu.Source { Controller = controller, DeferAncillaryGeometryValidation = true };
                metadata.Defaults["Face"] = 0;
                var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar,
                    controller, entry, 1, metadata: metadata, selectedState: selected));
                Assert.That(error.Message, Does.Contain("Eyes"));
                Assert.That(entry.Values.Single().Weight, Is.EqualTo(80));
                foreach (var value in hierarchy) Assert.That(EditorJsonUtility.ToJson(value.Key), Is.EqualTo(value.Value));
                Assert.That(EditorJsonUtility.ToJson(extraMesh), Is.EqualTo(extraJson));
                AssertDirectProbeSourceAssets(assets); AssertUnchanged(sourceJson, meshJson, probes);
            }
            finally { Object.DestroyImmediate(extraMesh); }
        }

        [TestCase("raw", "unbound-duplicate")]
        [TestCase("nondeferred", "unbound-duplicate")]
        [TestCase("raw", "stale-implicit")]
        [TestCase("nondeferred", "stale-implicit")]
        public void FacialFootprintBypassRequiresTheCompleteFixedDeferredPipeline(string route, string support)
        {
            var unrelated = BaseShapeFixture.Create();
            try
            {
                AnimatorState supportEndpoint = null;
                AnimationClip supportClip = null;
                var inputs = new Dictionary<string, float> { ["Face"] = 0 };
                if (support == "unbound-duplicate")
                {
                    FootprintRenderer("UnrelatedWardrobe", unrelated, 12);
                    FootprintRenderer("UnrelatedWardrobe", unrelated, 12);
                    Assert.That(avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).Count(renderer => renderer.name == "UnrelatedWardrobe"), Is.EqualTo(2));
                    Assert.That(controller.animationClips.SelectMany(AnimationUtility.GetCurveBindings).Any(binding => binding.path == "UnrelatedWardrobe"), Is.False,
                        "This duplicate is unbound, so the contrast checks the original global renderer resolution contract.");
                }
                else
                {
                    Assert.That(avatar.transform.Find("UnrelatedWardrobe"), Is.Null);
                    controller.AddParameter("FootprintSupportEnabled", AnimatorControllerParameterType.Bool);
                    var parameters = controller.parameters;
                    parameters.Single(parameter => parameter.name == "FootprintSupportEnabled").defaultBool = true;
                    controller.parameters = parameters; inputs["FootprintSupportEnabled"] = 1;
                    supportClip = new AnimationClip { name = "Stale implicit native wardrobe morph" };
                    AnimationUtility.SetEditorCurve(supportClip,
                        EditorCurveBinding.FloatCurve("UnrelatedWardrobe", typeof(SkinnedMeshRenderer), "blendShape.Face size"), AnimationCurve.Constant(0, 1, 65));
                    AssetDatabase.AddObjectToAsset(supportClip, controller);
                    var empty = new AnimationClip { name = "Implicit wardrobe initialization" }; AssetDatabase.AddObjectToAsset(empty, controller);
                    controller.AddLayer("Implicit stale support"); var layers = controller.layers; layers[2].defaultWeight = 1;
                    var machine = layers[2].stateMachine; var initial = State(machine, "Empty wardrobe state", empty); machine.defaultState = initial;
                    supportEndpoint = State(machine, "Active stale wardrobe state", supportClip);
                    var enter = initial.AddTransition(supportEndpoint); enter.hasExitTime = false; enter.duration = 0;
                    enter.AddCondition(AnimatorConditionMode.If, 0, "FootprintSupportEnabled"); controller.layers = layers;
                    Assert.That(AnimationUtility.GetCurveBindings(empty), Is.Empty);
                    Assert.That(initial.writeDefaultValues || supportEndpoint.writeDefaultValues, Is.False);
                }
                Assert.That(Native(controller, inputs, playable =>
                {
                    if (supportEndpoint == null) return;
                    Assert.That(playable.IsInTransition(2), Is.False);
                    Assert.That(playable.GetCurrentAnimatorStateInfo(2).shortNameHash, Is.EqualTo(Animator.StringToHash(supportEndpoint.name)));
                    Assert.That(playable.GetCurrentAnimatorClipInfo(2).Any(info => info.clip == supportClip && info.weight > .00001f), Is.True);
                }), Is.EqualTo(55).Within(.01f), "The original native facial pose is still valid despite the unrelated source targets.");
                var assets = DirectProbeSourceAssets(); var sourceJson = EditorJsonUtility.ToJson(controller);
                var meshJson = EditorJsonUtility.ToJson(mesh); var unrelatedJson = EditorJsonUtility.ToJson(unrelated); var probes = ProbeObjects();
                var hierarchy = FootprintSourceHierarchy();
                var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
                var metadata = new VrChatExpressionMenu.Source { Controller = controller, DeferAncillaryGeometryValidation = false };
                metadata.Defaults["Face"] = 0;
                if (support == "stale-implicit") metadata.Defaults["FootprintSupportEnabled"] = 1;
                var error = Assert.Throws<InvalidOperationException>(() =>
                {
                    if (route == "raw") VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1, metadata: metadata);
                    else VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar, controller, entry, 1, metadata: metadata);
                });
                Assert.That(error.Message, Does.Contain("UnrelatedWardrobe"));
                Assert.That(entry.Values.Single().Weight, Is.EqualTo(80), "The strict legacy route must leave the authored clip values intact when a target cannot be resolved.");
                Assert.That(entry.AncillaryGeometry, Is.Null, "The caller supplied no complete prepared geometry validation seam.");
                foreach (var value in hierarchy) Assert.That(EditorJsonUtility.ToJson(value.Key), Is.EqualTo(value.Value));
                Assert.That(EditorJsonUtility.ToJson(unrelated), Is.EqualTo(unrelatedJson));
                AssertDirectProbeSourceAssets(assets); AssertUnchanged(sourceJson, meshJson, probes);
            }
            finally { Object.DestroyImmediate(unrelated); }
        }

        [TestCase("same-default")]
        [TestCase("same-normalized")]
        [TestCase("unreachable-only")]
        [TestCase("derived-chain")]
        [TestCase("other-only-gate")]
        [TestCase("second-other-writer")]
        [TestCase("fixed-true")]
        [TestCase("unknown-input")]
        [TestCase("mutable-gate")]
        [TestCase("changed-default")]
        [TestCase("changed-selection")]
        [TestCase("add")]
        [TestCase("copy")]
        [TestCase("random")]
        [TestCase("curve")]
        [TestCase("omitted-selected")]
        [TestCase("shared-type")]
        [TestCase("shared-default")]
        [TestCase("unknown-callback")]
        [TestCase("mutual-gates")]
        [TestCase("raw")]
        public void OtherPlayableStageSetProofKeepsAnUnreachableBodyMotionOutOfTheFacialEndpoint(string mode)
        {
            var allowed = mode == "same-default" || mode == "same-normalized" || mode == "unreachable-only" || mode == "derived-chain" || mode == "other-only-gate";
            var gate = mode == "unknown-input" ? "EyeHeightAsMeters" : mode == "mutable-gate" ? "MutableSleepGate" : "AFK";
            var gateType = mode == "unknown-input" ? AnimatorControllerParameterType.Float : AnimatorControllerParameterType.Bool;
            if (mode != "other-only-gate") controller.AddParameter(gate, gateType);
            controller.AddParameter("ActionStage", AnimatorControllerParameterType.Int);
            var dormant = new AnimationClip { name = "Empty native body default" }; AssetDatabase.AddObjectToAsset(dormant, controller);
            var sleep = Clip("Unreachable body and face motion", 75);
            AnimationUtility.SetEditorCurve(sleep, EditorCurveBinding.FloatCurve("Face", typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Constant(0, 1, 2));
            controller.AddLayer("Body stage support"); var layers = controller.layers; layers[2].defaultWeight = 1;
            var body = layers[2].stateMachine; var awake = State(body, "Native awake body", dormant); body.defaultState = awake;
            var sleeping = State(body, "Native sleeping body", sleep);
            var bodyEnter = body.AddAnyStateTransition(sleeping); bodyEnter.hasExitTime = false; bodyEnter.duration = 0;
            bodyEnter.canTransitionToSelf = false; bodyEnter.AddCondition(AnimatorConditionMode.Equals, 10, "ActionStage");
            controller.layers = layers;
            Assert.That(AnimationUtility.GetCurveBindings(dormant), Is.Empty);
            Assert.That(awake.writeDefaultValues || sleeping.writeDefaultValues, Is.False);

            var other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Action.controller");
            other.AddParameter(gate, gateType);
            other.AddParameter("ActionStage", mode == "shared-type" ? AnimatorControllerParameterType.Float : AnimatorControllerParameterType.Int);
            if (mode == "shared-default")
            {
                var parameters = other.parameters; parameters.Single(parameter => parameter.name == "ActionStage").defaultInt = 1;
                other.parameters = parameters;
            }
            var machine = other.layers[0].stateMachine; var idle = State(machine, "Action blend out", null); machine.defaultState = idle;
            var nonzero = State(machine, "Action sleeping stage", null);
            var enter = machine.AddAnyStateTransition(nonzero); enter.hasExitTime = false; enter.duration = 0; enter.canTransitionToSelf = false;
            enter.AddCondition(gateType == AnimatorControllerParameterType.Bool ? AnimatorConditionMode.If : AnimatorConditionMode.Greater,
                gateType == AnimatorControllerParameterType.Bool ? 0 : .5f, gate);
            ParameterDriverExpressionTests.Driver(nonzero, ParameterDriverExpressionTests.Op("Set", "ActionStage", 10));
            if (mode != "unreachable-only" && mode != "mutual-gates")
                ParameterDriverExpressionTests.Driver(idle, ParameterDriverExpressionTests.Op("Set", "ActionStage", mode == "same-normalized" ? .9f : 0));
            if (mode == "add" || mode == "copy" || mode == "random")
            {
                idle.behaviours = Array.Empty<StateMachineBehaviour>();
                if (mode == "copy") other.AddParameter("CopySeed", AnimatorControllerParameterType.Int);
                ParameterDriverExpressionTests.Driver(idle, ParameterDriverExpressionTests.Op(mode == "add" ? "Add" : mode == "copy" ? "Copy" : "Random",
                    "ActionStage", 0, mode == "copy" ? "CopySeed" : null));
            }
            if (mode == "curve")
            {
                var writer = new AnimationClip { name = "Native scalar stage curve" };
                AnimationUtility.SetEditorCurve(writer, EditorCurveBinding.FloatCurve("", typeof(Animator), "ActionStage"), AnimationCurve.Constant(0, 1, 0));
                AssetDatabase.AddObjectToAsset(writer, other); idle.motion = writer;
            }
            if (mode == "mutable-gate") ParameterDriverExpressionTests.Driver(lower, ParameterDriverExpressionTests.Op("Set", gate, 1));
            if (mode == "omitted-selected") ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "ActionStage", 0));
            if (mode == "unknown-callback")
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAnimatorLayerControl"))
                    .FirstOrDefault(value => value != null);
                if (type == null) Assert.Ignore("Install the real VRChat SDK.");
                idle.AddStateMachineBehaviour(type);
            }
            if (mode == "derived-chain" || mode == "mutual-gates")
            {
                other.AddParameter("CompanionStage", AnimatorControllerParameterType.Int);
                enter.conditions = new[] { new AnimatorCondition { mode = AnimatorConditionMode.Equals, threshold = 1, parameter = "CompanionStage" } };
                var companion = State(machine, "Action companion writer", null);
                ParameterDriverExpressionTests.Driver(companion, ParameterDriverExpressionTests.Op("Set", "CompanionStage", 1));
                var change = machine.AddAnyStateTransition(companion); change.hasExitTime = false; change.duration = 0; change.canTransitionToSelf = false;
                if (mode == "derived-chain")
                {
                    ParameterDriverExpressionTests.Driver(idle, ParameterDriverExpressionTests.Op("Set", "CompanionStage", 0));
                    change.AddCondition(AnimatorConditionMode.If, 0, gate);
                }
                else change.AddCondition(AnimatorConditionMode.Equals, 10, "ActionStage");
            }
            if (mode == "other-only-gate")
            {
                other.AddParameter("UnrelatedContactInput", AnimatorControllerParameterType.Float);
                other.AddLayer("Unrelated external reader"); var otherLayers = other.layers; otherLayers[1].defaultWeight = 1;
                var unrelatedMachine = otherLayers[1].stateMachine;
                var unrelatedIdle = State(unrelatedMachine, "Unrelated normal state", null); unrelatedMachine.defaultState = unrelatedIdle;
                var unrelatedActive = State(unrelatedMachine, "Unrelated external state", null);
                var unrelatedEnter = unrelatedIdle.AddTransition(unrelatedActive); unrelatedEnter.hasExitTime = false; unrelatedEnter.duration = 0;
                unrelatedEnter.AddCondition(AnimatorConditionMode.Greater, .5f, "UnrelatedContactInput"); other.layers = otherLayers;
                Assert.That(controller.parameters.Any(parameter => parameter.name == gate || parameter.name == "UnrelatedContactInput"), Is.False);
            }
            AnimatorController secondOther = null;
            if (mode == "second-other-writer")
            {
                secondOther = AnimatorController.CreateAnimatorControllerAtPath(folder + "/SecondAction.controller");
                secondOther.AddParameter("ActionStage", AnimatorControllerParameterType.Int);
                var secondMachine = secondOther.layers[0].stateMachine;
                var secondIdle = State(secondMachine, "Second reachable noninitial Set", null); secondMachine.defaultState = secondIdle;
                ParameterDriverExpressionTests.Driver(secondIdle, ParameterDriverExpressionTests.Op("Set", "ActionStage", 1));
                Native(secondOther, null, playable =>
                {
                    Assert.That(playable.GetCurrentAnimatorStateInfo(0).shortNameHash, Is.EqualTo(Animator.StringToHash(secondIdle.name)));
                    Assert.That(playable.GetInteger("ActionStage"), Is.Zero, "SDK callbacks are supplied as data to the exporter, never guessed from the Editor graph.");
                });
            }
            // SDK Set commands do not execute in the Editor reference graph.
            // Observe the original Action states independently, and supply the
            // known initial/counterfactual stage once to the original FX graph.
            // The real body motion shares the captured facial renderer and must
            // never be accepted as unrelated ancillary Transform animation.
            var actionInputs = new Dictionary<string, float> { [gate] = 0 };
            Native(other, actionInputs, playable =>
            {
                Assert.That(playable.GetCurrentAnimatorStateInfo(0).shortNameHash, Is.EqualTo(Animator.StringToHash(idle.name)));
                var value = mode == "shared-type" ? playable.GetFloat("ActionStage") : playable.GetInteger("ActionStage");
                Assert.That(float.IsNaN(value) || float.IsInfinity(value), Is.False);
                Assert.That(value, Is.EqualTo(mode == "shared-default" ? 1 : 0));
            });
            if (mode != "derived-chain" && mode != "mutual-gates")
                Native(other, new Dictionary<string, float> { [gate] = 1 }, playable =>
                    Assert.That(playable.GetCurrentAnimatorStateInfo(0).shortNameHash, Is.EqualTo(Animator.StringToHash(nonzero.name))));
            Assert.That(Native(controller, new Dictionary<string, float> { ["ActionStage"] = 0 }, playable =>
            {
                Assert.That(playable.GetCurrentAnimatorStateInfo(2).shortNameHash, Is.EqualTo(Animator.StringToHash(awake.name)));
                Assert.That(playable.GetCurrentAnimatorClipInfo(2).Any(info => info.clip == sleep && info.weight > .00001f), Is.False);
            }), Is.EqualTo(55).Within(.01f));
            Assert.That(Native(controller, new Dictionary<string, float> { ["ActionStage"] = 10 }, playable =>
            {
                Assert.That(playable.IsInTransition(2), Is.False);
                Assert.That(playable.GetCurrentAnimatorStateInfo(2).shortNameHash, Is.EqualTo(Animator.StringToHash(sleeping.name)));
                Assert.That(playable.GetCurrentAnimatorClipInfo(2).Any(info => info.clip == sleep && info.weight > .00001f), Is.True);
            }), Is.EqualTo(75).Within(.01f));
            var assets = DirectProbeSourceAssets();
            var otherAssets = AssetDatabase.LoadAllAssetsAtPath(folder + "/Action.controller")
                .Concat(secondOther == null ? Array.Empty<Object>() : AssetDatabase.LoadAllAssetsAtPath(folder + "/SecondAction.controller")).ToArray();
            foreach (var clip in otherAssets.OfType<AnimationClip>()) { AnimationUtility.GetCurveBindings(clip); AnimationUtility.GetObjectReferenceCurveBindings(clip); }
            var otherJson = otherAssets.ToDictionary(value => value, value => EditorJsonUtility.ToJson(value));
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            var hierarchy = FootprintSourceHierarchy();
            var metadata = new VrChatExpressionMenu.Source { Controller = controller, DeferAncillaryGeometryValidation = true };
            metadata.OtherControllers.Add(other); metadata.Defaults["Face"] = 0;
            if (secondOther != null) metadata.OtherControllers.Add(secondOther);
            if (mode == "other-only-gate") metadata.ExternalParameters.Add("UnrelatedContactInput");
            if (mode == "changed-default") metadata.Defaults["ActionStage"] = 1;
            var entry = new VrChatExpressionMenu.Entry { Name = "Native facial stage endpoint" }; metadata.Entries.Add(entry);
            VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var context = FixedExpressionContext.Create(controller, metadata.Defaults, metadata);
            if (mode == "fixed-true") context.Values[gate] = 1;
            Action sample = () =>
            {
                if (mode == "changed-selection") VrChatExpressionSampler.SampleFixed(avatar, controller, metadata.Defaults,
                    new Dictionary<string, float> { ["Face"] = 1, ["ActionStage"] = 1 }, metadata: metadata, fixedContext: context, geometryEntry: entry);
                else VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1, metadata: metadata,
                    fixedContext: mode == "raw" ? null : context);
            };
            if (allowed)
            {
                sample(); Assert.That(entry.Values, Has.Count.EqualTo(1));
                Assert.That(entry.Values.Single().Path, Is.EqualTo("Face")); Assert.That(entry.Values.Single().Weight, Is.EqualTo(55).Within(.01f));
                Assert.That(context.UsedParameters, Does.Contain("AFK"), "The external gate used to derive the retained stage constant must be reported.");
                Assert.That(context.ReportedValue("AFK"), Is.Zero);
                Assert.That(context.UsedParameters, Does.Not.Contain("UnrelatedContactInput"));
                if (mode == "other-only-gate")
                {
                    Assert.That(context.Values.ContainsKey("AFK"), Is.False, "Reporting cannot inject an Other-only input into the FX evaluation environment.");
                    Assert.That(context.ReportedInputs["AFK"], Is.Zero);
                    Assert.That(context.ReportedInputs.ContainsKey("UnrelatedContactInput"), Is.False,
                        "A reader in an unrelated Other layer is not evidence for this derived constant.");
                    var preview = new VrChatExpressionMenu.Entry { Name = "Reported selectable native face" };
                    VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, preview);
                    VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar, controller, preview, 1, metadata: metadata);
                    Assert.That(preview.Values.Single().Weight, Is.EqualTo(55).Within(.01f));
                    Assert.That(preview.Messages.Any(message => message.Contains("AFK=0")), Is.True,
                        "The public fixed wrapper must surface the assumption, even when it is declared only by an Other controller.");
                    Assert.That(preview.Messages.Any(message => message.Contains("UnrelatedContactInput")), Is.False);
                }
                var owned = new List<Mesh>(); var skin = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
                try
                {
                    using (var prepared = new PreparedExpressionBindings(avatar, metadata))
                    {
                        prepared.Capture(metadata); Assert.That(entry.Error, Is.Null);
                        Assert.That(entry.AncillaryGeometry.IsValidatedFor(entry), Is.True);
                        var result = VrChatExpressionBaker.Bake(avatar, avatar, metadata, owned, new List<string>(), prepared);
                        Assert.That(result, Has.Count.EqualTo(1)); Assert.That(result.Single().Targets, Has.Count.EqualTo(1));
                    }
                }
                finally { skin.sharedMesh = mesh; foreach (var value in owned) Object.DestroyImmediate(value); }
            }
            else
            {
                var error = Assert.Throws<InvalidOperationException>(() => sample());
                Assert.That(error.Message, Does.Contain(mode == "unknown-callback" ? "VRCAnimatorLayerControl" : "ActionStage"));
                Assert.That(entry.Values.Single().Weight, Is.EqualTo(80), "Unproven writers cannot partially rewrite the registered facial clip.");
            }
            foreach (var value in hierarchy) Assert.That(EditorJsonUtility.ToJson(value.Key), Is.EqualTo(value.Value));
            foreach (var value in otherJson) Assert.That(EditorJsonUtility.ToJson(value.Key), Is.EqualTo(value.Value));
            AssertDirectProbeSourceAssets(assets); AssertUnchanged(sourceJson, meshJson, probes);
        }

        [TestCase("absent")]
        [TestCase("absent-nested")]
        [TestCase("present")]
        [TestCase("ancestor")]
        [TestCase("duplicate")]
        [TestCase("absent-transform")]
        [TestCase("nonfinite")]
        [TestCase("event")]
        [TestCase("object")]
        [TestCase("unknown-callback")]
        [TestCase("raw")]
        [TestCase("nondeferred")]
        public void AbsentActivationDurationMotionDoesNotBlockACompleteFixedFacialEntry(string kind)
        {
            var allowed = kind == "absent" || kind == "absent-nested";
            var path = kind == "ancestor" ? "" : kind == "absent-nested" ? "DurationGroup/DurationTarget" : "DurationTarget";
            if (kind == "absent-nested") new GameObject("DurationGroup").transform.SetParent(avatar.transform, false);
            if (kind == "present" || kind == "duplicate")
            {
                new GameObject(path).transform.SetParent(avatar.transform, false);
                if (kind == "duplicate") new GameObject(path).transform.SetParent(avatar.transform, false);
            }
            var binding = EditorCurveBinding.FloatCurve(path, typeof(GameObject), "m_IsActive");
            var duration = new AnimationClip { name = "One frame native control duration" };
            AnimationUtility.SetEditorCurve(duration, binding, new AnimationCurve(new Keyframe(1f / 60f, kind == "ancestor" ? 1 : 0)));
            AssetDatabase.AddObjectToAsset(duration, controller);
            controller.AddLayer("Native scalar expression control"); var layers = controller.layers; layers[2].defaultWeight = 1;
            var machine = layers[2].stateMachine;
            var initial = State(machine, "Native control initialization", duration); machine.defaultState = initial;
            var endpoint = State(machine, "Native selected scalar control", duration);
            var enter = initial.AddTransition(endpoint); enter.hasExitTime = false; enter.duration = 0;
            enter.AddCondition(AnimatorConditionMode.Equals, 1, "Face"); controller.layers = layers;
            ParameterDriverExpressionTests.Driver(initial, ParameterDriverExpressionTests.Op("Set", "Face", 1));
            ParameterDriverExpressionTests.Driver(endpoint, ParameterDriverExpressionTests.Op("Set", "Face", 1));
            Assert.That(AnimationUtility.GetCurveBindings(duration), Has.Length.EqualTo(1));
            Assert.That(duration.length, Is.EqualTo(1f / 60f).Within(.00001f));
            var count = avatar.GetComponentsInChildren<Transform>(true)
                .Count(value => AnimationUtility.CalculateTransformPath(value, avatar.transform) == path);
            Assert.That(count, Is.EqualTo(kind == "duplicate" ? 2 : kind == "present" || kind == "ancestor" ? 1 : 0));
            if (allowed) Assert.That(AnimationUtility.GetAnimatedObject(avatar, binding), Is.Null,
                "The activation target must be absent according to Unity's actual animation binding lookup too.");
            // The native Editor graph does not run SDK Set callbacks. Supply
            // their known value once to the original graph, then observe the
            // actual selected control state and fractional facial geometry.
            var expected = Native(controller, new Dictionary<string, float> { ["Face"] = 1 }, playable =>
            {
                Assert.That(playable.IsInTransition(2), Is.False);
                Assert.That(playable.GetCurrentAnimatorStateInfo(2).shortNameHash, Is.EqualTo(Animator.StringToHash(endpoint.name)));
                Assert.That(playable.GetCurrentAnimatorClipInfo(2).Any(info => info.clip == duration && info.weight > .00001f), Is.True);
            });
            Assert.That(expected, Is.EqualTo(70).Within(.01f));
            if (kind == "absent-transform")
                AnimationUtility.SetEditorCurve(duration, EditorCurveBinding.FloatCurve(path, typeof(Transform), "m_LocalPosition.x"),
                    AnimationCurve.Constant(0, 1f / 60f, 1));
            else if (kind == "nonfinite")
            {
                AnimationUtility.SetEditorCurve(duration, binding, AnimationCurve.Constant(0, 1f / 60f, float.NaN));
                Assert.That(AnimationUtility.GetCurveBindings(duration).Contains(binding), Is.True);
                var retained = AnimationUtility.GetEditorCurve(duration, binding);
                Assert.That(retained == null || retained.length == 0 || retained.keys.Any(key => float.IsNaN(key.value) || float.IsInfinity(key.value)), Is.True,
                    "The native float binding must still declare malformed data before the fatal assertion.");
            }
            else if (kind == "event")
                AnimationUtility.SetAnimationEvents(duration, new[] { new AnimationEvent { time = 0, functionName = "FutureCallback" } });
            else if (kind == "object")
                AnimationUtility.SetObjectReferenceCurve(duration, EditorCurveBinding.PPtrCurve("Face", typeof(SkinnedMeshRenderer), "m_Mesh"),
                    new[] { new ObjectReferenceKeyframe { time = 0, value = mesh } });
            else if (kind == "unknown-callback")
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAnimatorLayerControl"))
                    .FirstOrDefault(value => value != null);
                if (type == null) Assert.Ignore("Install the real VRChat SDK.");
                endpoint.AddStateMachineBehaviour(type);
            }
            if (kind == "event" || kind == "object" || kind == "absent-transform")
                Assert.That(Native(controller, new Dictionary<string, float> { ["Face"] = 1 }), Is.EqualTo(expected).Within(.01f),
                    "Warm the native reference caches after harmless source fixture changes; events remain disabled.");
            var assets = DirectProbeSourceAssets(); var sourceJson = EditorJsonUtility.ToJson(controller);
            var meshJson = EditorJsonUtility.ToJson(mesh); var hierarchy = FootprintSourceHierarchy(); var probes = ProbeObjects();
            var metadata = new VrChatExpressionMenu.Source { Controller = controller, DeferAncillaryGeometryValidation = kind != "nondeferred" };
            metadata.Defaults["Face"] = 0;
            var entry = new VrChatExpressionMenu.Entry { Name = "Selectable native scalar face" }; metadata.Entries.Add(entry);
            VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            Action sample = () =>
            {
                var selection = new Dictionary<string, float> { ["Face"] = 1 };
                var values = kind == "raw"
                    ? VrChatExpressionSampler.Sample(avatar, controller, metadata.Defaults, selection, metadata: metadata)
                    : VrChatExpressionSampler.SampleFixed(avatar, controller, metadata.Defaults, selection, metadata: metadata, geometryEntry: entry);
                entry.Values.Clear(); entry.Values.AddRange(values);
            };
            if (allowed)
            {
                sample(); Assert.That(entry.Values, Has.Count.EqualTo(1));
                Assert.That(entry.Values.Single().Path, Is.EqualTo("Face"));
                Assert.That(entry.Values.Single().Weight, Is.EqualTo(expected).Within(.01f));
                Assert.That(entry.Unevaluated, Is.Empty);
                var owned = new List<Mesh>(); var skin = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
                try
                {
                    using (var prepared = new PreparedExpressionBindings(avatar, metadata))
                    {
                        prepared.Capture(metadata); Assert.That(entry.Error, Is.Null);
                        Assert.That(entry.AncillaryGeometry.IsValidatedFor(entry), Is.True);
                        var result = VrChatExpressionBaker.Bake(avatar, avatar, metadata, owned, new List<string>(), prepared);
                        Assert.That(result, Has.Count.EqualTo(1)); Assert.That(result.Single().Targets, Has.Count.EqualTo(1));
                        var delta = new Vector3[mesh.vertexCount];
                        skin.sharedMesh.GetBlendShapeFrameVertices(skin.sharedMesh.GetBlendShapeIndex(result.Single().Targets.Single()), 0, delta, null, null);
                        Assert.That(delta.Any(value => value.magnitude > .1f), Is.True,
                            "The retained selectable expression must emit a real geometry binding, not merely an entry name.");
                    }
                }
                finally { skin.sharedMesh = mesh; foreach (var value in owned) Object.DestroyImmediate(value); }
            }
            else
            {
                var error = Assert.Throws<InvalidOperationException>(() => sample());
                Assert.That(error.Message, Does.Contain(kind == "nonfinite" ? "不正な値" : kind == "event" ? "影響範囲" :
                    kind == "object" ? "差し替え" : kind == "unknown-callback" ? "VRCAnimatorLayerControl" : "BlendShape以外"));
                Assert.That(entry.Values.Single().Weight, Is.EqualTo(80), "A rejected control capability cannot partially replace the authored facial value.");
            }
            foreach (var value in hierarchy) Assert.That(EditorJsonUtility.ToJson(value.Key), Is.EqualTo(value.Value));
            AssertDirectProbeSourceAssets(assets); AssertUnchanged(sourceJson, meshJson, probes);
        }


        [TestCase("valid")]
        [TestCase("fresh-replacement")]
        [TestCase("duplicate")]
        [TestCase("inactive-duplicate")]
        [TestCase("missing")]
        [TestCase("slash")]
        [TestCase("disabled")]
        [TestCase("inactive")]
        public void ExactNativeRendererLookupRemainsLocalToEachEvaluationAndKeepsStrictBoundaries(string kind)
        {
            Assert.That(Native(controller), Is.EqualTo(55).Within(.01f));
            Assert.That(Native(controller, new Dictionary<string, float> { ["Face"] = 1 }), Is.EqualTo(70).Within(.01f));
            var firstAssets = DirectProbeSourceAssets(); var firstController = EditorJsonUtility.ToJson(controller);
            var firstMesh = EditorJsonUtility.ToJson(mesh); var firstHierarchy = FootprintSourceHierarchy(); var firstProbes = ProbeObjects();
            var metadata = new VrChatExpressionMenu.Source { Controller = controller, DeferAncillaryGeometryValidation = true };
            metadata.Defaults["Face"] = 0;
            var initial = new VrChatExpressionMenu.Entry { Name = "First native renderer scope" };
            var first = VrChatExpressionSampler.SampleFixed(avatar, controller, metadata.Defaults,
                new Dictionary<string, float> { ["Face"] = 0 }, metadata: metadata, geometryEntry: initial);
            Assert.That(first.Single().Weight, Is.EqualTo(55).Within(.01f));
            foreach (var value in firstHierarchy) Assert.That(EditorJsonUtility.ToJson(value.Key), Is.EqualTo(value.Value));
            AssertDirectProbeSourceAssets(firstAssets); AssertUnchanged(firstController, firstMesh, firstProbes);

            var face = avatar.transform.Find("Face").GetComponent<SkinnedMeshRenderer>();
            if (kind == "fresh-replacement")
            {
                var old = face; Object.DestroyImmediate(face.gameObject);
                face = FootprintRenderer("Face", mesh, 20);
                Assert.That(old == null, Is.True); Assert.That(face, Is.Not.SameAs(old));
            }
            else if (kind == "duplicate" || kind == "inactive-duplicate")
            {
                var duplicate = new GameObject("Face"); duplicate.transform.SetParent(avatar.transform, false);
                if (kind == "duplicate") { duplicate.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh; }
                else duplicate.SetActive(false);
                Assert.That(avatar.GetComponentsInChildren<Transform>(true).Count(value =>
                    AnimationUtility.CalculateTransformPath(value, avatar.transform) == "Face"), Is.EqualTo(2),
                    "An inactive duplicate transform without a Renderer must still make the binding ambiguous.");
            }
            else if (kind == "missing") Object.DestroyImmediate(face.gameObject);
            else if (kind == "disabled") face.enabled = false;
            else if (kind == "inactive") face.gameObject.SetActive(false);
            else if (kind == "slash")
            {
                face.name = "Face/Part";
                var old = EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size");
                var renamed = EditorCurveBinding.FloatCurve("Face/Part", typeof(SkinnedMeshRenderer), "blendShape.Face size");
                foreach (var clip in new[] { (AnimationClip)lower.motion, (AnimationClip)alternate.motion, (AnimationClip)selected.motion })
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, old);
                    AnimationUtility.SetEditorCurve(clip, old, null); AnimationUtility.SetEditorCurve(clip, renamed, curve);
                }
                var native = Native(controller, new Dictionary<string, float> { ["Face"] = 1 });
                Assert.That(float.IsNaN(native) || float.IsInfinity(native), Is.False,
                    "Prime changed source clip caches through the independent graph before preservation checks. Native binding does not prove an unambiguous portable path.");
            }
            var allowed = kind == "valid" || kind == "fresh-replacement";
            if (allowed) Assert.That(Native(controller, new Dictionary<string, float> { ["Face"] = 1 }), Is.EqualTo(70).Within(.01f),
                "The fresh native Animator must bind the current Renderer identity, rather than a renderer from the previous probe.");
            var assets = DirectProbeSourceAssets(); var sourceJson = EditorJsonUtility.ToJson(controller);
            var meshJson = EditorJsonUtility.ToJson(mesh); var hierarchy = FootprintSourceHierarchy(); var probes = ProbeObjects();
            var entry = new VrChatExpressionMenu.Entry { Name = "Second native renderer scope" }; metadata.Entries.Add(entry);
            entry.Values.Add(new VrChatExpressionMenu.MorphValue { Path = kind == "slash" ? "Face/Part" : "Face", Shape = "Face size", Weight = 80 });
            Action sample = () =>
            {
                var values = VrChatExpressionSampler.SampleFixed(avatar, controller, metadata.Defaults,
                    new Dictionary<string, float> { ["Face"] = 1 }, metadata: metadata, geometryEntry: entry);
                entry.Values.Clear(); entry.Values.AddRange(values);
            };
            if (allowed)
            {
                sample(); Assert.That(entry.Values.Single().Path, Is.EqualTo("Face"));
                Assert.That(entry.Values.Single().Weight, Is.EqualTo(70).Within(.01f));
                Assert.That(entry.Unevaluated, Is.Empty);
                var owned = new List<Mesh>(); var skin = face;
                try
                {
                    using (var prepared = new PreparedExpressionBindings(avatar, metadata))
                    {
                        prepared.Capture(metadata); Assert.That(entry.Error, Is.Null);
                        Assert.That(entry.AncillaryGeometry.IsValidatedFor(entry), Is.True);
                        var result = VrChatExpressionBaker.Bake(avatar, avatar, metadata, owned, new List<string>(), prepared);
                        Assert.That(result, Has.Count.EqualTo(1)); Assert.That(result.Single().Targets, Has.Count.EqualTo(1));
                        var delta = new Vector3[mesh.vertexCount];
                        skin.sharedMesh.GetBlendShapeFrameVertices(skin.sharedMesh.GetBlendShapeIndex(result.Single().Targets.Single()), 0, delta, null, null);
                        Assert.That(delta.Any(value => value.magnitude > .1f), Is.True);
                    }
                }
                finally { skin.sharedMesh = mesh; foreach (var value in owned) Object.DestroyImmediate(value); }
            }
            else
            {
                var error = Assert.Throws<InvalidOperationException>(() => sample());
                Assert.That(error.Message, Does.Contain(kind == "duplicate" || kind == "inactive-duplicate" ? "重複する階層パス" :
                    kind == "slash" ? "名前に /" : kind == "disabled" || kind == "inactive" ? "非表示のRenderer" : "Rendererのパス"));
                Assert.That(entry.Values.Single().Weight, Is.EqualTo(80), "A failed exact lookup cannot partially replace the authored entry.");
            }
            foreach (var value in hierarchy) Assert.That(EditorJsonUtility.ToJson(value.Key), Is.EqualTo(value.Value));
            AssertDirectProbeSourceAssets(assets);
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(sourceJson)); Assert.That(EditorJsonUtility.ToJson(mesh), Is.EqualTo(meshJson));
            Assert.That(ProbeObjects(), Is.EqualTo(probes));
            if (face != null) Assert.That(face.GetBlendShapeWeight(0), Is.EqualTo(20));
        }

        [TestCase("null-mesh")]
        [TestCase("changed-mesh")]
        [TestCase("moved-identity")]
        public void LocalRendererLookupKeepsLiveMeshAndIdentityChecksWithoutFallback(string kind)
        {
            var face = avatar.transform.Find("Face").GetComponent<SkinnedMeshRenderer>();
            var lookup = new ExpressionRendererLookup(avatar);
            Assert.That(lookup.Resolve("Face"), Is.SameAs(VrChatExpressionSampler.FindRenderer(avatar, "Face")));
            var other = Object.Instantiate(mesh);
            try
            {
                if (kind == "null-mesh") face.sharedMesh = null;
                else if (kind == "changed-mesh") face.sharedMesh = other;
                else
                {
                    var group = new GameObject("MovedGroup"); group.transform.SetParent(avatar.transform, false);
                    face.transform.SetParent(group.transform, false);
                }
                var hierarchy = FootprintSourceHierarchy(); var source = EditorJsonUtility.ToJson(controller);
                var originalMesh = EditorJsonUtility.ToJson(mesh); var changedMesh = EditorJsonUtility.ToJson(other);
                if (kind == "changed-mesh")
                {
                    Assert.That(lookup.Resolve("Face"), Is.SameAs(face));
                    Assert.That(lookup.Resolve("Face").sharedMesh, Is.SameAs(other), "Do not cache an old mesh identity with the path.");
                }
                else
                {
                    var cached = Assert.Throws<InvalidOperationException>(() => lookup.Resolve("Face"));
                    var uncached = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.FindRenderer(avatar, "Face"));
                    Assert.That(cached.Message, Is.EqualTo(uncached.Message));
                    if (kind == "moved-identity")
                    {
                        Assert.That(VrChatExpressionSampler.FindRenderer(avatar, "MovedGroup/Face"), Is.SameAs(face));
                        Assert.That(Assert.Throws<InvalidOperationException>(() => lookup.Resolve("MovedGroup/Face")).Message, Does.Contain("Rendererのパス"),
                            "A frozen scope must reject a newly created route; it must never guess or reuse a moved cached object.");
                    }
                }
                foreach (var value in hierarchy) Assert.That(EditorJsonUtility.ToJson(value.Key), Is.EqualTo(value.Value));
                Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(source));
                Assert.That(EditorJsonUtility.ToJson(mesh), Is.EqualTo(originalMesh)); Assert.That(EditorJsonUtility.ToJson(other), Is.EqualTo(changedMesh));
            }
            finally { face.sharedMesh = mesh; Object.DestroyImmediate(other); }
        }


        private const string FrozenClock = "NormalizedFacialClock";

        private AnimatorState FrozenFacialMotion(float clock, out AnimationClip ramp, bool looping = false)
        {
            controller.AddParameter(FrozenClock, AnimatorControllerParameterType.Float);
            var parameters = controller.parameters;
            parameters.Single(parameter => parameter.name == FrozenClock).defaultFloat = clock; controller.parameters = parameters;
            ramp = new AnimationClip { name = "Native fixed-time facial ramp" };
            AnimationUtility.SetEditorCurve(ramp, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                AnimationCurve.Linear(0, 0, 1f / 6f, 100));
            var settings = AnimationUtility.GetAnimationClipSettings(ramp); settings.loopTime = looping;
            AnimationUtility.SetAnimationClipSettings(ramp, settings); AssetDatabase.AddObjectToAsset(ramp, controller);
            controller.AddLayer("Native frozen facial time"); var layers = controller.layers; var index = layers.Length - 1;
            layers[index].defaultWeight = .5f;
            var state = State(layers[index].stateMachine, "Native fixed-time facial state", ramp);
            state.timeParameter = FrozenClock; state.timeParameterActive = !looping;
            layers[index].stateMachine.defaultState = state; controller.layers = layers;
            Assert.That(ramp.length, Is.EqualTo(1f / 6f).Within(.000001f));
            Assert.That(VrChatExpressionSampler.IsConstant(AnimationUtility.GetEditorCurve(ramp,
                EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"))), Is.False);
            return state;
        }

        // Play the original authored controller for substantially longer than
        // the exporter's sample window. SDK callbacks are data in this test;
        // their independently chosen scalar counterfactual is supplied once.
        private float[] NativeFrozenClockHistory(AnimatorState state, AnimationClip clip, float clock, int face,
            bool requireClock = true, bool requireClip = true)
        {
            var copy = Object.Instantiate(avatar); var graph = PlayableGraph.Create("Original frozen facial clock reference");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            try
            {
                var animator = copy.GetComponent<Animator>(); animator.runtimeAnimatorController = null;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.applyRootMotion = false; animator.fireEvents = false;
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                var layers = controller.layers; var index = Array.FindIndex(layers, layer => layer.stateMachine.defaultState == state);
                Assert.That(index, Is.GreaterThanOrEqualTo(0));
                for (var layer = 0; layer < layers.Length; layer++) playable.SetLayerWeight(layer, layer == 0 ? 1 : layers[layer].defaultWeight);
                playable.SetInteger("Face", face); playable.SetFloat(FrozenClock, clock);
                AnimationPlayableOutput.Create(graph, "Original facial clock pose", animator).SetSourcePlayable(playable);
                graph.Play(); graph.Evaluate(0);
                var result = new List<float>(); var samples = new HashSet<int> { 120, 127, 181, 302 };
                for (var frame = 1; frame <= 302; frame++)
                {
                    graph.Evaluate(1f / 60f); if (!samples.Contains(frame)) continue;
                    Assert.That(playable.IsInTransition(index), Is.False);
                    Assert.That(playable.GetCurrentAnimatorStateInfo(index).shortNameHash, Is.EqualTo(Animator.StringToHash(state.name)));
                    if (requireClock) Assert.That(playable.GetFloat(FrozenClock), Is.EqualTo(clock).Within(.000001f));
                    if (requireClip)
                    {
                        var active = playable.GetCurrentAnimatorClipInfo(index).Where(info => info.weight > .00001f).ToArray();
                        Assert.That(active, Has.Length.EqualTo(1)); Assert.That(active.Single().clip, Is.SameAs(clip));
                    }
                    var value = copy.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0);
                    Assert.That(float.IsNaN(value) || float.IsInfinity(value), Is.False); result.Add(value);
                }
                return result.ToArray();
            }
            finally { if (graph.IsValid()) graph.Destroy(); Object.DestroyImmediate(copy); }
        }

        [TestCase(.5f, false, "menu")]
        [TestCase(1f, false, "menu")]
        [TestCase(.4f, true, "menu")]
        [TestCase(1f, true, "menu")]
        [TestCase(.5f, false, "direct")]
        public void AnImmutableNormalizedClockKeepsTheNativeFacialRampSelectable(float clock, bool sameInitialSet, string route)
        {
            Assert.That(Native(controller), Is.EqualTo(55).Within(.01f));
            Assert.That(Native(controller, new Dictionary<string, float> { ["Face"] = 1 }), Is.EqualTo(70).Within(.01f));
            var state = FrozenFacialMotion(clock, out var ramp);
            if (sameInitialSet) ParameterDriverExpressionTests.Driver(state, ParameterDriverExpressionTests.Op("Set", FrozenClock, clock));
            var face = route == "menu" ? 1 : 0; var expected = ((face == 1 ? 70 : 55) + clock * 100) * .5f;
            var reference = NativeFrozenClockHistory(state, ramp, clock, face);
            Assert.That(reference, Has.Length.EqualTo(4));
            foreach (var value in reference) Assert.That(value, Is.EqualTo(expected).Within(.01f),
                "A nonconstant clip must remain stationary when native motion time is supplied by this immutable Float.");
            var assets = DirectProbeSourceAssets(); var sourceJson = EditorJsonUtility.ToJson(controller);
            var meshJson = EditorJsonUtility.ToJson(mesh); var hierarchy = FootprintSourceHierarchy(); var probes = ProbeObjects();
            var metadata = new VrChatExpressionMenu.Source { Controller = controller, DeferAncillaryGeometryValidation = true };
            metadata.Defaults["Face"] = 0;
            var entry = new VrChatExpressionMenu.Entry { Name = "Selectable frozen-time facial pose" }; metadata.Entries.Add(entry);
            VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            if (route == "menu")
            {
                var values = VrChatExpressionSampler.SampleFixed(avatar, controller, metadata.Defaults,
                    new Dictionary<string, float> { ["Face"] = 1 }, metadata: metadata, geometryEntry: entry);
                entry.Values.Clear(); entry.Values.AddRange(values);
            }
            else VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar, controller, entry, 1, metadata: metadata, selectedState: selected);
            Assert.That(entry.Values, Has.Count.EqualTo(1)); Assert.That(entry.Values.Single().Path, Is.EqualTo("Face"));
            Assert.That(entry.Values.Single().Shape, Is.EqualTo("Face size")); Assert.That(entry.Values.Single().Weight, Is.EqualTo(expected).Within(.01f));
            var owned = new List<Mesh>(); var skin = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
            try
            {
                using (var prepared = new PreparedExpressionBindings(avatar, metadata))
                {
                    prepared.Capture(metadata); Assert.That(entry.Error, Is.Null);
                    Assert.That(entry.AncillaryGeometry.IsValidatedFor(entry), Is.True);
                    var result = VrChatExpressionBaker.Bake(avatar, avatar, metadata, owned, new List<string>(), prepared);
                    Assert.That(result, Has.Count.EqualTo(1)); Assert.That(result.Single().Targets, Has.Count.EqualTo(1));
                    var delta = new Vector3[mesh.vertexCount];
                    skin.sharedMesh.GetBlendShapeFrameVertices(skin.sharedMesh.GetBlendShapeIndex(result.Single().Targets.Single()), 0, delta, null, null);
                    Assert.That(delta.Any(value => value.magnitude > .1f), Is.True, "The selected endpoint must emit real geometry bindings.");
                }
            }
            finally { skin.sharedMesh = mesh; foreach (var value in owned) Object.DestroyImmediate(value); }
            foreach (var value in hierarchy) Assert.That(EditorJsonUtility.ToJson(value.Key), Is.EqualTo(value.Value));
            AssertDirectProbeSourceAssets(assets); AssertUnchanged(sourceJson, meshJson, probes);
        }

        [TestCase("autonomous-loop")]
        [TestCase("clock-curve")]
        [TestCase("conflicting-sets")]
        [TestCase("add")]
        [TestCase("other-set")]
        [TestCase("omitted-selected")]
        [TestCase("trigger-conflict")]
        [TestCase("nonfinite-default")]
        [TestCase("blend-tree")]
        [TestCase("timed-exit")]
        [TestCase("speed-modifier")]
        [TestCase("raw")]
        [TestCase("neutral")]
        [TestCase("nondeferred")]
        public void AClockWithoutAnImmutableNativeTimeProofCannotAdmitATimeVaryingFacialClip(string kind)
        {
            Assert.That(Native(controller), Is.EqualTo(55).Within(.01f));
            Assert.That(Native(controller, new Dictionary<string, float> { ["Face"] = 1 }), Is.EqualTo(70).Within(.01f));
            var state = FrozenFacialMotion(.5f, out var ramp, kind == "autonomous-loop");
            var reference = NativeFrozenClockHistory(state, ramp, .5f, 1);
            if (kind == "autonomous-loop") Assert.That(reference.Max() - reference.Min(), Is.GreaterThan(10),
                "An independently observed autonomous loop changes geometry; it cannot become a fixed-time exception.");
            else foreach (var value in reference) Assert.That(value, Is.EqualTo(60).Within(.01f));
            AnimatorController other = null;
            if (kind == "clock-curve")
            {
                var writer = new AnimationClip { name = "Native mutable facial clock writer" };
                AnimationUtility.SetEditorCurve(writer, EditorCurveBinding.FloatCurve("", typeof(Animator), FrozenClock), AnimationCurve.Linear(0, 0, 1, 1));
                AssetDatabase.AddObjectToAsset(writer, controller); controller.AddLayer("Native mutable clock curve");
                var layers = controller.layers; layers[3].defaultWeight = 1;
                layers[3].stateMachine.defaultState = State(layers[3].stateMachine, "Native clock curve state", writer); controller.layers = layers;
                var changed = NativeFrozenClockHistory(state, ramp, .5f, 1, requireClock: false);
                Assert.That(Math.Abs(changed.Last() - reference.Last()), Is.GreaterThan(.01f), "A native Animator parameter curve is a real time input writer.");
            }
            else if (kind == "conflicting-sets" || kind == "add")
            {
                if (kind == "conflicting-sets") ParameterDriverExpressionTests.Driver(state,
                    ParameterDriverExpressionTests.Op("Set", FrozenClock, .75f), ParameterDriverExpressionTests.Op("Set", FrozenClock, 1));
                else ParameterDriverExpressionTests.Driver(state, ParameterDriverExpressionTests.Op("Add", FrozenClock, .5f));
                foreach (var value in NativeFrozenClockHistory(state, ramp, 1, 1)) Assert.That(value, Is.EqualTo(85).Within(.01f),
                    "The scalar counterfactual of the real SDK command changes the same captured facial channel.");
            }
            else if (kind == "omitted-selected")
            {
                ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", FrozenClock, 1));
                foreach (var value in NativeFrozenClockHistory(state, ramp, 1, 0)) Assert.That(value, Is.EqualTo(77.5f).Within(.01f));
            }
            else if (kind == "other-set" || kind == "trigger-conflict")
            {
                other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/ClockAction.controller");
                other.AddParameter(FrozenClock, kind == "trigger-conflict" ? AnimatorControllerParameterType.Trigger : AnimatorControllerParameterType.Float);
                var machine = other.layers[0].stateMachine; var writer = State(machine, "Other native facial clock control", null); machine.defaultState = writer;
                if (kind == "other-set") ParameterDriverExpressionTests.Driver(writer, ParameterDriverExpressionTests.Op("Set", FrozenClock, 1));
                Native(other, null, playable => Assert.That(playable.GetCurrentAnimatorStateInfo(0).shortNameHash, Is.EqualTo(Animator.StringToHash(writer.name))));
            }
            else if (kind == "blend-tree")
            {
                var tree = new BlendTree { name = "Ambiguous native time tree", blendType = BlendTreeType.Simple1D, blendParameter = FrozenClock,
                    useAutomaticThresholds = false };
                var duplicate = Clip("Tree facial endpoint", 100); tree.AddChild(ramp, 0); tree.AddChild(duplicate, 1);
                AssetDatabase.AddObjectToAsset(tree, controller); state.motion = tree;
                NativeFrozenClockHistory(state, ramp, .5f, 1, requireClip: false);
            }
            else if (kind == "timed-exit")
            {
                var machine = controller.layers[2].stateMachine; var later = State(machine, "Delayed different facial endpoint", Clip("Later face", 100));
                var exit = state.AddTransition(later); exit.hasExitTime = true; exit.exitTime = 1000; exit.duration = 0;
                foreach (var value in NativeFrozenClockHistory(state, ramp, .5f, 1)) Assert.That(value, Is.EqualTo(60).Within(.01f),
                    "A short stationary reference cannot disprove the authored delayed exit.");
            }
            else if (kind == "speed-modifier")
            {
                controller.AddParameter("FacialPlaybackRate", AnimatorControllerParameterType.Float);
                var parameters = controller.parameters; parameters.Single(parameter => parameter.name == "FacialPlaybackRate").defaultFloat = 1;
                controller.parameters = parameters; state.speedParameter = "FacialPlaybackRate"; state.speedParameterActive = true;
                NativeFrozenClockHistory(state, ramp, .5f, 1);
            }
            // Warm the original assets after each valid graph mutation before
            // capturing their complete serialized preservation baseline.
            Native(controller, new Dictionary<string, float> { ["Face"] = 0, [FrozenClock] = .5f });
            Native(controller, new Dictionary<string, float> { ["Face"] = 1, [FrozenClock] = .5f });
            var assets = DirectProbeSourceAssets();
            if (other != null) foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(folder + "/ClockAction.controller"))
                assets.Add(asset, EditorJsonUtility.ToJson(asset));
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh);
            var hierarchy = FootprintSourceHierarchy(); var probes = ProbeObjects();
            var metadata = new VrChatExpressionMenu.Source { Controller = controller, DeferAncillaryGeometryValidation = kind != "nondeferred" };
            metadata.Defaults["Face"] = 0; if (other != null) metadata.OtherControllers.Add(other);
            if (kind == "nonfinite-default") { metadata.ExternalParameters.Add(FrozenClock); metadata.Defaults[FrozenClock] = float.NaN; }
            var entry = new VrChatExpressionMenu.Entry { Name = "Unproven timed facial candidate" }; metadata.Entries.Add(entry);
            VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            Action sample = () =>
            {
                var selection = new Dictionary<string, float> { ["Face"] = 1 };
                if (kind == "omitted-selected") VrChatExpressionSampler.ApplyFixedPermanentOverrides(avatar, controller, entry, 1, metadata: metadata);
                else if (kind == "neutral")
                {
                    var roots = new HashSet<EditorCurveBinding>(AnimationUtility.GetCurveBindings(ramp));
                    var dependency = ExpressionDependencies.AnalyzeNeutral(controller, roots, null, metadata);
                    VrChatExpressionSampler.SampleNeutral(avatar, controller, dependency, metadata, null);
                }
                else
                {
                    var values = kind == "raw" ? VrChatExpressionSampler.Sample(avatar, controller, metadata.Defaults, selection, metadata: metadata) :
                        VrChatExpressionSampler.SampleFixed(avatar, controller, metadata.Defaults, selection, metadata: metadata, geometryEntry: entry);
                    entry.Values.Clear(); entry.Values.AddRange(values);
                }
            };
            InvalidOperationException error = kind == "neutral" ? Assert.Throws<NeutralShapeSamplingException>(() => sample()) :
                Assert.Throws<InvalidOperationException>(() => sample());
            var message = kind == "other-set" ? "FX以外" : kind == "omitted-selected" ? "Parameter Driver" :
                kind == "nonfinite-default" ? "不正" : "時間";
            Assert.That(error.Message, Does.Contain(message));
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(80), "Unproven timing cannot partially rewrite the registered facial clip.");
            foreach (var value in hierarchy) Assert.That(EditorJsonUtility.ToJson(value.Key), Is.EqualTo(value.Value));
            AssertDirectProbeSourceAssets(assets); AssertUnchanged(sourceJson, meshJson, probes);
        }


        [TestCase("unique-fx-set")]
        [TestCase("repeated-fx-set")]
        [TestCase("clock-selected")]
        [TestCase("external-clock")]
        [TestCase("copy")]
        [TestCase("random")]
        [TestCase("other-same-set")]
        [TestCase("omitted-same-set")]
        [TestCase("unknown-callback")]
        public void AUniqueLiteralClockWriteCanFreezeMotionTimeWithoutChangingTheInitialEnvironment(string kind)
        {
            var allowed = kind == "unique-fx-set" || kind == "repeated-fx-set";
            Assert.That(Native(controller), Is.EqualTo(55).Within(.01f));
            Assert.That(Native(controller, new Dictionary<string, float> { ["Face"] = 1 }), Is.EqualTo(70).Within(.01f));
            var state = FrozenFacialMotion(.2f, out var ramp);
            foreach (var value in NativeFrozenClockHistory(state, ramp, .2f, 1)) Assert.That(value, Is.EqualTo(45).Within(.01f));
            if (kind == "copy")
            {
                controller.AddParameter("LiteralClockCopySource", AnimatorControllerParameterType.Float);
                var parameters = controller.parameters; parameters.Single(parameter => parameter.name == "LiteralClockCopySource").defaultFloat = .4f;
                controller.parameters = parameters;
                ParameterDriverExpressionTests.Driver(state, ParameterDriverExpressionTests.Op("Copy", FrozenClock, source: "LiteralClockCopySource"));
            }
            else ParameterDriverExpressionTests.Driver(state, ParameterDriverExpressionTests.Op(kind == "random" ? "Random" : "Set", FrozenClock, .4f));
            if (kind == "repeated-fx-set")
            {
                ParameterDriverExpressionTests.Driver(state, ParameterDriverExpressionTests.Op("Set", FrozenClock, .4f));
                controller.AddLayer("Second known literal clock writer"); var layers = controller.layers; layers[3].defaultWeight = 1;
                var writer = State(layers[3].stateMachine, "Same literal clock entry", null); layers[3].stateMachine.defaultState = writer; controller.layers = layers;
                ParameterDriverExpressionTests.Driver(writer, ParameterDriverExpressionTests.Op("Set", FrozenClock, .4f));
            }
            if (kind == "omitted-same-set") ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", FrozenClock, .4f));
            if (kind == "unknown-callback")
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAnimatorLayerControl"))
                    .FirstOrDefault(value => value != null);
                if (type == null) Assert.Ignore("Install the real VRChat SDK.");
                state.AddStateMachineBehaviour(type);
            }
            AnimatorController other = null;
            if (kind == "other-same-set")
            {
                other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/EventualClockAction.controller");
                other.AddParameter(FrozenClock, AnimatorControllerParameterType.Float);
                var parameters = other.parameters; parameters.Single(parameter => parameter.name == FrozenClock).defaultFloat = .2f; other.parameters = parameters;
                var machine = other.layers[0].stateMachine; var writer = State(machine, "Other same literal clock entry", null); machine.defaultState = writer;
                ParameterDriverExpressionTests.Driver(writer, ParameterDriverExpressionTests.Op("Set", FrozenClock, .4f));
                Native(other, null, playable => Assert.That(playable.GetCurrentAnimatorStateInfo(0).shortNameHash, Is.EqualTo(Animator.StringToHash(writer.name))));
            }
            // Original Editor playback does not execute SDK callbacks. Supply
            // only their scalar counterfactual once: the original direct clip,
            // fractional layer and geometry still determine this reference.
            // The source/default clock remains .2, and the exporter must audit
            // every original command before using a live .4 time certificate.
            foreach (var value in NativeFrozenClockHistory(state, ramp, .4f, 1)) Assert.That(value, Is.EqualTo(55).Within(.01f));
            Native(controller, new Dictionary<string, float> { ["Face"] = 0, [FrozenClock] = .4f });
            var assets = DirectProbeSourceAssets();
            if (other != null) foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(folder + "/EventualClockAction.controller")) assets.Add(asset, EditorJsonUtility.ToJson(asset));
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh);
            var hierarchy = FootprintSourceHierarchy(); var probes = ProbeObjects();
            var metadata = new VrChatExpressionMenu.Source { Controller = controller, DeferAncillaryGeometryValidation = true };
            metadata.Defaults["Face"] = 0; metadata.Defaults[FrozenClock] = .2f;
            if (other != null) metadata.OtherControllers.Add(other);
            if (kind == "external-clock") metadata.ExternalParameters.Add(FrozenClock);
            var context = FixedExpressionContext.Create(controller, metadata.Defaults, metadata);
            var contextBefore = new Dictionary<string, float>(context.Values, StringComparer.Ordinal);
            var defaultsBefore = new Dictionary<string, float>(metadata.Defaults, StringComparer.Ordinal);
            var entry = new VrChatExpressionMenu.Entry { Name = "Known literal fixed-time facial pose" }; metadata.Entries.Add(entry);
            VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            Action sample = () =>
            {
                if (kind == "omitted-same-set")
                    VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1, metadata: metadata, fixedContext: context);
                else
                {
                    var selection = new Dictionary<string, float> { ["Face"] = 1 };
                    if (kind == "clock-selected") selection[FrozenClock] = .2f;
                    var values = VrChatExpressionSampler.SampleFixed(avatar, controller, metadata.Defaults, selection,
                        metadata: metadata, fixedContext: context, geometryEntry: entry);
                    entry.Values.Clear(); entry.Values.AddRange(values);
                }
            };
            if (allowed)
            {
                sample(); Assert.That(entry.Values, Has.Count.EqualTo(1));
                Assert.That(entry.Values.Single().Path, Is.EqualTo("Face")); Assert.That(entry.Values.Single().Shape, Is.EqualTo("Face size"));
                Assert.That(entry.Values.Single().Weight, Is.EqualTo(55).Within(.01f));
                var owned = new List<Mesh>(); var skin = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
                try
                {
                    using (var prepared = new PreparedExpressionBindings(avatar, metadata))
                    {
                        prepared.Capture(metadata); Assert.That(entry.Error, Is.Null); Assert.That(entry.AncillaryGeometry.IsValidatedFor(entry), Is.True);
                        var result = VrChatExpressionBaker.Bake(avatar, avatar, metadata, owned, new List<string>(), prepared);
                        Assert.That(result, Has.Count.EqualTo(1)); Assert.That(result.Single().Targets, Has.Count.EqualTo(1));
                        var delta = new Vector3[mesh.vertexCount];
                        skin.sharedMesh.GetBlendShapeFrameVertices(skin.sharedMesh.GetBlendShapeIndex(result.Single().Targets.Single()), 0, delta, null, null);
                        Assert.That(delta.Any(value => value.magnitude > .1f), Is.True);
                    }
                }
                finally { skin.sharedMesh = mesh; foreach (var value in owned) Object.DestroyImmediate(value); }
            }
            else
            {
                var error = Assert.Throws<InvalidOperationException>(() => sample());
                Assert.That(error.Message, Does.Contain(kind == "other-same-set" ? "FX以外" : kind == "omitted-same-set" ? "Parameter Driver" :
                    kind == "unknown-callback" ? "VRCAnimatorLayerControl" : kind == "random" ? "Random" : "時間"));
                Assert.That(entry.Values.Single().Weight, Is.EqualTo(80));
            }
            Assert.That(context.Values, Is.EquivalentTo(contextBefore), "A live motion-time certificate must not become the initial environment or a reachability constant.");
            Assert.That(metadata.Defaults, Is.EquivalentTo(defaultsBefore));
            Assert.That(controller.parameters.Single(parameter => parameter.name == FrozenClock).defaultFloat, Is.EqualTo(.2f));
            foreach (var value in hierarchy) Assert.That(EditorJsonUtility.ToJson(value.Key), Is.EqualTo(value.Value));
            AssertDirectProbeSourceAssets(assets); AssertUnchanged(sourceJson, meshJson, probes);
        }


        private SkinnedMeshRenderer FootprintRenderer(string name, Mesh value, float rest)
        {
            var child = new GameObject(name, typeof(SkinnedMeshRenderer)); child.transform.SetParent(avatar.transform, false);
            var renderer = child.GetComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = value; renderer.SetBlendShapeWeight(0, rest); return renderer;
        }

        private Dictionary<Object, string> FootprintSourceHierarchy() => avatar.GetComponentsInChildren<Transform>(true)
            .SelectMany(value => new Object[] { value.gameObject }.Concat(value.GetComponents<Component>().Cast<Object>()))
            .ToDictionary(value => value, value => EditorJsonUtility.ToJson(value));

        private void ConfigureCapturedMorphSkin(bool affectsFace)
        {
            var faceBone = new GameObject("FacialBone"); faceBone.transform.SetParent(avatar.transform, false);
            var ear = new GameObject("Ear"); ear.transform.SetParent(avatar.transform, false);
            var skin = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
            skin.bones = new[] { faceBone.transform, ear.transform }; skin.rootBone = avatar.transform;
            mesh.bindposes = skin.bones.Select(bone => bone.worldToLocalMatrix * skin.transform.localToWorldMatrix).ToArray();
            mesh.boneWeights = new[]
            {
                new BoneWeight { boneIndex0 = affectsFace ? 1 : 0, weight0 = 1 },
                new BoneWeight { boneIndex0 = 1, weight0 = 1 }, new BoneWeight { boneIndex0 = 1, weight0 = 1 }
            };
            Assert.That(mesh.GetAllBoneWeights().Length, Is.EqualTo(3));
        }

        private Vector3[] NativeSkinnedVertices(IDictionary<string, float> defaults, Action<GameObject> observe = null)
        {
            var copy = Object.Instantiate(avatar); var baked = new Mesh();
            var graph = PlayableGraph.Create("Independent ancillary bone reference"); graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            try
            {
                var animator = copy.GetComponent<Animator>(); animator.runtimeAnimatorController = null;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.applyRootMotion = false; animator.fireEvents = false;
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                for (var index = 0; index < controller.layers.Length; index++)
                    playable.SetLayerWeight(index, index == 0 ? 1 : controller.layers[index].defaultWeight);
                foreach (var pair in defaults)
                {
                    var type = controller.parameters.Single(parameter => parameter.name == pair.Key).type;
                    if (type == AnimatorControllerParameterType.Bool) playable.SetBool(pair.Key, pair.Value != 0);
                    else playable.SetInteger(pair.Key, Mathf.RoundToInt(pair.Value));
                }
                AnimationPlayableOutput.Create(graph, "Original skinned pose", animator).SetSourcePlayable(playable);
                graph.Play(); graph.Evaluate(0);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                observe?.Invoke(copy);
                copy.GetComponentInChildren<SkinnedMeshRenderer>().BakeMesh(baked);
                return baked.vertices;
            }
            finally { if (graph.IsValid()) graph.Destroy(); Object.DestroyImmediate(baked); Object.DestroyImmediate(copy); }
        }


        private AnimationClip AncillaryGraph(string path = "Ear")
        {
            controller.AddParameter("AncillaryEnabled", AnimatorControllerParameterType.Bool);
            var parameters = controller.parameters; parameters.Single(parameter => parameter.name == "AncillaryEnabled").defaultBool = true;
            controller.parameters = parameters;
            if (path == "Ear" && avatar.transform.Find("Ear") == null)
            {
                var ear = new GameObject("Ear"); ear.transform.SetParent(avatar.transform, false);
            }
            var pose = new AnimationClip { name = "Ancillary activation and motion" };
            AnimationUtility.SetEditorCurve(pose, EditorCurveBinding.FloatCurve(path, typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1));
            AnimationUtility.SetEditorCurve(pose, EditorCurveBinding.FloatCurve(path, typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Linear(0, 0, 1, 1));
            AssetDatabase.AddObjectToAsset(pose, controller);
            var empty = new AnimationClip { name = "Ancillary zero-curve clip" }; AssetDatabase.AddObjectToAsset(empty, controller);
            controller.AddLayer("Ancillary native support"); var layers = controller.layers; layers[2].defaultWeight = 1;
            var idle = State(layers[2].stateMachine, "Empty initialization", empty); layers[2].stateMachine.defaultState = idle;
            var active = State(layers[2].stateMachine, "Ancillary active", pose);
            var transition = idle.AddTransition(active); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.If, 0, "AncillaryEnabled");
            controller.layers = layers;
            Assert.That(AnimationUtility.GetCurveBindings(empty), Is.Empty);
            Assert.That(idle.writeDefaultValues || active.writeDefaultValues, Is.False);
            return pose;
        }

        private Dictionary<Object, string> DirectProbeSourceAssets()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath(folder + "/FX.controller");
            foreach (var clip in assets.OfType<AnimationClip>())
            { AnimationUtility.GetCurveBindings(clip); AnimationUtility.GetObjectReferenceCurveBindings(clip); }
            return assets.ToDictionary(asset => asset, asset => EditorJsonUtility.ToJson(asset));
        }

        private static void AssertDirectProbeSourceAssets(Dictionary<Object, string> assets)
        {
            foreach (var asset in assets) Assert.That(EditorJsonUtility.ToJson(asset.Key), Is.EqualTo(asset.Value), asset.Key.name);
        }

        [TestCase("external")]
        [TestCase("parameter curve")]
        [TestCase("other controller")]
        [TestCase("moving lower morph")]
        public void LowerPoseDependenciesAreValidatedBeforeRewritingTheEntry(string dependency)
        {
            var metadata = new VrChatExpressionMenu.Source { Controller = controller };
            var expectedMessage = "時間で変わる";
            if (dependency == "external") { metadata.ExternalParameters.Add("Face"); expectedMessage = "外部入力"; }
            else if (dependency == "moving lower morph")
                AnimationUtility.SetEditorCurve((AnimationClip)lower.motion,
                    EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"), AnimationCurve.Linear(0, 30, 30, 60));
            else
            {
                var clip = new AnimationClip { name = "Global Face writer" };
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "Face"),
                    dependency == "parameter curve" ? AnimationCurve.Linear(0, 0, 30, 1) : AnimationCurve.Constant(0, 1, 1));
                if (dependency == "parameter curve")
                {
                    AssetDatabase.AddObjectToAsset(clip, controller); controller.AddLayer("Changing parameter writer"); var layers = controller.layers;
                    layers[2].defaultWeight = 1; layers[2].stateMachine.defaultState = State(layers[2].stateMachine, "Writer", clip); controller.layers = layers;
                }
                else
                {
                    var other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Gesture.controller");
                    other.AddParameter("Face", AnimatorControllerParameterType.Int); AssetDatabase.AddObjectToAsset(clip, other);
                    other.layers[0].stateMachine.defaultState = State(other.layers[0].stateMachine, "Writer", clip);
                    metadata.OtherControllers.Add(other); expectedMessage = "FX以外";
                }
            }
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            var error = Assert.Throws<InvalidOperationException>(() =>
                VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1, metadata: metadata));
            Assert.That(error.Message, Does.Contain(expectedMessage));
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(80), "Rejected dependencies cannot leave a partially rewritten expression.");
            AssertUnchanged(sourceJson, meshJson, probes);
        }
    }
}
