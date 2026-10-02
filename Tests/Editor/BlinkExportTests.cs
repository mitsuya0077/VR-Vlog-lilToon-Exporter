using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UniGLTF;
using UniVRM10;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class BlinkExportTests
    {
        GameObject root, copy;
        readonly List<Object> owned = new List<Object>();
        readonly List<Mesh> generated = new List<Mesh>();

        [SetUp] public void SetUp() => root = new GameObject("avatar");
        [TearDown] public void TearDown()
        {
            if (copy != null) Object.DestroyImmediate(copy);
            Object.DestroyImmediate(root);
            foreach (var value in generated) if (value != null) Object.DestroyImmediate(value);
            foreach (var value in owned) if (value != null) Object.DestroyImmediate(value);
            generated.Clear(); owned.Clear();
        }

        SkinnedMeshRenderer Skin(params string[] names)
        {
            var go = new GameObject("face"); go.transform.SetParent(root.transform, false);
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            foreach (var name in names)
                mesh.AddBlendShapeFrame(name, 100, new[] { Vector3.up * .2f, Vector3.zero, Vector3.zero }, new Vector3[3], new Vector3[3]);
            owned.Add(mesh);
            var skin = go.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = mesh;
            return skin;
        }

        [Test] public void EyeSpacingNeverWinsOverAnEyelidShape()
        {
            var skin = Skin("eye_close", "vrc.Blink", "eye_blink_1", "eye_blink_1_L", "eye_blink_1_R");
            var resolved = BlinkExportSession.Resolve(root);
            Assert.That(resolved.Slots[0].Single().Shape, Is.EqualTo("vrc.Blink"));
            Assert.That(resolved.Slots[1].Single().Shape, Is.EqualTo("eye_blink_1_L"));
            Assert.That(resolved.Slots[2].Single().Shape, Is.EqualTo("eye_blink_1_R"));
            Assert.That(resolved.Slots[0].Single().Renderer, Is.SameAs(skin));
        }

        [Test] public void StatusReusesScansUntilExpiryOrARelevantSettingChanges()
        {
            Skin("Blink");
            var cache = new LilToonExporterWindow.BlinkStatusCache();
            var calls = 0;
            BlinkExportSession Resolve() { calls++; return BlinkExportSession.Resolve(root); }
            cache.Refresh(10, Resolve);
            var first = cache.Resolved;
            for (var i = 1; i < 100; i++) cache.Refresh(10 + i * .01, Resolve);
            Assert.That(calls, Is.EqualTo(1), "Layout, repaint and input must share the status scan.");
            Assert.That(cache.Resolved, Is.SameAs(first));
            cache.Refresh(11, Resolve);
            Assert.That(calls, Is.EqualTo(2), "External avatar edits are picked up by the periodic refresh.");
            cache.Invalidate();
            cache.Refresh(11.01, Resolve);
            Assert.That(calls, Is.EqualTo(3), "Changing bindings or exclusions must refresh immediately.");
            cache.Invalidate();
        }

        [Test] public void StatusClearsAnEarlierSuccessWhenTheLiveBindingBecomesInvalid()
        {
            var skin = Skin("Blink");
            var cache = new LilToonExporterWindow.BlinkStatusCache();
            var calls = 0;
            BlinkExportSession Resolve() { calls++; return BlinkExportSession.Resolve(root); }
            cache.Refresh(0, Resolve);
            skin.enabled = false;
            cache.Refresh(1, Resolve);
            Assert.That(cache.Resolved, Is.Null);
            Assert.That(cache.Error, Is.Not.Empty);
            cache.Refresh(1.1, Resolve);
            Assert.That(calls, Is.EqualTo(2), "An invalid avatar must not be rescanned on every repaint either.");
            skin.enabled = true;
            cache.Invalidate();
            cache.Refresh(1.2, Resolve);
            Assert.That(cache.Resolved, Is.Not.Null);
            Assert.That(cache.Error, Is.Null);
            cache.Invalidate();
        }

        [TestCase("eye_close")][TestCase("previewBlink")][TestCase("eye_close_left")]
        public void AmbiguousOrPartialNamesNeedAnExplicitChoice(string name)
        {
            Skin(name);
            Assert.Throws<InvalidOperationException>(() => BlinkExportSession.Resolve(root));
            Assert.That(BlinkExportSession.Resolve(root, new BlinkExportOptions { Mode = BlinkExportMode.None }).Disabled, Is.True);
        }

        [Test] public void LeftAndRightMustBelongToOneNameFamily()
        {
            Skin("Blink_L", "eye_blink_1_R");
            Assert.Throws<InvalidOperationException>(() => BlinkExportSession.Resolve(root));
        }

        [Test] public void PartialUnifiedClosureDoesNotInventBilateralBlink()
        {
            Skin("EyeClosedLeft");
            var result = BlinkExportSession.Resolve(root);
            Assert.That(result.Disabled, Is.False);
            Assert.That(result.Slots.All(slot => slot.Count == 0), Is.True);
            copy = Object.Instantiate(root);
            using var prepared = result.ForClone(root, copy);
            Assert.DoesNotThrow(() => prepared.Bake(copy, generated));
        }

        [Test] public void UnifiedEyesOnDifferentRenderersFormOneBilateralClosure()
        {
            var left = Skin("EyeClosedLeft"); var right = Skin("EyeClosedRight");
            var result = BlinkExportSession.Resolve(root);
            Assert.That(result.Slots[0].Count, Is.EqualTo(2));
            Assert.That(result.Slots[1].Single().Renderer, Is.SameAs(left));
            Assert.That(result.Slots[2].Single().Renderer, Is.SameAs(right));
        }

        [TestCase(true, false)][TestCase(false, false)][TestCase(true, true)][TestCase(false, true)]
        public void ACompleteUnifiedPairIncludesCompatibleOneSidedEyelashes(bool left, bool normalized)
        {
            var face = Skin("UE/EyeClosedLeft", "UE/EyeClosedRight");
            var shape = normalized ? (left ? "face.eye_closed_left" : "face.eye_closed_right") : (left ? "UE/EyeClosedLeft" : "UE/EyeClosedRight");
            var eyelashes = Skin(shape);
            var result = BlinkExportSession.Resolve(root);
            Assert.That(result.Slots[0].Count, Is.EqualTo(3));
            Assert.That(result.Slots[left ? 1 : 2].Count, Is.EqualTo(2));
            Assert.That(result.Slots[left ? 2 : 1].Count, Is.EqualTo(1));
            Assert.That(result.Slots[0].Count(binding => binding.Renderer == eyelashes && binding.Shape == shape), Is.EqualTo(1));
            Assert.That(result.Slots[left ? 1 : 2].Any(binding => binding.Renderer == eyelashes), Is.True);
            Assert.That(result.Slots[left ? 2 : 1].Single().Renderer, Is.SameAs(face));
            Assert.That(face.sharedMesh.blendShapeCount, Is.EqualTo(2));
            Assert.That(eyelashes.sharedMesh.blendShapeCount, Is.EqualTo(1));
        }

        [Test] public void ACompleteUnifiedPairDoesNotAdoptAnUnrelatedLegacyPartial()
        {
            var face = Skin("UE/EyeClosedLeft", "UE/EyeClosedRight");
            var eyelashes = Skin("Blink_L");
            var result = BlinkExportSession.Resolve(root);
            Assert.That(result.Slots[0].Count, Is.EqualTo(2));
            Assert.That(result.Slots.All(slot => slot.All(binding => binding.Renderer == face)), Is.True);
            Assert.That(eyelashes.sharedMesh.blendShapeCount, Is.EqualTo(1));
        }

        [TestCase(false, false)][TestCase(true, false)][TestCase(false, true)][TestCase(true, true)]
        public void InertUnifiedClosureIsNotSelectedAlongsideUsableExplicitJaw(bool bilateral, bool endpointRest)
        {
            var names = bilateral ? new[] { "UE/JawOpen", "UE/EyeClosed" } : new[] { "UE/JawOpen", "UE/EyeClosedLeft", "UE/EyeClosedRight" };
            var skin = Skin(names);
            if (endpointRest)
                for (var index = 1; index < names.Length; index++) skin.SetBlendShapeWeight(index, 100f);
            else
            {
                skin.sharedMesh.ClearBlendShapes();
                for (var index = 0; index < names.Length; index++)
                    skin.sharedMesh.AddBlendShapeFrame(names[index], 100f, new[] { index == 0 ? Vector3.up * .2f : Vector3.zero, Vector3.zero, Vector3.zero }, new Vector3[3], new Vector3[3]);
            }
            var result = BlinkExportSession.Resolve(root);
            Assert.That(result.Slots.All(slot => slot.Count == 0), Is.True);
            Assert.That(result.HasBilateralPreset, Is.False);
            Assert.That(result.RequiresUnifiedEvidence, Is.True);
            Assert.That(skin.sharedMesh.blendShapeCount, Is.EqualTo(names.Length));
            for (var index = 1; index < names.Length; index++) Assert.That(skin.GetBlendShapeWeight(index), Is.EqualTo(endpointRest ? 100f : 0f));
        }

        [TestCase(false)][TestCase(true)]
        public void InertUnifiedClosureCannotWaiveMissingBlinkForSharedJaw(bool endpointRest)
        {
            var skin = Skin("JawOpen", "EyeClosedLeft", "EyeClosedRight");
            if (endpointRest) { skin.SetBlendShapeWeight(1, 100f); skin.SetBlendShapeWeight(2, 100f); }
            else
            {
                skin.sharedMesh.ClearBlendShapes();
                skin.sharedMesh.AddBlendShapeFrame("JawOpen", 100f, new[] { Vector3.up * .2f, Vector3.zero, Vector3.zero }, new Vector3[3], new Vector3[3]);
                foreach (var name in new[] { "EyeClosedLeft", "EyeClosedRight" }) skin.sharedMesh.AddBlendShapeFrame(name, 100f, new Vector3[3], new Vector3[3], new Vector3[3]);
            }
            Assert.Throws<InvalidOperationException>(() => BlinkExportSession.Resolve(root));
        }

        [Test] public void InertPreferredUnifiedAliasCannotSelectTheMovingLowerAlias()
        {
            var skin = Skin("UE/JawOpen", "UE/EyeClosedLeft", "EyeClosedLeft", "UE/EyeClosedRight");
            skin.SetBlendShapeWeight(1, 100f);
            var result = BlinkExportSession.Resolve(root);
            Assert.That(result.Slots.All(slot => slot.Count == 0), Is.True);
            Assert.That(skin.GetBlendShapeWeight(1), Is.EqualTo(100f));
            Assert.That(skin.GetBlendShapeWeight(2), Is.Zero);
        }

        [Test] public void ExplicitTrackingMarkerKeepsPartialBlinkValidation()
        {
            Skin("EyeClosedLeft");
            var profile = ScriptableObject.CreateInstance<VrmTrackingProfile>(); owned.Add(profile);
            root.AddComponent<VrmTrackingMarker>().profile = profile;
            Assert.Throws<InvalidOperationException>(() => BlinkExportSession.Resolve(root));
        }

        [Test] public void CompleteUnifiedBlinkPairWinsBeforeAnEarlierLegacySingleSide()
        {
            Skin("Blink_L", "EyeClosedLeft", "EyeClosedRight");
            var result = BlinkExportSession.Resolve(root);
            Assert.That(result.Slots[1].Single().Shape, Is.EqualTo("EyeClosedLeft"));
            Assert.That(result.Slots[2].Single().Shape, Is.EqualTo("EyeClosedRight"));
            Assert.That(result.Slots[0].Count, Is.EqualTo(2));
        }

        [Test] public void PartialUnifiedBlinkWinsOverLegacyPartialBeforeCrossRendererPairing()
        {
            var left = Skin("Blink_L", "UE/EyeClosedLeft"); var right = Skin("UE/EyeClosedRight");
            var result = BlinkExportSession.Resolve(root);
            Assert.That(result.Slots[1].Single().Shape, Is.EqualTo("UE/EyeClosedLeft"));
            Assert.That(result.Slots[1].Single().Renderer, Is.SameAs(left));
            Assert.That(result.Slots[2].Single().Renderer, Is.SameAs(right));
            Assert.That(result.Slots[0].Select(binding => binding.Shape), Is.EquivalentTo(new[] { "UE/EyeClosedLeft", "UE/EyeClosedRight" }));
        }

        [TestCase(false)][TestCase(true)]
        public void AllPartialFamiliesRemainAvailableForTheirMatchingOtherRenderer(bool reversed)
        {
            var right = Skin(reversed ? new[] { "eye_blink_1_R", "Blink_L", "UE/JawOpen" } : new[] { "Blink_L", "eye_blink_1_R", "UE/JawOpen" });
            var left = Skin("eye_blink_1_L");
            var result = BlinkExportSession.Resolve(root);
            Assert.That(result.Slots[0].Select(binding => binding.Shape), Is.EquivalentTo(new[] { "eye_blink_1_L", "eye_blink_1_R" }));
            Assert.That(result.Slots[1].Single().Renderer, Is.SameAs(left));
            Assert.That(result.Slots[2].Single().Renderer, Is.SameAs(right));
            Assert.That(result.Slots.All(slot => slot.All(binding => binding.Shape != "Blink_L")), Is.True);
            Assert.That(right.sharedMesh.blendShapeCount, Is.EqualTo(3));
            Assert.That(left.sharedMesh.blendShapeCount, Is.EqualTo(1));
        }

        [Test] public void ACompleteRendererPairKeepsPriorityOverAlternatePartialFamilies()
        {
            var face = Skin("Blink_L", "Blink_R", "eye_blink_1_R", "UE/JawOpen");
            Skin("eye_blink_1_L");
            var result = BlinkExportSession.Resolve(root);
            Assert.That(result.Slots[0].Select(binding => binding.Shape), Is.EquivalentTo(new[] { "Blink_L", "Blink_R" }));
            Assert.That(result.Slots[1].Single().Renderer, Is.SameAs(face));
            Assert.That(result.Slots[2].Single().Renderer, Is.SameAs(face));
        }

        [Test] public void AUnifiedPartialKeepsPriorityOverMultipleLegacyPartialFamilies()
        {
            var left = Skin("Blink_L", "eye_blink_1_R", "UE/EyeClosedLeft");
            var right = Skin("eye_blink_1_L", "UE/EyeClosedRight");
            var result = BlinkExportSession.Resolve(root);
            Assert.That(result.Slots[0].Select(binding => binding.Shape), Is.EquivalentTo(new[] { "UE/EyeClosedLeft", "UE/EyeClosedRight" }));
            Assert.That(result.Slots[1].Single().Renderer, Is.SameAs(left));
            Assert.That(result.Slots[2].Single().Renderer, Is.SameAs(right));
        }

        [Test] public void TheFirstCompatiblePartialFamilyOwnsEachRenderer()
        {
            var left = Skin("eye_blink_1_R", "Blink_L");
            var right = Skin("eye_blink_1_L", "Blink_R", "UE/JawOpen");
            var result = BlinkExportSession.Resolve(root);
            Assert.That(result.Slots[0].Select(binding => binding.Shape), Is.EquivalentTo(new[] { "Blink_L", "Blink_R" }));
            Assert.That(result.Slots[1].Single().Renderer, Is.SameAs(left));
            Assert.That(result.Slots[2].Single().Renderer, Is.SameAs(right));
        }

        [Test] public void DuplicateSemanticNamesAreNotGuessed()
        {
            Skin("Blink", "BLINK");
            Assert.Throws<InvalidOperationException>(() => BlinkExportSession.Resolve(root));
        }
        [TestCase("Blink", .2f)][TestCase("BLINK", .6f)]
        public void ExplicitManualChoiceResolvesCaseDistinctAliasesAndBakesTheSelectedChannel(string selected, float expected)
        {
            var skin = Skin("Blink", "BLINK");
            skin.sharedMesh.ClearBlendShapes();
            skin.sharedMesh.AddBlendShapeFrame("Blink", 100, new[] { Vector3.up * .2f, Vector3.zero, Vector3.zero }, new Vector3[3], new Vector3[3]);
            skin.sharedMesh.AddBlendShapeFrame("BLINK", 100, new[] { Vector3.up * .6f, Vector3.zero, Vector3.zero }, new Vector3[3], new Vector3[3]);
            Assert.Throws<InvalidOperationException>(() => BlinkExportSession.Resolve(root));
            var options = new BlinkExportOptions { Mode = BlinkExportMode.Manual };
            options.Both.Add(new BlinkShapeBinding { Renderer = skin, Shape = selected });
            using var resolved = BlinkExportSession.Resolve(root, options);
            Assert.That(resolved.Slots[0].Single().Shape, Is.EqualTo(selected));
            copy = Object.Instantiate(root);
            using var prepared = resolved.ForClone(root, copy);
            prepared.Bake(copy, generated);
            var binding = prepared.Slots[0].Single();
            var vertices = new Vector3[3];
            binding.Renderer.sharedMesh.GetBlendShapeFrameVertices(binding.Renderer.sharedMesh.GetBlendShapeIndex(binding.Shape), 0, vertices, null, null);
            Assert.That(vertices[0].y, Is.EqualTo(expected).Within(.00001));
            Assert.That(skin.sharedMesh.blendShapeCount, Is.EqualTo(2), "The source mesh is unchanged.");
        }

        [Test] public void ExplicitBindingsRequireExactNamesAndRejectRepeatedChannels()
        {
            var skin = Skin("Blink", "BLINK");
            var options = new BlinkExportOptions { Mode = BlinkExportMode.Manual };
            options.Both.Add(new BlinkShapeBinding { Renderer = skin, Shape = "blink" });
            Assert.Throws<InvalidOperationException>(() => BlinkExportSession.Resolve(root, options));
            options.Both[0].Shape = "Blink";
            options.Both.Add(new BlinkShapeBinding { Renderer = skin, Shape = "BLINK" });
            Assert.That(BlinkExportSession.Resolve(root, options).Slots[0].Count, Is.EqualTo(2));
            options.Both.Add(options.Both[0].Copy());
            Assert.Throws<InvalidOperationException>(() => BlinkExportSession.Resolve(root, options));
        }

        [Test] public void FailedAutomaticSetupOffersAnEmptyManualRowWithoutGuessing()
        {
            var skin = Skin("Blink", "BLINK");
            var options = new BlinkExportOptions();
            Assert.Throws<InvalidOperationException>(() => BlinkExportSession.Resolve(root, options));
            options.SelectMode(BlinkExportMode.Manual);
            Assert.That(options.Mode, Is.EqualTo(BlinkExportMode.Manual));
            Assert.That(options.Both.Count, Is.EqualTo(1));
            Assert.That(options.Both[0].Renderer, Is.Null);
            Assert.That(options.Both[0].Shape, Is.Empty);
            Assert.That(options.Left, Is.Empty);
            Assert.That(options.Right, Is.Empty);
            options.Both[0].Renderer = skin;
            options.Both[0].Shape = "BLINK";
            Assert.DoesNotThrow(() => BlinkExportSession.Resolve(root, options));
            options.SelectMode(BlinkExportMode.Auto);
            options.SelectMode(BlinkExportMode.Manual);
            Assert.That(options.Both.Count, Is.EqualTo(1), "Returning to manual must retain the explicit selection.");
            Assert.That(options.Both[0].Shape, Is.EqualTo("BLINK"));
        }

        [Test] public void ManualAdjustmentCopiesTheResolvedBindingsWithoutChangingTheAutomaticResult()
        {
            Skin("Blink", "Blink_L", "Blink_R");
            using var resolved = BlinkExportSession.Resolve(root);
            var options = new BlinkExportOptions();
            options.SelectMode(BlinkExportMode.Manual, resolved);
            Assert.That(options.Both.Count, Is.EqualTo(1));
            Assert.That(options.Left.Count, Is.EqualTo(1));
            Assert.That(options.Right.Count, Is.EqualTo(1));
            Assert.That(options.Both[0], Is.Not.SameAs(resolved.Slots[0][0]));
            options.Both[0].Weight = 65;
            Assert.That(resolved.Slots[0][0].Weight, Is.EqualTo(100));
        }

        [TestCase("Blink_L")][TestCase("Blink_R")]
        public void APartialEyelashPairCannotBeHiddenByAnotherRenderersBlink(string shape)
        {
            Skin("Blink"); Skin(shape);
            Assert.Throws<InvalidOperationException>(() => BlinkExportSession.Resolve(root));
        }

        [Test] public void ACompleteBilateralShapeCanCoverAnIncompleteAlternativePair()
        {
            Skin("Blink", "Blink_L");
            var result = BlinkExportSession.Resolve(root);
            Assert.That(result.Slots[0].Single().Shape, Is.EqualTo("Blink"));
            Assert.That(result.Slots[1], Is.Empty);
        }

        [Test] public void AllBlinkRenderersMustSupportTheIndividualPair()
        {
            Skin("Blink", "Blink_L", "Blink_R");
            Skin("Blink");
            var result = BlinkExportSession.Resolve(root);
            Assert.That(result.Slots[0].Count, Is.EqualTo(2));
            Assert.That(result.Slots[1], Is.Empty);
            Assert.That(result.Slots[2], Is.Empty);
        }

        [Test] public void ManualChoiceOverridesInferenceAndRejectsForeignOrRemovedShapes()
        {
            var skin = Skin("Blink", "custom_eyelids");
            var options = new BlinkExportOptions { Mode = BlinkExportMode.Manual };
            options.Both.Add(new BlinkShapeBinding { Renderer = skin, Shape = "custom_eyelids", Weight = 65 });
            Assert.That(BlinkExportSession.Resolve(root, options).Slots[0].Single().Weight, Is.EqualTo(65));
            Assert.Throws<InvalidOperationException>(() => BlinkExportSession.Resolve(root, options, _ => true));
            options.Both[0].Shape = "deleted";
            Assert.Throws<InvalidOperationException>(() => BlinkExportSession.Resolve(root, options));
            options.Both[0].Shape = "Blink";
            skin.transform.SetParent(null);
            try { Assert.Throws<InvalidOperationException>(() => BlinkExportSession.Resolve(root, options)); }
            finally { Object.DestroyImmediate(skin.gameObject); }
        }

        [Test] public void PairOnlyEyelashesContributeToTheBilateralFallback()
        {
            Skin("Blink");
            Skin("Blink_L", "Blink_R");
            var result = BlinkExportSession.Resolve(root);
            Assert.That(result.Slots[0].Select(b => b.Shape), Is.EquivalentTo(new[] { "Blink", "Blink_L", "Blink_R" }));
            Assert.That(result.Slots[1], Is.Empty);
            Assert.That(result.Slots[2], Is.Empty);
        }

        [Test] public void EmptyAuthoredVrmClipIsPreservedUnlessExplicitlyOverridden()
        {
            var skin = Skin("Blink", "custom");
            var vrm = ScriptableObject.CreateInstance<VRM10Object>(); owned.Add(vrm);
            var clip = ScriptableObject.CreateInstance<VRM10Expression>(); owned.Add(clip);
            vrm.Expression.Blink = clip;
            root.AddComponent<Vrm10Instance>().Vrm = vrm;
            var resolved = BlinkExportSession.Resolve(root);
            Assert.That(resolved.PreserveAuthored, Is.True);
            Assert.That(resolved.Slots.All(s => s.Count == 0), Is.True);
            var manual = new BlinkExportOptions { Mode = BlinkExportMode.Manual };
            manual.Both.Add(new BlinkShapeBinding { Renderer = skin, Shape = "custom" });
            Assert.That(BlinkExportSession.Resolve(root, manual).PreserveAuthored, Is.False);
        }

        [Test] public void SourceRendererIdentitySurvivesRenameAndSiblingReorder()
        {
            var first = Skin("Blink"); var second = Skin("Blink");
            var source = BlinkExportSession.Resolve(root);
            copy = Object.Instantiate(root);
            var mapped = source.ForClone(root, copy);
            var originalTarget = mapped.Slots[0][0].Renderer;
            originalTarget.name = "renamed";
            originalTarget.transform.SetAsLastSibling();
            mapped.Validate(copy);
            Assert.That(mapped.Slots[0][0].Renderer, Is.SameAs(originalTarget));
            Assert.That(mapped.Slots[0][0].Renderer, Is.Not.SameAs(first));
            Object.DestroyImmediate(originalTarget.gameObject);
            Assert.Throws<InvalidOperationException>(() => mapped.Validate(copy));
        }

        [Test] public void PartialClosureIsBakedRelativeToTheAuthoredRestWithoutTouchingSource()
        {
            var skin = Skin("custom"); skin.SetBlendShapeWeight(0, 20);
            var options = new BlinkExportOptions { Mode = BlinkExportMode.Manual };
            options.Both.Add(new BlinkShapeBinding { Renderer = skin, Shape = "custom", Weight = 50 });
            var resolved = BlinkExportSession.Resolve(root, options);
            copy = Object.Instantiate(root);
            var session = resolved.ForClone(root, copy); session.Bake(copy, generated);
            AvatarBaseShape.Preserve(copy, copy, generated, new List<string>());
            var binding = session.Slots[0].Single(); var mesh = binding.Renderer.sharedMesh;
            var delta = new Vector3[3];
            mesh.GetBlendShapeFrameVertices(mesh.GetBlendShapeIndex(binding.Shape), 0, delta, null, null);
            Assert.That(mesh.vertices[0].y, Is.EqualTo(.04f).Within(.00001));
            Assert.That(delta[0].y, Is.EqualTo(.06f).Within(.00001));
            Assert.That(skin.sharedMesh.blendShapeCount, Is.EqualTo(1));
            Assert.That(skin.sharedMesh.vertices[0], Is.EqualTo(Vector3.zero));
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(20));
        }

        [Test] public void NoneProducesAnInertBindingWithoutDestroyingAuthoredMorphs()
        {
            var skin = Skin("Blink");
            var resolved = BlinkExportSession.Resolve(root, new BlinkExportOptions { Mode = BlinkExportMode.None });
            copy = Object.Instantiate(root);
            var session = resolved.ForClone(root, copy); session.Bake(copy, generated);
            Assert.That(session.Slots.All(s => s.Count == 1), Is.True);
            var row = session.Slots[0].Single(); var mesh = row.Renderer.sharedMesh;
            var delta = new Vector3[3]; mesh.GetBlendShapeFrameVertices(mesh.GetBlendShapeIndex(row.Shape), 0, delta, null, null);
            Assert.That(delta.All(v => v == Vector3.zero), Is.True);
            Assert.That(mesh.GetBlendShapeIndex("Blink"), Is.EqualTo(0));
            Assert.That(skin.sharedMesh.blendShapeCount, Is.EqualTo(1));
        }

        [Test] public void DescriptorBlinkUsesOnlyTheClosedSlotAndValidatesMissingIndices()
        {
            var descriptorType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor", false)).FirstOrDefault(t => t != null);
            if (descriptorType == null) Assert.Ignore("VRChat SDK is required for the descriptor integration test.");
            var skin = Skin("custom_closed", "custom_up", "custom_down", "Blink");
            var descriptor = root.AddComponent(descriptorType);
            descriptorType.GetField("enableEyeLook").SetValue(descriptor, true);
            var settingsField = descriptorType.GetField("customEyeLookSettings"); var settings = settingsField.GetValue(descriptor);
            var settingsType = settings.GetType(); var lidType = settingsType.GetField("eyelidType");
            lidType.SetValue(settings, Enum.Parse(lidType.FieldType, "Blendshapes"));
            settingsType.GetField("eyelidsSkinnedMesh").SetValue(settings, skin);
            settingsType.GetField("eyelidsBlendshapes").SetValue(settings, new[] { 0, 1, 2 });
            settingsField.SetValue(descriptor, settings);
            Assert.That(BlinkExportSession.Resolve(root).Slots[0].Single().Shape, Is.EqualTo("custom_closed"));
            settingsType.GetField("eyelidsBlendshapes").SetValue(settings, new[] { -1, 1, 2 }); settingsField.SetValue(descriptor, settings);
            Assert.That(BlinkExportSession.Resolve(root).Slots[0].Single().Shape, Is.EqualTo("Blink"));
        }

        [TestCase(false)][TestCase(true)]
        public void ActualUniVrmExportBindsGeneratedTargetsToTheirFinalNodes(bool modularAvatar)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            if (modularAvatar)
            {
                var proxyType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("nadena.dev.modular_avatar.core.ModularAvatarBoneProxy")).FirstOrDefault(t => t != null);
                var markerType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("nadena.dev.ndmf.runtime.components.NDMFAvatarRoot")).FirstOrDefault(t => t != null);
                if (proxyType == null || markerType == null) Assert.Ignore("Requires Modular Avatar and NDMF.");
                fixture.Source.AddComponent(markerType);
                var hair = fixture.Source.transform.Find("Independent hair");
                fixture.Source.transform.Find("Front").SetParent(hair, true);
                var proxy = hair.gameObject.AddComponent(proxyType);
                proxyType.GetProperty("target").SetValue(proxy, fixture.Source.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Head));
            }
            var material = new Material(Shader.Find("lilToon")); owned.Add(material);
            foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial = material;
            var zeros = new Vector3[fixture.Mesh.vertexCount];
            fixture.Mesh.AddBlendShapeFrame("Blink", 100, zeros, zeros, zeros);
            var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Blink integration", "Tests");
            var glb = GlbDocument.Read(bytes);
            var vrm = (Dictionary<string, object>)((Dictionary<string, object>)glb.Json["extensions"])["VRMC_vrm"];
            var preset = (Dictionary<string, object>)((Dictionary<string, object>)vrm["expressions"])["preset"];
            var binds = (List<object>)((Dictionary<string, object>)preset["blink"])["morphTargetBinds"];
            Assert.That(binds.Count, Is.EqualTo(fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>().Length));
            foreach (Dictionary<string, object> binding in binds)
            {
                var node = (Dictionary<string, object>)((List<object>)glb.Json["nodes"])[Convert.ToInt32(binding["node"])];
                var mesh = (Dictionary<string, object>)((List<object>)glb.Json["meshes"])[Convert.ToInt32(node["mesh"])];
                var names = (List<object>)((Dictionary<string, object>)mesh["extras"])["targetNames"];
                Assert.That((string)names[Convert.ToInt32(binding["index"])], Does.StartWith("__VRVlog_Blink_"));
            }
            Assert.That(fixture.Mesh.blendShapeCount, Is.EqualTo(2));
        }

        [TestCase(false)][TestCase(true)]
        public void ActualExportPreservesAuthoredClipsIncludingExplicitEmpty(bool empty)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var material = new Material(Shader.Find("lilToon")); owned.Add(material);
            foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial = material;
            var vrm = ScriptableObject.CreateInstance<VRM10Object>(); owned.Add(vrm);
            var clip = ScriptableObject.CreateInstance<VRM10Expression>(); owned.Add(clip);
            clip.IsBinary = true;
            clip.MorphTargetBindings = empty ? Array.Empty<MorphTargetBinding>() : new[] { new MorphTargetBinding("Front", 0, .6f) };
            vrm.Expression.Blink = clip; fixture.Source.AddComponent<Vrm10Instance>().Vrm = vrm;
            var glb = GlbDocument.Read(UniVrmOneClickExporter.Export(fixture.Source, "Authored blink", "Tests"));
            var extension = (Dictionary<string, object>)((Dictionary<string, object>)glb.Json["extensions"])["VRMC_vrm"];
            var presets = (Dictionary<string, object>)((Dictionary<string, object>)extension["expressions"])["preset"];
            var blink = (Dictionary<string, object>)presets["blink"];
            Assert.That(blink["isBinary"], Is.EqualTo(true));
            var count = blink.TryGetValue("morphTargetBinds", out var raw) ? ((List<object>)raw).Count : 0;
            Assert.That(count, Is.EqualTo(empty ? 0 : 1));
            Assert.That(presets.ContainsKey("blinkLeft"), Is.False);
            Assert.That(vrm.Expression.Blink, Is.SameAs(clip));
            Assert.That(clip.MorphTargetBindings.Length, Is.EqualTo(empty ? 0 : 1));
            if (!empty) Assert.That(clip.MorphTargetBindings[0].Weight, Is.EqualTo(.6f));
        }

        [Test] public void PreviewOwnsItsCopyAndCleansItUpWithoutChangingTheSource()
        {
            var skin = Skin("Blink");
            var material = new Material(Shader.Find("Standard")); owned.Add(material); skin.sharedMaterial = material;
            skin.SetBlendShapeWeight(0, 15);
            var original = skin.sharedMesh;
            var window = ScriptableObject.CreateInstance<BlinkPreviewWindow>();
            try
            {
                window.Prepare(root, new BlinkExportOptions(), Array.Empty<GameObject>(), new ExportGimmickOptions { AutoExclude = false });
                var field = typeof(BlinkPreviewWindow).GetField("copy", BindingFlags.Instance | BindingFlags.NonPublic);
                var previewCopy = (GameObject)field.GetValue(window);
                Assert.That(previewCopy, Is.Not.Null);
                Assert.That(previewCopy, Is.Not.SameAs(root));
                window.Cleanup();
                Assert.That(previewCopy == null, Is.True);
                Assert.That(skin.sharedMesh, Is.SameAs(original));
                Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(15));
            }
            finally { Object.DestroyImmediate(window); }
        }

        [Test] public void PreviewRendersOpenClosedAndOpenWithinOneUpdateWithoutChangingSource()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                Assert.Ignore("Blink preview pixel regression requires a graphics device.");
            var skin = Skin("Blink");
            var mesh = skin.sharedMesh;
            // Both windings make the fixture visible from the preview camera;
            // one real bone exercises native skinned-renderer caching.
            mesh.triangles = new[] { 0, 1, 2, 2, 1, 0 };
            var bone = new GameObject("preview bone").transform;
            bone.SetParent(root.transform, false);
            skin.bones = new[] { bone }; skin.rootBone = bone;
            mesh.bindposes = new[] { bone.worldToLocalMatrix * skin.transform.localToWorldMatrix };
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, mesh.vertexCount).ToArray();
            mesh.RecalculateBounds();
            var shader = Shader.Find("Unlit/Color");
            Assert.That(shader, Is.Not.Null, "The GPU fixture requires Unity's built-in unlit color shader.");
            var material = new Material(shader) { color = Color.white }; owned.Add(material); skin.sharedMaterial = material;
            skin.SetBlendShapeWeight(0, 15);
            var originalVertices = mesh.vertices;
            var originalMatrixFlag = skin.forceMatrixRecalculationPerRender;
            var window = ScriptableObject.CreateInstance<BlinkPreviewWindow>();
            try
            {
                window.Prepare(root, new BlinkExportOptions(), Array.Empty<GameObject>(), new ExportGimmickOptions { AutoExclude = false });
                // No player-loop tick or yielding between captures: this is the
                // repeated manual rendering case that formerly showed stale eyes.
                var open = RenderPreviewPose(window, 0);
                var closed = RenderPreviewPose(window, 1);
                var reopened = RenderPreviewPose(window, 0);
                Assert.That(open.Distinct().Count(), Is.GreaterThan(1), "The fixture must render a visible triangle.");
                Assert.That(closed.Where((pixel, index) => !pixel.Equals(open[index])).Count(), Is.GreaterThan(0),
                    "Closing the eyes must change actual rendered pixels in the same update.");
                Assert.That(reopened, Is.EqualTo(open), "Reopening must restore the original rendered pose.");
                Assert.That(skin.sharedMesh, Is.SameAs(mesh));
                Assert.That(mesh.vertices, Is.EqualTo(originalVertices));
                Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(15));
                Assert.That(skin.forceMatrixRecalculationPerRender, Is.EqualTo(originalMatrixFlag));
            }
            finally { window.Cleanup(); Object.DestroyImmediate(window); }
        }

        static Color32[] RenderPreviewPose(BlinkPreviewWindow window, float closure)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(BlinkPreviewWindow);
            type.GetField("closure", flags).SetValue(window, closure);
            type.GetMethod("ApplyPose", flags).Invoke(window, null);
            var utility = (UnityEditor.PreviewRenderUtility)type.GetField("preview", flags).GetValue(window);
            var previewOpen = false;
            try
            {
                utility.BeginPreview(new Rect(0, 0, 128, 128), GUIStyle.none); previewOpen = true;
                utility.camera.transform.position = new Vector3(.5f, .5f, 3f);
                utility.camera.transform.LookAt(new Vector3(.5f, .5f, 0), Vector3.up);
                var pixels = 128 * UnityEditor.EditorGUIUtility.pixelsPerPoint;
                utility.camera.pixelRect = new Rect(0, 0, pixels, pixels);
                utility.Render(); var texture = utility.EndPreview(); previewOpen = false;
                var temporary = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32);
                var previous = RenderTexture.active;
                Texture2D snapshot = null;
                try
                {
                    Graphics.Blit(texture, temporary); RenderTexture.active = temporary;
                    snapshot = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
                    snapshot.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0); snapshot.Apply();
                    return snapshot.GetPixels32();
                }
                finally
                {
                    RenderTexture.active = previous;
                    if (snapshot != null) Object.DestroyImmediate(snapshot);
                    RenderTexture.ReleaseTemporary(temporary);
                }
            }
            finally { if (previewOpen) utility.EndPreview(); }
        }
        [TestCase(false)][TestCase(true)]
        public void PreviewUsesTheBilateralPresetAndFallsBackOnlyWhenItIsAbsent(bool bilateral)
        {
            var skin = Skin("both", "left", "right");
            var material = new Material(Shader.Find("Standard")); owned.Add(material); skin.sharedMaterial = material;
            var options = new BlinkExportOptions { Mode = BlinkExportMode.Manual };
            if (bilateral) options.Both.Add(new BlinkShapeBinding { Renderer = skin, Shape = "both", Weight = 50 });
            options.Left.Add(new BlinkShapeBinding { Renderer = skin, Shape = "left", Weight = 80 });
            options.Right.Add(new BlinkShapeBinding { Renderer = skin, Shape = "right", Weight = 100 });
            var window = ScriptableObject.CreateInstance<BlinkPreviewWindow>();
            try
            {
                window.Prepare(root, options, Array.Empty<GameObject>(), new ExportGimmickOptions { AutoExclude = false });
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var previewCopy = (GameObject)typeof(BlinkPreviewWindow).GetField("copy", flags).GetValue(window);
                var target = previewCopy.GetComponentInChildren<SkinnedMeshRenderer>();
                typeof(BlinkPreviewWindow).GetField("closure", flags).SetValue(window, 1f);
                var apply = typeof(BlinkPreviewWindow).GetMethod("ApplyPose", flags); apply.Invoke(window, null);
                Assert.That(target.GetBlendShapeWeight(0), Is.EqualTo(bilateral ? 50 : 0));
                Assert.That(target.GetBlendShapeWeight(1), Is.EqualTo(bilateral ? 0 : 80));
                Assert.That(target.GetBlendShapeWeight(2), Is.EqualTo(bilateral ? 0 : 100));
                typeof(BlinkPreviewWindow).GetField("side", flags).SetValue(window, 1); apply.Invoke(window, null);
                Assert.That(target.GetBlendShapeWeight(0), Is.Zero);
                Assert.That(target.GetBlendShapeWeight(1), Is.EqualTo(80));
                Assert.That(target.GetBlendShapeWeight(2), Is.Zero);
                Assert.That(skin.GetBlendShapeWeight(0), Is.Zero);
            }
            finally { window.Cleanup(); Object.DestroyImmediate(window); }
        }
    }
}
