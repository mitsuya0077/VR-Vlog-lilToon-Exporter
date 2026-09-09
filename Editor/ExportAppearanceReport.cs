using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    internal static class ExportAppearanceReport
    {
        internal static string[] Changes(IEnumerable<string> messages) => (messages ?? Array.Empty<string>())
            .Where(message => !string.IsNullOrEmpty(message) &&
                (message.Contains("省略") || message.Contains("近似") || message.Contains("縮小") || message.Contains("調整して") || message.Contains("自動除外せず保持")))
            .Distinct().ToArray();

        internal static string Summary(IEnumerable<string> changes)
        {
            var items = changes.ToArray();
            return string.Join("\n", items.Take(3).Select(item => "・" + (item.Length <= 110 ? item : item.Substring(0, 107) + "…"))) +
                (items.Length > 3 ? "\nほか " + (items.Length - 3) + " 件。詳細から確認できます。" : "");
        }
    }

    internal sealed class ExportAppearanceReportWindow : EditorWindow
    {
        private string details;
        private Vector2 scroll;
        internal static void Open(IEnumerable<string> messages)
        {
            var window = CreateInstance<ExportAppearanceReportWindow>();
            window.titleContent = new GUIContent("VR Vlog 書き出し詳細");
            window.minSize = new Vector2(460, 300);
            window.details = string.Join("\n\n", messages);
            window.ShowUtility();
        }
        private void OnGUI()
        {
            using (var view = new EditorGUILayout.ScrollViewScope(scroll))
            {
                scroll = view.scrollPosition;
                EditorGUILayout.LabelField(details ?? "", EditorStyles.wordWrappedLabel);
            }
            if (GUILayout.Button("詳細をコピー")) EditorGUIUtility.systemCopyBuffer = details;
        }
    }
}
