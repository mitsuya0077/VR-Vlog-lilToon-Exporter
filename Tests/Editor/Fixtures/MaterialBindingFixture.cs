using System;
using System.Collections.Generic;

namespace VRVlog.LilToonExporter.Tests
{
    // Synthetic, texture-free input for material-injection regression tests.
    // This is not a visual avatar fixture and contains no user model data.
    internal static class MaterialBindingFixture
    {
        public static byte[] Build(params string[] materialNames)
        {
            var bones = new[] { "hips", "spine", "head", "leftUpperLeg", "leftLowerLeg", "leftFoot",
                "rightUpperLeg", "rightLowerLeg", "rightFoot", "leftUpperArm", "leftLowerArm", "leftHand",
                "rightUpperArm", "rightLowerArm", "rightHand" };
            var parents = new[] { -1, 0, 1, 0, 3, 4, 0, 6, 7, 1, 9, 10, 1, 12, 13 };
            var nodes = new List<object>();
            var humanBones = new Dictionary<string, object>();
            for (var i = 0; i < bones.Length; i++)
            {
                var children = new List<object>();
                for (var j = 0; j < parents.Length; j++) if (parents[j] == i) children.Add((long)j);
                nodes.Add(Obj("name", bones[i], "children", children));
                humanBones.Add(bones[i], Obj("node", (long)i));
            }
            var materials = new List<object>();
            foreach (var name in materialNames)
                materials.Add(Obj("name", name, "extensions", Obj("VRMC_materials_mtoon", Obj("specVersion", "1.0"))));
            var meta = Obj("name", "Synthetic binding fixture", "authors", new List<object> { "Tests" },
                "avatarPermission", "onlyAuthor", "allowExcessivelyViolentUsage", false,
                "allowExcessivelySexualUsage", false, "commercialUsage", "personalNonProfit",
                "allowPoliticalOrReligiousUsage", false, "allowAntisocialOrHateUsage", false,
                "creditNotation", "required", "allowRedistribution", false, "modification", "prohibited");
            var root = Obj("asset", Obj("version", "2.0"), "nodes", nodes, "meshes", new List<object>(),
                "materials", materials, "extensionsUsed", new List<object> { "VRMC_vrm", "VRMC_materials_mtoon" },
                "extensions", Obj("VRMC_vrm", Obj("specVersion", "1.0", "meta", meta,
                    "humanoid", Obj("humanBones", humanBones))));
            return GlbDocument.Create(root, Array.Empty<byte>()).Write();
        }

        public static List<object> InjectedMaterials(byte[] bytes)
        {
            var root = GlbDocument.Read(bytes).Json;
            var extensions = (Dictionary<string, object>)root["extensions"];
            var lilToon = (Dictionary<string, object>)extensions[LilToonMobileProfile.ExtensionName];
            return (List<object>)lilToon["materials"];
        }

        private static Dictionary<string, object> Obj(params object[] pairs)
        {
            var result = new Dictionary<string, object>();
            for (var i = 0; i < pairs.Length; i += 2) result.Add((string)pairs[i], pairs[i + 1]);
            return result;
        }
    }
}
