using UnityEngine;

namespace VRVlog.LilToonExporter
{
    internal static class MobileMaterialMath
    {
        internal static Color MatcapColor(Color color, float blend)
        {
            // lilToon multiplies the MatCap contribution by color alpha.
            // MToon samples RGB only, so fold alpha into its RGB factor.
            var strength = Mathf.Clamp01(color.a) * Mathf.Clamp01(blend);
            // Material color factors are sRGB. Multiply the light contribution
            // in linear space before returning an sRGB material property.
            var linear = color.linear;
            return new Color(linear.r * strength, linear.g * strength, linear.b * strength, 1f).gamma;
        }

        internal static Color OutlineMask(Color sampled)
        {
            // lilToon reads red; MToon reads green. The input has already
            // been sampled in linear space, respecting the source importer.
            var width = Mathf.Clamp01(sampled.r);
            return new Color(width, width, width, 1f);
        }
    }
}
