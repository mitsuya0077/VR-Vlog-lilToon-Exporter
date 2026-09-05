using System;
using System.Collections.Generic;
using System.Linq;

namespace VRVlog.LilToonExporter
{
    internal static class VrmMenuExpressions
    {
        internal sealed class Expression
        {
            internal string Name;
            internal readonly List<string> Targets = new List<string>();
        }

        // Register only the composite targets made for actual menu entries. Raw
        // morph names are never promoted to selectable expressions.
        internal static byte[] Add(byte[] bytes, IList<Expression> expressions)
        {
            if (expressions.Count == 0) return bytes;
            var glb = GlbDocument.Read(bytes);
            var vrm = (Dictionary<string, object>)((Dictionary<string, object>)glb.Json["extensions"])["VRMC_vrm"];
            var root = Object(vrm, "expressions");
            var custom = Object(root, "custom");
            var nodes = (List<object>)glb.Json["nodes"];
            var meshes = (List<object>)glb.Json["meshes"];
            foreach (var expression in expressions)
            {
                var name = "VRChat / " + expression.Name;
                var suffix = 2;
                while (custom.ContainsKey(name)) name = "VRChat / " + expression.Name + " (" + suffix++ + ")";
                var binds = new List<object>();
                foreach (var target in expression.Targets)
                {
                    var matches = new List<object>();
                    for (var nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
                    {
                        var node = (Dictionary<string, object>)nodes[nodeIndex];
                        if (!node.TryGetValue("mesh", out var meshIndex)) continue;
                        var mesh = (Dictionary<string, object>)meshes[Convert.ToInt32(meshIndex)];
                        if (!mesh.TryGetValue("extras", out var extras) || !((Dictionary<string, object>)extras).TryGetValue("targetNames", out var rawNames)) continue;
                        var names = (List<object>)rawNames;
                        for (var i = 0; i < names.Count; i++)
                        {
                            if (!string.Equals(names[i] as string, target, StringComparison.Ordinal)) continue;
                            foreach (Dictionary<string, object> primitive in (List<object>)mesh["primitives"])
                                if (!primitive.TryGetValue("targets", out var targets) || ((List<object>)targets).Count != names.Count)
                                    throw new InvalidOperationException("表情の出力先モーフ数が一致しません: " + expression.Name);
                            matches.Add(new Dictionary<string, object> { ["node"] = (long)nodeIndex, ["index"] = (long)i, ["weight"] = 1.0 });
                        }
                    }
                    if (matches.Count != 1) throw new InvalidOperationException("表情の出力先を一意に特定できません: " + expression.Name);
                    binds.Add(matches.Single());
                }
                if (binds.Count == 0) throw new InvalidOperationException("表情の出力先がありません: " + expression.Name);
                custom.Add(name, new Dictionary<string, object>
                {
                    ["morphTargetBinds"] = binds, ["isBinary"] = true,
                    ["overrideBlink"] = "block", ["overrideMouth"] = "block", ["overrideLookAt"] = "block"
                });
            }
            return glb.Write();
        }

        private static Dictionary<string, object> Object(Dictionary<string, object> parent, string key)
        {
            if (!parent.TryGetValue(key, out var value)) parent[key] = value = new Dictionary<string, object>();
            return value as Dictionary<string, object> ?? throw new InvalidOperationException("Invalid VRM expression object: " + key);
        }
    }
}
