using System;
using System.Collections.Generic;

namespace VRVlog.LilToonExporter
{
    // Work on UniVRM's final nodes/targetNames so no Unity hierarchy index is
    // mistaken for a glTF node or morph target after mesh export.
    internal static class VrmExpressionBindings
    {
        private static readonly (string Preset, string[] Aliases)[] Mappings = {
            ("blink", new[] { "eye_close", "Blink", "Fcl_EYE_Close", "まばたき" }),
            ("blinkLeft", new[] { "eye_close_left", "Blink_L", "BlinkLeft", "EyeBlinkLeft", "Fcl_EYE_Close_L" }),
            ("blinkRight", new[] { "eye_close_right", "Blink_R", "BlinkRight", "EyeBlinkRight", "Fcl_EYE_Close_R" }),
            ("aa", new[] { "vrc.v.aa", "mouth_a", "Aa", "A", "Fcl_MTH_A" }),
            ("ih", new[] { "vrc.v.ih", "mouth_i", "Ih", "I", "Fcl_MTH_I" }),
            ("ou", new[] { "vrc.v.ou", "mouth_u", "Ou", "U", "Fcl_MTH_U" }),
            ("ee", new[] { "vrc.v.e", "mouth_e", "Ee", "E", "Fcl_MTH_E" }),
            ("oh", new[] { "vrc.v.oh", "mouth_o", "Oh", "O", "Fcl_MTH_O" }),
        };

        public static byte[] AddMissing(byte[] bytes, ICollection<string> warnings = null)
        {
            var glb = GlbDocument.Read(bytes);
            var vrm = Object(Object(glb.Json, "extensions"), "VRMC_vrm");
            var nodes = List(glb.Json, "nodes");
            var meshes = List(glb.Json, "meshes");
            if (vrm == null || nodes == null || meshes == null)
                throw new InvalidOperationException("VRMの表情を設定するためのnode・mesh情報がありません。");
            var expressions = Object(vrm, "expressions");
            var presets = Object(expressions, "preset");
            var changed = false;
            foreach (var mapping in Mappings)
            {
                // An authored preset, including an intentionally empty one,
                // always wins over inferred raw mesh names.
                if (presets != null && presets.ContainsKey(mapping.Preset)) continue;
                var bindings = new List<object>();
                for (var nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
                {
                    var node = nodes[nodeIndex] as Dictionary<string, object>;
                    if (node == null || !node.TryGetValue("mesh", out var rawMesh)) continue;
                    var meshIndex = Index(rawMesh, meshes.Count, "mesh");
                    var mesh = meshes[meshIndex] as Dictionary<string, object>;
                    var names = List(Object(mesh, "extras"), "targetNames");
                    if (names == null) continue;
                    var target = FindTarget(names, mapping.Aliases);
                    if (target == -2)
                    {
                        Warn(warnings, $"{mapping.Preset}: mesh {meshIndex} に同名の表情が複数あるため自動設定を省略しました。");
                        continue;
                    }
                    if (target < 0) continue;
                    var primitives = List(mesh, "primitives");
                    if (primitives == null || primitives.Count == 0) continue;
                    var valid = true;
                    foreach (var rawPrimitive in primitives)
                    {
                        var targets = List(rawPrimitive as Dictionary<string, object>, "targets");
                        if (targets == null || target >= targets.Count) { valid = false; break; }
                    }
                    if (!valid) throw new InvalidOperationException("表情名とmorph targetの対応が不正です。");
                    bindings.Add(new Dictionary<string, object> {
                        { "node", (long)nodeIndex }, { "index", (long)target }, { "weight", 1.0 }
                    });
                }
                if (bindings.Count == 0) continue;
                if (expressions == null) vrm["expressions"] = expressions = new Dictionary<string, object>();
                if (presets == null) expressions["preset"] = presets = new Dictionary<string, object>();
                presets[mapping.Preset] = new Dictionary<string, object> {
                    { "morphTargetBinds", bindings }, { "isBinary", false }
                };
                changed = true;
                Warn(warnings, $"{mapping.Preset}: 既知の表情名からVRMの割り当てを追加しました。動作を確認してください。");
            }
            if (presets == null || (!presets.ContainsKey("blink") &&
                !(presets.ContainsKey("blinkLeft") && presets.ContainsKey("blinkRight"))))
                Warn(warnings, "瞬きのVRM設定を自動生成できませんでした。元アバターにVRMのBlink設定を追加してください。");
            return changed ? glb.Write() : bytes;
        }

        // Alias order expresses a documented preference (e.g. authored VRChat
        // visemes before a generic mouth shape). Never use substring matching.
        private static int FindTarget(List<object> names, string[] aliases)
        {
            foreach (var alias in aliases)
            {
                var found = -1;
                for (var i = 0; i < names.Count; i++)
                    if (names[i] is string name && string.Equals(name, alias, StringComparison.OrdinalIgnoreCase))
                    {
                        if (found >= 0) return -2;
                        found = i;
                    }
                if (found >= 0) return found;
            }
            return -1;
        }

        private static int Index(object value, int count, string label)
        {
            if (!(value is long index) || index < 0 || index >= count)
                throw new InvalidOperationException($"Invalid {label} index.");
            return (int)index;
        }
        private static Dictionary<string, object> Object(Dictionary<string, object> parent, string key) =>
            parent != null && parent.TryGetValue(key, out var value) ? value as Dictionary<string, object> : null;
        private static List<object> List(Dictionary<string, object> parent, string key) =>
            parent != null && parent.TryGetValue(key, out var value) ? value as List<object> : null;
        private static void Warn(ICollection<string> warnings, string message)
        {
            if (warnings != null && !warnings.Contains(message)) warnings.Add(message);
        }
    }
}
