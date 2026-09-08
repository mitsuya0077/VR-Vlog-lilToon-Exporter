using System;
using System.Collections.Generic;
using NUnit.Framework;
using UniVRM10;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class ExportObjectExclusionTests
    {
        [Test]
        public void DuplicateNamesAndNestedExclusionsRemoveOnlySelectedCloneObjects()
        {
            var source = new GameObject("avatar");
            GameObject clone = null;
            try
            {
                var kept = Child(source, "same name");
                var pet = Child(source, "same name");
                var nested = Child(pet, "helper");
                var other = Child(source, "other");
                var exclusions = new ExportObjectExclusions(source, new[] { pet, nested, other });
                clone = Object.Instantiate(source);
                exclusions.Apply(clone, new List<string>());
                Assert.That(clone.transform.childCount, Is.EqualTo(1));
                Assert.That(clone.transform.GetChild(0).name, Is.EqualTo(kept.name));
                Assert.That(source.transform.childCount, Is.EqualTo(3));
                Assert.That(pet.transform.childCount, Is.EqualTo(1));
                Assert.That(pet.activeSelf, Is.True);
                Assert.Throws<ArgumentException>(() => exclusions.Apply(source, null));
            }
            finally { if (clone != null) Object.DestroyImmediate(clone); Object.DestroyImmediate(source); }
        }

        [Test]
        public void ExcludingBonesStillUsedByIncludedClothingIsRejected()
        {
            var source = new GameObject("avatar");
            try
            {
                var bodyBone = Child(source, "bone");
                var clothing = Child(source, "clothing").AddComponent<SkinnedMeshRenderer>();
                clothing.bones = new[] { bodyBone.transform };
                Assert.Throws<InvalidOperationException>(() => new ExportObjectExclusions(source, new[] { bodyBone }));
                Assert.DoesNotThrow(() => new ExportObjectExclusions(source, new[] { clothing.gameObject, bodyBone }));
                Assert.Throws<InvalidOperationException>(() => new ExportObjectExclusions(source, new[] { source }));
            }
            finally { Object.DestroyImmediate(source); }
        }

        [Test]
        public void AmbiguousAnimationPathIsExcludedOnlyWhenEveryMatchingObjectIsExcluded()
        {
            var source = new GameObject("avatar");
            try
            {
                var first = Child(source, "Same");
                var second = Child(source, "Same");
                using (var partial = new ExportObjectExclusions(source, new[] { second }))
                {
                    Assert.That(partial.ContainsPath("Same"), Is.False);
                    Assert.That(partial.ContainsPath("Same"), Is.False, "Cached lookups must retain ambiguous included objects.");
                    Assert.That(partial.ContainsPath("Missing"), Is.False);
                    Assert.That(partial.ContainsPath(null), Is.False);
                }
                using (var all = new ExportObjectExclusions(source, new[] { first, second }))
                    Assert.That(all.ContainsPath("Same"), Is.True);
                using (var none = new ExportObjectExclusions(source, null))
                    Assert.That(none.ContainsPath("Same"), Is.False);
            }
            finally { Object.DestroyImmediate(source); }
        }

        [Test]
        public void MixedExpressionRetainsAvatarFaceAndDropsExcludedPetMorph()
        {
            var source = new GameObject("avatar");
            var mesh = new Mesh { vertices = new[] { Vector3.zero } };
            mesh.AddBlendShapeFrame("Smile", 100, new[] { Vector3.up }, new Vector3[1], new Vector3[1]);
            try
            {
                Child(source, "Body").AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
                var pet = Child(source, "Pet");
                pet.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
                var entry = new VrChatExpressionMenu.Entry { Id = "mixed", Name = "smile" };
                entry.Values.Add(new VrChatExpressionMenu.MorphValue { Path = "Body", Shape = "Smile", Weight = 80 });
                entry.Values.Add(new VrChatExpressionMenu.MorphValue { Path = "Pet", Shape = "Smile", Weight = 30 });
                var petOnly = new VrChatExpressionMenu.Entry { Id = "pet", Name = "pet only" };
                petOnly.Values.Add(new VrChatExpressionMenu.MorphValue { Path = "Pet", Shape = "Smile", Weight = 50 });
                var menu = new VrChatExpressionMenu.Source();
                menu.Entries.Add(entry);
                menu.Entries.Add(petOnly);
                new ExportObjectExclusions(source, new[] { pet }).FilterExpressions(menu, new List<string>());
                Assert.That(entry.Values.Count, Is.EqualTo(1));
                Assert.That(entry.Values[0].Path, Is.EqualTo("Body"));
                Assert.That(entry.Error, Is.Null);
                Assert.That(petOnly.Error, Is.Not.Null);
                Assert.That(pet.activeSelf, Is.True);
            }
            finally { Object.DestroyImmediate(source); Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void ExcludedPetPrunesCloneSpringReferencesAndPreservesSource()
        {
            var source = new GameObject("avatar");
            GameObject clone = null;
            try
            {
                var instance = source.AddComponent<Vrm10Instance>();
                var pet = Child(source, "Pet");
                var petJoint = pet.AddComponent<VRM10SpringBoneJoint>();
                var petCollider = pet.AddComponent<VRM10SpringBoneCollider>();
                var petGroup = pet.AddComponent<VRM10SpringBoneColliderGroup>();
                petGroup.Colliders.Add(petCollider);
                var hair = Child(source, "Hair");
                var hairJoint = hair.AddComponent<VRM10SpringBoneJoint>();
                var hairCollider = hair.AddComponent<VRM10SpringBoneCollider>();
                var hairGroup = hair.AddComponent<VRM10SpringBoneColliderGroup>();
                hairGroup.Colliders.AddRange(new[] { hairCollider, petCollider });
                instance.SpringBone.ColliderGroups.AddRange(new[] { hairGroup, petGroup });
                var petSpring = new Vrm10InstanceSpringBone.Spring("pet");
                petSpring.Joints.Add(petJoint);
                petSpring.ColliderGroups.Add(petGroup);
                var hairSpring = new Vrm10InstanceSpringBone.Spring("hair") { Center = pet.transform };
                hairSpring.Joints.Add(hairJoint);
                hairSpring.ColliderGroups.AddRange(new[] { hairGroup, petGroup });
                instance.SpringBone.Springs.AddRange(new[] { petSpring, hairSpring });
                instance.LookAtTarget = pet.transform;
                using (var exclusions = new ExportObjectExclusions(source, new[] { pet }))
                {
                    clone = Object.Instantiate(source);
                    exclusions.Apply(clone, new List<string>());
                    var copy = clone.GetComponent<Vrm10Instance>();
                    Assert.That(copy.SpringBone.Springs.Count, Is.EqualTo(1));
                    Assert.That(copy.SpringBone.Springs[0].Name, Is.EqualTo("hair"));
                    Assert.That(copy.SpringBone.Springs[0].Joints[0], Is.Not.Null);
                    Assert.That(copy.SpringBone.Springs[0].Center, Is.Null);
                    Assert.That(copy.LookAtTarget, Is.Null);
                    Assert.That(copy.SpringBone.ColliderGroups.Count, Is.EqualTo(1));
                    Assert.That(copy.SpringBone.ColliderGroups[0].Colliders.Count, Is.EqualTo(1));
                    Assert.That(copy.SpringBone.Springs[0].ColliderGroups.Count, Is.EqualTo(1));
                    Assert.That(instance.SpringBone.Springs.Count, Is.EqualTo(2));
                    Assert.That(hairSpring.Center, Is.SameAs(pet.transform));
                    Assert.That(hairGroup.Colliders.Count, Is.EqualTo(2));
                    Assert.That(petSpring.Joints[0], Is.SameAs(petJoint));
                }
            }
            finally { if (clone != null) Object.DestroyImmediate(clone); Object.DestroyImmediate(source); }
        }

        [Test]
        public void ConstraintReferencingExcludedHelperIsRemovedOnlyFromCloneAndKeepsPose()
        {
            var source = new GameObject("avatar");
            GameObject clone = null;
            try
            {
                source.AddComponent<Vrm10Instance>();
                var helper = Child(source, "Helper");
                var target = Child(source, "Accessory");
                target.transform.localRotation = Quaternion.Euler(15, 25, 35);
                var constraint = target.AddComponent<Vrm10RotationConstraint>();
                constraint.Source = helper.transform;
                using (var exclusions = new ExportObjectExclusions(source, new[] { helper }))
                {
                    clone = Object.Instantiate(source);
                    exclusions.Apply(clone, new List<string>());
                    var copy = clone.transform.Find("Accessory");
                    Assert.That(copy.GetComponent<Vrm10RotationConstraint>(), Is.Null);
                    Assert.That(Quaternion.Angle(copy.localRotation, target.transform.localRotation), Is.LessThan(0.001f));
                    Assert.That(constraint.Source, Is.SameAs(helper.transform));
                    Assert.That(target.GetComponent<Vrm10RotationConstraint>(), Is.SameAs(constraint));
                }
            }
            finally { if (clone != null) Object.DestroyImmediate(clone); Object.DestroyImmediate(source); }
        }

        [Test]
        public void FirstPersonAndSharedPresetBindingsUseTemporarySettingsCopies()
        {
            var source = new GameObject("avatar");
            var settings = ScriptableObject.CreateInstance<VRM10Object>();
            var expression = ScriptableObject.CreateInstance<VRM10Expression>();
            var sharedMaterial = new Material(Shader.Find("Hidden/VRVlogTests/lilToon")) { name = "Shared material" };
            var petMaterial = new Material(sharedMaterial) { name = "Pet material" };
            GameObject clone = null;
            ExportObjectExclusions exclusions = null;
            try
            {
                var instance = source.AddComponent<Vrm10Instance>();
                instance.Vrm = settings;
                Child(source, "Body").AddComponent<MeshRenderer>().sharedMaterial = sharedMaterial;
                var pet = Child(source, "Pet");
                pet.AddComponent<MeshRenderer>().sharedMaterials = new[] { sharedMaterial, petMaterial };
                settings.FirstPerson.Renderers.Add(new RendererFirstPersonFlags { Renderer = "Body" });
                settings.FirstPerson.Renderers.Add(new RendererFirstPersonFlags { Renderer = "Pet" });
                expression.name = "Shared face";
                expression.MorphTargetBindings = new[] { new MorphTargetBinding("Body", 0, 1), new MorphTargetBinding("Pet", 0, 1) };
                expression.MaterialUVBindings = new[]
                {
                    new MaterialUVBinding { MaterialName = sharedMaterial.name, Scaling = Vector2.one },
                    new MaterialUVBinding { MaterialName = petMaterial.name, Scaling = Vector2.one }
                };
                settings.Expression.Happy = expression;
                settings.Expression.Angry = expression;
                settings.Expression.CustomClips.Add(expression);
                var settingsDirty = EditorUtility.IsDirty(settings);
                var expressionDirty = EditorUtility.IsDirty(expression);
                exclusions = new ExportObjectExclusions(source, new[] { pet });
                clone = Object.Instantiate(source);
                exclusions.Apply(clone, new List<string>());
                var copy = clone.GetComponent<Vrm10Instance>().Vrm;
                Assert.That(copy, Is.Not.SameAs(settings));
                Assert.That(copy.FirstPerson.Renderers.Count, Is.EqualTo(1));
                Assert.That(copy.FirstPerson.Renderers[0].Renderer, Is.EqualTo("Body"));
                var copiedExpression = copy.Expression.Happy;
                Assert.That(copiedExpression, Is.Not.SameAs(expression));
                Assert.That(copy.Expression.Angry, Is.SameAs(copiedExpression));
                Assert.That(copy.Expression.CustomClips[0], Is.SameAs(copiedExpression));
                Assert.That(copiedExpression.MorphTargetBindings.Length, Is.EqualTo(1));
                Assert.That(copiedExpression.MaterialUVBindings.Length, Is.EqualTo(1));
                Assert.That(copiedExpression.MaterialUVBindings[0].MaterialName, Is.EqualTo(sharedMaterial.name));
                Assert.That(settings.FirstPerson.Renderers.Count, Is.EqualTo(2));
                Assert.That(settings.Expression.Happy, Is.SameAs(expression));
                Assert.That(expression.MorphTargetBindings.Length, Is.EqualTo(2));
                Assert.That(expression.MaterialUVBindings.Length, Is.EqualTo(2));
                Assert.That(EditorUtility.IsDirty(settings), Is.EqualTo(settingsDirty));
                Assert.That(EditorUtility.IsDirty(expression), Is.EqualTo(expressionDirty));
                exclusions.Dispose();
                Assert.That(copy == null, Is.True, "Temporary settings must be disposed after serialization, including failed exports.");
                Assert.That(copiedExpression == null, Is.True);
                Assert.That(settings != null && expression != null, Is.True);
            }
            finally
            {
                if (clone != null) Object.DestroyImmediate(clone);
                exclusions?.Dispose();
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(settings);
                Object.DestroyImmediate(expression);
                Object.DestroyImmediate(sharedMaterial);
                Object.DestroyImmediate(petMaterial);
            }
        }

        [Test]
        public void ExternalColliderGroupIsRejectedWithoutEditingExternalSceneObject()
        {
            var source = new GameObject("avatar");
            var external = new GameObject("External group");
            GameObject clone = null;
            try
            {
                var instance = source.AddComponent<Vrm10Instance>();
                var group = external.AddComponent<VRM10SpringBoneColliderGroup>();
                var collider = external.AddComponent<VRM10SpringBoneCollider>();
                group.Colliders.Add(collider);
                instance.SpringBone.ColliderGroups.Add(group);
                var pet = Child(source, "Pet");
                using (var exclusions = new ExportObjectExclusions(source, new[] { pet }))
                {
                    clone = Object.Instantiate(source);
                    var error = Assert.Throws<InvalidOperationException>(() => exclusions.Apply(clone, new List<string>()));
                    Assert.That(error.Message, Does.Contain("アバター外"));
                    Assert.That(error.Message, Does.Contain("External group"));
                    Assert.That(group.Colliders.Count, Is.EqualTo(1));
                    Assert.That(group.Colliders[0], Is.SameAs(collider));
                    Assert.That(clone.transform.Find("Pet"), Is.Not.Null, "Reject before deleting the selected objects.");
                    Assert.That(pet.activeSelf, Is.True);
                }
            }
            finally
            {
                if (clone != null) Object.DestroyImmediate(clone);
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(external);
            }
        }

        private static GameObject Child(GameObject parent, string name)
        {
            var child = new GameObject(name);
            child.transform.SetParent(parent.transform);
            return child;
        }
    }
}
