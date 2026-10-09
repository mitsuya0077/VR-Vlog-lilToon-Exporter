using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VRVlog.LilToonExporter.LanTransfer
{
    // RFC 7518 A256CBC-HS512: MAC_KEY is the first half of the 64-byte CEK,
    // ENC_KEY the second. Authentication includes the length of AAD in bits.
    internal sealed class CloudEncryptedSnapshot : IDisposable
    {
        internal const string WireName = "encrypted-avatar.bin";
        private readonly object gate = new object();
        private readonly List<FileStream> readers = new List<FileStream>();
        private FileStream master;
        private byte[] key;
        internal string SnapshotPath { get; }
        internal long Size { get; }
        internal string FileHash { get; }
        internal bool HasKey { get { lock (gate) return key != null; } }
        private CloudEncryptedSnapshot(string path, FileStream file, byte[] key, long size, string hash)
        { SnapshotPath = path; master = file; this.key = key; Size = size; FileHash = hash; }

        internal static long WireSize(long plaintextSize)
        {
            if (plaintextSize < 1 || plaintextSize > CloudTransferProtocol.MaximumSize) throw CloudTransferProtocol.Invalid();
            return 56 + 16 * (plaintextSize / 16 + 1);
        }

        internal static byte[] Aad(string name, long size, string hash)
        {
            if (!CloudTransferProtocol.IsHex(hash, 64)) throw CloudTransferProtocol.Invalid();
            WireSize(size);
            var nameBytes = new UTF8Encoding(false, true).GetBytes(name);
            using (var output = new MemoryStream())
            {
                var label = Encoding.ASCII.GetBytes("VRVlog-Transfer-v3:A256CBC-HS512");
                output.Write(label, 0, label.Length);
                WriteBigEndian(output, (ulong)size, 8);
                for (var i = 0; i < hash.Length; i += 2) output.WriteByte(Convert.ToByte(hash.Substring(i, 2), 16));
                WriteBigEndian(output, (uint)nameBytes.Length, 4);
                output.Write(nameBytes, 0, nameBytes.Length);
                return output.ToArray();
            }
        }

        private static void WriteBigEndian(Stream output, ulong value, int bytes)
        { for (var i = bytes - 1; i >= 0; i--) output.WriteByte((byte)(value >> (8 * i))); }

        internal static async Task<CloudEncryptedSnapshot> CreateAsync(CloudVrmTransferSource source, CancellationToken cancellation, byte[] testKey = null, byte[] testIv = null, Func<Stream> testReader = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), "VRVlogEncryptedTransfers");
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".bin");
            var cek = new byte[64]; var iv = new byte[16]; var aesKey = new byte[32]; var macKey = new byte[32];
            var buffer = new byte[64 * 1024];
            FileStream file = null;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (testKey != null && testKey.Length != 64 || testIv != null && testIv.Length != 16) throw CloudTransferProtocol.Invalid();
                using (var random = RandomNumberGenerator.Create()) { random.GetBytes(cek); random.GetBytes(iv); }
                if (testKey != null) Buffer.BlockCopy(testKey, 0, cek, 0, 64);
                if (testIv != null) Buffer.BlockCopy(testIv, 0, iv, 0, 16);
                Buffer.BlockCopy(cek, 0, macKey, 0, 32); Buffer.BlockCopy(cek, 32, aesKey, 0, 32);
                var aad = Aad(source.Name, source.Size, source.FileHash);
                Directory.CreateDirectory(directory);
                file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, buffer.Length, FileOptions.SequentialScan);
                var magic = Encoding.ASCII.GetBytes("VRVLOGE3"); file.Write(magic, 0, magic.Length); file.Write(iv, 0, iv.Length);
                using (var reader = testReader != null ? testReader() : source.OpenRead())
                using (cancellation.Register(() => reader.Dispose()))
                using (var plainHash = SHA256.Create())
                using (var mac = new HMACSHA512(macKey))
                using (var aes = Aes.Create())
                {
                    aes.KeySize = 256; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7; aes.Key = aesKey; aes.IV = iv;
                    mac.TransformBlock(aad, 0, aad.Length, null, 0); mac.TransformBlock(iv, 0, iv.Length, null, 0);
                    using (var authenticated = new AuthenticationStream(file, mac))
                    using (var encryptor = aes.CreateEncryptor())
                    using (var encrypted = new CryptoStream(authenticated, encryptor, CryptoStreamMode.Write, true))
                    {
                        long consumed = 0; int count;
                        while ((count = await reader.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) != 0)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            consumed += count; if (consumed > source.Size) throw CloudTransferProtocol.Invalid();
                            plainHash.TransformBlock(buffer, 0, count, null, 0);
                            encrypted.Write(buffer, 0, count);
                        }
                        plainHash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        if (consumed != source.Size || LanTransferProtocol.Hex(plainHash.Hash) != source.FileHash) throw CloudTransferProtocol.Invalid();
                        cancellation.ThrowIfCancellationRequested(); encrypted.FlushFinalBlock();
                    }
                    using (var length = new MemoryStream())
                    {
                        WriteBigEndian(length, (ulong)aad.Length * 8, 8);
                        mac.TransformFinalBlock(length.ToArray(), 0, 8);
                    }
                    file.Write(mac.Hash, 0, 32);
                }
                cancellation.ThrowIfCancellationRequested();
                if (file.Length != WireSize(source.Size)) throw CloudTransferProtocol.Invalid();
                file.Position = 0;
                string hash;
                using (var digest = SHA256.Create())
                {
                    int count;
                    while ((count = await file.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) != 0)
                    { cancellation.ThrowIfCancellationRequested(); digest.TransformBlock(buffer, 0, count, null, 0); }
                    digest.TransformFinalBlock(Array.Empty<byte>(), 0, 0); hash = LanTransferProtocol.Hex(digest.Hash);
                }
                cancellation.ThrowIfCancellationRequested(); file.Position = 0;
                var result = new CloudEncryptedSnapshot(path, file, cek, file.Length, hash);
                file = null; cek = null;
                return result;
            }
            catch { file?.Dispose(); LanVrmTransferServer.TryDelete(path); throw; }
            finally
            {
                if (cek != null) Array.Clear(cek, 0, cek.Length);
                Array.Clear(iv, 0, iv.Length); Array.Clear(aesKey, 0, aesKey.Length); Array.Clear(macKey, 0, macKey.Length); Array.Clear(buffer, 0, buffer.Length);
            }
        }

        internal string KeyForQr()
        { lock (gate) return key != null ? LanTransferProtocol.Base64Url(key) : throw new ObjectDisposedException(nameof(CloudEncryptedSnapshot)); }
        internal FileStream OpenRead()
        {
            lock (gate)
            {
                if (master == null) throw new ObjectDisposedException(nameof(CloudEncryptedSnapshot));
                // The retained master has ReadWrite access. Windows requires a new
                // reader to share that existing Write access as well. The master's
                // FileShare.Read still forbids all other writers and deletes.
                var reader = new FileStream(SnapshotPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
                readers.Add(reader); return reader;
            }
        }
        internal void ReleaseFile()
        {
            lock (gate)
            {
                foreach (var reader in readers) reader.Dispose(); readers.Clear(); master?.Dispose(); master = null;
                LanVrmTransferServer.TryDelete(SnapshotPath);
            }
        }
        public void Dispose()
        {
            lock (gate) { if (key != null) { Array.Clear(key, 0, key.Length); key = null; } ReleaseFile(); }
        }

        private sealed class AuthenticationStream : Stream
        {
            private readonly Stream output; private readonly HMACSHA512 mac;
            internal AuthenticationStream(Stream output, HMACSHA512 mac) { this.output = output; this.mac = mac; }
            public override void Write(byte[] buffer, int offset, int count) { output.Write(buffer, offset, count); mac.TransformBlock(buffer, offset, count, null, 0); }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => output.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
