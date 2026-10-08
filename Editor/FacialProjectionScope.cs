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
                var vertices = mesh.vertices;
                var components = new List<List<int>>();
                var componentAt = Enumerable.Repeat(-1, mesh.vertexCount).ToArray();
                var boundaryComponents = new HashSet<int>();
                for (var vertex = 0; vertex < vertices.Length; vertex++)
                {
                    if (!allowed[vertex] || componentAt[vertex] >= 0) continue;
                    var index = components.Count; var members = new List<int>(); components.Add(members);
                    var queue = new Queue<int>(); queue.Enqueue(vertex); componentAt[vertex] = index;
                    while (queue.Count > 0)
                    {
                        var current = queue.Dequeue(); members.Add(current);
                        if (nonHeadBoundary[current]) boundaryComponents.Add(index);
                        foreach (var neighbour in adjacency[current])
                            if (componentAt[neighbour] < 0) { componentAt[neighbour] = index; queue.Enqueue(neighbour); }
                    }
                }
                // UV/material seams duplicate vertices without sharing their
                // indices. Reconstruct only exact coincident triangle edges;
                // a touching point or a nearby detached accessory is no seam.
                // New components must remain wholly head-skinned, including
                // their boundary. Never use deformation names or a distance
                // tolerance to turn an accessory into connected facial mesh.
                var seams = components.Select(_ => new HashSet<int>()).ToArray();
                var edgeOwners = new Dictionary<(Vector3, Vector3), List<int>>();
                bool FinitePoint(Vector3 point) => NeutralShapeSnapshot.Finite(point.x) &&
                    NeutralShapeSnapshot.Finite(point.y) && NeutralShapeSnapshot.Finite(point.z);
                int Compare(Vector3 a, Vector3 b)
                {
                    var comparison = a.x.CompareTo(b.x); if (comparison != 0) return comparison;
                    comparison = a.y.CompareTo(b.y); return comparison != 0 ? comparison : a.z.CompareTo(b.z);
                }
                for (var vertex = 0; vertex < vertices.Length; vertex++)
                    foreach (var neighbour in adjacency[vertex])
                    {
                        if (vertex >= neighbour || nonHeadBoundary[vertex] || nonHeadBoundary[neighbour]) continue;
                        var first = vertices[vertex]; var second = vertices[neighbour];
                        if (!FinitePoint(first) || !FinitePoint(second) || first.Equals(second)) continue;
                        if (Compare(first, second) > 0) (first, second) = (second, first);
                        var key = (first, second); var index = componentAt[vertex];
                        if (!edgeOwners.TryGetValue(key, out var owners)) edgeOwners.Add(key, new List<int> { index });
                        else if (!owners.Contains(index))
                        {
                            foreach (var owner in owners) { seams[owner].Add(index); seams[index].Add(owner); }
                            owners.Add(index);
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
                var seededComponents = new HashSet<int>(domain.Select(vertex => componentAt[vertex]));
                foreach (var component in seededComponents) domain.UnionWith(components[component]);
                // Preserve the original index-connected descriptor surface
                // for enclosure bounds. A welded seam proves its own surface,
                // never a larger box authorizing other detached components.
                var enclosureSurface = new HashSet<int>(domain);
                var pending = new Queue<int>(seededComponents);
                while (pending.Count > 0)
                {
                    var current = pending.Dequeue(); domain.UnionWith(components[current]);
                    foreach (var neighbour in seams[current])
                        if (!boundaryComponents.Contains(neighbour) && seededComponents.Add(neighbour)) pending.Enqueue(neighbour);
                }
                if (domain.Count == 0) continue;
                // Eye inserts, cheek overlays and tear surfaces commonly have
                // separate topology inside the descriptor's facial surface.
                // Admit complete head-skinned islands enclosed by that surface,
                // with a scale-relative margin for overlays. A point on a large
                // accessory is insufficient; the entire island must fit. Keep
                // this proof within the descriptor renderer, not every object
                // parented to Head or a shape with a suggestive name.
                var facialBounds = new Bounds(vertices[enclosureSurface.First()], Vector3.zero);
                foreach (var vertex in enclosureSurface) facialBounds.Encapsulate(vertices[vertex]);
                var extent = facialBounds.size.magnitude;
                if (NeutralShapeSnapshot.Finite(extent) && extent > 0)
                {
                    facialBounds.Expand(extent * .1f);
                    // A viseme-connected surface can be only the front of a
                    // face. Separate tears/lids/cheek surfaces behind it are
                    // still bounded facial topology. Prove anterior depth using
                    // the humanoid Head's bind anchor, in avatar-root axes;
                    // mesh axes can be rotated and current bone poses can move.
                    // This never widens the descriptor's lateral/vertical
                    // extent or admits an island touching non-head geometry.
                    var meshToAvatar = avatar.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                    var hasDepthEnvelope = TryDepthEnvelope(vertices, enclosureSurface, meshToAvatar, mesh.bindposes,
                        Array.IndexOf(bones, head), out var depthEnvelope);
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
                            NeutralShapeSnapshot.Finite(vertices[index].y) && NeutralShapeSnapshot.Finite(vertices[index].z)) &&
                            (hasDepthEnvelope ? island.All(index => depthEnvelope.Contains(meshToAvatar.MultiplyPoint3x4(vertices[index]))) :
                                island.All(index => facialBounds.Contains(vertices[index])))) domain.UnionWith(island);
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

        private static bool FiniteInvertible(Matrix4x4 matrix)
        {
            for (var i = 0; i < 16; i++) if (!NeutralShapeSnapshot.Finite(matrix[i])) return false;
            if (!NeutralShapeSnapshot.Finite(matrix.determinant) || matrix.determinant == 0) return false;
            var inverse = matrix.inverse;
            for (var i = 0; i < 16; i++) if (!NeutralShapeSnapshot.Finite(inverse[i])) return false;
            return inverse.determinant != 0 && NeutralShapeSnapshot.Finite(inverse.determinant);
        }

        private static bool TryDepthEnvelope(Vector3[] vertices, HashSet<int> domain, Matrix4x4 meshToAvatar,
            Matrix4x4[] bindposes, int headIndex, out Bounds envelope)
        {
            envelope = default;
            if (headIndex < 0 || headIndex >= bindposes.Length || !FiniteInvertible(meshToAvatar) ||
                !FiniteInvertible(bindposes[headIndex])) return false;
            var anchor = meshToAvatar.MultiplyPoint3x4(bindposes[headIndex].inverse.MultiplyPoint3x4(Vector3.zero));
            if (!NeutralShapeSnapshot.Finite(anchor.x) || !NeutralShapeSnapshot.Finite(anchor.y) ||
                !NeutralShapeSnapshot.Finite(anchor.z)) return false;
            var first = true;
            foreach (var vertex in domain)
            {
                var point = meshToAvatar.MultiplyPoint3x4(vertices[vertex]);
                if (!NeutralShapeSnapshot.Finite(point.x) || !NeutralShapeSnapshot.Finite(point.y) ||
                    !NeutralShapeSnapshot.Finite(point.z)) return false;
                if (first) { envelope = new Bounds(point, Vector3.zero); first = false; }
                else envelope.Encapsulate(point);
            }
            if (first) return false;
            var minimum = envelope.min; var maximum = envelope.max;
            var extent = envelope.size.magnitude;
            // An anchor outside the connected face's projection, or a surface
            // spanning both sides of it, cannot prove a new depth region.
            if (!NeutralShapeSnapshot.Finite(extent) || extent <= 0 ||
                anchor.x < minimum.x || anchor.x > maximum.x || anchor.y < minimum.y || anchor.y > maximum.y ||
                !(minimum.z >= anchor.z || maximum.z <= anchor.z)) return false;
            var front = minimum.z >= anchor.z;
            minimum.z = Mathf.Min(minimum.z, anchor.z);
            maximum.z = Mathf.Max(maximum.z, anchor.z);
            if (!NeutralShapeSnapshot.Finite(minimum.z) || !NeutralShapeSnapshot.Finite(maximum.z)) return false;
            envelope.SetMinMax(minimum, maximum);
            envelope.Expand(extent * .1f);
            // The lateral/vertical overlay margin cannot authorize a detached
            // island behind the anatomical anchor on the opposite side from
            // the descriptor surface.
            minimum = envelope.min; maximum = envelope.max;
            if (front) minimum.z = anchor.z; else maximum.z = anchor.z;
            envelope.SetMinMax(minimum, maximum);
            return true;
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
