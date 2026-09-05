using System;
using System.Collections.Generic;

namespace VRVlog.LilToonExporter
{
    // Register the final exported targets, not Unity's pre-export mesh indices.
    // Keep every named shape: names alone cannot distinguish a facial expression
    // from an author's intentional clothing/body expression.
    internal static class VrmMorphExpressions
    {
        internal static int Add(Dictionary<string, object> vrm, List<object> nodes, List<object> meshes)
        {
            var expressions = Object(vrm, "expressions");
            var custom = Object(expressions, "custom");
            var added = 0;
            for (var nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
            {
                var node = nodes[nodeIndex] as Dictionary<string, object>;
                if (node == null || !node.TryGetValue("mesh", out var meshValue)) continue;
                if (!(meshValue is long meshIndex) || meshIndex < 0 || meshIndex >= meshes.Count)
                    throw new InvalidOperationException("Invalid expression mesh index.");
                var mesh = meshes[(int)meshIndex] as Dictionary<string, object>;
                var names = List(Object(mesh, "extras"), "targetNames");
                if (names == null) continue;
                var primitives = List(mesh, "primitives");
                if (primitives == null || primitives.Count == 0) continue;
                for (var index = 0; index < names.Count; index++)
                {
                    if (!(names[index] is string name) || string.IsNullOrWhiteSpace(name)) continue;
                    foreach (var primitive in primitives)
                    {
                        var targets = List(primitive as Dictionary<string, object>, "targets");
                        if (targets == null || targets.Count != names.Count)
                            throw new InvalidOperationException("BlendShape names and exported targets do not match.");
                    }
                    var part = Text(node, "name") ?? Text(mesh, "name") ?? "Mesh";
                    var stem = part + " / " + name;
                    var key = stem;
                    // Exact binding equality makes the two exporter passes idempotent,
                    // without treating an authored material/composite clip as generated.
                    var suffix = 2;
                    while (custom != null && custom.TryGetValue(key, out var existing))
                    {
                        if (IsGenerated(existing as Dictionary<string, object>, nodeIndex, index)) break;
                        key = stem + " (" + suffix++ + ")";
                    }
                    if (custom != null && custom.ContainsKey(key)) continue;
                    if (expressions == null) vrm["expressions"] = expressions = new Dictionary<string, object>();
                    if (custom == null) expressions["custom"] = custom = new Dictionary<string, object>();
                    custom.Add(key, new Dictionary<string, object> {
                        { "morphTargetBinds", new List<object> { new Dictionary<string, object> {
                            { "node", (long)nodeIndex }, { "index", (long)index }, { "weight", 1.0 }
                        } } },
                        { "isBinary", false },
                        { "extras", new Dictionary<string, object> { { "vrvlogGeneratedMorph", true } } }
                    });
                    added++;
                }
            }
            return added;
        }

        private static bool IsGenerated(Dictionary<string, object> clip, int node, int index)
        {
            var extras = Object(clip, "extras");
            if (extras == null || !extras.TryGetValue("vrvlogGeneratedMorph", out var marker) || !Equals(marker, true)) return false;
            var binds = List(clip, "morphTargetBinds");
            if (binds == null || binds.Count != 1 || !(binds[0] is Dictionary<string, object> bind)) return false;
            return bind.TryGetValue("node", out var n) && Equals(n, (long)node) &&
                bind.TryGetValue("index", out var i) && Equals(i, (long)index);
        }

        private static Dictionary<string, object> Object(Dictionary<string, object> parent, string key) =>
            parent != null && parent.TryGetValue(key, out var value) ? value as Dictionary<string, object> : null;
        private static List<object> List(Dictionary<string, object> parent, string key) =>
            parent != null && parent.TryGetValue(key, out var value) ? value as List<object> : null;
        private static string Text(Dictionary<string, object> parent, string key) =>
            parent != null && parent.TryGetValue(key, out var value) && value is string text && !string.IsNullOrWhiteSpace(text) ? text : null;
    }
}
