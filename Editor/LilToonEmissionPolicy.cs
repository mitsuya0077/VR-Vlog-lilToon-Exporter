using UnityEngine;

namespace VRVlog.LilToonExporter
{
    internal static class LilToonEmissionPolicy
    {
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
