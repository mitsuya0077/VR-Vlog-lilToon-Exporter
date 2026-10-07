using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // Output ownership is geometric, not a consequence of sharing an Animator
    // parameter with a wardrobe/material layer. This proof uses the prepared
    // mesh and descriptor; renderer/morph names alone cannot authorize it.
    internal sealed class FacialProjectionScope
    {
        internal readonly HashSet<EditorCurveBinding> Morphs = new HashSet<EditorCurveBinding>();
        internal const string Notice = "表情は顔の形状変化だけを保存します。材質・オブジェクトの切り替えと顔以外の変形は、書き出し用コピーの見た目を保持します。";

        // Analyze reads one prepared geometry snapshot. The session is removed
        // before endpoint creation/baking or optimizer passes can mutate it.
        // No static cache may outlive that snapshot, including cached failure.
        internal sealed class Session
        {
            private readonly GameObject avatar;
            private bool computed;
            private FacialProjectionScope scope;
            internal Session(GameObject avatar) { this.avatar = avatar; }
            internal FacialProjectionScope Get(GameObject target)
            {
                if (target != avatar) throw new InvalidOperationException(ExporterLocalization.T("表情の顔形状の評価対象が一致していません。"));
                if (!computed) { scope = Create(avatar); computed = true; }
                return scope;
            }
        }

        internal static FacialProjectionScope For(GameObject avatar, VrChatExpressionMenu.Source metadata)
            => metadata?.FacialProjectionSession != null ? metadata.FacialProjectionSession.Get(avatar) : Create(avatar);

        internal static FacialProjectionScope Create(GameObject avatar)
        {
            var animator = avatar.GetComponent<Animator>();
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman || !animator.avatar.isValid) return null;
            var head = animator.GetBoneTransform(HumanBodyBones.Head);
            if (head == null) return null;
            var neck = animator.GetBoneTransform(HumanBodyBones.Neck);
            var descriptor = avatar.GetComponents<Component>().FirstOrDefault(c => c != null &&
                c.GetType().FullName == "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            var seeds = new Dictionary<SkinnedMeshRenderer, HashSet<int>>();
            void Add(SkinnedMeshRenderer renderer, int shape)
            {
                if (renderer == null || renderer.sharedMesh == null || !renderer.transform.IsChildOf(avatar.transform) ||
                    shape < 0 || shape >= renderer.sharedMesh.blendShapeCount) return;
                if (!seeds.TryGetValue(renderer, out var shapes)) seeds.Add(renderer, shapes = new HashSet<int>());
                shapes.Add(shape);
            }
            var visemes = VrChatExpressionMenu.Member(descriptor, "VisemeSkinnedMesh") as SkinnedMeshRenderer;
            // SDK deserialization can retain a managed Unity wrapper for an
            // unassigned or destroyed renderer. ?. only checks CLR null and
            // would dereference that wrapper before the conservative fallback.
            if (visemes != null && visemes.sharedMesh != null)
            {
                // The SDK retains the other mode's serialized fields when a
                // user switches modes. Only the currently active route proves
                // a facial role; stale references may now identify clothing.
                var lipSync = VrChatExpressionMenu.Member(descriptor, "lipSync")?.ToString();
                if (lipSync == "VisemeBlendShape" && VrChatExpressionMenu.Member(descriptor, "VisemeBlendShapes") is IEnumerable names)
                    foreach (var name in names.OfType<string>()) Add(visemes, visemes.sharedMesh.GetBlendShapeIndex(name));
                if (lipSync == "JawFlapBlendShape")
                {
                    var mouth = VrChatExpressionMenu.Member(descriptor, "MouthOpenBlendShapeName") as string;
                    if (!string.IsNullOrEmpty(mouth)) Add(visemes, visemes.sharedMesh.GetBlendShapeIndex(mouth));
                }
            }
            var eyes = VrChatExpressionMenu.Member(descriptor, "customEyeLookSettings");
            var eyelids = VrChatExpressionMenu.Member(eyes, "eyelidsSkinnedMesh") as SkinnedMeshRenderer;
            if (VrChatExpressionMenu.Member(descriptor, "enableEyeLook") is bool eyeLook && eyeLook &&
                VrChatExpressionMenu.Member(eyes, "eyelidType")?.ToString() == "Blendshapes" &&
                VrChatExpressionMenu.Member(eyes, "eyelidsBlendshapes") is IEnumerable indices)
                foreach (var index in indices.OfType<int>()) Add(eyelids, index);
            // Even on a merged Body renderer, a shape named Blink can belong
            // to a disconnected accessory. Names never establish new seeds.
            var result = new FacialProjectionScope();
            foreach (var pair in seeds)
            {
                var renderer = pair.Key; var mesh = renderer.sharedMesh;
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || !mesh.isReadable) continue;
                var bones = renderer.bones;
                var allowed = new bool[mesh.vertexCount];
                using (var counts = mesh.GetBonesPerVertex())
                using (var weights = mesh.GetAllBoneWeights())
                {
                    if (counts.Length != allowed.Length) continue;
                    var offset = 0;
                    for (var vertex = 0; vertex < allowed.Length; vertex++)
                    {
                        var valid = counts[vertex] > 0; var sum = 0f; var headWeight = 0f;
                        for (var influence = 0; influence < counts[vertex]; influence++)
                        {
                            if (offset >= weights.Length) { valid = false; break; }
                            var weight = weights[offset++]; sum += weight.weight;
                            if (!NeutralShapeSnapshot.Finite(weight.weight) || weight.weight < 0) valid = false;
                            if (weight.weight == 0) continue;
                            if (weight.boneIndex < 0 || weight.boneIndex >= bones.Length || bones[weight.boneIndex] == null)
                            { valid = false; continue; }
                            var bone = bones[weight.boneIndex];
                            if (bone == head || bone.IsChildOf(head)) headWeight += weight.weight;
                            else if (neck == null || bone != neck) valid = false;
                        }
                        // Jaw/face seams often blend Head with its humanoid
                        // Neck. They remain facial only when Head dominates;
                        // torso, limbs and a neck-dominated surface cannot
                        // establish or enlarge the descriptor face domain.
                        allowed[vertex] = valid && Mathf.Abs(sum - 1) < .0001f && headWeight > .5f;
                    }
                }
                var adjacency = Enumerable.Range(0, mesh.vertexCount).Select(_ => new List<int>()).ToArray();
                var nonHeadBoundary = new bool[mesh.vertexCount];
                for (var sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    if (mesh.GetTopology(sub) != MeshTopology.Triangles) continue;
                    var triangles = mesh.GetTriangles(sub);
                    for (var i = 0; i < triangles.Length; i += 3)
                        for (var edge = 0; edge < 3; edge++)
                        {
                            var a = triangles[i + edge]; var b = triangles[i + (edge + 1) % 3];
                            if (allowed[a] && allowed[b]) { adjacency[a].Add(b); adjacency[b].Add(a); }
                            else if (allowed[a]) nonHeadBoundary[a] = true;
                            else if (allowed[b]) nonHeadBoundary[b] = true;
                        }
                }
                var positions = new Vector3[mesh.vertexCount]; var normals = new Vector3[mesh.vertexCount]; var tangents = new Vector3[mesh.vertexCount];
                HashSet<int> Changed(int shape)
                {
                    var changed = new HashSet<int>();
                    for (var frame = 0; frame < mesh.GetBlendShapeFrameCount(shape); frame++)
                    {
                        mesh.GetBlendShapeFrameVertices(shape, frame, positions, normals, tangents);
                        for (var vertex = 0; vertex < positions.Length; vertex++)
                            if (!positions[vertex].Equals(Vector3.zero) || !normals[vertex].Equals(Vector3.zero) || !tangents[vertex].Equals(Vector3.zero)) changed.Add(vertex);
                    }
                    return changed;
                }
                var domain = new HashSet<int>();
                foreach (var seed in pair.Value)
                {
                    var changed = Changed(seed);
                    if (changed.Count > 0 && changed.All(vertex => allowed[vertex])) domain.UnionWith(changed);
                }
                var pending = new Queue<int>(domain);
                while (pending.Count > 0)
                    foreach (var neighbour in adjacency[pending.Dequeue()]) if (domain.Add(neighbour)) pending.Enqueue(neighbour);
                if (domain.Count == 0) continue;
                // Eye inserts, cheek overlays and tear surfaces commonly have
                // separate topology inside the descriptor's facial surface.
                // Admit complete head-skinned islands enclosed by that surface,
                // with a scale-relative margin for overlays. A point on a large
                // accessory is insufficient; the entire island must fit. Keep
                // this proof within the descriptor renderer, not every object
                // parented to Head or a shape with a suggestive name.
                var vertices = mesh.vertices;
                var facialBounds = new Bounds(vertices[domain.First()], Vector3.zero);
                foreach (var vertex in domain) facialBounds.Encapsulate(vertices[vertex]);
                var extent = facialBounds.size.magnitude;
                if (NeutralShapeSnapshot.Finite(extent) && extent > 0)
                {
                    facialBounds.Expand(extent * .1f);
                    var visited = new HashSet<int>(domain);
                    for (var vertex = 0; vertex < vertices.Length; vertex++)
                    {
                        if (!allowed[vertex] || !visited.Add(vertex)) continue;
                        var island = new List<int> { vertex };
                        var queue = new Queue<int>(); queue.Enqueue(vertex);
                        while (queue.Count > 0)
                            foreach (var neighbour in adjacency[queue.Dequeue()])
                                if (visited.Add(neighbour)) { island.Add(neighbour); queue.Enqueue(neighbour); }
                        if (island.All(index => !nonHeadBoundary[index] && NeutralShapeSnapshot.Finite(vertices[index].x) &&
                            NeutralShapeSnapshot.Finite(vertices[index].y) && NeutralShapeSnapshot.Finite(vertices[index].z) &&
                            facialBounds.Contains(vertices[index]))) domain.UnionWith(island);
                    }
                }
                var path = AnimationUtility.CalculateTransformPath(renderer.transform, avatar.transform);
                for (var shape = 0; shape < mesh.blendShapeCount; shape++)
                {
                    var changed = Changed(shape);
                    if (changed.Count > 0 && changed.IsSubsetOf(domain)) result.Morphs.Add(EditorCurveBinding.FloatCurve(path,
                        typeof(SkinnedMeshRenderer), "blendShape." + mesh.GetBlendShapeName(shape)));
                }
            }
            return result.Morphs.Count == 0 ? null : result;
        }

        internal static void Warn(ICollection<string> messages)
        {
            var text = ExporterLocalization.T(Notice);
            if (messages != null && !messages.Contains(text)) messages.Add(text);
        }

        internal static bool NeedsProjection(GameObject avatar, IEnumerable<AnimationClip> clips, Func<string, bool> excludedPath = null)
            => clips.Where(clip => clip != null).Distinct().Any(clip =>
                AnimationUtility.GetObjectReferenceCurveBindings(clip).Any(binding => excludedPath?.Invoke(binding.path) != true) ||
                AnimationUtility.GetCurveBindings(clip).Any(binding => excludedPath?.Invoke(binding.path) != true &&
                    binding.type != typeof(Animator) && !(binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)) &&
                    !SelectedExpressionAppearance.IsUnchanged(avatar, clip, binding)));

        internal void WarnOmitted(IEnumerable<EditorCurveBinding> bindings, ICollection<string> messages)
        {
            Warn(messages);
            var omitted = bindings.Where(binding => !Morphs.Contains(binding)).Select(binding => binding.path + " / " + binding.propertyName)
                .Distinct().OrderBy(value => value, StringComparer.Ordinal).ToArray();
            if (omitted.Length == 0 || messages == null) return;
            var text = ExporterLocalization.T("顔の形状だけを動かすことを確認できないBlendShapeは、現在の見た目を保持しました: ") +
                string.Join(", ", omitted.Take(8)) + (omitted.Length > 8 ? " ほか" + (omitted.Length - 8) + "件" : "");
            if (!messages.Contains(text)) messages.Add(text);
        }
    }
}
