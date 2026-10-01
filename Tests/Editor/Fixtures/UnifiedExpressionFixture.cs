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
            AuthoredEvidenceControlsSharedJaw(check);
            AuthoredMaterialResidualEvidence(check);
            var ordinary = Encode(Fixture("JawOpen", "EyeWideLeft", "TongueOut"));
            check(VrmUnifiedExpressions.Add(ordinary).SequenceEqual(ordinary), "ARKit shared names alone cannot establish UE support.");
            var unknown = Encode(Fixture("previewMouthClosed", "corrective_EyeClosedLeft_fix"));
            check(VrmUnifiedExpressions.Add(unknown).SequenceEqual(unknown), "Unknown substrings and corrective names are not tracking channels.");
            var manualNames = Encode(Fixture("VRChat / EyeClosedLeft", "VRChat / JawOpen"));
            check(VrmUnifiedExpressions.Add(manualNames).SequenceEqual(manualNames), "Reserved menu namespace names cannot establish raw UE tracking.");
            var manualRaw = Custom(GlbDocument.Read(VrmUnifiedExpressions.Add(Encode(Fixture("MouthClosed", "VRChat / EyeClosedLeft")))).Json);
            check(manualRaw.Count == 1 && manualRaw.ContainsKey("UE/MouthClosed"), "A reserved menu name remains excluded even on an established UE avatar.");
            var menuRoot = Fixture("EyeClosedLeft", "JawOpen", "__VRVlog_Menu_JawOpenFixture");
            var menuEntry = new VrmMenuExpressions.Expression { Name = "JawOpen" };
            menuEntry.Targets.Add("__VRVlog_Menu_JawOpenFixture");
            var menuOutput = VrmUnifiedExpressions.Add(VrmMenuExpressions.Add(Encode(menuRoot), new List<VrmMenuExpressions.Expression> { menuEntry }));
            var menuCustom = Custom(GlbDocument.Read(menuOutput).Json);
            var menuRoute = (Dictionary<string, object>)menuCustom["VRChat / JawOpen"];
            var trackedJaw = (Dictionary<string, object>)menuCustom["UE/JawOpen"];
            check((bool)menuRoute["isBinary"] && (string)menuRoute["overrideMouth"] == "block" && VrmMenuExpressions.CountRegistered(menuOutput) == 1,
                "A generated JawOpen menu remains a binary blocking selectable expression.");
            check(!(bool)trackedJaw["isBinary"] && (string)trackedJaw["overrideMouth"] == "none" &&
                (long)((Dictionary<string, object>)((List<object>)trackedJaw["morphTargetBinds"])[0])["index"] == 1,
                "A generated JawOpen menu cannot reserve or replace the continuous raw jaw channel.");
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

            var sharedNodes = Fixture("LipFunnel", "LipFunnelUpperLeft", "LipFunnelUpperRight");
            ((List<object>)sharedNodes["nodes"]).Add(Obj("mesh", 0L));
            var sharedVrm = (Dictionary<string, object>)((Dictionary<string, object>)sharedNodes["extensions"])["VRMC_vrm"];
            foreach (var weight in new[] { .4, 0.0, -1.0 })
            {
                var scopedRoute = Obj("morphTargetBinds", Arr(Obj("node", 1L, "index", 1L, "weight", weight)), "isBinary", false);
                sharedVrm["expressions"] = Obj("custom", Obj("UE/LipFunnelUpperLeft", scopedRoute));
                var scopedCustom = Custom(GlbDocument.Read(VrmUnifiedExpressions.Add(Encode(sharedNodes))).Json);
                var aggregateBinds = (List<object>)((Dictionary<string, object>)scopedCustom["UE/LipFunnel"])["morphTargetBinds"];
                check(aggregateBinds.Count == 1 && (long)((Dictionary<string, object>)aggregateBinds[0])["node"] == 2L,
                    "Authored coverage with weight " + weight + " reserves only node A while shared-mesh node B retains raw aggregate.");
            }
            var sharedCustom = Custom(GlbDocument.Read(VrmUnifiedExpressions.Add(Encode(sharedNodes))).Json);
            var remainingSplit = (List<object>)((Dictionary<string, object>)sharedCustom["UE/LipFunnelUpperRight"])["morphTargetBinds"];
            check(remainingSplit.Count == 1 && (long)((Dictionary<string, object>)remainingSplit[0])["node"] == 1L,
                "Per-node filtering precedes representation selection, so the disjoint split remains available on the authored node.");

            var duplicateAuthored = Fixture("MouthClosed");
            var duplicateVrm = (Dictionary<string, object>)((Dictionary<string, object>)duplicateAuthored["extensions"])["VRMC_vrm"];
            var firstAlias = Obj("morphTargetBinds", Arr(Obj("node", 1L, "index", 0L, "weight", .35)), "isBinary", false);
            var secondAlias = Obj("morphTargetBinds", Arr(Obj("node", 1L, "index", 0L, "weight", .75)), "isBinary", false);
            duplicateVrm["expressions"] = Obj("custom", Obj("UE/MouthClosed", firstAlias, "mouth_closed", secondAlias));
            var duplicateInput = Encode(duplicateAuthored);
            var duplicateDiagnostics = new List<string>();
            var duplicateOutput = VrmUnifiedExpressions.Add(duplicateInput, duplicateDiagnostics);
            check(duplicateDiagnostics.Count(message => message.Contains("重複") && message.Contains("MouthClosed")) == 1,
                "Duplicate retained aliases are diagnosed even when their raw channel was reserved before generation.");
            check(duplicateOutput.SequenceEqual(duplicateInput), "Both authored aliases and their settings remain byte-identical; duplicate raw fallback remains blocked.");
            secondAlias["morphTargetBinds"] = Arr();
            duplicateDiagnostics.Clear();
            VrmUnifiedExpressions.Add(Encode(duplicateAuthored), duplicateDiagnostics);
            check(!duplicateDiagnostics.Any(message => message.Contains("重複")), "An empty alias beside one retained authored route does not report a duplicate retained setting.");

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

            var materialOnly = Fixture("LipFunnel", "LipFunnelUpperLeft", "JawOpen");
            ((List<object>)materialOnly["nodes"]).Add(Obj("mesh", 1L));
            ((List<object>)materialOnly["meshes"]).Add(((List<object>)Fixture("LipFunnel", "JawOpen")["meshes"])[0]);
            materialOnly["materials"] = Arr(Obj());
            var materialVrm = (Dictionary<string, object>)((Dictionary<string, object>)materialOnly["extensions"])["VRMC_vrm"];
            var declarations = new[] {
                Obj("materialColorBinds", Arr(Obj("material", 0L, "type", "color", "targetValue", Arr(.1, .2, .3, 1.0)))),
                Obj("textureTransformBinds", Arr(Obj("material", 0L, "scale", Arr(1.0, 1.0), "offset", Arr(.2, .3)))),
                Obj("materialColorBinds", Arr(Obj("material", 999L, "type", "color", "targetValue", Arr(.1, .2, .3, 1.0)))),
                Obj("textureTransformBinds", Arr(Obj("material", 999L, "scale", Arr(1.0, 1.0), "offset", Arr(.2, .3)))),
                Obj("materialColorBinds", "malformed"),
                Obj("textureTransformBinds", Arr(Obj("material", 0L, "scale", Arr(1.0), "offset", Arr(.2, .3))))
            };
            for (var i = 0; i < declarations.Length; i++)
            {
                var declaration = declarations[i];
                materialVrm["expressions"] = Obj("custom", Obj("UE/LipFunnelUpperLeft", declaration));
                var materialDiagnostics = new List<string>();
                var routes = Custom(GlbDocument.Read(VrmUnifiedExpressions.Add(Encode(materialOnly), materialDiagnostics)).Json);
                check(routes.ContainsKey("UE/LipFunnelUpperLeft") && !routes.ContainsKey("UE/LipFunnel") &&
                    routes.ContainsKey("UE/JawOpen") == (i < 2) && materialDiagnostics.Any(message => message.Contains("既存")) == (i >= 2),
                    "Material/UV-only declaration " + i + " reserves conflicts globally; only usable authored UE enables shared JawOpen.");
            }
            materialVrm["expressions"] = Obj("custom", Obj("UE/LipFunnelUpperLeft", Obj("materialColorBinds", Arr(), "textureTransformBinds", Arr())));
            var emptyMaterialRoutes = Custom(GlbDocument.Read(VrmUnifiedExpressions.Add(Encode(materialOnly))).Json);
            check(emptyMaterialRoutes.ContainsKey("UE/LipFunnel") && emptyMaterialRoutes.ContainsKey("UE/JawOpen"), "Empty material/UV arrays leave raw UE aggregate available.");
            declarations[0]["morphTargetBinds"] = Arr(Obj("node", 1L, "index", 1L, "weight", .4));
            materialVrm["expressions"] = Obj("custom", Obj("UE/LipFunnelUpperLeft", declarations[0]));
            var mixedRoutes = Custom(GlbDocument.Read(VrmUnifiedExpressions.Add(Encode(materialOnly))).Json);
            var mixedBinds = (List<object>)((Dictionary<string, object>)mixedRoutes["UE/LipFunnel"])["morphTargetBinds"];
            check(mixedBinds.Count == 1 && (long)((Dictionary<string, object>)mixedBinds[0])["node"] == 2L,
                "Mixed morph/material routes retain anatomical coverage only on their declared morph mesh.");

            foreach (var materialKind in new[] { "materialColorBinds", "textureTransformBinds" })
            {
                var morphDeclarations = new[] {
                    Arr(Obj("index", 1L, "weight", .4)),
                    Arr(Obj("node", 999L, "index", 1L, "weight", .4)),
                    Arr(Obj("node", 0L, "index", 1L, "weight", .4)),
                    Arr(Obj("node", 1L, "index", 1L, "weight", .4)),
                    Arr(Obj("node", 1L, "index", 999L, "weight", .4)),
                    Arr(Obj("index", 1L, "weight", .4), Obj("node", 1L, "index", 1L, "weight", .4))
                };
                for (var index = 0; index < morphDeclarations.Length; index++)
                {
                    var material = materialKind == "materialColorBinds" ? Obj("material", 0L, "type", "color", "targetValue", Arr(.1, .2, .3, 1.0)) :
                        Obj("material", 0L, "scale", Arr(1.0, 1.0), "offset", Arr(.2, .3));
                    var declaration = Obj("morphTargetBinds", morphDeclarations[index], materialKind, Arr(material));
                    materialVrm["expressions"] = Obj("custom", Obj("UE/LipFunnelUpperLeft", declaration));
                    var sourceBytes = Encode(materialOnly);
                    var scopeDiagnostics = new List<string>();
                    var scopedBytes = VrmUnifiedExpressions.Add(sourceBytes, scopeDiagnostics);
                    var scopedRoutes = Custom(GlbDocument.Read(scopedBytes).Json);
                    var hasScope = index >= 3;
                    var aggregate = scopedRoutes.TryGetValue("UE/LipFunnel", out var aggregateValue) ?
                        (List<object>)((Dictionary<string, object>)aggregateValue)["morphTargetBinds"] : null;
                    check(scopedRoutes.ContainsKey("UE/LipFunnelUpperLeft") &&
                        (hasScope ? aggregate?.Count == 1 && (long)((Dictionary<string, object>)aggregate[0])["node"] == 2L :
                            aggregate == null && !scopedRoutes.ContainsKey("UE/JawOpen") && scopedBytes.SequenceEqual(sourceBytes)) &&
                        scopeDiagnostics.Any(message => message.Contains("既存")) == (index != 3),
                        materialKind + " plus morph declaration " + index + " reserves globally only when no morph has resolvable node scope, preserving diagnosed metadata.");
                }
            }

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
            check(!BlinkShapeNames.CompatiblePartials("Blink_L", "eye_blink_1_R"), "UE evidence cannot make different legacy partial families compatible.");
            check(BlinkShapeNames.CompatiblePartials("blink_l", "BLINK_R"), "A matching legacy family can join left and right renderers.");
            check(BlinkShapeNames.CompatiblePartials("face.eye_closed_left", "UE/EyeClosedRight"), "Canonical UE-eye sides remain compatible across normalized renderer names.");
            check(!BlinkShapeNames.CompatiblePartials("UE/EyeClosedLeft", "Blink_R"), "A UE partial cannot pair with an unrelated legacy partial.");
            var required = VrmUnifiedExpressions.Resolve(new[] { "LipFunnel", "LipFunnelUpperLeft" }, avatarSupportsUnified: true,
                authoredCoverage: new[] { "LipFunnelUpperLeft" }, reservedAuthoredNames: new HashSet<string>(new[] { "LipFunnelUpperLeft" }, StringComparer.Ordinal));
            check(required.Count == 0, "Preparation does not require unused raw aggregate/split endpoints when an authored route reserves that mesh and channel.");
        }

        private static void AuthoredMaterialResidualEvidence(Action<bool, string> check)
        {
            foreach(var kind in new[]{"color","uv"})
            foreach(var moving in new[]{false,true})
            foreach(var movingMorph in new[]{false,true})
            {
                var root=Fixture("JawOpen");root["materials"]=Arr(Obj());
                var route=kind=="color"?
                    Obj("materialColorBinds",Arr(Obj("material",0L,"type","color","targetValue",moving?Arr(.2,.3,.4,1.0):Arr(1.0,1.0,1.0,1.0)))):
                    Obj("textureTransformBinds",Arr(Obj("material",0L,"scale",Arr(1.0,1.0),"offset",moving?Arr(.2,.3):Arr(0.0,0.0))));
                if(movingMorph)route["morphTargetBinds"]=Arr(Obj("node",1L,"index",0L,"weight",.4));
                var vrm=(Dictionary<string,object>)((Dictionary<string,object>)root["extensions"])["VRMC_vrm"];
                vrm["expressions"]=Obj("custom",Obj("UE/MouthClosed",route));
                var input=Encode(root);var output=VrmUnifiedExpressions.Add(input);var custom=Custom(GlbDocument.Read(output).Json);
                check(custom.ContainsKey("UE/JawOpen")== (moving||movingMorph),"Only a residual "+kind+" or moving morph route establishes shared JawOpen; identity material alone stays inert.");
                check(JsonDom.Serialize(custom["UE/MouthClosed"])==JsonDom.Serialize(route),"Residual qualification preserves the authored "+kind+" target and mixed morph metadata verbatim.");
                if(!moving&&!movingMorph)check(output.SequenceEqual(input),"An identity "+kind+" author route cannot modify a shared-name-only export.");
            }
            foreach(var kind in new[]{"color","uv"})
            {
                var root=Fixture("LipFunnel","JawOpen");root["materials"]=Arr(Obj());
                var inert=kind=="color"?
                    Obj("materialColorBinds",Arr(Obj("material",0L,"type","color","targetValue",Arr(1.0,1.0,1.0,1.0)))):
                    Obj("textureTransformBinds",Arr(Obj("material",0L,"scale",Arr(1.0,1.0),"offset",Arr(0.0,0.0))));
                var vrm=(Dictionary<string,object>)((Dictionary<string,object>)root["extensions"])["VRMC_vrm"];
                vrm["expressions"]=Obj("custom",Obj("UE/LipFunnelUpperLeft",inert));
                var input=Encode(root);var output=VrmUnifiedExpressions.Add(input);var custom=Custom(GlbDocument.Read(output).Json);
                check(!custom.ContainsKey("UE/LipFunnel")&&!custom.ContainsKey("UE/JawOpen")&&output.SequenceEqual(input),"An inert nonempty "+kind+" split author still reserves global anatomy and cannot lend identity to shared JawOpen.");

                root=Fixture("JawOpen");root["materials"]=Arr(Obj());
                var moving=kind=="color"?
                    Obj("materialColorBinds",Arr(Obj("material",0L,"type","color","targetValue",Arr(.2,.3,.4,1.0)))):
                    Obj("textureTransformBinds",Arr(Obj("material",0L,"scale",Arr(1.0,1.0),"offset",Arr(.2,.3))));
                vrm=(Dictionary<string,object>)((Dictionary<string,object>)root["extensions"])["VRMC_vrm"];
                vrm["expressions"]=Obj("custom",Obj("UE/MouthClosed",inert,"mouth_closed",moving));
                input=Encode(root);output=VrmUnifiedExpressions.Add(input);custom=Custom(GlbDocument.Read(output).Json);
                check(!custom.ContainsKey("UE/JawOpen")&&output.SequenceEqual(input),"An inert explicit "+kind+" route retains author priority; a moving lower alias cannot establish tracking through it.");
            }
            foreach(var type in new[]{"color","emissionColor","shadeColor","matcapColor","rimColor","outlineColor"})
            foreach(var moving in new[]{false,true})
            {
                var root=Fixture("JawOpen");var material=Obj();
                if(type=="color")material["pbrMetallicRoughness"]=Obj("baseColorFactor",Arr(.21404114,.21404114,.21404114,.4));
                else if(type=="emissionColor")
                {
                    material["emissiveFactor"]=Arr(.1,.1,.1);material["extensions"]=Obj("KHR_materials_emissive_strength",Obj("emissiveStrength",2.0));
                }
                else
                {
                    var property=type=="shadeColor"?"shadeColorFactor":type=="matcapColor"?"matcapFactor":type=="rimColor"?"parametricRimColorFactor":"outlineColorFactor";
                    material["extensions"]=Obj("VRMC_materials_mtoon",Obj("specVersion","1.0",property,Arr(.21404114,.21404114,.21404114)));
                }
                root["materials"]=Arr(material);var baseline=type=="emissionColor"?.2:.5;var alpha=type=="color"?.4:1.0;
                var route=Obj("materialColorBinds",Arr(Obj("material",0L,"type",type,"targetValue",Arr(moving?baseline+.1:baseline,baseline,baseline,alpha))));
                var vrm=(Dictionary<string,object>)((Dictionary<string,object>)root["extensions"])["VRMC_vrm"];vrm["expressions"]=Obj("custom",Obj("UE/MouthClosed",route));
                var input=Encode(root);var output=VrmUnifiedExpressions.Add(input);var custom=Custom(GlbDocument.Read(output).Json);
                check(custom.ContainsKey("UE/JawOpen")==moving,"Final glTF "+type+" compares its pinned imported baseline, including linear/sRGB conversion or emissive strength.");
                check(moving?JsonDom.Serialize(custom["UE/MouthClosed"])==JsonDom.Serialize(route):output.SequenceEqual(input),"A "+type+" baseline match stays inert and retained; a moving target remains unchanged while establishing tracking.");
            }
            foreach(var moving in new[]{false,true})
            {
                var root=Fixture("JawOpen");root["materials"]=Arr(Obj("pbrMetallicRoughness",Obj("baseColorTexture",Obj("index",0L,
                    "extensions",Obj("KHR_texture_transform",Obj("scale",Arr(2.0,3.0),"offset",Arr(.2,-2.4)))))));
                var route=Obj("textureTransformBinds",Arr(Obj("material",0L,"scale",Arr(2.0,3.0),"offset",Arr(moving?.3:.2,-2.4))));
                var vrm=(Dictionary<string,object>)((Dictionary<string,object>)root["extensions"])["VRMC_vrm"];vrm["expressions"]=Obj("custom",Obj("UE/MouthClosed",route));
                var input=Encode(root);var output=VrmUnifiedExpressions.Add(input);var custom=Custom(GlbDocument.Read(output).Json);
                check(custom.ContainsKey("UE/JawOpen")==moving,"Nonidentity baseColorTexture KHR transform uses the same pinned vertical flip as authored UV bindings.");
                check(moving?JsonDom.Serialize(custom["UE/MouthClosed"])==JsonDom.Serialize(route):output.SequenceEqual(input),"A matching nonidentity UV transform is preserved inert, while an actual offset residual establishes tracking.");
            }
        }

        private static void AuthoredEvidenceControlsSharedJaw(Action<bool, string> check)
        {
            foreach (var issue in new[] { "empty", "emptyArrays", "malformed", "invalidIndex", "zero", "positive", "color", "uv", "rawDistinctive" })
            {
                var root = issue == "rawDistinctive" ? Fixture("EyeClosedLeft", "JawOpen") : Fixture("JawOpen");
                root["materials"] = Arr(Obj());
                var vrm = (Dictionary<string, object>)((Dictionary<string, object>)root["extensions"])["VRMC_vrm"];
                var authored = Obj();
                if (issue == "emptyArrays") authored = Obj("morphTargetBinds", Arr(), "materialColorBinds", Arr(), "textureTransformBinds", Arr());
                if (issue == "malformed") authored = Obj("morphTargetBinds", "malformed");
                if (issue == "invalidIndex" || issue == "zero" || issue == "positive")
                    authored = Obj("morphTargetBinds", Arr(Obj("node", 1L, "index", issue == "invalidIndex" ? 99L : 0L, "weight", issue == "zero" ? 0.0 : .4)));
                if (issue == "color") authored = Obj("materialColorBinds", Arr(Obj("material", 0L, "type", "color", "targetValue", Arr(.1, .2, .3, 1.0))));
                if (issue == "uv") authored = Obj("textureTransformBinds", Arr(Obj("material", 0L, "scale", Arr(.7, .8), "offset", Arr(.2, .3))));
                vrm["expressions"] = Obj("custom", Obj("UE/MouthClosed", authored));
                var input = Encode(root); var output = VrmUnifiedExpressions.Add(input);
                var routes = Custom(GlbDocument.Read(output).Json);
                var usable = issue == "positive" || issue == "color" || issue == "uv" || issue == "rawDistinctive";
                check(routes.ContainsKey("UE/JawOpen") == usable,
                    "Authored evidence " + issue + " enables a shared raw JawOpen only with usable authored or surviving distinctive support.");
                if (!usable)
                    check(output.SequenceEqual(input), "Empty/unusable/zero-only UE metadata " + issue + " cannot mutate shared-name-only export or bypass preserved author intent.");
                else
                {
                    var jaw = (Dictionary<string, object>)routes["UE/JawOpen"];
                    var binding = ((List<object>)jaw["morphTargetBinds"]).Cast<Dictionary<string, object>>().Single();
                    check((long)binding["node"] == 1L && (long)binding["index"] == (issue == "rawDistinctive" ? 1L : 0L) &&
                        Convert.ToDouble(binding["weight"]) == 1.0 && !(bool)jaw["isBinary"],
                        "Established support " + issue + " emits a continuous shared JawOpen on its actual final morph.");
                }
                if (issue == "positive")
                    check(Convert.ToDouble(((Dictionary<string, object>)((List<object>)((Dictionary<string, object>)routes["UE/MouthClosed"])["morphTargetBinds"])[0])["weight"]) == .4,
                        "Usable authored morph evidence retains its existing fractional weight.");
                if (issue == "rawDistinctive")
                    check(routes.ContainsKey("UE/EyeClosedLeft") && ((Dictionary<string, object>)routes["UE/MouthClosed"]).Count == 0,
                        "An independent surviving distinctive raw channel can establish UE beside an untouched empty author route.");
            }
            var suppressed = Fixture("LipFunnel", "JawOpen");
            suppressed["materials"] = Arr(Obj());
            var suppressedVrm = (Dictionary<string, object>)((Dictionary<string, object>)suppressed["extensions"])["VRMC_vrm"];
            suppressedVrm["expressions"] = Obj("custom", Obj("UE/LipFunnelUpperLeft",
                Obj("materialColorBinds", Arr(Obj("material", 99L, "type", "color", "targetValue", Arr(.1, .2, .3, 1.0))))));
            var suppressedInput = Encode(suppressed); var suppressedOutput = VrmUnifiedExpressions.Add(suppressedInput);
            var suppressedRoutes = Custom(GlbDocument.Read(suppressedOutput).Json);
            check(!suppressedRoutes.ContainsKey("UE/LipFunnel") && !suppressedRoutes.ContainsKey("UE/JawOpen"),
                "A conflict-suppressed distinctive raw aggregate and unusable authored material route cannot enable shared JawOpen.");
            check(suppressedOutput.SequenceEqual(suppressedInput), "Post-selection evidence preserves unusable anatomical author intent without adding unrelated shared tracking metadata.");
        }
    }
}
