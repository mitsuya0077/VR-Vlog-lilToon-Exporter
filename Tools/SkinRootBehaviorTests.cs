#if EXPORTER_BEHAVIOR_TESTS
using System;
using VRVlog.LilToonExporter.Tests;

public static class ExporterSkinRootBehaviorTests
{
    public static void Run()
    {
        Action<bool, string> check = (passed, message) =>
        {
            if (!passed) throw new Exception("Skin root regression: " + message);
        };
        SkinRootFixture.SiblingAnchor(check);
        SkinRootFixture.ValidAncestor(check);
        SkinRootFixture.OptionalSkeleton(check);
        SkinRootFixture.OmittedOuterRoot(check);
        foreach (var reason in new[] { "cycle", "multiple parents", "missing joint" })
            SkinRootFixture.InvalidReference(reason, check);
        Console.WriteLine("Skin root metadata regressions: 7 scenarios passed.");
    }
}
#endif
