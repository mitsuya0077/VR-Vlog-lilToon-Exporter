using System;
using VRVlog.LilToonExporter.Compatibility;

public static class DependencyCompatibilityTests
{
    static int count;
    static void Check(bool value, string message) { count++; if (!value) throw new Exception(message); }
    public static void Run()
    {
        foreach (var version in new[] { "0.131.0", "0.131.1", "0.131.2" })
        {
            Check(DependencyPolicy.UniVrmError(version, version) == null, "Matched supported pair " + version);
            DependencyPolicy.RequireUniVrm(version, version);
            foreach (var other in new[] { "0.131.0", "0.131.1", "0.131.2" })
                if (other != version) Check(DependencyPolicy.UniVrmError(version, other) != null, "Mixed pair must fail");
        }
        foreach (var version in new[] { null, "", "0.130.1", "0.131.3", "0.132.0", "0.131.1-preview.1", "0.131.1+local", " 0.131.1", "0.131.1.0" })
        {
            Check(!DependencyPolicy.SupportsUniVrm(version), "Unregistered version cannot select a backend");
            Check(DependencyPolicy.UniVrmError(version, "0.131.1") != null, "Invalid VRM");
            Check(DependencyPolicy.UniVrmError("0.131.1", version) != null, "Invalid glTF");
            bool rejected = false;
            try { DependencyPolicy.RequireUniVrm(version, version); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Direct export also fails before conversion");
        }
        Check(DependencyPolicy.LilToonError("2.3.4") == null, "Current renderer profile");
        foreach (var version in new[] { null, "2.3.3", "2.3.5", "2.3.4-preview.1" })
            Check(DependencyPolicy.LilToonError(version) != null, "No silent renderer-profile substitution");
        Console.WriteLine("Dependency compatibility: " + count + " assertions passed.");
    }
}
