using System;

namespace VRVlog.LilToonExporter
{
    internal static class VrChatFixedExpressionCurve
    {
        internal struct Key { internal float Value, InTangent, OutTangent; }

        internal static bool TryRead(Key[] keys, out float value)
        {
            value = 0;
            if (keys == null || keys.Length == 0) return false;
            value = keys[0].Value;
            if (float.IsNaN(value) || float.IsInfinity(value)) return false;
            for (var i = 0; i < keys.Length; i++)
            {
                var key = keys[i];
                if (key.Value != value) return false;
                // Only tangents within the curve affect its value. Stepped
                // segments with identical endpoints are constant as well.
                if (i > 0 && key.InTangent != 0 && !float.IsInfinity(key.InTangent)) return false;
                if (i + 1 < keys.Length && key.OutTangent != 0 && !float.IsInfinity(key.OutTangent)) return false;
            }
            return true;
        }
    }
}
