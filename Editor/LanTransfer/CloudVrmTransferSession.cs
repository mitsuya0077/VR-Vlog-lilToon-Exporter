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
        private static readonly HttpClient Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(90) };
        public async Task<CloudTransferResponse> SendAsync(string method, string path, string token, byte[] body, string contentType, CancellationToken cancellation)
        {
            // Every production request goes to the fixed service using the OS
            // certificate trust. Redirects and QR-supplied hosts are forbidden.
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using (var request = new HttpRequestMessage(new HttpMethod(method), CloudTransferProtocol.ServiceUrl + path))
            {
                // ResponseHeadersRead does not extend HttpClient.Timeout to
                // body reads. Bound the entire exchange, including the body.
                deadline.CancelAfter(TimeSpan.FromSeconds(90));
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
            Name = CloudTransferProtocol.DisplayName(name);
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
        private readonly Func<long> now;
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private string id, readToken, uploadToken;
        private FileStream activeReader;
        private CloudEncryptedSnapshot encrypted;
        private CloudTransferState state = CloudTransferState.Preparing;
        private string qr, failureMessage;
        private long transferred, expiresAt, uploadSize;
        private bool started, disposed;
        internal CloudTransferState State { get { lock (gate) return state; } }
        internal string Qr { get { lock (gate) return qr; } }
        internal long Transferred { get { lock (gate) return transferred; } }
        internal long UploadSize { get { lock (gate) return uploadSize; } }
        internal bool HasEncryptionKey { get { lock (gate) return encrypted?.HasKey == true; } }
        internal long ExpiresAt { get { lock (gate) return expiresAt; } }
        internal bool Terminal => State >= CloudTransferState.Completed;
        internal string Message => State == CloudTransferState.Preparing ? "転送用のアバターを暗号化しています…"
            : State == CloudTransferState.Uploading ? "暗号化したアバターを一時アップロードしています。"
            : State == CloudTransferState.Ready ? "スマホのVR Vlogで「PCから受け取る」を開き、QRを読み取ってください。"
            : State == CloudTransferState.Completed ? "スマホへの保存が完了しました。クラウドの転送用コピーは削除対象になりました。"
            : State == CloudTransferState.Canceled ? "転送を中止しました。"
            : State == CloudTransferState.Expired ? "受取期限が切れました。新しいQRを作成できます。"
            : FailureMessage;
        private string FailureMessage { get { lock (gate) return failureMessage ?? "転送サービスでエラーが発生しました。新しいQRを作成してください。"; } }

        internal CloudVrmTransferSession(CloudVrmTransferSource source, ICloudTransferTransport transport = null, Func<long> now = null)
        {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            this.transport = transport ?? new CloudTransferTransport();
            this.now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        internal async Task UploadAsync(CancellationToken cancellation = default)
        {
            lock (gate) { if (started || disposed) throw new InvalidOperationException("転送は開始済みです。"); started = true; }
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cancellation))
            {
                var ct = linked.Token;
                var stage = "暗号化の準備";
                try
                {
                    var protectedSnapshot = await CloudEncryptedSnapshot.CreateAsync(source, ct).ConfigureAwait(false);
                    lock (gate)
                    {
                        if (ct.IsCancellationRequested || disposed) { protectedSnapshot.Dispose(); ct.ThrowIfCancellationRequested(); throw new ObjectDisposedException(nameof(CloudVrmTransferSession)); }
                        encrypted = protectedSnapshot; uploadSize = protectedSnapshot.Size;
                    }
                    stage = "転送の作成";
                    var fields = await Send("POST", "/v3/transfers", null, Encoding.UTF8.GetBytes(CloudTransferProtocol.CreateBody(CloudEncryptedSnapshot.WireName, protectedSnapshot.Size, protectedSnapshot.FileHash)), "application/json", ct).ConfigureAwait(false);
                    fields.Exact("v", "id", "token", "uploadToken", "expiresAt", "partSize");
                    var newId = fields.Text("id"); var token = fields.Text("token"); var owner = fields.Text("uploadToken"); var expiry = fields.Number("expiresAt");
                    var currentTime = now();
                    if (!CloudTransferProtocol.IsHex(newId, 32) || !CloudTransferProtocol.IsToken(owner)) throw CloudTransferProtocol.Invalid();
                    // A valid create response has already reserved server storage.
                    // Retain authenticated cleanup credentials even when its
                    // lifetime is rejected before any VRM bytes are uploaded.
                    lock (gate) { id = newId; uploadToken = owner; }
                    if (fields.Number("v") != 3 || !CloudTransferProtocol.IsToken(token) || token == owner || fields.Number("partSize") != CloudTransferProtocol.PartSize) throw CloudTransferProtocol.Invalid();
                    if (expiry <= currentTime || expiry > currentTime + (CloudTransferProtocol.LifetimeMinutes + 1) * 60) throw new SafeTransferFailure("PCの日時が転送サービスと合っていません。OSの日時の自動設定を確認してください。");
                    lock (gate) { readToken = token; expiresAt = expiry; if (!disposed && state != CloudTransferState.Canceled) state = CloudTransferState.Uploading; }
                    ct.ThrowIfCancellationRequested();
                    stage = "暗号化ファイルの読み取り";
                    using (var reader = protectedSnapshot.OpenRead())
                    using (var hash = SHA256.Create())
                    {
                        lock (gate) { ct.ThrowIfCancellationRequested(); activeReader = reader; }
                        var remaining = protectedSnapshot.Size; var partNumber = 1;
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
                            stage = "暗号化ファイルのアップロード";
                            await Send("PUT", Path + "/parts/" + partNumber.ToString(CultureInfo.InvariantCulture), uploadToken, part, "application/octet-stream", ct, false).ConfigureAwait(false);
                            lock (gate) transferred += part.Length;
                            remaining -= part.Length; partNumber++;
                        }
                        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        if (reader.ReadByte() != -1 || LanTransferProtocol.Hex(hash.Hash) != protectedSnapshot.FileHash) throw CloudTransferProtocol.Invalid();
                    }
                    lock (gate) activeReader = null;
                    CheckExpiry();
                    stage = "QRの発行";
                    var published = await Send("POST", Path + "/publish", uploadToken, Array.Empty<byte>(), "application/octet-stream", ct).ConfigureAwait(false);
                    published.Exact("v", "id", "token", "name", "size", "sha256", "expiresAt");
                    if (published.Number("v") != 3 || published.Text("id") != id || published.Text("token") != readToken || published.Text("name") != CloudEncryptedSnapshot.WireName
                        || published.Number("size") != protectedSnapshot.Size || published.Text("sha256") != protectedSnapshot.FileHash || published.Number("expiresAt") != expiresAt) throw CloudTransferProtocol.Invalid();
                    CheckExpiry(); // A successful publication response may arrive after its deadline.
                    lock (gate)
                    {
                        ct.ThrowIfCancellationRequested();
                        qr = CloudTransferProtocol.Qr(id, readToken, source.Name, source.Size, source.FileHash, expiresAt, protectedSnapshot.KeyForQr());
                        state = CloudTransferState.Ready;
                        protectedSnapshot.ReleaseFile();
                    }
                }
                catch (Exception exception)
                {
                    lock (gate) failureMessage = SafeFailureMessage(exception, stage);
                    lock (gate) { activeReader = null; qr = null; encrypted?.Dispose(); encrypted = null; if (state != CloudTransferState.Canceled && state != CloudTransferState.Expired) state = CloudTransferState.Failed; }
                    await DeleteRemote().ConfigureAwait(false);
                    throw new InvalidOperationException(Message); // Never disclose service bodies, credentials or paths.
                }
            }
        }

        internal async Task RefreshAsync(CancellationToken cancellation = default)
        {
            if (State != CloudTransferState.Ready) return;
            CheckExpiry();
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cancellation))
            {
                var fields = await Send("GET", Path, uploadToken, null, null, linked.Token).ConfigureAwait(false);
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
                    qr = null; encrypted?.Dispose(); encrypted = null;
                }
            }
        }
        private string Path => "/v3/transfers/" + id;
        internal bool ExpireIfDue()
        {
            lock (gate)
            {
                if (state >= CloudTransferState.Completed || expiresAt == 0 || now() < expiresAt) return false;
                state = CloudTransferState.Expired; qr = null;
                stop.Cancel(); activeReader?.Dispose(); activeReader = null;
                encrypted?.Dispose(); encrypted = null;
                return true;
            }
        }
        private void CheckExpiry()
        {
            ExpireIfDue();
            if (State == CloudTransferState.Expired) throw new InvalidOperationException(Message);
        }
        private async Task<CloudTransferProtocol.Fields> Send(string method, string path, string token, byte[] body, string type, CancellationToken ct, bool parse = true)
        {
            var response = await transport.SendAsync(method, path, token, body, type, ct).ConfigureAwait(false);
            if (response.StatusCode < 200 || response.StatusCode >= 300)
                throw new SafeTransferFailure(response.StatusCode == 429
                    ? "転送サービスの利用上限または混雑により送信できません（HTTP 429）。時間を置いて試してください。"
                    : response.StatusCode == 413
                    ? "転送サイズがサービスの上限を超えています（HTTP 413）。サイズを減らしてください。"
                    : response.StatusCode == 401 || response.StatusCode == 403
                    ? "転送の認証が拒否されました（HTTP " + response.StatusCode + "）。新しいQRを作成してください。"
                    : "転送サービスが応答できませんでした（HTTP " + response.StatusCode + "）。時間を置いて新しいQRを作成してください。");
            return parse ? CloudTransferProtocol.Fields.Read(response.Body) : null;
        }
        // Only controlled text and a status code reach the UI. Exception messages,
        // paths, server bodies, QR contents and credentials never do.
        private sealed class SafeTransferFailure : Exception
        { internal SafeTransferFailure(string message) : base(message) { } }
        private static string SafeFailureMessage(Exception exception, string stage)
        {
            var reason = exception is SafeTransferFailure ? exception.Message
                : exception is UnauthorizedAccessException ? "一時ファイルへのアクセスが拒否されました。OSのアクセス権を確認してください。"
                : exception is InvalidDataException ? "転送サービスの応答を確認できませんでした。最新版の試験用パッケージで再試行してください。"
                : exception is IOException ? "一時ファイルを読み書きできませんでした。空き容量やファイルを使用中のソフトを確認してください。"
                : exception is HttpRequestException ? "転送サービスに接続できません。インターネット接続・VPN・プロキシを確認してください。"
                : exception is OperationCanceledException ? "通信が中断または時間切れになりました。接続を確認して新しいQRを作成してください。"
                : "転送データを確認できませんでした。最新版の試験用パッケージで再試行してください。";
            return stage + "で停止しました。\n" + reason;
        }

        private async Task DeleteRemote()
        {
            string transferId, owner;
            lock (gate) { transferId = id; owner = uploadToken; }
            if (transferId == null || owner == null) return;
            try
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    await transport.SendAsync("POST", "/v3/transfers/" + transferId + "/cancel", owner, Array.Empty<byte>(), "application/octet-stream", timeout.Token).ConfigureAwait(false);
            }
            catch { /* The fixed server expiry also cleans up when notification cannot reach it. */ }
        }
        internal async Task CancelAsync()
        {
            lock (gate) { if (Terminal) return; state = CloudTransferState.Canceled; qr = null; stop.Cancel(); activeReader?.Dispose(); activeReader = null; encrypted?.Dispose(); encrypted = null; }
            await DeleteRemote().ConfigureAwait(false);
        }
        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; stop.Cancel(); activeReader?.Dispose(); activeReader = null; if (!Terminal) state = CloudTransferState.Canceled; qr = null; encrypted?.Dispose(); encrypted = null; }
            _ = DeleteRemote();
        }
    }
}
