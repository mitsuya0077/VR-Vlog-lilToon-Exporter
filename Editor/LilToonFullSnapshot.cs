using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using VRVlog.LilToon;

namespace VRVlog.LilToonExporter
{
    // Snapshot before fallback baking. Values and texture references belong to
    // the prepared authoring result, never to its MToon approximation.
    internal sealed class LilToonFullSnapshot
    {
        readonly Dictionary<Renderer, Material[]> sources = new Dictionary<Renderer, Material[]>();
        readonly Dictionary<Material, Dictionary<string, object>> records = new Dictionary<Material, Dictionary<string, object>>();
        readonly List<object> textures = new List<object>();
        readonly Dictionary<(Texture, bool), int> textureIds = new Dictionary<(Texture, bool), int>();
        readonly List<DeflatePayload> payloads = new List<DeflatePayload>();
        readonly List<object> bindings = new List<object>();
        readonly Dictionary<int, Dictionary<string, object>> indexedRecords = new Dictionary<int, Dictionary<string, object>>();
        readonly HashSet<Texture> resizedWarnings = new HashSet<Texture>();
        ICollection<string> warnings;
        bool suppressSharedTextureEmission, suppressHdrTextureEmission;
        LilToonInactiveTextureProof inactiveTextures;

        internal static LilToonFullSnapshot Capture(GameObject avatar, bool suppressSharedTextureEmission=false, bool suppressHdrTextureEmission=false, ICollection<string> warnings=null, GameObject animationSource=null)
        {
            var result = new LilToonFullSnapshot {suppressSharedTextureEmission=suppressSharedTextureEmission, suppressHdrTextureEmission=suppressHdrTextureEmission, warnings=warnings,
                inactiveTextures=new LilToonInactiveTextureProof(avatar, animationSource)};
            foreach (var renderer in ExportRendererSelection.Enumerate(avatar))
            {
                // UniVRM exports mesh primitives, not particle/trail/line systems.
                // Their materials must not create bindings to absent glTF meshes.
                var filter = renderer.GetComponent<MeshFilter>();
                var mesh = renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : renderer is MeshRenderer && filter != null ? filter.sharedMesh : null;
                // The pinned ModelExporter omits meshes without vertices or
                // indices. They cannot have a full binding to an output mesh.
                // Keep their transforms/bones and every nonempty mesh intact.
                if(!ExportRendererSelection.HasGeometry(mesh))continue;
                var materials = renderer.sharedMaterials;
                result.sources.Add(renderer, materials);
                foreach (var material in materials)
                    if (material != null && LilToon234Catalogue.Shaders.ContainsKey(material.shader.name) && !result.records.ContainsKey(material))
                        result.records.Add(material, result.ReadMaterial(material));
            }
            if (result.records.Count == 0) throw new InvalidOperationException("公式lilToon材質が見つかりません。");
            return result;
        }

