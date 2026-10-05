using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UniGLTF;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using F = VRVlog.LilToon.LilToonFullContract;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter
{
    // This reader operates only on the exported bytes and their indexed glTF
    // import. It never obtains materials or geometry from the source avatar.
    internal static class ExportVrmLilToonPreview
    {
        internal static IDisposable Apply(byte[] exportedBytes, GameObject imported)
        {
            if (imported == null) throw new ArgumentNullException(nameof(imported));
            var document = GlbDocument.Read(exportedBytes);
            F.Validate(document.Json, exportedBytes.LongLength, document.Binary.Length, 1024L * 1024 * 1024);
            var root = F.Root(document.Json);
            var instance = imported.GetComponent<RuntimeGltfInstance>();
            if (instance == null) throw Invalid("読み込んだVRMのnode対応を取得できません。");
            var owner = new Resources();
            try
            {
                var textures = F.List(root, "textures").Select(F.Object)
                    .Select(spec => ReadTexture(document, root, spec, owner)).ToArray();
                var materials = new Dictionary<int, Material>();
                foreach (var spec in F.List(root, "materials").Select(F.Object))
                    materials.Add(F.Int(spec, "materialIndex"), ReadMaterial(spec, textures, owner));
                var bindings = F.List(root, "bindings").Select(F.Object).ToArray();
                var order = bindings.Select(spec => F.Object(F.Get(spec, "renderer")))
                    .Select(spec => (Layer: F.Int(spec, "sortingLayer"), Order: F.Int(spec, "sortingOrder")))
                    .Distinct().OrderBy(value => value.Layer).ThenBy(value => value.Order).ToList();
                if (order.Count > 65536) throw Invalid("描画順の数がUnityの範囲を超えています。");
                foreach (var spec in bindings)
                {
                    var node = F.Int(spec, "node");
                    if (node < 0 || node >= instance.Nodes.Count || instance.Nodes[node] == null)
                        throw Invalid("読み込んだVRMに対応するnodeがありません。");
                    var renderer = instance.Nodes[node].GetComponent<Renderer>();
                    if (renderer == null) throw Invalid("読み込んだVRMに対応するRendererがありません。");
                    var filter = renderer.GetComponent<MeshFilter>();
                    var original = renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : filter != null ? filter.sharedMesh : null;
                    if (original == null || original.vertexCount != F.Int(spec, "vertexCount"))
                        throw Invalid("読み込んだVRMの頂点数がlilToon拡張の記録と一致しません。");
                    var slots = renderer.sharedMaterials;
                    var ids = F.List(spec, "materials");
                    if (slots.Length != ids.Count || original.subMeshCount != ids.Count)
                        throw Invalid("読み込んだVRMの材質スロットがlilToon拡張の記録と一致しません。");
                    for (var index = 0; index < slots.Length; index++)
                        if (materials.TryGetValue(F.Integer(ids[index]), out var material)) slots[index] = material;
                    var vertexIds = ReadChunk(document, root, F.Int(spec, "vertexIds"));
                    var identity = true;
                    using (var input = new BinaryReader(new MemoryStream(vertexIds, false)))
                        for (var index = 0; index < original.vertexCount; index++)
                        {
                            var id = input.ReadUInt32();
                            if (id > int.MaxValue) throw Invalid("元頂点IDが範囲外です。");
                            identity &= id == index;
                        }
                    if (!identity)
                        foreach (var material in slots.Where(value => value != null && materials.Values.Contains(value)))
                            RequireIndependentVertexIds(material);
                    var mesh = owner.Own(Object.Instantiate(original));
                    mesh.name = original.name;
                    for (var channel = 0; channel < 8; channel++) mesh.SetUVs(channel, new List<Vector4>());
                    foreach (var uv in F.List(spec, "uv").Select(F.Object))
                        mesh.SetUVs(F.Int(uv, "channel"), ReadVectors(ReadChunk(document, root, F.Int(uv, "payload"))));
                    var state = F.Object(F.Get(spec, "renderer"));
                    var anchor = F.Int(state, "probeAnchor");
                    if (anchor >= instance.Nodes.Count || anchor >= 0 && instance.Nodes[anchor] == null)
                        throw Invalid("ライトプローブの参照nodeがありません。");
                    var rank = order.IndexOf((F.Int(state, "sortingLayer"), F.Int(state, "sortingOrder"))) + short.MinValue;
                    owner.Changes.Add(new Change(renderer, mesh, slots, State.Read(state, anchor < 0 ? null : instance.Nodes[anchor], rank)));
                }
                // Do not publish a partly restored model if any record fails.
                foreach (var change in owner.Changes) change.Apply();
                return owner;
            }
            catch { owner.Dispose(); throw; }
        }

        static Texture ReadTexture(GlbDocument document, Dictionary<string, object> root, Dictionary<string, object> spec, Resources owner)
        {
            var width = F.Int(spec, "width"); var height = F.Int(spec, "height");
            var mips = F.Int(spec, "mips"); var faces = F.Int(spec, "faces");
            var srgb = F.Bool(spec, "srgb"); var normal = F.Bool(spec, "normal");
            var formatName = F.Text(spec, "format");
            var format = formatName == "rgba32" ? TextureFormat.RGBA32 : formatName == "rgbaHalf" ? TextureFormat.RGBAHalf : TextureFormat.RGBAFloat;
            if (width > SystemInfo.maxTextureSize || height > SystemInfo.maxTextureSize || faces == 6 && width > SystemInfo.maxCubemapSize)
                throw Invalid("VRM内の画像がこのEditorのGPU上限を超えています。");
            if (normal && (srgb || faces != 1)) throw Invalid("法線画像の色空間または次元が不正です。");
            var graphicsFormat = GraphicsFormatUtility.GetGraphicsFormat(format, srgb);
            if (!SystemInfo.IsFormatSupported(graphicsFormat, FormatUsage.Sample))
                throw Invalid("このEditorではVRM内の画像形式を表示できません: " + formatName);
            Texture texture = faces == 1
                ? owner.Own(new Texture2D(width, height, format, mips, !srgb))
                : owner.Own(new Cubemap(width, graphicsFormat, mips > 1 ? TextureCreationFlags.MipChain : TextureCreationFlags.None, mips));
            var chunks = F.List(spec, "chunks");
            for (var face = 0; face < faces; face++)
                for (var mip = 0; mip < mips; mip++)
                {
                    var pixels = ReadChunk(document, root, F.Integer(chunks[face * mips + mip]));
                    F.ValidatePixels(pixels, formatName);
                    if (texture is Texture2D two) two.SetPixelData(pixels, mip);
                    else ((Cubemap)texture).SetPixelData(pixels, mip, (CubemapFace)face);
                }
            if (texture is Texture2D image) image.Apply(false, true); else ((Cubemap)texture).Apply(false, true);
            if (normal)
            {
                var canonical = (Texture2D)texture;
                texture = EncodeNormal(canonical, format, mips, owner);
                Object.DestroyImmediate(canonical);
            }
            texture.name = F.Text(spec, "name");
            texture.filterMode = (FilterMode)F.Int(spec, "filter");
            texture.wrapModeU = (TextureWrapMode)F.Int(spec, "wrapU");
            texture.wrapModeV = (TextureWrapMode)F.Int(spec, "wrapV");
            texture.wrapModeW = (TextureWrapMode)F.Int(spec, "wrapW");
            texture.anisoLevel = F.Int(spec, "aniso"); texture.mipMapBias = (float)F.Number(spec, "mipBias");
            return texture;
        }

        static Texture2D EncodeNormal(Texture2D canonical, TextureFormat format, int mips, Resources owner)
        {
            var shader = Shader.Find("Hidden/VRVlog/PreviewNormalEncode");
            if (shader == null || !shader.isSupported) throw Invalid("法線画像を専用プレビュー用に復元するシェーダーを利用できません。");
            var output = owner.Own(new Texture2D(canonical.width, canonical.height, format, mips, true));
            var targetFormat = format == TextureFormat.RGBAFloat ? RenderTextureFormat.ARGBFloat :
                format == TextureFormat.RGBAHalf ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
            if (!SystemInfo.SupportsRenderTextureFormat(targetFormat)) throw Invalid("このEditorのGPUでは法線画像の精度を保持できません。");
            var material = new Material(shader);
            var previous = RenderTexture.active; var write = GL.sRGBWrite;
            try
            {
                GL.sRGBWrite = false;
                for (var mip = 0; mip < mips; mip++)
                {
                    var width = Math.Max(1, canonical.width >> mip); var height = Math.Max(1, canonical.height >> mip);
                    var target = RenderTexture.GetTemporary(width, height, 0, targetFormat, RenderTextureReadWrite.Linear);
                    Texture2D pixels = null;
                    try
                    {
                        material.SetFloat("_Mip", mip);
                        Graphics.Blit(canonical, target, material);
                        RenderTexture.active = target;
                        pixels = new Texture2D(width, height, format, false, true);
                        pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                        pixels.Apply(false, false);
                        output.SetPixelData(pixels.GetRawTextureData(), mip);
                    }
                    finally
                    {
                        RenderTexture.active = previous;
                        RenderTexture.ReleaseTemporary(target);
                        if (pixels != null) Object.DestroyImmediate(pixels);
                    }
                }
                output.Apply(false, true);
                return output;
            }
            finally
            {
                RenderTexture.active = previous; GL.sRGBWrite = write;
                Object.DestroyImmediate(material);
            }
        }

        static Material ReadMaterial(Dictionary<string, object> spec, Texture[] textures, Resources owner)
        {
            var source = F.Text(spec, "sourceShader");
            var shader = Shader.Find(source);
            if (shader == null || !shader.isSupported) throw Invalid("保存されたlilToonシェーダーをこのEditorで利用できません: " + source);
            var material = owner.Own(new Material(shader));
            material.name = F.Text(spec, "name");
            foreach (var value in F.List(spec, "values").Select(F.Object))
            {
                var name = F.Text(value, "name"); var data = F.Vector(F.Get(value, "value"));
                if (!material.HasProperty(name)) throw Invalid("インストール済みlilToonに必要な設定がありません: " + name);
                switch (F.Text(value, "type"))
                {
                    case "color": material.SetColor(name, new Color(data[0], data[1], data[2], data[3])); break;
                    case "vector": material.SetVector(name, new Vector4(data[0], data[1], data[2], data[3])); break;
                    default: material.SetFloat(name, data[0]); break;
                }
            }
            foreach (var texture in F.List(spec, "textures").Select(F.Object))
            {
                var name = F.Text(texture, "name"); var id = F.Int(texture, "texture");
                var st = F.Vector(F.Get(texture, "st"));
                if (!material.HasProperty(name)) throw Invalid("インストール済みlilToonに必要な画像設定がありません: " + name);
                material.SetTexture(name, id < 0 ? null : textures[id]);
                material.SetTextureScale(name, new Vector2(st[0], st[1]));
                material.SetTextureOffset(name, new Vector2(st[2], st[3]));
            }
            material.shaderKeywords = F.List(spec, "keywords").Cast<string>().ToArray();
            material.renderQueue = F.Int(spec, "renderQueue");
            foreach (var pass in F.List(spec, "passes").Select(F.Object))
            {
                var name = F.Text(pass, "name"); var index = material.FindPass(name);
                if (index < 0) throw Invalid("インストール済みlilToonに描画パスがありません: " + name);
                var mode = shader.FindPassTagValue(index, new ShaderTagId("LightMode")).name;
                if (!string.Equals(mode, F.Text(pass, "lightMode"), StringComparison.OrdinalIgnoreCase))
                    throw Invalid("インストール済みlilToonの描画方式が保存時と一致しません: " + name);
                material.SetShaderPassEnabled(mode, F.Bool(pass, "enabled"));
            }
            if (!VRVlog.LilToon.LilToon234Catalogue.Shaders[source].StartsWith("ltsmulti", StringComparison.Ordinal))
            {
                var path = Path.GetFullPath(Path.Combine(Application.dataPath, "../ProjectSettings/lilToonSetting.json"));
                var settings = File.Exists(path) ? JsonDom.Parse(File.ReadAllText(path)) as Dictionary<string, object> : null;
                var clipping = settings != null && settings.TryGetValue("LIL_FEATURE_CLIPPING_CANCELLER", out var raw) && raw is bool enabled && enabled;
                if (clipping != F.Bool(spec, "clippingCanceller"))
                    throw Invalid("クリッピングキャンセラーのEditor設定がVRMの記録と異なるため、専用表示を再現できません。");
            }
            return material;
        }

        static void RequireIndependentVertexIds(Material material)
        {
            float Value(string name) => material.HasProperty(name) ? material.GetFloat(name) : 0;
            var usesId = Value("_IDMaskFrom") < 0 || Value("_IDMaskFrom") > 7;
            var mask = Enumerable.Range(1, 8).Any(index => Value("_IDMask" + index) != 0 ||
                Value("_IDMaskControlsDissolve") != 0 && Value("_IDMaskPrior" + index) != 0);
            if (usesId && mask)
                throw Invalid("元頂点IDを使うID Maskは、このEditorの専用プレビューでは再現できません。");
            if (material.shader.name.IndexOf("Fur", StringComparison.OrdinalIgnoreCase) >= 0 && Value("_FurRandomize") != 0)
                throw Invalid("元頂点IDを使うFurのランダム形状は、このEditorの専用プレビューでは再現できません。");
        }

        static byte[] ReadChunk(GlbDocument document, Dictionary<string, object> root, int index)
        {
            var chunk = F.Object(F.At(F.List(root, "chunks"), index));
            var view = F.Object(F.At(F.List(document.Json, "bufferViews"), F.Int(chunk, "bufferView")));
            var offset = view.ContainsKey("byteOffset") ? F.Int(view, "byteOffset") : 0;
            var bytes = new byte[F.Int(chunk, "decodedBytes")];
            using var input = new MemoryStream(document.Binary, offset, F.Int(view, "byteLength"), false);
            try
            {
                using var decoder = new DeflateStream(input, CompressionMode.Decompress);
                var read = 0;
                while (read < bytes.Length)
                {
                    var count = decoder.Read(bytes, read, bytes.Length - read);
                    if (count == 0) throw Invalid("VRMの圧縮データが途中で切れています。");
                    read += count;
                }
                if (decoder.ReadByte() != -1) throw Invalid("VRMの圧縮データが記録された長さを超えています。");
                return bytes;
            }
            catch (IOException exception)
            {
                // Mono reports malformed deflate input as IOException. Keep
                // the low-level details for diagnostics, with a readable error.
                throw new InvalidDataException("VRMの圧縮データを読み込めません。", exception);
            }
        }

        static List<Vector4> ReadVectors(byte[] bytes)
        {
            var result = new List<Vector4>(bytes.Length / 16);
            using var reader = new BinaryReader(new MemoryStream(bytes, false));
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                var value = new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                for (var component = 0; component < 4; component++)
                    if (float.IsNaN(value[component]) || float.IsInfinity(value[component])) throw Invalid("VRMに非有限のUVがあります。");
                result.Add(value);
            }
            return result;
        }

        static InvalidDataException Invalid(string message) => new InvalidDataException(message);

        sealed class Resources : IDisposable
        {
            readonly List<Object> objects = new List<Object>();
            internal readonly List<Change> Changes = new List<Change>();
            bool disposed;
            internal T Own<T>(T item) where T : Object
            { objects.Add(item); item.hideFlags = HideFlags.HideAndDontSave; return item; }
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                try { foreach (var change in Changes) change.Restore(); }
                finally
                {
                    for (var index = objects.Count - 1; index >= 0; index--)
                        if (objects[index] != null) Object.DestroyImmediate(objects[index]);
                    objects.Clear(); Changes.Clear();
                }
            }
        }

        sealed class Change
        {
            readonly Renderer renderer;
            readonly Mesh mesh, originalMesh;
            readonly Material[] materials, originalMaterials;
            readonly State state, originalState;
            readonly MaterialPropertyBlock originalProperties = new MaterialPropertyBlock();
            readonly MaterialPropertyBlock[] originalSlotProperties;
            internal Change(Renderer renderer, Mesh mesh, Material[] materials, State state)
            {
                this.renderer = renderer; this.mesh = mesh; this.materials = materials; this.state = state;
                originalMesh = renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : renderer.GetComponent<MeshFilter>().sharedMesh;
                originalMaterials = renderer.sharedMaterials; originalState = State.Capture(renderer);
                renderer.GetPropertyBlock(originalProperties);
                originalSlotProperties = new MaterialPropertyBlock[originalMaterials.Length];
                for (var index = 0; index < originalSlotProperties.Length; index++)
                {
                    originalSlotProperties[index] = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(originalSlotProperties[index], index);
                }
            }
            internal void Apply()
            {
                SetMesh(mesh); renderer.sharedMaterials = materials;
                renderer.SetPropertyBlock(null);
                for (var index = 0; index < materials.Length; index++) renderer.SetPropertyBlock(null, index);
                state.Apply(renderer);
            }
            internal void Restore()
            {
                if (renderer == null) return;
                SetMesh(originalMesh); renderer.sharedMaterials = originalMaterials;
                renderer.SetPropertyBlock(originalProperties);
                for (var index = 0; index < originalSlotProperties.Length; index++) renderer.SetPropertyBlock(originalSlotProperties[index], index);
                originalState.Apply(renderer);
            }
            void SetMesh(Mesh value)
            {
                if (renderer is SkinnedMeshRenderer skin) skin.sharedMesh = value;
                else
                {
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (filter != null) filter.sharedMesh = value;
                }
            }
        }

        sealed class State
        {
            bool receive;
            ShadowCastingMode casting;
            LightProbeUsage light;
            ReflectionProbeUsage reflection;
            Transform anchor;
            int layer, order;
            Bounds bounds;
            internal static State Capture(Renderer renderer) => new State {
                receive = renderer.receiveShadows, casting = renderer.shadowCastingMode,
                light = renderer.lightProbeUsage, reflection = renderer.reflectionProbeUsage,
                anchor = renderer.probeAnchor, layer = renderer.sortingLayerID, order = renderer.sortingOrder, bounds = renderer.localBounds
            };
            internal static State Read(Dictionary<string, object> spec, Transform anchor, int order)
            {
                var center = F.Vector(F.Get(spec, "boundsCenter")); var extents = F.Vector(F.Get(spec, "boundsExtents"));
                return new State {
                    receive = F.Bool(spec, "receiveShadows"), casting = (ShadowCastingMode)F.Int(spec, "shadowCasting"),
                    light = (LightProbeUsage)F.Int(spec, "lightProbes"), reflection = (ReflectionProbeUsage)F.Int(spec, "reflectionProbes"),
                    anchor = anchor, layer = 0, order = order,
                    bounds = new Bounds(new Vector3(center[0], center[1], center[2]), new Vector3(extents[0], extents[1], extents[2]) * 2)
                };
            }
            internal void Apply(Renderer renderer)
            {
                renderer.receiveShadows = receive; renderer.shadowCastingMode = casting;
                renderer.lightProbeUsage = light; renderer.reflectionProbeUsage = reflection;
                renderer.probeAnchor = anchor; renderer.sortingLayerID = layer; renderer.sortingOrder = order; renderer.localBounds = bounds;
            }
        }
    }
}
