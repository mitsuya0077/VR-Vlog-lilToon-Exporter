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
        private static readonly HashSet<string> OwnedSnapshots = new HashSet<string>(StringComparer.Ordinal);
        [NonSerialized] private CloudVrmTransferSource source;
        [NonSerialized] private CloudVrmTransferSession session;
        [NonSerialized] private Task uploading;
        [NonSerialized] private Task polling;
        [NonSerialized] private Texture2D qrTexture;
        private string error;
        private double nextPoll;
        private Vector2 scroll;

        public static string CreateSnapshotPath()
        {
            var path = Path.Combine(Path.GetTempPath(), "VRVlogCloudTransfers", Guid.NewGuid().ToString("N") + ".vrm");
            OwnedSnapshots.Add(path);
            return path;
        }

        public static void Show(string ownedSnapshotPath, string name)
        {
            // Only the explicitly generated output path can be adopted. An
            // ordinary saved VRM passed to this public entry is never deleted.
            if (!OwnedSnapshots.Remove(ownedSnapshotPath)) throw new InvalidOperationException("Exporterで転送用のVRMを書き出してください。");
            var window = GetWindow<CloudVrmTransferWindow>(false, "スマホに送る");
            window.StopAndClean();
            try { window.source = new CloudVrmTransferSource(ownedSnapshotPath, name); }
            catch
            {
                LanVrmTransferServer.TryDelete(ownedSnapshotPath);
                window.error = "転送用のVRMを開けませんでした。256 MiB以下のVRMを書き出してください。";
            }
            window.minSize = new Vector2(430, 650);
            window.Show();
        }

        private void OnEnable()
        {
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
            // Expiry cannot depend on a status request finishing. This also
            // interrupts a pending poll and unlocks fresh-session issuance.
            session?.ExpireIfDue();
            if (uploading?.IsCompleted == true)
            {
                if (uploading.IsFaulted) { _ = uploading.Exception; error = session?.Message; }
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
            using (var scrolling = new EditorGUILayout.ScrollViewScope(scroll))
            {
                scroll = scrolling.scrollPosition;
                EditorGUILayout.HelpBox("アバターをクラウドに一時保存し、スマホへ転送します。受取期限は転送作成時から3分です。アップロード中も期限が進みます。完了・中止・期限切れで転送用コピーを削除します。", MessageType.Info);
                EditorGUILayout.LabelField(source?.Name ?? "Exporterの「スマホに送る」でVRMを書き出してください。", EditorStyles.wordWrappedLabel);
                EditorGUILayout.HelpBox("PCとスマホにインターネット接続が必要です。同じWi-Fiは不要です。QRを持つ人はアバターを受け取れるため、共有・撮影しないでください。スマホではVR Vlog内のカメラで読み取ります。", MessageType.None);
                if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
                using (new EditorGUI.DisabledScope(source == null || uploading != null || session != null && !session.Terminal))
                    if (GUILayout.Button(session == null ? "アップロードしてQRを表示" : "新しいQRを作成", GUILayout.Height(32))) Begin();
                if (session != null)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.HelpBox(session.Message, session.State == CloudTransferState.Failed ? MessageType.Error : MessageType.Info);
                    if (session.State == CloudTransferState.Uploading && source != null)
                    {
                        var rect = EditorGUILayout.GetControlRect(false, 20);
                        EditorGUI.ProgressBar(rect, (float)session.Transferred / source.Size, session.Transferred.ToString("N0") + " / " + source.Size.ToString("N0") + " バイト");
                    }
                    if (!session.Terminal && session.ExpiresAt != 0)
                        EditorGUILayout.LabelField("残り時間", Math.Max(0, session.ExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) + " 秒");
                    if (qrTexture != null)
                    {
                        var available = Mathf.Min(position.width - 48, 380);
                        var size = Mathf.Floor(available / qrTexture.width) * qrTexture.width;
                        var rect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false));
                        GUI.DrawTexture(rect, qrTexture, ScaleMode.StretchToFill);
                    }
                    if (!session.Terminal && GUILayout.Button("転送を中止", GUILayout.Height(32)))
                    {
                        _ = session.CancelAsync(); ClearQr();
                        error = "中止通知が届かない場合も、受取期限でクラウドのコピーは削除されます。";
                    }
                }
            }
        }
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