        Dictionary<string, object> ReadMaterial(Material source)
        {
            bool Suppress(bool second) => LilToonEmissionPolicy.IsSuppressed(source,suppressSharedTextureEmission,second) ||
                suppressHdrTextureEmission && LilToonEmissionPolicy.HasHdrTextureEmission(source,second);
            var suppressEmission=Suppress(false);var suppressEmission2nd=Suppress(true);
            var values = new List<object>();
            var textureProperties = new List<object>();
            var shader = source.shader;
            for (var i = 0; i < shader.GetPropertyCount(); i++)
            {
                var name = shader.GetPropertyName(i);
                if (name == "_DummyProperty") continue;
                if (!LilToon234Catalogue.Properties.TryGetValue(name, out var declaration))
                    throw new InvalidOperationException("公式2.3.4にないプロパティ: " + name);
                if (declaration.EndsWith(":editor", StringComparison.Ordinal)) continue;
                if (declaration.EndsWith(":external", StringComparison.Ordinal))
                {
                    if (name == "_UseAudioLink" && source.GetFloat(name) != 0)
                        throw ExportRecoveryFailure.At(new NotSupportedException(source.name + ": AudioLinkは外部連携のため今回の再現対象外です。"), "audio-link", source);
                    continue;
                }
                var type = shader.GetPropertyType(i);
                if (type == ShaderPropertyType.Texture)
                {
                    var texture = source.GetTexture(name);
                    var normal = (shader.GetPropertyFlags(i) & ShaderPropertyFlags.Normal) != 0;
                    // Keep ordinary authored textures, including disabled
                    // ones. Only an unsupported reference proven unreachable
                    // by the current official shader can become unassigned.
                    if (texture != null && !(texture is Texture2D) && !(texture is Cubemap) &&
                        inactiveTextures?.CanOmit(source, name) == true)
                    {
                        texture = null;
                        warnings?.Add(source.name + ": 無効なlilToonレイヤーで使われない動的画像の参照を省略しました: " + name);
                    }
                    // Unassigned normal slots use Unity's encoded bump default.
                    // Export that default too, so the canonical decoder never
                    // interprets an app-platform-specific bump texture as RG.
                    if(texture==null && normal)texture=Texture2D.normalTexture;
                    int id;
                    try { id = texture == null ? -1 : Texture(texture, normal); }
                    catch (NotSupportedException error) when (texture != null && !(texture is Texture2D) && !(texture is Cubemap) &&
                        (name.StartsWith("_Main2nd", StringComparison.Ordinal) || name.StartsWith("_Main3rd", StringComparison.Ordinal)))
                    {
                        // Keep the original error type and attribute only an
                        // actual failure in a known optional main-color layer.
                        ExportRecoveryFailure.At(error, "material-layer", source);
                        error.Data[ExportRecoveryFailure.LayerKey] = name.StartsWith("_Main2nd", StringComparison.Ordinal) ? "2nd" : "3rd";
                        throw;
                    }
                    var scale = source.GetTextureScale(name); var offset = source.GetTextureOffset(name);
                    textureProperties.Add(new Dictionary<string, object> { {"name", name}, {"texture", id}, {"st", new List<object> {scale.x, scale.y, offset.x, offset.y}} });
                }
                else
                {
                    var value = type == ShaderPropertyType.Color ? (Vector4)source.GetColor(name) :
                        type == ShaderPropertyType.Vector ? source.GetVector(name) : new Vector4(source.GetFloat(name), 0, 0, 0);
                    if(suppressEmission && (name=="_UseEmission" || name=="_EmissionBlend") ||
                        suppressEmission2nd && (name=="_UseEmission2nd" || name=="_Emission2ndBlend")) value=Vector4.zero;
                    if(suppressEmission && name=="_EmissionColor" || suppressEmission2nd && name=="_Emission2ndColor") value=(Vector4)Color.black;
                    if (!Finite(value)) throw new InvalidDataException(source.name + ": 非有限の値: " + name);
                    values.Add(new Dictionary<string, object> { {"name", name}, {"type", declaration.Split(':')[0]}, {"value", new List<object> {value.x, value.y, value.z, value.w}} });
                }
            }
            var passes = new List<object>();
            for (var i = 0; i < source.passCount; i++)
            {
                var name = source.GetPassName(i);
                var lightMode=shader.FindPassTagValue(i,new ShaderTagId("LightMode")).name;
                if(lightMode=="Never")continue;
                if (!string.IsNullOrEmpty(name) && !passes.Cast<Dictionary<string, object>>().Any(p => (string)p["name"] == name))
                    passes.Add(new Dictionary<string, object> { {"name", name}, {"lightMode",lightMode}, {"enabled", source.GetShaderPassEnabled(lightMode)} });
            }
            var settingPath=Path.GetFullPath(Path.Combine(Application.dataPath,"../ProjectSettings/lilToonSetting.json"));
            var clipping=false;
            if(File.Exists(settingPath))
            {
                var settings=JsonDom.Parse(File.ReadAllText(settingPath)) as Dictionary<string,object>;
                clipping=settings!=null && settings.TryGetValue("LIL_FEATURE_CLIPPING_CANCELLER",out var enabled) && enabled is bool on && on;
            }
            if(LilToon234Catalogue.Shaders[shader.name].StartsWith("ltsmulti",StringComparison.Ordinal))clipping=source.GetFloat("_UseClippingCanceller")!=0;
            var keywords=source.shaderKeywords.Where(k=>!(suppressEmission && k=="_EMISSION") && !(suppressEmission2nd && k=="GEOM_TYPE_BRANCH")).Cast<object>().ToList();
            return new Dictionary<string, object> { {"sourceShader", shader.name}, {"name", source.name}, {"renderQueue", source.renderQueue}, {"values", values}, {"textures", textureProperties}, {"passes", passes}, {"keywords", keywords}, {"clippingCanceller",clipping} };
        }

