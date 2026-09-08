using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // Uses the installed, version-checked lilToon baker so layer blending and
    // tone correction follow the shader that authored the avatar.
    internal static class LilToonMainTextureBaker
    {
        internal static bool NeedsBake(Material material) => Enabled(material, "_UseMain2ndTex") ||
            Enabled(material, "_UseMain3rdTex") ||
            (material.HasProperty("_MainTexHSVG") && material.GetVector("_MainTexHSVG") != new Vector4(0, 1, 1, 1)) ||
            Value(material, "_MainGradationStrength") != 0;

        internal static void Prepare(GameObject avatar, List<Material> materials, List<Texture2D> textures,
            ICollection<string> warnings, bool suppressSharedEmission, bool suppressHdrTextureEmission)
        {
            var copies = new Dictionary<Material, Material>();
            foreach (var renderer in ExportRendererSelection.Enumerate(avatar))
            {
                var slots = renderer.sharedMaterials;
                for (var i = 0; i < slots.Length; i++)
                {
                    var original = slots[i];
                    if (!LilToonMaterialReader.IsLilToon(original)) continue;
                    if (!copies.TryGetValue(original, out var copy))
                    {
                        copy = new Material(original) { name = original.name };
                        materials.Add(copy);
                        copies.Add(original, copy);
                        // Test before baking changes the main texture's identity.
                        if (LilToonEmissionPolicy.IsSuppressed(original, suppressSharedEmission) ||
                            suppressHdrTextureEmission && LilToonEmissionPolicy.HasHdrTextureEmission(original))
                        {
                            copy.SetFloat("_UseEmission", 0);
                            copy.SetFloat("_EmissionBlend", 0);
                            copy.SetColor("_EmissionColor", Color.black);
                            warnings?.Add(original.name + ": 色変化を抑える設定に従い、テクスチャ発光を省略しました。発光を残す場合は書き出し設定を解除してください。");
                        }
                        if (NeedsBake(copy)) Bake(copy, textures, warnings);
                    }
                    slots[i] = copy;
                }
                renderer.sharedMaterials = slots;
            }
        }

        internal static void Bake(Material material, List<Texture2D> textures, ICollection<string> warnings)
        {
            if (textures == null) throw new ArgumentNullException(nameof(textures));
            foreach (var layer in new[] { "2nd", "3rd" })
            {
                if (!Enabled(material, "_UseMain" + layer + "Tex")) continue;
                var property = "_Main" + layer + "Tex";
                if (Value(material, property + "_UVMode") != 0 || Value(material, property + "IsDecal") != 0 ||
                    Value(material, property + "IsLeftOnly") != 0 || Value(material, property + "IsRightOnly") != 0 ||
                    Value(material, property + "ShouldCopy") != 0 || Value(material, property + "ShouldFlipMirror") != 0 ||
                    Value(material, property + "IsMSDF") != 0 || Value(material, property + "AlphaMode") != 0)
                    throw new InvalidOperationException(material.name + ": UV0以外・左右別・デカール・特殊アルファのメインカラーは自動ベイクできません。lilToon側で焼き込んでから書き出してください。");
                var dissolve = "_Main" + layer + "DissolveParams";
                var scroll = property + "_ScrollRotate";
                if (material.HasProperty(dissolve) && material.GetVector(dissolve).x != 0 ||
                    material.HasProperty(scroll) && material.GetVector(scroll) != Vector4.zero ||
                    material.HasProperty("_MainTex_ScrollRotate") && material.GetVector("_MainTex_ScrollRotate") != Vector4.zero)
                    throw new InvalidOperationException(material.name + ": 動くメインカラーレイヤーは自動ベイクできません。静止状態を焼き込んでから書き出してください。");
                var distanceFade = "_Main" + layer + "DistanceFade";
                if (Value(material, property + "_Cull") != 0 ||
                    Value(material, "_Main" + layer + "EnableLighting", 1) != 1 ||
                    material.HasProperty(distanceFade) && material.GetVector(distanceFade).z != 0 ||
                    Enabled(material, "_UseAudioLink") && Enabled(material, "_AudioLink2Main" + layer))
                    throw new InvalidOperationException(material.name + ": 表裏・照明・距離・AudioLinkで変化するメインカラーは一枚の画像に自動ベイクできません。lilToon側で通常のレイヤーに変更してから書き出してください。");
                if (material.GetTextureScale("_MainTex") != Vector2.one || material.GetTextureOffset("_MainTex") != Vector2.zero)
                    throw new InvalidOperationException(material.name + ": メインUVを移動・拡縮した多層マテリアルはlilToon側で先に焼き込んでください。");
            }
            var shader = Shader.Find("Hidden/ltsother_baker");
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("lilToonのベイク用シェーダーを利用できません。");
            var baker = new Material(shader);
            RenderTexture target = null;
            Texture2D result = null;
            var previous = RenderTexture.active;
            var previousSrgb = GL.sRGBWrite;
            try
            {
                baker.CopyPropertiesFromMaterial(material);
                baker.shaderKeywords = new string[0];
                // Apply base tint before the layers, as lilToon does. Both
                // consumers receive white tint after it is baked into pixels.
                var width = 4;
                var height = 4;
                var inputs = new List<string> { "_MainTex", "_MainColorAdjustMask" };
                if (Enabled(material, "_UseMain2ndTex")) inputs.AddRange(new[] { "_Main2ndTex", "_Main2ndBlendMask" });
                if (Enabled(material, "_UseMain3rdTex")) inputs.AddRange(new[] { "_Main3rdTex", "_Main3rdBlendMask" });
                foreach (var name in inputs)
                {
                    var texture = material.HasProperty(name) ? material.GetTexture(name) : null;
                    if (texture == null) continue;
                    width = Mathf.Max(width, texture.width);
                    height = Mathf.Max(height, texture.height);
                }
                if (width > LilToonMobileProfile.MaximumTextureSize || height > LilToonMobileProfile.MaximumTextureSize)
                {
                    var scale = LilToonMobileProfile.MaximumTextureSize / (float)Mathf.Max(width, height);
                    width = Mathf.Max(1, Mathf.RoundToInt(width * scale));
                    height = Mathf.Max(1, Mathf.RoundToInt(height * scale));
                    warnings?.Add(material.name + ": メインカラーの焼き込み画像をモバイル向けに " + width + "×" + height + " へ縮小しました。");
                }
                target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear;
                Graphics.Blit(material.GetTexture("_MainTex") ?? Texture2D.whiteTexture, target, baker, 0);
                RenderTexture.active = target;
                result = new Texture2D(width, height, TextureFormat.RGBA32, false, false)
                {
                    name = material.name + "__VRVlog_Main_" + Guid.NewGuid().ToString("N"),
                    wrapModeU = material.GetTexture("_MainTex")?.wrapModeU ?? TextureWrapMode.Repeat,
                    wrapModeV = material.GetTexture("_MainTex")?.wrapModeV ?? TextureWrapMode.Repeat,
                    filterMode = material.GetTexture("_MainTex")?.filterMode ?? FilterMode.Bilinear
                };
                result.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                result.Apply(false, false);
                textures.Add(result);
                if (material.HasProperty("_ShadowColorTex") && material.GetTexture("_MainTex") != null &&
                    material.GetTexture("_ShadowColorTex") == material.GetTexture("_MainTex"))
                    material.SetTexture("_ShadowColorTex", result);
                material.SetTexture("_MainTex", result);
                material.SetColor("_Color", Color.white);
                material.SetFloat("_UseMain2ndTex", 0);
                material.SetFloat("_UseMain3rdTex", 0);
                material.SetVector("_MainTexHSVG", new Vector4(0, 1, 1, 1));
                material.SetFloat("_MainGradationStrength", 0);
                warnings?.Add(material.name + ": メインカラー2nd/3rd・色調補正を出力用の画像へ焼き込みました。");
                result = null; // ownership transferred to the export
            }
            finally
            {
                GL.sRGBWrite = previousSrgb;
                RenderTexture.active = previous;
                if (result != null) UnityEngine.Object.DestroyImmediate(result);
                if (target != null) RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.DestroyImmediate(baker);
            }
        }

        private static float Value(Material m, string name, float fallback = 0) => m.HasProperty(name) ? m.GetFloat(name) : fallback;
        private static bool Enabled(Material m, string name) => Value(m, name) > 0.5f;
    }
}
