using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UniGLTF;
using UniVRM10;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class ExportFlowTests
    {
        [TestCase(false)][TestCase(true)]
        public async Task FreshNormalExportSavesAndReloadsWithoutARecoveryStep(bool fullLilToon)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var material = new Material(Shader.Find("lilToon"));
            var directory = Path.Combine(Path.GetTempPath(), "vrvlog-flow-" + Guid.NewGuid().ToString("N"));
            var destination = Path.Combine(directory, "avatar.vrm");
            Vrm10Instance imported = null;
            try
            {
                foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial = material;
                var vertices = fixture.Mesh.vertices;
                var state = EditorJsonUtility.ToJson(material);
                var transforms = fixture.Source.GetComponentsInChildren<Transform>(true);
                var matrices = transforms.Select(t => t.localToWorldMatrix).ToArray();
                var calls = 0; var completed = 0; byte[] exported = null;
                var recovery = LilToonExporterWindow.ExportAndSaveNormally(fixture.Source, destination, (options, report, warnings) =>
                {
                    calls++;
                    Assert.That(options.Actions, Is.Empty);
                    var output = UniVrmOneClickExporter.Export(fixture.Source, "Flow regression", "Tests", warnings,
                        exporterVersion: fullLilToon ? "0.11.9" : null, lilToonVersion: "2.3.4",
                        blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None }, recoveryOptions: options, recoveryReport: report);
                    // The atomic writer accepts the app's extension-bearing
                    // VRM contract. The legacy mobile path injects its profile
                    // after ordinary UniVRM export; the full path embeds it.
                    return fullLilToon ? output : LilToonGlbExtension.Inject(output, fixture.Source, "0.11.9", "2.3.4");
                }, (bytes, warnings) =>
                {
                    completed++; exported = bytes;
                    Assert.That(File.ReadAllBytes(destination), Is.EqualTo(bytes), "Completion must follow actual validated saving.");
                });
                Assert.That(recovery, Is.Null); Assert.That(calls, Is.EqualTo(1)); Assert.That(completed, Is.EqualTo(1));
                Assert.That(Directory.GetFiles(directory), Is.EqualTo(new[] { destination }));
                imported = await Vrm10.LoadBytesAsync(File.ReadAllBytes(destination), canLoadVrm0X: false,
                    awaitCaller: new ImmediateCaller(), controlRigGenerationOption: ControlRigGenerationOption.None);
                Assert.That(imported, Is.Not.Null);
                Assert.That(File.ReadAllBytes(destination), Is.EqualTo(exported));
                Assert.That(fixture.Mesh.vertices, Is.EqualTo(vertices));
                Assert.That(transforms.Select(t => t.localToWorldMatrix), Is.EqualTo(matrices));
                Assert.That(EditorJsonUtility.ToJson(material), Is.EqualTo(state));
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                Object.DestroyImmediate(material);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [TestCase("cancel")][TestCase("failure")][TestCase("invalid-output")]
        public void OnlyAnActualExportFailureOpensRecoveryAndNoneCanOverwriteTheDestination(string outcome)
        {
            using var fixture = new ExportRecoveryTests.RecoveryFixture();
            fixture.Material.SetFloat("_UseAudioLink", 1);
            var destination = Path.Combine(Path.GetTempPath(), "vrvlog-flow-" + Guid.NewGuid().ToString("N") + ".vrm");
            var existing = new byte[] { 51, 52, 53 }; var calls = 0; var completed = 0;
            var failure = ExportRecoveryFailure.At(new NotSupportedException("AudioLink unsupported"), "audio-link", fixture.Material);
            try
            {
                File.WriteAllBytes(destination, existing);
                ExportRecoverySession Run() => LilToonExporterWindow.ExportAndSaveNormally(fixture.Source, destination, (options, report, warnings) =>
                {
                    calls++;
                    if (outcome == "cancel") throw new OperationCanceledException();
                    if (outcome == "failure") throw failure;
                    return new byte[] { 1, 2, 3 };
                }, (bytes, warnings) => completed++);
                if (outcome == "invalid-output") Assert.Throws<InvalidDataException>(() => Run());
                else
                {
                    var recovery = Run();
                    if (outcome == "cancel") Assert.That(recovery, Is.Null);
                    else
                    {
                        Assert.That(recovery.Failure, Is.SameAs(failure));
                        Assert.That(recovery.AvailableDiagnostics.Single().Action.Kind, Is.EqualTo(ExportRecoveryActionKind.DisableAudioLink));
                        Assert.That(recovery.LastSuccess, Is.Null); Assert.That(recovery.HasPendingSave, Is.False);
                    }
                }
                Assert.That(calls, Is.EqualTo(1), "Opening recovery must not run the exporter a second time.");
                Assert.That(completed, Is.Zero); Assert.That(File.ReadAllBytes(destination), Is.EqualTo(existing));
                Assert.That(Directory.GetFiles(Path.GetDirectoryName(destination)).Any(name => Path.GetFileName(name).StartsWith("." + Path.GetFileName(destination) + ".", StringComparison.Ordinal)), Is.False);
            }
            finally { if (File.Exists(destination)) File.Delete(destination); }
        }

        [Test]
        public void FailedOutputWriteIsNotMisreportedAsAnAvatarConversionFailure()
        {
            using var fixture = new ExportRecoveryTests.RecoveryFixture();
            var directory = Path.Combine(Path.GetTempPath(), "vrvlog-flow-" + Guid.NewGuid().ToString("N"));
            var destination = Path.Combine(directory, "avatar.vrm");
            var bytes = LilToonGlbExtension.Inject(MaterialBindingFixture.Build(fixture.Material.name), fixture.Source, "0.11.9", "2.3.4");
            var completed = 0; var calls = 0;
            try
            {
                Directory.CreateDirectory(directory); Directory.CreateDirectory(destination);
                Assert.Throws<IOException>(() => LilToonExporterWindow.ExportAndSaveNormally(fixture.Source, destination,
                    (options, report, warnings) => { calls++; return bytes; }, (output, warnings) => completed++));
                Assert.That(calls, Is.EqualTo(1)); Assert.That(completed, Is.Zero);
                Assert.That(Directory.Exists(destination), Is.True); Assert.That(Directory.GetFiles(directory), Is.Empty);
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        [TestCase(false)][TestCase(true)]
        public void FreshReinspectionWithoutRemediesSavesAutomaticallyButChangedInputsStillBlockSave(bool changeBeforeSave)
        {
            using var fixture = new ExportRecoveryTests.RecoveryFixture();
            var destination = Path.Combine(Path.GetTempPath(), "vrvlog-flow-" + Guid.NewGuid().ToString("N") + ".vrm");
            var existing = new byte[] { 61, 62 }; var completed = 0; var calls = 0;
            var bytes = LilToonGlbExtension.Inject(MaterialBindingFixture.Build(fixture.Material.name), fixture.Source, "0.11.9", "2.3.4");
            try
            {
                File.WriteAllBytes(destination, existing);
                var session = ExportRecoverySession.FromFailedExport(fixture.Source, destination,
                    (options, report, warnings) => { calls++; return bytes; }, new InvalidOperationException("First failure"), null);
                fixture.Material.SetColor("_Color", Color.blue);
                Assert.That(session.CheckForChanges(), Is.True);
                Assert.That(session.Reinspect(), Is.True); Assert.That(calls, Is.EqualTo(1));
                Assert.That(session.LastSuccess.Options.Actions, Is.Empty);
                if (changeBeforeSave)
                {
                    fixture.Material.SetColor("_Color", Color.red);
                    Assert.Throws<InvalidOperationException>(() => ExportFailureWindow.SaveUnmodifiedResult(session, () => completed++));
                    Assert.That(File.ReadAllBytes(destination), Is.EqualTo(existing)); Assert.That(completed, Is.Zero);
                }
                else
                {
                    Assert.That(ExportFailureWindow.SaveUnmodifiedResult(session, () => completed++), Is.True);
                    Assert.That(File.ReadAllBytes(destination), Is.EqualTo(bytes)); Assert.That(completed, Is.EqualTo(1));
                    Assert.That(session.HasPendingSave, Is.False);
                }
            }
            finally { if (File.Exists(destination)) File.Delete(destination); }
        }

        [Test]
        public void ARemediedDraftStillRequiresExplicitPreviewConfirmation()
        {
            using var fixture = new ExportRecoveryTests.RecoveryFixture();
            fixture.Material.SetFloat("_UseAudioLink", 1);
            var destination = Path.Combine(Path.GetTempPath(), "vrvlog-flow-" + Guid.NewGuid().ToString("N") + ".vrm");
            var existing = new byte[] { 71, 72 }; var completed = 0;
            var bytes = LilToonGlbExtension.Inject(MaterialBindingFixture.Build(fixture.Material.name), fixture.Source, "0.11.9", "2.3.4");
            try
            {
                File.WriteAllBytes(destination, existing);
                var session = ExportRecoverySession.FromFailedExport(fixture.Source, destination, (options, report, warnings) => bytes,
                    ExportRecoveryFailure.At(new NotSupportedException("AudioLink unsupported"), "audio-link", fixture.Material), null);
                var options = new ExportRecoveryOptions(); options.Actions.Add(session.AvailableDiagnostics.Single().Action);
                Assert.That(session.Attempt(options), Is.True);
                Assert.That(ExportFailureWindow.SaveUnmodifiedResult(session, () => completed++), Is.False);
                Assert.That(File.ReadAllBytes(destination), Is.EqualTo(existing)); Assert.That(completed, Is.Zero); Assert.That(session.CanSave, Is.True);
            }
            finally { if (File.Exists(destination)) File.Delete(destination); }
        }

        [TestCase("DisableAudioLink", true)]
        [TestCase("SkipVrChatMenus", false)]
        [TestCase("ExcludeMenuBranch", false)]
        [TestCase("ExcludeHiddenRenderer", false)]
        public void SuggestedDirectFixNeverSelectsBroadMenuOrRendererOmission(string kindName, bool suggested)
        {
            using var fixture = new ExportRecoveryTests.RecoveryFixture();
            var kind = (ExportRecoveryActionKind)Enum.Parse(typeof(ExportRecoveryActionKind), kindName);
            var action = new ExportRecoveryAction { Id = "recipe", Kind = kind, Material = fixture.Material, Renderer = fixture.Source.GetComponentInChildren<Renderer>() };
            var report = new ExportRecoveryReport(); report.Diagnostics.Add(new ExportRecoveryDiagnostic { Id = "diagnosis", Action = action });
            var session = ExportRecoverySession.FromFailedExport(fixture.Source, "unused.vrm", (options, next, warnings) => new byte[] { 1 }, new InvalidOperationException("First failure"), report);
            var window = ScriptableObject.CreateInstance<ExportFailureWindow>();
            try
            {
                typeof(ExportFailureWindow).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, session);
                typeof(ExportFailureWindow).GetMethod("RefreshSession", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
                Assert.That(window.BuildSelectedOptions().Actions.Any(), Is.EqualTo(suggested));
                Assert.That(session.SelectedOptions.Actions, Is.Empty, "The suggestion is not applied until the user chooses the explicit retry action.");
            }
            finally { Object.DestroyImmediate(window); }
        }
    }
}
