using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using ZXing;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace VRVlog.LilToonExporter.LanTransfer.Tests
{
    public sealed class LanTransferTests
    {
        [TestCase("10.2.3.4", true)]
        [TestCase("172.16.1.2", true)]
        [TestCase("172.31.1.2", true)]
        [TestCase("192.168.2.3", true)]
        [TestCase("172.32.1.2", false)]
        [TestCase("127.0.0.1", false)]
        [TestCase("169.254.1.1", false)]
        [TestCase("8.8.8.8", false)]
        [TestCase("::1", false)]
        public void OnlyPrivateIPv4IsAccepted(string host, bool expected) =>
            Assert.That(LanTransferProtocol.IsPrivateIPv4(System.Net.IPAddress.Parse(host)), Is.EqualTo(expected));

        [Test]
        public void DuplicateHeadersBodiesAndArbitraryTargetsAreNotAuthorized()
        {
            foreach (var fields in new[] { "Authorization: x\r\nAuthorization: y\r\n", "Content-Length: 1\r\n", "Transfer-Encoding: chunked\r\n", "Host: duplicate\r\n", "Authorization: x\ny\r\n" })
                Assert.Throws<InvalidDataException>(() => Parse("GET /v1/vrm HTTP/1.1\r\nHost: local\r\n" + fields + "\r\n"));
            Assert.Throws<InvalidDataException>(() => Parse("GET /v1/vrm HTTP/1.1\r\nHost: " + new string('a', 8200) + "\r\n\r\n"));
            var request = Parse("GET /v1/vrm HTTP/1.1\r\nHost: local\r\nAuthorization: Bearer invalid\r\nX-VRVlog-Transfer: wrong\r\n\r\n");
            Assert.That(request.Authenticated(new byte[32], new string('a', 32)), Is.False);
        }

        [Test]
        public void QrUsesTheVersionedContractAndDisplayOnlyName()
        {
            var qr = LanTransferProtocol.QrPayload("192.168.1.2", 12345, new string('a', 32), new byte[32], new string('b', 64), "a\"\\\n\u202eb", 123, new string('c', 64), 100);
            Assert.That(qr.StartsWith("vrvlog-transfer:{\"v\":1,"), Is.True);
            Assert.That(Encoding.UTF8.GetByteCount(qr), Is.LessThanOrEqualTo(4096));
            Assert.That(qr.Contains("\"name\":\"a\\\"\\\\b\""), Is.True);
            Assert.That(LanTransferProtocol.DisplayName(new string('a', 1000)).Length, Is.EqualTo(256));
            Assert.That(LanTransferProtocol.DisplayName("a\ud800b\udc00c"), Is.EqualTo("abc"));
        }

        [Test]
        public void MaximumUnicodeNameQrCanBeGeneratedAndDecodedLocally()
        {
            // All fixture credentials are deliberately public synthetic zeros.
            var name = string.Concat(System.Linq.Enumerable.Repeat("\ud83d\ude00", 128));
            var qr = LanTransferProtocol.QrPayload("192.168.1.2", 65535, new string('0', 32), new byte[32], new string('0', 64), name,
                LanTransferProtocol.MaximumSize, new string('0', 64), 2147483647);
            var hints = new System.Collections.Generic.Dictionary<EncodeHintType, object>
            {
                [EncodeHintType.ERROR_CORRECTION] = ErrorCorrectionLevel.M,
                [EncodeHintType.MARGIN] = 0,
                [EncodeHintType.CHARACTER_SET] = "UTF-8"
            };
            var matrix = new QRCodeWriter().encode(qr, BarcodeFormat.QR_CODE, 0, 0, hints);
            Assert.That(new ZXing.QrCode.Internal.Decoder().decode(matrix, null).Text == qr, Is.True, "The local QR codec must preserve the wire payload.");
        }

        [Test]
        public void RawBytesAndHashMatchThenReceiptStopsAndDeletesSnapshot()
        {
            var data = FixtureBytes();
            var path = NewSnapshot(data);
            AssertSnapshotRoundTrip(path, data);
        }

        // Shared only between test assemblies so the real Exporter/writer fixtures
        // can verify transport without adding any production testing entry point.
        public static void AssertSnapshotRoundTrip(string path, byte[] data)
        {
            using (var server = LanVrmTransferServer.StartLoopbackForTests(path, "fixture.vrm", TimeSpan.FromMinutes(1)))
            {
                // Sender UI is Windows-only in this first release. Unix .NET's
                // file sharing semantics differ; CI still exercises real TLS there.
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                    Assert.Throws<IOException>(() => { using (File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { } });
                using (var connection = new Connection(server, false))
                {
                    connection.Send("GET", "/v1/vrm", Credentials(server));
                    var response = connection.ReadHeader();
                    Assert.That(response.StartsWith("HTTP/1.1 200"), Is.True);
                    Assert.That(response.Contains("Content-Length: " + data.Length), Is.True);
                    var received = connection.ReadExactly(data.Length);
                    Assert.That(received, Is.EqualTo(data));
                    using (var hash = SHA256.Create()) Assert.That(LanTransferProtocol.Hex(hash.ComputeHash(received)), Is.EqualTo(server.FileHash));
                }
                using (var connection = new Connection(server, false))
                {
                    connection.Send("POST", "/v1/complete", Credentials(server));
                    Assert.That(connection.ReadHeader().StartsWith("HTTP/1.1 200"), Is.True);
                }
                Assert.That(SpinWait.SpinUntil(() => server.State == TransferState.Completed && !File.Exists(path), 3000), Is.True);
                Assert.That(server.Qr == null, Is.True, "Credentials must be unavailable after completion.");
                Assert.Throws<SocketException>(() => { using (var socket = new TcpClient()) socket.Connect("127.0.0.1", server.Port); });
            }
        }

        [Test]
        public void InvalidTokenAndUnsupportedPathDoNotExposeFileAndCancelRequiresAuthentication()
        {
            var path = NewSnapshot(FixtureBytes());
            using (var server = LanVrmTransferServer.StartLoopbackForTests(path, "fixture.vrm", TimeSpan.FromMinutes(1)))
            {
                using (var connection = new Connection(server, false))
                {
                    connection.Send("POST", "/v1/cancel", "Authorization: Bearer " + new string('A', 43) + "\r\nX-VRVlog-Transfer: " + server.Id + "\r\n");
                    Assert.That(connection.ReadHeader().StartsWith("HTTP/1.1 401"), Is.True);
                }
                Assert.That(server.Terminal, Is.False);
                using (var connection = new Connection(server, false))
                {
                    connection.Send("GET", "/v1/vrm", Credentials(server).Replace(server.Id, new string('f', 32)));
                    Assert.That(connection.ReadHeader().StartsWith("HTTP/1.1 401"), Is.True);
                }
                using (var connection = new Connection(server, false))
                {
                    connection.Send("GET", "/../../private.txt", Credentials(server));
                    Assert.That(connection.ReadHeader().StartsWith("HTTP/1.1 404"), Is.True);
                }
                using (var connection = new Connection(server, false))
                {
                    connection.Send("POST", "/v1/cancel", Credentials(server));
                    Assert.That(connection.ReadHeader().StartsWith("HTTP/1.1 200"), Is.True);
                }
                Assert.That(SpinWait.SpinUntil(() => server.State == TransferState.Canceled && !File.Exists(path), 3000), Is.True);
            }
        }

        [Test]
        public void WrongCertificatePinFailsTheTlsHandshake()
        {
            var path = NewSnapshot(FixtureBytes());
            using (var server = LanVrmTransferServer.StartLoopbackForTests(path, "fixture.vrm", TimeSpan.FromMinutes(1)))
                Assert.Throws<TlsFatalAlert>(() => { using (new Connection(server, true)) { } });
        }

        [Test]
        public void CommunicationBreakCanRetryFromTheFirstByte()
        {
            var data = FixtureBytes();
            var path = NewSnapshot(data);
            using (var server = LanVrmTransferServer.StartLoopbackForTests(path, "fixture.vrm", TimeSpan.FromMinutes(1)))
            {
                var credentials = Credentials(server);
                using (var connection = new Connection(server, false))
                {
                    connection.Send("GET", "/v1/vrm", credentials);
                    Assert.That(connection.ReadHeader().StartsWith("HTTP/1.1 200"), Is.True);
                    connection.ReadExactly(100);
                }
                Thread.Sleep(100);
                using (var connection = new Connection(server, false))
                {
                    connection.Send("GET", "/v1/vrm", credentials);
                    Assert.That(connection.ReadHeader().StartsWith("HTTP/1.1 200"), Is.True);
                    Assert.That(connection.ReadExactly(data.Length), Is.EqualTo(data));
                }
                Assert.That(server.Terminal, Is.False);
            }
        }

        [Test]
        public void MonotonicExpiryInvalidatesQrAndDeletesSnapshot()
        {
            var path = NewSnapshot(FixtureBytes());
            using (var server = LanVrmTransferServer.StartLoopbackForTests(path, "fixture.vrm", TimeSpan.FromMilliseconds(100)))
            {
                Assert.That(SpinWait.SpinUntil(() => server.State == TransferState.Expired && !File.Exists(path), 3000), Is.True);
                Assert.That(server.Qr == null, Is.True, "Credentials must be unavailable after expiry.");
                Assert.Throws<SocketException>(() => { using (var socket = new TcpClient()) socket.Connect("127.0.0.1", server.Port); });
            }
        }

        [TestCase("/v1/complete", (int)TransferState.Completed)]
        [TestCase("/v1/cancel", (int)TransferState.Canceled)]
        public void AcceptedReceiptWinsExpiryBeforeAcknowledgement(string route, int expectedState)
        {
            var path = NewSnapshot(FixtureBytes());
            using (var accepted = new ManualResetEventSlim())
            using (var releaseAcknowledgement = new ManualResetEventSlim())
            using (var server = LanVrmTransferServer.StartLoopbackForTests(path, "fixture.vrm", TimeSpan.FromMinutes(1),
                beforeReceiptWrite: () =>
                {
                    accepted.Set();
                    if (!releaseAcknowledgement.Wait(3000)) throw new IOException("Receipt test timed out.");
                }))
            {
                try
                {
                    using (var connection = new Connection(server, false))
                    {
                        connection.Send("POST", route, Credentials(server));
                        Assert.That(accepted.Wait(3000), Is.True, "A real authenticated request must reach acceptance.");
                        Assert.That(server.State == TransferState.Completing, Is.True);
                        Assert.That(server.Qr == null, Is.True, "Acceptance immediately invalidates credentials.");
                        // Invoke the same production callback used by the expiry
                        // timer exactly while the real TLS response is paused.
                        server.Stop(TransferState.Expired);
                        Assert.That(server.State == TransferState.Completing, Is.True,
                            "Expiry cannot replace an authenticated receipt's accepted result.");
                        releaseAcknowledgement.Set();
                        Assert.That(connection.ReadHeader().StartsWith("HTTP/1.1 200"), Is.True,
                            "An accepted receipt still receives its acknowledgement.");
                    }
                    Assert.That(SpinWait.SpinUntil(() => server.State == (TransferState)expectedState && !File.Exists(path), 3000), Is.True);
                    Assert.That(server.Qr == null, Is.True);
                    Assert.Throws<SocketException>(() => { using (var socket = new TcpClient()) socket.Connect("127.0.0.1", server.Port); });
                }
                finally { releaseAcknowledgement.Set(); }
            }
        }

        [TestCase("/v1/complete", (int)TransferState.Completed, true)]
        [TestCase("/v1/cancel", (int)TransferState.Canceled, true)]
        [TestCase("/v1/complete", (int)TransferState.Completed, false)]
        [TestCase("/v1/cancel", (int)TransferState.Canceled, false)]
        public void StoppingAcceptedReceiptKeepsOutcomeAndClosesResources(string route, int expectedState, bool dispose)
        {
            var path = NewSnapshot(FixtureBytes());
            using (var accepted = new ManualResetEventSlim())
            using (var releaseAcknowledgement = new ManualResetEventSlim())
            using (var server = LanVrmTransferServer.StartLoopbackForTests(path, "fixture.vrm", TimeSpan.FromMinutes(1),
                beforeReceiptWrite: () =>
                {
                    accepted.Set();
                    if (!releaseAcknowledgement.Wait(3000)) throw new IOException("Receipt test timed out.");
                }))
            {
                try
                {
                    using (var connection = new Connection(server, false))
                    {
                        connection.Send("POST", route, Credentials(server));
                        Assert.That(accepted.Wait(3000), Is.True);
                        // Exercise the real window-close path, and the other
                        // external terminal path, before the TLS ACK can write.
                        if (dispose) server.Dispose();
                        else server.Stop(TransferState.Failed);
                        Assert.That(server.State == (TransferState)expectedState, Is.True,
                            "Explicit cleanup retains the authenticated outcome.");
                        Assert.That(server.Qr == null, Is.True);
                        Assert.That(File.Exists(path), Is.False, "Cleanup cannot wait for the paused response.");
                        Assert.That(connection.PeerClosed, Is.True, "Explicit cleanup closes accepted sockets immediately.");
                        Assert.Throws<SocketException>(() => { using (var socket = new TcpClient()) socket.Connect("127.0.0.1", server.Port); });
                        server.Stop(TransferState.Expired);
                        Assert.That(server.State == (TransferState)expectedState, Is.True,
                            "Later expiry cannot change the retired result.");
                        releaseAcknowledgement.Set();
                    }
                }
                finally { releaseAcknowledgement.Set(); }
            }
        }

        [Test]
        public void UnauthenticatedConnectionLimitAndStopClosePendingSockets()
        {
            var path = NewSnapshot(FixtureBytes());
            var pending = new System.Collections.Generic.List<TcpClient>();
            try
            {
                using (var server = LanVrmTransferServer.StartLoopbackForTests(path, "fixture.vrm", TimeSpan.FromMinutes(1)))
                {
                    for (var i = 0; i < 4; i++)
                    {
                        var client = new TcpClient(); pending.Add(client);
                        client.Connect("127.0.0.1", server.Port);
                    }
                    using (var fifth = new TcpClient())
                    {
                        fifth.Connect("127.0.0.1", server.Port);
                        Assert.That(fifth.Client.Poll(3000000, SelectMode.SelectRead) && fifth.Available == 0, Is.True,
                            "A fifth unauthenticated socket must be closed without a TLS handshake.");
                    }
                    server.Dispose();
                    foreach (var client in pending)
                        Assert.That(client.Client.Poll(3000000, SelectMode.SelectRead) && client.Available == 0, Is.True,
                            "Stopping must close sockets that have not authenticated.");
                    Assert.That(File.Exists(path), Is.False);
                }
            }
            finally { foreach (var client in pending) client.Dispose(); LanVrmTransferServer.TryDelete(path); }
        }

        [Test]
        public void DisposedStartupHandleCleansSnapshotWithoutOpeningListener()
        {
            var path = NewSnapshot(FixtureBytes());
            var owned = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            owned.Dispose();
            Assert.Throws<ObjectDisposedException>(() => LanVrmTransferServer.StartLoopbackForTests(path, "fixture.vrm", TimeSpan.FromMinutes(1), owned));
            Assert.That(File.Exists(path), Is.False, "A canceled startup must not retain its snapshot.");
        }

        private static LanTransferProtocol.Request Parse(string value) => LanTransferProtocol.ReadRequest(new MemoryStream(Encoding.ASCII.GetBytes(value)));
        private static byte[] FixtureBytes() { var bytes = new byte[128 * 1024]; for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251); return bytes; }
        private static string NewSnapshot(byte[] data) { var path = Path.Combine(Path.GetTempPath(), "vrvlog-lan-test-" + Guid.NewGuid().ToString("N") + ".vrm"); File.WriteAllBytes(path, data); return path; }
        private static string Credentials(LanVrmTransferServer server) => "Authorization: Bearer " + Regex.Match(server.Qr, "\"token\":\"([^\"]+)\"").Groups[1].Value + "\r\nX-VRVlog-Transfer: " + server.Id + "\r\n";

        private sealed class Connection : IDisposable
        {
            private readonly TcpClient client;
            private readonly TlsClientProtocol protocol;
            private Stream Stream => protocol.Stream;
            internal bool PeerClosed => client.Client.Poll(3000000, SelectMode.SelectRead) && client.Available == 0;
            internal Connection(LanVrmTransferServer server, bool wrongPin)
            {
                client = new TcpClient { ReceiveTimeout = 5000, SendTimeout = 5000 };
                try
                {
                    client.Connect("127.0.0.1", server.Port);
                    protocol = new TlsClientProtocol(client.GetStream());
                    protocol.Connect(new PinnedClient(wrongPin ? new string('0', 64) : server.CertificateHash));
                }
                catch { client.Dispose(); throw; }
            }
            internal void Send(string method, string path, string credentials)
            {
                var bytes = Encoding.ASCII.GetBytes(method + " " + path + " HTTP/1.1\r\nHost: local\r\n" + credentials + "Content-Length: 0\r\nConnection: close\r\n\r\n");
                Stream.Write(bytes, 0, bytes.Length);
                Stream.Flush();
            }
            internal string ReadHeader()
            {
                var result = new StringBuilder();
                while (!result.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    var value = Stream.ReadByte();
                    if (value < 0 || result.Length > 8192) throw new IOException("Invalid response header.");
                    result.Append((char)value);
                }
                return result.ToString();
            }
            internal byte[] ReadExactly(int count)
            {
                var result = new byte[count];
                var offset = 0;
                while (offset < count)
                {
                    var read = Stream.Read(result, offset, count - offset);
                    if (read <= 0) throw new IOException("Incomplete response.");
                    offset += read;
                }
                return result;
            }
            public void Dispose() { client.Dispose(); }
        }

        private sealed class PinnedClient : DefaultTlsClient
        {
            private readonly string fingerprint;
            internal PinnedClient(string fingerprint) : base(new BcTlsCrypto()) { this.fingerprint = fingerprint; }
            protected override ProtocolVersion[] GetSupportedVersions() => new[] { ProtocolVersion.TLSv12 };
            public override TlsAuthentication GetAuthentication() => new Authentication(fingerprint);
        }

        private sealed class Authentication : TlsAuthentication
        {
            private readonly string fingerprint;
            internal Authentication(string fingerprint) { this.fingerprint = fingerprint; }
            public TlsCredentials GetClientCredentials(CertificateRequest request) => null;
            public void NotifyServerCertificate(TlsServerCertificate certificate)
            {
                using (var hash = SHA256.Create())
                    if (LanTransferProtocol.Hex(hash.ComputeHash(certificate.Certificate.GetCertificateAt(0).GetEncoded())) != fingerprint)
                        throw new TlsFatalAlert(AlertDescription.bad_certificate);
            }
        }
    }
}
