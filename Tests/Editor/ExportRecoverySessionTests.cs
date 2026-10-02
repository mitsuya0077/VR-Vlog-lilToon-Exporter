using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class ExportRecoverySessionTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void FailedOrCanceledRetryPreservesLastSuccessAndExistingDestination(bool cancel)
        {
            using var f = new ExportRecoveryTests.RecoveryFixture();
            var destination = Path.Combine(Path.GetTempPath(), "vrvlog-recovery-" + Guid.NewGuid().ToString("N") + ".vrm");
            var existing = new byte[] { 21, 22, 23 };
            var successful = new byte[] { 31, 32, 33 };
            var fail = false;
            try
            {
                File.WriteAllBytes(destination, existing);
                var session = new ExportRecoverySession(f.Source, destination, (options, report, warnings) =>
                {
                    if (fail)
                    {
                        if (cancel) throw new OperationCanceledException();
                        throw new InvalidOperationException("Retry failed");
                    }
                    warnings.Add("successful warning");
                    return successful;
                });
                Assert.That(session.Attempt(new ExportRecoveryOptions()), Is.True);
                var last = session.LastSuccess;
                Assert.That(last.Warnings, Does.Contain("successful warning"));
                Assert.That(File.ReadAllBytes(destination), Is.EqualTo(existing), "An attempt is only a draft until confirmation.");
                fail = true;
                Assert.That(session.Attempt(new ExportRecoveryOptions()), Is.False);
                Assert.That(session.LastSuccess, Is.SameAs(last));
                Assert.That(session.LastSuccess.Bytes, Is.EqualTo(successful));
                Assert.That(session.HasPendingSave, Is.True);
                Assert.That(File.ReadAllBytes(destination), Is.EqualTo(existing));
            }
            finally { if (File.Exists(destination)) File.Delete(destination); }
        }

        [Test]
        public void StaleSourceInvalidatesCandidatesAndBlocksRetryAndSaveUntilReinspection()
        {
            using var f = new ExportRecoveryTests.RecoveryFixture();
            f.Material.SetFloat("_UseAudioLink", 1);
            var calls = 0;
            var session = new ExportRecoverySession(f.Source, "unused.vrm", (options, report, warnings) =>
            {
                calls++;
                if (options.Actions.Count == 0) throw AudioLinkFailure(f.Material);
                return new byte[] { 1, 2, 3 };
            });
            Assert.That(session.Attempt(new ExportRecoveryOptions()), Is.False);
            var options = new ExportRecoveryOptions(); options.Actions.Add(session.AvailableDiagnostics.Single().Action);
            Assert.That(session.Attempt(options), Is.True);
            var last = session.LastSuccess;
            f.Material.SetColor("_Color", Color.blue);
            Assert.That(session.CheckForChanges(), Is.True);
            Assert.That(session.AvailableDiagnostics, Is.Empty);
            Assert.That(session.SelectedOptions.Actions, Is.Empty);
            Assert.That(session.CanSave, Is.False);
            Assert.That(session.Attempt(options), Is.False);
            Assert.That(calls, Is.EqualTo(2));
            Assert.Throws<InvalidOperationException>(() => session.SavePending());
            Assert.That(session.LastSuccess, Is.SameAs(last));
            Assert.That(session.Reinspect(), Is.False, "Reinspection starts with the original settings.");
            Assert.That(session.IsInvalidated, Is.False);
            Assert.That(session.SelectedOptions.Actions, Is.Empty);
            Assert.That(session.AvailableDiagnostics, Has.Count.EqualTo(1));
            Assert.That(calls, Is.EqualTo(3));
            Assert.That(session.CanSave, Is.False, "An old draft must remain blocked after failed reinspection.");
            Assert.That(session.HasCurrentSuccess, Is.False);
        }

        [Test]
        public void SourceChangeDuringAnAttemptCannotReplaceTheLastSuccessfulDraft()
        {
            using var f = new ExportRecoveryTests.RecoveryFixture();
            var mutateDuringAttempt = false;
            var session = new ExportRecoverySession(f.Source, "unused.vrm", (options, report, warnings) =>
            {
                if (mutateDuringAttempt) f.Material.SetFloat("_UseMain2ndTex", 1);
                return new byte[] { (byte)(mutateDuringAttempt ? 2 : 1) };
            });
            Assert.That(session.Attempt(null), Is.True);
            var last = session.LastSuccess;
            mutateDuringAttempt = true;
            Assert.That(session.Attempt(null), Is.False);
            Assert.That(session.IsInvalidated, Is.True);
            Assert.That(session.LastSuccess, Is.SameAs(last));
            Assert.That(session.CanSave, Is.False);
        }

        [Test]
        public void ConsecutiveMaterialFailuresKeepBothIndependentRecipeChoices()
        {
            using var f = new ExportRecoveryTests.RecoveryFixture();
            var other = new Material(f.Material) { name = f.Material.name };
            try
            {
                f.Material.SetFloat("_UseAudioLink", 1); other.SetFloat("_UseAudioLink", 1);
                f.AddRenderer("display", other);
                var session = new ExportRecoverySession(f.Source, "unused.vrm", (options, report, warnings) =>
                {
                    if (!options.Actions.Any(a => a.Material == f.Material)) throw AudioLinkFailure(f.Material);
                    if (!options.Actions.Any(a => a.Material == other)) throw AudioLinkFailure(other);
                    return new byte[] { 1 };
                });
                Assert.That(session.Attempt(null), Is.False);
                var first = session.AvailableDiagnostics.Single().Action;
                var options = new ExportRecoveryOptions(); options.Actions.Add(first);
                Assert.That(session.Attempt(options), Is.False);
                Assert.That(session.AvailableDiagnostics.Select(d => d.Action.Material), Is.EquivalentTo(new[] { f.Material, other }));
                Assert.That(session.AvailableDiagnostics.Select(d => d.Action.Id).Distinct().Count(), Is.EqualTo(2));
                options.Actions.Add(session.AvailableDiagnostics.Single(d => d.Action.Material == other).Action);
                Assert.That(session.Attempt(options), Is.True);
                Assert.That(session.LastSuccess.Options.Actions, Has.Count.EqualTo(2));
            }
            finally { Object.DestroyImmediate(other); }
        }

        [Test]
        public void ConfirmedValidSaveReplacesExistingFileAndLeavesNoTemporaryFile()
        {
            using var f = new ExportRecoveryTests.RecoveryFixture();
            var directory = Path.Combine(Path.GetTempPath(), "vrvlog-recovery-" + Guid.NewGuid().ToString("N"));
            var destination = Path.Combine(directory, "confirmed.vrm");
            var bytes = LilToonGlbExtension.Inject(MaterialBindingFixture.Build(f.Material.name), f.Source, "0.11.5", "2.3.4");
            try
            {
                Directory.CreateDirectory(directory); File.WriteAllBytes(destination, new byte[] { 41, 42 });
                var session = new ExportRecoverySession(f.Source, destination, (options, report, warnings) => bytes);
                Assert.That(session.Attempt(null), Is.True);
                Assert.That(session.CanSave, Is.True);
                session.SavePending();
                Assert.That(File.ReadAllBytes(destination), Is.EqualTo(bytes));
                Assert.That(Directory.GetFiles(directory), Is.EqualTo(new[] { destination }));
                Assert.That(session.HasPendingSave, Is.False);
                Assert.That(session.CanSave, Is.False);
                Assert.Throws<InvalidOperationException>(() => session.SavePending());
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        [Test]
        public void InvalidDraftCannotOverwriteExistingFileOrLeaveTemporaryFiles()
        {
            using var f = new ExportRecoveryTests.RecoveryFixture();
            var directory = Path.Combine(Path.GetTempPath(), "vrvlog-recovery-" + Guid.NewGuid().ToString("N"));
            var destination = Path.Combine(directory, "existing.vrm");
            var original = new byte[] { 41, 42 };
            try
            {
                Directory.CreateDirectory(directory); File.WriteAllBytes(destination, original);
                var session = new ExportRecoverySession(f.Source, destination, (options, report, warnings) => new byte[] { 1, 2 });
                Assert.That(session.Attempt(null), Is.True);
                Assert.Throws<InvalidDataException>(() => session.SavePending());
                Assert.That(File.ReadAllBytes(destination), Is.EqualTo(original));
                Assert.That(Directory.GetFiles(directory), Is.EqualTo(new[] { destination }));
                Assert.That(session.HasPendingSave, Is.True);
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        [Test]
        public void SaveChecksFreshSourceStateEvenWhenNoPeriodicCheckHasRun()
        {
            using var f = new ExportRecoveryTests.RecoveryFixture();
            var destination = Path.Combine(Path.GetTempPath(), "vrvlog-recovery-" + Guid.NewGuid().ToString("N") + ".vrm");
            var original = new byte[] { 51, 52, 53 };
            var bytes = LilToonGlbExtension.Inject(MaterialBindingFixture.Build(f.Material.name), f.Source, "0.11.5", "2.3.4");
            try
            {
                File.WriteAllBytes(destination, original);
                var session = new ExportRecoverySession(f.Source, destination, (options, report, warnings) => bytes);
                Assert.That(session.Attempt(null), Is.True);
                var last = session.LastSuccess;
                f.Material.SetColor("_Color", Color.blue);
                // Do not call CheckForChanges or query a property that refreshes
                // the fingerprint. Saving is independently responsible for it.
                Assert.Throws<InvalidOperationException>(() => session.SavePending());
                Assert.That(session.IsInvalidated, Is.True);
                Assert.That(session.LastSuccess, Is.SameAs(last));
                Assert.That(session.HasPendingSave, Is.True);
                Assert.That(File.ReadAllBytes(destination), Is.EqualTo(original));
            }
            finally { if (File.Exists(destination)) File.Delete(destination); }
        }

        static NotSupportedException AudioLinkFailure(Material material)
            => ExportRecoveryFailure.At(new NotSupportedException("AudioLink unsupported"), "audio-link", material);
    }
}
