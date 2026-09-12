using System;
using NUnit.Framework;
using VRVlog.LilToonExporter.Compatibility;

namespace VRVlog.LilToonExporter.Tests
{
    public class DependencyEnvironmentTests
    {
        [Test]
        public void ActualInstalledPackagesMatchRequestedTestEnvironment()
        {
            var expected = Environment.GetEnvironmentVariable("VRVLOG_TEST_UNIVRM");
            if (string.IsNullOrEmpty(expected)) Assert.Ignore("Run through Tools/run-unity-compatibility.py with an explicit environment.");
            Assert.That(UnityEngine.Application.unityVersion, Is.EqualTo(Environment.GetEnvironmentVariable("VRVLOG_TEST_UNITY")));
            var packages = DependencyDiagnostics.Installed();
            var vrm = DependencyDiagnostics.Version(packages, "com.vrmc.vrm");
            var gltf = DependencyDiagnostics.Version(packages, "com.vrmc.gltf");
            if (expected == "missing")
            {
                Assert.That(vrm, Is.Null);
                Assert.That(gltf, Is.Null);
                Assert.That(DependencyDiagnostics.OpenExporter, Is.Null);
                Assert.That(DependencyDiagnostics.Error(packages), Is.Not.Null);
            }
            else
            {
                Assert.That(vrm, Is.EqualTo(expected));
                Assert.That(gltf, Is.EqualTo(expected));
                var supported = DependencyPolicy.SupportsUniVrm(expected);
                Assert.That(DependencyDiagnostics.OpenExporter != null, Is.EqualTo(supported));
                Assert.That(DependencyPolicy.UniVrmError(vrm, gltf) == null, Is.EqualTo(supported));
                if (supported)
                {
                    Assert.That(DependencyDiagnostics.Version(packages, "jp.lilxyzw.liltoon"), Is.EqualTo(DependencyPolicy.LilToonVersion));
                    Assert.That(DependencyDiagnostics.Error(packages), Is.Null);
                }
            }
        }
    }
}
