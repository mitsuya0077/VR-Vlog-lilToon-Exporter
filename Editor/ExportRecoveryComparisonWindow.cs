using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UniGLTF;
using UniVRM10;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter
{
    internal sealed class ExportRecoveryComparisonWindow : EditorWindow
    {
        private ExportRecoverySession session;
        private IDisposable inputOwnership;
        private Action saved;
        private PreviewRenderUtility vrmRender;
        private IDisposable lilToonPreview;
        private Vrm10Instance imported;
        private CancellationTokenSource loading;
        private Vector3 vrmCenter;
        private float vrmDistance = 3, yaw;
        private bool preparing;
        private bool showAppliedRemedies = true;
        private Vector2 remediesScroll, contentScroll;
        private int previewRevision;
        private string error;
        private double nextSourceCheck;
        internal bool HasVrmPreview => imported != null && vrmRender != null;
        internal ExportRecoverySession Session => session;

        internal static void Show(ExportRecoverySession session, Action saved = null)
        {
            if (session == null || !session.HasCurrentSuccess) throw new ArgumentException(nameof(session));
            var window = Resources.FindObjectsOfTypeAll<ExportRecoveryComparisonWindow>()
                .FirstOrDefault(item => item.session == session) ?? CreateInstance<ExportRecoveryComparisonWindow>();
            window.session = session;
            if (window.inputOwnership == null) window.inputOwnership = session.AcquireInputOwnership();
            window.saved = saved;
            window.titleContent = new GUIContent(ExporterLocalization.T("書き出すVRMを確認"));
            window.minSize = new Vector2(780, 580);
            window.ShowUtility();
            EditorApplication.delayCall += () => { if (window != null) window.StartRebuild(); };
        }

        private void OnEnable() => EditorApplication.update += CheckSource;

        private void CheckSource()
        {
            if (session == null || EditorApplication.timeSinceStartup < nextSourceCheck) return;
            nextSourceCheck = EditorApplication.timeSinceStartup + 1;
            if (!session.CheckForChanges() && session.HasCurrentSuccess) return;
            Cleanup();
            Repaint();
        }

        private async void StartRebuild()
        {
            try { await PrepareAsync(); }
            catch (Exception exception)
            {
                error = exception.Message;
                preparing = false;
                Repaint();
            }
        }

        internal async Task PrepareAsync()
        {
            Cleanup();
            error = null;
            if (session == null || !session.HasCurrentSuccess) return;
            preparing = true;
            var attempt = session.LastSuccess;
            var revision = previewRevision;
            loading = new CancellationTokenSource();
            var token = loading.Token;
            try
            {
                // UniVRM owns the loaded meshes/materials/textures through its
                // RuntimeGltfInstance. Destroying this root releases them too.
                var loaded = await Vrm10.LoadBytesAsync(attempt.Bytes, canLoadVrm0X: false,
                    awaitCaller: new ImmediateCaller(), ct: token,
                    controlRigGenerationOption: ControlRigGenerationOption.None);
                if (loaded == null)
                    throw new InvalidOperationException(ExporterLocalization.T("VRMの再読み込み結果を取得できませんでした。"));
                if (token.IsCancellationRequested || this == null || revision != previewRevision || session.CheckForChanges())
                {
                    Object.DestroyImmediate(loaded.gameObject);
                    return;
                }
                imported = loaded;
                // Restore the saved lilToon payload onto the reloaded VRM.
                // Never substitute a copy of the source avatar for the output.
                lilToonPreview = ExportVrmLilToonPreview.Apply(attempt.Bytes, imported.gameObject);
                NormalizeForPreview(imported.gameObject);
                imported.UpdateType = Vrm10Instance.UpdateTypes.None;
                vrmRender = CreateRenderer(imported.gameObject);
                RefreshPreviewFraming();
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                if (revision == previewRevision)
                {
                    ReleasePreview();
                    error = exception.Message;
                }
            }
            finally
            {
                if (revision == previewRevision)
                {
                    preparing = false;
                    if (this != null) Repaint();
                }
            }
        }

        private static void NormalizeForPreview(GameObject copy)
        {
            copy.hideFlags = HideFlags.HideAndDontSave;
            copy.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            copy.transform.localScale = Vector3.one;
            foreach (var behaviour in copy.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
            foreach (var skin in copy.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                skin.updateWhenOffscreen = true;
                skin.forceMatrixRecalculationPerRender = true;
            }
        }

        private static PreviewRenderUtility CreateRenderer(GameObject copy)
        {
            var preview = new PreviewRenderUtility();
            preview.AddSingleGO(copy);
            preview.camera.fieldOfView = 30;
            preview.camera.nearClipPlane = .01f;
            preview.camera.farClipPlane = 100;
            preview.camera.clearFlags = CameraClearFlags.SolidColor;
            preview.camera.backgroundColor = new Color(.18f, .18f, .2f);
            preview.lights[0].intensity = 1;
            preview.lights[0].transform.rotation = Quaternion.Euler(30, 150, 0);
            preview.lights[1].intensity = .5f;
            preview.ambientColor = Color.gray;
            return preview;
        }

        internal static Bounds VisibleBounds(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true)
                .Where(renderer => renderer.enabled && renderer.gameObject.activeInHierarchy).ToArray();
            if (renderers.Length == 0)
                throw new InvalidOperationException(ExporterLocalization.T("プレビューするメッシュがありません。"));
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
            return bounds;
        }

        internal void RefreshPreviewFraming()
        {
            if (imported != null)
            {
                // The glTF model may have a different root wrapper/origin from
                // the input copy. Frame its actual loaded bounds; do not move
                // its nodes or meshes to hide a difference in exported geometry.
                var outputBounds = VisibleBounds(imported.gameObject);
                vrmCenter = outputBounds.center;
                vrmDistance = Mathf.Max(.5f, outputBounds.size.magnitude * 1.8f);
            }
        }

        private void OnGUI()
        {
            if (session == null)
            {
                EditorGUILayout.HelpBox(ExporterLocalization.T("Unityの再読み込みで確認内容が無効になりました。書き出し画面から再度書き出してください。"), MessageType.Warning);
                if (GUILayout.Button(ExporterLocalization.T("閉じる"))) Close();
                return;
            }
            if (session.IsInvalidated || !session.HasCurrentSuccess)
            {
                EditorGUILayout.HelpBox(ExporterLocalization.T("アバターの設定が変わったため、前の結果は保存できません。今のアバターから書き出し直してください。"), MessageType.Warning);
                if (GUILayout.Button(ExporterLocalization.T("今のアバターで書き出し直す"))) Reinspect();
                return;
            }
            using (var content = new EditorGUILayout.ScrollViewScope(contentScroll))
            {
            contentScroll = content.scrollPosition;
            EditorGUILayout.LabelField(ExporterLocalization.T("書き出せました。まだ保存していません。"), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(ExporterLocalization.T("見た目と変更点を確認し、下の「このVRMを保存する」で保存してください。"), EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField(ExporterLocalization.T("保存するファイル: ") + System.IO.Path.GetFileName(session.Destination), EditorStyles.wordWrappedMiniLabel);
            var skipAllMenus = session.LastSuccess.Options.Actions.Any(action => action.Kind == ExportRecoveryActionKind.SkipVrChatMenus);
            var appliedActions = session.LastSuccess.Options.Actions.Where(action =>
                !skipAllMenus || action.Kind != ExportRecoveryActionKind.ExcludeMenuBranch).ToArray();
            DrawMenuOmissions(appliedActions);
            if (appliedActions.Length > 0)
            {
                showAppliedRemedies = EditorGUILayout.Foldout(showAppliedRemedies,
                    ExporterLocalization.T("このVRMで変わる点: ") + appliedActions.Length, true);
                if (showAppliedRemedies)
                {
                    using (var view = new EditorGUILayout.ScrollViewScope(remediesScroll, GUILayout.MaxHeight(120)))
                    {
                        remediesScroll = view.scrollPosition;
                        foreach (var action in appliedActions)
                        {
                            var diagnostic = AppliedDiagnostic(action);
                            if (diagnostic == null) continue;
                            EditorGUILayout.LabelField(diagnostic.Target + " / " + ExporterLocalization.T(ExportFailureWindow.ActionLabel(action.Kind)), EditorStyles.wordWrappedLabel);
                            EditorGUILayout.LabelField(ExporterLocalization.T("変わる点: ") + ExporterLocalization.T(diagnostic.LostEffect), EditorStyles.wordWrappedMiniLabel);
                        }
                    }
                }
            }
            EditorGUILayout.LabelField(ExporterLocalization.T("保存するVRMをlilToonで表示しています。"), EditorStyles.wordWrappedLabel);
            yaw = EditorGUILayout.Slider(ExporterLocalization.T("向き"), yaw, -180, 180);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(ExporterLocalization.T("正面"))) yaw = 0;
                if (GUILayout.Button(ExporterLocalization.T("斜め"))) yaw = 45;
                if (GUILayout.Button(ExporterLocalization.T("横"))) yaw = 90;
            }
            if (preparing) EditorGUILayout.HelpBox(ExporterLocalization.T("書き出すVRMを準備しています…"), MessageType.Info);
            if (error != null) EditorGUILayout.HelpBox(ExporterLocalization.T(error), MessageType.Error);
            DrawPreview(vrmRender, ExporterLocalization.T("書き出すVRM"));
            var changes = ExportAppearanceReport.Changes(session.LastSuccess.Warnings);
            if (changes.Length > 0)
                EditorGUILayout.HelpBox(ExporterLocalization.T("見た目の変更: ") + ExportAppearanceReport.Summary(changes), MessageType.Warning);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(preparing))
                    if (GUILayout.Button(ExporterLocalization.T("対処を変更する"), GUILayout.Height(32)))
                        ExportFailureWindow.Show(session, saved);
                using (new EditorGUI.DisabledScope(preparing || !HasVrmPreview || !session.CanSave))
                    if (GUILayout.Button(ExporterLocalization.T("このVRMを保存する"), GUILayout.Height(32))) Save();
                if (GUILayout.Button(ExporterLocalization.T("保存せずに戻る"), GUILayout.Height(32))) Close();
            }
        }

        private void DrawMenuOmissions(ExportRecoveryAction[] actions)
        {
            var omissions = actions.Where(action => action.Kind == ExportRecoveryActionKind.SkipVrChatMenus ||
                action.Kind == ExportRecoveryActionKind.ExcludeMenuBranch).ToArray();
            if (omissions.Length == 0) return;
            // A still image cannot reveal that expression and pose entries were
            // not imported. Keep their selected scope visible outside a foldout.
            EditorGUILayout.LabelField(ExporterLocalization.T("取り込まない表情・ポーズ"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(ExporterLocalization.T("以下のメニュー由来の表情・ポーズは、このVRMには入りません。静止画像では確認できないため、範囲を確認してください。"), MessageType.Warning);
            foreach (var action in omissions)
            {
                var diagnostic = AppliedDiagnostic(action);
                var target = diagnostic?.Target ?? (action.Kind == ExportRecoveryActionKind.SkipVrChatMenus
                    ? ExporterLocalization.T("すべてのVRChatメニュー") : action.MenuPath);
                var lost = diagnostic?.LostEffect ?? "指定した範囲のメニュー由来の表情・ポーズは取り込まれません。";
                EditorGUILayout.LabelField(ExporterLocalization.T("対象: ") + target, EditorStyles.wordWrappedLabel);
                EditorGUILayout.LabelField(ExporterLocalization.T("変わる点: ") + ExporterLocalization.T(lost), EditorStyles.wordWrappedLabel);
            }
            EditorGUILayout.LabelField(ExporterLocalization.T("この対策はメニュー由来の表情・ポーズの取り込みだけを変えます。衣装・髪・骨格のオブジェクト自体は削除しません。"), EditorStyles.wordWrappedMiniLabel);
        }

        private ExportRecoveryDiagnostic AppliedDiagnostic(ExportRecoveryAction action) =>
            session.LastSuccess.Diagnostics.FirstOrDefault(item => item.Action?.Id == action.Id) ??
            session.AvailableDiagnostics.FirstOrDefault(item => item.Action?.Id == action.Id);

        private void DrawPreview(PreviewRenderUtility preview, string label)
        {
            using (new EditorGUILayout.VerticalScope())
            {
                EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
                var rect = GUILayoutUtility.GetRect(100, 180, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
                if (preview == null || Event.current.type != EventType.Repaint || rect.width <= 0 || rect.height <= 0) return;
                preview.BeginPreview(rect, GUIStyle.none);
                PositionCamera(preview);
                preview.Render();
                GUI.DrawTexture(rect, preview.EndPreview(), ScaleMode.ScaleToFit, false);
            }
        }

        private void PositionCamera(PreviewRenderUtility preview)
        {
            preview.camera.farClipPlane = Mathf.Max(100, vrmDistance * 3);
            preview.camera.transform.position = vrmCenter + Quaternion.Euler(0, yaw, 0) * Vector3.forward * vrmDistance;
            preview.camera.transform.LookAt(vrmCenter, Vector3.up);
        }

        // A validation hook for the very same cameras/materials displayed in
        // this window. The caller owns the returned texture. This is rendering
        // evidence, separate from a capture of the actual Editor window.
        internal Texture2D CapturePreview(int width = 512, int height = 512)
        {
            if (session == null || !session.HasCurrentSuccess)
                throw new InvalidOperationException("The preview is no longer current.");
            var preview = vrmRender;
            if (preview == null) throw new InvalidOperationException("The preview is not ready.");
            preview.BeginStaticPreview(new Rect(0, 0, Mathf.Max(1, width), Mathf.Max(1, height)));
            PositionCamera(preview);
            preview.Render();
            return preview.EndStaticPreview();
        }

        private void Reinspect()
        {
            EditorApplication.delayCall += () =>
            {
                if (this == null) return;
                try
                {
                    if (session.Reinspect())
                    {
                        if (!ExportFailureWindow.SaveUnmodifiedResult(session, saved)) StartRebuild();
                    }
                    else { ExportFailureWindow.Show(session, saved); Close(); }
                }
                catch (Exception exception)
                {
                    if (session.IsInvalidated) { ExportFailureWindow.Show(session, saved); Close(); }
                    else ExportFailureWindow.Show(exception);
                }
                if (this != null) Repaint();
            };
        }

        private void Save()
        {
            if (!session.CanSave) return;
            if (System.IO.File.Exists(session.Destination) && !EditorUtility.DisplayDialog(
                ExporterLocalization.T("ファイルを上書きしますか？"), session.Destination,
                ExporterLocalization.T("上書き"), ExporterLocalization.T("キャンセル"))) return;
            try
            {
                session.SavePending();
                ExportFailureWindow.CloseSessionWindows(session);
                saved?.Invoke();
            }
            catch (Exception exception)
            {
                if (session.IsInvalidated) { ExportFailureWindow.Show(session, saved); Close(); }
                else ExportFailureWindow.Show(exception);
            }
        }

        private void OnDisable()
        {
            EditorApplication.update -= CheckSource;
            Cleanup();
            inputOwnership?.Dispose(); inputOwnership = null;
        }

        private void Cleanup()
        {
            previewRevision++;
            loading?.Cancel(); loading?.Dispose(); loading = null;
            ReleasePreview();
            preparing = false;
        }

        private void ReleasePreview()
        {
            vrmRender?.Cleanup(); vrmRender = null;
            lilToonPreview?.Dispose(); lilToonPreview = null;
            if (imported != null) Object.DestroyImmediate(imported.gameObject);
            imported = null;
        }
    }
}
