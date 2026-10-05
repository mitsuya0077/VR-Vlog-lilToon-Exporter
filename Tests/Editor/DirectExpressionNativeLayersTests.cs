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
        private float Native(RuntimeAnimatorController runtime, IDictionary<string, float> defaults = null)
        {
            var copy = Object.Instantiate(avatar); var graph = PlayableGraph.Create("Direct expression native reference");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            try
            {
                var animator = copy.GetComponent<Animator>(); animator.runtimeAnimatorController = null;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.applyRootMotion = false; animator.fireEvents = false;
                var playable = AnimatorControllerPlayable.Create(graph, runtime);
                for (var index = 0; index < controller.layers.Length; index++)
                    playable.SetLayerWeight(index, index == 0 ? 1 : controller.layers[index].defaultWeight);
                AnimationPlayableOutput.Create(graph, "Face", animator).SetSourcePlayable(playable);
                if (defaults != null)
                    foreach (var pair in defaults) playable.SetInteger(pair.Key, Mathf.RoundToInt(pair.Value));
                graph.Play(); graph.Evaluate(0);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                Assert.That(playable.IsInTransition(0), Is.False);
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
        public void GestureRegistrationRetainsTheExactStatesDriverAndEffectiveClip(bool overrideClip)
        {
            ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "Face", 1));
            var sibling = State(controller.layers[1].stateMachine, "Same clip different callback", (AnimationClip)selected.motion);
            ParameterDriverExpressionTests.Driver(sibling, ParameterDriverExpressionTests.Op("Set", "Face", 0));
            controller.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
            var transition = controller.layers[1].stateMachine.AddAnyStateTransition(selected);
            transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "GestureRight");
            RuntimeAnimatorController runtime = controller;
            if (overrideClip)
            {
                var overrides = new AnimatorOverrideController(controller);
                overrides[(AnimationClip)selected.motion] = Clip("Effective registered face", 90);
                AssetDatabase.CreateAsset(overrides, folder + "/Registered.overrideController"); runtime = overrides;
            }
            var expected = Native(runtime, new Dictionary<string, float> { ["Face"] = 1 });
            Assert.That(expected, Is.EqualTo(overrideClip ? 75 : 70).Within(.01));
            Assert.That(Native(runtime), Is.EqualTo(overrideClip ? 60 : 55).Within(.01),
                "Dropping the selected callback produces a distinct native pose.");
            var sourceAssets = AssetDatabase.LoadAllAssetsAtPath(folder + "/FX.controller")
                .ToDictionary(asset => asset, asset => EditorJsonUtility.ToJson(asset));
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh);
            var runtimeJson = EditorJsonUtility.ToJson(runtime); var probes = ProbeObjects();
            var metadata = new VrChatExpressionMenu.Source { Controller = runtime };
            VrChatGestureExpressions.Add(avatar, metadata);
            Assert.That(metadata.Entries.Count, Is.EqualTo(1));
            var entry = metadata.Entries.Single();
            Assert.That(entry.Error, Is.Null);
            Assert.That(entry.Values.Count, Is.EqualTo(1));
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(expected).Within(.01));
            foreach (var pair in sourceAssets) Assert.That(EditorJsonUtility.ToJson(pair.Key), Is.EqualTo(pair.Value), pair.Key.name);
            Assert.That(EditorJsonUtility.ToJson(runtime), Is.EqualTo(runtimeJson));
            AssertUnchanged(sourceJson, meshJson, probes);
        }

        [TestCase("wrong layer")]
        [TestCase("write defaults")]
        [TestCase("ancestor callback")]
        public void InvalidSelectedStateProvenanceCannotRewriteTheDirectEntry(string invalid)
        {
            var selectedSource = invalid == "wrong layer" ? lower : selected;
            if (invalid == "ancestor callback")
            {
                var driver = ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "Face", 1));
                selected.behaviours = Array.Empty<StateMachineBehaviour>();
                controller.layers[1].stateMachine.behaviours = new[] { driver };
            }
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, (AnimationClip)selected.motion, entry);
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry,
                1, invalid == "write defaults", sourceState: selectedSource));
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(80));
            AssertUnchanged(sourceJson, meshJson, probes);
        }

        [Test]
        public void SharedGestureClipKeepsDistinctNativeOutcomesFromItsSelectedCallbacks()
        {
            ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "Face", 1));
            var sibling = State(controller.layers[1].stateMachine, "Alternative callback", (AnimationClip)selected.motion);
            ParameterDriverExpressionTests.Driver(sibling, ParameterDriverExpressionTests.Op("Set", "Face", 0));
            controller.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
            foreach (var target in new[] { selected, sibling })
            {
                var transition = controller.layers[1].stateMachine.AddAnyStateTransition(target);
                transition.hasExitTime = false; transition.duration = 0;
                transition.AddCondition(AnimatorConditionMode.Equals, target == selected ? 1 : 2, "GestureRight");
            }
            var expected = new[] { Native(controller), Native(controller, new Dictionary<string, float> { ["Face"] = 1 }) };
            Assert.That(expected, Is.EqualTo(new[] { 55f, 70f }).Within(.01));
            var sourceJson = EditorJsonUtility.ToJson(controller); var meshJson = EditorJsonUtility.ToJson(mesh); var probes = ProbeObjects();
            var source = new VrChatExpressionMenu.Source { Controller = controller };
            VrChatGestureExpressions.Add(avatar, source);
            Assert.That(source.Entries.Count, Is.EqualTo(2));
            Assert.That(source.Entries.Select(entry => entry.Error), Is.All.Null);
            Assert.That(source.Entries.Select(entry => entry.Id).Distinct().Count(), Is.EqualTo(2));
            Assert.That(source.Entries.Select(entry => entry.Values.Single().Weight).OrderBy(value => value).ToArray(),
                Is.EqualTo(expected).Within(.01), "Reusing a clip cannot discard a different callback-dependent pose.");
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

        [TestCase("external")]
        [TestCase("parameter curve")]
        [TestCase("other controller")]
        [TestCase("moving lower morph")]
        public void LowerPoseDependenciesAreValidatedBeforeRewritingTheEntry(string dependency)
        {
            var metadata = new VrChatExpressionMenu.Source { Controller = controller };
            var expectedMessage = "時間で変わる";
            if (dependency == "external")
            {
                controller.AddParameter("EyeHeightAsMeters", AnimatorControllerParameterType.Float);
                var transition = lower.transitions.Single();
                foreach (var condition in transition.conditions) transition.RemoveCondition(condition);
                transition.AddCondition(AnimatorConditionMode.Greater, .5f, "EyeHeightAsMeters");
                metadata.ExternalParameters.Add("EyeHeightAsMeters"); expectedMessage = "外部入力";
            }
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
            var error = Assert.Catch<InvalidOperationException>(() =>
                VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 1, metadata: metadata));
            Assert.That(error.Message, Does.Contain(expectedMessage));
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(80), "Rejected dependencies cannot leave a partially rewritten expression.");
            AssertUnchanged(sourceJson, meshJson, probes);
        }
    }
}
