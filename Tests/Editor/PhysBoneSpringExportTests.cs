using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UniGLTF;
using UniVRM10;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class PhysBoneSpringExportTests
    {
        static Type Sdk(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);
        static Component PhysBone(GameObject target)
        {
            var type = Sdk(PhysBoneSpringExport.PhysBoneType);
            if (type == null) Assert.Ignore("Real VRChat SDK integration test; SDK-free export is covered separately.");
            return target.AddComponent(type);
        }
        static Transform Child(Transform root, string name, Vector3 position)
        { var t = new GameObject(name).transform; t.SetParent(root, false); t.localPosition = position; return t; }
        static void Set(Component component, string name, object value)
        {
            using var data = new SerializedObject(component);
            var p = data.FindProperty(name); Assert.That(p, Is.Not.Null, name);
            if (value is Vector3 v) p.vector3Value = v;
            else if (value is float f) p.floatValue = f;
            else if (value is bool b) p.boolValue = b;
            else if (value is int n) p.intValue = n;
            else if (value is AnimationCurve curve) p.animationCurveValue = curve;
            else if (value is Object reference) p.objectReferenceValue = reference;
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        [TestCase(0, 2)]
        [TestCase(1, 2)]
        [TestCase(2, 3)]
        public void BranchesAndVirtualEndpointsHaveOneOwnerAndKeepSource(int multi, int expectedChains)
        {
            var source = new GameObject("source"); GameObject copy = null;
            try
            {
                var root = Child(source.transform, "Root", Vector3.zero);
                Child(root, "a", new Vector3(-.1f, -.1f, 0));
                Child(root, "b", new Vector3(.1f, -.1f, 0));
                var pb = PhysBone(root.gameObject);
                Set(pb, "multiChildType", multi); Set(pb, "endpointPosition", new Vector3(0, -.1f, 0));
                copy = Object.Instantiate(source);
                var report = PhysBoneSpringExport.Convert(source, copy, new List<string>());
                Assert.That(report.Chains, Is.EqualTo(expectedChains));
                var springs = copy.GetComponent<Vrm10Instance>().SpringBone.Springs;
                var moving = springs.SelectMany(s => s.Joints.Take(s.Joints.Count - 1)).ToArray();
                Assert.That(moving.Distinct().Count(), Is.EqualTo(moving.Length));
                Assert.That(springs.All(s => s.Center == null), Is.True);
                Assert.That(source.GetComponentsInChildren<Transform>().Length, Is.EqualTo(4));
                Assert.That(source.GetComponentsInChildren<VRM10SpringBoneJoint>(), Is.Empty);
            }
            finally { Object.DestroyImmediate(copy); Object.DestroyImmediate(source); }
        }

        [Test]
        public void CurvesCollidersAndLimitsUseRealSdkSerializedValues()
        {
            var source = new GameObject("source"); GameObject copy = null;
            try
            {
                var root = Child(source.transform, "Hair", Vector3.zero);
                Child(root, "HairTip", Vector3.down * .1f);
                var pb = PhysBone(root.gameObject);
                Set(pb, "endpointPosition", Vector3.down * .1f);
                Set(pb, "pull", .4f); Set(pb, "pullCurve", AnimationCurve.Linear(0, 1, 1, .5f));
                Set(pb, "radius", .02f); Set(pb, "limitType", 1); Set(pb, "maxAngleX", 30f);
                var collider = source.AddComponent(Sdk("VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider"));
                Set(collider, "shapeType", 1); Set(collider, "height", .3f); Set(collider, "radius", .05f);
                using (var data = new SerializedObject(pb))
                {
                    var list = data.FindProperty("colliders"); list.arraySize = 1;
                    list.GetArrayElementAtIndex(0).objectReferenceValue = collider; data.ApplyModifiedPropertiesWithoutUndo();
                }
                copy = Object.Instantiate(source);
                var report = PhysBoneSpringExport.Convert(source, copy, new List<string>());
                Assert.That(report.Colliders, Is.EqualTo(1));
                var spring = copy.GetComponent<Vrm10Instance>().SpringBone.Springs.Single();
                Assert.That(spring.Joints[0].m_stiffnessForce, Is.EqualTo(1.6f).Within(.001));
                Assert.That(spring.Joints[1].m_stiffnessForce, Is.LessThan(spring.Joints[0].m_stiffnessForce));
                Assert.That(spring.Joints[0].m_pitch, Is.EqualTo(30 * Mathf.Deg2Rad).Within(.001));
                var output = spring.ColliderGroups.Single().Colliders.Single();
                Assert.That(output.ColliderType, Is.EqualTo(VRM10SpringBoneColliderTypes.Capsule));
                Assert.That(Vector3.Distance(output.Offset, output.Tail), Is.EqualTo(.2f).Within(.001));
                Assert.That(output.transform, Is.EqualTo(copy.transform));
            }
            finally { Object.DestroyImmediate(copy); Object.DestroyImmediate(source); }
        }

        [Test]
        public void ExistingVrmSettingsWinAndRemovedExplicitRootsDoNotAnimateTheBody()
        {
            var source = new GameObject("source"); GameObject copy = null;
            try
            {
                var hair = Child(source.transform, "Hair", Vector3.up);
                var pb = PhysBone(source); Set(pb, "rootTransform", hair);
                Set(pb, "endpointPosition", Vector3.up * .1f);
                copy = Object.Instantiate(source);
                var copiedHair = copy.transform.Find("Hair");
                var instance = copy.AddComponent<Vrm10Instance>();
                var authored = copiedHair.gameObject.AddComponent<VRM10SpringBoneJoint>(); authored.m_dragForce = .123f;
                var tip = Child(copiedHair, "Tip", Vector3.up * .1f).gameObject.AddComponent<VRM10SpringBoneJoint>();
                instance.SpringBone.Springs.Add(new Vrm10InstanceSpringBone.Spring("authored") { Joints = { authored, tip } });
                Assert.That(PhysBoneSpringExport.Convert(source, copy, new List<string>()).Converted, Is.Zero);
                Assert.That(authored.m_dragForce, Is.EqualTo(.123f));
                PhysBoneSpringExport.PruneRemovedRoots(copy, t => t == copiedHair, new List<string>());
                Assert.That(copy.GetComponents<Component>().Any(PhysBoneSpringExport.IsPhysBone), Is.False);
                Assert.That(source.GetComponents<Component>().Any(PhysBoneSpringExport.IsPhysBone), Is.True);
            }
            finally { Object.DestroyImmediate(copy); Object.DestroyImmediate(source); }
        }

        [Test]
        public void AbsentSdkAndRigidModelsRemainValidWithoutInventedSprings()
        {
            var source = new GameObject("rigid"); var copy = Object.Instantiate(source);
            try
            {
                Assert.That(PhysBoneSpringExport.Convert(source, copy, new List<string>()).Sources, Is.Zero);
                Assert.That(copy.GetComponent<Vrm10Instance>(), Is.Null);
                Assert.Throws<InvalidOperationException>(() => PhysBoneSpringExport.Convert(source, source, null));
            }
            finally { Object.DestroyImmediate(copy); Object.DestroyImmediate(source); }
        }

        [Test]
        public void IgnoredSubtreesDisabledBonesAndOverlappingOwnersAreExplicit()
        {
            var source = new GameObject("source"); GameObject copy = null;
            try
            {
                var root = Child(source.transform, "root", Vector3.zero);
                var ignored = Child(root, "ignored", Vector3.left);
                Child(ignored, "ignoredTip", Vector3.down);
                var kept = Child(root, "kept", Vector3.right);
                Child(kept, "keptTip", Vector3.down);
                var pb = PhysBone(root.gameObject);
                Set(pb, "endpointPosition", Vector3.down * .1f);
                using (var data = new SerializedObject(pb))
                {
                    var list = data.FindProperty("ignoreTransforms"); list.arraySize = 1;
                    list.GetArrayElementAtIndex(0).objectReferenceValue = ignored;
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
                var disabled = PhysBone(Child(source.transform, "disabled", Vector3.zero).gameObject);
                ((Behaviour)disabled).enabled = false;
                copy = Object.Instantiate(source);
                var warnings = new List<string>();
                var report = PhysBoneSpringExport.Convert(source, copy, warnings);
                Assert.That(report.Converted, Is.EqualTo(1)); Assert.That(report.Skipped, Is.EqualTo(1));
                Assert.That(copy.transform.Find("root/ignored").GetComponentsInChildren<VRM10SpringBoneJoint>(), Is.Empty);
                Assert.That(warnings.Any(w => w.Contains("無効または非表示")), Is.True);
                Object.DestroyImmediate(copy);
                var second = PhysBone(source); Set(second, "rootTransform", root);
                copy = Object.Instantiate(source);
                Assert.Throws<InvalidOperationException>(() => PhysBoneSpringExport.Convert(source, copy, warnings));
            }
            finally { Object.DestroyImmediate(copy); Object.DestroyImmediate(source); }
        }

        [Test]
        public void EmptyOrTruncatedSerializedSpringsPreventSaving()
        {
            var result = new PhysBoneSpringExport.Result { Converted = 1, Chains = 1, Joints = 2 };
            var document = GlbDocument.Create(new Dictionary<string, object>(), null);
            Assert.Throws<InvalidOperationException>(() => PhysBoneSpringExport.VerifyOutput(document.Write(), result));
            document.Json["extensions"] = new Dictionary<string, object>
            {
                ["VRMC_springBone"] = new Dictionary<string, object>
                { ["springs"] = new List<object> { new Dictionary<string, object> { ["joints"] = new List<object>() } } }
            };
            Assert.Throws<InvalidOperationException>(() => PhysBoneSpringExport.VerifyOutput(document.Write(), result));
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task ActualExportImportsSpringsAndMovesHairWithoutChangingOriginal(bool full, bool modularAvatar)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var hair = fixture.Source.transform.Find("Independent hair/Head");
            var head = fixture.Source.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Head);
            if (modularAvatar)
            {
                var proxyType = Sdk("nadena.dev.modular_avatar.core.ModularAvatarBoneProxy");
                var markerType = Sdk("nadena.dev.ndmf.runtime.components.NDMFAvatarRoot");
                if (proxyType == null || markerType == null) Assert.Ignore("Optional MA integration requires MA/NDMF.");
                fixture.Source.AddComponent(markerType);
                var proxy = hair.parent.gameObject.AddComponent(proxyType);
                proxyType.GetProperty("target").SetValue(proxy, head);
                var mode = proxyType.GetField("attachmentMode");
                mode.SetValue(proxy, Enum.Parse(mode.FieldType, "AsChildKeepWorldPose"));
            }
            else hair.parent.SetParent(head, true);
            Child(hair, "Spring tip", Vector3.down * .15f);
            var pb = PhysBone(hair.gameObject);
            Set(pb, "pull", .25f); Set(pb, "spring", .6f); Set(pb, "gravity", 0f);
            Set(pb, "immobile", 0f); Set(pb, "limitType", 1); Set(pb, "maxAngleX", 60f);
            var collider = head.gameObject.AddComponent(Sdk("VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider"));
            Set(collider, "radius", .025f); Set(collider, "position", Vector3.left);
            using (var data = new SerializedObject(pb))
            {
                var list = data.FindProperty("colliders"); list.arraySize = 1;
                list.GetArrayElementAtIndex(0).objectReferenceValue = collider; data.ApplyModifiedPropertiesWithoutUndo();
            }
            foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = Shader.Find("lilToon");
            var positions = fixture.Source.GetComponentsInChildren<Transform>().Select(t => t.localPosition).ToArray();
            var originalParent = hair.parent.parent;
            var warnings = new List<string>(); Vrm10Instance imported = null;
            try
            {
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Spring test", "Tests", warnings,
                    exporterVersion: full ? "test" : null, lilToonVersion: full ? "2.3.4" : null,
                    blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                var solver = new Vrm10FastSpringboneRuntimeStandalone();
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller(), springboneRuntime: solver);
                Assert.That(imported.SpringBone.Springs.Count, Is.EqualTo(1));
                imported.UpdateType = Vrm10Instance.UpdateTypes.None;
                Assert.That(solver.ReconstructSpringBone(), Is.True);
                for (var i = 0; i < 30; i++) solver.Process(1f / 60);
                var joint = imported.SpringBone.Springs.Single().Joints[0].transform;
                Assert.That(joint.GetComponent<VRM10SpringBoneJoint>().m_pitch, Is.EqualTo(60 * Mathf.Deg2Rad).Within(.001));
                var rest = joint.localRotation;
                joint.parent.localRotation *= Quaternion.Euler(0, 0, 25);
                solver.Process(1f / 60);
                Assert.That(Quaternion.Angle(rest, joint.localRotation), Is.GreaterThan(1), "A serialized count alone does not prove secondary motion.");
                for (var i = 0; i < 240; i++) solver.Process(1f / 60);
                Assert.That(Quaternion.Angle(rest, joint.localRotation), Is.LessThan(1), "Hair must settle after the parent stops.");
                var importedCollider = imported.SpringBone.Springs.Single().ColliderGroups.Single().Colliders.Single();
                var tip = imported.SpringBone.Springs.Single().Joints.Last().transform;
                importedCollider.Offset = importedCollider.transform.InverseTransformPoint(tip.position + Vector3.right * .01f);
                Assert.That(solver.ReconstructSpringBone(), Is.True);
                for (var i = 0; i < 60; i++) solver.Process(1f / 60);
                Assert.That(Vector3.Distance(tip.position, importedCollider.transform.TransformPoint(importedCollider.Offset)),
                    Is.GreaterThan(.024f), "Imported sphere must push the hair endpoint outside its radius.");
                Assert.That(fixture.Source.GetComponentsInChildren<Transform>().Select(t => t.localPosition), Is.EqualTo(positions));
                Assert.That(hair.parent.parent, Is.EqualTo(originalParent));
                Assert.That(fixture.Source.GetComponentsInChildren<VRM10SpringBoneJoint>(), Is.Empty);
                Assert.That(warnings.Any(w => w.Contains("PhysBone変換:")), Is.True);
                var artifact = Environment.GetEnvironmentVariable("VRVLOG_SPRING_FIXTURE");
                if (full && !string.IsNullOrEmpty(artifact)) System.IO.File.WriteAllBytes(artifact, bytes);
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); }
        }
    }
}
