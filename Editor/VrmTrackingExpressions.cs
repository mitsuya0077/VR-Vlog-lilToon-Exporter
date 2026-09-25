using System;
using System.Collections.Generic;
using System.Linq;

namespace VRVlog.LilToonExporter
{
    internal static class VrmTrackingExpressions
    {
        internal static readonly string[] Names = {
            "BrowDownLeft", "BrowDownRight", "BrowInnerUp", "BrowOuterUpLeft", "BrowOuterUpRight",
            "CheekPuff", "CheekSquintLeft", "CheekSquintRight", "EyeBlinkLeft", "EyeBlinkRight",
            "EyeLookDownLeft", "EyeLookDownRight", "EyeLookInLeft", "EyeLookInRight",
            "EyeLookOutLeft", "EyeLookOutRight", "EyeLookUpLeft", "EyeLookUpRight",
            "EyeSquintLeft", "EyeSquintRight", "EyeWideLeft", "EyeWideRight",
            "JawForward", "JawLeft", "JawOpen", "JawRight", "MouthClose",
            "MouthDimpleLeft", "MouthDimpleRight", "MouthFrownLeft", "MouthFrownRight",
            "MouthFunnel", "MouthLeft", "MouthLowerDownLeft", "MouthLowerDownRight",
            "MouthPressLeft", "MouthPressRight", "MouthPucker", "MouthRight",
            "MouthRollLower", "MouthRollUpper", "MouthShrugLower", "MouthShrugUpper",
            "MouthSmileLeft", "MouthSmileRight", "MouthStretchLeft", "MouthStretchRight",
            "MouthUpperUpLeft", "MouthUpperUpRight", "NoseSneerLeft", "NoseSneerRight", "TongueOut"
        };

        internal static byte[] Add(byte[] bytes, VrmTrackingProfile profile)
        {
            if (profile == null) return bytes;
            var entries = profile.expressions ?? Array.Empty<TrackingExpression>();
            if (entries.Length != Names.Length || entries.Any(e => e == null) ||
                entries.Select(e => e.name).Distinct(StringComparer.Ordinal).Count() != Names.Length ||
                Names.Any(n => entries.All(e => e.name != n)))
                throw new InvalidOperationException("追跡設定にはARKit標準52項目を一度ずつ設定してください。");

            var glb = GlbDocument.Read(bytes);
            var extensions = Obj(glb.Json, "extensions", false);
            var vrm = Obj(extensions, "VRMC_vrm", false);
            var expressions = Obj(vrm, "expressions", true);
            var custom = Obj(expressions, "custom", true);
            var nodes = Arr(glb.Json, "nodes");
            var meshes = Arr(glb.Json, "meshes");

            foreach (var entry in entries)
            {
                if (custom.ContainsKey(entry.name))
                    throw new InvalidOperationException("既存のVRM表情と追跡名が重複しています: " + entry.name);
                if (entry.morphs == null || entry.morphs.Length == 0)
                    throw new InvalidOperationException("追跡表情にシェイプキーがありません: " + entry.name);
                var binds = new List<object>();
                foreach (var morph in entry.morphs)
                {
                    if (morph == null || string.IsNullOrWhiteSpace(morph.shape) || morph.weight <= 0f || morph.weight > 1f)
                        throw new InvalidOperationException("追跡表情の設定が不正です: " + entry.name);
                    var found = 0;
                    for (var nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
                    {
                        var node = nodes[nodeIndex] as Dictionary<string, object>;
                        if (node == null || !node.TryGetValue("mesh", out var rawMesh)) continue;
                        var mesh = meshes[Convert.ToInt32(rawMesh)] as Dictionary<string, object>;
                        var extras = Obj(mesh, "extras", false);
                        if (extras == null || !extras.TryGetValue("targetNames", out var rawNames) || !(rawNames is List<object> names)) continue;
                        var index = names.FindIndex(n => string.Equals(n as string, morph.shape, StringComparison.Ordinal));
                        if (index < 0) continue;
                        foreach (var rawPrimitive in Arr(mesh, "primitives"))
                        {
                            var primitive = rawPrimitive as Dictionary<string, object>;
                            if (primitive == null || !primitive.TryGetValue("targets", out var rawTargets) ||
                                !(rawTargets is List<object> targets) || index >= targets.Count)
                                throw new InvalidOperationException("VRMモーフ番号が不正です: " + morph.shape);
                        }
                        binds.Add(new Dictionary<string, object> {
                            ["node"] = (long)nodeIndex, ["index"] = (long)index, ["weight"] = (double)morph.weight
                        });
                        found++;
                    }
                    if (found == 0) throw new InvalidOperationException("VRMにシェイプキーがありません: " + morph.shape + " (" + entry.name + ")");
                }
                custom[entry.name] = new Dictionary<string, object> {
                    ["morphTargetBinds"] = binds, ["isBinary"] = false
                };
            }
            return glb.Write();
        }

        private static Dictionary<string, object> Obj(Dictionary<string, object> parent, string key, bool create)
        {
            if (parent == null) throw new InvalidOperationException("VRM情報がありません: " + key);
            if (!parent.TryGetValue(key, out var value))
            {
                if (!create) return null;
                parent[key] = value = new Dictionary<string, object>();
            }
            return value as Dictionary<string, object> ?? throw new InvalidOperationException("VRM情報が不正です: " + key);
        }

        private static List<object> Arr(Dictionary<string, object> parent, string key)
        {
            if (parent == null || !parent.TryGetValue(key, out var value) || !(value is List<object> array))
                throw new InvalidOperationException("VRM配列がありません: " + key);
            return array;
        }
    }
}