        int Texture(Texture source, bool normal)
        {
            if (textureIds.TryGetValue((source, normal), out var existing)) return existing;
            if (!(source is Texture2D) && !(source is Cubemap)) throw new NotSupportedException("動的テクスチャは保存できません: " + source.name);
            var chunks = new List<object>();
            var size = TextureResizePolicy.Size(source.width, source.height);
            var resized = size.Item1 != source.width || size.Item2 != source.height;
            var sourceMipCount = source is Texture2D two ? two.mipmapCount : ((Cubemap)source).mipmapCount;
            // Keep intentionally mipless inputs mipless. A resized mipmapped
            // texture needs the chain for its new dimensions, not the old chain.
            var count = resized && sourceMipCount > 1 ? LilToonFullTexture.MipCount(size.Item1, size.Item2) : sourceMipCount;
            var faces = source is Cubemap ? 6 : 1;
            // Gamma rendering can expose an UNorm GPU view of sRGB pixels.
            // Preserve the stored pixel encoding, independent of this Editor's
            // sampling mode, so a Linear-space consumer decodes it correctly.
            var srgb = !normal && source.isDataSRGB;
            var storage = StorageFormat(source.graphicsFormat, normal);
            var hdr = storage != "rgba32";
            var half = storage == "rgbaHalf";
            for (var face = 0; face < faces; face++)
                for (var mip = 0; mip < count; mip++)
                {
                    var bytes = resized
                        ? LilToonFullTexture.ReadResized(source, Math.Max(1, size.Item1 >> mip), Math.Max(1, size.Item2 >> mip), face, normal, srgb, hdr, half)
                        : LilToonFullTexture.Read(source, mip, face, normal, srgb, hdr, half);
                    chunks.Add(payloads.Count); payloads.Add(new DeflatePayload(bytes));
                }
            if (resized && resizedWarnings.Add(source))
                warnings?.Add($"lilToon画像「{source.name}」を{source.width}×{source.height}から{size.Item1}×{size.Item2}に縮小しました（読み込み時のメモリ使用量を軽減）。");
            var index = textures.Count;
            textures.Add(new Dictionary<string, object> {
                {"name", source.name}, {"width", size.Item1}, {"height", size.Item2}, {"mips", count}, {"faces", faces},
                {"format", half ? "rgbaHalf" : hdr ? "rgbaFloat" : "rgba32"}, {"srgb", srgb}, {"normal", normal},
                {"filter", (int)source.filterMode}, {"wrapU", (int)source.wrapModeU}, {"wrapV", (int)source.wrapModeV}, {"wrapW", (int)source.wrapModeW},
                {"aniso", source.anisoLevel}, {"mipBias", source.mipMapBias}, {"chunks", chunks}
            });
            textureIds.Add((source, normal), index);
            return index;
        }

