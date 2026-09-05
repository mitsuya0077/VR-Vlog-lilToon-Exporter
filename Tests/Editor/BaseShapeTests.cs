using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class BaseShapeTests
    {
        [Test] public void GeometryAndExpressionEndpoints() => BaseShapeFixture.Run((ok, message) => Assert.IsTrue(ok, message));

        [Test] public void PreserveSharedMeshesPerRendererAndLeaveSourceUntouched()
        {
            var source = new GameObject("Avatar");
            GameObject clone = null;
            var mesh = BaseShapeFixture.Create();
            var temporary = new List<Mesh>();
            try
            {
                var a = new GameObject("Duplicate name");
                var b = new GameObject("Duplicate name");
                a.transform.SetParent(source.transform, false);
                b.transform.SetParent(source.transform, false);
                var first = a.AddComponent<SkinnedMeshRenderer>();
                var second = b.AddComponent<SkinnedMeshRenderer>();
                first.sharedMesh = mesh; second.sharedMesh = mesh;
                first.SetBlendShapeWeight(0, 25); second.SetBlendShapeWeight(0, 75);
                clone = Object.Instantiate(source);
                AvatarBaseShape.Preserve(source, clone, temporary, null);
                var copies = clone.GetComponentsInChildren<SkinnedMeshRenderer>();
                Assert.AreEqual(2f, copies[0].sharedMesh.vertices[0].x);
                Assert.AreEqual(4f, copies[1].sharedMesh.vertices[0].x);
                Assert.AreEqual(0f, copies[0].GetBlendShapeWeight(0));
                Assert.AreEqual(25f, first.GetBlendShapeWeight(0));
                Assert.AreSame(mesh, first.sharedMesh);
                Assert.AreEqual(1f, mesh.vertices[0].x);
                Assert.AreNotSame(copies[0].sharedMesh, copies[1].sharedMesh);
            }
            finally
            {
                Object.DestroyImmediate(clone); Object.DestroyImmediate(source);
                foreach (var item in temporary) Object.DestroyImmediate(item);
                Object.DestroyImmediate(mesh);
            }
        }
    }
}
