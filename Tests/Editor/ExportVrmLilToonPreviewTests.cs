using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UniGLTF;
using UniVRM10;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using F = VRVlog.LilToon.LilToonFullContract;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class ExportVrmLilToonPreviewTests
    {
        [Test]
        public async Task ExportedEmissionAlphaAndMaterialSettingsDriveThePreviewWithoutEditingTheSource()
        {
            using var f = new Fixture();
            f.First.SetColor("_Color", new Color(.17f, .23f, .31f, 1));
            f.First.SetFloat("_AsUnlit", 1);
            f.First.SetFloat("_UseEmission", 1);
            f.First.SetFloat("_EmissionBlend", .7f);
            f.First.SetColor("_EmissionColor", new Color(4, 3, 2, 0));
            f.First.SetFloat("_UseEmission2nd", 1);
            f.First.SetColor("_Emission2ndColor", new Color(.2f, .4f, .1f, .25f));
            f.First.SetVector("_MainTex_ScrollRotate", new Vector4(0, 0, .2f, 0));
            f.First.renderQueue = 2457;
            f.First.SetShaderPassEnabled("ShadowCaster", false);
            var sourceState = EditorJsonUtility.ToJson(f.First);
            var sourceMesh = f.Avatar.Mesh.vertices;
            var sourceMatrices = f.Avatar.Source.GetComponentsInChildren<Transform>(true).Select(t => t.localToWorldMatrix).ToArray();
            var bytes = f.Export(); var originalBytes = (byte[])bytes.Clone();
            await f.Import(bytes);
            var native = f.ImportedFront.sharedMaterial;
            Assert.That(native.shader.name, Does.Contain("MToon"));
            using (ExportVrmLilToonPreview.Apply(bytes, f.Imported.gameObject))
            {
                var restored = f.ImportedFront.sharedMaterial;
                Assert.That(restored.shader, Is.SameAs(f.First.shader));
                Assert.That(restored, Is.Not.SameAs(f.First).And.Not.SameAs(native));
                Assert.That(restored.GetColor("_EmissionColor"), Is.EqualTo(new Color(4, 3, 2, 0)));
                Assert.That(restored.GetColor("_Emission2ndColor"), Is.EqualTo(new Color(.2f, .4f, .1f, .25f)));
                Assert.That(restored.GetFloat("_EmissionBlend"), Is.EqualTo(.7f));
                Assert.That(restored.GetVector("_MainTex_ScrollRotate"), Is.EqualTo(new Vector4(0, 0, .2f, 0)));
                Assert.That(restored.renderQueue, Is.EqualTo(2457));
                Assert.That(restored.GetShaderPassEnabled("ShadowCaster"), Is.False);
                Assert.That(restored.shaderKeywords, Is.EquivalentTo(f.First.shaderKeywords));
                var expected = Render(f.First);
                var actual = Render(restored);
                AssertImages(expected, actual);
                using var visibleEmission = new MaterialOwner(restored);
                visibleEmission.Value.SetColor("_EmissionColor", new Color(4, 3, 2, 1));
                var lit = Render(visibleEmission.Value);
                Assert.That(lit.Zip(actual, (a, b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b)).Max(),
                    Is.GreaterThan(.1f), "The pixel comparison must exercise emission alpha rather than an inactive shader feature.");
            }
            Assert.That(f.ImportedFront.sharedMaterial, Is.SameAs(native));
            Assert.That(EditorJsonUtility.ToJson(f.First), Is.EqualTo(sourceState));
            Assert.That(f.Avatar.Mesh.vertices, Is.EqualTo(sourceMesh));
            Assert.That(f.Avatar.Source.GetComponentsInChildren<Transform>(true).Select(t => t.localToWorldMatrix), Is.EqualTo(sourceMatrices));
            Assert.That(f.Avatar.Source.GetComponentsInChildren<SkinnedMeshRenderer>().Select(s => s.sharedMaterial), Is.EqualTo(new[] { f.First, f.Second }));
            Assert.That(bytes, Is.EqualTo(originalBytes));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ResizedEmbeddedPixelsSamplerAndTextureTransformsSurviveTheActualRoundTrip(bool srgb)
        {
            using var f = new Fixture();
            var color = new Color32(73, 119, 181, 93);
            var source = f.Own(new Texture2D(2048, 8, TextureFormat.RGBA32, false, !srgb) { name = "Preview fixture resized color" });
            source.SetPixels32(Enumerable.Repeat(color, source.width * source.height).ToArray()); source.Apply(false, false);
            source.filterMode = FilterMode.Point; source.wrapModeU = TextureWrapMode.Clamp;
            source.wrapModeV = TextureWrapMode.Mirror; source.wrapModeW = TextureWrapMode.Repeat;
            source.anisoLevel = 3; source.mipMapBias = .25f;
            f.First.SetTexture("_MainTex", source); f.First.SetTexture("_EmissionMap", source); f.Second.SetTexture("_MainTex", source);
            f.First.SetTextureScale("_MainTex", new Vector2(2.5f, .75f)); f.First.SetTextureOffset("_MainTex", new Vector2(-.1f, .2f));
            var bytes = f.Export(); var originalBytes = (byte[])bytes.Clone();
            // A later source edit must not leak into a preview of already exported bytes.
            source.SetPixels32(Enumerable.Repeat(new Color32(255, 0, 0, 255), source.width * source.height).ToArray()); source.Apply(false, false);
            var editedPixels = source.GetPixels32();
            await f.Import(bytes);
            using (ExportVrmLilToonPreview.Apply(bytes, f.Imported.gameObject))
            {
                var material = f.ImportedFront.sharedMaterial;
                var image = (Texture2D)material.GetTexture("_MainTex");
                Assert.That(image, Is.Not.SameAs(source));
                Assert.That(image.width, Is.EqualTo(1024)); Assert.That(image.height, Is.EqualTo(4));
                Assert.That(image.mipmapCount, Is.EqualTo(1)); Assert.That(image.isDataSRGB, Is.EqualTo(srgb));
                Assert.That(image.filterMode, Is.EqualTo(FilterMode.Point)); Assert.That(image.wrapModeU, Is.EqualTo(TextureWrapMode.Clamp));
                Assert.That(image.wrapModeV, Is.EqualTo(TextureWrapMode.Mirror)); Assert.That(image.wrapModeW, Is.EqualTo(TextureWrapMode.Repeat));
                Assert.That(image.anisoLevel, Is.EqualTo(3)); Assert.That(image.mipMapBias, Is.EqualTo(.25f));
                Assert.That(material.GetTextureScale("_MainTex"), Is.EqualTo(new Vector2(2.5f, .75f)));
                Assert.That(material.GetTextureOffset("_MainTex"), Is.EqualTo(new Vector2(-.1f, .2f)));
                Assert.That(material.GetTexture("_EmissionMap"), Is.SameAs(image));
                Assert.That(f.ImportedBack.sharedMaterial.GetTexture("_MainTex"), Is.SameAs(image));
                AssertRgba(LilToonFullTexture.Read(image, 0, 0, false, srgb, false), color);
            }
            Assert.That(source.width, Is.EqualTo(2048)); Assert.That(source.height, Is.EqualTo(8));
            Assert.That(source.GetPixels32(), Is.EqualTo(editedPixels));
            Assert.That(f.First.GetTexture("_MainTex"), Is.SameAs(source));
            Assert.That(bytes, Is.EqualTo(originalBytes));
        }

        [Test]
        public async Task HdrAlphaAndAuthoredCubeFacesAndMipLevelsArePreserved()
        {
            using var f = new Fixture();
            var hdr = f.Own(new Texture2D(4, 4, TextureFormat.RGBAHalf, false, true) { name = "Preview fixture HDR" });
            hdr.SetPixels(Enumerable.Repeat(new Color(3, .25f, 1.5f, .125f), 16).ToArray()); hdr.Apply(false, false);
            var cube = f.Own(new Cubemap(4, TextureFormat.RGBA32, true) { name = "Preview fixture authored cube", filterMode = FilterMode.Point });
            for (var face = 0; face < 6; face++) for (var mip = 0; mip < 3; mip++)
                cube.SetPixels(Enumerable.Repeat((Color)CubeColor(face, mip), Math.Max(1, 4 >> mip) * Math.Max(1, 4 >> mip)).ToArray(), (CubemapFace)face, mip);
            cube.Apply(false, false);
            f.First.SetTexture("_EmissionMap", hdr); f.First.SetTexture("_ReflectionCubeTex", cube);
            var bytes = f.Export(); await f.Import(bytes);
            using (ExportVrmLilToonPreview.Apply(bytes, f.Imported.gameObject))
            {
                var restored = (Texture2D)f.ImportedFront.sharedMaterial.GetTexture("_EmissionMap");
                Assert.That(restored.format, Is.EqualTo(TextureFormat.RGBAHalf));
                var pixels = LilToonFullTexture.Read(restored, 0, 0, false, false, true);
                for (var i = 0; i < 16; i++)
                {
                    Assert.That(BitConverter.ToSingle(pixels, i * 16), Is.EqualTo(3).Within(.002f));
                    Assert.That(BitConverter.ToSingle(pixels, i * 16 + 4), Is.EqualTo(.25f).Within(.002f));
                    Assert.That(BitConverter.ToSingle(pixels, i * 16 + 8), Is.EqualTo(1.5f).Within(.002f));
                    Assert.That(BitConverter.ToSingle(pixels, i * 16 + 12), Is.EqualTo(.125f).Within(.002f));
                }
                var restoredCube = (Cubemap)f.ImportedFront.sharedMaterial.GetTexture("_ReflectionCubeTex");
                Assert.That(restoredCube.mipmapCount, Is.EqualTo(3));
                for (var face = 0; face < 6; face++) for (var mip = 0; mip < 3; mip++)
                    AssertRgba(LilToonFullTexture.Read(restoredCube, mip, face, false, cube.isDataSRGB, false), CubeColor(face, mip));
            }
        }

        [Test]
        public async Task NonFlatNormalsRetainTheirNativeDecodedDirectionAtEveryAuthoredMip()
        {
            using var f = new Fixture();
            var normal = f.Own(new Texture2D(8, 8, TextureFormat.RGBA32, 4, true)
                { name = "Preview fixture authored normal mips", filterMode = FilterMode.Point });
            var authoredPixels = new List<Color32[]>();
            for (var mip = 0; mip < 4; mip++)
            {
                var size = Math.Max(1, 8 >> mip);
                var color = new Color32((byte)(101 + mip * 13), (byte)(157 - mip * 9), 239, (byte)(213 - mip * 11));
                var pixels = Enumerable.Repeat(color, size * size).ToArray();
                normal.SetPixels32(pixels, mip); authoredPixels.Add(pixels);
            }
            normal.Apply(false, false);
            f.First.SetFloat("_UseBumpMap", 1); f.First.SetFloat("_BumpScale", .8f); f.First.SetTexture("_BumpMap", normal);
            // Read the directions sampled from the authored native encoding.
            // The restored texture has different packed bytes; its decoded
            // normals must still agree, including the intentionally distinct mips.
            var expected = Enumerable.Range(0, 4).Select(mip => LilToonFullTexture.Read(normal, mip, 0, true, false, true)).ToArray();
            Assert.That(expected.SelectMany(pixels => Enumerable.Range(0, pixels.Length / 16)
                .Select(i => Mathf.Abs(BitConverter.ToSingle(pixels, i * 16) - .5f))).Max(), Is.GreaterThan(.05f),
                "A flat normal cannot exercise the native packing conversion.");
            var bytes = f.Export(); var originalBytes = (byte[])bytes.Clone(); await f.Import(bytes);
            using (ExportVrmLilToonPreview.Apply(bytes, f.Imported.gameObject))
            {
                var restored = (Texture2D)f.ImportedFront.sharedMaterial.GetTexture("_BumpMap");
                Assert.That(restored, Is.Not.SameAs(normal)); Assert.That(restored.mipmapCount, Is.EqualTo(4));
                Assert.That(restored.isDataSRGB, Is.False); Assert.That(f.ImportedFront.sharedMaterial.GetFloat("_BumpScale"), Is.EqualTo(.8f));
                for (var mip = 0; mip < 4; mip++)
                {
                    var actual = LilToonFullTexture.Read(restored, mip, 0, true, false, true);
                    Assert.That(actual.Length, Is.EqualTo(expected[mip].Length));
                    for (var component = 0; component < actual.Length; component += 4)
                        Assert.That(BitConverter.ToSingle(actual, component),
                            Is.EqualTo(BitConverter.ToSingle(expected[mip], component)).Within(2e-5f), "mip " + mip + " byte " + component);
                    Assert.That(normal.GetPixels32(mip), Is.EqualTo(authoredPixels[mip]));
                }
            }
            Assert.That(bytes, Is.EqualTo(originalBytes));
            Assert.That(f.First.GetTexture("_BumpMap"), Is.SameAs(normal));
        }

        [Test]
        public async Task DuplicateNamesAndReorderedObjectsCannotSwapPrimitiveMaterialsOrFourComponentUvs()
        {
            using var f = new Fixture();
            f.UseTwoPrimitives();
            var sourceMesh = f.Front.sharedMesh;
            f.Front.sortingOrder = 17; f.Back.sortingOrder = -3;
            f.Front.receiveShadows = false; f.Front.shadowCastingMode = ShadowCastingMode.Off;
            f.Front.probeAnchor = f.Avatar.Head;
            var bounds = new Bounds(new Vector3(.1f, 1.7f, -.2f), new Vector3(.8f, .6f, .4f)); f.Front.localBounds = bounds;
            var bytes = f.Export(); await f.Import(bytes);
            var front = f.ImportedFront; var back = f.ImportedBack;
            front.name = back.name = "Duplicate renderer"; front.transform.SetAsLastSibling();
            foreach (var material in f.Imported.GetComponentsInChildren<Renderer>().SelectMany(r => r.sharedMaterials)) material.name = "Duplicate material";
            using (ExportVrmLilToonPreview.Apply(bytes, f.Imported.gameObject))
            {
                Assert.That(front.sharedMaterials.Length, Is.EqualTo(2));
                for (var channel = 0; channel < 4; channel++)
                {
                    Assert.That(front.sharedMaterials[0].GetColor("_Color")[channel], Is.EqualTo(f.First.GetColor("_Color")[channel]).Within(1e-6f), "first material channel " + channel);
                    Assert.That(front.sharedMaterials[1].GetColor("_Color")[channel], Is.EqualTo(f.Second.GetColor("_Color")[channel]).Within(1e-6f), "second material channel " + channel);
                }
                Assert.That(back.sharedMaterial, Is.SameAs(front.sharedMaterials[1]));
                Assert.That(front.sharedMesh.vertexCount, Is.EqualTo(6));
                foreach (var channel in new[] { 0, 1, 7 })
                {
                    var actual = new List<Vector4>(); front.sharedMesh.GetUVs(channel, actual);
                    var authored = new List<Vector4>(); sourceMesh.GetUVs(channel, authored);
                    Assert.That(actual, Is.EqualTo(new[] { 0, 1, 2, 1, 2, 3 }.Select(i => authored[i])), "UV channel " + channel);
                }
                Assert.That(front.sortingOrder, Is.GreaterThan(back.sortingOrder), "The relative draw order survives host sorting-layer differences.");
                Assert.That(front.receiveShadows, Is.False); Assert.That(front.shadowCastingMode, Is.EqualTo(ShadowCastingMode.Off));
                Assert.That(front.localBounds, Is.EqualTo(bounds));
                Assert.That(f.Imported.TryGetBoneTransform(HumanBodyBones.Head, out var importedHead), Is.True);
                Assert.That(front.probeAnchor, Is.SameAs(importedHead));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task DisposalRestoresImportStateAndReleasesOwnedResourcesEvenAfterRootDestruction(bool destroyRootFirst)
        {
            using var f = new Fixture();
            var bytes = f.Export(); await f.Import(bytes);
            var renderer = f.ImportedFront;
            var originalMesh = renderer.sharedMesh; var originalMaterials = renderer.sharedMaterials;
            var originalBounds = renderer.localBounds; renderer.sortingOrder = 83;
            var block = new MaterialPropertyBlock(); block.SetFloat("_PreviewFixtureMarker", .625f); renderer.SetPropertyBlock(block);
            var preview = ExportVrmLilToonPreview.Apply(bytes, f.Imported.gameObject);
            var restoredMesh = renderer.sharedMesh; var restoredMaterials = renderer.sharedMaterials;
            var restoredTexture = restoredMaterials[0].GetTexture("_BumpMap");
            try
            {
                Assert.That(restoredMesh, Is.Not.SameAs(originalMesh));
                Assert.That(restoredTexture, Is.Not.Null);
                if (destroyRootFirst) { Object.DestroyImmediate(f.Imported.gameObject); f.Imported = null; }
                preview.Dispose(); preview.Dispose();
                Assert.That(restoredMesh == null, Is.True);
                Assert.That(restoredMaterials.All(m => m == null), Is.True);
                Assert.That(restoredTexture == null, Is.True);
                if (!destroyRootFirst)
                {
                    Assert.That(renderer.sharedMesh, Is.SameAs(originalMesh));
                    Assert.That(renderer.sharedMaterials, Is.EqualTo(originalMaterials));
                    Assert.That(renderer.localBounds, Is.EqualTo(originalBounds)); Assert.That(renderer.sortingOrder, Is.EqualTo(83));
                    renderer.GetPropertyBlock(block); Assert.That(block.GetFloat("_PreviewFixtureMarker"), Is.EqualTo(.625f));
                    Assert.That(originalMesh != null && originalMaterials.All(m => m != null), Is.True);
                }
                Assert.That(f.First != null && f.Second != null && f.Avatar.Mesh != null, Is.True);
            }
            finally { preview.Dispose(); }
        }

        [TestCase("missing")]
        [TestCase("version")]
        [TestCase("corruptPayload")]
        public async Task InvalidSavedAppearanceFailsInsteadOfReturningAnMToonPreview(string corruption)
        {
            using var f = new Fixture();
            var bytes = f.Export(); await f.Import(bytes);
            var document = GlbDocument.Read(bytes);
            if (corruption == "missing") F.Object(F.Get(document.Json, "extensions")).Remove(F.ExtensionName);
            else if (corruption == "version") F.Root(document.Json)["schemaMajor"] = 99;
            else
            {
                var chunk = F.Object(F.List(F.Root(document.Json), "chunks")[0]);
                var view = F.Object(F.List(document.Json, "bufferViews")[F.Int(chunk, "bufferView")]);
                var offset = view.ContainsKey("byteOffset") ? F.Int(view, "byteOffset") : 0;
                for (var i = 0; i < F.Int(view, "byteLength"); i++) document.Binary[offset + i] = 0;
            }
            var malformed = document.Write(); var unchanged = (byte[])malformed.Clone();
            var oldMesh = f.ImportedFront.sharedMesh; var oldMaterial = f.ImportedFront.sharedMaterial;
            IDisposable result = null;
            try
            {
                Assert.Throws<InvalidDataException>(() => result = ExportVrmLilToonPreview.Apply(malformed, f.Imported.gameObject));
                Assert.That(result, Is.Null, "Only a completely restored appearance may be exposed as a successful preview.");
                Assert.That(f.ImportedFront.sharedMesh, Is.SameAs(oldMesh)); Assert.That(f.ImportedFront.sharedMaterial, Is.SameAs(oldMaterial));
                Assert.That(malformed, Is.EqualTo(unchanged));
            }
            finally { result?.Dispose(); }
        }

        [Test]
        public async Task AFailureOnTheSecondRendererDoesNotPublishTheFirstOrLeakPreviewResources()
        {
            using var f = new Fixture();
            var bytes = f.Export(); await f.Import(bytes);
            f.ImportedBack.sharedMaterials = Array.Empty<Material>();
            var meshes = f.Imported.GetComponentsInChildren<SkinnedMeshRenderer>().Select(r => r.sharedMesh).ToArray();
            var first = f.ImportedFront.sharedMaterial;
            var resourceIds = PreviewResourceIds();
            IDisposable result = null;
            try
            {
                Assert.Throws<InvalidDataException>(() => result = ExportVrmLilToonPreview.Apply(bytes, f.Imported.gameObject));
                Assert.That(f.ImportedFront.sharedMaterial, Is.SameAs(first));
                Assert.That(f.ImportedBack.sharedMaterials, Is.Empty);
                Assert.That(f.Imported.GetComponentsInChildren<SkinnedMeshRenderer>().Select(r => r.sharedMesh), Is.EqualTo(meshes));
                Assert.That(PreviewResourceIds(), Is.EquivalentTo(resourceIds));
            }
            finally { result?.Dispose(); }
        }

        [Test]
        public async Task ReorderedVertexIdDependentMaterialsAreRejectedRatherThanRenderedWithDifferentMasks()
        {
            using var f = new Fixture();
            f.UseTwoPrimitives();
            f.Second.SetFloat("_IDMaskFrom", 8); f.Second.SetFloat("_IDMask1", 1);
            var bytes = f.Export(); await f.Import(bytes);
            var oldMaterial = f.ImportedFront.sharedMaterial;
            IDisposable result = null;
            try
            {
                Assert.Throws<InvalidDataException>(() => result = ExportVrmLilToonPreview.Apply(bytes, f.Imported.gameObject));
                Assert.That(f.ImportedFront.sharedMaterial, Is.SameAs(oldMaterial));
            }
            finally { result?.Dispose(); }
        }

        static int[] PreviewResourceIds() => Resources.FindObjectsOfTypeAll<Object>()
            .Where(value => (value is Material || value is Mesh || value is Texture2D || value is Cubemap) && value.hideFlags == HideFlags.HideAndDontSave)
            .Select(value => value.GetInstanceID()).OrderBy(value => value).ToArray();

        static Color32 CubeColor(int face, int mip) => new Color32((byte)(31 + face * 23), (byte)(59 + mip * 31), 173, 211);
        static void AssertRgba(byte[] pixels, Color32 expected)
        {
            Assert.That(pixels.Length, Is.GreaterThan(0)); Assert.That(pixels.Length % 4, Is.Zero);
            var components = new[] { expected.r, expected.g, expected.b, expected.a };
            for (var pixel = 0; pixel < pixels.Length; pixel++)
                Assert.That((int)pixels[pixel], Is.EqualTo((int)components[pixel % 4]).Within(1), "component " + pixel);
        }

        static void AssertImages(Color[] expected, Color[] actual)
        {
            var region = Enumerable.Range(0, expected.Length).Where(i => expected[i].a > .5f || actual[i].a > .5f).ToArray();
            Assert.That(region.Length, Is.GreaterThan(100), "A blank image must not satisfy the comparison.");
            foreach (var index in region)
                for (var channel = 0; channel < 3; channel++) Assert.That(actual[index][channel], Is.EqualTo(expected[index][channel]).Within(2f / 255));
        }

        static Color[] Render(Material material)
        {
            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere); sphere.layer = 31; sphere.hideFlags = HideFlags.HideAndDontSave;
            var cameraObject = new GameObject("Preview fixture camera") { hideFlags = HideFlags.HideAndDontSave };
            var camera = cameraObject.AddComponent<Camera>(); camera.enabled = false;
            var target = new RenderTexture(96, 96, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var pixels = new Texture2D(96, 96, TextureFormat.RGBAFloat, false, true);
            var previous = RenderTexture.active;
            try
            {
                sphere.GetComponent<Renderer>().sharedMaterial = material;
                camera.transform.position = new Vector3(0, 0, -2); camera.fieldOfView = 40; camera.cullingMask = 1 << 31;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.clear; camera.allowHDR = true; camera.allowMSAA = false;
                target.Create(); camera.targetTexture = target; camera.Render(); RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, 96, 96), 0, 0); pixels.Apply(false, false); return pixels.GetPixels();
            }
            finally
            {
                camera.targetTexture = null; RenderTexture.active = previous; target.Release();
                Object.DestroyImmediate(sphere); Object.DestroyImmediate(cameraObject); Object.DestroyImmediate(target); Object.DestroyImmediate(pixels);
            }
        }

        sealed class MaterialOwner : IDisposable
        {
            internal readonly Material Value;
            internal MaterialOwner(Material source) { Value = new Material(source); }
            public void Dispose() { Object.DestroyImmediate(Value); }
        }

        sealed class Fixture : IDisposable
        {
            internal readonly AttachmentConnectionTests.Fixture Avatar = new AttachmentConnectionTests.Fixture();
            readonly List<Object> owned = new List<Object>();
            internal readonly Material First, Second;
            internal Vrm10Instance Imported;
            internal SkinnedMeshRenderer Front => Avatar.Skins.Single(r => r.name == "Front");
            internal SkinnedMeshRenderer Back => Avatar.Skins.Single(r => r.name == "Back");
            internal SkinnedMeshRenderer ImportedFront => Imported.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r => r.name == "Front");
            internal SkinnedMeshRenderer ImportedBack => Imported.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r => r.name == "Back");

            internal Fixture()
            {
                var shader = Shader.Find("lilToon"); Assert.That(shader, Is.Not.Null, "The installed real lilToon package is required.");
                First = Own(new Material(shader) { name = "Duplicate authored material" }); First.SetColor("_Color", new Color(.2f, .3f, .4f, 1));
                Second = Own(new Material(shader) { name = First.name }); Second.SetColor("_Color", new Color(.7f, .4f, .2f, 1));
                foreach (var root in new[] { Avatar.Source, Avatar.Copy })
                    foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial = skin.name == "Front" ? First : Second;
                Avatar.PrepareMeshesForExport();
            }

            internal T Own<T>(T value) where T : Object { owned.Add(value); return value; }

            internal void UseTwoPrimitives()
            {
                var mesh = Own(new Mesh { name = "Preview fixture overlapping primitives" });
                mesh.vertices = new[] { new Vector3(-.1f, 1.7f, .08f), new Vector3(.1f, 1.7f, .08f), new Vector3(-.1f, 1.9f, .08f), new Vector3(.1f, 1.9f, .08f) };
                mesh.normals = Enumerable.Repeat(Vector3.forward, 4).ToArray();
                mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 4).ToArray(); mesh.bindposes = Avatar.Mesh.bindposes;
                mesh.subMeshCount = 2; mesh.SetTriangles(new[] { 0, 1, 2 }, 0); mesh.SetTriangles(new[] { 1, 3, 2 }, 1);
                foreach (var channel in new[] { 0, 1, 7 })
                    mesh.SetUVs(channel, Enumerable.Range(0, 4).Select(i => new Vector4(i + .1f, channel + .2f, i + .3f, channel + .4f)).ToList());
                Front.sharedMesh = mesh; Front.sharedMaterials = new[] { First, Second };
            }

            internal byte[] Export()
            {
                var snapshot = LilToonFullSnapshot.Capture(Avatar.Copy);
                var skins = Avatar.Skins; var original = skins.Select(r => r.sharedMaterials).ToArray();
                var replacements = new Dictionary<Material, Material>();
                foreach (var source in original.SelectMany(values => values).Distinct())
                    replacements.Add(source, Own(new Material(Shader.Find("VRM10/MToon10")) { name = source.name }));
                try
                {
                    for (var i = 0; i < skins.Length; i++) skins[i].sharedMaterials = original[i].Select(material => replacements[material]).ToArray();
                    var bytes = Vrm10AppearanceExporter.Export(new GltfExportSettings(), Avatar.Copy,
                        new BuiltInVrm10MaterialExporter(), new MobileTextureSerializer(null),
                        new VRM10ObjectMeta { Name = "Saved appearance fixture", Version = "1", Authors = new List<string> { "Tests" } },
                        afterExport: snapshot.Bind);
                    return snapshot.Inject(bytes, "preview-tests", "2.3.4");
                }
                finally { for (var i = 0; i < skins.Length; i++) skins[i].sharedMaterials = original[i]; }
            }

            internal async Task Import(byte[] bytes)
            {
                Imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller(),
                    controlRigGenerationOption: ControlRigGenerationOption.None);
                Assert.That(Imported, Is.Not.Null);
                Imported.UpdateType = Vrm10Instance.UpdateTypes.None;
            }

            public void Dispose()
            {
                if (Imported != null) Object.DestroyImmediate(Imported.gameObject);
                Avatar.Dispose();
                foreach (var value in owned) if (value != null) Object.DestroyImmediate(value);
            }
        }
    }
}
