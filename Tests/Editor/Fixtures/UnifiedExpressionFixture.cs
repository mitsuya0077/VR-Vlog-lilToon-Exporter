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
            aliases = VrmUnifiedExpressions.Resolve(new[] { "EyeClosedLeft", "UE/eye_closed_left" });
            check(aliases["EyeClosedLeft"] == 1, "An explicit UE morph wins over a bare canonical alias.");
            aliases = VrmUnifiedExpressions.Resolve(new[] { "UE/EyeClosedLeft", "ue/eye_closed_left", "MouthClosed" }, warnings);
            check(!aliases.ContainsKey("EyeClosedLeft") && aliases.ContainsKey("MouthClosed"), "Equally preferred explicit aliases are ambiguous; lower tiers cannot bypass them.");
            var invalidPreferred = Fixture("EyeClosedLeft", "UE/EyeClosedLeft");
            ((List<object>)((Dictionary<string, object>)((List<object>)((Dictionary<string, object>)((List<object>)invalidPreferred["meshes"])[0])["primitives"])[0])["targets"]).RemoveAt(1);
            var rejectedPreferred = false;
            try { VrmUnifiedExpressions.Add(Encode(invalidPreferred)); } catch (InvalidOperationException) { rejectedPreferred = true; }
            check(rejectedPreferred, "A selected explicit route with an invalid target cannot fall back to a lower-priority raw alias.");

            var covered = Fixture("LipFunnel", "LipFunnelUpperLeft");
            var coveredVrm = (Dictionary<string, object>)((Dictionary<string, object>)covered["extensions"])["VRMC_vrm"];
            var splitRoute = Obj("morphTargetBinds", Arr(Obj("node", 1L, "index", 1L, "weight", .4)), "isBinary", false);
            coveredVrm["expressions"] = Obj("custom", Obj("UE/LipFunnelUpperLeft", splitRoute));
            var coveredCustom = Custom(GlbDocument.Read(VrmUnifiedExpressions.Add(Encode(covered))).Json);
            check(!coveredCustom.ContainsKey("UE/LipFunnel") && coveredCustom.ContainsKey("UE/LipFunnelUpperLeft"), "An authored split route reserves its mesh before raw aggregate selection.");
            splitRoute["morphTargetBinds"] = Arr(Obj("node", 1L, "index", 1L, "weight", 0.0));
            coveredCustom = Custom(GlbDocument.Read(VrmUnifiedExpressions.Add(Encode(covered))).Json);
            check(!coveredCustom.ContainsKey("UE/LipFunnel"), "Zero-only authored coverage cannot be bypassed by an overlapping aggregate.");
            ((List<object>)covered["nodes"]).Add(Obj("mesh", 1L));
            ((List<object>)covered["meshes"]).Add(((List<object>)Fixture("LipFunnel")["meshes"])[0]);
            coveredCustom = Custom(GlbDocument.Read(VrmUnifiedExpressions.Add(Encode(covered))).Json);
            var uncoveredBinds = (List<object>)((Dictionary<string, object>)coveredCustom["UE/LipFunnel"])["morphTargetBinds"];
            check(uncoveredBinds.Count == 1 && (long)((Dictionary<string, object>)uncoveredBinds[0])["node"] == 2L, "Authored anatomical coverage reserves only its actual mesh; another face mesh remains usable.");
            var coveredRoutes = (Dictionary<string, object>)((Dictionary<string, object>)coveredVrm["expressions"])["custom"];
            coveredRoutes.Remove("UE/LipFunnelUpperLeft"); coveredRoutes.Add("lip_funnel_upper_left", splitRoute);
            coveredCustom = Custom(GlbDocument.Read(VrmUnifiedExpressions.Add(Encode(covered))).Json);
            uncoveredBinds = (List<object>)((Dictionary<string, object>)coveredCustom["UE/LipFunnel"])["morphTargetBinds"];
            check(coveredCustom.ContainsKey("lip_funnel_upper_left") && !coveredCustom.ContainsKey("UE/LipFunnelUpperLeft") && uncoveredBinds.Count == 1 &&
                (long)((Dictionary<string, object>)uncoveredBinds[0])["node"] == 2L, "Once UE is established, normalized bare authored routes reserve their actual mesh too.");
            splitRoute["morphTargetBinds"] = Arr(Obj("node", 1L, "index", 999L, "weight", .4));
            var coveredDiagnostics = new List<string>();
            coveredCustom = Custom(GlbDocument.Read(VrmUnifiedExpressions.Add(Encode(covered), coveredDiagnostics)).Json);
            uncoveredBinds = (List<object>)((Dictionary<string, object>)coveredCustom["UE/LipFunnel"])["morphTargetBinds"];
            check(uncoveredBinds.Count == 1 && (long)((Dictionary<string, object>)uncoveredBinds[0])["node"] == 2L && coveredDiagnostics.Any(message => message.Contains("既存")),
                "Malformed authored indices still reserve their known mesh and produce diagnostics, preventing a conflicting raw bypass.");

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
            blink = BlinkShapeNames.Resolve(new[] { "Blink_L", "EyeClosedLeft", "EyeClosedRight" }, allowPartial: true);
            check(blink[1] == 1 && blink[2] == 2, "A complete UE eyelid pair wins before accepting an earlier incomplete legacy alias.");
            blink = BlinkShapeNames.Resolve(new[] { "Blink_L", "UE/EyeClosedLeft" }, allowPartial: true);
            check(blink[1] == 1 && blink[2] < 0, "A UE partial closure wins over a legacy partial before cross-mesh pairing.");
            blink = BlinkShapeNames.Resolve(new[] { "Blink_L", "Blink_R", "UE/EyeClosedLeft" }, allowPartial: true);
            check(blink[1] == 0 && blink[2] == 1, "A complete legacy closure pair retains its existing priority.");
            var required = VrmUnifiedExpressions.Resolve(new[] { "LipFunnel", "LipFunnelUpperLeft" }, avatarSupportsUnified: true,
                authoredCoverage: new[] { "LipFunnelUpperLeft" }, reservedAuthoredNames: new HashSet<string>(new[] { "LipFunnelUpperLeft" }, StringComparer.Ordinal));
            check(required.Count == 0, "Preparation does not require unused raw aggregate/split endpoints when an authored route reserves that mesh and channel.");
        }
    }
}
