using System;
using System.Collections.Generic;
using System.Linq;
using VRVlog.FaceTracking;

namespace VRVlog.LilToonExporter
{
    // Resolve against final glTF node identities, after authoring passes and
    // UniVRM mesh export. Tracking channels are continuous VRM custom expressions.
    internal static class VrmUnifiedExpressions
    {
        internal static Dictionary<string, int> Resolve(IReadOnlyList<string> names,
            ICollection<string> warnings = null, string label = "mesh", bool avatarSupportsUnified = false,
            IEnumerable<string> authoredCoverage = null, ISet<string> reservedAuthoredNames = null)
        {
            var matches = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var eligible = avatarSupportsUnified;
            for (var index = 0; index < names.Count; index++)
            {
                if (!UnifiedExpressionRegistry.TryCanonicalize(names[index], out var canonical)) continue;
                eligible |= UnifiedExpressionRegistry.IsDistinctive(canonical) || UnifiedExpressionRegistry.IsExplicit(names[index]);
                if (!matches.TryGetValue(canonical, out var indices)) matches.Add(canonical, indices = new List<int>());
                indices.Add(index);
            }
            var resolved = new Dictionary<string, int>(StringComparer.Ordinal);
            if (!eligible) return resolved;
            foreach (var pair in matches)
            {
                if (reservedAuthoredNames?.Contains(pair.Key) == true) continue;
                var priority = pair.Value.Min(index => RawPriority(names[index], pair.Key));
                var candidates = pair.Value.Where(index => RawPriority(names[index], pair.Key) == priority).ToArray();
                if (candidates.Length != 1)
                {
                    Warn(warnings, label + ": Unified Expressions の名前が重複するため省略しました: " + pair.Key);
                    continue;
                }
                resolved.Add(pair.Key, candidates[0]);
            }
            // Authored routes reserve their anatomical coverage before raw
            // aggregate/split selection, even when their weight is zero.
            if (authoredCoverage != null)
            {
                var authored = authoredCoverage.ToArray();
                foreach (var name in resolved.Keys.Where(name => authored.Any(existing =>
                    existing != name && UnifiedExpressionRegistry.Conflicts(name, existing))).ToArray()) resolved.Remove(name);
            }
            var selected = new HashSet<string>(UnifiedExpressionRegistry.SelectCandidates(resolved.Keys), StringComparer.Ordinal);
            foreach (var name in resolved.Keys.Where(name => !selected.Contains(name)).ToArray()) resolved.Remove(name);
            return resolved;
        }

        internal static bool HasEvidence(IEnumerable<string> names) => names.Any(name =>
            UnifiedExpressionRegistry.TryCanonicalize(name, out var canonical) &&
            (UnifiedExpressionRegistry.IsDistinctive(canonical) || UnifiedExpressionRegistry.IsExplicit(name)));

        private static int RawPriority(string name, string canonical) => UnifiedExpressionRegistry.IsExplicit(name) ? 0 :
            string.Equals(name, canonical, StringComparison.OrdinalIgnoreCase) ? 1 : 2;

