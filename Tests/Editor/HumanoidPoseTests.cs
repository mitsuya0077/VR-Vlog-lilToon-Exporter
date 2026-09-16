using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UniGLTF;
using UniVRM10;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRVlog.Poses;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class HumanoidPoseTests
    {
        internal static AnimationClip Clip(GameObject avatar, float angle = 40)
        {
            var clip = new AnimationClip { name = "Pose" };
            var arm = avatar.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.LeftUpperArm);
            var binding = EditorCurveBinding.FloatCurve(AnimationUtility.CalculateTransformPath(arm, avatar.transform), typeof(Transform), "localEulerAnglesRaw.z");
            AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, 1, angle));
            return clip;
        }
        internal static Avatar AddFingers(GameObject root)
        {
            var animator = root.GetComponent<Animator>();
            var hair = root.transform.Find("Independent hair/Head"); if (hair != null) hair.name = "HairJoint";
            var bones = new Dictionary<HumanBodyBones, Transform>();
            for (var i = 0; i < (int)HumanBodyBones.LastBone; i++)
            { var b = animator.GetBoneTransform((HumanBodyBones)i); if (b != null) bones[(HumanBodyBones)i] = b; }
            foreach (var side in new[] { "Left", "Right" })
                foreach (var finger in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
                {
                    var hand = bones[(HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), side + "Hand")]; var parent = hand;
                    for (var i = 0; i < 3; i++)
                    {
                        var bone = (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), side + finger + new[] { "Proximal", "Intermediate", "Distal" }[i]);
                        var t = new GameObject(bone.ToString()).transform; t.SetParent(parent, false);
                        t.localPosition = new Vector3(side == "Left" ? -.025f : .025f, 0, i == 0 ? Array.IndexOf(new[] { "Thumb", "Index", "Middle", "Ring", "Little" }, finger) * .012f : 0);
                        bones.Add(bone, t); parent = t;
                    }
                }
            var avatar = AvatarBuilder.BuildHumanAvatar(root, new HumanDescription {
                human = bones.Select(p => new HumanBone { boneName = p.Value.name, humanName = HumanTrait.BoneName[(int)p.Key], limit = new HumanLimit { useDefaultValues = true } }).ToArray(),
                skeleton = root.GetComponentsInChildren<Transform>().Select(t => new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale }).ToArray(),
                upperArmTwist = .5f, lowerArmTwist = .5f, upperLegTwist = .5f, lowerLegTwist = .5f });
            Assert.That(avatar.isValid && avatar.isHuman, Is.True); animator.avatar = avatar; return avatar;
        }
        [TestCase(HumanBodyBones.Neck)]
        [TestCase(HumanBodyBones.Head)]
        [TestCase(HumanBodyBones.LeftEye)]
        [TestCase(HumanBodyBones.Jaw)]
        public void TrackingOnlyMusclesCannotBecomeBodyPoses(HumanBodyBones bone)
        {
            using var f = new AttachmentConnectionTests.Fixture(); var clip = new AnimationClip();
            try
            {
                var muscle = Enumerable.Range(0, HumanTrait.MuscleCount).First(i => HumanTrait.BoneFromMuscle(i) == (int)bone);
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), HumanTrait.MuscleName[muscle]), AnimationCurve.Constant(0, 1, .5f));
                Assert.That(PoseMenuResolver.HasBody(clip), Is.False);
                var candidate = new PoseCandidate { Id = "tracking-only", Name = "Tracking", Source = "test" };
                candidate.Layers.Add(new PoseLayer { Clip = clip });
                Assert.Throws<InvalidOperationException>(() => PoseSampling.Sample(f.Source, candidate));
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "Left Arm Down-Up"), AnimationCurve.Constant(0, 1, .5f));
                Assert.That(PoseMenuResolver.HasBody(clip), Is.True);
                var clean = PoseSampling.BodyClip(f.Source, f.Source.GetComponent<Animator>(), clip);
                try { Assert.That(AnimationUtility.GetCurveBindings(clean).Select(b => b.propertyName), Is.EquivalentTo(new[] { "Left Arm Down-Up" })); }
                finally { Object.DestroyImmediate(clean); }
            }
            finally { Object.DestroyImmediate(clip); }
        }
        [Test]
        public void FingerMusclesMasksAndWeightsAreEvaluatedByUnity()
        {
            using var f = new AttachmentConnectionTests.Fixture(); var avatar = AddFingers(f.Source);
            var clip = new AnimationClip(); var mask = new AvatarMask();
            try
            {
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "LeftHand.Index.1 Stretched"), AnimationCurve.Constant(0, 1, -1));
                var candidate = new PoseCandidate { Id = "finger", Name = "Finger", Category = "", Source = "test" };
                candidate.Layers.Add(new PoseLayer { Clip = clip });
                var full = PoseSampling.Sample(f.Source, candidate).Bones.Single(b => b.Name == "leftIndexProximal").Rotation;
                Assert.That(Math.Abs(full[3]), Is.LessThan(.999), "Finger muscle curve must not be discarded.");
                candidate.Layers[0].Weight = .5f;
                var half = PoseSampling.Sample(f.Source, candidate).Bones.Single(b => b.Name == "leftIndexProximal").Rotation;
                Assert.That(Math.Abs(half[3]), Is.GreaterThan(Math.Abs(full[3])).And.LessThan(.999));
                candidate.Layers[0].Weight = 0;
                var zero = PoseSampling.Sample(f.Source, candidate).Bones.Single(b => b.Name == "leftIndexProximal").Rotation;
                Assert.That(Math.Abs(zero[3]), Is.GreaterThan(.999));
                candidate.Layers[0].Weight = 1;
                mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftFingers, false); candidate.Layers[0].Mask = mask;
                var masked = PoseSampling.Sample(f.Source, candidate).Bones.Single(b => b.Name == "leftIndexProximal").Rotation;
                Assert.That(Math.Abs(masked[3]), Is.GreaterThan(.999));
            }
            finally { Object.DestroyImmediate(clip); Object.DestroyImmediate(mask); Object.DestroyImmediate(avatar); }
        }
        [Test]
        public void ASingleArmMusclePreservesUnboundAuthoredJointLocals()
        {
            using var f = new AttachmentConnectionTests.Fixture(); var clip = new AnimationClip();
            try
            {
                var animator = f.Source.GetComponent<Animator>();
                animator.GetBoneTransform(HumanBodyBones.LeftLowerArm).localRotation = Quaternion.Euler(15, 8, 24);
                animator.GetBoneTransform(HumanBodyBones.LeftHand).localRotation = Quaternion.Euler(-12, 17, 9);
                var muscle = Array.FindIndex(HumanTrait.MuscleName, n => n == "Left Arm Down-Up"); Assert.That(muscle, Is.GreaterThanOrEqualTo(0));
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), HumanTrait.MuscleName[muscle]), AnimationCurve.Constant(0, 1, -.6f));
                var candidate = new PoseCandidate { Id = "arm-muscle", Name = "Arm", Category = "", Source = "test" }; candidate.Layers.Add(new PoseLayer { Clip = clip });
                var data = PoseSampling.Sample(f.Source, candidate);
                Quaternion Delta(string name) { var q = data.Bones.Single(b => b.Name == name).Rotation; return new Quaternion((float)q[0],(float)q[1],(float)q[2],(float)q[3]); }
                Assert.That(Quaternion.Angle(Delta("leftUpperArm"), Delta("leftLowerArm")), Is.LessThan(.05));
                Assert.That(Quaternion.Angle(Delta("leftLowerArm"), Delta("leftHand")), Is.LessThan(.05));
            }
            finally { Object.DestroyImmediate(clip); }
        }
        [Test]
        public void RootRotationUsesRootMaskIndependentlyOfBodyMask()
        {
            using var f = new AttachmentConnectionTests.Fixture(); var clip = new AnimationClip(); var mask = new AvatarMask();
            try
            {
                var q = Quaternion.Euler(0, 35, 0);
                for (var i = 0; i < 4; i++) AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "RootQ." + "xyzw"[i]), AnimationCurve.Constant(0, 1, q[i]));
                var candidate = new PoseCandidate { Id = "root-mask", Name = "Root", Category = "", Source = "test" }; candidate.Layers.Add(new PoseLayer { Clip = clip, Mask = mask });
                mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Root, true); mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Body, false);
                var active = PoseSampling.Sample(f.Source, candidate).Bones.Single(b => b.Name == "hips").Rotation;
                Assert.That(Math.Abs(active[3]), Is.LessThan(.99));
                mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Root, false); mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Body, true);
                var blocked = PoseSampling.Sample(f.Source, candidate).Bones.Single(b => b.Name == "hips").Rotation;
                Assert.That(Math.Abs(blocked[3]), Is.GreaterThan(.999));
            }
            finally { Object.DestroyImmediate(clip); Object.DestroyImmediate(mask); }
        }
        [Test]
        public void HumanoidRootTranslationUsesMetersAndIgnoresScenePlacementScale()
        {
            using var f = new AttachmentConnectionTests.Fixture(); var clip = new AnimationClip(); var mask = new AvatarMask();
            try
            {
                var animator = f.Source.GetComponent<Animator>(); var pose = new HumanPose();
                using (var handler = new HumanPoseHandler(animator.avatar, f.Source.transform)) handler.GetHumanPose(ref pose);
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "RootT.y"), AnimationCurve.Constant(0, 1, pose.bodyPosition.y - .25f));
                var candidate = new PoseCandidate { Id = "root-position", Name = "Crouch", Category = "", Source = "test" }; candidate.Layers.Add(new PoseLayer { Clip = clip, Mask = mask });
                mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Root, true); mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Body, false);
                var first = PoseSampling.Sample(f.Source, candidate);
                Assert.That(first.HipsOffset[1], Is.EqualTo(-.25f * animator.humanScale).Within(.002));
                f.Source.transform.SetPositionAndRotation(new Vector3(3, 2, -4), Quaternion.Euler(0, 55, 0)); f.Source.transform.localScale = Vector3.one * 3;
                var placed = PoseSampling.Sample(f.Source, candidate);
                Assert.That(placed.HipsOffset[1], Is.EqualTo(first.HipsOffset[1]).Within(.002));
                mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Root, false); mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Body, true);
                Assert.That(PoseSampling.Sample(f.Source, candidate).HipsOffset[1], Is.EqualTo(0).Within(.002));
            }
            finally { Object.DestroyImmediate(clip); Object.DestroyImmediate(mask); }
        }
        [Test]
        public void ManualSamplingIsIsolatedAndKeepsDifferentTimesAndConditions()
        {
            using var f = new AttachmentConnectionTests.Fixture(); var clip = Clip(f.Source);
            try
            {
                var before = f.Source.GetComponentsInChildren<Transform>().Select(t => t.localToWorldMatrix).ToArray();
                var json = EditorJsonUtility.ToJson(clip);
                var a = new PoseCandidate { Name = "A", Category = "", Source = "手動" }; a.Layers.Add(new PoseLayer { Clip = clip }); a.Id = PoseSampling.Identity(a);
                var sampled = PoseSampling.Sample(f.Source, a);
                var rotation = sampled.Bones.Single(b => b.Name == "leftUpperArm").Rotation;
                Assert.That(Math.Abs(rotation[2]), Is.GreaterThan(.2), "The actual arm, not only metadata, must move.");
                a.Layers[0].Weight = .5f;
                var half = PoseSampling.Sample(f.Source, a).Bones.Single(b => b.Name == "leftUpperArm").Rotation;
                Assert.That(2 * Math.Asin(Math.Abs(half[2])) * Mathf.Rad2Deg, Is.EqualTo(20).Within(1));
                a.Layers[0].Weight = 1;
                Assert.That(f.Source.GetComponentsInChildren<Transform>().Select(t => t.localToWorldMatrix), Is.EqualTo(before));
                Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(json));
                a.Layers[0].Time = .5f; Assert.That(PoseSampling.Identity(a), Is.Not.EqualTo(a.Id));
                a.Layers[0].Time = 0; a.Conditions = "menu=1"; Assert.That(PoseSampling.Identity(a), Is.Not.EqualTo(a.Id));
            }
            finally { Object.DestroyImmediate(clip); }
        }
        [Test]
        public void MenuGraphRequiresConvergenceAndRejectsTimeOrExternalInput()
        {
            var machine = new AnimatorStateMachine();
            try
            {
                var idle = machine.AddState("Idle"); var pose = machine.AddState("Pose");
                var edge = machine.AddAnyStateTransition(pose); edge.hasExitTime = false; edge.duration = 0; edge.canTransitionToSelf = false;
                edge.AddCondition(AnimatorConditionMode.Equals, 1, "Pose");
                var selected = new Dictionary<string, float> { ["Pose"] = 1 };
                Assert.That(PoseMenuResolver.Resolve(machine, selected), Is.SameAs(pose));
                edge.hasExitTime = true; Assert.Throws<InvalidOperationException>(() => PoseMenuResolver.Resolve(machine, selected)); edge.hasExitTime = false;
                Assert.Throws<InvalidOperationException>(() => PoseMenuResolver.Resolve(machine, selected, new HashSet<string> { "Pose" }));
                selected["Pose"] = 0; Assert.Throws<InvalidOperationException>(() => PoseMenuResolver.Resolve(machine, selected));
                var extra = idle.AddTransition(pose); extra.duration = 0; extra.AddCondition(AnimatorConditionMode.If, 0, "Contact");
                Assert.Throws<InvalidOperationException>(() => PoseMenuResolver.Resolve(machine, selected));
            }
            finally { Object.DestroyImmediate(machine); }
        }
        [Test]
        public void ContractRejectsVersionNonFiniteInvalidBonesAndDuplicates()
        {
            var pose = new HumanoidPoseData { Id = "a", Name = "Pose", Category = "", Source = "test" };
            pose.Bones.Add(new HumanoidPoseData.Bone { Name = "hips", Node = 0, Rotation = new double[] { 0, 0, 0, 1 } });
            var data = HumanoidPoseData.Write(new[] { pose }); Assert.That(HumanoidPoseData.Read(data).Count, Is.EqualTo(1));
            data["schemaVersion"] = 2L; Assert.Throws<InvalidOperationException>(() => HumanoidPoseData.Read(data));
            pose.HipsOffset[0] = double.NaN; Assert.Throws<InvalidOperationException>(() => HumanoidPoseData.Write(new[] { pose })); pose.HipsOffset[0] = 0;
            Assert.Throws<InvalidOperationException>(() => HumanoidPoseData.Write(new[] { pose, pose }));
            pose.Bones[0].Name = "head"; Assert.Throws<InvalidOperationException>(() => HumanoidPoseData.Write(new[] { pose }));
        }
        [Test]
        public async Task ExportAndReimportKeepsNodeBindingAndSourceThenProducesApplicationFixture()
        {
            using var f = new AttachmentConnectionTests.Fixture();
            var sourceAvatar = AddFingers(f.Source); var copyAvatar = AddFingers(f.Copy); var clip = Clip(f.Source);
            var seated = Clip(f.Source, 0);
            void Curve(AnimationClip target, HumanBodyBones bone, string property, float value)
            {
                var t = f.Source.GetComponent<Animator>().GetBoneTransform(bone);
                AnimationUtility.SetEditorCurve(target, EditorCurveBinding.FloatCurve(AnimationUtility.CalculateTransformPath(t, f.Source.transform), typeof(Transform), property), AnimationCurve.Constant(0, 1, value));
            }
            Curve(clip, HumanBodyBones.LeftIndexProximal, "localEulerAnglesRaw.z", 65);
            var originalHipY = f.Source.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Hips).localPosition.y;
            Curve(seated, HumanBodyBones.Hips, "m_LocalPosition.y", originalHipY - .45f);
            foreach (var bone in new[] { HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg }) Curve(seated, bone, "localEulerAnglesRaw.x", -65);
            foreach (var bone in new[] { HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg }) Curve(seated, bone, "localEulerAnglesRaw.x", 100);
            Vrm10Instance imported = null;
            try
            {
                var options = new PoseExportOptions(); options.Manual.Add(new ManualPose { Clip = clip, Name = "Arm pose" });
                options.Manual.Add(new ManualPose { Clip = clip, Name = "Duplicate" });
                options.Manual.Add(new ManualPose { Clip = clip, Time = .5f, Name = "At half second" });
                options.Manual.Add(new ManualPose { Clip = seated, Name = "Seated", Category = "Floor" });
                using var session = new PoseExportSession(f.Source, options); session.CollectPrepared(f.Copy);
                Assert.That(session.Entries.Count, Is.EqualTo(3));
                Assert.That(session.Entries.All(e => e.Error == null), Is.True, string.Join(";", session.Entries.Select(e => e.Error)));
                f.PrepareMeshesForExport();
                var bytes = Vrm10AppearanceExporter.Export(new GltfExportSettings(), f.Copy, new BuiltInVrm10MaterialExporter(),
                    new MobileTextureSerializer(null), new VRM10ObjectMeta { Name = "Pose fixture", Authors = new List<string> { "Test" } }, afterExport: session.Bind);
                var legacy = bytes; bytes = session.Inject(bytes);
                var json = GlbDocument.Read(bytes).Json;
                var data = HumanoidPoseData.Read(((Dictionary<string, object>)json["extensions"])[HumanoidPoseData.Extension]);
                HumanoidPoseData.ValidateReferences(data, json);
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported, Is.Not.Null);
                var path = Environment.GetEnvironmentVariable("VRVLOG_POSE_FIXTURE");
                if (!string.IsNullOrEmpty(path))
                {
                    File.WriteAllBytes(path, bytes);
                    File.WriteAllBytes(path + ".legacy.bytes", legacy);
                    var broken = GlbDocument.Read(bytes);
                    ((Dictionary<string, object>)((Dictionary<string, object>)broken.Json["extensions"])[HumanoidPoseData.Extension])["schemaVersion"] = 99L;
                    File.WriteAllBytes(path + ".unsupported.bytes", broken.Write());
                }
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); Object.DestroyImmediate(clip); Object.DestroyImmediate(seated); Object.DestroyImmediate(sourceAvatar); Object.DestroyImmediate(copyAvatar); }
        }
    }
}
