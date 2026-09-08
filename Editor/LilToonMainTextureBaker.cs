using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
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

        internal static void ValidateAvatar(GameObject avatar, Func<Transform, bool> excluded = null, MaterialBakeOptions options = null)
        {
            var issues = new List<MaterialBakeIssue>();
            foreach (var renderer in ExportRendererSelection.Enumerate(avatar))
            {
                if (excluded?.Invoke(renderer.transform) == true) continue;
                var path = AnimationUtility.CalculateTransformPath(renderer.transform, avatar.transform);
                if (string.IsNullOrEmpty(path)) path = avatar.name;
                var slots = renderer.sharedMaterials;
                for (var slot = 0; slot < slots.Length; slot++)
                {
                    var material = slots[slot];
                    if (!LilToonMaterialReader.IsLilToon(material) || !NeedsBake(material)) continue;
                    foreach (var layer in new[] { "2nd", "3rd" })
                    {
                        if (!Enabled(material, "_UseMain" + layer + "Tex") || options?.Omits(material, layer) == true) continue;
                        issues.AddRange(LayerIssues(material, layer, path));
                        var property = "_Main" + layer + "Tex";
                        // A decal or copied half is not periodic. Repeating a
                        // baked UV tile outside 0..1 would invent extra decals.
                        if ((Enabled(material, property + "IsDecal") || Enabled(material, property + "ShouldCopy") ||
                            Enabled(material, property + "ShouldFlipCopy")) && !HasSingleUvTile(renderer, slot))
                            issues.Add(Issue(material, path, layer, "模様を置くUVの範囲",
                                "このメッシュのUVが0〜1の範囲に収まることを確認できず、模様が繰り返される可能性があります。",
                                "模様を残すには、メッシュのUV配置に対応した焼き込みが必要です。省略して続行する場合は、このレイヤーの模様が出力されません。"));
                    }
                }
            }
            if (issues.Count > 0) throw new MaterialBakeException(issues, true);
        }

        private static bool HasSingleUvTile(Renderer renderer, int slot)
        {
            var mesh = renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null || mesh.subMeshCount == 0) return false;
            try
            {
                var uv = mesh.uv;
                var indices = mesh.GetIndices(Mathf.Min(slot, mesh.subMeshCount - 1));
                if (indices.Length == 0) return false;
                return indices.All(index => index >= 0 && index < uv.Length &&
                    uv[index].x >= 0 && uv[index].x <= 1 && uv[index].y >= 0 && uv[index].y <= 1);
            }
            catch (UnityException) { return false; }
        }

        internal static void Prepare(GameObject avatar, List<Material> materials, List<Texture2D> textures,
            ICollection<string> warnings, bool suppressSharedEmission, bool suppressHdrTextureEmission, MaterialBakeOptions options = null)
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
                        foreach (var layer in new[] { "2nd", "3rd" })
                            if (options?.Omits(original, layer) == true && Enabled(copy, "_UseMain" + layer + "Tex"))
                            {
                                copy.SetFloat("_UseMain" + layer + "Tex", 0);
                                warnings?.Add(original.name + ": 確認した内容に従い、メインカラー" + layer + "の模様・文字を出力用コピーから省略しました。");
                            }
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
            var issues = new[] { "2nd", "3rd" }.Where(layer => Enabled(material, "_UseMain" + layer + "Tex"))
                .SelectMany(layer => LayerIssues(material, layer, "")).ToArray();
            if (issues.Length > 0) throw new MaterialBakeException(issues);
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
                if (width > LilToonMobileProfile.DefaultMaximumTextureSize || height > LilToonMobileProfile.DefaultMaximumTextureSize)
                {
                    var scale = LilToonMobileProfile.DefaultMaximumTextureSize / (float)Mathf.Max(width, height);
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

        private static IEnumerable<MaterialBakeIssue> LayerIssues(Material material, string layer, string path)
        {
            var property = "_Main" + layer + "Tex";
            var uvMode = Value(material, property + "_UVMode");
            if (uvMode != 0)
                yield return Issue(material, path, layer, "UV Mode: " + (uvMode == 4 ? "MatCap" : "UV" + uvMode),
                    uvMode == 4 ? "見る方向によって模様の位置が変わる設定です。" : "この模様は、メイン画像とは別のUV（画像の配置）を使っています。",
                    "この配置を保つ自動変換は未対応です。模様が不要なら、このレイヤーを出力用コピーだけで省略できます。");
            foreach (var side in new[] { ("IsLeftOnly", "左側のみ"), ("IsRightOnly", "右側のみ"), ("ShouldFlipMirror", "ミラー側を反転") })
                if (Enabled(material, property + side.Item1))
                    yield return Issue(material, path, layer, side.Item2,
                        "メッシュの向きに応じて模様を出し分けています。一枚の画像として焼くと左右が変わる可能性があります。",
                        "左右の出し分けを保つ変換は未対応です。このレイヤーを省略して出力することはできます。");
            // The opaque shader does not execute its stored layer-alpha mode.
            // The official baker does not implement layer alpha for cutout or
            // transparent shaders, even though lilToon's editor copies the value.
            if (Value(material, property + "AlphaMode") != 0 && !IsOpaque(material))
                yield return Issue(material, path, layer, "透明度の合成: " + Value(material, property + "AlphaMode"),
                    "このレイヤーで服や顔の透明度も変更しています。通常の色の焼き込みだけでは透明部分を再現できません。",
                    "透明度を保つ変換は未対応です。省略すると、このレイヤーによる透明度の変更も出力されません。");

            var atlas = Vector(material, property + "DecalAnimation", new Vector4(1, 1, 1, 30));
            // Live lilToon interprets z as a fixed frame index when fps is zero;
            // its static baker always samples frame zero. Default one-frame
            // decals are safe, including custom cell scaling/border parameters.
            if (!Finite(atlas.x) || !Finite(atlas.y) || !Finite(atlas.z) || !Finite(atlas.w) ||
                atlas.x < 1 || atlas.y < 1 || atlas.z < 0 ||
                !(atlas.w != 0 && atlas.z >= 1 && atlas.z < 2 || atlas.w == 0 && atlas.z < 1))
                yield return Issue(material, path, layer, "デカールのコマ設定: " + atlas,
                    "アニメーションまたは先頭以外のコマが設定されています。通常の焼き込みでは先頭のコマになってしまいます。",
                    "現在のコマを保つ変換は未対応です。このレイヤーを省略して出力することはできます。");
            var scroll = Vector(material, property + "_ScrollRotate", Vector4.zero);
            // z is unused on 2nd/3rd; their fixed angle is TexAngle.
            if (scroll.x != 0 || scroll.y != 0 || scroll.w != 0)
                yield return Issue(material, path, layer, "模様のスクロール・回転速度: " + scroll,
                    "時間によって模様が移動または回転する設定です。",
                    "一枚の画像では動きを残せません。このレイヤーを省略して出力することはできます。");
            if (Vector(material, "_Main" + layer + "DissolveParams", Vector4.zero).x != 0)
                yield return Issue(material, path, layer, "Dissolve（模様を徐々に消す効果）",
                    "このレイヤーに溶解・消失の効果が設定されています。",
                    "効果を保つ自動変換は未対応です。このレイヤーを省略して出力することはできます。");
            // Matching the material's culling removes only faces the material
            // already hides, so it adds no separate layer-dependent appearance.
            if (Value(material, property + "_Cull") != 0 && Value(material, property + "_Cull") != Value(material, "_Cull", 2))
                yield return Issue(material, path, layer, "表示する面: " + (Value(material, property + "_Cull") == 1 ? "裏面のみ" : "表面のみ"),
                    "表と裏で模様の表示を分ける設定です。",
                    "表裏の違いを保つ変換は未対応です。このレイヤーを省略して出力することはできます。");
            if (Value(material, "_Main" + layer + "EnableLighting", 1) != 1)
                yield return Issue(material, path, layer, "ライティングの適用: " + Value(material, "_Main" + layer + "EnableLighting", 1),
                    "この模様だけ光の影響を変える設定です。メイン画像に混ぜると明るさが変わります。",
                    "照明の違いを保つ変換は未対応です。このレイヤーを省略して出力することはできます。");
            if (Vector(material, "_Main" + layer + "DistanceFade", Vector4.zero).z != 0)
                yield return Issue(material, path, layer, "距離フェード",
                    "カメラからの距離によって模様の濃さが変わります。",
                    "一枚の画像では距離による変化を残せません。このレイヤーを省略して出力することはできます。");
            if (Enabled(material, "_UseAudioLink") && Enabled(material, "_AudioLink2Main" + layer))
                yield return Issue(material, path, layer, "AudioLink",
                    "音に合わせて模様が変化する設定です。",
                    "一枚の画像では音による変化を残せません。このレイヤーを省略して出力することはできます。");
            var mainScroll = Vector(material, "_MainTex_ScrollRotate", Vector4.zero);
            if (mainScroll != Vector4.zero || material.GetTextureScale("_MainTex") != Vector2.one ||
                material.GetTextureOffset("_MainTex") != Vector2.zero)
                yield return Issue(material, path, layer, "メイン画像の移動・拡縮・回転",
                    "メイン画像と追加の模様で画像の配置が異なるため、そのまま混ぜると位置がずれます。",
                    "位置を保つ多層焼き込みは未対応です。追加レイヤーを省略して出力することはできます。");
        }

        private static MaterialBakeIssue Issue(Material material, string path, string layer, string setting, string reason, string nextStep) =>
            new MaterialBakeIssue(material, path, layer, setting, reason, nextStep);

        private static bool IsOpaque(Material material)
        {
            if (material.GetTag("RenderType", false, "") != "Opaque" || Value(material, "_TransparentMode") != 0) return false;
            var name = material.shader != null ? material.shader.name : "";
            foreach (var variant in new[] { "Cutout", "Transparent", "Refraction", "Gem", "Fur" })
                if (name.IndexOf(variant, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            foreach (var keyword in new[] { "UNITY_UI_CLIP_RECT", "UNITY_UI_ALPHACLIP", "_ALPHATEST_ON", "_ALPHABLEND_ON", "_ALPHAPREMULTIPLY_ON" })
                if (material.IsKeywordEnabled(keyword)) return false;
            return true;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static Vector4 Vector(Material material, string name, Vector4 fallback) =>
            material.HasProperty(name) ? material.GetVector(name) : fallback;
        private static float Value(Material m, string name, float fallback = 0) => m.HasProperty(name) ? m.GetFloat(name) : fallback;
        private static bool Enabled(Material m, string name) => Value(m, name) > 0.5f;
    }
}
