using System;
using System.Collections.Generic;
using System.Linq;
using VRVlog.Expressions;

namespace VRVlog.LilToonExporter
{
    internal static class VrmMenuExpressions
    {
        internal sealed class Expression
        {
            internal string Name;
            internal readonly List<string> Targets = new List<string>();
            internal ExpressionAnimationData Animation = null;
        }

        internal static int CountRegistered(byte[] bytes)
        {
            var current = GlbDocument.Read(bytes).Json;
            foreach (var key in new[] { "extensions", "VRMC_vrm", "expressions", "custom" })
            {
                if (!current.TryGetValue(key, out var value) || !(value is Dictionary<string, object> next)) return 0;
                current = next;
            }
            return current.Count(p => p.Key.StartsWith("VRChat / ", StringComparison.Ordinal) &&
                p.Value is Dictionary<string, object> expression && expression.TryGetValue("morphTargetBinds", out var binds) &&
                binds is List<object> list && list.Count > 0);
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
            var animations = new List<ExpressionAnimationData>();
            foreach (var expression in expressions)
            {
                var name = "VRChat / " + expression.Name;
                var suffix = 2;
                while (custom.ContainsKey(name)) name = "VRChat / " + expression.Name + " (" + suffix++ + ")";
                var binds = new List<object>();
                var allTargets = expression.Targets.Concat(expression.Animation == null ? Enumerable.Empty<string>() :
                    expression.Animation.Channels.SelectMany(c => c.Points).Select(p => p.Target).Where(t => t != null));
                foreach (var target in allTargets)
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
                    if (expression.Targets.Contains(target)) binds.Add(matches.Single());
                }
                if (binds.Count == 0) throw new InvalidOperationException("表情の出力先がありません: " + expression.Name);
                custom.Add(name, new Dictionary<string, object>
                {
                    ["morphTargetBinds"] = binds, ["isBinary"] = true,
                    ["overrideBlink"] = "block", ["overrideMouth"] = "block", ["overrideLookAt"] = "block"
                });
                if (expression.Animation != null)
                {
                    expression.Animation.Expression = name; // Includes any duplicate-name suffix.
                    animations.Add(expression.Animation);
                }
            }
            if (animations.Count > 0)
            {
                var extensions = (Dictionary<string, object>)glb.Json["extensions"];
                if (extensions.ContainsKey(ExpressionAnimationData.Extension))
                    throw new InvalidOperationException("既存の表情アニメーション拡張を上書きできません。");
                extensions.Add(ExpressionAnimationData.Extension, ExpressionAnimationData.Write(animations));
                if (!glb.Json.TryGetValue("extensionsUsed", out var used)) glb.Json["extensionsUsed"] = used = new List<object>();
                ((List<object>)used).Add(ExpressionAnimationData.Extension);
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
