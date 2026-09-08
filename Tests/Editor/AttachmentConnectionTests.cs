using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UniGLTF;
using UniVRM10;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class AttachmentConnectionTests
    {
        [Test]
        public void RealWeightsFindSharedHairRigWithoutGuessingByNameOrRootBone()
        {
            using var f = new Fixture();
            var review = new ExportAttachmentSession(f.Source, f.Copy);
            Assert.That(review.Parts.Count, Is.EqualTo(1));
            Assert.That(review.Parts[0].Root, Is.SameAs(f.Hair));
            Assert.That(review.Parts[0].Renderers.Length, Is.EqualTo(2));
            Assert.That(f.Hair.parent, Is.SameAs(f.Copy.transform));
            Assert.That(review.Targets[0], Is.SameAs(f.Head));
            Assert.That(f.Skins[0].rootBone, Is.SameAs(f.Head), "A bounds anchor is not proof of weighted attachment.");
        }

        [Test]
        public void AttachingSharedRigPreservesRestMorphsAndSourceThenFollowsHeadMotion()
        {
            using var f = new Fixture();
            var originalMesh = f.Mesh;
            var originalBinds = originalMesh.bindposes;
            var originalWeights = originalMesh.boneWeights;
            var before = WorldVertices(f.Skins[0]);
            var sourceBefore = WorldVertices(f.Source.GetComponentInChildren<SkinnedMeshRenderer>());
            var headBefore = f.Head.localToWorldMatrix;
            var review = new ExportAttachmentSession(f.Source, f.Copy);
            review.Attach(f.Hair, f.Head);
            AssertVertices(before, WorldVertices(f.Skins[0]));
            Assert.That(new ExportAttachmentSession(f.Source, f.Copy).Parts.Count, Is.Zero);
            f.Head.localRotation = Quaternion.Euler(18f, 44f, -12f);
            var delta = f.Head.localToWorldMatrix * headBefore.inverse;
            foreach (var skin in f.Skins) AssertVertices(before.Select(delta.MultiplyPoint3x4).ToArray(), WorldVertices(skin));
            AssertVertices(sourceBefore, WorldVertices(f.Source.GetComponentInChildren<SkinnedMeshRenderer>()));
            Assert.That(f.Source.transform.Find("Independent hair").parent, Is.SameAs(f.Source.transform));
            Assert.That(f.Skins.All(s => s.sharedMesh == originalMesh), Is.True);
            Assert.That(originalMesh.bindposes, Is.EqualTo(originalBinds));
            Assert.That(originalMesh.boneWeights, Is.EqualTo(originalWeights));
            Assert.That(originalMesh.blendShapeCount, Is.EqualTo(1));
        }

        [Test]
        public void AlreadyAttachedPartsStayUnchangedAndCanBeExplicitlyReviewed()
        {
            using var f = new Fixture();
            f.Hair.SetParent(f.Head, true);
            Assert.That(new ExportAttachmentSession(f.Source, f.Copy).Parts.Count, Is.Zero);
            Assert.That(new ExportAttachmentSession(f.Source, f.Copy, true).Parts.Any(p => p.Root == f.Hair), Is.True);
        }

        [Test]
        public void OriginalExternalAndHumanoidRootsCannotBeReparented()
        {
            using var f = new Fixture();
            Assert.Throws<InvalidOperationException>(() => new ExportAttachmentSession(f.Source, f.Source));
            var review = new ExportAttachmentSession(f.Source, f.Copy);
            Assert.Throws<InvalidOperationException>(() => review.Attach(f.Hair, f.Source.transform));
            Assert.Throws<InvalidOperationException>(() => review.Attach(f.Head, f.Hair));
            Assert.Throws<InvalidOperationException>(() => review.Attach(f.Source.transform.Find("Independent hair"), f.Head));
            Assert.That(f.Hair.parent, Is.SameAs(f.Copy.transform));
        }

        [Test]
        public void UnrepresentableScaleRestoresTheCopyHierarchyAndPose()
        {
            using var f = new Fixture();
            f.Head.localScale = new Vector3(2f, 0.5f, 1f);
            f.Head.localRotation = Quaternion.Euler(0, 35, 0);
            f.Hair.localRotation = Quaternion.Euler(15, 0, 25);
            var matrix = f.Hair.localToWorldMatrix;
            var sibling = f.Hair.GetSiblingIndex();
            Assert.Throws<InvalidOperationException>(() => new ExportAttachmentSession(f.Source, f.Copy).Attach(f.Hair, f.Head));
            Assert.That(f.Hair.parent, Is.SameAs(f.Copy.transform));
            Assert.That(f.Hair.GetSiblingIndex(), Is.EqualTo(sibling));
            Assert.That(f.Hair.localToWorldMatrix, Is.EqualTo(matrix));
        }

        [Test]
        public void ExistingVrmConstraintIsRecognizedAndNotDoubleAttached()
        {
            using var f = new Fixture();
            var constraint = f.Hair.gameObject.AddComponent<Vrm10RotationConstraint>();
            constraint.Source = f.Head;
            constraint.Weight = 1f;
            Assert.That(new ExportAttachmentSession(f.Source, f.Copy).Parts.Count, Is.Zero);
            var review = new ExportAttachmentSession(f.Source, f.Copy, true);
            Assert.Throws<InvalidOperationException>(() => review.Attach(f.Hair, f.Head));
            Assert.That(constraint.Source, Is.SameAs(f.Head));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SpringsArePreservedAndCannotBeSplitAcrossAttachments(bool crossesBoundary)
        {
            using var f = new Fixture();
            var instance = f.Copy.AddComponent<Vrm10Instance>();
            var joint = f.Hair.GetChild(0).gameObject.AddComponent<VRM10SpringBoneJoint>();
            var spring = new Vrm10InstanceSpringBone.Spring("Existing hair") { Center = f.Head };
            spring.Joints.Add(joint);
            if (crossesBoundary) spring.Joints.Add(f.Head.gameObject.AddComponent<VRM10SpringBoneJoint>());
            instance.SpringBone.Springs.Add(spring);
            var joints = spring.Joints.ToArray();
            var review = new ExportAttachmentSession(f.Source, f.Copy);
            if (crossesBoundary) Assert.Throws<InvalidOperationException>(() => review.Attach(f.Hair, f.Head));
            else review.Attach(f.Hair, f.Head);
            Assert.That(instance.SpringBone.Springs.Single(), Is.SameAs(spring));
            Assert.That(spring.Center, Is.SameAs(f.Head));
            Assert.That(spring.Joints, Is.EqualTo(joints));
            Assert.That(f.Hair.parent, Is.SameAs(crossesBoundary ? f.Copy.transform : f.Head));
        }

        [Test]
        public void InstalledMaBoneProxyAutomaticallyConnectsHairWithoutAnInferredTarget()
        {
            var proxyType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("nadena.dev.modular_avatar.core.ModularAvatarBoneProxy")).FirstOrDefault(t => t != null);
            var markerType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("nadena.dev.ndmf.runtime.components.NDMFAvatarRoot")).FirstOrDefault(t => t != null);
            if (proxyType == null || markerType == null) Assert.Ignore("Requires installed Modular Avatar and NDMF in Unity.");
            using var f = new Fixture();
            foreach (var avatarRoot in new[] { f.Source, f.Copy })
            {
                avatarRoot.AddComponent(markerType);
                var proxy = avatarRoot.transform.Find("Independent hair").gameObject.AddComponent(proxyType);
                proxyType.GetProperty("target").SetValue(proxy, avatarRoot.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Head));
                var mode = proxyType.GetField("attachmentMode");
                mode.SetValue(proxy, Enum.Parse(mode.FieldType, "AsChildKeepWorldPose"));
            }
            var before = WorldVertices(f.Skins[0]);
            using (NdmfExportPreparation.Prepare(f.Source, f.Copy))
            {
                Assert.That(new ExportAttachmentSession(f.Source, f.Copy).Parts.Count, Is.Zero);
                AssertVertices(before, WorldVertices(f.Skins[0]));
                var matrix = f.Head.localToWorldMatrix;
                f.Head.localRotation = Quaternion.Euler(10, 35, 0);
                var delta = f.Head.localToWorldMatrix * matrix.inverse;
                AssertVertices(before.Select(delta.MultiplyPoint3x4).ToArray(), WorldVertices(f.Skins[0]));
                Assert.That(f.Source.transform.Find("Independent hair").parent, Is.SameAs(f.Source.transform));
                Assert.That(f.Source.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh, Is.SameAs(f.Mesh));
            }
        }

        [Test]
        public void PreviewBuildsOnlyRenderComponentsAndCleansItsSceneWithoutTouchingSource()
        {
            using var f = new Fixture();
            var window = ScriptableObject.CreateInstance<AttachmentPreviewWindow>();
            var type = typeof(AttachmentPreviewWindow);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var scene = f.Source.scene;
            var beforeRoots = scene.GetRootGameObjects();
            var dirty = scene.isDirty;
            GameObject preview = null;
            try
            {
                var review = new ExportAttachmentSession(f.Source, f.Copy);
                type.GetField("session", flags).SetValue(window, review);
                type.GetField("choices", flags).SetValue(window, new int[review.Parts.Count]);
                type.GetMethod("InitializePreview", flags).Invoke(window, null);
                preview = (GameObject)type.GetField("previewCopy", flags).GetValue(window);
                Assert.That(preview.scene, Is.Not.EqualTo(scene));
                Assert.That(preview.GetComponentsInChildren<MonoBehaviour>(true).Length, Is.Zero);
                Assert.That(preview.GetComponentsInChildren<Animator>(true).Length, Is.Zero);
                Assert.That(preview.GetComponentsInChildren<SkinnedMeshRenderer>().All(s => s.sharedMesh == f.Mesh), Is.True);
                Assert.That(f.Hair.parent, Is.SameAs(f.Copy.transform));
                Assert.That(scene.GetRootGameObjects(), Is.EquivalentTo(beforeRoots));
                Assert.That(scene.isDirty, Is.EqualTo(dirty));
            }
            finally { Object.DestroyImmediate(window); }
            Assert.That(preview == null, Is.True);
        }

        [Test]
        public void ModalWaitDisablesPreparedCopyAndRestoresItWhenCanceled()
        {
            using var f = new Fixture();
            Assert.Throws<OperationCanceledException>(() => AttachmentPreviewWindow.WhileCopyInactive(f.Copy, () =>
            {
                Assert.That(f.Copy.activeInHierarchy, Is.False);
                Assert.That(f.Source.activeInHierarchy, Is.True);
                throw new OperationCanceledException();
            }));
            Assert.That(f.Copy.activeSelf, Is.True);
            Assert.That(f.Source.activeSelf, Is.True);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void ExistingExpressionAndFirstPersonPathsRemainBoundWithoutEditingSharedSettings(int collision)
        {
            using var f = new Fixture();
            f.Skins[0].transform.SetParent(f.Hair, true);
            var settings = ScriptableObject.CreateInstance<VRM10Object>();
            var expression = ScriptableObject.CreateInstance<VRM10Expression>();
            expression.name = "Existing hair expression";
            var oldPath = "Independent hair/Front";
            expression.MorphTargetBindings = new[] { new MorphTargetBinding { RelativePath = oldPath, Index = 0, Weight = .4f } };
            settings.Expression.Happy = settings.Expression.Angry = expression;
            settings.FirstPerson.Renderers.Add(new RendererFirstPersonFlags { Renderer = oldPath,
                FirstPersonFlag = UniGLTF.Extensions.VRMC_vrm.FirstPersonType.thirdPersonOnly });
            var original = f.Source.AddComponent<Vrm10Instance>(); original.Vrm = settings;
            var instance = f.Copy.AddComponent<Vrm10Instance>(); instance.Vrm = settings;
            VRM10Object owned = null;
            VRM10Expression ownedExpression = null;
            try
            {
                if (collision != 0)
                {
                    var other = new GameObject("Independent hair").transform; other.SetParent(f.Head, false);
                    if (collision == 1) new GameObject("Front").transform.SetParent(other, false);
                }
                using (var review = new ExportAttachmentSession(f.Source, f.Copy))
                {
                    if (collision != 0)
                    {
                        Assert.Throws<InvalidOperationException>(() => review.Attach(f.Hair, f.Head));
                        Assert.That(instance.Vrm, Is.SameAs(settings));
                        Assert.That(f.Hair.parent, Is.SameAs(f.Copy.transform));
                    }
                    else
                    {
                        review.Attach(f.Hair, f.Head);
                        owned = instance.Vrm; ownedExpression = owned.Expression.Happy;
                        var expected = review.Path(f.Skins[0].transform);
                        Assert.That(owned, Is.Not.SameAs(settings));
                        Assert.That(ownedExpression, Is.Not.SameAs(expression));
                        Assert.That(owned.Expression.Angry, Is.SameAs(ownedExpression));
                        Assert.That(ownedExpression.name, Is.EqualTo(expression.name));
                        Assert.That(ownedExpression.MorphTargetBindings[0].RelativePath, Is.EqualTo(expected));
                        Assert.That(ownedExpression.MorphTargetBindings[0].Weight, Is.EqualTo(.4f));
                        Assert.That(owned.FirstPerson.Renderers[0].Renderer, Is.EqualTo(expected));
                        Assert.That(owned.FirstPerson.Renderers[0].FirstPersonFlag, Is.EqualTo(UniGLTF.Extensions.VRMC_vrm.FirstPersonType.thirdPersonOnly));
                    }
                    Assert.That(original.Vrm, Is.SameAs(settings));
                    Assert.That(settings.Expression.Happy, Is.SameAs(expression));
                    Assert.That(expression.MorphTargetBindings[0].RelativePath, Is.EqualTo(oldPath));
                    Assert.That(settings.FirstPerson.Renderers[0].Renderer, Is.EqualTo(oldPath));
                }
                Assert.That(owned == null && ownedExpression == null, Is.True, "Copy-owned settings must be disposed.");
                Assert.That(settings != null && expression != null, Is.True);
            }
            finally { Object.DestroyImmediate(settings); Object.DestroyImmediate(expression); }
        }

        [Test]
        public async Task NewVrmReimportKeepsHairAttachedWhenItsControlRigHeadTurns()
        {
            using var f = new Fixture();
            new ExportAttachmentSession(f.Source, f.Copy).Attach(f.Hair, f.Head);
            var bytes = Vrm10Exporter.Export(new GltfExportSettings(), f.Copy,
                textureSerializer: new MobileTextureSerializer(null),
                vrmMeta: new VRM10ObjectMeta { Name = "Attachment regression", Version = "1", Authors = new List<string> { "Test" } });
            Vrm10Instance imported = null;
            try
            {
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported, Is.Not.Null);
                var skin = imported.GetComponentsInChildren<SkinnedMeshRenderer>().First(s => s.name == "Front");
                var target = imported.Runtime.ControlRig.GetBoneTransform(HumanBodyBones.Head);
                Assert.That(imported.TryGetBoneTransform(HumanBodyBones.Head, out var originalHead), Is.True);
                imported.Runtime.Process();
                var before = WorldVertices(skin);
                var matrix = originalHead.localToWorldMatrix;
                target.localRotation = Quaternion.Euler(12, 40, 0);
                imported.Runtime.Process();
                var delta = originalHead.localToWorldMatrix * matrix.inverse;
                AssertVertices(before.Select(delta.MultiplyPoint3x4).ToArray(), WorldVertices(skin));
                Assert.That(skin.sharedMesh.blendShapeCount, Is.EqualTo(1));
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
            }
        }

        private static Vector3[] WorldVertices(SkinnedMeshRenderer skin)
        {
            var baked = new Mesh();
            try { skin.BakeMesh(baked, false); return baked.vertices.Select(skin.transform.TransformPoint).ToArray(); }
            finally { Object.DestroyImmediate(baked); }
        }

        private static void AssertVertices(Vector3[] expected, Vector3[] actual)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            for (var i = 0; i < expected.Length; i++) Assert.That(Vector3.Distance(expected[i], actual[i]), Is.LessThan(0.0005f));
        }

        private sealed class Fixture : IDisposable
        {
            internal GameObject Source, Copy;
            internal Transform Hair, Head;
            internal Mesh Mesh;
            internal SkinnedMeshRenderer[] Skins;
            private Avatar avatar;
            private Material material;

            internal Fixture()
            {
                Source = new GameObject("Avatar");
                var bones = new Dictionary<HumanBodyBones, Transform>();
                Transform Bone(HumanBodyBones kind, Transform parent, Vector3 world)
                {
                    var value = Child(parent, kind.ToString()); value.position = world; bones.Add(kind, value); return value;
                }
                var hips = Bone(HumanBodyBones.Hips, Source.transform, new Vector3(0, 1, 0));
                var spine = Bone(HumanBodyBones.Spine, hips, new Vector3(0, 1.2f, 0));
                var chest = Bone(HumanBodyBones.Chest, spine, new Vector3(0, 1.4f, 0));
                var neck = Bone(HumanBodyBones.Neck, chest, new Vector3(0, 1.6f, 0));
                Bone(HumanBodyBones.Head, neck, new Vector3(0, 1.75f, 0));
                foreach (var left in new[] { true, false })
                {
                    var x = left ? -1f : 1f;
                    var arm = Bone(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm, chest, new Vector3(x * .25f, 1.5f, 0));
                    var forearm = Bone(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm, arm, new Vector3(x * .55f, 1.5f, 0));
                    Bone(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand, forearm, new Vector3(x * .8f, 1.5f, 0));
                    var leg = Bone(left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg, hips, new Vector3(x * .12f, .9f, 0));
                    var shin = Bone(left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg, leg, new Vector3(x * .12f, .45f, 0));
                    Bone(left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot, shin, new Vector3(x * .12f, .1f, .1f));
                }
                avatar = AvatarBuilder.BuildHumanAvatar(Source, new HumanDescription
                {
                    human = bones.Select(p => new HumanBone { boneName = p.Value.name, humanName = HumanTrait.BoneName[(int)p.Key], limit = new HumanLimit { useDefaultValues = true } }).ToArray(),
                    skeleton = Source.GetComponentsInChildren<Transform>().Select(t => new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale }).ToArray(),
                    upperArmTwist = .5f, lowerArmTwist = .5f, upperLegTwist = .5f, lowerLegTwist = .5f, armStretch = .05f, legStretch = .05f
                });
                Assert.That(avatar.isValid && avatar.isHuman, Is.True);
                Source.AddComponent<Animator>().avatar = avatar;
                var rig = Child(Source.transform, "Independent hair");
                var joint = Child(rig, "Head"); joint.position = new Vector3(0, 1.75f, 0);
                Mesh = new Mesh { name = "Shared hair" };
                Mesh.vertices = new[] { new Vector3(-.1f, 1.7f, .08f), new Vector3(.1f, 1.7f, .08f), new Vector3(0, 1.9f, .08f) };
                Mesh.triangles = new[] { 0, 1, 2 };
                Mesh.normals = Enumerable.Repeat(Vector3.forward, 3).ToArray();
                Mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 3).ToArray();
                Mesh.bindposes = new[] { joint.worldToLocalMatrix, bones[HumanBodyBones.Head].worldToLocalMatrix };
                Mesh.AddBlendShapeFrame("Hair detail", 100, Enumerable.Repeat(new Vector3(.02f, .01f, 0), 3).ToArray(), new Vector3[3], new Vector3[3]);
                material = new Material(Shader.Find("VRM10/MToon10"));
                foreach (var name in new[] { "Front", "Back" })
                {
                    var skin = Child(Source.transform, name).gameObject.AddComponent<SkinnedMeshRenderer>();
                    skin.sharedMesh = Mesh;
                    skin.sharedMaterials = new[] { material };
                    skin.bones = new[] { joint, bones[HumanBodyBones.Head] };
                    skin.rootBone = bones[HumanBodyBones.Head];
                    skin.SetBlendShapeWeight(0, 35);
                }
                Copy = Object.Instantiate(Source); Copy.name = Source.name;
                Hair = Copy.transform.Find("Independent hair");
                Head = Copy.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Head);
                Skins = Copy.GetComponentsInChildren<SkinnedMeshRenderer>();
            }

            private static Transform Child(Transform parent, string name)
            {
                var value = new GameObject(name).transform; value.SetParent(parent, false); return value;
            }
            public void Dispose()
            {
                Object.DestroyImmediate(Copy); Object.DestroyImmediate(Source);
                Object.DestroyImmediate(Mesh); Object.DestroyImmediate(material); Object.DestroyImmediate(avatar);
            }
        }
    }
}
