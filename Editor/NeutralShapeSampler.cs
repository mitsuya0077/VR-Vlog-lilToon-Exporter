using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UniVRM10;
using UnityEditor;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // A valid FX graph may need live inputs or unsupported appearance support
    // to choose its rest pose. Keep such capability limits separate from bad
    // data and unresolved callbacks: only neutral preparation retains authored
    // weights, without accepting sampled phases or partial appearance changes.
    internal sealed class NeutralShapeSamplingException : InvalidOperationException
    {
        internal readonly HashSet<EditorCurveBinding> DependencyMorphs;
        internal readonly HashSet<EditorCurveBinding> CoupledMorphs;
        internal NeutralShapeSamplingException(string message, Exception inner = null, IEnumerable<EditorCurveBinding> dependencyMorphs = null,
            IEnumerable<EditorCurveBinding> coupledMorphs = null) : base(message, inner)
        {
            DependencyMorphs = dependencyMorphs == null ? null : new HashSet<EditorCurveBinding>(dependencyMorphs);
            // Missing coupling metadata grants no new independence exception.
            var connected = coupledMorphs ?? dependencyMorphs;
            CoupledMorphs = connected == null ? null : new HashSet<EditorCurveBinding>(connected);
        }
    }

    internal static class NeutralShapeSampler
    {
        internal static List<VrChatExpressionMenu.MorphValue> Sample(GameObject prepared, Func<string, bool> excludedPath = null,
            ICollection<string> warnings = null, IEnumerable<EditorCurveBinding> requiredMorphs = null)
        {
            if (prepared == null) throw new ArgumentNullException(nameof(prepared));
            // This reads the prepared FX and parameter defaults without walking
            // a large expression menu or reusing a pre-NDMF controller.
            var metadata = VrChatExpressionMenu.Read(prepared, new VrChatMenuImportPolicy { SkipAll = true });
            if (metadata.Controller == null) return new List<VrChatExpressionMenu.MorphValue>();
            NeutralInputProof.Read(prepared, metadata);
            var fixedContext = FixedExpressionContext.Create(metadata.Controller, metadata.Defaults, metadata);
            var automatic = AutomaticChannels(prepared);
            var completed = new HashSet<string>(StringComparer.Ordinal);
            var values = new Dictionary<(string Path, string Shape), VrChatExpressionMenu.MorphValue>();
            var preserved = new HashSet<EditorCurveBinding>();
            var reported = new HashSet<string>(StringComparer.Ordinal);
            var coupledPreserved = new HashSet<EditorCurveBinding>();
            var independent = new HashSet<EditorCurveBinding>();
            var refusals = new List<(HashSet<EditorCurveBinding> Roots, NeutralShapeSamplingException Error)>();
            void Preserve(IEnumerable<EditorCurveBinding> bindings, NeutralShapeSamplingException error)
            {
                var roots = new HashSet<EditorCurveBinding>(bindings);
                coupledPreserved.UnionWith(roots);
                if (error.CoupledMorphs != null) coupledPreserved.UnionWith(error.CoupledMorphs);
                preserved.UnionWith(roots);
                if (error.DependencyMorphs != null) preserved.UnionWith(error.DependencyMorphs);
                if (error.CoupledMorphs != null) preserved.UnionWith(error.CoupledMorphs);
                refusals.Add((roots, error));
            }
            HashSet<EditorCurveBinding>[] groups;
            try { groups = ExpressionDependencies.NeutralRoots(metadata.Controller, excludedPath, metadata, automatic, fixedContext).ToArray(); }
            catch (InvalidOperationException error)
            {
                var bindings = metadata.Controller.animationClips.SelectMany(AnimationUtility.GetCurveBindings)
                    .Where(binding => binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal) &&
                        excludedPath?.Invoke(binding.path) != true);
                throw WithAffected(error, bindings);
            }
            var planWarnings = new List<string>();
            var plan = NeutralShapePlan.Create(prepared, metadata.Controller, groups, excludedPath, requiredMorphs, planWarnings, metadata, fixedContext,
                retainUnresolvedRest: true);
            // Even when every resolved morph belongs to prepared appearance, retaining
            // that rest must not hide an unresolved callback or playable writer.
            // Analyze the complete graph once before ownership removes outputs.
            var allRoots = new HashSet<EditorCurveBinding>(plan.CommittedMorphs.Concat(plan.PreservedMorphs));
            if (allRoots.Count > 0)
            {
                try { ExpressionDependencies.AnalyzeNeutral(metadata.Controller, allRoots, excludedPath, metadata, automatic, fixedContext,
                    preserveCommittedMorphs: true); }
                catch (NeutralShapeSamplingException) { /* Missing live input can retain prepared rest after the data checks below. */ }
                catch (InvalidOperationException error) { throw WithAffected(error, allRoots); }
            }
            // Complete data/callback checks before any group's recoverable
            // refusal. An unsupported appearance curve must not conceal a bad
            // parameter curve or event later in the same native support graph.
            if (allRoots.Count > 0)
            {
                var layers = ExpressionDependencies.Inspect(metadata.Controller, excludedPath,
                    new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), new List<string>(), true);
                var fixedValues = ExpressionDependencies.FixedNeutralValues(metadata.Controller, metadata, layers, excludedPath, fixedContext);
                var reachable = ExpressionDependencies.Inspect(metadata.Controller, excludedPath,
                    new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), new List<string>(), true, fixedValues);
                var needed = new HashSet<string>(reachable.SelectMany(layer => layer.Reads), StringComparer.Ordinal);
                var types = ExpressionDependencies.Controller(metadata.Controller).parameters
                    .ToDictionary(parameter => parameter.name, parameter => parameter.type, StringComparer.Ordinal);
                foreach (var program in reachable.SelectMany(layer => layer.DriverPrograms).Distinct())
                {
                    // Check the entire needed program before Random can stop
                    // native evaluation and retain the authored rest component.
                    VrChatParameterDriver.ValidateTargets(program, types, needed);
                }
                var clips = ExpressionDependencies.NeutralClips(metadata.Controller, fixedValues, excludedPath);
                plan.ValidateBindings(clips, excludedPath);
                foreach (var clip in clips)
                    if (AnimationUtility.GetAnimationEvents(clip).Length != 0)
                        throw new InvalidOperationException(ExporterLocalization.T("常時適用FXと表情の影響範囲を確定できません: ") + clip.name + " / AnimationEvent");
                NeutralAdditionalDataPreflight.Validate(metadata.Controller, metadata, fixedContext, plan, excludedPath);
            }
            foreach (var warning in planWarnings) warnings?.Add(warning);
            var randomRestLayers = NeutralRandomRest.Preserve(metadata.Controller, metadata, plan, excludedPath, fixedContext, warnings);
            var randomRestMorphs = new HashSet<EditorCurveBinding>(plan.TemporalMorphs);
            foreach (var roots in groups)
            {
                // Appearance ownership may remove every captured channel in a
                // group. Still report its known neutral capability after all
                // data checks, and retain coupled companions from earlier or
                // later groups instead of silently accepting partial rest.
                var owned = new HashSet<EditorCurveBinding>(roots.Where(plan.PreservedMorphs.Contains));
                if (owned.Count > 0)
                {
                    try { ExpressionDependencies.AnalyzeNeutral(metadata.Controller, owned, excludedPath, metadata, automatic, fixedContext,
                        preserveCommittedMorphs: true); }
                    catch (NeutralShapeSamplingException error) { Preserve(owned, error); }
                    catch (InvalidOperationException error) { throw WithAffected(error, owned); }
                }
                roots.IntersectWith(plan.CommittedMorphs);
                roots.ExceptWith(randomRestMorphs);
                if (roots.Count == 0) continue;
                ExpressionDependencies dependencies;
                try { dependencies = ExpressionDependencies.AnalyzeNeutral(metadata.Controller, roots, excludedPath, metadata, automatic, fixedContext,
                    preserveCommittedMorphs: true); }
                catch (NeutralShapeSamplingException error) { Preserve(roots, error); continue; }
                catch (InvalidOperationException error) { throw WithAffected(error, roots); }
                dependencies.Layers.ExceptWith(randomRestLayers);
                dependencies.NativeSupportLayers.ExceptWith(randomRestLayers);
                var identity = string.Join(",", dependencies.Layers.OrderBy(index => index)) + "|" +
                    string.Join(",", dependencies.NativeSupportLayers.OrderBy(index => index)) + "|" +
                    string.Join(",", dependencies.Morphs.OrderBy(binding => binding.path, StringComparer.Ordinal)
                        .ThenBy(binding => binding.propertyName, StringComparer.Ordinal).Select(binding => binding.path + "/" + binding.propertyName));
                if (!completed.Add(identity)) continue;
                List<VrChatExpressionMenu.MorphValue> sampled;
                try { sampled = VrChatExpressionSampler.SampleNeutral(prepared, metadata.Controller, dependencies, metadata, excludedPath,
                    fixedContext, plan, preserveTemporalRest: true); }
                catch (NeutralShapeSamplingException error)
                {
                    // Native capability refusals can lack a dependency payload.
                    // Keep explicit error metadata and add the known typed
                    // component, without changing the hard data-error catch.
                    var dependencyMorphs = new HashSet<EditorCurveBinding>(dependencies.NeutralDependencyMorphs);
                    var coupledMorphs = new HashSet<EditorCurveBinding>(dependencies.NeutralCoupledMorphs);
                    if (error.DependencyMorphs != null) dependencyMorphs.UnionWith(error.DependencyMorphs);
                    if (error.CoupledMorphs != null) coupledMorphs.UnionWith(error.CoupledMorphs);
                    Preserve(dependencies.Morphs, new NeutralShapeSamplingException(error.Message, error, dependencyMorphs, coupledMorphs));
                    continue;
                }
                catch (InvalidOperationException error) { throw WithAffected(error, dependencies.Morphs); }
                // Static proof alone is insufficient. Only a complete native
                // sample can release support-only outputs from propagation;
                // explicit clip/parameter companions remain co-retained.
                if (dependencies.IndependentTopOverrideLayer >= 0)
                    independent.UnionWith(sampled.Select(value => EditorCurveBinding.FloatCurve(value.Path,
                        typeof(SkinnedMeshRenderer), "blendShape." + value.Shape)));
                foreach (var value in sampled)
                {
                    var binding = EditorCurveBinding.FloatCurve(value.Path, typeof(SkinnedMeshRenderer), "blendShape." + value.Shape);
                    if (coupledPreserved.Contains(binding) || preserved.Contains(binding) && !independent.Contains(binding)) continue;
                    var key = (value.Path, value.Shape);
                    if (values.TryGetValue(key, out var previous) && Math.Abs(previous.Weight - value.Weight) > .01f)
                        throw new InvalidOperationException("FXの初期表情を一意に確定できません: " + value.Path + " / " + value.Shape);
                    values[key] = value;
                }
            }
            plan.ReportTemporalRest(warnings);
            // All probes start from the same unchanged prepared avatar. If a
            // later group cannot resolve a shared output, discard earlier
            // samples for that output too; never combine a partial FX result
            // with the authored rest selected by the fallback.
            preserved.UnionWith(plan.TemporalMorphs);
            coupledPreserved.UnionWith(plan.TemporalMorphs);
            var released = new HashSet<EditorCurveBinding>(independent);
            released.ExceptWith(coupledPreserved);
            preserved.ExceptWith(released);
            foreach (var refusal in refusals)
            {
                var targets = new HashSet<EditorCurveBinding>(refusal.Roots);
                if (refusal.Error.DependencyMorphs != null) targets.UnionWith(refusal.Error.DependencyMorphs);
                if (refusal.Error.CoupledMorphs != null) targets.UnionWith(refusal.Error.CoupledMorphs);
                targets.ExceptWith(released);
                var message = string.Format(ExporterLocalization.T(
                    "FXの初期状態を固定できなかったため、書き出し用コピーの設定を保持しました（対象: {0}）。FXによる通常時の見た目と異なる場合があります。理由: {1}"),
                    Describe(targets), refusal.Error.Message);
                if (reported.Add(message)) warnings?.Add(message);
            }
            return values.Values.Where(value => !preserved.Contains(EditorCurveBinding.FloatCurve(value.Path,
                    typeof(SkinnedMeshRenderer), "blendShape." + value.Shape))).OrderBy(value => value.Path, StringComparer.Ordinal)
                .ThenBy(value => value.Shape, StringComparer.Ordinal).ToList();
        }

        private static InvalidOperationException WithAffected(InvalidOperationException error, IEnumerable<EditorCurveBinding> bindings)
        {
            var message = "FXの初期表情を確定できません（対象: " + Describe(bindings) + "）: " + error.Message;
            return error is NeutralShapeSamplingException neutral ? new NeutralShapeSamplingException(message, error, neutral.DependencyMorphs, neutral.CoupledMorphs) :
                new InvalidOperationException(message, error);
        }

        private static string Describe(IEnumerable<EditorCurveBinding> bindings)
        {
            var targets = bindings.Select(binding => binding.path + " / " + binding.propertyName.Substring("blendShape.".Length))
                .Distinct().OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var affected = string.Join(", ", targets.Take(16));
            if (targets.Length > 16) affected += " ほか" + (targets.Length - 16) + "件";
            return affected;
        }

        internal static HashSet<EditorCurveBinding> AutomaticChannels(GameObject avatar)
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
                var blinkSeeds = BlinkShapeNames.AutomaticCandidates(names).ToArray();
                foreach (var index in blinkSeeds) Add(renderer, names[index]);
                // Some authors separate an automatic eyelid from the manual
                // blink channel. Its exact semantic alias must additionally
                // reproduce a known blink on this mesh at every authored frame.
                // The two bounded candidates avoid probing unrelated morphs.
                foreach (var alias in new[] { "Auto_Blink", "AutoBlink" })
                {
                    var index = BlinkShapeNames.Unique(names, alias);
                    if (index >= 0 && blinkSeeds.Any(seed => EquivalentBlinkGeometry(renderer.sharedMesh, seed, index))) Add(renderer, names[index]);
                }
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

        private static bool EquivalentBlinkGeometry(Mesh mesh, int seed, int candidate)
        {
            if (!mesh.isReadable) return false;
            var frames = mesh.GetBlendShapeFrameCount(seed);
            if (frames == 0 || mesh.GetBlendShapeFrameCount(candidate) != frames) return false;
            for (var frame = 0; frame < frames; frame++)
            {
                var weight = mesh.GetBlendShapeFrameWeight(seed, frame);
                if (float.IsNaN(weight) || float.IsInfinity(weight) || weight != mesh.GetBlendShapeFrameWeight(candidate, frame)) return false;
            }
            var count = mesh.vertexCount;
            var seedPositions = new Vector3[count]; var seedNormals = new Vector3[count]; var seedTangents = new Vector3[count];
            var positions = new Vector3[count]; var normals = new Vector3[count]; var tangents = new Vector3[count];
            var effective = false;
            bool Equal(Vector3 first, Vector3 second) =>
                !float.IsNaN(first.x) && !float.IsNaN(first.y) && !float.IsNaN(first.z) &&
                !float.IsInfinity(first.x) && !float.IsInfinity(first.y) && !float.IsInfinity(first.z) && first.Equals(second);
            for (var frame = 0; frame < frames; frame++)
            {
                mesh.GetBlendShapeFrameVertices(seed, frame, seedPositions, seedNormals, seedTangents);
                mesh.GetBlendShapeFrameVertices(candidate, frame, positions, normals, tangents);
                for (var vertex = 0; vertex < count; vertex++)
                {
                    if (!Equal(seedPositions[vertex], positions[vertex]) || !Equal(seedNormals[vertex], normals[vertex]) ||
                        !Equal(seedTangents[vertex], tangents[vertex])) return false;
                    effective |= !seedPositions[vertex].Equals(Vector3.zero) || !seedNormals[vertex].Equals(Vector3.zero) || !seedTangents[vertex].Equals(Vector3.zero);
                }
            }
            return effective;
        }
    }
}
