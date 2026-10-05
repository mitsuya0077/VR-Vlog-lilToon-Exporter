using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRVlog.LilToon;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class AppearanceRecoveryTests
    {
        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void EmptyMeshDoesNotCreateAFullBindingOrRemoveItsBone(bool hasVertices, bool skinned)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var material = new Material(Shader.Find("lilToon"));
            var empty = new Mesh { name = "Removed clothing geometry" };
            try
            {
                if (hasVertices) empty.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
                empty.subMeshCount = 1;
                empty.SetIndices(Array.Empty<int>(), MeshTopology.Triangles, 0);
                foreach (var renderer in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) renderer.sharedMaterial = material;
                var head = fixture.Source.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Head);
                Renderer emptyRenderer;
                if (skinned)
                {
                    var skin = head.gameObject.AddComponent<SkinnedMeshRenderer>();
                    skin.sharedMesh = empty; skin.rootBone = head;
                    emptyRenderer = skin;
                }
                else
                {
                    head.gameObject.AddComponent<MeshFilter>().sharedMesh = empty;
                    emptyRenderer = head.gameObject.AddComponent<MeshRenderer>();
                }
                emptyRenderer.sharedMaterial = material;
                var sourceVertices = fixture.Mesh.vertices;
                var sourceIndices = fixture.Mesh.triangles;
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Empty geometry", "Tests",
                    exporterVersion: "tests", lilToonVersion: "2.3.4", blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                var glb = GlbDocument.Read(bytes);
                LilToonFullContract.Validate(glb.Json, bytes.Length, glb.Binary.Length);
                Assert.That(LilToonFullContract.List(LilToonFullContract.Root(glb.Json), "bindings").Count, Is.EqualTo(2));
                Assert.That(LilToonFullContract.List(glb.Json, "nodes").Select(LilToonFullContract.Object)
                    .Any(node => node.TryGetValue("name", out var name) && (string)name == head.name), Is.True);
                Assert.That(head, Is.Not.Null);
                Assert.That(emptyRenderer.enabled, Is.True);
                Assert.That(empty.vertexCount, Is.EqualTo(hasVertices ? 3 : 0));
                Assert.That(empty.GetIndexCount(0), Is.Zero);
                Assert.That(fixture.Mesh.vertices, Is.EqualTo(sourceVertices));
                Assert.That(fixture.Mesh.triangles, Is.EqualTo(sourceIndices));
                var sourceSkins = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>().Where(skin => skin.sharedMesh == fixture.Mesh).ToArray();
                Assert.That(sourceSkins.Length, Is.EqualTo(2), "Shared visible meshes remain owned by the original avatar.");
                Assert.That(sourceSkins.All(skin => skin.GetBlendShapeWeight(0) == 35), Is.True);
            }
            finally { Object.DestroyImmediate(empty); Object.DestroyImmediate(material); }
        }

        [Test]
        public void NonemptyUnsupportedTopologyIsNotClassifiedAsEmpty()
        {
            var mesh = new Mesh();
            try
            {
                mesh.vertices = new[] { Vector3.zero, Vector3.right };
                mesh.SetIndices(new[] { 0, 1 }, MeshTopology.Lines, 0);
                Assert.That(ExportRendererSelection.HasGeometry(mesh), Is.True);
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [TestCase("lilToon", "2nd", "Tex")]
        [TestCase("lilToon", "2nd", "BlendMask")]
        [TestCase("lilToon", "2nd", "DissolveMask")]
        [TestCase("lilToon", "2nd", "DissolveNoiseMask")]
        [TestCase("lilToon", "3rd", "Tex")]
        [TestCase("lilToon", "3rd", "BlendMask")]
        [TestCase("lilToon", "3rd", "DissolveMask")]
        [TestCase("lilToon", "3rd", "DissolveNoiseMask")]
        [TestCase("_lil/lilToonMulti", "2nd", "Tex")]
        [TestCase("_lil/lilToonMulti", "2nd", "BlendMask")]
        [TestCase("_lil/lilToonMulti", "2nd", "DissolveMask")]
        [TestCase("_lil/lilToonMulti", "2nd", "DissolveNoiseMask")]
        [TestCase("_lil/lilToonMulti", "3rd", "Tex")]
        [TestCase("_lil/lilToonMulti", "3rd", "BlendMask")]
        [TestCase("_lil/lilToonMulti", "3rd", "DissolveMask")]
        [TestCase("_lil/lilToonMulti", "3rd", "DissolveNoiseMask")]
        public void UnsupportedImageInAProvenInactiveLayerKeepsEveryMaterialProperty(string shader, string layer, string suffix)
        {
            using var fixture = new MaterialFixture(shader);
            var property = "_Main" + layer + suffix;
            fixture.Material.SetFloat("_UseMain" + layer + "Tex", 0);
            fixture.Material.DisableKeyword(layer == "2nd" ? "_COLORADDSUBDIFF_ON" : "_COLORCOLOR_ON");
            fixture.Material.SetTexture(property, fixture.Dynamic);
            var before = EditorJsonUtility.ToJson(fixture.Material);
            var warnings = new List<string>();
            var snapshot = LilToonFullSnapshot.Capture(fixture.Root, warnings: warnings);
            var record = Record(snapshot, fixture.Material);
            Assert.That(TextureId(record, property), Is.EqualTo(-1));
            var properties = LilToonFullContract.List(record, "values").Concat(LilToonFullContract.List(record, "textures"))
                .Select(LilToonFullContract.Object).Select(value => LilToonFullContract.Text(value, "name"));
            Assert.That(properties, Is.EquivalentTo(LilToon234Catalogue.RequiredProperties[shader]));
            Assert.That(warnings.Single(), Does.Contain(property));
            Assert.That(EditorJsonUtility.ToJson(fixture.Material), Is.EqualTo(before));
            Assert.That(fixture.Material.GetTexture(property), Is.SameAs(fixture.Dynamic));
        }

        [Test]
        public void SupportedImageInAnInactiveLayerIsStillStored()
        {
            using var fixture = new MaterialFixture();
            fixture.Material.SetFloat("_UseMain2ndTex", 0);
            fixture.Material.SetTexture("_Main2ndTex", Texture2D.whiteTexture);
            var warnings = new List<string>();
            var snapshot = LilToonFullSnapshot.Capture(fixture.Root, warnings: warnings);
            Assert.That(TextureId(Record(snapshot, fixture.Material), "_Main2ndTex"), Is.GreaterThanOrEqualTo(0));
            Assert.That(warnings, Is.Empty);
        }

        [TestCase("lilToon", 1f, false)]
        [TestCase("_lil/lilToonMulti", 1f, false)]
        [TestCase("_lil/lilToonMulti", 0f, true)]
        public void ActiveOrKeywordEnabledLayersKeepTheOriginalDynamicImageDiagnostic(string shader, float enabled, bool keyword)
        {
            using var fixture = new MaterialFixture(shader);
            fixture.Material.SetFloat("_UseMain2ndTex", enabled);
            if (keyword) fixture.Material.EnableKeyword("_COLORADDSUBDIFF_ON");
            else fixture.Material.DisableKeyword("_COLORADDSUBDIFF_ON");
            fixture.Material.SetTexture("_Main2ndTex", fixture.Dynamic);
            var error = Assert.Throws<NotSupportedException>(() => LilToonFullSnapshot.Capture(fixture.Root));
            Assert.That(error.Data[ExportRecoveryFailure.LayerKey], Is.EqualTo("2nd"));
        }

        [TestCase("_MainTex")] [TestCase("_EmissionMap")]
        public void UnsupportedImagesOutsideTheProvenLayerSetAreNotOmitted(string property)
        {
            using var fixture = new MaterialFixture();
            fixture.Material.SetTexture(property, fixture.Dynamic);
            Assert.Throws<NotSupportedException>(() => LilToonFullSnapshot.Capture(fixture.Root));
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void LiveOrConsumedMaterialAnimationPreventsInactiveTextureOmission(bool originalOnly, bool materialSwap)
        {
            using var fixture = new MaterialFixture();
            var original = new GameObject("Original animation holder");
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            var machine = controller.layers[0].stateMachine;
            var state = machine.AddState("Enable optional layer");
            var clip = new AnimationClip();
            try
            {
                fixture.Material.SetFloat("_UseMain2ndTex", 0);
                fixture.Material.SetTexture("_Main2ndTex", fixture.Dynamic);
                if (materialSwap)
                    AnimationUtility.SetObjectReferenceCurve(clip,
                        EditorCurveBinding.PPtrCurve("", typeof(MeshRenderer), "m_Materials.Array.data[0]"),
                        new[] { new ObjectReferenceKeyframe { time = 0, value = fixture.Material } });
                else clip.SetCurve("", typeof(MeshRenderer), "material._UseMain2ndTex", AnimationCurve.Constant(0, 1, 1));
                state.motion = clip;
                (originalOnly ? original : fixture.Root).AddComponent<Animator>().runtimeAnimatorController = controller;
                Assert.Throws<NotSupportedException>(() => LilToonFullSnapshot.Capture(fixture.Root, animationSource: original));
                Assert.That(fixture.Material.GetTexture("_Main2ndTex"), Is.SameAs(fixture.Dynamic));
            }
            finally
            {
                Object.DestroyImmediate(original); Object.DestroyImmediate(controller);
                Object.DestroyImmediate(machine); Object.DestroyImmediate(state); Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void MaterialPropertyBlockPreventsInactiveTextureOmission()
        {
            using var fixture = new MaterialFixture();
            fixture.Material.SetFloat("_UseMain2ndTex", 0);
            fixture.Material.SetTexture("_Main2ndTex", fixture.Dynamic);
            var block = new MaterialPropertyBlock(); block.SetFloat("_UseMain2ndTex", 1);
            fixture.Root.GetComponent<Renderer>().SetPropertyBlock(block);
            Assert.Throws<NotSupportedException>(() => LilToonFullSnapshot.Capture(fixture.Root));
        }

        [Test]
        public void FullExportAcceptsAnInactiveDynamicLayerWithoutEditingSourceAppearance()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var material = new Material(Shader.Find("lilToon"));
            var dynamic = new RenderTexture(4, 4, 0);
            try
            {
                material.SetFloat("_UseMain2ndTex", 0); material.SetTexture("_Main2ndTex", dynamic);
                foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial = material;
                var before = EditorJsonUtility.ToJson(material);
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Inactive image", "Tests",
                    exporterVersion: "tests", lilToonVersion: "2.3.4", blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                var glb = GlbDocument.Read(bytes);
                LilToonFullContract.Validate(glb.Json, bytes.Length, glb.Binary.Length);
                var record = LilToonFullContract.List(LilToonFullContract.Root(glb.Json), "materials").Select(LilToonFullContract.Object).Single();
                Assert.That(TextureId(record, "_Main2ndTex"), Is.EqualTo(-1));
                Assert.That(EditorJsonUtility.ToJson(material), Is.EqualTo(before));
            }
            finally { Object.DestroyImmediate(material); Object.DestroyImmediate(dynamic); }
        }

        static Dictionary<string, object> Record(LilToonFullSnapshot snapshot, Material material) =>
            ((Dictionary<Material, Dictionary<string, object>>)typeof(LilToonFullSnapshot)
                .GetField("records", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(snapshot))[material];

        static int TextureId(Dictionary<string, object> record, string property) =>
            LilToonFullContract.Int(LilToonFullContract.List(record, "textures").Select(LilToonFullContract.Object)
                .Single(value => LilToonFullContract.Text(value, "name") == property), "texture");

        sealed class MaterialFixture : IDisposable
        {
            internal readonly GameObject Root = new GameObject("Static appearance");
            internal readonly Material Material;
            internal readonly RenderTexture Dynamic = new RenderTexture(4, 4, 0) { name = "Unused dynamic image" };
            readonly Mesh mesh = new Mesh();

            internal MaterialFixture(string shader = "lilToon")
            {
                Material = new Material(Shader.Find(shader));
                mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
                mesh.triangles = new[] { 0, 1, 2 };
                Root.AddComponent<MeshFilter>().sharedMesh = mesh;
                Root.AddComponent<MeshRenderer>().sharedMaterial = Material;
            }

            public void Dispose()
            {
                Object.DestroyImmediate(Root); Object.DestroyImmediate(Material);
                Object.DestroyImmediate(Dynamic); Object.DestroyImmediate(mesh);
            }
        }
    }
}
