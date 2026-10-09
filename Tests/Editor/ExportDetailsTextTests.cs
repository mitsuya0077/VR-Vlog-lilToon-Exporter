using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace VRVlog.LilToonExporter.Tests
{
    public class ExportDetailsTextTests
    {
        private string directory;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "VRVlog-details-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void TearDown() => Directory.Delete(directory, true);

        private static Exception Failure()
        {
            try { throw new IOException("合成エラー /synthetic/project/avatar.png"); }
            catch (IOException inner) { return new InvalidOperationException("書き出し失敗", inner); }
        }

        [Test]
        public void AllDiagnosticFieldsAndTheExactNestedExceptionSurviveLargeReports()
        {
            var error = Failure();
            var issues = Enumerable.Range(0, 120).Select(index => new ExportRecoveryDiagnostic
            {
                Stage = "工程" + index, Target = "対象" + index,
                Reason = "原因" + index + new string('あ', 2048),
                Remedy = "対処" + index, LostEffect = "影響" + index
            }).ToArray();
            var text = ExportDetailsText.Failure(error.Message, error.ToString(), issues);
            foreach (var issue in issues)
                foreach (var field in new[] { issue.Stage, issue.Target, issue.Reason, issue.Remedy, issue.LostEffect })
                    Assert.That(text, Does.Contain(field));
            Assert.That(text, Does.EndWith(error.ToString()));
            Assert.That(text, Does.Contain(nameof(IOException)));
            Assert.That(text, Does.Contain(nameof(Failure)));
        }

        [Test]
        public void NativeClipboardAndUtf8FileContainTheSameCompleteUnicodeText()
        {
            var details = new string('文', 180000) + "\n最後のエラー 👁️\r\n最終行\t保存終了";
            var previous = EditorGUIUtility.systemCopyBuffer;
            try
            {
                ExportDetailsText.Copy(details);
                Assert.That(EditorGUIUtility.systemCopyBuffer, Is.EqualTo(details));
                var path = Path.Combine(directory, "全文.txt");
                Assert.That(ExportDetailsText.Save(details, () => path, out var error), Is.True, error);
                Assert.That(File.ReadAllText(path, Encoding.UTF8), Is.EqualTo(EditorGUIUtility.systemCopyBuffer));
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(new UTF8Encoding(false).GetBytes(details)));
            }
            finally { EditorGUIUtility.systemCopyBuffer = previous; }
        }

        [Test]
        public void CancelDoesNotTouchAnExistingFileOrLeaveTemporaryFiles()
        {
            var path = Path.Combine(directory, "existing.txt");
            File.WriteAllText(path, "元の内容");
            Assert.That(ExportDetailsText.Save("新しい内容", () => "", out var error), Is.False);
            Assert.That(error, Is.Null);
            Assert.That(File.ReadAllText(path), Is.EqualTo("元の内容"));
            Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));
        }

        [Test]
        public void ConfirmedExistingFileIsReplacedWithCompleteTextWithoutTemporaryFiles()
        {
            var path = Path.Combine(directory, "existing.txt");
            File.WriteAllText(path, "元の内容");
            Assert.That(ExportDetailsText.Save("新しい全文\n終わり", () => path, out var error), Is.True, error);
            Assert.That(File.ReadAllText(path), Is.EqualTo("新しい全文\n終わり"));
            Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));
        }

        [Test]
        public void WriteFailureIsReportedAndPreservesOtherFiles()
        {
            var existing = Path.Combine(directory, "existing.txt");
            File.WriteAllText(existing, "保持する内容");
            var path = Path.Combine(directory, "missing", "details.txt");
            Assert.That(ExportDetailsText.Save("全文", () => path, out var error), Is.False);
            Assert.That(error, Is.Not.Empty);
            Assert.That(File.ReadAllText(existing), Is.EqualTo("保持する内容"));
            Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));
        }

        [Test]
        public void FailedReplacementOfDirectoryCleansOnlyItsOwnTemporaryFile()
        {
            var target = Path.Combine(directory, "folder.txt");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "keep.txt"), "保持");
            Assert.That(ExportDetailsText.Save("全文", () => target, out var error), Is.False);
            Assert.That(error, Is.Not.Empty);
            Assert.That(File.ReadAllText(Path.Combine(target, "keep.txt")), Is.EqualTo("保持"));
            Assert.That(Directory.GetFiles(directory), Is.Empty);
        }

        [Test]
        public void FailureSnapshotKeepsEveryTargetSharingOneRecoveryActionAfterReload()
        {
            var window = ScriptableObject.CreateInstance<ExportFailureWindow>();
            var restored = ScriptableObject.CreateInstance<ExportFailureWindow>();
            try
            {
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                typeof(ExportFailureWindow).GetField("technicalDetails", flags).SetValue(window, Failure().ToString());
                var action = new ExportRecoveryAction { Id = "same-action" };
                var issues = new[]
                {
                    new ExportRecoveryDiagnostic { Id = "first", Target = "対象A", Reason = "原因A", Action = action },
                    new ExportRecoveryDiagnostic { Id = "second", Target = "対象B", Reason = "原因B", Action = action }
                };
                typeof(ExportFailureWindow).GetMethod("SetIssues", flags).Invoke(window, new object[] { issues });
                var field = typeof(ExportFailureWindow).GetField("fullDetails", flags);
                var text = (string)field.GetValue(window);
                Assert.That(text, Does.Contain("対象A").And.Contain("対象B").And.Contain("原因B"));
                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(window), restored);
                Assert.That(field.GetValue(restored), Is.EqualTo(text));
            }
            finally { UnityEngine.Object.DestroyImmediate(window); UnityEngine.Object.DestroyImmediate(restored); }
        }

        [Test]
        public void ExceptionWithoutDiagnosticsIsStillAvailableInFull()
        {
            var error = Failure();
            Assert.That(ExportDetailsText.Failure(error.Message, error.ToString(), null), Does.EndWith(error.ToString()));
        }
    }
}
