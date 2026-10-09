using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    internal sealed class ExperimentalExpressionCaptureWindow : EditorWindow
    {
        [SerializeField] GameObject source;
        [SerializeField] string author = "";
        SavedExpressionVrm saved;
        bool showPoses, showRecords, showSetup;
        readonly System.Collections.Generic.Dictionary<ExperimentalExpressionCaptureSession.ClipInput, ExperimentalExpressionCaptureSession.Pose> fileRecords =
            new System.Collections.Generic.Dictionary<ExperimentalExpressionCaptureSession.ClipInput, ExperimentalExpressionCaptureSession.Pose>();
        ExperimentalExpressionCaptureSession session;
        PreviewRenderUtility preview;
        Action pending;
        string error, status, expressionName = "笑顔", filter = "";
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

        [MenuItem("VR Vlog/表情・ポーズを指定して書き出す", false, 110)]
        [MenuItem("VR Vlog/実験/表情を記録して書き出す", false, 150)]
        internal static void Open()
        {
            var window = GetWindow<ExperimentalExpressionCaptureWindow>();
            window.titleContent = new GUIContent("表情・ポーズ書き出し");
            window.minSize = new Vector2(580, 720);
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
            EditorGUILayout.LabelField("表情・ポーズを指定してVRMを書き出す", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("① アバターを指定　→　② 表情を確認　→　③ 保存・QR転送", EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(8);
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                EditorGUILayout.HelpBox("Playモードを終了すると編集できます。", MessageType.Info);
            using (new EditorGUI.DisabledScope(pending != null || EditorApplication.isPlayingOrWillChangePlaymode))
            {
                EditorGUILayout.LabelField("1. アバターを読み込む", EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(session != null))
                    source = (GameObject)EditorGUILayout.ObjectField("アバター", source, typeof(GameObject), true);
                if (session == null)
                {
                    EditorGUILayout.LabelField("Hierarchyからアバターの一番上のオブジェクトを指定します。", EditorStyles.wordWrappedMiniLabel);
                    using (new EditorGUI.DisabledScope(source == null))
                        if (GUILayout.Button("このアバターの表情を準備", GUILayout.Height(32))) Queue(Prepare);
                    if (source == null) EditorGUILayout.HelpBox("アバターを指定すると次へ進めます。", MessageType.None);
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
                status = "表情ファイルを追加してください。FaceEmoの候補からも選べます。";
                saved = null;
            }
            catch { Cleanup(); throw; }
        }

        void DrawSession()
        {
            var rect = GUILayoutUtility.GetRect(100, 200, GUILayout.ExpandWidth(true));
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
            showLegacy = EditorGUILayout.Foldout(showLegacy, "詳細：メニュー全体から自動取込", true);
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
                    if (candidate.Error != null || candidate.Unevaluated.Count != 0 || candidate.Animation.Count != 0)
                        EditorGUILayout.LabelField(candidate.Error ?? "固定した顔として取得できません。上の表情ファイル指定を使ってください。", EditorStyles.wordWrappedMiniLabel);
                }
                EditorGUILayout.EndScrollView();
                if (GUILayout.Button("選んだ表情をまとめて取り込む")) Queue(() => {
                    var result = session.ImportCandidates(entries.Where(candidate => selectedCandidates.TryGetValue(candidate, out var selectedValue) && selectedValue));
                    status = "取り込み " + result.Imported.Count + "件、省略 " + result.Skipped.Count + "件。記録した表情の「確認」で顔を確認できます。" +
                        (result.Skipped.Count == 0 ? "" : "\n" + string.Join("\n", result.Skipped));
                });
            }

            }
            showManual = EditorGUILayout.Foldout(showManual, "詳細：顔の手動調整・基準の顔", true);
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
            EditorGUILayout.LabelField("3. VRMを保存してスマホで使う", EditorStyles.boldLabel);
            var chosen = clipInputs.Where(input => input.Selected).ToArray();
            var remaining = session.Expressions.Count(pose => !fileRecords.Values.Contains(pose));
            EditorGUILayout.LabelField("保存する表情: " + (remaining + chosen.Length) + "件　/　ポーズ: " + session.PoseOptions.Manual.Count + "件");
            author = EditorGUILayout.TextField(new GUIContent("作者名（必須）", "VRMファイルに記録される作者名です。"), author);
            if (licenseOptions == null) licenseOptions = new AvatarLicenseOptions();
            AvatarLicenseSettingsUi.Draw(ref showLicenseSettings, licenseOptions);
            automaticBlink = EditorGUILayout.Toggle("瞬きを自動設定する", automaticBlink);
            if (remaining + chosen.Length == 0) EditorGUILayout.HelpBox("表情ファイルを追加し、保存する表情にチェックを入れてください。", MessageType.Info);
            else if (string.IsNullOrWhiteSpace(author)) EditorGUILayout.HelpBox("作者名を入力すると保存できます。", MessageType.Info);
            if (chosen.Any(input => input.Error != null)) EditorGUILayout.HelpBox("対応待ちの表情が選択されています。項目の理由を確認してください。", MessageType.Warning);
            using (new EditorGUI.DisabledScope(remaining + chosen.Length == 0 || string.IsNullOrWhiteSpace(author) || chosen.Any(input => input.Error != null)))
                if (GUILayout.Button("表情を記録してVRMを保存", GUILayout.Height(40))) Queue(Export);
            DrawSavedResult();
            showRecords = EditorGUILayout.Foldout(showRecords, "記録済みの表情・設定の保存（" + session.Expressions.Count + "件）", true);
            if (showRecords)
            {
                foreach (var pose in session.Expressions.ToArray())
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(pose.Name);
                    if (GUILayout.Button("顔を確認", GUILayout.Width(80))) Queue(() => { session.PreviewRecorded(pose); ShowFace(pose.Name); });
                    if (GUILayout.Button("削除", GUILayout.Width(50))) Queue(() => {
                        session.Expressions.Remove(pose);
                        foreach (var input in fileRecords.Where(pair => pair.Value == pose).Select(pair => pair.Key).ToArray()) { input.Selected = false; fileRecords.Remove(input); }
                    });
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("設定を保存")) Queue(() => { RecordSelectedClips(); SaveSettings(); });
                if (GUILayout.Button("設定を読み込む")) Queue(LoadSettings);
                EditorGUILayout.EndHorizontal();
            }
            showSetup = EditorGUILayout.Foldout(showSetup, "準備用コピーを作り直す（詳細）", true);
            if (showSetup && GUILayout.Button("コピーを作り直す") && EditorUtility.DisplayDialog("コピーを作り直す", "未保存の記録がある場合は失われます。設定を保存してから作り直してください。", "作り直す", "戻る")) Queue(Prepare);
            EditorGUILayout.LabelField("表情は指定時刻の固定の顔として保存します。材質・顔のボーン・表示切替を含む表情は未対応です。", EditorStyles.wordWrappedMiniLabel);
        }

        void ShowFace(string name) { previewName = name; pageScroll = Vector2.zero; Repaint(); }

        void RecordSelectedClips()
        {
            var selected = clipInputs.Where(input => input.Selected).ToArray();
            var poses = session.ReplaceClipRecords(selected, fileRecords.Values);
            fileRecords.Clear();
            for (var index = 0; index < selected.Length; index++) fileRecords.Add(selected[index], poses[index]);
            status = "表情を記録・更新しました: " + poses.Length + "件";
        }

        void DrawSavedResult()
        {
            if (saved == null) return;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("VRMを保存しました", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(Path.GetFileName(saved.Path) + "　/　表情 " + saved.Expressions + "件・ポーズ " + saved.Poses + "件", EditorStyles.wordWrappedLabel);
                EditorGUILayout.LabelField("下の転送は最後に保存したVRMを送ります。編集した内容を送るには、もう一度保存してください。", EditorStyles.wordWrappedMiniLabel);
                var transfer = LilToonExporterWindow.SavedVrmTransferMethod();
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(transfer == null || !File.Exists(saved.Path)))
                        if (GUILayout.Button("QRコードでスマホに送る", GUILayout.Height(32))) Queue(OpenSavedTransfer);
                    using (new EditorGUI.DisabledScope(!File.Exists(saved.Path)))
                        if (GUILayout.Button("保存先を開く", GUILayout.Height(32))) EditorUtility.RevealInFinder(saved.Path);
                }
                if (!File.Exists(saved.Path)) EditorGUILayout.HelpBox("保存したVRMが見つかりません。もう一度保存してください。", MessageType.Warning);
                else if (transfer == null) EditorGUILayout.HelpBox("このパッケージにはQR転送がありません。QR対応の試験用パッケージを導入してください。", MessageType.Info);
                else EditorGUILayout.LabelField("次の画面でアップロードを開始します。QR受信対応のiPhone版VR Vlogを使ってください。", EditorStyles.wordWrappedMiniLabel);
            }
        }

        void DrawClipInputs()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("2. 表情ファイルを追加して顔を確認", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("保存する表情にチェックを入れます。保存時に記録するので、先にプレビューだけ試せます。", EditorStyles.wordWrappedMiniLabel);
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
                clipScroll = EditorGUILayout.BeginScrollView(clipScroll, GUILayout.Height(Mathf.Min(300, clipInputs.Count * 145)));
                foreach (var input in clipInputs.ToArray())
                {
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        EditorGUILayout.BeginHorizontal();
                        using (new EditorGUI.DisabledScope(input.Error != null)) input.Selected = EditorGUILayout.Toggle(new GUIContent("", input.Error == null ? "この表情をVRMに保存" : "未対応のため保存できません。下の理由を確認してください。"), input.Selected, GUILayout.Width(20));
                        input.Name = EditorGUILayout.TextField("表情名", input.Name);
                        if (GUILayout.Button("削除", GUILayout.Width(50))) Queue(() => { if (fileRecords.TryGetValue(input, out var old)) session.Expressions.Remove(old); fileRecords.Remove(input); clipInputs.Remove(input); });
                        EditorGUILayout.EndHorizontal();
                        EditorGUILayout.LabelField(new GUIContent("元ファイル: " + (input.Clip != null ? input.Clip.name : "ファイルが見つかりません"), input.Clip != null ? AssetDatabase.GetAssetPath(input.Clip) : ""), EditorStyles.miniLabel);
                        var time = EditorGUILayout.Slider("収録する秒", input.Time, 0, input.Clip != null ? input.Clip.length : 0);
                        if (time != input.Time) { input.Time = time; Queue(() => input.Error = session.ClipError(input.Clip, input.Time)); }
                        if (input.Error == null)
                        {
                            EditorGUILayout.LabelField(input.Selected ? "保存対象" : "確認のみ・保存しない", EditorStyles.miniLabel);
                            if (GUILayout.Button("この表情をプレビュー")) Queue(() => { session.PreviewClip(input.Clip, input.Time); ShowFace(input.Name); });
                        }
                        else EditorGUILayout.HelpBox("保存できない理由:\n" + input.Error, MessageType.Warning);
                    }
                }
                EditorGUILayout.EndScrollView();
            }
            var selected = clipInputs.Where(input => input.Selected).ToArray();
            if (clipInputs.Count > 0)
            {
                EditorGUILayout.LabelField("追加済み " + clipInputs.Count + "件 / 保存対象 " + selected.Length + "件", EditorStyles.miniLabel);
                using (new EditorGUI.DisabledScope(selected.Length == 0 || selected.Any(input => input.Error != null)))
                    if (GUILayout.Button("選択した表情を記録・更新（確認用）")) Queue(RecordSelectedClips);
            }
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
            status = input.Error == null ? "追加しました。プレビューで顔を確認し、下の保存へ進めます。" : "追加しました。項目内の理由を確認してください。";
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
            showPoses = EditorGUILayout.Foldout(showPoses, "全身ポーズを追加（任意・" + session.PoseOptions.Manual.Count + "件）", true);
            if (!showPoses) return;
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
            fileRecords.Clear(); clipInputs.Clear(); showRecords = true;
            status = "記録設定を読み込み、基準の顔に戻しました。";
        }

        void Export()
        {
            var path = EditorUtility.SaveFilePanel("VRMの保存先", "", source.name + "-expressions", "vrm");
            if (string.IsNullOrEmpty(path)) return;
            SaveVrm(path);
        }

        internal void SaveVrm(string path)
        {
            RecordSelectedClips();
            var warnings = new System.Collections.Generic.List<string>(session.Warnings);
            var bytes = session.Export(source.name, author, warnings,
                automaticBlink ? null : new BlinkExportOptions { Mode = BlinkExportMode.None }, licenseOptions.Copy());
            AtomicWrite(path, bytes);
            saved = new SavedExpressionVrm(path, bytes, session.Expressions.Count, session.PoseOptions.Manual.Count);
            status = "VRMを保存しました。" + (warnings.Count == 0 ? "" : "\n" + string.Join("\n", warnings));
        }

        internal void OpenSavedTransfer()
        {
            if (saved == null) throw new InvalidOperationException("先にVRMを保存してください。");
            var entry = LilToonExporterWindow.SavedVrmTransferMethod();
            if (entry == null) throw new InvalidOperationException("QR対応のパッケージを導入してください。");
            try { entry.Invoke(null, new object[] { saved.VerifiedPath() }); }
            catch (System.Reflection.TargetInvocationException)
            { throw new InvalidOperationException("転送用のコピーを準備できませんでした。256 MiB以下の保存済みVRMを指定してください。"); }
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
            selectedCandidates.Clear(); clipInputs.Clear(); fileRecords.Clear(); recommendations = null; saved = null;
        }
    }
}
