using System;
using System.Collections.Generic;
using System.Linq;
using UniVRM10;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter
{
    // Resolve authoring paths once, before NDMF moves objects. Geometry and rest
    // weights are captured only after the authoring passes finish.
    internal sealed class PreparedExpressionBindings : IDisposable
    {
        internal sealed class Binding
        {
            internal SkinnedMeshRenderer Renderer;
            internal Mesh Mesh;
            internal float[] Weights;
            internal HashSet<string> OriginalShapes;
        }

        private readonly Dictionary<string, Binding> bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);
        private readonly Dictionary<SkinnedMeshRenderer, (string Path, Binding Binding)> originalRenderers =
            new Dictionary<SkinnedMeshRenderer, (string, Binding)>();
        private readonly GameObject root;
        internal GameObject Root => root;
        private readonly Dictionary<Renderer, bool> excludedRenderers = new Dictionary<Renderer, bool>();
        private readonly HashSet<string> excludedAuthoringPaths = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<Object> ownedAssets = new List<Object>();
        private readonly List<AuthoredClip> authoredClips = new List<AuthoredClip>();

        private sealed class AuthoredClip
        {
            internal VRM10Expression Clip;
            internal AuthoredMorph[] Morphs;
        }

        private sealed class AuthoredMorph
        {
            internal MorphTargetBinding Value;
            internal Binding Renderer;
            internal string Shape;
        }

        internal PreparedExpressionBindings(GameObject clone, VrChatExpressionMenu.Source menu, GameObject source = null,
            Func<string, bool> excludedPath = null)
        {
            root = clone;
            foreach (var target in clone.GetComponentsInChildren<Transform>(true))
            {
                var path = AnimationUtility.CalculateTransformPath(target, clone.transform);
                if (excludedPath?.Invoke(path) == true) excludedAuthoringPaths.Add(path);
            }
            foreach (var renderer in clone.GetComponentsInChildren<Renderer>(true))
                excludedRenderers.Add(renderer, excludedPath?.Invoke(AnimationUtility.CalculateTransformPath(renderer.transform, clone.transform)) == true);
            // A FaceEmo underlay can use renderers absent from every authored
            // menu branch. Record all identities before plugins move them.
            foreach (var renderer in clone.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var path = AnimationUtility.CalculateTransformPath(renderer.transform, clone.transform);
                var original = renderer.sharedMesh;
                if (source != null)
                {
                    var target = string.IsNullOrEmpty(path) ? source.transform : source.transform.Find(path);
                    var index = Array.IndexOf(renderer.GetComponents<SkinnedMeshRenderer>(), renderer);
                    var siblings = target == null ? Array.Empty<SkinnedMeshRenderer>() : target.GetComponents<SkinnedMeshRenderer>();
                    if (index >= 0 && index < siblings.Length) original = siblings[index].sharedMesh;
                }
                originalRenderers.Add(renderer, (path, new Binding {
                    Renderer = renderer,
                    OriginalShapes = new HashSet<string>(original == null ? Enumerable.Empty<string>() :
                        Enumerable.Range(0, original.blendShapeCount).Select(original.GetBlendShapeName), StringComparer.Ordinal)
                }));
            }
            foreach (var group in originalRenderers.Values.GroupBy(value => value.Path, StringComparer.Ordinal))
                if (group.Count() == 1) bindings.Add(group.Key, group.Single().Binding);
            foreach (var path in (menu?.Entries ?? new List<VrChatExpressionMenu.Entry>()).Where(e => e.Error == null)
                         .SelectMany(e => e.Values.Select(v => v.Path).Concat(e.Animation.Select(v => v.Path))
                             .Concat(e.Unevaluated.Select(v => v.Path))).Distinct())
            {
                var renderer = VrChatExpressionSampler.FindRenderer(clone, path);
                bindings[path] = originalRenderers[renderer].Binding;
            }
        }

        internal string AuthoringPath(string preparedPath)
        {
            var paths = AuthoringPaths(preparedPath);
            return paths.Length == 1 ? paths[0] : null;
        }

        internal string[] AuthoringPaths(string preparedPath)
        {
            SkinnedMeshRenderer resolved;
            try { resolved = VrChatExpressionSampler.FindRenderer(root, preparedPath ?? ""); }
            catch (InvalidOperationException) { return Array.Empty<string>(); }
            var matches = originalRenderers.Where(pair => pair.Value.Binding.Renderer == resolved && pair.Value.Binding.Renderer.enabled &&
                pair.Value.Binding.Renderer.gameObject.activeInHierarchy && pair.Value.Binding.Renderer.sharedMesh != null &&
                (pair.Value.Binding.Renderer.transform == root.transform || pair.Value.Binding.Renderer.transform.IsChildOf(root.transform)) &&
                string.Equals(AnimationUtility.CalculateTransformPath(pair.Value.Binding.Renderer.transform, root.transform), preparedPath ?? "", StringComparison.Ordinal))
                .Select(pair => pair.Value).ToArray();
            if (matches.Length == 0 || matches.Any(match => originalRenderers.Values.Count(value => value.Path == match.Path) != 1))
                return Array.Empty<string>();
            return matches.Select(match => match.Path).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        }

        internal bool ExcludesPreparedPath(string preparedPath)
        {
            var live = root.GetComponentsInChildren<Renderer>(true).Where(renderer =>
                string.Equals(AnimationUtility.CalculateTransformPath(renderer.transform, root.transform), preparedPath ?? "", StringComparison.Ordinal)).ToArray();
            // A moved included renderer, or a newly generated one, must not
            // inherit exclusion solely by reusing an omitted source path.
            if (live.Length > 0) return live.All(renderer => excludedRenderers.TryGetValue(renderer, out var excluded) && excluded);
            return excludedAuthoringPaths.Contains(preparedPath ?? "");
        }

        internal void RebindPrepared(Func<SkinnedMeshRenderer, SkinnedMeshRenderer> replacement)
        {
            if (replacement == null) throw new ArgumentNullException(nameof(replacement));
            foreach (var pair in originalRenderers)
            {
                var old = pair.Value.Binding.Renderer;
                var current = replacement(old);
                if (current == null || ReferenceEquals(current, old)) continue;
                if (excludedRenderers.TryGetValue(old, out var excluded)) excludedRenderers[current] = excluded;
                pair.Value.Binding.Renderer = current;
            }
        }

        // Capture after pruning/recovery and before NDMF. These are the actual
        // authoring indices; an added/reordered shape must not change their meaning.
        internal void CaptureAuthoredExpressions()
        {
            authoredClips.Clear();
            var vrm = root.GetComponent<Vrm10Instance>()?.Vrm;
            if (vrm?.Expression == null) return;
            foreach (var clip in vrm.Expression.Clips.Select(value => value.Clip).Where(value => value != null).Distinct())
            {
                var morphs = (clip.MorphTargetBindings ?? Array.Empty<MorphTargetBinding>()).Select(value => {
                    SkinnedMeshRenderer resolved;
                    try { resolved = VrChatExpressionSampler.FindRenderer(root, value.RelativePath ?? ""); }
                    catch (InvalidOperationException) { resolved = null; }
                    var target = resolved == null ? null : originalRenderers.Values.Select(item => item.Binding)
                        .SingleOrDefault(item => item.Renderer == resolved);
                    var mesh = target?.Renderer.sharedMesh;
                    return new AuthoredMorph {
                        Value = value,
                        Renderer = target,
                        Shape = mesh != null && value.Index >= 0 && value.Index < mesh.blendShapeCount
                            ? mesh.GetBlendShapeName(value.Index) : null
                    };
                }).ToArray();
                authoredClips.Add(new AuthoredClip { Clip = clip, Morphs = morphs });
            }
        }

        internal void RebindAuthoredExpressions(Func<Object, Object> isolatedCopy)
        {
            if (isolatedCopy == null) throw new ArgumentNullException(nameof(isolatedCopy));
            var instance = root.GetComponent<Vrm10Instance>();
            var vrm = instance?.Vrm;
            if (vrm?.Expression == null) return;
            var currentClips = new HashSet<VRM10Expression>(vrm.Expression.Clips.Select(value => value.Clip));
            var replacements = new Dictionary<VRM10Expression, VRM10Expression>();
            foreach (var captured in authoredClips)
            {
                var current = isolatedCopy(captured.Clip) as VRM10Expression;
                if (current == null || !currentClips.Contains(current)) continue;
                var values = current.MorphTargetBindings ?? Array.Empty<MorphTargetBinding>();
                // A plugin that replaced the binding array owns the new routes.
                // Only unchanged, identity-correlated authoring binds are ours.
                if (values.Length != captured.Morphs.Length) continue;
                var result = values.ToArray();
                var changed = false;
                for (var i = 0; i < values.Length; i++)
                {
                    var old = captured.Morphs[i]; var value = values[i];
                    if (value.RelativePath != old.Value.RelativePath || value.Index != old.Value.Index ||
                        !value.Weight.Equals(old.Value.Weight)) continue;
                    if (old.Shape == null)
                    {
                        // Appended/generated channels must not make an invalid
                        // authoring index suddenly address a real expression.
                        if (value.Index >= 0 && value.Index != int.MaxValue)
                        {
                            result[i] = new MorphTargetBinding(value.RelativePath, int.MaxValue, value.Weight); changed = true;
                        }
                        continue;
                    }
                    var target = old.Renderer?.Renderer;
                    var mesh = target == null ? null : target.sharedMesh;
                    var index = mesh == null ? -1 : mesh.GetBlendShapeIndex(old.Shape);
                    if (target == null || index < 0)
                    {
                        // Preserve disabled/invalid author binds without turning
                        // them into a new usable endpoint or inventing a channel.
                        if (value.Weight == 0 || !NeutralShapeSnapshot.Finite(value.Weight) || value.Weight < 0 || value.Weight > 1) continue;
                        throw new InvalidOperationException(NdmfExportPreparation.UnknownRendererRelocation + " (" +
                            old.Value.RelativePath + " / " + old.Shape + ")");
                    }
                    var path = AnimationUtility.CalculateTransformPath(target.transform, root.transform);
                    bool unique;
                    try { unique = VrChatExpressionSampler.FindRenderer(root, path) == target; }
                    catch (InvalidOperationException) { unique = false; }
                    if (!unique)
                    {
                        if (value.Weight == 0 || !NeutralShapeSnapshot.Finite(value.Weight) || value.Weight < 0 || value.Weight > 1) continue;
                        throw new InvalidOperationException("処理後の表情のRendererパスを一意に確定できません: " + path);
                    }
                    if (path == value.RelativePath && index == value.Index) continue;
                    result[i] = new MorphTargetBinding(path, index, value.Weight); changed = true;
                }
                if (!changed) continue;
                var copy = Object.Instantiate(current); ownedAssets.Add(copy); copy.name = current.name;
                copy.MorphTargetBindings = result;
                replacements.Add(current, copy);
            }
            if (replacements.Count == 0) return;
            var settings = Object.Instantiate(vrm); ownedAssets.Add(settings); settings.name = vrm.name;
            settings.Expression = new VRM10ObjectExpression();
            foreach (var entry in vrm.Expression.Clips)
                settings.Expression.AddClip(entry.Preset, replacements.TryGetValue(entry.Clip, out var copy) ? copy : entry.Clip);
            instance.Vrm = settings;
        }

        internal void Capture(VrChatExpressionMenu.Source menu)
        {
            foreach (var binding in bindings.Values)
            {
                var renderer = binding.Renderer;
                binding.Mesh = null;
                binding.Weights = null;
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.sharedMesh == null) continue;
                binding.Mesh = renderer.sharedMesh;
                binding.Weights = Enumerable.Range(0, binding.Mesh.blendShapeCount).Select(renderer.GetBlendShapeWeight).ToArray();
            }
            foreach (var entry in menu.Entries.Where(e => e.Error == null))
            {
                var missing = new HashSet<(string Path, string Shape)>();
                var unevaluated = new HashSet<(string Path, string Shape)>(entry.Unevaluated.Select(v => (v.Path, v.Shape)));
                foreach (var value in entry.Values.Select(v => (v.Path, v.Shape)).Concat(entry.Animation.Select(v => (v.Path, v.Shape)))
                             .Concat(unevaluated).Distinct())
                {
                    if (!bindings.TryGetValue(value.Path, out var binding) || binding.Mesh == null ||
                        binding.OriginalShapes.Contains(value.Shape) && binding.Mesh.GetBlendShapeIndex(value.Shape) < 0)
                    {
                        entry.Error = "衣装・体形の処理後に表情の対象が残っていないため、この表情を省略しました: " + value.Path + " / " + value.Shape;
                        break;
                    }
                    if (binding.Mesh.GetBlendShapeIndex(value.Shape) < 0) missing.Add(value);
                    else if (unevaluated.Contains(value))
                    {
                        entry.Error = "前処理後に生成されたBlendShapeのメニュー選択値を確定できません: " + value.Path + " / " + value.Shape;
                        break;
                    }
                }
                if (entry.Error != null) continue;
                entry.Values.RemoveAll(v => missing.Contains((v.Path, v.Shape)));
                entry.Animation.RemoveAll(v => missing.Contains((v.Path, v.Shape)));
                entry.Unevaluated.Clear();
                if (missing.Count > 0)
                    entry.Messages.Add("元モデルにも出力モデルにもないBlendShape参照を省略しました: " +
                                       string.Join(", ", missing.OrderBy(v => v.Path, StringComparer.Ordinal).ThenBy(v => v.Shape, StringComparer.Ordinal).Select(v => v.Path + "/" + v.Shape)));
                if (entry.Animation.Count == 0) { entry.Duration = 0; entry.Loop = false; }
                if (entry.Values.Count == 0) entry.Error = "参照を解決した結果、有効な顔のBlendShapeがないため、この表情を省略しました。";
                if (entry.Error == null && entry.AncillaryGeometry != null)
                {
                    try { entry.AncillaryGeometry.Validate(entry, this); }
                    catch (AncillaryExpressionGeometryException error) { entry.Error = error.Message; }
                }
            }
        }

        internal void FilterRemoved(VrChatExpressionMenu.Source menu, ISet<Renderer> removed, ICollection<string> warnings)
        {
            if (menu == null) return;
            bool Excluded(string path) => bindings.TryGetValue(path, out var binding) && removed.Contains(binding.Renderer);
            foreach (var entry in menu.Entries.Where(e => e.Error == null))
            {
                var count = entry.Values.RemoveAll(v => Excluded(v.Path)) + entry.Animation.RemoveAll(v => Excluded(v.Path)) + entry.Unevaluated.RemoveAll(v => Excluded(v.Path));
                if (count == 0) continue;
                if (entry.Values.Count == 0 && entry.Animation.Count == 0 && entry.Unevaluated.Count == 0)
                    entry.Error = "除外した補助Rendererだけを変更する表情のため省略しました。";
                else warnings?.Add(entry.Name + ": 補助Rendererの表情だけを省略し、残る表情を保持しました。");
            }
        }

        internal Binding Get(string path) => bindings[path];

        public void Dispose()
        {
            foreach (var asset in ownedAssets) if (asset != null) Object.DestroyImmediate(asset);
            ownedAssets.Clear();
        }
    }
}
