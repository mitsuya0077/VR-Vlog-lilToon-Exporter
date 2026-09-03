using System;
using System.Collections.Generic;

namespace VRVlog.LilToonExporter
{
    public static class LilToonExtensionValidator
    {
        public static bool TryValidate(
            LilToonExtensionRoot extension,
            out string error)
        {
            if (extension == null)
            {
                error = "Extension is missing.";
                return false;
            }
            if (extension.schemaMajor != LilToonMobileProfile.SchemaMajor)
            {
                error = $"Unsupported schema major: {extension.schemaMajor}.";
                return false;
            }
            if (extension.schemaMinor < 0 ||
                !IsValidVersion(extension.exporterVersion) ||
                !IsValidVersion(extension.sourceLilToonVersion))
            {
                error = "Extension version metadata is invalid.";
                return false;
            }
            if (extension.materials == null ||
                extension.materials.Count > LilToonMobileProfile.MaximumMaterials)
            {
                error = "Material count exceeds the mobile profile.";
                return false;
            }

            var materialIndices = new HashSet<int>();
            for (var i = 0; i < extension.materials.Count; i++)
            {
                var material = extension.materials[i];
                if (material == null || material.materialIndex < 0)
                {
                    error = $"Material record {i} has an invalid index.";
                    return false;
                }
                if (!materialIndices.Add(material.materialIndex))
                {
                    error = $"Duplicate materialIndex: {material.materialIndex}.";
                    return false;
                }
                if (!LilToonMobileProfile.IsSupportedShaderFamily(material.shaderFamily))
                {
                    error = $"Unsupported shader family on material {material.materialIndex}: {material.shaderFamily}.";
                    return false;
                }
                if (!LilToonMobileProfile.IsSupportedRenderMode(material.renderMode) ||
                    !LilToonMobileProfile.IsSupportedCullMode(material.cullMode) ||
                    material.renderQueue < -1 ||
                    material.renderQueue > 5000)
                {
                    error = $"Invalid render state on material {material.materialIndex}.";
                    return false;
                }
                if (material.features == null ||
                    material.features.Count > LilToonMobileProfile.MaximumFeaturesPerMaterial)
                {
                    error = $"Material {material.materialIndex} has an invalid feature list.";
                    return false;
                }
                var features = new HashSet<string>(StringComparer.Ordinal);
                foreach (var feature in material.features)
                {
                    if (!LilToonMobileProfile.IsSupportedFeature(feature) ||
                        !features.Add(feature))
                    {
                        error = $"Unsupported or duplicate feature on material {material.materialIndex}: {feature}.";
                        return false;
                    }
                }
                if (!ValidateProperties(material, out error))
                    return false;
            }

            error = "";
            return true;
        }

        private static bool ValidateProperties(
            LilToonMaterialRecord material,
            out string error)
        {
            if (material.floats == null ||
                material.floats.Count > LilToonMobileProfile.MaximumFloatProperties ||
                material.colors == null ||
                material.colors.Count > LilToonMobileProfile.MaximumColorProperties ||
                material.textures == null ||
                material.textures.Count > LilToonMobileProfile.MaximumTextureProperties)
            {
                error = $"Invalid property collection on material {material.materialIndex}.";
                return false;
            }
            var floatNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in material.floats)
            {
                if (item == null || !IsValidName(item.name) ||
                    !floatNames.Add(item.name) || !IsFinite(item.value))
                {
                    error = $"Invalid float property on material {material.materialIndex}.";
                    return false;
                }
            }
            var colorNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in material.colors)
            {
                if (item == null || !IsValidName(item.name) ||
                    !colorNames.Add(item.name) ||
                    !IsFinite(item.r) || !IsFinite(item.g) ||
                    !IsFinite(item.b) || !IsFinite(item.a))
                {
                    error = $"Invalid color property on material {material.materialIndex}.";
                    return false;
                }
            }
            var textureNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in material.textures)
            {
                if (item == null || !IsValidName(item.name) ||
                    !textureNames.Add(item.name) ||
                    item.textureIndex < 0 ||
                    item.textureIndex >= LilToonMobileProfile.MaximumTextures ||
                    string.IsNullOrWhiteSpace(item.semantic) ||
                    item.semantic.Length > LilToonMobileProfile.MaximumSemanticLength ||
                    !IsFinite(item.scaleX) || !IsFinite(item.scaleY) ||
                    !IsFinite(item.offsetX) || !IsFinite(item.offsetY))
                {
                    error = $"Invalid texture property on material {material.materialIndex}.";
                    return false;
                }
            }

            error = "";
            return true;
        }

        private static bool IsValidVersion(string version)
        {
            return !string.IsNullOrWhiteSpace(version) &&
                   version.Length <= LilToonMobileProfile.MaximumVersionLength;
        }

        private static bool IsValidName(string name)
        {
            return !string.IsNullOrWhiteSpace(name) &&
                   name.Length <= LilToonMobileProfile.MaximumPropertyNameLength;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
