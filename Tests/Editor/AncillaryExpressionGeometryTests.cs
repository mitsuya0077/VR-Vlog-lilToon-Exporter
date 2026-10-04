using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class AncillaryExpressionGeometryTests
    {
        const string AccessoryPath = "AccessoryRoot/Accessory";

        [TestCase("renderer", false)]
        [TestCase("bone", false)]
        [TestCase("ancestor", false)]
        [TestCase("renderer", true)]
        public void IdenticalNonzeroRestEndpointKeepsUnrelatedRendererSupportOutOfFacialGeometry(string support, bool animated)
        {
            using var f = new Fixture();
            var rest = f.NativeVertices(AccessoryPath, 35);
            var endpoint = f.NativeVertices(AccessoryPath, 35);
            Assert.That((rest[0] - f.AccessoryMesh.vertices[0]).magnitude, Is.GreaterThan(.1f),
                "The nonzero authored rest is real native mesh deformation, not an inert zero channel.");
            Assert.That(endpoint, Is.EqualTo(rest));
            var faceRest = f.NativeVertices("Face", 20);
            var faceEndpoint = f.NativeVertices("Face", 80);
            Assert.That((faceEndpoint[0] - faceRest[0]).magnitude, Is.GreaterThan(.1f));
            var source = f.SourceSnapshot();
            var entry = f.Entry(35);
            if (animated)
            {
                entry.Animation.Add(new VrChatExpressionMenu.AnimatedMorph { Path = AccessoryPath, Shape = "Authored shape",
                    Curve = VrChatGestureExpressions.ReadCurve(AnimationCurve.Constant(0, 1, 35)) });
                entry.Duration = 1;
            }
            var menu = f.Menu(entry, support);
            using (var prepared = new PreparedExpressionBindings(f.Copy, menu, f.Source))
            {
                prepared.Capture(menu);
                Assert.That(prepared.Get(AccessoryPath).Weights.Single(), Is.EqualTo(35));
                Assert.That(entry.Values.Single(value => value.Path == AccessoryPath).Weight, Is.EqualTo(35));
                Assert.That(entry.Error, Is.Null);
                Assert.That(entry.AncillaryGeometry.IsValidatedFor(entry), Is.True);
                AvatarBaseShape.Preserve(f.Source, f.Copy, f.Generated, new List<string>());
                var baked = VrChatExpressionBaker.Bake(f.Source, f.Copy, menu, f.Generated, new List<string>(), prepared);
                Assert.That(baked, Has.Count.EqualTo(1));
                var face = f.Renderer(f.Copy, "Face"); var accessory = f.Renderer(f.Copy, AccessoryPath);
                var faceTarget = baked.Single().Targets.Single(target => face.sharedMesh.GetBlendShapeIndex(target) >= 0);
                var accessoryTarget = baked.Single().Targets.Single(target => accessory.sharedMesh.GetBlendShapeIndex(target) >= 0);
                Assert.That(f.TargetVertices(face.sharedMesh, faceTarget).Any(value => value.magnitude > .1f), Is.True);
                Assert.That(f.TargetVertices(accessory.sharedMesh, accessoryTarget), Is.All.EqualTo(Vector3.zero),
                    "The emitted accessory target is zero against its actual nonzero final rest.");
                Assert.That(entry.Messages.Any(message => message.Contains("表示・Transform")), Is.True);
            }
            f.AssertSourceUnchanged(source);
        }

        [TestCase("renderer")]
        [TestCase("bone")]
        [TestCase("ancestor")]
        public void ChangedFinalResidualStillProtectsRendererVisibilityAndItsAncestry(string support)
        {
            using var f = new Fixture();
            var rest = f.NativeVertices(AccessoryPath, 35);
            var endpoint = f.NativeVertices(AccessoryPath, 75);
            Assert.That((endpoint[0] - rest[0]).magnitude, Is.GreaterThan(.1f));
            var source = f.SourceSnapshot();
            var entry = f.Entry(75); var menu = f.Menu(entry, support);
            using (var prepared = new PreparedExpressionBindings(f.Copy, menu, f.Source))
            {
                prepared.Capture(menu);
                Assert.That(entry.Error, Does.Contain("BlendShape以外").And.Contain("Accessory"));
                Assert.That(entry.AncillaryGeometry.IsValidatedFor(entry), Is.False);
                Assert.That(VrChatExpressionBaker.Bake(f.Source, f.Copy, menu, f.Generated, new List<string>(), prepared), Is.Empty);
                Assert.That(f.Generated, Is.Empty);
            }
            f.AssertSourceUnchanged(source);
        }

        [Test]
        public void CancelledAnimationEndpointsStillProtectNonzeroIntermediateEmittedGeometry()
        {
            using var f = new Fixture();
            f.AccessoryMesh.ClearBlendShapes();
            f.AccessoryMesh.AddBlendShapeFrame("Authored shape", 50, new[] { new Vector3(.5f, 0, 0), Vector3.zero, Vector3.zero }, null, null);
            f.AccessoryMesh.AddBlendShapeFrame("Authored shape", 100, new Vector3[3], null, null);
            f.Renderer(f.Source, AccessoryPath).SetBlendShapeWeight(0, 0);
            f.Renderer(f.Copy, AccessoryPath).SetBlendShapeWeight(0, 0);
            var first = f.NativeVertices(AccessoryPath, 0); var last = f.NativeVertices(AccessoryPath, 100);
            var middle = f.NativeVertices(AccessoryPath, 50);
            Assert.That(last, Is.EqualTo(first));
            Assert.That((middle[0] - first[0]).magnitude, Is.GreaterThan(.1f),
                "An actual intermediate blendshape frame moves even when both endpoint geometries cancel.");
            var source = f.SourceSnapshot();
            var entry = f.Entry(0);
            entry.Animation.Add(new VrChatExpressionMenu.AnimatedMorph { Path = AccessoryPath, Shape = "Authored shape",
                Curve = VrChatGestureExpressions.ReadCurve(AnimationCurve.Linear(0, 0, 1, 100)) });
            entry.Duration = 1;
            var menu = f.Menu(entry, "renderer");
            using (var prepared = new PreparedExpressionBindings(f.Copy, menu, f.Source))
            {
                prepared.Capture(menu);
                Assert.That(entry.Error, Does.Contain("BlendShape以外").And.Contain("Accessory"));
                Assert.That(entry.AncillaryGeometry.IsValidatedFor(entry), Is.False);
                Assert.That(VrChatExpressionBaker.Bake(f.Source, f.Copy, menu, f.Generated, new List<string>(), prepared), Is.Empty);
                Assert.That(f.Generated, Is.Empty);
            }
            f.AssertSourceUnchanged(source);
        }

        sealed class Fixture : IDisposable
        {
            internal readonly GameObject Source, Copy;
            internal readonly Mesh FaceMesh, AccessoryMesh;
            internal readonly List<Mesh> Generated = new List<Mesh>();

            internal Fixture()
            {
                Source = new GameObject("Avatar");
                FaceMesh = Skin(Source.transform, "Face", "Smile", 20);
                var group = new GameObject("AccessoryRoot"); group.transform.SetParent(Source.transform, false);
                AccessoryMesh = Skin(group.transform, "Accessory", "Authored shape", 35);
                Copy = Object.Instantiate(Source);
            }

            static Mesh Skin(Transform parent, string name, string shape, float rest)
            {
                var child = new GameObject(name, typeof(SkinnedMeshRenderer)); child.transform.SetParent(parent, false);
                var bone = new GameObject(name + "Bone"); bone.transform.SetParent(parent, false);
                var renderer = child.GetComponent<SkinnedMeshRenderer>(); renderer.bones = new[] { bone.transform }; renderer.rootBone = bone.transform;
                var mesh = new Mesh { name = name + " mesh", vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                    normals = Enumerable.Repeat(Vector3.forward, 3).ToArray(), triangles = new[] { 0, 1, 2 },
                    bindposes = new[] { bone.transform.worldToLocalMatrix * child.transform.localToWorldMatrix },
                    boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 3).ToArray() };
                mesh.AddBlendShapeFrame(shape, 100, new[] { new Vector3(.5f, 0, 0), Vector3.zero, Vector3.zero }, null, null);
                renderer.sharedMesh = mesh; renderer.SetBlendShapeWeight(0, rest); return mesh;
            }

            internal SkinnedMeshRenderer Renderer(GameObject root, string path) => root.transform.Find(path).GetComponent<SkinnedMeshRenderer>();

            internal Vector3[] NativeVertices(string path, float weight)
            {
                var copy = Object.Instantiate(Source); var baked = new Mesh();
                try { var renderer = Renderer(copy, path); renderer.SetBlendShapeWeight(0, weight); renderer.BakeMesh(baked); return baked.vertices; }
                finally { Object.DestroyImmediate(copy); Object.DestroyImmediate(baked); }
            }

            internal VrChatExpressionMenu.Entry Entry(float accessory) => new VrChatExpressionMenu.Entry { Name = "Facial expression",
                Values = { new VrChatExpressionMenu.MorphValue { Path = "Face", Shape = "Smile", Weight = 80 },
                    new VrChatExpressionMenu.MorphValue { Path = AccessoryPath, Shape = "Authored shape", Weight = accessory } } };

            internal VrChatExpressionMenu.Source Menu(VrChatExpressionMenu.Entry entry, string support)
            {
                entry.AncillaryGeometry = new AncillaryExpressionGeometry(Copy);
                entry.AncillaryGeometry.CaptureRenderers(new[] { EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Smile"),
                    EditorCurveBinding.FloatCurve(AccessoryPath, typeof(SkinnedMeshRenderer), "blendShape.Authored shape") });
                var binding = support == "bone"
                    ? EditorCurveBinding.FloatCurve("AccessoryRoot/AccessoryBone", typeof(Transform), "m_LocalPosition.x")
                    : EditorCurveBinding.FloatCurve(support == "ancestor" ? "AccessoryRoot" : AccessoryPath, typeof(GameObject), "m_IsActive");
                entry.AncillaryGeometry.Record("Implicit support", "Authored support", binding);
                var menu = new VrChatExpressionMenu.Source(); menu.Entries.Add(entry); return menu;
            }

            internal Vector3[] TargetVertices(Mesh mesh, string target)
            {
                var values = new Vector3[mesh.vertexCount]; mesh.GetBlendShapeFrameVertices(mesh.GetBlendShapeIndex(target), 0, values, null, null); return values;
            }

            internal Dictionary<Object, string> SourceSnapshot() => Source.GetComponentsInChildren<Component>(true).Cast<Object>()
                .Concat(new Object[] { FaceMesh, AccessoryMesh }).ToDictionary(value => value, value => EditorJsonUtility.ToJson(value));

            internal void AssertSourceUnchanged(Dictionary<Object, string> snapshot)
            {
                foreach (var value in snapshot) Assert.That(EditorJsonUtility.ToJson(value.Key), Is.EqualTo(value.Value));
                Assert.That(Renderer(Source, "Face").sharedMesh, Is.SameAs(FaceMesh));
                Assert.That(Renderer(Source, AccessoryPath).sharedMesh, Is.SameAs(AccessoryMesh));
            }

            public void Dispose()
            {
                Object.DestroyImmediate(Copy); Object.DestroyImmediate(Source);
                foreach (var value in Generated.Distinct()) Object.DestroyImmediate(value);
                Object.DestroyImmediate(FaceMesh); Object.DestroyImmediate(AccessoryMesh);
            }
        }
    }
}
