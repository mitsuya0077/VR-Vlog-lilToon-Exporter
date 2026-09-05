using System;
using System.Collections.Generic;
using System.Linq;

namespace VRVlog.LilToonExporter.Tests
{
    internal static class MenuExpressionFixture
    {
        private static Dictionary<string, object> Obj(params object[] pairs)
        {
            var result = new Dictionary<string, object>();
            for (var i = 0; i < pairs.Length; i += 2) result.Add((string)pairs[i], pairs[i + 1]);
            return result;
        }
        private static List<object> Arr(params object[] values) => values.ToList();
        private static Dictionary<string, object> Mesh(params string[] names) => Obj("extras", Obj("targetNames", names.Cast<object>().ToList()),
            "primitives", Arr(Obj("targets", names.Select(n => (object)Obj()).ToList())));
        private static Dictionary<string, object> Custom(Dictionary<string, object> root) =>
            (Dictionary<string, object>)((Dictionary<string, object>)((Dictionary<string, object>)((Dictionary<string, object>)root["extensions"])["VRMC_vrm"])["expressions"])["custom"];

        internal static void Run(Action<bool, string> check)
        {
            var binary = new byte[] { 1, 2, 3, 4 };
            var root = Obj("asset", Obj("version", "2.0"), "extensions", Obj("VRMC_vrm", Obj("expressions", Obj(
                "preset", Obj("blink", Obj("isBinary", false)), "custom", Obj("VRChat / 顔 / 笑顔", Obj("isBinary", false))))),
                "nodes", Arr(Obj("mesh", 1L), Obj(), Obj("mesh", 0L)),
                "meshes", Arr(Mesh("raw-eye", "composite-face"), Mesh("raw-body", "composite-brow")));
            var expressions = new List<VrmMenuExpressions.Expression> { new VrmMenuExpressions.Expression { Name = "顔 / 笑顔" } };
            expressions[0].Targets.AddRange(new[] { "composite-face", "composite-brow" });
            var bytes = GlbDocument.Create(root, binary).Write();
            var result = GlbDocument.Read(VrmMenuExpressions.Add(bytes, expressions));
            var custom = Custom(result.Json);
            check(custom.Count == 2 && custom.ContainsKey("VRChat / 顔 / 笑顔 (2)"), "One composed menu entry is added; same-named authored expressions are retained.");
            check(!(bool)((Dictionary<string, object>)custom["VRChat / 顔 / 笑顔"])["isBinary"], "Existing expression settings remain unchanged.");
            var expression = (Dictionary<string, object>)custom["VRChat / 顔 / 笑顔 (2)"];
            var binds = (List<object>)expression["morphTargetBinds"];
            check(binds.Count == 2 && (long)((Dictionary<string, object>)binds[0])["node"] == 2 &&
                (long)((Dictionary<string, object>)binds[1])["node"] == 0, "Multi-renderer expressions bind final exported nodes after reordering.");
            check(binds.Cast<Dictionary<string, object>>().All(b => (long)b["index"] == 1 && Convert.ToDouble(b["weight"]) == 1),
                "Each combined residual is applied once at weight one.");
            check((string)expression["overrideBlink"] == "block" && (string)expression["overrideMouth"] == "block" && (string)expression["overrideLookAt"] == "block",
                "Procedural blink, mouth and gaze cannot add onto the selected composed face.");
            check(result.Binary.SequenceEqual(binary), "Registering expressions does not rewrite exported geometry or textures.");
            check(!custom.ContainsKey("raw-eye") && !custom.ContainsKey("raw-body"), "Raw BlendShapes are never catalogued as expressions.");
            check(VrmMenuExpressions.Add(bytes, new List<VrmMenuExpressions.Expression>()).SequenceEqual(bytes), "No menu expressions leaves an existing VRM byte-identical.");
            expressions[0].Targets.Add("absent");
            bool rejected = false;
            try { VrmMenuExpressions.Add(bytes, expressions); } catch (InvalidOperationException) { rejected = true; }
            check(rejected, "Missing exported composite targets fail before saving a misleading expression.");
            expressions[0].Targets.Remove("absent");
            ((List<object>)root["nodes"]).Add(Obj("mesh", 0L));
            rejected = false;
            try { VrmMenuExpressions.Add(GlbDocument.Create(root, binary).Write(), expressions); } catch (InvalidOperationException) { rejected = true; }
            check(rejected, "Ambiguous instanced output targets are rejected rather than bound to an arbitrary node.");
        }
    }
}
