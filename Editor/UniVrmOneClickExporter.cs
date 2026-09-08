using System;
using System.Collections.Generic;
using UniGLTF;
using UniVRM10;
using UnityEngine;
using VRM10.MToon10;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace VRVlog.LilToonExporter
{
    internal static class UniVrmOneClickExporter
    {
        internal const string SupportedUniVrmSeries = "0.131";

        public static byte[] Export(GameObject source, string avatarName, string author, ICollection<string> warnings = null, bool suppressSharedTextureEmission = true,
            string exporterVersion = null, string lilToonVersion = null, bool suppressHdrTextureEmission = true,
            IEnumerable<GameObject> excludedObjects = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            // Cloning detaches the avatar from its parents. Reject an inactive
            // source before the clone can accidentally become active.
            ExportRendererSelection.RequireActiveRoot(source);
            if (string.IsNullOrWhiteSpace(avatarName)) throw new InvalidOperationException("アバター名を取得できませんでした。");
            if (string.IsNullOrWhiteSpace(author)) throw new InvalidOperationException("作者名を入力してください。");

            EnsureUniVrmVersion();
            using var exclusions = new ExportObjectExclusions(source, excludedObjects);
            // Re-read the live assets on every export; a preview is never a stale
            // cached source of expression weights after the user edits a clip.
            var menu = VrChatExpressionSampler.Analyze(source, exclusions.ContainsPath);
            exclusions.FilterExpressions(menu, warnings);
            var clone = UnityEngine.Object.Instantiate(source);
            clone.name = source.name;
            var temporaryMaterials = new List<Material>();
            var temporaryMeshes = new List<Mesh>();
            var temporaryTextures = new List<Texture2D>();
            try
            {
                AvatarBaseShape.Preserve(source, clone, temporaryMeshes, warnings, exclusions.Contains);
                var expressions = VrChatExpressionBaker.Bake(source, clone, menu, temporaryMeshes, warnings);
                exclusions.Apply(clone, warnings);
                LilToonMainTextureBaker.Prepare(clone, temporaryMaterials, temporaryTextures, warnings, suppressSharedTextureEmission, suppressHdrTextureEmission);
                var preparedMaterials = new Dictionary<Renderer, Material[]>();
                foreach (var renderer in ExportRendererSelection.Enumerate(clone)) preparedMaterials.Add(renderer, renderer.sharedMaterials);
                ReplaceLilToonMaterials(clone, temporaryMaterials, temporaryTextures, warnings, suppressSharedTextureEmission);
                var exported = Vrm10Exporter.Export(
                    new GltfExportSettings(),
                    clone,
                    materialExporter: new BuiltInVrm10MaterialExporter(),
                    textureSerializer: new EditorTextureSerializer(),
                    vrmMeta: CreateMeta(avatarName.Trim(), author.Trim()));
                exported = VrmExpressionBindings.AddMissing(VrmMenuExpressions.Add(exported, expressions), warnings);
                if (exporterVersion != null)
                {
                    // Inject from the same prepared materials, while their baked
                    // images are alive. Re-reading source assets here would undo
                    // the bake in apps that enable the lilToon extension.
                    foreach (var pair in preparedMaterials) pair.Key.sharedMaterials = pair.Value;
                    exported = LilToonGlbExtension.Inject(exported, clone, exporterVersion, lilToonVersion, warnings, suppressSharedTextureEmission);
                }
                return exported;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clone);
                foreach (var material in temporaryMaterials) UnityEngine.Object.DestroyImmediate(material);
                foreach (var mesh in temporaryMeshes) UnityEngine.Object.DestroyImmediate(mesh);
                foreach (var texture in temporaryTextures) UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static void EnsureUniVrmVersion()
        {
            var package = PackageManagerPackageInfo.FindForAssembly(typeof(Vrm10Exporter).Assembly);
            var version = package != null ? package.version : null;
            if (string.IsNullOrWhiteSpace(version) || !version.StartsWith(SupportedUniVrmSeries + ".", StringComparison.Ordinal))
                throw new InvalidOperationException($"UniVRM {SupportedUniVrmSeries}.x が必要です。現在のバージョン：{version ?? "不明"}");
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

        private static void ReplaceLilToonMaterials(GameObject clone, List<Material> created, List<Texture2D> textures, ICollection<string> warnings, bool suppressSharedTextureEmission)
        {
            var converted = new Dictionary<Material, Material>();
            // Shared masks are converted once per export and owned until all
            // fallback materials have been serialized. Never cache across exports.
            var outlineMasks = new Dictionary<Texture, Texture2D>();
            foreach (var renderer in ExportRendererSelection.Enumerate(clone))
            {
                var materials = renderer.sharedMaterials;
                var changed = false;
                for (var i = 0; i < materials.Length; i++)
                {
                    var source = materials[i];
                    if (!LilToonMaterialReader.IsLilToon(source)) continue;
                    if (!converted.TryGetValue(source, out var fallback))
                    {
                        fallback = CreateMToonFallback(source, created, warnings, suppressSharedTextureEmission, textures, outlineMasks);
                        converted.Add(source, fallback);
                    }
                    materials[i] = fallback;
                    changed = true;
                }
                if (changed) renderer.sharedMaterials = materials;
            }
            if (converted.Count == 0) throw new InvalidOperationException("選択したアバターに対応するlilToonマテリアルがありません。");
        }

        internal static Material CreateMToonFallback(Material source, List<Material> created, ICollection<string> warnings, bool suppressSharedTextureEmission = true, List<Texture2D> textures = null, IDictionary<Texture, Texture2D> outlineMasks = null)
        {
            // Validate the full mobile subset before producing any fallback output.
            LilToonMaterialReader.Read(source, 0, (_, __) => 0, warnings, suppressSharedTextureEmission);
            var shader = Shader.Find(MToon10Meta.UnityShaderName);
            if (shader == null) throw new InvalidOperationException("UniVRMのMToon10シェーダーを利用できません。");
            var material = new Material(shader) { name = source.name };
            created.Add(material);
            var shadowEnabled = source.HasProperty("_UseShadow") && source.GetFloat("_UseShadow") > 0.5f;
            var backlightEnabled = source.HasProperty("_UseBacklight") && source.GetFloat("_UseBacklight") > 0.5f;
            var normalEnabled = EnabledOrTexture(source, "_UseBumpMap", "_BumpMap");
            var emissionEnabled = EnabledOrTexture(source, "_UseEmission", "_EmissionMap") &&
                !LilToonEmissionPolicy.IsSuppressed(source, suppressSharedTextureEmission);
            var matcapEnabled = source.HasProperty("_UseMatCap") && source.GetFloat("_UseMatCap") > 0.5f;
            var rimEnabled = source.HasProperty("_UseRim") && source.GetFloat("_UseRim") > 0.5f;
            var outlineEnabled = LilToonMaterialReader.HasPortableOutline(source);
            var shadeTexture = Texture(source, "_ShadowColorTex");
            if (shadeTexture == Texture2D.whiteTexture) shadeTexture = null;
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
                // An unset MToon shade texture is white, not the base image.
                ShadeColorTexture = shadowEnabled
                    ? shadeTexture ?? Texture(source, "_MainTex")
                    : Texture(source, "_MainTex"),
                NormalTexture = normalEnabled ? Texture(source, "_BumpMap") : null,
                NormalTextureScale = normalEnabled ? Float(source, "_BumpScale", 1f) : 0f,
                EmissiveFactorLinear = emissionEnabled ? Color(source, "_EmissionColor", UnityEngine.Color.black).linear : UnityEngine.Color.black,
                EmissiveTexture = emissionEnabled ? Texture(source, "_EmissionMap") : null,
                MatcapColorFactorSrgb = matcapEnabled ? MobileMaterialMath.MatcapColor(Color(source, "_MatCapColor", UnityEngine.Color.white), Float(source, "_MatCapBlend", 1f)) : UnityEngine.Color.black,
                MatcapTexture = matcapEnabled ? Texture(source, "_MatCapTex") : null,
                // MToon has no directional backlight. When lilToon rim light is
                // unused, its parametric rim is the closest portable fallback.
                ParametricRimColorFactorSrgb = rimEnabled
                    ? Color(source, "_RimColor", UnityEngine.Color.black)
                    : backlightEnabled ? Color(source, "_BacklightColor", UnityEngine.Color.black) : UnityEngine.Color.black,
                ParametricRimFresnelPowerFactor = Mathf.Max(0f, rimEnabled
                    ? Float(source, "_RimFresnelPower", 1f)
                    : backlightEnabled ? Float(source, "_BacklightDirectivity", 5f) : 1f),
                RimMultiplyTexture = rimEnabled ? Texture(source, "_RimColorTex") : null,
                OutlineWidthMode = outlineEnabled ? MToon10OutlineMode.World : MToon10OutlineMode.None,
                // lilToon stores this UI value in centimetre-like units and
                // multiplies it by 0.01 in the outline vertex shader. MToon10
                // world-coordinate width is already expressed in metres.
                OutlineWidthFactor = Mathf.Max(0f, Float(source, "_OutlineWidth", 0f)) * 0.01f,
                // lilToon's _OutlineTex colors the outline; MToon's texture is
                // a green-channel width mask, so they are not interchangeable.
                OutlineWidthMultiplyTexture = outlineEnabled ? OutlineMaskTexture.Create(Texture(source, "_OutlineWidthMask"), textures, outlineMasks) : null,
                OutlineColorFactorSrgb = Color(source, "_OutlineColor", UnityEngine.Color.black),
                OutlineLightingMixFactor = Mathf.Clamp01(Float(source, "_OutlineEnableLighting", 0f)),
            };
            if (source.HasProperty("_MainTex"))
            {
                // lilToon 2.3.4 also samples its NoScaleOffset width mask with
                // uvMain (_MainTex_ST), not a separate _OutlineWidthMask_ST.
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
            if (shaderName.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                shaderName.IndexOf("Refraction", StringComparison.OrdinalIgnoreCase) >= 0 ||
                shaderName.IndexOf("Gem", StringComparison.OrdinalIgnoreCase) >= 0 ||
                shaderName.IndexOf("Fur", StringComparison.OrdinalIgnoreCase) >= 0) return MToon10AlphaMode.Transparent;
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
