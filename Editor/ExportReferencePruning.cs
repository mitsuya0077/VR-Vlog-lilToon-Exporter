using System;
using System.Collections.Generic;
using System.Linq;
using UniVRM10;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter
{
    // UniVRM follows component references and shared VRM settings after reading
    // the hierarchy. Remove references to excluded clone objects before deleting
    // them, and copy every ScriptableObject whose serialized settings we change.
    internal static class ExportReferencePruning
    {
        internal static void Prepare(GameObject clone, IList<Transform> removedRoots,
            ICollection<Object> temporaryAssets, ICollection<string> warnings)
        {
            bool Removed(Transform value) => value != null && removedRoots.Any(root => value == root || value.IsChildOf(root));
            bool RemovedComponent(Component value) => value == null || Removed(value.transform);
            void RequireLocal(Component value)
            {
                if (value != null && !value.transform.IsChildOf(clone.transform))
                    throw new InvalidOperationException("除外処理中にアバター外へのVRM参照が見つかりました: " + value.name + "。参照先をアバター内へ設定してから書き出してください。");
            }
            // Only a root Vrm10Instance is serialized by Vrm10Exporter.
            var instance = clone.GetComponent<Vrm10Instance>();
            if (instance == null) return;
            var removedPaths = new HashSet<string>(clone.GetComponentsInChildren<Transform>(true)
                .GroupBy(t => AnimationUtility.CalculateTransformPath(t, clone.transform), StringComparer.Ordinal)
                .Where(group => group.All(Removed)).Select(group => group.Key), StringComparer.Ordinal);
            bool RemovedPath(string path) => path != null && removedPaths.Contains(path);
            foreach (var constraint in clone.GetComponentsInChildren<MonoBehaviour>(true).OfType<IVrm10Constraint>().ToArray())
            {
                if (Removed(constraint.ConstraintTarget)) continue;
                RequireLocal(constraint.ConstraintSource);
                if (!Removed(constraint.ConstraintSource)) continue;
                // The exported transform keeps its current local pose.
                var component = constraint as Component;
                warnings?.Add(component.name + ": 除外したオブジェクトを参照するVRM拘束を省略し、現在の姿勢を保存しました。");
                Object.DestroyImmediate(component);
            }

            var springBone = instance.SpringBone;
            if (springBone != null)
            {
                // Group components on the clone own their lists; never edit an
                // external group that Unity preserved as a shared reference.
                foreach (var group in springBone.ColliderGroups) RequireLocal(group);
                foreach (var spring in springBone.Springs)
                {
                    if (spring == null) continue;
                    foreach (var joint in spring.Joints) RequireLocal(joint);
                    foreach (var group in spring.ColliderGroups) RequireLocal(group);
                    RequireLocal(spring.Center);
                }
                var groups = springBone.ColliderGroups.Concat(springBone.Springs.Where(s => s != null).SelectMany(s => s.ColliderGroups))
                    .Where(group => !RemovedComponent(group)).Distinct().ToArray();
                foreach (var group in groups)
                {
                    foreach (var collider in group.Colliders) RequireLocal(collider);
                    group.Colliders.RemoveAll(collider => RemovedComponent(collider));
                }
                springBone.ColliderGroups.RemoveAll(group => RemovedComponent(group));
                foreach (var spring in springBone.Springs)
                {
                    if (spring == null) continue;
                    spring.Joints.RemoveAll(joint => RemovedComponent(joint));
                    spring.ColliderGroups.RemoveAll(group => RemovedComponent(group));
                    if (Removed(spring.Center)) spring.Center = null;
                }
                var removedSprings = springBone.Springs.RemoveAll(spring => spring == null || spring.Joints.Count == 0);
                if (removedSprings > 0) warnings?.Add("除外したオブジェクトのVRM揺れ物設定を " + removedSprings + " 件省略しました。");
            }
            if (Removed(instance.LookAtTarget)) instance.LookAtTarget = null;

            var sourceSettings = instance.Vrm;
            if (sourceSettings == null) return;
            var excludedMaterials = new HashSet<string>(StringComparer.Ordinal);
            var retainedMaterials = new HashSet<string>(StringComparer.Ordinal);
            foreach (var renderer in clone.GetComponentsInChildren<Renderer>(true))
                foreach (var material in renderer.sharedMaterials)
                    if (material != null)
                    {
                        if (Removed(renderer.transform)) excludedMaterials.Add(material.name);
                        else if (renderer.enabled && renderer.gameObject.activeInHierarchy) retainedMaterials.Add(material.name);
                    }
            excludedMaterials.ExceptWith(retainedMaterials);

            var firstPersonChanged = sourceSettings.FirstPerson != null && sourceSettings.FirstPerson.Renderers.Any(flag => RemovedPath(flag.Renderer));
            var replacements = new Dictionary<VRM10Expression, VRM10Expression>();
            if (sourceSettings.Expression != null)
                foreach (var entry in sourceSettings.Expression.Clips)
                {
                    var expression = entry.Clip;
                    if (replacements.ContainsKey(expression)) continue;
                    var morphs = expression.MorphTargetBindings.Where(binding => !RemovedPath(binding.RelativePath)).ToArray();
                    var colors = expression.MaterialColorBindings.Where(binding => !excludedMaterials.Contains(binding.MaterialName)).ToArray();
                    var uv = expression.MaterialUVBindings.Where(binding => !excludedMaterials.Contains(binding.MaterialName)).ToArray();
                    if (morphs.Length == expression.MorphTargetBindings.Length && colors.Length == expression.MaterialColorBindings.Length &&
                        uv.Length == expression.MaterialUVBindings.Length) continue;
                    var copy = Object.Instantiate(expression);
                    temporaryAssets.Add(copy);
                    copy.name = expression.name;
                    copy.MorphTargetBindings = morphs;
                    copy.MaterialColorBindings = colors;
                    copy.MaterialUVBindings = uv;
                    replacements.Add(expression, copy);
                }
            if (!firstPersonChanged && replacements.Count == 0) return;
            var settings = Object.Instantiate(sourceSettings);
            temporaryAssets.Add(settings);
            settings.name = sourceSettings.name;
            if (firstPersonChanged) settings.FirstPerson.Renderers.RemoveAll(flag => RemovedPath(flag.Renderer));
            if (sourceSettings.Expression != null && replacements.Count > 0)
            {
                // Rebuild the container to replace every use of a shared clip,
                // including one clip assigned to multiple preset slots.
                settings.Expression = new VRM10ObjectExpression();
                foreach (var entry in sourceSettings.Expression.Clips)
                    settings.Expression.AddClip(entry.Preset, replacements.TryGetValue(entry.Clip, out var copy) ? copy : entry.Clip);
            }
            instance.Vrm = settings;
            warnings?.Add("除外したオブジェクトのVRM一人称・表情参照を出力用コピーから省略しました。");
        }
    }
}
