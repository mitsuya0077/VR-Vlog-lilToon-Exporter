using System;
using System.Collections.Generic;

namespace VRVlog.LilToonExporter
{
    // Defaults from lilToon 2.3.4 lts/ltsl.shader. Keep the original values in
    // the extension: the portable MToon approximation is a separate consumer.
    internal static class LilToonLightingProfile
    {
        internal const string DirectionProperty = "_LightDirectionOverride";

        internal readonly struct Property
        {
            internal readonly string Name;
            internal readonly string Feature;
            internal readonly float Default;
            internal Property(string name, float value, string feature = null)
            { Name = name; Default = value; Feature = feature; }
            internal bool AppliesTo(ICollection<string> features) =>
                Feature == null || features.Contains(Feature);
            internal float DefaultFor(string family) =>
                family == "lilToonLite" && Name == "_RimShadowMask" ? 0f : Default;
        }

        internal static readonly Property[] Properties = {
            new Property("_AsUnlit", 0f),
            new Property("_LightMinLimit", 0.05f),
            new Property("_LightMaxLimit", 1f),
            new Property("_MonochromeLighting", 0f),
            new Property("_lilDirectionalLightStrength", 1f),
            new Property("_VertexLightStrength", 0f),
            new Property("_ShadowNormalStrength", 1f, "shadow"),
            new Property("_ShadowReceive", 0f, "shadow"),
            new Property("_ShadowMainStrength", 0f, "shadow"),
            new Property("_ShadowEnvStrength", 0f, "shadow"),
            new Property("_RimEnableLighting", 1f, "rimLight"),
            new Property("_RimMainStrength", 0f, "rimLight"),
            new Property("_RimShadowMask", 0.5f, "rimLight"),
            new Property("_RimNormalStrength", 1f, "rimLight"),
            new Property("_RimBlendMode", 1f, "rimLight"),
            new Property("_MatCapEnableLighting", 1f, "matCap"),
            new Property("_MatCapMainStrength", 0f, "matCap"),
            new Property("_MatCapShadowMask", 0f, "matCap"),
            new Property("_MatCapNormalStrength", 1f, "matCap"),
            new Property("_MatCapBlendMode", 1f, "matCap")
        };

        internal static bool ValidateRequired(LilToonMaterialRecord record, ISet<string> floats, out string error)
        {
            foreach (var property in Properties)
            {
                if (property.AppliesTo(record.features) && !floats.Contains(property.Name))
                {
                    error = $"Material {record.materialIndex} is missing lighting property {property.Name}.";
                    return false;
                }
                foreach (var item in record.floats)
                    if (item.name == property.Name && Normalize(property, item.value, record.shaderFamily) != item.value)
                    {
                        error = $"Material {record.materialIndex} has an out-of-range lighting property {property.Name}.";
                        return false;
                    }
            }
            if (record.vectors.Count != 1 || record.vectors[0].name != DirectionProperty)
            {
                error = $"Material {record.materialIndex} requires {DirectionProperty}.";
                return false;
            }
            var direction = record.vectors[0];
            if (Math.Abs(direction.x) > 10000f || Math.Abs(direction.y) > 10000f || Math.Abs(direction.z) > 10000f ||
                (direction.w != 0f && direction.w != 1f))
            {
                error = $"Material {record.materialIndex} has an invalid light direction.";
                return false;
            }
            var min = record.floats.Find(item => item.name == "_LightMinLimit").value;
            var max = record.floats.Find(item => item.name == "_LightMaxLimit").value;
            if (min > max)
            {
                error = $"Material {record.materialIndex} has inverted lighting limits.";
                return false;
            }
            error = "";
            return true;
        }

        internal static float Normalize(Property property, float value, string family)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return property.DefaultFor(family);
            var maximum = property.Name == "_LightMaxLimit" ? 10f : property.Name.EndsWith("BlendMode", StringComparison.Ordinal) ? 3f : 1f;
            value = Math.Min(maximum, Math.Max(0f, value));
            return property.Name.EndsWith("BlendMode", StringComparison.Ordinal) ? (float)Math.Round(value) : value;
        }
    }
}
