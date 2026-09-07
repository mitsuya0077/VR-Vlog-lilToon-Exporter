using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class ExcludedAnimationTests
    {
        [Test]
        public void ExcludedPetMovementAndObjectSwapDoNotDiscardFacePose()
        {
            var avatar = new GameObject("avatar");
            var clip = new AnimationClip();
            var mesh = ShapeMesh();
            try
            {
                var face = Child(avatar, "Face").AddComponent<SkinnedMeshRenderer>();
                face.sharedMesh = mesh;
                var pet = Child(avatar, "Pet");
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Smile"), AnimationCurve.Constant(0, 1, 60));
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Pet", typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Linear(0, 0, 1, 100));
                AnimationUtility.SetObjectReferenceCurve(clip, new EditorCurveBinding { path = "Pet", type = typeof(MeshFilter), propertyName = "m_Mesh" },
                    new[] { new ObjectReferenceKeyframe { time = 0, value = mesh } });
                Assert.Throws<InvalidOperationException>(() => VrChatGestureExpressions.ReadPose(avatar, clip));
                using var exclusions = new ExportObjectExclusions(avatar, new[] { pet });
                var pose = VrChatGestureExpressions.ReadPose(avatar, clip, exclusions.ContainsPath);
                Assert.That(pose.Count, Is.EqualTo(1));
                Assert.That(pose[0].Path, Is.EqualTo("Face"));
                Assert.That(pose[0].Weight, Is.EqualTo(60));
                Assert.That(mesh.blendShapeCount, Is.EqualTo(1));
                Assert.That(pet.transform.localPosition, Is.EqualTo(Vector3.zero));
            }
            finally { Object.DestroyImmediate(avatar); Object.DestroyImmediate(clip); Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void ExcludedUnreadablePetMeshIsNotRebased()
        {
            var avatar = new GameObject("avatar");
            var mesh = ShapeMesh();
            var faceMesh = ShapeMesh();
            GameObject clone = null;
            var temporary = new List<Mesh>();
            try
            {
                var face = Child(avatar, "Face").AddComponent<SkinnedMeshRenderer>();
                face.sharedMesh = faceMesh;
                face.SetBlendShapeWeight(0, 50);
                var pet = Child(avatar, "Pet").AddComponent<SkinnedMeshRenderer>();
                pet.sharedMesh = mesh;
                pet.SetBlendShapeWeight(0, 50);
                mesh.UploadMeshData(true);
                clone = Object.Instantiate(avatar);
                using var exclusions = new ExportObjectExclusions(avatar, new[] { pet.gameObject });
                AvatarBaseShape.Preserve(avatar, clone, temporary, null, exclusions.Contains);
                Assert.That(temporary.Count, Is.EqualTo(1));
                Assert.That(clone.transform.Find("Face").GetComponent<SkinnedMeshRenderer>().sharedMesh.vertices[0].y, Is.EqualTo(.5f));
                Assert.That(faceMesh.vertices[0], Is.EqualTo(Vector3.zero));
                Assert.That(pet.sharedMesh, Is.SameAs(mesh));
            }
            finally
            {
                if (clone != null) Object.DestroyImmediate(clone);
                foreach (var item in temporary) Object.DestroyImmediate(item);
                Object.DestroyImmediate(avatar); Object.DestroyImmediate(mesh); Object.DestroyImmediate(faceMesh);
            }
        }

        private static GameObject Child(GameObject parent, string name)
        {
            var child = new GameObject(name);
            child.transform.SetParent(parent.transform, false);
            return child;
        }

        private static Mesh ShapeMesh()
        {
            var mesh = new Mesh { vertices = new[] { Vector3.zero } };
            mesh.AddBlendShapeFrame("Smile", 100, new[] { Vector3.up }, new Vector3[1], new Vector3[1]);
            return mesh;
        }
    }
}
