using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VRVlog.LilToonExporter
{
    // Existing-fallback imports already contain normalized glTF image channels.
    // Resize their output buffer, preserving mesh bytes and texture references.
    internal static class GlbTextureDownsizer
    {
        private sealed class Use
        {
            internal Dictionary<string, object> Info;
            internal int Texture;
            internal bool Srgb;
        }
        private static readonly HashSet<string> ColorProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "baseColorTexture", "emissiveTexture", "shadeMultiplyTexture", "matcapTexture",
            "rimMultiplyTexture", "specularColorTexture", "sheenColorTexture", "diffuseTexture"
        };

        internal static GlbDocument Resize(GlbDocument glb, ICollection<string> warnings = null, Func<byte[], bool, byte[]> encode = null)
        {
            var images = List(glb.Json, "images");
            if (images == null || images.Count == 0) return glb;
            var views = List(glb.Json, "bufferViews");
            if (views == null) return glb; // The normal validator reports missing embedded data.
            var textures = List(glb.Json, "textures") ?? new List<object>();
            var uses = new List<Use>();
            CollectUses(List(glb.Json, "materials"), uses);
            var originalImageCount = images.Count;
            var originalTextureCount = textures.Count;
            var originalViewCount = views.Count;
            var changedViews = new Dictionary<int, byte[]>();
            var oldImageViews = new HashSet<int>();
            encode = encode ?? MobileTextureEncoder.ResizeEncoded;
            for (var imageIndex = 0; imageIndex < originalImageCount; imageIndex++)
            {
                var image = images[imageIndex] as Dictionary<string, object>;
                if (image == null || !image.TryGetValue("bufferView", out var rawView)) continue;
                var viewIndex = Convert.ToInt32(rawView);
                if (viewIndex < 0 || viewIndex >= originalViewCount) throw new InvalidOperationException("Image bufferView is invalid.");
                var view = (Dictionary<string, object>)views[viewIndex];
                var offset = Number(view, "byteOffset", 0); var length = Number(view, "byteLength", -1);
                if (Number(view, "buffer", 0) != 0 || offset < 0 || length <= 0 || (long)offset + length > glb.Binary.Length)
                    throw new InvalidOperationException("Image bufferView range is invalid.");
                var size = LilToonGlbExtension.ImageSize(glb.Binary, offset, length);
                var target = TextureResizePolicy.Size(size.Item1, size.Item2);
                if (target.Item1 == size.Item1 && target.Item2 == size.Item2) continue;
                var payload = new byte[length];
                Buffer.BlockCopy(glb.Binary, offset, payload, 0, length);
                var expectedMime = payload[0] == 0x89 ? "image/png" : "image/jpeg";
                if (!image.TryGetValue("mimeType", out var mime) || !string.Equals(mime as string, expectedMime, StringComparison.Ordinal))
                    throw new InvalidOperationException("Fallback image MIME type does not match its encoded payload.");
                var sourceTextures = Enumerable.Range(0, originalTextureCount).Where(t =>
                    textures[t] is Dictionary<string, object> texture && Number(texture, "source", -1) == imageIndex).ToArray();
                var color = sourceTextures.Any(t => uses.Any(u => u.Texture == t && u.Srgb));
                var linear = sourceTextures.Any(t => !uses.Any(u => u.Texture == t) || uses.Any(u => u.Texture == t && !u.Srgb));
                // A standalone image (for example a VRM thumbnail) is color.
                if (sourceTextures.Length == 0) color = true;
                var variants = new Dictionary<bool, int>();
                foreach (var srgb in new[] { true, false })
                {
                    if (srgb ? !color : !linear) continue;
                    var png = encode(payload, srgb);
                    var encodedSize = LilToonGlbExtension.ImageSize(png, 0, png.Length);
                    if (png[0] != 0x89 || encodedSize.Item1 != target.Item1 || encodedSize.Item2 != target.Item2)
                        throw new InvalidOperationException("Resized texture dimensions or encoding are invalid.");
                    var outputImage = new Dictionary<string, object>(image);
                    outputImage["mimeType"] = "image/png";
                    outputImage["bufferView"] = (long)views.Count;
                    outputImage.Remove("uri");
                    changedViews.Add(views.Count, png);
                    views.Add(new Dictionary<string, object> { { "buffer", 0L }, { "byteLength", (long)png.Length } });
                    if (variants.Count == 0) { images[imageIndex] = outputImage; variants.Add(srgb, imageIndex); }
                    else { variants.Add(srgb, images.Count); images.Add(outputImage); }
                }
                foreach (var textureIndex in sourceTextures)
                {
                    var texture = (Dictionary<string, object>)textures[textureIndex];
                    var textureUses = uses.Where(u => u.Texture == textureIndex).ToArray();
                    var useColor = textureUses.Any(u => u.Srgb);
                    var useLinear = textureUses.Length == 0 || textureUses.Any(u => !u.Srgb);
                    texture["source"] = (long)variants[useColor];
                    if (useColor && useLinear)
                    {
                        // One image/texture can serve both color and numeric
                        // channels. Separate only this oversized shared use.
                        var numericTexture = new Dictionary<string, object>(texture) { ["source"] = (long)variants[false] };
                        var numericIndex = textures.Count;
                        textures.Add(numericTexture);
                        foreach (var use in textureUses.Where(u => !u.Srgb)) use.Info["index"] = (long)numericIndex;
                    }
                }
                oldImageViews.Add(viewIndex);
                MobileTextureEncoder.Warn(warnings, image.TryGetValue("name", out var name) ? name as string ?? "VRM画像" : "VRM画像",
                    size.Item1, size.Item2, target.Item1, target.Item2);
            }
            if (changedViews.Count == 0) return glb;
            // BufferView extensions can contain independent binary offsets
            // (for example EXT_meshopt_compression). Keep those opaque bytes
            // and offsets intact; only ordinary views can be safely repacked.
            if (views.Take(originalViewCount).Cast<Dictionary<string, object>>().Any(view =>
                view.TryGetValue("extensions", out var raw) && raw is Dictionary<string, object> extensions && extensions.Count > 0))
            {
                foreach (var pair in changedViews)
                {
                    var view = (Dictionary<string, object>)views[pair.Key];
                    view["byteOffset"] = (long)glb.AppendBinary(pair.Value);
                }
                return glb;
            }
            var referencedViews = new HashSet<int>();
            CollectViewReferences(glb.Json, referencedViews);
            using var binary = new MemoryStream();
            for (var index = 0; index < views.Count; index++)
            {
                var view = (Dictionary<string, object>)views[index];
                byte[] payload;
                if (!changedViews.TryGetValue(index, out payload))
                {
                    if (oldImageViews.Contains(index) && !referencedViews.Contains(index)) payload = new byte[1];
                    else
                    {
                        var offset = Number(view, "byteOffset", 0); var length = Number(view, "byteLength", -1);
                        if (Number(view, "buffer", 0) != 0 || offset < 0 || length < 0 || (long)offset + length > glb.Binary.Length)
                            throw new InvalidOperationException("BufferView range is invalid while resizing textures.");
                        payload = new byte[length];
                        Buffer.BlockCopy(glb.Binary, offset, payload, 0, length);
                    }
                }
                while (binary.Position % 4 != 0) binary.WriteByte(0);
                view["byteOffset"] = binary.Position;
                view["byteLength"] = (long)payload.Length;
                binary.Write(payload, 0, payload.Length);
            }
            var buffers = List(glb.Json, "buffers");
            if (buffers == null || buffers.Count != 1 || !(buffers[0] is Dictionary<string, object> buffer))
                throw new InvalidOperationException("GLB must contain exactly one buffer.");
            buffer["byteLength"] = binary.Length;
            return GlbDocument.Create(glb.Json, binary.ToArray());
        }

        private static void CollectUses(object value, List<Use> uses)
        {
            if (value is List<object> array) foreach (var item in array) CollectUses(item, uses);
            else if (value is Dictionary<string, object> dictionary)
                foreach (var pair in dictionary)
                {
                    if (pair.Key.EndsWith("Texture", StringComparison.Ordinal) && pair.Value is Dictionary<string, object> info && info.TryGetValue("index", out var index))
                        uses.Add(new Use { Info = info, Texture = Convert.ToInt32(index), Srgb = ColorProperties.Contains(pair.Key) });
                    else CollectUses(pair.Value, uses);
                }
        }
        private static void CollectViewReferences(object value, HashSet<int> references)
        {
            if (value is List<object> array) foreach (var item in array) CollectViewReferences(item, references);
            else if (value is Dictionary<string, object> dictionary)
                foreach (var pair in dictionary)
                {
                    if (pair.Key == "bufferView") references.Add(Convert.ToInt32(pair.Value));
                    else if (pair.Key != "bufferViews") CollectViewReferences(pair.Value, references);
                }
        }
        private static List<object> List(Dictionary<string, object> value, string name) => value.TryGetValue(name, out var raw) ? raw as List<object> : null;
        private static int Number(Dictionary<string, object> value, string name, int fallback) => value.TryGetValue(name, out var raw) ? Convert.ToInt32(raw) : fallback;
    }
}
