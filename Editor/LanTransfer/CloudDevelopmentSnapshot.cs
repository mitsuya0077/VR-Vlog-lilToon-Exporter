using System;
using System.IO;

namespace VRVlog.LilToonExporter.LanTransfer
{
    // A selected export is read only. This object owns only its newly created
    // copy until the transfer window adopts it, including when adoption fails.
    internal sealed class CloudDevelopmentSnapshot : IDisposable
    {
        internal string SnapshotPath { get; }
        internal string Name { get; }
        private bool owned;

        private CloudDevelopmentSnapshot(string path, string name)
        {
            SnapshotPath = path;
            Name = name;
        }

        internal static CloudDevelopmentSnapshot CopySavedVrm(string savedVrmPath, Func<Stream> testReader = null, Action<string> testCreated = null)
        {
            if (string.IsNullOrEmpty(savedVrmPath) || !string.Equals(Path.GetExtension(savedVrmPath), ".vrm", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("書き出し済みのVRMファイルを選択してください。");
            var directory = Path.Combine(Path.GetTempPath(), "VRVlogCloudTransfers");
            Directory.CreateDirectory(directory);
            var snapshot = new CloudDevelopmentSnapshot(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".vrm"), Path.GetFileName(savedVrmPath));
            try
            {
                using (var input = testReader != null ? testReader() : new FileStream(savedVrmPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
                {
                    var size = input.Length;
                    if (size < 1 || size > CloudTransferProtocol.MaximumSize)
                        throw new InvalidOperationException("256 MiB以下の書き出し済みVRMファイルを選択してください。");
                    using (var output = new FileStream(snapshot.SnapshotPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
                    {
                        snapshot.owned = true;
                        testCreated?.Invoke(snapshot.SnapshotPath);
                        var buffer = new byte[64 * 1024];
                        long copied = 0;
                        while (copied < size)
                        {
                            var count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, size - copied));
                            if (count == 0) throw new IOException("選択したVRMを読み取れませんでした。");
                            output.Write(buffer, 0, count);
                            copied += count;
                        }
                        if (input.ReadByte() != -1) throw new IOException("選択したVRMが変更されました。選択し直してください。");
                    }
                }
                return snapshot;
            }
            catch
            {
                snapshot.Dispose();
                throw;
            }
        }

        internal void ReleaseOwnership() => owned = false;

        public void Dispose()
        {
            if (!owned) return;
            owned = false;
            LanVrmTransferServer.TryDelete(SnapshotPath);
        }
    }
}
