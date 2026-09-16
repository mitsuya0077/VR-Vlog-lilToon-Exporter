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
        GameObject prepared;

        internal PoseExportSession(GameObject source, PoseExportOptions options, Func<Transform, bool> excluded = null)
        {
            this.options = options ?? new PoseExportOptions();
            foreach (var component in source.GetComponentsInChildren<Component>(false))
            {
                if (component == null || component.GetType().FullName != AplType || excluded?.Invoke(component.transform) == true) continue;
                if (component.GetComponentInParent<Animator>() != source.GetComponent<Animator>()) continue;
                var data = PoseMenuResolver.Member(component, "data");
                foreach (var category in PoseMenuResolver.Items(PoseMenuResolver.Member(data, "categories")))
                    foreach (var pose in PoseMenuResolver.Items(PoseMenuResolver.Member(category, "poses")))
                    {
                        if (Entries.Count >= HumanoidPoseData.MaximumPoses) throw new InvalidOperationException("登録ポーズは128件までです。");
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
                        else if (PoseSampling.Moving(clip)) row.Error = "APLの動くクリップです。手動追加で採用時刻を指定できます。";
                        else if (PoseMenuResolver.Member(data, "enableLocomotionAnimator") is bool active && !active)
                            row.Error = "APLのHumanoidポーズ出力が無効です。";
                        row.Id = PoseSampling.Identity(row); Entries.Add(row);
                    }
            }
            foreach (var manual in this.options.Manual)
            {
                var row = new PoseCandidate { Name = string.IsNullOrWhiteSpace(manual.Name) ? manual.Clip?.name ?? "未設定" : manual.Name,
                    Category = manual.Category ?? "", Source = "手動", SampledMotion = manual.Clip != null && PoseSampling.Moving(manual.Clip) };
                row.Layers.Add(new PoseLayer { Clip = manual.Clip, Time = manual.Time });
                row.Note = row.SampledMotion ? "動くクリップの " + manual.Time + " 秒を静止姿勢として採用（再生しません）。" : "";
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

        internal void CollectPrepared(GameObject copy, ICollection<string> warnings = null)
        {
            prepared = copy;
            Entries.AddRange(PoseMenuResolver.Read(copy));
            var seen = new Dictionary<string, PoseCandidate>();
            foreach (var row in Entries.ToArray())
            {
                if (row.Error == null && seen.TryGetValue(row.Id, out var existing) && existing.Error == null)
                { if (!existing.Source.Contains(row.Source)) existing.Source += " / " + row.Source; Entries.Remove(row); continue; }
                if (row.Error == null) seen[row.Id] = row;
                if (options.Names.TryGetValue(row.Id, out var name)) row.Name = name;
                if (row.Error == null)
                    try { row.Data = PoseSampling.Sample(copy, row); }
                    catch (Exception error) { row.Error = error.Message; }
                if (row.Error != null) warnings?.Add("ポーズ未対応: " + row.Name + " — " + row.Error);
                else if (!string.IsNullOrEmpty(row.Note)) warnings?.Add("ポーズ: " + row.Name + " — " + row.Note);
            }
            // Refresh merged provenance after deduplication.
            foreach (var row in Entries.Where(e => e.Data != null)) row.Data.Source = row.Source;
        }

        internal void Bind(ModelExporter converter, VrmLib.Model model, ExportingGltfData storage)
        {
            var animator = prepared.GetComponent<Animator>();
            foreach (var row in Selected())
                foreach (var bone in row.Bones)
                {
                    var transform = animator.GetBoneTransform(PoseSampling.HumanBone(bone.Name));
                    if (transform == null || !converter.Nodes.TryGetValue(transform.gameObject, out var node))
                        throw new InvalidOperationException("ポーズの骨が出力にありません: " + bone.Name);
                    bone.Node = model.Nodes.IndexOf(node);
                }
        }
        List<HumanoidPoseData> Selected() => Entries.Where(e => e.Error == null && e.Data != null && !options.Excluded.Contains(e.Id)).Select(e => e.Data).ToList();
        internal byte[] Inject(byte[] bytes)
        {
            var poses = Selected(); if (poses.Count == 0) return bytes;
            var document = GlbDocument.Read(bytes);
            var extension = HumanoidPoseData.Write(poses);
            if (Encoding.UTF8.GetByteCount(JsonDom.Serialize(extension)) > HumanoidPoseData.MaximumBytes) throw new InvalidOperationException("ポーズ拡張が2MiBを超えました。");
            HumanoidPoseData.ValidateReferences(poses, document.Json);
            var extensions = (Dictionary<string, object>)document.Json["extensions"]; extensions.Add(HumanoidPoseData.Extension, extension);
            if (!document.Json.TryGetValue("extensionsUsed", out var raw)) document.Json["extensionsUsed"] = raw = new List<object>();
            ((List<object>)raw).Add(HumanoidPoseData.Extension);
            return document.Write();
        }
        public void Dispose() { foreach (var value in owned) if (value != null) Object.DestroyImmediate(value); owned.Clear(); }
    }
}
