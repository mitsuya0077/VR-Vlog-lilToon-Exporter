using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace VRVlog.LilToonExporter
{
    // Preserve every bufferView/accessor identity. Only physical BIN storage is
    // shared; consumers still create independent meshes, textures and expressions.
    internal static class GlbBinaryOptimizer
    {
        sealed class Range
        {
            internal int Offset, Length, Destination;
            internal Dictionary<string, object> View;
        }

        internal static long Compact(GlbDocument document)
        {
            if (!document.Json.TryGetValue("bufferViews", out var raw) || !(raw is List<object> views)) return 0;
            var source = document.Binary;
            var ranges = new List<Range>();
            foreach (var item in views)
            {
                var view = (Dictionary<string, object>)item;
                var offset = Number(view, "byteOffset");
                var length = Number(view, "byteLength");
                if (Number(view, "buffer") != 0 || offset < 0 || length <= 0 || (long)offset + length > source.Length)
                    throw new InvalidDataException("Cannot compact an invalid embedded bufferView.");
                ranges.Add(new Range { Offset = offset, Length = length, View = view });
            }
            // An existing overlap can carry aliasing semantics in an importer.
            // Preserve it exactly, including when this file is already compacted.
            var sorted = new List<Range>(ranges);
            sorted.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            long previousEnd = 0;
            foreach (var range in sorted)
            {
                if (range.Offset < previousEnd) return 0;
                previousEnd = (long)range.Offset + range.Length;
            }
            var writable = new HashSet<int>();
            if (document.Json.TryGetValue("accessors", out raw) && raw is List<object> accessors)
                foreach (var item in accessors)
                {
                    var accessor = (Dictionary<string, object>)item;
                    // UniGLTF applies sparse overrides to its source BIN array.
                    if (accessor.ContainsKey("sparse") && accessor.ContainsKey("bufferView"))
                        writable.Add(Number(accessor, "bufferView"));
                }
            var buckets = new Dictionary<string, List<Range>>(StringComparer.Ordinal);
            var copies = new List<Range>();
            var lengthAfter = 0;
            using var hash = SHA256.Create();
            for (var i = 0; i < ranges.Count; i++)
            {
                var range = ranges[i];
                List<Range> candidates = null;
                Range match = null;
                if (!writable.Contains(i))
                {
                    var key = range.Length + ":" + Number(range.View, "byteStride") + ":" + Number(range.View, "target") + ":" +
                        Convert.ToBase64String(hash.ComputeHash(source, range.Offset, range.Length));
                    if (!buckets.TryGetValue(key, out candidates)) buckets.Add(key, candidates = new List<Range>());
                    foreach (var candidate in candidates)
                        if (Equal(source, candidate.Offset, range.Offset, range.Length)) { match = candidate; break; }
                }
                if (match != null) range.Destination = match.Destination;
                else
                {
                    range.Destination = checked((lengthAfter + 3) & ~3);
                    lengthAfter = checked(range.Destination + range.Length);
                    copies.Add(range);
                    candidates?.Add(range);
                }
            }
            if (lengthAfter >= source.Length) return 0;
            var buffer = document.EmbeddedBuffer();
            var oldLength = buffer["byteLength"];
            var before = document.SerializedLength();
            var oldOffsets = new object[ranges.Count];
            for (var i = 0; i < ranges.Count; i++)
            {
                ranges[i].View.TryGetValue("byteOffset", out oldOffsets[i]);
                ranges[i].View["byteOffset"] = (long)ranges[i].Destination;
            }
            buffer["byteLength"] = (long)lengthAfter;
            var committed = false;
            try
            {
                var after = document.SerializedLength(lengthAfter);
                if (after >= before) return 0;
                var compacted = new byte[lengthAfter];
                foreach (var range in copies)
                    Buffer.BlockCopy(source, range.Offset, compacted, range.Destination, range.Length);
                document.ReplaceBinary(compacted);
                committed = true;
                return before - after;
            }
            finally
            {
                if (!committed)
                {
                    buffer["byteLength"] = oldLength;
                    for (var i = 0; i < ranges.Count; i++)
                        if (oldOffsets[i] == null) ranges[i].View.Remove("byteOffset");
                        else ranges[i].View["byteOffset"] = oldOffsets[i];
                }
            }
        }

        static int Number(Dictionary<string, object> value, string key) =>
            value.TryGetValue(key, out var raw) ? checked(Convert.ToInt32(raw)) : 0;
        static bool Equal(byte[] bytes, int left, int right, int length)
        {
            for (var i = 0; i < length; i++) if (bytes[left + i] != bytes[right + i]) return false;
            return true;
        }
    }
}
