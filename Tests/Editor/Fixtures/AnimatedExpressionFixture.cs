using System;
using System.Collections.Generic;
using VRVlog.Expressions;

namespace VRVlog.LilToonExporter.Tests
{
    internal static class AnimatedExpressionFixture
    {
        internal static void Run(Action<bool, string> check)
        {
            var curve = new ExpressionAnimationData.Curve();
            curve.Keys.Add(new ExpressionAnimationData.Key { Time = 0, Value = 0, InTangent = 100, OutTangent = 100 });
            curve.Keys.Add(new ExpressionAnimationData.Key { Time = 1, Value = 100, InTangent = 100, OutTangent = 100 });
            curve.Validate();
            check(Math.Abs(curve.Evaluate(.25) - 25) < .00001, "Portable linear curve retains continuous intermediate values.");
            curve.PreWrap = "loop"; curve.PostWrap = "pingPong";
            check(Math.Abs(curve.Evaluate(-.25) - 75) < .00001 && Math.Abs(curve.Evaluate(1.25) - 75) < .00001,
                "Per-curve pre/post wrapping is preserved independently of clip looping.");
            curve.Keys[0].OutTangent = null;
            check(curve.Evaluate(.999) == 0 && curve.Evaluate(1) == 100, "Stepped curves change at the authored key, without a blended transition.");
            curve.Keys[0].OutTangent = 100; curve.Keys[0].OutWeight = .8;
            curve.Keys[1].InTangent = 0; curve.Keys[1].InWeight = .1;
            check(Math.Abs(curve.Evaluate(.7625) - 80) < .00001, "Weighted tangents solve Bezier time rather than treating time as its parameter.");
            var animation = new ExpressionAnimationData { Expression = "VRChat / ジェスチャー / Cry", Duration = 1, Loop = true };
            var channel = new ExpressionAnimationData.Channel { Curve = curve };
            channel.Points.Add(new ExpressionAnimationData.Point { Value = 0, Target = ExpressionAnimationData.TargetPrefix + "low" });
            channel.Points.Add(new ExpressionAnimationData.Point { Value = 50, Target = ExpressionAnimationData.TargetPrefix + "middle" });
            channel.Points.Add(new ExpressionAnimationData.Point { Value = 100, Target = ExpressionAnimationData.TargetPrefix + "high" });
            animation.Channels.Add(channel);
            var encoded = JsonDom.Serialize(ExpressionAnimationData.Write(new[] { animation }));
            var decoded = ExpressionAnimationData.Read(JsonDom.Parse(encoded))[0];
            check(decoded.Expression == animation.Expression && decoded.Loop && Math.Abs(decoded.TimeAt(2.25) - .25) < .00001,
                "Animation names and loop timing round-trip through real JSON.");
            decoded.Channels[0].Weights(.7625, out var left, out var right, out var blend);
            check(left == 1 && right == 2 && Math.Abs(blend - .6) < .00001, "Multi-frame morphs interpolate between the correct geometry knots.");
            decoded.Loop = false;
            check(decoded.TimeAt(2) == 1, "Non-looping expressions hold their final pose.");
            channel.Points[2].Value = 99;
            var rejected = false;
            try { ExpressionAnimationData.Write(new[] { animation }); } catch (InvalidOperationException) { rejected = true; }
            check(rejected, "An insufficient geometry range cannot silently clip the animated face.");
            channel.Points[2].Value = 100;
            channel.Points[1].Target = channel.Points[0].Target;
            rejected = false;
            try { ExpressionAnimationData.Write(new[] { animation }); } catch (InvalidOperationException) { rejected = true; }
            check(rejected, "Two animated channels cannot alias the same runtime morph target.");
        }
    }
}
