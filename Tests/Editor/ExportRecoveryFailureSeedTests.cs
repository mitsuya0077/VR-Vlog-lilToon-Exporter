using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class ExportRecoveryFailureSeedTests
    {
        [TestCase("existing")][TestCase("empty")][TestCase("missing")]
        public void OpeningRecoveryKeepsTheFirstRealFailureWithoutRepeatingTheExport(string reportKind)
        {
            using var fixture = new ExportRecoveryTests.RecoveryFixture();
            fixture.Material.SetFloat("_UseAudioLink", 1);
            var failure = AudioLinkFailure(fixture.Material);
            var report = reportKind == "missing" ? null : reportKind == "empty"
                ? new ExportRecoveryReport { Stage = "材質保存" }
                : ExportRecoveryReport.FromException(fixture.Source, failure, "材質保存");
            var calls = 0;
            var session = ExportRecoverySession.FromFailedExport(fixture.Source, "unused.vrm",
                (options, nextReport, warnings) => { calls++; return new byte[] { 1 }; }, failure, report);
            Assert.That(calls, Is.Zero, "Opening diagnostics must not repeat the failed normal export.");
            Assert.That(session.Failure, Is.SameAs(failure));
            if (reportKind == "existing") Assert.That(session.Report, Is.SameAs(report));
            if (report != null) Assert.That(session.Report.Stage, Is.EqualTo("材質保存"));
            Assert.That(session.Report.Succeeded, Is.False);
            Assert.That(session.AvailableDiagnostics.Single().Action.Material, Is.SameAs(fixture.Material));
            Assert.That(session.SelectedOptions.Actions, Is.Empty);
            Assert.That(session.LastSuccess, Is.Null); Assert.That(session.HasPendingSave, Is.False); Assert.That(session.CanSave, Is.False);
            Assert.That(session.CheckForChanges(), Is.False); Assert.That(session.WasCanceled, Is.False);
        }

        [Test]
        public void ExplicitRecoveryRetryCreatesAFreshDraftAndOnlyConfirmedSaveReplacesTheDestination()
        {
            using var fixture = new ExportRecoveryTests.RecoveryFixture();
            fixture.Material.SetFloat("_UseAudioLink", 1);
            var failure = AudioLinkFailure(fixture.Material);
            var report = ExportRecoveryReport.FromException(fixture.Source, failure);
            var bytes = LilToonGlbExtension.Inject(MaterialBindingFixture.Build(fixture.Material.name), fixture.Source, "0.11.8", "2.3.4");
            var directory = Path.Combine(Path.GetTempPath(), "vrvlog-seeded-recovery-" + Guid.NewGuid().ToString("N"));
            var destination = Path.Combine(directory, "avatar.vrm");
            var existing = new byte[] { 41, 42, 43 }; var calls = 0;
            try
            {
                Directory.CreateDirectory(directory); File.WriteAllBytes(destination, existing);
                var session = ExportRecoverySession.FromFailedExport(fixture.Source, destination, (options, nextReport, warnings) =>
                {
                    calls++;
                    Assert.That(options.Actions.Single().Kind, Is.EqualTo(ExportRecoveryActionKind.DisableAudioLink));
                    using var copy = ExportRecoveryPreview.Create(fixture.Source, options);
                    Assert.That(copy.Copy.GetComponentInChildren<Renderer>().sharedMaterial.GetFloat("_UseAudioLink"), Is.Zero);
                    return bytes;
                }, failure, report);
                var recipe = new ExportRecoveryOptions(); recipe.Actions.Add(session.AvailableDiagnostics.Single().Action);
                Assert.That(calls, Is.Zero); Assert.That(File.ReadAllBytes(destination), Is.EqualTo(existing));
                Assert.That(session.Attempt(recipe), Is.True); Assert.That(calls, Is.EqualTo(1));
                Assert.That(session.CanSave, Is.True); Assert.That(File.ReadAllBytes(destination), Is.EqualTo(existing));
                Assert.That(fixture.Material.GetFloat("_UseAudioLink"), Is.EqualTo(1), "Remedies only affect the fresh copy.");
                session.SavePending();
                Assert.That(File.ReadAllBytes(destination), Is.EqualTo(bytes)); Assert.That(Directory.GetFiles(directory), Is.EqualTo(new[] { destination }));
                Assert.That(session.HasPendingSave, Is.False); Assert.That(session.CanSave, Is.False);
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        [TestCase("retry")][TestCase("preview")][TestCase("save")]
        public void SeedingARealFailureNeverWeakensLaterSourceChangeGuards(string operation)
        {
            using var fixture = new ExportRecoveryTests.RecoveryFixture();
            fixture.Material.SetFloat("_UseAudioLink", 1);
            var failure = AudioLinkFailure(fixture.Material);
            var destination = Path.Combine(Path.GetTempPath(), "vrvlog-seeded-stale-" + Guid.NewGuid().ToString("N") + ".vrm");
            var existing = new byte[] { 61, 62 }; var calls = 0;
            try
            {
                File.WriteAllBytes(destination, existing);
                var session = ExportRecoverySession.FromFailedExport(fixture.Source, destination,
                    (options, report, warnings) => { calls++; return new byte[] { 1 }; }, failure,
                    ExportRecoveryReport.FromException(fixture.Source, failure));
                if (operation == "save") Assert.That(session.Attempt(null), Is.True);
                var priorCalls = calls; var lastSuccess = session.LastSuccess;
                fixture.Material.SetColor("_Color", Color.red);
                if (operation == "retry") Assert.That(session.Attempt(null), Is.False);
                else if (operation == "preview") Assert.Throws<InvalidOperationException>(() => session.CreatePreview(null));
                else Assert.Throws<InvalidOperationException>(() => session.SavePending());
                Assert.That(session.IsInvalidated, Is.True); Assert.That(session.CanSave, Is.False);
                Assert.That(session.AvailableDiagnostics, Is.Empty); Assert.That(session.LastSuccess, Is.SameAs(lastSuccess));
                Assert.That(calls, Is.EqualTo(priorCalls)); Assert.That(File.ReadAllBytes(destination), Is.EqualTo(existing));
            }
            finally { if (File.Exists(destination)) File.Delete(destination); }
        }

        [Test]
        public void CancellationAndMissingFailureCannotStartRecovery()
        {
            using var fixture = new ExportRecoveryTests.RecoveryFixture();
            var calls = 0;
            byte[] Create(ExportRecoveryOptions options, ExportRecoveryReport report, System.Collections.Generic.ICollection<string> warnings)
            { calls++; return new byte[] { 1 }; }
            Assert.Throws<ArgumentNullException>(() => ExportRecoverySession.FromFailedExport(fixture.Source, "unused.vrm", Create, null, null));
            Assert.Throws<ArgumentException>(() => ExportRecoverySession.FromFailedExport(fixture.Source, "unused.vrm", Create, new OperationCanceledException(), null));
            Assert.That(calls, Is.Zero);
        }

        static Exception AudioLinkFailure(Material material) => ExportRecoveryFailure.At(
            new NotSupportedException("AudioLinkは外部連携のため今回の再現対象外です。"), "audio-link", material);
    }
}
