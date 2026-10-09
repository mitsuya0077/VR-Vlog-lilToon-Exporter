using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    internal sealed class ExperimentalExpressionCaptureWindow : EditorWindow
    {
        GameObject source;
        ExperimentalExpressionCaptureSession session;
        PreviewRenderUtility preview;
        Action pending;
        string error, status, author = "", expressionName = "笑顔", filter = "";
        Vector2 scroll;
        Vector2 pageScroll;
        Vector3 center;
        float distance = 1, yaw, zoom = 1, candidateTime;
        int rendererIndex, candidateIndex;
        bool automaticBlink = true;
        bool showLicenseSettings;
        [SerializeField] AvatarLicenseOptions licenseOptions = new AvatarLicenseOptions();

        [MenuItem("VR Vlog/実験/表情を記録して書き出す", false, 150)]
        internal static void Open()
        {
            var window = GetWindow<ExperimentalExpressionCaptureWindow>();
            window.titleContent = new GUIContent("表情の記録（実験）");
            window.minSize = new Vector2(520, 720);
        }

        void OnEnable() { EditorApplication.update += Advance; }
        void OnDisable() { EditorApplication.update -= Advance; pending = null; Cleanup(); }

        void Advance()
        {
            if (pending == null || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            var action = pending; pending = null;
            try { action(); error = null; }
            catch (OperationCanceledException) { status = "操作をキャンセルしました。"; }
            catch (Exception exception) { error = exception.Message; Debug.LogException(exception); }
            finally { EditorUtility.ClearProgressBar(); Repaint(); }
        }

        void Queue(Action action) { pending = action; error = null; Repaint(); }

        void OnGUI()
        {
            pageScroll = EditorGUILayout.BeginScrollView(pageScroll);
            EditorGUILayout.HelpBox("実験用の固定表情記録です。コピーのBlendShapeを調整し、完成した顔を保存します。基準は準備後の現在の顔です。", MessageType.Info);
            using (new EditorGUI.DisabledScope(pending != null || EditorApplication.isPlayingOrWillChangePlaymode))
            {
                using (new EditorGUI.DisabledScope(session != null))
                    source = (GameObject)EditorGUILayout.ObjectField("アバター", source, typeof(GameObject), true);
                author = EditorGUILayout.TextField("作者名", author);
                if (licenseOptions == null) licenseOptions = new AvatarLicenseOptions();
                AvatarLicenseSettingsUi.Draw(ref showLicenseSettings, licenseOptions);
                using (new EditorGUI.DisabledScope(source == null))
                    if (GUILayout.Button(session == null ? "記録用コピーを作成" : "コピーを作り直す"))
                    {
                        if (session == null || EditorUtility.DisplayDialog("コピーを作り直す", "未保存の記録がある場合は失われます。設定を保存してから作り直してください。", "作り直す", "戻る"))
                            Queue(Prepare);
                    }
                if (session != null && preview != null)
                {
                    if (GUILayout.Button("別のアバターを選ぶ") && (session.Expressions.Count == 0 ||
                        EditorUtility.DisplayDialog("アバターを変更", "未保存の記録がある場合は失われます。必要な設定を保存してから変更してください。", "変更する", "戻る")))
                        Queue(() => { Cleanup(); status = "別のアバターを指定できます。"; });
                    DrawSession();
                }
            }
            if (pending != null) EditorGUILayout.HelpBox("コピーで処理しています…", MessageType.Info);
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.Info);
            EditorGUILayout.EndScrollView();
        }

        void Prepare()
        {
            Cleanup();
            try
            {
                session = new ExperimentalExpressionCaptureSession(source);
                preview = new PreviewRenderUtility();
                preview.AddSingleGO(session.Copy);
                var renderers = ExportRendererSelection.Enumerate(session.Copy).ToArray();
                if (renderers.Length == 0) throw new InvalidOperationException("表示するメッシュがありません。");
                var bounds = renderers[0].bounds;
                foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
                var animator = session.Copy.GetComponent<Animator>();
                var head = animator != null && animator.avatar != null && animator.avatar.isHuman
                    ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
                center = head != null ? head.position + Vector3.up * bounds.size.y * .065f : bounds.center;
                distance = head != null ? Mathf.Max(.35f, bounds.size.y * .65f) : Mathf.Max(.5f, bounds.size.magnitude * 1.2f);
                preview.camera.fieldOfView = 30;
                preview.camera.nearClipPlane = .01f; preview.camera.farClipPlane = 100;
                preview.camera.clearFlags = CameraClearFlags.SolidColor;
                preview.camera.backgroundColor = new Color(.18f, .18f, .2f);
                preview.lights[0].intensity = 1;
                preview.lights[0].transform.rotation = Quaternion.Euler(30, 150, 0);
                preview.lights[1].intensity = .5f; preview.ambientColor = Color.gray;
                rendererIndex = candidateIndex = 0; candidateTime = 0; yaw = 0; zoom = 1;
                status = "コピーを作成しました。基準の顔を確認し、表情を記録してください。";
            }
            catch { Cleanup(); throw; }
        }

        void DrawSession()
        {
            var rect = GUILayoutUtility.GetRect(100, 220, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                preview.BeginPreview(rect, GUIStyle.none);
                preview.camera.transform.position = center + Quaternion.AngleAxis(yaw, Vector3.up) * session.Copy.transform.forward * distance * zoom;
                preview.camera.transform.LookAt(center, Vector3.up);
                preview.Render();
                GUI.DrawTexture(rect, preview.EndPreview(), ScaleMode.ScaleToFit, false);
            }
            EditorGUILayout.BeginHorizontal();
            yaw = EditorGUILayout.Slider("見る方向", yaw, -90, 90);
            zoom = EditorGUILayout.Slider("距離", zoom, .25f, 2);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("今の顔を基準にする")) Queue(() => { session.CaptureBaseline(); status = "基準の顔を記録しました。既存の表情は新しい基準から差分を生成します。"; });
            if (GUILayout.Button("基準の顔に戻す")) Queue(session.RestoreBaseline);
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("既存の表情候補を読み込む")) Queue(() => {
                session.DiscoverCandidates(); candidateIndex = 0; candidateTime = 0;
                status = "候補 " + session.Candidates.Entries.Count + "件を取得しました。選んでプレビューし、必要なものを記録してください。";
            });
            var entries = session.Candidates?.Entries;
            if (entries != null && entries.Count > 0)
            {
                var selected = EditorGUILayout.Popup("表情候補", candidateIndex,
                    entries.Select(entry => (entry.Error == null ? "" : "[取得不可] ") + entry.Name).ToArray());
                if (selected != candidateIndex) { candidateIndex = selected; candidateTime = 0; }
                var entry = entries[candidateIndex];
                if (entry.Animation.Count > 0 && entry.Duration > 0)
                    candidateTime = EditorGUILayout.Slider("記録する時刻", candidateTime, 0, (float)entry.Duration);
                if (!string.IsNullOrEmpty(entry.Error)) EditorGUILayout.HelpBox(entry.Error, MessageType.Warning);
                using (new EditorGUI.DisabledScope(entry.Error != null))
                    if (GUILayout.Button("候補の顔をプレビュー")) Queue(() => { session.PreviewCandidate(entry, candidateTime); expressionName = entry.Name; });
            }

            rendererIndex = EditorGUILayout.Popup("調整するメッシュ", rendererIndex, session.Channels.Select(channel => channel.Path.Length == 0 ? "(root)" : channel.Path).ToArray());
            filter = EditorGUILayout.TextField("変形名を絞り込む", filter);
            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(155));
            var channel = session.Channels[rendererIndex];
            for (var shape = 0; shape < channel.Shapes.Length; shape++)
            {
                if (!string.IsNullOrEmpty(filter) && channel.Shapes[shape].IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var value = channel.Renderer.GetBlendShapeWeight(shape);
                var first = channel.Mesh.GetBlendShapeFrameWeight(shape, 0);
                var last = channel.Mesh.GetBlendShapeFrameWeight(shape, channel.Mesh.GetBlendShapeFrameCount(shape) - 1);
                var minimum = Mathf.Min(0, Mathf.Min(first, value)); var maximum = Mathf.Max(100, Mathf.Max(last, value));
                var changed = EditorGUILayout.Slider(channel.Shapes[shape], value, minimum, maximum);
                if (changed != value) session.SetWeight(rendererIndex, shape, changed);
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.BeginHorizontal();
            expressionName = EditorGUILayout.TextField("表情名", expressionName);
            if (GUILayout.Button("今の顔を記録", GUILayout.Width(120))) Queue(() => { session.Capture(expressionName); status = "表情を記録しました: " + expressionName; });
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField("記録した表情: " + session.Expressions.Count + "件");
            foreach (var pose in session.Expressions.ToArray())
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(pose.Name);
                if (GUILayout.Button("確認", GUILayout.Width(60))) Queue(() => session.PreviewRecorded(pose));
                if (GUILayout.Button("削除", GUILayout.Width(60))) session.Expressions.Remove(pose);
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("記録設定を保存")) Queue(SaveSettings);
            if (GUILayout.Button("記録設定を読み込む")) Queue(LoadSettings);
            EditorGUILayout.EndHorizontal();
            automaticBlink = EditorGUILayout.Toggle("瞬きを自動設定する", automaticBlink);
            using (new EditorGUI.DisabledScope(session.Expressions.Count == 0 || string.IsNullOrWhiteSpace(author)))
                if (GUILayout.Button("記録した表情をlilToon VRMへ書き出す", GUILayout.Height(30))) Queue(Export);
            EditorGUILayout.LabelField("材質・ボーン・表示切り替えは今回の記録対象外です。動く候補は選んだ時刻の固定表情として保存します。", EditorStyles.wordWrappedMiniLabel);
        }

        void SaveSettings()
        {
            var path = EditorUtility.SaveFilePanel("表情の記録設定を保存", "", "expression-capture", "json");
            if (string.IsNullOrEmpty(path)) return;
            AtomicWrite(path, System.Text.Encoding.UTF8.GetBytes(session.SaveSettings()));
            status = "記録設定を保存しました。コピーを作り直した後も読み込めます。";
        }

        void LoadSettings()
        {
            var path = EditorUtility.OpenFilePanel("表情の記録設定を読み込む", "", "json");
            if (string.IsNullOrEmpty(path)) return;
            if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidOperationException("記録設定は8 MiBまでです。");
            session.LoadSettings(File.ReadAllText(path));
            status = "記録設定を読み込み、基準の顔に戻しました。";
        }

        void Export()
        {
            var path = EditorUtility.SaveFilePanel("実験用VRMを保存", "", source.name + "-captured", "vrm");
            if (string.IsNullOrEmpty(path)) return;
            var warnings = new System.Collections.Generic.List<string>(session.Warnings);
            var bytes = session.Export(source.name, author, warnings,
                automaticBlink ? null : new BlinkExportOptions { Mode = BlinkExportMode.None }, licenseOptions.Copy());
            AtomicWrite(path, bytes);
            status = "VRMを保存しました。\n" + path + (warnings.Count == 0 ? "" : "\n" + string.Join("\n", warnings));
        }

        internal static void AtomicWrite(string path, byte[] bytes)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, bytes);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        void Cleanup()
        {
            // PreviewRenderUtility owns the preview scene; session owns prepared assets.
            preview?.Cleanup(); preview = null;
            session?.Dispose(); session = null;
        }
    }
}
