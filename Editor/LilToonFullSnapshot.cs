using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
        readonly List<byte[]> payloads = new List<byte[]>();
        readonly List<object> bindings = new List<object>();
        readonly Dictionary<int, Dictionary<string, object>> indexedRecords = new Dictionary<int, Dictionary<string, object>>();

        internal static LilToonFullSnapshot Capture(GameObject avatar)
        {
            var result = new LilToonFullSnapshot();
            foreach (var renderer in ExportRendererSelection.Enumerate(avatar))
            {
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
                        throw new NotSupportedException(source.name + ": AudioLinkは外部連携のため今回の再現対象外です。");
                    continue;
                }
                var type = shader.GetPropertyType(i);
                if (type == ShaderPropertyType.Texture)
                {
                    var texture = source.GetTexture(name);
                    var normal = (shader.GetPropertyFlags(i) & ShaderPropertyFlags.Normal) != 0;
                    // Unassigned normal slots use Unity's encoded bump default.
                    // Export that default too, so the canonical decoder never
                    // interprets an app-platform-specific bump texture as RG.
                    if(texture==null && normal)texture=Texture2D.normalTexture;
                    var id = texture == null ? -1 : Texture(texture, normal);
                    var scale = source.GetTextureScale(name); var offset = source.GetTextureOffset(name);
                    textureProperties.Add(new Dictionary<string, object> { {"name", name}, {"texture", id}, {"st", new List<object> {scale.x, scale.y, offset.x, offset.y}} });
                }
                else
                {
                    var value = type == ShaderPropertyType.Color ? (Vector4)source.GetColor(name) :
                        type == ShaderPropertyType.Vector ? source.GetVector(name) : new Vector4(source.GetFloat(name), 0, 0, 0);
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
            return new Dictionary<string, object> { {"sourceShader", shader.name}, {"name", source.name}, {"renderQueue", source.renderQueue}, {"values", values}, {"textures", textureProperties}, {"passes", passes}, {"keywords", source.shaderKeywords.Cast<object>().ToList()}, {"clippingCanceller",clipping} };
        }

        int Texture(Texture source, bool normal)
        {
            if (textureIds.TryGetValue((source, normal), out var existing)) return existing;
            if (!(source is Texture2D) && !(source is Cubemap)) throw new NotSupportedException("動的テクスチャは保存できません: " + source.name);
            var chunks = new List<object>();
            var count = source is Texture2D two ? two.mipmapCount : ((Cubemap)source).mipmapCount;
            var faces = source is Cubemap ? 6 : 1;
            var srgb = !normal && UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(source.graphicsFormat);
            var hdr = UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsHDRFormat(source.graphicsFormat);
            var half = hdr && source.graphicsFormat.ToString().StartsWith("R16",StringComparison.Ordinal);
            for (var face = 0; face < faces; face++)
                for (var mip = 0; mip < count; mip++)
                {
                    var bytes = LilToonFullTexture.Read(source, mip, face, normal, srgb, hdr, half);
                    chunks.Add(payloads.Count); payloads.Add(bytes);
                }
            var index = textures.Count;
            textures.Add(new Dictionary<string, object> {
                {"name", source.name}, {"width", source.width}, {"height", source.height}, {"mips", count}, {"faces", faces},
                {"format", half ? "rgbaHalf" : hdr ? "rgbaFloat" : "rgba32"}, {"srgb", srgb}, {"normal", normal},
                {"filter", (int)source.filterMode}, {"wrapU", (int)source.wrapModeU}, {"wrapV", (int)source.wrapModeV}, {"wrapW", (int)source.wrapModeW},
                {"aniso", source.anisoLevel}, {"mipBias", source.mipMapBias}, {"chunks", chunks}
            });
            textureIds.Add((source, normal), index);
            return index;
        }

        // Called while UniVRM's exact Unity-object/node/mesh mapping is alive.
        internal void Bind(UniVRM10.ModelExporter converter, VrmLib.Model model, UniGLTF.ExportingGltfData storage)
        {
            foreach (var entry in sources)
            {
                var renderer = entry.Key;
                var node = model.Nodes.IndexOf(converter.Nodes[renderer.gameObject]);
                if (node < 0 || node >= storage.Gltf.nodes.Count) throw new InvalidOperationException("出力nodeの対応が一致しません。");
                var mesh = renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) throw new InvalidOperationException("描画メッシュがありません。");
                var group = converter.Meshes[mesh];
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
                var ids = AddVectors(originalIds.Select(id => new Vector4(id, 0, 0, 0)));
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

        int AddVectors(IEnumerable<Vector4> values)
        {
            using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
            foreach (var v in values) { if (!Finite(v)) throw new InvalidDataException("非有限の頂点データ。"); writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); writer.Write(v.w); }
            var index = payloads.Count; payloads.Add(bytes.ToArray()); return index;
        }

        internal byte[] Inject(byte[] bytes, string exporterVersion, string lilToonVersion)
        {
            if (lilToonVersion != LilToon234Catalogue.Version) throw new NotSupportedException("lilToon 2.3.4が必要です。");
            var glb = GlbDocument.Read(bytes);
            var views = List(glb.Json, "bufferViews");
            var chunks = new List<object>();
            foreach (var payload in payloads)
            {
                using var compressed = new MemoryStream();
                using (var encoder = new DeflateStream(compressed, System.IO.Compression.CompressionLevel.Optimal, true)) encoder.Write(payload, 0, payload.Length);
                var encoded = compressed.ToArray();
                var offset = glb.AppendBinary(encoded);
                chunks.Add(new Dictionary<string, object> { {"bufferView", views.Count}, {"decodedBytes", payload.Length}, {"codec", "deflate"} });
                views.Add(new Dictionary<string, object> { {"buffer", 0}, {"byteOffset", offset}, {"byteLength", encoded.Length} });
            }
            var extension = new Dictionary<string, object> {
                {"schemaMajor", 2}, {"schemaMinor", 0}, {"sourceLilToonVersion", lilToonVersion}, {"sourceCommit", LilToon234Catalogue.Commit},
                {"exporterVersion", exporterVersion}, {"materials", indexedRecords.OrderBy(p => p.Key).Select(p => (object)p.Value).ToList()},
                {"textures", textures}, {"bindings", bindings}, {"chunks", chunks}
            };
            if (!glb.Json.TryGetValue("extensions", out var raw)) glb.Json["extensions"] = raw = new Dictionary<string, object>();
            ((Dictionary<string, object>)raw)[LilToonMobileProfile.ExtensionName] = extension;
            var used = List(glb.Json, "extensionsUsed"); if (!used.Contains(LilToonMobileProfile.ExtensionName)) used.Add(LilToonMobileProfile.ExtensionName);
            if (System.Text.Encoding.UTF8.GetByteCount(JsonDom.Serialize(glb.Json)) > 8 * 1024 * 1024) throw new InvalidDataException("JSONが8MiB上限を超えています。");
            var output = glb.Write();
            if (output.LongLength > 160L * 1024 * 1024) throw new InvalidDataException("VRMが160MiB上限を超えています。画像を自動縮小せず書き出しを中止しました。");
            LilToonFullContract.Validate(glb.Json, output.LongLength, glb.Binary.Length);
            return output;
        }

        static List<object> List(Dictionary<string, object> root, string key)
        { if (!root.TryGetValue(key, out var raw)) root[key] = raw = new List<object>(); return (List<object>)raw; }
        static bool Finite(Vector4 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z) && !float.IsNaN(v.w) && !float.IsInfinity(v.w);
    }
}
