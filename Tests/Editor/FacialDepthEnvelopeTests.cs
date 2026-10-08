using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class FacialDepthEnvelopeTests
    {
        private AttachmentConnectionTests.Fixture rig;
        private GameObject avatar;
        private SkinnedMeshRenderer skin;
        private Mesh mesh;

        [SetUp]
        public void SetUp()
        {
            var descriptorType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor"))
                .FirstOrDefault(t => t != null);
            if (descriptorType == null) Assert.Ignore("Install the real VRChat SDK.");
            rig = new AttachmentConnectionTests.Fixture(); avatar = rig.Source;
            foreach (var renderer in avatar.GetComponentsInChildren<SkinnedMeshRenderer>()) Object.DestroyImmediate(renderer.gameObject);
            skin = new GameObject("Merged geometry", typeof(SkinnedMeshRenderer)).GetComponent<SkinnedMeshRenderer>();
            skin.transform.SetParent(avatar.transform, false);
            var animator = avatar.GetComponent<Animator>();
            skin.bones = new[] { animator.GetBoneTransform(HumanBodyBones.Head), animator.GetBoneTransform(HumanBodyBones.Hips) };
            skin.rootBone = skin.bones[1];
            var descriptor = avatar.AddComponent(descriptorType);
            var lipSync = descriptorType.GetField("lipSync");
            lipSync.SetValue(descriptor, Enum.Parse(lipSync.FieldType, "VisemeBlendShape"));
            descriptorType.GetField("VisemeSkinnedMesh").SetValue(descriptor, skin);
            descriptorType.GetField("VisemeBlendShapes").SetValue(descriptor, new[] { "descriptor-channel" });
        }

        [TearDown]
        public void TearDown() { rig?.Dispose(); Object.DestroyImmediate(mesh); }

        private static EditorCurveBinding Insert => EditorCurveBinding.FloatCurve("Merged geometry", typeof(SkinnedMeshRenderer), "blendShape.opaque-island");

        private void Geometry(float rotation, float scale, string damage = "")
        {
            skin.transform.localRotation = Quaternion.Euler(rotation, 0, 0);
            skin.transform.localScale = Vector3.one * scale;
            var height = skin.bones[0].position.y;
            // The descriptor changes one connected front surface. The small
            // disconnected part is behind that surface, inside the projected
            // anatomical face, with opaque names and no descriptor assignment.
            var points = new[] {
                new Vector3(-.07f, height - .04f, .07f), new Vector3(.07f, height - .04f, .07f),
                new Vector3(-.07f, height + .09f, .07f), new Vector3(.07f, height + .09f, .07f),
                new Vector3(-.015f, height + .02f, .012f), new Vector3(.015f, height + .02f, .012f),
                new Vector3(0, height + .04f, .012f), new Vector3(0, height + .02f, .012f)
            };
            if (damage == "rear") for (var i = 4; i < 7; i++) points[i].z = -.2f;
            if (damage == "posterior inside mirrored bounds") for (var i = 4; i < 7; i++) points[i].z = -.012f;
            if (damage == "posterior inside original margin")
            {
                for (var i = 0; i < 4; i++) points[i].z = .005f;
                for (var i = 4; i < 7; i++) points[i].z = -.001f;
            }
            if (damage == "side") for (var i = 4; i < 7; i++) points[i].x += .2f;
            if (damage == "above") for (var i = 4; i < 7; i++) points[i].y += .3f;
            if (damage == "one outside vertex") points[6].y += .3f;
            if (damage == "straddling descriptor")
            {
                points[2].z = -.03f;
                // This lies beyond the original surface bounds, but inside
                // an unproven depth extension if its one-side guard were lost.
                for (var i = 4; i < 7; i++) points[i].z = -.06f;
            }
            mesh = new Mesh { name = "Disconnected original facial topology" };
            mesh.vertices = points.Select(p => skin.transform.InverseTransformPoint(p)).ToArray();
            mesh.triangles = damage == "nonhead boundary" ? new[] { 0, 1, 2, 1, 3, 2, 4, 5, 6, 5, 6, 7 } :
                new[] { 0, 1, 2, 1, 3, 2, 4, 5, 6 };
            mesh.boneWeights = Enumerable.Range(0, points.Length).Select(i => new BoneWeight { boneIndex0 = i == 7 ? 1 : 0, weight0 = 1 }).ToArray();
            mesh.bindposes = skin.bones.Select(b => b.worldToLocalMatrix * skin.transform.localToWorldMatrix).ToArray();
            var seed = new Vector3[points.Length]; seed[0] = Vector3.right * .01f / scale;
            mesh.AddBlendShapeFrame("descriptor-channel", 100, seed, null, null);
            var delta = new Vector3[points.Length]; delta[4] = Vector3.up * .01f / scale;
            mesh.AddBlendShapeFrame("opaque-island", 100, delta, null, null); skin.sharedMesh = mesh;
            if (damage == "anchor outside")
            {
                var poses = mesh.bindposes; var headToMesh = poses[0].inverse;
                headToMesh.SetColumn(3, new Vector4(1, headToMesh.m13, headToMesh.m23, 1)); poses[0] = headToMesh.inverse; mesh.bindposes = poses;
            }
            if (damage == "singular bindpose") { var poses = mesh.bindposes; poses[0] = Matrix4x4.zero; mesh.bindposes = poses; }
            if (damage == "nonfinite bindpose") { var poses = mesh.bindposes; poses[0].m00 = float.NaN; mesh.bindposes = poses; }
            if (damage == "singular renderer") skin.transform.localScale = new Vector3(1, 0, 1);
        }

        [TestCase(0f, .01f)]
        [TestCase(90f, .01f)]
        [TestCase(0f, 1f)]
        [TestCase(90f, 1f)]
        [TestCase(0f, 100f)]
        [TestCase(90f, 100f)]
        public void HeadBoundedDisconnectedDepthWorksWithRotatedRenderersAndMeshScales(float rotation, float scale)
        {
            Geometry(rotation, scale);
            var before = ExportSourceFingerprint.Compute(avatar);
            Assert.That(FacialProjectionScope.Create(avatar).Morphs.Contains(Insert), Is.True);
            Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before));
        }

        [TestCase("rear")]
        [TestCase("posterior inside mirrored bounds")]
        [TestCase("posterior inside original margin")]
        [TestCase("side")]
        [TestCase("above")]
        [TestCase("one outside vertex")]
        [TestCase("nonhead boundary")]
        [TestCase("anchor outside")]
        [TestCase("straddling descriptor")]
        [TestCase("singular bindpose")]
        [TestCase("nonfinite bindpose")]
        [TestCase("singular renderer")]
        public void DepthCannotAuthorizeUnboundedOrUnprovenTopology(string damage)
        {
            Geometry(90, 1, damage);
            Assert.That(FacialProjectionScope.Create(avatar).Morphs.Contains(Insert), Is.False);
        }

        [Test]
        public void CurrentHeadAnimationDoesNotMoveTheOriginalMeshEnvelope()
        {
            Geometry(90, 1);
            skin.bones[0].position += new Vector3(.4f, .3f, -.5f);
            var before = ExportSourceFingerprint.Compute(avatar);
            Assert.That(FacialProjectionScope.Create(avatar).Morphs.Contains(Insert), Is.True);
            Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before));
        }

        private void SeamGeometry(float rotation, float scale, string damage = "")
        {
            skin.transform.localRotation = Quaternion.Euler(rotation, 0, 0);
            skin.transform.localScale = Vector3.one * scale;
            var height = skin.bones[0].position.y;
            var points = new[] {
                new Vector3(-.07f, height - .04f, .07f), new Vector3(.07f, height - .04f, .07f),
                new Vector3(-.07f, height + .09f, .07f), new Vector3(.07f, height + .09f, .07f),
                new Vector3(.07f, height - .04f, .07f), new Vector3(-.07f, height - .04f, .07f),
                new Vector3(0, height + .02f, -.012f), new Vector3(0, height + .04f, -.012f),
                new Vector3(-.015f, height + .02f, -.012f), new Vector3(.015f, height + .02f, -.012f),
                new Vector3(0, height + .04f, -.012f)
            };
            if (damage == "point only") points[4].x -= .001f;
            if (damage == "near edge") { points[4].z += .0000001f; points[5].z += .0000001f; }
            if (damage == "degenerate edge") points[4] = points[5];
            var triangles = new[] { 0, 1, 2, 1, 3, 2, 4, 5, 6 };
            if (damage == "nonhead boundary") triangles = triangles.Concat(new[] { 5, 6, 7 }).ToArray();
            if (damage == "another detached posterior") triangles = triangles.Concat(new[] { 8, 9, 10 }).ToArray();
            mesh = new Mesh { name = "Exact duplicated triangle-edge seam" };
            mesh.vertices = points.Select(p => skin.transform.InverseTransformPoint(p)).ToArray();
            mesh.triangles = triangles;
            mesh.boneWeights = Enumerable.Range(0, points.Length).Select(i => new BoneWeight {
                boneIndex0 = i == 7 ? 1 : 0, weight0 = 1 }).ToArray();
            mesh.bindposes = skin.bones.Select(b => b.worldToLocalMatrix * skin.transform.localToWorldMatrix).ToArray();
            var seed = new Vector3[points.Length]; seed[0] = Vector3.right * .01f / scale;
            mesh.AddBlendShapeFrame("descriptor-channel", 100, seed, null, null);
            var delta = new Vector3[points.Length]; delta[6] = Vector3.up * .01f / scale;
            mesh.AddBlendShapeFrame("opaque-island", 100, delta, null, null);
            var detached = new Vector3[points.Length]; detached[8] = Vector3.up * .01f / scale;
            mesh.AddBlendShapeFrame("unrelated-posterior", 100, detached, null, null);
            skin.sharedMesh = mesh;
        }

        [TestCase(0f, .01f)]
        [TestCase(90f, .01f)]
        [TestCase(0f, 1f)]
        [TestCase(90f, 1f)]
        [TestCase(0f, 100f)]
        [TestCase(90f, 100f)]
        public void ExactFacialEdgesConnectDuplicatedMaterialSeams(float rotation, float scale)
        {
            SeamGeometry(rotation, scale);
            var before = ExportSourceFingerprint.Compute(avatar);
            Assert.That(FacialProjectionScope.Create(avatar).Morphs.Contains(Insert), Is.True);
            Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before));
        }

        [TestCase("point only")]
        [TestCase("near edge")]
        [TestCase("degenerate edge")]
        public void CoincidentPointsAndNearbyEdgesCannotConnectDetachedPosteriorGeometry(string damage)
        {
            SeamGeometry(90, 1, damage);
            Assert.That(FacialProjectionScope.Create(avatar).Morphs.Contains(Insert), Is.False);
        }

        [Test]
        public void ASeamCannotEnlargeTheEnclosureForOtherDetachedPosteriorGeometry()
        {
            SeamGeometry(90, 1, "another detached posterior");
            var scope = FacialProjectionScope.Create(avatar);
            Assert.That(scope.Morphs.Contains(Insert), Is.True);
            var detached = EditorCurveBinding.FloatCurve("Merged geometry", typeof(SkinnedMeshRenderer), "blendShape.unrelated-posterior");
            Assert.That(scope.Morphs.Contains(detached), Is.False);
        }

        [Test]
        public void ASeamTouchingNonHeadGeometryCannotAuthorizeTheRemainingSurface()
        {
            SeamGeometry(90, 1, "nonhead boundary");
            Assert.That(FacialProjectionScope.Create(avatar).Morphs.Contains(Insert), Is.False);
        }
    }
}
