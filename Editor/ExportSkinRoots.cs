using System;
using System.Collections.Generic;
using System.IO;

namespace VRVlog.LilToonExporter
{
    internal static class ExportSkinRoots
    {
        // Unity rootBone also selects a bounds/implicit-skinning anchor and can
        // be a sibling of the joints (lilToon's AutoAnchorObject does this).
        // glTF skin.skeleton is only ancestry metadata. Repair the JSON after
        // export; changing the Unity rootBone would change zero-weight geometry.
        internal static byte[] Repair(byte[] exported, ICollection<string> warnings = null)
        {
            var document = GlbDocument.Read(exported);
            if (!document.Json.TryGetValue("skins", out var rawSkins) || !(rawSkins is List<object> skins) || skins.Count == 0)
                return exported;
            if (!document.Json.TryGetValue("nodes", out var rawNodes) || !(rawNodes is List<object> nodes))
                throw new InvalidDataException("スキンの参照先ノードがありません。");
            var parents = new int[nodes.Count];
            for (var i = 0; i < parents.Length; i++) parents[i] = -1;
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = Object(nodes[i]);
                if (!node.TryGetValue("children", out var rawChildren)) continue;
                if (!(rawChildren is List<object> children)) throw new InvalidDataException("ノードの子リストが不正です。");
                foreach (var rawChild in children)
                {
                    var child = Index(rawChild, nodes.Count);
                    if (parents[child] != -1) throw new InvalidDataException("同じノードに複数の親があります。");
                    parents[child] = i;
                }
            }
            ValidateTree(parents);
            var changed = false;
            for (var i = 0; i < skins.Count; i++)
            {
                var skin = Object(skins[i]);
                if (!skin.TryGetValue("skeleton", out var rawSkeleton)) continue;
                var skeleton = Index(rawSkeleton, nodes.Count);
                if (!skin.TryGetValue("joints", out var rawJoints) || !(rawJoints is List<object> joints) || joints.Count == 0)
                    throw new InvalidDataException("スキンのジョイントがありません。");
                var jointIndices = new List<int>(joints.Count);
                var valid = true;
                foreach (var joint in joints)
                {
                    var index = Index(joint, nodes.Count);
                    jointIndices.Add(index);
                    if (!IsAncestor(skeleton, index, parents)) valid = false;
                }
                if (valid) continue;
                var common = jointIndices[0];
                for (var j = 1; j < jointIndices.Count && common != -1; j++)
                    while (common != -1 && !IsAncestor(common, jointIndices[j], parents)) common = parents[common];
                if (common >= 0) skin["skeleton"] = (long)common;
                else skin.Remove("skeleton"); // The omitted Unity avatar root may be the only common ancestor.
                changed = true;
                warnings?.Add($"スキン {i}: Unity の境界アンカーを、VRM のジョイント階層に対応するルート情報に変換しました。");
            }
            return changed ? document.Write() : exported;
        }

        private static void ValidateTree(int[] parents)
        {
            var states = new byte[parents.Length];
            for (var start = 0; start < parents.Length; start++)
            {
                if (states[start] != 0) continue;
                var node = start;
                while (node >= 0 && states[node] == 0) { states[node] = 1; node = parents[node]; }
                if (node >= 0 && states[node] == 1) throw new InvalidDataException("ノード階層が循環しています。");
                node = start;
                while (node >= 0 && states[node] == 1) { states[node] = 2; node = parents[node]; }
            }
        }

        private static bool IsAncestor(int ancestor, int node, int[] parents)
        {
            while (node >= 0) { if (node == ancestor) return true; node = parents[node]; }
            return false;
        }

        private static Dictionary<string, object> Object(object value) => value as Dictionary<string, object>
            ?? throw new InvalidDataException("VRM のノードまたはスキンが不正です。");
        private static int Index(object value, int count)
        {
            // JsonDom parses integer tokens as Int64. Accept Int32 as well so
            // programmatically constructed fixtures use the same contract.
            var number = value is long longValue ? longValue : value is int intValue ? intValue : -1;
            if (number < 0 || number >= count) throw new InvalidDataException("VRM のノード参照が範囲外です。");
            return (int)number;
        }
    }
}
