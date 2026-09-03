using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VRVlog.LilToonExporter
{
    internal sealed class GlbDocument
    {
        private const uint Magic = 0x46546C67;
        private const uint JsonChunk = 0x4E4F534A;
        private const uint BinChunk = 0x004E4942;

        public Dictionary<string, object> Json { get; private set; }
        public byte[] Binary { get; private set; }

        private GlbDocument(Dictionary<string, object> json, byte[] binary)
        {
            Json = json;
            Binary = binary ?? Array.Empty<byte>();
        }

        internal static GlbDocument Create(Dictionary<string, object> json, byte[] binary) =>
            new GlbDocument(json ?? throw new ArgumentNullException(nameof(json)), binary);

        public static GlbDocument Read(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 20) throw new InvalidDataException("GLB is truncated.");
            using var stream = new MemoryStream(bytes, false);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != Magic) throw new InvalidDataException("Not a GLB file.");
            if (reader.ReadUInt32() != 2) throw new InvalidDataException("Only GLB 2.0 is supported.");
            if (reader.ReadUInt32() != bytes.Length) throw new InvalidDataException("GLB length is invalid.");

            Dictionary<string, object> json = null;
            byte[] binary = Array.Empty<byte>();
            while (stream.Position < stream.Length)
            {
                if (stream.Length - stream.Position < 8) throw new InvalidDataException("GLB chunk header is truncated.");
                var length = reader.ReadUInt32();
                var type = reader.ReadUInt32();
                if (length > int.MaxValue || stream.Position + length > stream.Length)
                    throw new InvalidDataException("GLB chunk length is invalid.");
                var payload = reader.ReadBytes((int)length);
                if (type == JsonChunk)
                {
                    if (json != null) throw new InvalidDataException("GLB contains multiple JSON chunks.");
                    var text = Encoding.UTF8.GetString(payload).TrimEnd(' ', '\0', '\t', '\r', '\n');
                    json = JsonDom.Parse(text) as Dictionary<string, object>
                        ?? throw new InvalidDataException("GLB JSON root must be an object.");
                }
                else if (type == BinChunk)
                {
                    if (binary.Length != 0) throw new InvalidDataException("GLB contains multiple BIN chunks.");
                    binary = payload;
                }
            }
            return new GlbDocument(json ?? throw new InvalidDataException("GLB has no JSON chunk."), binary);
        }

        public byte[] Write()
        {
            var json = Pad(Encoding.UTF8.GetBytes(JsonDom.Serialize(Json)), 0x20);
            var binary = Binary.Length == 0 ? Array.Empty<byte>() : Pad(Binary, 0x00);
            var total = 12 + 8 + json.Length + (binary.Length == 0 ? 0 : 8 + binary.Length);
            using var stream = new MemoryStream(total);
            using var writer = new BinaryWriter(stream);
            writer.Write(Magic); writer.Write((uint)2); writer.Write((uint)total);
            writer.Write((uint)json.Length); writer.Write(JsonChunk); writer.Write(json);
            if (binary.Length != 0)
            {
                writer.Write((uint)binary.Length); writer.Write(BinChunk); writer.Write(binary);
            }
            return stream.ToArray();
        }

        internal int AppendBinary(byte[] payload)
        {
            if (payload == null || payload.Length == 0) throw new ArgumentException("Binary payload is required.", nameof(payload));
            if (!Json.TryGetValue("buffers", out var rawBuffers) || !(rawBuffers is List<object> buffers) || buffers.Count != 1 || !(buffers[0] is Dictionary<string, object> buffer))
                throw new InvalidDataException("GLB must contain exactly one buffer before binary data can be appended.");
            var offset = checked((Binary.Length + 3) & ~3);
            var combined = new byte[checked(offset + payload.Length)];
            Buffer.BlockCopy(Binary, 0, combined, 0, Binary.Length);
            Buffer.BlockCopy(payload, 0, combined, offset, payload.Length);
            Binary = combined;
            buffer["byteLength"] = (long)Binary.Length;
            return offset;
        }

        private static byte[] Pad(byte[] source, byte value)
        {
            var length = (source.Length + 3) & ~3;
            if (length == source.Length) return source;
            var result = new byte[length];
            Buffer.BlockCopy(source, 0, result, 0, source.Length);
            for (var i = source.Length; i < length; i++) result[i] = value;
            return result;
        }
    }
}
