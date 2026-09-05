using System;
using UnityEngine;
using VRVlog.LilToonExporter;

namespace VRVlog.LilToonExporter.Tests
{
    internal static class BaseShapeFixture
    {
        internal static Mesh Create()
        {
            var mesh = new Mesh { name = "Synthetic rest face" };
            mesh.vertices = new[] { new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1) };
            mesh.normals = new[] { new Vector3(0, 0, 1), new Vector3(0, 0, 1), new Vector3(0, 0, 1) };
            mesh.tangents = new[] { new Vector4(1, 0, 0, -1), new Vector4(1, 0, 0, -1), new Vector4(1, 0, 0, -1) };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.AddBlendShapeFrame("Face size", 100, Delta(4, 0, 0), Delta(0, 1, 0), Delta(0, 0, 1));
            mesh.AddBlendShapeFrame("Blink", 100, Delta(0, 2, 0), Delta(1, 0, 0), Delta(0, 1, 0));
            return mesh;
        }

        static Vector3[] Delta(float x, float y, float z) => new[] { new Vector3(x, y, z), Vector3.zero, Vector3.zero };
        static bool Near(float a, float b) => Math.Abs(a - b) < 0.00001f;

        internal static void Run(Action<bool, string> check)
        {
            var source = Create();
            var target = Create();
            try
            {
                AvatarBaseShape.Rebase(source, target, new[] { 25f, 50f });
                check(Near(target.vertices[0].x, 2) && Near(target.vertices[0].y, 1), "Authored face and partially closed eye are stored in base geometry.");
                check(target.blendShapeCount == 2 && target.GetBlendShapeName(1) == "Blink", "Expression target indices and names are preserved.");
                var v = new Vector3[3]; var n = new Vector3[3]; var t = new Vector3[3];
                target.GetBlendShapeFrameVertices(1, 0, v, n, t);
                check(Near(target.vertices[0].y + v[0].y, 2), "Full blink reaches original endpoint without double applying the initial weight.");
                check(Near(target.vertices[0].x + v[0].x, 2), "Blink keeps the authored face-size customization.");
                check(Near(target.normals[0].x, 0.5f) && Near(target.normals[0].y, 0.25f), "Normal deltas are baked.");
                check(Near(target.tangents[0].y, 0.5f) && Near(target.tangents[0].z, 0.25f) && target.tangents[0].w == -1, "Tangent deltas and handedness are preserved.");
                check(source.vertices[0].x == 1 && source.vertices[0].y == 0, "The original shared mesh is unchanged.");
                check(target.triangles[2] == 2, "Topology is unchanged.");
                AvatarBaseShape.AppendExpression(source, target, "Menu smile", new[] { 25f, 50f }, new[] { 75f, 0f });
                target.GetBlendShapeFrameVertices(2, 0, v, n, t);
                check(Near(target.vertices[0].x + v[0].x, 4) && Near(target.vertices[0].y + v[0].y, 0),
                    "One menu expression combines partial weights and can reopen a customized half-closed eye.");
                check(target.blendShapeCount == 3 && target.GetBlendShapeName(1) == "Blink" && source.blendShapeCount == 2,
                    "A composite is appended without replacing source morphs or existing VRM indices.");
                check(Near(target.normals[0].x + n[0].x, 0) && Near(target.normals[0].y + n[0].y, .75f),
                    "Composite normals subtract the authored rest as well as adding the selected pose.");
                bool rejected = false;
                try { AvatarBaseShape.AppendExpression(source, target, "Invalid", new[] { 25f, 50f }, new[] { float.NaN, 0f }); }
                catch (InvalidOperationException) { rejected = true; }
                check(rejected, "Invalid expression weights cannot enter geometry.");
                AvatarBaseShape.Rebase(source, target, new[] { 100f, 0f });
                target.GetBlendShapeFrameVertices(0, 0, v, n, t);
                check(Near(v[0].x, 0) && target.blendShapeCount == 2, "Fully authored shapes retain their zero residual target slot.");
                source.ClearBlendShapes();
                source.AddBlendShapeFrame("Multi", 50, Delta(2, 0, 0), null, null);
                source.AddBlendShapeFrame("Multi", 100, Delta(6, 0, 0), null, null);
                AvatarBaseShape.Rebase(source, target, new[] { 75f });
                check(Near(target.vertices[0].x, 5), "Authored values interpolate between multiple frames.");
                AvatarBaseShape.AppendExpression(source, target, "Menu multi", new[] { 75f }, new[] { 25f });
                target.GetBlendShapeFrameVertices(1, 0, v, n, t);
                check(Near(target.vertices[0].x + v[0].x, 2), "Composed expressions evaluate multi-frame curves in the original mesh, not rebased linear weights.");
                AvatarBaseShape.Rebase(source, target, new[] { 150f });
                check(Near(target.vertices[0].x, 11), "Values above the final frame extrapolate.");
                AvatarBaseShape.Rebase(source, target, new[] { -25f });
                check(Near(target.vertices[0].x, 0), "Negative authored values extrapolate from zero.");
                AvatarBaseShape.Rebase(source, target, new[] { 0f });
                check(Near(target.vertices[0].x, 1), "Zero defaults keep the original rest geometry.");
                AvatarBaseShape.AppendAnimatedShape(source, target, "Animated negative", 0, 25, -25);
                target.GetBlendShapeFrameVertices(1, 0, v, n, t);
                check(Near(v[0].x, -2), "Animation basis subtracts the first pose and supports negative source weights.");
                AvatarBaseShape.AppendAnimatedShape(source, target, "Animated second frame", 0, 25, 75);
                target.GetBlendShapeFrameVertices(2, 0, v, n, t);
                check(Near(v[0].x, 3), "Animation basis retains the authored multi-frame geometry.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(target);
            }
        }
    }
}
