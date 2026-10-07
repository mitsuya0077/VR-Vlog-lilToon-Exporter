using System;
using System.Collections.Generic;
using System.Globalization;
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
        internal bool IsAnimation;
        internal readonly List<PoseLayer> Layers = new List<PoseLayer>();
        internal HumanoidPoseData Data;
        internal HumanoidAnimationData Animation;
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
        static readonly string[] AnimationBoneNames = HumanoidPoseData.BoneNames.Concat(new[] { "neck", "head" }).ToArray();
        internal static HumanBodyBones HumanBone(string name)
        {
            // VRM 1.0 corrected thumb names; Unity retains the older names.
            name = name.Replace("ThumbProximal", "ThumbIntermediate").Replace("ThumbMetacarpal", "ThumbProximal");
            return (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), name, true);
        }
        internal static Quaternion Reflect(Quaternion q) => new Quaternion(q.x, -q.y, -q.z, q.w);
        internal static Vector3 Reflect(Vector3 v) => new Vector3(-v.x, v.y, v.z);
        internal static HashSet<string> BodyPaths(GameObject root, bool includeHead = false)
        {
            var animator = root.GetComponent<Animator>();
            if (animator == null || !animator.isHuman) return new HashSet<string>();
            return new HashSet<string>((includeHead ? AnimationBoneNames : HumanoidPoseData.BoneNames).Select(n => animator.GetBoneTransform(HumanBone(n)))
                .Where(t => t != null).Select(t => AnimationUtility.CalculateTransformPath(t, root.transform)));
        }
        internal static bool HasEffectiveBody(GameObject root, PoseLayer layer)
        {
            var animator = root.GetComponent<Animator>();
            if (layer.Clip == null || animator == null || !animator.isHuman) return false;
            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            return HumanoidPoseData.BoneNames.Any(name =>
            {
                var bone = animator.GetBoneTransform(HumanBone(name));
                return bone != null && WritesBone(layer, name, bone, root);
            }) || hips != null && WritesHipsPosition(layer, AnimationUtility.CalculateTransformPath(hips, root.transform));
        }
        internal static IEnumerable<EditorCurveBinding> EffectiveBodyBindings(GameObject root, PoseLayer layer, bool includeHead = false)
        {
            var animator = root.GetComponent<Animator>();
            if (layer.Clip == null || animator == null || !animator.isHuman) yield break;
            var bones = (includeHead ? AnimationBoneNames : HumanoidPoseData.BoneNames).Select(n => (name: n, human: HumanBone(n), bone: animator.GetBoneTransform(HumanBone(n)))).Where(b => b.bone != null).ToArray();
            var paths = bones.ToDictionary(b => AnimationUtility.CalculateTransformPath(b.bone, root.transform), b => b.name);
            foreach (var binding in AnimationUtility.GetCurveBindings(layer.Clip))
            {
                var muscle = binding.type == typeof(Animator);
                AvatarMaskBodyPart part;
                if (muscle)
                {
                    if (layer.SkipMuscles || binding.path != "" || !(includeHead ? AnimationMuscles : BodyMuscles).Contains(binding.propertyName)) continue;
                    if (binding.propertyName.StartsWith("RootT.") || binding.propertyName.StartsWith("RootQ.")) part = AvatarMaskBodyPart.Root;
                    else
                    {
                        var index = Array.FindIndex(HumanTrait.MuscleName, n => MuscleProperty(n) == binding.propertyName || n == binding.propertyName);
                        var name = index < 0 ? null : bones.FirstOrDefault(b => b.human == (HumanBodyBones)HumanTrait.BoneFromMuscle(index)).name;
                        if (name == null) continue;
                        part = Part(name);
                    }
                }
                else
                {
                    if (binding.type != typeof(Transform) || !paths.TryGetValue(binding.path, out var name) ||
                        !(binding.propertyName.StartsWith("m_LocalRotation.") || binding.propertyName.StartsWith("localEulerAngles") || binding.propertyName.StartsWith("m_LocalPosition.") || binding.propertyName.StartsWith("m_LocalScale."))) continue;
                    part = Part(name);
                }
                if (MaskAllows(layer.Mask, part, binding.path, muscle) && MaskAllows(layer.OuterMask, part, binding.path, muscle)) yield return binding;
            }
        }
        internal static string Hash(string text)
        { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant(); }

        internal static bool Moving(GameObject root, PoseLayer layer, bool includeHead = false)
        {
            if (layer.Weight == 0 || layer.GroupWeight == 0) return false;
            foreach (var binding in EffectiveBodyBindings(root, layer, includeHead))
            {
                var keys = AnimationUtility.GetEditorCurve(layer.Clip, binding).keys;
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
            if (candidate.IsAnimation) parts.Append("|animation:v1");
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

        internal static HumanoidPoseData Sample(GameObject avatar, PoseCandidate candidate) => SampleOwned(avatar, candidate, false, null);

        internal static HumanoidAnimationData SampleAnimation(GameObject avatar, PoseCandidate candidate,
            int maximumFrames = HumanoidAnimationData.MaximumFramesPerAnimation)
        {
            if (candidate.Layers.Count != 1 || candidate.Layers[0].Clip == null)
                throw new InvalidOperationException("動くポーズは手動追加した一つのクリップを指定してください。");
            var layer = candidate.Layers[0];
            var start = (double)layer.Time; var duration = layer.Clip.length - start;
            if (!Finite(layer.Time) || start < 0 || duration <= 0 || duration > 600 || start > 600)
                throw new InvalidOperationException("動くポーズの開始秒はクリップ末尾より前、長さは0秒より長く600秒以下である必要があります。");
            var data = new HumanoidAnimationData { Id = candidate.Id, Name = candidate.Name, Category = candidate.Category,
                Source = candidate.Source, SourceStartTime = start, Duration = duration, Loop = layer.Clip.isLooping };
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            maximumFrames = Math.Min(maximumFrames, HumanoidAnimationData.MaximumFramesPerAnimation);
            void CheckBudget(int frames)
            {
                if (frames > maximumFrames)
                    throw new InvalidOperationException(maximumFrames < HumanoidAnimationData.MaximumFramesPerAnimation
                        ? "動くポーズの合計フレーム数が32768を超えました。不要な項目を除外してください。"
                        : "動くポーズのフレーム数が8192を超えました。クリップの長さやカーブの密度を減らしてください。");
                if (deadline.Elapsed.TotalSeconds > 30)
                    throw new InvalidOperationException("動くポーズの解析が30秒を超えました。クリップの長さやカーブの密度を減らしてください。");
            }
            CheckBudget((int)Math.Ceiling(duration * 60) + 1);
            SampleOwned(avatar, candidate, true, (sample, copy, activeLayers) =>
            {
                var times = new SortedSet<double> { 0, duration };
                var steps = new Dictionary<double, double>();
                var uniformFrames = (int)Math.Ceiling(duration * 60);
                CheckBudget(uniformFrames + 1);
                // Unity samples source curves at float timestamps. Match that
                // grid before merging it with authored keys, so e.g. 0.3 and
                // 0.3f do not become two samples on opposite sides of a STEP.
                for (var i = 1; i < uniformFrames; i++) times.Add((double)(float)(start + i / 60.0) - start);
                foreach (var binding in EffectiveBodyBindings(copy, layer, true))
                {
                    var curve = AnimationUtility.GetEditorCurve(layer.Clip, binding);
                    var keys = curve.keys;
                    for (var i = 0; i < keys.Length; i++)
                    {
                        var time = (double)keys[i].time - start;
                        if (time < 0 || time > duration) continue;
                        times.Add(time);
                        if (time > 0 && i > 0 && keys[i].value != keys[i - 1].value)
                        {
                            var bytes = BitConverter.GetBytes(keys[i].time);
                            var before = BitConverter.ToSingle(BitConverter.GetBytes(BitConverter.ToInt32(bytes, 0) - 1), 0);
                            if (float.IsInfinity(keys[i - 1].outTangent) || float.IsInfinity(keys[i].inTangent) ||
                                NativeKeyDiscontinuity(curve, keys[i - 1], keys[i], before))
                            {
                                // Both explicit STEP tangents and a verified
                                // native evaluator discontinuity need the left
                                // limit and exact-key value at the same time.
                                steps[time] = Math.Max(0, (double)before - start);
                            }
                        }
                    }
                }
                CheckBudget(times.Count + steps.Count);
                var eulerCurves = EffectiveBodyBindings(copy, layer, true)
                    .Where(binding => binding.type == typeof(Transform) && binding.propertyName.StartsWith("localEulerAngles"))
                    .Select(binding => (binding, curve: AnimationUtility.GetEditorCurve(layer.Clip, binding))).ToArray();
                HumanoidAnimationData.Frame Capture(double time, double sampleTime)
                {
                    CheckBudget(data.Frames.Count + 1);
                    var pose = sample(sampleTime);
                    if (data.Bones.Count == 0)
                        foreach (var bone in pose.Bones) data.Bones.Add(new HumanoidAnimationData.Bone { Name = bone.Name, Node = bone.Node });
                    return new HumanoidAnimationData.Frame { Time = time, HipsOffset = pose.HipsOffset,
                        Rotations = pose.Bones.Select(bone => bone.Rotation).ToArray() };
                }
                void Add(HumanoidAnimationData.Frame frame)
                {
                    CheckBudget(data.Frames.Count + 1);
                    if (data.Frames.Count > 0)
                        for (var i = 0; i < frame.Rotations.Length; i++)
                        {
                            var previous = data.Frames[data.Frames.Count - 1].Rotations[i]; var next = frame.Rotations[i];
                            if (previous.Select((value, axis) => value * next[axis]).Sum() < 0)
                                for (var axis = 0; axis < 4; axis++) next[axis] = -next[axis];
                        }
                    data.Frames.Add(frame);
                }
                bool Matches(HumanoidAnimationData.Frame left, HumanoidAnimationData.Frame right,
                    HumanoidAnimationData.Frame actual, double blend)
                {
                    var expectedHip = Vector3.Lerp(Vector(left.HipsOffset), Vector(right.HipsOffset), (float)blend);
                    if (Vector3.Distance(expectedHip, Vector(actual.HipsOffset)) > .0005f) return false;
                    for (var i = 0; i < actual.Rotations.Length; i++)
                        if (Quaternion.Angle(Quaternion.Slerp(Rotation(left.Rotations[i]), Rotation(right.Rotations[i]), (float)blend),
                            Rotation(actual.Rotations[i])) > .15f) return false;
                    return true;
                }
                void Refine(HumanoidAnimationData.Frame left, HumanoidAnimationData.Frame right, int depth, double sampleLimit)
                {
                    var middleTime = (left.Time + right.Time) * .5;
                    // Probes approaching a STEP key can round to the key's
                    // right value. Its preceding interval must sample only
                    // through the last representable source time on the left.
                    HumanoidAnimationData.Frame Probe(double time) => Capture(time, Math.Min(time, sampleLimit));
                    var quarter = Probe(left.Time + (right.Time - left.Time) * .25);
                    var middle = Probe(middleTime);
                    var threeQuarter = Probe(left.Time + (right.Time - left.Time) * .75);
                    // Quaternion samples can alias complete turns to identity.
                    // Keep the authored, unwrapped Euler channel changes below
                    // 90 degrees between probes even when all quaternions agree.
                    var probeTimes = new[] { left.Time, quarter.Time, middle.Time, threeQuarter.Time, right.Time }
                        .Select(time => Math.Min(time, sampleLimit)).ToArray();
                    var largeEulerChange = eulerCurves.Any(channel =>
                    {
                        var previous = channel.curve.Evaluate((float)(start + probeTimes[0]));
                        for (var i = 1; i < probeTimes.Length; i++)
                        {
                            var value = channel.curve.Evaluate((float)(start + probeTimes[i]));
                            if (Math.Abs(value - previous) > 90) return true;
                            previous = value;
                        }
                        return false;
                    });
                    if (!largeEulerChange && Matches(left, right, quarter, .25) && Matches(left, right, middle, .5) && Matches(left, right, threeQuarter, .75)) return;
                    // Time is relative to the clip, but Unity evaluates curves
                    // on the source float grid. A short interval near zero can
                    // still contain millions of distinct timestamps (authored
                    // humanoid clips commonly have an initial key at 2^-24).
                    // Stop only when the native evaluator cannot subdivide it,
                    // rather than rejecting all intervals shorter than 1 us.
                    var sourceLeft = (float)(start + Math.Min(left.Time, sampleLimit));
                    var sourceMiddle = (float)(start + Math.Min(middleTime, sampleLimit));
                    var sourceRight = (float)(start + Math.Min(right.Time, sampleLimit));
                    if (depth >= 12 || sourceMiddle <= sourceLeft || sourceMiddle >= sourceRight)
                    {
                        // Report the actual failed oracle comparison, not just
                        // a generic suggestion to edit an otherwise valid clip.
                        // Keep this extra scan on the failure path only.
                        var score = 0.0; var metric = ""; var bone = ""; var binding = ""; var unit = "";
                        var errorValue = 0.0; var limit = 0.0; var sampleTime = 0.0; var blend = 0.0;
                        void Record(string kind, string name, string curveBinding, double value, double tolerance,
                            string suffix, double time, double amount)
                        {
                            if (value / tolerance <= score) return;
                            score = value / tolerance; metric = kind; bone = name; binding = curveBinding;
                            errorValue = value; limit = tolerance; unit = suffix;
                            sampleTime = (float)(start + Math.Min(time, sampleLimit)); blend = amount;
                        }
                        foreach (var probe in new[] { quarter, middle, threeQuarter })
                        {
                            var amount = (probe.Time - left.Time) / (right.Time - left.Time);
                            Record("hipsError", "hips", "", Vector3.Distance(
                                Vector3.Lerp(Vector(left.HipsOffset), Vector(right.HipsOffset), (float)amount),
                                Vector(probe.HipsOffset)), .0005, "m", probe.Time, amount);
                            for (var i = 0; i < probe.Rotations.Length; i++)
                                Record("rotationError", data.Bones[i].Name, "", Quaternion.Angle(
                                    Quaternion.Slerp(Rotation(left.Rotations[i]), Rotation(right.Rotations[i]), (float)amount),
                                    Rotation(probe.Rotations[i])), .15, "deg", probe.Time, amount);
                        }
                        foreach (var channel in eulerCurves)
                        {
                            var animator = copy.GetComponent<Animator>();
                            var name = data.Bones.First(b => AnimationUtility.CalculateTransformPath(
                                animator.GetBoneTransform(HumanBone(b.Name)), copy.transform) == channel.binding.path).Name;
                            var previous = channel.curve.Evaluate((float)(start + probeTimes[0]));
                            for (var i = 1; i < probeTimes.Length; i++)
                            {
                                var value = channel.curve.Evaluate((float)(start + probeTimes[i]));
                                Record("eulerChange", name, channel.binding.path + "/" + channel.binding.propertyName,
                                    Math.Abs(value - previous), 90, "deg", probeTimes[i],
                                    (probeTimes[i] - left.Time) / (right.Time - left.Time));
                                previous = value;
                            }
                        }
                        string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
                        var error = new InvalidOperationException(ExporterLocalization.T(
                            "動くポーズのカーブを十分な精度で保存できません。カーブの変化を緩やかにしてください。") +
                            " [bone=" + bone + "; sourceTime=" + Number(sampleTime) + "s; sourceInterval=[" +
                            Number(start + left.Time) + ", " + Number(start + right.Time) + "]s; " + metric + "=" +
                            Number(errorValue) + unit + "; limit=" + Number(limit) + unit +
                            (binding.Length == 0 ? "" : "; binding=" + binding) + "]");
                        error.Data["bone"] = bone; error.Data["metric"] = metric; error.Data["binding"] = binding;
                        error.Data["sourceTime"] = sampleTime; error.Data["sourceIntervalStart"] = start + left.Time;
                        error.Data["sourceIntervalEnd"] = start + right.Time; error.Data["sampleLimit"] = start + sampleLimit;
                        error.Data["error"] = errorValue; error.Data["limit"] = limit; error.Data["blend"] = blend;
                        throw error;
                    }
                    Refine(left, middle, depth + 1, sampleLimit); Add(middle); Refine(middle, right, depth + 1, sampleLimit);
                }
                var first = Capture(0, 0); Add(first);
                foreach (var time in times.Where(time => time > 0))
                {
                    var previous = data.Frames[data.Frames.Count - 1];
                    var right = Capture(time, time);
                    var stepped = steps.TryGetValue(time, out var before);
                    var leftLimit = stepped ? Capture(time, before) : right;
                    Refine(previous, leftLimit, 0, stepped ? before : time); Add(leftLimit);
                    if (!ReferenceEquals(leftLimit, right)) Add(right);
                }
            });
            HumanoidAnimationData.Write(new[] { data });
            return data;
        }

        static Vector3 Vector(double[] value) => new Vector3((float)value[0], (float)value[1], (float)value[2]);
        static Quaternion Rotation(double[] value) => new Quaternion((float)value[0], (float)value[1], (float)value[2], (float)value[3]);

        static bool NativeKeyDiscontinuity(AnimationCurve curve, Keyframe left, Keyframe right, float before)
        {
            // Unity can collapse a very short finite-tangent Hermite interval
            // toward its left value, then jump at the exact right key. Authored
            // humanoid clips contain such intervals at 0 -> 2^-24 seconds.
            // Distinguish that native jump from a steep continuous curve using
            // an upper bound on its derivative, rather than a time threshold.
            if (!Finite(left.outTangent) || !Finite(right.inTangent) ||
                (left.weightedMode & WeightedMode.Out) != 0 || (right.weightedMode & WeightedMode.In) != 0 || before <= left.time) return false;
            var span = (double)right.time - left.time;
            var derivative = 1.5 * Math.Abs((double)right.value - left.value) / span + Math.Abs((double)left.outTangent) + Math.Abs((double)right.inTangent);
            var beforeBits = BitConverter.ToInt32(BitConverter.GetBytes(before), 0);
            var earlier = BitConverter.ToSingle(BitConverter.GetBytes(beforeBits - 1), 0);
            if (earlier < left.time) return false;
            var a = (double)curve.Evaluate(earlier); var b = (double)curve.Evaluate(before); var c = (double)curve.Evaluate(right.time);
            // The conservative margin also leaves poorly resolved fast curves
            // to the normal precision check. Never invent a jump to make those
            // pass. The preceding segment must remain within the same bound.
            var rounding = Math.Max(Math.Max(Math.Abs(a), Math.Abs(b)), Math.Abs(c)) * 0.000001;
            return Math.Abs(c - b) > 64 * derivative * ((double)right.time - before) + rounding &&
                Math.Abs(b - a) <= 64 * derivative * ((double)before - earlier) + rounding;
        }

        static HumanoidPoseData SampleOwned(GameObject avatar, PoseCandidate candidate, bool includeHead,
            Action<Func<double, HumanoidPoseData>, GameObject, List<PoseLayer>> sampleAnimation)
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
                var allBones = AnimationBoneNames.Select(name => (name, bone: animator.GetBoneTransform(HumanBone(name))))
                    .Where(b => b.bone != null).ToArray();
                var bones = allBones.Where(b => HumanoidPoseData.BoneNames.Contains(b.name) || includeHead &&
                    candidate.Layers.Any(layer => layer.Clip != null && WritesBone(layer, b.name, b.bone, copy))).ToArray();
                var rotations = bones.ToDictionary(b => b.name, b => b.bone.rotation);
                var locals = allBones.ToDictionary(b => b.name, b => b.bone.localRotation);
                var positions = allBones.ToDictionary(b => b.name, b => b.bone.localPosition);
                var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                var hipPosition = hips.position;
                animator.runtimeAnimatorController = null; animator.enabled = true;
                animator.fireEvents = false; animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                copy.SetActive(true);
                graph = PlayableGraph.Create("VR Vlog pose sampling"); graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                // Empty humanoid streams evaluate Unity's muscle defaults,
                // which need not equal the avatar's authored rest (notably
                // fingers). Supply its actual rest as the bottom layer.
                var bodyClips = new Dictionary<PoseLayer, AnimationClip>();
                foreach (var layer in candidate.Layers)
                {
                    if (layer.Clip == null || !Finite(layer.Time) || layer.Time < 0 || layer.Time > 600 || layer.Time > layer.Clip.length)
                        throw new InvalidOperationException("クリップまたは採用時刻が不正です（0秒〜クリップ末尾、最大600秒）。");
                    if (layer.Weight == 0 || layer.GroupWeight == 0) continue;
                    var effective = new HashSet<EditorCurveBinding>(EffectiveBodyBindings(copy, layer, includeHead));
                    if (effective.Count == 0) continue;
                    var clean = BodyClip(copy, animator, layer.Clip, layer.SkipMuscles, effective, includeHead); clips.Add(clean);
                    if (includeHead)
                    {
                        var settings = AnimationUtility.GetAnimationClipSettings(clean);
                        // Disabling looping also disables native Loop Pose
                        // correction. Keep corrected loops intact; a regular
                        // loop instead captures its authored end before wrap.
                        if (!settings.loopBlend)
                        { settings.loopTime = false; AnimationUtility.SetAnimationClipSettings(clean, settings); }
                    }
                    if (AnimationUtility.GetCurveBindings(clean).Length != 0) bodyClips.Add(layer, clean);
                }
                var activeLayers = candidate.Layers.Where(bodyClips.ContainsKey).ToList();
                if (activeLayers.Count == 0) throw new InvalidOperationException("有効なマスク・重みで書き出すHumanoidの体・手足・指のカーブがありません。");
                var hasMuscles = bodyClips.Values.Any(c => AnimationUtility.GetCurveBindings(c).Any(b => b.type == typeof(Animator)));
                var hasTransforms = bodyClips.Values.Any(c => AnimationUtility.GetCurveBindings(c).Any(b => b.type == typeof(Transform)));
                if (hasMuscles && hasTransforms) throw new InvalidOperationException("HumanoidカーブとTransformカーブの混合は未対応です。");
                if (hasMuscles && activeLayers.Count > 1)
                    throw new InvalidOperationException("複数Humanoidクリップの筋肉カーブ合成は未対応です。");
                var groups = activeLayers.GroupBy(l => l.Group).ToArray();
                var mixer = AnimationLayerMixerPlayable.Create(graph, groups.Length + 1);
                var output = AnimationPlayableOutput.Create(graph, "Pose", animator); output.SetSourcePlayable(mixer);
                var restHumanPose = new HumanPose();
                if (hasMuscles) using (var handler = new HumanPoseHandler(animator.avatar, copy.transform)) handler.GetHumanPose(ref restHumanPose);
                var baseline = hasMuscles ? RestClip(animator, copy) : TransformRestClip(animator, copy, includeHead);
                clips.Add(baseline);
                AnimationClipPlayable Baseline()
                {
                    var basePlayable = AnimationClipPlayable.Create(graph, baseline);
                    basePlayable.SetApplyFootIK(false); basePlayable.SetApplyPlayableIK(false); basePlayable.SetSpeed(0);
                    return basePlayable;
                }
                graph.Connect(Baseline(), 0, mixer, 0); mixer.SetInputWeight(0, 1);
                // Real layers sit above the rest input so even a single layer
                // retains its authored fractional weight.
                var any = false;
                var playables = new Dictionary<PoseLayer, AnimationClipPlayable>();
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
                        var clean = bodyClips[layer];
                        any |= AnimationUtility.GetCurveBindings(clean).Length > 0 &&
                            (bones.Any(b => WritesBone(layer, b.name, b.bone, copy)) ||
                             WritesHipsPosition(layer, AnimationUtility.CalculateTransformPath(hips, copy.transform)));
                        var playable = AnimationClipPlayable.Create(graph, clean);
                        playable.SetApplyFootIK(false); playable.SetApplyPlayableIK(false); playable.SetSpeed(0); playable.SetTime(layer.Time);
                        playables.Add(layer, playable);
                        graph.Connect(playable, 0, inner, i + 1); inner.SetInputWeight(i + 1, layer.Weight);
                        inner.SetLayerAdditive((uint)(i + 1), layer.Additive);
                        if (layer.Mask != null) inner.SetLayerMaskFromAvatarMask((uint)(i + 1), layer.Mask);
                    }
                }
                if (!any) throw new InvalidOperationException("有効なマスク・重みで書き出すHumanoidの体・手足・指のカーブがありません。");
                var originalTimes = activeLayers.ToDictionary(layer => layer, layer => layer.Time);
                var writtenBones = allBones.ToDictionary(b => b.name, b => activeLayers.Any(l => WritesBone(l, b.name, b.bone, copy)));
                // Humanoid body rotation also moves hips around its body
                // pivot. Preserve that native offset in an animation, even
                // when the clip has RootQ without an explicit RootT channel.
                var writesHips = activeLayers.Any(l => WritesHipsPosition(l, AnimationUtility.CalculateTransformPath(hips, copy.transform), includeHead));
                graph.Play();
                HumanoidPoseData CaptureAt(double elapsed)
                {
                    foreach (var b in allBones)
                    { b.bone.localRotation = locals[b.name]; b.bone.localPosition = positions[b.name]; }
                    foreach (var layer in activeLayers)
                    {
                        layer.Time = (float)(originalTimes[layer] + elapsed);
                        var playable = playables[layer]; playable.SetTime(layer.Time); playable.SetDone(false);
                    }
                    try
                    {
                        if (hasTransforms) EvaluateTransforms(copy, animator, activeLayers, includeHead);
                        else { graph.Evaluate(0); ApplyHumanoidRoot(animator, copy, restHumanPose, activeLayers, includeHead); }
                        // A clip playable can populate unbound humanoid channels with
                        // defaults. Retain the exact authored locals outside its body
                        // parts, and outside every effective mask, before capturing.
                        foreach (var b in allBones)
                            if ((!includeHead && (b.name == "neck" || b.name == "head")) || !writtenBones[b.name]) b.bone.localRotation = locals[b.name];
                        if (!writesHips) hips.position = hipPosition;
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
                            data.Bones.Add(new HumanoidPoseData.Bone { Name = b.name, Node = Array.IndexOf(includeHead ? AnimationBoneNames : HumanoidPoseData.BoneNames, b.name),
                                Rotation = new double[] { delta.x, delta.y, delta.z, delta.w } });
                        }
                        if (!includeHead) HumanoidPoseData.Write(new[] { data });
                        return data;
                    }
                    finally { foreach (var layer in activeLayers) layer.Time = originalTimes[layer]; }
                }
                if (sampleAnimation != null) { sampleAnimation(CaptureAt, copy, activeLayers); return null; }
                return CaptureAt(0);
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

        internal static AnimationClip BodyClip(GameObject root, Animator animator, AnimationClip source, bool skipMuscles = false, ISet<EditorCurveBinding> effective = null, bool includeHead = false)
        {
            var result = Object.Instantiate(source); result.name = source.name;
            // All events and non-pose curves are removed from this owned clip.
            AnimationUtility.SetAnimationEvents(result, Array.Empty<AnimationEvent>());
            var paths = BodyPaths(root, includeHead);
            var hips = AnimationUtility.CalculateTransformPath(animator.GetBoneTransform(HumanBodyBones.Hips), root.transform);
            foreach (var binding in AnimationUtility.GetCurveBindings(result))
            {
                var p = binding.propertyName;
                var bodyBinding = binding.type == typeof(Transform) && paths.Contains(binding.path) ||
                    binding.type == typeof(Animator) && binding.path == "" && (includeHead ? AnimationMuscles : BodyMuscles).Contains(p);
                if (effective != null && bodyBinding && !effective.Contains(binding))
                { AnimationUtility.SetEditorCurve(result, binding, null); continue; }
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
                    (includeHead ? AnimationMuscles : BodyMuscles).Contains(p);
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

        static void ApplyHumanoidRoot(Animator animator, GameObject root, HumanPose baseline, List<PoseLayer> layers, bool preserveUnwrittenBodyChannels = false)
        {
            // RootQ/RootT are humanoid body coordinates. With root motion
            // disabled, a clip playable can extract them instead of moving the
            // body. Transfer those channels through HumanPose explicitly;
            // never move/rotate the avatar's scene placement transform.
            foreach (var layer in layers)
            {
                if (layer.SkipMuscles || layer.Weight <= 0 || layer.GroupWeight <= 0 ||
                    !MaskAllows(layer.Mask, AvatarMaskBodyPart.Root, "", true) || !MaskAllows(layer.OuterMask, AvatarMaskBodyPart.Root, "", true)) continue;
                var position = baseline.bodyPosition; var rotation = baseline.bodyRotation;
                var hasPosition = false; var hasRotation = false;
                foreach (var b in AnimationUtility.GetCurveBindings(layer.Clip).Where(b => b.type == typeof(Animator) && b.path == ""))
                {
                    var p = b.propertyName; var axis = "xyzw".IndexOf(p[p.Length - 1]);
                    if (axis < 0 || !IsBodyMuscle(p) || !(p.StartsWith("RootT.") || p.StartsWith("RootQ."))) continue;
                    var value = AnimationUtility.GetEditorCurve(layer.Clip, b).Evaluate(layer.Time);
                    if (!Finite(value)) throw new InvalidOperationException("HumanoidのRoot評価値が有限ではありません。");
                    if (p.StartsWith("RootT.") && axis < 3) { position[axis] = value; hasPosition = true; }
                    else if (p.StartsWith("RootQ.")) { rotation[axis] = value; hasRotation = true; }
                }
                if (!hasPosition && !hasRotation) continue;
                if (hasRotation && Quaternion.Dot(rotation, rotation) < 1e-8f) throw new InvalidOperationException("HumanoidのRoot回転が不正です。");
                using var handler = new HumanPoseHandler(animator.avatar, root.transform);
                var pose = new HumanPose(); handler.GetHumanPose(ref pose);
                var weight = layer.Weight * layer.GroupWeight;
                if (hasPosition || preserveUnwrittenBodyChannels) pose.bodyPosition = Vector3.Lerp(baseline.bodyPosition, position, weight);
                if (hasRotation || preserveUnwrittenBodyChannels) pose.bodyRotation = Quaternion.Slerp(baseline.bodyRotation, rotation.normalized, weight);
                handler.SetHumanPose(ref pose);
            }
        }

        // Transform-bound clips already name the authored local axes. Sending
        // these through a humanoid playable retargets/clamps them a second time.
        // Evaluate Unity curves on the owned skeleton and compose override
        // layers in their authored order instead.
        static void EvaluateTransforms(GameObject root, Animator animator, List<PoseLayer> layers, bool includeHead = false)
        {
            var bones = (includeHead ? AnimationBoneNames : HumanoidPoseData.BoneNames).Select(n => (name: n, t: animator.GetBoneTransform(HumanBone(n)))).Where(b => b.t != null).ToArray();
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
        static AnimationClip TransformRestClip(Animator animator, GameObject root, bool includeHead = false)
        {
            var clip = new AnimationClip();
            foreach (var name in includeHead ? AnimationBoneNames : HumanoidPoseData.BoneNames)
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
        static readonly HashSet<string> BodyMuscles = BuildBodyMuscles(HumanoidPoseData.BoneNames);
        static readonly HashSet<string> AnimationMuscles = BuildBodyMuscles(AnimationBoneNames);
        static HashSet<string> BuildBodyMuscles(IEnumerable<string> names)
        {
            var bones = new HashSet<HumanBodyBones>(names.Select(HumanBone));
            var result = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < HumanTrait.MuscleCount; i++)
                if (bones.Contains((HumanBodyBones)HumanTrait.BoneFromMuscle(i)))
                { result.Add(HumanTrait.MuscleName[i]); result.Add(MuscleProperty(HumanTrait.MuscleName[i])); }
            foreach (var axis in "xyz") result.Add("RootT." + axis);
            foreach (var axis in "xyzw") result.Add("RootQ." + axis);
            return result;
        }
        internal static bool IsBodyMuscle(string property) => BodyMuscles.Contains(property);
        internal static AvatarMaskBodyPart Part(string bone)
        {
            if (bone == "neck" || bone == "head") return AvatarMaskBodyPart.Head;
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
        internal static bool WritesBone(PoseLayer layer, string name, Transform bone, GameObject root)
        {
            if (layer.Weight == 0 || layer.GroupWeight == 0) return false;
            var path = AnimationUtility.CalculateTransformPath(bone, root.transform); var part = Part(name);
            foreach (var binding in AnimationUtility.GetCurveBindings(layer.Clip))
            {
                if (binding.type == typeof(Transform) && binding.path == path &&
                    (binding.propertyName.StartsWith("m_LocalRotation.") || binding.propertyName.StartsWith("localEulerAngles") || name == "hips" && binding.propertyName.StartsWith("m_LocalPosition.")) &&
                    MaskAllows(layer.Mask, part, path, false) && MaskAllows(layer.OuterMask, part, path, false)) return true;
                if (layer.SkipMuscles || binding.type != typeof(Animator) || binding.path != "" || !AnimationMuscles.Contains(binding.propertyName)) continue;
                if (name == "hips" && binding.propertyName.StartsWith("RootQ."))
                {
                    if (MaskAllows(layer.Mask, AvatarMaskBodyPart.Root, "", true) && MaskAllows(layer.OuterMask, AvatarMaskBodyPart.Root, "", true)) return true;
                    continue;
                }
                if (!MaskAllows(layer.Mask, part, path, true) || !MaskAllows(layer.OuterMask, part, path, true)) continue;
                for (var i = 0; i < HumanTrait.MuscleCount; i++)
                    if (MuscleProperty(HumanTrait.MuscleName[i]) == binding.propertyName || HumanTrait.MuscleName[i] == binding.propertyName)
                    {
                        var human = (HumanBodyBones)HumanTrait.BoneFromMuscle(i);
                        if (HumanBone(name) == human) return true;
                    }
            }
            return false;
        }
        static bool WritesHipsPosition(PoseLayer layer, string hipsPath, bool includeBodyRotation = false) => layer.Weight > 0 && layer.GroupWeight > 0 &&
            AnimationUtility.GetCurveBindings(layer.Clip).Any(b =>
                b.type == typeof(Animator) && b.path == "" && !layer.SkipMuscles && IsBodyMuscle(b.propertyName) &&
                (b.propertyName.StartsWith("RootT.") || includeBodyRotation && b.propertyName.StartsWith("RootQ.")) &&
                MaskAllows(layer.Mask, AvatarMaskBodyPart.Root, "", true) && MaskAllows(layer.OuterMask, AvatarMaskBodyPart.Root, "", true) ||
                b.type == typeof(Transform) && b.path == hipsPath && b.propertyName.StartsWith("m_LocalPosition.") &&
                MaskAllows(layer.Mask, AvatarMaskBodyPart.Body, hipsPath, false) && MaskAllows(layer.OuterMask, AvatarMaskBodyPart.Body, hipsPath, false));
    }
}