        internal static string StorageFormat(UnityEngine.Experimental.Rendering.GraphicsFormat format, bool normal)
        {
            // Canonical normals need more precision than an RGBA8 copy, but
            // ordinary imported maps do not require full 32-bit components.
            // Half retains finer precision than their 8-bit source samples and
            // supports linear filtering on mobile GPUs without Float32 filtering.
            // Keep authored HDR and >8-bit normalized source precision intact.
            var name = format.ToString();
            if (name.StartsWith("R16", StringComparison.Ordinal) && name.EndsWith("_SFloat", StringComparison.Ordinal)) return "rgbaHalf";
            if (UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsHDRFormat(format)) return "rgbaFloat";
            foreach (System.Text.RegularExpressions.Match component in System.Text.RegularExpressions.Regex.Matches(name, "[RGBA]([0-9]+)"))
                if (int.Parse(component.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) > 8) return "rgbaFloat";
            // EAC decodes 11-bit components although Unity's names omit that width.
            if (normal && (format == UnityEngine.Experimental.Rendering.GraphicsFormat.R_EAC_UNorm
                || format == UnityEngine.Experimental.Rendering.GraphicsFormat.RG_EAC_UNorm
                || format == UnityEngine.Experimental.Rendering.GraphicsFormat.R_EAC_SNorm
                || format == UnityEngine.Experimental.Rendering.GraphicsFormat.RG_EAC_SNorm)) return "rgbaFloat";
            if (normal) return "rgbaHalf";
            if (name.EndsWith("_SNorm", StringComparison.Ordinal)) return "rgbaFloat";
            return "rgba32";
        }

        // Called while UniVRM's exact Unity-object/node/mesh mapping is alive.
        internal void Bind(UniVRM10.ModelExporter converter, VrmLib.Model model, UniGLTF.ExportingGltfData storage)
        {
            foreach (var entry in sources)
            {
                var renderer = entry.Key;
                var node = model.Nodes.IndexOf(converter.Nodes[renderer.gameObject]);
                if (node < 0 || node >= storage.Gltf.nodes.Count) throw new InvalidOperationException("出力nodeの対応が一致しません。");
                var filter = renderer.GetComponent<MeshFilter>();
                var mesh = renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : filter != null ? filter.sharedMesh : null;
                if (mesh == null) throw new InvalidOperationException("描画メッシュがありません。");
                // Nodes retain the exact group for this renderer, including
                // shared static meshes with different additional streams.
                var group = converter.Nodes[renderer.gameObject].MeshGroup;
                var meshIndex = model.MeshGroups.IndexOf(group);
                var gltfMesh = storage.Gltf.meshes[meshIndex];
                if (gltfMesh.primitives.Count != entry.Value.Length) throw new InvalidOperationException("材質スロットとprimitiveの対応が一致しません。");
                var materialIndices = new List<object>();
                var originalIds = new List<int>();
                for (var slot = 0; slot < entry.Value.Length; slot++)
                {
                    var index = gltfMesh.primitives[slot].material ?? throw new InvalidDataException("出力primitiveに材質番号がありません。");
                    materialIndices.Add(index);
                    if (entry.Value[slot] != null && records.TryGetValue(entry.Value[slot], out var record))
                    {
                        if (!indexedRecords.ContainsKey(index))
                        {
                            var indexed = new Dictionary<string, object>(record);
                            indexed["materialIndex"] = index;
                            indexedRecords.Add(index, indexed);
                        }
                    }
                    var used = new SortedSet<int>(mesh.GetIndices(slot));
                    if (used.Count != storage.Gltf.accessors[gltfMesh.primitives[slot].attributes.POSITION].count)
                        throw new InvalidOperationException("元頂点と出力頂点の対応が一致しません。");
                    originalIds.AddRange(used);
                }
                var attributes = new List<object>();
                for (var channel = 0; channel < 8; channel++)
                {
                    var uv = new List<Vector4>(); mesh.GetUVs(channel, uv);
                    if (uv.Count == 0) continue;
                    if (uv.Count != mesh.vertexCount) throw new InvalidDataException("UV数が一致しません。");
                    attributes.Add(new Dictionary<string, object> { {"channel", channel}, {"payload", AddVectors(originalIds.Select(id => uv[id]))} });
                }
                var ids = AddVertexIds(originalIds);
                var bounds=renderer.localBounds;
                List<object> Vec(Vector3 v)=>new List<object>{v.x,v.y,v.z,0};
                var anchor=renderer.probeAnchor;
                var anchorNode=anchor!=null && converter.Nodes.TryGetValue(anchor.gameObject,out var anchorModel)?model.Nodes.IndexOf(anchorModel):-1;
                if(anchor!=null && anchorNode<0)throw new NotSupportedException("アバター外のライトプローブ参照は環境データを必要とします。");
                var state=new Dictionary<string,object>{{"receiveShadows",renderer.receiveShadows},{"shadowCasting",(int)renderer.shadowCastingMode},
                    {"lightProbes",(int)renderer.lightProbeUsage},{"reflectionProbes",(int)renderer.reflectionProbeUsage},{"probeAnchor",anchorNode},
                    {"sortingLayer",SortingLayer.GetLayerValueFromID(renderer.sortingLayerID)},{"sortingOrder",renderer.sortingOrder},
                    {"boundsCenter",Vec(bounds.center)},{"boundsExtents",Vec(bounds.extents)}};
                bindings.Add(new Dictionary<string, object> { {"node", node}, {"mesh", meshIndex}, {"vertexCount", originalIds.Count}, {"materials", materialIndices}, {"uv", attributes}, {"vertexIds", ids}, {"renderer",state} });
            }
        }

