using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UniGLTF.Extensions.VRMC_vrm;
using UniVRM10;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class NeutralShapeEndpointTests
    {
        sealed class Fixture : IDisposable
        {
            internal readonly GameObject Source = new GameObject("Avatar");
            internal readonly Mesh Mesh = new Mesh { name = "Authored face" };
            internal readonly VRM10Object Vrm = ScriptableObject.CreateInstance<VRM10Object>();
            internal readonly VRM10Expression Clip = ScriptableObject.CreateInstance<VRM10Expression>();
            internal readonly List<Mesh> Owned = new List<Mesh>();
            internal readonly List<Object> Additional = new List<Object>();
            internal readonly SkinnedMeshRenderer Original;
            internal GameObject Copy;
            internal SkinnedMeshRenderer Skin => Copy.transform.Find("Face").GetComponent<SkinnedMeshRenderer>();
            internal Fixture(float rest = 75, float endpoint = .25f, bool overHundredFrame = false)
            {
                Mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
                Mesh.triangles = new[] { 0, 1, 2 };
                Mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward };
                Mesh.tangents = Enumerable.Repeat(new Vector4(1, 0, 0, -1), 3).ToArray();
                Vector3[] Delta(Vector3 value) => new[] { value, Vector3.zero, Vector3.zero };
                Mesh.AddBlendShapeFrame("Multi", 50, Delta(Vector3.right * 2), Delta(Vector3.right), Delta(Vector3.up));
                Mesh.AddBlendShapeFrame("Multi", overHundredFrame ? 200 : 100,
                    Delta(Vector3.right * (overHundredFrame ? 8 : 6)), Delta(Vector3.right * 3), Delta(Vector3.up * 3));
                Mesh.AddBlendShapeFrame("Customization", 100, Delta(Vector3.up * 4), null, null);
                var face = new GameObject("Face"); face.transform.SetParent(Source.transform, false);
                Original = face.AddComponent<SkinnedMeshRenderer>(); Original.sharedMesh = Mesh;
                Original.SetBlendShapeWeight(0, rest); Original.SetBlendShapeWeight(1, 25);
                Clip.name = "Authored partial";
                Clip.MorphTargetBindings = new[] { new MorphTargetBinding("Face", 0, endpoint) };
                Vrm.Expression.Happy = Clip;
                Source.AddComponent<Vrm10Instance>().Vrm = Vrm;
                Copy = Object.Instantiate(Source);
            }
            internal void Preserve() => AvatarBaseShape.Preserve(Copy, Copy, Owned, null);
            internal Vector3 Endpoint(VRM10Expression clip)
            {
                var result = Skin.sharedMesh.vertices[0];
                foreach (var binding in clip.MorphTargetBindings.GroupBy(binding => binding.Index).Select(group => group.First()))
                {
                    var delta = new Vector3[Skin.sharedMesh.vertexCount];
                    Skin.sharedMesh.GetBlendShapeFrameVertices(binding.Index, 0, delta, null, null);
                    result += delta[0] * binding.Weight;
                }
                return result;
            }
            public void Dispose()
            {
                Object.DestroyImmediate(Copy); Object.DestroyImmediate(Source);
                foreach (var mesh in Owned) if (mesh != null) Object.DestroyImmediate(mesh);
                foreach (var asset in Additional) if (asset != null) Object.DestroyImmediate(asset);
                Object.DestroyImmediate(Clip); Object.DestroyImmediate(Vrm); Object.DestroyImmediate(Mesh);
            }
        }

        [Test]
        public void PreparedSnapshotRetainsIdentityMeshAndExplicitZeroWithoutChangingSource()
        {
            using var fixture = new Fixture();
            NeutralShapeSnapshot.Apply(fixture.Copy, new[] {
                new VrChatExpressionMenu.MorphValue { Path = "Face", Shape = "Multi", Weight = 0 },
                new VrChatExpressionMenu.MorphValue { Path = "Face", Shape = "Customization", Weight = 80 }
            });
            var snapshot = NeutralShapeSnapshot.Capture(fixture.Copy);
            var state = snapshot.Get(fixture.Skin);
            fixture.Preserve();
            Assert.That(state.Renderer, Is.SameAs(fixture.Skin));
            Assert.That(state.OriginalMesh, Is.SameAs(fixture.Mesh));
            Assert.That(state.Weights, Is.EqualTo(new[] { 0f, 80f }));
            Assert.That(fixture.Skin.sharedMesh, Is.Not.SameAs(state.OriginalMesh));
            Assert.That(fixture.Original.GetBlendShapeWeight(0), Is.EqualTo(75));
            Assert.That(fixture.Original.GetBlendShapeWeight(1), Is.EqualTo(25));
            Assert.That(fixture.Mesh.vertices[0], Is.EqualTo(Vector3.zero));
        }

        [TestCase(0f)]
        [TestCase(.25f)]
        public void InvalidAuthorIndexCannotAddressNewlyAppendedBlinkOrExpressionChannel(float weight)
        {
            using var fixture = new Fixture();
            var originallyInvalid = fixture.Mesh.blendShapeCount;
            fixture.Clip.MorphTargetBindings = new[] { new MorphTargetBinding("Face", originallyInvalid, weight) };
            using var session = AuthoredExpressionEndpoints.Capture(fixture.Copy, NeutralShapeSnapshot.Capture(fixture.Copy));
            fixture.Preserve();
            fixture.Skin.sharedMesh.AddBlendShapeFrame("Generated closure", 100,
                new[] { Vector3.down, Vector3.zero, Vector3.zero }, null, null);
            Assert.That(fixture.Skin.sharedMesh.GetBlendShapeIndex("Generated closure"), Is.EqualTo(originallyInvalid));
            session.Bake(fixture.Owned);
            var clip = fixture.Copy.GetComponent<Vrm10Instance>().Vrm.Expression.Happy;
            Assert.That(clip.MorphTargetBindings.Single().Index, Is.EqualTo(int.MaxValue));
            Assert.That(clip.MorphTargetBindings.Single().Weight, Is.EqualTo(weight));
            Assert.That(AvatarBaseShape.HasUsableMorphEndpoint(fixture.Skin.sharedMesh,
                clip.MorphTargetBindings.Single().Index, 0, weight * 100), Is.False);
            Assert.That(fixture.Clip.MorphTargetBindings.Single().Index, Is.EqualTo(originallyInvalid));
            Assert.That(fixture.Mesh.blendShapeCount, Is.EqualTo(originallyInvalid));
            Assert.That(fixture.Source.GetComponent<Vrm10Instance>().Vrm, Is.SameAs(fixture.Vrm));
        }

        [Test]
        public void DuplicateRendererPathsCannotSilentlyApplySampleToTheFirstSibling()
        {
            using var fixture = new Fixture();
            var duplicate = Object.Instantiate(fixture.Skin.gameObject, fixture.Copy.transform); duplicate.name = "Face";
            var snapshot = NeutralShapeSnapshot.Capture(fixture.Copy);
            Assert.That(snapshot.Renderers.Count, Is.EqualTo(2));
            Assert.That(snapshot.Get(duplicate.GetComponent<SkinnedMeshRenderer>()).OriginalMesh, Is.SameAs(fixture.Mesh));
            Assert.Throws<InvalidOperationException>(() => NeutralShapeSnapshot.Apply(fixture.Copy, new[] {
                new VrChatExpressionMenu.MorphValue { Path = "Face", Shape = "Multi", Weight = 0 }
            }));
            Assert.That(fixture.Skin.GetBlendShapeWeight(0), Is.EqualTo(75));
        }

        [Test]
        public void AuthoredPartialEndpointUsesSourceMultiFrameCurveAndKeepsUnboundNeutral()
        {
            using var fixture = new Fixture();
            fixture.Clip.IsBinary = true; fixture.Clip.OverrideBlink = ExpressionOverrideType.block;
            fixture.Clip.OverrideLookAt = ExpressionOverrideType.blend; fixture.Clip.OverrideMouth = ExpressionOverrideType.block;
            fixture.Clip.MaterialColorBindings = new[] { new MaterialColorBinding {
                MaterialName = "Face material", BindType = MaterialColorType.color, TargetValue = Color.red } };
            fixture.Clip.MaterialUVBindings = new[] { new MaterialUVBinding {
                MaterialName = "Face material", Scaling = new Vector2(2, 3), Offset = new Vector2(.2f, .3f) } };
            using var session = AuthoredExpressionEndpoints.Capture(fixture.Copy, NeutralShapeSnapshot.Capture(fixture.Copy));
            fixture.Preserve(); session.Bake(fixture.Owned);
            var clip = fixture.Copy.GetComponent<Vrm10Instance>().Vrm.Expression.Happy;
            Assert.That(clip, Is.Not.SameAs(fixture.Clip));
            Assert.That(clip.MorphTargetBindings.Single().Weight, Is.EqualTo(1));
            Assert.That(fixture.Endpoint(clip), Is.EqualTo(new Vector3(1, 1, 0)),
                "The selected absolute source25 endpoint must replace neutral75 rather than scaling the remaining range.");
            var normal = new Vector3[3]; var tangent = new Vector3[3];
            fixture.Skin.sharedMesh.GetBlendShapeFrameVertices(clip.MorphTargetBindings.Single().Index, 0, null, normal, tangent);
            Assert.That(fixture.Skin.sharedMesh.normals[0] + normal[0], Is.EqualTo(new Vector3(.5f, 0, 1)));
            var baseTangent = fixture.Skin.sharedMesh.tangents[0];
            Assert.That(new Vector3(baseTangent.x, baseTangent.y, baseTangent.z) + tangent[0], Is.EqualTo(new Vector3(1, .5f, 0)));
            Assert.That(baseTangent.w, Is.EqualTo(-1));
            Assert.That(clip.IsBinary, Is.True); Assert.That(clip.OverrideBlink, Is.EqualTo(fixture.Clip.OverrideBlink));
            Assert.That(clip.OverrideLookAt, Is.EqualTo(fixture.Clip.OverrideLookAt)); Assert.That(clip.OverrideMouth, Is.EqualTo(fixture.Clip.OverrideMouth));
            Assert.That(clip.MaterialColorBindings, Is.EqualTo(fixture.Clip.MaterialColorBindings));
            Assert.That(clip.MaterialUVBindings, Is.EqualTo(fixture.Clip.MaterialUVBindings));
            Assert.That(fixture.Clip.MorphTargetBindings.Single().Weight, Is.EqualTo(.25f));
            Assert.That(fixture.Vrm.Expression.Happy, Is.SameAs(fixture.Clip));
            Assert.That(fixture.Mesh.blendShapeCount, Is.EqualTo(2));
        }

        [Test]
        public void EndpointBakeKeepsBlinkSettingsCreatedAfterCapture()
        {
            using var fixture = new Fixture();
            using var session = AuthoredExpressionEndpoints.Capture(fixture.Copy, NeutralShapeSnapshot.Capture(fixture.Copy));
            var blinkVrm = Object.Instantiate(fixture.Vrm); fixture.Additional.Add(blinkVrm);
            var blink = ScriptableObject.CreateInstance<VRM10Expression>(); fixture.Additional.Add(blink);
            blink.name = "Generated blink"; blink.MorphTargetBindings = new[] { new MorphTargetBinding("Face", 0, 1) };
            blinkVrm.Expression.Blink = blink; fixture.Copy.GetComponent<Vrm10Instance>().Vrm = blinkVrm;
            fixture.Preserve(); session.Bake(fixture.Owned);
            Assert.That(fixture.Copy.GetComponent<Vrm10Instance>().Vrm.Expression.Blink, Is.SameAs(blink));
            Assert.That(fixture.Vrm.Expression.Blink, Is.Null);
        }

        [TestCase(0f, 1f, 4f)]
        [TestCase(.25f, .75f, 1f)]
        public void DuplicateAuthoredBindingsKeepFirstChannelSemantics(float first, float second, float expected)
        {
            using var fixture = new Fixture();
            fixture.Clip.MorphTargetBindings = new[] { new MorphTargetBinding("Face", 0, first), new MorphTargetBinding("Face", 0, second) };
            using var session = AuthoredExpressionEndpoints.Capture(fixture.Copy, NeutralShapeSnapshot.Capture(fixture.Copy));
            fixture.Preserve(); session.Bake(fixture.Owned);
            var clip = fixture.Copy.GetComponent<Vrm10Instance>().Vrm.Expression.Happy;
            Assert.That(fixture.Endpoint(clip).x, Is.EqualTo(expected).Within(.0001f));
            if (first == 0) Assert.That(clip, Is.SameAs(fixture.Clip), "A disabled first binding cannot be re-enabled by a later duplicate.");
            Assert.That(fixture.Clip.MorphTargetBindings.Length, Is.EqualTo(2));
        }

        [Test]
        public void AuthoredZeroOnlyRemainsDisabledWhileExplicitFxZeroCanReopen()
        {
            using var fixture = new Fixture(100, 0);
            var neutral = NeutralShapeSnapshot.Capture(fixture.Copy);
            using var session = AuthoredExpressionEndpoints.Capture(fixture.Copy, neutral);
            fixture.Preserve(); session.Bake(fixture.Owned);
            var clip = fixture.Copy.GetComponent<Vrm10Instance>().Vrm.Expression.Happy;
            Assert.That(clip, Is.SameAs(fixture.Clip)); Assert.That(clip.MorphTargetBindings.Single().Weight, Is.Zero);
            Assert.That(fixture.Skin.sharedMesh.blendShapeCount, Is.EqualTo(2));
            AvatarBaseShape.AppendExpression(neutral.Get(fixture.Skin).OriginalMesh, fixture.Skin.sharedMesh,
                "Explicit FX zero", neutral.Get(fixture.Skin).Weights, new[] { 0f, 25f });
            var delta = new Vector3[3]; fixture.Skin.sharedMesh.GetBlendShapeFrameVertices(2, 0, delta, null, null);
            Assert.That(fixture.Skin.sharedMesh.vertices[0] + delta[0], Is.EqualTo(new Vector3(0, 1, 0)));
        }

        [TestCase(-25f, -1f)]
        [TestCase(0f, 0f)]
        [TestCase(150f, 10f)]
        public void ExplicitEndpointsSupportNegativeZeroAndAboveHundred(float endpoint, float expected)
        {
            using var fixture = new Fixture(); var neutral = NeutralShapeSnapshot.Capture(fixture.Copy);
            fixture.Preserve();
            var state = neutral.Get(fixture.Skin);
            AvatarBaseShape.AppendExpression(state.OriginalMesh, fixture.Skin.sharedMesh, "Absolute endpoint", state.Weights, new[] { endpoint, 25f });
            var delta = new Vector3[3]; fixture.Skin.sharedMesh.GetBlendShapeFrameVertices(2, 0, delta, null, null);
            Assert.That((fixture.Skin.sharedMesh.vertices[0] + delta[0]).x, Is.EqualTo(expected).Within(.0001f));
            Assert.That(fixture.Mesh.GetBlendShapeFrameCount(0), Is.EqualTo(2));
            Assert.That(fixture.Original.GetBlendShapeWeight(0), Is.EqualTo(75));
        }

        [Test]
        public void PartialEndpointAtNeutralRemainsInertAndExplicitZeroHasGeometry()
        {
            using var fixture = new Fixture(25, .25f);
            Assert.That(AvatarBaseShape.HasUsableMorphEndpoint(fixture.Mesh, 0, 25, 25), Is.False);
            Assert.That(AvatarBaseShape.HasUsableMorphEndpoint(fixture.Mesh, 0, 25, 0), Is.True);
            using var session = AuthoredExpressionEndpoints.Capture(fixture.Copy, NeutralShapeSnapshot.Capture(fixture.Copy));
            fixture.Preserve(); session.Bake(fixture.Owned);
            var binding = fixture.Copy.GetComponent<Vrm10Instance>().Vrm.Expression.Happy.MorphTargetBindings.Single();
            Assert.That(AvatarBaseShape.HasUsableMorphEndpoint(fixture.Skin.sharedMesh, binding.Index, 0), Is.False,
                "A valid authored declaration at neutral reserves its route without inventing expression motion.");
        }

        [TestCase(-.1f)]
        [TestCase(1.1f)]
        [TestCase(float.NaN)]
        public void MalformedAuthoredClipKeepsItsDeclarationAndCannotActivateItsValidPortion(float malformedWeight)
        {
            using var fixture = new Fixture();
            fixture.Clip.MorphTargetBindings = new[] {
                new MorphTargetBinding("Face", 0, .25f), new MorphTargetBinding("Face", 1, malformedWeight)
            };
            using var session = AuthoredExpressionEndpoints.Capture(fixture.Copy, NeutralShapeSnapshot.Capture(fixture.Copy));
            fixture.Preserve(); session.Bake(fixture.Owned);
            Assert.That(fixture.Copy.GetComponent<Vrm10Instance>().Vrm.Expression.Happy, Is.SameAs(fixture.Clip));
            Assert.That(fixture.Skin.sharedMesh.blendShapeCount, Is.EqualTo(2));
            Assert.That(fixture.Clip.MorphTargetBindings[0].Weight, Is.EqualTo(.25f));
        }

        [TestCase(-25f)]
        [TestCase(150f)]
        public void ProfileWeightsAreAbsoluteSourceEndpointsAndMapEachRendererIndependently(float rest)
        {
            using var fixture = new Fixture(rest, .5f, overHundredFrame: true);
            var second = new GameObject("Other face"); second.transform.SetParent(fixture.Copy.transform, false);
            var other = second.AddComponent<SkinnedMeshRenderer>(); other.sharedMesh = fixture.Mesh;
            other.SetBlendShapeWeight(0, 25);
            var profile = ScriptableObject.CreateInstance<VrmTrackingProfile>(); fixture.Additional.Add(profile);
            profile.expressions = VrmTrackingExpressions.Names.Select(name => new TrackingExpression {
                name = name, morphs = new[] { new TrackingMorph { shape = "Multi", weight = .5f } }
            }).ToArray();
            using var session = AuthoredExpressionEndpoints.Capture(fixture.Copy, NeutralShapeSnapshot.Capture(fixture.Copy), profile);
            fixture.Preserve(); session.Bake(fixture.Owned);
            var entry = session.TrackingProfile.expressions.First();
            Assert.That(entry.morphs.Length, Is.EqualTo(2));
            Assert.That(entry.morphs.Select(morph => morph.shape).Distinct().Count(), Is.EqualTo(2));
            foreach (var skin in new[] { fixture.Skin, other })
            {
                var binding = entry.morphs.Single(morph => skin.sharedMesh.GetBlendShapeIndex(morph.shape) >= 0);
                Assert.That(binding.weight, Is.EqualTo(1));
                var delta = new Vector3[3]; skin.sharedMesh.GetBlendShapeFrameVertices(skin.sharedMesh.GetBlendShapeIndex(binding.shape), 0, delta, null, null);
                Assert.That((skin.sharedMesh.vertices[0] + delta[0]).x, Is.EqualTo(2).Within(.0001f),
                    "Profile0.5 means source weight50 even when the last source frame is200 and neutral differs between renderers.");
            }
            Assert.That(profile.expressions.First().morphs.Single().weight, Is.EqualTo(.5f));
            Assert.That(profile.expressions.First().morphs.Single().shape, Is.EqualTo("Multi"));
        }

        [TestCase(0f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        public void ProfileRetainsExistingInvalidWeightContract(float weight)
        {
            Assert.Throws<InvalidOperationException>(() => VrmTrackingExpressions.ValidateMorph(new TrackingMorph { shape = "Multi", weight = weight }, "JawOpen"));
        }
    }
}
