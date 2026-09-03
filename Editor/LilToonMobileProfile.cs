using System;
using System.Collections.Generic;

namespace VRVlog.LilToonExporter
{
    public static class LilToonMobileProfile
    {
        public const string ExtensionName = "VRVLOG_materials_liltoon";
        public const int SchemaMajor = 1;
        public const int SchemaMinor = 1;
        public const int DefaultMaximumTextureSize = 1024;
        public const int MaximumTextureSize = 2048;
        public const int MaximumMaterials = 64;
        public const int MaximumTextures = 128;
        public const int MaximumFeaturesPerMaterial = 16;
        public const int MaximumVersionLength = 64;
        public const int MaximumPropertyNameLength = 96;
        public const int MaximumSemanticLength = 32;
        public const int MaximumFloatProperties = 128;
        public const int MaximumColorProperties = 32;
        public const int MaximumTextureProperties = 32;

        public static readonly IReadOnlyCollection<string> SupportedFeatures =
            Array.AsReadOnly(new[]
            {
                "mainColor",
                "shadow",
                "backlight",
                "normalMap",
                "emission",
                "rimLight",
                "matCap",
                "outline"
            });

        public static readonly IReadOnlyCollection<string> SupportedShaderFamilies =
            Array.AsReadOnly(new[]
            {
                "lilToon",
                "lilToonLite",
                "lilToonMulti"
            });

        public static readonly IReadOnlyCollection<string> SupportedRenderModes =
            Array.AsReadOnly(new[] { "opaque", "cutout", "transparent" });

        public static readonly IReadOnlyCollection<string> SupportedCullModes =
            Array.AsReadOnly(new[] { "off", "front", "back" });

        public static readonly IReadOnlyCollection<string> RejectedShaderFamilies =
            Array.AsReadOnly(new[]
            {
                "fur",
                "refraction",
                "gem",
                "tessellation",
                "audioLink",
                "custom"
            });

        public static bool IsSupportedFeature(string feature)
        {
            if (string.IsNullOrWhiteSpace(feature)) return false;
            foreach (var supported in SupportedFeatures)
            {
                if (string.Equals(supported, feature, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        public static bool IsSupportedShaderFamily(string shaderFamily)
        {
            if (string.IsNullOrWhiteSpace(shaderFamily)) return false;
            foreach (var supported in SupportedShaderFamilies)
            {
                if (string.Equals(supported, shaderFamily, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        public static bool IsSupportedRenderMode(string renderMode)
        {
            return ContainsOrdinal(SupportedRenderModes, renderMode);
        }

        public static bool IsSupportedCullMode(string cullMode)
        {
            return ContainsOrdinal(SupportedCullModes, cullMode);
        }

        private static bool ContainsOrdinal(
            IReadOnlyCollection<string> supportedValues,
            string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            foreach (var supported in supportedValues)
            {
                if (string.Equals(supported, value, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }
}
