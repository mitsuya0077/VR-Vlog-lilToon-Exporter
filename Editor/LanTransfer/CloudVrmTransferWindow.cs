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
        private GUIStyle contentStyle, cardStyle, headingStyle, bodyStyle, consentStyle, linkStyle;

        internal const string PrivacyPolicyUrl = "https://vrvlog.fun/privacy/";
        internal const string PrivacyConsentPreference = "VRVlog.CloudTransfer.PrivacyConsent.v1";
        internal static bool PrivacyConsentAccepted
        {
            get => EditorPrefs.GetBool(PrivacyConsentPreference, false);
            set => EditorPrefs.SetBool(PrivacyConsentPreference, value);
        }
        internal bool CanBeginTransfer => CloudTransferAvailability.Enabled && PrivacyConsentAccepted &&
            source != null && uploading == null && (session == null || session.Terminal);

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
                var compact = window.source == null && !window.docked;
                window.StopAndClean();
                window.source = prepared;
                window.minSize = new Vector2(360, 340);
                if (compact) window.position = new Rect(window.position.x, window.position.y, 430, 460);
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
            // Recheck the saved choice at the action boundary, including retries.
            // Remembering consent never starts an upload by itself.
            if (!CanBeginTransfer) return;
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
                EnsureStyles();
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    using (new EditorGUILayout.VerticalScope(contentStyle, GUILayout.Width(Mathf.Min(position.width - 22, 560))))
                    {
                        GUILayout.Label(ExporterLocalization.T("QRコードでスマホに送る"), headingStyle);
                        EditorGUILayout.Space(14);
                        using (new EditorGUILayout.VerticalScope(cardStyle))
                        {
                            GUILayout.Label(ExporterLocalization.T("送るアバター"), EditorStyles.miniLabel);
                            GUILayout.Label(source?.Name ?? ExporterLocalization.T("VRMを保存してから、この画面を開いてください。"), bodyStyle);
                            if (source != null) GUILayout.Label(EditorUtility.FormatBytes(source.Size), EditorStyles.miniLabel);
                        }
                        EditorGUILayout.Space(12);
                        GUILayout.Label(ExporterLocalization.T("アバターを暗号化してクラウドに一時保存し、スマホのVR Vlogで受け取ります。"), bodyStyle);
                        GUILayout.Label(ExporterLocalization.T("受取期限は作成から3分。同じWi-Fiは不要です。"), bodyStyle);
                        EditorGUILayout.Space(16);
                        if (session != null) DrawTransferStatus();
                        if (ExtraMessage != null)
                        {
                            EditorGUILayout.HelpBox(ExporterLocalization.T(ExtraMessage), MessageType.Warning);
                            EditorGUILayout.Space(8);
                        }
                        if (session == null || session.Terminal)
                        {
                            DrawConsent();
                            using (new EditorGUI.DisabledScope(!CanBeginTransfer))
                                if (GUILayout.Button(ExporterLocalization.T(session == null ? "アップロードしてQRを表示" : "新しいQRを作成"), GUILayout.Height(36))) Begin();
                        }
                        else if (GUILayout.Button(ExporterLocalization.T("転送を中止"), GUILayout.Height(32)))
                        {
                            _ = session.CancelAsync(); ClearQr();
                            error = "中止通知が届かない場合も、受取期限でクラウドのコピーは削除されます。";
                        }
                    }
                    GUILayout.FlexibleSpace();
                }
            }
        }

        private void EnsureStyles()
        {
            if (contentStyle != null) return;
            contentStyle = new GUIStyle { padding = new RectOffset(16, 16, 16, 16) };
            cardStyle = new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(12, 12, 10, 10) };
            headingStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 18, wordWrap = true };
            bodyStyle = new GUIStyle(EditorStyles.wordWrappedLabel) { fontSize = 12 };
            consentStyle = new GUIStyle(bodyStyle);
            linkStyle = new GUIStyle(EditorStyles.linkLabel) { wordWrap = true, alignment = TextAnchor.MiddleLeft };
        }

        private void DrawConsent()
        {
            if (GUILayout.Button(ExporterLocalization.T("プライバシーポリシーを開く"), linkStyle))
                Application.OpenURL(PrivacyPolicyUrl);
            EditorGUILayout.Space(8);
            var label = ExporterLocalization.T("プライバシーポリシーに同意する");
            var height = Mathf.Max(EditorGUIUtility.singleLineHeight, consentStyle.CalcHeight(new GUIContent(label), Mathf.Max(100, position.width - 80)));
            var accepted = EditorGUILayout.ToggleLeft(label, PrivacyConsentAccepted, consentStyle, GUILayout.Height(height));
            if (accepted != PrivacyConsentAccepted) PrivacyConsentAccepted = accepted;
            if (!accepted) GUILayout.Label(ExporterLocalization.T("同意すると、QRコードを作成できます。"), bodyStyle);
            EditorGUILayout.Space(12);
        }

        private void DrawTransferStatus()
        {
            using (new EditorGUILayout.VerticalScope(cardStyle))
            {
                var state = session.State;
                var title = state == CloudTransferState.Ready ? "QRを読み取ってください"
                    : state == CloudTransferState.Completed ? "スマホに保存しました"
                    : state == CloudTransferState.Canceled ? "転送を中止しました"
                    : state == CloudTransferState.Expired ? "QRの期限が切れました"
                    : state == CloudTransferState.Failed ? "転送できませんでした"
                    : state == CloudTransferState.Uploading ? "アップロード中" : "転送を準備しています";
                GUILayout.Label(ExporterLocalization.T(title), EditorStyles.boldLabel);
                EditorGUILayout.Space(6);
                if (state == CloudTransferState.Failed)
                    EditorGUILayout.HelpBox(session.DisplayMessage(ExporterLocalization.T), MessageType.Error);
                else if (state == CloudTransferState.Ready)
                    GUILayout.Label(ExporterLocalization.T("VR Vlogの「モデルを変更 → PCから受け取る」で読み取ります。"), bodyStyle);
                else if (state == CloudTransferState.Completed)
                    GUILayout.Label(ExporterLocalization.T("スマホのVR Vlogでアバターを確認できます。"), bodyStyle);
                else if (!session.Terminal)
                    GUILayout.Label(ExporterLocalization.T("このまま少しお待ちください。"), bodyStyle);
                if (state == CloudTransferState.Uploading && source != null)
                {
                    EditorGUILayout.Space(8);
                    var rect = EditorGUILayout.GetControlRect(false, 20);
                    EditorGUI.ProgressBar(rect, (float)session.Transferred / Math.Max(1, session.UploadSize),
                        string.Format(ExporterLocalization.T("{0:N0} / {1:N0} バイト（暗号化済み）"), session.Transferred, session.UploadSize));
                }
                if (qrTexture != null)
                {
                    EditorGUILayout.Space(12);
                    var available = Mathf.Min(position.width - 88, 340);
                    var size = available >= qrTexture.width ? Mathf.Floor(available / qrTexture.width) * qrTexture.width : available;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.FlexibleSpace();
                        var rect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false));
                        GUI.DrawTexture(rect, qrTexture, ScaleMode.StretchToFill);
                        GUILayout.FlexibleSpace();
                    }
                    EditorGUILayout.Space(8);
                    GUILayout.Label(ExporterLocalization.T("QRコードは共有・撮影しないでください。"), bodyStyle);
                }
                if (!session.Terminal && session.ExpiresAt != 0)
                {
                    var seconds = Math.Max(0, session.ExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    GUILayout.Label(string.Format(ExporterLocalization.T("残り {0}:{1:00}"), seconds / 60, seconds % 60), EditorStyles.miniLabel);
                }
            }
            EditorGUILayout.Space(14);
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
