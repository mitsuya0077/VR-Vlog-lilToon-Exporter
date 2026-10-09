using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    internal sealed partial class ExperimentalExpressionCaptureSession
    {
        internal sealed class ClipInput
        {
            internal AnimationClip Clip;
            internal string Name;
            internal float Time;
            internal bool Selected = true;
            internal string Error;
        }

        internal sealed class ClipRecommendation
        {
            internal AnimationClip Clip;
            internal string Source;
        }

        [Serializable]
        internal sealed class PoseReference
        {
            public string Guid, Name, Category;
            public long LocalId;
            public float Time;
        }

        [Serializable]
        internal sealed class PoseNameReference { public string Id, Name; }

        internal List<ClipRecommendation> RecommendClips()
        {
            RequireValid();
            var result = new List<ClipRecommendation>();
            var seen = new HashSet<AnimationClip>();
            void Add(IEnumerable<AnimationClip> clips, string origin)
            {
                foreach (var clip in clips.Where(clip => clip != null))
                    if (seen.Add(clip) && AnimationUtility.GetCurveBindings(clip).Any(binding =>
                        binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)))
                        result.Add(new ClipRecommendation { Clip = clip, Source = origin });
            }
            Add(faceEmoBindings.RegisteredClips, "FaceEmoの登録");
            var metadata = VrChatExpressionMenu.Read(Source, new VrChatMenuImportPolicy { SkipAll = true });
            var controllers = metadata.OtherControllers.Concat(new[] { metadata.Controller })
                .Concat(Source.GetComponentsInChildren<Animator>(true).Select(animator => animator.runtimeAnimatorController))
                .Where(controller => controller != null).ToHashSet();
            // MergeAnimator and similar authoring components can contribute
            // their own controller before NDMF combines the avatar's FX.
            foreach (var component in Source.GetComponentsInChildren<Component>(true).Where(component => component != null))
            {
                using var serialized = new SerializedObject(component);
                var properties = serialized.GetIterator();
                while (properties.Next(true))
                    if (properties.propertyType == SerializedPropertyType.ObjectReference && properties.objectReferenceValue is RuntimeAnimatorController controller)
                        controllers.Add(controller);
            }
            foreach (var controller in controllers) Add(controller.animationClips, "アバターに登録されたController");
            return result.OrderBy(item => item.Clip.name, StringComparer.Ordinal).ToList();
        }

        internal string ClipError(AnimationClip clip, float time)
        {
            try { BuildClipPose(clip, time, "プレビュー"); return null; }
            catch (InvalidOperationException error) { return error.Message; }
        }

        internal Dictionary<AnimationClip, string> ClipErrors(IEnumerable<AnimationClip> clips)
        {
            RequireValid(); // One geometry/fingerprint check for the complete synchronous batch.
            var result = new Dictionary<AnimationClip, string>();
            foreach (var clip in clips.Where(clip => clip != null).Distinct())
            {
                try { BuildClipPoseUnchecked(clip, clip.length, "プレビュー"); result.Add(clip, null); }
                catch (InvalidOperationException error) { result.Add(clip, error.Message); }
            }
            return result;
        }

        // Read only the explicitly authored morph curves. No Animator graph,
        // callbacks, events, physics or unrelated idle layers participate.
        Pose BuildClipPose(AnimationClip clip, float time, string name)
        {
            RequireValid();
            return BuildClipPoseUnchecked(clip, time, name);
        }

        Pose BuildClipPoseUnchecked(AnimationClip clip, float time, string name)
        {
            if (clip == null) throw new InvalidOperationException(ExporterLocalization.T("UnityのProjectからAnimationClipを指定してください。"));
            if (!Finite(time) || time < 0 || time > clip.length)
                throw new InvalidOperationException(ExporterLocalization.T("収録時刻は0〜") + clip.length.ToString("G9", CultureInfo.InvariantCulture) + ExporterLocalization.T("秒で指定してください。"));
            if (AnimationUtility.GetAnimationEvents(clip).Length != 0)
                throw new InvalidOperationException(ExporterLocalization.T("Animation Eventを含みます。この実験版ではイベント付きの表情は収録できません。"));
            var bindings = AnimationUtility.GetCurveBindings(clip);
            var unsupported = bindings.Where(binding => binding.type != typeof(SkinnedMeshRenderer) ||
                !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                .Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip)).ToArray();
            if (unsupported.Length > 0)
                throw new InvalidOperationException(ExporterLocalization.T("顔の変形以外の変更を含みます。材質・ボーン・表示切替は追加対応が必要です: ") +
                    unsupported[0].path + " / " + unsupported[0].propertyName);
            if (bindings.Length == 0) throw new InvalidOperationException(ExporterLocalization.T("このクリップにはBlendShapeの表情がありません。全身ポーズはポーズ欄へ追加してください。"));
            var pose = ClonePose(Baseline); pose.Name = ValidateName(name);
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in bindings)
            {
                if (!authoringRenderers.TryGetValue(binding.path, out var original))
                    throw new InvalidOperationException(ExporterLocalization.T("元のアバターに対象メッシュがないか、同じ階層に複数あります: ") + binding.path);
                var renderer = preparation.PreparedRendererFor(original);
                var channels = Channels.Where(channel => channel.Renderer == renderer).ToArray();
                if (channels.Length != 1)
                    throw new InvalidOperationException(ExporterLocalization.T("表示中の書き出し対象にメッシュがありません。統合・分割の対応が必要です: ") + binding.path);
                var channel = channels[0]; var shape = binding.propertyName.Substring("blendShape.".Length);
                if (!used.Add(channel.Path + "\n" + shape)) throw new InvalidOperationException(ExporterLocalization.T("変形の参照が重複しています: ") + shape);
                var nativeCurve = AnimationUtility.GetEditorCurve(clip, binding);
                VrChatGestureExpressions.ReadCurve(nativeCurve); // Reuse the finite curve/weighted tangent limits.
                var weight = nativeCurve.Evaluate(time);
                if (!Finite(weight)) throw new InvalidOperationException(ExporterLocalization.T("表情の変形量が有限ではありません: ") + shape);
                Set(pose, channel.Path, shape, weight);
            }
            pose.InstalledSourceId = ClipId(clip, time);
            ValidatePose(pose);
            return pose;
        }

        static string ClipId(AnimationClip clip, float time)
        {
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out string guid, out long localId);
            return "clip/" + (string.IsNullOrEmpty(guid) ? "temporary-" + clip.GetInstanceID() : guid + "/" + localId) +
                "/" + time.ToString("R", CultureInfo.InvariantCulture);
        }

        internal void PreviewClip(AnimationClip clip, float time = 0) => ApplyUnchecked(BuildClipPose(clip, time, "プレビュー"));

        internal ImportResult ImportClips(IEnumerable<ClipInput> inputs)
        {
            RequireValid();
            var result = new ImportResult(); var staged = new List<Pose>();
            var names = new HashSet<string>(Expressions.Select(pose => pose.Name), StringComparer.Ordinal);
            foreach (var input in inputs)
            {
                var pose = BuildClipPoseUnchecked(input.Clip, input.Time, input.Name);
                if (!names.Add(pose.Name)) { result.Skipped.Add(pose.Name + ": 同名の表情を登録済みです。"); continue; }
                if (Expressions.Count + staged.Count >= MaximumExpressions) throw new InvalidOperationException(ExporterLocalization.T("記録できる表情は64件までです。"));
                staged.Add(pose); result.Imported.Add(pose.Name);
            }
            Expressions.AddRange(staged); // Atomic: invalid files never publish a partial batch or change the preview.
            return result;
        }

        // Replace only records owned by this window's file list. Validate the
        // complete batch before changing any record; manual/loaded faces stay.
        internal Pose[] ReplaceClipRecords(IEnumerable<ClipInput> inputs, IEnumerable<Pose> previous)
        {
            RequireValid();
            var replaced = new HashSet<Pose>(previous ?? Array.Empty<Pose>());
            var retained = Expressions.Where(pose => !replaced.Contains(pose)).ToArray();
            var staged = inputs.Select(input => BuildClipPoseUnchecked(input.Clip, input.Time, input.Name)).ToArray();
            if (retained.Length + staged.Length > MaximumExpressions)
                throw new InvalidOperationException(ExporterLocalization.T("記録できる表情は64件までです。"));
            var names = new HashSet<string>(retained.Select(pose => pose.Name), StringComparer.Ordinal);
            foreach (var pose in staged)
                if (!names.Add(pose.Name)) throw new InvalidOperationException(ExporterLocalization.T("表情名が重複しています。別の名前を付けてください: ") + pose.Name);
            Expressions.Clear(); Expressions.AddRange(retained); Expressions.AddRange(staged);
            return staged;
        }

        static PoseReference SavePoseReference(ManualPose pose)
        {
            if (pose.Clip == null || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(pose.Clip, out string guid, out long id) || string.IsNullOrEmpty(guid))
                throw new InvalidOperationException(ExporterLocalization.T("ポーズの元クリップをUnityのProjectへ保存してから設定を保存してください。"));
            return new PoseReference { Guid = guid, LocalId = id, Name = pose.Name, Category = pose.Category, Time = pose.Time };
        }

        static ManualPose LoadPoseReference(PoseReference reference)
        {
            if (reference == null || !Finite(reference.Time) || string.IsNullOrEmpty(reference.Guid))
                throw new InvalidOperationException(ExporterLocalization.T("ポーズの記録設定が不正です。"));
            var clip = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GUIDToAssetPath(reference.Guid)).OfType<AnimationClip>().FirstOrDefault(candidate =>
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(candidate, out string guid, out long id) && id == reference.LocalId);
            if (clip == null || reference.Time < 0 || reference.Time > clip.length)
                throw new InvalidOperationException(ExporterLocalization.T("ポーズの元クリップが見つからないか、時刻が範囲外です: ") + reference.Name);
            return new ManualPose { Clip = clip, Name = reference.Name, Category = reference.Category, Time = reference.Time };
        }

        byte[] InjectSourcePoses(byte[] bytes, ICollection<string> warnings)
        {
            // APL definitions still belong to the original avatar; generated
            // menu poses belong to the prepared copy. Collect both before
            // binding the data to the final VRM's Humanoid nodes.
            using var poses = new PoseExportSession(Source, PoseOptions.Copy(), exclusions.Contains);
            poses.CollectPrepared(Copy, warnings);
            var failed = poses.Entries.FirstOrDefault(entry => entry.Source.Contains("手動") && entry.Error != null && !PoseOptions.Excluded.Contains(entry.Id));
            if (failed != null) throw new InvalidOperationException(ExporterLocalization.T("ポーズを書き出せません: ") + failed.Name + " / " + failed.Error);
            var document = GlbDocument.Read(bytes);
            var extensions = (Dictionary<string, object>)document.Json["extensions"];
            var vrm = (Dictionary<string, object>)extensions["VRMC_vrm"];
            var bones = (Dictionary<string, object>)((Dictionary<string, object>)vrm["humanoid"])["humanBones"];
            int Node(string name)
            {
                if (!bones.TryGetValue(name, out var value)) throw new InvalidOperationException(ExporterLocalization.T("出力VRMにポーズの骨がありません: ") + name);
                return Convert.ToInt32(((Dictionary<string, object>)value)["node"], CultureInfo.InvariantCulture);
            }
            foreach (var pose in poses.Selected()) foreach (var bone in pose.Bones) bone.Node = Node(bone.Name);
            foreach (var animation in poses.SelectedAnimations()) foreach (var bone in animation.Bones) bone.Node = Node(bone.Name);
            return poses.Inject(bytes);
        }
    }
}
