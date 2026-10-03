using System;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // This marker exists only on the disposable export copy, between expression
    // baking and optimizer completion. Nonserialized routes keep original
    // identities intact while AAO remaps the clone's serialized references.
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    internal sealed class ExportOptimizationMarker : MonoBehaviour
    {
        internal static bool AvatarOptimizerAdapterAvailable;
        [NonSerialized] internal OptimizationMorphRoute[] Morphs = Array.Empty<OptimizationMorphRoute>();
        [NonSerialized] internal OptimizationMaterialRoute[] Materials = Array.Empty<OptimizationMaterialRoute>();
        [NonSerialized] internal Component[] Dependencies = Array.Empty<Component>();
        [NonSerialized] internal OptimizationPropertyMutation[] PropertyMutations = Array.Empty<OptimizationPropertyMutation>();
        [NonSerialized] internal bool MappingApplied;
        [NonSerialized] internal string MappingError;
    }

    internal sealed class OptimizationPropertyMutation
    {
        internal Renderer Renderer;
        internal string[] Properties = Array.Empty<string>();
    }

    internal sealed class OptimizationMorphRoute
    {
        internal int SourceRendererId;
        internal string SourceShape;
        internal string Label;
        internal SkinnedMeshRenderer Renderer;
        internal string Shape;
        internal bool Removed;
        internal bool RequireUsableEndpoint;
        internal bool NoOp;
    }

    internal sealed class OptimizationMaterialRoute
    {
        internal const string SlotPrefix = "m_Materials.Array.data[";
        internal int SourceRendererId;
        internal int SourceSlot;
        internal Renderer Renderer;
        internal int Slot;
        internal string Label;
        internal bool Removed;
        internal string[] Mutations = Array.Empty<string>();
        internal string SlotProperty => SlotPrefix + Slot + "]";

        internal static bool TryParseSlot(string property, out int slot)
        {
            slot = -1;
            return property != null && property.StartsWith(SlotPrefix, StringComparison.Ordinal) &&
                property.EndsWith("]", StringComparison.Ordinal) &&
                int.TryParse(property.Substring(SlotPrefix.Length, property.Length - SlotPrefix.Length - 1), out slot) && slot >= 0;
        }
    }
}
