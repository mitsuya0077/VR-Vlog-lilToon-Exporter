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
                fixture.Mesh.AddBlendShapeFrame("EyeClosedLeft", 100, Enumerable.Repeat(Vector3.up * .02f, fixture.Mesh.vertexCount).ToArray(), null, null);
                mouthMesh.AddBlendShapeFrame("JawOpen", 100, Enumerable.Repeat(Vector3.right * .03f, mouthMesh.vertexCount).ToArray(), null, null);
                if (bilateral) mouthMesh.AddBlendShapeFrame("EyeClosedRight", 100, Enumerable.Repeat(Vector3.up * .02f, mouthMesh.vertexCount).ToArray(), null, null);
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
                // A disabled authored route reserves both meshes without
                // establishing UE support. Verify the automatic safeguard,
                // then opt out of blink for this authored-preservation probe.
                var noBlink = authoredWeight == 0 && !otherMeshUncovered;
                if (noBlink) Assert.Throws<InvalidOperationException>(() => UniVrmOneClickExporter.Export(fixture.Source, "Authored UE coverage", "Tests"));
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Authored UE coverage", "Tests",
                    blinkOptions: noBlink ? new BlinkExportOptions { Mode = BlinkExportMode.None } : null);
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

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task UeEvidencePairsOnlyCompatibleLegacySidesAcrossRenderers(bool compatible, bool fullLilToon)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var rightMesh = Object.Instantiate(fixture.Mesh);
            Vrm10Instance imported = null;
            try
            {
                fixture.Mesh.AddBlendShapeFrame("MouthClosed", 100, Enumerable.Repeat(Vector3.right * .01f, fixture.Mesh.vertexCount).ToArray(), null, null);
                fixture.Mesh.AddBlendShapeFrame("Blink_L", 100, Enumerable.Repeat(Vector3.up * .02f, fixture.Mesh.vertexCount).ToArray(), null, null);
                var rightName = compatible ? "Blink_R" : "eye_blink_1_R";
                rightMesh.AddBlendShapeFrame(rightName, 100, Enumerable.Repeat(Vector3.up * .03f, rightMesh.vertexCount).ToArray(), null, null);
                var skins = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
                skins[1].sharedMesh = rightMesh;
                foreach (var skin in skins) skin.sharedMaterial.shader = Shader.Find("lilToon");
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "UE legacy family regression", "Tests",
                    exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null);
                var glb = GlbDocument.Read(bytes);
                var metadata = (Dictionary<string, object>)((Dictionary<string, object>)glb.Json["extensions"])["VRMC_vrm"];
                var presets = (Dictionary<string, object>)((Dictionary<string, object>)metadata["expressions"])["preset"];
                foreach (var name in BlinkShapeNames.Presets) Assert.That(presets.ContainsKey(name), Is.EqualTo(compatible));
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported.Vrm.Expression.CustomClips.Any(clip => clip.name == "UE/MouthClosed"), Is.True);
                Assert.That(imported.Vrm.Expression.Blink.MorphTargetBindings.Length, Is.EqualTo(compatible ? 2 : 0));
                Assert.That(imported.Vrm.Expression.BlinkLeft.MorphTargetBindings.Length, Is.EqualTo(compatible ? 1 : 0));
                Assert.That(imported.Vrm.Expression.BlinkRight.MorphTargetBindings.Length, Is.EqualTo(compatible ? 1 : 0));
                if (compatible)
                {
                    imported.Runtime.Expression.SetWeight(ExpressionKey.Blink, 1f);
                    imported.Runtime.Process();
                    foreach (var binding in imported.Vrm.Expression.Blink.MorphTargetBindings)
                    {
                        var skin = imported.transform.Find(binding.RelativePath).GetComponent<SkinnedMeshRenderer>();
                        Assert.That(skin.GetBlendShapeWeight(binding.Index), Is.EqualTo(100).Within(.001));
                        var delta = new Vector3[skin.sharedMesh.vertexCount];
                        skin.sharedMesh.GetBlendShapeFrameVertices(binding.Index, 0, delta, null, null);
                        Assert.That(delta[0].y, Is.EqualTo(binding.RelativePath == "Front" ? .02f : .03f).Within(.0001));
                    }
                }
                Assert.That(skins[0].sharedMesh, Is.SameAs(fixture.Mesh)); Assert.That(skins[1].sharedMesh, Is.SameAs(rightMesh));
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); Object.DestroyImmediate(rightMesh); }
        }

        [TestCase("en")]
        [TestCase("ko")]
        [TestCase("zh-Hans")]
        [TestCase("zh-Hant")]
        public void UnifiedExpressionFailuresHaveLocalizedActionsAndPreserveTargetNames(string locale)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("VRVlog.LilToonExporter.ExporterLocalization")).First(value => value != null);
            var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var localeField = type.GetField("_locale", flags); var messagesField = type.GetField("_messages", flags);
            var previousLocale = localeField.GetValue(null); var previousMessages = messagesField.GetValue(null);
            try
            {
                localeField.SetValue(null, locale); messagesField.SetValue(null, null);
                string T(string source) => (string)type.GetMethod("T").Invoke(null, new object[] { source });
                const string lost = "Modular Avatar / NDMF の処理で Unified Expressions の追跡用変形が失われました。メッシュや BlendShape を変更する追加ツールの設定を確認してください。";
                Assert.That(T(lost), Is.Not.EqualTo(lost));
                const string missing = "Unified Expressions の書き出しに必要な VRM 情報がありません。";
                Assert.That(T(missing), Is.Not.EqualTo(missing));
                foreach (var prefix in new[] { "Unified Expressions の出力 mesh に primitive がありません: ", "Unified Expressions の morph target 参照が不正です: " })
                {
                    var source = prefix + "UE/EyeClosedLeft";
                    Assert.That(T(source), Is.Not.EqualTo(source));
                    Assert.That(T(source), Does.EndWith("UE/EyeClosedLeft"));
                }
                foreach (var index in new[] { "node", "mesh", "morph", "material" })
                    Assert.That(T("Invalid Unified Expressions " + index + " index."), Is.Not.EqualTo("Invalid Unified Expressions " + index + " index."));
            }
            finally { localeField.SetValue(null, previousLocale); messagesField.SetValue(null, previousMessages); }
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

        [TestCase("empty", false)]
        [TestCase("empty", true)]
        [TestCase("authoredInert", false)]
        [TestCase("authoredInert", true)]
        [TestCase("authoredRest100", false)]
        [TestCase("authoredRest100", true)]
        [TestCase("authoredRest100Partial", false)]
        [TestCase("authoredRest100Partial", true)]
        [TestCase("disabled", false)]
        [TestCase("disabled", true)]
        [TestCase("invalid", false)]
        [TestCase("invalid", true)]
        [TestCase("missingRenderer", false)]
        [TestCase("missingRenderer", true)]
        [TestCase("missingMaterial", false)]
        [TestCase("missingMaterial", true)]
        [TestCase("inert", false)]
        [TestCase("inert", true)]
        [TestCase("rest100", false)]
        [TestCase("rest100", true)]
        [TestCase("unsupportedRest", false)]
        [TestCase("unsupportedRest", true)]
        [TestCase("suppressed", false)]
        [TestCase("suppressed", true)]
        [TestCase("inertPreferred", false)]
        [TestCase("inertPreferred", true)]
        [TestCase("ambiguousRaw", false)]
        [TestCase("ambiguousRaw", true)]
        public void MissingBlinkRequiresUsableUnifiedRoute(string scenario, bool fullLilToon)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var vrm = ScriptableObject.CreateInstance<VRM10Object>();
            var clip = ScriptableObject.CreateInstance<VRM10Expression>();
            try
            {
                // A shared ARKit jaw is useful only after a surviving UE route
                // establishes the profile; the rejected route cannot enable it.
                AddTrackingDelta(fixture.Mesh, "JawOpen");
                clip.name = "UE/MouthClosed";
                if (scenario == "empty") vrm.Expression.CustomClips.Add(clip);
                else if (scenario == "disabled" || scenario == "invalid" || scenario == "missingRenderer" ||
                    scenario == "authoredInert" || scenario == "authoredRest100" || scenario == "authoredRest100Partial")
                {
                    AddTrackingDelta(fixture.Mesh, "MouthClosed", scenario == "authoredInert" ? 0 : .02f);
                    clip.MorphTargetBindings = new[] { new MorphTargetBinding(
                        scenario == "missingRenderer" ? null : "Front", scenario == "invalid" ? 999 : 2,
                        scenario == "disabled" ? 0 : scenario == "authoredRest100" ? 1 : .5f) };
                    if (scenario == "authoredRest100" || scenario == "authoredRest100Partial")
                        foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.SetBlendShapeWeight(2, 100);
                    vrm.Expression.CustomClips.Add(clip);
                }
                else if (scenario == "missingMaterial")
                {
                    AddTrackingDelta(fixture.Mesh, "MouthClosed");
                    clip.MaterialColorBindings = new[] { new MaterialColorBinding {
                        MaterialName = "missing material", BindType = MaterialColorType.color, TargetValue = Color.red } };
                    vrm.Expression.CustomClips.Add(clip);
                }
                else if (scenario == "suppressed")
                {
                    AddTrackingDelta(fixture.Mesh, "LipFunnel");
                    clip.name = "UE/LipFunnelUpperLeft";
                    clip.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 2, 0), new MorphTargetBinding("Back", 2, 0) };
                    vrm.Expression.CustomClips.Add(clip);
                }
                else if (scenario == "ambiguousRaw")
                {
                    AddTrackingDelta(fixture.Mesh, "mouth_closed"); AddTrackingDelta(fixture.Mesh, "mouth-closed");
                }
                else
                {
                    AddTrackingDelta(fixture.Mesh, "MouthClosed", scenario == "inert" ? 0 : .02f);
                    if (scenario == "inertPreferred") AddTrackingDelta(fixture.Mesh, "UE/MouthClosed", 0);
                    if (scenario == "rest100" || scenario == "unsupportedRest")
                        foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>())
                            skin.SetBlendShapeWeight(2, scenario == "rest100" ? 100 : -10);
                }
                foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = Shader.Find("lilToon");
                fixture.Source.AddComponent<Vrm10Instance>().Vrm = vrm;
                Assert.That(UnifiedExpressionPreparation.HasUsableEvidence(fixture.Source), Is.False);
                var error = Assert.Throws<InvalidOperationException>(() => UniVrmOneClickExporter.Export(fixture.Source,
                    "Missing blink safeguard", "Tests", exporterVersion: fullLilToon ? "0.11.5" : null,
                    lilToonVersion: fullLilToon ? "2.3.4" : null));
                Assert.That(error.Message, Does.Contain("閉眼"));
                Assert.That(fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>().All(skin => skin.sharedMesh == fixture.Mesh), Is.True);
                Assert.That(fixture.Mesh.GetBlendShapeIndex("JawOpen"), Is.EqualTo(1));
            }
            finally { Object.DestroyImmediate(clip); Object.DestroyImmediate(vrm); }
        }

        [TestCase("partial", false)]
        [TestCase("partial", true)]
        [TestCase("rawMouth", false)]
        [TestCase("rawMouth", true)]
        [TestCase("authoredMorph", false)]
        [TestCase("authoredMorph", true)]
        [TestCase("authoredColor", false)]
        [TestCase("authoredColor", true)]
        [TestCase("authoredUv", false)]
        [TestCase("authoredUv", true)]
        [TestCase("authoredColorInert", false)]
        [TestCase("authoredColorInert", true)]
        [TestCase("authoredUvInert", false)]
        [TestCase("authoredUvInert", true)]
        [TestCase("emptyRepair", false)]
        [TestCase("emptyRepair", true)]
        public async Task SupportedUnifiedRouteWithoutBilateralEyeRigSurvivesBothExportModes(string scenario, bool fullLilToon)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var vrm = ScriptableObject.CreateInstance<VRM10Object>();
            var clip = ScriptableObject.CreateInstance<VRM10Expression>();
            Vrm10Instance imported = null;
            try
            {
                AddTrackingDelta(fixture.Mesh, "JawOpen");
                clip.name = "UE/MouthClosed";
                if (scenario == "partial") AddTrackingDelta(fixture.Mesh, "EyeClosedLeft");
                else if (scenario == "rawMouth" || scenario == "emptyRepair")
                {
                    AddTrackingDelta(fixture.Mesh, "MouthClosed");
                    if (scenario == "emptyRepair") vrm.Expression.CustomClips.Add(clip);
                }
                else
                {
                    if (scenario == "authoredMorph") clip.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 0, .4f) };
                    if (scenario == "authoredColorInert" || scenario == "authoredUvInert")
                    {
                        AddTrackingDelta(fixture.Mesh, "MouthClosed", 0);
                        clip.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 2, .5f) };
                    }
                    if (scenario == "authoredColor" || scenario == "authoredColorInert") clip.MaterialColorBindings = new[] { new MaterialColorBinding {
                        MaterialName = fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>().sharedMaterial.name,
                        BindType = MaterialColorType.color, TargetValue = Color.red } };
                    if (scenario == "authoredUv" || scenario == "authoredUvInert") clip.MaterialUVBindings = new[] { new MaterialUVBinding {
                        MaterialName = fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>().sharedMaterial.name,
                        Scaling = Vector2.one, Offset = Vector2.up } };
                    vrm.Expression.CustomClips.Add(clip);
                }
                foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = Shader.Find("lilToon");
                fixture.Source.AddComponent<Vrm10Instance>().Vrm = vrm;
                Assert.That(UnifiedExpressionPreparation.HasUsableEvidence(fixture.Source), Is.True);
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Supported UE without bilateral eyes", "Tests",
                    exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null);
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported.Vrm.Expression.Blink.MorphTargetBindings.Length, Is.Zero);
                Assert.That(imported.Vrm.Expression.BlinkLeft.MorphTargetBindings.Length, Is.Zero);
                Assert.That(imported.Vrm.Expression.BlinkRight.MorphTargetBindings.Length, Is.Zero);
                var jaw = imported.Vrm.Expression.CustomClips.Single(value => value.name == "UE/JawOpen");
                Assert.That(jaw.MorphTargetBindings.Length, Is.EqualTo(2));
                imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(jaw.name), .3f);
                imported.Runtime.Process();
                foreach (var binding in jaw.MorphTargetBindings)
                    Assert.That(imported.transform.Find(binding.RelativePath).GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(binding.Index), Is.EqualTo(30).Within(.001));
                var route = imported.Vrm.Expression.CustomClips.Single(value => value.name == (scenario == "partial" ? "UE/EyeClosedLeft" : "UE/MouthClosed"));
                if (scenario == "authoredMorph") Assert.That(route.MorphTargetBindings.Single().Weight, Is.EqualTo(.4f).Within(.001));
                if (scenario == "authoredColor" || scenario == "authoredColorInert") Assert.That(route.MaterialColorBindings.Length, Is.EqualTo(1));
                if (scenario == "authoredUv" || scenario == "authoredUvInert") Assert.That(route.MaterialUVBindings.Length, Is.EqualTo(1));
                Assert.That(fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>().All(skin => skin.sharedMesh == fixture.Mesh), Is.True);
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                Object.DestroyImmediate(clip); Object.DestroyImmediate(vrm);
            }
        }

        [TestCase(100f, 1f, false)]
        [TestCase(100f, 1f, true)]
        [TestCase(100f, .5f, false)]
        [TestCase(100f, .5f, true)]
        [TestCase(50f, .5f, false)]
        [TestCase(50f, .5f, true)]
        public async Task AuthoredMorphEligibilityMatchesActualExportedResidual(float rest, float bindingWeight, bool fullLilToon)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var vrm = ScriptableObject.CreateInstance<VRM10Object>();
            var clip = ScriptableObject.CreateInstance<VRM10Expression>();
            Vrm10Instance imported = null;
            try
            {
                AddTrackingDelta(fixture.Mesh, "MouthClosed");
                var skins = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
                foreach (var skin in skins) { skin.sharedMaterial.shader = Shader.Find("lilToon"); skin.SetBlendShapeWeight(1, rest); }
                clip.name = "UE/MouthClosed";
                clip.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 1, bindingWeight), new MorphTargetBinding("Back", 1, bindingWeight) };
                vrm.Expression.CustomClips.Add(clip); fixture.Source.AddComponent<Vrm10Instance>().Vrm = vrm;
                var usable = rest < 100;
                Assert.That(UnifiedExpressionPreparation.HasUsableEvidence(fixture.Source), Is.EqualTo(usable));
                if (!usable) Assert.Throws<InvalidOperationException>(() => UniVrmOneClickExporter.Export(fixture.Source,
                    "Authored residual safeguard", "Tests", exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null));
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Actual authored residual", "Tests",
                    exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null,
                    blinkOptions: usable ? null : new BlinkExportOptions { Mode = BlinkExportMode.None });
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                var authored = imported.Vrm.Expression.CustomClips.Single(value => value.name == clip.name);
                Assert.That(authored.MorphTargetBindings.All(binding => binding.Weight == bindingWeight), Is.True);
                imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(clip.name), 1f); imported.Runtime.Process();
                foreach (var binding in authored.MorphTargetBindings)
                {
                    var skin = imported.transform.Find(binding.RelativePath).GetComponent<SkinnedMeshRenderer>();
                    var delta = new Vector3[skin.sharedMesh.vertexCount];
                    skin.sharedMesh.GetBlendShapeFrameVertices(binding.Index, 0, delta, null, null);
                    Assert.That(delta[0].y, Is.EqualTo(.02f * (1 - rest / 100)).Within(.00001),
                        "The actual exported endpoint, including rest100+author.5, must agree with eligibility.");
                    Assert.That(skin.GetBlendShapeWeight(binding.Index), Is.EqualTo(bindingWeight * 100).Within(.001));
                }
                Assert.That(skins.All(skin => skin.GetBlendShapeWeight(1) == rest && skin.sharedMesh == fixture.Mesh), Is.True);
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); Object.DestroyImmediate(clip); Object.DestroyImmediate(vrm); }
        }

        [TestCase("rawGeometry")]
        [TestCase("rawRest")]
        [TestCase("authoredGeometry")]
        [TestCase("authoredRest")]
        [TestCase("authoredDisabled")]
        [TestCase("authoredRemoved")]
        [TestCase("color")]
        [TestCase("uv")]
        public void PreparationRejectsLossOfInitiallyEffectiveRoutesDespitePreservedNames(string change)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var vrm = ScriptableObject.CreateInstance<VRM10Object>();
            var clip = ScriptableObject.CreateInstance<VRM10Expression>();
            Mesh altered = null;
            try
            {
                AddTrackingDelta(fixture.Mesh, "MouthClosed");
                // An inert optional eye name stays present but is not an
                // initially effective endpoint requiring residual geometry.
                AddTrackingDelta(fixture.Mesh, "EyeClosedLeft", 0);
                clip.name = "UE/MouthClosed";
                var authored = change.StartsWith("authored", StringComparison.Ordinal);
                if (authored) clip.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 1, .5f), new MorphTargetBinding("Back", 1, .5f) };
                if (change == "color" || change == "uv")
                {
                    if (change == "color") clip.MaterialColorBindings = new[] { new MaterialColorBinding {
                        MaterialName = fixture.Skins[0].sharedMaterial.name, BindType = MaterialColorType.color, TargetValue = Color.red } };
                    else clip.MaterialUVBindings = new[] { new MaterialUVBinding {
                        MaterialName = fixture.Skins[0].sharedMaterial.name, Scaling = Vector2.one, Offset = Vector2.up } };
                }
                if (authored || change == "color" || change == "uv") vrm.Expression.CustomClips.Add(clip);
                fixture.Copy.AddComponent<Vrm10Instance>().Vrm = vrm;
                var guard = new UnifiedExpressionPreparation(fixture.Copy);
                Assert.That(guard.SupportsUnified, Is.True); Assert.DoesNotThrow(() => guard.Verify());
                var front = fixture.Copy.transform.Find("Front").GetComponent<SkinnedMeshRenderer>();
                if (change.EndsWith("Geometry", StringComparison.Ordinal))
                {
                    altered = Object.Instantiate(fixture.Mesh); altered.ClearBlendShapes();
                    altered.AddBlendShapeFrame("Hair detail", 100, Enumerable.Repeat(Vector3.up * .01f, altered.vertexCount).ToArray(), null, null);
                    AddTrackingDelta(altered, "MouthClosed", 0); AddTrackingDelta(altered, "EyeClosedLeft", 0);
                    front.sharedMesh = altered;
                }
                else if (change.EndsWith("Rest", StringComparison.Ordinal)) front.SetBlendShapeWeight(1, 100);
                else if (change == "authoredDisabled") clip.MorphTargetBindings = clip.MorphTargetBindings.Select(binding => new MorphTargetBinding(binding.RelativePath, binding.Index, 0)).ToArray();
                else if (change == "authoredRemoved") vrm.Expression.CustomClips.Clear();
                else if (change == "color") clip.MaterialColorBindings = new[] { new MaterialColorBinding { MaterialName = "lost material", BindType = MaterialColorType.color, TargetValue = Color.red } };
                else clip.MaterialUVBindings = new[] { new MaterialUVBinding { MaterialName = "lost material", Scaling = Vector2.one, Offset = Vector2.up } };
                if (change.EndsWith("Geometry", StringComparison.Ordinal) || change.EndsWith("Rest", StringComparison.Ordinal))
                    Assert.That(UnifiedExpressionPreparation.HasUsableEvidence(fixture.Copy), Is.True, "Another renderer's effective route must not conceal this renderer's lost endpoint.");
                var error = Assert.Throws<InvalidOperationException>(() => guard.Verify());
                Assert.That(error.Message, Does.Contain("追跡用変形"));
                Assert.That(fixture.Copy.transform.Find("Back").GetComponent<SkinnedMeshRenderer>().sharedMesh, Is.SameAs(fixture.Mesh));
            }
            finally { if (altered != null) Object.DestroyImmediate(altered); Object.DestroyImmediate(clip); Object.DestroyImmediate(vrm); }
        }

        [Test]
        public void PreparationAllowsPreviouslyInertOptionalNamesWhileUsableRouteSurvives()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            AddTrackingDelta(fixture.Mesh, "MouthClosed"); AddTrackingDelta(fixture.Mesh, "EyeClosedLeft", 0);
            foreach (var skin in fixture.Copy.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.SetBlendShapeWeight(2, 100);
            var guard = new UnifiedExpressionPreparation(fixture.Copy);
            Assert.That(guard.SupportsUnified, Is.True); Assert.DoesNotThrow(() => guard.Verify());
            foreach (var skin in fixture.Copy.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.SetBlendShapeWeight(2, 0);
            Assert.DoesNotThrow(() => guard.Verify(), "A preexisting inert optional morph was not an effective tracking endpoint.");
        }

        [Test]
        public void PreparationChecksEarlierAutomaticBlinkBypassWhenEvidenceWasAlreadyLost()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            AddTrackingDelta(fixture.Mesh, "MouthClosed");
            using var blink = BlinkExportSession.Resolve(fixture.Source);
            Assert.That(blink.RequiresUnifiedEvidence, Is.True);
            var inert = Object.Instantiate(fixture.Mesh);
            try
            {
                inert.ClearBlendShapes(); AddTrackingDelta(inert, "Hair detail"); AddTrackingDelta(inert, "MouthClosed", 0);
                foreach (var skin in fixture.Copy.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMesh = inert;
                var guard = new UnifiedExpressionPreparation(fixture.Copy);
                Assert.That(guard.SupportsUnified, Is.False);
                Assert.DoesNotThrow(() => guard.Verify(), "The inert morph already existed at this preparation stage.");
                Assert.Throws<InvalidOperationException>(() => guard.Verify(blink.RequiresUnifiedEvidence),
                    "An earlier source-based automatic bypass must still require effective output evidence.");
            }
            finally { Object.DestroyImmediate(inert); }
        }

        [TestCase("mixedMorph", false)]
        [TestCase("mixedMorph", true)]
        [TestCase("discardedInvalidMorph", false)]
        [TestCase("discardedInvalidMorph", true)]
        [TestCase("onlyExcluded", false)]
        [TestCase("onlyExcluded", true)]
        [TestCase("color", false)]
        [TestCase("color", true)]
        [TestCase("uv", false)]
        [TestCase("uv", true)]
        [TestCase("onlyExcludedColor", false)]
        [TestCase("onlyExcludedColor", true)]
        public async Task AuthoredExclusionsMatchFinalReferencePruning(string kind, bool fullLilToon)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var vrm = ScriptableObject.CreateInstance<VRM10Object>();
            var clip = ScriptableObject.CreateInstance<VRM10Expression>();
            Material excludedMaterial = null; Vrm10Instance imported = null;
            try
            {
                AddTrackingDelta(fixture.Mesh, "MouthClosed");
                var front = fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>();
                var back = fixture.Source.transform.Find("Back").GetComponent<SkinnedMeshRenderer>();
                front.sharedMaterial.shader = Shader.Find("lilToon");
                excludedMaterial = new Material(back.sharedMaterial) { name = "UE excluded material" }; back.sharedMaterial = excludedMaterial;
                clip.name = "UE/MouthClosed";
                if (kind == "mixedMorph" || kind == "discardedInvalidMorph") clip.MorphTargetBindings = new[] {
                    new MorphTargetBinding("Front", 1, .5f), new MorphTargetBinding("Back", kind == "discardedInvalidMorph" ? 999 : 1, .7f) };
                if (kind == "onlyExcluded") clip.MorphTargetBindings = new[] { new MorphTargetBinding("Back", 1, .7f) };
                if (kind == "color" || kind == "uv") clip.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 1, .5f) };
                if (kind == "color" || kind == "onlyExcludedColor") clip.MaterialColorBindings =
                    (kind == "color" ? new[] { front.sharedMaterial.name, excludedMaterial.name } : new[] { excludedMaterial.name })
                    .Select(name => new MaterialColorBinding { MaterialName = name, BindType = MaterialColorType.color, TargetValue = Color.red }).ToArray();
                if (kind == "uv") clip.MaterialUVBindings = new[] { front.sharedMaterial.name, excludedMaterial.name }.Select(name =>
                    new MaterialUVBinding { MaterialName = name, Scaling = Vector2.one, Offset = Vector2.up }).ToArray();
                vrm.Expression.CustomClips.Add(clip); fixture.Source.AddComponent<Vrm10Instance>().Vrm = vrm;
                Assert.That(UnifiedExpressionPreparation.HasUsableEvidence(fixture.Source, target => target == back.transform), Is.True);
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Retained authored UE references", "Tests",
                    excludedObjects: new[] { back.gameObject }, exporterVersion: fullLilToon ? "0.11.5" : null,
                    lilToonVersion: fullLilToon ? "2.3.4" : null);
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                var route = imported.Vrm.Expression.CustomClips.Single(value => value.name == clip.name);
                Assert.That(route.MorphTargetBindings.Length, Is.EqualTo(1));
                Assert.That(route.MorphTargetBindings[0].RelativePath, Is.EqualTo("Front"));
                var repaired = kind == "onlyExcluded" || kind == "onlyExcludedColor";
                Assert.That(route.MorphTargetBindings[0].Weight, Is.EqualTo(repaired ? 1 : .5f).Within(.001));
                Assert.That(route.MaterialColorBindings.Length, Is.EqualTo(kind == "color" ? 1 : 0));
                Assert.That(route.MaterialUVBindings.Length, Is.EqualTo(kind == "uv" ? 1 : 0));
                Assert.That(imported.transform.Find("Back"), Is.Null);
                imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(route.name), .5f); imported.Runtime.Process();
                Assert.That(imported.transform.Find("Front").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(route.MorphTargetBindings[0].Index),
                    Is.EqualTo(repaired ? 50 : 25).Within(.001));
                Assert.That(back.sharedMesh, Is.SameAs(fixture.Mesh)); Assert.That(back.sharedMaterial, Is.SameAs(excludedMaterial));
                Assert.That(vrm.Expression.CustomClips.Single(), Is.SameAs(clip));
                Assert.That(clip.MorphTargetBindings.Any(binding => binding.RelativePath == "Back"), Is.EqualTo(kind == "mixedMorph" || kind == "discardedInvalidMorph" || kind == "onlyExcluded"));
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); if (excludedMaterial != null) Object.DestroyImmediate(excludedMaterial); Object.DestroyImmediate(clip); Object.DestroyImmediate(vrm); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AuthoredRouteWithOnlyExcludedTargetsCannotWaiveMissingBlink(bool fullLilToon)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var vrm = ScriptableObject.CreateInstance<VRM10Object>(); var clip = ScriptableObject.CreateInstance<VRM10Expression>();
            try
            {
                var back = fixture.Source.transform.Find("Back"); clip.name = "UE/MouthClosed";
                clip.MorphTargetBindings = new[] { new MorphTargetBinding("Back", 0, .5f) };
                vrm.Expression.CustomClips.Add(clip); fixture.Source.AddComponent<Vrm10Instance>().Vrm = vrm;
                Assert.That(UnifiedExpressionPreparation.HasUsableEvidence(fixture.Source, target => target == back), Is.False);
                Assert.Throws<InvalidOperationException>(() => UniVrmOneClickExporter.Export(fixture.Source, "No retained UE route", "Tests",
                    excludedObjects: new[] { back.gameObject }, exporterVersion: fullLilToon ? "0.11.5" : null,
                    lilToonVersion: fullLilToon ? "2.3.4" : null));
                Assert.That(clip.MorphTargetBindings.Single().Weight, Is.EqualTo(.5f));
            }
            finally { Object.DestroyImmediate(clip); Object.DestroyImmediate(vrm); }
        }

        [TestCase(0f, false, false)]
        [TestCase(0f, false, true)]
        [TestCase(0f, true, false)]
        [TestCase(0f, true, true)]
        [TestCase(25f, false, false)]
        [TestCase(25f, false, true)]
        [TestCase(25f, true, false)]
        [TestCase(25f, true, true)]
        public async Task LeadingNeutralFrameSurvivesActualGlbAndContinuousPlayback(float rest, bool authored, bool fullLilToon)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var vrm = ScriptableObject.CreateInstance<VRM10Object>(); var clip = ScriptableObject.CreateInstance<VRM10Expression>();
            var baked = new Mesh(); Vrm10Instance imported = null;
            try
            {
                fixture.Mesh.AddBlendShapeFrame("MouthClosed", 0, new Vector3[fixture.Mesh.vertexCount], null, null);
                AddTrackingDelta(fixture.Mesh, "MouthClosed");
                foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) { skin.SetBlendShapeWeight(1, rest); skin.sharedMaterial.shader = Shader.Find("lilToon"); }
                clip.name = "UE/MouthClosed";
                if (authored)
                {
                    clip.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 1, .6f), new MorphTargetBinding("Back", 1, .6f) };
                    vrm.Expression.CustomClips.Add(clip); fixture.Source.AddComponent<Vrm10Instance>().Vrm = vrm;
                }
                Assert.That(UnifiedExpressionPreparation.HasUsableEvidence(fixture.Source), Is.True);
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Leading neutral UE frame", "Tests",
                    exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null);
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                var route = imported.Vrm.Expression.CustomClips.Single(value => value.name == clip.name);
                var baseline = new Dictionary<string, Vector3>();
                foreach (var input in new[] { 0f, .5f, 1f })
                {
                    imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(route.name), input); imported.Runtime.Process();
                    foreach (var binding in route.MorphTargetBindings)
                    {
                        var skin = imported.transform.Find(binding.RelativePath).GetComponent<SkinnedMeshRenderer>();
                        Assert.That(skin.GetBlendShapeWeight(binding.Index), Is.EqualTo(input * (authored ? 60 : 100)).Within(.001));
                        var delta = new Vector3[skin.sharedMesh.vertexCount]; skin.sharedMesh.GetBlendShapeFrameVertices(binding.Index, 0, delta, null, null);
                        Assert.That(delta[0].y, Is.EqualTo(.02f * (1 - rest / 100)).Within(.00001));
                        skin.BakeMesh(baked);
                        if (input == 0) baseline.Add(binding.RelativePath, baked.vertices[0]);
                        Assert.That(Vector3.Distance(baseline[binding.RelativePath], baked.vertices[0]),
                            Is.EqualTo(.02f * (1 - rest / 100) * input * (authored ? .6f : 1)).Within(.0001));
                    }
                }
                Assert.That(fixture.Mesh.GetBlendShapeFrameCount(1), Is.EqualTo(2));
                Assert.That(fixture.Mesh.GetBlendShapeFrameWeight(1, 0), Is.Zero);
                Assert.That(fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>().All(skin => skin.GetBlendShapeWeight(1) == rest), Is.True);
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); Object.DestroyImmediate(baked); Object.DestroyImmediate(clip); Object.DestroyImmediate(vrm); }
        }

        [TestCase("inertExplicitAlias")]
        [TestCase("validExplicitAlias")]
        [TestCase("ambiguousAlias")]
        [TestCase("authoredCoverage")]
        [TestCase("authoredDisabledAlias")]
        [TestCase("authoredHigherAlias")]
        [TestCase("authoredWeight")]
        public void PreparationRejectsChangedCanonicalRouteWhileOtherUnifiedEvidenceSurvives(string change)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var vrm = ScriptableObject.CreateInstance<VRM10Object>(); var clip = ScriptableObject.CreateInstance<VRM10Expression>();
            var replacementClip = ScriptableObject.CreateInstance<VRM10Expression>(); Mesh changed = null;
            try
            {
                var originalName = change == "ambiguousAlias" ? "mouth_closed" : change == "authoredCoverage" ? "LipFunnel" : "MouthClosed";
                AddTrackingDelta(fixture.Mesh, originalName); AddTrackingDelta(fixture.Mesh, "EyeClosedLeft");
                var isAuthored = change == "authoredDisabledAlias" || change == "authoredHigherAlias" || change == "authoredWeight";
                if (isAuthored) { clip.name = "MouthClosed"; clip.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 1, .5f) }; vrm.Expression.CustomClips.Add(clip); }
                fixture.Copy.AddComponent<Vrm10Instance>().Vrm = vrm;
                var guard = new UnifiedExpressionPreparation(fixture.Copy); Assert.DoesNotThrow(() => guard.Verify());
                var front = fixture.Copy.transform.Find("Front").GetComponent<SkinnedMeshRenderer>();
                if (change == "inertExplicitAlias" || change == "validExplicitAlias" || change == "ambiguousAlias")
                {
                    changed = Object.Instantiate(fixture.Mesh); AddTrackingDelta(changed, change == "ambiguousAlias" ? "mouth-closed" : "UE/MouthClosed", change == "inertExplicitAlias" ? 0 : .03f); front.sharedMesh = changed;
                }
                else if (change == "authoredCoverage")
                {
                    replacementClip.name = "UE/LipFunnelUpperLeft"; replacementClip.MaterialColorBindings = new[] { new MaterialColorBinding {
                        MaterialName = front.sharedMaterial.name, BindType = MaterialColorType.color, TargetValue = Color.red } }; vrm.Expression.CustomClips.Add(replacementClip);
                }
                else if (change == "authoredWeight") clip.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 1, .7f) };
                else
                {
                    replacementClip.name = "UE/MouthClosed"; replacementClip.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 1, change == "authoredDisabledAlias" ? 0 : .7f) }; vrm.Expression.CustomClips.Add(replacementClip);
                }
                Assert.That(AvatarBaseShape.HasUsableMorphEndpoint(front, front.sharedMesh.GetBlendShapeIndex(originalName)), Is.True);
                Assert.That(UnifiedExpressionPreparation.HasUsableEvidence(fixture.Copy), Is.True, "Other UE channels must not conceal a changed selected canonical route.");
                Assert.Throws<InvalidOperationException>(() => guard.Verify());
            }
            finally { if (changed != null) Object.DestroyImmediate(changed); Object.DestroyImmediate(replacementClip); Object.DestroyImmediate(clip); Object.DestroyImmediate(vrm); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PreparationAuthoredTargetIdentityAllowsOnlyCorrectIndexRemapping(bool remapped)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var vrm = ScriptableObject.CreateInstance<VRM10Object>(); var clip = ScriptableObject.CreateInstance<VRM10Expression>(); Mesh reordered = null;
            try
            {
                AddTrackingDelta(fixture.Mesh, "EyeClosedLeft"); AddTrackingDelta(fixture.Mesh, "JawOpen");
                clip.name = "UE/EyeClosedLeft"; clip.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 1, .5f) };
                vrm.Expression.CustomClips.Add(clip); fixture.Copy.AddComponent<Vrm10Instance>().Vrm = vrm;
                var guard = new UnifiedExpressionPreparation(fixture.Copy); Assert.DoesNotThrow(() => guard.Verify());
                reordered = Object.Instantiate(fixture.Mesh); reordered.ClearBlendShapes(); AddTrackingDelta(reordered, "Hair detail");
                AddTrackingDelta(reordered, "JawOpen"); AddTrackingDelta(reordered, "EyeClosedLeft");
                fixture.Copy.transform.Find("Front").GetComponent<SkinnedMeshRenderer>().sharedMesh = reordered;
                if (remapped) clip.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 2, .5f) };
                Assert.That(UnifiedExpressionPreparation.HasUsableEvidence(fixture.Copy), Is.True);
                if (remapped) Assert.DoesNotThrow(() => guard.Verify(), "An updated index that preserves renderer, shape and weight retains the authored route.");
                else Assert.Throws<InvalidOperationException>(() => guard.Verify(), "A stale index retargeting a valid but different shape loses the authored route.");
            }
            finally { if (reordered != null) Object.DestroyImmediate(reordered); Object.DestroyImmediate(clip); Object.DestroyImmediate(vrm); }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task DuplicateAuthoredTargetsMatchPinnedFirstBindingPlayback(bool positiveFirst, bool fullLilToon)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var vrm = ScriptableObject.CreateInstance<VRM10Object>(); var clip = ScriptableObject.CreateInstance<VRM10Expression>(); Vrm10Instance imported = null;
            try
            {
                AddTrackingDelta(fixture.Mesh, "MouthClosed");
                foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = Shader.Find("lilToon");
                clip.name = "UE/MouthClosed"; clip.MorphTargetBindings = new[] {
                    new MorphTargetBinding("Front", 1, positiveFirst ? .6f : 0), new MorphTargetBinding("Front", 1, positiveFirst ? 0 : .6f) };
                vrm.Expression.CustomClips.Add(clip); fixture.Source.AddComponent<Vrm10Instance>().Vrm = vrm;
                Assert.That(UnifiedExpressionPreparation.HasUsableEvidence(fixture.Source), Is.EqualTo(positiveFirst));
                if (!positiveFirst) Assert.Throws<InvalidOperationException>(() => UniVrmOneClickExporter.Export(fixture.Source, "Disabled first authored target", "Tests",
                    exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null));
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Pinned duplicate target semantics", "Tests",
                    exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null,
                    blinkOptions: positiveFirst ? null : new BlinkExportOptions { Mode = BlinkExportMode.None });
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                var route = imported.Vrm.Expression.CustomClips.Single(value => value.name == clip.name);
                Assert.That(route.MorphTargetBindings.Length, Is.EqualTo(2));
                Assert.That(route.MorphTargetBindings[0].Weight, Is.EqualTo(positiveFirst ? .6f : 0));
                imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(route.name), .5f); imported.Runtime.Process();
                var target = imported.transform.Find("Front").GetComponent<SkinnedMeshRenderer>();
                Assert.That(target.GetBlendShapeWeight(route.MorphTargetBindings[0].Index), Is.EqualTo(positiveFirst ? 30 : 0).Within(.001));
                Assert.That(clip.MorphTargetBindings.Length, Is.EqualTo(2));
                Assert.That(clip.MorphTargetBindings[1].Weight, Is.EqualTo(positiveFirst ? 0 : .6f));
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); Object.DestroyImmediate(clip); Object.DestroyImmediate(vrm); }
        }

        [TestCase(0f, false)]
        [TestCase(0f, true)]
        [TestCase(100f, false)]
        [TestCase(100f, true)]
        [TestCase(150f, false)]
        [TestCase(150f, true)]
        [TestCase(200f, false)]
        [TestCase(200f, true)]
        public async Task ExtendedRawFramesUseActualRemainingExportRange(float rest, bool fullLilToon)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var baked = new Mesh(); Vrm10Instance imported = null;
            try
            {
                AddTrackingDelta(fixture.Mesh, "MouthClosed");
                fixture.Mesh.AddBlendShapeFrame("MouthClosed", 200, Enumerable.Repeat(Vector3.up * .04f, fixture.Mesh.vertexCount).ToArray(), null, null);
                var sourceSkins = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
                foreach (var skin in sourceSkins) { skin.SetBlendShapeWeight(1, rest); skin.sharedMaterial.shader = Shader.Find("lilToon"); }
                var usable = rest < 200;
                Assert.That(UnifiedExpressionPreparation.HasUsableEvidence(fixture.Source), Is.EqualTo(usable));
                if (!usable) Assert.Throws<InvalidOperationException>(() => UniVrmOneClickExporter.Export(fixture.Source, "Fully resting extended UE target", "Tests",
                    exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null));
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Extended raw UE frames", "Tests",
                    exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null,
                    blinkOptions: usable ? null : new BlinkExportOptions { Mode = BlinkExportMode.None });
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                var route = imported.Vrm.Expression.CustomClips.Single(clip => clip.name == "UE/MouthClosed");
                Assert.That(route.MorphTargetBindings.Length, Is.EqualTo(2));
                var baseline = new Dictionary<string, Vector3>();
                foreach (var input in new[] { 0f, .5f, 1f })
                {
                    imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(route.name), input); imported.Runtime.Process();
                    foreach (var binding in route.MorphTargetBindings)
                    {
                        var skin = imported.transform.Find(binding.RelativePath).GetComponent<SkinnedMeshRenderer>();
                        Assert.That(skin.GetBlendShapeWeight(binding.Index), Is.EqualTo(input * 100).Within(.001));
                        Assert.That(skin.sharedMesh.GetBlendShapeFrameCount(binding.Index), Is.EqualTo(1));
                        Assert.That(skin.sharedMesh.GetBlendShapeFrameWeight(binding.Index, 0), Is.EqualTo(100));
                        var delta = new Vector3[skin.sharedMesh.vertexCount]; skin.sharedMesh.GetBlendShapeFrameVertices(binding.Index, 0, delta, null, null);
                        var residual = .04f - .04f * rest / 200;
                        Assert.That(delta[0].y, Is.EqualTo(residual).Within(.00001));
                        skin.BakeMesh(baked);
                        if (input == 0) baseline.Add(binding.RelativePath, baked.vertices[0]);
                        Assert.That(Vector3.Distance(baseline[binding.RelativePath], baked.vertices[0]), Is.EqualTo(residual * input).Within(.0001));
                    }
                }
                Assert.That(fixture.Mesh.GetBlendShapeFrameCount(1), Is.EqualTo(2));
                Assert.That(fixture.Mesh.GetBlendShapeFrameWeight(1, 1), Is.EqualTo(200));
                Assert.That(sourceSkins.All(skin => skin.GetBlendShapeWeight(1) == rest && skin.sharedMesh == fixture.Mesh), Is.True);
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); Object.DestroyImmediate(baked); }
        }

        [TestCase(0f, false, false)]
        [TestCase(0f, false, true)]
        [TestCase(0f, true, false)]
        [TestCase(0f, true, true)]
        [TestCase(25f, false, false)]
        [TestCase(25f, false, true)]
        [TestCase(25f, true, false)]
        [TestCase(25f, true, true)]
        [TestCase(100f, false, false)]
        [TestCase(100f, false, true)]
        [TestCase(100f, true, false)]
        [TestCase(100f, true, true)]
        public async Task NegativeIntermediateFramesKeepTheirPositiveExportedEndpoint(float rest, bool authored, bool fullLilToon)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var vrm = ScriptableObject.CreateInstance<VRM10Object>(); var clip = ScriptableObject.CreateInstance<VRM10Expression>();
            var baked = new Mesh(); Vrm10Instance imported = null;
            try
            {
                fixture.Mesh.AddBlendShapeFrame("MouthClosed", -50, Enumerable.Repeat(Vector3.down * .02f, fixture.Mesh.vertexCount).ToArray(), null, null);
                fixture.Mesh.AddBlendShapeFrame("MouthClosed", 100, Enumerable.Repeat(Vector3.up * .04f, fixture.Mesh.vertexCount).ToArray(), null, null);
                Assert.That(fixture.Mesh.GetBlendShapeFrameCount(1), Is.EqualTo(2), "Actual Unity accepts the negative intermediate frame.");
                Assert.That(fixture.Mesh.GetBlendShapeFrameWeight(1, 0), Is.EqualTo(-50));
                var sourceSkins = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
                // Verify the native interpolation independently of our evaluator.
                sourceSkins[0].SetBlendShapeWeight(0, 0);
                foreach (var weight in new[] { 0f, 50f, 100f })
                {
                    sourceSkins[0].SetBlendShapeWeight(1, weight); sourceSkins[0].BakeMesh(baked);
                    Assert.That(Vector3.Distance(baked.vertices[0], fixture.Mesh.vertices[0] + Vector3.up * (.04f * weight / 100)), Is.LessThan(.000001f));
                }
                sourceSkins[0].SetBlendShapeWeight(0, 35);
                foreach (var skin in sourceSkins) { skin.SetBlendShapeWeight(1, rest); skin.sharedMaterial.shader = Shader.Find("lilToon"); }
                clip.name = "UE/MouthClosed";
                if (authored)
                {
                    clip.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 1, .6f), new MorphTargetBinding("Back", 1, .6f) };
                    vrm.Expression.CustomClips.Add(clip); fixture.Source.AddComponent<Vrm10Instance>().Vrm = vrm;
                }
                var usable = rest < 100;
                Assert.That(UnifiedExpressionPreparation.HasUsableEvidence(fixture.Source), Is.EqualTo(usable));
                if (!usable)
                {
                    var error = Assert.Throws<InvalidOperationException>(() => UniVrmOneClickExporter.Export(fixture.Source, "Negative curve fully resting safeguard", "Tests",
                        exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null));
                    Assert.That(error.Message, Does.Contain("閉眼"));
                }
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Negative intermediate UE curve", "Tests",
                    exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null,
                    blinkOptions: usable ? null : new BlinkExportOptions { Mode = BlinkExportMode.None });
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                var route = imported.Vrm.Expression.CustomClips.Single(value => value.name == clip.name);
                var baseline = new Dictionary<string, Vector3>();
                foreach (var input in new[] { 0f, .5f, 1f })
                {
                    imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(route.name), input); imported.Runtime.Process();
                    foreach (var binding in route.MorphTargetBindings)
                    {
                        var skin = imported.transform.Find(binding.RelativePath).GetComponent<SkinnedMeshRenderer>();
                        Assert.That(skin.GetBlendShapeWeight(binding.Index), Is.EqualTo(input * (authored ? 60 : 100)).Within(.001));
                        Assert.That(skin.sharedMesh.GetBlendShapeFrameCount(binding.Index), Is.EqualTo(1));
                        Assert.That(skin.sharedMesh.GetBlendShapeFrameWeight(binding.Index, 0), Is.EqualTo(100));
                        var delta = new Vector3[skin.sharedMesh.vertexCount]; skin.sharedMesh.GetBlendShapeFrameVertices(binding.Index, 0, delta, null, null);
                        Assert.That(delta[0].y, Is.EqualTo(.04f * (1 - rest / 100)).Within(.000001));
                        skin.BakeMesh(baked); if (input == 0) baseline.Add(binding.RelativePath, baked.vertices[0]);
                        Assert.That(Vector3.Distance(baseline[binding.RelativePath], baked.vertices[0]),
                            Is.EqualTo(.04f * (1 - rest / 100) * input * (authored ? .6f : 1)).Within(.00001));
                    }
                }
                Assert.That(fixture.Mesh.GetBlendShapeFrameCount(1), Is.EqualTo(2));
                Assert.That(fixture.Mesh.GetBlendShapeFrameWeight(1, 0), Is.EqualTo(-50));
                Assert.That(fixture.Mesh.GetBlendShapeFrameWeight(1, 1), Is.EqualTo(100));
                Assert.That(sourceSkins.All(skin => skin.sharedMesh == fixture.Mesh && skin.GetBlendShapeWeight(0) == 35 && skin.GetBlendShapeWeight(1) == rest), Is.True);
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); Object.DestroyImmediate(baked); Object.DestroyImmediate(clip); Object.DestroyImmediate(vrm); }
        }

        private static IEnumerable<TestCaseData> NativeNegativeBracketCases()
        {
            foreach (var curve in new[] { "nonproportional", "zeroEndpoint", "explicitNeutral" })
            foreach (var rest in new[] { 0f, 25f, 100f })
            foreach (var authored in new[] { false, true })
            foreach (var full in new[] { false, true })
                yield return new TestCaseData(curve, rest, authored, full);
        }

        [TestCaseSource(nameof(NativeNegativeBracketCases))]
        public async Task NegativeBracketPreservesNativeRestAndRebasedContinuousPlayback(string curve, float rest, bool authored, bool fullLilToon)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var vrm = ScriptableObject.CreateInstance<VRM10Object>(); var clip = ScriptableObject.CreateInstance<VRM10Expression>();
            var baked = new Mesh(); Vrm10Instance imported = null;
            try
            {
                fixture.Mesh.AddBlendShapeFrame("MouthClosed", -50, Enumerable.Repeat(Vector3.up * .03f, fixture.Mesh.vertexCount).ToArray(), null, null);
                if (curve == "explicitNeutral") fixture.Mesh.AddBlendShapeFrame("MouthClosed", 0, new Vector3[fixture.Mesh.vertexCount], null, null);
                fixture.Mesh.AddBlendShapeFrame("MouthClosed", 100, Enumerable.Repeat(Vector3.up * (curve == "zeroEndpoint" ? 0 : .02f), fixture.Mesh.vertexCount).ToArray(), null, null);
                var sourceSkins = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
                var nativeRest = new Dictionary<string, Vector3>(); var nativeEnd = new Dictionary<string, Vector3>();
                foreach (var skin in sourceSkins)
                {
                    // All stored weights really are zero in the rest0 cases.
                    // BakeMesh provides an independent native neutral reference.
                    skin.SetBlendShapeWeight(0, 0); skin.SetBlendShapeWeight(1, rest); skin.sharedMaterial.shader = Shader.Find("lilToon");
                    skin.BakeMesh(baked); nativeRest.Add(skin.name, skin.transform.TransformPoint(baked.vertices[0]));
                    if (rest == 0)
                    {
                        var expectedOffset = curve == "explicitNeutral" ? 0f : curve == "zeroEndpoint" ? .02f : .026666667f;
                        Assert.That(Vector3.Distance(baked.vertices[0], fixture.Mesh.vertices[0] + Vector3.up * expectedOffset), Is.LessThan(.000001f));
                    }
                    skin.SetBlendShapeWeight(1, 100); skin.BakeMesh(baked); nativeEnd.Add(skin.name, skin.transform.TransformPoint(baked.vertices[0]));
                    skin.SetBlendShapeWeight(1, rest);
                }
                clip.name = "UE/MouthClosed";
                if (authored)
                {
                    clip.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 1, .6f), new MorphTargetBinding("Back", 1, .6f) };
                    vrm.Expression.CustomClips.Add(clip); fixture.Source.AddComponent<Vrm10Instance>().Vrm = vrm;
                }
                var usable = rest < 100;
                Assert.That(UnifiedExpressionPreparation.HasUsableEvidence(fixture.Source), Is.EqualTo(usable));
                if (!usable)
                {
                    var error = Assert.Throws<InvalidOperationException>(() => UniVrmOneClickExporter.Export(fixture.Source, "Native negative bracket safeguard", "Tests",
                        exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null));
                    Assert.That(error.Message, Does.Contain("閉眼"));
                }
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Native negative bracket", "Tests",
                    exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null,
                    blinkOptions: usable ? null : new BlinkExportOptions { Mode = BlinkExportMode.None });
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                var route = imported.Vrm.Expression.CustomClips.Single(value => value.name == clip.name);
                Assert.That(route.MorphTargetBindings.Length, Is.EqualTo(2));
                foreach (var input in new[] { 0f, .5f, 1f })
                {
                    imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(route.name), input); imported.Runtime.Process();
                    foreach (var binding in route.MorphTargetBindings)
                    {
                        var skin = imported.transform.Find(binding.RelativePath).GetComponent<SkinnedMeshRenderer>();
                        var expected = Vector3.LerpUnclamped(nativeRest[skin.name], nativeEnd[skin.name], input * (authored ? .6f : 1));
                        skin.BakeMesh(baked);
                        Assert.That(Vector3.Distance(skin.transform.TransformPoint(baked.vertices[0]), expected), Is.LessThan(.00001f),
                            "Exported coefficient " + input + " must start from the actual native rest, including a negative/positive bracket's nonzero weight0 geometry.");
                        Assert.That(skin.sharedMesh.GetBlendShapeFrameCount(binding.Index), Is.EqualTo(1));
                        Assert.That(skin.sharedMesh.GetBlendShapeFrameWeight(binding.Index, 0), Is.EqualTo(100));
                    }
                }
                Assert.That(fixture.Mesh.GetBlendShapeFrameCount(1), Is.EqualTo(curve == "explicitNeutral" ? 3 : 2));
                Assert.That(fixture.Mesh.GetBlendShapeFrameWeight(1, 0), Is.EqualTo(-50));
                if (curve == "explicitNeutral") Assert.That(fixture.Mesh.GetBlendShapeFrameWeight(1, 1), Is.Zero);
                Assert.That(sourceSkins.All(skin => skin.sharedMesh == fixture.Mesh && skin.GetBlendShapeWeight(0) == 0 && skin.GetBlendShapeWeight(1) == rest), Is.True);
                Assert.That(fixture.Mesh.vertices[0], Is.EqualTo(new Vector3(-.1f, 1.7f, .08f)));
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); Object.DestroyImmediate(baked); Object.DestroyImmediate(clip); Object.DestroyImmediate(vrm); }
        }

        private static void AddTrackingDelta(Mesh mesh, string name, float amount = .02f) =>
            mesh.AddBlendShapeFrame(name, 100, Enumerable.Repeat(Vector3.up * amount, mesh.vertexCount).ToArray(), null, null);

        [TestCase("missing", false)]
        [TestCase("missing", true)]
        [TestCase("invalid", false)]
        [TestCase("invalid", true)]
        [TestCase("scoped", false)]
        [TestCase("scoped", true)]
        public async Task MaterialCoverageWithMalformedMorphKeepsItsDeclaredNodeScopeInRealGlb(string scope, bool fullLilToon)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            Vrm10Instance imported = null;
            try
            {
                foreach (var name in new[] { "LipFunnel", "LipFunnelUpperLeft", "MouthClosed" })
                    fixture.Mesh.AddBlendShapeFrame(name, 100, Enumerable.Repeat(Vector3.up * .02f, fixture.Mesh.vertexCount).ToArray(), null, null);
                foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = Shader.Find("lilToon");
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Mixed material scope", "Tests",
                    exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null);
                var glb = GlbDocument.Read(bytes);
                var nodes = ((List<object>)glb.Json["nodes"]).Cast<Dictionary<string, object>>().ToArray();
                var frontIndex = Array.FindIndex(nodes, node => node.TryGetValue("name", out var name) && (string)name == "Front" && node.ContainsKey("mesh"));
                var backIndex = Array.FindIndex(nodes, node => node.TryGetValue("name", out var name) && (string)name == "Back" && node.ContainsKey("mesh"));
                Assert.That(frontIndex, Is.GreaterThanOrEqualTo(0)); Assert.That(backIndex, Is.GreaterThanOrEqualTo(0));
                var metadata = (Dictionary<string, object>)((Dictionary<string, object>)glb.Json["extensions"])["VRMC_vrm"];
                var custom = (Dictionary<string, object>)((Dictionary<string, object>)metadata["expressions"])["custom"];
                foreach (var key in custom.Keys.Where(key => key.StartsWith("UE/", StringComparison.Ordinal)).ToArray()) custom.Remove(key);
                var morph = new Dictionary<string, object> { ["index"] = 2L, ["weight"] = .4 };
                if (scope != "missing") morph["node"] = scope == "scoped" ? (long)frontIndex : (long)nodes.Length + 10;
                const string authoredName = "UE/LipFunnelUpperLeft";
                custom[authoredName] = new Dictionary<string, object> {
                    ["morphTargetBinds"] = new List<object> { morph }, ["isBinary"] = false,
                    ["materialColorBinds"] = new List<object> { new Dictionary<string, object> {
                        ["material"] = 0L, ["type"] = "color", ["targetValue"] = new List<object> { .1, .2, .3, 1.0 } } }
                };
                var warnings = new List<string>();
                bytes = VrmUnifiedExpressions.Add(glb.Write(), warnings);
                var output = GlbDocument.Read(bytes);
                var outputVrm = (Dictionary<string, object>)((Dictionary<string, object>)output.Json["extensions"])["VRMC_vrm"];
                var routes = (Dictionary<string, object>)((Dictionary<string, object>)outputVrm["expressions"])["custom"];
                var retained = (Dictionary<string, object>)routes[authoredName];
                var retainedMorph = (Dictionary<string, object>)((List<object>)retained["morphTargetBinds"]).Single();
                Assert.That(retainedMorph.ContainsKey("node"), Is.EqualTo(scope != "missing"));
                if (scope != "missing") Assert.That(retainedMorph["node"], Is.EqualTo(morph["node"]));
                Assert.That(((List<object>)retained["materialColorBinds"]).Count, Is.EqualTo(1));
                Assert.That(warnings.Any(message => message.Contains("既存")), Is.EqualTo(scope != "scoped"));
                Assert.That(routes.ContainsKey("UE/LipFunnel"), Is.EqualTo(scope == "scoped"));
                if (scope == "scoped")
                {
                    var aggregate = (List<object>)((Dictionary<string, object>)routes["UE/LipFunnel"])["morphTargetBinds"];
                    Assert.That(aggregate.Count, Is.EqualTo(1));
                    Assert.That(((Dictionary<string, object>)aggregate.Single())["node"], Is.EqualTo((long)backIndex));
                }
                // The exporter intentionally preserves malformed declarations.
                // Remove only that malformed portion for the native playback
                // probe after proving retained metadata and no raw bypass.
                else retained.Remove("morphTargetBinds");
                imported = await Vrm10.LoadBytesAsync(output.Write(), canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                var authored = imported.Vrm.Expression.CustomClips.Single(clip => clip.name == authoredName);
                Assert.That(authored.MaterialColorBindings.Length, Is.EqualTo(1));
                Assert.That(authored.MorphTargetBindings.Length, Is.EqualTo(scope == "scoped" ? 1 : 0));
                imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(authoredName), .5f);
                imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom("UE/MouthClosed"), .3f);
                if (scope == "scoped") imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom("UE/LipFunnel"), .6f);
                imported.Runtime.Process();
                foreach (var path in new[] { "Front", "Back" })
                {
                    var skin = imported.transform.Find(path).GetComponent<SkinnedMeshRenderer>();
                    Assert.That(skin.GetBlendShapeWeight(skin.sharedMesh.GetBlendShapeIndex("LipFunnel")), Is.EqualTo(scope == "scoped" && path == "Back" ? 60 : 0).Within(.001));
                    Assert.That(skin.GetBlendShapeWeight(skin.sharedMesh.GetBlendShapeIndex("MouthClosed")), Is.EqualTo(30).Within(.001));
                    Assert.That(skin.GetBlendShapeWeight(skin.sharedMesh.GetBlendShapeIndex("LipFunnelUpperLeft")), Is.EqualTo(scope == "scoped" && path == "Front" ? 20 : 0).Within(.001));
                }
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); }
        }

        [TestCase("Front")]
        [TestCase("missing")]
        [TestCase(null)]
        public void MixedMaterialPreparationUsesOnlyResolvableRendererScopes(string path)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var vrm = ScriptableObject.CreateInstance<VRM10Object>();
            var clip = ScriptableObject.CreateInstance<VRM10Expression>();
            var trimmed = Object.Instantiate(fixture.Mesh);
            try
            {
                fixture.Mesh.AddBlendShapeFrame("LipFunnel", 100, new Vector3[fixture.Mesh.vertexCount], null, null);
                fixture.Mesh.AddBlendShapeFrame("LipFunnelUpperLeft", 100, new Vector3[fixture.Mesh.vertexCount], null, null);
                fixture.Mesh.AddBlendShapeFrame("MouthClosed", 100, new Vector3[fixture.Mesh.vertexCount], null, null);
                clip.name = "UE/LipFunnelUpperLeft";
                clip.MorphTargetBindings = new[] { new MorphTargetBinding(path, 2, .4f) };
                clip.MaterialColorBindings = new[] { new MaterialColorBinding {
                    MaterialName = fixture.Skins[0].sharedMaterial.name, BindType = MaterialColorType.color, TargetValue = Color.red } };
                vrm.Expression.CustomClips.Add(clip); fixture.Copy.AddComponent<Vrm10Instance>().Vrm = vrm;
                var guard = new UnifiedExpressionPreparation(fixture.Copy);
                trimmed.AddBlendShapeFrame("LipFunnelUpperLeft", 100, new Vector3[trimmed.vertexCount], null, null);
                trimmed.AddBlendShapeFrame("MouthClosed", 100, new Vector3[trimmed.vertexCount], null, null);
                fixture.Copy.transform.Find("Front").GetComponent<SkinnedMeshRenderer>().sharedMesh = trimmed;
                Assert.DoesNotThrow(() => guard.Verify());
                fixture.Copy.transform.Find("Back").GetComponent<SkinnedMeshRenderer>().sharedMesh = trimmed;
                if (path == "Front") Assert.Throws<InvalidOperationException>(() => guard.Verify(), "The uncovered renderer keeps aggregate coverage when the authored route has one valid renderer scope.");
                else Assert.DoesNotThrow(() => guard.Verify(), "Unresolvable morph paths cannot defeat global material coverage.");
            }
            finally { Object.DestroyImmediate(trimmed); Object.DestroyImmediate(clip); Object.DestroyImmediate(vrm); }
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

        [TestCase("color", false, false)]
        [TestCase("color", false, true)]
        [TestCase("color", true, false)]
        [TestCase("color", true, true)]
        [TestCase("emissionColor", false, false)]
        [TestCase("emissionColor", false, true)]
        [TestCase("emissionColor", true, false)]
        [TestCase("emissionColor", true, true)]
        [TestCase("shadeColor", false, false)]
        [TestCase("shadeColor", false, true)]
        [TestCase("shadeColor", true, false)]
        [TestCase("shadeColor", true, true)]
        [TestCase("matcapColor", false, false)]
        [TestCase("matcapColor", false, true)]
        [TestCase("matcapColor", true, false)]
        [TestCase("matcapColor", true, true)]
        [TestCase("rimColor", false, false)]
        [TestCase("rimColor", false, true)]
        [TestCase("rimColor", true, false)]
        [TestCase("rimColor", true, true)]
        [TestCase("outlineColor", false, false)]
        [TestCase("outlineColor", false, true)]
        [TestCase("outlineColor", true, false)]
        [TestCase("outlineColor", true, true)]
        [TestCase("uv", false, false)]
        [TestCase("uv", false, true)]
        [TestCase("uv", true, false)]
        [TestCase("uv", true, true)]
        public async Task MaterialEndpointEvidenceMatchesActualImportedProxy(string kind, bool moving, bool fullLilToon)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var vrm = ScriptableObject.CreateInstance<VRM10Object>();
            var clip = ScriptableObject.CreateInstance<VRM10Expression>();
            Vrm10Instance imported = null;
            var texture = new Texture2D(2, 2); texture.SetPixels(Enumerable.Repeat(Color.white, 4).ToArray()); texture.Apply();
            try
            {
                AddTrackingDelta(fixture.Mesh, "JawOpen");
                var sourceMaterial = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()[0].sharedMaterial;
                sourceMaterial.shader = Shader.Find("lilToon");
                sourceMaterial.SetColor("_Color", new Color(.5f, .3f, .2f, .6f));
                sourceMaterial.SetTexture("_MainTex", texture);
                sourceMaterial.SetTextureScale("_MainTex", new Vector2(2, 3)); sourceMaterial.SetTextureOffset("_MainTex", new Vector2(.2f, .4f));
                sourceMaterial.SetFloat("_UseEmission", 1); sourceMaterial.SetFloat("_EmissionBlend", .5f);
                sourceMaterial.SetColor("_EmissionColor", new Color(.5f, .3f, .2f, 1));
                sourceMaterial.SetFloat("_UseMatCap", 1); sourceMaterial.SetFloat("_MatCapBlend", .4f);
                sourceMaterial.SetColor("_MatCapColor", new Color(.6f, .3f, .2f, 1));
                sourceMaterial.SetFloat("_UseRim", 1); sourceMaterial.SetColor("_RimColor", new Color(.2f, .3f, .4f, 1));
                sourceMaterial.SetColor("_OutlineColor", new Color(.2f, .5f, .3f, 1));
                clip.name = "UE/MouthClosed";
                Vector4 target;
                string property = null;
                if (kind == "uv")
                {
                    var scale = sourceMaterial.mainTextureScale; var offset = sourceMaterial.mainTextureOffset;
                    target = new Vector4(scale.x, scale.y, offset.x + (moving ? .3f : 0), offset.y);
                    clip.MaterialUVBindings = new[] { new MaterialUVBinding { MaterialName = sourceMaterial.name,
                        Scaling = new Vector2(target.x, target.y), Offset = new Vector2(target.z, target.w) } };
                }
                else
                {
                    var type = (MaterialColorType)Enum.Parse(typeof(MaterialColorType), kind);
                    target = UniVrmOneClickExporter.FallbackColor(sourceMaterial, type);
                    if (type != MaterialColorType.color) target.w = 1;
                    if (moving) target.x += .1f;
                    clip.MaterialColorBindings = new[] { new MaterialColorBinding { MaterialName = sourceMaterial.name, BindType = type, TargetValue = target } };
                    property = UnifiedExpressionPreparation.ColorProperty(type);
                }
                var originalColor = sourceMaterial.GetColor("_Color"); var originalScale = sourceMaterial.mainTextureScale; var originalOffset = sourceMaterial.mainTextureOffset;
                vrm.Expression.CustomClips.Add(clip); fixture.Source.AddComponent<Vrm10Instance>().Vrm = vrm;
                Assert.That(UnifiedExpressionPreparation.HasUsableEvidence(fixture.Source), Is.EqualTo(moving));
                if (!moving) Assert.Throws<InvalidOperationException>(() => UniVrmOneClickExporter.Export(fixture.Source, "Inert material safeguard", "Tests",
                    exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null));
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Material endpoint proxy", "Tests",
                    exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null,
                    blinkOptions: moving ? null : new BlinkExportOptions { Mode = BlinkExportMode.None });
                Assert.That(VrmUnifiedExpressions.HasUsableEvidence(bytes), Is.EqualTo(moving));
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                var retained = imported.Vrm.Expression.CustomClips.Single(value => value.name == clip.name);
                Assert.That(retained.MaterialColorBindings.Length + retained.MaterialUVBindings.Length, Is.EqualTo(1));
                Assert.That(imported.Vrm.Expression.CustomClips.Any(value => value.name == "UE/JawOpen"), Is.EqualTo(moving));
                var material = imported.GetComponentsInChildren<Renderer>().SelectMany(renderer => renderer.sharedMaterials).First(value => value.name == sourceMaterial.name);
                var baseline = kind == "uv" ? new Vector4(material.mainTextureScale.x, material.mainTextureScale.y, material.mainTextureOffset.x, material.mainTextureOffset.y) : material.GetVector(property);
                foreach (var coefficient in new[] { 0f, .5f, 1f })
                {
                    imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(clip.name), coefficient); imported.Runtime.Process();
                    var actual = kind == "uv" ? new Vector4(material.mainTextureScale.x, material.mainTextureScale.y, material.mainTextureOffset.x, material.mainTextureOffset.y) : material.GetVector(property);
                    Assert.That(Vector4.Distance(actual, baseline + (target - baseline) * coefficient), Is.LessThan(.00005f), "Actual pinned material merger endpoint");
                }
                Assert.That(sourceMaterial.GetColor("_Color"), Is.EqualTo(originalColor));
                Assert.That(sourceMaterial.mainTextureScale, Is.EqualTo(originalScale)); Assert.That(sourceMaterial.mainTextureOffset, Is.EqualTo(originalOffset));
                Assert.That(sourceMaterial.GetTexture("_MainTex"), Is.SameAs(texture));
                if (kind == "uv") Assert.That(clip.MaterialUVBindings.Single().ScalingOffset, Is.EqualTo(target));
                else Assert.That(clip.MaterialColorBindings.Single().TargetValue, Is.EqualTo(target));
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                Object.DestroyImmediate(texture); Object.DestroyImmediate(clip); Object.DestroyImmediate(vrm);
            }
        }

        [TestCase("reorder")]
        [TestCase("replacement")]
        [TestCase("shared")]
        [TestCase("introduced")]
        public void DuplicateMaterialNamesKeepTheOriginallySelectedRendererSlot(string change)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var vrm = ScriptableObject.CreateInstance<VRM10Object>(); var clip = ScriptableObject.CreateInstance<VRM10Expression>();
            var first = new Material(Shader.Find("VRM10/MToon10")) { name = "Repeated UE Material" };
            var second = new Material(first) { name = first.name }; Material replacement = null; GameObject introduced = null;
            try
            {
                first.SetColor("_Color", Color.white); second.SetColor("_Color", Color.blue);
                fixture.Skins[0].sharedMaterial = first; fixture.Skins[1].sharedMaterial = change == "shared" || change == "introduced" ? first : second;
                clip.name = "UE/MouthClosed"; clip.MaterialColorBindings = new[] { new MaterialColorBinding {
                    MaterialName = first.name, BindType = MaterialColorType.color, TargetValue = Color.red } };
                vrm.Expression.CustomClips.Add(clip); fixture.Copy.AddComponent<Vrm10Instance>().Vrm = vrm;
                var guard = new UnifiedExpressionPreparation(fixture.Copy);
                Assert.That(guard.SupportsUnified, Is.True);
                if (change == "replacement")
                {
                    replacement = new Material(first) { name = first.name }; fixture.Skins[0].sharedMaterial = replacement;
                }
                else if (change == "introduced")
                {
                    introduced = new GameObject("NDMF introduced material renderer"); introduced.transform.SetParent(fixture.Copy.transform, false);
                    var renderer = introduced.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = fixture.Mesh; renderer.sharedMaterial = second;
                    introduced.transform.SetSiblingIndex(0);
                }
                else fixture.Skins[1].transform.SetSiblingIndex(0);
                Assert.That(UnifiedExpressionPreparation.HasUsableEvidence(fixture.Copy), Is.True, "Both competing material endpoints still move.");
                if (change == "reorder" || change == "introduced") Assert.Throws<InvalidOperationException>(() => guard.Verify(), "A different first same-name material cannot silently replace the selected authored target.");
                else Assert.DoesNotThrow(() => guard.Verify(), "Owned same-slot material copies and the same shared instance retain their authored target.");
                Assert.That(first.GetColor("_Color"), Is.EqualTo(Color.white)); Assert.That(second.GetColor("_Color"), Is.EqualTo(Color.blue));
                Assert.That(clip.MaterialColorBindings.Single().MaterialName, Is.EqualTo(first.name));
            }
            finally
            {
                if (introduced != null) Object.DestroyImmediate(introduced);
                if (replacement != null) Object.DestroyImmediate(replacement);
                Object.DestroyImmediate(first); Object.DestroyImmediate(second); Object.DestroyImmediate(clip); Object.DestroyImmediate(vrm);
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task StandardEmissionEvidenceUsesItsActualGammaProxy(bool moving, bool fullLilToon)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var vrm = ScriptableObject.CreateInstance<VRM10Object>(); var clip = ScriptableObject.CreateInstance<VRM10Expression>();
            var material = new Material(Shader.Find("Standard")) { name = "Standard UE Material" };
            Vrm10Instance imported = null;
            try
            {
                AddTrackingDelta(fixture.Mesh, "JawOpen");
                var skins = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
                skins[0].sharedMaterial.shader = Shader.Find("lilToon"); skins[1].sharedMaterial = material;
                var baseline = new Color(.5f, .3f, .2f, 1);
                material.SetColor("_EmissionColor", baseline); material.EnableKeyword("_EMISSION");
                Vector4 target = baseline; if (moving) target.x += .1f;
                clip.name = "UE/MouthClosed"; clip.MaterialColorBindings = new[] { new MaterialColorBinding {
                    MaterialName = material.name, BindType = MaterialColorType.emissionColor, TargetValue = target } };
                vrm.Expression.CustomClips.Add(clip); fixture.Source.AddComponent<Vrm10Instance>().Vrm = vrm;
                Assert.That(UnifiedExpressionPreparation.HasUsableEvidence(fixture.Source), Is.EqualTo(moving));
                if (!moving) Assert.Throws<InvalidOperationException>(() => UniVrmOneClickExporter.Export(fixture.Source, "Inert Standard emission", "Tests",
                    exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null));
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Standard emission gamma", "Tests",
                    exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null,
                    blinkOptions: moving ? null : new BlinkExportOptions { Mode = BlinkExportMode.None });
                Assert.That(VrmUnifiedExpressions.HasUsableEvidence(bytes), Is.EqualTo(moving));
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                var proxy = imported.GetComponentsInChildren<Renderer>().SelectMany(renderer => renderer.sharedMaterials).First(value => value.name == material.name);
                Assert.That(proxy.shader.name, Is.EqualTo("Standard"));
                Assert.That(Vector4.Distance(proxy.GetVector("_EmissionColor"), baseline), Is.LessThan(.00005f));
                Assert.That(imported.Vrm.Expression.CustomClips.Any(value => value.name == "UE/JawOpen"), Is.EqualTo(moving));
                foreach (var coefficient in new[] { 0f, .5f, 1f })
                {
                    imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(clip.name), coefficient); imported.Runtime.Process();
                    Assert.That(Vector4.Distance(proxy.GetVector("_EmissionColor"), (Vector4)baseline + (target - (Vector4)baseline) * coefficient), Is.LessThan(.00005f));
                }
                Assert.That(material.GetColor("_EmissionColor"), Is.EqualTo(baseline));
                Assert.That(material.IsKeywordEnabled("_EMISSION"), Is.True);
                Assert.That(clip.MaterialColorBindings.Single().TargetValue, Is.EqualTo(target));
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                Object.DestroyImmediate(material); Object.DestroyImmediate(clip); Object.DestroyImmediate(vrm);
            }
        }

        [TestCase("shared", false, false)]
        [TestCase("shared", false, true)]
        [TestCase("shared", true, false)]
        [TestCase("shared", true, true)]
        [TestCase("hdr", false, false)]
        [TestCase("hdr", false, true)]
        [TestCase("hdr", true, false)]
        [TestCase("hdr", true, true)]
        public async Task EmissionEvidenceUsesTheActualSuppressionOptions(string kind, bool suppressed, bool fullLilToon)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var vrm = ScriptableObject.CreateInstance<VRM10Object>(); var clip = ScriptableObject.CreateInstance<VRM10Expression>();
            Vrm10Instance imported = null;
            var main = new Texture2D(2, 2); main.SetPixels(Enumerable.Repeat(Color.white, 4).ToArray()); main.Apply();
            var emission = new Texture2D(2, 2); emission.SetPixels(Enumerable.Repeat(Color.white, 4).ToArray()); emission.Apply();
            try
            {
                AddTrackingDelta(fixture.Mesh, "JawOpen");
                var material = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()[0].sharedMaterial;
                material.shader = Shader.Find("lilToon"); material.SetTexture("_MainTex", main);
                material.SetTexture("_EmissionMap", kind == "shared" ? main : emission);
                material.SetFloat("_UseEmission", 1); material.SetFloat("_EmissionBlend", 1);
                var originalColor = kind == "shared" ? new Color(.5f, .2f, .1f, 1) : new Color(2, 1, .5f, 1);
                material.SetColor("_EmissionColor", originalColor);
                Vector4 target = UniVrmOneClickExporter.FallbackColor(material, MaterialColorType.emissionColor); target.w = 1;
                var sharedSuppressed = kind == "shared" && suppressed; var hdrSuppressed = kind == "hdr" && suppressed;
                clip.name = "UE/MouthClosed"; clip.MaterialColorBindings = new[] { new MaterialColorBinding {
                    MaterialName = material.name, BindType = MaterialColorType.emissionColor, TargetValue = target } };
                vrm.Expression.CustomClips.Add(clip); fixture.Source.AddComponent<Vrm10Instance>().Vrm = vrm;
                Assert.That(UnifiedExpressionPreparation.HasUsableEvidence(fixture.Source,
                    suppressSharedTextureEmission: sharedSuppressed, suppressHdrTextureEmission: hdrSuppressed), Is.EqualTo(suppressed));
                if (!suppressed) Assert.Throws<InvalidOperationException>(() => UniVrmOneClickExporter.Export(fixture.Source, "Unsuppressed inert emission safeguard", "Tests",
                    suppressSharedTextureEmission: sharedSuppressed, suppressHdrTextureEmission: hdrSuppressed,
                    exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null));
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Emission suppression endpoint", "Tests",
                    suppressSharedTextureEmission: sharedSuppressed, suppressHdrTextureEmission: hdrSuppressed,
                    exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null,
                    blinkOptions: suppressed ? null : new BlinkExportOptions { Mode = BlinkExportMode.None });
                Assert.That(VrmUnifiedExpressions.HasUsableEvidence(bytes), Is.EqualTo(suppressed));
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                var retained = imported.Vrm.Expression.CustomClips.Single(value => value.name == clip.name);
                Assert.That(Vector4.Distance(retained.MaterialColorBindings.Single().TargetValue, target), Is.LessThan(.00001f));
                Assert.That(imported.Vrm.Expression.CustomClips.Any(value => value.name == "UE/JawOpen"), Is.EqualTo(suppressed));
                var proxy = imported.GetComponentsInChildren<Renderer>().SelectMany(renderer => renderer.sharedMaterials).First(value => value.name == material.name);
                var baseline = suppressed ? new Vector4(0, 0, 0, 1) : target;
                Assert.That(Vector4.Distance(proxy.GetVector("_EmissionColor"), baseline), Is.LessThan(.00001f));
                foreach (var coefficient in new[] { 0f, .5f, 1f })
                {
                    imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(clip.name), coefficient); imported.Runtime.Process();
                    Assert.That(Vector4.Distance(proxy.GetVector("_EmissionColor"), baseline + (target - baseline) * coefficient), Is.LessThan(.00001f));
                }
                Assert.That(material.GetColor("_EmissionColor"), Is.EqualTo(originalColor)); Assert.That(material.GetFloat("_UseEmission"), Is.EqualTo(1));
                Assert.That(material.GetTexture("_MainTex"), Is.SameAs(main)); Assert.That(material.GetTexture("_EmissionMap"), Is.SameAs(kind == "shared" ? main : emission));
                Assert.That(clip.MaterialColorBindings.Single().TargetValue, Is.EqualTo(target));
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                Object.DestroyImmediate(main); Object.DestroyImmediate(emission); Object.DestroyImmediate(clip); Object.DestroyImmediate(vrm);
            }
        }

        [TestCase("color")]
        [TestCase("uv")]
        [TestCase("cancellation")]
        public void PreparationRejectsLossOfPreviouslyMovingMaterialEndpoint(string kind)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var vrm = ScriptableObject.CreateInstance<VRM10Object>(); var clip = ScriptableObject.CreateInstance<VRM10Expression>();
            try
            {
                AddTrackingDelta(fixture.Mesh, "EyeClosedLeft");
                clip.name = "UE/MouthClosed";
                var material = fixture.Skins[0].sharedMaterial;
                if (kind == "uv") clip.MaterialUVBindings = new[] { new MaterialUVBinding { MaterialName = material.name, Scaling = Vector2.one, Offset = Vector2.up } };
                else clip.MaterialColorBindings = new[] { new MaterialColorBinding { MaterialName = material.name, BindType = MaterialColorType.color, TargetValue = Color.red } };
                vrm.Expression.CustomClips.Add(clip); fixture.Copy.AddComponent<Vrm10Instance>().Vrm = vrm;
                var guard = new UnifiedExpressionPreparation(fixture.Copy);
                if (kind == "uv") material.mainTextureOffset = Vector2.up;
                else if (kind == "color") material.SetColor("_Color", Color.red);
                else clip.MaterialColorBindings = new[] {
                    new MaterialColorBinding { MaterialName = material.name, BindType = MaterialColorType.color, TargetValue = Color.red },
                    new MaterialColorBinding { MaterialName = material.name, BindType = MaterialColorType.color, TargetValue = new Vector4(1,2,2,1) } };
                Assert.That(UnifiedExpressionPreparation.HasUsableEvidence(fixture.Copy), Is.True, "Other moving UE survives the material loss.");
                Assert.Throws<InvalidOperationException>(() => guard.Verify());
            }
            finally { Object.DestroyImmediate(clip); Object.DestroyImmediate(vrm); }
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void FallbackBakingCannotReplaceMovingMaterialEvidenceWithAnInertBase(bool fullLilToon, bool otherUnifiedSurvives = false)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var vrm = ScriptableObject.CreateInstance<VRM10Object>(); var clip = ScriptableObject.CreateInstance<VRM10Expression>();
            try
            {
                AddTrackingDelta(fixture.Mesh, "JawOpen");
                if (otherUnifiedSurvives) AddTrackingDelta(fixture.Mesh, "EyeClosedLeft");
                var material = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()[0].sharedMaterial;
                material.shader = Shader.Find("lilToon"); material.SetColor("_Color", Color.red);
                // Color adjustment invokes the existing actual layer baker,
                // which writes the tint into pixels and makes _Color white.
                material.SetVector("_MainTexHSVG", new Vector4(.1f, 1, 1, 1));
                Assert.That(LilToonMainTextureBaker.NeedsBake(material), Is.True);
                clip.name = "UE/MouthClosed"; clip.MaterialColorBindings = new[] { new MaterialColorBinding {
                    MaterialName = material.name, BindType = MaterialColorType.color, TargetValue = Color.white } };
                vrm.Expression.CustomClips.Add(clip); fixture.Source.AddComponent<Vrm10Instance>().Vrm = vrm;
                Assert.That(UnifiedExpressionPreparation.HasUsableEvidence(fixture.Source), Is.True);
                var error = Assert.Throws<InvalidOperationException>(() => UniVrmOneClickExporter.Export(fixture.Source, "Baked material safeguard", "Tests",
                    exporterVersion: fullLilToon ? "0.11.5" : null, lilToonVersion: fullLilToon ? "2.3.4" : null));
                Assert.That(error.Message, Is.EqualTo(UnifiedExpressionPreparation.LostTracking));
                Assert.That(material.GetColor("_Color"), Is.EqualTo(Color.red));
                Assert.That(material.GetVector("_MainTexHSVG").x, Is.EqualTo(.1f));
                Assert.That(clip.MaterialColorBindings.Single().TargetValue, Is.EqualTo((Vector4)Color.white));
            }
            finally { Object.DestroyImmediate(clip); Object.DestroyImmediate(vrm); }
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