        internal static byte[] Add(byte[] bytes, ICollection<string> warnings = null)
        {
            var glb = GlbDocument.Read(bytes);
            var nodes = Array(glb.Json, "nodes");
            var meshes = Array(glb.Json, "meshes");
            var vrm = Object(Object(glb.Json, "extensions"), "VRMC_vrm");
            if (nodes == null || meshes == null || vrm == null)
                throw new InvalidOperationException("Unified Expressions の書き出しに必要な VRM 情報がありません。");
            var expressions = Object(vrm, "expressions");
            var custom = Object(expressions, "custom");
            // A face may divide its eyes and mouth between separate meshes.
            // Shared ARKit names join UE only after the avatar establishes UE.
            var referencedMeshes = nodes.OfType<Dictionary<string, object>>()
                .Where(node => node.ContainsKey("mesh")).Select(node => Index(node["mesh"], meshes.Count, "mesh")).Distinct().ToArray();
            var supportsUnified = HasEvidence(referencedMeshes.SelectMany(index =>
                Array(Object(meshes[index] as Dictionary<string, object>, "extras"), "targetNames") ?? new List<object>()).OfType<string>()
                .Concat(custom?.Keys ?? Enumerable.Empty<string>()));
            if (!supportsUnified) return bytes;
            var authoredKeys = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var retainedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in custom?.Keys ?? Enumerable.Empty<string>())
            {
                if (!UnifiedExpressionRegistry.TryCanonicalize(key, out var canonical)) continue;
                if (!authoredKeys.TryGetValue(canonical, out var keys)) authoredKeys.Add(canonical, keys = new List<string>());
                keys.Add(key);
                if (PreserveAuthored(custom[key], nodes, meshes, Array(glb.Json, "materials"), key, warnings)) retainedKeys.Add(key);
            }
            var authoredCoverage = AuthoredMorphCoverage(custom, retainedKeys, nodes, meshes);
            var bindings = new Dictionary<string, List<object>>(StringComparer.Ordinal);
            var meshCandidates = new Dictionary<int, Dictionary<string, int>>();
            for (var nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
            {
                if (!(nodes[nodeIndex] is Dictionary<string, object> node) || !node.TryGetValue("mesh", out var rawMesh)) continue;
                var meshIndex = Index(rawMesh, meshes.Count, "mesh");
                var mesh = meshes[meshIndex] as Dictionary<string, object>;
                if (!meshCandidates.TryGetValue(meshIndex, out var candidates))
                {
                    var names = Array(Object(mesh, "extras"), "targetNames");
                    candidates = names == null ? new Dictionary<string, int>() :
                        Resolve(names.Select(name => name as string).ToArray(), warnings, "mesh " + meshIndex, supportsUnified,
                            authoredCoverage.TryGetValue(meshIndex, out var coverage) ? coverage : null);
                    meshCandidates.Add(meshIndex, candidates);
                }
                foreach (var pair in candidates)
                {
                    RequireTarget(mesh, pair.Value, pair.Key);
                    if (!bindings.TryGetValue(pair.Key, out var binds)) bindings.Add(pair.Key, binds = new List<object>());
                    binds.Add(new Dictionary<string, object> {
                        ["node"] = (long)nodeIndex, ["index"] = (long)pair.Value, ["weight"] = 1.0
                    });
                }
            }
            if (bindings.Count == 0) return bytes;
            if (expressions == null) vrm["expressions"] = expressions = new Dictionary<string, object>();
            if (custom == null) expressions["custom"] = custom = new Dictionary<string, object>();
            var added = 0;
            foreach (var pair in bindings)
            {
                var key = UnifiedExpressionRegistry.Prefix + pair.Key;
                if (authoredKeys.TryGetValue(pair.Key, out var existingKeys))
                {
                    // A declared nonempty route, including an intentional zero
                    // weight, is authoritative. Empty bindings can be repaired.
                    var retained = existingKeys.Where(retainedKeys.Contains).ToArray();
                    if (retained.Length > 1)
                        Warn(warnings, "Unified Expressions の手動設定名が重複するため自動設定を省略しました: " + pair.Key);
                    if (retained.Length > 0) continue;
                    foreach (var existingKey in existingKeys) custom.Remove(existingKey);
                }
                custom.Add(key, new Dictionary<string, object> {
                    ["morphTargetBinds"] = pair.Value, ["isBinary"] = false,
                    ["overrideBlink"] = "none", ["overrideMouth"] = "none", ["overrideLookAt"] = "none"
                });
                added++;
            }
            if (added == 0) return bytes;
            Warn(warnings, "Unified Expressions の追跡表情を " + added + " 項目登録しました。端末で検出できる動きだけを反映します。");
            return glb.Write();
        }

        private static Dictionary<int, HashSet<string>> AuthoredMorphCoverage(Dictionary<string, object> custom,
            IEnumerable<string> retainedKeys, List<object> nodes, List<object> meshes)
        {
            var result = new Dictionary<int, HashSet<string>>();
            foreach (var key in retainedKeys)
            {
                UnifiedExpressionRegistry.TryCanonicalize(key, out var canonical);
                foreach (var item in Array(custom[key] as Dictionary<string, object>, "morphTargetBinds") ?? new List<object>())
                {
                    try
                    {
                        if (!(item is Dictionary<string, object> bind) || !bind.TryGetValue("node", out var rawNode)) continue;
                        var node = nodes[Index(rawNode, nodes.Count, "node")] as Dictionary<string, object>;
                        if (node == null || !node.TryGetValue("mesh", out var rawMesh)) continue;
                        var meshIndex = Index(rawMesh, meshes.Count, "mesh");
                        // A known mesh reserves declared coverage even when
                        // index/weight is malformed. Validation diagnoses it;
                        // an overlapping raw route must not bypass that choice.
                        if (!result.TryGetValue(meshIndex, out var coverage)) result.Add(meshIndex, coverage = new HashSet<string>(StringComparer.Ordinal));
                        coverage.Add(canonical);
                    }
                    catch (InvalidOperationException) { /* PreserveAuthored already diagnoses malformed references. */ }
                }
            }
            return result;
        }

