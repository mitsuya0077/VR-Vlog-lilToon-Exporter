using System;
using System.Collections.Generic;
using UniGLTF;
using UniVRM10;
using UnityEditor.PackageManager;
using UnityEngine;
using VRM10.MToon10;

namespace VRVlog.LilToonExporter
{
    internal static class UniVrmOneClickExporter
    {
        internal const string SupportedUniVrmSeries = "0.131";

        public static byte[] Export(GameObject source, string avatarName, string author)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (string.IsNullOrWhiteSpace(avatarName)) throw new InvalidOperationException("Avatar name is required by VRM 1.0.");
            if (string.IsNullOrWhiteSpace(author)) throw new InvalidOperationException("Author is required by VRM 1.0.");

            EnsureUniVrmVersion();
            var clone = UnityEngine.Object.Instantiate(source);
            clone.name = source.name;
            var temporaryMaterials = new List<Material>();
            try
            {
                ReplaceLilToonMaterials(clone, temporaryMaterials);
                return Vrm10Exporter.Export(
                    new GltfExportSettings(),
                    clone,
                    materialExporter: new BuiltInVrm10MaterialExporter(),
                    textureSerializer: new EditorTextureSerializer(),
                    vrmMeta: CreateMeta(avatarName.Trim(), author.Trim()));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clone);
                foreach (var material in temporaryMaterials) UnityEngine.Object.DestroyImmediate(material);
            }
        }

        private static void EnsureUniVrmVersion()
        {
            var package = PackageInfo.FindForAssembly(typeof(Vrm10Exporter).Assembly);
            var version = package != null ? package.version : null;
            if (string.IsNullOrWhiteSpace(version) || !version.StartsWith(SupportedUniVrmSeries + ".", StringComparison.Ordinal))
                throw new InvalidOperationException($"UniVRM {SupportedUniVrmSeries}.x is required. Installed package version: {version ?? "unknown"}.");
        }

        private static VRM10ObjectMeta CreateMeta(string avatarName, string author)
        {
            // Build disclosure-sensitive metadata from the fields shown in this
            // export window. Never copy contact information, references,
            // thumbnails, or license settings from an imported VRM implicitly.
            return new VRM10ObjectMeta
            {
                Name = avatarName,
                Version = "1.0",
                Authors = new List<string> { author },
                Redistribution = false,
            };
        }

        private static void ReplaceLilToonMaterials(GameObject clone, List<Material> created)
        {
            var converted = new Dictionary<Material, Material>();
            foreach (var renderer in clone.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                var changed = false;
                for (var i = 0; i < materials.Length; i++)
                {
                    var source = materials[i];
                    if (!LilToonMaterialReader.IsLilToon(source)) continue;
                    if (!converted.TryGetValue(source, out var fallback))
                    {
                        fallback = CreateMToonFallback(source, created);
                        converted.Add(source, fallback);
                    }
                    materials[i] = fallback;
                    changed = true;
                }
                if (changed) renderer.sharedMaterials = materials;
            }
            if (converted.Count == 0) throw new InvalidOperationException("The selected avatar has no supported lilToon materials.");
        }

        private static Material CreateMToonFallback(Material source, List<Material> created)
        {
            // Validate the full mobile subset before producing any fallback output.
            LilToonMaterialReader.Read(source, 0, _ => 0);
            var shader = Shader.Find(MToon10Meta.UnityShaderName);
            if (shader == null) throw new InvalidOperationException("UniVRM MToon10 shader is unavailable.");
            var material = new Material(shader) { name = source.name };
            created.Add(material);
            var shadowEnabled = source.HasProperty("_UseShadow") && source.GetFloat("_UseShadow") > 0.5f;
            var normalEnabled = EnabledOrTexture(source, "_UseBumpMap", "_BumpMap");
            var emissionEnabled = EnabledOrTexture(source, "_UseEmission", "_EmissionMap");
            var matcapEnabled = EnabledOrTexture(source, "_UseMatCap", "_MatCapTex");
            var rimEnabled = EnabledOrTexture(source, "_UseRim", "_RimColorTex");
            var context = new MToon10Context(material)
            {
                AlphaMode = AlphaMode(source),
                AlphaCutoff = Float(source, "_Cutoff", 0.5f),
                // glTF/MToon cannot express front-face culling. Double-sided is
                // the safe portable approximation for lilToon's front/off modes.
                DoubleSidedMode = Float(source, "_Cull", 2f) == 2f ? MToon10DoubleSidedMode.Off : MToon10DoubleSidedMode.On,
                BaseColorFactorSrgb = Color(source, "_Color", UnityEngine.Color.white),
                BaseColorTexture = Texture(source, "_MainTex"),
                ShadeColorFactorSrgb = shadowEnabled ? Color(source, "_ShadowColor", UnityEngine.Color.gray) : Color(source, "_Color", UnityEngine.Color.white),
                ShadeColorTexture = shadowEnabled ? Texture(source, "_ShadowColorTex") : null,
                NormalTexture = Texture(source, "_BumpMap"),
                NormalTextureScale = normalEnabled ? Float(source, "_BumpScale", 1f) : 0f,
                EmissiveFactorLinear = emissionEnabled ? Color(source, "_EmissionColor", UnityEngine.Color.black).linear : UnityEngine.Color.black,
                EmissiveTexture = Texture(source, "_EmissionMap"),
                MatcapColorFactorSrgb = matcapEnabled ? Color(source, "_MatCapColor", UnityEngine.Color.white) : UnityEngine.Color.black,
                MatcapTexture = Texture(source, "_MatCapTex"),
                ParametricRimColorFactorSrgb = rimEnabled ? Color(source, "_RimColor", UnityEngine.Color.black) : UnityEngine.Color.black,
                ParametricRimFresnelPowerFactor = Mathf.Max(0f, Float(source, "_RimFresnelPower", 1f)),
                RimMultiplyTexture = Texture(source, "_RimColorTex"),
                OutlineWidthMode = Float(source, "_UseOutline", 0f) > 0.5f ? MToon10OutlineMode.World : MToon10OutlineMode.None,
                OutlineWidthFactor = Mathf.Max(0f, Float(source, "_OutlineWidth", 0f)),
                OutlineWidthMultiplyTexture = Texture(source, "_OutlineTex"),
                OutlineColorFactorSrgb = Color(source, "_OutlineColor", UnityEngine.Color.black),
                OutlineLightingMixFactor = Mathf.Clamp01(Float(source, "_OutlineEnableLighting", 0f)),
            };
            if (source.HasProperty("_MainTex"))
            {
                context.TextureScale = source.GetTextureScale("_MainTex");
                context.TextureOffset = source.GetTextureOffset("_MainTex");
            }
            context.Validate();
            return material;
        }

        private static MToon10AlphaMode AlphaMode(Material material)
        {
            var shaderName = material.shader != null ? material.shader.name : "";
            if (shaderName.IndexOf("Cutout", StringComparison.OrdinalIgnoreCase) >= 0) return MToon10AlphaMode.Cutout;
            if (shaderName.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0) return MToon10AlphaMode.Transparent;
            var mode = Mathf.RoundToInt(Float(material, "_TransparentMode", 0f));
            return mode == 1 ? MToon10AlphaMode.Cutout : mode == 2 ? MToon10AlphaMode.Transparent : MToon10AlphaMode.Opaque;
        }

        private static float Float(Material material, string property, float fallback) => material.HasProperty(property) ? material.GetFloat(property) : fallback;
        private static Color Color(Material material, string property, Color fallback) => material.HasProperty(property) ? material.GetColor(property) : fallback;
        private static Texture Texture(Material material, string property) => material.HasProperty(property) ? material.GetTexture(property) : null;
        private static bool EnabledOrTexture(Material material, string enableProperty, string textureProperty) =>
            material.HasProperty(enableProperty)
                ? material.GetFloat(enableProperty) > 0.5f
                : Texture(material, textureProperty) != null;
    }
}
