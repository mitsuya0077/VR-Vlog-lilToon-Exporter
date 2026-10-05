using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using ZXing;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace VRVlog.LilToonExporter.LanTransfer
{
    public sealed class LanVrmTransferWindow : EditorWindow
    {
        private const string LifetimePreference = "VRVlog.LanTransfer.LifetimeMinutes";
        [NonSerialized] private LanVrmTransferServer server;
        [NonSerialized] private Task<LanVrmTransferServer> starting;
        [NonSerialized] private CancellationTokenSource startupCancellation;
        [NonSerialized] private FileStream startupSnapshot;
        [NonSerialized] private Texture2D qrTexture;
        [NonSerialized] private string snapshotPath;
        [NonSerialized] private string displayName;
        private string error;
        private IPAddress[] addresses = Array.Empty<IPAddress>();
        private string[] addressLabels = Array.Empty<string>();
        private int selectedAddress;
        private int lifetimeMinutes = 10;
        private Vector2 scroll;

        public static string CreateSnapshotPath()
        {
            LanTransferAvailability.RequireEnabled();
            return Path.Combine(Path.GetTempPath(), "VRVlogLanTransfers", Guid.NewGuid().ToString("N") + ".vrm");
        }

        public static void Show(string path, string name)
        {
            LanTransferAvailability.RequireEnabled();
            var window = GetWindow<LanVrmTransferWindow>(false, "スマホに送る");
            window.StopAndClean();
            window.snapshotPath = path;
            window.displayName = LanTransferProtocol.DisplayName(name);
            window.error = null;
            window.RefreshAddresses();
            window.minSize = new Vector2(430, 650);
            window.Show();
        }

        private void OnEnable()
        {
            if (!LanTransferAvailability.Enabled)
            {
                StopAndClean();
                addresses = Array.Empty<IPAddress>();
                addressLabels = Array.Empty<string>();
                error = null;
                return;
            }
            lifetimeMinutes = Mathf.Clamp(EditorPrefs.GetInt(LifetimePreference, 10), 1, 60);
            RefreshAddresses();
            EditorApplication.update += Poll;
            EditorApplication.quitting += StopAndClean;
            AssemblyReloadEvents.beforeAssemblyReload += StopAndClean;
        }

        private void OnDisable()
        {
            EditorApplication.update -= Poll;
            EditorApplication.quitting -= StopAndClean;
            AssemblyReloadEvents.beforeAssemblyReload -= StopAndClean;
            StopAndClean();
        }

        private void RefreshAddresses()
        {
            if (!LanTransferAvailability.Enabled) return;
            var found = new List<(IPAddress Address, string Label)>();
            try
            {
                foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType != NetworkInterfaceType.Ethernet
                        && network.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                    foreach (var unicast in network.GetIPProperties().UnicastAddresses)
                        if (LanTransferProtocol.IsPrivateIPv4(unicast.Address)) found.Add((unicast.Address, network.Name + " — " + unicast.Address));
                }
            }
            catch (NetworkInformationException) { error = "LANの接続情報を取得できませんでした。PCのネットワーク接続を確認してください。"; }
            addresses = found.Select(item => item.Address).ToArray();
            addressLabels = found.Select(item => item.Label).ToArray();
            selectedAddress = Math.Min(selectedAddress, Math.Max(0, addresses.Length - 1));
        }

        private void Begin()
        {
            if (!LanTransferAvailability.Enabled)
            {
                StopAndClean();
                error = LanTransferAvailability.DisabledMessage;
                return;
            }
            if (addresses.Length == 0 || starting != null || server != null || string.IsNullOrEmpty(snapshotPath)) return;
            var path = snapshotPath;
            var name = displayName;
            var address = addresses[selectedAddress];
            var lifetime = TimeSpan.FromMinutes(lifetimeMinutes);
            try { startupSnapshot = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan); }
            catch
            {
                error = "転送用のVRMを開けませんでした。再度「スマホに送る」で書き出してください。";
                return;
            }
            var ownedSnapshot = startupSnapshot;
            startupCancellation = new CancellationTokenSource();
            var cancellation = startupCancellation.Token;
            error = null;
            starting = Task.Run(() =>
            {
                var created = LanVrmTransferServer.Start(path, name, address, lifetime, ownedSnapshot);
                if (!cancellation.IsCancellationRequested) return created;
                created.Dispose();
                throw new OperationCanceledException();
            });
        }

        private void Poll()
        {
            if (!LanTransferAvailability.Enabled)
            {
                StopAndClean();
                return;
            }
            if (starting != null && starting.IsCompleted)
            {
                var completed = starting;
                starting = null;
                if (completed.Status == TaskStatus.RanToCompletion)
                {
                    server = completed.Result;
                    startupSnapshot = null; // Ownership moves to the ready server.
                    try { if (!server.Terminal) qrTexture = RenderQr(server.Qr); }
                    catch
                    {
                        // QR content is a credential. Never allow a library exception to log that content.
                        server.Dispose();
                        error = "QRを表示できませんでした。再度「スマホに送る」で書き出してください。";
                    }
                }
                else
                {
                    // Transport exceptions may contain paths or endpoint details; never display/log raw exception text.
                    _ = completed.Exception;
                    startupSnapshot?.Dispose();
                    startupSnapshot = null;
                    error = "待受を開始できませんでした。256 MiB以下のVRM、選択したLAN接続、通信許可を確認し、再度「スマホに送る」で書き出してください。";
                }
                startupCancellation?.Dispose();
                startupCancellation = null;
            }
            if (server?.Terminal == true && qrTexture != null) { DestroyImmediate(qrTexture); qrTexture = null; }
            if (server != null || starting != null) Repaint();
        }

        private static Texture2D RenderQr(string payload)
        {
            var matrix = new QRCodeWriter().encode(payload, BarcodeFormat.QR_CODE, 0, 0,
                new Dictionary<EncodeHintType, object> { [EncodeHintType.ERROR_CORRECTION] = ErrorCorrectionLevel.M, [EncodeHintType.MARGIN] = 4, [EncodeHintType.CHARACTER_SET] = "UTF-8" });
            var pixels = new Color32[matrix.Width * matrix.Height];
            for (var y = 0; y < matrix.Height; y++)
                for (var x = 0; x < matrix.Width; x++)
                    pixels[(matrix.Height - y - 1) * matrix.Width + x] = matrix[x, y] ? new Color32(0, 0, 0, 255) : new Color32(255, 255, 255, 255);
            var texture = new Texture2D(matrix.Width, matrix.Height, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private void OnGUI()
        {
            if (!LanTransferAvailability.Enabled)
            {
                StopAndClean();
                EditorGUILayout.HelpBox(LanTransferAvailability.DisabledMessage, MessageType.Info);
                return;
            }
            using (var scrolling = new EditorGUILayout.ScrollViewScope(scroll))
            {
                scroll = scrolling.scrollPosition;
                EditorGUILayout.HelpBox("アバターはPCからスマホへ直接転送され、この転送でクラウドに送信・保存されません", MessageType.Info);
                EditorGUILayout.LabelField(displayName ?? "アバターを選んで、Exporterの「スマホに送る」を押してください。", EditorStyles.wordWrappedLabel);
                EditorGUILayout.HelpBox("PCとiPhoneを同じLANに接続してください。PCは有線LANでも利用できます。VRVlogのカメラ・ローカルネットワーク権限とWindowsの通信を許可してください。QRには受取用の秘密情報が含まれます。共有・撮影しないでください。", MessageType.None);
                if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
                using (new EditorGUI.DisabledScope(server != null || starting != null))
                {
                    if (addresses.Length > 0) selectedAddress = EditorGUILayout.Popup("PCのLAN接続", selectedAddress, addressLabels);
                    else EditorGUILayout.HelpBox("対応するLAN接続が見つかりません。PCの有線LANまたはWi-FiにプライベートIPv4が必要です。VPN・ゲストネットワークは避けてください。", MessageType.Warning);
                    if (GUILayout.Button("LAN接続を再確認")) RefreshAddresses();
                    var minutes = Mathf.Clamp(EditorGUILayout.IntField("受取期限（分・1〜60）", lifetimeMinutes), 1, 60);
                    if (minutes != lifetimeMinutes) { lifetimeMinutes = minutes; EditorPrefs.SetInt(LifetimePreference, minutes); }
                    using (new EditorGUI.DisabledScope(addresses.Length == 0 || string.IsNullOrEmpty(snapshotPath)))
                        if (GUILayout.Button("待受を開始してQRを表示", GUILayout.Height(32))) Begin();
                }
                if (starting != null) EditorGUILayout.LabelField("安全な転送の準備中…");
                if (server != null)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.HelpBox(server.Message, server.State == TransferState.Failed ? MessageType.Error : MessageType.Info);
                    if (!server.Terminal)
                    {
                        EditorGUILayout.LabelField("接続先", server.Host + ":" + server.Port);
                        EditorGUILayout.LabelField("残り時間", Mathf.CeilToInt((float)server.RemainingSeconds) + " 秒");
                        var rect = EditorGUILayout.GetControlRect(false, 20);
                        EditorGUI.ProgressBar(rect, (float)server.Transferred / server.Size, server.Transferred.ToString("N0") + " / " + server.Size.ToString("N0") + " バイト");
                        if (qrTexture != null)
                        {
                            var available = Mathf.Min(position.width - 48, 380);
                            var size = Mathf.Floor(available / qrTexture.width) * qrTexture.width;
                            var qrRect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false));
                            GUI.DrawTexture(qrRect, qrTexture, ScaleMode.StretchToFill);
                        }
                        EditorGUILayout.HelpBox("接続できない場合：同じLAN、選択した接続先、Windows Firewallのプライベートネットワーク許可、iPhoneのローカルネットワーク許可を確認してください。ゲストWi-Fiの端末間通信制限・VPN・会社のネットワーク制限が原因になることもあります。Firewall全体を無効にする必要はありません。", MessageType.None);
                        if (GUILayout.Button("Windowsの狭い通信許可コマンドをコピー"))
                        {
                            var program = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName.Replace("'", "''");
                            // No QR/token/key values enter the clipboard or command; these are firewall scope only.
                            EditorGUIUtility.systemCopyBuffer = "New-NetFirewallRule -Name 'VRVlogLanTransfer-" + server.Port + "' -DisplayName 'VRVlog LAN Transfer' -Direction Inbound -Action Allow -Profile Private -Protocol TCP -Program '"
                                + program + "' -LocalAddress '" + server.Host + "' -LocalPort " + server.Port + " -RemoteAddress LocalSubnet -EdgeTraversalPolicy Block";
                        }
                        EditorGUILayout.LabelField("通信許可コマンドは管理者PowerShellで実行します。転送後は Remove-NetFirewallRule -Name 'VRVlogLanTransfer-" + server.Port + "' で削除してください。", EditorStyles.wordWrappedLabel);
                        if (GUILayout.Button("キャンセル・待受停止", GUILayout.Height(32))) { server.Dispose(); Poll(); }
                    }
                }
            }
        }

        private void StopAndClean()
        {
            startupCancellation?.Cancel();
            startupCancellation?.Dispose();
            startupCancellation = null;
            // Before reload, release the shared handle synchronously even while
            // background certificate generation is still running.
            startupSnapshot?.Dispose();
            startupSnapshot = null;
            if (starting != null)
            {
                var abandoned = starting;
                _ = abandoned.ContinueWith(done => { if (done.Status == TaskStatus.RanToCompletion) done.Result.Dispose(); else _ = done.Exception; }, TaskScheduler.Default);
                starting = null;
            }
            server?.Dispose();
            server = null;
            if (qrTexture != null) DestroyImmediate(qrTexture);
            qrTexture = null;
            if (!string.IsNullOrEmpty(snapshotPath)) LanVrmTransferServer.TryDelete(snapshotPath);
            snapshotPath = null;
        }
    }
}
