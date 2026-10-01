using System;
using System.Collections.Generic;
using System.Linq;
using VRVlog.FaceTracking;

namespace VRVlog.LilToonExporter.Tests
{
    internal static class UnifiedExpressionFixture
    {
        private static Dictionary<string, object> Obj(params object[] pairs)
        {
            var value = new Dictionary<string, object>();
            for (var i = 0; i < pairs.Length; i += 2) value.Add((string)pairs[i], pairs[i + 1]);
            return value;
        }
        private static List<object> Arr(params object[] values) => new List<object>(values);
        private static Dictionary<string, object> Fixture(params string[] names) => Obj(
            "asset", Obj("version", "2.0"), "extensions", Obj("VRMC_vrm", Obj("specVersion", "1.0")),
            "nodes", Arr(Obj("name", "unrelated"), Obj("mesh", 0L)),
            "meshes", Arr(Obj("extras", Obj("targetNames", names.Cast<object>().ToList()),
                "primitives", Arr(Obj("targets", names.Select(_ => (object)Obj()).ToList())))));
        private static byte[] Encode(Dictionary<string, object> root) => GlbDocument.Create(root, new byte[] { 7, 8, 9, 10 }).Write();
        private static Dictionary<string, object> Custom(Dictionary<string, object> root) =>
            (Dictionary<string, object>)((Dictionary<string, object>)((Dictionary<string, object>)((Dictionary<string, object>)root["extensions"])["VRMC_vrm"])["expressions"])["custom"];

