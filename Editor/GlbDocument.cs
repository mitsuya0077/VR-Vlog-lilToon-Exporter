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
            var json = Encoding.UTF8.GetBytes(JsonDom.Serialize(Json));
            var jsonLength = Align(json.Length);
            var binaryLength = Align(Binary.Length);
            var total = checked(20 + jsonLength + (binaryLength == 0 ? 0 : 8 + binaryLength));
            var result = new byte[total];
            using var stream = new MemoryStream(result, true);
            using var writer = new BinaryWriter(stream);
            writer.Write(Magic); writer.Write((uint)2); writer.Write((uint)total);
            writer.Write((uint)jsonLength); writer.Write(JsonChunk); writer.Write(json);
            for (var i = json.Length; i < jsonLength; i++) writer.Write((byte)0x20);
            if (binaryLength != 0)
            {
                writer.Write((uint)binaryLength); writer.Write(BinChunk); writer.Write(Binary);
            }
            return result;
        }

        internal int SerializedLength(int? binaryLength = null)
        {
            var binary = Align(binaryLength ?? Binary.Length);
            return checked(20 + Align(Encoding.UTF8.GetByteCount(JsonDom.Serialize(Json))) + (binary == 0 ? 0 : 8 + binary));
        }

        internal Dictionary<string, object> EmbeddedBuffer()
        {
            if (!Json.TryGetValue("buffers", out var rawBuffers) || !(rawBuffers is List<object> buffers) || buffers.Count != 1 || !(buffers[0] is Dictionary<string, object> buffer))
                throw new InvalidDataException("GLB must contain exactly one buffer before binary data can be appended.");
            return buffer;
        }

        internal void ReplaceBinary(byte[] bytes)
        {
            EmbeddedBuffer()["byteLength"] = (long)bytes.Length;
            Binary = bytes;
        }

        internal int AppendBinary(byte[] payload) => AppendBinaryBatch(new[] { payload })[0];

        internal int[] AppendBinaryBatch(IReadOnlyList<byte[]> payloads)
        {
            EmbeddedBuffer();
            var offsets = new int[payloads.Count];
            var length = Binary.Length;
            for (var i = 0; i < payloads.Count; i++)
            {
                var payload = payloads[i];
                if (payload == null || payload.Length == 0) throw new ArgumentException("Binary payload is required.", nameof(payloads));
                offsets[i] = Align(length);
                length = checked(offsets[i] + payload.Length);
            }
            var combined = new byte[length];
            Buffer.BlockCopy(Binary, 0, combined, 0, Binary.Length);
            for (var i = 0; i < payloads.Count; i++)
                Buffer.BlockCopy(payloads[i], 0, combined, offsets[i], payloads[i].Length);
            ReplaceBinary(combined);
            return offsets;
        }

        private static int Align(int length) => checked((length + 3) & ~3);
    }
}
