using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.LanTransfer.Tests
{
    public sealed class PhoneTransferDisabledTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public void PublicTransferEntryPointsCannotOpenAWindowOrUseTheLocalExport()
        {
            var path = Path.Combine(Path.GetTempPath(), "vrvlog-local-" + Guid.NewGuid().ToString("N") + ".vrm");
            var bytes = new byte[] { 51, 52, 53 };
            var windows = Resources.FindObjectsOfTypeAll<LanVrmTransferWindow>().Select(window => window.GetInstanceID()).OrderBy(id => id).ToArray();
            try
            {
                File.WriteAllBytes(path, bytes);
                Assert.Throws<NotSupportedException>(() => LanVrmTransferWindow.CreateSnapshotPath());
                Assert.Throws<NotSupportedException>(() => LanVrmTransferWindow.Show(path, "local.vrm"));
                Assert.That(Resources.FindObjectsOfTypeAll<LanVrmTransferWindow>().Select(window => window.GetInstanceID()).OrderBy(id => id), Is.EqualTo(windows));
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes), "A rejected phone operation must preserve an ordinary exported VRM.");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void RestoredTransferWindowCannotStartFromStaleSnapshotOrAddressState()
        {
            var window = ScriptableObject.CreateInstance<LanVrmTransferWindow>();
            var path = Path.Combine(Path.GetTempPath(), "vrvlog-disabled-window-" + Guid.NewGuid().ToString("N") + ".vrm");
            try
            {
                File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                Set(window, "snapshotPath", path);
                Set(window, "displayName", "restored.vrm");
                Set(window, "addresses", new[] { IPAddress.Parse("192.168.1.2") });
                Set(window, "addressLabels", new[] { "Saved interface" });
                Invoke(window, "OnEnable");
                Assert.That((IPAddress[])Get(window, "addresses"), Is.Empty);
                Assert.That((string[])Get(window, "addressLabels"), Is.Empty);
                Assert.That(File.Exists(path), Is.False, "An obsolete transfer window cleans only its owned temporary snapshot.");
                Invoke(window, "Begin");
                Invoke(window, "Poll");
                Assert.That(Get(window, "server"), Is.Null);
                Assert.That(Get(window, "starting"), Is.Null);
                Assert.That(Get(window, "startupSnapshot"), Is.Null);
                Assert.That(Get(window, "qrTexture"), Is.Null);
                Assert.That(Get(window, "snapshotPath"), Is.Null);
            }
            finally { Object.DestroyImmediate(window); if (File.Exists(path)) File.Delete(path); }
        }

        [TestCase("OnEnable")][TestCase("Poll")]
        public void DisabledRestoredWindowStopsAnOldListenerAndReleasesItsSnapshot(string callback)
        {
            var window = ScriptableObject.CreateInstance<LanVrmTransferWindow>();
            var path = Path.Combine(Path.GetTempPath(), "vrvlog-disabled-listener-" + Guid.NewGuid().ToString("N") + ".vrm");
            LanVrmTransferServer server = null;
            try
            {
                File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                // Only the explicit loopback fixture can create this synthetic
                // old session; production Start remains disabled.
                server = LanVrmTransferServer.StartLoopbackForTests(path, "old.vrm", TimeSpan.FromMinutes(1));
                Set(window, "server", server);
                Set(window, "snapshotPath", path);
                Invoke(window, callback);
                Assert.That(server.State, Is.EqualTo(TransferState.Canceled));
                Assert.That(File.Exists(path), Is.False);
                Assert.That(Get(window, "server"), Is.Null);
                Assert.Throws<SocketException>(() => { using (var client = new TcpClient()) client.Connect(IPAddress.Loopback, server.Port); });
            }
            finally { Object.DestroyImmediate(window); server?.Dispose(); if (File.Exists(path)) File.Delete(path); }
        }

        private static void Set(LanVrmTransferWindow window, string name, object value) => typeof(LanVrmTransferWindow).GetField(name, PrivateInstance).SetValue(window, value);
        private static object Get(LanVrmTransferWindow window, string name) => typeof(LanVrmTransferWindow).GetField(name, PrivateInstance).GetValue(window);
        private static void Invoke(LanVrmTransferWindow window, string name) => typeof(LanVrmTransferWindow).GetMethod(name, PrivateInstance).Invoke(window, null);
    }
}
