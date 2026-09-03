using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    internal static class LilToonMaterialReader
    {
        private static readonly string[] FloatNames = { "_Cutoff", "_ShadowStrength", "_ShadowBorder", "_ShadowBlur", "_BacklightMainStrength", "_BacklightNormalStrength", "_BacklightBorder", "_BacklightBlur", "_BacklightDirectivity", "_BacklightViewStrength", "_BacklightReceiveShadow", "_BacklightBackfaceMask", "_BumpScale", "_EmissionBlend", "_RimBorder", "_RimBlur", "_RimFresnelPower", "_MatCapBlend", "_OutlineWidth", "_OutlineEnableLighting" };
        private static readonly string[] ColorNames = { "_Color", "_ShadowColor", "_BacklightColor", "_EmissionColor", "_RimColor", "_MatCapColor", "_OutlineColor" };
        private static readonly (string Name, string Semantic)[] TextureNames = {
            ("_MainTex", "mainColor"), ("_ShadowColorTex", "shadow"), ("_BumpMap", "normalMap"),
            ("_EmissionMap", "emission"), ("_RimColorTex", "rimLight"), ("_MatCapTex", "matCap"), ("_OutlineTex", "outline")
        };
        private static readonly string[] UnsupportedFeatureToggles = {
            "_UseMain2ndTex", "_UseMain3rdTex", "_UseAnisotropy",
            "_UseReflection", "_UseRefraction", "_UseFur", "_UseGem", "_UseAudioLink",
            "_UseDissolve", "_UseDistanceFade", "_UseGlitter", "_UseParallax", "_UseTessellation",
            "_UseEmission2nd", "_UseBump2ndMap", "_UseMatCap2nd", "_AlphaMaskMode"
        };

        public static LilToonMaterialRecord Read(Material material, int materialIndex, Func<Texture, int> textureIndex)
        {
            if (material == null || material.shader == null) throw new ArgumentException("Material and shader are required.");
            var family = ShaderFamily(material.shader.name);
            if (!LilToonMobileProfile.IsSupportedShaderFamily(family))
                throw new NotSupportedException($"Unsupported lilToon shader: {material.shader.name}.");
            foreach (var toggle in UnsupportedFeatureToggles)
                if (Enabled(material, toggle)) throw new NotSupportedException($"Unsupported enabled lilToon feature: {toggle}.");
            if (Enabled(material, "_UseBacklight") && HasCustomBacklightTexture(material))
                throw new NotSupportedException("バックライトのカスタム色テクスチャにはまだ対応していません。色と数値設定だけを使用してください。");
            var record = new LilToonMaterialRecord {
                materialIndex = materialIndex, shaderFamily = family, renderMode = RenderMode(material),
                renderQueue = material.renderQueue, cullMode = CullMode(material)
            };
            AddFeature(record, "mainColor", true);
            AddFeature(record, "shadow", Enabled(material, "_UseShadow"));
            AddFeature(record, "backlight", Enabled(material, "_UseBacklight"));
            AddFeature(record, "normalMap", EnabledOrTexture(material, "_UseBumpMap", "_BumpMap"));
            AddFeature(record, "emission", EnabledOrTexture(material, "_UseEmission", "_EmissionMap"));
            AddFeature(record, "rimLight", Enabled(material, "_UseRim"));
            AddFeature(record, "matCap", Enabled(material, "_UseMatCap"));
            AddFeature(record, "outline", Enabled(material, "_UseOutline") || material.shader.name.EndsWith("Outline", StringComparison.Ordinal));

            foreach (var name in FloatNames) if (material.HasProperty(name)) record.floats.Add(new LilToonFloatProperty { name = name, value = material.GetFloat(name) });
            foreach (var name in ColorNames) if (material.HasProperty(name)) { var c = material.GetColor(name); record.colors.Add(new LilToonColorProperty { name = name, r = c.r, g = c.g, b = c.b, a = c.a }); }
            foreach (var item in TextureNames)
            {
                if (!TextureFeatureEnabled(material, item.Semantic)) continue;
                if (!material.HasProperty(item.Name)) continue; var texture = material.GetTexture(item.Name); if (texture == null) continue;
                var index = textureIndex(texture); if (index < 0) throw new InvalidOperationException($"Texture '{texture.name}' is not present in the fallback VRM.");
                var scale = material.GetTextureScale(item.Name); var offset = material.GetTextureOffset(item.Name);
                record.textures.Add(new LilToonTextureProperty { name = item.Name, semantic = item.Semantic, textureIndex = index, scaleX = scale.x, scaleY = scale.y, offsetX = offset.x, offsetY = offset.y });
            }
            return record;
        }

        public static bool IsLilToon(Material material) => material != null && material.shader != null && material.shader.name.IndexOf("lilToon", StringComparison.OrdinalIgnoreCase) >= 0;
        private static string ShaderFamily(string name)
        {
            var leaf = name.Substring(name.LastIndexOf('/') + 1);
            var family = leaf.StartsWith("lilToonLite", StringComparison.Ordinal) ? "lilToonLite" : leaf.StartsWith("lilToonMulti", StringComparison.Ordinal) ? "lilToonMulti" : leaf.StartsWith("lilToon", StringComparison.Ordinal) ? "lilToon" : "";
            var suffix = family.Length == 0 ? "" : leaf.Substring(family.Length);
            var renderSuffix = suffix.EndsWith("Outline", StringComparison.Ordinal) ? suffix.Substring(0, suffix.Length - "Outline".Length) : suffix;
            if (family.Length == 0 || (renderSuffix != "" && renderSuffix != "Cutout" && renderSuffix != "Transparent" && renderSuffix != "OnePassTransparent" && renderSuffix != "TwoPassTransparent"))
                throw new NotSupportedException($"Unsupported lilToon shader variant: {name}.");
            return family;
        }
        private static bool Enabled(Material m, string p) => m.HasProperty(p) && m.GetFloat(p) > 0.5f;
        private static bool EnabledOrTexture(Material m, string enable, string texture) => m.HasProperty(enable) ? Enabled(m, enable) : HasTexture(m, texture);
        private static bool HasTexture(Material m, string p) => m.HasProperty(p) && m.GetTexture(p) != null;
        private static bool HasCustomBacklightTexture(Material material)
        {
            if (!material.HasProperty("_BacklightColorTex")) return false;
            var texture = material.GetTexture("_BacklightColorTex");
            return texture != null && texture != Texture2D.whiteTexture;
        }
        private static bool TextureFeatureEnabled(Material material, string semantic)
        {
            switch (semantic)
            {
                case "mainColor": return true;
                case "shadow": return Enabled(material, "_UseShadow");
                case "normalMap": return EnabledOrTexture(material, "_UseBumpMap", "_BumpMap");
                case "emission": return EnabledOrTexture(material, "_UseEmission", "_EmissionMap");
                case "rimLight": return Enabled(material, "_UseRim");
                case "matCap": return Enabled(material, "_UseMatCap");
                case "outline": return Enabled(material, "_UseOutline") || material.shader.name.EndsWith("Outline", StringComparison.Ordinal);
                default: throw new NotSupportedException($"Unsupported texture semantic: {semantic}.");
            }
        }
        private static void AddFeature(LilToonMaterialRecord r, string name, bool enabled) { if (enabled) r.features.Add(name); }
        private static string RenderMode(Material m) { var n = m.shader.name; if (n.IndexOf("Cutout", StringComparison.OrdinalIgnoreCase) >= 0) return "cutout"; if (n.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0) return "transparent"; if (m.HasProperty("_TransparentMode")) { var v = Mathf.RoundToInt(m.GetFloat("_TransparentMode")); if (v == 1) return "cutout"; if (v == 2) return "transparent"; if (v != 0) throw new NotSupportedException($"Unsupported lilToon transparent mode: {v}."); } return "opaque"; }
        private static string CullMode(Material m) { if (!m.HasProperty("_Cull")) return "back"; switch (Mathf.RoundToInt(m.GetFloat("_Cull"))) { case 0: return "off"; case 1: return "front"; default: return "back"; } }
    }
}
