using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.LanTransfer.Tests
{
    public sealed class CloudTransferWindowTests
    {
        private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;

        [Test]
        public void DevelopmentAvailabilityAndMenuRequireTheExplicitDefineWhileLanStaysDisabled()
        {
            Assert.That(LanTransferAvailability.Enabled, Is.False);
            var menu = typeof(CloudVrmTransferWindow).GetMethod("OpenDevelopmentTransfer", BindingFlags.NonPublic | BindingFlags.Static);
#if UNITY_EDITOR
            Assert.That(menu, Is.Not.Null);
            Assert.That(menu.GetCustomAttributes(typeof(MenuItem), false).Length, Is.EqualTo(1));
            var validation = typeof(CloudVrmTransferWindow).GetMethod("CanOpenDevelopmentTransfer", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(validation, Is.Not.Null);
            Assert.That(validation.GetCustomAttributes(typeof(MenuItem), false).Length, Is.EqualTo(1));
            Assert.That(validation.Invoke(null, null), Is.EqualTo(CloudTransferAvailability.Enabled));
            Assert.That(CloudVrmTransferWindow.IsAvailable, Is.EqualTo(CloudTransferAvailability.Enabled));
#else
            Assert.That(CloudTransferAvailability.Enabled, Is.False);
            Assert.That(menu, Is.Null);
#endif
        }

        [Test]
        public void OptionalExporterEntryMatchesInstalledReleaseChannel()
        {
            var exporter = Type.GetType("VRVlog.LilToonExporter.LilToonExporterWindow, VRVlog.LilToonExporter.Editor", true);
            var method = exporter.GetMethod("SavedVrmTransferMethod", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            var entry = (MethodInfo)method.Invoke(null, null);
            Assert.That(entry != null, Is.EqualTo(CloudTransferAvailability.Enabled));
            if (entry != null)
            {
                Assert.That(entry.DeclaringType, Is.EqualTo(typeof(CloudVrmTransferWindow)));
                Assert.That(entry.Name, Is.EqualTo("OpenSavedVrmForDevelopment"));
            }
        }

#if UNITY_EDITOR
        [Test]
        public void ExpressionCaptureSavedResultOpensAnImmutableCopyWithoutUploading()
        {
            var type = Type.GetType("VRVlog.LilToonExporter.ExperimentalExpressionCaptureWindow, VRVlog.LilToonExporter.Editor", true);
            var savedType = Type.GetType("VRVlog.LilToonExporter.SavedExpressionVrm, VRVlog.LilToonExporter.Editor", true);
            var capture = ScriptableObject.CreateInstance(type);
            var path = Path.Combine(Path.GetTempPath(), "vrvlog-expression-qr-" + Guid.NewGuid().ToString("N") + ".vrm");
            var bytes = new byte[] { 9, 8, 7, 6 };
            string copyPath = null;
            CloudVrmTransferWindow transfer = null;
            try
            {
                File.WriteAllBytes(path, bytes);
                var saved = Activator.CreateInstance(savedType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { path, bytes, 9, 1 }, null);
                type.GetField("saved", Instance).SetValue(capture, saved);
                var open = type.GetMethod("OpenSavedTransfer", Instance);
                if (!CloudTransferAvailability.Enabled)
                {
                    Assert.Throws<TargetInvocationException>(() => open.Invoke(capture, null));
                    return;
                }
                open.Invoke(capture, null);
                transfer = Resources.FindObjectsOfTypeAll<CloudVrmTransferWindow>().Single();
                Assert.That(typeof(CloudVrmTransferWindow).GetField("uploading", Instance).GetValue(transfer), Is.Null);
                Assert.That(typeof(CloudVrmTransferWindow).GetField("session", Instance).GetValue(transfer), Is.Null, "Opening the transfer must not create remote storage.");
                var source = (CloudVrmTransferSource)typeof(CloudVrmTransferWindow).GetField("source", Instance).GetValue(transfer);
                copyPath = (string)typeof(CloudVrmTransferSource).GetField("path", Instance).GetValue(source);
                Assert.That(copyPath, Is.Not.EqualTo(path));
                Assert.That(File.ReadAllBytes(copyPath), Is.EqualTo(bytes));
                File.WriteAllBytes(path, new byte[] { 1 });
                Assert.That(File.ReadAllBytes(copyPath), Is.EqualTo(bytes));
                Assert.Throws<TargetInvocationException>(() => open.Invoke(capture, null));
                Assert.That(typeof(CloudVrmTransferWindow).GetField("source", Instance).GetValue(transfer), Is.SameAs(source));
                transfer.Close(); transfer = null;
                Assert.That(File.Exists(copyPath), Is.False);
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 1 }));
            }
            finally
            {
                transfer?.Close(); Object.DestroyImmediate(capture);
                if (copyPath != null && File.Exists(copyPath)) File.Delete(copyPath);
                if (File.Exists(path)) File.Delete(path);
            }
        }
#endif

        [Test]
        public void PublicCloudEntryCannotAdoptOrDeleteAnOrdinarySavedVrm()
        {
            var path = Path.Combine(Path.GetTempPath(), "vrvlog-saved-export-" + Guid.NewGuid().ToString("N") + ".vrm");
            var bytes = new byte[] { 51, 52, 53 };
            var windows = Resources.FindObjectsOfTypeAll<CloudVrmTransferWindow>();
            try
            {
                File.WriteAllBytes(path, bytes);
                if (CloudTransferAvailability.Enabled)
                    Assert.Throws<InvalidOperationException>(() => CloudVrmTransferWindow.Show(path, "saved.vrm"));
                else
                {
                Assert.Throws<NotSupportedException>(() => CloudVrmTransferWindow.CreateSnapshotPath());
                Assert.Throws<NotSupportedException>(() => CloudVrmTransferWindow.Show(path, "saved.vrm"));
                Assert.Throws<NotSupportedException>(() => CloudVrmTransferWindow.OpenSavedVrmForDevelopment(path));
                }
                Assert.That(Resources.FindObjectsOfTypeAll<CloudVrmTransferWindow>(), Is.EquivalentTo(windows));
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
            }
            finally { File.Delete(path); }
        }

#if !(UNITY_EDITOR)
        [Test]
        public void RejectedCloudEntryDeletesOnlyItsPreviouslyIssuedSnapshot()
        {
            var path = Path.Combine(Path.GetTempPath(), "vrvlog-owned-cloud-export-" + Guid.NewGuid().ToString("N") + ".vrm");
            var owned = (HashSet<string>)typeof(CloudVrmTransferWindow).GetField("OwnedSnapshots", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            try
            {
                File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                // Reproduce a transfer-only path issued before the current
                // disabled release; no public API can issue one now.
                owned.Add(path);
                Assert.Throws<NotSupportedException>(() => CloudVrmTransferWindow.Show(path, "owned.vrm"));
                Assert.That(File.Exists(path), Is.False);
                Assert.That(owned.Contains(path), Is.False);
            }
            finally { owned.Remove(path); if (File.Exists(path)) File.Delete(path); }
        }

        [TestCase("OnEnable")]
        [TestCase("Begin")]
        [TestCase("Poll")]
        public void DisabledRestoredWindowReleasesItsOldSourceAndSessionBeforeStartingWork(string callback)
        {
            var window = ScriptableObject.CreateInstance<CloudVrmTransferWindow>();
            var path = Path.Combine(Path.GetTempPath(), "vrvlog-restored-cloud-export-" + Guid.NewGuid().ToString("N") + ".vrm");
            CloudVrmTransferSource source = null;
            CloudVrmTransferSession session = null;
            try
            {
                File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                source = new CloudVrmTransferSource(path, "old.vrm");
                session = new CloudVrmTransferSession(source);
                typeof(CloudVrmTransferWindow).GetField("source", Instance).SetValue(window, source);
                typeof(CloudVrmTransferWindow).GetField("session", Instance).SetValue(window, session);
                typeof(CloudVrmTransferWindow).GetField("uploading", Instance).SetValue(window, new TaskCompletionSource<bool>().Task);
                typeof(CloudVrmTransferWindow).GetField("polling", Instance).SetValue(window, new TaskCompletionSource<bool>().Task);
                typeof(CloudVrmTransferWindow).GetField("qrTexture", Instance).SetValue(window, new Texture2D(1, 1));
                typeof(CloudVrmTransferWindow).GetMethod(callback, Instance).Invoke(window, null);
                Assert.That(session.State, Is.EqualTo(CloudTransferState.Canceled));
                Assert.That(File.Exists(path), Is.False);
                Assert.Throws<ObjectDisposedException>(() => source.OpenRead());
                foreach (var field in new[] { "source", "session", "uploading", "polling", "qrTexture" })
                    Assert.That(typeof(CloudVrmTransferWindow).GetField(field, Instance).GetValue(window), Is.Null);
            }
            finally { Object.DestroyImmediate(window); session?.Dispose(); source?.Dispose(); if (File.Exists(path)) File.Delete(path); }
        }
#endif

#if UNITY_EDITOR
        [Test]
        public void DevelopmentFileSelectionCopiesTheVrmWithoutStartingAnUploadAndClosureDeletesOnlyTheCopy()
        {
            var path = Path.Combine(Path.GetTempPath(), "vrvlog-dev-selected-" + Guid.NewGuid().ToString("N") + ".vrm");
            var bytes = new byte[] { 51, 52, 53, 54 };
            CloudVrmTransferWindow window = null;
            string copyPath = null;
            try
            {
                File.WriteAllBytes(path, bytes);
                if (!CloudTransferAvailability.Enabled)
                {
                    Assert.Throws<NotSupportedException>(() => CloudVrmTransferWindow.OpenSavedVrmForDevelopment(path));
                    Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
                    return;
                }
                CloudVrmTransferWindow.OpenSavedVrmForDevelopment(path);
                window = EditorWindow.GetWindow<CloudVrmTransferWindow>();
                var source = (CloudVrmTransferSource)Get(window, "source");
                Assert.That(source, Is.Not.Null);
                Assert.That(source.Name, Is.EqualTo(Path.GetFileName(path)));
                using (var reader = source.OpenRead())
                {
                    copyPath = reader.Name;
                    Assert.That(Path.GetFullPath(copyPath), Is.Not.EqualTo(Path.GetFullPath(path)));
                    using (var copied = new MemoryStream())
                    {
                        reader.CopyTo(copied);
                        Assert.That(copied.ToArray(), Is.EqualTo(bytes));
                    }
                }
                foreach (var field in new[] { "session", "uploading", "polling", "qrTexture" })
                    Assert.That(Get(window, field), Is.Null, "Selecting a file must wait for the explicit upload button.");
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
                Object.DestroyImmediate(window); window = null;
                Assert.That(File.Exists(copyPath), Is.False);
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
                Assert.Throws<ObjectDisposedException>(() => source.OpenRead());
            }
            finally { if (window != null) Object.DestroyImmediate(window); if (copyPath != null && File.Exists(copyPath)) File.Delete(copyPath); if (File.Exists(path)) File.Delete(path); }
        }

        [TestCase(null)]
        [TestCase("")]
        public void CanceledDevelopmentFileSelectionKeepsTheExistingSourceAndSession(string selection)
        {
            var window = EditorWindow.GetWindow<CloudVrmTransferWindow>();
            var path = Path.Combine(Path.GetTempPath(), "vrvlog-dev-existing-" + Guid.NewGuid().ToString("N") + ".vrm");
            CloudVrmTransferSource source = null;
            CloudVrmTransferSession session = null;
            try
            {
                File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                source = new CloudVrmTransferSource(path, "existing.vrm");
                session = new CloudVrmTransferSession(source);
                Set(window, "source", source); Set(window, "session", session);
                if (CloudTransferAvailability.Enabled) CloudVrmTransferWindow.OpenSavedVrmForDevelopment(selection);
                else Assert.Throws<NotSupportedException>(() => CloudVrmTransferWindow.OpenSavedVrmForDevelopment(selection));
                Assert.That(Get(window, "source"), Is.SameAs(source));
                Assert.That(Get(window, "session"), Is.SameAs(session));
                Assert.That(session.State, Is.EqualTo(CloudTransferState.Preparing));
                Assert.That(File.Exists(path), Is.True);
                using (var reader = source.OpenRead()) Assert.That(reader.Length, Is.EqualTo(3));
                Assert.That(Get(window, "uploading"), Is.Null);
            }
            finally { Object.DestroyImmediate(window); session?.Dispose(); source?.Dispose(); if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void RejectedDevelopmentFileSelectionKeepsTheExistingSourceAndSession()
        {
            var window = EditorWindow.GetWindow<CloudVrmTransferWindow>();
            var path = Path.Combine(Path.GetTempPath(), "vrvlog-dev-existing-invalid-" + Guid.NewGuid().ToString("N") + ".vrm");
            CloudVrmTransferSource source = null;
            CloudVrmTransferSession session = null;
            try
            {
                File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                source = new CloudVrmTransferSource(path, "existing.vrm");
                session = new CloudVrmTransferSession(source);
                Set(window, "source", source); Set(window, "session", session);
                if (CloudTransferAvailability.Enabled)
                    Assert.Throws<FileNotFoundException>(() => CloudVrmTransferWindow.OpenSavedVrmForDevelopment(path + ".missing.vrm"));
                else Assert.Throws<NotSupportedException>(() => CloudVrmTransferWindow.OpenSavedVrmForDevelopment(path + ".missing.vrm"));
                Assert.That(Get(window, "source"), Is.SameAs(source));
                Assert.That(Get(window, "session"), Is.SameAs(session));
                Assert.That(session.State, Is.EqualTo(CloudTransferState.Preparing));
                Assert.That(File.Exists(path), Is.True);
                Assert.That(Get(window, "uploading"), Is.Null);
            }
            finally { Object.DestroyImmediate(window); session?.Dispose(); source?.Dispose(); if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void InvalidOwnedSelectionKeepsTheExistingSessionAndDeletesOnlyTheRejectedSnapshot()
        {
            if (!CloudTransferAvailability.Enabled)
            {
                Assert.Throws<NotSupportedException>(() => CloudVrmTransferWindow.CreateSnapshotPath());
                return;
            }
            var window = EditorWindow.GetWindow<CloudVrmTransferWindow>();
            var path = Path.Combine(Path.GetTempPath(), "vrvlog-dev-existing-owned-" + Guid.NewGuid().ToString("N") + ".vrm");
            var rejected = CloudVrmTransferWindow.CreateSnapshotPath();
            CloudVrmTransferSource source = null;
            CloudVrmTransferSession session = null;
            try
            {
                File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                source = new CloudVrmTransferSource(path, "existing.vrm");
                session = new CloudVrmTransferSession(source);
                Set(window, "source", source); Set(window, "session", session);
                Directory.CreateDirectory(Path.GetDirectoryName(rejected));
                File.WriteAllBytes(rejected, Array.Empty<byte>());
                Assert.Throws<InvalidOperationException>(() => CloudVrmTransferWindow.Show(rejected, "empty.vrm"));
                Assert.That(Get(window, "source"), Is.SameAs(source));
                Assert.That(Get(window, "session"), Is.SameAs(session));
                Assert.That(session.State, Is.EqualTo(CloudTransferState.Preparing));
                Assert.That(File.Exists(path), Is.True);
                Assert.That(File.Exists(rejected), Is.False);
                Assert.That(Get(window, "uploading"), Is.Null);
                var owned = (HashSet<string>)typeof(CloudVrmTransferWindow).GetField("OwnedSnapshots", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                Assert.That(owned.Contains(rejected), Is.False);
            }
            finally { Object.DestroyImmediate(window); session?.Dispose(); source?.Dispose(); if (File.Exists(path)) File.Delete(path); if (File.Exists(rejected)) File.Delete(rejected); }
        }
#endif

        [Test]
        public void WindowClosureCancelsStartupAndDeletesOnlyTheOwnedSnapshot()
        {
            var window = ScriptableObject.CreateInstance<CloudVrmTransferWindow>();
            var path = Path.Combine(Path.GetTempPath(), "vrvlog-cloud-window-" + Guid.NewGuid().ToString("N") + ".vrm");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            var source = new CloudVrmTransferSource(path, "fixture.vrm");
            var session = new CloudVrmTransferSession(source);
            try
            {
                typeof(CloudVrmTransferWindow).GetField("source", Instance).SetValue(window, source);
                typeof(CloudVrmTransferWindow).GetField("session", Instance).SetValue(window, session);
                typeof(CloudVrmTransferWindow).GetMethod("StopAndClean", Instance).Invoke(window, null);
                Assert.That(session.State, Is.EqualTo(CloudTransferState.Canceled));
                Assert.That(File.Exists(path), Is.False);
                Assert.Throws<ObjectDisposedException>(() => source.OpenRead());
            }
            finally { Object.DestroyImmediate(window); session.Dispose(); source.Dispose(); }
        }

        [Test]
        public void RestoredWindowWithoutItsNonserializedSourceCannotStartAnUpload()
        {
            var window = ScriptableObject.CreateInstance<CloudVrmTransferWindow>();
            try
            {
                typeof(CloudVrmTransferWindow).GetMethod("Begin", Instance).Invoke(window, null);
                Assert.That(typeof(CloudVrmTransferWindow).GetField("session", Instance).GetValue(window), Is.Null);
                Assert.That(typeof(CloudVrmTransferWindow).GetField("uploading", Instance).GetValue(window), Is.Null);
            }
            finally { Object.DestroyImmediate(window); }
        }

        private static object Get(CloudVrmTransferWindow window, string name) => typeof(CloudVrmTransferWindow).GetField(name, Instance).GetValue(window);
        private static void Set(CloudVrmTransferWindow window, string name, object value) => typeof(CloudVrmTransferWindow).GetField(name, Instance).SetValue(window, value);
    }
}
