using System;
using NUnit.Framework;
using UnityEngine;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class RendererSelectionTests
    {
        [TestCase("inactive-object")]
        [TestCase("inactive-parent")]
        [TestCase("disabled-renderer")]
        public void HiddenOutfitDoesNotRequireAFallbackMaterial(string mode)
        {
            var avatar = new GameObject("avatar");
            var body = new Material(Shader.Find("Hidden/VRVlogTests/lilToon")) { name = "body" };
            var outfit = new Material(body) { name = "outfit" };
            try
            {
                foreach (var material in new[] { body, outfit })
                {
                    material.SetFloat("_UseEmission", 0f);
                    material.SetFloat("_UseShadow", 0f);
                    material.SetFloat("_UseOutline", 0f);
                    material.SetTexture("_MainTex", null);
                }
                var wardrobe = new GameObject("wardrobe");
                wardrobe.transform.SetParent(avatar.transform);
                var clothing = new GameObject("clothing");
                clothing.transform.SetParent(wardrobe.transform);
                var hidden = clothing.AddComponent<SkinnedMeshRenderer>();
                hidden.sharedMaterial = outfit;
                if (mode == "inactive-object") clothing.SetActive(false);
                if (mode == "inactive-parent") wardrobe.SetActive(false);
                if (mode == "disabled-renderer") hidden.enabled = false;
                var visible = avatar.AddComponent<SkinnedMeshRenderer>();
                visible.sharedMaterial = body;
                var source = MaterialBindingFixture.Build("body");
                var result = LilToonGlbExtension.Inject(source, avatar, "0.4.1", "2.3.4");
                Assert.AreEqual(1, MaterialBindingFixture.InjectedMaterials(result).Count);
                Assert.AreSame(outfit, hidden.sharedMaterial);
                Assert.AreEqual(mode != "inactive-object", clothing.activeSelf);
                Assert.AreEqual(mode != "inactive-parent", wardrobe.activeSelf);
                Assert.AreEqual(mode != "disabled-renderer", hidden.enabled);

                // Re-enabling the outfit makes it required again, never silently
                // discard a visible material just because name lookup fails.
                clothing.SetActive(true);
                wardrobe.SetActive(true);
                hidden.enabled = true;
                var error = Assert.Throws<InvalidOperationException>(() =>
                    LilToonGlbExtension.Inject(source, avatar, "0.4.1", "2.3.4"));
                StringAssert.Contains("outfit", error.Message);
                result = LilToonGlbExtension.Inject(MaterialBindingFixture.Build("body", "outfit"), avatar, "0.4.1", "2.3.4");
                Assert.AreEqual(2, MaterialBindingFixture.InjectedMaterials(result).Count);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(avatar);
                UnityEngine.Object.DestroyImmediate(body);
                UnityEngine.Object.DestroyImmediate(outfit);
            }
        }

        [Test]
        public void InactiveAncestorIsRejectedBeforeCreatingAnActiveClone()
        {
            var parent = new GameObject("inactive parent");
            try
            {
                var avatar = new GameObject("avatar");
                avatar.transform.SetParent(parent.transform);
                parent.SetActive(false);
                Assert.Throws<InvalidOperationException>(() => UniVrmOneClickExporter.Export(avatar, "avatar", "Tests"));
            }
            finally { UnityEngine.Object.DestroyImmediate(parent); }
        }
    }
}
