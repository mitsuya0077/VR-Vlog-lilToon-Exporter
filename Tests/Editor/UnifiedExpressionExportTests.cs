using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UniGLTF;
using UniVRM10;
using UnityEngine;
using VRVlog.FaceTracking;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class UnifiedExpressionExportTests
    {
        [Test] public void FinalGlbTrackingContract() => UnifiedExpressionFixture.Run((ok, message) => Assert.That(ok, Is.True, message));

        [TestCase(false)]
        [TestCase(true)]
        public async Task PartialChannelsAndMultipleRestingMeshesSurviveExportAndReimport(bool fullLilToon)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            Vrm10Instance imported = null;
            try
            {
                var mesh = fixture.Mesh;
                var jawDelta = Enumerable.Repeat(new Vector3(.04f, 0, 0), mesh.vertexCount).ToArray();
                var closedDelta = Enumerable.Repeat(new Vector3(0, .06f, 0), mesh.vertexCount).ToArray();
                mesh.AddBlendShapeFrame("JawOpen", 100, jawDelta, null, null);
                mesh.AddBlendShapeFrame("MouthClosed", 100, closedDelta, null, null);
                mesh.AddBlendShapeFrame("EyeClosedLeft", 100, new Vector3[mesh.vertexCount], null, null);
                mesh.AddBlendShapeFrame("EyeClosedRight", 100, new Vector3[mesh.vertexCount], null, null);
                var sourceSkins = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
                foreach (var skin in sourceSkins)
                {
                    skin.sharedMaterial.shader = Shader.Find("lilToon");
                    skin.SetBlendShapeWeight(1, 25);
                    skin.SetBlendShapeWeight(2, 40);
                }
                var originalVertices = mesh.vertices;
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "UE regression", "Tests",
                    exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null);
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                var jaw = imported.Vrm.Expression.CustomClips.Single(clip => clip.name == "UE/JawOpen");
                var closed = imported.Vrm.Expression.CustomClips.Single(clip => clip.name == "UE/MouthClosed");
                Assert.That(jaw.MorphTargetBindings.Length, Is.EqualTo(2));
                Assert.That(closed.MorphTargetBindings.Length, Is.EqualTo(2));
                Assert.That(jaw.IsBinary, Is.False);
                Assert.That(jaw.OverrideMouth.ToString(), Is.EqualTo("none"));
                foreach (var weight in new[] { 0f, .5f, 1f })
                {
                    imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom("UE/JawOpen"), weight);
                    imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom("UE/MouthClosed"), weight);
                    imported.Runtime.Process();
                    foreach (var binding in jaw.MorphTargetBindings)
                    {
                        var skin = imported.transform.Find(binding.RelativePath).GetComponent<SkinnedMeshRenderer>();
                        var closedBinding = closed.MorphTargetBindings.Single(value => value.RelativePath == binding.RelativePath);
                        Assert.That(skin.GetBlendShapeWeight(binding.Index), Is.EqualTo(weight * 100).Within(.001));
                        Assert.That(skin.GetBlendShapeWeight(closedBinding.Index), Is.EqualTo(weight * 100).Within(.001));
                        var jawResidual = new Vector3[skin.sharedMesh.vertexCount];
                        var closedResidual = new Vector3[skin.sharedMesh.vertexCount];
                        skin.sharedMesh.GetBlendShapeFrameVertices(binding.Index, 0, jawResidual, null, null);
                        skin.sharedMesh.GetBlendShapeFrameVertices(closedBinding.Index, 0, closedResidual, null, null);
                        // Coordinate conversion does not change these independent x/y magnitudes.
                        var value = skin.sharedMesh.vertices[0] + jawResidual[0] * weight + closedResidual[0] * weight;
                        var neutral = skin.sharedMesh.vertices[0];
                        var expectedNeutral = originalVertices[0] + new Vector3(.02f, .01f, 0) * .35f + jawDelta[0] * .25f + closedDelta[0] * .40f;
                        Assert.That(Vector3.Distance(neutral, expectedNeutral), Is.LessThan(.0001f), "All authored resting channels are baked once into the neutral face.");
                        Assert.That(Mathf.Abs(value.x - neutral.x), Is.EqualTo(.04f * .75f * weight).Within(.0001));
                        Assert.That(Mathf.Abs(value.y - neutral.y), Is.EqualTo(.06f * .60f * weight).Within(.0001));
                    }
                }
                Assert.That(imported.Vrm.Expression.Blink, Is.Not.Null);
                Assert.That(imported.Vrm.Expression.CustomClips.Any(clip => clip.name.StartsWith(UnifiedExpressionRegistry.RestPrefix, StringComparison.Ordinal)), Is.False);
                Assert.That(mesh.vertices, Is.EqualTo(originalVertices));
                Assert.That(sourceSkins.All(skin => skin.sharedMesh == mesh && skin.GetBlendShapeWeight(1) == 25 && skin.GetBlendShapeWeight(2) == 40), Is.True);
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); }
        }

        [Test]
        public async Task ExplicitArkitMarkerKeepsItsExisting52ChannelPriority()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var profile = ScriptableObject.CreateInstance<VrmTrackingProfile>();
            Vrm10Instance imported = null;
            try
            {
                fixture.Mesh.AddBlendShapeFrame("MouthClosed", 100, new Vector3[fixture.Mesh.vertexCount], null, null);
                foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = Shader.Find("lilToon");
                profile.expressions = VrmTrackingExpressions.Names.Select(name => new TrackingExpression {
                    name = name, morphs = new[] { new TrackingMorph { shape = "Hair detail", weight = .6f } }
                }).ToArray();
                fixture.Source.AddComponent<VrmTrackingMarker>().profile = profile;
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Explicit ARKit", "Tests", blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported.Vrm.Expression.CustomClips.Count, Is.EqualTo(52));
                Assert.That(imported.Vrm.Expression.CustomClips.Any(clip => clip.name.StartsWith("UE/", StringComparison.Ordinal)), Is.False);
                Assert.That(imported.Vrm.Expression.CustomClips.Single(clip => clip.name == "JawOpen").MorphTargetBindings.All(binding => Math.Abs(binding.Weight - .6f) < .0001f), Is.True);
                Assert.That(fixture.Source.GetComponent<VrmTrackingMarker>().profile, Is.SameAs(profile));
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                Object.DestroyImmediate(profile);
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task SplitFaceExportsSharedMouthAndOnlyAvailableBilateralBlink(bool bilateral, bool fullLilToon)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var mouthMesh = Object.Instantiate(fixture.Mesh);
            Vrm10Instance imported = null;
            try
            {
                fixture.Mesh.AddBlendShapeFrame("EyeClosedLeft", 100, new Vector3[fixture.Mesh.vertexCount], null, null);
                mouthMesh.AddBlendShapeFrame("JawOpen", 100, new Vector3[mouthMesh.vertexCount], null, null);
                if (bilateral) mouthMesh.AddBlendShapeFrame("EyeClosedRight", 100, new Vector3[mouthMesh.vertexCount], null, null);
                var skins = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
                skins[1].sharedMesh = mouthMesh;
                foreach (var skin in skins) skin.sharedMaterial.shader = Shader.Find("lilToon");
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Split UE face", "Tests",
                    exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null);
                var json = GlbDocument.Read(bytes).Json;
                var extension = (Dictionary<string, object>)((Dictionary<string, object>)json["extensions"])["VRMC_vrm"];
                var presets = (Dictionary<string, object>)((Dictionary<string, object>)extension["expressions"])["preset"];
                foreach (var name in BlinkShapeNames.Presets)
                    Assert.That(presets.ContainsKey(name), Is.EqualTo(bilateral), "A partial UE face does not serialize an invented standard blink route: " + name);
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                foreach (var pair in new[] { ("UE/JawOpen", .6f), ("UE/EyeClosedLeft", .4f) })
                {
                    var clip = imported.Vrm.Expression.CustomClips.Single(value => value.name == pair.Item1);
                    Assert.That(clip.MorphTargetBindings.Length, Is.EqualTo(1));
                    imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(pair.Item1), pair.Item2);
                    imported.Runtime.Process();
                    var binding = clip.MorphTargetBindings[0];
                    var skin = imported.transform.Find(binding.RelativePath).GetComponent<SkinnedMeshRenderer>();
                    Assert.That(skin.GetBlendShapeWeight(binding.Index), Is.EqualTo(pair.Item2 * 100).Within(.001));
                }
                Assert.That(imported.Vrm.Expression.CustomClips.Any(clip => clip.name == "UE/EyeClosedRight"), Is.EqualTo(bilateral));
                // UniVRM creates empty preset assets even when the serialized
                // key is absent; binding counts express actual capability.
                Assert.That(imported.Vrm.Expression.Blink.MorphTargetBindings.Length, Is.EqualTo(bilateral ? 2 : 0), "One-sided closure cannot be synthesized into bilateral blink.");
                Assert.That(imported.Vrm.Expression.BlinkLeft.MorphTargetBindings.Length, Is.EqualTo(bilateral ? 1 : 0));
                Assert.That(imported.Vrm.Expression.BlinkRight.MorphTargetBindings.Length, Is.EqualTo(bilateral ? 1 : 0));
                Assert.That(skins[0].sharedMesh, Is.SameAs(fixture.Mesh));
                Assert.That(skins[1].sharedMesh, Is.SameAs(mouthMesh));
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                Object.DestroyImmediate(mouthMesh);
            }
        }

        [Test]
        public void PreparationRejectsLostMorphOnTheTrackedRenderer()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            fixture.Mesh.AddBlendShapeFrame("MouthClosed", 100, new Vector3[fixture.Mesh.vertexCount], null, null);
            var guard = new UnifiedExpressionPreparation(fixture.Copy);
            var privateMesh = Object.Instantiate(fixture.Mesh);
            try
            {
                privateMesh.ClearBlendShapes();
                fixture.Copy.GetComponentsInChildren<SkinnedMeshRenderer>()[0].sharedMesh = privateMesh;
                Assert.Throws<InvalidOperationException>(() => guard.Verify());
            }
            finally { Object.DestroyImmediate(privateMesh); }
        }

        [TestCase(0f, false, "UE/LipFunnelUpperLeft")]
        [TestCase(.4f, false, "UE/LipFunnelUpperLeft")]
        [TestCase(0f, true, "UE/LipFunnelUpperLeft")]
        [TestCase(.4f, true, "UE/LipFunnelUpperLeft")]
        [TestCase(.4f, false, "LipFunnelUpperLeft")]
        [TestCase(.4f, true, "lip_funnel_upper_left")]
        public async Task AuthoredSplitCoverageWinsBeforeRawAggregateOnItsMeshes(float authoredWeight, bool otherMeshUncovered, string authoredName)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var vrm = ScriptableObject.CreateInstance<VRM10Object>();
            var clip = ScriptableObject.CreateInstance<VRM10Expression>();
            Vrm10Instance imported = null;
            try
            {
                fixture.Mesh.AddBlendShapeFrame("LipFunnel", 100, Enumerable.Repeat(Vector3.right * .03f, fixture.Mesh.vertexCount).ToArray(), null, null);
                fixture.Mesh.AddBlendShapeFrame("LipFunnelUpperLeft", 100, Enumerable.Repeat(Vector3.up * .04f, fixture.Mesh.vertexCount).ToArray(), null, null);
                foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = Shader.Find("lilToon");
                clip.name = authoredName;
                clip.MorphTargetBindings = (otherMeshUncovered ? new[] { "Front" } : new[] { "Front", "Back" })
                    .Select(path => new MorphTargetBinding(path, 2, authoredWeight)).ToArray();
                vrm.Expression.CustomClips.Add(clip);
                fixture.Source.AddComponent<Vrm10Instance>().Vrm = vrm;
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Authored UE coverage", "Tests");
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                var retained = imported.Vrm.Expression.CustomClips.Single(value => value.name == clip.name);
                Assert.That(retained.MorphTargetBindings.All(binding => Math.Abs(binding.Weight - authoredWeight) < .0001), Is.True);
                var aggregate = imported.Vrm.Expression.CustomClips.SingleOrDefault(value => value.name == "UE/LipFunnel");
                Assert.That(aggregate != null, Is.EqualTo(otherMeshUncovered), "Raw aggregate cannot displace authored split coverage.");
                if (aggregate != null)
                {
                    Assert.That(aggregate.MorphTargetBindings.Length, Is.EqualTo(1));
                    Assert.That(aggregate.MorphTargetBindings[0].RelativePath, Is.EqualTo("Back"));
                    imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(aggregate.name), .5f);
                }
                imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(retained.name), 1f);
                imported.Runtime.Process();
                var front = imported.transform.Find("Front").GetComponent<SkinnedMeshRenderer>();
                Assert.That(front.GetBlendShapeWeight(front.sharedMesh.GetBlendShapeIndex("LipFunnel")), Is.Zero);
                Assert.That(front.GetBlendShapeWeight(front.sharedMesh.GetBlendShapeIndex("LipFunnelUpperLeft")), Is.EqualTo(authoredWeight * 100).Within(.001));
                Assert.That(vrm.Expression.CustomClips.Single(), Is.SameAs(clip));
                Assert.That(clip.MorphTargetBindings.All(binding => binding.Weight == authoredWeight), Is.True);
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                Object.DestroyImmediate(clip); Object.DestroyImmediate(vrm);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ExplicitRawAliasWinsOverCanonicalInExportedBindings(bool fullLilToon)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            Vrm10Instance imported = null;
            try
            {
                fixture.Mesh.AddBlendShapeFrame("EyeClosedLeft", 100, Enumerable.Repeat(Vector3.right * .03f, fixture.Mesh.vertexCount).ToArray(), null, null);
                fixture.Mesh.AddBlendShapeFrame("UE/EyeClosedLeft", 100, Enumerable.Repeat(Vector3.up * .04f, fixture.Mesh.vertexCount).ToArray(), null, null);
                fixture.Mesh.AddBlendShapeFrame("EyeClosedRight", 100, new Vector3[fixture.Mesh.vertexCount], null, null);
                foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = Shader.Find("lilToon");
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "UE alias priority", "Tests",
                    exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null);
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                var clip = imported.Vrm.Expression.CustomClips.Single(value => value.name == "UE/EyeClosedLeft");
                Assert.That(clip.MorphTargetBindings.Length, Is.EqualTo(2));
                imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(clip.name), .5f);
                imported.Runtime.Process();
                foreach (var binding in clip.MorphTargetBindings)
                {
                    var skin = imported.transform.Find(binding.RelativePath).GetComponent<SkinnedMeshRenderer>();
                    Assert.That(skin.sharedMesh.GetBlendShapeName(binding.Index), Is.EqualTo("UE/EyeClosedLeft"));
                    Assert.That(skin.GetBlendShapeWeight(binding.Index), Is.EqualTo(50).Within(.001));
                    Assert.That(skin.GetBlendShapeWeight(skin.sharedMesh.GetBlendShapeIndex("EyeClosedLeft")), Is.Zero);
                }
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); }
        }

        [Test]
        public void PreparationProtectsSharedChannelsOnOtherFaceMeshes()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var mouth = Object.Instantiate(fixture.Mesh);
            var empty = Object.Instantiate(fixture.Mesh);
            try
            {
                fixture.Mesh.AddBlendShapeFrame("EyeClosedLeft", 100, new Vector3[fixture.Mesh.vertexCount], null, null);
                mouth.AddBlendShapeFrame("JawOpen", 100, new Vector3[mouth.vertexCount], null, null);
                var skins = fixture.Copy.GetComponentsInChildren<SkinnedMeshRenderer>();
                skins[1].sharedMesh = mouth;
                var guard = new UnifiedExpressionPreparation(fixture.Copy);
                skins[1].sharedMesh = empty;
                Assert.Throws<InvalidOperationException>(() => guard.Verify(), "UE eye evidence also preserves the separately authored mouth mesh.");
            }
            finally { Object.DestroyImmediate(mouth); Object.DestroyImmediate(empty); }
        }
    }
}