        private static bool PreserveAuthored(object value, List<object> nodes, List<object> meshes,
            List<object> materials, string key, ICollection<string> warnings)
        {
            try
            {
                if (!(value is Dictionary<string, object> expression)) throw new InvalidOperationException("expression が不正です");
                var morphs = DeclaredBindings(expression, "morphTargetBinds");
                var colors = DeclaredBindings(expression, "materialColorBinds");
                var textures = DeclaredBindings(expression, "textureTransformBinds");
                if (morphs.Count + colors.Count + textures.Count == 0) return false;
                foreach (var item in morphs)
                {
                    if (!(item is Dictionary<string, object> bind) || !bind.TryGetValue("node", out var rawNode) ||
                        !bind.TryGetValue("index", out var rawIndex) || !bind.TryGetValue("weight", out var rawWeight))
                        throw new InvalidOperationException("morphTargetBinds が不正です");
                    var node = nodes[Index(rawNode, nodes.Count, "node")] as Dictionary<string, object>;
                    if (node == null || !node.TryGetValue("mesh", out var rawMesh)) throw new InvalidOperationException("node に mesh がありません");
                    RequireTarget(meshes[Index(rawMesh, meshes.Count, "mesh")] as Dictionary<string, object>, Index(rawIndex, int.MaxValue, "morph"), key);
                    var weight = Number(rawWeight);
                    if (weight < 0 || weight > 1) throw new InvalidOperationException("weight が範囲外です");
                }
                foreach (var item in colors)
                {
                    var bind = MaterialBinding(item, materials);
                    if (!bind.TryGetValue("type", out var type) || !(type is string colorType) ||
                        !new[] { "color", "emissionColor", "shadeColor", "matcapColor", "rimColor", "outlineColor" }.Contains(colorType))
                        throw new InvalidOperationException("materialColorBinds.type が不正です");
                    RequireVector(bind, "targetValue", 4);
                }
                foreach (var item in textures)
                {
                    var bind = MaterialBinding(item, materials);
                    RequireVector(bind, "scale", 2);
                    RequireVector(bind, "offset", 2);
                }
                return true;
            }
            catch (InvalidOperationException error)
            {
                Warn(warnings, "既存の Unified Expressions 設定を保持し、自動設定を省略しました: " + key + " (" + error.Message + ")");
                return true;
            }
        }

        private static List<object> DeclaredBindings(Dictionary<string, object> expression, string key)
        {
            if (!expression.TryGetValue(key, out var raw)) return new List<object>();
            if (raw is List<object> bindings) return bindings;
            throw new InvalidOperationException(key + " が配列ではありません");
        }
        private static Dictionary<string, object> MaterialBinding(object item, List<object> materials)
        {
            if (!(item is Dictionary<string, object> bind) || !bind.TryGetValue("material", out var rawMaterial))
                throw new InvalidOperationException("material binding が不正です");
            Index(rawMaterial, materials?.Count ?? 0, "material");
            return bind;
        }
        private static void RequireVector(Dictionary<string, object> bind, string key, int length)
        {
            var vector = Array(bind, key);
            if (vector == null || vector.Count != length) throw new InvalidOperationException(key + " が不正です");
            foreach (var value in vector) Number(value);
        }
        private static double Number(object value)
        {
            if (!(value is long) && !(value is double)) throw new InvalidOperationException("値が数値ではありません");
            var number = Convert.ToDouble(value);
            if (double.IsNaN(number) || double.IsInfinity(number)) throw new InvalidOperationException("値が有限ではありません");
            return number;
        }

        private static void RequireTarget(Dictionary<string, object> mesh, int index, string name)
        {
            var primitives = Array(mesh, "primitives");
            if (primitives == null || primitives.Count == 0)
                throw new InvalidOperationException("Unified Expressions の出力 mesh に primitive がありません: " + name);
            foreach (var primitive in primitives)
            {
                var targets = Array(primitive as Dictionary<string, object>, "targets");
                if (targets == null || index >= targets.Count)
                    throw new InvalidOperationException("Unified Expressions の morph target 参照が不正です: " + name);
            }
        }

        private static int Index(object value, int count, string label)
        {
            if (!(value is long index) || index < 0 || index >= count)
                throw new InvalidOperationException("Invalid Unified Expressions " + label + " index.");
            return (int)index;
        }
        private static Dictionary<string, object> Object(Dictionary<string, object> parent, string key) =>
            parent != null && parent.TryGetValue(key, out var value) ? value as Dictionary<string, object> : null;
        private static List<object> Array(Dictionary<string, object> parent, string key) =>
            parent != null && parent.TryGetValue(key, out var value) ? value as List<object> : null;
        private static void Warn(ICollection<string> warnings, string message)
        {
            if (warnings != null && !warnings.Contains(message)) warnings.Add(message);
        }
    }
}
