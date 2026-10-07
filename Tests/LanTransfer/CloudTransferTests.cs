using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace VRVlog.LilToonExporter.LanTransfer.Tests
{
    public sealed class CloudTransferTests
    {
        [Test]
        public async Task MultipartUploadPreservesBytesAndPublishesQrOnlyAfterSuccess()
        {
            using (var fixture = new Fixture(CloudTransferProtocol.PartSize + 1))
            using (var session = new CloudVrmTransferSession(fixture.Source, fixture.Transport))
            {
                fixture.Transport.BeforePublish = () => Assert.That(session.Qr == null, Is.True);
                await session.UploadAsync();
                Assert.That(session.State, Is.EqualTo(CloudTransferState.Ready));
                Assert.That(fixture.Transport.Parts.SelectMany(part => part).ToArray(), Is.EqualTo(fixture.Bytes));
                Assert.That(fixture.Transport.Parts.Select(part => part.Length).ToArray(), Is.EqualTo(new[] { CloudTransferProtocol.PartSize, 1 }));
                Assert.That(session.Transferred, Is.EqualTo(fixture.Source.Size));
                var fields = CloudTransferProtocol.Fields.Read(session.Qr.Substring(LanTransferProtocol.QrPrefix.Length));
                fields.Exact("v", "id", "token", "name", "size", "sha256", "expiresAt");
                Assert.That(fields.Number("v"), Is.EqualTo(2L));
                Assert.That(fields.Text("name"), Is.EqualTo("テストアバター.vrm"));
                Assert.That(fields.Text("sha256"), Is.EqualTo(fixture.Source.FileHash));
                Assert.That(fields.Text("token"), Is.EqualTo(fixture.Transport.ReadToken));
                Assert.That(session.Qr.Contains(fixture.Transport.UploadToken), Is.False);
                Assert.That(session.Qr.Contains("host") || session.Qr.Contains("https://"), Is.False);
                Assert.That(fixture.Transport.AuthenticatedCorrectly, Is.True);
            }
        }

        [Test]
        public async Task CloudNameSanitizationKeepsTheCompletedVrmBytesAndHash()
        {
            using (var fixture = new Fixture(47, "衣装/夜\\昼\u0000\u202e.vrm"))
            using (var session = new CloudVrmTransferSession(fixture.Source, fixture.Transport))
            {
                Assert.That(fixture.Source.Name, Is.EqualTo("衣装-夜-昼.vrm"));
                using (var hash = SHA256.Create())
                    Assert.That(fixture.Source.FileHash, Is.EqualTo(LanTransferProtocol.Hex(hash.ComputeHash(fixture.Bytes))));
                await session.UploadAsync();
                Assert.That(fixture.Transport.Parts.SelectMany(part => part).ToArray(), Is.EqualTo(fixture.Bytes));
                Assert.That(File.ReadAllBytes(fixture.Path), Is.EqualTo(fixture.Bytes));
                var fields = CloudTransferProtocol.Fields.Read(session.Qr.Substring(LanTransferProtocol.QrPrefix.Length));
                Assert.That(fields.Text("name"), Is.EqualTo("衣装-夜-昼.vrm"));
                Assert.That(fields.Text("sha256"), Is.EqualTo(fixture.Source.FileHash));
            }
        }

        [Test]
        public void MaximumUnicodeCloudNameMatchesTheWorkerUtf16LengthBound()
        {
            var name = string.Concat(Enumerable.Repeat("\ud83d\ude00", 128)) + "/\\suffix";
            var display = CloudTransferProtocol.DisplayName(name);
            Assert.That(display.Length, Is.EqualTo(256));
            Assert.That(display, Is.EqualTo(name.Substring(0, 256)));
            var qr = CloudTransferProtocol.Qr(new string('0', 32), new string('A', 43), display, CloudTransferProtocol.MaximumSize, new string('0', 64), 2147483647);
            Assert.That(Encoding.UTF8.GetByteCount(qr), Is.LessThanOrEqualTo(4096));
        }

        [TestCase("part-failure")][TestCase("publish-mismatch")][TestCase("redirect")][TestCase("quota")]
        public async Task UploadFailuresNeverExposeQrAndCleanCreatedTransfer(string failure)
        {
            using (var fixture = new Fixture(17))
            using (var session = new CloudVrmTransferSession(fixture.Source, fixture.Transport))
            {
                fixture.Transport.Failure = failure;
                try { await session.UploadAsync(); Assert.That(false, Is.True); }
                catch (InvalidOperationException exception) { Assert.That(exception.Message.Contains(fixture.Transport.UploadToken), Is.False); }
                Assert.That(session.State, Is.EqualTo(CloudTransferState.Failed));
                Assert.That(session.Qr == null, Is.True);
                Assert.That(File.Exists(fixture.Path), Is.True);
                Assert.That(fixture.Transport.CancelCalls, Is.EqualTo(failure == "quota" || failure == "redirect" ? 0 : 1));
            }
        }

        [TestCase("completed")][TestCase("cancelled")][TestCase("expired")][TestCase("failed")]
        public async Task TerminalStatusInvalidatesQrAndAllowsFreshSessionWithSameBytes(string remote)
        {
            using (var fixture = new Fixture(19))
            {
                string oldQr;
                using (var first = new CloudVrmTransferSession(fixture.Source, fixture.Transport))
                {
                    await first.UploadAsync(); oldQr = first.Qr;
                    fixture.Transport.RemoteState = remote;
                    await first.RefreshAsync();
                    Assert.That(first.Terminal, Is.True); Assert.That(first.Qr == null, Is.True);
                    Assert.That(File.Exists(fixture.Path), Is.True);
                }
                fixture.Transport.RemoteState = "ready";
                fixture.Transport.Parts.Clear();
                using (var second = new CloudVrmTransferSession(fixture.Source, fixture.Transport))
                {
                    await second.UploadAsync();
                    Assert.That(second.Qr != oldQr, Is.True);
                    Assert.That(fixture.Transport.Parts.SelectMany(part => part).ToArray(), Is.EqualTo(fixture.Bytes));
                }
            }
        }

        [Test]
        public async Task CancellationDuringCreateStillDeletesSessionWhenCredentialsArrive()
        {
            using (var fixture = new Fixture(23))
            using (var session = new CloudVrmTransferSession(fixture.Source, fixture.Transport))
            {
                fixture.Transport.CreateRelease = new TaskCompletionSource<bool>();
                var upload = session.UploadAsync();
                await session.CancelAsync();
                fixture.Transport.CreateRelease.SetResult(true);
                try { await upload; Assert.That(false, Is.True); } catch (InvalidOperationException) { }
                Assert.That(session.State, Is.EqualTo(CloudTransferState.Canceled));
                Assert.That(session.Qr == null, Is.True);
                Assert.That(fixture.Transport.Parts.Count, Is.EqualTo(0));
                Assert.That(fixture.Transport.CancelCalls, Is.EqualTo(1));
            }
        }

        [Test]
        public async Task FailedStatusPollKeepsReadySessionForRetry()
        {
            using (var fixture = new Fixture(7))
            using (var session = new CloudVrmTransferSession(fixture.Source, fixture.Transport))
            {
                await session.UploadAsync(); var qr = session.Qr;
                fixture.Transport.Failure = "poll-failure";
                try { await session.RefreshAsync(); Assert.That(false, Is.True); } catch (InvalidDataException) { }
                Assert.That(session.Qr, Is.EqualTo(qr)); Assert.That(session.State, Is.EqualTo(CloudTransferState.Ready));
                fixture.Transport.Failure = null; fixture.Transport.RemoteState = "completed";
                await session.RefreshAsync(); Assert.That(session.State, Is.EqualTo(CloudTransferState.Completed));
            }
        }

        [Test]
        public void FlatParserRejectsAmbiguousAndUnboundedServiceMetadata()
        {
            foreach (var body in new[] { "{\"v\":2,\"v\":2}", "{\"size\":1.0}", "{\"size\":01}", "{\"id\":{}}", "{\"id\":[]}", "{\"id\":true}", "{\"id\":\"x\",}", "{}tail", new string(' ', 4097) })
                Assert.Throws<InvalidDataException>(() => CloudTransferProtocol.Fields.Read(body));
            Assert.That(CloudTransferProtocol.IsToken(new string('A', 42) + "B"), Is.False);
            Assert.That(CloudTransferProtocol.IsToken(new string('A', 43)), Is.True);
        }

        [Test]
        public void SourceDisposalDeletesOnlyItsOwnedTransferSnapshot()
        {
            var fixture = new Fixture(9); var path = fixture.Path;
            var ordinary = path + ".ordinary.vrm"; File.WriteAllBytes(ordinary, fixture.Bytes);
            try
            {
                fixture.Dispose();
                Assert.That(File.Exists(path), Is.False); Assert.That(File.ReadAllBytes(ordinary), Is.EqualTo(fixture.Bytes));
            }
            finally { File.Delete(ordinary); }
        }

        [Test]
        public void ClosingSourceReleasesReadersEvenBeforeSessionRegistersThem()
        {
            var fixture = new Fixture(31);
            var reader = fixture.Source.OpenRead();
            fixture.Dispose();
            Assert.That(reader.CanRead, Is.False);
            Assert.That(File.Exists(fixture.Path), Is.False);
            Assert.Throws<ObjectDisposedException>(() => fixture.Source.OpenRead());
        }

        [TestCase(0L)][TestCase(268435457L)]
        public void InvalidSnapshotSizesAreRejectedAndOwnedFileIsCleaned(long size)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vrvlog-cloud-size-" + Guid.NewGuid().ToString("N") + ".vrm");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) file.SetLength(size);
            Assert.Throws<InvalidOperationException>(() => new CloudVrmTransferSource(path, "fixture.vrm"));
            Assert.That(File.Exists(path), Is.False);
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vrvlog-cloud-test-" + Guid.NewGuid().ToString("N") + ".vrm");
            internal readonly byte[] Bytes;
            internal readonly CloudVrmTransferSource Source;
            internal readonly FakeTransport Transport;
            internal Fixture(int length, string name = "テストアバター.vrm")
            {
                Bytes = new byte[length]; for (var i = 0; i < length; i++) Bytes[i] = (byte)(i % 251);
                File.WriteAllBytes(Path, Bytes);
                Source = new CloudVrmTransferSource(Path, name); Transport = new FakeTransport(Source);
            }
            public void Dispose() => Source.Dispose();
        }

        private sealed class FakeTransport : ICloudTransferTransport
        {
            private readonly CloudVrmTransferSource source;
            internal readonly string ReadToken = LanTransferProtocol.Base64Url(new byte[32]);
            internal readonly string UploadToken = LanTransferProtocol.Base64Url(Enumerable.Repeat((byte)1, 32).ToArray());
            internal readonly List<byte[]> Parts = new List<byte[]>();
            internal string Failure, RemoteState = "ready";
            internal int CancelCalls, Created;
            internal bool AuthenticatedCorrectly = true;
            internal Action BeforePublish;
            internal TaskCompletionSource<bool> CreateRelease;
            private string id; private long expiry;
            internal FakeTransport(CloudVrmTransferSource source) { this.source = source; }
            public async Task<CloudTransferResponse> SendAsync(string method, string path, string token, byte[] body, string contentType, CancellationToken cancellation)
            {
                if (path == "/v2/transfers")
                {
                    Assert.That(method, Is.EqualTo("POST")); Assert.That(token == null, Is.True);
                    if (Failure == "quota") return new CloudTransferResponse(429, "{}");
                    if (Failure == "redirect") return new CloudTransferResponse(302, "{}");
                    var request = CloudTransferProtocol.Fields.Read(Encoding.UTF8.GetString(body));
                    var displayName = request.Text("name");
                    Assert.That(displayName.Length >= 1 && displayName.Length <= 256 && !displayName.Contains("/") && !displayName.Contains("\\") && !displayName.Any(c => c < 32 || c == 127), Is.True);
                    Assert.That(request.Number("size"), Is.EqualTo(source.Size)); Assert.That(request.Text("sha256"), Is.EqualTo(source.FileHash));
                    Created++; id = Created.ToString("x32"); expiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 900;
                    if (CreateRelease != null) await CreateRelease.Task;
                    return new CloudTransferResponse(201, "{\"v\":2,\"id\":\"" + id + "\",\"token\":\"" + ReadToken + "\",\"uploadToken\":\"" + UploadToken + "\",\"expiresAt\":" + expiry + ",\"partSize\":8388608}");
                }
                AuthenticatedCorrectly &= token == UploadToken;
                if (path.EndsWith("/cancel", StringComparison.Ordinal)) { CancelCalls++; return new CloudTransferResponse(200, "{}"); }
                cancellation.ThrowIfCancellationRequested();
                if (path.Contains("/parts/"))
                {
                    Assert.That(method, Is.EqualTo("PUT")); Assert.That(path, Is.EqualTo("/v2/transfers/" + id + "/parts/" + (Parts.Count + 1)));
                    if (Failure == "part-failure") return new CloudTransferResponse(500, "private service failure");
                    Parts.Add((byte[])body.Clone()); return new CloudTransferResponse(200, "{}");
                }
                if (path.EndsWith("/publish", StringComparison.Ordinal))
                {
                    BeforePublish?.Invoke();
                    var hash = Failure == "publish-mismatch" ? new string('f', 64) : source.FileHash;
                    return new CloudTransferResponse(200, CloudTransferProtocol.Qr(id, ReadToken, source.Name, source.Size, hash, expiry).Substring(LanTransferProtocol.QrPrefix.Length));
                }
                Assert.That(method, Is.EqualTo("GET"));
                return new CloudTransferResponse(Failure == "poll-failure" ? 503 : 200, "{\"state\":\"" + RemoteState + "\"}");
            }
        }
    }
}
