using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UniGLTF;
using UniVRM10;
using UnityEditor;
using UnityEditor.Animations;
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

        // A generic AAO avatar uses its root Animator controller, unlike a
        // VRChat descriptor or authored Vrm10Instance. Give the synthetic
        // generic fixture a real idle controller without altering AAO settings.
        sealed class StaticAnimatorController : IDisposable
        {
            internal readonly AnimatorController Controller;
            readonly Animator animator;
            readonly RuntimeAnimatorController previous;
            readonly AnimatorStateMachine machine;
            readonly AnimatorState idle;

            internal StaticAnimatorController(Animator target)
            {
                animator = target; previous = target.runtimeAnimatorController;
                Controller = new AnimatorController { name = "PhysBone generic fixture controller" };
                Controller.AddLayer("Base Layer");
                machine = Controller.layers[0].stateMachine;
                idle = machine.AddState("Idle"); idle.writeDefaultValues = false;
                machine.defaultState = idle;
                animator.runtimeAnimatorController = Controller;
            }

            public void Dispose()
            {
                if (animator != null) animator.runtimeAnimatorController = previous;
                Object.DestroyImmediate(idle); Object.DestroyImmediate(machine); Object.DestroyImmediate(Controller);
            }
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
                Assert.That(output.transform.parent, Is.EqualTo(copy.transform));
                Assert.That(output.transform.localToWorldMatrix, Is.EqualTo(copy.transform.localToWorldMatrix));
                Assert.That(source.GetComponentsInChildren<VRM10SpringBoneCollider>(), Is.Empty);
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

        [Test]
        public void InactiveExplicitRootsChildrenAndColliderRootsAreSkipped()
        {
            var source = new GameObject("source"); GameObject copy = null;
            try
            {
                var hidden = Child(source.transform, "hidden", Vector3.up); hidden.gameObject.SetActive(false);
                Child(hidden, "tip", Vector3.down);
                var externalHost = PhysBone(source); Set(externalHost, "rootTransform", hidden);
                var active = Child(source.transform, "active", Vector3.zero);
                var pb = PhysBone(active.gameObject); Set(pb, "endpointPosition", Vector3.down * .1f);
                var hiddenChild = Child(active, "hiddenChild", Vector3.down); hiddenChild.gameObject.SetActive(false);
                var collider = source.AddComponent(Sdk("VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider"));
                Set(collider, "rootTransform", hidden);
                using (var data = new SerializedObject(pb))
                {
                    var list = data.FindProperty("colliders"); list.arraySize = 1;
                    list.GetArrayElementAtIndex(0).objectReferenceValue = collider; data.ApplyModifiedPropertiesWithoutUndo();
                }
                copy = Object.Instantiate(source); var warnings = new List<string>();
                var result = PhysBoneSpringExport.Convert(source, copy, warnings);
                Assert.That(result.Skipped, Is.EqualTo(1)); Assert.That(result.Chains, Is.EqualTo(1));
                Assert.That(result.Colliders, Is.Zero);
                Assert.That(copy.transform.Find("hidden").GetComponentsInChildren<VRM10SpringBoneJoint>(true), Is.Empty);
                Assert.That(copy.transform.Find("active/hiddenChild").GetComponent<VRM10SpringBoneJoint>(), Is.Null);
                Assert.That(warnings.Any(w => w.Contains("Root Transformが非表示")), Is.True);
            }
            finally { Object.DestroyImmediate(copy); Object.DestroyImmediate(source); }
        }

        [Test]
        public void ExistingTerminalFieldsAndVerificationBaselinesAreProtected()
        {
            var source = new GameObject("source"); GameObject copy = null;
            try
            {
                var root = Child(source.transform, "root", Vector3.zero);
                var terminal = Child(root, "terminal", Vector3.down);
                var a = root.gameObject.AddComponent<VRM10SpringBoneJoint>();
                var b = terminal.gameObject.AddComponent<VRM10SpringBoneJoint>(); b.m_dragForce = .123f; b.m_pitch = .234f;
                source.AddComponent<Vrm10Instance>().SpringBone.Springs.Add(new Vrm10InstanceSpringBone.Spring("authored") { Joints = { a, b } });
                source.AddComponent<VRM10SpringBoneCollider>();
                var pb = PhysBone(terminal.gameObject); Set(pb, "endpointPosition", Vector3.down * .1f);
                var independent = Child(source.transform, "independent", Vector3.right);
                var other = PhysBone(independent.gameObject); Set(other, "endpointPosition", Vector3.down * .1f);
                copy = Object.Instantiate(source);
                var result = PhysBoneSpringExport.Convert(source, copy, new List<string>());
                var kept = copy.transform.Find("root/terminal").GetComponent<VRM10SpringBoneJoint>();
                Assert.That(kept.m_dragForce, Is.EqualTo(.123f)); Assert.That(kept.m_pitch, Is.EqualTo(.234f));
                Assert.That(result.Converted, Is.EqualTo(1)); Assert.That(result.ExistingChains, Is.EqualTo(1));
                Assert.That(result.ExistingJoints, Is.EqualTo(1)); Assert.That(result.ExistingColliders, Is.EqualTo(1));
                // A serialized old chain alone must not mask the newly converted chain disappearing.
                var document = GlbDocument.Create(new Dictionary<string, object> {
                    ["extensions"] = new Dictionary<string, object> {
                        ["VRMC_springBone"] = new Dictionary<string, object> {
                            ["springs"] = new List<object> { new Dictionary<string, object> {
                                ["joints"] = new List<object> { new Dictionary<string, object>(), new Dictionary<string, object>() } } },
                            ["colliders"] = new List<object> { new Dictionary<string, object>() }
                        } } }, null);
                Assert.Throws<InvalidOperationException>(() => PhysBoneSpringExport.VerifyOutput(document.Write(), result));
            }
            finally { Object.DestroyImmediate(copy); Object.DestroyImmediate(source); }
        }

        [Test]
        public async Task AuthoredRootColliderSurvivesWithoutAnyPhysBoneOrSdkDependency()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var hair = fixture.Source.transform.Find("Independent hair/Head");
            var head = fixture.Source.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Head);
            hair.parent.SetParent(head, true);
            var authored = fixture.Source.AddComponent<Vrm10Instance>();
            var joint = hair.gameObject.AddComponent<VRM10SpringBoneJoint>();
            var tip = Child(hair, "Authored tip", Vector3.down * .15f).gameObject.AddComponent<VRM10SpringBoneJoint>();
            var collider = fixture.Source.AddComponent<VRM10SpringBoneCollider>();
            collider.Radius = .025f; collider.Offset = Vector3.left;
            var group = fixture.Source.AddComponent<VRM10SpringBoneColliderGroup>(); group.Colliders.Add(collider);
            authored.SpringBone.ColliderGroups.Add(group);
            authored.SpringBone.Springs.Add(new Vrm10InstanceSpringBone.Spring("authored") {
                Joints = { joint, tip }, ColliderGroups = { group }
            });
            foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = Shader.Find("lilToon");
            Vrm10Instance imported = null;
            try
            {
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Authored spring", "Tests",
                    blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                var restored = imported.SpringBone.Springs.Single().ColliderGroups.Single().Colliders.Single();
                Assert.That(restored.Radius, Is.EqualTo(collider.Radius).Within(.00001));
                Assert.That(Vector3.Distance(restored.transform.TransformPoint(restored.Offset),
                    collider.transform.TransformPoint(collider.Offset)), Is.LessThan(.00001));
                Assert.That(fixture.Source.GetComponent<VRM10SpringBoneCollider>(), Is.SameAs(collider));
                Assert.That(group.Colliders.Single(), Is.SameAs(collider));
                Assert.That(fixture.Source.transform.Find("VRVlog Spring Colliders"), Is.Null);
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); }
        }

        [TestCase(false, false, false)]
        [TestCase(true, false, false)]
        [TestCase(true, true, false)]
        [TestCase(true, false, true)]
        public Task ActualExportImportsSpringsAndMovesHairWithoutChangingOriginal(bool full, bool modularAvatar, bool rootCollider) =>
            ExportImportsSpringsAndMovesHair(full, modularAvatar, rootCollider, false);

        [TestCase(false)]
        [TestCase(true)]
        public Task InstalledAvatarOptimizerPreservesSpringMotionAndCollisionsAfterFullExport(bool full)
        {
            var optimizer = Sdk("Anatawa12.AvatarOptimizer.TraceAndOptimize");
            if (optimizer == null) Assert.Ignore("Optional AAO integration requires installed Avatar Optimizer.");
            Assert.That(Sdk("nadena.dev.ndmf.BuildContext"), Is.Not.Null, "Installed AAO requires its NDMF dependency.");
            Assert.That(ExportOptimizationMarker.AvatarOptimizerAdapterAvailable, Is.True,
                "The installed AAO compatibility adapter must compile and register.");
            return ExportImportsSpringsAndMovesHair(full, false, false, true);
        }

        async Task ExportImportsSpringsAndMovesHair(bool full, bool modularAvatar, bool rootCollider, bool avatarOptimizer)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            using var controller = avatarOptimizer ? new StaticAnimatorController(fixture.Source.GetComponent<Animator>()) : null;
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
            var collider = (rootCollider ? fixture.Source : head.gameObject).AddComponent(Sdk("VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider"));
            Set(collider, "radius", .025f); Set(collider, "position", Vector3.left);
            Component duplicateCollider = null;
            if (avatarOptimizer)
            {
                duplicateCollider = head.gameObject.AddComponent(collider.GetType());
                Set(duplicateCollider, "radius", .025f); Set(duplicateCollider, "position", Vector3.left);
                fixture.Source.AddComponent(Sdk("Anatawa12.AvatarOptimizer.TraceAndOptimize"));
                Assert.That(NdmfExportPreparation.NeedsProcessing(fixture.Source), Is.True,
                    "AAO authoring alone must start the NDMF build.");
                Assert.That(fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>().All(skin =>
                    skin.bones[0] == hair && skin.sharedMesh.boneWeights.All(weight => weight.boneIndex0 == 0 && weight.weight0 == 1)), Is.True,
                    "The PhysBone must drive weighted visible geometry, so AAO cannot discard it as unused.");
            }
            using (var data = new SerializedObject(pb))
            {
                var list = data.FindProperty("colliders"); list.arraySize = avatarOptimizer ? 2 : 1;
                list.GetArrayElementAtIndex(0).objectReferenceValue = collider;
                if (avatarOptimizer) list.GetArrayElementAtIndex(1).objectReferenceValue = duplicateCollider;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = Shader.Find("lilToon");
            // A scene-root collider must survive on an identity child; an
            // exported bone's orphan is serialized even without a group.
            if (!avatarOptimizer)
            {
                fixture.Source.AddComponent<VRM10SpringBoneCollider>().Radius = .123f;
                head.gameObject.AddComponent<VRM10SpringBoneCollider>().Radius = .234f;
            }
            var positions = fixture.Source.GetComponentsInChildren<Transform>().Select(t => t.localPosition).ToArray();
            var originalParent = hair.parent.parent;
            var sourceSkins = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
            var sourceBones = sourceSkins.Select(skin => skin.bones).ToArray();
            var sourceVertices = fixture.Mesh.vertices;
            var sourceWeights = fixture.Mesh.boneWeights;
            var sourceBinds = fixture.Mesh.bindposes;
            var sourcePhysBone = EditorJsonUtility.ToJson(pb);
            var sourceCollider = EditorJsonUtility.ToJson(collider);
            var sourceDuplicate = duplicateCollider != null ? EditorJsonUtility.ToJson(duplicateCollider) : null;
            var warnings = new List<string>(); Vrm10Instance imported = null;
            try
            {
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Spring test", "Tests", warnings,
                    exporterVersion: full ? "test" : null, lilToonVersion: full ? "2.3.4" : null,
                    blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                using var solver = new Vrm10FastSpringboneRuntimeStandalone();
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller(), springboneRuntime: solver);
                Assert.That(imported.SpringBone.Springs.Count, Is.EqualTo(1));
                Assert.That(imported.GetComponentsInChildren<VRM10SpringBoneCollider>().Length, Is.EqualTo(avatarOptimizer ? 1 : 3),
                    avatarOptimizer ? "AAO must actually merge the two referenced equivalent colliders before VRM conversion." :
                    "Both authored unreferenced colliders and the converted collider must survive export.");
                imported.UpdateType = Vrm10Instance.UpdateTypes.None;
                Assert.That(solver.ReconstructSpringBone(), Is.True);
                for (var i = 0; i < 30; i++) solver.Process(1f / 60);
                var joint = imported.SpringBone.Springs.Single().Joints[0].transform;
                Assert.That(joint.GetComponent<VRM10SpringBoneJoint>().m_pitch, Is.EqualTo(60 * Mathf.Deg2Rad).Within(.001));
                var rest = joint.localRotation;
                Assert.That(imported.TryGetBoneTransform(HumanBodyBones.Head, out var importedHead), Is.True);
                importedHead.localRotation *= Quaternion.Euler(0, 0, 25);
                solver.Process(1f / 60);
                Assert.That(Quaternion.Angle(rest, joint.localRotation), Is.GreaterThan(1), "A serialized count alone does not prove secondary motion.");
                for (var i = 0; i < 240; i++) solver.Process(1f / 60);
                Assert.That(Quaternion.Angle(rest, joint.localRotation), Is.LessThan(1), "Hair must settle after the parent stops.");
                var importedCollider = imported.SpringBone.Springs.Single().ColliderGroups.Single().Colliders.Single();
                Assert.That(importedCollider.Radius, Is.EqualTo(.025f).Within(.00001),
                    "Omitted scene-root components must not shift the group's collider index.");
                var tip = imported.SpringBone.Springs.Single().Joints.Last().transform;
                importedCollider.Offset = importedCollider.transform.InverseTransformPoint(tip.position + Vector3.right * .01f);
                Assert.That(solver.ReconstructSpringBone(), Is.True);
                for (var i = 0; i < 60; i++) solver.Process(1f / 60);
                Assert.That(Vector3.Distance(tip.position, importedCollider.transform.TransformPoint(importedCollider.Offset)),
                    Is.GreaterThan(.024f), "Imported sphere must push the hair endpoint outside its radius.");
                Assert.That(fixture.Source.GetComponentsInChildren<Transform>().Select(t => t.localPosition), Is.EqualTo(positions));
                Assert.That(hair.parent.parent, Is.EqualTo(originalParent));
                Assert.That(sourceSkins.All(skin => skin.sharedMesh == fixture.Mesh), Is.True);
                for (var i = 0; i < sourceSkins.Length; i++) Assert.That(sourceSkins[i].bones, Is.EqualTo(sourceBones[i]));
                Assert.That(fixture.Mesh.vertices, Is.EqualTo(sourceVertices));
                Assert.That(fixture.Mesh.boneWeights, Is.EqualTo(sourceWeights));
                Assert.That(fixture.Mesh.bindposes, Is.EqualTo(sourceBinds));
                Assert.That(EditorJsonUtility.ToJson(pb), Is.EqualTo(sourcePhysBone));
                Assert.That(EditorJsonUtility.ToJson(collider), Is.EqualTo(sourceCollider));
                if (avatarOptimizer)
                {
                    Assert.That(fixture.Source.GetComponent<Animator>().runtimeAnimatorController, Is.SameAs(controller.Controller));
                    Assert.That(controller.Controller.layers.Single().stateMachine.states.Single().state.name, Is.EqualTo("Idle"));
                    Assert.That(duplicateCollider != null, Is.True);
                    Assert.That(EditorJsonUtility.ToJson(duplicateCollider), Is.EqualTo(sourceDuplicate));
                    Assert.That(fixture.Source.GetComponentsInChildren<Component>().Count(component => component != null &&
                        component.GetType() == collider.GetType()), Is.EqualTo(2), "Only the disposable clone may merge colliders.");
                    Assert.That(fixture.Source.GetComponent<ExportOptimizationMarker>(), Is.Null);
                }
                Assert.That(fixture.Source.GetComponentsInChildren<VRM10SpringBoneJoint>(), Is.Empty);
                Assert.That(warnings.Any(w => w.Contains("PhysBone変換:")), Is.True);
                var artifact = Environment.GetEnvironmentVariable("VRVLOG_SPRING_FIXTURE");
                if (full && !string.IsNullOrEmpty(artifact)) System.IO.File.WriteAllBytes(artifact, bytes);
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); }
        }
    }
}
