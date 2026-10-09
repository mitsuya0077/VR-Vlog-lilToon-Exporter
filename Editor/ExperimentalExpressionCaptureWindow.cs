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
        bool showPoses;
        readonly System.Collections.Generic.Dictionary<ExperimentalExpressionCaptureSession.ClipInput, ExperimentalExpressionCaptureSession.Pose> fileRecords =
            new System.Collections.Generic.Dictionary<ExperimentalExpressionCaptureSession.ClipInput, ExperimentalExpressionCaptureSession.Pose>();
        ExperimentalExpressionCaptureSession session;
        PreviewRenderUtility preview;
        Action pending;
        string error, status;
        Vector2 pageScroll;
        Vector3 center;
        float distance = 1, yaw, zoom = 1;
        bool automaticBlink = true;
        BlinkExportOptions configuredBlink;
        BlinkConfigurationWindow blinkConfiguration;
        bool showLicenseSettings;
        readonly System.Collections.Generic.HashSet<AnimationClip> selectedClips = new System.Collections.Generic.HashSet<AnimationClip>();
        readonly System.Collections.Generic.Dictionary<AnimationClip, string> clipErrors = new System.Collections.Generic.Dictionary<AnimationClip, string>();
        readonly System.Collections.Generic.Dictionary<AnimationClip, Rect> candidateRects = new System.Collections.Generic.Dictionary<AnimationClip, Rect>();
        readonly System.Collections.Generic.Dictionary<AnimationClip, Rect> candidateToggleRects = new System.Collections.Generic.Dictionary<AnimationClip, Rect>();
        Rect addSelectedRect;
        // AssetDatabase-backed localization must run after ScriptableObject construction.
        string previewName = "基準の顔", clipFilter = "";
        Vector2 clipScroll, recommendationScroll;
        AnimationClip clipToAdd;
        PoseReviewWindow poseReview;
        Rect expressionDropRect;
        readonly System.Collections.Generic.List<ExperimentalExpressionCaptureSession.ClipInput> clipInputs =
            new System.Collections.Generic.List<ExperimentalExpressionCaptureSession.ClipInput>();
        System.Collections.Generic.List<ExperimentalExpressionCaptureSession.ClipRecommendation> recommendations;
        [SerializeField] AvatarLicenseOptions licenseOptions = new AvatarLicenseOptions();

        public static void Open()
        {
            var window = GetWindow<ExperimentalExpressionCaptureWindow>();
            window.titleContent = new GUIContent(ExporterLocalization.T("VRM書き出し"));
            window.minSize = new Vector2(600, 760);
        }

        void OnEnable() { EditorApplication.update += Advance; }
        void OnDisable() { EditorApplication.update -= Advance; pending = null; Cleanup(); }

        void Advance()
        {
            if (pending == null || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            var action = pending; pending = null;
            try { action(); error = null; }
            catch (OperationCanceledException) { status = ExporterLocalization.T("操作をキャンセルしました。"); }
            catch (Exception exception) { error = exception.Message; Debug.LogException(exception); }
            finally { EditorUtility.ClearProgressBar(); Repaint(); }
        }

        void Queue(Action action) { pending = action; error = null; Repaint(); }

        void OnGUI()
        {
            using (new EditorGUILayout.VerticalScope(new GUIStyle { padding = new RectOffset(16, 16, 12, 12) }))
            {
                EditorGUILayout.LabelField(ExporterLocalization.T("VRMを書き出す"), EditorStyles.boldLabel);
                EditorGUILayout.Space(8);
                var blocked = pending != null || EditorApplication.isPlayingOrWillChangePlaymode;
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                    EditorGUILayout.HelpBox(ExporterLocalization.T("Playモードを終了すると編集できます。"), MessageType.Info);
                using (new EditorGUI.DisabledScope(blocked))
                {
                    EditorGUILayout.BeginHorizontal();
                    using (new EditorGUI.DisabledScope(session != null))
                        source = (GameObject)EditorGUILayout.ObjectField(ExporterLocalization.T("アバター"), source, typeof(GameObject), true);
                    if (session != null && GUILayout.Button(ExporterLocalization.T("変更"), GUILayout.Width(50)) &&
                        (clipInputs.Count == 0 && session.PoseOptions.Manual.Count == 0 || EditorUtility.DisplayDialog(ExporterLocalization.T("アバターを変更"), ExporterLocalization.T("追加した表情・ポーズの一覧をリセットします。保存済みVRMは残ります。"), ExporterLocalization.T("変更する"), ExporterLocalization.T("戻る"))))
                        Queue(() => { Cleanup(); status = null; });
                    EditorGUILayout.EndHorizontal();
                    if (session == null)
                    {
                        EditorGUILayout.Space(8);
                        EditorGUILayout.LabelField(ExporterLocalization.T("Hierarchyからアバターの一番上のオブジェクトを指定します。"), EditorStyles.wordWrappedLabel);
                        using (new EditorGUI.DisabledScope(source == null))
                            if (GUILayout.Button(ExporterLocalization.T("書き出しを準備"), GUILayout.Height(34))) Queue(Prepare);
                    }
                    else if (preview != null)
                    {
                        EditorGUILayout.Space(8);
                        DrawPreview();
                        EditorGUILayout.Space(12);
                        pageScroll = EditorGUILayout.BeginScrollView(pageScroll);
                        DrawClipInputs();
                        EditorGUILayout.Space(12);
                        DrawPoseInputs();
                        EditorGUILayout.Space(16);
                        DrawSave();
                        DrawFeedback();
                        EditorGUILayout.EndScrollView();
                    }
                }
                if (pending != null) EditorGUILayout.LabelField(ExporterLocalization.T("処理しています…"), EditorStyles.miniLabel);
                if (session == null) DrawFeedback();
            }
        }

        void DrawFeedback()
        {
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            else if (!string.IsNullOrEmpty(status)) EditorGUILayout.LabelField(status, EditorStyles.wordWrappedMiniLabel);
        }

        void Prepare()
        {
            Cleanup();
            try
            {
                session = new ExperimentalExpressionCaptureSession(source, replayInstalledDefaults: false);
                recommendations = session.RecommendClips();
                foreach (var item in session.ClipErrors(recommendations.Select(item => item.Clip))) clipErrors[item.Key] = item.Value;
                preview = new PreviewRenderUtility();
                preview.AddSingleGO(session.Copy);
                var renderers = ExportRendererSelection.Enumerate(session.Copy).ToArray();
                if (renderers.Length == 0) throw new InvalidOperationException(ExporterLocalization.T("表示するメッシュがありません。"));
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
                yaw = 0; zoom = 1;
                previewName = ExporterLocalization.T("基準の顔");
                status = null;
                saved = null;
            }
            catch { Cleanup(); throw; }
        }

        void DrawPreview()
        {
            var rect = GUILayoutUtility.GetRect(100, 165, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint && rect.width > 0)
            {
                preview.BeginPreview(rect, GUIStyle.none);
                preview.camera.transform.position = center + Quaternion.AngleAxis(yaw, Vector3.up) * session.Copy.transform.forward * distance * zoom;
                preview.camera.transform.LookAt(center, Vector3.up);
                preview.Render();
                GUI.DrawTexture(rect, preview.EndPreview(), ScaleMode.ScaleToFit, false);
            }
            if (rect.Contains(Event.current.mousePosition))
            {
                if (Event.current.type == EventType.MouseDrag && Event.current.button == 0)
                { yaw = Mathf.Clamp(yaw + Event.current.delta.x, -90, 90); Event.current.Use(); Repaint(); }
                if (Event.current.type == EventType.ScrollWheel)
                { zoom = Mathf.Clamp(zoom + Event.current.delta.y * .04f, .25f, 2); Event.current.Use(); Repaint(); }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(previewName, EditorStyles.boldLabel);
                if (GUILayout.Button(new GUIContent(ExporterLocalization.T("元の顔"), ExporterLocalization.T("元の顔に戻す。画像をドラッグで回転、ホイールで拡大。")), GUILayout.Width(65)))
                    Queue(() => { session.RestoreBaseline(); previewName = ExporterLocalization.T("基準の顔"); });
            }
        }

        void DrawSave()
        {
            EditorGUILayout.LabelField(ExporterLocalization.T("保存"), EditorStyles.boldLabel);
            EditorGUILayout.Space(6);
            var chosen = clipInputs.Where(input => input.Selected).ToArray();
            var remaining = session.Expressions.Count(pose => !fileRecords.Values.Contains(pose));
            EditorGUILayout.LabelField(ExporterLocalization.T("表情 ") + (remaining + chosen.Length) + ExporterLocalization.T("件　・　ポーズ ") + session.PoseOptions.Manual.Count + ExporterLocalization.T("件"), EditorStyles.miniLabel);
            author = EditorGUILayout.TextField(new GUIContent(ExporterLocalization.T("作者名（必須）"), ExporterLocalization.T("VRMファイルに記録される作者名です。")), author);
            if (licenseOptions == null) licenseOptions = new AvatarLicenseOptions();
            AvatarLicenseSettingsUi.Draw(ref showLicenseSettings, licenseOptions);
            using (new EditorGUILayout.HorizontalScope())
            {
                automaticBlink = EditorGUILayout.Toggle(ExporterLocalization.T(configuredBlink?.Mode == BlinkExportMode.Manual ? "瞬き" : "瞬きを自動設定する"), automaticBlink);
                if (GUILayout.Button(ExporterLocalization.T("確認・調整"), GUILayout.Width(100))) Queue(ShowBlinkConfiguration);
            }
            EditorGUILayout.Space(8);
            if (string.IsNullOrWhiteSpace(author)) EditorGUILayout.LabelField(ExporterLocalization.T("作者名を入力すると保存できます。"), EditorStyles.miniLabel);
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(author) || chosen.Any(input => input.Error != null)))
                if (GUILayout.Button(ExporterLocalization.T("VRMを保存"), GUILayout.Height(38))) Queue(Export);
            EditorGUILayout.Space(8);
            DrawSavedResult();
        }

        void ShowFace(string name) { previewName = name; Repaint(); }

        void RecordSelectedClips()
        {
            var selected = clipInputs.Where(input => input.Selected).ToArray();
            foreach (var input in selected) input.Time = DefaultTime(input.Clip);
            var poses = session.ReplaceClipRecords(selected, fileRecords.Values);
            fileRecords.Clear();
            for (var index = 0; index < selected.Length; index++) fileRecords.Add(selected[index], poses[index]);
            status = ExporterLocalization.T("表情を記録・更新しました: ") + poses.Length + ExporterLocalization.T("件");
        }

        void DrawSavedResult()
        {
            if (saved == null) return;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(ExporterLocalization.T("VRMを保存しました"), EditorStyles.boldLabel);
                EditorGUILayout.LabelField(Path.GetFileName(saved.Path) + ExporterLocalization.T("　/　表情 ") + saved.Expressions + ExporterLocalization.T("件・ポーズ ") + saved.Poses + ExporterLocalization.T("件"), EditorStyles.wordWrappedLabel);
                EditorGUILayout.LabelField(ExporterLocalization.T("下の転送は最後に保存したVRMを送ります。編集した内容を送るには、もう一度保存してください。"), EditorStyles.wordWrappedMiniLabel);
                var transfer = LilToonExporterWindow.SavedVrmTransferMethod();
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(transfer == null || !File.Exists(saved.Path)))
                        if (GUILayout.Button(ExporterLocalization.T("QRコードでスマホに送る"), GUILayout.Height(32))) Queue(OpenSavedTransfer);
                    using (new EditorGUI.DisabledScope(!File.Exists(saved.Path)))
                        if (GUILayout.Button(ExporterLocalization.T("保存先を開く"), GUILayout.Height(32))) EditorUtility.RevealInFinder(saved.Path);
                }
                if (!File.Exists(saved.Path)) EditorGUILayout.HelpBox(ExporterLocalization.T("保存したVRMが見つかりません。もう一度保存してください。"), MessageType.Warning);
                else if (transfer == null) EditorGUILayout.HelpBox(ExporterLocalization.T("このパッケージにはQR転送がありません。QR対応の試験用パッケージを導入してください。"), MessageType.Info);
                else EditorGUILayout.LabelField(ExporterLocalization.T("次の画面でアップロードを開始します。QR受信対応のiPhone版VR Vlogを使ってください。"), EditorStyles.wordWrappedMiniLabel);
            }
        }

        void DrawClipInputs()
        {
            EditorGUILayout.LabelField(ExporterLocalization.T("候補ファイル"), EditorStyles.boldLabel);
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(ExporterLocalization.T("名前を押すと顔を確認できます。複数選んでまとめて追加できます。"), EditorStyles.wordWrappedMiniLabel);
            if (recommendations != null && recommendations.Count > 0)
            {
                clipFilter = EditorGUILayout.TextField(new GUIContent(ExporterLocalization.T("検索")), clipFilter);
                var visible = recommendations.Where(item => string.IsNullOrEmpty(clipFilter) || item.Clip.name.IndexOf(clipFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(ExporterLocalization.T("表示中を選択"), EditorStyles.miniButton, GUILayout.Width(100)))
                        foreach (var item in visible) if (clipErrors[item.Clip] == null && !clipInputs.Any(input => input.Clip == item.Clip)) selectedClips.Add(item.Clip);
                    if (GUILayout.Button(ExporterLocalization.T("選択を解除"), EditorStyles.miniButton, GUILayout.Width(90))) selectedClips.Clear();
                    GUILayout.FlexibleSpace();
                }
                recommendationScroll = EditorGUILayout.BeginScrollView(recommendationScroll, GUILayout.Height(Mathf.Min(155, Mathf.Max(35, visible.Length * 28))));
                foreach (var item in visible)
                {
                    var added = clipInputs.Any(input => input.Clip == item.Clip);
                    var reason = clipErrors[item.Clip];
                    using (new EditorGUILayout.HorizontalScope(GUILayout.Height(26)))
                    {
                        using (new EditorGUI.DisabledScope(added || reason != null))
                        {
                            var next = EditorGUILayout.Toggle(added || selectedClips.Contains(item.Clip), GUILayout.Width(20));
                            if (Event.current.type == EventType.Repaint) candidateToggleRects[item.Clip] = ScreenRect(GUILayoutUtility.GetLastRect());
                            if (!added && reason == null) { if (next) selectedClips.Add(item.Clip); else selectedClips.Remove(item.Clip); }
                        }
                        // Unsupported names remain clickable so the reason is visible.
                        if (GUILayout.Button(new GUIContent(item.Clip.name, reason ?? ExporterLocalization.T(item.Source) + "\n" + AssetDatabase.GetAssetPath(item.Clip)), EditorStyles.label, GUILayout.Height(24)))
                        {
                            var clip = item.Clip;
                            Queue(() => { if (reason != null) throw new InvalidOperationException(reason); session.PreviewClip(clip, DefaultTime(clip)); ShowFace(clip.name); });
                        }
                        var nameRect = GUILayoutUtility.GetLastRect();
                        if (Event.current.type == EventType.Repaint) candidateRects[item.Clip] = ScreenRect(nameRect);
                        GUILayout.Label(added ? ExporterLocalization.T("追加済み") : reason != null ? ExporterLocalization.T("対応外") : "", EditorStyles.miniLabel, GUILayout.Width(60));
                    }
                }
                EditorGUILayout.EndScrollView();
                var count = selectedClips.Count(clip => !clipInputs.Any(input => input.Clip == clip));
                using (new EditorGUI.DisabledScope(count == 0))
                    if (GUILayout.Button(ExporterLocalization.T("選んだ ") + count + ExporterLocalization.T("件を追加"), GUILayout.Height(30))) Queue(AddSelectedClips);
                if (Event.current.type == EventType.Repaint) addSelectedRect = ScreenRect(GUILayoutUtility.GetLastRect());
            }
            else EditorGUILayout.LabelField(ExporterLocalization.T("候補がありません。Projectから.animを追加できます。"), EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(8);
            DropClips(ExporterLocalization.T("Projectから表情の.animをドロップ（複数可）"), AddClips, true);
            using (new EditorGUILayout.HorizontalScope())
            {
                clipToAdd = (AnimationClip)EditorGUILayout.ObjectField(clipToAdd, typeof(AnimationClip), false);
                using (new EditorGUI.DisabledScope(clipToAdd == null))
                    if (GUILayout.Button(ExporterLocalization.T("追加"), GUILayout.Width(60))) { var clip = clipToAdd; Queue(() => AddClip(clip)); }
            }
            if (clipInputs.Count == 0) return;
            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField(ExporterLocalization.T("追加した表情（保存対象にチェック）"), EditorStyles.boldLabel);
            clipScroll = EditorGUILayout.BeginScrollView(clipScroll, GUILayout.Height(Mathf.Min(160, clipInputs.Count * 32 + 8)));
            foreach (var input in clipInputs.ToArray())
            {
                using (new EditorGUILayout.HorizontalScope(GUILayout.Height(28)))
                {
                    using (new EditorGUI.DisabledScope(input.Error != null))
                        input.Selected = EditorGUILayout.Toggle(new GUIContent("", input.Error ?? ExporterLocalization.T("この表情を保存")), input.Selected, GUILayout.Width(20));
                    input.Name = EditorGUILayout.TextField(input.Name);
                    if (GUILayout.Button(new GUIContent(input.Error == null ? ExporterLocalization.T("確認") : ExporterLocalization.T("理由"), input.Error ?? input.Clip.name), GUILayout.Width(50)))
                        Queue(() => PreviewClipInput(input));
                    if (GUILayout.Button(new GUIContent("×", ExporterLocalization.T("一覧から削除")), GUILayout.Width(26))) Queue(() => {
                        if (fileRecords.TryGetValue(input, out var old)) session.Expressions.Remove(old);
                        fileRecords.Remove(input); clipInputs.Remove(input);
                    });
                }
            }
            EditorGUILayout.EndScrollView();
        }

        Rect ScreenRect(Rect rect) => new Rect(GUIUtility.GUIToScreenPoint(rect.position) - position.position, rect.size);
        internal static float DefaultTime(AnimationClip clip) => clip == null ? 0 : clip.length;

        void PreviewClipInput(ExperimentalExpressionCaptureSession.ClipInput input)
        {
            input.Time = DefaultTime(input.Clip);
            input.Error = session.ClipError(input.Clip, input.Time);
            if (input.Error != null) throw new InvalidOperationException(input.Error);
            session.PreviewClip(input.Clip, input.Time);
            ShowFace(input.Name);
        }

        void AddSelectedClips()
        {
            // Follow the visible source ordering; a HashSet alone would choose an arbitrary preview.
            AddClips(recommendations.Where(item => selectedClips.Contains(item.Clip)).Select(item => item.Clip).ToArray());
            selectedClips.Clear();
        }

        void AddClips(AnimationClip[] clips)
        {
            var additions = clips.Where(clip => clip != null && !clipInputs.Any(input => input.Clip == clip)).Distinct().ToArray();
            if (clipInputs.Count + additions.Length > ExperimentalExpressionCaptureSession.MaximumExpressions)
                throw new InvalidOperationException(ExporterLocalization.T("表情は64件までです。選択を減らして追加してください。"));
            foreach (var clip in additions) AddClip(clip);
            if (additions.Length > 0) status = additions.Length + ExporterLocalization.T("件を追加しました。確認してVRMを保存してください。");
        }

        void AddClip(AnimationClip clip)
        {
            if (clip == null || clipInputs.Any(input => input.Clip == clip)) return;
            if (clipInputs.Count >= ExperimentalExpressionCaptureSession.MaximumExpressions)
                throw new InvalidOperationException(ExporterLocalization.T("表情は64件までです。"));
            var input = new ExperimentalExpressionCaptureSession.ClipInput { Clip = clip, Name = clip.name, Time = DefaultTime(clip) };
            input.Error = session.ClipError(clip, input.Time); input.Selected = input.Error == null;
            clipInputs.Add(input);
            if (input.Error == null) { session.PreviewClip(clip, input.Time); ShowFace(input.Name); }
            status = input.Error == null ? ExporterLocalization.T("追加しました。顔を確認して保存してください。") : ExporterLocalization.T("対応外のファイルです。「理由」で確認できます。");
        }

        void DropClips(string label, Action<AnimationClip[]> accept, bool expression = false)
        {
            var area = GUILayoutUtility.GetRect(100, 42, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint && expression)
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
            showPoses = EditorGUILayout.Foldout(showPoses, ExporterLocalization.T("全身ポーズを追加（任意・") + session.PoseOptions.Manual.Count + ExporterLocalization.T("件）"), true);
            if (!showPoses) return;
            DropClips(ExporterLocalization.T("UnityのProjectからポーズの.animをここへドロップ"), clips => {
                foreach (var clip in clips)
                    if (!session.PoseOptions.Manual.Any(pose => pose.Clip == clip))
                        session.PoseOptions.Manual.Add(new ManualPose { Clip = clip, Name = clip.name });
            });
            foreach (var pose in session.PoseOptions.Manual.ToArray())
            {
                EditorGUILayout.BeginHorizontal();
                pose.Name = EditorGUILayout.TextField(pose.Name);
                if (GUILayout.Button(ExporterLocalization.T("削除"), GUILayout.Width(50))) Queue(() => session.PoseOptions.Manual.Remove(pose));
                EditorGUILayout.EndHorizontal();
            }
            if (session.PoseOptions.Manual.Count > 0 && GUILayout.Button(ExporterLocalization.T("ポーズを確認"))) Queue(() => {
                poseReview?.Close();
                poseReview = PoseReviewWindow.Show(source, session.PoseOptions, Array.Empty<GameObject>(), new ExportGimmickOptions { AutoExclude = false }, manualOnly: true);
            });
            EditorGUILayout.LabelField(ExporterLocalization.T("ポーズと表情は別々に選べるデータとして同じVRMへ保存します。"), EditorStyles.wordWrappedMiniLabel);
        }

        void Export()
        {
            var path = EditorUtility.SaveFilePanel(ExporterLocalization.T("VRMの保存先"), "", source.name + "-expressions", "vrm");
            if (string.IsNullOrEmpty(path)) return;
            SaveVrm(path);
        }

        internal void SaveVrm(string path)
        {
            RecordSelectedClips();
            var warnings = new System.Collections.Generic.List<string>(session.Warnings);
            var bytes = session.Export(source.name, author, warnings,
                automaticBlink ? configuredBlink?.Copy() : new BlinkExportOptions { Mode = BlinkExportMode.None }, licenseOptions.Copy());
            AtomicWrite(path, bytes);
            var extensions = (System.Collections.Generic.Dictionary<string, object>)GlbDocument.Read(bytes).Json["extensions"];
            var poseCount = extensions.TryGetValue(VRVlog.Poses.HumanoidPoseData.Extension, out var poses)
                ? VRVlog.Poses.HumanoidPoseData.Read(poses).Count : 0;
            if (extensions.TryGetValue(VRVlog.Poses.HumanoidAnimationData.Extension, out var animations))
                poseCount += VRVlog.Poses.HumanoidAnimationData.Read(animations).Count;
            saved = new SavedExpressionVrm(path, bytes, session.Expressions.Count, poseCount);
            status = ExporterLocalization.T("VRMを保存しました。") + (warnings.Count == 0 ? "" : "\n" + string.Join("\n", warnings));
        }

        internal void OpenSavedTransfer()
        {
            if (saved == null) throw new InvalidOperationException(ExporterLocalization.T("先にVRMを保存してください。"));
            var entry = LilToonExporterWindow.SavedVrmTransferMethod();
            if (entry == null) throw new InvalidOperationException(ExporterLocalization.T("QR対応のパッケージを導入してください。"));
            try { entry.Invoke(null, new object[] { saved.VerifiedPath() }); }
            catch (System.Reflection.TargetInvocationException)
            { throw new InvalidOperationException(ExporterLocalization.T("転送用のコピーを準備できませんでした。256 MiB以下の保存済みVRMを指定してください。")); }
        }

        void ShowBlinkConfiguration()
        {
            blinkConfiguration?.Close();
            var options = automaticBlink ? configuredBlink?.Copy() ?? new BlinkExportOptions() : new BlinkExportOptions { Mode = BlinkExportMode.None };
            blinkConfiguration = BlinkConfigurationWindow.Show(source, options, selected => {
                configuredBlink = selected.Mode == BlinkExportMode.Auto ? null : selected.Copy();
                automaticBlink = selected.Mode != BlinkExportMode.None;
                Repaint();
            });
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
            blinkConfiguration?.Close(); blinkConfiguration = null; configuredBlink = null;
            // PreviewRenderUtility owns the preview scene; session owns prepared assets.
            preview?.Cleanup(); preview = null;
            session?.Dispose(); session = null;
            poseReview?.Close(); poseReview = null;
            selectedClips.Clear(); clipErrors.Clear(); candidateRects.Clear(); candidateToggleRects.Clear(); clipInputs.Clear(); fileRecords.Clear(); recommendations = null; saved = null;
        }
    }
}