        internal static void Run(Action<bool, string> check)
        {
            var ordinary = Encode(Fixture("JawOpen", "EyeWideLeft", "TongueOut"));
            check(VrmUnifiedExpressions.Add(ordinary).SequenceEqual(ordinary), "ARKit shared names alone cannot establish UE support.");
            var unknown = Encode(Fixture("previewMouthClosed", "corrective_EyeClosedLeft_fix"));
            check(VrmUnifiedExpressions.Add(unknown).SequenceEqual(unknown), "Unknown substrings and corrective names are not tracking channels.");
            var partial = Encode(Fixture("MouthClosed", "JawOpen", "unused"));
            var warnings = new List<string>();
            var output = VrmUnifiedExpressions.Add(partial, warnings);
            var glb = GlbDocument.Read(output);
            var custom = Custom(glb.Json);
            check(custom.Count == 2 && custom.ContainsKey("UE/MouthClosed") && custom.ContainsKey("UE/JawOpen"), "Partial UE sets export available channels only.");
            foreach (Dictionary<string, object> expression in custom.Values)
            {
                var binding = (Dictionary<string, object>)((List<object>)expression["morphTargetBinds"])[0];
                check((long)binding["node"] == 1 && Convert.ToDouble(binding["weight"]) == 1 && !(bool)expression["isBinary"], "Continuous bindings use final glTF node identity.");
                check((string)expression["overrideBlink"] == "none" && (string)expression["overrideMouth"] == "none" && (string)expression["overrideLookAt"] == "none", "Tracking channels do not block other tracking groups.");
            }
            check(glb.Binary.SequenceEqual(new byte[] { 7, 8, 9, 10 }), "Adding UE metadata preserves original BIN data.");
            check(VrmUnifiedExpressions.Add(output).SequenceEqual(output), "UE export is idempotent and preserves existing valid metadata.");

            var multi = Fixture("MouthClosed", "JawOpen");
            ((List<object>)multi["nodes"]).Add(Obj("mesh", 0L));
            ((List<object>)multi["nodes"]).Add(Obj("mesh", 1L));
            ((List<object>)multi["meshes"]).Add(((List<object>)Fixture("UE/JawOpen")["meshes"])[0]);
            var multiCustom = Custom(GlbDocument.Read(VrmUnifiedExpressions.Add(Encode(multi))).Json);
            var jawBinds = (List<object>)((Dictionary<string, object>)multiCustom["UE/JawOpen"])["morphTargetBinds"];
            check(jawBinds.Count == 3 && jawBinds.Cast<Dictionary<string, object>>().Select(bind => (long)bind["node"]).SequenceEqual(new long[] { 1, 2, 3 }), "Shared meshes and separate face meshes retain every final node binding.");
            var split = Fixture("EyeClosedLeft");
            ((List<object>)split["nodes"]).Add(Obj("mesh", 1L));
            ((List<object>)split["meshes"]).Add(((List<object>)Fixture("JawOpen")["meshes"])[0]);
            var splitCustom = Custom(GlbDocument.Read(VrmUnifiedExpressions.Add(Encode(split))).Json);
            check(splitCustom.ContainsKey("UE/EyeClosedLeft") && splitCustom.ContainsKey("UE/JawOpen"), "UE evidence on one renderer enables shared channels on the avatar's other meshes.");
            check((long)((Dictionary<string, object>)((List<object>)((Dictionary<string, object>)splitCustom["UE/JawOpen"])["morphTargetBinds"])[0])["node"] == 2, "Split mouth channels bind their own final mesh node.");

            var aliases = VrmUnifiedExpressions.Resolve(new[] { "MouthClosed", "mouth_closed", "JawOpen" });
            check(aliases["MouthClosed"] == 0, "An exact canonical shape wins over normalized aliases on the same renderer.");
            aliases = VrmUnifiedExpressions.Resolve(new[] { "mouth_closed", "mouth-closed", "JawOpen" }, warnings);
            check(!aliases.ContainsKey("MouthClosed") && aliases.ContainsKey("JawOpen") && warnings.Any(warning => warning.Contains("重複")), "Ambiguous aliases are skipped with a diagnostic.");
            aliases = VrmUnifiedExpressions.Resolve(new[] { "LipFunnel", "LipFunnelUpperLeft", "MouthClosed" });
            check(aliases.ContainsKey("LipFunnel") && !aliases.ContainsKey("LipFunnelUpperLeft"), "Conflicting aggregate and split representations are selected once per mesh.");
            check(VrmUnifiedExpressions.Resolve(new[] { "ue/jaw_open" }).ContainsKey("JawOpen"), "Explicit UE prefixes enable shared-name partial profiles.");

            var authored = Fixture("MouthClosed", "JawOpen");
            var vrm = (Dictionary<string, object>)((Dictionary<string, object>)authored["extensions"])["VRMC_vrm"];
            var own = Obj("morphTargetBinds", Arr(Obj("node", 1L, "index", 0L, "weight", .35)), "isBinary", false, "overrideBlink", "block");
            vrm["expressions"] = Obj("custom", Obj("UE/MouthClosed", own, "Manual expression", Obj("isBinary", true)));
            var authoredCustom = Custom(GlbDocument.Read(VrmUnifiedExpressions.Add(Encode(authored))).Json);
            var retained = (Dictionary<string, object>)authoredCustom["UE/MouthClosed"];
            check(Convert.ToDouble(((Dictionary<string, object>)((List<object>)retained["morphTargetBinds"])[0])["weight"]) == .35 && (string)retained["overrideBlink"] == "block" && authoredCustom.ContainsKey("Manual expression"), "Authored valid metadata and manual expressions win without changes.");
            var authoredRoot = (Dictionary<string, object>)((Dictionary<string, object>)vrm["expressions"])["custom"];
            authoredRoot.Remove("UE/MouthClosed");
            authoredRoot.Add("ue/mouth_closed", own);
            authoredCustom = Custom(GlbDocument.Read(VrmUnifiedExpressions.Add(Encode(authored))).Json);
            check(authoredCustom.ContainsKey("ue/mouth_closed") && !authoredCustom.ContainsKey("UE/MouthClosed"), "Normalized explicit authored UE metadata prevents a duplicate semantic channel.");
            own["morphTargetBinds"] = Arr(Obj("node", 1L, "index", 999L, "weight", .35));
            var diagnostics = new List<string>();
            authoredCustom = Custom(GlbDocument.Read(VrmUnifiedExpressions.Add(Encode(authored), diagnostics)).Json);
            check(authoredCustom.ContainsKey("ue/mouth_closed") && !authoredCustom.ContainsKey("UE/MouthClosed") &&
                (long)((Dictionary<string, object>)((List<object>)((Dictionary<string, object>)authoredCustom["ue/mouth_closed"])["morphTargetBinds"])[0])["index"] == 999 && diagnostics.Any(message => message.Contains("既存")),
                "Malformed nonempty authored bindings are diagnosed and retained without a raw-shape bypass.");
            own["morphTargetBinds"] = Arr(Obj("node", 1L, "index", 0L, "weight", 0.0));
            authoredCustom = Custom(GlbDocument.Read(VrmUnifiedExpressions.Add(Encode(authored))).Json);
            check(Convert.ToDouble(((Dictionary<string, object>)((List<object>)((Dictionary<string, object>)authoredCustom["ue/mouth_closed"])["morphTargetBinds"])[0])["weight"]) == 0 && !authoredCustom.ContainsKey("UE/MouthClosed"),
                "An authored zero-only route intentionally disables raw fallback.");
            own["morphTargetBinds"] = Arr();
            own["materialColorBinds"] = Arr();
            own["textureTransformBinds"] = Arr();
            authoredCustom = Custom(GlbDocument.Read(VrmUnifiedExpressions.Add(Encode(authored))).Json);
            check(authoredCustom.ContainsKey("UE/MouthClosed") && !authoredCustom.ContainsKey("ue/mouth_closed"), "Empty authored binding arrays are repaired using the available raw channel.");
            authored["materials"] = Arr(Obj());
            own["materialColorBinds"] = Arr(Obj("material", 0L, "type", "color", "targetValue", Arr(.1, .2, .3, 1.0)));
            diagnostics.Clear();
            authoredCustom = Custom(GlbDocument.Read(VrmUnifiedExpressions.Add(Encode(authored), diagnostics)).Json);
            check(authoredCustom.ContainsKey("ue/mouth_closed") && !authoredCustom.ContainsKey("UE/MouthClosed") && !diagnostics.Any(message => message.Contains("既存")), "Authored material-color tracking is valid and takes precedence over raw morphs.");
            own["materialColorBinds"] = Arr();
            own["textureTransformBinds"] = Arr(Obj("material", 0L, "scale", Arr(1.0, 1.0), "offset", Arr(.2, .3)));
            diagnostics.Clear();
            authoredCustom = Custom(GlbDocument.Read(VrmUnifiedExpressions.Add(Encode(authored), diagnostics)).Json);
            check(authoredCustom.ContainsKey("ue/mouth_closed") && !authoredCustom.ContainsKey("UE/MouthClosed") && !diagnostics.Any(message => message.Contains("既存")), "Authored texture transforms also take precedence over raw morphs.");

            var invalid = Fixture("MouthClosed");
            ((Dictionary<string, object>)((List<object>)invalid["nodes"])[1])["mesh"] = 999L;
            var rejected = false;
            try { VrmUnifiedExpressions.Add(Encode(invalid)); } catch (InvalidOperationException) { rejected = true; }
            check(rejected, "Invalid final node/mesh indices are rejected.");
            invalid = Fixture("MouthClosed");
            ((List<object>)((Dictionary<string, object>)((List<object>)invalid["meshes"])[0])["primitives"]).Add(Obj("targets", Arr()));
            rejected = false;
            try { VrmUnifiedExpressions.Add(Encode(invalid)); } catch (InvalidOperationException) { rejected = true; }
            check(rejected, "Split primitives must contain the referenced morph target.");
            check(!custom.Keys.Any(name => name.StartsWith(UnifiedExpressionRegistry.RestPrefix, StringComparison.Ordinal)), "Runtime-only restoration channels are never serialized.");
            var blink = BlinkShapeNames.Resolve(new[] { "EyeClosedLeft", "EyeClosedRight" });
            check(blink[1] == 0 && blink[2] == 1, "UE closure shapes pass the exporter's automatic blink gate.");
            blink = BlinkShapeNames.Resolve(new[] { "face.eye_closed_left", "UE/EyeClosedRight" });
            check(blink[1] == 0 && blink[2] == 1, "Normalized UE closure aliases also pass the blink gate.");
            check(BlinkShapeNames.Resolve(new[] { "UE/EyeClosed" })[0] == 0, "The bilateral UE closure form supports automatic blinking.");
            check(BlinkShapeNames.Resolve(new[] { "EyeClosedLeft" }, allowPartial: true)[1] == 0 && BlinkShapeNames.Resolve(new[] { "EyeClosedLeft" }, allowPartial: true)[2] < 0, "Partial UE closure retains its available side for avatar-wide pairing.");
        }
    }
}
