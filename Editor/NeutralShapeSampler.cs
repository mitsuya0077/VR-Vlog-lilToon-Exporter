using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UniVRM10;
using UnityEditor;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // A valid runtime FX graph need not have a single fixed neutral pose.
    // These capability limits keep the prepared authored weights available;
    // malformed data and unresolved target identities remain fatal.
    internal sealed class NeutralShapeSamplingException : InvalidOperationException
    {
        internal NeutralShapeSamplingException(string message, Exception inner = null) : base(message, inner) { }
    }

    internal static class NeutralShapeSampler
    {
        internal static List<VrChatExpressionMenu.MorphValue> Sample(GameObject prepared, Func<string, bool> excludedPath = null,
            ICollection<string> warnings = null)
        {
            if (prepared == null) throw new ArgumentNullException(nameof(prepared));
            // This reads the prepared FX and parameter defaults without walking
            // a large expression menu or reusing a pre-NDMF controller.
            var metadata = VrChatExpressionMenu.Read(prepared, new VrChatMenuImportPolicy { SkipAll = true });
            if (metadata.Controller == null) return new List<VrChatExpressionMenu.MorphValue>();
            var automatic = AutomaticChannels(prepared);
            var completed = new HashSet<string>(StringComparer.Ordinal);
            var warned = new HashSet<string>(StringComparer.Ordinal);
            var values = new Dictionary<(string Path, string Shape), VrChatExpressionMenu.MorphValue>();
            void RetainPreparedWeights(NeutralShapeSamplingException error, IEnumerable<EditorCurveBinding> bindings)
            {
                var message = "FXの初期表情を固定できない部分は、アバターに設定されたシェイプキーの値を使って書き出します。" +
                    "実行時に動く表情は初期の形へ固定しません。 " + WithAffected(error, bindings).Message;
                if (!warned.Add(message)) return;
                if (warnings != null) warnings.Add(message);
                else Debug.LogWarning(message);
            }
            IEnumerable<EditorCurveBinding> AllMorphBindings() => metadata.Controller.animationClips.SelectMany(AnimationUtility.GetCurveBindings)
                .Where(binding => binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal) &&
                    excludedPath?.Invoke(binding.path) != true);
            HashSet<EditorCurveBinding>[] groups;
            try { groups = ExpressionDependencies.NeutralRoots(metadata.Controller, excludedPath, metadata, automatic).ToArray(); }
            catch (NeutralShapeSamplingException error)
            {
                // A callback with unknown effects cannot establish independent
                // groups. Preserve the complete prepared appearance in this case.
                RetainPreparedWeights(error, AllMorphBindings());
                return new List<VrChatExpressionMenu.MorphValue>();
            }
            catch (InvalidOperationException error)
            {
                throw WithAffected(error, AllMorphBindings());
            }
            foreach (var roots in groups)
            {
                ExpressionDependencies dependencies;
                try { dependencies = ExpressionDependencies.AnalyzeNeutral(metadata.Controller, roots, excludedPath, metadata, automatic); }
                catch (NeutralShapeSamplingException) when (roots.All(automatic.Contains)) { continue; }
                catch (NeutralShapeSamplingException error) { RetainPreparedWeights(error, roots); continue; }
                catch (InvalidOperationException error) { throw WithAffected(error, roots); }
                var identity = string.Join(",", dependencies.Layers.OrderBy(index => index)) + "|" +
                    string.Join(",", dependencies.NativeSupportLayers.OrderBy(index => index));
                if (!completed.Add(identity)) continue;
                List<VrChatExpressionMenu.MorphValue> sampled;
                try { sampled = VrChatExpressionSampler.SampleNeutral(prepared, metadata.Controller, dependencies, metadata, excludedPath); }
                catch (NeutralShapeSamplingException) when (dependencies.Morphs.All(automatic.Contains)) { continue; }
                catch (NeutralShapeSamplingException error) { RetainPreparedWeights(error, dependencies.Morphs); continue; }
                catch (InvalidOperationException error) { throw WithAffected(error, dependencies.Morphs); }
                foreach (var value in sampled)
                {
                    var key = (value.Path, value.Shape);
                    if (values.TryGetValue(key, out var previous) && Math.Abs(previous.Weight - value.Weight) > .01f)
                        throw new InvalidOperationException("FXの初期表情を一意に確定できません: " + value.Path + " / " + value.Shape);
                    values[key] = value;
                }
            }
            return values.Values.OrderBy(value => value.Path, StringComparer.Ordinal)
                .ThenBy(value => value.Shape, StringComparer.Ordinal).ToList();
        }

        private static InvalidOperationException WithAffected(InvalidOperationException error, IEnumerable<EditorCurveBinding> bindings)
        {
            var targets = bindings.Select(binding => binding.path + " / " + binding.propertyName.Substring("blendShape.".Length))
                .Distinct().OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var affected = string.Join(", ", targets.Take(16));
            if (targets.Length > 16) affected += " ほか" + (targets.Length - 16) + "件";
            var message = "FXの初期表情を確定できません（対象: " + affected + "）: " + error.Message;
            return error is NeutralShapeSamplingException ? new NeutralShapeSamplingException(message, error) :
                new InvalidOperationException(message, error);
        }

        private static HashSet<EditorCurveBinding> AutomaticChannels(GameObject avatar)
        {
            var result = new HashSet<EditorCurveBinding>();
            void Add(SkinnedMeshRenderer renderer, string shape)
            {
                if (renderer == null || renderer.sharedMesh == null || string.IsNullOrEmpty(shape) ||
                    renderer.sharedMesh.GetBlendShapeIndex(shape) < 0 || !renderer.transform.IsChildOf(avatar.transform)) return;
                result.Add(EditorCurveBinding.FloatCurve(AnimationUtility.CalculateTransformPath(renderer.transform, avatar.transform),
                    typeof(SkinnedMeshRenderer), "blendShape." + shape));
            }
            foreach (var renderer in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh == null) continue;
                var names = Enumerable.Range(0, renderer.sharedMesh.blendShapeCount).Select(renderer.sharedMesh.GetBlendShapeName).ToArray();
                foreach (var index in BlinkShapeNames.Resolve(names, allowPartial: true).Where(index => index >= 0)) Add(renderer, names[index]);
                foreach (var name in names.Where(name => name.StartsWith("vrc.v.", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("vrc.v_", StringComparison.OrdinalIgnoreCase))) Add(renderer, name);
            }
            if (BlinkExportSession.TryDescriptor(avatar, null, out var blink)) Add(blink.Renderer, blink.Shape);
            var descriptor = avatar.GetComponents<Component>().FirstOrDefault(component => component != null &&
                component.GetType().FullName == "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            var visemeRenderer = VrChatExpressionMenu.Member(descriptor, "VisemeSkinnedMesh") as SkinnedMeshRenderer;
            if (VrChatExpressionMenu.Member(descriptor, "VisemeBlendShapes") is IEnumerable visemes)
                foreach (var shape in visemes.OfType<string>()) Add(visemeRenderer, shape);
            Add(visemeRenderer, VrChatExpressionMenu.Member(descriptor, "MouthOpenBlendShapeName") as string);
            var expressions = avatar.GetComponent<Vrm10Instance>()?.Vrm?.Expression;
            foreach (var clip in new[] { expressions?.Blink, expressions?.BlinkLeft, expressions?.BlinkRight })
            {
                if (clip == null) continue;
                foreach (var binding in clip.MorphTargetBindings ?? Array.Empty<MorphTargetBinding>())
                {
                    var target = string.IsNullOrEmpty(binding.RelativePath) ? avatar.transform : avatar.transform.Find(binding.RelativePath);
                    var renderer = target == null ? null : target.GetComponent<SkinnedMeshRenderer>();
                    if (renderer != null && renderer.sharedMesh != null && binding.Index >= 0 && binding.Index < renderer.sharedMesh.blendShapeCount)
                        Add(renderer, renderer.sharedMesh.GetBlendShapeName(binding.Index));
                }
            }
            return result;
        }
    }
}
