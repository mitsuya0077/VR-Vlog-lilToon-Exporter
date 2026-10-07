using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VRVlog.LilToonExporter.LanTransfer
{
    internal sealed class CloudTransferResponse
    {
        internal int StatusCode { get; }
        internal string Body { get; }
        internal CloudTransferResponse(int statusCode, string body) { StatusCode = statusCode; Body = body; }
    }

    internal interface ICloudTransferTransport
    {
        Task<CloudTransferResponse> SendAsync(string method, string path, string token, byte[] body, string contentType, CancellationToken cancellation);
    }

    internal sealed class CloudTransferTransport : ICloudTransferTransport
    {
        private static readonly HttpClient Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
        public async Task<CloudTransferResponse> SendAsync(string method, string path, string token, byte[] body, string contentType, CancellationToken cancellation)
        {
            // Every production request goes to the fixed service using the OS
            // certificate trust. Redirects and QR-supplied hosts are forbidden.
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using (var request = new HttpRequestMessage(new HttpMethod(method), CloudTransferProtocol.ServiceUrl + path))
            {
                // ResponseHeadersRead does not extend HttpClient.Timeout to
                // body reads. Bound the entire exchange, including the body.
                deadline.CancelAfter(TimeSpan.FromSeconds(45));
                if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (body != null)
                {
                    request.Content = new ByteArrayContent(body);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                    request.Content.Headers.ContentLength = body.Length;
                }
                using (var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new MemoryStream())
                {
                    var buffer = new byte[1024]; int count;
                    while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, deadline.Token).ConfigureAwait(false)) != 0)
                    {
                        if (output.Length + count > 4096) throw CloudTransferProtocol.Invalid();
                        output.Write(buffer, 0, count);
                    }
                    return new CloudTransferResponse((int)response.StatusCode, new UTF8Encoding(false, true).GetString(output.ToArray()));
                }
            }
        }
    }

    // This is only the explicitly generated transfer snapshot, never an
    // ordinary user-selected export. Sessions borrow it; closing the window
    // releases the immutable master and deletes it, including after retries.
    internal sealed class CloudVrmTransferSource : IDisposable
    {
        private readonly object gate = new object();
        private readonly List<FileStream> readers = new List<FileStream>();
        private readonly string path;
        private FileStream master;
        internal string Name { get; }
        internal long Size { get; }
        internal string FileHash { get; }
        internal CloudVrmTransferSource(string ownedSnapshotPath, string name)
        {
            path = ownedSnapshotPath;
            Name = LanTransferProtocol.DisplayName(name);
            try
            {
                master = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                Size = master.Length;
                if (Size < 1 || Size > CloudTransferProtocol.MaximumSize) throw new InvalidOperationException("スマホへの転送は256 MiB以下のVRMに対応しています。");
                using (var hash = SHA256.Create()) FileHash = LanTransferProtocol.Hex(hash.ComputeHash(master));
                master.Position = 0;
            }
            catch { Dispose(); throw; }
        }
        internal FileStream OpenRead()
        {
            lock (gate)
            {
                if (master == null) throw new ObjectDisposedException(nameof(CloudVrmTransferSource));
                var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
                readers.Add(reader);
                return reader;
            }
        }
        public void Dispose()
        {
            lock (gate)
            {
                // A close/reload can race with startup before the session has
                // registered its active reader. Close every borrowed handle.
                foreach (var reader in readers) reader.Dispose();
                readers.Clear(); master?.Dispose(); master = null;
                LanVrmTransferServer.TryDelete(path);
            }
        }
    }

    internal enum CloudTransferState { Preparing, Uploading, Ready, Completed, Canceled, Expired, Failed }

    internal sealed class CloudVrmTransferSession : IDisposable
    {
        private readonly object gate = new object();
        private readonly CloudVrmTransferSource source;
        private readonly ICloudTransferTransport transport;
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private string id, readToken, uploadToken;
        private FileStream activeReader;
        private CloudTransferState state = CloudTransferState.Preparing;
        private string qr;
        private long transferred, expiresAt;
        private bool started, disposed;
        internal CloudTransferState State { get { lock (gate) return state; } }
        internal string Qr { get { lock (gate) return qr; } }
        internal long Transferred { get { lock (gate) return transferred; } }
        internal long ExpiresAt { get { lock (gate) return expiresAt; } }
        internal bool Terminal => State >= CloudTransferState.Completed;
        internal string Message => State == CloudTransferState.Preparing ? "転送の準備中…"
            : State == CloudTransferState.Uploading ? "アバターを一時アップロードしています。"
            : State == CloudTransferState.Ready ? "スマホのVR Vlogで「PCから受け取る」を開き、QRを読み取ってください。"
            : State == CloudTransferState.Completed ? "スマホへの保存が完了しました。クラウドの転送用コピーを削除しました。"
            : State == CloudTransferState.Canceled ? "転送を中止しました。"
            : State == CloudTransferState.Expired ? "受取期限が切れました。新しいQRを作成できます。"
            : "転送を続けられませんでした。接続を確認して新しいQRを作成してください。混雑時は時間を置いて試してください。";

        internal CloudVrmTransferSession(CloudVrmTransferSource source, ICloudTransferTransport transport = null)
        { this.source = source ?? throw new ArgumentNullException(nameof(source)); this.transport = transport ?? new CloudTransferTransport(); }

        internal async Task UploadAsync(CancellationToken cancellation = default)
        {
            lock (gate) { if (started || disposed) throw new InvalidOperationException("転送は開始済みです。"); started = true; }
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cancellation))
            {
                var ct = linked.Token;
                try
                {
                    var fields = await Send("POST", "/v2/transfers", null, Encoding.UTF8.GetBytes(CloudTransferProtocol.CreateBody(source.Name, source.Size, source.FileHash)), "application/json", ct).ConfigureAwait(false);
                    fields.Exact("v", "id", "token", "uploadToken", "expiresAt", "partSize");
                    var newId = fields.Text("id"); var token = fields.Text("token"); var owner = fields.Text("uploadToken"); var expiry = fields.Number("expiresAt");
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    if (fields.Number("v") != 2 || !CloudTransferProtocol.IsHex(newId, 32) || !CloudTransferProtocol.IsToken(token) || !CloudTransferProtocol.IsToken(owner)
                        || token == owner || fields.Number("partSize") != CloudTransferProtocol.PartSize || expiry <= now || expiry > now + 16 * 60) throw CloudTransferProtocol.Invalid();
                    lock (gate) { id = newId; readToken = token; uploadToken = owner; expiresAt = expiry; if (!disposed && state != CloudTransferState.Canceled) state = CloudTransferState.Uploading; }
                    ct.ThrowIfCancellationRequested();
                    using (var reader = source.OpenRead())
                    using (var hash = SHA256.Create())
                    {
                        lock (gate) { ct.ThrowIfCancellationRequested(); activeReader = reader; }
                        var remaining = source.Size; var partNumber = 1;
                        while (remaining > 0)
                        {
                            ct.ThrowIfCancellationRequested(); CheckExpiry();
                            var part = new byte[(int)Math.Min(CloudTransferProtocol.PartSize, remaining)]; var offset = 0;
                            while (offset < part.Length)
                            {
                                var count = await reader.ReadAsync(part, offset, part.Length - offset, ct).ConfigureAwait(false);
                                if (count == 0) throw CloudTransferProtocol.Invalid(); offset += count;
                            }
                            hash.TransformBlock(part, 0, part.Length, null, 0);
                            await Send("PUT", Path + "/parts/" + partNumber.ToString(CultureInfo.InvariantCulture), uploadToken, part, "application/octet-stream", ct, false).ConfigureAwait(false);
                            lock (gate) transferred += part.Length;
                            remaining -= part.Length; partNumber++;
                        }
                        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        if (reader.ReadByte() != -1 || LanTransferProtocol.Hex(hash.Hash) != source.FileHash) throw CloudTransferProtocol.Invalid();
                    }
                    lock (gate) activeReader = null;
                    CheckExpiry();
                    var published = await Send("POST", Path + "/publish", uploadToken, Array.Empty<byte>(), "application/octet-stream", ct).ConfigureAwait(false);
                    published.Exact("v", "id", "token", "name", "size", "sha256", "expiresAt");
                    if (published.Number("v") != 2 || published.Text("id") != id || published.Text("token") != readToken || published.Text("name") != source.Name
                        || published.Number("size") != source.Size || published.Text("sha256") != source.FileHash || published.Number("expiresAt") != expiresAt) throw CloudTransferProtocol.Invalid();
                    lock (gate)
                    {
                        ct.ThrowIfCancellationRequested();
                        qr = CloudTransferProtocol.Qr(id, readToken, source.Name, source.Size, source.FileHash, expiresAt);
                        state = CloudTransferState.Ready;
                    }
                }
                catch (Exception)
                {
                    lock (gate) { activeReader = null; qr = null; if (state != CloudTransferState.Canceled && state != CloudTransferState.Expired) state = CloudTransferState.Failed; }
                    await DeleteRemote().ConfigureAwait(false);
                    throw new InvalidOperationException(Message); // Never disclose service bodies, credentials or paths.
                }
            }
        }

        internal async Task RefreshAsync(CancellationToken cancellation = default)
        {
            if (State != CloudTransferState.Ready) return;
            CheckExpiry();
            var fields = await Send("GET", Path, uploadToken, null, null, cancellation).ConfigureAwait(false);
            var remote = fields.Text("state");
            lock (gate)
            {
                if (state != CloudTransferState.Ready) return;
                switch (remote)
                {
                    case "ready": return;
                    case "completed": state = CloudTransferState.Completed; break;
                    case "cancelled": state = CloudTransferState.Canceled; break;
                    case "expired": state = CloudTransferState.Expired; break;
                    case "failed": state = CloudTransferState.Failed; break;
                    default: throw CloudTransferProtocol.Invalid();
                }
                qr = null;
            }
        }
        private string Path => "/v2/transfers/" + id;
        private void CheckExpiry()
        {
            lock (gate)
                if (expiresAt != 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expiresAt)
                { state = CloudTransferState.Expired; qr = null; throw new InvalidOperationException(Message); }
        }
        private async Task<CloudTransferProtocol.Fields> Send(string method, string path, string token, byte[] body, string type, CancellationToken ct, bool parse = true)
        {
            var response = await transport.SendAsync(method, path, token, body, type, ct).ConfigureAwait(false);
            if (response.StatusCode < 200 || response.StatusCode >= 300) throw CloudTransferProtocol.Invalid();
            return parse ? CloudTransferProtocol.Fields.Read(response.Body) : null;
        }
        private async Task DeleteRemote()
        {
            string transferId, owner;
            lock (gate) { transferId = id; owner = uploadToken; }
            if (transferId == null || owner == null) return;
            try
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    await transport.SendAsync("POST", "/v2/transfers/" + transferId + "/cancel", owner, Array.Empty<byte>(), "application/octet-stream", timeout.Token).ConfigureAwait(false);
            }
            catch { /* The fixed server expiry also cleans up when notification cannot reach it. */ }
        }
        internal async Task CancelAsync()
        {
            lock (gate) { if (Terminal) return; state = CloudTransferState.Canceled; qr = null; stop.Cancel(); activeReader?.Dispose(); activeReader = null; }
            await DeleteRemote().ConfigureAwait(false);
        }
        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; stop.Cancel(); activeReader?.Dispose(); activeReader = null; if (!Terminal) state = CloudTransferState.Canceled; qr = null; }
            _ = DeleteRemote();
        }
    }
}
