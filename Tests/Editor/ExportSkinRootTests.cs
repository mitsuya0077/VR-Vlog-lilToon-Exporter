using NUnit.Framework;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class ExportSkinRootTests
    {
        [Test]
        public void SiblingBoundsAnchorIsReplacedWithoutTouchingGeometryOrInput() => SkinRootFixture.SiblingAnchor(Check);

        [Test]
        public void ValidAncestorIsPreservedByteForByte() => SkinRootFixture.ValidAncestor(Check);

        [Test]
        public void MissingSkeletonStaysOptional() => SkinRootFixture.OptionalSkeleton(Check);

        [Test]
        public void OmittedOuterRootDoesNotBecomeAnUnrelatedJoint() => SkinRootFixture.OmittedOuterRoot(Check);

        [TestCase("cycle")]
        [TestCase("multiple parents")]
        [TestCase("missing joint")]
        public void InvalidCrossReferencesAreRejected(string failure) => SkinRootFixture.InvalidReference(failure, Check);

        private static void Check(bool passed, string message) => Assert.That(passed, Is.True, message);
    }
}
