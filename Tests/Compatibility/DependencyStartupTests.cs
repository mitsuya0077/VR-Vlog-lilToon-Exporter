using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using VRVlog.LilToonExporter.Compatibility;

namespace VRVlog.LilToonExporter.Tests
{
    public class DependencyStartupTests
    {
        [Test]
        public void MenuResolvesBackendWithoutInitializationRegistration()
        {
            var expected = Environment.GetEnvironmentVariable("VRVLOG_TEST_UNIVRM");
            if (string.IsNullOrEmpty(expected)) Assert.Ignore("Requires an explicit compatibility environment.");
            var supported = DependencyPolicy.SupportsUniVrm(expected);
            // Reproduces the 0.10.3 dead end without relying on the user's reload
            // timing. This is a no-op once the eager registration field is removed.
            var legacyField = typeof(DependencyDiagnostics).GetField("OpenExporter", BindingFlags.Public | BindingFlags.Static);
            var original = legacyField?.GetValue(null);
            try
            {
                if (legacyField == null)
                {
                    // Environment tests may already have resolved the backend.
                    // Require and reset the new resolver before exercising the menu.
                    var refresh = typeof(DependencyDiagnostics).GetMethod("RefreshBackend", BindingFlags.Public | BindingFlags.Static);
                    Assert.That(refresh, Is.Not.Null);
                    refresh.Invoke(null, null);
                }
                legacyField?.SetValue(null, null);
                Assert.That(EditorApplication.ExecuteMenuItem("VR Vlog/lilToon VRM 1.0を書き出す"), Is.True);
                var exporter = Resources.FindObjectsOfTypeAll<EditorWindow>()
                    .SingleOrDefault(w => w.GetType().FullName == "VRVlog.LilToonExporter.LilToonExporterWindow");
                var diagnostics = Resources.FindObjectsOfTypeAll<DependencyDiagnosticsWindow>();
                Assert.That(exporter != null, Is.EqualTo(supported));
                Assert.That(diagnostics.Length, Is.EqualTo(supported ? 0 : 1));
            }
            finally
            {
                legacyField?.SetValue(null, original);
                foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>()
                    .Where(w => w.GetType().Namespace == "VRVlog.LilToonExporter" || w is DependencyDiagnosticsWindow)) window.Close();
            }
        }
    }
}
