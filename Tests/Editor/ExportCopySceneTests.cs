using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class ExportCopySceneTests
    {
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void CloneEditsAndDisposalPreserveSourceSceneAndActiveSelection(bool dirty, bool otherActiveScene)
        {
            using var f = new SceneFixture(dirty, otherActiveScene);
            var originalRoots = f.Scene.GetRootGameObjects();
            var originalJson = SourceJson(f.Source);
            var originalFile = File.ReadAllBytes(f.Path);
            var previousActive = SceneManager.GetActiveScene();
            var loadedScenes = SceneManager.sceneCount;
            GameObject clone;
            Scene ownedScene;
            using (var copy = new ExportCopyScene(f.Source))
            {
                clone = copy.Copy; ownedScene = clone.scene;
                Assert.That(EditorSceneManager.IsPreviewScene(ownedScene), Is.True);
                Assert.That(ownedScene, Is.Not.EqualTo(f.Scene));
                Assert.That(clone.transform.parent, Is.Null);
                Assert.That(clone.activeInHierarchy, Is.True);
                Assert.That(Vector3.Distance(clone.transform.position, f.ExpectedPosition), Is.LessThan(.00001f),
                    $"Position must match native parentless Instantiate: actual={clone.transform.position}, expected={f.ExpectedPosition}.");
                Assert.That(Quaternion.Angle(clone.transform.rotation, f.ExpectedRotation), Is.LessThan(.001f),
                    $"Rotation must match native parentless Instantiate: actual={clone.transform.rotation}, expected={f.ExpectedRotation}.");
                Assert.That(Vector3.Distance(clone.transform.lossyScale, f.ExpectedScale), Is.LessThan(.00001f),
                    $"Scale must match native parentless Instantiate: actual={clone.transform.lossyScale}, expected={f.ExpectedScale}.");
                Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(previousActive));
                Assert.That(f.Scene.isDirty, Is.EqualTo(dirty), "Cloning must not transiently dirty a clean source scene.");
                clone.transform.GetChild(0).localPosition += Vector3.one;
                clone.GetComponentInChildren<Light>().intensity = 9;
                Object.DestroyImmediate(clone.transform.GetChild(0).GetChild(0).gameObject);
                Assert.That(f.Scene.isDirty, Is.EqualTo(dirty));
            }
            Assert.That(clone == null, Is.True);
            Assert.That(ownedScene.IsValid(), Is.False, "The owned scene must close after disposing the copy.");
            Assert.That(SceneManager.sceneCount, Is.EqualTo(loadedScenes));
            Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(previousActive));
            Assert.That(f.Scene.isDirty, Is.EqualTo(dirty), "Authored dirty state must be retained without clearing it.");
            Assert.That(f.Scene.GetRootGameObjects(), Is.EquivalentTo(originalRoots));
            Assert.That(SourceJson(f.Source), Is.EqualTo(originalJson));
            Assert.That(File.ReadAllBytes(f.Path), Is.EqualTo(originalFile));
        }

        [Test]
        public void UnrelatedExceptionClosesOwnedSceneWithoutDirtyingSavedSource()
        {
            using var f = new SceneFixture(false, false);
            var active = SceneManager.GetActiveScene();
            var before = SourceJson(f.Source);
            GameObject clone = null;
            Scene owned = default;
            var failure = new InvalidOperationException("Fixture failure after copy creation");
            Assert.That(Assert.Throws<InvalidOperationException>(() =>
            {
                using var copy = new ExportCopyScene(f.Source);
                clone = copy.Copy; owned = clone.scene;
                throw failure;
            }), Is.SameAs(failure));
            Assert.That(clone == null, Is.True);
            Assert.That(owned.IsValid(), Is.False);
            Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(active));
            Assert.That(f.Scene.isDirty, Is.False);
            Assert.That(SourceJson(f.Source), Is.EqualTo(before));
        }

        [Test]
        public void UnsavedSceneRemainsLoadedAndCloneCallbacksRunInsideOwnedScene()
        {
            // The export helper must work with the runner's unsaved scene;
            // NewScene(Additive) is not a safe prerequisite in this situation.
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var source = new GameObject("Unsaved source");
            source.AddComponent<ExportCloneSceneCallbackProbe>();
            var dirty = scene.isDirty;
            var before = SourceJson(source);
            ExportCloneSceneCallbackProbe.ExpectedSource = source;
            ExportCloneSceneCallbackProbe.ClonedEnableScenes.Clear();
            try
            {
                using (var copy = new ExportCopyScene(source))
                {
                    Assert.That(copy.Copy.scene, Is.Not.EqualTo(scene));
                    Assert.That(copy.Copy.activeInHierarchy, Is.True);
                    Assert.That(ExportCloneSceneCallbackProbe.ClonedEnableScenes, Is.Not.Empty,
                        "The fixture must actually exercise ExecuteAlways OnEnable during cloning.");
                    Assert.That(ExportCloneSceneCallbackProbe.ClonedEnableScenes.All(value => value == copy.Copy.scene), Is.True,
                        "A copied component must never enable in the user's source scene before being moved away.");
                    Assert.That(scene.isDirty, Is.EqualTo(dirty));
                }
                Assert.That(scene.IsValid() && scene.isLoaded, Is.True);
                Assert.That(scene.path, Is.Empty);
                Assert.That(scene.isDirty, Is.EqualTo(dirty));
                Assert.That(SourceJson(source), Is.EqualTo(before));
                Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(scene));
            }
            finally
            {
                ExportCloneSceneCallbackProbe.ExpectedSource = null;
                ExportCloneSceneCallbackProbe.ClonedEnableScenes.Clear();
                Object.DestroyImmediate(source);
            }
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void FullExportPreservesCleanSceneWithAndWithoutInstalledNdmf(bool fullLilToon, bool ndmf)
        {
            var shader = Shader.Find("lilToon");
            if (shader == null) Assert.Ignore("Install the real lilToon shader.");
            var proxyType = AppDomain.CurrentDomain.GetAssemblies().Select(value =>
                value.GetType("nadena.dev.modular_avatar.core.ModularAvatarBoneProxy")).FirstOrDefault(value => value != null);
            var markerType = AppDomain.CurrentDomain.GetAssemblies().Select(value =>
                value.GetType("nadena.dev.ndmf.runtime.components.NDMFAvatarRoot")).FirstOrDefault(value => value != null);
            if (ndmf && (proxyType == null || markerType == null)) Assert.Ignore("Install real Modular Avatar and NDMF.");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var path = "Assets/ExportCopyScene-" + Guid.NewGuid().ToString("N") + ".unity";
            using var f = new AttachmentConnectionTests.Fixture();
            try
            {
                foreach (var skin in f.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = shader;
                if (ndmf)
                {
                    f.Source.AddComponent(markerType);
                    var proxy = f.Source.transform.Find("Independent hair").gameObject.AddComponent(proxyType);
                    proxyType.GetProperty("target").SetValue(proxy, f.Source.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Head));
                    var mode = proxyType.GetField("attachmentMode");
                    mode.SetValue(proxy, Enum.Parse(mode.FieldType, "AsChildKeepWorldPose"));
                }
                Assert.That(NdmfExportPreparation.NeedsProcessing(f.Source), Is.EqualTo(ndmf));
                Assert.That(EditorSceneManager.SaveScene(scene, path), Is.True);
                Assert.That(scene.isDirty, Is.False);
                var file = File.ReadAllBytes(path);
                var before = SourceJson(f.Source);
                var roots = scene.GetRootGameObjects();
                var active = SceneManager.GetActiveScene();
                var meshes = f.Source.GetComponentsInChildren<SkinnedMeshRenderer>().Select(value => value.sharedMesh).ToArray();
                var weights = f.Source.GetComponentsInChildren<SkinnedMeshRenderer>().Select(value => value.GetBlendShapeWeight(0)).ToArray();
                var bytes = UniVrmOneClickExporter.Export(f.Source, "Scene ownership regression", "Tests",
                    exporterVersion: fullLilToon ? "scene-ownership-regression" : null,
                    lilToonVersion: fullLilToon ? "2.3.4" : null,
                    blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                Assert.That(bytes.Length, Is.GreaterThan(200));
                Assert.That(bytes.Take(4).ToArray(), Is.EqualTo(new byte[] { 103, 108, 84, 70 }));
                Assert.That(scene.isDirty, Is.False, "An export may edit its clone but must not mark the authored scene modified.");
                Assert.That(scene.GetRootGameObjects(), Is.EquivalentTo(roots));
                Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(active));
                Assert.That(SourceJson(f.Source), Is.EqualTo(before));
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(file));
                Assert.That(f.Source.GetComponentsInChildren<SkinnedMeshRenderer>().Select(value => value.sharedMesh).ToArray(), Is.EqualTo(meshes));
                Assert.That(f.Source.GetComponentsInChildren<SkinnedMeshRenderer>().Select(value => value.GetBlendShapeWeight(0)).ToArray(), Is.EqualTo(weights));
            }
            finally
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                AssetDatabase.DeleteAsset(path);
            }
        }

        [Test]
        public void FailureAfterExportCopyCreationPreservesCleanSourceScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var path = "Assets/ExportCopyFailure-" + Guid.NewGuid().ToString("N") + ".unity";
            using var f = new AttachmentConnectionTests.Fixture();
            try
            {
                var recipe = new ExportRecoveryOptions();
                recipe.Actions.Add(new ExportRecoveryAction { Kind = (ExportRecoveryActionKind)int.MaxValue,
                    Material = f.Source.GetComponentInChildren<SkinnedMeshRenderer>().sharedMaterial });
                Assert.That(EditorSceneManager.SaveScene(scene, path), Is.True);
                var before = SourceJson(f.Source);
                var file = File.ReadAllBytes(path);
                var roots = scene.GetRootGameObjects();
                var active = SceneManager.GetActiveScene();
                var error = Assert.Throws<InvalidOperationException>(() => UniVrmOneClickExporter.Export(
                    f.Source, "Scene failure regression", "Tests", recoveryOptions: recipe,
                    blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None }));
                Assert.That(error.Message, Is.EqualTo("未対応の対策です。"), "The failure must happen after an independent export copy was created.");
                Assert.That(scene.isDirty, Is.False);
                Assert.That(scene.GetRootGameObjects(), Is.EquivalentTo(roots));
                Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(active));
                Assert.That(SourceJson(f.Source), Is.EqualTo(before));
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(file));
            }
            finally
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                AssetDatabase.DeleteAsset(path);
            }
        }

        static string[] SourceJson(GameObject source) => source.GetComponentsInChildren<Transform>(true)
            .SelectMany(value => new Object[] { value.gameObject }.Concat(value.GetComponents<Component>().Cast<Object>()))
            .Where(value => value != null).Select(value => EditorJsonUtility.ToJson(value)).ToArray();

        sealed class SceneFixture : IDisposable
        {
            internal readonly Scene Scene;
            internal readonly GameObject Source;
            internal readonly string Path;
            internal readonly Vector3 ExpectedPosition, ExpectedScale;
            internal readonly Quaternion ExpectedRotation;
            readonly string otherPath;

            internal SceneFixture(bool dirty, bool otherActiveScene)
            {
                Scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Path = "Assets/ExportCopyOwnership-" + Guid.NewGuid().ToString("N") + ".unity";
                var parent = new GameObject("Authored source parent");
                parent.transform.SetPositionAndRotation(new Vector3(1.2f, -.3f, .8f), Quaternion.Euler(12, 31, 7));
                parent.transform.localScale = new Vector3(.75f, 1.2f, 1.4f);
                Source = new GameObject("Avatar"); Source.transform.SetParent(parent.transform, false);
                Source.transform.localPosition = new Vector3(.1f, .2f, .3f);
                Source.transform.localRotation = Quaternion.Euler(17, 2, 11);
                Source.transform.localScale = Vector3.one * 1.4f;
                var child = new GameObject("Authored child", typeof(Light)); child.transform.SetParent(Source.transform, false);
                new GameObject("Authored grandchild").transform.SetParent(child.transform, false);
                // Native old-path reference is sampled only during fixture
                // setup, before saving the clean-scene preservation baseline.
                var reference = Object.Instantiate(Source);
                ExpectedPosition = reference.transform.position; ExpectedRotation = reference.transform.rotation;
                ExpectedScale = reference.transform.lossyScale;
                Object.DestroyImmediate(reference);
                Assert.That(EditorSceneManager.SaveScene(Scene, Path), Is.True);
                if (dirty)
                {
                    child.GetComponent<Light>().intensity = 3;
                    EditorSceneManager.MarkSceneDirty(Scene);
                }
                if (otherActiveScene)
                {
                    var other = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                    otherPath = "Assets/ExportCopyOtherScene-" + Guid.NewGuid().ToString("N") + ".unity";
                    Assert.That(EditorSceneManager.SaveScene(other, otherPath), Is.True);
                    if (SceneManager.GetActiveScene() != other) SceneManager.SetActiveScene(other);
                    Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(other),
                        "An already-active scene need not return true from SetActiveScene.");
                }
                Assert.That(Scene.isDirty, Is.EqualTo(dirty));
            }

            public void Dispose()
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                AssetDatabase.DeleteAsset(Path);
                if (otherPath != null) AssetDatabase.DeleteAsset(otherPath);
            }
        }
    }

    [ExecuteAlways]
    public sealed class ExportCloneSceneCallbackProbe : MonoBehaviour
    {
        internal static GameObject ExpectedSource;
        internal static readonly System.Collections.Generic.List<Scene> ClonedEnableScenes =
            new System.Collections.Generic.List<Scene>();
        void OnEnable()
        {
            if (ExpectedSource != null && gameObject != ExpectedSource) ClonedEnableScenes.Add(gameObject.scene);
        }
    }
}
