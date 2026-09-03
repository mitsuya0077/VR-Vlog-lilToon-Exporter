using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class GlbDocumentTests
    {
        [Test]
        public void RoundTripPreservesJsonAndBinary()
        {
            var json = new Dictionary<string, object> { { "asset", new Dictionary<string, object> { { "version", "2.0" } } }, { "name", "日本語" } };
            var source = Build(json, new byte[] { 1, 2, 3, 4 });
            var document = GlbDocument.Read(source);
            var output = document.Write();
            var roundTrip = GlbDocument.Read(output);
            Assert.AreEqual("日本語", roundTrip.Json["name"]);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, roundTrip.Binary);
        }

        [TestCase(0x46546C66u)] [TestCase(0x46546C67u)]
        public void RejectsInvalidHeader(uint magic)
        {
            var bytes = Build(new Dictionary<string, object>{{"asset",new Dictionary<string, object>{{"version","2.0"}}}}, Array.Empty<byte>());
            bytes[0]=(byte)magic; bytes[1]=(byte)(magic>>8); bytes[2]=(byte)(magic>>16); bytes[3]=(byte)(magic>>24);
            if (magic == 0x46546C67u) Assert.DoesNotThrow(()=>GlbDocument.Read(bytes)); else Assert.Throws<System.IO.InvalidDataException>(()=>GlbDocument.Read(bytes));
        }

        private static byte[] Build(Dictionary<string, object> json, byte[] binary)
        {
            return GlbDocument.Create(json, binary).Write();
        }
    }
}
