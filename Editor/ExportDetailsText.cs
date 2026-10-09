using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    internal static class ExportDetailsText
    {
        internal static void Copy(string details) => EditorGUIUtility.systemCopyBuffer = details ?? "";

        internal static string Failure(string message, string technicalDetails,
            IEnumerable<ExportRecoveryDiagnostic> diagnostics)
        {
            var text = new StringBuilder();
            if (!string.IsNullOrEmpty(message)) text.AppendLine(message).AppendLine();
            foreach (var issue in diagnostics ?? Array.Empty<ExportRecoveryDiagnostic>())
            {
                if (issue == null) continue;
                text.AppendLine(ExporterLocalization.T("工程: ") + ExporterLocalization.T(issue.Stage));
                text.AppendLine(ExporterLocalization.T("対象: ") + issue.Target);
                text.AppendLine(ExporterLocalization.T("理由: ") + ExporterLocalization.T(issue.Reason));
                text.AppendLine(ExporterLocalization.T("次にすること: ") + ExporterLocalization.T(issue.Remedy));
                text.AppendLine(ExporterLocalization.T("変わる点: ") + ExporterLocalization.T(issue.LostEffect));
                text.AppendLine();
            }
            // Keep Exception.ToString() verbatim, including inner exceptions and
            // stack traces. The separate support report is deliberately redacted.
            if (!string.IsNullOrEmpty(technicalDetails)) text.Append(technicalDetails);
            return text.ToString();
        }

        internal static bool Save(string details, Func<string> selectPath, out string error)
        {
            error = null;
            // Unity can exit an IMGUI event while showing the native dialog.
            // Do not treat that control flow as an I/O failure.
            var path = selectPath();
            if (string.IsNullOrEmpty(path)) return false;
            string temporary = null;
            try
            {
                var destination = Path.GetFullPath(path);
                temporary = Path.Combine(Path.GetDirectoryName(destination),
                    "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(temporary, details ?? "", new UTF8Encoding(false));
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
                return true;
            }
            catch (Exception failure) when (failure is IOException || failure is UnauthorizedAccessException ||
                failure is ArgumentException || failure is NotSupportedException || failure is SecurityException)
            {
                error = failure.Message;
                return false;
            }
            finally
            {
                if (temporary != null && File.Exists(temporary))
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        internal static void DrawActions(string details, ref string feedback, ref bool failed, ref Vector2 feedbackScroll)
        {
            EditorGUILayout.LabelField(ExporterLocalization.T("全文にはアバター名やパスを含みます"), EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(details)))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(ExporterLocalization.T("全文をコピー"), GUILayout.Height(28)))
                {
                    Copy(details);
                    feedback = ExporterLocalization.T("全文をコピーしました");
                    failed = false;
                }
                if (GUILayout.Button(ExporterLocalization.T("テキスト保存"), GUILayout.Height(28)))
                {
                    var saved = Save(details, () => EditorUtility.SaveFilePanel(
                        ExporterLocalization.T("書き出し詳細を保存"), "", "VRVlog-export-details", "txt"), out var error);
                    if (saved || error != null)
                    {
                        failed = !saved;
                        feedback = saved ? ExporterLocalization.T("テキストを保存しました")
                            : ExporterLocalization.T("テキストを保存できませんでした") + "\n" + error;
                    }
                    else { feedback = null; failed = false; }
                }
            }
            // Reserve the feedback area so the buttons stay in place after a
            // copy, cancel or save. Long filesystem errors remain scrollable.
            using (var view = new EditorGUILayout.ScrollViewScope(feedbackScroll, GUILayout.Height(44)))
            {
                feedbackScroll = view.scrollPosition;
                if (!string.IsNullOrEmpty(feedback))
                    EditorGUILayout.HelpBox(feedback, failed ? MessageType.Error : MessageType.Info);
            }
        }
    }
}
