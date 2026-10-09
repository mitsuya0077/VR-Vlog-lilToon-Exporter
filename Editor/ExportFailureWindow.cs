using System;
using System.Collections.Generic;
using System.IO;
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
            public ExportRecoveryActionKind actionKind;
        }

        [SerializeField] private string message, technicalDetails, supportText;
        [SerializeField] private string fullDetails, detailsFeedback;
        [SerializeField] private bool detailsSaveFailed;
        [SerializeField] private Vector2 detailsFeedbackScroll;
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
            window.titleContent = new GUIContent(ExporterLocalization.T("VRMの書き出しを続ける"));
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
                .Concat(session.AvailableDiagnostics).Distinct();
            SetIssues(diagnostics);
            // Only a single directly diagnosed material fix can be suggested.
            // Broad menu/renderer omission always requires an explicit choice.
            var current = (session.Report?.Diagnostics ?? new List<ExportRecoveryDiagnostic>())
                .Where(issue => issue.Action != null).Select(issue => issue.Action).GroupBy(action => action.Id).Select(group => group.First()).ToArray();
            if (selected.Count == 0 && session.LastSuccess == null && current.Length == 1 &&
                (current[0].Kind == ExportRecoveryActionKind.DisableAudioLink || current[0].Kind == ExportRecoveryActionKind.OmitSecondLayer || current[0].Kind == ExportRecoveryActionKind.OmitThirdLayer) &&
                session.AvailableDiagnostics.Any(issue => issue.Action?.Id == current[0].Id)) selected.Add(current[0].Id);
            copied = false;
            Repaint();
        }

        private void SetIssues(IEnumerable<ExportRecoveryDiagnostic> diagnostics)
        {
            var all = diagnostics.Where(issue => issue != null).ToArray();
            fullDetails = ExportDetailsText.Failure(message, technicalDetails, all);
            detailsFeedback = null;
            detailsSaveFailed = false;
            issues.Clear();
            foreach (var issue in all.GroupBy(item => item.Action?.Id ?? item.Id).Select(group => group.First()))
            {
                if (issue == null) continue;
                issues.Add(new IssueView
                {
                    id = issue.Action?.Id ?? issue.Id, stage = issue.Stage, target = issue.Target,
                    reason = issue.Reason, remedy = issue.Remedy, lostEffect = issue.LostEffect,
                    source = issue.Source, hasAction = issue.Action != null,
                    actionKind = issue.Action != null ? issue.Action.Kind : default
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
            EditorGUILayout.Space(8);
            var stale = session?.IsInvalidated == true;
            EditorGUILayout.LabelField(ExporterLocalization.T(stale ? "アバターの変更を反映して書き出す" : session?.WasCanceled == true ? "キャンセル" : session != null && session.Failure == null ? "対処を変更する" : "この設定では書き出せませんでした"), EditorStyles.boldLabel);
            if (hadSession && session == null)
            {
                EditorGUILayout.HelpBox(ExporterLocalization.T("Unityの再読み込みで確認内容が無効になりました。書き出し画面から再度書き出してください。"), MessageType.Warning);
                if (GUILayout.Button(ExporterLocalization.T("書き出し画面を開く"))) { LilToonExporterWindow.Open(); Close(); }
                ExportDetailsText.DrawActions(fullDetails, ref detailsFeedback, ref detailsSaveFailed, ref detailsFeedbackScroll);
                return;
            }
            using (var scroll = new EditorGUILayout.ScrollViewScope(scrollPosition))
            {
                scrollPosition = scroll.scrollPosition;
                if (stale)
                    EditorGUILayout.HelpBox(ExporterLocalization.T("アバターの設定が変わったため、前の結果は保存できません。下のボタンで今のアバターから書き出し直します。保存先と書き出し設定は前回の指定を使います。"), MessageType.Info);
                else
                {
                    if (session?.WasCanceled == true) EditorGUILayout.HelpBox(ExporterLocalization.T("書き出しをキャンセルしました。前に成功した結果があれば確認できます。"), MessageType.Info);
                    if (issues.Count == 0 && !string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(ExporterLocalization.T(message), MessageType.Error);
                    if (session != null) EditorGUILayout.LabelField(ExporterLocalization.T(issues.Any(issue => issue.hasAction)
                        ? "次に試すことを選んで、もう一度書き出してください。元のアバターは変更しません。"
                        : "表示された設定を確認してください。元のアバターは変更しません。"), EditorStyles.wordWrappedLabel);
                    if (issues.Any(IsMenuIssue))
                    {
                        EditorGUILayout.HelpBox(ExporterLocalization.T("必要な表情を残し、取り込まないメニューを選んでください。メニューを分割するだけでは合計項目数は減りません。"), MessageType.Info);
                        if (issues.Any(issue => issue.hasAction && issue.actionKind == ExportRecoveryActionKind.SkipVrChatMenus && selected.Contains(issue.id)))
                            EditorGUILayout.HelpBox(ExporterLocalization.T("全省略を選ぶと、枝の選択にかかわらずVRChatメニュー由来の表情・ポーズをすべて省略します。"), MessageType.Warning);
                    }
                    using (new EditorGUI.DisabledScope(busy)) foreach (var issue in issues) DrawIssue(issue);
                    showTechnicalDetails = EditorGUILayout.Foldout(showTechnicalDetails, ExporterLocalization.T("エラーの詳細・問い合わせ"));
                    if (showTechnicalDetails)
                    {
                        foreach (var issue in issues) EditorGUILayout.LabelField(ExporterLocalization.T("工程: ") + ExporterLocalization.T(issue.stage), EditorStyles.wordWrappedLabel);
                        EditorGUILayout.LabelField(technicalDetails ?? "", EditorStyles.wordWrappedLabel);
                        if (GUILayout.Button(copied ? ExporterLocalization.T("共有用の診断をコピーしました") : ExporterLocalization.T("共有用の診断をコピー")))
                        {
                            EditorGUIUtility.systemCopyBuffer = supportText ?? "";
                            copied = true;
                        }
                    }
                }
            }
            using (new EditorGUI.DisabledScope(busy))
                ExportDetailsText.DrawActions(fullDetails, ref detailsFeedback, ref detailsSaveFailed, ref detailsFeedbackScroll);
            if (session != null)
            {
                using (new EditorGUI.DisabledScope(busy))
                {
                    if (stale)
                    {
                        if (GUILayout.Button(ExporterLocalization.T("今のアバターで書き出し直す"), GUILayout.Height(36))) Schedule(true);
                    }
                    else
                    {
                        var count = session.AvailableDiagnostics.Count(issue => issue.Action != null && selected.Contains(issue.Action.Id));
                        if (count > 0 || session.SelectedOptions.Actions.Count > 0 || session.WasCanceled || !issues.Any(issue => issue.hasAction))
                            if (GUILayout.Button(ExporterLocalization.T(count > 0 ? "選んだ対処で書き出す" : "設定を変えずにもう一度試す"), GUILayout.Height(36))) Schedule(false);
                        if (count == 0 && issues.Any(issue => issue.hasAction))
                            EditorGUILayout.LabelField(ExporterLocalization.T("試す対処にチェックを入れてください。変わる点を確認してから進めます。"), EditorStyles.wordWrappedMiniLabel);
                    }
                    using (new EditorGUI.DisabledScope(stale || !session.HasCurrentSuccess || !session.HasPendingSave))
                        if (session.HasCurrentSuccess && session.HasPendingSave && GUILayout.Button(ExporterLocalization.T("前に成功したVRMを確認する"), GUILayout.Height(28)))
                        {
                            ExportRecoveryComparisonWindow.Show(session, saved);
                            Close();
                        }
                }
            }
            using (new EditorGUI.DisabledScope(busy))
                if (GUILayout.Button(ExporterLocalization.T("書き出しをやめる"), GUILayout.Height(28))) Close();
            EditorGUILayout.Space(4);
        }

        internal static string ActionLabel(ExportRecoveryActionKind kind)
        {
            switch (kind)
            {
                case ExportRecoveryActionKind.DisableAudioLink: return "AudioLinkをOFFにする";
                case ExportRecoveryActionKind.OmitSecondLayer: return "カラー2ndレイヤーを省略する";
                case ExportRecoveryActionKind.OmitThirdLayer: return "カラー3rdレイヤーを省略する";
                case ExportRecoveryActionKind.ExcludeHiddenRenderer: return "この補助表示を省略する";
                case ExportRecoveryActionKind.SkipVrChatMenus: return "VRChatメニュー由来の表情・ポーズをすべて取り込まない";
                case ExportRecoveryActionKind.ExcludeMenuBranch: return "このメニューの枝を取り込まない";
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        private void DrawIssue(IssueView issue)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (issue.hasAction && session != null)
                {
                    var enabled = selected.Contains(issue.id);
                    var next = EditorGUILayout.ToggleLeft(ExporterLocalization.T(ActionLabel(issue.actionKind)), enabled, EditorStyles.boldLabel);
                    if (next != enabled) { if (next) selected.Add(issue.id); else selected.Remove(issue.id); }
                }
                EditorGUILayout.LabelField(ExporterLocalization.T("対象: ") + issue.target, EditorStyles.wordWrappedLabel);
                EditorGUILayout.LabelField(ExporterLocalization.T("理由: ") + ExporterLocalization.T(issue.reason), EditorStyles.wordWrappedLabel);
                if (!issue.hasAction) EditorGUILayout.LabelField(ExporterLocalization.T("次にすること: ") + ExporterLocalization.T(issue.remedy), EditorStyles.wordWrappedLabel);
                if (!string.IsNullOrEmpty(issue.lostEffect)) EditorGUILayout.HelpBox(ExporterLocalization.T("変わる点: ") + ExporterLocalization.T(issue.lostEffect), MessageType.Warning);
                using (new EditorGUI.DisabledScope(issue.source == null || busy))
                    if (GUILayout.Button(ExporterLocalization.T("Unityで対象を確認")))
                    {
                        Selection.activeObject = issue.source;
                        EditorGUIUtility.PingObject(issue.source);
                    }
            }
        }

        internal static bool SaveUnmodifiedResult(ExportRecoverySession session, Action saved = null,
            Func<string, bool> confirmOverwrite = null)
        {
            if (session == null || !session.HasCurrentSuccess) throw new ArgumentException(nameof(session));
            if (session.LastSuccess.Options.Actions.Count != 0) return false;
            if (session.CheckForChanges())
                throw new InvalidOperationException(ExporterLocalization.T("アバターまたは関連アセットが変わりました。再検査してから保存してください。"));
            // A recovery window can outlive the initial file selection. Confirm
            // the file that exists now, even when this retry needed no remedy.
            if (File.Exists(session.Destination) && !(confirmOverwrite ?? ConfirmOverwrite)(session.Destination)) return false;
            session.SavePending();
            CloseSessionWindows(session);
            saved?.Invoke();
            return true;
        }

        private static bool ConfirmOverwrite(string destination) => EditorUtility.DisplayDialog(
            ExporterLocalization.T("ファイルを上書きしますか？"), destination,
            ExporterLocalization.T("上書き"), ExporterLocalization.T("キャンセル"));

        internal static void CloseSessionWindows(ExportRecoverySession session)
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<ExportFailureWindow>().Where(window => window.session == session)) window.Close();
            foreach (var window in Resources.FindObjectsOfTypeAll<ExportRecoveryComparisonWindow>().Where(window => window.Session == session)) window.Close();
        }

        private static bool IsMenuIssue(IssueView issue) => issue.hasAction &&
            (issue.actionKind == ExportRecoveryActionKind.SkipVrChatMenus || issue.actionKind == ExportRecoveryActionKind.ExcludeMenuBranch);

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
                        if (!SaveUnmodifiedResult(session, saved) && session.LastSuccess.Options.Actions.Count != 0)
                        {
                            ExportRecoveryComparisonWindow.Show(session, saved);
                            Close();
                        }
                    }
                }
                catch (Exception exception)
                {
                    if (session.IsInvalidated) Show(session, saved);
                    else Show(exception);
                }
                finally { busy = false; if (this != null) Repaint(); }
            };
        }
    }
}
