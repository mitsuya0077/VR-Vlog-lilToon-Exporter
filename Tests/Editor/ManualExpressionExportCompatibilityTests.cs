using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UniGLTF;
using UniGLTF.Extensions.VRMC_vrm;
using UniVRM10;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class ManualExpressionExportCompatibilityTests
    {
        [TestCase(false, 0f)]
        [TestCase(true, 0f)]
        [TestCase(false, .5f)]
        [TestCase(true, .5f)]
        public async Task FixedRestInputsKeepMenuAndNativeGestureSelectableAfterExport(bool fullLilToon, float contactRest)
        {
            using var f = new Fixture(contactRest);
            var original = f.Snapshot();
            var menu = VrChatExpressionSampler.Analyze(f.Avatar.Source);
            Assert.That(menu.Entries, Has.Count.EqualTo(2));
            Assert.That(menu.Entries.Select(entry => entry.Error), Is.All.Null, Errors(menu));
            var selected = menu.Entries.Single(entry => entry.Name == "Manual face");
            var gesture = menu.Entries.Single(entry => entry.Name == "ジェスチャー / Gesture face");
            AssertMorph(selected, "Smile", 50 + 40 * contactRest);
            AssertMorph(gesture, "Smile", 40 + 40 * contactRest);
            var native = f.NativePose(f.GestureState, f.SelectedLayer);
            Assert.That(native["Smile"], Is.EqualTo(40 + 40 * contactRest).Within(.02f));
            Assert.That(native["Brow"], Is.EqualTo(20).Within(.02f));
            var warnings = new List<string>();
            var bytes = Export(f.Avatar.Source, fullLilToon, warnings);
            Assert.That(VrmMenuExpressions.CountRegistered(bytes), Is.EqualTo(2), string.Join("\n", warnings));
            await AssertRoundTrip(f, bytes, new Dictionary<string, float> {
                ["VRChat / Manual face"] = 50 + 40 * contactRest,
                ["VRChat / ジェスチャー / Gesture face"] = 40 + 40 * contactRest
            });
            f.AssertUnchanged(original);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task KnownGestureSetRetainsItsNativeRestPoseAndSelectableVrmBinding(bool fullLilToon)
        {
            using var f = new Fixture(.5f);
            ParameterDriverExpressionTests.Driver(f.GestureState, ParameterDriverExpressionTests.Op("Set", "Pet", .25f));
            var original = f.Snapshot();
            var native = f.NativePose(f.GestureState, f.SelectedLayer, new Dictionary<string, float> { ["Pet"] = .25f });
            Assert.That(native["Smile"], Is.EqualTo(50).Within(.02f));
            Assert.That(native["Brow"], Is.EqualTo(20).Within(.02f));
            var menu = VrChatExpressionSampler.Analyze(f.Avatar.Source);
            Assert.That(menu.Entries, Has.Count.EqualTo(2));
            Assert.That(menu.Entries.Select(entry => entry.Error), Is.All.Null, Errors(menu));
            AssertMorph(menu.Entries.Single(entry => entry.Name == "ジェスチャー / Gesture face"), "Smile", native["Smile"]);
            var warnings = new List<string>(); var bytes = Export(f.Avatar.Source, fullLilToon, warnings);
            Assert.That(VrmMenuExpressions.CountRegistered(bytes), Is.EqualTo(2), string.Join("\n", warnings));
            await AssertRoundTrip(f, bytes, new Dictionary<string, float> {
                ["VRChat / Manual face"] = 70, ["VRChat / ジェスチャー / Gesture face"] = native["Smile"]
            });
            f.AssertUnchanged(original);
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task RegisteredNativeTreeStateSetKeepsItsSelectableVrmBinding(bool fullLilToon, bool pluginReplacement)
        {
            using var f = new Fixture(.5f, faceEmo: true);
            var clipIdentity = f.ConfigureRegisteredStateSet(pluginReplacement: pluginReplacement);
            var nativeInputs = new Dictionary<string, float> { ["Pet"] = .25f };
            // Native playback lazily initializes the registered asset's binding
            // cache. Establish the export guard after our own independent raw
            // reference, then prove that repeating that reference is stable.
            var native = f.NativePose(f.RegisteredState, f.SelectedLayer, nativeInputs);
            Assert.That(native["Smile"], Is.EqualTo(55).Within(.02f));
            Assert.That(native["Brow"], Is.EqualTo(20).Within(.02f));
            var original = f.Snapshot();
            var repeatedNative = f.NativePose(f.RegisteredState, f.SelectedLayer, nativeInputs);
            Assert.That(repeatedNative["Smile"], Is.EqualTo(native["Smile"]).Within(.02f));
            Assert.That(repeatedNative["Brow"], Is.EqualTo(native["Brow"]).Within(.02f));
            f.AssertUnchanged(original);
            var clone = Object.Instantiate(f.Avatar.Source); var owned = new List<Mesh>();
            try
            {
                var source = VrChatExpressionMenu.Read(clone, new VrChatMenuImportPolicy { SkipAll = true });
                var snapshot = FaceEmoExpressions.CaptureRegistered(clone, f.Registered, deferPermanentOverrides: true);
                using var preparation = new NdmfExportPreparation();
                if (pluginReplacement)
                    PreparedAnimationClipIdentityTests.RegisterReplacement(preparation, clone.transform, clipIdentity.Original, clipIdentity.Current);
                snapshot.RebindPrepared(renderer => renderer, preparation.IsolatedCopyOf, preparation.PreparedClipFor);
                var serial = 0;
                FaceEmoExpressions.ReadRegistered(clone, f.Registered, "FaceEmo", source,
                    ref serial, new HashSet<object>(), 0, bindings: snapshot);
                var entry = source.Entries.Single(); Assert.That(entry.Error, Is.Null, Errors(source));
                using var bindings = new PreparedExpressionBindings(clone, source);
                FaceEmoExpressions.ApplyPreparedDefaultFace(clone, source, bindings, registeredBindings: snapshot);
                Assert.That(entry.Error, Is.Null, Errors(source));
                AssertPose(entry, native["Smile"], native["Brow"]);
                bindings.Capture(source);
                var warnings = new List<string>(); AvatarBaseShape.Preserve(f.Avatar.Source, clone, owned, warnings);
                var baked = VrChatExpressionBaker.Bake(f.Avatar.Source, clone, source, owned, warnings, bindings);
                Assert.That(baked, Has.Count.EqualTo(1), string.Join("\n", warnings));
                Object.DestroyImmediate(clone.GetComponent(SdkType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor")));
                var bytes = VrmMenuExpressions.Add(Export(clone, fullLilToon, warnings), baked);
                Assert.That(VrmMenuExpressions.CountRegistered(bytes), Is.EqualTo(1), string.Join("\n", warnings));
                await AssertRoundTrip(f, bytes, new Dictionary<string, float> { ["VRChat / " + entry.Name] = native["Smile"] });
            }
            finally { Object.DestroyImmediate(clone); foreach (var mesh in owned) Object.DestroyImmediate(mesh); }
            f.AssertUnchanged(original);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NonDeferredRegisteredPlayerUsesKnownSetAndRejectsAmbiguousStateCallbacks(bool ambiguous)
        {
            using var f = new Fixture(.5f, faceEmo: true);
            f.ConfigureRegisteredStateSet(.625f);
            if (ambiguous)
            {
                var machine = f.Controller.layers[f.SelectedLayer].stateMachine;
                var other = machine.AddState("Conflicting registered callback");
                other.motion = f.RegisteredState.motion; other.writeDefaultValues = false;
                ParameterDriverExpressionTests.Driver(other, ParameterDriverExpressionTests.Op("Set", "Pet", .5f));
            }
            var inputs = new Dictionary<string, float> { ["Pet"] = .625f };
            var native = f.NativePose(f.RegisteredState, f.SelectedLayer, inputs);
            Assert.That(native["Smile"], Is.EqualTo(70).Within(.02f));
            Assert.That(native["Brow"], Is.EqualTo(20).Within(.02f));
            var original = f.Snapshot();
            var repeatedNative = f.NativePose(f.RegisteredState, f.SelectedLayer, inputs);
            Assert.That(repeatedNative["Smile"], Is.EqualTo(native["Smile"]).Within(.02f));
            f.AssertUnchanged(original);
            var source = VrChatExpressionMenu.Read(f.Avatar.Source, new VrChatMenuImportPolicy { SkipAll = true });
            var serial = 0;
            FaceEmoExpressions.ReadRegistered(f.Avatar.Source, f.Registered, "FaceEmo", source,
                ref serial, new HashSet<object>(), 0);
            Assert.That(source.Entries, Has.Count.EqualTo(1));
            var entry = source.Entries.Single();
            if (ambiguous)
            {
                Assert.That(entry.Error, Does.Contain("Parameter Driver").And.Contain("Pet"));
                Assert.That(entry.Values, Has.Count.EqualTo(2));
                Assert.That(entry.Values.Select(value => value.Weight), Is.All.EqualTo(90),
                    "An ambiguous preview cannot partially rewrite the registered clip.");
            }
            else
            {
                Assert.That(entry.Error, Is.Null, Errors(source));
                AssertPose(entry, native["Smile"], native["Brow"]);
            }
            f.AssertUnchanged(original);
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task PreparedRegisteredPlayerKeepsNativeContactUnderlayAndSelectableBinding(bool fullLilToon, bool longCommonCurve)
        {
            using var f = new Fixture(.5f, faceEmo: true, longCommonCurve: longCommonCurve);
            var original = f.Snapshot();
            var native = f.NativePose(f.RegisteredState, f.SelectedLayer);
            Assert.That(native["Smile"], Is.EqualTo(65).Within(.02f));
            Assert.That(native["Brow"], Is.EqualTo(20).Within(.02f));
            if (longCommonCurve)
            {
                var curve = AnimationUtility.GetEditorCurve(f.CommonClip,
                    EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Brow"));
                Assert.That(curve.keys.Last().time, Is.EqualTo(3601));
                Assert.That(curve.Evaluate(1800), Is.EqualTo(20));
                Assert.Throws<InvalidOperationException>(() => VrChatGestureExpressions.ReadCurve(curve),
                    "The portable animation format retains its own limits.");
            }
            var clone = Object.Instantiate(f.Avatar.Source);
            var owned = new List<Mesh>();
            try
            {
                var source = VrChatExpressionMenu.Read(clone, new VrChatMenuImportPolicy { SkipAll = true });
                var snapshot = FaceEmoExpressions.CaptureRegistered(clone, f.Registered, deferPermanentOverrides: true);
                var serial = 0;
                FaceEmoExpressions.ReadRegistered(clone, f.Registered, "FaceEmo", source,
                    ref serial, new HashSet<object>(), 0, bindings: snapshot);
                var entry = source.Entries.Single();
                Assert.That(entry.Error, Is.Null);
                using var bindings = new PreparedExpressionBindings(clone, source);
                FaceEmoExpressions.ApplyPreparedDefaultFace(clone, source, bindings, registeredBindings: snapshot);
                Assert.That(entry.Error, Is.Null, Errors(source));
                AssertPose(entry, native["Smile"], native["Brow"]);
                bindings.Capture(source);
                var warnings = new List<string>();
                AvatarBaseShape.Preserve(f.Avatar.Source, clone, owned, warnings);
                var baked = VrChatExpressionBaker.Bake(f.Avatar.Source, clone, source, owned, warnings, bindings);
                Assert.That(baked, Has.Count.EqualTo(1), string.Join("\n", warnings));
                // The registration has already passed the prepared native PLAYER
                // route. Prevent automatic menu discovery from registering it twice.
                Object.DestroyImmediate(clone.GetComponent(SdkType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor")));
                var bytes = VrmMenuExpressions.Add(Export(clone, fullLilToon, warnings), baked);
                Assert.That(VrmMenuExpressions.CountRegistered(bytes), Is.EqualTo(1), string.Join("\n", warnings));
                await AssertRoundTrip(f, bytes, new Dictionary<string, float> { ["VRChat / " + entry.Name] = 65 });
            }
            finally
            {
                Object.DestroyImmediate(clone);
                foreach (var mesh in owned) Object.DestroyImmediate(mesh);
            }
            f.AssertUnchanged(original);
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void UnreachableOtherPlayableWriterDoesNotPoisonFixedFacialClosure(bool gateReadByFx, bool menuOff)
        {
            using var f = new Fixture(.5f, gateReadByFx: gateReadByFx);
            var original = f.Snapshot();
            var source = VrChatExpressionMenu.Read(f.Avatar.Source);
            Assert.That(source.OtherControllers, Has.Count.EqualTo(1));
            Assert.That(source.ExternalParameters, Does.Contain("Pet"));
            var values = VrChatExpressionSampler.SampleFixed(f.Avatar.Source, f.Controller, source.Defaults,
                new Dictionary<string, float> { ["Face"] = menuOff ? 0 : 1 }, metadata: source);
            foreach (var value in values.Where(value => value.Shape == "Smile"))
                Assert.That(value.Weight, Is.EqualTo(menuOff ? 40 : 70).Within(.02f));
            Assert.That(values.Count(value => value.Shape == "Smile"), Is.EqualTo(2));
            f.AssertUnchanged(original);
        }

        [TestCase("reachable-writer")]
        [TestCase("fx-driver-changes-gate")]
        [TestCase("fx-curve-changes-gate")]
        [TestCase("reachable-unknown-callback")]
        public void ReachableOtherPlayableChangesRemainDiagnosed(string kind)
        {
            using var f = new Fixture(.5f);
            if (kind == "reachable-writer")
                ParameterDriverExpressionTests.Driver(f.OtherIdle, ParameterDriverExpressionTests.Op("Set", "Pet", 1));
            else if (kind == "fx-driver-changes-gate")
                ParameterDriverExpressionTests.Driver(f.BaseState, ParameterDriverExpressionTests.Op("Set", "AFK", 1));
            else if (kind == "fx-curve-changes-gate")
                AnimationUtility.SetEditorCurve((AnimationClip)f.BaseState.motion,
                    EditorCurveBinding.FloatCurve("", typeof(Animator), "AFK"), AnimationCurve.Constant(0, 1, 1));
            else f.OtherIdle.AddStateMachineBehaviour(SdkType("VRC.SDK3.Avatars.Components.VRCAnimatorLayerControl"));
            var original = f.Snapshot();
            var source = VrChatExpressionMenu.Read(f.Avatar.Source);
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleFixed(
                f.Avatar.Source, f.Controller, source.Defaults, new Dictionary<string, float> { ["Face"] = 1 }, metadata: source));
            Assert.That(error.Message, Does.Contain(kind == "reachable-unknown-callback" ? "VRCAnimatorLayerControl" : "FX以外"));
            f.AssertUnchanged(original);
        }

        [TestCase("different-defaults")]
        [TestCase("different-types")]
        [TestCase("selected-gate-changes")]
        public void SharedOtherPlayableGatesCannotUseUnsafeFixedValues(string kind)
        {
            using var f = new Fixture(.5f);
            var gate = kind == "selected-gate-changes" ? "Face" : "SharedGate";
            if (kind != "selected-gate-changes")
                f.Controller.AddParameter(gate, kind == "different-types" ? AnimatorControllerParameterType.Bool : AnimatorControllerParameterType.Int);
            f.Other.AddParameter(gate, AnimatorControllerParameterType.Int);
            if (kind == "different-defaults")
            {
                var parameters = f.Other.parameters;
                parameters.Single(parameter => parameter.name == gate).defaultInt = 1;
                f.Other.parameters = parameters;
            }
            var fxGate = f.Controller.parameters.Single(parameter => parameter.name == gate);
            var otherGate = f.Other.parameters.Single(parameter => parameter.name == gate);
            Assert.That(fxGate.type, Is.EqualTo(kind == "different-types" ? AnimatorControllerParameterType.Bool : AnimatorControllerParameterType.Int));
            Assert.That(otherGate.type, Is.EqualTo(AnimatorControllerParameterType.Int));
            Assert.That(fxGate.defaultInt, Is.Zero);
            Assert.That(fxGate.defaultBool, Is.False);
            Assert.That(otherGate.defaultInt, Is.EqualTo(kind == "different-defaults" ? 1 : 0));
            var writer = f.Other.layers[0].stateMachine.AddState("Shared gate writer");
            writer.writeDefaultValues = false;
            ParameterDriverExpressionTests.Driver(writer, ParameterDriverExpressionTests.Op("Set", "Pet", 1));
            var transition = f.OtherIdle.AddTransition(writer);
            transition.hasExitTime = false;
            transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, gate);
            var original = f.Snapshot();
            var source = VrChatExpressionMenu.Read(f.Avatar.Source);
            Assert.That(source.Defaults["Face"], Is.Zero);
            if (gate == "SharedGate") Assert.That(source.Defaults.ContainsKey(gate), Is.False);
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleFixed(
                f.Avatar.Source, f.Controller, source.Defaults, new Dictionary<string, float> { ["Face"] = 1 }, metadata: source));
            Assert.That(error.Message, Does.Contain("FX以外").And.Contain("Pet"));
            f.AssertUnchanged(original);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FalseNormalTimedGateKeepsTheManualAndOffFace(bool menuOff)
        {
            using var f = new Fixture(.5f);
            TimedGate(f, "AFK");
            var original = f.Snapshot();
            var source = VrChatExpressionMenu.Read(f.Avatar.Source);
            var state = f.Controller.layers[f.SelectedLayer].stateMachine.states.Single(child =>
                child.state.name == (menuOff ? "Manual idle" : "Manual face")).state;
            var native = f.NativePose(state, f.SelectedLayer);
            Assert.That(native["Smile"], Is.EqualTo(menuOff ? 40 : 70).Within(.02f));
            var values = VrChatExpressionSampler.SampleFixed(f.Avatar.Source, f.Controller, source.Defaults,
                new Dictionary<string, float> { ["Face"] = menuOff ? 0 : 1 }, metadata: source);
            Assert.That(values.Count(value => value.Shape == "Smile"), Is.EqualTo(2));
            foreach (var value in values.Where(value => value.Shape == "Smile"))
                Assert.That(value.Weight, Is.EqualTo(native["Smile"]).Within(.02f));
            f.AssertUnchanged(original);
        }

        [Test]
        public void FalseNormalTimedGateKeepsTheNativeDirectGesture()
        {
            using var f = new Fixture(.5f);
            TimedGate(f, "AFK");
            var original = f.Snapshot();
            var source = VrChatExpressionMenu.Read(f.Avatar.Source);
            var native = f.NativePose(f.GestureState, f.SelectedLayer);
            Assert.That(native["Smile"], Is.EqualTo(60).Within(.02f));
            var entry = new VrChatExpressionMenu.Entry();
            VrChatGestureExpressions.ReadClip(f.Avatar.Source, (AnimationClip)f.GestureState.motion, entry);
            VrChatExpressionSampler.ApplyFixedPermanentOverrides(f.Avatar.Source, f.Controller, entry,
                f.SelectedLayer, metadata: source);
            Assert.That(entry.Values.Count(value => value.Shape == "Smile"), Is.EqualTo(2));
            foreach (var value in entry.Values.Where(value => value.Shape == "Smile"))
                Assert.That(value.Weight, Is.EqualTo(native["Smile"]).Within(.02f));
            f.AssertUnchanged(original);
        }

        [TestCase("raw")]
        [TestCase("driver")]
        [TestCase("curve")]
        [TestCase("unmodelled")]
        public void TimedGatesWithoutAnUnwrittenFixedProofRemainDiagnosed(string kind)
        {
            using var f = new Fixture(.5f);
            var gate = kind == "unmodelled" ? "EyeHeightAsMeters" : "TimingGate";
            f.Controller.AddParameter(gate, AnimatorControllerParameterType.Float);
            TimedGate(f, gate);
            if (kind == "driver")
                ParameterDriverExpressionTests.Driver(f.BaseState, ParameterDriverExpressionTests.Op("Set", gate, 1));
            if (kind == "curve")
                AnimationUtility.SetEditorCurve((AnimationClip)f.BaseState.motion,
                    EditorCurveBinding.FloatCurve("", typeof(Animator), gate), AnimationCurve.Linear(0, 0, 1000, 1));
            var original = f.Snapshot();
            var source = VrChatExpressionMenu.Read(f.Avatar.Source);
            // Isolate the legacy raw timed-state policy from the separate
            // contact-producer capability check; its authored rest remains .5.
            if (kind == "raw") source.ExternalParameters.Remove("Pet");
            var selected = new Dictionary<string, float> { ["Face"] = 1 };
            var error = Assert.Throws<InvalidOperationException>(() =>
            {
                if (kind == "raw") VrChatExpressionSampler.Sample(f.Avatar.Source, f.Controller, source.Defaults, selected, metadata: source);
                else VrChatExpressionSampler.SampleFixed(f.Avatar.Source, f.Controller, source.Defaults, selected, metadata: source);
            });
            Assert.That(error.Message, Does.Contain(kind == "unmodelled" ? "EyeHeightAsMeters" : "時間で遷移"));
            f.AssertUnchanged(original);
        }

        private static void TimedGate(Fixture f, string gate)
        {
            // Give the active base an actual one-second native morph clip and
            // a timed edge beyond the probe window. Contact rest overrides its
            // zero Smile, so the established manual/native reference stays valid.
            foreach (var path in new[] { "Front", "Back" })
                AnimationUtility.SetEditorCurve((AnimationClip)f.BaseState.motion,
                    EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape.Smile"), AnimationCurve.Constant(0, 1, 0));
            var transition = f.BaseState.transitions.Single();
            transition.conditions = new[] { new AnimatorCondition { parameter = gate,
                mode = gate == "AFK" ? AnimatorConditionMode.If : AnimatorConditionMode.Greater, threshold = gate == "AFK" ? 0 : .5f } };
            transition.hasExitTime = true;
            transition.exitTime = 1000;
            Assert.That(((AnimationClip)f.BaseState.motion).length, Is.EqualTo(1));
        }

        [Test]
        public void UnmodelledExternalHeightCannotBecomeAnInventedManualFace()
        {
            using var f = new Fixture(.5f);
            f.Controller.AddParameter("EyeHeightAsMeters", AnimatorControllerParameterType.Float);
            var tree = (BlendTree)f.ContactState.motion;
            tree.blendParameter = "EyeHeightAsMeters";
            var source = VrChatExpressionMenu.Read(f.Avatar.Source);
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleFixed(
                f.Avatar.Source, f.Controller, source.Defaults, new Dictionary<string, float> { ["Face"] = 1 }, metadata: source));
            Assert.That(error.Message, Does.Contain("EyeHeightAsMeters"));
        }

        private static Type SdkType(string name)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(name)).FirstOrDefault(value => value != null);
            if (type == null) Assert.Ignore("Install the real VRChat SDK: " + name);
            return type;
        }

        private static string Errors(VrChatExpressionMenu.Source source) =>
            string.Join("\n", source.Entries.Select(entry => entry.Name + ": " + entry.Error));

        private static void AssertPose(VrChatExpressionMenu.Entry entry, float smile, float brow)
        {
            foreach (var shape in new[] { ("Smile", smile), ("Brow", brow) })
                AssertMorph(entry, shape.Item1, shape.Item2);
        }

        private static void AssertMorph(VrChatExpressionMenu.Entry entry, string shape, float weight)
        {
            var values = entry.Values.Where(value => value.Shape == shape).ToArray();
            Assert.That(values.Select(value => value.Path), Is.EquivalentTo(new[] { "Front", "Back" }), entry.Name + " / " + shape);
            foreach (var value in values) Assert.That(value.Weight, Is.EqualTo(weight).Within(.02f), entry.Name + " / " + shape);
        }

        private static byte[] Export(GameObject avatar, bool fullLilToon, List<string> warnings) =>
            UniVrmOneClickExporter.Export(avatar, "Manual expression compatibility", "Tests", warnings,
                exporterVersion: fullLilToon ? "manual-expression-regression" : null,
                lilToonVersion: fullLilToon ? "2.3.4" : null,
                gimmickOptions: new ExportGimmickOptions { AutoExclude = false });

        private static async Task AssertRoundTrip(Fixture f, byte[] bytes, IDictionary<string, float> expressions)
        {
            var imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller(),
                controlRigGenerationOption: ControlRigGenerationOption.None);
            try
            {
                var names = imported.Vrm.Expression.CustomClips.Where(clip => clip.name.StartsWith("VRChat / ", StringComparison.Ordinal)).Select(clip => clip.name);
                Assert.That(names, Is.EquivalentTo(expressions.Keys), "Successful export must retain the actual selectable manual entries.");
                Assert.That(imported.Vrm.Expression.Blink, Is.Not.Null);
                Assert.That(imported.Vrm.Expression.Blink.MorphTargetBindings, Has.Length.EqualTo(2));
                var color = imported.Vrm.Expression.CustomClips.Single(clip => clip.name == "Authored color");
                Assert.That(color.MaterialColorBindings, Has.Length.EqualTo(1));
                Assert.That(imported.GetComponentsInChildren<SkinnedMeshRenderer>().Any(skin =>
                    skin.sharedMaterial.name == color.MaterialColorBindings[0].MaterialName), Is.True);
                foreach (var expression in expressions)
                {
                    foreach (var name in expressions.Keys) imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(name), 0);
                    var clip = imported.Vrm.Expression.CustomClips.Single(value => value.name == expression.Key);
                    Assert.That(clip.MorphTargetBindings, Has.Length.EqualTo(2));
                    imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(expression.Key), 1);
                    imported.Runtime.Process();
                    foreach (var binding in clip.MorphTargetBindings)
                    {
                        var skin = imported.transform.Find(binding.RelativePath).GetComponent<SkinnedMeshRenderer>();
                        Assert.That(binding.Index, Is.LessThan(skin.sharedMesh.blendShapeCount));
                        var point = skin.sharedMesh.vertices[0];
                        var delta = new Vector3[skin.sharedMesh.vertexCount];
                        for (var index = 0; index < skin.sharedMesh.blendShapeCount; index++)
                        {
                            skin.sharedMesh.GetBlendShapeFrameVertices(index, 0, delta, null, null);
                            point += delta[0] * skin.GetBlendShapeWeight(index) / 100;
                        }
                        var expected = f.Avatar.Mesh.vertices[0] + Vector3.right * (.02f * expression.Value / 100) + Vector3.up * (.03f * .2f);
                        Assert.That(Vector3.Distance(point, expected), Is.LessThan(.0001f), expression.Key);
                    }
                }
            }
            finally { imported.DisposeRuntime(); Object.DestroyImmediate(imported.gameObject); }
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly AttachmentConnectionTests.Fixture Avatar;
            internal readonly AnimatorController Controller, Other;
            internal readonly AnimatorState BaseState, ContactState, GestureState, RegisteredState, OtherIdle;
            internal readonly int SelectedLayer;
            internal readonly AnimationClip CommonClip;
            internal readonly object Registered;
            private readonly string folder;
            private readonly VRM10Object settings;
            private readonly VRM10Expression blink, color;

            internal Fixture(float contactRest, bool faceEmo = false, bool longCommonCurve = false, bool gateReadByFx = true)
            {
                var descriptorType = SdkType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
                var shader = Shader.Find("lilToon");
                if (shader == null) Assert.Ignore("Install the real lilToon shader.");
                Avatar = new AttachmentConnectionTests.Fixture();
                var folderName = "__ManualExpressionCompatibility_" + Guid.NewGuid().ToString("N");
                AssetDatabase.CreateFolder("Assets", folderName); folder = "Assets/" + folderName;
                Avatar.Mesh.ClearBlendShapes();
                foreach (var shape in new[] { ("Smile", Vector3.right * .02f), ("Brow", Vector3.up * .03f), ("Blink", Vector3.down * .04f) })
                    Avatar.Mesh.AddBlendShapeFrame(shape.Item1, 100, Enumerable.Repeat(shape.Item2, Avatar.Mesh.vertexCount).ToArray(), null, null);
                foreach (var skin in Avatar.Source.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    skin.sharedMaterial.shader = shader; skin.sharedMaterial.name = "Manual face material";
                    for (var index = 0; index < 3; index++) skin.SetBlendShapeWeight(index, 0);
                }
                settings = ScriptableObject.CreateInstance<VRM10Object>();
                blink = ScriptableObject.CreateInstance<VRM10Expression>(); blink.name = "Authored blink";
                blink.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 2, 1), new MorphTargetBinding("Back", 2, 1) };
                settings.Expression.Blink = blink;
                color = ScriptableObject.CreateInstance<VRM10Expression>(); color.name = "Authored color";
                color.MaterialColorBindings = new[] { new MaterialColorBinding { MaterialName = "Manual face material", BindType = MaterialColorType.color, TargetValue = Color.red } };
                settings.Expression.CustomClips.Add(color); Avatar.Source.AddComponent<Vrm10Instance>().Vrm = settings;
                Controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
                Controller.AddParameter("Face", AnimatorControllerParameterType.Int);
                Controller.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
                Controller.AddParameter("Pet", AnimatorControllerParameterType.Float);
                var parameters = Controller.parameters; parameters.Single(value => value.name == "Pet").defaultFloat = contactRest; Controller.parameters = parameters;
                Controller.AddParameter("AFK_Step", AnimatorControllerParameterType.Int);
                if (gateReadByFx) Controller.AddParameter("AFK", AnimatorControllerParameterType.Bool);
                Controller.AddParameter(NeutralShapeSamplerTests.GestureWeightProxy, AnimatorControllerParameterType.Float);
                BaseState = State(Controller.layers[0].stateMachine, "Body neutral", Clip("Body neutral"));
                if (gateReadByFx)
                {
                    var body = State(Controller.layers[0].stateMachine, "AFK body", Clip("AFK face", ("Smile", 10)));
                    var transition = Transition(BaseState, body, "AFK", 0, AnimatorConditionMode.If);
                    transition.AddCondition(AnimatorConditionMode.Greater, 0, "AFK_Step");
                }
                ContactState = State(Layer("Contact rest"), "Authored contact rest", Tree("Contact rest", "Pet", "Smile", 80));
                State(Layer("Generated proxy face"), "Proxy face", Tree("Proxy face", NeutralShapeSamplerTests.GestureWeightProxy, "Brow", 40));
                if (faceEmo)
                {
                    CommonClip = Clip("Common face", ("Brow", 20));
                    if (longCommonCurve) foreach (var path in new[] { "Front", "Back" })
                        AnimationUtility.SetEditorCurve(CommonClip, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape.Brow"), AnimationCurve.Constant(0, 3601, 20));
                    State(Layer("[ USER EDIT ] DEFAULT FACE"), "DEFAULT", CommonClip);
                }
                var selected = Layer(faceEmo ? "[ USER EDIT ] FACE EMOTE PLAYER" : "Manual expressions", .5f);
                SelectedLayer = Controller.layers.Length - 1;
                var idle = State(selected, "Manual idle", Clip("Manual idle"));
                if (faceEmo)
                {
                    var clip = Clip("Registered face", ("Smile", 90));
                    RegisteredState = State(selected, "Registered face", clip);
                    var externalClip = Object.Instantiate(clip); externalClip.name = "Registered face";
                    AssetDatabase.CreateAsset(externalClip, folder + "/Registered.anim");
                    Registered = new { Modes = new[] { new { ChangeDefaultFace = true, DisplayName = "Registered",
                        Animation = new { GUID = AssetDatabase.AssetPathToGUID(folder + "/Registered.anim") } } } };
                }
                else
                {
                    var face = State(selected, "Manual face", Clip("Menu face", ("Smile", 100)));
                    Transition(idle, face, "Face", 1);
                    GestureState = State(selected, "Gesture face", Clip("Gesture face", ("Smile", 80)));
                    Transition(idle, GestureState, "GestureRight", 2);
                }
                var proxy = Clip("Generated gesture proxy");
                AnimationUtility.SetEditorCurve(proxy, EditorCurveBinding.FloatCurve("", typeof(Animator), NeutralShapeSamplerTests.GestureWeightProxy),
                    NeutralShapeSamplerTests.GeneratedProxyCurve("firstIn"));
                NeutralShapeSamplerTests.AssertNativeProxyMetadata(proxy, "firstIn");
                State(Layer("Generated proxy writer"), "Proxy writer", proxy);
                Other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Body.controller");
                Other.AddParameter("AFK", AnimatorControllerParameterType.Bool);
                Other.AddParameter("AFK_Step", AnimatorControllerParameterType.Int);
                Other.AddParameter("Pet", AnimatorControllerParameterType.Float);
                parameters = Other.parameters; parameters.Single(value => value.name == "Pet").defaultFloat = contactRest; Other.parameters = parameters;
                OtherIdle = State(Other.layers[0].stateMachine, "Tracked body", null);
                var tracking = OtherIdle.AddStateMachineBehaviour(SdkType("VRC.SDK3.Avatars.Components.VRCAnimatorTrackingControl"));
                using (var data = new SerializedObject(tracking))
                { var hand = data.FindProperty("trackingLeftHand"); hand.enumValueIndex = Array.IndexOf(hand.enumNames, "Tracking"); data.ApplyModifiedPropertiesWithoutUndo(); }
                var afk = State(Other.layers[0].stateMachine, "Unreachable AFK writer", null);
                ParameterDriverExpressionTests.Driver(afk, ParameterDriverExpressionTests.Op("Set", "AFK_Step", 1));
                Transition(OtherIdle, afk, "AFK", 0, AnimatorConditionMode.If);
                var contact = Avatar.Source.AddComponent(SdkType("VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver"));
                using (var data = new SerializedObject(contact)) { data.FindProperty("parameter").stringValue = "Pet"; data.ApplyModifiedPropertiesWithoutUndo(); }
                var menu = ScriptableObject.CreateInstance(SdkType("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu"));
                AssetDatabase.CreateAsset(menu, folder + "/Menu.asset");
                using (var data = new SerializedObject(menu))
                {
                    var controls = data.FindProperty("controls"); controls.arraySize = faceEmo ? 0 : 1;
                    if (!faceEmo)
                    {
                        var row = controls.GetArrayElementAtIndex(0); row.FindPropertyRelative("name").stringValue = "Manual face";
                        var type = row.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "Toggle");
                        row.FindPropertyRelative("parameter").FindPropertyRelative("name").stringValue = "Face";
                        row.FindPropertyRelative("value").floatValue = 1;
                    }
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
                var expressionParameters = ScriptableObject.CreateInstance(SdkType("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters"));
                AssetDatabase.CreateAsset(expressionParameters, folder + "/Parameters.asset");
                using (var data = new SerializedObject(expressionParameters))
                {
                    var rows = data.FindProperty("parameters"); rows.arraySize = 2;
                    for (var i = 0; i < 2; i++)
                    {
                        var row = rows.GetArrayElementAtIndex(i); row.FindPropertyRelative("name").stringValue = i == 0 ? "Face" : "Pet";
                        var type = row.FindPropertyRelative("valueType"); type.enumValueIndex = Array.IndexOf(type.enumNames, i == 0 ? "Int" : "Float");
                        row.FindPropertyRelative("defaultValue").floatValue = i == 0 ? 0 : contactRest;
                    }
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
                var descriptor = Avatar.Source.AddComponent(descriptorType);
                using (var data = new SerializedObject(descriptor))
                {
                    data.FindProperty("customExpressions").boolValue = true;
                    data.FindProperty("expressionsMenu").objectReferenceValue = menu;
                    data.FindProperty("expressionParameters").objectReferenceValue = expressionParameters;
                    data.FindProperty("customizeAnimationLayers").boolValue = true;
                    var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = 2;
                    for (var i = 0; i < 2; i++)
                    {
                        var row = layers.GetArrayElementAtIndex(i); var type = row.FindPropertyRelative("type");
                        type.enumValueIndex = Array.IndexOf(type.enumNames, i == 0 ? "FX" : "Action");
                        row.FindPropertyRelative("isDefault").boolValue = false;
                        row.FindPropertyRelative("animatorController").objectReferenceValue = i == 0 ? Controller : Other;
                    }
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
                AssetDatabase.SaveAssets();
            }

            private AnimatorStateMachine Layer(string name, float weight = 1)
            {
                Controller.AddLayer(name); var layers = Controller.layers; layers.Last().defaultWeight = weight; Controller.layers = layers;
                return layers.Last().stateMachine;
            }
            private AnimationClip Clip(string name, params (string Shape, float Weight)[] values)
            {
                var clip = new AnimationClip { name = name }; AssetDatabase.AddObjectToAsset(clip, Controller);
                foreach (var path in new[] { "Front", "Back" }) foreach (var value in values)
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + value.Shape),
                        AnimationCurve.Constant(0, 1, value.Weight));
                return clip;
            }
            private BlendTree Tree(string name, string parameter, string shape, float end)
            {
                var tree = new BlendTree { name = name, blendType = BlendTreeType.Simple1D, blendParameter = parameter, useAutomaticThresholds = false };
                tree.AddChild(Clip(name + " zero", (shape, 0)), 0); tree.AddChild(Clip(name + " full", (shape, end)), 1);
                AssetDatabase.AddObjectToAsset(tree, Controller); return tree;
            }
            private static AnimatorState State(AnimatorStateMachine machine, string name, Motion motion)
            {
                var state = machine.AddState(name); state.motion = motion; state.writeDefaultValues = false;
                if (machine.states.Length == 1) machine.defaultState = state; return state;
            }
            private static AnimatorStateTransition Transition(AnimatorState from, AnimatorState to, string parameter, float value,
                AnimatorConditionMode mode = AnimatorConditionMode.Equals)
            {
                var transition = from.AddTransition(to); transition.hasExitTime = false; transition.duration = 0;
                transition.AddCondition(mode, value, parameter); return transition;
            }
            internal (AnimationClip Original, AnimationClip Current) ConfigureRegisteredStateSet(float value = .25f, bool pluginReplacement = false)
            {
                var original = AssetDatabase.LoadAssetAtPath<AnimationClip>(folder + "/Registered.anim");
                Assert.That(original, Is.Not.Null);
                var clip = original;
                if (pluginReplacement)
                {
                    clip = Object.Instantiate(original); clip.name = "Renamed committed registered endpoint";
                    AssetDatabase.AddObjectToAsset(clip, Controller);
                    Assert.That(clip, Is.Not.SameAs(original));
                    Assert.That(clip.name, Is.Not.EqualTo(original.name));
                }
                var tree = new BlendTree { name = "Registered endpoint tree", blendType = BlendTreeType.Simple1D,
                    blendParameter = "Pet", useAutomaticThresholds = false };
                tree.AddChild(clip, 0); tree.AddChild(clip, 1); AssetDatabase.AddObjectToAsset(tree, Controller);
                RegisteredState.motion = tree;
                ParameterDriverExpressionTests.Driver(RegisteredState, ParameterDriverExpressionTests.Op("Set", "Pet", value));
                Assert.That(tree.children.Select(child => child.motion), Is.All.SameAs(clip),
                    "The endpoint tree retains one committed clip identity in both children.");
                return (original, clip);
            }

            internal Dictionary<string, float> NativePose(AnimatorState selected, int layer, IDictionary<string, float> parameterValues = null)
            {
                var clone = Object.Instantiate(Avatar.Source); var graph = PlayableGraph.Create("Independent manual expression native reference");
                try
                {
                    var animator = clone.GetComponent<Animator>(); animator.runtimeAnimatorController = null;
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                    var playable = AnimatorControllerPlayable.Create(graph, Controller);
                    for (var index = 0; index < Controller.layers.Length; index++)
                        playable.SetLayerWeight(index, index == 0 ? 1 : Controller.layers[index].defaultWeight);
                    AnimationPlayableOutput.Create(graph, "Native reference", animator).SetSourcePlayable(playable);
                    graph.Play(); graph.Evaluate(0);
                    playable.Play(selected.name, layer, 0); graph.Evaluate(0);
                    // Independent SDK Set counterfactual: use the real authored
                    // controller and native clips without exporter adapters or
                    // invoking the SDK's process-wide client callbacks.
                    if (parameterValues != null)
                        foreach (var pair in parameterValues)
                        {
                            var type = Controller.parameters.Single(parameter => parameter.name == pair.Key).type;
                            if (type == AnimatorControllerParameterType.Bool) playable.SetBool(pair.Key, pair.Value != 0);
                            else if (type == AnimatorControllerParameterType.Int) playable.SetInteger(pair.Key, (int)pair.Value);
                            else if (type == AnimatorControllerParameterType.Float) playable.SetFloat(pair.Key, pair.Value);
                            else Assert.Fail("The native counterfactual cannot set a trigger.");
                        }
                    Dictionary<string, float> stable = null;
                    for (var frame = 0; frame < 144; frame++)
                    {
                        graph.Evaluate(1f / 60);
                        if (frame < 120) continue;
                        var skin = clone.transform.Find("Front").GetComponent<SkinnedMeshRenderer>();
                        Assert.That(playable.GetCurrentAnimatorStateInfo(layer).shortNameHash, Is.EqualTo(Animator.StringToHash(selected.name)));
                        var values = Enumerable.Range(0, Avatar.Mesh.blendShapeCount).ToDictionary(Avatar.Mesh.GetBlendShapeName, skin.GetBlendShapeWeight);
                        Assert.That(float.IsNaN(playable.GetFloat(NeutralShapeSamplerTests.GestureWeightProxy)) || float.IsInfinity(playable.GetFloat(NeutralShapeSamplerTests.GestureWeightProxy)), Is.False);
                        foreach (var value in values)
                        {
                            Assert.That(float.IsNaN(value.Value) || float.IsInfinity(value.Value), Is.False);
                            if (stable != null) Assert.That(value.Value, Is.EqualTo(stable[value.Key]).Within(.01f));
                        }
                        stable = values;
                    }
                    return stable;
                }
                finally { graph.Destroy(); Object.DestroyImmediate(clone); }
            }
            internal Dictionary<Object, string> Snapshot()
            {
                var assets = AssetDatabase.FindAssets("", new[] { folder }).Select(AssetDatabase.GUIDToAssetPath)
                    .SelectMany(AssetDatabase.LoadAllAssetsAtPath).Concat(new Object[] { settings, blink, color })
                    .Concat(Avatar.Source.GetComponents<Component>()).Where(value => value != null).Distinct().ToArray();
                foreach (var clip in assets.OfType<AnimationClip>()) { AnimationUtility.GetCurveBindings(clip); AnimationUtility.GetObjectReferenceCurveBindings(clip); }
                return assets.ToDictionary(value => value, value => EditorJsonUtility.ToJson(value));
            }
            internal void AssertUnchanged(Dictionary<Object, string> original)
            {
                foreach (var value in original) Assert.That(EditorJsonUtility.ToJson(value.Key), Is.EqualTo(value.Value), value.Key.name);
                Assert.That(Avatar.Source.GetComponent<Vrm10Instance>().Vrm, Is.SameAs(settings));
                Assert.That(Avatar.Mesh.blendShapeCount, Is.EqualTo(3));
                foreach (var skin in Avatar.Source.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    Assert.That(skin.sharedMesh, Is.SameAs(Avatar.Mesh));
                    for (var i = 0; i < 3; i++) Assert.That(skin.GetBlendShapeWeight(i), Is.Zero);
                }
            }
            public void Dispose()
            {
                Avatar.Dispose(); Object.DestroyImmediate(settings); Object.DestroyImmediate(blink); Object.DestroyImmediate(color);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
