using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using ZXing;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace VRVlog.LilToonExporter.LanTransfer
{
    public sealed class CloudVrmTransferWindow : EditorWindow
    {
        public static bool IsAvailable => CloudTransferAvailability.Enabled;
        private static readonly HashSet<string> OwnedSnapshots = new HashSet<string>(StringComparer.Ordinal);
        [NonSerialized] private CloudVrmTransferSource source;
        [NonSerialized] private CloudVrmTransferSession session;
        [NonSerialized] private Task uploading;
        [NonSerialized] private Task polling;
        [NonSerialized] private Texture2D qrTexture;
        private string error;
        private double nextPoll;
        private Vector2 scroll;

#if UNITY_EDITOR
        private static void OpenDevelopmentTransfer()
        {
            CloudTransferAvailability.RequireEnabled();
            var selected = EditorUtility.OpenFilePanel(ExporterLocalization.T("書き出し済みVRMを選択"), "", "vrm");
            try { OpenSavedVrmForDevelopment(selected); }
            catch
            {
                // File/provider exceptions can include private paths or values.
                EditorUtility.DisplayDialog(ExporterLocalization.T("スマホに送る"), ExporterLocalization.T("転送用のコピーを準備できませんでした。256 MiB以下の書き出し済みVRMを選択してください。"), ExporterLocalization.T("閉じる"));
            }
        }

        private static bool CanOpenDevelopmentTransfer() => IsAvailable;
#endif
        public static void OpenSavedVrmForDevelopment(string savedVrmPath)
        {
            CloudTransferAvailability.RequireEnabled();
            if (string.IsNullOrEmpty(savedVrmPath)) return;
            using (var snapshot = CloudDevelopmentSnapshot.CopySavedVrm(savedVrmPath))
            {
                OwnedSnapshots.Add(snapshot.SnapshotPath);
                try
                {
                    Show(snapshot.SnapshotPath, snapshot.Name);
                    snapshot.ReleaseOwnership();
                }
                finally { OwnedSnapshots.Remove(snapshot.SnapshotPath); }
            }
        }

        public static string CreateSnapshotPath()
        {
            CloudTransferAvailability.RequireEnabled();
            var path = Path.Combine(Path.GetTempPath(), "VRVlogCloudTransfers", Guid.NewGuid().ToString("N") + ".vrm");
            OwnedSnapshots.Add(path);
            return path;
        }

        public static void Show(string ownedSnapshotPath, string name)
        {
            if (!CloudTransferAvailability.Enabled)
            {
                // A caller may still own a snapshot issued by an earlier
                // version. Ordinary saved VRMs are never in this registry.
                if (OwnedSnapshots.Remove(ownedSnapshotPath)) LanVrmTransferServer.TryDelete(ownedSnapshotPath);
                CloudTransferAvailability.RequireEnabled();
            }
            // Only the explicitly generated output path can be adopted. An
            // ordinary saved VRM passed to this public entry is never deleted.
            if (!OwnedSnapshots.Remove(ownedSnapshotPath)) throw new InvalidOperationException("Exporterで転送用のVRMを書き出してください。");
            CloudVrmTransferSource prepared = null;
            CloudVrmTransferWindow window = null;
            try
            {
                // Validate the new owned copy before replacing an existing
                // window/session. A failed selection leaves that session live.
                prepared = new CloudVrmTransferSource(ownedSnapshotPath, name);
                window = GetWindow<CloudVrmTransferWindow>(false, ExporterLocalization.T("スマホに送る"));
                window.StopAndClean();
                window.source = prepared;
                window.minSize = new Vector2(430, 650);
                window.Show();
            }
            catch
            {
                if (window != null && ReferenceEquals(window.source, prepared)) window.StopAndClean();
                else prepared?.Dispose();
                LanVrmTransferServer.TryDelete(ownedSnapshotPath);
                throw new InvalidOperationException("転送用のVRMを開けませんでした。256 MiB以下の書き出し済みVRMを選択してください。");
            }
        }

        private void OnEnable()
        {
            if (!CloudTransferAvailability.Enabled)
            {
                StopAndClean();
                return;
            }
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
        private void Begin()
        {
            if (!CloudTransferAvailability.Enabled)
            {
                StopAndClean();
                error = CloudTransferAvailability.DisabledMessage;
                return;
            }
            if (source == null || uploading != null || session != null && !session.Terminal) return;
            session?.Dispose();
            session = new CloudVrmTransferSession(source);
            error = null; ClearQr();
            // File I/O and multipart upload run away from the Editor thread.
            var current = session;
            uploading = Task.Run(() => current.UploadAsync());
        }
        private void Poll()
        {
            if (!CloudTransferAvailability.Enabled)
            {
                StopAndClean();
                return;
            }
            // Expiry cannot depend on a status request finishing. This also
            // interrupts a pending poll and unlocks fresh-session issuance.
            session?.ExpireIfDue();
            if (uploading?.IsCompleted == true)
            {
                if (uploading.IsFaulted) { _ = uploading.Exception; error = null; }
                uploading = null;
                if (session?.State == CloudTransferState.Ready)
                {
                    try { qrTexture = RenderQr(session.Qr); }
                    catch { session.Dispose(); error = "QRを表示できませんでした。新しいQRを作成してください。"; }
                    nextPoll = EditorApplication.timeSinceStartup;
                }
            }
            if (polling?.IsCompleted == true)
            {
                if (polling.IsFaulted)
                {
                    _ = polling.Exception;
                    error = session?.Terminal == true ? null : "転送の状態を確認できませんでした。インターネット接続を確認してください。";
                }
                else error = null;
                polling = null;
            }
            if (session?.State == CloudTransferState.Ready && polling == null && EditorApplication.timeSinceStartup >= nextPoll)
            {
                nextPoll = EditorApplication.timeSinceStartup + 5;
                polling = session.RefreshAsync();
            }
            if (session?.Terminal == true) ClearQr();
            if (session != null || uploading != null) Repaint();
        }
        private static Texture2D RenderQr(string payload)
        {
            var matrix = new QRCodeWriter().encode(payload, BarcodeFormat.QR_CODE, 0, 0,
                new Dictionary<EncodeHintType, object> { [EncodeHintType.ERROR_CORRECTION] = ErrorCorrectionLevel.M, [EncodeHintType.MARGIN] = 4, [EncodeHintType.CHARACTER_SET] = "UTF-8" });
            var pixels = new Color32[matrix.Width * matrix.Height];
            for (var y = 0; y < matrix.Height; y++) for (var x = 0; x < matrix.Width; x++)
                pixels[(matrix.Height - y - 1) * matrix.Width + x] = matrix[x, y] ? new Color32(0, 0, 0, 255) : new Color32(255, 255, 255, 255);
            var texture = new Texture2D(matrix.Width, matrix.Height, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
            texture.SetPixels32(pixels); texture.Apply(false, true); return texture;
        }
        private void OnGUI()
        {
            if (!CloudTransferAvailability.Enabled)
            {
                StopAndClean();
                EditorGUILayout.HelpBox(ExporterLocalization.T(CloudTransferAvailability.DisabledMessage), MessageType.Info);
                return;
            }
            using (var scrolling = new EditorGUILayout.ScrollViewScope(scroll))
            {
                scroll = scrolling.scrollPosition;
                EditorGUILayout.HelpBox(ExporterLocalization.T("アバターをPCで暗号化してからクラウドに一時保存し、スマホで復号します。復号鍵はQRにだけ含まれ、転送サービスには送りません。受取期限は転送作成時から3分です。アップロード中も期限が進みます。完了・中止・期限切れで暗号化コピーを削除します。"), MessageType.Info);
                EditorGUILayout.LabelField(source?.Name ?? ExporterLocalization.T("メニューから書き出し済みVRMを選択してください。"), EditorStyles.wordWrappedLabel);
                EditorGUILayout.HelpBox(ExporterLocalization.T("PCとスマホにインターネット接続が必要です。同じWi-Fiは不要です。QRを持つ人はアバターを受け取れるため、共有・撮影しないでください。スマホではVR Vlog内のカメラで読み取ります。"), MessageType.None);
                if (ExtraMessage != null) EditorGUILayout.HelpBox(ExporterLocalization.T(ExtraMessage), MessageType.Error);
                using (new EditorGUI.DisabledScope(source == null || uploading != null || session != null && !session.Terminal))
                    if (GUILayout.Button(ExporterLocalization.T(session == null ? "アップロードしてQRを表示" : "新しいQRを作成"), GUILayout.Height(32))) Begin();
                if (session != null)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.HelpBox(ExporterLocalization.T(session.Message), session.State == CloudTransferState.Failed ? MessageType.Error : MessageType.Info);
                    if (session.State == CloudTransferState.Uploading && source != null)
                    {
                        var rect = EditorGUILayout.GetControlRect(false, 20);
                        EditorGUI.ProgressBar(rect, (float)session.Transferred / Math.Max(1, session.UploadSize), string.Format(ExporterLocalization.T("{0:N0} / {1:N0} バイト（暗号化済み）"), session.Transferred, session.UploadSize));
                    }
                    if (!session.Terminal && session.ExpiresAt != 0)
                        EditorGUILayout.LabelField(ExporterLocalization.T("残り時間"), string.Format(ExporterLocalization.T("{0} 秒"), Math.Max(0, session.ExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds())));
                    if (qrTexture != null)
                    {
                        var available = Mathf.Min(position.width - 48, 380);
                        var size = Mathf.Floor(available / qrTexture.width) * qrTexture.width;
                        var rect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false));
                        GUI.DrawTexture(rect, qrTexture, ScaleMode.StretchToFill);
                    }
                    if (!session.Terminal && GUILayout.Button(ExporterLocalization.T("転送を中止"), GUILayout.Height(32)))
                    {
                        _ = session.CancelAsync(); ClearQr();
                        error = "中止通知が届かない場合も、受取期限でクラウドのコピーは削除されます。";
                    }
                }
            }
        }
        // Session failures already have one message below the retry button.
        internal string ExtraMessage => string.IsNullOrEmpty(error) || error == session?.Message ? null : error;
        private void ClearQr() { if (qrTexture != null) DestroyImmediate(qrTexture); qrTexture = null; }
        private void StopAndClean()
        {
            session?.Dispose(); session = null;
            // Observe abandoned task failures without logging transport details.
            if (uploading != null) _ = uploading.ContinueWith(done => { _ = done.Exception; }, TaskScheduler.Default);
            if (polling != null) _ = polling.ContinueWith(done => { _ = done.Exception; }, TaskScheduler.Default);
            uploading = null; polling = null;
            source?.Dispose(); source = null;
            error = null; ClearQr();
        }
    }
}
