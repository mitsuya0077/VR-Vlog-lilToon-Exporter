using NUnit.Framework;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class GlbBinaryOptimizerTests
    {
        [Test]
        public void CompactionPreservesValuesAndWritableIsolation() =>
            GlbBinaryFixture.Run((condition, message) => Assert.That(condition, Is.True, message));
    }
}