        internal int AddVertexIds(IEnumerable<int> values)
        {
            using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
            foreach (var id in values)
            {
                if (id < 0) throw new InvalidDataException("負の元頂点ID。");
                writer.Write((uint)id);
            }
            var index = payloads.Count; payloads.Add(new DeflatePayload(bytes.ToArray())); return index;
        }

        int AddVectors(IEnumerable<Vector4> values)
        {
            using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
            foreach (var v in values) { if (!Finite(v)) throw new InvalidDataException("非有限の頂点データ。"); writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); writer.Write(v.w); }
            var index = payloads.Count; payloads.Add(new DeflatePayload(bytes.ToArray())); return index;
        }

        internal byte[] Inject(byte[] bytes, string exporterVersion, string lilToonVersion)
        {
            if (lilToonVersion != LilToon234Catalogue.Version) throw new NotSupportedException("lilToon 2.3.4が必要です。");
            var glb = GlbDocument.Read(bytes);
            var views = List(glb.Json, "bufferViews");
            var chunks = new List<object>();
            var offsets = glb.AppendBinaryBatch(payloads.Select(p => p.Encoded).ToArray());
            for (var i = 0; i < payloads.Count; i++)
            {
                var payload = payloads[i];
                chunks.Add(new Dictionary<string, object> { {"bufferView", views.Count}, {"decodedBytes", payload.DecodedBytes}, {"codec", "deflate"} });
                views.Add(new Dictionary<string, object> { {"buffer", 0}, {"byteOffset", offsets[i]}, {"byteLength", payload.Encoded.Length} });
            }
            var extension = new Dictionary<string, object> {
                {"schemaMajor", 2}, {"schemaMinor", 0}, {"sourceLilToonVersion", lilToonVersion}, {"sourceCommit", LilToon234Catalogue.Commit},
                {"exporterVersion", exporterVersion}, {"materials", indexedRecords.OrderBy(p => p.Key).Select(p => (object)p.Value).ToList()},
                {"textures", textures}, {"bindings", bindings}, {"chunks", chunks}
            };
            if (!glb.Json.TryGetValue("extensions", out var raw)) glb.Json["extensions"] = raw = new Dictionary<string, object>();
            ((Dictionary<string, object>)raw)[LilToonMobileProfile.ExtensionName] = extension;
            var used = List(glb.Json, "extensionsUsed"); if (!used.Contains(LilToonMobileProfile.ExtensionName)) used.Add(LilToonMobileProfile.ExtensionName);
            GlbBinaryOptimizer.Compact(glb);
            if (System.Text.Encoding.UTF8.GetByteCount(JsonDom.Serialize(glb.Json)) > 8 * 1024 * 1024) throw new InvalidDataException("JSONが8MiB上限を超えています。");
            var output = glb.Write();
            LilToonFullContract.Validate(glb.Json, output.LongLength, glb.Binary.Length);
            return output;
        }

        static List<object> List(Dictionary<string, object> root, string key)
        { if (!root.TryGetValue(key, out var raw)) root[key] = raw = new List<object>(); return (List<object>)raw; }
        static bool Finite(Vector4 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z) && !float.IsNaN(v.w) && !float.IsInfinity(v.w);
    }
}
