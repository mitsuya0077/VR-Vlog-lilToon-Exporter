using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using VRVlog.Poses;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter
{
    internal sealed class PoseLayer
    {
        internal AnimationClip Clip;
        internal AvatarMask Mask;
        internal AvatarMask OuterMask;
        internal string Group = "";
        internal float GroupWeight = 1;
        internal float Weight = 1;
        internal bool Additive;
        internal bool SkipMuscles;
        internal float Time;
        internal string ClipIdentity;
    }
    internal sealed class PoseCandidate
    {
        internal string Id, Name, Category = "", Source, Error, Note = "", Conditions = "";
        internal bool SampledMotion;
        internal readonly List<PoseLayer> Layers = new List<PoseLayer>();
        internal HumanoidPoseData Data;
    }
    internal sealed class ManualPose
    {
        internal AnimationClip Clip;
        internal float Time;
        internal string Name, Category = "手動";
        internal ManualPose Copy() => (ManualPose)MemberwiseClone();
    }
    internal sealed class PoseExportOptions
    {
        internal readonly List<ManualPose> Manual = new List<ManualPose>();
        internal readonly HashSet<string> Excluded = new HashSet<string>();
        internal readonly Dictionary<string, string> Names = new Dictionary<string, string>();
        internal PoseExportOptions Copy()
        {
            var result = new PoseExportOptions(); result.Manual.AddRange(Manual.Select(m => m.Copy()));
            result.Excluded.UnionWith(Excluded); foreach (var p in Names) result.Names.Add(p.Key, p.Value); return result;
        }
    }

    internal static class PoseSampling
    {
        internal static HumanBodyBones HumanBone(string name)
        {
            // VRM 1.0 corrected thumb names; Unity retains the older names.
            name = name.Replace("ThumbProximal", "ThumbIntermediate").Replace("ThumbMetacarpal", "ThumbProximal");
            return (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), name, true);
        }
        internal static Quaternion Reflect(Quaternion q) => new Quaternion(q.x, -q.y, -q.z, q.w);
        internal static Vector3 Reflect(Vector3 v) => new Vector3(-v.x, v.y, v.z);
        internal static string Hash(string text)
        { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant(); }

        internal static bool Moving(AnimationClip clip)
        {
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var keys = AnimationUtility.GetEditorCurve(clip, binding).keys;
                if (keys.Length < 2) continue;
                if (keys.Any(k => k.value != keys[0].value)) return true;
                for (var i = 1; i < keys.Length; i++)
                    if (!float.IsInfinity(keys[i - 1].outTangent) && !float.IsInfinity(keys[i].inTangent) &&
                        (keys[i - 1].outTangent != 0 || keys[i].inTangent != 0)) return true;
            }
            return false;
        }

        internal static string Identity(PoseCandidate candidate)
        {
            var parts = new StringBuilder(candidate.Conditions);
            foreach (var layer in candidate.Layers)
            {
                if (layer.Clip == null) { parts.Append("missing"); continue; }
                // Asset identity matters even if two clips contain the same curves.
                var persistent = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(layer.Clip, out string guid, out long local) && EditorUtility.IsPersistent(layer.Clip);
                parts.Append('|').Append(layer.ClipIdentity ?? (persistent ? guid + ":" + local : "instance:" + layer.Clip.GetInstanceID()));
                parts.Append('|').Append(layer.Time.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                parts.Append('|').Append(layer.Weight.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|').Append(layer.Additive).Append('|').Append(layer.SkipMuscles);
                parts.Append('|').Append(MaskIdentity(layer.Mask));
                parts.Append('|').Append(layer.Group).Append('|').Append(layer.GroupWeight.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                parts.Append('|').Append(MaskIdentity(layer.OuterMask));
            }
            return Hash(parts.ToString());
        }
        static string MaskIdentity(AvatarMask mask)
        {
            if (mask == null) return "all";
            var parts = new StringBuilder();
            for (var i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++) parts.Append(mask.GetHumanoidBodyPartActive((AvatarMaskBodyPart)i) ? '1' : '0');
            for (var i = 0; i < mask.transformCount; i++) parts.Append('|').Append(mask.GetTransformPath(i)).Append(':').Append(mask.GetTransformActive(i));
            return parts.ToString();
        }
        internal static string GeneratedClipIdentity(AnimationClip clip)
        {
            var path = AssetDatabase.GetAssetPath(clip);
            if (EditorUtility.IsPersistent(clip) && !path.StartsWith("Assets/VRVlogExportTemp-", StringComparison.Ordinal)) return null;
            // NDMF regenerates native clip objects/assets on each preview/export.
            // Hash real curves, not EditorJsonUtility (which omits native curves).
            var rows = new List<object>();
            foreach (var b in AnimationUtility.GetCurveBindings(clip).OrderBy(b => b.path, StringComparer.Ordinal).ThenBy(b => b.propertyName, StringComparer.Ordinal))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, b);
                rows.Add(new object[] { b.path, b.type.FullName, b.propertyName, (int)curve.preWrapMode, (int)curve.postWrapMode,
                    curve.keys.Select(k => new object[] { k.time, k.value, k.inTangent.ToString("R", System.Globalization.CultureInfo.InvariantCulture), k.outTangent.ToString("R", System.Globalization.CultureInfo.InvariantCulture), k.inWeight, k.outWeight, (int)k.weightedMode }).ToArray() });
            }
            return "generated:" + Hash(JsonDom.Serialize(rows));
        }

        internal static HumanoidPoseData Sample(GameObject avatar, PoseCandidate candidate)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            GameObject copy = null, staging = null; var graph = default(PlayableGraph);
            var clips = new List<AnimationClip>();
            try
            {
                staging = new GameObject("Inactive pose staging") { hideFlags = HideFlags.HideAndDontSave }; staging.SetActive(false);
                SceneManager.MoveGameObjectToScene(staging, scene);
                copy = Object.Instantiate(avatar, staging.transform, false); copy.hideFlags = HideFlags.HideAndDontSave;
                foreach (var behaviour in copy.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                copy.transform.SetParent(null, false);
                copy.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                copy.transform.localScale = Vector3.one;
                var animator = copy.GetComponent<Animator>();
                if (animator == null || !animator.isHuman || animator.avatar == null || !animator.avatar.isValid)
                    throw new InvalidOperationException("有効なHumanoid Animatorが必要です。");
                var bones = HumanoidPoseData.BoneNames.Select(name => (name, bone: animator.GetBoneTransform(HumanBone(name))))
                    .Where(b => b.bone != null).ToArray();
                var rotations = bones.ToDictionary(b => b.name, b => b.bone.rotation);
                var locals = bones.ToDictionary(b => b.name, b => b.bone.localRotation);
                var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                var hipPosition = hips.position;
                animator.runtimeAnimatorController = null; animator.enabled = true;
                animator.fireEvents = false; animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                copy.SetActive(true);
                graph = PlayableGraph.Create("VR Vlog static pose"); graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var groups = candidate.Layers.GroupBy(l => l.Group).ToArray();
                var mixer = AnimationLayerMixerPlayable.Create(graph, groups.Length + 1);
                var output = AnimationPlayableOutput.Create(graph, "Pose", animator); output.SetSourcePlayable(mixer);
                // Empty humanoid streams evaluate Unity's muscle defaults,
                // which need not equal the avatar's authored rest (notably
                // fingers). Supply its actual rest as the bottom layer.
                var hasMuscles = candidate.Layers.Any(l => !l.SkipMuscles && AnimationUtility.GetCurveBindings(l.Clip).Any(b => b.type == typeof(Animator)));
                var hasTransforms = candidate.Layers.Any(l => AnimationUtility.GetCurveBindings(l.Clip).Any(b => b.type == typeof(Transform)));
                if (hasMuscles && hasTransforms) throw new InvalidOperationException("HumanoidカーブとTransformカーブの混合は未対応です。");
                if (hasMuscles && candidate.Layers.Count(l => l.Weight > 0 && l.GroupWeight > 0) > 1)
                    throw new InvalidOperationException("複数Humanoidクリップの筋肉カーブ合成は未対応です。");
                var baseline = hasMuscles ? RestClip(animator, copy) : TransformRestClip(animator, copy);
                clips.Add(baseline);
                AnimationClipPlayable Baseline()
                {
                    var basePlayable = AnimationClipPlayable.Create(graph, baseline);
                    basePlayable.SetApplyFootIK(false); basePlayable.SetApplyPlayableIK(false); basePlayable.SetSpeed(0);
                    return basePlayable;
                }
                graph.Connect(Baseline(), 0, mixer, 0); mixer.SetInputWeight(0, 1);
                // Input zero stays empty. Each real layer has its authored weight,
                // including a single fractional layer (Unity special-cases input 0).
                var any = false;
                for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
                {
                    var layers = groups[groupIndex].ToArray();
                    var inner = AnimationLayerMixerPlayable.Create(graph, layers.Length + 1);
                    graph.Connect(Baseline(), 0, inner, 0); inner.SetInputWeight(0, 1);
                    graph.Connect(inner, 0, mixer, groupIndex + 1); mixer.SetInputWeight(groupIndex + 1, layers[0].GroupWeight);
                    if (layers[0].OuterMask != null) mixer.SetLayerMaskFromAvatarMask((uint)(groupIndex + 1), layers[0].OuterMask);
                    for (var i = 0; i < layers.Length; i++)
                    {
                    var layer = layers[i];
                    if (layer.Clip == null || !Finite(layer.Time) || layer.Time < 0 || layer.Time > 600 || layer.Time > layer.Clip.length)
                        throw new InvalidOperationException("クリップまたは採用時刻が不正です（0秒〜クリップ末尾、最大600秒）。");
                    var clean = BodyClip(copy, animator, layer.Clip, layer.SkipMuscles); clips.Add(clean);
                    any |= AnimationUtility.GetCurveBindings(clean).Length > 0;
                    var playable = AnimationClipPlayable.Create(graph, clean);
                    playable.SetApplyFootIK(false); playable.SetApplyPlayableIK(false); playable.SetSpeed(0); playable.SetTime(layer.Time);
                    graph.Connect(playable, 0, inner, i + 1); inner.SetInputWeight(i + 1, layer.Weight);
                    inner.SetLayerAdditive((uint)(i + 1), layer.Additive);
                    if (layer.Mask != null) inner.SetLayerMaskFromAvatarMask((uint)(i + 1), layer.Mask);
                    }
                }
                if (!any) throw new InvalidOperationException("書き出すHumanoidの体・手足・指のカーブがありません。");
                if (hasTransforms) EvaluateTransforms(copy, animator, candidate.Layers);
                else { graph.Play(); graph.Evaluate(0); }
                // A clip playable can populate unbound humanoid channels with
                // defaults. Retain the exact authored locals outside its body
                // parts, and outside every effective mask, before capturing.
                foreach (var b in bones)
                    if (!candidate.Layers.Any(l => WritesBone(l, b.name, b.bone, copy))) b.bone.localRotation = locals[b.name];
                if (!candidate.Layers.Any(l => WritesHipsPosition(l, AnimationUtility.CalculateTransformPath(hips, copy.transform)))) hips.position = hipPosition;
                var data = new HumanoidPoseData { Id = candidate.Id, Name = candidate.Name, Category = candidate.Category,
                    Source = candidate.Source, SampleTime = candidate.Layers.Count == 1 ? candidate.Layers[0].Time : 0,
                    SampledMotion = candidate.SampledMotion };
                // ModelExporter omits the avatar root node. Sample with the
                // same identity root TRS, independent of scene placement/scale.
                var offset = Reflect(hips.position - hipPosition);
                data.HipsOffset = new double[] { offset.x, offset.y, offset.z };
                foreach (var b in bones)
                {
                    var delta = Reflect((b.bone.rotation * Quaternion.Inverse(rotations[b.name])).normalized);
                    data.Bones.Add(new HumanoidPoseData.Bone { Name = b.name, Node = Array.IndexOf(HumanoidPoseData.BoneNames, b.name),
                        Rotation = new double[] { delta.x, delta.y, delta.z, delta.w } });
                }
                HumanoidPoseData.Write(new[] { data });
                return data;
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                if (copy != null) Object.DestroyImmediate(copy);
                if (staging != null) Object.DestroyImmediate(staging);
                foreach (var clip in clips) Object.DestroyImmediate(clip);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        internal static AnimationClip BodyClip(GameObject root, Animator animator, AnimationClip source, bool skipMuscles = false)
        {
            var result = Object.Instantiate(source); result.name = source.name;
            // All events and non-pose curves are removed from this owned clip.
            AnimationUtility.SetAnimationEvents(result, Array.Empty<AnimationEvent>());
            var paths = new HashSet<string>(HumanoidPoseData.BoneNames.Select(n => animator.GetBoneTransform(HumanBone(n)))
                .Where(t => t != null).Select(t => AnimationUtility.CalculateTransformPath(t, root.transform)));
            var hips = AnimationUtility.CalculateTransformPath(animator.GetBoneTransform(HumanBodyBones.Hips), root.transform);
            var muscles = new HashSet<string>(HumanTrait.MuscleName);
            foreach (var side in new[] { "Left", "Right" })
                foreach (var finger in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
                {
                    muscles.Add(side + "Hand." + finger + ".Spread");
                    for (var joint = 1; joint <= 3; joint++) muscles.Add(side + "Hand." + finger + "." + joint + " Stretched");
                }
            foreach (var binding in AnimationUtility.GetCurveBindings(result))
            {
                var p = binding.propertyName;
                if (binding.type == typeof(Transform) && binding.path == "" || binding.type == typeof(Animator) && (p.StartsWith("MotionT.") || p.StartsWith("MotionQ.")))
                {
                    var identity = p.EndsWith("Rotation.w") || p == "MotionQ.w" || p.StartsWith("m_LocalScale.") ? 1f : 0f;
                    var neutral = AnimationUtility.GetEditorCurve(result, binding).keys.All(k => k.value == identity &&
                        (k.inTangent == 0 || float.IsInfinity(k.inTangent)) && (k.outTangent == 0 || float.IsInfinity(k.outTangent)));
                    if (!neutral) { Object.DestroyImmediate(result); throw new InvalidOperationException("アバターrootの移動・回転を伴うポーズは未対応です。"); }
                    AnimationUtility.SetEditorCurve(result, binding, null); continue;
                }
                if (binding.type == typeof(Transform) && paths.Contains(binding.path) &&
                    (p.StartsWith("m_LocalScale.") || binding.path != hips && p.StartsWith("m_LocalPosition.")))
                { Object.DestroyImmediate(result); throw new InvalidOperationException("腰以外の位置や骨スケールを変更するポーズは未対応です。"); }
                var allowed = !skipMuscles && binding.type == typeof(Animator) && binding.path == "" &&
                    (muscles.Contains(p) || p.StartsWith("RootT.") || p.StartsWith("RootQ."));
                allowed |= binding.type == typeof(Transform) && paths.Contains(binding.path) &&
                    (p.StartsWith("m_LocalRotation.") || p.StartsWith("localEulerAngles") || binding.path == hips && p.StartsWith("m_LocalPosition."));
                if (!allowed) AnimationUtility.SetEditorCurve(result, binding, null);
                else foreach (var key in AnimationUtility.GetEditorCurve(result, binding).keys)
                    if (!Finite(key.time) || !Finite(key.value)) { Object.DestroyImmediate(result); throw new InvalidOperationException("ポーズのカーブに不正な数値があります。"); }
            }
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(result)) AnimationUtility.SetObjectReferenceCurve(result, binding, null);
            return result;
        }
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        // Transform-bound clips already name the authored local axes. Sending
        // these through a humanoid playable retargets/clamps them a second time.
        // Evaluate Unity curves on the owned skeleton and compose override
        // layers in their authored order instead.
        static void EvaluateTransforms(GameObject root, Animator animator, List<PoseLayer> layers)
        {
            var bones = HumanoidPoseData.BoneNames.Select(n => (name: n, t: animator.GetBoneTransform(HumanBone(n)))).Where(b => b.t != null).ToArray();
            foreach (var group in layers.GroupBy(l => l.Group))
            {
                var before = bones.ToDictionary(b => b.t, b => b.t.localRotation);
                var hips = animator.GetBoneTransform(HumanBodyBones.Hips); var beforeHip = hips.localPosition;
                foreach (var layer in group)
                {
                    if (layer.Additive) throw new InvalidOperationException("加算レイヤーは未対応です。");
                    var curves = AnimationUtility.GetCurveBindings(layer.Clip).Where(b => b.type == typeof(Transform)).ToArray();
                    foreach (var bone in bones)
                    {
                        if (!WritesBone(layer, bone.name, bone.t, root)) continue;
                        var path = AnimationUtility.CalculateTransformPath(bone.t, root.transform);
                        var rotation = bone.t.localRotation; var euler = rotation.eulerAngles; var position = bone.t.localPosition;
                        var hasEuler = false; var hasQuaternion = false;
                        foreach (var binding in curves.Where(b => b.path == path))
                        {
                            var p = binding.propertyName; var axis = "xyzw".IndexOf(p[p.Length - 1]); if (axis < 0) continue;
                            var value = AnimationUtility.GetEditorCurve(layer.Clip, binding).Evaluate(layer.Time);
                            if (!Finite(value)) throw new InvalidOperationException("ポーズの評価値が有限ではありません。");
                            if (p.StartsWith("m_LocalRotation.")) { rotation[axis] = value; hasQuaternion = true; }
                            else if (p.StartsWith("localEulerAngles") && axis < 3) { euler[axis] = value; hasEuler = true; }
                            else if (bone.t == hips && p.StartsWith("m_LocalPosition.") && axis < 3) position[axis] = value;
                        }
                        if (hasEuler && hasQuaternion) throw new InvalidOperationException("同じ骨のEulerとQuaternionカーブの混合は未対応です。");
                        if (hasQuaternion && Quaternion.Dot(rotation, rotation) < 1e-8f) throw new InvalidOperationException("回転カーブがゼロQuaternionになっています。");
                        bone.t.localRotation = Quaternion.Slerp(bone.t.localRotation, hasEuler ? Quaternion.Euler(euler) : rotation.normalized, layer.Weight);
                        if (bone.t == hips) bone.t.localPosition = Vector3.Lerp(bone.t.localPosition, position, layer.Weight);
                    }
                }
                var weight = group.First().GroupWeight;
                foreach (var bone in bones) bone.t.localRotation = Quaternion.Slerp(before[bone.t], bone.t.localRotation, weight);
                hips.localPosition = Vector3.Lerp(beforeHip, hips.localPosition, weight);
            }
        }

        static AnimationClip RestClip(Animator animator, GameObject root)
        {
            var pose = new HumanPose();
            using (var handler = new HumanPoseHandler(animator.avatar, root.transform)) handler.GetHumanPose(ref pose);
            var clip = new AnimationClip();
            for (var i = 0; i < pose.muscles.Length; i++)
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), MuscleProperty(HumanTrait.MuscleName[i])), AnimationCurve.Constant(0, 1, pose.muscles[i]));
            var position = pose.bodyPosition; var rotation = pose.bodyRotation;
            for (var i = 0; i < 3; i++) AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "RootT." + "xyz"[i]), AnimationCurve.Constant(0, 1, position[i]));
            for (var i = 0; i < 4; i++) AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "RootQ." + "xyzw"[i]), AnimationCurve.Constant(0, 1, rotation[i]));
            return clip;
        }
        static AnimationClip TransformRestClip(Animator animator, GameObject root)
        {
            var clip = new AnimationClip();
            foreach (var name in HumanoidPoseData.BoneNames)
            {
                var bone = animator.GetBoneTransform(HumanBone(name)); if (bone == null) continue;
                var path = AnimationUtility.CalculateTransformPath(bone, root.transform);
                for (var i = 0; i < 4; i++) AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(Transform), "m_LocalRotation." + "xyzw"[i]), AnimationCurve.Constant(0, 1, bone.localRotation[i]));
                if (name == "hips") for (var i = 0; i < 3; i++) AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(Transform), "m_LocalPosition." + "xyz"[i]), AnimationCurve.Constant(0, 1, bone.localPosition[i]));
            }
            return clip;
        }
        static string MuscleProperty(string name)
        {
            foreach (var side in new[] { "Left", "Right" })
                foreach (var finger in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
                    if (name.StartsWith(side + " " + finger + " ", StringComparison.Ordinal))
                        return side + "Hand." + finger + "." + name.Substring((side + " " + finger + " ").Length);
            return name;
        }
        internal static AvatarMaskBodyPart Part(string bone)
        {
            var left = bone.StartsWith("left");
            if (bone.Contains("Thumb") || bone.Contains("Index") || bone.Contains("Middle") || bone.Contains("Ring") || bone.Contains("Little"))
                return left ? AvatarMaskBodyPart.LeftFingers : AvatarMaskBodyPart.RightFingers;
            if (bone.Contains("Arm") || bone.Contains("Hand") || bone.Contains("Shoulder")) return left ? AvatarMaskBodyPart.LeftArm : AvatarMaskBodyPart.RightArm;
            if (bone.Contains("Leg") || bone.Contains("Foot") || bone.Contains("Toes")) return left ? AvatarMaskBodyPart.LeftLeg : AvatarMaskBodyPart.RightLeg;
            return AvatarMaskBodyPart.Body;
        }
        static bool MaskAllows(AvatarMask mask, AvatarMaskBodyPart part, string path, bool muscle)
        {
            if (mask == null) return true;
            if (muscle) return mask.GetHumanoidBodyPartActive(part);
            if (mask.transformCount == 0) return true;
            for (var i = 0; i < mask.transformCount; i++) if (mask.GetTransformPath(i) == path) return mask.GetTransformActive(i);
            return false;
        }
        static bool WritesBone(PoseLayer layer, string name, Transform bone, GameObject root)
        {
            if (layer.Weight == 0 || layer.GroupWeight == 0) return false;
            var path = AnimationUtility.CalculateTransformPath(bone, root.transform); var part = Part(name);
            foreach (var binding in AnimationUtility.GetCurveBindings(layer.Clip))
            {
                if (binding.type == typeof(Transform) && binding.path == path &&
                    MaskAllows(layer.Mask, part, path, false) && MaskAllows(layer.OuterMask, part, path, false)) return true;
                if (layer.SkipMuscles || binding.type != typeof(Animator) || binding.path != "") continue;
                if (!MaskAllows(layer.Mask, part, path, true) || !MaskAllows(layer.OuterMask, part, path, true)) continue;
                if (name == "hips" && binding.propertyName.StartsWith("RootQ.")) return true;
                for (var i = 0; i < HumanTrait.MuscleCount; i++)
                    if (MuscleProperty(HumanTrait.MuscleName[i]) == binding.propertyName || HumanTrait.MuscleName[i] == binding.propertyName)
                    {
                        var human = (HumanBodyBones)HumanTrait.BoneFromMuscle(i);
                        var muscleName = char.ToLowerInvariant(human.ToString()[0]) + human.ToString().Substring(1);
                        if (Part(muscleName) == part) return true;
                    }
            }
            return false;
        }
        static bool WritesHipsPosition(PoseLayer layer, string hipsPath) => layer.Weight > 0 && layer.GroupWeight > 0 &&
            AnimationUtility.GetCurveBindings(layer.Clip).Any(b =>
                b.type == typeof(Animator) && !layer.SkipMuscles && b.propertyName.StartsWith("RootT.") &&
                MaskAllows(layer.Mask, AvatarMaskBodyPart.Root, "", true) && MaskAllows(layer.OuterMask, AvatarMaskBodyPart.Root, "", true) ||
                b.type == typeof(Transform) && b.path == hipsPath && b.propertyName.StartsWith("m_LocalPosition.") &&
                MaskAllows(layer.Mask, AvatarMaskBodyPart.Body, hipsPath, false) && MaskAllows(layer.OuterMask, AvatarMaskBodyPart.Body, hipsPath, false));
    }
}
