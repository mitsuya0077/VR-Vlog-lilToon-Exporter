using System;
using Key = VRVlog.LilToonExporter.VrChatFixedExpressionCurve.Key;

namespace VRVlog.LilToonExporter.Tests
{
    internal static class FixedExpressionCurveFixture
    {
        internal static void Run(Action<bool, string> check)
        {
            bool Read(Key[] keys, float expected) => VrChatFixedExpressionCurve.TryRead(keys, out var value) && value == expected;
            check(Read(new[] { new Key { Value = 37 }, new Key { Value = 37 } }, 37), "Constant authored partial weights remain partial; no guessed 100-percent face.");
            check(Read(new[] { new Key { Value = 0 } }, 0), "Explicit zero/reset values are retained in composed expressions.");
            check(Read(new[] { new Key { Value = -25 }, new Key { Value = -25 } }, -25), "Authored negative weights are retained for base-shape residual evaluation.");
            check(Read(new[] { new Key { Value = 75, OutTangent = float.PositiveInfinity }, new Key { Value = 75, InTangent = float.PositiveInfinity } }, 75), "Identical stepped keys describe a fixed pose.");
            check(!VrChatFixedExpressionCurve.TryRead(new[] { new Key { Value = 75 }, new Key { Value = 100 } }, out _), "A future changed key cannot masquerade as a fixed expression.");
            check(!VrChatFixedExpressionCurve.TryRead(new[] { new Key { Value = 75, OutTangent = 1 }, new Key { Value = 75 } }, out _), "Equal endpoints with a curved interior are animated.");
            check(!VrChatFixedExpressionCurve.TryRead(new[] { new Key { Value = float.NaN } }, out _), "Non-finite facial weights are rejected.");
            check(!VrChatFixedExpressionCurve.TryRead(Array.Empty<Key>(), out _), "An empty animation curve is not a face.");
        }
    }
}
