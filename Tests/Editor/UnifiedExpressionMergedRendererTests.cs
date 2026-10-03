using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UniVRM10;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    // Explicit prepared identity maps isolate the guard's many-to-one contract.
    // The pinned NDMF diagnostic registry itself exposes one source per object.
    public sealed class UnifiedExpressionMergedRendererTests
    {
        GameObject avatar;
        readonly List<Object> owned = new List<Object>();

        [SetUp] public void SetUp() => avatar = new GameObject("Merged tracking fixture");
        [TearDown] public void TearDown()
        {
            Object.DestroyImmediate(avatar);
            foreach (var value in owned) if (value != null) Object.DestroyImmediate(value);
            owned.Clear();
        }

        SkinnedMeshRenderer Skin(string name, params string[] shapes)
        {
            var child = new GameObject(name); child.transform.SetParent(avatar.transform, false);
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            foreach (var shape in shapes) mesh.AddBlendShapeFrame(shape, 100, new[] { Vector3.up, Vector3.zero, Vector3.zero }, null, null);
            owned.Add(mesh);
            var skin = child.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = mesh;
            return skin;
        }

        static Func<SkinnedMeshRenderer, SkinnedMeshRenderer> Map(SkinnedMeshRenderer first, SkinnedMeshRenderer second, SkinnedMeshRenderer merged) =>
            value => value == first || value == second ? merged : value;

        [TestCase(false)]
        [TestCase(true)]
        public void UnreferencedBodyCustomizationBaselineDifferencesDoNotCreateTrackingConflicts(bool hasTracking)
        {
            var shapes = hasTracking ? new[] { "UE/JawOpen", "Body customization" } : new[] { "Body customization" };
            var first = Skin("First", shapes); var second = Skin("Second", shapes);
            var customIndex = shapes.Length - 1; first.SetBlendShapeWeight(customIndex, 25); second.SetBlendShapeWeight(customIndex, 50);
            var guard = new UnifiedExpressionPreparation(avatar);
            Assert.That(guard.SupportsUnified, Is.EqualTo(hasTracking));
            var merged = Skin("Merged", shapes); merged.SetBlendShapeWeight(customIndex, 40);
            first.enabled = false; second.enabled = false;
            Assert.DoesNotThrow(() => guard.RebindPrepared(Map(first, second, merged)));
            Assert.DoesNotThrow(() => guard.VerifyIdentityAndDeformation());
            Assert.DoesNotThrow(() => guard.Verify());
            Assert.That(merged.GetBlendShapeWeight(customIndex), Is.EqualTo(40), "Guarding tracking does not change the prepared customization.");
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void SharedAliasBaselinesAreEnforcedOnlyWhenSourceOrPreparedOutputDeclaresUnified(bool generatedUnified, bool serializedResting)
        {
            var first = Skin("First", "JawOpen"); var second = Skin("Second", "JawOpen");
            first.SetBlendShapeWeight(0, 25); second.SetBlendShapeWeight(0, 50);
            var guard = new UnifiedExpressionPreparation(avatar);
            Assert.That(guard.SupportsUnified, Is.False, "A shared alias alone does not establish UE support.");
            var merged = generatedUnified ? Skin("Merged", "JawOpen", "UE/EyeClosed") : Skin("Merged", "JawOpen");
            merged.SetBlendShapeWeight(0, serializedResting ? 100 : 40);
            if (generatedUnified && serializedResting) merged.SetBlendShapeWeight(1, 100);
            first.enabled = false; second.enabled = false;
            Assert.That(new UnifiedExpressionPreparation(avatar).SupportsUnified, Is.EqualTo(generatedUnified && !serializedResting));
            if (generatedUnified)
                Assert.That(Assert.Throws<InvalidOperationException>(() => guard.RebindPrepared(Map(first, second, merged))).Message,
                    Does.StartWith(UnifiedExpressionPreparation.ConflictingMergedWeights).And.Contain("Merged / JawOpen"),
                    "Prepared structural UE evidence needs a unique original baseline before FX establishes neutral.");
            else
            {
                Assert.DoesNotThrow(() => guard.RebindPrepared(Map(first, second, merged)));
                Assert.DoesNotThrow(() => guard.VerifyIdentityAndDeformation());
                Assert.That(merged.GetBlendShapeWeight(0), Is.EqualTo(40), "Inactive tracking cannot choose or alter a shared-alias baseline.");
            }
        }

        [Test]
        public void SharedAndDistinctMergedTrackingRoutesRetainEveryRequiredChannel()
        {
            var first = Skin("Eyes", "UE/JawOpen", "UE/EyeClosed");
            var second = Skin("Mouth", "UE/JawOpen", "UE/JawForward");
            first.SetBlendShapeWeight(0, 25); second.SetBlendShapeWeight(0, 25);
            var guard = new UnifiedExpressionPreparation(avatar);
            Assert.That(guard.SupportsUnified, Is.True);
            var merged = Skin("Merged", "UE/JawForward", "UE/JawOpen", "UE/EyeClosed");
            merged.SetBlendShapeWeight(1, 25);
            first.enabled = false; second.enabled = false;
            Assert.DoesNotThrow(() => guard.RebindPrepared(Map(first, second, merged)));
            Assert.DoesNotThrow(() => guard.VerifyIdentityAndDeformation());
            Assert.DoesNotThrow(() => guard.Verify(requireUsableEvidence: true));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MergedTrackingStillRejectsLostOrRestingSecondOriginChannel(bool resting)
        {
            var first = Skin("Eyes", "UE/EyeClosed");
            var second = Skin("Mouth", "UE/JawOpen");
            var guard = new UnifiedExpressionPreparation(avatar);
            var merged = resting ? Skin("Merged", "UE/EyeClosed", "UE/JawOpen") : Skin("Merged", "UE/EyeClosed");
            if (resting) merged.SetBlendShapeWeight(1, 100);
            first.enabled = false; second.enabled = false;
            guard.RebindPrepared(Map(first, second, merged));
            if (resting)
            {
                Assert.DoesNotThrow(() => guard.VerifyIdentityAndDeformation(), "The original JawOpen baseline remains zero.");
                Assert.That(Assert.Throws<InvalidOperationException>(() => guard.Verify()).Message,
                    Is.EqualTo(UnifiedExpressionPreparation.LostTracking));
            }
            else Assert.That(Assert.Throws<InvalidOperationException>(() => guard.VerifyIdentityAndDeformation()).Message,
                Is.EqualTo(UnifiedExpressionPreparation.LostTracking));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DifferentMergedStaticBaselinesStopWithoutDiscardingOriginalObligations(bool reverse)
        {
            var first = Skin("First", "UE/JawOpen"); var second = Skin("Second", "UE/JawOpen");
            first.SetBlendShapeWeight(0, 25); second.SetBlendShapeWeight(0, 75);
            if (reverse) second.transform.SetSiblingIndex(0);
            var guard = new UnifiedExpressionPreparation(avatar);
            var merged = Skin("Merged", "UE/JawOpen"); merged.SetBlendShapeWeight(0, 50);
            Assert.That(Assert.Throws<InvalidOperationException>(() => guard.RebindPrepared(Map(first, second, merged))).Message,
                Does.StartWith(UnifiedExpressionPreparation.ConflictingMergedWeights).And.Contain("UE/JawOpen"));
            Assert.DoesNotThrow(() => guard.VerifyIdentityAndDeformation(), "Failed remapping must leave both original baselines intact.");
            second.enabled = false;
            Assert.That(Assert.Throws<InvalidOperationException>(() => guard.VerifyIdentityAndDeformation()).Message,
                Is.EqualTo(UnifiedExpressionPreparation.LostTracking), "The second original route was not silently discarded.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DifferentRawShapesForOneMergedCanonicalRouteStopWithoutChoosingAnAlias(bool reverse)
        {
            var first = Skin("Explicit", "UE/JawOpen"); var second = Skin("Shared", "JawOpen");
            if (reverse) second.transform.SetSiblingIndex(0);
            var guard = new UnifiedExpressionPreparation(avatar);
            var merged = Skin("Merged", "UE/JawOpen", "JawOpen");
            Assert.That(Assert.Throws<InvalidOperationException>(() => guard.RebindPrepared(Map(first, second, merged))).Message,
                Does.StartWith(UnifiedExpressionPreparation.ConflictingMergedRoutes).And.Contain("JawOpen"));
            Assert.DoesNotThrow(() => guard.VerifyIdentityAndDeformation());
        }

        [TestCase(.25f, .25f, true)]
        [TestCase(.25f, .75f, false)]
        [TestCase(0f, .75f, false)]
        public void MergedAuthoredEndpointsKeepTheOriginalFirstBindingAndEveryEffectiveRoute(float firstWeight, float secondWeight, bool compatible)
        {
            var first = Skin("First", "Authored jaw"); var second = Skin("Second", "Authored jaw");
            var clip = ScriptableObject.CreateInstance<VRM10Expression>(); owned.Add(clip); clip.name = "UE/JawOpen";
            clip.MorphTargetBindings = new[] { new MorphTargetBinding("First", 0, firstWeight), new MorphTargetBinding("Second", 0, secondWeight) };
            var vrm = ScriptableObject.CreateInstance<VRM10Object>(); owned.Add(vrm); vrm.Expression.CustomClips.Add(clip);
            avatar.AddComponent<Vrm10Instance>().Vrm = vrm;
            var guard = new UnifiedExpressionPreparation(avatar); Assert.That(guard.SupportsUnified, Is.True);
            var merged = Skin("Merged", "Authored jaw"); first.enabled = false; second.enabled = false;
            guard.RebindPrepared(Map(first, second, merged));
            // Prepared authoring data preserves order and absolute endpoint weights.
            clip.MorphTargetBindings = new[] { new MorphTargetBinding("Merged", 0, firstWeight), new MorphTargetBinding("Merged", 0, secondWeight) };
            if (compatible) Assert.DoesNotThrow(() => guard.VerifyIdentityAndDeformation());
            else Assert.That(Assert.Throws<InvalidOperationException>(() => guard.VerifyIdentityAndDeformation()).Message,
                Is.EqualTo(UnifiedExpressionPreparation.LostTracking));
        }
    }
}
