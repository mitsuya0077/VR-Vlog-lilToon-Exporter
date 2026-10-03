using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Org.BouncyCastle.X509;
using BcCertificate = Org.BouncyCastle.Tls.Certificate;

namespace VRVlog.LilToonExporter.LanTransfer
{
    internal enum TransferState { Waiting, Sending, AwaitingReceipt, Completing, Completed, Canceled, Expired, Failed }

    /// <summary>A single immutable file and an in-memory identity, with no document root or arbitrary path API.</summary>
    internal sealed class LanVrmTransferServer : IDisposable
    {
        private readonly object gate = new object();
        private readonly List<TcpClient> clients = new List<TcpClient>();
        private readonly FileStream snapshot;
        private readonly string snapshotPath;
        private readonly TcpListener listener;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly TimeSpan lifetime;
        private readonly Timer expiry;
        private readonly byte[] token;
        private readonly SessionTlsServer tlsIdentity;
        // Supplied only by the loopback test factory to pause an accepted receipt.
        private readonly Action beforeReceiptWrite;
        private bool activeDownload;
        private TransferState? acceptedOutcome;
        private long transferred;
        private TransferState state = TransferState.Waiting;
        private string message = "iPhoneのVRVlogで「PCから受け取る」を開き、QRを読み取ってください。";

        internal string Id { get; }
        internal string Host { get; }
        internal int Port { get; }
        internal long Size { get; }
        internal string FileHash { get; }
        internal string CertificateHash { get; }
        internal string Qr { get; private set; }
        internal TransferState State { get { lock (gate) return state; } }
        internal long Transferred { get { lock (gate) return transferred; } }
        internal string Message { get { lock (gate) return message; } }
        internal double RemainingSeconds => Math.Max(0, (lifetime - clock.Elapsed).TotalSeconds);
        internal bool Terminal => State >= TransferState.Completed;

        private LanVrmTransferServer(string path, string displayName, IPAddress address, TimeSpan timeout, bool loopbackForTests, FileStream ownedSnapshot, Action beforeReceiptWrite)
        {
            if (!LanTransferProtocol.IsPrivateIPv4(address) && !(loopbackForTests && IPAddress.IsLoopback(address)))
                throw new InvalidOperationException("同じLANのプライベートIPv4アドレスを選んでください。");
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
            snapshotPath = path;
            this.beforeReceiptWrite = beforeReceiptWrite;
            lifetime = timeout;
            Host = address.ToString();
            Id = LanTransferProtocol.Hex(LanTransferProtocol.RandomBytes(16));
            token = LanTransferProtocol.RandomBytes(32);
            snapshot = ownedSnapshot ?? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            try
            {
                Size = snapshot.Length;
                if (Size < 1 || Size > LanTransferProtocol.MaximumSize) throw new InvalidOperationException("スマホへの転送は1バイト以上256 MiB以下のVRMに対応しています。");
                snapshot.Position = 0;
                using (var hash = SHA256.Create()) FileHash = LanTransferProtocol.Hex(hash.ComputeHash(snapshot));
                snapshot.Position = 0;
                tlsIdentity = new SessionTlsServer(address, timeout);
                using (var hash = SHA256.Create()) CertificateHash = LanTransferProtocol.Hex(hash.ComputeHash(tlsIdentity.CertificateDer));
                listener = new TcpListener(address, 0);
                listener.Start(4);
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                clock.Restart();
                var expiresAt = new DateTimeOffset(DateTime.UtcNow.Add(timeout)).ToUnixTimeSeconds();
                Qr = LanTransferProtocol.QrPayload(Host, Port, Id, token, CertificateHash, displayName, Size, FileHash, expiresAt);
                expiry = new Timer(_ => Stop(TransferState.Expired), null, timeout, Timeout.InfiniteTimeSpan);
                _ = Task.Run(AcceptLoop);
            }
            catch
            {
                listener?.Stop();
                snapshot.Dispose();
                tlsIdentity?.Clear();
                Array.Clear(token, 0, token.Length);
                TryDelete(path);
                throw;
            }
        }

        internal static LanVrmTransferServer Start(string path, string displayName, IPAddress address, TimeSpan timeout, FileStream ownedSnapshot = null) =>
            new LanVrmTransferServer(path, displayName, address, timeout, false, ownedSnapshot, null);

        internal static LanVrmTransferServer StartLoopbackForTests(string path, string name, TimeSpan timeout, FileStream ownedSnapshot = null, Action beforeReceiptWrite = null) =>
            new LanVrmTransferServer(path, name, IPAddress.Loopback, timeout, true, ownedSnapshot, beforeReceiptWrite);

