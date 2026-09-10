using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace VRVlog.LilToonExporter.Tests
{
    public static class GlbBinaryFixture
    {
        static Dictionary<string, object> D(params object[] pairs)
        {
            var result = new Dictionary<string, object>();
            for (var i = 0; i < pairs.Length; i += 2) result.Add((string)pairs[i], pairs[i + 1]);
            return result;
        }
        static GlbDocument Document()
        {
            var bytes = new byte[4096];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
            // Four identical 1 KiB blocks, with the same sampler-independent data.
            for (var i = 1; i < 4; i++) Buffer.BlockCopy(bytes, 0, bytes, i * 1024, 1024);
            return GlbDocument.Create(D("asset", D("version", "2.0"),
                "buffers", new List<object> { D("byteLength", (long)bytes.Length) },
                "bufferViews", Enumerable.Range(0, 4).Select(i => (object)D("buffer", 0L, "byteOffset", i * 1024L, "byteLength", 1024L)).ToList(),
                "accessors", new List<object> { D("bufferView", 0L), D("bufferView", 1L), D("bufferView", 2L), D("bufferView", 3L) }), bytes);
        }
        static List<object> Views(GlbDocument doc) => (List<object>)doc.Json["bufferViews"];
        static int Offset(GlbDocument doc, int index) => Convert.ToInt32(((Dictionary<string, object>)Views(doc)[index])["byteOffset"]);

        public static void Run(Action<bool, string> check)
        {
            var doc = Document();
            var original = (byte[])doc.Binary.Clone();
            var accessors = doc.Json["accessors"];
            var before = doc.Write().Length;
            var saved = GlbBinaryOptimizer.Compact(doc);
            check(saved > 0 && saved == before - doc.Write().Length, "Measured savings include aligned JSON and BIN sizes");
            check(ReferenceEquals(accessors, doc.Json["accessors"]) && Views(doc).Count == 4, "All accessor and view identities survive compaction");
            for (var i = 0; i < 4; i++)
                check(original.Skip(i * 1024).Take(1024).SequenceEqual(doc.Binary.Skip(Offset(doc, i)).Take(1024)), "Every view retains exactly the same bytes");
            check(GlbBinaryOptimizer.Compact(doc) == 0, "Already shared ranges remain unchanged");
            var roundTrip = GlbDocument.Read(doc.Write());
            check(roundTrip.Binary.Take(doc.Binary.Length).SequenceEqual(doc.Binary), "Compacted storage survives GLB round trip");

            doc = Document();
            var list = (List<object>)doc.Json["accessors"];
            ((Dictionary<string, object>)list[0])["sparse"] = D("count", 1L);
            ((Dictionary<string, object>)list[1])["sparse"] = D("count", 1L);
            check(GlbBinaryOptimizer.Compact(doc) > 0, "Read-only duplicates can still be compacted beside writable ranges");
            check(Offset(doc, 0) != Offset(doc, 1) && Offset(doc, 0) != Offset(doc, 2) && Offset(doc, 1) != Offset(doc, 2), "Sparse bases never alias another view");
            doc.Binary[Offset(doc, 0)] = 255;
            check(doc.Binary[Offset(doc, 1)] != 255 && doc.Binary[Offset(doc, 2)] != 255, "Applying a sparse update cannot corrupt another accessor");

            doc = Document();
            for (var i = 0; i < 4; i++) ((Dictionary<string, object>)Views(doc)[i])["byteStride"] = 4L * (i + 1);
            check(GlbBinaryOptimizer.Compact(doc) == 0, "Different layouts do not share storage");
            doc = Document();
            ((Dictionary<string, object>)Views(doc)[1])["byteOffset"] = 512L;
            check(GlbBinaryOptimizer.Compact(doc) == 0, "Original partial overlaps preserve their aliasing semantics");

            doc = Document();
            ((Dictionary<string, object>)Views(doc)[3])["byteLength"] = long.MaxValue;
            var refused = false;
            try { GlbBinaryOptimizer.Compact(doc); } catch (OverflowException) { refused = true; }
            check(refused, "Out-of-range storage lengths are rejected");

            doc = Document();
            var offsets = doc.AppendBinaryBatch(new[] { new byte[] { 17 }, new byte[] { 23, 29 }, new byte[] { 31 } });
            check(offsets.SequenceEqual(new[] { 4096, 4100, 4104 }), "Batch append aligns each independent payload");
            check(doc.Binary[4096] == 17 && doc.Binary[4100] == 23 && doc.Binary[4101] == 29 && doc.Binary[4104] == 31, "Batch append keeps payload bytes");
            check(doc.Write().Length == doc.SerializedLength(), "Direct serialization writes the declared total length");

            var raw = new byte[65537]; new Random(11).NextBytes(raw);
            var payload = new DeflatePayload(raw);
            using var input = new MemoryStream(payload.Encoded);
            using var decoder = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(); decoder.CopyTo(output);
            check(payload.DecodedBytes == raw.Length && output.ToArray().SequenceEqual(raw), "Eager compression preserves every source byte");
        }
    }
}
