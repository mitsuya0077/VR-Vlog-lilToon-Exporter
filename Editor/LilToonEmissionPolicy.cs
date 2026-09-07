using UnityEngine;

namespace VRVlog.LilToonExporter
{
    internal static class LilToonEmissionPolicy
    {
        public static bool HasHdrTextureEmission(Material material)
        {
            if (material == null || !material.HasProperty("_UseEmission") || material.GetFloat("_UseEmission") <= 0.5f ||
                !material.HasProperty("_EmissionMap") || material.GetTexture("_EmissionMap") == null ||
                !material.HasProperty("_EmissionColor")) return false;
            if (material.HasProperty("_EmissionBlend") && material.GetFloat("_EmissionBlend") <= 0) return false;
            var color = material.GetColor("_EmissionColor");
            return color.a > 0 && (color.r > 1 || color.g > 1 || color.b > 1);
        }

        // A common mobile-unfriendly setup adds the entire base image back as
        // HDR emission. Make this appearance approximation explicit and opt-out;
        // never identify eyes or a particular avatar by their material names.
        public static bool IsSuppressed(Material material, bool suppressSharedTextureEmission)
        {
            if (!suppressSharedTextureEmission || material == null ||
                !material.HasProperty("_MainTex") || !material.HasProperty("_EmissionMap")) return false;
            var main = material.GetTexture("_MainTex");
            return main != null && main == material.GetTexture("_EmissionMap");
        }
    }
}
