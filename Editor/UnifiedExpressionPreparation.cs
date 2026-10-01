using System;
using System.Collections.Generic;
using System.Linq;
using UniVRM10;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // Preserve required morph identity across authoring passes. Names on another
    // renderer cannot conceal loss of the tracked face or a selected endpoint.
    internal sealed class UnifiedExpressionPreparation
    {
        private readonly Dictionary<SkinnedMeshRenderer, string[]> required = new Dictionary<SkinnedMeshRenderer, string[]>();

        internal UnifiedExpressionPreparation(GameObject clone)
        {
            var meshes = ExportRendererSelection.Enumerate(clone).OfType<SkinnedMeshRenderer>()
                .Where(skin => skin.sharedMesh != null).ToDictionary(skin => skin, skin =>
                    Enumerable.Range(0, skin.sharedMesh.blendShapeCount).Select(skin.sharedMesh.GetBlendShapeName).ToArray());
            var authoredNames = clone.GetComponent<Vrm10Instance>()?.Vrm?.Expression?.CustomClips?.Where(clip => clip != null)
                .Select(clip => clip.name) ?? Enumerable.Empty<string>();
            var supportsUnified = VrmUnifiedExpressions.HasEvidence(meshes.Values.SelectMany(names => names).Concat(authoredNames));
            foreach (var pair in meshes)
            {
                var candidates = VrmUnifiedExpressions.Resolve(pair.Value, avatarSupportsUnified: supportsUnified);
                if (candidates.Count > 0) required.Add(pair.Key, candidates.Values.Select(index => pair.Value[index]).ToArray());
            }
        }

        internal void Verify()
        {
            foreach (var pair in required)
            {
                var skin = pair.Key;
                if (skin == null || !skin.enabled || !skin.gameObject.activeInHierarchy || skin.sharedMesh == null ||
                    pair.Value.Any(name => skin.sharedMesh.GetBlendShapeIndex(name) < 0))
                    throw new InvalidOperationException("Modular Avatar / NDMF の処理で Unified Expressions の追跡用変形が失われました。メッシュや BlendShape を変更する追加ツールの設定を確認してください。");
            }
        }
    }
}
