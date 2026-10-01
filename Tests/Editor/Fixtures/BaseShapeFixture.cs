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
            AuthorEndpointChecks(check);
            LeadingNeutralFrameChecks(check);
            var source = Create();
            var target = Create();
            try
            {
                check(AvatarBaseShape.HasUsableRawEndpoint(source, 0, 0), "A finite nonzero raw endpoint is usable tracking evidence.");
                check(AvatarBaseShape.HasUsableRawEndpoint(source, 0, 75), "A partially resting raw endpoint keeps usable residual range.");
                foreach (var weight in new[] { 100f, 150f, -1f, float.NaN, float.PositiveInfinity })
                    check(!AvatarBaseShape.HasUsableRawEndpoint(source, 0, weight), "A fully resting or unsupported raw weight cannot waive blink validation.");
                check(!AvatarBaseShape.HasUsableRawEndpoint(source, 99, 0), "An invalid raw morph index is not usable evidence.");
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
                check(AvatarBaseShape.HasUsableRawEndpoint(source, 0, 75), "Multiframe residual evidence uses the original interpolation curve.");
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
                source.ClearBlendShapes();
                source.AddBlendShapeFrame("Inert", 100, Delta(0, 0, 0), null, null);
                check(!AvatarBaseShape.HasUsableRawEndpoint(source, 0, 0), "An inert raw morph cannot waive blink validation.");
                source.ClearBlendShapes();
                source.AddBlendShapeFrame("Cancelled endpoint", 50, Delta(2, 0, 0), null, null);
                source.AddBlendShapeFrame("Cancelled endpoint", 100, Delta(0, 0, 0), null, null);
                check(!AvatarBaseShape.HasUsableRawEndpoint(source, 0, 0), "A meaningful intermediate frame cannot conceal a zero exported endpoint.");
                check(AvatarBaseShape.HasUsableRawEndpoint(source, 0, 50), "A nonzero authored rest can leave a usable residual at a zero final frame.");
#if EXPORTER_BEHAVIOR_TESTS
                // Array-only host fixtures can represent invalid payloads that
                // Unity's native mesh API may reject while constructing them.
                source.ClearBlendShapes();
                source.AddBlendShapeFrame("Invalid delta", 100, Delta(float.NaN, 0, 0), null, null);
                check(!AvatarBaseShape.HasUsableRawEndpoint(source, 0, 0), "A nonfinite raw delta cannot waive blink validation.");
                source.ClearBlendShapes();
                source.AddBlendShapeFrame("Invalid frame", float.NaN, Delta(2, 0, 0), null, null);
                check(!AvatarBaseShape.HasUsableRawEndpoint(source, 0, 0), "A nonfinite raw frame weight cannot waive blink validation.");
                source.ClearBlendShapes();
                source.AddBlendShapeFrame("Invalid normal", 100, Delta(2, 0, 0), Delta(float.PositiveInfinity, 0, 0), null);
                check(!AvatarBaseShape.HasUsableRawEndpoint(source, 0, 0), "Invalid auxiliary raw geometry cannot waive blink validation.");
#endif

            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        private static void LeadingNeutralFrameChecks(Action<bool, string> check)
        {
            var source=Create();var target=Create();
            try
            {
                source.ClearBlendShapes();
                source.AddBlendShapeFrame("Neutral origin",0,Delta(0,0,0),null,null);
                source.AddBlendShapeFrame("Neutral origin",100,Delta(4,0,0),null,null);
                foreach(var rest in new[]{0f,25f,100f})
                {
                    check(AvatarBaseShape.HasUsableRawEndpoint(source,0,rest)==(rest<100f),"A pure leading0 frame preserves raw residual qualification at rest"+rest+".");
                    check(AvatarBaseShape.HasUsableMorphEndpoint(source,0,rest)==(rest<100f),"A pure leading0 frame preserves authored residual qualification at rest"+rest+".");
                    AvatarBaseShape.Rebase(source,target,new[]{rest});
                    var vertices=new Vector3[3];target.GetBlendShapeFrameVertices(0,0,vertices,null,null);
                    check(Near(target.vertices[0].x,1f+4f*rest/100f) && Near(vertices[0].x,4f*(1f-rest/100f)) && Near(target.vertices[0].x+vertices[0].x,5f),
                        "Rebase evaluates a leading purezero as the implicit origin, preserves authored rest and reaches the original endpoint.");
                }
                check(source.GetBlendShapeFrameCount(0)==2 && source.GetBlendShapeFrameWeight(0,0)==0f && source.GetBlendShapeFrameWeight(0,1)==100f && source.vertices[0].x==1f,"Neutral-frame qualification and rebase never mutate the source mesh or frame intervals.");
                source.ClearBlendShapes();source.AddBlendShapeFrame("Only neutral",0,Delta(0,0,0),null,null);
                check(!AvatarBaseShape.HasUsableRawEndpoint(source,0,0) && !AvatarBaseShape.HasUsableMorphEndpoint(source,0,0),"A leading zero frame without any positive moving frame cannot establish an endpoint.");
#if EXPORTER_BEHAVIOR_TESTS
                foreach(var invalid in new[]{"nonzeroZero","negative","nonfinite"})
                {
                    source.ClearBlendShapes();source.AddBlendShapeFrame("Invalid origin",invalid=="negative"?-1f:invalid=="nonfinite"?float.PositiveInfinity:0f,
                        Delta(invalid=="nonzeroZero"?1f:0f,0,0),null,null);
                    source.AddBlendShapeFrame("Invalid origin",100,Delta(4,0,0),null,null);
                    check(!AvatarBaseShape.HasUsableRawEndpoint(source,0,0) && !AvatarBaseShape.HasUsableMorphEndpoint(source,0,0),"Only a finite purezero leading frame is an implicit origin; malformed"+invalid+" remains excluded.");
                }
#endif
            }
            finally { UnityEngine.Object.DestroyImmediate(source);UnityEngine.Object.DestroyImmediate(target); }
        }

        private static void AuthorEndpointChecks(Action<bool, string> check)
        {
            var source = Create();
            try
            {
                check(AvatarBaseShape.HasUsableMorphEndpoint(source, 0, 0), "A positive authored morph route needs a finite nonzero residual endpoint.");
                check(AvatarBaseShape.HasUsableMorphEndpoint(source, 0, 75), "A partially resting authored route preserves its remaining endpoint range.");
                check(!AvatarBaseShape.HasUsableMorphEndpoint(source, 0, 100), "An authored morph already at its exported endpoint cannot establish tracking support.");
                check(AvatarBaseShape.HasUsableMorphEndpoint(source, 0, -25), "A finite negative authored rest remains usable when its exported endpoint differs.");
                check(AvatarBaseShape.HasUsableMorphEndpoint(source, 0, 150), "A finite authored rest beyond the final frame can retain a nonzero residual endpoint.");
                foreach (var rest in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
                    check(!AvatarBaseShape.HasUsableMorphEndpoint(source, 0, rest), "Nonfinite authored rest cannot qualify an endpoint.");
                check(!AvatarBaseShape.HasUsableMorphEndpoint(source, -1, 0) && !AvatarBaseShape.HasUsableMorphEndpoint(source, 99, 0), "Invalid authored target indices cannot qualify geometry.");
                source.ClearBlendShapes();
                source.AddBlendShapeFrame("Inert author", 100, Delta(0, 0, 0), null, null);
                check(!AvatarBaseShape.HasUsableMorphEndpoint(source, 0, 0), "A named authored zero-delta shape has no usable endpoint.");
                source.ClearBlendShapes();
                source.AddBlendShapeFrame("Normal-only author", 100, Delta(0, 0, 0), Delta(1, 0, 0), null);
                check(AvatarBaseShape.HasUsableMorphEndpoint(source, 0, 0), "Finite authored normal movement qualifies even without positional delta.");
                source.ClearBlendShapes();
                source.AddBlendShapeFrame("Tangent-only author", 100, Delta(0, 0, 0), null, Delta(0, 1, 0));
                check(AvatarBaseShape.HasUsableMorphEndpoint(source, 0, 0), "Finite authored tangent movement qualifies even without positional delta.");
                source.ClearBlendShapes();
                source.AddBlendShapeFrame("Rest cancels endpoint", 50, Delta(3, 0, 0), null, null);
                source.AddBlendShapeFrame("Rest cancels endpoint", 100, Delta(3, 0, 0), null, null);
                check(!AvatarBaseShape.HasUsableMorphEndpoint(source, 0, 50), "An intermediate rest equal to the final endpoint has no exported residual despite nonzero frames.");
                check(AvatarBaseShape.HasUsableMorphEndpoint(source, 0, 0), "The same multiframe authored morph can qualify from a different rest.");
                source.ClearBlendShapes();
                source.AddBlendShapeFrame("Final cancellation", 50, Delta(2, 0, 0), null, null);
                source.AddBlendShapeFrame("Final cancellation", 100, Delta(0, 0, 0), null, null);
                check(!AvatarBaseShape.HasUsableMorphEndpoint(source, 0, 0), "An intermediate moving frame cannot make a zero final/rest residual usable.");
                check(AvatarBaseShape.HasUsableMorphEndpoint(source, 0, 50), "A zero final frame still moves away from a nonzero authored rest.");
#if EXPORTER_BEHAVIOR_TESTS
                foreach (var invalid in new[] { "delta", "normal", "tangent", "frame" })
                {
                    source.ClearBlendShapes();
                    source.AddBlendShapeFrame("Malformed author", invalid == "frame" ? float.NaN : 100,
                        Delta(invalid == "delta" ? float.NaN : 2, 0, 0),
                        invalid == "normal" ? Delta(float.PositiveInfinity, 0, 0) : null,
                        invalid == "tangent" ? Delta(0, float.NegativeInfinity, 0) : null);
                    check(!AvatarBaseShape.HasUsableMorphEndpoint(source, 0, 0), "Malformed authored " + invalid + " data cannot qualify an endpoint.");
                }
#endif
            }
            finally { UnityEngine.Object.DestroyImmediate(source); }
        }
    }
}
