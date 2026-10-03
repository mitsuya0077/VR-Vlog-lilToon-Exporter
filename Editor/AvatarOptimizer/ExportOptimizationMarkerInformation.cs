using System;
using Anatawa12.AvatarOptimizer.API;
using UnityEditor;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    [InitializeOnLoad]
    internal static class ExportAvatarOptimizerAdapter
    {
        static ExportAvatarOptimizerAdapter()
        {
            ExportOptimizationMarker.AvatarOptimizerAdapterAvailable = true;
        }
    }

    [ComponentInformation(typeof(ExportOptimizationMarker))]
    internal sealed class ExportOptimizationMarkerInformation : ComponentInformation<ExportOptimizationMarker>
    {
        protected override void CollectDependency(ExportOptimizationMarker component, ComponentDependencyCollector collector)
        {
            collector.MarkEntrypoint();
            collector.MarkBehaviour();
            collector.AddDependency(component.transform, component);
            foreach (var dependency in component.Dependencies)
                if (dependency != null) collector.AddDependency(dependency);
            foreach (var route in component.Morphs)
                if (route.Renderer != null) collector.AddDependency(route.Renderer);
            foreach (var route in component.Materials)
                if (route.Renderer != null) collector.AddDependency(route.Renderer);
        }

        protected override void CollectMutations(ExportOptimizationMarker component, ComponentMutationsCollector collector)
        {
            foreach (var route in component.Morphs)
                if (route.Renderer != null && !route.Removed)
                    collector.ModifyProperties(route.Renderer, "blendShape." + route.Shape);
            foreach (var route in component.Materials)
                if (route.Renderer != null && !route.Removed)
                {
                    collector.ModifyProperties(route.Renderer, route.SlotProperty);
                    collector.ModifyProperties(route.Renderer, route.Mutations);
                }
            foreach (var mutation in component.PropertyMutations)
                if (mutation.Renderer != null) collector.ModifyProperties(mutation.Renderer, mutation.Properties);
        }

        protected override void ApplySpecialMapping(ExportOptimizationMarker component, MappingSource mappingSource)
        {
            // A destroyed Unity component still has its original managed
            // reference. MappingSource explicitly accepts missing components.
            foreach (var route in component.Morphs)
            {
                if (route.Removed || ReferenceEquals(route.Renderer, null)) continue;
                var mapped = mappingSource.GetMappedComponent(route.Renderer);
                if (!mapped.TryMapProperty("blendShape." + route.Shape, out var property))
                {
                    route.Removed = true;
                    continue;
                }
                if (!(property.Component is SkinnedMeshRenderer renderer) || property.Property == null ||
                    !property.Property.StartsWith("blendShape.", StringComparison.Ordinal))
                {
                    component.MappingError = "BlendShape の移動先を解決できません: " + route.Label;
                    continue;
                }
                route.Renderer = renderer;
                route.Shape = property.Property.Substring("blendShape.".Length);
            }
            foreach (var route in component.Materials)
            {
                if (route.Removed || ReferenceEquals(route.Renderer, null)) continue;
                var mapped = mappingSource.GetMappedComponent(route.Renderer);
                if (!mapped.TryMapProperty(route.SlotProperty, out var property))
                {
                    route.Removed = true;
                    continue;
                }
                if (!(property.Component is Renderer renderer) || !OptimizationMaterialRoute.TryParseSlot(property.Property, out var slot))
                {
                    component.MappingError = "マテリアルの移動先を解決できません: " + route.Label;
                    continue;
                }
                route.Renderer = renderer;
                route.Slot = slot;
            }
            component.MappingApplied = true;
        }
    }
}
