using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public class ExportFidelityTests
    {
        [Test]
        public void SecondLayerIsCompositedAfterBaseTintAndSerializedOnce()
        {
            var shader = Shader.Find("lilToon");
            Assert.That(shader, Is.Not.Null, "Install lilToon 2.3.4 to run GPU bake tests.");
            var material = new Material(shader);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            texture.SetPixels(Enumerable.Repeat(Color.red, 4).ToArray());
            texture.Apply();
            var baked = new List<Texture2D>();
            try
            {
                material.SetTexture("_MainTex", texture);
                material.SetColor("_Color", Color.black);
                material.SetFloat("_UseMain2ndTex", 1);
                material.SetTexture("_Main2ndTex", Texture2D.whiteTexture);
                material.SetTexture("_Main2ndBlendMask", Texture2D.whiteTexture);
                material.SetColor("_Color2nd", new Color(0, 1, 0, 0.5f));
                material.SetFloat("_Main2ndTexBlendMode", 0);
                Assert.That(LilToonMainTextureBaker.NeedsBake(material), Is.True);
                LilToonMainTextureBaker.Bake(material, baked, new List<string>());
                var pixel = baked.Single().GetPixel(0, 0);
                var expected = QualitySettings.activeColorSpace == ColorSpace.Linear ? Mathf.LinearToGammaSpace(0.5f) : 0.5f;
                Assert.That(pixel.r, Is.LessThan(0.02f));
                Assert.That(pixel.g, Is.EqualTo(expected).Within(0.02f));
                Assert.That(pixel.b, Is.LessThan(0.02f));
                Assert.That(material.GetColor("_Color"), Is.EqualTo(Color.white));
                Assert.That(material.GetFloat("_UseMain2ndTex"), Is.Zero);
                var record = LilToonMaterialReader.Read(material, 0, (image, _) => image == baked[0] ? 42 : 0);
                Assert.That(record.textures.Single(t => t.name == "_MainTex").textureIndex, Is.EqualTo(42));
                Assert.That(record.colors.Single(c => c.name == "_Color").r, Is.EqualTo(1));
                Assert.That(texture.GetPixel(0, 0), Is.EqualTo(Color.red));
            }
            finally
            {
                foreach (var image in baked) Object.DestroyImmediate(image);
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void ThirdLayerBlendsOverSecondLayerAndRetainsBaseAlpha()
        {
            var shader = Shader.Find("lilToon");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            var baked = new List<Texture2D>();
            try
            {
                material.SetTexture("_MainTex", Texture2D.whiteTexture);
                material.SetColor("_Color", new Color(1, 0, 0, 0.25f));
                foreach (var layer in new[] { "2nd", "3rd" })
                {
                    material.SetFloat("_UseMain" + layer + "Tex", 1);
                    material.SetTexture("_Main" + layer + "Tex", Texture2D.whiteTexture);
                    material.SetTexture("_Main" + layer + "BlendMask", Texture2D.whiteTexture);
                    material.SetFloat("_Main" + layer + "TexBlendMode", 0);
                }
                material.SetColor("_Color2nd", new Color(0, 1, 0, 0.5f));
                material.SetColor("_Color3rd", new Color(0, 0, 1, 0.5f));
                LilToonMainTextureBaker.Bake(material, baked, new List<string>());
                var pixel = baked.Single().GetPixel(0, 0);
                float Encoded(float value) => QualitySettings.activeColorSpace == ColorSpace.Linear ? Mathf.LinearToGammaSpace(value) : value;
                Assert.That(pixel.r, Is.EqualTo(Encoded(0.25f)).Within(0.02f));
                Assert.That(pixel.g, Is.EqualTo(Encoded(0.25f)).Within(0.02f));
                Assert.That(pixel.b, Is.EqualTo(Encoded(0.5f)).Within(0.02f));
                Assert.That(pixel.a, Is.EqualTo(0.25f).Within(0.01f));
                Assert.That(LilToonMainTextureBaker.NeedsBake(material), Is.False);
            }
            finally
            {
                foreach (var image in baked) Object.DestroyImmediate(image);
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void LayerBlendMaskDeterminesBakeResolutionAndVisibleColor()
        {
            var shader = Shader.Find("lilToon");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            var mask = new Texture2D(64, 16, TextureFormat.RGBA32, false, true);
            var baked = new List<Texture2D>();
            try
            {
                mask.SetPixels(Enumerable.Range(0, 64 * 16).Select(i => i % 64 < 32 ? Color.black : Color.white).ToArray());
                mask.Apply();
                material.SetTexture("_MainTex", Texture2D.whiteTexture);
                material.SetColor("_Color", Color.red);
                material.SetFloat("_UseMain2ndTex", 1);
                material.SetTexture("_Main2ndTex", Texture2D.whiteTexture);
                material.SetColor("_Color2nd", Color.green);
                material.SetTexture("_Main2ndBlendMask", mask);
                LilToonMainTextureBaker.Bake(material, baked, new List<string>());
                var image = baked.Single();
                Assert.That(image.width, Is.EqualTo(64));
                Assert.That(image.height, Is.EqualTo(16));
                Assert.That(image.GetPixel(4, 8).r, Is.GreaterThan(0.98f));
                Assert.That(image.GetPixel(4, 8).g, Is.LessThan(0.02f));
                Assert.That(image.GetPixel(60, 8).g, Is.GreaterThan(0.98f));
                Assert.That(image.GetPixel(60, 8).r, Is.LessThan(0.02f));
            }
            finally
            {
                foreach (var image in baked) Object.DestroyImmediate(image);
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(mask);
            }
        }

        [Test]
        public void LargeSourceBakesAtMobileLimitWithoutLosingLayer()
        {
            var shader = Shader.Find("lilToon");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            var original = new Texture2D(4096, 4, TextureFormat.RGBA32, false, false);
            var baked = new List<Texture2D>();
            try
            {
                original.SetPixels(Enumerable.Repeat(Color.red, 4096 * 4).ToArray());
                original.Apply();
                material.SetTexture("_MainTex", original);
                material.SetFloat("_UseMain2ndTex", 1);
                material.SetTexture("_Main2ndTex", Texture2D.whiteTexture);
                material.SetTexture("_Main2ndBlendMask", Texture2D.whiteTexture);
                material.SetColor("_Color2nd", Color.green);
                var warnings = new List<string>();
                LilToonMainTextureBaker.Bake(material, baked, warnings);
                Assert.That(baked.Single().width, Is.EqualTo(LilToonMobileProfile.DefaultMaximumTextureSize));
                Assert.That(baked.Single().height, Is.EqualTo(1));
                Assert.That(baked.Single().GetPixel(0, 0).g, Is.GreaterThan(0.98f));
                Assert.That(original.GetPixel(0, 0), Is.EqualTo(Color.red));
                Assert.That(warnings.Any(w => w.Contains("縮小")), Is.True);
            }
            finally
            {
                foreach (var image in baked) Object.DestroyImmediate(image);
                Object.DestroyImmediate(original);
                Object.DestroyImmediate(material);
            }
        }

        [TestCase("_Main2ndEnableLighting", 0f, "ライティングの適用: 0")]
        [TestCase("_Main2ndTex_Cull", 1f, "表示する面: 裏面のみ")]
        public void BakeReportsLayersThatNeedLightingOrSurfaceDirection(string property, float value, string setting)
        {
            var shader = Shader.Find("lilToon");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            var baked = new List<Texture2D>();
            try
            {
                material.SetFloat("_UseMain2ndTex", 1);
                material.SetFloat(property, value);
                var error = Assert.Throws<MaterialBakeException>(() => LilToonMainTextureBaker.Bake(material, baked, new List<string>()));
                Assert.That(error.Issues.Any(issue => issue.Setting == setting), Is.True);
                Assert.That(error.SourceUnchanged, Is.False);
                Assert.That(baked, Is.Empty);
                Assert.That(material.GetFloat("_UseMain2ndTex"), Is.EqualTo(1));
            }
            finally { Object.DestroyImmediate(material); }
        }

        [TestCase("2nd", false)]
        [TestCase("2nd", true)]
        [TestCase("3rd", false)]
        [TestCase("3rd", true)]
        public void StaticDecalBoundsAndUvCopyAreBaked(string layer, bool copy)
        {
            var material = CreateLayerMaterial(layer);
            var baked = new List<Texture2D>();
            try
            {
                material.SetFloat("_Main" + layer + "TexIsDecal", 1);
                material.SetFloat("_Main" + layer + "TexShouldCopy", copy ? 1 : 0);
                material.SetTextureScale("_Main" + layer + "Tex", new Vector2(2, 1));
                material.SetTextureOffset("_Main" + layer + "Tex", new Vector2(-1, 0));
                LilToonMainTextureBaker.Bake(material, baked, new List<string>());
                var image = baked.Single();
                var left = image.GetPixel(0, image.height / 2);
                var right = image.GetPixel(image.width - 1, image.height / 2);
                Assert.That(right.g, Is.GreaterThan(0.98f));
                Assert.That(left.g, Is.EqualTo(copy ? 1 : 0).Within(0.02f));
                Assert.That(left.r, Is.EqualTo(copy ? 0 : 1).Within(0.02f));
            }
            finally
            {
                foreach (var image in baked) Object.DestroyImmediate(image);
                Object.DestroyImmediate(material);
            }
        }

        [TestCase("2nd")]
        [TestCase("3rd")]
        public void MsdfCoverageAndUnusedLayerScrollZAreBaked(string layer)
        {
            var material = CreateLayerMaterial(layer);
            var source = HorizontalPattern(Color.black, Color.white);
            var baked = new List<Texture2D>();
            try
            {
                material.SetTexture("_Main" + layer + "Tex", source);
                material.SetFloat("_Main" + layer + "TexIsMSDF", 1);
                // lilToon's layer shader uses TexAngle; ScrollRotate.z is unused.
                material.SetVector("_Main" + layer + "Tex_ScrollRotate", new Vector4(0, 0, 1.234f, 0));
                LilToonMainTextureBaker.Bake(material, baked, new List<string>());
                var image = baked.Single();
                Assert.That(image.GetPixel(2, 2).r, Is.GreaterThan(0.98f));
                Assert.That(image.GetPixel(2, 2).g, Is.LessThan(0.02f));
                Assert.That(image.GetPixel(13, 2).g, Is.GreaterThan(0.98f));
                Assert.That(image.GetPixel(13, 2).r, Is.LessThan(0.02f));
                Assert.That(source.GetPixel(2, 2), Is.EqualTo(Color.black));
            }
            finally
            {
                foreach (var image in baked) Object.DestroyImmediate(image);
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(material);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void StaticLayerAngleAndUvCopyFlipPreservePattern(bool copyFlip)
        {
            var material = CreateLayerMaterial("2nd");
            var source = HorizontalPattern(Color.green, Color.blue);
            var baked = new List<Texture2D>();
            try
            {
                material.SetTexture("_Main2ndTex", source);
                material.SetColor("_Color2nd", Color.white);
                if (copyFlip)
                {
                    material.SetFloat("_Main2ndTexShouldCopy", 1);
                    material.SetFloat("_Main2ndTexShouldFlipCopy", 1);
                }
                else
                {
                    material.SetFloat("_Main2ndTexAngle", Mathf.PI);
                }
                LilToonMainTextureBaker.Bake(material, baked, new List<string>());
                var image = baked.Single();
                Assert.That(image.GetPixel(2, 2).g, Is.EqualTo(copyFlip ? 1 : 0).Within(0.02f));
                Assert.That(image.GetPixel(2, 2).b, Is.EqualTo(copyFlip ? 0 : 1).Within(0.02f));
                Assert.That(image.GetPixel(13, 2).g, Is.EqualTo(copyFlip ? 0 : 1).Within(0.02f));
                Assert.That(image.GetPixel(13, 2).b, Is.EqualTo(copyFlip ? 1 : 0).Within(0.02f));
            }
            finally
            {
                foreach (var image in baked) Object.DestroyImmediate(image);
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(material);
            }
        }

        private static Material CreateLayerMaterial(string layer)
        {
            var shader = Shader.Find("lilToon");
            Assert.That(shader, Is.Not.Null, "Install lilToon 2.3.4 to run GPU bake tests.");
            var material = new Material(shader);
            material.SetTexture("_MainTex", Texture2D.whiteTexture);
            material.SetColor("_Color", Color.red);
            material.SetFloat("_UseMain" + layer + "Tex", 1);
            material.SetTexture("_Main" + layer + "Tex", Texture2D.whiteTexture);
            material.SetTexture("_Main" + layer + "BlendMask", Texture2D.whiteTexture);
            material.SetColor("_Color" + layer, Color.green);
            return material;
        }

        private static Texture2D HorizontalPattern(Color left, Color right)
        {
            var texture = new Texture2D(16, 4, TextureFormat.RGBA32, false, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixels(Enumerable.Range(0, 16 * 4).Select(i => i % 16 < 8 ? left : right).ToArray());
            texture.Apply();
            return texture;
        }

        [Test]
        public void PreflightCollectsAllMaterialCausesWithoutChangingObjects()
        {
            var avatar = new GameObject("Avatar");
            var body = new GameObject("Body");
            var clothing = new GameObject("Clothing");
            body.transform.SetParent(avatar.transform, false);
            clothing.transform.SetParent(avatar.transform, false);
            var first = CreateLayerMaterial("2nd");
            var second = CreateLayerMaterial("2nd");
            try
            {
                first.name = second.name = "Same material name";
                first.SetFloat("_Main2ndTex_UVMode", 1);
                first.SetFloat("_Main2ndTexIsLeftOnly", 1);
                first.SetFloat("_UseMain3rdTex", 1);
                first.SetFloat("_Main3rdEnableLighting", 0);
                second.SetFloat("_Main2ndTexIsRightOnly", 1);
                var bodyRenderer = body.AddComponent<MeshRenderer>();
                var clothingRenderer = clothing.AddComponent<MeshRenderer>();
                bodyRenderer.sharedMaterial = first;
                clothingRenderer.sharedMaterial = second;
                var firstBefore = EditorJsonUtility.ToJson(first);
                var secondBefore = EditorJsonUtility.ToJson(second);
                var materialCount = Resources.FindObjectsOfTypeAll<Material>().Length;
                var textureCount = Resources.FindObjectsOfTypeAll<Texture2D>().Length;
                var objectCount = Resources.FindObjectsOfTypeAll<GameObject>().Length;

                var error = Assert.Throws<MaterialBakeException>(() => LilToonMainTextureBaker.ValidateAvatar(avatar));

                Assert.That(error.SourceUnchanged, Is.True);
                Assert.That(error.Issues.Count, Is.EqualTo(4));
                Assert.That(error.Issues.Any(i => i.Material == first && i.RendererPath == "Body" && i.Layer == "2nd" && i.Setting == "UV Mode: UV1"), Is.True);
                Assert.That(error.Issues.Any(i => i.Material == first && i.RendererPath == "Body" && i.Layer == "2nd" && i.Setting == "左側のみ"), Is.True);
                Assert.That(error.Issues.Any(i => i.Material == first && i.RendererPath == "Body" && i.Layer == "3rd" && i.Setting == "ライティングの適用: 0"), Is.True);
                Assert.That(error.Issues.Any(i => i.Material == second && i.RendererPath == "Clothing" && i.Setting == "右側のみ"), Is.True);
                Assert.That(error.Issues.All(i => !string.IsNullOrWhiteSpace(i.Reason) && !string.IsNullOrWhiteSpace(i.NextStep)), Is.True);
                Assert.That(bodyRenderer.sharedMaterial, Is.SameAs(first));
                Assert.That(clothingRenderer.sharedMaterial, Is.SameAs(second));
                Assert.That(EditorJsonUtility.ToJson(first), Is.EqualTo(firstBefore));
                Assert.That(EditorJsonUtility.ToJson(second), Is.EqualTo(secondBefore));
                Assert.That(Resources.FindObjectsOfTypeAll<Material>().Length, Is.EqualTo(materialCount));
                Assert.That(Resources.FindObjectsOfTypeAll<Texture2D>().Length, Is.EqualTo(textureCount));
                Assert.That(Resources.FindObjectsOfTypeAll<GameObject>().Length, Is.EqualTo(objectCount));
            }
            finally
            {
                Object.DestroyImmediate(avatar);
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void PreflightExclusionsAndApprovedOmissionsUseMaterialIdentity()
        {
            var avatar = new GameObject("Avatar");
            var pet = new GameObject("Pet");
            pet.transform.SetParent(avatar.transform, false);
            var bodyMaterial = CreateLayerMaterial("2nd");
            var petMaterial = CreateLayerMaterial("2nd");
            try
            {
                bodyMaterial.name = petMaterial.name = "Shared display name";
                bodyMaterial.SetFloat("_Main2ndTexIsLeftOnly", 1);
                petMaterial.SetFloat("_Main2ndTexIsRightOnly", 1);
                avatar.AddComponent<MeshRenderer>().sharedMaterial = bodyMaterial;
                pet.AddComponent<MeshRenderer>().sharedMaterial = petMaterial;
                Func<Transform, bool> excluded = t => t == pet.transform || t.IsChildOf(pet.transform);
                var error = Assert.Throws<MaterialBakeException>(() => LilToonMainTextureBaker.ValidateAvatar(avatar, excluded));
                Assert.That(error.Issues.Count, Is.EqualTo(1));
                Assert.That(error.Issues.Single().Material, Is.SameAs(bodyMaterial));
                var originalOptions = new MaterialBakeOptions();
                var approved = originalOptions.WithOmissions(error.Issues);
                Assert.DoesNotThrow(() => LilToonMainTextureBaker.ValidateAvatar(avatar, excluded, approved));
                Assert.Throws<MaterialBakeException>(() => LilToonMainTextureBaker.ValidateAvatar(avatar, excluded, originalOptions));
                var remaining = Assert.Throws<MaterialBakeException>(() => LilToonMainTextureBaker.ValidateAvatar(avatar, null, approved));
                Assert.That(remaining.Issues.Count, Is.EqualTo(1));
                Assert.That(remaining.Issues.Single().Material, Is.SameAs(petMaterial));
                Assert.That(bodyMaterial.GetFloat("_UseMain2ndTex"), Is.EqualTo(1));
                Assert.That(petMaterial.GetFloat("_UseMain2ndTex"), Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(avatar);
                Object.DestroyImmediate(bodyMaterial);
                Object.DestroyImmediate(petMaterial);
            }
        }

        [Test]
        public void ApprovedLayerOmissionChangesOnlyExportCopy()
        {
            var avatar = new GameObject("Avatar");
            var source = CreateLayerMaterial("2nd");
            var created = new List<Material>();
            var textures = new List<Texture2D>();
            GameObject clone = null;
            try
            {
                source.SetFloat("_Main2ndTexShouldFlipMirror", 1);
                var renderer = avatar.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = source;
                var before = EditorJsonUtility.ToJson(source);
                var error = Assert.Throws<MaterialBakeException>(() => LilToonMainTextureBaker.ValidateAvatar(avatar));
                var options = new MaterialBakeOptions().WithOmissions(error.Issues);
                clone = Object.Instantiate(avatar);
                var warnings = new List<string>();
                LilToonMainTextureBaker.Prepare(clone, created, textures, warnings, false, false, options);
                var copy = clone.GetComponent<MeshRenderer>().sharedMaterial;
                Assert.That(copy, Is.Not.SameAs(source));
                Assert.That(created.Count, Is.EqualTo(1));
                Assert.That(created.Single(), Is.SameAs(copy));
                Assert.That(copy.GetFloat("_UseMain2ndTex"), Is.Zero);
                Assert.That(copy.GetTexture("_MainTex"), Is.SameAs(source.GetTexture("_MainTex")));
                Assert.That(copy.GetColor("_Color"), Is.EqualTo(source.GetColor("_Color")));
                Assert.That(textures, Is.Empty, "Omitting the only layer needs no GPU bake.");
                Assert.That(warnings.Count, Is.GreaterThan(0));
                Assert.That(renderer.sharedMaterial, Is.SameAs(source));
                Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(before));
            }
            finally
            {
                if (clone != null) Object.DestroyImmediate(clone);
                Object.DestroyImmediate(avatar);
                foreach (var material in created) Object.DestroyImmediate(material);
                foreach (var texture in textures) Object.DestroyImmediate(texture);
                Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void DifferentHdrEmissionImageCanBeSuppressedWithoutChangingSource()
        {
            var shader = Shader.Find("Hidden/VRVlogTests/lilToon");
            var avatar = new GameObject("avatar");
            var source = new Material(shader);
            var main = new Texture2D(2, 2);
            var emission = new Texture2D(2, 2);
            var created = new List<Material>();
            var textures = new List<Texture2D>();
            try
            {
                source.SetTexture("_MainTex", main);
                source.SetTexture("_EmissionMap", emission);
                source.SetFloat("_UseEmission", 1);
                source.SetColor("_EmissionColor", new Color(1.414f, 1.414f, 1.414f, 1));
                var renderer = avatar.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = source;
                var warnings = new List<string>();
                LilToonMainTextureBaker.Prepare(avatar, created, textures, warnings, true, true);
                Assert.That(renderer.sharedMaterial.GetFloat("_UseEmission"), Is.Zero);
                Assert.That(renderer.sharedMaterial.GetTexture("_MainTex"), Is.SameAs(main));
                Assert.That(source.GetFloat("_UseEmission"), Is.EqualTo(1));
                Assert.That(source.GetTexture("_EmissionMap"), Is.SameAs(emission));
                Assert.That(warnings.Count, Is.EqualTo(1));
                renderer.sharedMaterial = source;
                LilToonMainTextureBaker.Prepare(avatar, created, textures, warnings, true, false);
                Assert.That(renderer.sharedMaterial.GetFloat("_UseEmission"), Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(avatar);
                foreach (var material in created) Object.DestroyImmediate(material);
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(main);
                Object.DestroyImmediate(emission);
            }
        }

        // Match FaceEmo's serialized field contract without an SDK dependency.
        sealed class AnimationData { public string GUID; }
        sealed class BranchData { public AnimationData BaseAnimation; }
        sealed class ModeData
        {
            public bool ChangeDefaultFace = true;
            public string DisplayName = "茶色の目";
            public AnimationData Animation;
            public List<BranchData> Branches = new List<BranchData>();
        }
        sealed class ListData
        {
            public string DisplayName = "お気に入り";
            public List<string> Types;
            public List<ModeData> Modes = new List<ModeData>();
            public List<ListData> Groups = new List<ListData>();
        }

        sealed class SettingsData
        {
            public Component TargetAvatar;
            public List<Component> SubTargetAvatars = new List<Component>();
        }

        [Test]
        public void FaceEmoSettingsMatchExactPrimaryAndSecondaryAvatarObjects()
        {
            var avatar = new GameObject("avatar");
            var other = new GameObject("avatar"); // Names are deliberately identical.
            var child = new GameObject("child");
            child.transform.SetParent(avatar.transform);
            try
            {
                var settings = new SettingsData { TargetAvatar = other.transform };
                Assert.That(FaceEmoExpressions.TargetsAvatar(avatar, settings), Is.False);
                settings.TargetAvatar = avatar.transform;
                Assert.That(FaceEmoExpressions.TargetsAvatar(avatar, settings), Is.True);
                settings.TargetAvatar = child.transform;
                Assert.That(FaceEmoExpressions.TargetsAvatar(avatar, settings), Is.False);
                settings.SubTargetAvatars.Add(avatar.transform);
                Assert.That(FaceEmoExpressions.TargetsAvatar(avatar, settings), Is.True);
                Assert.That(FaceEmoExpressions.TargetsAvatar(avatar, null), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(avatar);
                Object.DestroyImmediate(other);
            }
        }

        [Test]
        public void FaceEmoReadsRegisteredNestedPatternsAndReportsMissingClips()
        {
            var avatar = new GameObject("avatar");
            var body = new GameObject("Body");
            body.transform.SetParent(avatar.transform);
            var mesh = new Mesh { vertices = new[] { Vector3.zero } };
            mesh.AddBlendShapeFrame("Smile", 100, new[] { Vector3.up }, new Vector3[1], new Vector3[1]);
            body.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
            var clip = new AnimationClip { name = "笑顔" };
            clip.SetCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Smile", AnimationCurve.Constant(0, 1, 75));
            var assetPath = "Assets/__VRVlogFaceEmoTest_" + Guid.NewGuid().ToString("N") + ".anim";
            try
            {
                AssetDatabase.CreateAsset(clip, assetPath);
                var valid = new AnimationData { GUID = AssetDatabase.AssetPathToGUID(assetPath) };
                var mode = new ModeData { Animation = valid };
                mode.Branches.Add(new BranchData { BaseAnimation = new AnimationData { GUID = "missing" } });
                var group = new ListData();
                group.Modes.Add(mode);
                var registered = new ListData();
                registered.Groups.Add(group);
                var source = new VrChatExpressionMenu.Source();
                var serial = 0;
                FaceEmoExpressions.ReadRegistered(avatar, registered, "FaceEmo", source, ref serial, new HashSet<object>(), 0);
                Assert.That(source.Entries.Count, Is.EqualTo(2));
                Assert.That(source.Entries[0].Name, Does.Contain("お気に入り / 茶色の目"));
                Assert.That(source.Entries[0].Values.Single().Weight, Is.EqualTo(75));
                Assert.That(source.Entries[1].Error, Is.Not.Null);
                Assert.That(body.GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.Zero);
                // FaceEmo stores groups and modes in separate arrays and their
                // interleaved authored order in Types.
                registered.Types = new List<string> { "Group", "Mode" };
                registered.Modes.Add(new ModeData { DisplayName = "末尾の表情", Animation = valid });
                var ordered = new VrChatExpressionMenu.Source();
                FaceEmoExpressions.ReadRegistered(avatar, registered, "FaceEmo", ordered, ref serial, new HashSet<object>(), 0);
                Assert.That(ordered.Entries.First().Name, Does.Contain("お気に入り"));
                Assert.That(ordered.Entries.Last().Name, Does.Contain("末尾の表情"));
                registered.Types.RemoveAt(1);
                Assert.Throws<InvalidOperationException>(() => FaceEmoExpressions.ReadRegistered(avatar, registered, "FaceEmo", new VrChatExpressionMenu.Source(), ref serial, new HashSet<object>(), 0));
                registered.Types = null;
                group.Groups.Add(registered);
                Assert.Throws<InvalidOperationException>(() => FaceEmoExpressions.ReadRegistered(avatar, registered, "FaceEmo", new VrChatExpressionMenu.Source(), ref serial, new HashSet<object>(), 0));
            }
            finally
            {
                AssetDatabase.DeleteAsset(assetPath);
                Object.DestroyImmediate(avatar);
                Object.DestroyImmediate(mesh);
            }
        }
    }
}
