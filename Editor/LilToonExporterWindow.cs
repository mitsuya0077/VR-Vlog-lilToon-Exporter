using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace VRVlog.LilToonExporter
{
    public sealed class LilToonExporterWindow : EditorWindow
    {
        private const string SupportedLilToonVersion = Compatibility.DependencyPolicy.LilToonVersion;
        private GameObject avatar;
        private string author = "";
        private string outputPath = "";
        private string lastSavedPath, lastSavedSummary;
        private string[] lastSavedWarnings = Array.Empty<string>();
        private bool showAppearanceOptions;
        private bool showEnvironment;
        private bool showBlink;
        private BlinkExportOptions blinkOptions = new BlinkExportOptions();
        private PoseExportOptions poseOptions = new PoseExportOptions();
        private readonly List<GameObject> excludedObjects = new List<GameObject>();
        private bool autoExcludeGimmicks = true;
        private readonly List<GameObject> includedGimmicks = new List<GameObject>();
        private List<ExportGimmickFinding> gimmickFindings = new List<ExportGimmickFinding>();
        private double nextGimmickScan;
        private readonly BlinkStatusCache blinkStatus = new BlinkStatusCache();
        private Vector2 scrollPosition;
        private GUIStyle requiredLabelStyle;
        private GUIStyle hintStyle;
        private GUIStyle centeredHintStyle;
        private GUIStyle placeholderStyle;
        private const string AuthorControlName = "VRVlogExporterAuthor";

        // IMGUI runs for layout, repaint and input. Only the status label may
        // reuse a scan; export and preview still resolve the live avatar.
        internal sealed class BlinkStatusCache
        {
            internal BlinkExportSession Resolved { get; private set; }
            internal string Error { get; private set; }
            private double nextScan = double.NegativeInfinity;

            internal void Refresh(double now, Func<BlinkExportSession> resolve)
            {
                if (now < nextScan) return;
                Invalidate();
                nextScan = now + 1;
                try { Resolved = resolve(); }
                catch (Exception exception) { Error = exception.Message; }
            }

            internal void Invalidate()
            {
                Resolved?.Dispose();
                Resolved = null;
                Error = null;
                nextScan = double.NegativeInfinity;
            }
        }

        private void OnDisable() => blinkStatus.Invalidate();

        private void InvalidateAvatarScan()
        {
            nextGimmickScan = 0;
            blinkStatus.Invalidate();
        }

        public static void Open()
        {
            var window = GetWindow<LilToonExporterWindow>(true, ExporterLocalization.T("VR Vlog VRM書き出し"));
            window.minSize = new Vector2(430f, 430f);
        }

        private void OnGUI()
        {
            using (var scroll = new EditorGUILayout.ScrollViewScope(scrollPosition))
            {
                scrollPosition = scroll.scrollPosition;
                using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true)))
                {
                    EditorGUILayout.Space(16f);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(16f);
                        using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true))) DrawWindow();
                        GUILayout.Space(16f);
                    }
                    EditorGUILayout.Space(16f);
                }
            }
        }

        private void DrawWindow()
        {
            EnsureStyles();
            EditorGUILayout.LabelField(ExporterLocalization.T("保存先を選ぶと、VRMの書き出しと保存まで自動で進みます。"), EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(18f);
            DrawRequiredLabel(ExporterLocalization.T("アバター"), ExporterLocalization.T("Hierarchyにあるアバターの一番上のオブジェクトを指定します。"));
            var selectedAvatar = (GameObject)EditorGUILayout.ObjectField(
                avatar,
                typeof(GameObject),
                true,
                GUILayout.Height(24f));
            if (selectedAvatar != avatar)
            {
                blinkOptions = new BlinkExportOptions();
                poseOptions = new PoseExportOptions();
                showBlink = false;
                excludedObjects.Clear();
                includedGimmicks.Clear();
                gimmickFindings.Clear();
                InvalidateAvatarScan();
            }
            avatar = selectedAvatar;
            EditorGUILayout.LabelField(ExporterLocalization.T("Hierarchyからアバターを指定"), hintStyle);

            EditorGUILayout.Space(14f);
            DrawRequiredLabel(ExporterLocalization.T("作者名"), ExporterLocalization.T("VRMファイルに記録される作者名です。"));
            var authorRect = EditorGUILayout.GetControlRect(false, 24f);
            GUI.SetNextControlName(AuthorControlName);
            author = EditorGUI.TextField(authorRect, author);
            if (string.IsNullOrEmpty(author) && GUI.GetNameOfFocusedControl() != AuthorControlName)
                GUI.Label(authorRect, ExporterLocalization.T("作者名を入力"), placeholderStyle);

            EditorGUILayout.Space(12f);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(avatar == null))
                    if (GUILayout.Button(ExporterLocalization.T("ポーズを確認・調整"), GUILayout.Width(160f), GUILayout.Height(26f)))
                        PoseReviewWindow.Show(avatar, poseOptions, excludedObjects.ToArray(),
                            new ExportGimmickOptions { AutoExclude = autoExcludeGimmicks, IncludedObjects = includedGimmicks.ToArray() });
            }

            DrawSeparator();
            showAppearanceOptions = EditorGUILayout.Foldout(showAppearanceOptions, ExporterLocalization.T("書き出し設定"), true);
            if (showAppearanceOptions)
            {
                EditorGUILayout.Space(6f);
                DrawBlink();
                EditorGUILayout.Space(6f);
                DrawGimmicks();
                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField(ExporterLocalization.T("書き出さないオブジェクト（ペット・ギミックなど）"));
                EditorGUILayout.HelpBox(ExporterLocalization.T("除外したい子オブジェクトを指定します。ワンクリック書き出しに適用され、Unityの元アバターは変更しません。"), MessageType.None);
                EditorGUI.BeginChangeCheck();
                for (var index = 0; index < excludedObjects.Count; index++)
                {
                    EditorGUILayout.BeginHorizontal();
                    excludedObjects[index] = (GameObject)EditorGUILayout.ObjectField(excludedObjects[index], typeof(GameObject), true);
                    if (GUILayout.Button(ExporterLocalization.T("削除"), GUILayout.Width(48))) { excludedObjects.RemoveAt(index); index--; InvalidateAvatarScan(); }
                    EditorGUILayout.EndHorizontal();
                }
                if (EditorGUI.EndChangeCheck()) InvalidateAvatarScan();
                if (GUILayout.Button(ExporterLocalization.T("除外するオブジェクトを追加"))) { excludedObjects.Add(null); InvalidateAvatarScan(); }
            }

            EditorGUILayout.Space(14f);
            var canExport = avatar != null && !string.IsNullOrWhiteSpace(author);
            using (new EditorGUI.DisabledScope(!canExport))
                if (GUILayout.Button(ExporterLocalization.T("保存先を選んでVRMを書き出す"), GUILayout.Height(40f))) ExportOneClick();

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField(canExport ? "" : ExporterLocalization.T("アバターと作者名を入力してください"), centeredHintStyle);

            if (!string.IsNullOrEmpty(lastSavedPath))
            {
                EditorGUILayout.HelpBox(ExporterLocalization.T("VRMを保存しました。") + "\n" + System.IO.Path.GetFileName(lastSavedPath) + "\n" + lastSavedSummary, MessageType.Info);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(ExporterLocalization.T("保存先を開く"))) EditorUtility.RevealInFinder(lastSavedPath);
                    if (lastSavedWarnings.Length > 0 && GUILayout.Button(ExporterLocalization.T("書き出しの詳細"))) ExportAppearanceReportWindow.Open(lastSavedWarnings);
                }
            }

            DrawSeparator();
            showEnvironment = EditorGUILayout.Foldout(showEnvironment, ExporterLocalization.T("動作環境"), true);
            if (showEnvironment)
            {
                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("lilToon", ExporterLocalization.T(InstalledLilToonStatus()));
                EditorGUILayout.LabelField("UniVRM", Compatibility.DependencyPolicy.UniVrmVersions);
            }
        }

        private void EnsureStyles()
        {
            if (requiredLabelStyle != null) return;
            requiredLabelStyle = new GUIStyle(EditorStyles.helpBox)
            {
                fontSize = 10,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(5, 5, 1, 1),
                margin = new RectOffset(6, 0, 0, 0)
            };
            hintStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                wordWrap = true,
                normal = { textColor = EditorStyles.centeredGreyMiniLabel.normal.textColor }
            };
            centeredHintStyle = new GUIStyle(hintStyle) { alignment = TextAnchor.MiddleCenter };
            placeholderStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(5, 5, 0, 0),
                normal = { textColor = EditorStyles.centeredGreyMiniLabel.normal.textColor }
            };
        }

        private void DrawRequiredLabel(string label, string tooltip)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(new GUIContent(label, tooltip), EditorStyles.boldLabel, GUILayout.ExpandWidth(false));
                GUILayout.Label(ExporterLocalization.T("必須"), requiredLabelStyle, GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();
            }
            EditorGUILayout.Space(3f);
        }

        private static void DrawSeparator()
        {
            EditorGUILayout.Space(12f);
            var rect = EditorGUILayout.GetControlRect(false, 1f);
            EditorGUI.DrawRect(rect, EditorGUIUtility.isProSkin
                ? new Color(1f, 1f, 1f, 0.14f)
                : new Color(0f, 0f, 0f, 0.18f));
            EditorGUILayout.Space(10f);
        }

        private BlinkExportSession ResolveBlink()
        {
            using var manual = new ExportObjectExclusions(avatar, excludedObjects);
            var options = new ExportGimmickOptions { AutoExclude = autoExcludeGimmicks, IncludedObjects = includedGimmicks.ToArray() };
            var findings = autoExcludeGimmicks ? ExportGimmickDetection.Analyze(avatar, manual.Contains) : new List<ExportGimmickFinding>();
            using var exclusions = new ExportObjectExclusions(avatar, excludedObjects.Concat(ExportGimmickDetection.AutomaticRoots(findings, options)));
            return BlinkExportSession.Resolve(avatar, blinkOptions, exclusions.Contains);
        }

        private void DrawBlink()
        {
            if (avatar == null) return;
            blinkStatus.Refresh(EditorApplication.timeSinceStartup, ResolveBlink);
            var resolved = blinkStatus.Resolved;
            var error = blinkStatus.Error;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(ExporterLocalization.T("瞬き"), ExporterLocalization.T(resolved?.Description) ?? ExporterLocalization.T("設定が必要です"));
            if (GUILayout.Button(showBlink ? ExporterLocalization.T("設定を閉じる") : ExporterLocalization.T("確認・調整"), GUILayout.Width(100))) showBlink = !showBlink;
            EditorGUILayout.EndHorizontal();
            if (!showBlink && error == null) return;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(ExporterLocalization.T("瞬きはこの欄で設定します。設定後、「開閉をプレビュー」で確認できます。"), EditorStyles.wordWrappedLabel);
                var mode = (BlinkExportMode)EditorGUILayout.Popup(ExporterLocalization.T("設定"), (int)blinkOptions.Mode,
                    new[] { ExporterLocalization.T("自動"), ExporterLocalization.T("手動"), ExporterLocalization.T("瞬きなし") });
                if (mode != blinkOptions.Mode)
                {
                    blinkOptions.SelectMode(mode, resolved);
                    blinkStatus.Invalidate();
                    Repaint();
                }
                if (blinkOptions.Mode == BlinkExportMode.Auto && error != null)
                {
                    EditorGUILayout.HelpBox(ExporterLocalization.T(error), MessageType.Warning);
                    EditorGUILayout.LabelField(ExporterLocalization.T("自動では閉眼用の変形を確定できません。手動でメッシュと変形を選択してください。"), EditorStyles.wordWrappedLabel);
                    if (GUILayout.Button(ExporterLocalization.T("手動で閉眼を設定")))
                    {
                        blinkOptions.SelectMode(BlinkExportMode.Manual);
                        showBlink = true;
                        blinkStatus.Invalidate();
                        Repaint();
                    }
                }
                if (blinkOptions.Mode == BlinkExportMode.Manual)
                {
                    EditorGUILayout.LabelField(ExporterLocalization.T("メッシュと閉眼用の変形を選び、適用量を調整してください。"), EditorStyles.wordWrappedLabel);
                    DrawBlinkBindings(ExporterLocalization.T("両目"), blinkOptions.Both);
                    var individual = blinkOptions.Left.Count > 0 || blinkOptions.Right.Count > 0;
                    var next = EditorGUILayout.ToggleLeft(ExporterLocalization.T("左右を個別に設定"), individual);
                    if (next && !individual) { blinkOptions.Left.Add(new BlinkShapeBinding()); blinkOptions.Right.Add(new BlinkShapeBinding()); blinkStatus.Invalidate(); }
                    if (!next && individual) { blinkOptions.Left.Clear(); blinkOptions.Right.Clear(); blinkStatus.Invalidate(); }
                    if (next) { DrawBlinkBindings(ExporterLocalization.T("左目"), blinkOptions.Left); DrawBlinkBindings(ExporterLocalization.T("右目"), blinkOptions.Right); }
                }
                if (error != null && blinkOptions.Mode != BlinkExportMode.Auto) EditorGUILayout.HelpBox(ExporterLocalization.T(error), MessageType.Warning);
                using (new EditorGUI.DisabledScope(error != null))
                    if (GUILayout.Button(ExporterLocalization.T("開閉をプレビュー")))
                        try
                        {
                            BlinkPreviewWindow.Show(avatar, blinkOptions.Copy(), excludedObjects.ToArray(),
                                new ExportGimmickOptions { AutoExclude = autoExcludeGimmicks, IncludedObjects = includedGimmicks.ToArray() });
                        }
                        catch (Exception exception) { ExportFailureWindow.Show(exception); }
            }
        }

        private void DrawBlinkBindings(string label, List<BlinkShapeBinding> bindings)
        {
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            var renderers = ExportRendererSelection.Enumerate(avatar).OfType<SkinnedMeshRenderer>()
                .Where(renderer => renderer.sharedMesh != null && renderer.sharedMesh.blendShapeCount > 0).ToArray();
            var rendererChoices = new[] { ExporterLocalization.T("メッシュを選択") }.Concat(renderers.Select(renderer =>
                AnimationUtility.CalculateTransformPath(renderer.transform, avatar.transform))).ToArray();
            for (var i = 0; i < bindings.Count; i++)
            {
                var binding = bindings[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    var renderer = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(ExporterLocalization.T("メッシュ"), binding.Renderer, typeof(SkinnedMeshRenderer), true);
                    if (renderer != binding.Renderer) { binding.Renderer = renderer; binding.Shape = ""; }
                    if (GUILayout.Button(ExporterLocalization.T("削除"), GUILayout.Width(48))) { bindings.RemoveAt(i--); blinkStatus.Invalidate(); continue; }
                }
                var selectedRenderer = Array.IndexOf(renderers, binding.Renderer);
                var nextRenderer = EditorGUILayout.Popup(ExporterLocalization.T("アバター内のメッシュ"), selectedRenderer + 1, rendererChoices);
                if (nextRenderer > 0 && renderers[nextRenderer - 1] != binding.Renderer)
                {
                    binding.Renderer = renderers[nextRenderer - 1];
                    binding.Shape = "";
                }
                var mesh = binding.Renderer != null ? binding.Renderer.sharedMesh : null;
                if (mesh == null) continue;
                var names = Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToArray();
                var selected = Array.IndexOf(names, binding.Shape);
                var choices = new[] { string.IsNullOrEmpty(binding.Shape) ? ExporterLocalization.T("変形を選択") : ExporterLocalization.T("見つかりません: ") + binding.Shape }.Concat(names).ToArray();
                var next = EditorGUILayout.Popup(ExporterLocalization.T("閉眼用の変形"), selected + 1, choices);
                if (next > 0) binding.Shape = names[next - 1];
                binding.Weight = EditorGUILayout.Slider(ExporterLocalization.T("適用量 (%)"), binding.Weight, 0, 100);
            }
            if (EditorGUI.EndChangeCheck()) blinkStatus.Invalidate();
            if (GUILayout.Button(label + ExporterLocalization.T("の変形を追加"))) { bindings.Add(new BlinkShapeBinding()); blinkStatus.Invalidate(); }
        }

        private void DrawGimmicks()
        {
            EditorGUI.BeginChangeCheck();
            autoExcludeGimmicks = EditorGUILayout.Toggle(ExporterLocalization.T("補助ギミックを自動除外"), autoExcludeGimmicks);
            if (EditorGUI.EndChangeCheck()) InvalidateAvatarScan();
            EditorGUILayout.HelpBox(ExporterLocalization.T("ワンクリック書き出しで、確認できた補助ギミックを省略します。衣装や表情は保持し、Unityの元アバターは変更しません。判定は書き出すたびに更新します。"), MessageType.None);
            if (avatar == null) return;
            if (GUILayout.Button(ExporterLocalization.T("検出一覧を更新"))) InvalidateAvatarScan();
            if (Event.current.type == EventType.Layout && EditorApplication.timeSinceStartup >= nextGimmickScan)
            {
                bool Manual(Transform t) => excludedObjects.Any(go => go != null && go != avatar &&
                    (t == go.transform || t.IsChildOf(go.transform)));
                gimmickFindings = ExportGimmickDetection.Analyze(avatar, Manual);
                nextGimmickScan = EditorApplication.timeSinceStartup + 1;
            }
            var roots = gimmickFindings.Where(f => f.Target != null && f.Unit == GimmickExclusionUnit.Hierarchy).ToArray();
            foreach (var finding in gimmickFindings)
            {
                if (finding.Target == null || roots.Any(root => root != finding && finding.Target.transform.IsChildOf(root.Target.transform))) continue;
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.ObjectField(finding.Target, typeof(GameObject), true);
                EditorGUILayout.LabelField(ExporterLocalization.T(finding.Reason), EditorStyles.wordWrappedLabel);
                if (finding.Unit == GimmickExclusionUnit.Review)
                {
                    if (GUILayout.Button("手動の除外一覧に追加") && !excludedObjects.Contains(finding.Target))
                    {
                        excludedObjects.Add(finding.Target);
                        InvalidateAvatarScan();
                    }
                }
                else
                {
                    var inherited = includedGimmicks.Any(go => go != null && go != finding.Target && finding.Target.transform.IsChildOf(go.transform));
                    using (new EditorGUI.DisabledScope(!autoExcludeGimmicks || inherited))
                    {
                        var keep = includedGimmicks.Contains(finding.Target);
                        var selected = EditorGUILayout.ToggleLeft(inherited ? ExporterLocalization.T("親の指定により含める") : ExporterLocalization.T("この対象は含める"), keep || inherited);
                        if (!inherited && selected != keep)
                        {
                            if (selected) includedGimmicks.Add(finding.Target);
                            else includedGimmicks.Remove(finding.Target);
                            blinkStatus.Invalidate();
                        }
                    }
                }
                EditorGUILayout.EndVertical();
            }
            // Keep exceptions editable if an asset changes and is no longer a
            // candidate, or a formerly safe hierarchy becomes a review item.
            for (var i = includedGimmicks.Count - 1; i >= 0; i--)
            {
                var go = includedGimmicks[i];
                if (go == null) { includedGimmicks.RemoveAt(i); blinkStatus.Invalidate(); continue; }
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(ExporterLocalization.T("含める指定: ") + go.name);
                if (GUILayout.Button(ExporterLocalization.T("解除"), GUILayout.Width(48))) { includedGimmicks.RemoveAt(i); blinkStatus.Invalidate(); }
                EditorGUILayout.EndHorizontal();
            }
        }

        private void ExportOneClick()
        {
            try { using var resolved = ResolveBlink(); }
            catch (Exception)
            {
                blinkStatus.Invalidate();
                showAppearanceOptions = true;
                showBlink = true;
                Repaint();
                return;
            }
            outputPath = EditorUtility.SaveFilePanel(ExporterLocalization.T("VRMの保存先"), "", DefaultFileName(), "vrm");
            if (string.IsNullOrEmpty(outputPath)) return;
            lastSavedPath = null;
            lastSavedSummary = null;
            lastSavedWarnings = Array.Empty<string>();
            // A nonmodal failure window may remain open while the user changes
            // this window. Retry exactly the avatar, destination and options
            // they chose for this attempt; revalidate the live avatar each time.
            var targetAvatar = avatar;
            var targetName = AvatarName();
            var targetAuthor = author;
            var targetOutput = outputPath;
            var targetBlink = blinkOptions.Copy();
            var targetPoses = poseOptions.Copy();
            var targetExclusions = excludedObjects.ToArray();
            var targetGimmicks = new ExportGimmickOptions { AutoExclude = autoExcludeGimmicks, IncludedObjects = includedGimmicks.ToArray() };
            if (File.Exists(targetOutput) && !EditorUtility.DisplayDialog(
                ExporterLocalization.T("ファイルを上書きしますか？"), targetOutput,
                ExporterLocalization.T("上書き"), ExporterLocalization.T("キャンセル"))) return;
            ExportRecoverySession session = null;
            void Completed() => ShowExportCompletion(session.LastSuccess.Bytes, session.LastSuccess.Warnings, targetOutput);
            try
            {
                session = ExportAndSaveNormally(targetAvatar, targetOutput, (options, report, warnings) =>
                {
                    if (targetAvatar == null) throw new InvalidOperationException(ExporterLocalization.T("この書き出しで選んだアバターが見つかりません。アバターを指定し直してください。"));
                    return UniVrmOneClickExporter.Export(targetAvatar, targetName, targetAuthor, warnings, false,
                        PackageVersion(), RequireSupportedLilToon(), false, targetExclusions, null, targetGimmicks, targetBlink, targetPoses,
                        recoveryOptions: options, recoveryReport: report);
                }, (bytes, warnings) => ShowExportCompletion(bytes, warnings, targetOutput), targetExclusions, targetGimmicks);
                if (session != null) ExportFailureWindow.Show(session, Completed);
            }
            catch (Exception exception) { ExportFailureWindow.Show(exception); }
        }

        // A fresh export has no old diagnosis or preview to invalidate. The
        // exporter works on its own copy; only an actual export failure starts
        // a guarded recovery session. Successful normal exports save directly.
        internal static ExportRecoverySession ExportAndSaveNormally(GameObject source, string destination,
            Func<ExportRecoveryOptions, ExportRecoveryReport, ICollection<string>, byte[]> create,
            Action<byte[], IEnumerable<string>> completed = null,
            IEnumerable<GameObject> previewExcludedObjects = null, ExportGimmickOptions previewGimmicks = null)
        {
            if (create == null) throw new ArgumentNullException(nameof(create));
            var warnings = new List<string>();
            var report = new ExportRecoveryReport();
            byte[] bytes;
            try { bytes = create(new ExportRecoveryOptions(), report, warnings); }
            catch (OperationCanceledException) { return null; }
            catch (Exception exception)
            {
                return ExportRecoverySession.FromFailedExport(source, destination, create, exception, report,
                    previewExcludedObjects, previewGimmicks);
            }
            ExportOutputWriter.Write(destination, bytes);
            completed?.Invoke(bytes, warnings);
            return null;
        }

        internal static bool ShouldShowFailureAfterFailedAttempt(ExportRecoverySession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            return session.IsInvalidated || !session.WasCanceled;
        }

        private string AvatarName()
        {
            return avatar != null && !string.IsNullOrWhiteSpace(avatar.name) ? avatar.name.Trim() : "avatar";
        }

        private string DefaultFileName()
        {
            var name = AvatarName();
            foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '-');
            return name + "-liltoon.vrm";
        }

        private void ShowExportCompletion(byte[] bytes, IEnumerable<string> warnings, string destination)
        {
            var details = (warnings ?? Array.Empty<string>()).ToArray();
            if (details.Length > 0) Debug.Log(ExporterLocalization.T("VR Vlog 書き出し詳細\n・") + string.Join("\n・", details));
            var resultWindow = this != null ? this : GetWindow<LilToonExporterWindow>(true, ExporterLocalization.T("VR Vlog VRM書き出し"));
            resultWindow.lastSavedPath = destination;
            resultWindow.lastSavedSummary = string.Format(ExporterLocalization.T("{0:N0}バイト・VRChat表情 {1}件"), bytes.Length, VrmMenuExpressions.CountRegistered(bytes));
            resultWindow.lastSavedWarnings = details;
            resultWindow.Repaint();
        }

        private static string PackageVersion()
        {
            var info = PackageManagerPackageInfo.FindForAssembly(typeof(LilToonExporterWindow).Assembly);
            return info != null && !string.IsNullOrWhiteSpace(info.version) ? info.version : "0.11.9";
        }

        private static string InstalledLilToonStatus()
        {
            var package = FindLilToonPackage();
            return package == null ? ExporterLocalization.T("未インストール（VCC／ALCOMで追加してください）") : package.version;
        }

        private static string RequireSupportedLilToon()
        {
            var package = FindLilToonPackage();
            if (package == null || !string.Equals(package.version, SupportedLilToonVersion, StringComparison.Ordinal))
                throw new InvalidOperationException(string.Format(
                    ExporterLocalization.T("lilToon {0} が必要です。現在のバージョン：{1}"),
                    SupportedLilToonVersion, package?.version ?? ExporterLocalization.T("不明")));
            return package.version;
        }

        private static PackageManagerPackageInfo FindLilToonPackage()
        {
            foreach (var package in PackageManagerPackageInfo.GetAllRegisteredPackages())
                if (string.Equals(package.name, "jp.lilxyzw.liltoon", StringComparison.Ordinal)) return package;
            return null;
        }
    }
}
