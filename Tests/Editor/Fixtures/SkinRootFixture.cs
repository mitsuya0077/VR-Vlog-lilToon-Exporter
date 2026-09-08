using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VRVlog.LilToonExporter.Tests
{
    // Shared by actual NUnit tests and the host CI executable. No Unity doubles
    // are needed: every scenario exercises the production GLB parser/repair.
    internal static class SkinRootFixture
    {
        internal static void SiblingAnchor(Action<bool, string> check)
        {
            var json = Avatar();
            var original = GlbDocument.Create(json, new byte[] { 7, 6, 5, 4 }).Write();
            var snapshot = (byte[])original.Clone();
            var repaired = GlbDocument.Read(ExportSkinRoots.Repair(original));
            var skin = Skin(repaired.Json);
            check(Equals(skin["skeleton"], 1L), "Sibling anchor becomes the nearest common joint ancestor.");
            check(Equals(skin["inverseBindMatrices"], 0L), "Inverse-bind accessor remains unchanged.");
            check(repaired.Binary.SequenceEqual(new byte[] { 7, 6, 5, 4 }), "Binary skin/geometry data remains unchanged.");
            check(original.SequenceEqual(snapshot), "Input bytes remain unchanged.");
            Skin(json).Remove("skeleton");
            skin.Remove("skeleton");
            check(JsonDom.Serialize(repaired.Json) == JsonDom.Serialize(json), "Only skeleton metadata changes; all transforms and geometry references survive.");
        }

        internal static void ValidAncestor(Action<bool, string> check)
        {
            var json = Avatar();
            Skin(json)["skeleton"] = 0L;
            var original = GlbDocument.Create(json, new byte[] { 1, 2, 3, 4 }).Write();
            check(ReferenceEquals(ExportSkinRoots.Repair(original), original), "A valid ancestor is preserved byte for byte.");
        }

        internal static void OptionalSkeleton(Action<bool, string> check)
        {
            var json = Avatar();
            Skin(json).Remove("skeleton");
            var original = GlbDocument.Create(json, Array.Empty<byte>()).Write();
            check(ReferenceEquals(ExportSkinRoots.Repair(original), original), "Missing optional skeleton metadata stays absent.");
        }

        internal static void OmittedOuterRoot(Action<bool, string> check)
        {
            var json = Avatar();
            Node(json, 0).Remove("children");
            Skin(json)["joints"] = new List<object> { 1L, 4L };
            var repaired = GlbDocument.Read(ExportSkinRoots.Repair(GlbDocument.Create(json, Array.Empty<byte>()).Write()));
            check(!Skin(repaired.Json).ContainsKey("skeleton"), "An omitted common root does not become an unrelated skeleton node.");
            check(JsonDom.Serialize(Skin(repaired.Json)["joints"]) == "[1,4]", "Joint ordering remains unchanged.");
        }

        internal static void InvalidReference(string failure, Action<bool, string> check)
        {
            var json = Avatar();
            if (failure == "cycle") Node(json, 3)["children"] = new List<object> { 0L };
            else if (failure == "multiple parents") Node(json, 4)["children"] = new List<object> { 2L };
            else if (failure == "missing joint") Skin(json)["joints"] = new List<object> { 99L };
            else throw new ArgumentException(nameof(failure));
            var rejected = false;
            try { ExportSkinRoots.Repair(GlbDocument.Create(json, Array.Empty<byte>()).Write()); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "Malformed skin hierarchy is rejected: " + failure);
        }

        private static Dictionary<string, object> Avatar() => new Dictionary<string, object>
        {
            ["asset"] = new Dictionary<string, object> { ["version"] = "2.0" },
            ["nodes"] = new List<object>
            {
                new Dictionary<string, object> { ["name"] = "Exported common parent", ["children"] = new List<object> { 1L, 4L } },
                new Dictionary<string, object> { ["name"] = "Head", ["children"] = new List<object> { 2L, 3L }, ["translation"] = new List<object> { 0L, 1L, 0L } },
                new Dictionary<string, object> { ["name"] = "Nose" },
                new Dictionary<string, object> { ["name"] = "Face" },
                new Dictionary<string, object> { ["name"] = "AutoAnchorObject", ["translation"] = new List<object> { 0L, 2L, 0L } }
            },
            ["skins"] = new List<object>
            {
                new Dictionary<string, object> { ["joints"] = new List<object> { 2L, 3L }, ["skeleton"] = 4L, ["inverseBindMatrices"] = 0L }
            },
            ["accessors"] = new List<object> { new Dictionary<string, object> { ["count"] = 2L, ["type"] = "MAT4" } },
            ["meshes"] = new List<object> { new Dictionary<string, object> { ["weights"] = new List<object> { 0L, 1L } } }
        };
        private static Dictionary<string, object> Node(Dictionary<string, object> json, int index) =>
            (Dictionary<string, object>)((List<object>)json["nodes"])[index];
        private static Dictionary<string, object> Skin(Dictionary<string, object> json) =>
            (Dictionary<string, object>)((List<object>)json["skins"])[0];
    }
}
