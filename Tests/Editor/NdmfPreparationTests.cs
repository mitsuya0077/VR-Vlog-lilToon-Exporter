using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    // These doubles exercise the versioned reflection protocol and ownership.
    // They do not replace a Unity test with installed MA/NDMF packages.
    public sealed class NdmfPreparationTests
    {
        private GameObject source, clone;
        private Mesh original, generated;
        private Material unrelated;

        [SetUp]
        public void SetUp()
        {
            source = new GameObject("source");
            clone = new GameObject("copy");
            original = MakeMesh("__VRVlog_Menu_fixture");
            source.AddComponent<SkinnedMeshRenderer>().sharedMesh = original;
            clone.AddComponent<SkinnedMeshRenderer>().sharedMesh = original;
            FakeContext.Last = null;
            FakeContext.Success = true;
            FakeContext.FailFinish = false;
            FakeContext.FailSaverDispose = false;
            FakeDirectoryScope.FailDispose = false;
            FakeDirectoryScope.Current = "original-directory";
            FakeRegistry.Selected = new FakeProvider();
            FakeProcessor.Action = null;
            FakeProcessor.Calls = 0;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(source);
            Object.DestroyImmediate(clone);
            if (original != null) Object.DestroyImmediate(original);
            if (generated != null) Object.DestroyImmediate(generated);
            if (unrelated != null) Object.DestroyImmediate(unrelated);
        }

        [Test]
        public void RunsCanonicalPhasesInMemoryAndOwnsOnlyNewReachableAssets()
        {
            FakeProcessor.Action = root =>
            {
                Assert.AreEqual(null, FakeDirectoryScope.Current);
                generated = MakeMesh("__VRVlog_Menu_fixture");
                root.GetComponent<SkinnedMeshRenderer>().sharedMesh = generated;
                unrelated = new Material(Shader.Find("Unlit/Color"));
            };
            using (NdmfExportPreparation.ProcessClone(source, clone, Resolve()))
            {
                Assert.AreEqual(1, FakeProcessor.Calls);
                Assert.AreSame(FakePhase.Start, FakeProcessor.First);
                Assert.AreSame(FakePhase.Transforming, FakeProcessor.Last);
                Assert.AreSame(FakeRegistry.Selected, FakeContext.Last.Platform);
                Assert.IsNull(FakeContext.Last.AssetPath);
                Assert.IsTrue(FakeContext.Last.Finished);
                Assert.IsTrue(FakeContext.Last.Saver.Disposed);
                Assert.AreEqual("original-directory", FakeDirectoryScope.Current);
                Assert.IsTrue(generated != null);
            }
            Assert.IsTrue(generated == null);
            Assert.IsTrue(original != null);
            Assert.IsTrue(unrelated != null, "Global resource collection must not claim another owner's assets.");
            Assert.AreSame(original, source.GetComponent<SkinnedMeshRenderer>().sharedMesh);
        }

        [Test]
        public void UsesGenericOnlyWhenSourceHasNoPrimaryPlatform()
        {
            FakeRegistry.Selected = null;
            using (NdmfExportPreparation.ProcessClone(source, clone, Resolve())) { }
            Assert.AreSame(FakeGeneric.Instance, FakeContext.Last.Platform);
        }

        [Test]
        public void LoggedBuildErrorStopsExportAndReleasesGeneratedAssets()
        {
            FakeContext.Success = false;
            FakeProcessor.Action = ReplaceWithGenerated;
            Assert.Throws<InvalidOperationException>(() => NdmfExportPreparation.ProcessClone(source, clone, Resolve()));
            Assert.IsTrue(FakeContext.Last.Finished);
            Assert.IsTrue(generated == null);
            Assert.IsTrue(original != null);
            Assert.AreEqual("original-directory", FakeDirectoryScope.Current);
        }

        [Test]
        public void PhaseExceptionStillFinishesAndReleasesGeneratedAssets()
        {
            FakeProcessor.Action = root => { ReplaceWithGenerated(root); throw new InvalidOperationException("phase failed"); };
            var error = Assert.Throws<InvalidOperationException>(() => NdmfExportPreparation.ProcessClone(source, clone, Resolve()));
            Assert.AreEqual("phase failed", error.InnerException.Message);
            Assert.IsTrue(FakeContext.Last.Finished);
            Assert.IsTrue(generated == null);
            Assert.AreEqual("original-directory", FakeDirectoryScope.Current);
        }

        [TestCase("finish")]
        [TestCase("saver")]
        [TestCase("directory")]
        public void CleanupFailuresStillRestoreScopeAndReleaseOwnedAssets(string failingStage)
        {
            FakeProcessor.Action = ReplaceWithGenerated;
            FakeContext.FailFinish = failingStage == "finish";
            FakeContext.FailSaverDispose = failingStage == "saver";
            FakeDirectoryScope.FailDispose = failingStage == "directory";
            Assert.Throws<InvalidOperationException>(() => NdmfExportPreparation.ProcessClone(source, clone, Resolve()));
            Assert.IsTrue(generated == null);
            Assert.IsTrue(original != null);
            Assert.IsTrue(FakeContext.Last.Saver.Disposed);
            Assert.AreEqual("original-directory", FakeDirectoryScope.Current);
        }

        [Test]
        public void MissingBakedExpressionStopsExport()
        {
            FakeProcessor.Action = root =>
            {
                generated = MakeMesh("different-shape");
                root.GetComponent<SkinnedMeshRenderer>().sharedMesh = generated;
            };
            var error = Assert.Throws<InvalidOperationException>(() => NdmfExportPreparation.ProcessClone(source, clone, Resolve()));
            StringAssert.Contains("表情が失われ", error.Message);
            Assert.IsTrue(generated == null);
        }

        [Test]
        public void UnsavedSharedAssetsAreIsolatedBeforeAnyPassCanMutateThem()
        {
            var material = new Material(Shader.Find("Unlit/Color")) { color = Color.red };
            var clip = new AnimationClip { frameRate = 30f };
            var settings = ScriptableObject.CreateInstance<NdmfSharedAssetFixture>();
            settings.Mesh = original;
            settings.Material = material;
            settings.Clip = clip;
            settings.Next = settings;
            source.AddComponent<NdmfSharedAssetHolder>().Settings = settings;
            clone.AddComponent<NdmfSharedAssetHolder>().Settings = settings;
            var originalVertices = original.vertices;
            NdmfSharedAssetFixture copiedSettings = null;
            Mesh copiedMesh = null;
            Material copiedMaterial = null;
            AnimationClip copiedClip = null;
            try
            {
                FakeProcessor.Action = root =>
                {
                    copiedSettings = root.GetComponent<NdmfSharedAssetHolder>().Settings;
                    copiedMesh = root.GetComponent<SkinnedMeshRenderer>().sharedMesh;
                    copiedMaterial = copiedSettings.Material;
                    copiedClip = copiedSettings.Clip;
                    Assert.AreNotSame(settings, copiedSettings);
                    Assert.AreNotSame(original, copiedMesh);
                    Assert.AreSame(copiedMesh, copiedSettings.Mesh);
                    Assert.AreSame(copiedSettings, copiedSettings.Next, "Cyclic asset references must point into the copied graph.");
                    copiedMesh.vertices = new[] { Vector3.down, Vector3.down, Vector3.down };
                    copiedMaterial.color = Color.blue;
                    copiedClip.frameRate = 60f;
                    copiedSettings.Value = 42;
                };
                using (NdmfExportPreparation.ProcessClone(source, clone, Resolve()))
                {
                    CollectionAssert.AreEqual(originalVertices, original.vertices);
                    Assert.AreEqual(Color.red, material.color);
                    Assert.AreEqual(30f, clip.frameRate);
                    Assert.AreEqual(0, settings.Value);
                    Assert.AreSame(settings, settings.Next);
                    Assert.AreSame(settings, source.GetComponent<NdmfSharedAssetHolder>().Settings);
                }
                Assert.IsTrue(copiedSettings == null && copiedMesh == null && copiedMaterial == null && copiedClip == null);
                Assert.IsTrue(settings != null && material != null && clip != null && original != null);
            }
            finally
            {
                Object.DestroyImmediate(settings);
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(clip);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PersistentContainerOfUnsavedAssetIsCopiedWithoutEditingOriginalReferences(bool containerIsCloneOnly)
        {
            var settings = ScriptableObject.CreateInstance<NdmfSharedAssetFixture>();
            var assetPath = "Assets/VRVlog-ndmf-ownership-" + Guid.NewGuid().ToString("N") + ".asset";
            NdmfSharedAssetFixture copied = null;
            try
            {
                UnityEditor.AssetDatabase.CreateAsset(settings, assetPath);
                settings.Mesh = original;
                if (!containerIsCloneOnly) source.AddComponent<NdmfSharedAssetHolder>().Settings = settings;
                clone.AddComponent<NdmfSharedAssetHolder>().Settings = settings;
                FakeProcessor.Action = root =>
                {
                    copied = root.GetComponent<NdmfSharedAssetHolder>().Settings;
                    Assert.AreNotSame(settings, copied);
                    Assert.AreNotSame(original, copied.Mesh);
                    copied.Value = 99;
                };
                using (NdmfExportPreparation.ProcessClone(source, clone, Resolve()))
                {
                    Assert.AreSame(original, settings.Mesh);
                    Assert.AreEqual(0, settings.Value);
                    Assert.IsTrue(UnityEditor.EditorUtility.IsPersistent(settings));
                }
                Assert.IsTrue(copied == null);
                Assert.IsTrue(settings != null && original != null);
            }
            finally { UnityEditor.AssetDatabase.DeleteAsset(assetPath); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SharedSettingsPointingIntoOriginalAvatarStopBeforeAnyPass(bool componentReference)
        {
            var settings = ScriptableObject.CreateInstance<NdmfSharedAssetFixture>();
            settings.name = "unsafe scene reference fixture";
            settings.Target = componentReference ? (Object)source.transform : source;
            source.AddComponent<NdmfSharedAssetHolder>().Settings = settings;
            clone.AddComponent<NdmfSharedAssetHolder>().Settings = settings;
            try
            {
                var error = Assert.Throws<InvalidOperationException>(() => NdmfExportPreparation.ProcessClone(source, clone, Resolve()));
                StringAssert.Contains(settings.name, error.Message);
                StringAssert.Contains("元のアバター", error.Message);
                Assert.AreEqual(0, FakeProcessor.Calls);
                Assert.AreEqual("original-directory", FakeDirectoryScope.Current);
                Assert.AreSame(settings, clone.GetComponent<NdmfSharedAssetHolder>().Settings);
                Assert.AreSame(componentReference ? (Object)source.transform : source, settings.Target);
                Assert.IsTrue(original != null && settings != null);
            }
            finally { Object.DestroyImmediate(settings); }
        }

        [Test]
        public void SettingsPointingIntoAnotherSceneObjectStopBeforeAnyPass()
        {
            var external = new GameObject("another scene avatar");
            var settings = ScriptableObject.CreateInstance<NdmfSharedAssetFixture>();
            settings.name = "external scene reference fixture";
            settings.Target = external.transform;
            clone.AddComponent<NdmfSharedAssetHolder>().Settings = settings;
            try
            {
                var error = Assert.Throws<InvalidOperationException>(() => NdmfExportPreparation.ProcessClone(source, clone, Resolve()));
                StringAssert.Contains(settings.name, error.Message);
                StringAssert.Contains("外部を指すシーン参照", error.Message);
                Assert.AreEqual(0, FakeProcessor.Calls);
                Assert.AreSame(external.transform, settings.Target);
                Assert.IsTrue(external != null && settings != null && original != null);
            }
            finally
            {
                Object.DestroyImmediate(settings);
                Object.DestroyImmediate(external);
            }
        }

        [Test]
        public void OptionalDependencyIsNotRequiredWithoutAuthoringTags()
        {
            Assert.IsFalse(NdmfExportPreparation.NeedsProcessing(source));
            using (NdmfExportPreparation.Prepare(source, clone)) { }
            Assert.AreEqual(0, FakeProcessor.Calls);
            Assert.IsTrue(original != null);
        }

        [Test]
        public void OriginalAndNestedCopiesAreRejectedBeforeProcessing()
        {
            Assert.Throws<InvalidOperationException>(() => NdmfExportPreparation.Prepare(source, source));
            clone.transform.SetParent(source.transform);
            Assert.Throws<InvalidOperationException>(() => NdmfExportPreparation.Prepare(source, clone));
            Assert.AreEqual(0, FakeProcessor.Calls);
        }

        [TestCase("1.8.3")]
        [TestCase("1.13.0")]
        [TestCase("1.14.8")]
        public void KnownVersionsResolveCompleteBridge(string version) => Assert.IsNotNull(Resolve(version));

        [TestCase(null)]
        [TestCase("1.8.2")]
        [TestCase("2.0.0")]
        [TestCase("1.14.8-alpha.0")]
        public void UnvalidatedVersionsFailBeforeProcessing(string version) =>
            Assert.Throws<InvalidOperationException>(() => Resolve(version));

        [Test]
        public void IncompatibleErrorApiFailsBeforeProcessing()
        {
            Assert.Throws<InvalidOperationException>(() => NdmfExportPreparation.Bridge.Resolve(
                name => name == "nadena.dev.ndmf.BuildContext" ? typeof(object) : FakeType(name), "1.14.8"));
            Assert.AreEqual(0, FakeProcessor.Calls);
        }

        [Test]
        public void InstalledModularAvatarMergesSleeveRigAndPreservesSourceAndExpression()
        {
            var mergeType = InstalledType("nadena.dev.modular_avatar.core.ModularAvatarMergeArmature");
            var rootType = InstalledType("nadena.dev.ndmf.runtime.components.NDMFAvatarRoot");
            if (mergeType == null || rootType == null) Assert.Ignore("Install Modular Avatar and NDMF to run their real processing pipeline.");
            source.AddComponent(rootType);
            var mainRig = Child(source.transform, "MainRig", Vector3.zero);
            var mainArm = Child(mainRig, "UpperArm.L", new Vector3(.25f, .7f, 0f));
            var clothingRig = Child(source.transform, "ClothingRig", Vector3.zero);
            var clothingArm = Child(clothingRig, "UpperArm.L", mainArm.localPosition);
            var merge = clothingRig.gameObject.AddComponent(mergeType);
            var reference = mergeType.GetField("mergeTarget").GetValue(merge);
            reference.GetType().GetMethod("Set", new[] { typeof(GameObject) }).Invoke(reference, new object[] { mainRig.gameObject });
            generated = MakeMesh("__VRVlog_Menu_sleeve");
            generated.vertices = new[] { new Vector3(.5f, .7f, 0f), new Vector3(.7f, .7f, 0f), new Vector3(.5f, .8f, 0f) };
            var sleeve = Child(source.transform, "Sleeve", Vector3.zero).gameObject.AddComponent<SkinnedMeshRenderer>();
            sleeve.sharedMesh = generated;
            sleeve.bones = new[] { clothingArm };
            sleeve.rootBone = clothingRig;
            generated.bindposes = new[] { clothingArm.worldToLocalMatrix * sleeve.transform.localToWorldMatrix };
            generated.boneWeights = new[] { ArmWeight(), ArmWeight(), ArmWeight() };
            NdmfExportPreparation.ValidateSource(source);
            Object.DestroyImmediate(clone);
            clone = Object.Instantiate(source);
            var before = new Mesh();
            var after = new Mesh();
            try
            {
                using (NdmfExportPreparation.Prepare(source, clone))
                {
                    var copiedArm = clone.transform.Find("MainRig/UpperArm.L");
                    var copiedSleeve = clone.transform.Find("Sleeve").GetComponent<SkinnedMeshRenderer>();
                    Assert.GreaterOrEqual(copiedSleeve.sharedMesh.GetBlendShapeIndex("__VRVlog_Menu_sleeve"), 0);
                    copiedSleeve.BakeMesh(before);
                    copiedArm.localRotation = Quaternion.Euler(0f, 0f, -60f);
                    copiedSleeve.BakeMesh(after);
                    Assert.Greater(Vector3.Distance(before.vertices[0], after.vertices[0]), .1f,
                        "The sleeve must move with the primary arm after the declared MA merge.");
                    Assert.AreSame(clothingRig, clothingArm.parent);
                    Assert.AreSame(clothingArm, sleeve.bones[0]);
                    Assert.AreEqual(Quaternion.identity, mainArm.localRotation);
                    Assert.AreEqual(Quaternion.identity, clothingArm.localRotation);
                    Assert.AreSame(generated, sleeve.sharedMesh);
                    Assert.IsTrue(merge != null);
                }
                Assert.IsTrue(generated != null, "Source mesh must not be owned by preparation cleanup.");
            }
            finally
            {
                Object.DestroyImmediate(before);
                Object.DestroyImmediate(after);
            }
        }

        [Test]
        public void InstalledModularAvatarRejectsTargetOutsideSelectedSubtreeBeforeCloning()
        {
            var mergeType = InstalledType("nadena.dev.modular_avatar.core.ModularAvatarMergeArmature");
            var rootType = InstalledType("nadena.dev.ndmf.runtime.components.NDMFAvatarRoot");
            if (mergeType == null || rootType == null) Assert.Ignore("Install Modular Avatar and NDMF to test their authored references.");
            var wholeAvatar = new GameObject("whole-avatar");
            try
            {
                wholeAvatar.AddComponent(rootType);
                source.transform.SetParent(wholeAvatar.transform);
                var target = Child(wholeAvatar.transform, "outside-selected-subtree", Vector3.zero);
                var merge = source.AddComponent(mergeType);
                var reference = mergeType.GetField("mergeTarget").GetValue(merge);
                reference.GetType().GetMethod("Set", new[] { typeof(GameObject) }).Invoke(reference, new object[] { target.gameObject });
                var error = Assert.Throws<InvalidOperationException>(() => NdmfExportPreparation.ValidateSource(source));
                StringAssert.Contains("外にあります", error.Message);
                Assert.AreSame(wholeAvatar.transform, source.transform.parent);
                Assert.AreSame(wholeAvatar.transform, target.parent);
            }
            finally
            {
                if (source != null) source.transform.SetParent(null);
                Object.DestroyImmediate(wholeAvatar);
            }
        }

        private static Transform Child(Transform parent, string name, Vector3 position)
        {
            var child = new GameObject(name).transform;
            child.SetParent(parent, false);
            child.localPosition = position;
            return child;
        }

        private static BoneWeight ArmWeight() => new BoneWeight { boneIndex0 = 0, weight0 = 1f };

        private static Type InstalledType(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(name, false);
                if (type != null) return type;
            }
            return null;
        }

        private void ReplaceWithGenerated(GameObject root)
        {
            generated = MakeMesh("__VRVlog_Menu_fixture");
            root.GetComponent<SkinnedMeshRenderer>().sharedMesh = generated;
        }

        private static Mesh MakeMesh(string shape)
        {
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            mesh.AddBlendShapeFrame(shape, 100f, new[] { Vector3.up, Vector3.zero, Vector3.zero }, new Vector3[3], new Vector3[3]);
            return mesh;
        }

        private static NdmfExportPreparation.Bridge Resolve(string version = "1.14.8") =>
            NdmfExportPreparation.Bridge.Resolve(FakeType, version);

        private static Type FakeType(string name)
        {
            switch (name)
            {
                case "nadena.dev.ndmf.AvatarProcessor": return typeof(FakeProcessor);
                case "nadena.dev.ndmf.BuildContext": return typeof(FakeContext);
                case "nadena.dev.ndmf.BuildPhase": return typeof(FakePhase);
                case "nadena.dev.ndmf.platform.INDMFPlatformProvider": return typeof(IFakeProvider);
                case "nadena.dev.ndmf.platform.PlatformRegistry": return typeof(FakeRegistry);
                case "nadena.dev.ndmf.platform.GenericPlatform": return typeof(FakeGeneric);
                case "nadena.dev.ndmf.OverrideTemporaryDirectoryScope": return typeof(FakeDirectoryScope);
                default: return null;
            }
        }

        public interface IFakeProvider { }
        public sealed class FakeProvider : IFakeProvider { }
        public static class FakeGeneric { public static IFakeProvider Instance { get; } = new FakeProvider(); }
        public static class FakeRegistry
        {
            public static IFakeProvider Selected;
            public static IFakeProvider GetPrimaryPlatformForAvatar(GameObject root) => Selected;
        }
        public sealed class FakePhase
        {
            public static readonly FakePhase Start = new FakePhase(), Transforming = new FakePhase();
            private static FakePhase First => Start;
        }
        public sealed class FakeDirectoryScope : IDisposable
        {
            public static string Current;
            public static bool FailDispose;
            private readonly string previous;
            public FakeDirectoryScope(string path) { previous = Current; Current = path; }
            public void Dispose() { Current = previous; if (FailDispose) throw new InvalidOperationException("directory disposal failed"); }
        }
        public sealed class FakeContext
        {
            public static FakeContext Last;
            public static bool Success, FailFinish, FailSaverDispose;
            public readonly GameObject Root;
            public readonly string AssetPath;
            public readonly IFakeProvider Platform;
            public readonly FakeSaver Saver = new FakeSaver();
            public bool Finished;
            public bool Successful => Success;
            public IDisposable AssetSaver => Saver;
            public FakeContext(GameObject root, string path, IFakeProvider platform, bool isClone)
            { Root = root; AssetPath = path; Platform = platform; Last = this; }
            internal void Finish() { Finished = true; if (FailFinish) throw new InvalidOperationException("finish failed"); }
        }
        public sealed class FakeSaver : IDisposable
        {
            public bool Disposed;
            public void Dispose() { Disposed = true; if (FakeContext.FailSaverDispose) throw new InvalidOperationException("saver disposal failed"); }
        }
        public static class FakeProcessor
        {
            public static Action<GameObject> Action;
            public static int Calls;
            public static FakePhase First, Last;
            internal static void ProcessAvatar(FakeContext context, FakePhase first, FakePhase last)
            { Calls++; First = first; Last = last; Action?.Invoke(context.Root); }
        }
    }

    public sealed class NdmfSharedAssetFixture : ScriptableObject
    {
        public Mesh Mesh;
        public Material Material;
        public AnimationClip Clip;
        public NdmfSharedAssetFixture Next;
        public Object Target;
        public int Value;
    }

    public sealed class NdmfSharedAssetHolder : MonoBehaviour
    {
        public NdmfSharedAssetFixture Settings;
    }
}
