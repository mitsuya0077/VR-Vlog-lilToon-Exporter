using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class AppearancePreparationTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void PreparedRestAndMovedRendererKeepExpressionEndpointsWithoutDoubling(bool restChanges)
        {
            var source = new GameObject("Avatar");
            var face = new GameObject("Face"); face.transform.SetParent(source.transform, false);
            var skin = face.AddComponent<SkinnedMeshRenderer>();
            var original = BaseShapeFixture.Create(); skin.sharedMesh = original;
            skin.SetBlendShapeWeight(0, 25);
            var menu = Menu();
            var clone = Object.Instantiate(source);
            var owned = new List<Mesh>();
            try
            {
                var bindings = new PreparedExpressionBindings(clone, menu);
                var prepared = clone.GetComponentInChildren<SkinnedMeshRenderer>();
                prepared.name = "Renamed by authoring";
                prepared.sharedMesh = Object.Instantiate(original); owned.Add(prepared.sharedMesh);
                prepared.SetBlendShapeWeight(0, restChanges ? 75 : 0);
                prepared.SetBlendShapeWeight(1, restChanges ? 50 : 0);
                bindings.Capture(menu);
                AvatarBaseShape.Preserve(clone, clone, owned, null);
                var expressions = VrChatExpressionBaker.Bake(null, clone, menu, owned, null, bindings);
                Assert.That(prepared.sharedMesh.vertices[0], Is.EqualTo(new Vector3(restChanges ? 4 : 1, restChanges ? 1 : 0, 0)));
                var delta = new Vector3[3];
                prepared.sharedMesh.GetBlendShapeFrameVertices(prepared.sharedMesh.GetBlendShapeIndex(expressions[0].Targets[0]), 0, delta, null, null);
                Assert.That(prepared.sharedMesh.vertices[0] + delta[0], Is.EqualTo(new Vector3(restChanges ? 4 : 1, 2, 0)), "Blink keeps prepared face size and reaches one full blink.");
                Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(25));
                Assert.That(original.vertices[0], Is.EqualTo(new Vector3(1, 0, 0)));
            }
            finally { Object.DestroyImmediate(clone); Object.DestroyImmediate(source); foreach (var mesh in owned) Object.DestroyImmediate(mesh); Object.DestroyImmediate(original); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InstalledMaResolvesShapeMaterialAndVisibilityBeforeBaseShape(bool vrchat)
        {
            var rootType = TypeNamed(vrchat ? "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor" : "nadena.dev.ndmf.runtime.components.NDMFAvatarRoot");
            if (rootType == null || TypeNamed("nadena.dev.modular_avatar.core.ModularAvatarShapeChanger") == null) Assert.Ignore("Install MA/NDMF and VRChat SDK.");
            var source = new GameObject("Avatar"); source.AddComponent(rootType);
            var face = new GameObject("Face"); face.transform.SetParent(source.transform, false);
            var skin = face.AddComponent<SkinnedMeshRenderer>();
            var original = BaseShapeFixture.Create(); skin.sharedMesh = original;
            skin.SetBlendShapeWeight(0, 25);
            var material = new Material(Shader.Find("Hidden/VRVlogTests/lilToon"));
            var replacement = new Material(material); replacement.SetColor("_Color", Color.red);
            skin.sharedMaterial = material;
            var hidden = new GameObject("Hidden outfit"); hidden.transform.SetParent(source.transform, false);
            AddRule(source, "ModularAvatarShapeChanger", "Shapes", "ChangedShape", face,
                ("ShapeName", "Face size"), ("ChangeType", 1), ("Value", 75f));
            AddRule(source, "ModularAvatarMaterialSetter", "Objects", "MaterialSwitchObject", face,
                ("Material", replacement), ("MaterialIndex", 0));
            AddRule(source, "ModularAvatarObjectToggle", "Objects", "ToggledObject", hidden, ("Active", false));
            var clone = Object.Instantiate(source);
            var owned = new List<Mesh>();
            try
            {
                MaAppearanceSnapshot.Apply(source, clone, owned);
                using (NdmfExportPreparation.Prepare(source, clone))
                {
                    var prepared = clone.GetComponentInChildren<SkinnedMeshRenderer>();
                    Assert.That(prepared.GetBlendShapeWeight(0), Is.EqualTo(75));
                    Assert.That(prepared.sharedMaterial.GetColor("_Color"), Is.EqualTo(Color.red));
                    Assert.That(clone.transform.Find("Hidden outfit").gameObject.activeSelf, Is.False);
                    AvatarBaseShape.Preserve(clone, clone, owned, null);
                    Assert.That(prepared.sharedMesh.vertices[0].x, Is.EqualTo(4));
                    Assert.That(prepared.GetBlendShapeWeight(0), Is.Zero);
                    Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(25));
                    Assert.That(skin.sharedMaterial, Is.SameAs(material));
                    Assert.That(hidden.activeSelf, Is.True);
                }
            }
            finally { Object.DestroyImmediate(clone); Object.DestroyImmediate(source); foreach (var mesh in owned) Object.DestroyImmediate(mesh); Object.DestroyImmediate(original); Object.DestroyImmediate(material); Object.DestroyImmediate(replacement); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InstalledMeshCutterPreservesMorphsAndSourceMesh(bool combineShape)
        {
            var cutterType = TypeNamed("nadena.dev.modular_avatar.core.ModularAvatarMeshCutter");
            if (cutterType == null) Assert.Ignore("Install MA/NDMF.");
            var source = new GameObject("Avatar");
            source.AddComponent(TypeNamed("nadena.dev.ndmf.runtime.components.NDMFAvatarRoot"));
            var skin = source.AddComponent<SkinnedMeshRenderer>();
            var original = BaseShapeFixture.Create(); skin.sharedMesh = original;
            var cutterObject = new GameObject("Cutter"); cutterObject.transform.SetParent(source.transform, false);
            var cutter = cutterObject.AddComponent(cutterType);
            var reference = cutterType.GetProperty("Object").GetValue(cutter);
            reference.GetType().GetMethod("Set", new[] { typeof(GameObject) }).Invoke(reference, new object[] { source });
            var filter = cutterObject.AddComponent(TypeNamed("nadena.dev.modular_avatar.core.vertex_filters.VertexFilterByAxisComponent"));
            filter.GetType().GetProperty("Center").SetValue(filter, new Vector3(-10, 0, 0));
            filter.GetType().GetProperty("Axis").SetValue(filter, Vector3.right);
            if (combineShape) AddRule(source, "ModularAvatarShapeChanger", "Shapes", "ChangedShape", source,
                ("ShapeName", "Face size"), ("ChangeType", 1), ("Value", 75f));
            var clone = Object.Instantiate(source); var owned = new List<Mesh>();
            try
            {
                MaAppearanceSnapshot.Apply(source, clone, owned);
                var prepared = clone.GetComponent<SkinnedMeshRenderer>();
                var vertices = prepared.sharedMesh.vertices; var triangles = prepared.sharedMesh.triangles;
                for (var i = 0; i < triangles.Length; i += 3)
                    Assert.That(Vector3.Cross(vertices[triangles[i+1]]-vertices[triangles[i]], vertices[triangles[i+2]]-vertices[triangles[i]]).sqrMagnitude,
                        Is.Zero, "MA may retain a degenerate triangle, which must have no visible area.");
                Assert.That(prepared.sharedMesh.blendShapeCount, Is.EqualTo(2), "Cutter must preserve expression correspondence.");
                Assert.That(prepared.GetBlendShapeWeight(0), Is.EqualTo(combineShape ? 75 : 0));
                Assert.That(original.triangles.Length, Is.EqualTo(3));
                Assert.That(skin.sharedMesh, Is.SameAs(original));
                Assert.That(cutter, Is.Not.Null);
            }
            finally { Object.DestroyImmediate(clone); Object.DestroyImmediate(source); foreach (var mesh in owned) Object.DestroyImmediate(mesh); Object.DestroyImmediate(original); }
        }

        static VrChatExpressionMenu.Source Menu()
        {
            var menu = new VrChatExpressionMenu.Source();
            var entry = new VrChatExpressionMenu.Entry { Name = "Blink" };
            entry.Values.Add(new VrChatExpressionMenu.MorphValue { Path = "Face", Shape = "Blink", Weight = 100 });
            menu.Entries.Add(entry);
            return menu;
        }
        static Type TypeNamed(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);
        static void AddRule(GameObject root, string componentName, string listName, string itemName, GameObject target, params (string name, object value)[] values)
        {
            const string prefix = "nadena.dev.modular_avatar.core.";
            var node = new GameObject(componentName); node.transform.SetParent(root.transform, false);
            var component = node.AddComponent(TypeNamed(prefix + componentName));
            var itemType = TypeNamed(prefix + itemName);
            var item = Activator.CreateInstance(itemType);
            var referenceType = TypeNamed(prefix + "AvatarObjectReference");
            var reference = Activator.CreateInstance(referenceType);
            referenceType.GetMethod("Set", new[] { typeof(GameObject) }).Invoke(reference, new object[] { target });
            itemType.GetField("Object").SetValue(item, reference);
            foreach (var value in values)
            {
                var field = itemType.GetField(value.name);
                field.SetValue(item, field.FieldType.IsEnum ? Enum.ToObject(field.FieldType, value.value) : value.value);
            }
            ((IList)component.GetType().GetProperty(listName).GetValue(component)).Add(item);
        }
    }
}
