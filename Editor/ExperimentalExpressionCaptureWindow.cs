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
        bool showRecommendations = true, showLegacy, showManual, showView;
        string previewName = "基準の顔", clipFilter = "";
        Vector2 clipScroll, recommendationScroll;
        AnimationClip clipToAdd;
        PoseReviewWindow poseReview;
        Rect expressionDropRect;
        readonly System.Collections.Generic.List<ExperimentalExpressionCaptureSession.ClipInput> clipInputs =
            new System.Collections.Generic.List<ExperimentalExpressionCaptureSession.ClipInput>();
        System.Collections.Generic.List<ExperimentalExpressionCaptureSession.ClipRecommendation> recommendations;
        readonly System.Collections.Generic.Dictionary<VrChatExpressionMenu.Entry, bool> selectedCandidates =
            new System.Collections.Generic.Dictionary<VrChatExpressionMenu.Entry, bool>();
        Vector2 candidateScroll;
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
            EditorGUILayout.HelpBox("表情の.animを追加 → プレビュー → 表情を記録 → VRMを書き出す。ポーズも別の欄から同梱できます。", MessageType.Info);
            using (new EditorGUI.DisabledScope(pending != null || EditorApplication.isPlayingOrWillChangePlaymode))
            {
                EditorGUILayout.LabelField("1. アバターを読み込む", EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(session != null))
                    source = (GameObject)EditorGUILayout.ObjectField("アバター", source, typeof(GameObject), true);
                author = EditorGUILayout.TextField("作者名", author);
                if (licenseOptions == null) licenseOptions = new AvatarLicenseOptions();
                AvatarLicenseSettingsUi.Draw(ref showLicenseSettings, licenseOptions);
                using (new EditorGUI.DisabledScope(source == null))
                    if (GUILayout.Button(session == null ? "アバターを読み込む" : "コピーを作り直す"))
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
                session = new ExperimentalExpressionCaptureSession(source, replayInstalledDefaults: false);
                recommendations = session.RecommendClips();
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
                previewName = "基準の顔";
                status = "表情の.animをドロップするか、候補ファイルの「追加」を押してください。";
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
            EditorGUILayout.LabelField("プレビュー: " + previewName, EditorStyles.boldLabel);
            showView = EditorGUILayout.Foldout(showView, "見る方向・距離", true);
            if (showView)
            {
                yaw = EditorGUILayout.Slider("見る方向", yaw, -90, 90);
                zoom = EditorGUILayout.Slider("距離", zoom, .25f, 2);
            }
            if (GUILayout.Button("基準の顔に戻す")) Queue(() => { session.RestoreBaseline(); previewName = "基準の顔"; });
            DrawClipInputs();
            DrawPoseInputs();
            showLegacy = EditorGUILayout.Foldout(showLegacy, "従来のメニューから自動取込（詳細）", true);
            if (showLegacy)
            {
                EditorGUILayout.HelpBox("Animator全体の再現が必要な表情向けです。しっぽ等の回転差で取得不可になる場合は、上の表情ファイル指定を使ってください。", MessageType.Info);
            if (GUILayout.Button("既存の表情を読み込む")) Queue(() => {
                session.DiscoverCandidates(); candidateIndex = 0; candidateTime = 0;
                selectedCandidates.Clear();
                foreach (var candidate in session.Candidates.Entries)
                    selectedCandidates[candidate] = session.SelectCandidateByDefault(candidate);
                status = "候補 " + session.Candidates.Entries.Count + "件を取得しました。取り込む表情を確認してください。";
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
                candidateScroll = EditorGUILayout.BeginScrollView(candidateScroll, GUILayout.Height(155));
                foreach (var candidate in entries)
                {
                    using (new EditorGUI.DisabledScope(candidate.Error != null || candidate.Unevaluated.Count != 0 || candidate.Animation.Count != 0))
                    {
                        selectedCandidates.TryGetValue(candidate, out var selectedValue);
                        selectedCandidates[candidate] = EditorGUILayout.ToggleLeft(candidate.Name, selectedValue);
                    }
                }
                EditorGUILayout.EndScrollView();
                if (GUILayout.Button("選んだ表情をまとめて取り込む")) Queue(() => {
                    var result = session.ImportCandidates(entries.Where(candidate => selectedCandidates.TryGetValue(candidate, out var selectedValue) && selectedValue));
                    status = "取り込み " + result.Imported.Count + "件、省略 " + result.Skipped.Count + "件。記録した表情の「確認」で顔を確認できます。" +
                        (result.Skipped.Count == 0 ? "" : "\n" + string.Join("\n", result.Skipped));
                });
            }

            }
            showManual = EditorGUILayout.Foldout(showManual, "BlendShapeの手動調整・基準の顔（詳細）", true);
            if (showManual)
            {
                if (GUILayout.Button("プレビュー中の顔を基準にする")) Queue(() => { session.CaptureBaseline(); status = "基準の顔を更新しました。未指定の変形はこの顔から補います。"; });
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

            }
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("4. 記録した表情を確認・書き出す", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("記録した表情: " + session.Expressions.Count + "件");
            foreach (var pose in session.Expressions.ToArray())
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(pose.Name);
                if (GUILayout.Button("確認", GUILayout.Width(60))) Queue(() => { session.PreviewRecorded(pose); previewName = pose.Name; });
                if (GUILayout.Button("削除", GUILayout.Width(60))) session.Expressions.Remove(pose);
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("記録設定を保存")) Queue(SaveSettings);
            if (GUILayout.Button("記録設定を読み込む")) Queue(LoadSettings);
            EditorGUILayout.EndHorizontal();
            if (session.Expressions.Count == 0) EditorGUILayout.HelpBox("上の「選択した表情を記録」で、書き出す表情を1件以上記録してください。", MessageType.Info);
            else if (string.IsNullOrWhiteSpace(author)) EditorGUILayout.HelpBox("書き出すには画面上部の作者名を入力してください。", MessageType.Info);
            automaticBlink = EditorGUILayout.Toggle("瞬きを自動設定する", automaticBlink);
            using (new EditorGUI.DisabledScope(session.Expressions.Count == 0 || string.IsNullOrWhiteSpace(author)))
                if (GUILayout.Button("記録した表情をlilToon VRMへ書き出す", GUILayout.Height(30))) Queue(Export);
            EditorGUILayout.LabelField("表情は指定時刻の固定の顔として保存します。表情の材質・ボーン・表示切替は追加対応が必要です。ポーズは表情とは別に同梱します。", EditorStyles.wordWrappedMiniLabel);
        }

        void DrawClipInputs()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("2. 表情ファイルを追加して試す", EditorStyles.boldLabel);
            DropClips("UnityのProjectから表情の.animをここへドロップ（複数可）", clips => {
                foreach (var clip in clips) AddClip(clip);
            });
            EditorGUILayout.BeginHorizontal();
            clipToAdd = (AnimationClip)EditorGUILayout.ObjectField("表情ファイル", clipToAdd, typeof(AnimationClip), false);
            using (new EditorGUI.DisabledScope(clipToAdd == null))
                if (GUILayout.Button("追加", GUILayout.Width(60))) { var clip = clipToAdd; Queue(() => AddClip(clip)); }
            EditorGUILayout.EndHorizontal();
            showRecommendations = EditorGUILayout.Foldout(showRecommendations, "FaceEmoなどの候補ファイル（追加は任意）", true);
            if (showRecommendations)
            {
                if (GUILayout.Button("候補ファイルを探し直す")) Queue(() => recommendations = session.RecommendClips());
                clipFilter = EditorGUILayout.TextField("名前で絞り込む", clipFilter);
                if (recommendations == null || recommendations.Count == 0)
                    EditorGUILayout.HelpBox("参照する表情ファイルが見つかりません。Projectから直接追加できます。", MessageType.Info);
                else
                {
                    recommendationScroll = EditorGUILayout.BeginScrollView(recommendationScroll, GUILayout.Height(110));
                    foreach (var item in recommendations)
                    {
                        if (!string.IsNullOrEmpty(clipFilter) && item.Clip.name.IndexOf(clipFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(new GUIContent(item.Clip.name + "  / " + item.Source, AssetDatabase.GetAssetPath(item.Clip)));
                        if (GUILayout.Button("ファイル", GUILayout.Width(60))) EditorGUIUtility.PingObject(item.Clip);
                        var added = clipInputs.Any(input => input.Clip == item.Clip);
                        using (new EditorGUI.DisabledScope(added))
                            if (GUILayout.Button(added ? "追加済み" : "追加", GUILayout.Width(65))) { var clip = item.Clip; Queue(() => AddClip(clip)); }
                        EditorGUILayout.EndHorizontal();
                    }
                    EditorGUILayout.EndScrollView();
                }
            }
            if (clipInputs.Count > 0)
            {
                clipScroll = EditorGUILayout.BeginScrollView(clipScroll, GUILayout.Height(230));
                foreach (var input in clipInputs.ToArray())
                {
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        EditorGUILayout.BeginHorizontal();
                        using (new EditorGUI.DisabledScope(input.Error != null)) input.Selected = EditorGUILayout.Toggle(input.Selected, GUILayout.Width(20));
                        input.Name = EditorGUILayout.TextField("表情名", input.Name);
                        if (GUILayout.Button("削除", GUILayout.Width(50))) Queue(() => clipInputs.Remove(input));
                        EditorGUILayout.EndHorizontal();
                        using (new EditorGUI.DisabledScope(true)) EditorGUILayout.ObjectField("元ファイル", input.Clip, typeof(AnimationClip), false);
                        var time = EditorGUILayout.Slider("収録する秒", input.Time, 0, input.Clip.length);
                        if (time != input.Time) { input.Time = time; Queue(() => input.Error = session.ClipError(input.Clip, input.Time)); }
                        if (input.Error == null)
                        {
                            EditorGUILayout.LabelField("プレビュー・記録ができます", EditorStyles.miniLabel);
                            if (GUILayout.Button("この表情をプレビュー")) Queue(() => { session.PreviewClip(input.Clip, input.Time); previewName = input.Name; });
                        }
                        else EditorGUILayout.HelpBox("この表情は追加対応が必要です。\n" + input.Error, MessageType.Warning);
                    }
                }
                EditorGUILayout.EndScrollView();
            }
            var selected = clipInputs.Where(input => input.Selected).ToArray();
            using (new EditorGUI.DisabledScope(selected.Length == 0 || selected.Any(input => input.Error != null)))
                if (GUILayout.Button("選択した " + selected.Length + " 表情を記録", GUILayout.Height(30))) Queue(() => {
                    var result = session.ImportClips(selected);
                    status = "記録 " + result.Imported.Count + "件。下の記録一覧で確認し、VRMを書き出せます。" +
                        (result.Skipped.Count == 0 ? "" : "\n" + string.Join("\n", result.Skipped));
                });
        }

        void AddClip(AnimationClip clip)
        {
            if (clipInputs.Any(input => input.Clip == clip)) { status = "このファイルは追加済みです: " + clip.name; return; }
            if (clipInputs.Count >= ExperimentalExpressionCaptureSession.MaximumExpressions)
                throw new InvalidOperationException("表情ファイルは64件までです。");
            var input = new ExperimentalExpressionCaptureSession.ClipInput { Clip = clip, Name = clip.name };
            input.Error = session.ClipError(clip, 0); input.Selected = input.Error == null;
            clipInputs.Add(input);
            if (input.Error == null) { session.PreviewClip(clip); previewName = input.Name; }
            status = input.Error == null ? "追加しました。顔を確認し「選択した表情を記録」を押してください。" : "追加しました。項目内の理由を確認してください。";
        }

        void DropClips(string label, Action<AnimationClip[]> accept)
        {
            var area = GUILayoutUtility.GetRect(100, 42, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint && label.Contains("表情"))
                expressionDropRect = new Rect(GUIUtility.GUIToScreenPoint(area.position) - position.position, area.size);
            GUI.Box(area, label);
            var current = Event.current;
            if (!area.Contains(current.mousePosition) || current.type != EventType.DragUpdated && current.type != EventType.DragPerform) return;
            var clips = DragAndDrop.objectReferences.OfType<AnimationClip>().Where(clip => clip != null).Distinct().ToArray();
            var valid = GUI.enabled && clips.Length > 0 && DragAndDrop.objectReferences.All(item => item is AnimationClip);
            DragAndDrop.visualMode = valid ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
            if (valid && current.type == EventType.DragPerform) { DragAndDrop.AcceptDrag(); Queue(() => accept(clips)); }
            current.Use();
        }

        void DrawPoseInputs()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("3. 全身ポーズを同梱する（任意）", EditorStyles.boldLabel);
            DropClips("UnityのProjectからポーズの.animをここへドロップ", clips => {
                foreach (var clip in clips)
                    if (!session.PoseOptions.Manual.Any(pose => pose.Clip == clip))
                        session.PoseOptions.Manual.Add(new ManualPose { Clip = clip, Name = clip.name });
            });
            foreach (var pose in session.PoseOptions.Manual.ToArray())
            {
                EditorGUILayout.BeginHorizontal();
                pose.Name = EditorGUILayout.TextField(pose.Name);
                if (GUILayout.Button("削除", GUILayout.Width(50))) Queue(() => session.PoseOptions.Manual.Remove(pose));
                EditorGUILayout.EndHorizontal();
            }
            if (session.PoseOptions.Manual.Count > 0 && GUILayout.Button("ポーズのプレビュー・開始秒を確認")) Queue(() => {
                poseReview?.Close();
                poseReview = PoseReviewWindow.Show(source, session.PoseOptions, Array.Empty<GameObject>(), new ExportGimmickOptions { AutoExclude = false }, manualOnly: true);
            });
            EditorGUILayout.LabelField("ポーズと表情は別々に選べるデータとして同じVRMへ保存します。", EditorStyles.wordWrappedMiniLabel);
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
            poseReview?.Close(); poseReview = null;
            selectedCandidates.Clear(); clipInputs.Clear(); recommendations = null;
        }
    }
}
