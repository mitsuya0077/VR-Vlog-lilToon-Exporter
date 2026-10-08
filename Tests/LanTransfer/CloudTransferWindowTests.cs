using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.LanTransfer.Tests
{
    public sealed class CloudTransferWindowTests
    {
        private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;

        [Test]
        public void PublicCloudEntryCannotAdoptOrDeleteAnOrdinarySavedVrm()
        {
            var path = Path.Combine(Path.GetTempPath(), "vrvlog-saved-export-" + Guid.NewGuid().ToString("N") + ".vrm");
            var bytes = new byte[] { 51, 52, 53 };
            var windows = Resources.FindObjectsOfTypeAll<CloudVrmTransferWindow>();
            try
            {
                File.WriteAllBytes(path, bytes);
                Assert.Throws<NotSupportedException>(() => CloudVrmTransferWindow.CreateSnapshotPath());
                Assert.Throws<NotSupportedException>(() => CloudVrmTransferWindow.Show(path, "saved.vrm"));
                Assert.That(Resources.FindObjectsOfTypeAll<CloudVrmTransferWindow>(), Is.EquivalentTo(windows));
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
            }
            finally { File.Delete(path); }
        }

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
    }
}
