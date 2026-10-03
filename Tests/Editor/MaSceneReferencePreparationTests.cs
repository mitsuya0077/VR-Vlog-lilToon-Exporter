using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    // Native serialization fixtures: the installed MA getter can resolve an
    // avatar-relative path while its serialized direct target is still stale.
    public sealed class MaSceneReferencePreparationTests
    {
        const string MaNamespace = "nadena.dev.modular_avatar.core.";
        readonly List<Object> owned = new List<Object>();
        GameObject source, clone, external;
        Component merge;
        Transform mainRig, mainArm, clothingRig, clothingArm, externalRig;
        SkinnedMeshRenderer sleeve;
        Mesh originalMesh;

        [TearDown]
        public void TearDown()
        {
            NdmfPreparationTests.FakeProcessor.Action = null;
            NdmfPreparationTests.FakeProcessor.OptimizationAction = null;
            if (clone != null) Object.DestroyImmediate(clone);
            foreach (var item in owned.AsEnumerable().Reverse())
                if (item != null) Object.DestroyImmediate(item);
            owned.Clear();
        }

        T Own<T>(T item) where T : Object { owned.Add(item); return item; }

        static Type InstalledType(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(name)).FirstOrDefault(type => type != null);

        static Transform Child(Transform parent, string name, Vector3 position)
        {
            var child = new GameObject(name).transform;
            child.SetParent(parent, false);
            child.localPosition = position;
            return child;
        }

        void CreateFixture(bool targetIsAvatarRoot = false)
        {
            var mergeType = InstalledType(MaNamespace + "ModularAvatarMergeArmature");
            var markerType = InstalledType("nadena.dev.ndmf.runtime.components.NDMFAvatarRoot");
            var referenceType = InstalledType(MaNamespace + "AvatarObjectReference");
            if (mergeType == null || markerType == null || referenceType?.GetMethod("Get", new[] { typeof(SerializedProperty) }) == null)
                Assert.Ignore("Install supported Modular Avatar and NDMF for native scene-reference preparation tests.");

            source = Own(new GameObject("scene-reference avatar"));
            source.AddComponent(markerType);
            mainRig = targetIsAvatarRoot ? source.transform : Child(source.transform, "MainRig", Vector3.zero);
            mainArm = Child(mainRig, "UpperArm.L", new Vector3(.25f, .7f, 0));
            clothingRig = Child(source.transform, "clothing-merge", Vector3.zero);
            clothingArm = Child(clothingRig, "UpperArm.L", mainArm.localPosition);
            clothingRig.gameObject.SetActive(false);
            merge = clothingRig.gameObject.AddComponent(mergeType);
            var mode = mergeType.GetField("LockMode");
            if (mode != null) mode.SetValue(merge, Enum.Parse(mode.FieldType, "NotLocked"));
            mergeType.GetField("mangleNames").SetValue(merge, false);
            SetRawReference(merge, "mergeTarget", mainRig.gameObject,
                targetIsAvatarRoot ? "$$$AVATAR_ROOT$$$" : "MainRig");
            clothingRig.gameObject.SetActive(true);

            external = Own(new GameObject("other scene avatar"));
            external.AddComponent(markerType);
            externalRig = Child(external.transform, "MainRig", new Vector3(3, 2, 1));
            Child(externalRig, "UpperArm.L", mainArm.localPosition);

            sleeve = Child(source.transform, "Sleeve", Vector3.zero).gameObject.AddComponent<SkinnedMeshRenderer>();
            originalMesh = Own(new Mesh
            {
                name = "scene-reference sleeve mesh",
                vertices = new[] { new Vector3(.5f, .7f, 0), new Vector3(.7f, .7f, 0), new Vector3(.5f, .8f, 0) },
                triangles = new[] { 0, 1, 2 },
                normals = Enumerable.Repeat(Vector3.forward, 3).ToArray(),
                bindposes = new[] { clothingArm.worldToLocalMatrix * sleeve.transform.localToWorldMatrix },
                boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 3).ToArray()
            });
            sleeve.sharedMesh = originalMesh;
            sleeve.bones = new[] { clothingArm };
            sleeve.rootBone = clothingRig;
        }

        static void SetRawReference(Component owner, string path, GameObject target, string referencePath)
        {
            using var serialized = new SerializedObject(owner);
            var reference = serialized.FindProperty(path);
            Assert.That(reference, Is.Not.Null, path);
            reference.FindPropertyRelative("targetObject").objectReferenceValue = target;
            reference.FindPropertyRelative("referencePath").stringValue = referencePath;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        static void AssertRawReference(Component owner, string path, GameObject target, string referencePath)
        {
            using var serialized = new SerializedObject(owner);
            var reference = serialized.FindProperty(path);
            Assert.That(reference.FindPropertyRelative("targetObject").objectReferenceValue, Is.SameAs(target));
            Assert.That(reference.FindPropertyRelative("referencePath").stringValue, Is.EqualTo(referencePath));
        }

        static GameObject EffectiveSerializedTarget(Component owner, string path)
        {
            using var serialized = new SerializedObject(owner);
            return (GameObject)InstalledType(MaNamespace + "AvatarObjectReference")
                .GetMethod("Get", new[] { typeof(SerializedProperty) })
                .Invoke(null, new object[] { serialized.FindProperty(path) });
        }

        void AssertRawGuardRejectsBeforeNormalization()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var dependencies = typeof(NdmfExportPreparation).GetMethod("Dependencies", flags, null,
                new[] { typeof(GameObject) }, null).Invoke(null, new object[] { clone });
            var error = Assert.Throws<TargetInvocationException>(() => typeof(NdmfExportPreparation)
                .GetMethod("RequireCopySceneReferences", flags).Invoke(null, new[] { (object)clone, dependencies }));
            Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>());
            StringAssert.Contains("外部を指すシーン参照", error.InnerException.Message);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InstalledMaResolvesStaleDirectTargetAndMergesOnlyTheOwnedAvatar(bool targetIsAvatarRoot)
        {
            CreateFixture(targetIsAvatarRoot);
            var path = targetIsAvatarRoot ? "$$$AVATAR_ROOT$$$" : "MainRig";
            SetRawReference(merge, "mergeTarget", externalRig.gameObject, path);
            var vertices = originalMesh.vertices;
            var externalPosition = externalRig.localPosition;
            clone = Object.Instantiate(source);
            var copiedMerge = clone.transform.Find("clothing-merge").GetComponent(merge.GetType());
            var copiedRig = targetIsAvatarRoot ? clone.transform : clone.transform.Find("MainRig");
            AssertRawReference(copiedMerge, "mergeTarget", externalRig.gameObject, path);
            Assert.That(EffectiveSerializedTarget(copiedMerge, "mergeTarget"), Is.SameAs(copiedRig.gameObject));
            AssertRawGuardRejectsBeforeNormalization();

            var before = Own(new Mesh());
            var after = Own(new Mesh());
            using (NdmfExportPreparation.Prepare(source, clone))
            {
                var copiedArm = copiedRig.Find("UpperArm.L");
                var copiedSleeve = clone.transform.Find("Sleeve").GetComponent<SkinnedMeshRenderer>();
                Assert.That(copiedSleeve.bones[0], Is.SameAs(copiedArm));
                copiedSleeve.BakeMesh(before);
                copiedArm.localRotation = Quaternion.Euler(0, 0, -60);
                copiedSleeve.BakeMesh(after);
                Assert.That(Vector3.Distance(before.vertices[0], after.vertices[0]), Is.GreaterThan(.1f),
                    "The installed MA merge must bind the sleeve to the copied rig.");
                AssertRawReference(merge, "mergeTarget", externalRig.gameObject, path);
                Assert.That(clothingArm.parent, Is.SameAs(clothingRig));
                Assert.That(clothingRig.parent, Is.SameAs(source.transform));
                Assert.That(sleeve.bones[0], Is.SameAs(clothingArm));
                Assert.That(sleeve.sharedMesh, Is.SameAs(originalMesh));
                CollectionAssert.AreEqual(vertices, originalMesh.vertices);
                Assert.That(mainArm.localRotation, Is.EqualTo(Quaternion.identity));
                Assert.That(clothingArm.localRotation, Is.EqualTo(Quaternion.identity));
                Assert.That(externalRig.parent, Is.SameAs(external.transform));
                Assert.That(externalRig.localPosition, Is.EqualTo(externalPosition));
                Assert.That(externalRig.Find("UpperArm.L").localRotation, Is.EqualTo(Quaternion.identity));
                Assert.That(merge != null, Is.True);
            }
            Assert.That(originalMesh != null, Is.True, "Cleanup must not own the source mesh.");
        }

        [Test]
        public void UnresolvedStaleMaPathRemainsRejectedBeforeAnyPass()
        {
            CreateFixture();
            SetRawReference(merge, "mergeTarget", externalRig.gameObject, "missing rig");
            clone = Object.Instantiate(source);
            ResetFakeBridge();
            var error = Assert.Throws<InvalidOperationException>(() => NdmfExportPreparation.ProcessClone(source, clone, FakeBridge()));
            StringAssert.Contains("外部を指すシーン参照", error.Message);
            StringAssert.Contains("ModularAvatarMergeArmature", error.Message);
            StringAssert.Contains("mergeTarget.targetObject", error.Message);
            Assert.That(NdmfPreparationTests.FakeProcessor.Calls, Is.EqualTo(0));
            AssertRawReference(merge, "mergeTarget", externalRig.gameObject, "missing rig");
            AssertRawReference(clone.transform.Find("clothing-merge").GetComponent(merge.GetType()),
                "mergeTarget", externalRig.gameObject, "missing rig");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OrdinaryExternalComponentReferencesRemainRejectedWithTheExactField(bool probeAnchor)
        {
            CreateFixture();
            var externalRenderer = externalRig.gameObject.AddComponent<MeshRenderer>();
            if (probeAnchor) sleeve.probeAnchor = externalRig;
            else source.AddComponent<NdmfSharedAssetHolder>().Renderer = externalRenderer;
            clone = Object.Instantiate(source);
            ResetFakeBridge();
            var error = Assert.Throws<InvalidOperationException>(() => NdmfExportPreparation.ProcessClone(source, clone, FakeBridge()));
            StringAssert.Contains("外部を指すシーン参照", error.Message);
            StringAssert.Contains(probeAnchor ? "SkinnedMeshRenderer" : nameof(NdmfSharedAssetHolder), error.Message);
            StringAssert.Contains(probeAnchor ? "m_ProbeAnchor" : "Renderer", error.Message);
            StringAssert.Contains("other scene avatar/MainRig", error.Message);
            Assert.That(NdmfPreparationTests.FakeProcessor.Calls, Is.EqualTo(0));
            if (probeAnchor) Assert.That(sleeve.probeAnchor, Is.SameAs(externalRig));
            else Assert.That(source.GetComponent<NdmfSharedAssetHolder>().Renderer, Is.SameAs(externalRenderer));
            Assert.That(externalRig.parent, Is.SameAs(external.transform));
        }

        [Test]
        public void NestedMaReferencesAreResolvedInEverySerializedArrayElementWithoutEditingTheSource()
        {
            CreateFixture();
            var shapeType = InstalledType(MaNamespace + "ModularAvatarShapeChanger");
            var entryType = InstalledType(MaNamespace + "ChangedShape");
            var referenceType = InstalledType(MaNamespace + "AvatarObjectReference");
            if (shapeType == null || entryType == null) Assert.Ignore("Install MA's shape changer to test nested references.");
            var setting = Child(source.transform, "nested shape setting", Vector3.zero).gameObject.AddComponent(shapeType);
            var shapes = (IList)shapeType.GetProperty("Shapes").GetValue(setting);
            for (var i = 0; i < 2; i++)
            {
                var entry = Activator.CreateInstance(entryType);
                entryType.GetField("Object").SetValue(entry, Activator.CreateInstance(referenceType));
                entryType.GetField("ShapeName").SetValue(entry, "fixture shape " + i);
                shapes.Add(entry);
            }
            for (var i = 0; i < 2; i++)
            {
                SetRawReference(setting, "m_shapes.Array.data[" + i + "].Object", externalRig.gameObject, "Sleeve");
            }
            clone = Object.Instantiate(source);
            AssertRawGuardRejectsBeforeNormalization();
            ResetFakeBridge();
            NdmfPreparationTests.FakeProcessor.Action = root =>
            {
                var copiedSetting = root.transform.Find("nested shape setting").GetComponent(shapeType);
                for (var i = 0; i < 2; i++)
                    AssertRawReference(copiedSetting, "m_shapes.Array.data[" + i + "].Object",
                        root.transform.Find("Sleeve").gameObject, "Sleeve");
            };
            using (NdmfExportPreparation.ProcessClone(source, clone, FakeBridge()))
                for (var i = 0; i < 2; i++)
                    AssertRawReference(setting, "m_shapes.Array.data[" + i + "].Object", externalRig.gameObject, "Sleeve");
            Assert.That(NdmfPreparationTests.FakeProcessor.Calls, Is.EqualTo(1));
        }

        [Test]
        public void InternalSceneReferenceDoesNotPreventTraversalAndIsolationOfItsQueuedRenderer()
        {
            CreateFixture();
            source.AddComponent<NdmfSharedAssetHolder>().Renderer = sleeve;
            clone = Object.Instantiate(source);
            ResetFakeBridge();
            NdmfPreparationTests.FakeProcessor.Action = root =>
            {
                var copiedSleeve = root.transform.Find("Sleeve").GetComponent<SkinnedMeshRenderer>();
                Assert.That(root.GetComponent<NdmfSharedAssetHolder>().Renderer, Is.SameAs(copiedSleeve));
                Assert.That(copiedSleeve.sharedMesh, Is.Not.SameAs(originalMesh),
                    "A reference to a queued component must not suppress that component's asset traversal.");
                copiedSleeve.sharedMesh.vertices = Enumerable.Repeat(Vector3.down, 3).ToArray();
            };
            var originalVertices = originalMesh.vertices;
            using (NdmfExportPreparation.ProcessClone(source, clone, FakeBridge()))
            {
                Assert.That(source.GetComponent<NdmfSharedAssetHolder>().Renderer, Is.SameAs(sleeve));
                Assert.That(sleeve.sharedMesh, Is.SameAs(originalMesh));
                CollectionAssert.AreEqual(originalVertices, originalMesh.vertices);
            }
        }

        static void ResetFakeBridge()
        {
            NdmfPreparationTests.FakeContext.Last = null;
            NdmfPreparationTests.FakeContext.Success = true;
            NdmfPreparationTests.FakeContext.FailFinish = false;
            NdmfPreparationTests.FakeContext.FailSaverDispose = false;
            NdmfPreparationTests.FakeContext.ReportedErrors.Clear();
            NdmfPreparationTests.FakeDirectoryScope.FailDispose = false;
            NdmfPreparationTests.FakeDirectoryScope.Current = "scene-reference fixture";
            NdmfPreparationTests.FakeRegistry.Selected = new NdmfPreparationTests.FakeProvider();
            NdmfPreparationTests.FakeProcessor.Calls = 0;
            NdmfPreparationTests.FakeProcessor.Action = null;
            NdmfPreparationTests.FakeProcessor.OptimizationAction = null;
            NdmfPreparationTests.FakeProcessor.Ranges.Clear();
        }

        static NdmfExportPreparation.Bridge FakeBridge() => (NdmfExportPreparation.Bridge)typeof(NdmfPreparationTests)
            .GetMethod("Resolve", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { "1.14.8" });
    }
}
