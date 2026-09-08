using System;
using System.Collections.Generic;
using System.Linq;
using UniVRM10;
using UnityEditor;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // Operates after authoring passes and implicit-weight conversion. Names and
    // renderer parents are not evidence of a skin's actual attachment.
    internal sealed class ExportAttachmentSession : IDisposable
    {
        internal sealed class Part
        {
            internal Transform Root;
            internal Renderer[] Renderers;
        }

        internal readonly GameObject Copy;
        internal readonly List<Part> Parts;
        internal readonly List<Transform> Targets;
        private readonly HashSet<Transform> humanoid;
        private readonly Dictionary<Transform, Transform[]> constraints;
        private readonly HashSet<Transform> fixedRootJoints;
        private readonly List<UnityEngine.Object> ownedSettings = new List<UnityEngine.Object>();

        internal ExportAttachmentSession(GameObject source, GameObject copy, bool includeConnected = false,
            IEnumerable<Transform> fixedRootJoints = null)
        {
            if (source == null || copy == null || source == copy || EditorUtility.IsPersistent(copy) ||
                copy.transform.IsChildOf(source.transform) || source.transform.IsChildOf(copy.transform))
                throw new InvalidOperationException("追従の確認には、元アバターから独立した書き出し用コピーが必要です。");
            Copy = copy;
            // These exact joints preserve authored avatar-root weights. They
            // are serialization details, not disconnected hair or accessories.
            this.fixedRootJoints = new HashSet<Transform>((fixedRootJoints ?? Enumerable.Empty<Transform>())
                .Where(joint => Inside(joint, copy.transform)));
            var animator = copy.GetComponent<Animator>();
            if (animator == null || animator.avatar == null || !animator.avatar.isValid || !animator.avatar.isHuman)
                animator = null; // A nested pet Animator must not become the main rig.
            humanoid = new HashSet<Transform>();
            Targets = new List<Transform>();
            if (animator != null)
            {
                for (var i = 0; i < (int)HumanBodyBones.LastBone; i++)
                {
                    var bone = animator.GetBoneTransform((HumanBodyBones)i);
                    if (Inside(bone, copy.transform)) humanoid.Add(bone);
                }
                foreach (var kind in new[] { HumanBodyBones.Head, HumanBodyBones.Neck, HumanBodyBones.Chest, HumanBodyBones.Spine, HumanBodyBones.Hips })
                {
                    var bone = animator.GetBoneTransform(kind);
                    if (humanoid.Contains(bone) && !Targets.Contains(bone)) Targets.Add(bone);
                }
            }
            var constraintComponents = copy.GetComponentsInChildren<MonoBehaviour>(true)
                .Where(component => component is IVrm10Constraint).ToArray();
            // UniVRM 0.131 serializes disabled constraint components without
            // an enabled flag. Remove only those components on the owned copy
            // so a disabled authored constraint cannot revive after export.
            foreach (var component in constraintComponents)
                if (!component.isActiveAndEnabled) UnityEngine.Object.DestroyImmediate(component);
            constraints = constraintComponents.Where(component => component != null && component.isActiveAndEnabled)
                .OfType<IVrm10Constraint>()
                .Where(c => c.ConstraintTarget != null && c.ConstraintSource != null)
                .GroupBy(c => c.ConstraintTarget).ToDictionary(g => g.Key, g => g.Select(c => c.ConstraintSource).ToArray());
            Parts = FindParts(includeConnected);
        }

        private List<Part> FindParts(bool includeConnected)
        {
            var result = new List<Part>();
            if (humanoid.Count == 0) return result;
            var renderers = ExportRendererSelection.Enumerate(Copy).Where(r => r is SkinnedMeshRenderer || r is MeshRenderer).ToArray();
            var influences = renderers.ToDictionary(r => r, Influences);
            var roots = new HashSet<Transform>();
            foreach (var bones in influences.Values)
                foreach (var bone in bones)
                {
                    if (!Inside(bone, Copy.transform) || (!includeConnected && HasHumanoidRoute(bone, new HashSet<Transform>()))) continue;
                    var root = bone;
                    while (root.parent != null && root.parent != Copy.transform &&
                        !humanoid.Any(h => Inside(h, root.parent))) root = root.parent;
                    if (root != Copy.transform && !ContainsFixedRootJoint(root) && !humanoid.Any(h => Inside(h, root))) roots.Add(root);
                }
            foreach (var root in roots.OrderBy(Path, StringComparer.Ordinal))
                result.Add(new Part
                {
                    Root = root,
                    Renderers = renderers.Where(r => Inside(r.transform, root) || influences[r].Any(b => Inside(b, root))).ToArray()
                });
            return result;
        }

        private bool HasHumanoidRoute(Transform bone, HashSet<Transform> visited)
        {
            for (var current = bone; Inside(current, Copy.transform); current = current.parent)
            {
                if (!visited.Add(current)) return false;
                if (humanoid.Contains(current)) return true;
                if (constraints.TryGetValue(current, out var sources) && sources.Any(s => HasHumanoidRoute(s, visited))) return true;
            }
            return false;
        }

        private bool ContainsFixedRootJoint(Transform root) => fixedRootJoints.Any(joint => Inside(joint, root));

        internal static Transform[] Influences(Renderer renderer)
        {
            if (!(renderer is SkinnedMeshRenderer skin) || skin.sharedMesh == null) return new[] { renderer.transform };
            var bones = skin.bones ?? Array.Empty<Transform>();
            var used = new HashSet<Transform>();
            // These NativeArrays are mesh-owned read-only views (Allocator.None).
            foreach (var weight in skin.sharedMesh.GetAllBoneWeights())
                if (weight.weight > 0f && weight.boneIndex >= 0 && weight.boneIndex < bones.Length && bones[weight.boneIndex] != null)
                    used.Add(bones[weight.boneIndex]);
            if (used.Count == 0) used.Add(skin.rootBone != null ? skin.rootBone : skin.transform);
            return used.ToArray();
        }

        internal void Attach(Transform root, Transform target, ICollection<string> warnings = null)
        {
            ValidateConnection(root, target);
            var affected = ExportRendererSelection.Enumerate(Copy)
                .Where(r => Inside(r.transform, root) || Influences(r).Any(b => Inside(b, root))).Select(r => Path(r.transform)).ToArray();
            var instance = Copy.GetComponent<Vrm10Instance>();
            var paths = BindingPaths(root, target);
            var settings = CopyBindings(instance, paths);
            ReparentPreservingPose(root, target);
            if (settings != null) instance.Vrm = settings;
            warnings?.Add("追従を指定: " + root.name + " → " + target.name + "。同じ骨を使う箇所: " + string.Join(", ", affected));
        }

        internal void ValidateConnection(Transform root, Transform target)
        {
            if (!Inside(root, Copy.transform) || !Inside(target, Copy.transform) || root == Copy.transform ||
                Inside(target, root) || humanoid.Any(h => Inside(h, root)))
                throw new InvalidOperationException("追従先または接続範囲が正しくありません。本体の骨を含まないパーツと、コピー内の追従先を指定してください。");
            if (!Targets.Contains(target)) throw new InvalidOperationException("追従先には表示された本体の骨を指定してください。");
            if (ContainsFixedRootJoint(root))
                throw new InvalidOperationException(Path(root) + ": アバターのルートに固定された頂点を保つ出力用ジョイントが含まれるため、別の骨には接続できません。");
            BindingPaths(root, target);
            foreach (var pair in constraints)
                if (Inside(pair.Key, root) && pair.Value.Any(s => !Inside(s, root)))
                    throw new InvalidOperationException(Path(root) + ": 別の骨を参照する VRM 拘束が含まれています。既存の追従を重ねないため、この範囲は接続できません。元の拘束の対象を確認してください。");
            var instance = Copy.GetComponent<Vrm10Instance>();
            if (instance != null && instance.SpringBone != null)
                foreach (var spring in instance.SpringBone.Springs)
                {
                    if (spring == null) continue;
                    var joints = spring.Joints.Where(j => j != null).Select(j => j.transform).ToArray();
                    if (joints.Any(j => Inside(j, root)) && joints.Any(j => !Inside(j, root)))
                        throw new InvalidOperationException(Path(root) + ": 揺れ物の連続した骨が接続範囲の外にもあります。揺れ物全体を同じ範囲にしてから確認してください。");
                }
        }

        private Dictionary<string, string> BindingPaths(Transform root, Transform target)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var settings = Copy.GetComponent<Vrm10Instance>()?.Vrm;
            if (settings == null) return result;
            var bound = new HashSet<string>(StringComparer.Ordinal);
            if (settings.Expression != null)
                foreach (var entry in settings.Expression.Clips)
                    foreach (var binding in entry.Clip.MorphTargetBindings) bound.Add(binding.RelativePath);
            if (settings.FirstPerson != null)
                foreach (var flag in settings.FirstPerson.Renderers) bound.Add(flag.Renderer);
            if (bound.Count == 0) return result;
            var all = Copy.GetComponentsInChildren<Transform>(true).GroupBy(Path)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            var prefix = Path(root);
            var destination = Path(target) + "/" + root.name;
            foreach (var value in root.GetComponentsInChildren<Transform>(true))
            {
                var oldPath = Path(value);
                if (!bound.Contains(oldPath)) continue;
                var newPath = destination + oldPath.Substring(prefix.Length);
                if (oldPath == newPath) continue;
                // An empty sibling with the moved root's name also shadows
                // descendant paths: Transform.Find stops at that first sibling.
                if (all[oldPath].Length != 1 || Copy.transform.Find(oldPath) != value ||
                    Copy.transform.Find(Path(target)) != target ||
                    (all.TryGetValue(destination, out var roots) && roots.Any(t => t != root)) ||
                    (all.TryGetValue(newPath, out var collisions) && collisions.Any(t => !Inside(t, root))))
                    throw new InvalidOperationException(oldPath + ": 接続先で同じ名前の階層と重なり、既存の表情・一人称設定を一意に保てません。対象の名前を区別してから書き出してください。");
                result[oldPath] = newPath;
            }
            return result;
        }

        private VRM10Object CopyBindings(Vrm10Instance instance, Dictionary<string, string> paths)
        {
            if (paths.Count == 0 || instance == null || instance.Vrm == null) return null;
            string Remap(string path) => path != null && paths.TryGetValue(path, out var value) ? value : path;
            var original = instance.Vrm;
            var settings = UnityEngine.Object.Instantiate(original);
            ownedSettings.Add(settings);
            settings.name = original.name;
            if (original.FirstPerson != null)
                settings.FirstPerson = new VRM10ObjectFirstPerson { Renderers = original.FirstPerson.Renderers.Select(flag =>
                { flag.Renderer = Remap(flag.Renderer); return flag; }).ToList() };
            if (original.Expression != null)
            {
                settings.Expression = new VRM10ObjectExpression();
                var copies = new Dictionary<VRM10Expression, VRM10Expression>();
                foreach (var entry in original.Expression.Clips)
                {
                    if (!copies.TryGetValue(entry.Clip, out var clip))
                    {
                        clip = entry.Clip;
                        if (clip.MorphTargetBindings.Any(b => b.RelativePath != Remap(b.RelativePath)))
                        {
                            clip = UnityEngine.Object.Instantiate(clip);
                            ownedSettings.Add(clip);
                            clip.name = entry.Clip.name;
                            clip.MorphTargetBindings = clip.MorphTargetBindings.Select(binding =>
                            { binding.RelativePath = Remap(binding.RelativePath); return binding; }).ToArray();
                        }
                        copies.Add(entry.Clip, clip);
                    }
                    settings.Expression.AddClip(entry.Preset, clip);
                }
            }
            return settings;
        }

        public void Dispose()
        {
            foreach (var value in ownedSettings) if (value != null) UnityEngine.Object.DestroyImmediate(value);
            ownedSettings.Clear();
        }

        internal static void ReparentPreservingPose(Transform root, Transform target)
        {
            var oldParent = root.parent;
            var oldPosition = root.localPosition;
            var oldRotation = root.localRotation;
            var oldScale = root.localScale;
            var oldSibling = root.GetSiblingIndex();
            var world = root.localToWorldMatrix;
            var kept = false;
            try
            {
                if (!Invertible(world) || !Invertible(target.localToWorldMatrix))
                    throw new InvalidOperationException("ゼロまたは不正な拡縮が含まれています。");
                root.SetParent(target, true);
                // A rotated, non-uniformly scaled parent can require shear that
                // Unity Transform/glTF TRS cannot preserve. Never silently snap.
                if (!SameMatrix(world, root.localToWorldMatrix))
                    throw new InvalidOperationException("回転と不均等な拡縮の組み合わせで、現在の形を保てません。");
                kept = true;
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(root.name + ": 追従を適用できません。" + error.Message, error);
            }
            finally
            {
                if (!kept)
                {
                    root.SetParent(oldParent, false);
                    root.localPosition = oldPosition;
                    root.localRotation = oldRotation;
                    root.localScale = oldScale;
                    root.SetSiblingIndex(oldSibling);
                }
            }
        }

        internal string Path(Transform transform) => transform == null ? "(参照なし)" : AnimationUtility.CalculateTransformPath(transform, Copy.transform);
        internal static bool Inside(Transform value, Transform root) => value != null && root != null && (value == root || value.IsChildOf(root));
        private static bool Invertible(Matrix4x4 matrix) => !float.IsNaN(matrix.determinant) && !float.IsInfinity(matrix.determinant) && Mathf.Abs(matrix.determinant) > 1e-12f;
        private static bool SameMatrix(Matrix4x4 a, Matrix4x4 b)
        {
            for (var i = 0; i < 16; i++)
                if (float.IsNaN(b[i]) || float.IsInfinity(b[i]) || Mathf.Abs(a[i] - b[i]) > 0.0002f * Mathf.Max(1f, Mathf.Abs(a[i]))) return false;
            return true;
        }
    }
}
