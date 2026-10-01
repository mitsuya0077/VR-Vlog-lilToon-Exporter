using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UniGLTF;
using UniGLTF.Extensions.VRMC_vrm;
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
        public async Task SharedGlbMeshKeepsAuthoredCoverageScopedToItsRendererNode(bool fullLilToon)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var vrm = ScriptableObject.CreateInstance<VRM10Object>();
            var clip = ScriptableObject.CreateInstance<VRM10Expression>();
            Vrm10Instance imported = null;
            try
            {
                fixture.Mesh.AddBlendShapeFrame("LipFunnel", 100, Enumerable.Repeat(Vector3.right * .03f, fixture.Mesh.vertexCount).ToArray(), null, null);
                fixture.Mesh.AddBlendShapeFrame("LipFunnelUpperLeft", 100, Enumerable.Repeat(Vector3.up * .04f, fixture.Mesh.vertexCount).ToArray(), null, null);
                fixture.Mesh.AddBlendShapeFrame("LipFunnelUpperRight", 100, Enumerable.Repeat(Vector3.forward * .02f, fixture.Mesh.vertexCount).ToArray(), null, null);
                foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = Shader.Find("lilToon");
                clip.name = "UE/LipFunnelUpperLeft";
                clip.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 2, .4f) };
                vrm.Expression.CustomClips.Add(clip);
                fixture.Source.AddComponent<Vrm10Instance>().Vrm = vrm;
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Shared mesh UE coverage", "Tests",
                    exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null);
                var glb = GlbDocument.Read(bytes);
                var nodes = ((List<object>)glb.Json["nodes"]).Cast<Dictionary<string, object>>().ToArray();
                var frontIndex = Array.FindIndex(nodes, node => node.TryGetValue("name", out var name) && (string)name == "Front" && node.ContainsKey("mesh"));
                var backIndex = Array.FindIndex(nodes, node => node.TryGetValue("name", out var name) && (string)name == "Back" && node.ContainsKey("mesh"));
                Assert.That(frontIndex, Is.GreaterThanOrEqualTo(0)); Assert.That(backIndex, Is.GreaterThanOrEqualTo(0));
                nodes[backIndex]["mesh"] = nodes[frontIndex]["mesh"];
                var metadata = (Dictionary<string, object>)((Dictionary<string, object>)glb.Json["extensions"])["VRMC_vrm"];
                var custom = (Dictionary<string, object>)((Dictionary<string, object>)metadata["expressions"])["custom"];
                foreach (var key in custom.Keys.Where(key => key.StartsWith("UE/", StringComparison.Ordinal) && key != clip.name).ToArray()) custom.Remove(key);
                bytes = VrmUnifiedExpressions.Add(glb.Write());
                var output = GlbDocument.Read(bytes).Json;
                var outputNodes = (List<object>)output["nodes"];
                Assert.That(((Dictionary<string, object>)outputNodes[frontIndex])["mesh"], Is.EqualTo(((Dictionary<string, object>)outputNodes[backIndex])["mesh"]));
                var outputCustom = (Dictionary<string, object>)((Dictionary<string, object>)((Dictionary<string, object>)((Dictionary<string, object>)output["extensions"])["VRMC_vrm"])["expressions"])["custom"];
                var rawBinds = (List<object>)((Dictionary<string, object>)outputCustom["UE/LipFunnel"])["morphTargetBinds"];
                Assert.That(rawBinds.Count, Is.EqualTo(1));
                Assert.That(((Dictionary<string, object>)rawBinds[0])["node"], Is.EqualTo((long)backIndex));
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                var authored = imported.Vrm.Expression.CustomClips.Single(value => value.name == clip.name);
                var aggregate = imported.Vrm.Expression.CustomClips.Single(value => value.name == "UE/LipFunnel");
                var split = imported.Vrm.Expression.CustomClips.Single(value => value.name == "UE/LipFunnelUpperRight");
                Assert.That(authored.MorphTargetBindings.Single().RelativePath, Is.EqualTo("Front"));
                Assert.That(aggregate.MorphTargetBindings.Single().RelativePath, Is.EqualTo("Back"));
                Assert.That(split.MorphTargetBindings.Single().RelativePath, Is.EqualTo("Front"));
                imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(authored.name), 1f);
                imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(aggregate.name), .5f);
                imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(split.name), .3f);
                imported.Runtime.Process();
                var front = imported.transform.Find("Front").GetComponent<SkinnedMeshRenderer>();
                var back = imported.transform.Find("Back").GetComponent<SkinnedMeshRenderer>();
                Assert.That(front.GetBlendShapeWeight(front.sharedMesh.GetBlendShapeIndex("LipFunnel")), Is.Zero);
                Assert.That(front.GetBlendShapeWeight(front.sharedMesh.GetBlendShapeIndex("LipFunnelUpperLeft")), Is.EqualTo(40).Within(.001));
                Assert.That(front.GetBlendShapeWeight(front.sharedMesh.GetBlendShapeIndex("LipFunnelUpperRight")), Is.EqualTo(30).Within(.001));
                Assert.That(back.GetBlendShapeWeight(back.sharedMesh.GetBlendShapeIndex("LipFunnel")), Is.EqualTo(50).Within(.001));
                Assert.That(back.GetBlendShapeWeight(back.sharedMesh.GetBlendShapeIndex("LipFunnelUpperLeft")), Is.Zero);
                Assert.That(back.GetBlendShapeWeight(back.sharedMesh.GetBlendShapeIndex("LipFunnelUpperRight")), Is.Zero);
                Assert.That(clip.MorphTargetBindings.Single().RelativePath, Is.EqualTo("Front"));
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                Object.DestroyImmediate(clip); Object.DestroyImmediate(vrm);
            }
        }

        [Test]
        public void PreparationSharedMeshCoverageIsScopedPerRenderer()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var vrm = ScriptableObject.CreateInstance<VRM10Object>();
            var clip = ScriptableObject.CreateInstance<VRM10Expression>();
            var trimmed = Object.Instantiate(fixture.Mesh);
            try
            {
                fixture.Mesh.AddBlendShapeFrame("LipFunnel", 100, new Vector3[fixture.Mesh.vertexCount], null, null);
                fixture.Mesh.AddBlendShapeFrame("LipFunnelUpperLeft", 100, new Vector3[fixture.Mesh.vertexCount], null, null);
                clip.name = "UE/LipFunnelUpperLeft";
                clip.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 2, .4f) };
                vrm.Expression.CustomClips.Add(clip);
                fixture.Copy.AddComponent<Vrm10Instance>().Vrm = vrm;
                var guard = new UnifiedExpressionPreparation(fixture.Copy);
                trimmed.AddBlendShapeFrame("LipFunnelUpperLeft", 100, new Vector3[trimmed.vertexCount], null, null);
                fixture.Copy.transform.Find("Front").GetComponent<SkinnedMeshRenderer>().sharedMesh = trimmed;
                Assert.DoesNotThrow(() => guard.Verify(), "Only the authored renderer may omit the unused aggregate.");
                fixture.Copy.transform.Find("Back").GetComponent<SkinnedMeshRenderer>().sharedMesh = trimmed;
                Assert.Throws<InvalidOperationException>(() => guard.Verify(), "The uncovered renderer still requires its raw aggregate despite initially sharing the same mesh.");
            }
            finally { Object.DestroyImmediate(trimmed); Object.DestroyImmediate(clip); Object.DestroyImmediate(vrm); }
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

        [TestCase(.4f, 2)]
        [TestCase(0f, 2)]
        [TestCase(float.NaN, 2)]
        [TestCase(.4f, 999)]
        public void PreparationAllowsUnusedAggregateRemovalAndProtectsAuthoredEndpoint(float weight, int index)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var vrm = ScriptableObject.CreateInstance<VRM10Object>();
            var clip = ScriptableObject.CreateInstance<VRM10Expression>();
            var trimmed = Object.Instantiate(fixture.Mesh);
            try
            {
                fixture.Mesh.AddBlendShapeFrame("LipFunnel", 100, new Vector3[fixture.Mesh.vertexCount], null, null);
                fixture.Mesh.AddBlendShapeFrame("LipFunnelUpperLeft", 100, new Vector3[fixture.Mesh.vertexCount], null, null);
                clip.name = "UE/LipFunnelUpperLeft";
                clip.MorphTargetBindings = new[] { new MorphTargetBinding("Front", index, weight) };
                vrm.Expression.CustomClips.Add(clip);
                fixture.Copy.AddComponent<Vrm10Instance>().Vrm = vrm;
                var guard = new UnifiedExpressionPreparation(fixture.Copy);
                trimmed.AddBlendShapeFrame("LipFunnelUpperLeft", 100, new Vector3[trimmed.vertexCount], null, null);
                var front = fixture.Copy.transform.Find("Front").GetComponent<SkinnedMeshRenderer>();
                front.sharedMesh = trimmed;
                if (index == 2) clip.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 1, weight) };
                Assert.DoesNotThrow(() => guard.Verify(), "The unused aggregate may be removed while the authored split route remains.");
                trimmed.ClearBlendShapes();
                if (index == 2)
                    Assert.Throws<InvalidOperationException>(() => guard.Verify(), "A genuinely authored endpoint remains protected even for zero/invalid weights.");
                else Assert.DoesNotThrow(() => guard.Verify(), "An invalid authored index reserves coverage without inventing a raw route to preserve.");
                Assert.That(fixture.Mesh.GetBlendShapeIndex("LipFunnel"), Is.EqualTo(1));
                Assert.That(fixture.Mesh.GetBlendShapeIndex("LipFunnelUpperLeft"), Is.EqualTo(2));
            }
            finally { Object.DestroyImmediate(trimmed); Object.DestroyImmediate(clip); Object.DestroyImmediate(vrm); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task SplitPartialBlinkUsesTheSameUeClosureInStandardAndDetailedPlayback(bool fullLilToon)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var rightMesh = Object.Instantiate(fixture.Mesh);
            Vrm10Instance imported = null;
            try
            {
                fixture.Mesh.AddBlendShapeFrame("Blink_L", 100, Enumerable.Repeat(Vector3.right * .01f, fixture.Mesh.vertexCount).ToArray(), null, null);
                fixture.Mesh.AddBlendShapeFrame("UE/EyeClosedLeft", 100, Enumerable.Repeat(Vector3.up * .02f, fixture.Mesh.vertexCount).ToArray(), null, null);
                rightMesh.AddBlendShapeFrame("UE/EyeClosedRight", 100, Enumerable.Repeat(Vector3.up * .02f, rightMesh.vertexCount).ToArray(), null, null);
                var skins = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>(); skins[1].sharedMesh = rightMesh;
                foreach (var skin in skins) skin.sharedMaterial.shader = Shader.Find("lilToon");
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Partial UE blink agreement", "Tests",
                    exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null);
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                var binding = imported.Vrm.Expression.BlinkLeft.MorphTargetBindings.Single();
                var front = imported.transform.Find(binding.RelativePath).GetComponent<SkinnedMeshRenderer>();
                var delta = new Vector3[front.sharedMesh.vertexCount];
                front.sharedMesh.GetBlendShapeFrameVertices(binding.Index, 0, delta, null, null);
                Assert.That(delta[0].x, Is.Zero.Within(.0001));
                Assert.That(delta[0].y, Is.EqualTo(.02f).Within(.0001), "Standard blink closes the same authored UE shape as detailed tracking.");
                imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom("UE/EyeClosedLeft"), .5f);
                imported.Runtime.Process();
                Assert.That(front.GetBlendShapeWeight(front.sharedMesh.GetBlendShapeIndex("UE/EyeClosedLeft")), Is.EqualTo(50).Within(.001));
                Assert.That(front.GetBlendShapeWeight(front.sharedMesh.GetBlendShapeIndex("Blink_L")), Is.Zero);
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                Object.DestroyImmediate(rightMesh);
            }
        }

        [TestCase("color", false)]
        [TestCase("color", true)]
        [TestCase("uv", false)]
        [TestCase("uv", true)]
        public async Task MaterialOnlyAuthoredCoverageSurvivesRoundtripWithoutRawAggregate(string kind, bool fullLilToon)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var vrm = ScriptableObject.CreateInstance<VRM10Object>();
            var clip = ScriptableObject.CreateInstance<VRM10Expression>();
            Vrm10Instance imported = null;
            try
            {
                fixture.Mesh.AddBlendShapeFrame("LipFunnel", 100, Enumerable.Repeat(Vector3.right * .03f, fixture.Mesh.vertexCount).ToArray(), null, null);
                fixture.Mesh.AddBlendShapeFrame("JawOpen", 100, Enumerable.Repeat(Vector3.up * .02f, fixture.Mesh.vertexCount).ToArray(), null, null);
                var skins = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
                foreach (var skin in skins) skin.sharedMaterial.shader = Shader.Find("lilToon");
                clip.name = "UE/LipFunnelUpperLeft";
                if (kind == "color") clip.MaterialColorBindings = new[] { new MaterialColorBinding {
                    MaterialName = skins[0].sharedMaterial.name, BindType = MaterialColorType.color, TargetValue = new Color(.1f, .2f, .3f, 1f) } };
                else clip.MaterialUVBindings = new[] { new MaterialUVBinding {
                    MaterialName = skins[0].sharedMaterial.name, Scaling = new Vector2(.7f, .8f), Offset = new Vector2(.2f, .3f) } };
                vrm.Expression.CustomClips.Add(clip);
                fixture.Source.AddComponent<Vrm10Instance>().Vrm = vrm;
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Material-only UE coverage", "Tests",
                    exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null);
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                var retained = imported.Vrm.Expression.CustomClips.Single(value => value.name == clip.name);
                Assert.That(retained.MorphTargetBindings.Length, Is.Zero);
                Assert.That(retained.MaterialColorBindings.Length, Is.EqualTo(kind == "color" ? 1 : 0));
                Assert.That(retained.MaterialUVBindings.Length, Is.EqualTo(kind == "uv" ? 1 : 0));
                Assert.That(imported.Vrm.Expression.CustomClips.Any(value => value.name == "UE/LipFunnel"), Is.False,
                    "Material-only authored anatomy has no mesh scope and blocks raw aggregate on both meshes.");
                var jaw = imported.Vrm.Expression.CustomClips.Single(value => value.name == "UE/JawOpen");
                Assert.That(jaw.MorphTargetBindings.Length, Is.EqualTo(2));
                imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(clip.name), .5f);
                imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(jaw.name), .4f);
                imported.Runtime.Process();
                foreach (var skin in imported.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    Assert.That(skin.GetBlendShapeWeight(skin.sharedMesh.GetBlendShapeIndex("LipFunnel")), Is.Zero);
                    Assert.That(skin.GetBlendShapeWeight(skin.sharedMesh.GetBlendShapeIndex("JawOpen")), Is.EqualTo(40).Within(.001));
                }
                Assert.That((clip.MaterialColorBindings?.Length ?? 0) + (clip.MaterialUVBindings?.Length ?? 0), Is.EqualTo(1));
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                Object.DestroyImmediate(clip); Object.DestroyImmediate(vrm);
            }
        }

        [TestCase("color", true)]
        [TestCase("color", false)]
        [TestCase("uv", true)]
        [TestCase("uv", false)]
        public void MaterialOnlyPreparationDoesNotRequireSuppressedMorphs(string kind, bool usable)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var vrm = ScriptableObject.CreateInstance<VRM10Object>();
            var clip = ScriptableObject.CreateInstance<VRM10Expression>();
            var trimmed = Object.Instantiate(fixture.Mesh);
            try
            {
                fixture.Mesh.AddBlendShapeFrame("LipFunnel", 100, new Vector3[fixture.Mesh.vertexCount], null, null);
                fixture.Mesh.AddBlendShapeFrame("JawOpen", 100, new Vector3[fixture.Mesh.vertexCount], null, null);
                clip.name = "UE/LipFunnelUpperLeft";
                var materialName = usable ? fixture.Skins[0].sharedMaterial.name : "missing material";
                if (kind == "color") clip.MaterialColorBindings = new[] { new MaterialColorBinding {
                    MaterialName = materialName, BindType = MaterialColorType.color, TargetValue = Color.red } };
                else clip.MaterialUVBindings = new[] { new MaterialUVBinding {
                    MaterialName = materialName, Scaling = Vector2.one, Offset = Vector2.up } };
                vrm.Expression.CustomClips.Add(clip);
                fixture.Copy.AddComponent<Vrm10Instance>().Vrm = vrm;
                var guard = new UnifiedExpressionPreparation(fixture.Copy);
                trimmed.AddBlendShapeFrame("JawOpen", 100, new Vector3[trimmed.vertexCount], null, null);
                fixture.Skins[0].sharedMesh = trimmed;
                Assert.DoesNotThrow(() => guard.Verify(), "The material-only route suppresses the unused aggregate globally.");
                trimmed.ClearBlendShapes();
                if (usable) Assert.Throws<InvalidOperationException>(() => guard.Verify(), "Usable authored UE still enables and protects the shared raw jaw.");
                else Assert.DoesNotThrow(() => guard.Verify(), "An unusable retained material route cannot alone establish tracking for shared raw jaw.");
            }
            finally { Object.DestroyImmediate(trimmed); Object.DestroyImmediate(clip); Object.DestroyImmediate(vrm); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task GeneratedJawMenuRemainsBinaryAlongsideContinuousUnifiedJaw(bool fullLilToon)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            Mesh menuMesh = null;
            Vrm10Instance imported = null;
            try
            {
                fixture.Mesh.AddBlendShapeFrame("JawOpen", 100, Enumerable.Repeat(Vector3.right * .03f, fixture.Mesh.vertexCount).ToArray(), null, null);
                fixture.Mesh.AddBlendShapeFrame("EyeClosedLeft", 100, Enumerable.Repeat(Vector3.up * .02f, fixture.Mesh.vertexCount).ToArray(), null, null);
                fixture.Mesh.AddBlendShapeFrame("EyeClosedRight", 100, Enumerable.Repeat(Vector3.up * .02f, fixture.Mesh.vertexCount).ToArray(), null, null);
                menuMesh = Object.Instantiate(fixture.Mesh);
                const string target = "__VRVlog_Menu_JawOpenFixture";
                menuMesh.AddBlendShapeFrame(target, 100, Enumerable.Repeat(Vector3.forward * .04f, menuMesh.vertexCount).ToArray(), null, null);
                var skins = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>(); skins[0].sharedMesh = menuMesh;
                foreach (var skin in skins) skin.sharedMaterial.shader = Shader.Find("lilToon");
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Jaw menu namespace", "Tests",
                    exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null);
                var glb = GlbDocument.Read(bytes);
                var vrm = (Dictionary<string, object>)((Dictionary<string, object>)glb.Json["extensions"])["VRMC_vrm"];
                var custom = (Dictionary<string, object>)((Dictionary<string, object>)vrm["expressions"])["custom"];
                foreach (var key in custom.Keys.Where(key => key.StartsWith("UE/", StringComparison.Ordinal)).ToArray()) custom.Remove(key);
                // Exercise the production menu registrar before automatic UE
                // injection, using real exported meshes without optional SDKs.
                var entry = new VrmMenuExpressions.Expression { Name = "JawOpen" }; entry.Targets.Add(target);
                bytes = VrmUnifiedExpressions.Add(VrmMenuExpressions.Add(glb.Write(), new List<VrmMenuExpressions.Expression> { entry }));
                Assert.That(VrmMenuExpressions.CountRegistered(bytes), Is.EqualTo(1));
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                var menu = imported.Vrm.Expression.CustomClips.Single(clip => clip.name == "VRChat / JawOpen");
                var jaw = imported.Vrm.Expression.CustomClips.Single(clip => clip.name == "UE/JawOpen");
                Assert.That(menu.IsBinary, Is.True);
                Assert.That(menu.OverrideMouth.ToString(), Is.EqualTo("block"));
                Assert.That(menu.OverrideBlink.ToString(), Is.EqualTo("block"));
                Assert.That(menu.OverrideLookAt.ToString(), Is.EqualTo("block"));
                Assert.That(jaw.IsBinary, Is.False);
                Assert.That(jaw.OverrideMouth.ToString(), Is.EqualTo("none"));
                Assert.That(jaw.MorphTargetBindings.Length, Is.EqualTo(2));
                imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(menu.name), .25f);
                imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(jaw.name), .3f);
                imported.Runtime.Process();
                foreach (var binding in jaw.MorphTargetBindings)
                {
                    var skin = imported.transform.Find(binding.RelativePath).GetComponent<SkinnedMeshRenderer>();
                    Assert.That(skin.sharedMesh.GetBlendShapeName(binding.Index), Is.EqualTo("JawOpen"));
                    Assert.That(skin.GetBlendShapeWeight(binding.Index), Is.EqualTo(30).Within(.001));
                }
                var menuBinding = menu.MorphTargetBindings.Single();
                var menuSkin = imported.transform.Find(menuBinding.RelativePath).GetComponent<SkinnedMeshRenderer>();
                Assert.That(menuSkin.GetBlendShapeWeight(menuBinding.Index), Is.Zero);
                imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(jaw.name), 0f);
                imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(menu.name), 1f);
                imported.Runtime.Process();
                Assert.That(menuSkin.GetBlendShapeWeight(menuBinding.Index), Is.EqualTo(100).Within(.001));
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                if (menuMesh != null) Object.DestroyImmediate(menuMesh);
            }
        }
    }
}
