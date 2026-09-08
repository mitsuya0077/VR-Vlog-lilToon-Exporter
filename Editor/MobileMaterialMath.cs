using System;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    internal static class MobileMaterialMath
    {
        internal static float ShadowShift(float border, float blur)
        {
            var lower = Mathf.Clamp01(border - Mathf.Clamp01(blur) * 0.5f);
            var upper = Mathf.Clamp01(border + Mathf.Clamp01(blur) * 0.5f);
            return 1f - lower - upper;
        }

        internal static float ShadowToony(float border, float blur)
        {
            var lower = Mathf.Clamp01(border - Mathf.Clamp01(blur) * 0.5f);
            var upper = Mathf.Clamp01(border + Mathf.Clamp01(blur) * 0.5f);
            return 1f - (upper - lower);
        }

        internal static Color ShadeColor(Color main, Color shade, float strength)
        {
            var weight = Mathf.Clamp01(strength);
            main = main.linear;
            shade = shade.linear;
            return new Color(main.r + (shade.r - main.r) * weight,
                main.g + (shade.g - main.g) * weight,
                main.b + (shade.b - main.b) * weight, 1f).gamma;
        }

        internal static float RimPower(float border, float fresnel)
        {
            // Standard MToon has no independent rim border/blur. Match the
            // authored half-intensity angle without a positive lift, which
            // would add a highlight even on a front-facing surface.
            var threshold = Math.Min(0.999f, Math.Max(0.001f, border));
            var power = Math.Max(0.0001f, fresnel);
            return (float)Math.Min(100.0, Math.Max(0.0001, Math.Log(0.5) * power / Math.Log(threshold)));
        }

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
