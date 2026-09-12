using System;
using System.Collections.Generic;

namespace VRVlog.LilToonExporter
{
    // Exact semantic names only. "eye_close" can mean eye spacing, as in Plum.
    internal static class BlinkShapeNames
    {
        internal const int PartialPair = -3;
        internal static readonly string[] Presets = { "blink", "blinkLeft", "blinkRight" };
        static readonly string[] Both = { "vrc.Blink", "Blink", "Fcl_EYE_Close", "まばたき", "eye_blink_1", "eye_blink_2" };
        static readonly (string Left, string Right)[] Pairs = {
            ("Blink_L", "Blink_R"), ("BlinkLeft", "BlinkRight"),
            ("EyeBlinkLeft", "EyeBlinkRight"), ("Fcl_EYE_Close_L", "Fcl_EYE_Close_R"),
            ("eye_blink_1_L", "eye_blink_1_R"), ("eye_blink_2_L", "eye_blink_2_R")
        };

        internal static int Unique(IReadOnlyList<string> names, string name)
        {
            var result = -1;
            for (var i = 0; i < names.Count; i++)
                if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    if (result >= 0) return -2;
                    result = i;
                }
            return result;
        }

        internal static int[] Resolve(IReadOnlyList<string> names)
        {
            var result = new[] { -1, -1, -1 };
            foreach (var name in Both)
            {
                var index = Unique(names, name);
                if (index == -2) return new[] { -2, -2, -2 };
                if (index < 0) continue;
                result[0] = index;
                break;
            }
            var partial = false;
            foreach (var pair in Pairs)
            {
                var left = Unique(names, pair.Left);
                var right = Unique(names, pair.Right);
                if (left == -2 || right == -2) return new[] { -2, -2, -2 };
                if (left < 0 || right < 0)
                {
                    partial |= left >= 0 || right >= 0;
                    continue;
                }
                result[1] = left;
                result[2] = right;
                break;
            }
            if (partial && result[1] < 0) result[1] = result[2] = PartialPair;
            return result;
        }
    }
}
