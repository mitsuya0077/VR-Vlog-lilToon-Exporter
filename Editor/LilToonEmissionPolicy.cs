using UnityEngine;

namespace VRVlog.LilToonExporter
{
    internal static class LilToonEmissionPolicy
    {
        public static bool HasHdrTextureEmission(Material material, bool second=false)
        {
            var suffix=second?"2nd":"";var prefix="_Emission"+suffix;
            if (material == null || !material.HasProperty("_UseEmission"+suffix) || material.GetFloat("_UseEmission"+suffix) <= 0.5f ||
                !material.HasProperty(prefix+"Map") || material.GetTexture(prefix+"Map") == null ||
                !material.HasProperty(prefix+"Color")) return false;
            if (material.HasProperty(prefix+"Blend") && material.GetFloat(prefix+"Blend") <= 0) return false;
            var color = material.GetColor(prefix+"Color");
            return color.a > 0 && (color.r > 1 || color.g > 1 || color.b > 1);
        }

        // A common mobile-unfriendly setup adds the entire base image back as
        // HDR emission. Make this appearance approximation explicit and opt-out;
        // never identify eyes or a particular avatar by their material names.
        public static bool IsSuppressed(Material material, bool suppressSharedTextureEmission, bool second=false)
        {
            var map=second?"_Emission2ndMap":"_EmissionMap";
            if (!suppressSharedTextureEmission || material == null ||
                !material.HasProperty("_MainTex") || !material.HasProperty(map)) return false;
            var main = material.GetTexture("_MainTex");
            return main != null && main == material.GetTexture(map);
        }
    }
}
