using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UniGLTF;
using UniVRM10;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class SkinnedMeshFallbackWeightTests
    {
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void RigidMeshesPreserveGeometryAndMixedZeroWeightsFailSafely(bool mixedWeights, bool useRootBone)
        {
            using var fixture = new Fixture(mixedWeights, useRootBone);
            var original = fixture.SourceSkin.sharedMesh;
            var originalVertices = original.vertices;
            var originalBones = fixture.SourceSkin.bones;
            var originalBinds = original.bindposes;
            var originalWeights = original.GetAllBoneWeights().ToArray();
            var originalBounds = fixture.CloneSkin.localBounds;
            if (mixedWeights)
            {
                var error = Assert.Throws<InvalidOperationException>(() => SkinnedMeshFallbackWeights.Preserve(fixture.Clone, fixture.Owned, null));
                Assert.That(error.Message, Does.Contain("ウェイトのない頂点"));
                Assert.That(fixture.CloneSkin.sharedMesh, Is.SameAs(original));
                Assert.That(original.vertices, Is.EqualTo(originalVertices));
                Assert.That(original.GetAllBoneWeights().ToArray(), Is.EqualTo(originalWeights));
                Assert.That(fixture.Owned, Is.Empty);
                return;
            }
            SkinnedMeshFallbackWeights.Preserve(fixture.Clone, fixture.Owned, null);
            var converted = fixture.CloneSkin.sharedMesh;
            Assert.That(converted, Is.Not.SameAs(original));
            Assert.That(fixture.SourceSkin.sharedMesh, Is.SameAs(original));
            Assert.That(fixture.SourceSkin.bones, Is.EqualTo(originalBones));
            Assert.That(original.vertices, Is.EqualTo(originalVertices));
            Assert.That(original.bindposes, Is.EqualTo(originalBinds));
            Assert.That(original.GetAllBoneWeights().ToArray(), Is.EqualTo(originalWeights));
            Assert.That(fixture.SourceSkin.GetBlendShapeWeight(0), Is.EqualTo(25f));
            Assert.That(fixture.CloneSkin.GetBlendShapeWeight(0), Is.EqualTo(25f));
            Assert.That(converted.vertices, Is.EqualTo(originalVertices));
            Assert.That(converted.normals, Is.EqualTo(original.normals));
            Assert.That(converted.tangents, Is.EqualTo(original.tangents));
            Assert.That(fixture.CloneSkin.localBounds, Is.EqualTo(originalBounds));
            AssertMorphsUnchanged(original, converted);
            var newBinds = converted.bindposes;
            for (var i = 0; i < originalBinds.Length; i++) Assert.That(newBinds[i], Is.EqualTo(originalBinds[i]));
            Assert.That(fixture.CloneSkin.bones.Length, Is.EqualTo(originalBones.Length + 1));

            var poses = new[] { 0f, 25f, 75f, 100f };
            for (var i = 0; i < poses.Length; i++)
            {
                fixture.SourceSkin.SetBlendShapeWeight(0, poses[i]);
                fixture.CloneSkin.SetBlendShapeWeight(0, poses[i]);
                fixture.Move(i);
                AssertRenderedGeometry(fixture.SourceSkin, fixture.CloneSkin);
            }
            if (mixedWeights)
            {
                // The original weighted vertex still uses exactly its old joint
                // indices/weights, while the zero-weight nose vertex is explicit.
                var oldLegacy = original.boneWeights;
                var newLegacy = converted.boneWeights;
                Assert.That(newLegacy[1], Is.EqualTo(oldLegacy[1]));
                Assert.That(newLegacy[2], Is.EqualTo(oldLegacy[2]));
                Assert.That(newLegacy[0].weight0, Is.EqualTo(1f));
                Assert.That(newLegacy[0].boneIndex0, Is.EqualTo(originalBones.Length));
            }
        }

        [Test]
        public void RigidMeshKeepsItsPoseWhenPreparationChangesTheBoundsAnchor()
        {
            using var fixture = new Fixture(false, true);
            var originalMesh = fixture.SourceSkin.sharedMesh;
            var originalRoot = fixture.SourceSkin.rootBone;
            SkinnedMeshFallbackWeights.Preserve(fixture.Clone, fixture.Owned, null);
            var explicitMesh = fixture.CloneSkin.sharedMesh;
            var explicitBones = fixture.CloneSkin.bones;
            var ownedCount = fixture.Owned.Count;

            // Bounds anchors must not determine rigid mesh placement.
            var boundsAnchor = new GameObject("New bounds anchor").transform;
            boundsAnchor.SetParent(fixture.Clone.transform, false);
            boundsAnchor.localPosition = new Vector3(-0.3f, 1.9f, 0.4f);
            fixture.CloneSkin.rootBone = boundsAnchor;
            AssertRenderedGeometry(fixture.SourceSkin, fixture.CloneSkin);

            // A second pass handles meshes generated by preparation, and must
            // leave an already explicit mesh and its bone indices unchanged.
            SkinnedMeshFallbackWeights.Preserve(fixture.Clone, fixture.Owned, null);
            Assert.That(fixture.CloneSkin.sharedMesh, Is.SameAs(explicitMesh));
            Assert.That(fixture.CloneSkin.bones, Is.EqualTo(explicitBones));
            Assert.That(fixture.Owned.Count, Is.EqualTo(ownedCount));
            Assert.That(fixture.CloneSkin.rootBone, Is.SameAs(boundsAnchor));
            Assert.That(fixture.SourceSkin.rootBone, Is.SameAs(originalRoot));
            Assert.That(fixture.SourceSkin.sharedMesh, Is.SameAs(originalMesh));
            fixture.SourceSkin.SetBlendShapeWeight(0, 75f);
            fixture.CloneSkin.SetBlendShapeWeight(0, 75f);
            fixture.Move(2);
            boundsAnchor.localPosition += new Vector3(0.2f, -0.5f, 0.1f);
            AssertRenderedGeometry(fixture.SourceSkin, fixture.CloneSkin);
        }

        [Test]
        public void RigidMeshFollowsItsRendererWhenBoundsAnchorIsAvatarRoot()
        {
            using var fixture = new Fixture(false, true);
            fixture.SourceSkin.rootBone = fixture.Source.transform;
            fixture.CloneSkin.rootBone = fixture.Clone.transform;
            var oldChildren = fixture.Clone.transform.childCount;
            SkinnedMeshFallbackWeights.Preserve(fixture.Clone, fixture.Owned, null);
            var joint = fixture.CloneSkin.bones[0];
            Assert.That(joint, Is.Not.SameAs(fixture.Clone.transform));
            Assert.That(joint, Is.SameAs(fixture.CloneSkin.transform));
            Assert.That(fixture.Clone.transform.childCount, Is.EqualTo(oldChildren));
            Assert.That(fixture.CloneSkin.rootBone, Is.SameAs(fixture.Clone.transform));
            fixture.Move(2);
            AssertRenderedGeometry(fixture.SourceSkin, fixture.CloneSkin);
        }

        [Test]
        public void FullyWeightedMeshIsNotReplaced()
        {
            using var fixture = new Fixture(true, true);
            var mesh = fixture.SourceSkin.sharedMesh;
            var weights = mesh.boneWeights;
            for (var i = 0; i < weights.Length; i++) weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
            mesh.boneWeights = weights;
            SkinnedMeshFallbackWeights.Preserve(fixture.Clone, fixture.Owned, null);
            Assert.That(fixture.CloneSkin.sharedMesh, Is.SameAs(mesh));
            Assert.That(fixture.Owned.Count, Is.EqualTo(0));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OriginalAvatarRootJointSurvivesUniVrmNodeExport(bool fullyWeighted)
        {
            using var fixture = new Fixture(true, true);
            var mesh = fixture.SourceSkin.sharedMesh;
            var sourceBones = fixture.SourceSkin.bones;
            var cloneBones = fixture.CloneSkin.bones;
            sourceBones[0] = fixture.Source.transform;
            cloneBones[0] = fixture.Clone.transform;
            fixture.SourceSkin.bones = sourceBones;
            fixture.CloneSkin.bones = cloneBones;
            var bindposes = mesh.bindposes;
            bindposes[0] = fixture.Source.transform.worldToLocalMatrix * fixture.SourceSkin.transform.localToWorldMatrix;
            mesh.bindposes = bindposes;
            if (fullyWeighted)
            {
                var weights = mesh.boneWeights;
                weights[0] = weights[3] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
                mesh.boneWeights = weights;
            }
            else
            {
                var error = Assert.Throws<InvalidOperationException>(() => SkinnedMeshFallbackWeights.Preserve(fixture.Clone, fixture.Owned, null));
                Assert.That(error.Message, Does.Contain("ウェイトのない頂点"));
                Assert.That(fixture.SourceSkin.bones[0], Is.SameAs(fixture.Source.transform));
                Assert.That(fixture.CloneSkin.sharedMesh, Is.SameAs(mesh));
                return;
            }
            SkinnedMeshFallbackWeights.Preserve(fixture.Clone, fixture.Owned, null);
            Assert.That(fixture.SourceSkin.bones[0], Is.SameAs(fixture.Source.transform));
            Assert.That(fixture.CloneSkin.bones[0], Is.Not.SameAs(fixture.Clone.transform));
            Assert.That(fixture.CloneSkin.bones[0].parent, Is.SameAs(fixture.Clone.transform));
            Assert.That(fixture.CloneSkin.sharedMesh.bindposes[0], Is.EqualTo(bindposes[0]));
            Assert.That(fixture.CloneSkin.sharedMesh.boneWeights[1], Is.EqualTo(mesh.boneWeights[1]));
            if (fullyWeighted) Assert.That(fixture.CloneSkin.sharedMesh, Is.SameAs(mesh));
            fixture.Move(2);
            AssertRenderedGeometry(fixture.SourceSkin, fixture.CloneSkin);
            using var arrays = new NativeArrayManager();
            var exporter = new ModelExporter();
            var model = exporter.Export(new GltfExportSettings(), arrays, fixture.Clone);
            Assert.That(model.Nodes.Contains(model.Root), Is.False, "UniVRM omits the selected avatar root.");
            Assert.That(model.Skins.Count, Is.EqualTo(1));
            foreach (var joint in model.Skins[0].Joints)
                Assert.That(model.Nodes.Contains(joint), Is.True, "Every skin joint must survive node export.");
        }

        [Test]
        public void MoreThanFourInfluencesRemainUnchanged()
        {
            using var fixture = new Fixture(true, true);
            var template = fixture.SourceSkin.sharedMesh;
            // SetBoneWeights cannot convert an existing legacy weight buffer in
            // Unity 2022 without logging a native vertex-format error.
            var mesh = new Mesh { vertices = template.vertices, triangles = template.triangles,
                normals = template.normals, tangents = template.tangents };
            fixture.Owned.Add(mesh);
            fixture.SourceSkin.sharedMesh = mesh;
            fixture.CloneSkin.sharedMesh = mesh;
            var bones = new Transform[6];
            var cloneBones = new Transform[6];
            var binds = new Matrix4x4[6];
            for (var i = 0; i < 6; i++)
            {
                bones[i] = fixture.SourceSkin.bones[i % 2];
                cloneBones[i] = fixture.CloneSkin.bones[i % 2];
                binds[i] = template.bindposes[i % 2];
            }
            fixture.SourceSkin.bones = bones;
            fixture.CloneSkin.bones = cloneBones;
            mesh.bindposes = binds;
            // Use valid nonzero influences. Unity 2022 normalizes an all-zero
            // SetBoneWeights input to a single influence, not an implicit vertex.
            var counts = new byte[] { 1, 6, 1, 1 };
            var weights = new List<BoneWeight1> { new BoneWeight1 { boneIndex = 0, weight = 1f } };
            for (var i = 0; i < 6; i++) weights.Add(new BoneWeight1 { boneIndex = i, weight = 1f / 6 });
            weights.Add(new BoneWeight1 { boneIndex = 0, weight = 1f });
            weights.Add(new BoneWeight1 { boneIndex = 0, weight = 1f });
            using (var nativeCounts = new NativeArray<byte>(counts, Allocator.Temp))
            using (var nativeWeights = new NativeArray<BoneWeight1>(weights.ToArray(), Allocator.Temp))
                mesh.SetBoneWeights(nativeCounts, nativeWeights);
            var before = mesh.GetAllBoneWeights().ToArray();
            SkinnedMeshFallbackWeights.Preserve(fixture.Clone, fixture.Owned, null);
            var after = fixture.CloneSkin.sharedMesh.GetAllBoneWeights().ToArray();
            Assert.That(fixture.CloneSkin.sharedMesh.GetBonesPerVertex()[1], Is.EqualTo(6));
            Assert.That(after, Is.EqualTo(before));
            AssertRenderedGeometry(fixture.SourceSkin, fixture.CloneSkin);
        }

        [TestCase("external")]
        [TestCase("singular")]
        public void UnrepresentableAnchorFailsWithoutReplacingSourceOrCopyMesh(string reason)
        {
            using var fixture = new Fixture(false, true);
            var external = new GameObject("External anchor");
            try
            {
                var original = fixture.SourceSkin.sharedMesh;
                var oldBones = fixture.CloneSkin.bones;
                if (reason == "external") fixture.CloneSkin.rootBone = external.transform;
                else fixture.CloneSkin.transform.localScale = new Vector3(1, 0, 1);
                Assert.Throws<InvalidOperationException>(() => SkinnedMeshFallbackWeights.Preserve(fixture.Clone, fixture.Owned, null));
                Assert.That(fixture.CloneSkin.sharedMesh, Is.SameAs(original));
                Assert.That(fixture.SourceSkin.sharedMesh, Is.SameAs(original));
                Assert.That(fixture.CloneSkin.bones, Is.EqualTo(oldBones));
                Assert.That(fixture.CloneSkin.GetBlendShapeWeight(0), Is.EqualTo(25f));
                Assert.That(fixture.Owned.Count, Is.EqualTo(0));
            }
            finally { Object.DestroyImmediate(external); }
        }

        private static void AssertMorphsUnchanged(Mesh a, Mesh b)
        {
            Assert.That(b.blendShapeCount, Is.EqualTo(a.blendShapeCount));
            for (var i = 0; i < a.blendShapeCount; i++)
            {
                Assert.That(b.GetBlendShapeName(i), Is.EqualTo(a.GetBlendShapeName(i)));
                Assert.That(b.GetBlendShapeFrameCount(i), Is.EqualTo(a.GetBlendShapeFrameCount(i)));
                for (var j = 0; j < a.GetBlendShapeFrameCount(i); j++)
                {
                    Assert.That(b.GetBlendShapeFrameWeight(i, j), Is.EqualTo(a.GetBlendShapeFrameWeight(i, j)));
                    var av = new Vector3[a.vertexCount]; var an = new Vector3[a.vertexCount]; var at = new Vector3[a.vertexCount];
                    var bv = new Vector3[b.vertexCount]; var bn = new Vector3[b.vertexCount]; var bt = new Vector3[b.vertexCount];
                    a.GetBlendShapeFrameVertices(i, j, av, an, at);
                    b.GetBlendShapeFrameVertices(i, j, bv, bn, bt);
                    Assert.That(bv, Is.EqualTo(av)); Assert.That(bn, Is.EqualTo(an)); Assert.That(bt, Is.EqualTo(at));
                }
            }
        }

        private static void AssertRenderedGeometry(SkinnedMeshRenderer a, SkinnedMeshRenderer b)
        {
            var first = new Mesh(); var second = new Mesh();
            try
            {
                a.BakeMesh(first, false); b.BakeMesh(second, false);
                Assert.That(first.vertexCount, Is.EqualTo(a.sharedMesh.vertexCount));
                AssertVectors(first.vertices, second.vertices);
                AssertVectors(first.normals, second.normals);
                var at = first.tangents; var bt = second.tangents;
                Assert.That(bt.Length, Is.EqualTo(at.Length));
                for (var i = 0; i < at.Length; i++)
                    Assert.That(Vector4.Distance(at[i], bt[i]), Is.LessThan(0.0005f), "Tangent " + i);
            }
            finally { Object.DestroyImmediate(first); Object.DestroyImmediate(second); }
        }
        private static void AssertVectors(Vector3[] a, Vector3[] b)
        {
            Assert.That(b.Length, Is.EqualTo(a.Length));
            for (var i = 0; i < a.Length; i++) Assert.That(Vector3.Distance(a[i], b[i]), Is.LessThan(0.0005f), "Vertex/direction " + i);
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly GameObject Source;
            internal readonly GameObject Clone;
            internal readonly SkinnedMeshRenderer SourceSkin;
            internal readonly SkinnedMeshRenderer CloneSkin;
            internal readonly List<Mesh> Owned = new List<Mesh>();
            private readonly Mesh mesh;
            internal Fixture(bool mixedWeights, bool rootBone)
            {
                Source = new GameObject("Avatar");
                Source.transform.SetPositionAndRotation(new Vector3(1.2f, -0.3f, 0.7f), Quaternion.Euler(7, 13, -5));
                Source.transform.localScale = Vector3.one * 1.1f;
                var anchor = Child(Source.transform, "AutoAnchorObject", new Vector3(0.1f, 1.1f, -0.2f));
                anchor.localRotation = Quaternion.Euler(11, -13, 17);
                anchor.localScale = new Vector3(1.1f, 0.9f, 1.05f);
                var head = Child(Source.transform, "Head", new Vector3(0, 1.4f, 0));
                var detail = Child(head, "Detail", new Vector3(0, 0.1f, 0.03f));
                var renderNode = Child(Source.transform, "Nose", new Vector3(0.1f, 0.2f, -0.3f));
                renderNode.localRotation = Quaternion.Euler(9, 7, 11);
                renderNode.localScale = new Vector3(1.2f, 0.8f, 1.1f);
                SourceSkin = renderNode.gameObject.AddComponent<SkinnedMeshRenderer>();
                SourceSkin.sharedMaterials = new Material[] { null };
                SourceSkin.rootBone = rootBone ? anchor : null;
                mesh = new Mesh { name = "Morph nose fixture" };
                mesh.vertices = new[] { new Vector3(0.02f, 0.04f, 0.03f), new Vector3(0.03f, 0.04f, 0.03f), new Vector3(0.02f, 0.05f, 0.03f), new Vector3(0.02f, 0.04f, 0.04f) };
                mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
                mesh.tangents = new[] { new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, -1), new Vector4(1, 0, 0, -1) };
                if (mixedWeights)
                {
                    SourceSkin.bones = new[] { head, detail };
                    mesh.bindposes = new[] { head.worldToLocalMatrix * renderNode.localToWorldMatrix, detail.worldToLocalMatrix * renderNode.localToWorldMatrix };
                    mesh.boneWeights = new[] { new BoneWeight(), new BoneWeight { boneIndex0 = 0, weight0 = 1 }, new BoneWeight { boneIndex0 = 0, weight0 = 0.7f, boneIndex1 = 1, weight1 = 0.3f }, new BoneWeight() };
                }
                for (var frame = 1; frame <= 2; frame++)
                {
                    var v = new Vector3[4]; var n = new Vector3[4]; var t = new Vector3[4];
                    for (var i = 0; i < 4; i++)
                    {
                        v[i] = new Vector3(0.005f * frame, 0.003f * i * frame, -0.002f * frame);
                        n[i] = new Vector3(0.01f * frame, 0, 0);
                        t[i] = new Vector3(0, 0.01f * frame, 0);
                    }
                    mesh.AddBlendShapeFrame("Nose expression", frame * 50f, v, n, t);
                }
                SourceSkin.sharedMesh = mesh;
                SourceSkin.SetBlendShapeWeight(0, 25f);
                Clone = Object.Instantiate(Source);
                CloneSkin = Clone.GetComponentInChildren<SkinnedMeshRenderer>();
            }
            internal void Move(int pose)
            {
                foreach (var root in new[] { Source.transform, Clone.transform })
                {
                    root.Find("AutoAnchorObject").localPosition = new Vector3(0.1f + pose * 0.02f, 1.1f + pose * 0.01f, -0.2f);
                    root.Find("AutoAnchorObject").localRotation = Quaternion.Euler(11 + pose * 7, -13 + pose * 3, 17);
                    root.Find("Head").localRotation = Quaternion.Euler(pose * 3, pose * 11, -pose * 2);
                    root.Find("Nose").localPosition = new Vector3(0.1f + pose * 0.01f, 0.2f, -0.3f);
                }
            }
            private static Transform Child(Transform parent, string name, Vector3 position)
            {
                var child = new GameObject(name).transform;
                child.SetParent(parent, false); child.localPosition = position;
                return child;
            }
            public void Dispose()
            {
                Object.DestroyImmediate(Clone); Object.DestroyImmediate(Source);
                foreach (var owned in Owned) Object.DestroyImmediate(owned);
                Object.DestroyImmediate(mesh);
            }
        }
    }
}
