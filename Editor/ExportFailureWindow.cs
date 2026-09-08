using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    internal sealed class ExportFailureWindow : EditorWindow
    {
        [Serializable]
        private sealed class IssueView
        {
            public Material material;
            public string materialName, rendererPath, layer, setting, reason, nextStep;
        }

        [SerializeField] private string message;
        [SerializeField] private string technicalDetails;
        [SerializeField] private bool isBakeFailure;
        [SerializeField] private bool sourceUnchanged;
        [SerializeField] private List<IssueView> issues = new List<IssueView>();
        [SerializeField] private Vector2 scrollPosition;
        [SerializeField] private bool showTechnicalDetails;
        private MaterialBakeException bakeFailure;
        private Action<MaterialBakeException> omitAndRetry;
        private bool copied;

        internal static void Show(Exception exception, Action<MaterialBakeException> omitAndRetry = null)
        {
            if (exception == null) throw new ArgumentNullException(nameof(exception));
            var window = CreateInstance<ExportFailureWindow>();
            window.titleContent = new GUIContent("VR Vlog 書き出しの確認");
            window.minSize = new Vector2(480f, 380f);
            window.position = new Rect(120f, 120f, 580f, 640f);
            window.message = exception.Message;
            window.technicalDetails = exception.ToString();
            window.bakeFailure = exception as MaterialBakeException;
            window.omitAndRetry = omitAndRetry;
            if (window.bakeFailure != null)
            {
                window.isBakeFailure = true;
                window.sourceUnchanged = window.bakeFailure.SourceUnchanged;
                foreach (var issue in window.bakeFailure.Issues)
                {
                    if (issue == null) continue;
                    window.issues.Add(new IssueView
                    {
                        material = issue.Material,
                        materialName = issue.MaterialName,
                        rendererPath = issue.RendererPath,
                        layer = issue.Layer,
                        setting = issue.Setting,
                        reason = issue.Reason,
                        nextStep = issue.NextStep
                    });
                }
                var details = new StringBuilder(window.technicalDetails);
                foreach (var issue in window.issues)
                {
                    details.Append("\n\nマテリアル: ").Append(issue.materialName)
                        .Append("\n使用箇所: ").Append(issue.rendererPath)
                        .Append("\nレイヤー: メインカラー").Append(issue.layer)
                        .Append("\n設定: ").Append(issue.setting)
                        .Append("\n原因: ").Append(issue.reason)
                        .Append("\n次の操作: ").Append(issue.nextStep);
                }
                window.technicalDetails = details.ToString();
            }
            window.ShowUtility();
        }

        private bool CanOmitAndRetry()
        {
            if (omitAndRetry == null || bakeFailure == null || !bakeFailure.SourceUnchanged ||
                bakeFailure.Issues == null || bakeFailure.Issues.Count == 0) return false;
            foreach (var issue in bakeFailure.Issues)
                if (issue == null || issue.Material == null || (issue.Layer != "2nd" && issue.Layer != "3rd")) return false;
            return true;
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("書き出しを完了できませんでした", EditorStyles.boldLabel);
            using (var scroll = new EditorGUILayout.ScrollViewScope(scrollPosition))
            {
                scrollPosition = scroll.scrollPosition;
                if (isBakeFailure && issues.Count > 0)
                {
                    EditorGUILayout.HelpBox("マテリアルの設定に、自動で焼き込めないものがあります。対象と次の操作を確認してください。", MessageType.Error);
                    if (sourceUnchanged)
                        EditorGUILayout.HelpBox("このエラーの確認では、元のアバターとマテリアルは変更していません。", MessageType.Info);
                    foreach (var issue in issues) DrawIssue(issue);
                }
                else
                {
                    EditorGUILayout.HelpBox(message ?? "エラーの内容を取得できませんでした。", MessageType.Error);
                }
                EditorGUILayout.Space(6f);
                showTechnicalDetails = EditorGUILayout.Foldout(showTechnicalDetails, "技術的な詳細");
                if (showTechnicalDetails)
                    EditorGUILayout.LabelField(technicalDetails ?? "", EditorStyles.wordWrappedLabel);
            }

            if (CanOmitAndRetry())
            {
                EditorGUILayout.HelpBox(
                    "同じマテリアルを使うすべての箇所で、表示されたレイヤーの模様・文字・透明度の変更が出力から省かれます。元のアバターは変更しません。",
                    MessageType.Warning);
                if (GUILayout.Button("表示されたレイヤーを省略して書き出す", GUILayout.Height(32f)))
                {
                    // Continue outside the current IMGUI event. The callback
                    // belongs to this attempt's captured inputs, not live fields
                    // in the export window. It is deliberately not persisted.
                    var callback = omitAndRetry;
                    var failure = bakeFailure;
                    omitAndRetry = null;
                    Close();
                    EditorApplication.delayCall += () => callback(failure);
                    return;
                }
            }
            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(copied ? "詳細をコピーしました" : "詳細をコピー", GUILayout.Height(28f)))
                {
                    EditorGUIUtility.systemCopyBuffer = technicalDetails ?? message ?? "";
                    copied = true;
                }
                if (GUILayout.Button("閉じる", GUILayout.Height(28f))) Close();
            }
            EditorGUILayout.Space(4f);
        }

        private static void DrawIssue(IssueView issue)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("マテリアル: " + issue.materialName, EditorStyles.wordWrappedLabel);
                if (!string.IsNullOrEmpty(issue.rendererPath))
                    EditorGUILayout.LabelField("使用箇所: " + issue.rendererPath, EditorStyles.wordWrappedLabel);
                EditorGUILayout.LabelField("メインカラー" + issue.layer + " / " + issue.setting, EditorStyles.wordWrappedLabel);
                EditorGUILayout.LabelField("原因: " + issue.reason, EditorStyles.wordWrappedLabel);
                EditorGUILayout.Space(3f);
                EditorGUILayout.LabelField("次の操作: " + issue.nextStep, EditorStyles.wordWrappedLabel);
                using (new EditorGUI.DisabledScope(issue.material == null))
                    if (GUILayout.Button("元のマテリアルを選択"))
                    {
                        Selection.activeObject = issue.material;
                        EditorGUIUtility.PingObject(issue.material);
                    }
            }
        }
    }
}