        private async Task AcceptLoop()
        {
            try
            {
                while (!Terminal)
                {
                    var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    lock (gate)
                    {
                        if (state >= TransferState.Completing || clients.Count >= 4) { client.Dispose(); continue; }
                        clients.Add(client);
                    }
                    _ = Task.Run(() => Handle(client));
                }
            }
            catch (ObjectDisposedException) { }
            catch (SocketException) { if (!Terminal) Stop(TransferState.Failed); }
            catch (InvalidOperationException) { if (!Terminal) Stop(TransferState.Failed); }
        }

        private void Handle(TcpClient client)
        {
            var ownsDownload = false;
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 15000;
                client.SendTimeout = 15000;
                using (var deadline = new Timer(_ => client.Dispose(), null, 10000, Timeout.Infinite))
                {
                    var protocol = new TlsServerProtocol(client.GetStream());
                    try
                    {
                    // Each connection needs a fresh TLS peer/context; certificate/key identity is shared read-only.
                    SessionTlsServer peer;
                    lock (gate)
                    {
                        if (state >= TransferState.Completing) return;
                        peer = tlsIdentity.NewPeer();
                    }
                    protocol.Accept(peer);
                    deadline.Change(5000, Timeout.Infinite);
                    var stream = protocol.Stream;
                    var request = LanTransferProtocol.ReadRequest(stream);
                    deadline.Change(Timeout.Infinite, Timeout.Infinite);
                    bool authenticated;
                    lock (gate)
                    {
                        if (clock.Elapsed >= lifetime || state >= TransferState.Completing) return;
                        authenticated = request.Authenticated(token, Id);
                    }
                    if (!authenticated) { Reply(stream, 401, "Unauthorized"); return; }
                    if (request.Method == "GET" && request.Path == "/v1/vrm")
                    {
                        bool conflict;
                        lock (gate)
                        {
                            if (clock.Elapsed >= lifetime || state >= TransferState.Completing) return;
                            conflict = activeDownload;
                            if (!conflict)
                            {
                                activeDownload = ownsDownload = true;
                                snapshot.Position = 0;
                                transferred = 0;
                                state = TransferState.Sending;
                                message = "iPhoneへ直接転送しています。";
                            }
                        }
                        if (conflict) { Reply(stream, 409, "Conflict"); return; }
                        WriteHeader(stream, 200, "OK", Size, "model/vrm");
                        var buffer = new byte[64 * 1024];
                        while (true)
                        {
                            int count;
                            lock (gate)
                            {
                                if (state >= TransferState.Completing || clock.Elapsed >= lifetime) return;
                                count = snapshot.Read(buffer, 0, buffer.Length);
                            }
                            if (count == 0) break;
                            stream.Write(buffer, 0, count);
                            lock (gate) transferred += count;
                        }
                        stream.Flush();
                        lock (gate)
                        {
                            if (state < TransferState.Completing)
                            {
                                state = TransferState.AwaitingReceipt;
                                message = "転送済みです。iPhoneの検証・保存完了通知を待っています。";
                            }
                        }
                    }
                    else if (request.Method == "POST" && (request.Path == "/v1/complete" || request.Path == "/v1/cancel"))
                    {
                        var terminal = request.Path == "/v1/complete" ? TransferState.Completed : TransferState.Canceled;
                        lock (gate)
                        {
                            if (state >= TransferState.Completing || clock.Elapsed >= lifetime) return;
                            acceptedOutcome = terminal;
                            state = TransferState.Completing;
                            Qr = null;
                            Array.Clear(token, 0, token.Length);
                        }
                        // Acceptance fixes the outcome; expiry allows this bounded
                        // acknowledgement, while explicit disposal still cleans up.
                        deadline.Change(5000, Timeout.Infinite);
                        try { beforeReceiptWrite?.Invoke(); Reply(stream, 200, "OK"); }
                        finally { Stop(terminal); }
                    }
                    else Reply(stream, 404, "Not Found");
                    }
                    finally { protocol.Close(); }
                }
            }
            catch (Exception exception) when (exception is IOException || exception is SocketException || exception is ObjectDisposedException
                || exception is InvalidOperationException || exception is Org.BouncyCastle.Tls.TlsFatalAlert)
            {
                // Never log request/QR/token/certificate or arbitrary transport exception text.
                if (ownsDownload)
                {
                    lock (gate)
                    {
                        if (state < TransferState.Completing)
                        {
                            state = TransferState.Waiting;
                            message = "通信が切れました。期限内ならiPhoneで「再試行」できます。同じLAN・通信許可・Wi-Fiの接続を確認してください。";
                        }
                    }
                }
            }
            finally
            {
                lock (gate)
                {
                    clients.Remove(client);
                    if (ownsDownload) activeDownload = false;
                }
                client.Dispose();
            }
        }

