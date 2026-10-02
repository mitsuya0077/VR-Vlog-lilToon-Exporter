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
        private Action saved;
        private ExportRecoveryPreview before, after;
        private PreviewRenderUtility beforeRender, afterRender, vrmRender;
        private Vrm10Instance imported;
        private CancellationTokenSource loading;
        private Vector3 center, vrmCenter;
        private float distance = 3, vrmDistance = 3, yaw;
        private int tab;
        private bool preparing;
        private bool showAppliedRemedies;
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
            window.saved = saved;
            window.titleContent = new GUIContent(ExporterLocalization.T("対策後の見た目を確認"));
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
                before = session.CreatePreview(new ExportRecoveryOptions());
                after = session.CreatePreview(attempt.Options);
                NormalizeForPreview(before.Copy);
                NormalizeForPreview(after.Copy);
                beforeRender = CreateRenderer(before.Copy);
                afterRender = CreateRenderer(after.Copy);
                var bounds = VisibleBounds(before.Copy);
                center = bounds.center;
                distance = Mathf.Max(.5f, bounds.size.magnitude * 1.8f);
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
                NormalizeForPreview(imported.gameObject);
                imported.UpdateType = Vrm10Instance.UpdateTypes.None;
                vrmRender = CreateRenderer(imported.gameObject);
                RefreshPreviewFraming();
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { if (revision == previewRevision) error = exception.Message; }
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
            if (before?.Copy != null)
            {
                var inputBounds = VisibleBounds(before.Copy);
                center = inputBounds.center;
                distance = Mathf.Max(.5f, inputBounds.size.magnitude * 1.8f);
            }
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
                EditorGUILayout.HelpBox(ExporterLocalization.T("アバターまたは関連アセットが変わりました。古い診断とプレビューは無効です。再検査してください。"), MessageType.Warning);
                if (GUILayout.Button(ExporterLocalization.T("再検査する"))) Reinspect();
                return;
            }
            using (var content = new EditorGUILayout.ScrollViewScope(contentScroll))
            {
            contentScroll = content.scrollPosition;
            EditorGUILayout.HelpBox(ExporterLocalization.T("この設定を変更したコピーでは書き出せました。見た目を確認してから保存してください。"), MessageType.Info);
            if (session.LastSuccess.Options.Actions.Count > 0)
            {
                showAppliedRemedies = EditorGUILayout.Foldout(showAppliedRemedies,
                    ExporterLocalization.T("適用した対策: ") + session.LastSuccess.Options.Actions.Count, true);
                if (showAppliedRemedies)
                {
                    using (var view = new EditorGUILayout.ScrollViewScope(remediesScroll, GUILayout.MaxHeight(120)))
                    {
                        remediesScroll = view.scrollPosition;
                        foreach (var action in session.LastSuccess.Options.Actions)
                        {
                            var diagnostic = session.AvailableDiagnostics.FirstOrDefault(item => item.Action?.Id == action.Id);
                            if (diagnostic == null) continue;
                            EditorGUILayout.LabelField(diagnostic.Target + " / " + ExporterLocalization.T(diagnostic.Remedy), EditorStyles.wordWrappedLabel);
                            EditorGUILayout.LabelField(ExporterLocalization.T("失われる効果: ") + ExporterLocalization.T(diagnostic.LostEffect), EditorStyles.wordWrappedMiniLabel);
                        }
                    }
                }
            }
            tab = GUILayout.Toolbar(tab, new[] { ExporterLocalization.T("対策前後の入力コピー"), ExporterLocalization.T("再読み込みしたVRM（標準MToon）") });
            if (tab == 0)
                EditorGUILayout.LabelField(ExporterLocalization.T("同じカメラ・照明・姿勢で対策の影響を比較します。ビルド処理後の結果はVRMタブで確認してください。"), EditorStyles.wordWrappedLabel);
            else
                EditorGUILayout.LabelField(ExporterLocalization.T("標準MToonによるVRMの表示です。VR Vlogの専用lilToon表示とは見た目が異なる場合があります。"), EditorStyles.wordWrappedLabel);
            yaw = EditorGUILayout.Slider(ExporterLocalization.T("向き"), yaw, -180, 180);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(ExporterLocalization.T("正面"))) yaw = 0;
                if (GUILayout.Button(ExporterLocalization.T("斜め"))) yaw = 45;
                if (GUILayout.Button(ExporterLocalization.T("横"))) yaw = 90;
            }
            if (preparing) EditorGUILayout.HelpBox(ExporterLocalization.T("比較用のコピーとVRMを準備しています…"), MessageType.Info);
            if (error != null) EditorGUILayout.HelpBox(ExporterLocalization.T(error), MessageType.Error);
            if (tab == 0)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawPreview(beforeRender, ExporterLocalization.T("対策前"));
                    DrawPreview(afterRender, ExporterLocalization.T("対策後"));
                }
            }
            else DrawPreview(vrmRender, ExporterLocalization.T("再読み込みしたVRM（標準MToon）"));
            var changes = ExportAppearanceReport.Changes(session.LastSuccess.Warnings);
            if (changes.Length > 0)
                EditorGUILayout.HelpBox(ExporterLocalization.T("見た目の変更: ") + ExportAppearanceReport.Summary(changes), MessageType.Warning);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(preparing))
                    if (GUILayout.Button(ExporterLocalization.T("対策を選び直す"), GUILayout.Height(32)))
                        ExportFailureWindow.Show(session, saved);
                using (new EditorGUI.DisabledScope(preparing || !HasVrmPreview || !session.CanSave))
                    if (GUILayout.Button(ExporterLocalization.T("確認して保存"), GUILayout.Height(32))) Save();
                if (GUILayout.Button(ExporterLocalization.T("保存しないで閉じる"), GUILayout.Height(32))) Close();
            }
        }

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
            var target = preview == vrmRender ? vrmCenter : center;
            var frameDistance = preview == vrmRender ? vrmDistance : distance;
            preview.camera.farClipPlane = Mathf.Max(100, frameDistance * 3);
            preview.camera.transform.position = target + Quaternion.Euler(0, yaw, 0) * Vector3.forward * frameDistance;
            preview.camera.transform.LookAt(target, Vector3.up);
        }

        // A validation hook for the very same cameras/materials displayed in
        // this window. The caller owns the returned texture. This is rendering
        // evidence, separate from a capture of the actual Editor window.
        internal Texture2D CapturePreview(int pane, int width = 512, int height = 512)
        {
            if (session == null || !session.HasCurrentSuccess)
                throw new InvalidOperationException("The preview is no longer current.");
            var preview = pane == 0 ? beforeRender : pane == 1 ? afterRender : pane == 2 ? vrmRender : null;
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
                if (session.Reinspect()) StartRebuild();
                else ExportFailureWindow.Show(session, saved);
                Repaint();
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
                saved?.Invoke();
                Close();
            }
            catch (Exception exception) { ExportFailureWindow.Show(exception); }
        }

        private void OnDisable()
        {
            EditorApplication.update -= CheckSource;
            Cleanup();
        }

        private void Cleanup()
        {
            previewRevision++;
            loading?.Cancel(); loading?.Dispose(); loading = null;
            beforeRender?.Cleanup(); beforeRender = null;
            afterRender?.Cleanup(); afterRender = null;
            vrmRender?.Cleanup(); vrmRender = null;
            before?.Dispose(); before = null;
            after?.Dispose(); after = null;
            if (imported != null) Object.DestroyImmediate(imported.gameObject);
            imported = null;
            preparing = false;
        }
    }
}
