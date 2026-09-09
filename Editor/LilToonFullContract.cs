using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace VRVlog.LilToon
{
    // Shared verbatim with the exporter. Parsing never interprets shader code,
    // file paths, external URLs or unknown property names from an avatar.
    internal static class LilToonFullContract
    {
        internal const string ExtensionName = "VRVLOG_materials_liltoon";
        internal static void ValidatePixels(byte[] bytes, string format)
        {
            // Payloads use little-endian IEEE-754 components. Check exponents
            // directly, including alpha, without allocating a second image.
            if (format == "rgba32") return;
            var stride = format == "rgbaHalf" ? 2 : format == "rgbaFloat" ? 4 : 0;
            if (stride == 0 || bytes.Length % (stride * 4) != 0) Fail("Invalid pixel payload.");
            for (var i = 0; i < bytes.Length; i += stride)
            {
                var nonFinite = stride == 2 ? (bytes[i + 1] & 0x7c) == 0x7c :
                    (bytes[i + 3] & 0x7f) == 0x7f && (bytes[i + 2] & 0x80) != 0;
                if (nonFinite) Fail("Non-finite HDR pixel component.");
            }
        }
        internal static Dictionary<string, object> Root(Dictionary<string, object> gltf)
        {
            if (!gltf.TryGetValue("extensions", out var raw) || !(raw is Dictionary<string, object> ext) || !ext.TryGetValue(ExtensionName, out raw)) return null;
            return Object(raw);
        }
        internal static bool IsFull(Dictionary<string, object> gltf)
        {
            var root = Root(gltf);
            return root != null && Int(root, "schemaMajor") >= 2;
        }
        internal static void Validate(Dictionary<string, object> gltf, long fileLength, int binaryLength, long decodedByteBudget = long.MaxValue)
        {
            if (fileLength > 160L * 1024 * 1024) Fail("VRM exceeds 160 MiB.");
            var root = Root(gltf) ?? throw new InvalidDataException("Missing full lilToon extension.");
            Keys(root, "schemaMajor", "schemaMinor", "sourceLilToonVersion", "sourceCommit", "exporterVersion", "materials", "textures", "bindings", "chunks");
            if (Int(root, "schemaMajor") != 2 || Int(root, "schemaMinor") != 0 || Text(root, "sourceLilToonVersion") != LilToon234Catalogue.Version || Text(root, "sourceCommit") != LilToon234Catalogue.Commit)
                Fail("Unsupported full lilToon version; update the app/exporter.");
            Text(root, "exporterVersion");
            if (!List(gltf, "extensionsUsed").Contains(ExtensionName) || gltf.TryGetValue("extensionsRequired", out var required) && List(required).Contains(ExtensionName)) Fail("The lilToon extension must be declared and optional.");
            var views = List(gltf, "bufferViews"); var chunks = List(root, "chunks");
            long decodedBytes = 0;
            foreach (var raw in chunks)
            {
                var chunk = Object(raw); Keys(chunk, "bufferView", "decodedBytes", "codec");
                if (Text(chunk, "codec") != "deflate" || Int(chunk, "decodedBytes") <= 0) Fail("Invalid binary payload.");
                decodedBytes = checked(decodedBytes + Int(chunk, "decodedBytes"));
                if (decodedBytes > decodedByteBudget) throw new OutOfMemoryException("The aggregate decoded lilToon payload exceeds the device memory budget.");
                var view = Object(At(views, Int(chunk, "bufferView")));
                if (Int(view, "buffer") != 0) Fail("External buffers are not allowed.");
                var offset = view.ContainsKey("byteOffset") ? Int(view, "byteOffset") : 0;
                var length = Int(view, "byteLength");
                if (offset < 0 || length <= 0 || (long)offset + length > binaryLength) Fail("Binary payload is outside the GLB.");
            }
            var textures = List(root, "textures");
            foreach (var raw in textures)
            {
                var texture = Object(raw);
                Keys(texture, "name", "width", "height", "mips", "faces", "format", "srgb", "normal", "filter", "wrapU", "wrapV", "wrapW", "aniso", "mipBias", "chunks");
                Text(texture, "name");
                var width = Int(texture, "width"); var height = Int(texture, "height"); var mips = Int(texture, "mips"); var faces = Int(texture, "faces");
                if (width <= 0 || height <= 0 || (faces != 1 && faces != 6) || faces == 6 && width != height) Fail("Invalid texture dimensions.");
                var maxMips = 1; for (var size = Math.Max(width, height); size > 1; size >>= 1) maxMips++;
                if (mips < 1 || mips > maxMips) Fail("Invalid mip count.");
                var format = Text(texture, "format"); if (format != "rgba32" && format != "rgbaFloat" && format != "rgbaHalf") Fail("Unsupported pixel format.");
                Bool(texture, "srgb"); Bool(texture, "normal"); Number(texture, "mipBias");
                if (Int(texture, "filter") < 0 || Int(texture, "filter") > 2 || Int(texture, "aniso") < 0 || Int(texture, "aniso") > 16) Fail("Invalid texture sampling.");
                foreach (var key in new[] {"wrapU", "wrapV", "wrapW"}) if (Int(texture, key) < 0 || Int(texture, key) > 3) Fail("Invalid wrap mode.");
                var imageChunks = List(texture, "chunks");
                if (imageChunks.Count != faces * mips) Fail("Texture payload count does not match its mip chain.");
                for (var face = 0; face < faces; face++) for (var mip = 0; mip < mips; mip++)
                {
                    var expected = checked((long)Math.Max(1, width >> mip) * Math.Max(1, height >> mip) * (format == "rgba32" ? 4 : format == "rgbaHalf" ? 8 : 16));
                    var chunk = Object(At(chunks, Integer(imageChunks[face * mips + mip])));
                    if (expected != Int(chunk, "decodedBytes")) Fail("Texture payload size does not match its dimensions.");
                }
            }
            var gltfMaterials = List(gltf, "materials"); var seen = new HashSet<int>();
            foreach (var raw in List(root, "materials"))
            {
                var material = Object(raw); Keys(material, "sourceShader", "name", "materialIndex", "renderQueue", "values", "textures", "passes", "keywords", "clippingCanceller");
                Bool(material,"clippingCanceller");
                var keywords=new HashSet<string>(StringComparer.Ordinal);
                foreach(var keyword in List(material,"keywords"))if(!(keyword is string text)||text.Length>128||!keywords.Add(text))Fail("Invalid shader keyword record.");
                var id = Int(material, "materialIndex");
                if (!seen.Add(id)) Fail("Duplicate material index.");
                var fallback = Object(At(gltfMaterials, id));
                if (!Object(Get(fallback, "extensions")).ContainsKey("VRMC_materials_mtoon")) Fail("Missing portable MToon material.");
                var sourceShader=Text(material, "sourceShader");
                if (!LilToon234Catalogue.Shaders.ContainsKey(sourceShader)) Fail("Unknown official shader.");
                Text(material, "name"); var queue = Int(material, "renderQueue"); if (queue < -1 || queue > 5000) Fail("Invalid render queue.");
                var props = new HashSet<string>(StringComparer.Ordinal);
                foreach (var valueRaw in List(material, "values"))
                {
                    var value = Object(valueRaw); Keys(value, "name", "type", "value"); var name = Text(value, "name"); var type = Text(value, "type");
                    if (!props.Add(name) || !LilToon234Catalogue.Properties.TryGetValue(name, out var declaration) || declaration != type + ":runtime" || type == "texture") Fail("Unknown, duplicate or incorrectly typed property: " + name);
                    Vector(Get(value, "value"));
                }
                foreach (var textureRaw in List(material, "textures"))
                {
                    var texture = Object(textureRaw); Keys(texture, "name", "texture", "st"); var name = Text(texture, "name");
                    if (!props.Add(name) || !LilToon234Catalogue.Properties.TryGetValue(name, out var declaration) || declaration != "texture:runtime") Fail("Invalid texture property: " + name);
                    var textureId=Int(texture,"texture");
                    if(textureId != -1 && Int(Object(At(textures, textureId)),"faces")!=LilToon234Catalogue.TextureFaces[name]) Fail("Texture dimension does not match the shader property.");
                    Vector(Get(texture, "st"));
                }
                if(!props.SetEquals(LilToon234Catalogue.RequiredProperties[sourceShader]))Fail("The material property set is incomplete or does not match its official shader.");
                var passNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var passRaw in List(material, "passes"))
                {
                    var pass = Object(passRaw); Keys(pass, "name", "lightMode", "enabled"); var name = Text(pass, "name");
                    if (!passNames.Add(name)) Fail("Duplicate pass.");
                    // Unity may expose interned ShaderTagId names in uppercase.
                    if (!LilToon234Catalogue.PassLightModes[sourceShader].TryGetValue(name, out var mode) || !string.Equals(Text(pass,"lightMode"), mode, StringComparison.OrdinalIgnoreCase))
                        Fail("The pass LightMode does not match its official shader.");
                    Bool(pass, "enabled");
                }
                if(!passNames.SetEquals(LilToon234Catalogue.Passes[sourceShader]))Fail("The material pass set does not match its official shader: " + sourceShader + " [" + string.Join(",",passNames) + "].");
            }
            if (seen.Count == 0) Fail("No full lilToon materials.");
            var nodes = List(gltf, "nodes"); var meshes = List(gltf, "meshes"); var boundNodes = new HashSet<int>(); var usedMaterials = new HashSet<int>();
            foreach (var bindingRaw in List(root, "bindings"))
            {
                var binding = Object(bindingRaw); Keys(binding, "node", "mesh", "vertexCount", "materials", "uv", "vertexIds", "renderer");
                var renderer=Object(Get(binding,"renderer"));
                Keys(renderer,"receiveShadows","shadowCasting","lightProbes","reflectionProbes","probeAnchor","sortingLayer","sortingOrder","boundsCenter","boundsExtents");
                Bool(renderer,"receiveShadows");
                foreach(var key in new[]{"shadowCasting","lightProbes","reflectionProbes"}) if(Int(renderer,key)<0 || Int(renderer,key)>3)Fail("Invalid renderer state.");
                if(Int(renderer,"lightProbes")==2)Fail("A light probe proxy volume requires external environment data.");
                var anchor=Int(renderer,"probeAnchor");if(anchor != -1)At(nodes,anchor);
                Int(renderer,"sortingLayer");var order=Int(renderer,"sortingOrder");if(order<short.MinValue || order>short.MaxValue)Fail("Invalid sorting order.");
                Vector(Get(renderer,"boundsCenter"));foreach(var extent in Vector(Get(renderer,"boundsExtents")))if(extent<0)Fail("Invalid renderer bounds.");
                var nodeId = Int(binding, "node"); if (!boundNodes.Add(nodeId)) Fail("Duplicate renderer binding.");
                var node = Object(At(nodes, nodeId)); var meshId = Int(binding, "mesh"); if (Int(node, "mesh") != meshId) Fail("Node/mesh binding mismatch.");
                var primitives = List(Object(At(meshes, meshId)), "primitives"); var mats = List(binding, "materials");
                if (mats.Count != primitives.Count) Fail("Primitive/material count mismatch.");
                long importedCount = 0;
                for (var i = 0; i < mats.Count; i++)
                {
                    var primitive = Object(primitives[i]); var id = Integer(mats[i]);
                    if (id != Int(primitive, "material")) Fail("Primitive/material identity mismatch."); usedMaterials.Add(id);
                    var attributes = Object(Get(primitive, "attributes"));
                    var accessor = Object(At(List(gltf, "accessors"), Int(attributes, "POSITION")));
                    var positionCount = Int(accessor, "count");
                    if (positionCount <= 0 || Text(accessor, "type") != "VEC3" || Int(accessor, "componentType") != 5126)
                        Fail("Invalid primitive position accessor.");
                    importedCount = checked(importedCount + positionCount);
                }
                var count = Int(binding, "vertexCount"); if (count <= 0) Fail("Empty vertex binding.");
                if (count != importedCount) Fail("Vertex binding differs from the imported primitive accessor counts.");
                var expected = checked((long)count * 16);
                if (Int(Object(At(chunks, Int(binding, "vertexIds"))), "decodedBytes") != expected) Fail("Vertex ID payload mismatch.");
                var channels = new HashSet<int>();
                foreach (var uvRaw in List(binding, "uv"))
                {
                    var uv = Object(uvRaw); Keys(uv, "channel", "payload"); var channel = Int(uv, "channel");
                    if (channel < 0 || channel > 7 || !channels.Add(channel) || Int(Object(At(chunks, Int(uv, "payload"))), "decodedBytes") != expected) Fail("UV payload mismatch.");
                }
            }
            if (!seen.IsSubsetOf(usedMaterials)) Fail("Unbound full lilToon material.");
        }
        internal static object Get(Dictionary<string, object> obj, string key) => obj.TryGetValue(key, out var value) ? value : throw new InvalidDataException("Missing " + key);
        internal static Dictionary<string, object> Object(object raw) => raw as Dictionary<string, object> ?? throw new InvalidDataException("Expected object.");
        internal static List<object> List(object raw) => raw as List<object> ?? throw new InvalidDataException("Expected array.");
        internal static List<object> List(Dictionary<string, object> obj, string key) => List(Get(obj, key));
        internal static object At(List<object> array, int index) => index >= 0 && index < array.Count ? array[index] : throw new InvalidDataException("Index out of bounds.");
        internal static int Int(Dictionary<string, object> obj, string key) => Integer(Get(obj, key));
        internal static int Integer(object raw) { var value = Numeric(raw); if (value < int.MinValue || value > int.MaxValue || value != Math.Truncate(value)) Fail("Expected integer."); return (int)value; }
        internal static double Number(Dictionary<string, object> obj, string key) => Numeric(Get(obj, key));
        internal static double Numeric(object raw)
        {
            if (!(raw is long || raw is int || raw is double || raw is float || raw is decimal)) Fail("Expected number.");
            var value = Convert.ToDouble(raw, CultureInfo.InvariantCulture); if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > float.MaxValue) Fail("Non-finite number."); return value;
        }
        internal static float[] Vector(object raw) { var values = List(raw); if (values.Count != 4) Fail("Expected four components."); return new[] {(float)Numeric(values[0]), (float)Numeric(values[1]), (float)Numeric(values[2]), (float)Numeric(values[3])}; }
        internal static string Text(Dictionary<string, object> obj, string key) { var value = Get(obj, key) as string; if (value == null || value.Length > 1024) Fail("Invalid string."); return value; }
        internal static bool Bool(Dictionary<string, object> obj, string key) => Get(obj, key) is bool value ? value : throw new InvalidDataException("Expected boolean.");
        internal static void Keys(Dictionary<string, object> obj, params string[] keys) { var allowed = new HashSet<string>(keys, StringComparer.Ordinal); foreach (var key in obj.Keys) if (!allowed.Contains(key)) Fail("Unknown field: " + key); }
        static void Fail(string message) => throw new InvalidDataException(message);
    }
}
