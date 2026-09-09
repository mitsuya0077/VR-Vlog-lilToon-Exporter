using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UniVRM10;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class ExportGimmickTests
    {
        readonly List<Object> owned = new List<Object>();
        GameObject avatar;
        Material hidden, normal, unspecified;

        [SetUp] public void SetUp()
        {
            avatar = Own(new GameObject("avatar"));
            hidden = Own(new Material(Shader.Find("VRVlogTests/UnsupportedHiddenFallback")));
            normal = Own(new Material(Shader.Find("Hidden/VRVlogTests/lilToon")));
            unspecified = Own(new Material(Shader.Find("Hidden/VRVlogTests/ShadowSphere")));
        }
        [TearDown] public void TearDown()
        {
            for (var i = owned.Count - 1; i >= 0; i--) if (owned[i] != null) Object.DestroyImmediate(owned[i]);
            owned.Clear();
        }
        T Own<T>(T item) where T : Object { owned.Add(item); return item; }
        GameObject Child(string name, Transform parent = null)
        {
            var go = new GameObject(name); go.transform.SetParent(parent ?? avatar.transform, false); return go;
        }
        Mesh Mesh()
        {
            var mesh = Own(new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } });
            mesh.RecalculateNormals();
            mesh.AddBlendShapeFrame("Smile", 100, new[] { Vector3.up, Vector3.up, Vector3.up }, new Vector3[3], new Vector3[3]);
            return mesh;
        }
        MeshRenderer Render(string name, Material material, Transform parent = null)
        {
            var go = Child(name, parent); go.AddComponent<MeshFilter>().sharedMesh = Mesh();
            var renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material; return renderer;
        }
        SkinnedMeshRenderer Skin(string name, Material material)
        {
            var renderer = Child(name).AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = Mesh(); renderer.sharedMaterial = material; return renderer;
        }

        [Test] public void EffectiveHiddenTagRemovesOnlyUnsupportedMaterials()
        {
            Assert.That(ExportGimmickDetection.Inspect(Render("effect", hidden)).Unit, Is.EqualTo(GimmickExclusionUnit.Renderer));
            normal.SetOverrideTag("VRCFallback", "Hidden");
            Assert.That(ExportGimmickDetection.Inspect(Render("clothing", normal)), Is.Null);
            hidden.SetOverrideTag("VRCFallback", "Toon");
            Assert.That(ExportGimmickDetection.Inspect(Render("authored override", hidden)).Unit, Is.EqualTo(GimmickExclusionUnit.Review));
        }

        [Test] public void NamesBlackColorAndShapeDoNotIdentifyAGimmick()
        {
            Assert.That(ExportGimmickDetection.Inspect(Render("NadeShadow", unspecified)).Unit, Is.EqualTo(GimmickExclusionUnit.Review));
            var fake = Child("NadeSystem"); Child("HeadSystem", fake.transform); Child("Proxies", fake.transform);
            Assert.That(ExportGimmickDetection.KnownNadeRoot(fake), Is.False);
            Assert.That(ExportGimmickDetection.Analyze(avatar).Any(f => f.Unit == GimmickExclusionUnit.Hierarchy), Is.False);
        }

        [Test] public void MixedMissingAndUndersuppliedSlotsRemainIncluded()
        {
            var renderer = Render("mixed", hidden);
            renderer.sharedMaterials = new[] { hidden, normal };
            Assert.That(ExportGimmickDetection.Inspect(renderer).Unit, Is.EqualTo(GimmickExclusionUnit.Review));
            renderer.sharedMaterials = new Material[] { hidden, null };
            Assert.That(ExportGimmickDetection.Inspect(renderer).Unit, Is.EqualTo(GimmickExclusionUnit.Review));
            renderer.sharedMaterials = new[] { hidden };
            var mesh = renderer.GetComponent<MeshFilter>().sharedMesh; mesh.subMeshCount = 2;
            Assert.That(ExportGimmickDetection.Inspect(renderer).Unit, Is.EqualTo(GimmickExclusionUnit.Review));
            renderer.sharedMaterials = new[] { normal };
            Assert.That(ExportGimmickDetection.Inspect(renderer).Unit, Is.EqualTo(GimmickExclusionUnit.Review));
        }

        [Test] public void InactiveAndDisabledRenderersAreNotCandidates()
        {
            var renderer = Render("effect", hidden); renderer.enabled = false;
            Assert.That(ExportGimmickDetection.Inspect(renderer), Is.Null);
            renderer.enabled = true; renderer.gameObject.SetActive(false);
            Assert.That(ExportGimmickDetection.Inspect(renderer), Is.Null);
        }

        [Test] public void RemovingRendererKeepsItsTransformChildAndReferencedBone()
        {
            var effect = Render("effect", hidden);
            Render("accessory", normal, effect.transform);
            var skin = Skin("body", normal); skin.bones = new[] { effect.transform }; skin.rootBone = effect.transform;
            var clone = Own(Object.Instantiate(avatar));
            using var session = new ExportGimmickSession(avatar, clone, null);
            session.Apply(null, null, new List<string>());
            var kept = clone.transform.Find("effect");
            Assert.That(kept, Is.Not.Null);
            Assert.That(kept.GetComponent<Renderer>(), Is.Null);
            Assert.That(kept.Find("accessory").GetComponent<Renderer>(), Is.Not.Null);
            Assert.That(clone.transform.Find("body").GetComponent<SkinnedMeshRenderer>().bones[0], Is.SameAs(kept));
            Assert.That(effect.GetComponent<Renderer>(), Is.SameAs(effect));
            Assert.That(effect.sharedMaterial, Is.SameAs(hidden));
        }

        [Test] public void DuplicateNamesUseIdentityAndManualExclusionWins()
        {
            var first = Render("same", hidden); var second = Render("same", hidden);
            var clone = Own(Object.Instantiate(avatar));
            var options = new ExportGimmickOptions { IncludedObjects = new[] { second.gameObject } };
            using var session = new ExportGimmickSession(avatar, clone, options);
            using var exclusions = new ExportObjectExclusions(avatar, new[] { second.gameObject });
            exclusions.Apply(clone, null); session.Apply(null, null, null);
            Assert.That(clone.transform.childCount, Is.EqualTo(1));
            Assert.That(clone.GetComponentsInChildren<Renderer>(), Is.Empty);
            Assert.That(avatar.GetComponentsInChildren<Renderer>().Length, Is.EqualTo(2));
        }

        [Test] public void IncludeSurvivesRenameReparentAndSecondPassFindsGeneratedRenderer()
        {
            var first = Render("same", hidden); var second = Render("same", hidden);
            var clone = Own(Object.Instantiate(avatar));
            using var session = new ExportGimmickSession(avatar, clone, new ExportGimmickOptions { IncludedObjects = new[] { second.gameObject } });
            var kept = clone.transform.GetChild(1); kept.name = "moved"; kept.SetParent(clone.transform.GetChild(0), false);
            session.Apply(null, null, null);
            Assert.That(kept.GetComponent<Renderer>(), Is.Not.Null);
            var generated = Render("generated", hidden, clone.transform);
            session.Apply(null, null, null);
            Assert.That(generated == null, Is.True);
            Assert.That(kept.GetComponent<Renderer>(), Is.Not.Null);
        }

        [Test] public void DisabledAutomaticOptionPreservesAllRenderers()
        {
            Render("effect", hidden); var clone = Own(Object.Instantiate(avatar));
            using var session = new ExportGimmickSession(avatar, clone, new ExportGimmickOptions { AutoExclude = false });
            session.Apply(null, null, null);
            Assert.That(clone.GetComponentsInChildren<Renderer>().Length, Is.EqualTo(1));
        }

        [Test] public void UniVrmDoesNotSerializeRemovedGeometryOrItsMaterial()
        {
            Render("effect", hidden); Render("body", normal);
            var clone = Own(Object.Instantiate(avatar));
            using var session = new ExportGimmickSession(avatar, clone, null);
            session.Apply(null, null, null);
            using var arrays = new UniGLTF.NativeArrayManager();
            var exporter = new ModelExporter();
            var model = exporter.Export(new UniGLTF.GltfExportSettings(), arrays, clone);
            Assert.That(model.MeshGroups.Count, Is.EqualTo(1));
            Assert.That(exporter.Materials, Is.EquivalentTo(new[] { normal }));
            Assert.That(clone.transform.Find("effect"), Is.Not.Null);
        }

        [Test] public void PreparedMaterialChangeIsReevaluatedInsteadOfTrustingPreview()
        {
            Render("effect", hidden); var clone = Own(Object.Instantiate(avatar));
            using var session = new ExportGimmickSession(avatar, clone, null);
            clone.GetComponentInChildren<Renderer>().sharedMaterial = normal;
            session.Apply(null, null, null);
            Assert.That(clone.GetComponentInChildren<Renderer>(), Is.Not.Null);
        }

        [Test] public void RootWithOrdinaryClothingOrIncomingReferenceIsNotRemoved()
        {
            var root = Child("system"); var effect = Render("effect", hidden, root.transform);
            Assert.That(ExportGimmickDetection.UnsafeRootReason(avatar, root.transform, _ => false), Is.Null);
            var clothing = Render("clothing", normal, root.transform); clothing.gameObject.SetActive(false);
            Assert.That(ExportGimmickDetection.UnsafeRootReason(avatar, root.transform, _ => false), Is.Not.Null);
            Object.DestroyImmediate(clothing.gameObject);
            var skin = Skin("body", normal); skin.bones = new[] { effect.transform };
            Assert.That(ExportGimmickDetection.UnsafeRootReason(avatar, root.transform, _ => false), Is.Not.Null);
            Assert.That(ExportGimmickDetection.UnsafeRootReason(avatar, root.transform, t => t == skin.transform), Is.Null);
        }

        [Test] public void KeepingAChildPreventsAutomaticHierarchyDeletion()
        {
            var root = Child("system"); var child = Child("keep", root.transform);
            var findings = new[] { new ExportGimmickFinding { Target = root, Unit = GimmickExclusionUnit.Hierarchy } };
            Assert.That(ExportGimmickDetection.AutomaticRoots(findings, null).Single(), Is.SameAs(root));
            Assert.That(ExportGimmickDetection.AutomaticRoots(findings, new ExportGimmickOptions { IncludedObjects = new[] { child } }), Is.Empty);
        }

        [TestCase("ModularAvatarMergeArmature", "mergeTarget.referencePath")]
        [TestCase("ModularAvatarBoneProxy", "subPath")]
        public void MaPathBasedConnectionsProtectTheHierarchy(string componentName, string pathProperty)
        {
            Type FindType(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);
            var type = FindType("nadena.dev.modular_avatar.core." + componentName);
            var descriptor = FindType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            if (type == null || descriptor == null) Assert.Ignore("Installed MA and VRChat SDK are required for this connection regression.");
            avatar.AddComponent(descriptor);
            var root = Child("system"); var effect = Render("effect", hidden, root.transform);
            var wardrobe = Render("wardrobe", normal).gameObject;
            var component = wardrobe.AddComponent(type);
            using (var serialized = new SerializedObject(component))
            {
                serialized.FindProperty(pathProperty).stringValue = "system/effect";
                var direct = serialized.FindProperty("mergeTarget.targetObject");
                if (direct != null) direct.objectReferenceValue = null;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            Assert.That(NdmfExportPreparation.FollowingTarget(component), Is.SameAs(effect.transform));
            Assert.That(ExportGimmickDetection.UnsafeRootReason(avatar, root.transform, _ => false), Is.Not.Null);
            Assert.That(ExportGimmickDetection.UnsafeRootReason(avatar, root.transform, t => t == wardrobe.transform), Is.Null);
        }

        [Test] public void MissingMeshOrMaterialSlotsProtectTheHierarchy()
        {
            var root = Child("system"); var renderer = Render("effect", hidden, root.transform);
            var mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
            mesh.subMeshCount = 2;
            Assert.That(ExportGimmickDetection.UnsafeRootReason(avatar, root.transform, _ => false), Is.Not.Null);
            renderer.GetComponent<MeshFilter>().sharedMesh = null;
            Assert.That(ExportGimmickDetection.UnsafeRootReason(avatar, root.transform, _ => false), Is.Not.Null);
        }

        [Test] public void UnrecognizedAnimatorControllerProtectsTheHierarchy()
        {
            var root = Child("system"); Render("effect", hidden, root.transform);
            var animator = root.AddComponent<Animator>();
            Assert.That(ExportGimmickDetection.UnsafeRootReason(avatar, root.transform, _ => false), Is.Null);
            animator.runtimeAnimatorController = Own(new UnityEditor.Animations.AnimatorController());
            Assert.That(ExportGimmickDetection.UnsafeRootReason(avatar, root.transform, _ => false), Is.Not.Null);
        }

        [Test] public void MixedFacialExpressionDropsOnlyRemovedRendererBindings()
        {
            Skin("body", normal); Skin("effect", hidden);
            var menu = new VrChatExpressionMenu.Source();
            var mixed = new VrChatExpressionMenu.Entry { Name = "mixed" };
            mixed.Values.Add(new VrChatExpressionMenu.MorphValue { Path = "body", Shape = "Smile", Weight = 50 });
            mixed.Values.Add(new VrChatExpressionMenu.MorphValue { Path = "effect", Shape = "Smile", Weight = 50 });
            menu.Entries.Add(mixed);
            var only = new VrChatExpressionMenu.Entry { Name = "only effect" };
            only.Values.Add(new VrChatExpressionMenu.MorphValue { Path = "effect", Shape = "Smile", Weight = 50 }); menu.Entries.Add(only);
            var clone = Own(Object.Instantiate(avatar));
            var bindings = new PreparedExpressionBindings(clone, menu);
            using var session = new ExportGimmickSession(avatar, clone, null);
            session.Apply(bindings, menu, null); bindings.Capture(menu);
            Assert.That(mixed.Error, Is.Null);
            Assert.That(mixed.Values.Single().Path, Is.EqualTo("body"));
            Assert.That(only.Error, Is.Not.Null);
        }

        [Test] public void MeshReferencePruningKeepsBoneSpringAndOriginalVrmSettings()
        {
            var renderer = Skin("effect", hidden);
            var instance = avatar.AddComponent<Vrm10Instance>();
            var settings = Own(ScriptableObject.CreateInstance<VRM10Object>());
            var expression = Own(ScriptableObject.CreateInstance<VRM10Expression>());
            expression.MorphTargetBindings = new[] { new MorphTargetBinding { RelativePath = "effect", Index = 0, Weight = 1 } };
            settings.Expression.AddClip(ExpressionPreset.happy, expression);
            settings.FirstPerson.Renderers.Add(new RendererFirstPersonFlags { Renderer = "effect" });
            instance.Vrm = settings;
            var joint = renderer.gameObject.AddComponent<VRM10SpringBoneJoint>();
            var spring = new Vrm10InstanceSpringBone.Spring("keep") { Joints = { joint } };
            instance.SpringBone.Springs.Add(spring);
            var clone = Own(Object.Instantiate(avatar));
            using var session = new ExportGimmickSession(avatar, clone, null); session.Apply(null, null, null);
            var copy = clone.GetComponent<Vrm10Instance>();
            Assert.That(copy.SpringBone.Springs[0].Joints[0], Is.Not.Null);
            Assert.That(copy.Vrm.FirstPerson.Renderers, Is.Empty);
            Assert.That(copy.Vrm.Expression.Clips.First().Clip.MorphTargetBindings, Is.Empty);
            Assert.That(settings.FirstPerson.Renderers.Count, Is.EqualTo(1));
            Assert.That(expression.MorphTargetBindings.Length, Is.EqualTo(1));
        }
    }
}
