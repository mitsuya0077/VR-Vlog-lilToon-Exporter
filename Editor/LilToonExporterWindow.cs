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
        private const string SupportedLilToonVersion = "2.3.4";
        private GameObject avatar;
        private string author = "";
        private string outputPath = "";
        private bool showAppearanceOptions;
        private bool showBlink;
        private BlinkExportOptions blinkOptions = new BlinkExportOptions();
        private readonly List<GameObject> excludedObjects = new List<GameObject>();
        private bool autoExcludeGimmicks = true;
        private readonly List<GameObject> includedGimmicks = new List<GameObject>();
        private List<ExportGimmickFinding> gimmickFindings = new List<ExportGimmickFinding>();
        private double nextGimmickScan;
        private Vector2 scrollPosition;

        [MenuItem("VR Vlog/lilToon VRM 1.0を書き出す")]
        public static void Open()
        {
            var window = GetWindow<LilToonExporterWindow>(true, "VR Vlog VRM書き出し");
            window.minSize = new Vector2(430f, 300f);
        }

        private void OnGUI()
        {
            using (var scroll = new EditorGUILayout.ScrollViewScope(scrollPosition))
            {
                scrollPosition = scroll.scrollPosition;
                DrawWindow();
            }
        }

        private void DrawWindow()
        {
            EditorGUILayout.HelpBox(
                "アバターを選び、作者名を入力するだけでVRMを書き出せます。\nMToon互換データとlilToonデータは自動で追加されます。",
                MessageType.Info);
            EditorGUILayout.Space(4f);
            var selectedAvatar = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("① アバター（必須）", "Hierarchyにあるアバターの一番上のオブジェクトを指定します。"),
                avatar,
                typeof(GameObject),
                true);
            if (selectedAvatar != avatar)
            {
                blinkOptions = new BlinkExportOptions();
                showBlink = false;
                excludedObjects.Clear();
                includedGimmicks.Clear();
                gimmickFindings.Clear();
                nextGimmickScan = 0;
            }
            avatar = selectedAvatar;
            EditorGUILayout.HelpBox("Hierarchyから、書き出したいアバターの一番上のオブジェクトを指定してください。", MessageType.None);

            author = EditorGUILayout.TextField(
                new GUIContent("② 作者名（必須）", "VRMファイルに記録される作者名です。"),
                author);
            EditorGUILayout.HelpBox("VRMファイルに記録する作者名を入力してください。", MessageType.None);
            EditorGUILayout.HelpBox("現在有効な衣装・オブジェクトを書き出します。非表示のオブジェクトや無効なRendererは含まれません。", MessageType.None);
            EditorGUILayout.HelpBox("Modular Avatar の髪・衣装は、設定済みの接続先を自動で反映します。ここでボーンを指定する必要はありません。", MessageType.None);

            DrawBlink();
            showAppearanceOptions = EditorGUILayout.Foldout(showAppearanceOptions, "書き出し設定");
            if (showAppearanceOptions)
            {
                DrawGimmicks();
                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("書き出さないオブジェクト（ペット・ギミックなど）");
                EditorGUILayout.HelpBox("除外したい子オブジェクトを指定します。ワンクリック書き出しに適用され、Unityの元アバターは変更しません。", MessageType.None);
                for (var index = 0; index < excludedObjects.Count; index++)
                {
                    EditorGUILayout.BeginHorizontal();
                    excludedObjects[index] = (GameObject)EditorGUILayout.ObjectField(excludedObjects[index], typeof(GameObject), true);
                    if (GUILayout.Button("削除", GUILayout.Width(48))) { excludedObjects.RemoveAt(index); index--; }
                    EditorGUILayout.EndHorizontal();
                }
                if (GUILayout.Button("除外するオブジェクトを追加")) excludedObjects.Add(null);
            }

            EditorGUILayout.Space(4f);
            using (new EditorGUI.DisabledScope(avatar == null || string.IsNullOrWhiteSpace(author)))
                if (GUILayout.Button("③ 保存先を選んでVRMを書き出す", GUILayout.Height(32f))) ExportOneClick();

            if (avatar == null || string.IsNullOrWhiteSpace(author))
                EditorGUILayout.HelpBox("上の2項目を入力すると書き出せます。", MessageType.Warning);

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("動作環境", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("lilToon", InstalledLilToonStatus());
            EditorGUILayout.LabelField("UniVRM", UniVrmOneClickExporter.SupportedUniVrmSeries + ".x（VCC／ALCOMが自動インストール）");

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
            BlinkExportSession resolved = null;
            string error = null;
            try { resolved = ResolveBlink(); }
            catch (Exception exception) { error = exception.Message; }
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("瞬き", resolved?.Description ?? "設定が必要です");
            if (GUILayout.Button(showBlink ? "調整を閉じる" : "確認・調整", GUILayout.Width(100))) showBlink = !showBlink;
            EditorGUILayout.EndHorizontal();
            if (!showBlink && error == null) return;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var mode = (BlinkExportMode)EditorGUILayout.Popup("設定", (int)blinkOptions.Mode,
                    new[] { "自動", "手動", "瞬きなし" });
                if (mode != blinkOptions.Mode)
                {
                    if (mode == BlinkExportMode.Manual && blinkOptions.Both.Count == 0 && blinkOptions.Left.Count == 0 && blinkOptions.Right.Count == 0)
                    {
                        if (resolved != null)
                        {
                            blinkOptions.Both.AddRange(resolved.Slots[0].Select(b => b.Copy()));
                            blinkOptions.Left.AddRange(resolved.Slots[1].Select(b => b.Copy()));
                            blinkOptions.Right.AddRange(resolved.Slots[2].Select(b => b.Copy()));
                        }
                        if (blinkOptions.Both.Count == 0 && blinkOptions.Left.Count == 0)
                            blinkOptions.Both.Add(new BlinkShapeBinding());
                    }
                    blinkOptions.Mode = mode;
                    Repaint();
                }
                if (blinkOptions.Mode == BlinkExportMode.Manual)
                {
                    DrawBlinkBindings("両目", blinkOptions.Both);
                    var individual = blinkOptions.Left.Count > 0 || blinkOptions.Right.Count > 0;
                    var next = EditorGUILayout.ToggleLeft("左右を個別に設定", individual);
                    if (next && !individual) { blinkOptions.Left.Add(new BlinkShapeBinding()); blinkOptions.Right.Add(new BlinkShapeBinding()); }
                    if (!next && individual) { blinkOptions.Left.Clear(); blinkOptions.Right.Clear(); }
                    if (next) { DrawBlinkBindings("左目", blinkOptions.Left); DrawBlinkBindings("右目", blinkOptions.Right); }
                }
                if (error != null) EditorGUILayout.HelpBox(error, MessageType.Warning);
                using (new EditorGUI.DisabledScope(error != null))
                    if (GUILayout.Button("開閉をプレビュー"))
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
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            for (var i = 0; i < bindings.Count; i++)
            {
                var binding = bindings[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    var renderer = (SkinnedMeshRenderer)EditorGUILayout.ObjectField("メッシュ", binding.Renderer, typeof(SkinnedMeshRenderer), true);
                    if (renderer != binding.Renderer) { binding.Renderer = renderer; binding.Shape = ""; }
                    if (GUILayout.Button("削除", GUILayout.Width(48))) { bindings.RemoveAt(i--); continue; }
                }
                var mesh = binding.Renderer != null ? binding.Renderer.sharedMesh : null;
                if (mesh == null) continue;
                var names = Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToArray();
                var selected = Array.IndexOf(names, binding.Shape);
                var choices = new[] { string.IsNullOrEmpty(binding.Shape) ? "変形を選択" : "見つかりません: " + binding.Shape }.Concat(names).ToArray();
                var next = EditorGUILayout.Popup("閉眼用の変形", selected + 1, choices);
                if (next > 0) binding.Shape = names[next - 1];
                binding.Weight = EditorGUILayout.Slider("適用量 (%)", binding.Weight, 0, 100);
            }
            if (GUILayout.Button(label + "の変形を追加")) bindings.Add(new BlinkShapeBinding());
        }

        private void DrawGimmicks()
        {
            autoExcludeGimmicks = EditorGUILayout.Toggle("補助ギミックを自動除外", autoExcludeGimmicks);
            EditorGUILayout.HelpBox("ワンクリック書き出しで、確認できた補助ギミックを省略します。衣装や表情は保持し、Unityの元アバターは変更しません。判定は書き出すたびに更新します。", MessageType.None);
            if (avatar == null) return;
            if (GUILayout.Button("検出一覧を更新")) nextGimmickScan = 0;
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
                EditorGUILayout.LabelField(finding.Reason, EditorStyles.wordWrappedLabel);
                if (finding.Unit == GimmickExclusionUnit.Review)
                {
                    if (GUILayout.Button("手動の除外一覧に追加") && !excludedObjects.Contains(finding.Target))
                    {
                        excludedObjects.Add(finding.Target);
                        nextGimmickScan = 0;
                    }
                }
                else
                {
                    var inherited = includedGimmicks.Any(go => go != null && go != finding.Target && finding.Target.transform.IsChildOf(go.transform));
                    using (new EditorGUI.DisabledScope(!autoExcludeGimmicks || inherited))
                    {
                        var keep = includedGimmicks.Contains(finding.Target);
                        var selected = EditorGUILayout.ToggleLeft(inherited ? "親の指定により含める" : "この対象は含める", keep || inherited);
                        if (!inherited && selected != keep)
                        {
                            if (selected) includedGimmicks.Add(finding.Target);
                            else includedGimmicks.Remove(finding.Target);
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
                if (go == null) { includedGimmicks.RemoveAt(i); continue; }
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("含める指定: " + go.name);
                if (GUILayout.Button("解除", GUILayout.Width(48))) includedGimmicks.RemoveAt(i);
                EditorGUILayout.EndHorizontal();
            }
        }

        private void ExportOneClick()
        {
            try { ResolveBlink(); }
            catch (Exception) { showBlink = true; Repaint(); return; }
            outputPath = EditorUtility.SaveFilePanel("VRMの保存先", "", DefaultFileName(), "vrm");
            if (string.IsNullOrEmpty(outputPath)) return;
            // A nonmodal failure window may remain open while the user changes
            // this window. Retry exactly the avatar, destination and options
            // they chose for this attempt; revalidate the live avatar each time.
            var targetAvatar = avatar;
            var targetName = AvatarName();
            var targetAuthor = author;
            var targetOutput = outputPath;
            var targetBlink = blinkOptions.Copy();
            var targetExclusions = excludedObjects.ToArray();
            var targetGimmicks = new ExportGimmickOptions { AutoExclude = autoExcludeGimmicks, IncludedObjects = includedGimmicks.ToArray() };
            MaterialBakeOptions bakeOptions = null;
            void Attempt()
            {
                var warnings = new List<string>();
                ExportAtomically(() =>
                {
                    if (targetAvatar == null) throw new InvalidOperationException("この書き出しで選んだアバターが見つかりません。アバターを指定し直してください。");
                    return UniVrmOneClickExporter.Export(targetAvatar, targetName, targetAuthor, warnings, false,
                        PackageVersion(), RequireSupportedLilToon(), false, targetExclusions, bakeOptions, targetGimmicks, targetBlink);
                }, warnings, targetOutput, failure =>
                {
                    try
                    {
                        bakeOptions = (bakeOptions ?? new MaterialBakeOptions()).WithOmissions(failure.Issues);
                        Attempt();
                    }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception);
                        ExportFailureWindow.Show(exception);
                    }
                });
            }
            Attempt();
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

        private void ExportAtomically(Func<byte[]> create, ICollection<string> warnings, string destination,
            Action<MaterialBakeException> omitAndRetry = null)
        {
            try
            {
                if (!string.Equals(Path.GetExtension(destination), ".vrm", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("保存先の拡張子は .vrm にしてください。保存先を選び直して書き出してください。");
                if (File.Exists(destination) && !EditorUtility.DisplayDialog("ファイルを上書きしますか？", destination, "上書き", "キャンセル")) return;
                var bytes = create();
                var directory = Path.GetDirectoryName(Path.GetFullPath(destination));
                if (string.IsNullOrEmpty(directory)) throw new InvalidOperationException("保存先フォルダーが正しくありません。");
                Directory.CreateDirectory(directory);
                var temporary = Path.Combine(directory, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    File.WriteAllBytes(temporary, bytes);
                    LilToonGlbExtension.Validate(File.ReadAllBytes(temporary));
                    if (File.Exists(destination)) File.Replace(temporary, destination, null); else File.Move(temporary, destination);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                EditorUtility.RevealInFinder(destination);
                // Keep the native modal short even for avatars with hundreds of
                // omitted items. Diagnostics remain in one expandable Console entry.
                if (warnings != null && warnings.Count > 0) Debug.Log("VR Vlog 書き出し詳細\n・" + string.Join("\n・", warnings));
                var expressionCount = VrmMenuExpressions.CountRegistered(bytes);
                var completion = $"VRMを書き出しました（{bytes.Length:N0}バイト）。\nVRChat表情: {expressionCount}件。";
                var changes = ExportAppearanceReport.Changes(warnings);
                if (changes.Length == 0) EditorUtility.DisplayDialog("書き出し完了", completion, "閉じる");
                else if (!EditorUtility.DisplayDialog("書き出し完了", completion + "\n\n見た目の変更: " + changes.Length + "件\n" + ExportAppearanceReport.Summary(changes), "閉じる", "詳細を見る"))
                    ExportAppearanceReportWindow.Open(warnings);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                ExportFailureWindow.Show(exception, omitAndRetry);
            }
        }

        private static string PackageVersion()
        {
            var info = PackageManagerPackageInfo.FindForAssembly(typeof(LilToonExporterWindow).Assembly);
            return info != null && !string.IsNullOrWhiteSpace(info.version) ? info.version : "0.10.2";
        }

        private static string InstalledLilToonStatus()
        {
            var package = FindLilToonPackage();
            return package == null ? "未インストール（VCC／ALCOMで追加してください）" : package.version;
        }

        private static string RequireSupportedLilToon()
        {
            var package = FindLilToonPackage();
            if (package == null || !string.Equals(package.version, SupportedLilToonVersion, StringComparison.Ordinal))
                throw new InvalidOperationException($"lilToon {SupportedLilToonVersion} が必要です。現在のバージョン：{package?.version ?? "不明"}");
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
