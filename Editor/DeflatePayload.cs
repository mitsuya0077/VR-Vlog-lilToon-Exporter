using System.IO;
using System.IO.Compression;

namespace VRVlog.LilToonExporter
{
    internal sealed class DeflatePayload
    {
        internal byte[] Encoded { get; }
        internal int DecodedBytes { get; }

        internal DeflatePayload(byte[] bytes)
        {
            DecodedBytes = bytes.Length;
            using var compressed = new MemoryStream();
            using (var encoder = new DeflateStream(compressed, CompressionLevel.Optimal, true))
                encoder.Write(bytes, 0, bytes.Length);
            Encoded = compressed.ToArray();
        }
    }
}
