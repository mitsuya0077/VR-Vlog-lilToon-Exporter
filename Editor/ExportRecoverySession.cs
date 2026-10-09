using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // The export inputs are captured by the caller. A retry always invokes the
    // exporter again; an earlier preview is never reused as an export source.
    internal sealed class ExportRecoverySession
    {
        internal sealed class SuccessfulAttempt
        {
            internal byte[] Bytes { get; }
            internal IReadOnlyList<string> Warnings { get; }
            internal ExportRecoveryOptions Options { get; }
            internal ExportRecoverySourceStamp Stamp { get; }
            internal IReadOnlyList<ExportRecoveryDiagnostic> Diagnostics { get; }
            internal SuccessfulAttempt(byte[] bytes, ICollection<string> warnings,
                ExportRecoveryOptions options, ExportRecoverySourceStamp stamp,
                IEnumerable<ExportRecoveryDiagnostic> diagnostics = null)
            {
                Bytes = bytes;
                Warnings = warnings.ToArray();
                Options = CopyOptions(options);
                Stamp = stamp;
                Diagnostics = (diagnostics ?? Array.Empty<ExportRecoveryDiagnostic>())
                    .Where(item => item?.Action != null && Options.Actions.Any(action => action.Id == item.Action.Id))
                    .GroupBy(item => item.Action.Id).Select(group =>
                    {
                        var item = group.First();
                        return new ExportRecoveryDiagnostic
                        {
                            Id = item.Id, Code = item.Code, Stage = item.Stage, Target = item.Target,
                            Reason = item.Reason, Remedy = item.Remedy, LostEffect = item.LostEffect,
                            Source = item.Source, Action = Options.Actions.First(action => action.Id == item.Action.Id)
                        };
                    }).ToArray();
            }
        }

        IDisposable retainedInput;
        int inputOwners;
        sealed class InputOwnership : IDisposable
        {
            ExportRecoverySession owner;
            internal InputOwnership(ExportRecoverySession owner) { this.owner = owner; owner.inputOwners++; }
            public void Dispose()
            {
                var current = owner; owner = null;
                if (current != null && --current.inputOwners == 0) current.ReleaseInput();
            }
        }
        internal IDisposable AcquireInputOwnership() => new InputOwnership(this);
        internal void RetainInput(IDisposable input)
        {
            if (retainedInput != null) throw new InvalidOperationException("Recovery input is already owned.");
            retainedInput = input;
        }
        internal void ReleaseInput() { var input = retainedInput; retainedInput = null; input?.Dispose(); }

        private readonly Func<ExportRecoveryOptions, ExportRecoveryReport, ICollection<string>, byte[]> create;
        private readonly GameObject[] previewExcludedObjects;
        private readonly ExportGimmickOptions previewGimmicks;
        private ExportRecoverySourceStamp stamp;
        private readonly List<ExportRecoveryDiagnostic> availableDiagnostics = new List<ExportRecoveryDiagnostic>();
        internal GameObject Source { get; }
        internal string Destination { get; }
        internal ExportRecoveryReport Report { get; private set; }
        internal Exception Failure { get; private set; }
        internal ExportRecoveryOptions SelectedOptions { get; private set; } = new ExportRecoveryOptions();
        internal IReadOnlyList<ExportRecoveryDiagnostic> AvailableDiagnostics => availableDiagnostics;
        internal SuccessfulAttempt LastSuccess { get; private set; }
        internal bool IsInvalidated { get; private set; }
        internal bool WasCanceled { get; private set; }
        internal bool HasPendingSave { get; private set; }
        // IMGUI queries this several times for layout and repaint. The update
        // loop refreshes source validity; mutations and saving force a fresh
        // check. A reinspection creates a new stamp object, so an older draft
        // cannot become current merely because IsInvalidated was reset.
        internal bool HasCurrentSuccess => Source != null && LastSuccess != null && !IsInvalidated && ReferenceEquals(LastSuccess.Stamp, stamp);
        internal bool CanSave => HasPendingSave && HasCurrentSuccess;

        internal ExportRecoverySession(GameObject source, string destination,
            Func<ExportRecoveryOptions, ExportRecoveryReport, ICollection<string>, byte[]> create,
            IEnumerable<GameObject> previewExcludedObjects = null, ExportGimmickOptions previewGimmicks = null)
        {
            Source = source;
            Destination = destination;
            this.create = create ?? throw new ArgumentNullException(nameof(create));
            this.previewExcludedObjects = (previewExcludedObjects ?? Array.Empty<GameObject>()).ToArray();
            this.previewGimmicks = previewGimmicks == null ? null : new ExportGimmickOptions
            {
                AutoExclude = previewGimmicks.AutoExclude,
                IncludedObjects = (previewGimmicks.IncludedObjects ?? Array.Empty<GameObject>()).ToArray()
            };
            stamp = ExportRecoverySourceStamp.Capture(source);
        }

        // A normal export runs once before recovery exists. Seed that actual
        // failure instead of rerunning the exporter merely to open diagnostics.
        // This is a new session baseline; existing sessions are never restamped.
        internal static ExportRecoverySession FromFailedExport(GameObject source, string destination,
            Func<ExportRecoveryOptions, ExportRecoveryReport, ICollection<string>, byte[]> create,
            Exception failure, ExportRecoveryReport report,
            IEnumerable<GameObject> previewExcludedObjects = null, ExportGimmickOptions previewGimmicks = null)
        {
            if (failure == null) throw new ArgumentNullException(nameof(failure));
            if (failure is OperationCanceledException)
                throw new ArgumentException("Canceled exports do not create recovery diagnostics.", nameof(failure));
            var session = new ExportRecoverySession(source, destination, create, previewExcludedObjects, previewGimmicks);
            session.Failure = failure;
            session.Report = report != null && report.Diagnostics.Count > 0
                ? report : ExportRecoveryReport.FromException(source, failure, report?.Stage);
            session.Report.Succeeded = false;
            session.RememberDiagnostics(session.Report);
            return session;
        }

        internal ExportRecoveryPreview CreatePreview(ExportRecoveryOptions options)
        {
            if (CheckForChanges())
                throw new InvalidOperationException(ExporterLocalization.T("アバターまたは関連アセットが変わりました。再検査してから保存してください。"));
            return ExportRecoveryPreview.Create(Source, options, previewExcludedObjects, previewGimmicks);
        }

        internal bool CheckForChanges()
        {
            if (!IsInvalidated && (Source == null || stamp == null || !stamp.Matches(Source)))
            {
                IsInvalidated = true;
                WasCanceled = false;
                availableDiagnostics.Clear();
                SelectedOptions = new ExportRecoveryOptions();
            }
            return IsInvalidated;
        }

        internal bool Attempt(ExportRecoveryOptions options)
        {
            WasCanceled = false;
            if (CheckForChanges()) return false;
            SelectedOptions = CopyOptions(options);
            var warnings = new List<string>();
            var report = new ExportRecoveryReport();
            try
            {
                var bytes = create(SelectedOptions, report, warnings);
                if (CheckForChanges()) return false;
                Report = report;
                Report.Succeeded = true;
                Failure = null;
                LastSuccess = new SuccessfulAttempt(bytes, warnings, SelectedOptions, stamp,
                    report.Diagnostics.Concat(availableDiagnostics));
                HasPendingSave = true;
                RememberDiagnostics(report);
                return true;
            }
            catch (OperationCanceledException)
            {
                // A canceled attempt must not discard the last good output.
                Failure = null;
                Report = report;
                Report.Diagnostics.Clear();
                WasCanceled = true;
                // Source invalidation takes priority over a cancellation, so
                // the caller still offers reinspection when the input changed.
                CheckForChanges();
                return false;
            }
            catch (Exception exception)
            {
                Failure = exception;
                Report = report.Diagnostics.Count > 0
                    ? report : ExportRecoveryReport.FromException(Source, exception, report.Stage);
                RememberDiagnostics(Report);
                return false;
            }
        }

        internal bool Reinspect()
        {
            WasCanceled = false;
            availableDiagnostics.Clear();
            SelectedOptions = new ExportRecoveryOptions();
            if (Source == null)
            {
                IsInvalidated = true;
                Failure = new InvalidOperationException(ExporterLocalization.T("この書き出しで選んだアバターが見つかりません。アバターを指定し直してください。"));
                Report = ExportRecoveryReport.FromException(null, Failure);
                return false;
            }
            stamp = ExportRecoverySourceStamp.Capture(Source);
            IsInvalidated = false;
            return Attempt(SelectedOptions);
        }

        private void RememberDiagnostics(ExportRecoveryReport report)
        {
            // A build pass changed the menu tree. Routes from the original
            // failure can no longer be offered as a valid prepared-tree remedy.
            if (report.Diagnostics.Any(diagnostic => diagnostic?.Code == "menu-import-changed"))
                availableDiagnostics.RemoveAll(diagnostic => diagnostic?.Action?.Kind == ExportRecoveryActionKind.ExcludeMenuBranch);
            // Do not offer a recipe which the captured normal-export settings
            // have already applied. In particular, the default safe-gimmick
            // filter has already removed these hidden renderer components.
            report.Diagnostics.RemoveAll(diagnostic => diagnostic?.Action?.Kind == ExportRecoveryActionKind.ExcludeHiddenRenderer &&
                previewGimmicks?.AutoExclude == true && diagnostic.Action.Renderer != null &&
                !ExportGimmickDetection.Kept(diagnostic.Action.Renderer.transform, previewGimmicks.IncludedObjects));
            foreach (var diagnostic in report.Diagnostics)
            {
                if (diagnostic?.Action == null) continue;
                var index = availableDiagnostics.FindIndex(item => item.Action.Id == diagnostic.Action.Id);
                if (index < 0) availableDiagnostics.Add(diagnostic);
                else availableDiagnostics[index] = diagnostic;
            }
        }

        internal void SavePending()
        {
            if (CheckForChanges() || !CanSave)
                throw new InvalidOperationException(ExporterLocalization.T("アバターまたは関連アセットが変わりました。再検査してから保存してください。"));
            ExportOutputWriter.Write(Destination, LastSuccess.Bytes);
            HasPendingSave = false;
        }

        internal static ExportRecoveryOptions CopyOptions(ExportRecoveryOptions options)
        {
            var copy = new ExportRecoveryOptions();
            if (options != null)
                foreach (var action in options.Actions.Where(action => action != null))
                    copy.Actions.Add(new ExportRecoveryAction
                    {
                        Id = action.Id, Kind = action.Kind, Material = action.Material, Renderer = action.Renderer,
                        MenuRoot = action.MenuRoot, MenuOwner = action.MenuOwner, MenuPath = action.MenuPath
                    });
            return copy;
        }
    }

    internal static class ExportOutputWriter
    {
        internal static void Write(string destination, byte[] bytes)
        {
            if (!string.Equals(Path.GetExtension(destination), ".vrm", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(ExporterLocalization.T("保存先の拡張子は .vrm にしてください。保存先を選び直して書き出してください。"));
            // Validate before touching an existing destination or its directory.
            LilToonGlbExtension.Validate(bytes);
            var directory = Path.GetDirectoryName(Path.GetFullPath(destination));
            if (string.IsNullOrEmpty(directory))
                throw new InvalidOperationException(ExporterLocalization.T("保存先フォルダーが正しくありません。"));
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(temporary, bytes);
                LilToonGlbExtension.Validate(File.ReadAllBytes(temporary));
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