        private static void WriteHeader(Stream stream, int code, string reason, long length, string type)
        {
            var header = "HTTP/1.1 " + code.ToString(CultureInfo.InvariantCulture) + " " + reason + "\r\nContent-Length: "
                + length.ToString(CultureInfo.InvariantCulture) + "\r\nContent-Type: " + type
                + "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(header);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static void Reply(Stream stream, int code, string reason) { WriteHeader(stream, code, reason, 0, "application/octet-stream"); stream.Flush(); }

        internal void Stop(TransferState finalState)
        {
            TcpClient[] connections;
            lock (gate)
            {
                if (state >= TransferState.Completed) return;
                if (state == TransferState.Completing && finalState == TransferState.Expired) return;
                // Window closure, local cancellation or a listener failure must
                // clean up immediately without rewriting an accepted receipt.
                if (acceptedOutcome.HasValue) finalState = acceptedOutcome.Value;
                state = finalState;
                Qr = null;
                Array.Clear(token, 0, token.Length);
                message = finalState == TransferState.Completed ? "iPhoneへの保存が完了しました。待受を停止しました。"
                    : finalState == TransferState.Expired ? "受取期限が切れました。待受を停止しました。再度「スマホに送る」でQRを作成してください。"
                    : finalState == TransferState.Canceled ? "転送をキャンセルしました。待受を停止しました。"
                    : "待受を停止しました。同じLANの接続・選択IP・Windowsの通信許可を確認してください。";
                connections = clients.ToArray();
                listener.Stop();
                expiry?.Dispose();
            }
            foreach (var connection in connections) connection.Dispose();
            snapshot.Dispose();
            tlsIdentity.Clear();
            TryDelete(snapshotPath);
        }

        internal static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public void Dispose() => Stop(TransferState.Canceled);

        private sealed class SessionTlsServer : DefaultTlsServer
        {
            private readonly SecureRandom random;
            private AsymmetricCipherKeyPair keyPair;
            private readonly BcCertificate certificate;
            internal byte[] CertificateDer { get; }

            internal SessionTlsServer(IPAddress address, TimeSpan lifetime) : base(new BcTlsCrypto(new SecureRandom()))
            {
                random = new SecureRandom();
                var generator = new RsaKeyPairGenerator();
                generator.Init(new KeyGenerationParameters(random, 2048));
                keyPair = generator.GenerateKeyPair();
                var cert = new X509V3CertificateGenerator();
                cert.SetSerialNumber(new BigInteger(1, LanTransferProtocol.RandomBytes(16)));
                var name = new X509Name("CN=VRVlog LAN Transfer");
                cert.SetIssuerDN(name);
                cert.SetSubjectDN(name);
                cert.SetNotBefore(DateTime.UtcNow.AddMinutes(-5));
                cert.SetNotAfter(DateTime.UtcNow.Add(lifetime).AddMinutes(5));
                cert.SetPublicKey(keyPair.Public);
                cert.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(false));
                cert.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.DigitalSignature | KeyUsage.KeyEncipherment));
                cert.AddExtension(X509Extensions.ExtendedKeyUsage, false, new ExtendedKeyUsage(KeyPurposeID.id_kp_serverAuth));
                cert.AddExtension(X509Extensions.SubjectAlternativeName, false, new GeneralNames(new GeneralName(GeneralName.IPAddress, address.ToString())));
                CertificateDer = cert.Generate(new Asn1SignatureFactory("SHA256WITHRSA", keyPair.Private, random)).GetEncoded();
                certificate = new BcCertificate(new[] { Crypto.CreateCertificate(CertificateDer) });
            }

            private SessionTlsServer(SessionTlsServer identity) : base(new BcTlsCrypto(new SecureRandom()))
            {
                random = identity.random;
                keyPair = identity.keyPair;
                certificate = identity.certificate;
                CertificateDer = identity.CertificateDer;
            }

            internal SessionTlsServer NewPeer() => new SessionTlsServer(this);
            internal void Clear() { keyPair = null; }
            protected override ProtocolVersion[] GetSupportedVersions() => new[] { ProtocolVersion.TLSv12 };
            protected override int[] GetSupportedCipherSuites() => new[]
            {
                CipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
                CipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384
            };
            public override CertificateRequest GetCertificateRequest() => null;
            public override int GetMaxHandshakeMessageSize() => 64 * 1024;
            protected override TlsCredentialedSigner GetRsaSignerCredentials() => new BcDefaultTlsCredentialedSigner(
                new TlsCryptoParameters(m_context), (BcTlsCrypto)Crypto, keyPair.Private, certificate,
                new SignatureAndHashAlgorithm(Org.BouncyCastle.Tls.HashAlgorithm.sha256, SignatureAlgorithm.rsa));
        }
    }
}
