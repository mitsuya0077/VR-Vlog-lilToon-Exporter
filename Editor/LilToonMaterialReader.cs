using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    internal static class LilToonMaterialReader
    {
        private static readonly string[] FloatNames = { "_Cutoff", "_ShadowStrength", "_ShadowBorder", "_ShadowBlur", "_BacklightMainStrength", "_BacklightNormalStrength", "_BacklightBorder", "_BacklightBlur", "_BacklightDirectivity", "_BacklightViewStrength", "_BacklightReceiveShadow", "_BacklightBackfaceMask", "_BumpScale", "_EmissionBlend", "_EmissionBlendMode", "_EmissionMainStrength", "_EmissionFluorescence", "_OutlineVertexR2Width", "_RimBorder", "_RimBlur", "_RimFresnelPower", "_MatCapBlend", "_OutlineWidth", "_OutlineEnableLighting" };
        private static readonly string[] ColorNames = { "_Color", "_ShadowColor", "_BacklightColor", "_EmissionColor", "_RimColor", "_MatCapColor", "_OutlineColor" };
        private static readonly (string Name, string Semantic)[] TextureNames = {
            ("_MainTex", "mainColor"), ("_ShadowColorTex", "shadow"), ("_BumpMap", "normalMap"),
            ("_BacklightColorTex", "backlight"), ("_EmissionMap", "emission"), ("_EmissionBlendMask", "emission"), ("_OutlineWidthMask", "outline"), ("_RimColorTex", "rimLight"), ("_MatCapTex", "matCap"), ("_OutlineTex", "outline")
        };
        private static readonly string[] UnsupportedFeatureToggles = {
            "_UseMain2ndTex", "_UseMain3rdTex", "_UseAnisotropy",
            "_UseReflection", "_UseRefraction", "_UseFur", "_UseGem", "_UseAudioLink",
            "_UseDissolve", "_UseDistanceFade", "_UseGlitter", "_UseParallax", "_UseTessellation",
            "_UseEmission2nd", "_UseBump2ndMap", "_UseMatCap2nd"
        };

        public static LilToonMaterialRecord Read(Material material, int materialIndex, Func<Texture, string, int> textureIndex, ICollection<string> warnings = null, bool suppressSharedTextureEmission = false)
        {
            if (material == null || material.shader == null) throw new ArgumentException("Material and shader are required.");
            var family = ShaderFamily(material.shader.name, warnings);
            if (!LilToonMobileProfile.IsSupportedShaderFamily(family))
                throw new NotSupportedException($"Unsupported lilToon shader: {material.shader.name}.");
            foreach (var toggle in UnsupportedFeatureToggles)
                if (Enabled(material, toggle)) AddWarning(warnings, $"{material.name}: 未対応機能 {toggle} は省略し、対応部分だけを書き出しました。");
            if (EnabledOrTexture(material, "_UseEmission", "_EmissionMap"))
            {
                if ((material.HasProperty("_EmissionMap_UVMode") && material.GetFloat("_EmissionMap_UVMode") != 0) ||
                    (material.HasProperty("_EmissionMap_ScrollRotate") && !material.GetVector("_EmissionMap_ScrollRotate").Equals(new Vector4(0, 0, 0, 0))) ||
                    (material.HasProperty("_EmissionBlink") && material.GetVector("_EmissionBlink").x != 0) ||
                    Enabled(material, "_EmissionUseGrad") ||
                    (material.HasProperty("_EmissionParallaxDepth") && material.GetFloat("_EmissionParallaxDepth") != 0))
                    AddWarning(warnings, $"{material.name}: 発光の特殊UV・回転・時間変化は省略し、UV0の固定した発光として近似しました。");
                if ((HasTexture(material, "_EmissionBlendMask") && material.GetTexture("_EmissionBlendMask") != Texture2D.whiteTexture) ||
                    (material.HasProperty("_EmissionBlendMode") && material.GetFloat("_EmissionBlendMode") != 1))
                    AddWarning(warnings, $"{material.name}: 発光のマスク・合成方法は専用表示へ保存しました。標準MToonでは加算発光として近似します。");
            }
            if (HasOutline(material) && ((HasTexture(material, "_OutlineTex") && material.GetTexture("_OutlineTex") != Texture2D.whiteTexture) ||
                (material.HasProperty("_OutlineFixWidth") && material.GetFloat("_OutlineFixWidth") != 0)))
                AddWarning(warnings, $"{material.name}: 輪郭の色模様・カメラ距離による幅の補正は、単色・一定の幅として近似しました。");
            if (HasOutline(material) && material.HasProperty("_OutlineWidth") && material.GetFloat("_OutlineWidth") > .5f)
                AddWarning(warnings, $"{material.name}: 太い輪郭をモバイル表示の上限5mmへ近似します。");
            var record = new LilToonMaterialRecord {
                materialIndex = materialIndex, shaderFamily = family, renderMode = RenderMode(material, warnings),
                renderQueue = material.renderQueue, cullMode = CullMode(material)
            };
            var suppressEmission = LilToonEmissionPolicy.IsSuppressed(material, suppressSharedTextureEmission);
            if (suppressEmission && EnabledOrTexture(material, "_UseEmission", "_EmissionMap"))
                AddWarning(warnings, $"{material.name}: 白飛びを抑えるため、メイン画像と同じ画像を使う発光を省略しました。必要な場合は書き出し設定で解除できます。");
            AddFeature(record, "mainColor", true);
            AddFeature(record, "shadow", Enabled(material, "_UseShadow"));
            AddFeature(record, "backlight", Enabled(material, "_UseBacklight"));
            AddFeature(record, "normalMap", EnabledOrTexture(material, "_UseBumpMap", "_BumpMap"));
            AddFeature(record, "emission", !suppressEmission && EnabledOrTexture(material, "_UseEmission", "_EmissionMap"));
            AddFeature(record, "rimLight", Enabled(material, "_UseRim"));
            AddFeature(record, "matCap", Enabled(material, "_UseMatCap"));
            AddFeature(record, "outline", HasOutline(material));
            if (HasOutline(material) && !HasPortableOutline(material))
                AddWarning(warnings, $"{material.name}: 頂点カラーによる輪郭幅を専用表示へ保存しました。標準MToonの互換表示では輪郭線を省略します。");

            foreach (var name in FloatNames)
                if (material.HasProperty(name))
                {
                    var value = suppressEmission && name == "_EmissionBlend" ? 0f : material.GetFloat(name);
                    if (LilToonExtensionValidator.IsAppearanceProperty(name) && !LilToonExtensionValidator.ValidAppearanceValue(name, value))
                    {
                        value = name == "_EmissionBlendMode" ? 1 : 0;
                        AddWarning(warnings, $"{material.name}: {name} を既定値へ調整して書き出しました。");
                    }
                    record.floats.Add(new LilToonFloatProperty { name = name, value = value });
                }
            foreach (var property in LilToonLightingProfile.Properties)
                if (property.AppliesTo(record.features))
                {
                    var authored = material.HasProperty(property.Name) ? material.GetFloat(property.Name) : property.DefaultFor(family);
                    var normalized = LilToonLightingProfile.Normalize(property, authored, family);
                    if (normalized != authored)
                        AddWarning(warnings, $"{material.name}: {property.Name} を有効な範囲へ調整して書き出しました（元のマテリアルは変更していません）。");
                    record.floats.Add(new LilToonFloatProperty {
                        name = property.Name,
                        value = normalized
                    });
                }
            var minimum = record.floats.Find(item => item.name == "_LightMinLimit");
            var maximum = record.floats.Find(item => item.name == "_LightMaxLimit");
            if (minimum.value > maximum.value)
            {
                minimum.value = maximum.value;
                AddWarning(warnings, $"{material.name}: 明るさの下限が上限を超えていたため、書き出し用の下限を上限に合わせました。");
            }
            var direction = material.HasProperty(LilToonLightingProfile.DirectionProperty)
                ? material.GetVector(LilToonLightingProfile.DirectionProperty)
                : new Vector4(0.001f, 0.002f, 0.001f, 0f);
            if (!ValidDirection(direction))
            {
                direction = new Vector4(0.001f, 0.002f, 0.001f, 0f);
                AddWarning(warnings, $"{material.name}: 不正なライト方向を既定値へ調整して書き出しました。");
            }
            record.vectors.Add(new LilToonVectorProperty {
                name = LilToonLightingProfile.DirectionProperty,
                x = direction.x, y = direction.y, z = direction.z, w = direction.w
            });
            foreach (var name in ColorNames) if (material.HasProperty(name)) { var c = suppressEmission && name == "_EmissionColor" ? Color.black : material.GetColor(name); record.colors.Add(new LilToonColorProperty { name = name, r = c.r, g = c.g, b = c.b, a = c.a }); }
            foreach (var item in TextureNames)
            {
                if (suppressEmission && item.Semantic == "emission") continue;
                if (!TextureFeatureEnabled(material, item.Semantic)) continue;
                if (!material.HasProperty(item.Name)) continue; var texture = material.GetTexture(item.Name); if (texture == null) continue;
                if ((item.Name == "_BacklightColorTex" || item.Name == "_EmissionBlendMask" || item.Name == "_OutlineWidthMask") && texture == Texture2D.whiteTexture) continue;
                // Keep the base-image shade fallback when Unity exposes an
                // unassigned lilToon shade map as its built-in white image.
                if (item.Name == "_ShadowColorTex" && texture == Texture2D.whiteTexture) continue;
                var index = textureIndex(texture, item.Name == "_EmissionBlendMask" ? "emissionMask" : item.Name == "_OutlineWidthMask" ? "outlineMask" : item.Semantic);
                if (index < 0)
                {
                    if (item.Semantic == "mainColor") throw new InvalidOperationException($"メイン画像 '{texture.name}' を元のVRMへ対応付けできません。");
                    AddWarning(warnings, $"{material.name}: {item.Name} はVRMへ対応付けできないため省略しました。");
                    continue;
                }
                var stName = item.Name == "_OutlineWidthMask" ? "_MainTex" : item.Name;
                var scale = material.GetTextureScale(stName); var offset = material.GetTextureOffset(stName);
                record.textures.Add(new LilToonTextureProperty { name = item.Name, semantic = item.Semantic, textureIndex = index, scaleX = scale.x, scaleY = scale.y, offsetX = offset.x, offsetY = offset.y });
            }
            return record;
        }

        private static bool HasOutline(Material m) => Enabled(m, "_UseOutline") || m.shader.name.EndsWith("Outline", StringComparison.Ordinal);
        private static bool ValidDirection(Vector4 direction) =>
            !float.IsNaN(direction.x) && Math.Abs(direction.x) <= 10000f &&
            !float.IsNaN(direction.y) && Math.Abs(direction.y) <= 10000f &&
            !float.IsNaN(direction.z) && Math.Abs(direction.z) <= 10000f &&
            (direction.w == 0f || direction.w == 1f);
        internal static bool HasPortableOutline(Material m) => HasOutline(m) &&
            (!m.HasProperty("_OutlineVertexR2Width") || m.GetFloat("_OutlineVertexR2Width") == 0f);

        public static bool IsLilToon(Material material) => material != null && material.shader != null && ShaderVariant(material.shader.name).Length > 0;
        private static string ShaderFamily(string name, ICollection<string> warnings)
        {
            var variant = ShaderVariant(name);
            var family = variant.StartsWith("lilToonLite", StringComparison.OrdinalIgnoreCase) ? "lilToonLite" : variant.StartsWith("lilToonMulti", StringComparison.OrdinalIgnoreCase) ? "lilToonMulti" : variant.StartsWith("lilToon", StringComparison.OrdinalIgnoreCase) ? "lilToon" : "";
            var suffix = family.Length == 0 ? "" : variant.Substring(family.Length);
            var renderSuffix = suffix.EndsWith("Outline", StringComparison.Ordinal) ? suffix.Substring(0, suffix.Length - "Outline".Length) : suffix;
            if (family.Length == 0)
                throw new NotSupportedException($"Unsupported lilToon shader: {name}.");
            if (renderSuffix != "" && renderSuffix != "Cutout" && renderSuffix != "Transparent" && renderSuffix != "OnePassTransparent" && renderSuffix != "TwoPassTransparent")
                AddWarning(warnings, $"{name}: 特殊シェーダーは標準lilToonとして近似しました。");
            return family;
        }
        private static string ShaderVariant(string name)
        {
            var leaf = name.Substring(name.LastIndexOf('/') + 1).TrimStart();
            const string optionalPrefix = "[Optional]";
            if (leaf.StartsWith(optionalPrefix, StringComparison.OrdinalIgnoreCase)) leaf = leaf.Substring(optionalPrefix.Length).TrimStart();
            return leaf.StartsWith("lilToon", StringComparison.OrdinalIgnoreCase) ? leaf : "";
        }
        private static bool Enabled(Material m, string p) => m.HasProperty(p) && m.GetFloat(p) > 0.5f;
        private static bool EnabledOrTexture(Material m, string enable, string texture) => m.HasProperty(enable) ? Enabled(m, enable) : HasTexture(m, texture);
        private static bool HasTexture(Material m, string p) => m.HasProperty(p) && m.GetTexture(p) != null;
        private static bool TextureFeatureEnabled(Material material, string semantic)
        {
            switch (semantic)
            {
                case "mainColor": return true;
                case "shadow": return Enabled(material, "_UseShadow");
                case "backlight": return Enabled(material, "_UseBacklight");
                case "normalMap": return EnabledOrTexture(material, "_UseBumpMap", "_BumpMap");
                case "emission": return EnabledOrTexture(material, "_UseEmission", "_EmissionMap");
                case "rimLight": return Enabled(material, "_UseRim");
                case "matCap": return Enabled(material, "_UseMatCap");
                case "outline": return HasOutline(material);
                default: throw new NotSupportedException($"Unsupported texture semantic: {semantic}.");
            }
        }
        private static void AddFeature(LilToonMaterialRecord r, string name, bool enabled) { if (enabled) r.features.Add(name); }
        private static string RenderMode(Material m, ICollection<string> warnings) { var n = m.shader.name; if (n.IndexOf("Cutout", StringComparison.OrdinalIgnoreCase) >= 0) return "cutout"; if (n.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Refraction", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Gem", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Fur", StringComparison.OrdinalIgnoreCase) >= 0) return "transparent"; if (m.HasProperty("_TransparentMode")) { var v = Mathf.RoundToInt(m.GetFloat("_TransparentMode")); if (v == 1) return "cutout"; if (v == 2) return "transparent"; if (v != 0) AddWarning(warnings, $"{m.name}: 未対応の透明モード {v} は不透明として近似しました。"); } return "opaque"; }
        private static void AddWarning(ICollection<string> warnings, string message) { if (warnings != null && !warnings.Contains(message)) warnings.Add(message); }
        private static string CullMode(Material m) { if (!m.HasProperty("_Cull")) return "back"; switch (Mathf.RoundToInt(m.GetFloat("_Cull"))) { case 0: return "off"; case 1: return "front"; default: return "back"; } }
    }
}
