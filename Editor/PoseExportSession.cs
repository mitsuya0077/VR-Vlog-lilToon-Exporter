using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UniGLTF;
using UniVRM10;
using UnityEditor;
using UnityEngine;
using VRVlog.Poses;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter
{
    internal sealed class PoseExportSession : IDisposable
    {
        internal const string AplType = "com.hhotatea.avatar_pose_library.component.AvatarPoseLibrary";
        internal readonly List<PoseCandidate> Entries = new List<PoseCandidate>();
        readonly List<Object> owned = new List<Object>();
        readonly PoseExportOptions options;
        readonly VrChatMenuImportPolicy menuPolicy;
        GameObject prepared;

        internal PoseExportSession(GameObject source, PoseExportOptions options, Func<Transform, bool> excluded = null, VrChatMenuImportPolicy menuPolicy = null)
        {
            this.options = options ?? new PoseExportOptions();
            this.menuPolicy = menuPolicy;
            foreach (var component in source.GetComponentsInChildren<Component>(false))
            {
                if (component == null || component.GetType().FullName != AplType || excluded?.Invoke(component.transform) == true) continue;
                if (component.GetComponentInParent<Animator>() != source.GetComponent<Animator>()) continue;
                var data = PoseMenuResolver.Member(component, "data");
                foreach (var category in PoseMenuResolver.Items(PoseMenuResolver.Member(data, "categories")))
                    foreach (var pose in PoseMenuResolver.Items(PoseMenuResolver.Member(category, "poses")))
                    {
                        var clip = PoseMenuResolver.Member(pose, "animationClip") as AnimationClip;
                        var name = PoseMenuResolver.Member(pose, "name") as string;
                        var row = new PoseCandidate { Name = string.IsNullOrWhiteSpace(name) ? clip?.name ?? "未設定" : name,
                            Category = PoseMenuResolver.Member(category, "name") as string ?? "", Source = "APL" };
                        var tracking = PoseMenuResolver.Member(pose, "tracking");
                        var mask = AplMask(tracking, source); if (mask != null) owned.Add(mask);
                        row.Layers.Add(new PoseLayer { Clip = clip, Mask = mask });
                        if (PoseMenuResolver.Member(pose, "beforeAnimationClip") is AnimationClip || PoseMenuResolver.Member(pose, "afterAnimationClip") is AnimationClip)
                            row.Error = "APLの開始／終了アニメーションを伴う登録は未対応です。";
                        else if (clip == null) row.Error = "APLの元クリップがありません。";
                        else if (PoseSampling.Moving(source, row.Layers[0])) row.Error = "APLの体の動くクリップです。手動追加で動くポーズとして保存できます。";
                        else if (PoseMenuResolver.Member(data, "enableLocomotionAnimator") is bool active && !active)
                            row.Error = "APLのHumanoidポーズ出力が無効です。";
                        row.Id = PoseSampling.Identity(row); Entries.Add(row);
                    }
            }
            foreach (var manual in this.options.Manual)
            {
                var row = new PoseCandidate { Name = string.IsNullOrWhiteSpace(manual.Name) ? manual.Clip?.name ?? "未設定" : manual.Name,
                    Category = manual.Category ?? "", Source = "手動" };
                row.Layers.Add(new PoseLayer { Clip = manual.Clip, Time = manual.Time });
                row.IsAnimation = manual.Clip != null && PoseSampling.Moving(source, row.Layers[0], includeHead: true);
                row.Note = row.IsAnimation ? ExporterLocalization.T("動くクリップの ") + manual.Time +
                    ExporterLocalization.T(" 秒から末尾までの動きを保存します。") : "";
                row.Id = PoseSampling.Identity(row); Entries.Add(row);
            }
        }

        static AvatarMask AplMask(object tracking, GameObject source)
        {
            bool On(string name) => !(PoseMenuResolver.Member(tracking, name) is bool b) || b;
            if (On("arm") && On("foot") && On("finger") && On("locomotion")) return null;
            var mask = new AvatarMask();
            for (var i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++) mask.SetHumanoidBodyPartActive((AvatarMaskBodyPart)i, false);
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Root, On("locomotion"));
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Body, On("locomotion"));
            foreach (var part in new[] { AvatarMaskBodyPart.LeftArm, AvatarMaskBodyPart.RightArm }) mask.SetHumanoidBodyPartActive(part, On("arm"));
            foreach (var part in new[] { AvatarMaskBodyPart.LeftLeg, AvatarMaskBodyPart.RightLeg }) mask.SetHumanoidBodyPartActive(part, On("foot"));
            foreach (var part in new[] { AvatarMaskBodyPart.LeftFingers, AvatarMaskBodyPart.RightFingers }) mask.SetHumanoidBodyPartActive(part, On("finger"));
            // APL tracking restrictions also apply to Transform-bound clips.
            // Humanoid flags alone do not mask those generic curve bindings.
            mask.AddTransformPath(source.transform, true);
            var animator = source.GetComponent<Animator>();
            var parts = HumanoidPoseData.BoneNames.Select(n => (name: n, bone: animator.GetBoneTransform(PoseSampling.HumanBone(n))))
                .Where(b => b.bone != null).ToDictionary(b => AnimationUtility.CalculateTransformPath(b.bone, source.transform), b => PoseSampling.Part(b.name));
            for (var i = 0; i < mask.transformCount; i++)
                mask.SetTransformActive(i, parts.TryGetValue(mask.GetTransformPath(i), out var part) && mask.GetHumanoidBodyPartActive(part));
            return mask;
        }

        internal static void RemoveAplFromCopy(GameObject source, GameObject copy)
        {
            if (source == copy || copy.transform.IsChildOf(source.transform)) throw new ArgumentException("An independent copy is required.");
            foreach (var c in copy.GetComponentsInChildren<Component>(true))
                if (c != null && c.GetType().FullName == AplType) Object.DestroyImmediate(c);
        }

        internal void CollectPrepared(GameObject copy, ICollection<string> warnings = null, VrChatMenuImportPolicy preparedMenuPolicy = null)
        {
            foreach (var row in CollectPreparedIncrementally(copy, warnings, preparedMenuPolicy)) { }
        }

        // Export keeps the synchronous path; the review window yields between
        // candidates while sharing exactly the same validation/deduplication.
        internal IEnumerable<PoseCandidate> CollectPreparedIncrementally(GameObject copy, ICollection<string> warnings = null, VrChatMenuImportPolicy preparedMenuPolicy = null)
        {
            prepared = copy;
            Entries.AddRange(PoseMenuResolver.Read(copy, preparedMenuPolicy ?? menuPolicy));
            var seen = new Dictionary<string, PoseCandidate>();
            var collectedFrames = 0;
            foreach (var row in Entries.ToArray())
            {
                if (row.Error == null && seen.TryGetValue(row.Id, out var existing) && existing.Error == null)
                { if (!existing.Source.Contains(row.Source)) existing.Source += " / " + row.Source; Entries.Remove(row); continue; }
                if (row.Error == null) seen[row.Id] = row;
                if (options.Names.TryGetValue(row.Id, out var name)) row.Name = name;
                if (row.Error == null)
                    try
                    {
                        if (row.IsAnimation && !options.Excluded.Contains(row.Id))
                        {
                            row.Animation = PoseSampling.SampleAnimation(copy, row, HumanoidAnimationData.MaximumTotalFrames - collectedFrames);
                            collectedFrames += row.Animation.Frames.Count;
                        }
                        else if (row.IsAnimation) row.Animation = null;
                        else row.Data = PoseSampling.Sample(copy, row);
                    }
                    catch (Exception error) { row.Error = error.Message; }
                if (row.Error != null) warnings?.Add("ポーズ未対応: " + row.Name + " — " + row.Error);
                else if (!string.IsNullOrEmpty(row.Note)) warnings?.Add("ポーズ: " + row.Name + " — " + row.Note);
                yield return row;
            }
            // Refresh merged provenance after deduplication.
            foreach (var row in Entries.Where(e => e.Data != null)) row.Data.Source = row.Source;
            foreach (var row in Entries.Where(e => e.Animation != null)) row.Animation.Source = row.Source;
        }

        internal void Bind(ModelExporter converter, VrmLib.Model model, ExportingGltfData storage)
        {
            var animator = prepared.GetComponent<Animator>();
            int Node(string name)
            {
                var transform = animator.GetBoneTransform(PoseSampling.HumanBone(name));
                if (transform == null || !converter.Nodes.TryGetValue(transform.gameObject, out var node))
                    throw new InvalidOperationException("ポーズの骨が出力にありません: " + name);
                return model.Nodes.IndexOf(node);
            }
            foreach (var row in Selected())
                foreach (var bone in row.Bones)
                    bone.Node = Node(bone.Name);
            foreach (var row in SelectedAnimations())
                foreach (var bone in row.Bones) bone.Node = Node(bone.Name);
        }
        internal int SelectedCount => Entries.Count(e => e.Error == null && (e.Data != null || e.Animation != null) && !options.Excluded.Contains(e.Id));
        void CheckSelectedCount()
        {
            if (SelectedCount > HumanoidPoseData.MaximumPoses)
                throw new InvalidOperationException("同梱ポーズは128件までです。「ポーズを確認・調整」で不要な項目を除外してください。");
        }
        internal List<HumanoidPoseData> Selected()
        {
            CheckSelectedCount();
            var selected = Entries.Where(e => e.Error == null && e.Data != null && !options.Excluded.Contains(e.Id)).Select(e => e.Data).ToList();
            return selected;
        }
        internal List<HumanoidAnimationData> SelectedAnimations()
        {
            CheckSelectedCount();
            var selected = Entries.Where(e => e.Error == null && e.Animation != null && !options.Excluded.Contains(e.Id)).Select(e => e.Animation).ToList();
            if (selected.Sum(animation => animation.Frames.Count) > HumanoidAnimationData.MaximumTotalFrames)
                throw new InvalidOperationException("動くポーズの合計フレーム数が32768を超えました。不要な項目を除外してください。");
            return selected;
        }
        internal byte[] Inject(byte[] bytes)
        {
            var poses = Selected(); var animations = SelectedAnimations();
            if (poses.Count == 0 && animations.Count == 0) return bytes;
            var document = GlbDocument.Read(bytes);
            var extensions = (Dictionary<string, object>)document.Json["extensions"];
            if (!document.Json.TryGetValue("extensionsUsed", out var raw)) document.Json["extensionsUsed"] = raw = new List<object>();
            if (poses.Count > 0)
            {
                var extension = HumanoidPoseData.Write(poses);
                if (Encoding.UTF8.GetByteCount(JsonDom.Serialize(extension)) > HumanoidPoseData.MaximumBytes) throw new InvalidOperationException("ポーズ拡張が2MiBを超えました。");
                HumanoidPoseData.ValidateReferences(poses, document.Json);
                extensions.Add(HumanoidPoseData.Extension, extension); ((List<object>)raw).Add(HumanoidPoseData.Extension);
            }
            if (animations.Count > 0)
            {
                var extension = HumanoidAnimationData.Write(animations);
                if (Encoding.UTF8.GetByteCount(JsonDom.Serialize(extension)) > HumanoidAnimationData.MaximumBytes)
                    throw new InvalidOperationException("動くポーズの拡張が16MiBを超えました。不要な項目を除外してください。");
                HumanoidAnimationData.ValidateReferences(animations, document.Json);
                extensions.Add(HumanoidAnimationData.Extension, extension); ((List<object>)raw).Add(HumanoidAnimationData.Extension);
            }
            return document.Write();
        }
        public void Dispose() { foreach (var value in owned) if (value != null) Object.DestroyImmediate(value); owned.Clear(); }
    }
}
