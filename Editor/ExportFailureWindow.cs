using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    internal sealed class ExportFailureWindow : EditorWindow
    {
        [Serializable]
        private sealed class IssueView
        {
            public string id, stage, target, reason, remedy, lostEffect;
            public UnityEngine.Object source;
            public bool hasAction;
        }

        [SerializeField] private string message, technicalDetails, supportText;
        [SerializeField] private bool hadSession;
        [SerializeField] private List<IssueView> issues = new List<IssueView>();
        [SerializeField] private Vector2 scrollPosition;
        [SerializeField] private bool showTechnicalDetails;
        private ExportRecoverySession session;
        private Action saved;
        private readonly HashSet<string> selected = new HashSet<string>();
        private bool copied, busy;
        private double nextSourceCheck;

        internal static void Show(Exception exception)
        {
            if (exception == null) throw new ArgumentNullException(nameof(exception));
            var window = NewWindow();
            window.message = exception.Message;
            window.technicalDetails = exception.ToString();
            var report = ExportRecoveryReport.FromException(null, exception);
            window.supportText = report.BuildSupportText();
            window.SetIssues(report.Diagnostics);
            window.ShowUtility();
        }

        internal static void Show(ExportRecoverySession session, Action saved = null)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            var window = Resources.FindObjectsOfTypeAll<ExportFailureWindow>()
                .FirstOrDefault(item => item.session == session) ?? NewWindow();
            window.session = session;
            window.saved = saved;
            window.hadSession = true;
            window.RefreshSession();
            window.ShowUtility();
        }

        private static ExportFailureWindow NewWindow()
        {
            var window = CreateInstance<ExportFailureWindow>();
            window.titleContent = new GUIContent(ExporterLocalization.T("VR Vlog 書き出しの確認"));
            window.minSize = new Vector2(520, 420);
            window.position = new Rect(120, 120, 620, 680);
            return window;
        }

        private void OnEnable() => EditorApplication.update += CheckSource;
        private void OnDisable() => EditorApplication.update -= CheckSource;

        private void CheckSource()
        {
            if (session == null || EditorApplication.timeSinceStartup < nextSourceCheck) return;
            nextSourceCheck = EditorApplication.timeSinceStartup + 1;
            if (session.CheckForChanges()) { selected.Clear(); Repaint(); }
        }

        private void RefreshSession()
        {
            message = session.WasCanceled ? ExporterLocalization.T("キャンセル") : session.Failure?.Message;
            technicalDetails = session.Failure?.ToString();
            supportText = session.Report?.BuildSupportText() ?? "";
            selected.Clear();
            foreach (var action in session.SelectedOptions.Actions) selected.Add(action.Id);
            var diagnostics = (session.Report?.Diagnostics ?? new List<ExportRecoveryDiagnostic>())
                .Concat(session.AvailableDiagnostics).GroupBy(item => item.Action?.Id ?? item.Id).Select(group => group.First());
            SetIssues(diagnostics);
            copied = false;
            Repaint();
        }

        private void SetIssues(IEnumerable<ExportRecoveryDiagnostic> diagnostics)
        {
            issues.Clear();
            foreach (var issue in diagnostics)
            {
                if (issue == null) continue;
                issues.Add(new IssueView
                {
                    id = issue.Action?.Id ?? issue.Id, stage = issue.Stage, target = issue.Target,
                    reason = issue.Reason, remedy = issue.Remedy, lostEffect = issue.LostEffect,
                    source = issue.Source, hasAction = issue.Action != null
                });
            }
        }

        internal ExportRecoveryOptions BuildSelectedOptions()
        {
            var options = new ExportRecoveryOptions();
            if (session == null || session.CheckForChanges()) return options;
            options.Actions.AddRange(session.AvailableDiagnostics.Where(issue => issue.Action != null && selected.Contains(issue.Action.Id))
                .Select(issue => issue.Action).GroupBy(action => action.Id).Select(group => group.First()));
            return options;
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(ExporterLocalization.T(session?.Failure != null || !hadSession
                ? "書き出しを完了できませんでした" : "コピーに適用する対策を選んでください"), EditorStyles.boldLabel);
            if (hadSession && session == null)
            {
                EditorGUILayout.HelpBox(ExporterLocalization.T("Unityの再読み込みで確認内容が無効になりました。書き出し画面から再度書き出してください。"), MessageType.Warning);
                if (GUILayout.Button(ExporterLocalization.T("閉じる"))) Close();
                return;
            }
            var stale = session?.IsInvalidated == true;
            using (var scroll = new EditorGUILayout.ScrollViewScope(scrollPosition))
            {
                scrollPosition = scroll.scrollPosition;
                if (stale)
                {
                    EditorGUILayout.HelpBox(ExporterLocalization.T("アバターまたは関連アセットが変わりました。古い診断とプレビューは無効です。再検査してください。"), MessageType.Warning);
                }
                else
                {
                    if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(ExporterLocalization.T(message), session?.WasCanceled == true ? MessageType.Info : MessageType.Error);
                    if (session != null)
                        EditorGUILayout.HelpBox(ExporterLocalization.T("対策は新しい変換用コピーに適用します。元のアバターと共有マテリアルは変更しません。未知の仕組みという理由だけでは除外しません。"), MessageType.Info);
                    using (new EditorGUI.DisabledScope(busy))
                        foreach (var issue in issues) DrawIssue(issue);
                    showTechnicalDetails = EditorGUILayout.Foldout(showTechnicalDetails, ExporterLocalization.T("技術的な詳細"));
                    if (showTechnicalDetails) EditorGUILayout.LabelField(technicalDetails ?? "", EditorStyles.wordWrappedLabel);
                }
            }
            if (session != null)
            {
                using (new EditorGUI.DisabledScope(busy))
                {
                    if (stale)
                    {
                        if (GUILayout.Button(ExporterLocalization.T("再検査する"), GUILayout.Height(32))) Schedule(true);
                    }
                    else
                    {
                        var count = session.AvailableDiagnostics.Count(issue => issue.Action != null && selected.Contains(issue.Action.Id));
                        using (new EditorGUI.DisabledScope(count == 0 && session.SelectedOptions.Actions.Count == 0 && !session.WasCanceled))
                            if (GUILayout.Button(ExporterLocalization.T("コピーで対策を適用して試す"), GUILayout.Height(32))) Schedule(false);
                        EditorGUILayout.LabelField(ExporterLocalization.T("チェックを外して再試行すると、その対策を解除した新しいコピーで確認します。"), EditorStyles.wordWrappedMiniLabel);
                    }
                    using (new EditorGUI.DisabledScope(stale || !session.HasCurrentSuccess))
                        if (GUILayout.Button(ExporterLocalization.T("最後に成功した結果を確認")))
                            ExportRecoveryComparisonWindow.Show(session, saved);
                }
            }
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(stale))
                    if (GUILayout.Button(copied ? ExporterLocalization.T("共有用の診断をコピーしました") : ExporterLocalization.T("共有用の診断をコピー"), GUILayout.Height(28)))
                    {
                        // Never put raw exception stacks or private absolute paths
                        // in the text intended for a support report.
                        EditorGUIUtility.systemCopyBuffer = supportText ?? "";
                        copied = true;
                    }
                if (GUILayout.Button(ExporterLocalization.T("閉じる"), GUILayout.Height(28))) Close();
            }
            EditorGUILayout.Space(4);
        }

        private void DrawIssue(IssueView issue)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (issue.hasAction && session != null)
                {
                    var enabled = selected.Contains(issue.id);
                    var next = EditorGUILayout.ToggleLeft(ExporterLocalization.T("この対策をコピーに適用する"), enabled, EditorStyles.boldLabel);
                    if (next != enabled) { if (next) selected.Add(issue.id); else selected.Remove(issue.id); }
                }
                EditorGUILayout.LabelField(ExporterLocalization.T("工程: ") + ExporterLocalization.T(issue.stage), EditorStyles.wordWrappedLabel);
                EditorGUILayout.LabelField(ExporterLocalization.T("対象: ") + issue.target, EditorStyles.wordWrappedLabel);
                EditorGUILayout.LabelField(ExporterLocalization.T("理由: ") + ExporterLocalization.T(issue.reason), EditorStyles.wordWrappedLabel);
                EditorGUILayout.LabelField(ExporterLocalization.T("対策: ") + ExporterLocalization.T(issue.remedy), EditorStyles.wordWrappedLabel);
                if (!string.IsNullOrEmpty(issue.lostEffect))
                    EditorGUILayout.LabelField(ExporterLocalization.T("失われる効果: ") + ExporterLocalization.T(issue.lostEffect), EditorStyles.wordWrappedLabel);
                using (new EditorGUI.DisabledScope(issue.source == null))
                    if (GUILayout.Button(ExporterLocalization.T("元の対象を選択")))
                    {
                        Selection.activeObject = issue.source;
                        EditorGUIUtility.PingObject(issue.source);
                    }
            }
        }

        private void Schedule(bool reinspect)
        {
            var options = BuildSelectedOptions();
            busy = true;
            EditorApplication.delayCall += () =>
            {
                if (this == null) return;
                try
                {
                    var success = reinspect ? session.Reinspect() : session.Attempt(options);
                    RefreshSession();
                    if (success)
                    {
                        ExportRecoveryComparisonWindow.Show(session, saved);
                        Close();
                    }
                }
                catch (Exception exception) { Show(exception); }
                finally { busy = false; if (this != null) Repaint(); }
            };
        }
    }
}
