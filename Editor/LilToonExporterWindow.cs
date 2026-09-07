using System;
using System.IO;
using System.Collections.Generic;
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
        private string fallbackPath = "";
        private bool showAdvanced;
        private bool showAppearanceOptions;
        private bool suppressSharedTextureEmission = true;
        private bool suppressHdrTextureEmission = true;
        private readonly List<GameObject> excludedObjects = new List<GameObject>();
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
            if (selectedAvatar != avatar) excludedObjects.Clear();
            avatar = selectedAvatar;
            EditorGUILayout.HelpBox("Hierarchyから、書き出したいアバターの一番上のオブジェクトを指定してください。", MessageType.None);

            author = EditorGUILayout.TextField(
                new GUIContent("② 作者名（必須）", "VRMファイルに記録される作者名です。"),
                author);
            EditorGUILayout.HelpBox("VRMファイルに記録する作者名を入力してください。", MessageType.None);
            EditorGUILayout.HelpBox("現在有効な衣装・オブジェクトを書き出します。非表示のオブジェクトや無効なRendererは含まれません。", MessageType.None);

            showAppearanceOptions = EditorGUILayout.Foldout(showAppearanceOptions, "書き出し設定");
            if (showAppearanceOptions)
            {
                suppressSharedTextureEmission = EditorGUILayout.Toggle(
                    new GUIContent("目などの白飛びを抑える", "メイン画像と同じ画像を使う発光を省略します。意図的な発光も抑えられるため、必要に応じて解除してください。"),
                    suppressSharedTextureEmission);
                suppressHdrTextureEmission = EditorGUILayout.Toggle(
                    new GUIContent("別画像の強い発光も抑える", "焼き込み前の目画像など、別のテクスチャを使うHDR発光を省略します。意図的な発光を残す場合は解除してください。ワンクリック書き出しに適用されます。"),
                    suppressHdrTextureEmission);
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

            EditorGUILayout.Space(8f);
            showAdvanced = EditorGUILayout.Foldout(showAdvanced, "上級者向け：既存のVRM 1.0へlilToonデータを追加");
            if (!showAdvanced) return;
            EditorGUILayout.HelpBox("通常は使用しません。UniVRM互換のMToonデータを持つVRM 1.0が既にある場合だけ使用してください。元のVRMは変更されません。", MessageType.Warning);
            PathField("元にするVRM 1.0", ref fallbackPath, false);
            PathField("保存先", ref outputPath, true);
            using (new EditorGUI.DisabledScope(avatar == null || string.IsNullOrWhiteSpace(fallbackPath) || string.IsNullOrWhiteSpace(outputPath)))
                if (GUILayout.Button("lilToonデータを追加して別名保存")) ExportExistingFallback();
        }

        private void ExportOneClick()
        {
            outputPath = EditorUtility.SaveFilePanel("VRMの保存先", "", DefaultFileName(), "vrm");
            if (string.IsNullOrEmpty(outputPath)) return;
            var warnings = new List<string>();
            ExportAtomically(() =>
            {
                return UniVrmOneClickExporter.Export(avatar, AvatarName(), author, warnings, suppressSharedTextureEmission,
                    PackageVersion(), RequireSupportedLilToon(), suppressHdrTextureEmission, excludedObjects);
            }, warnings);
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

        private void ExportExistingFallback()
        {
            var warnings = new List<string>();
            ExportAtomically(() =>
            {
                if (!File.Exists(fallbackPath)) throw new FileNotFoundException("元にするVRMが見つかりません。", fallbackPath);
                if (string.Equals(Path.GetFullPath(fallbackPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("元のVRMを保護するため、別の保存先を指定してください。");
                foreach (var renderer in ExportRendererSelection.Enumerate(avatar))
                    foreach (var material in renderer.sharedMaterials)
                        if (LilToonMaterialReader.IsLilToon(material) && LilToonMainTextureBaker.NeedsBake(material))
                            throw new InvalidOperationException(material.name + ": 未ベイクのメインカラーがあります。画像とマテリアルを一緒に更新するため、ワンクリック書き出しを使用してください。");
                return LilToonGlbExtension.Inject(File.ReadAllBytes(fallbackPath), avatar, PackageVersion(), RequireSupportedLilToon(), warnings, suppressSharedTextureEmission);
            }, warnings);
        }

        private void ExportAtomically(Func<byte[]> create, ICollection<string> warnings)
        {
            try
            {
                if (File.Exists(outputPath) && !EditorUtility.DisplayDialog("ファイルを上書きしますか？", outputPath, "上書き", "キャンセル")) return;
                var bytes = create();
                var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                if (string.IsNullOrEmpty(directory)) throw new InvalidOperationException("保存先フォルダーが正しくありません。");
                Directory.CreateDirectory(directory);
                var temporary = Path.Combine(directory, "." + Path.GetFileName(outputPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    File.WriteAllBytes(temporary, bytes);
                    LilToonGlbExtension.Validate(File.ReadAllBytes(temporary));
                    if (File.Exists(outputPath)) File.Replace(temporary, outputPath, null); else File.Move(temporary, outputPath);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                EditorUtility.RevealInFinder(outputPath);
                // Keep the native modal short even for avatars with hundreds of
                // omitted items. Diagnostics remain in one expandable Console entry.
                if (warnings != null && warnings.Count > 0) Debug.Log("VR Vlog 書き出し詳細\n・" + string.Join("\n・", warnings));
                var expressionCount = VrmMenuExpressions.CountRegistered(bytes);
                EditorUtility.DisplayDialog("書き出し完了", $"VRMを書き出しました（{bytes.Length:N0}バイト）。\nVRChat表情: {expressionCount}件。", "閉じる");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("書き出しに失敗しました", exception.Message, "閉じる");
            }
        }

        private static void PathField(string label, ref string value, bool save)
        {
            EditorGUILayout.BeginHorizontal();
            value = EditorGUILayout.TextField(label, value);
            if (GUILayout.Button("選択…", GUILayout.Width(70)))
            {
                var chosen = save
                    ? EditorUtility.SaveFilePanel(label, "", "avatar-liltoon.vrm", "vrm")
                    : EditorUtility.OpenFilePanel(label, "", "vrm");
                if (!string.IsNullOrEmpty(chosen)) value = chosen;
            }
            EditorGUILayout.EndHorizontal();
        }

        private static string PackageVersion()
        {
            var info = PackageManagerPackageInfo.FindForAssembly(typeof(LilToonExporterWindow).Assembly);
            return info != null && !string.IsNullOrWhiteSpace(info.version) ? info.version : "0.7.1";
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
