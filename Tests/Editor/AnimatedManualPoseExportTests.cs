using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UniGLTF;
using UniVRM10;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using VRVlog.Poses;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class AnimatedManualPoseExportTests
    {
        static AnimationClip Moving(GameObject avatar, AnimationCurve curve, HumanBodyBones bone = HumanBodyBones.LeftUpperArm)
        {
            var clip = new AnimationClip { name = "Moving pose" };
            var path = AnimationUtility.CalculateTransformPath(avatar.GetComponent<Animator>().GetBoneTransform(bone), avatar.transform);
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(Transform), "localEulerAnglesRaw.z"), curve);
            return clip;
        }
        static PoseCandidate Candidate(AnimationClip clip, float start = 0)
        {
            var row = new PoseCandidate { Name = "Moving pose", Source = "手動", IsAnimation = true };
            row.Layers.Add(new PoseLayer { Clip = clip, Time = start }); row.Id = PoseSampling.Identity(row); return row;
        }
        static Quaternion Rotation(double[] q) => new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]);
        static Quaternion At(HumanoidAnimationData data, string bone, double time)
        {
            data.Locate(time, out var left, out var right, out var blend);
            var index = data.Bones.FindIndex(value => value.Name == bone);
            return Quaternion.Slerp(Rotation(left.Rotations[index]), Rotation(right.Rotations[index]), (float)blend);
        }
        static Quaternion Native(GameObject avatar, AnimationClip clip, float time, HumanBodyBones bone, bool muscle = false, bool retainTransformAvatar = false)
            => NativeSnapshot(avatar, clip, time, bone, muscle, retainTransformAvatar).rotation;
        static (Quaternion rotation, Vector3 hipsOffset) NativeSnapshot(GameObject avatar, AnimationClip clip, float time, HumanBodyBones bone,
            bool muscle = false, bool retainTransformAvatar = false)
        {
            var staging = new GameObject("Inactive native pose oracle") { hideFlags = HideFlags.HideAndDontSave };
            staging.SetActive(false);
            var copy = Object.Instantiate(avatar, staging.transform, false); copy.hideFlags = HideFlags.HideAndDontSave;
            var graph = default(PlayableGraph);
            try
            {
                foreach (var behaviour in copy.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                copy.transform.SetParent(null, false);
                copy.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity); copy.transform.localScale = Vector3.one;
                var animator = copy.GetComponent<Animator>(); var target = animator.GetBoneTransform(bone); var rest = target.rotation;
                var hips = animator.GetBoneTransform(HumanBodyBones.Hips); var restHip = hips.position;
                animator.runtimeAnimatorController = null; animator.fireEvents = false; animator.applyRootMotion = false;
                if (muscle)
                {
                    animator.enabled = true; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    graph = PlayableGraph.Create("Independent authored muscle oracle"); graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                    var playable = AnimationClipPlayable.Create(graph, clip); playable.SetApplyFootIK(false); playable.SetApplyPlayableIK(false);
                    playable.SetSpeed(0); playable.SetTime(time);
                    AnimationPlayableOutput.Create(graph, "Original muscle clip", animator).SetSourcePlayable(playable);
                    graph.Play(); graph.Evaluate(0);
                }
                else
                {
                    // Transform curves name explicit authored local axes. A
                    // Humanoid avatar can retarget those a second time even on
                    // a disabled Animator. Sample the original clip natively
                    // on the exact same skeleton without that retargeting.
                    if (!retainTransformAvatar) animator.avatar = null;
                    clip.SampleAnimation(copy, time);
                }
                return (PoseSampling.Reflect((target.rotation * Quaternion.Inverse(rest)).normalized), PoseSampling.Reflect(hips.position - restHip));
            }
            finally { if (graph.IsValid()) graph.Destroy(); Object.DestroyImmediate(copy); Object.DestroyImmediate(staging); }
        }

        static Vector3 HipsAt(HumanoidAnimationData data, double time)
        {
            data.Locate(time, out var left, out var right, out var blend);
            Vector3 Position(double[] value) => new Vector3((float)value[0], (float)value[1], (float)value[2]);
            return Vector3.Lerp(Position(left.HipsOffset), Position(right.HipsOffset), (float)blend);
        }

        static (Quaternion rotation, Vector3 hipsOffset) NativeBodyCoordinates(GameObject avatar, AnimationClip clip, float time)
        {
            var staging = new GameObject("Inactive body-coordinate oracle"); staging.SetActive(false);
            var copy = Object.Instantiate(avatar, staging.transform, false);
            try
            {
                foreach (var behaviour in copy.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                copy.transform.SetParent(null, false); copy.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity); copy.transform.localScale = Vector3.one;
                var animator = copy.GetComponent<Animator>(); var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                var restRotation = hips.rotation; var restPosition = hips.position;
                using var handler = new HumanPoseHandler(animator.avatar, copy.transform);
                var pose = new HumanPose(); handler.GetHumanPose(ref pose);
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    var axis = "xyzw".IndexOf(binding.propertyName.Last()); var value = AnimationUtility.GetEditorCurve(clip, binding).Evaluate(time);
                    if (binding.propertyName.StartsWith("RootT.")) pose.bodyPosition[axis] = value;
                    if (binding.propertyName.StartsWith("RootQ.")) pose.bodyRotation[axis] = value;
                }
                pose.bodyRotation = pose.bodyRotation.normalized;
                // RootT/RootQ are documented HumanPose body coordinates; the
                // native handler performs their skeleton/scale conversion.
                handler.SetHumanPose(ref pose);
                return (PoseSampling.Reflect((hips.rotation * Quaternion.Inverse(restRotation)).normalized), PoseSampling.Reflect(hips.position - restPosition));
            }
            finally { Object.DestroyImmediate(copy); Object.DestroyImmediate(staging); }
        }

        [Test]
        public void MovingHipsTransformMatchesNativeMetersIndependentlyOfScenePlacement()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var clip = Moving(fixture.Source, AnimationCurve.Linear(0, 0, 1, 40), HumanBodyBones.Hips);
            var hips = fixture.Source.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Hips);
            var path = AnimationUtility.CalculateTransformPath(hips, fixture.Source.transform);
            try
            {
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(Transform), "m_LocalPosition.y"),
                    AnimationCurve.Linear(0, hips.localPosition.y, 1, hips.localPosition.y - .4f));
                fixture.Source.transform.SetPositionAndRotation(new Vector3(3, 2, -4), Quaternion.Euler(12, 55, -7));
                fixture.Source.transform.localScale = Vector3.one * 3; var before = fixture.Source.transform.localToWorldMatrix;
                var animation = PoseSampling.SampleAnimation(fixture.Source, Candidate(clip));
                foreach (var time in new[] { .083f, .397f, .713f, .991f })
                {
                    var native = NativeSnapshot(fixture.Source, clip, time, HumanBodyBones.Hips);
                    Assert.That(Quaternion.Angle(At(animation, "hips", time), native.rotation), Is.LessThan(.25f));
                    Assert.That(Vector3.Distance(HipsAt(animation, time), native.hipsOffset), Is.LessThan(.001f));
                }
                Assert.That(animation.Frames.Last().HipsOffset[1], Is.EqualTo(-.4).Within(.001));
                Assert.That(fixture.Source.transform.localToWorldMatrix, Is.EqualTo(before));
            }
            finally { Object.DestroyImmediate(clip); }
        }

        [TestCase("translation")]
        [TestCase("rotation")]
        [TestCase("both")]
        public void MovingHumanoidRootChannelsMatchNativeBodyCoordinatesWithoutSceneRootMotion(string kind)
        {
            using var fixture = new AttachmentConnectionTests.Fixture(); var clip = new AnimationClip { name = "Moving body coordinates" };
            try
            {
                var animator = fixture.Source.GetComponent<Animator>(); var pose = new HumanPose();
                using (var handler = new HumanPoseHandler(animator.avatar, fixture.Source.transform)) handler.GetHumanPose(ref pose);
                if (kind != "rotation") AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "RootT.y"),
                    AnimationCurve.Linear(0, pose.bodyPosition.y, 1, pose.bodyPosition.y - .25f));
                if (kind != "translation")
                {
                    var end = Quaternion.Euler(0, 55, 0) * pose.bodyRotation;
                    for (var axis = 0; axis < 4; axis++) AnimationUtility.SetEditorCurve(clip,
                        EditorCurveBinding.FloatCurve("", typeof(Animator), "RootQ." + "xyzw"[axis]), AnimationCurve.Linear(0, pose.bodyRotation[axis], 1, end[axis]));
                }
                fixture.Source.transform.SetPositionAndRotation(new Vector3(3, 2, -4), Quaternion.Euler(12, 55, -7));
                fixture.Source.transform.localScale = Vector3.one * 3; var before = fixture.Source.transform.localToWorldMatrix;
                var animation = PoseSampling.SampleAnimation(fixture.Source, Candidate(clip));
                foreach (var time in new[] { .083f, .397f, .713f, .991f })
                {
                    var native = NativeBodyCoordinates(fixture.Source, clip, time);
                    Assert.That(Quaternion.Angle(At(animation, "hips", time), native.rotation), Is.LessThan(.25f));
                    Assert.That(Vector3.Distance(HipsAt(animation, time), native.hipsOffset), Is.LessThan(.001f));
                }
                if (kind != "rotation") Assert.That(animation.Frames.Last().HipsOffset[1], Is.EqualTo(-.25f * animator.humanScale).Within(.002));
                Assert.That(fixture.Source.transform.localToWorldMatrix, Is.EqualTo(before));
            }
            finally { Object.DestroyImmediate(clip); }
        }

        [TestCase(HumanBodyBones.Neck)]
        [TestCase(HumanBodyBones.Head)]
        public void ExplicitMovingHeadAndNeckMusclesMatchNativeHumanoidInterpolation(HumanBodyBones bone)
        {
            using var fixture = new AttachmentConnectionTests.Fixture(); var clip = new AnimationClip { name = "Moving head muscle" };
            try
            {
                var muscle = Enumerable.Range(0, HumanTrait.MuscleCount).First(index => HumanTrait.BoneFromMuscle(index) == (int)bone);
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), HumanTrait.MuscleName[muscle]), AnimationCurve.Linear(0, -.3f, 1, .65f));
                var animation = PoseSampling.SampleAnimation(fixture.Source, Candidate(clip));
                var name = bone == HumanBodyBones.Head ? "head" : "neck";
                Assert.That(animation.Bones.Any(value => value.Name == name), Is.True);
                Assert.That(animation.Bones.Any(value => value.Name == (name == "head" ? "neck" : "head")), Is.False);
                foreach (var time in new[] { .127f, .457f, .839f })
                    Assert.That(Quaternion.Angle(At(animation, name, time), Native(fixture.Source, clip, time, bone, true)), Is.LessThan(.5f));
            }
            finally { Object.DestroyImmediate(clip); }
        }

        [Test]
        public void NativeTransformSamplingActuallyAppliesAuthoredCurvesBeforeComparison()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var clip = Moving(fixture.Source, AnimationCurve.Linear(0, 0, 1, 100));
            try
            {
                var atZero = Native(fixture.Source, clip, 0, HumanBodyBones.LeftUpperArm);
                var atHalf = Native(fixture.Source, clip, .5f, HumanBodyBones.LeftUpperArm);
                var source = fixture.Source.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.LeftUpperArm);
                var humanoidHalf = Native(fixture.Source, clip, .5f, HumanBodyBones.LeftUpperArm, retainTransformAvatar: true);
                TestContext.WriteLine("Source local rest=" + source.localRotation + "; native generic delta at 0=" + atZero +
                    "; native generic delta at .5=" + atHalf + "; same clip with Humanoid avatar=" + humanoidHalf);
                Assert.That(Quaternion.Angle(atZero, atHalf), Is.EqualTo(50).Within(.1f), "The independent native oracle must visibly evaluate the source clip.");
            }
            finally { Object.DestroyImmediate(clip); }
        }

        [Test]
        public void MultipleEulerTurnsInsideOneSixtiethSecondCannotAliasToAStaticAnimation()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var clip = Moving(fixture.Source, new AnimationCurve(new Keyframe(0, 0, 0, 86400),
                new Keyframe(1f / 60, 1440, 86400, 0), new Keyframe(1, 1440, 0, 0)));
            try
            {
                var animation = PoseSampling.SampleAnimation(fixture.Source, Candidate(clip));
                var native = Native(fixture.Source, clip, 1f / 480, HumanBodyBones.LeftUpperArm);
                Assert.That(Quaternion.Angle(Native(fixture.Source, clip, 0, HumanBodyBones.LeftUpperArm), native), Is.EqualTo(180).Within(.1f));
                Assert.That(Quaternion.Angle(At(animation, "leftUpperArm", 1.0 / 480), native), Is.LessThan(.25f));
                Assert.That(animation.Frames.Any(frame => frame.Time > 0 && frame.Time < 1.0 / 60), Is.True);
            }
            finally { Object.DestroyImmediate(clip); }
        }

        [TestCase("linear")]
        [TestCase("weighted")]
        [TestCase("turns")]
        public void MovingTransformClipRetainsNativeIntermediateRotationsAndOriginalAssets(string kind)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var curve = AnimationCurve.Linear(0, 0, 1, kind == "turns" ? 720 : 100);
            if (kind == "weighted") curve = new AnimationCurve(
                new Keyframe(0, 0, 0, 300, 1f / 3, .12f) { weightedMode = WeightedMode.Out },
                new Keyframe(1, 100, -180, 0, .7f, 1f / 3) { weightedMode = WeightedMode.In });
            var clip = Moving(fixture.Source, curve);
            try
            {
                AnimationUtility.SetAnimationEvents(clip, new[] { new AnimationEvent { functionName = "MustNeverExecute", time = .2f } });
                var before = fixture.Source.GetComponentsInChildren<Transform>().Select(value => value.localToWorldMatrix).ToArray();
                var json = EditorJsonUtility.ToJson(clip); var keys = AnimationUtility.GetEditorCurve(clip, AnimationUtility.GetCurveBindings(clip).Single()).keys;
                var animation = PoseSampling.SampleAnimation(fixture.Source, Candidate(clip));
                Assert.That(animation.Duration, Is.EqualTo(1)); Assert.That(animation.Frames.First().Time, Is.EqualTo(0));
                Assert.That(animation.Frames.Last().Time, Is.EqualTo(1));
                foreach (var time in new[] { 0f, .009f, .123f, .317f, .581f, .923f, 1f })
                    Assert.That(Quaternion.Angle(At(animation, "leftUpperArm", time), Native(fixture.Source, clip, time, HumanBodyBones.LeftUpperArm)),
                        Is.LessThan(.25f), "Native rotation at " + time);
                for (var frame = 1; frame < animation.Frames.Count; frame++)
                    for (var bone = 0; bone < animation.Bones.Count; bone++)
                        Assert.That(Quaternion.Dot(Rotation(animation.Frames[frame - 1].Rotations[bone]), Rotation(animation.Frames[frame].Rotations[bone])), Is.GreaterThanOrEqualTo(-.00001f));
                Assert.That(animation.Bones.Any(value => value.Name == "neck" || value.Name == "head"), Is.False);
                Assert.That(fixture.Source.GetComponentsInChildren<Transform>().Select(value => value.localToWorldMatrix), Is.EqualTo(before));
                Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(json));
                Assert.That(AnimationUtility.GetEditorCurve(clip, AnimationUtility.GetCurveBindings(clip).Single()).keys, Is.EqualTo(keys));
                Assert.That(AnimationUtility.GetAnimationEvents(clip).Length, Is.EqualTo(1));
            }
            finally { Object.DestroyImmediate(clip); }
        }

        [Test]
        public void SteppedClipStoresBothLimitsAndSelectsTheRightValueAtTheExactKey()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var clip = Moving(fixture.Source, new AnimationCurve(new Keyframe(0, 0, 0, float.PositiveInfinity),
                new Keyframe(.5f, 90, float.PositiveInfinity, 0), new Keyframe(1, 90, 0, 0)));
            try
            {
                var animation = PoseSampling.SampleAnimation(fixture.Source, Candidate(clip));
                Assert.That(animation.Frames.Count(value => value.Time == .5), Is.EqualTo(2));
                foreach (var time in new[] { .499f, .5f, .501f })
                    Assert.That(Quaternion.Angle(At(animation, "leftUpperArm", time), Native(fixture.Source, clip, time, HumanBodyBones.LeftUpperArm)), Is.LessThan(.1f));
            }
            finally { Object.DestroyImmediate(clip); }
        }

        [Test]
        public void QuaternionCurveKeepsNativeInterpolationThroughItsSignChange()
        {
            using var fixture = new AttachmentConnectionTests.Fixture(); var clip = new AnimationClip { name = "Quaternion pose" };
            var target = fixture.Source.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.LeftUpperArm);
            var path = AnimationUtility.CalculateTransformPath(target, fixture.Source.transform);
            var left = Quaternion.Euler(0, 0, 170); var right = Quaternion.Euler(0, 0, 230);
            try
            {
                for (var i = 0; i < 4; i++) AnimationUtility.SetEditorCurve(clip,
                    EditorCurveBinding.FloatCurve(path, typeof(Transform), "m_LocalRotation." + "xyzw"[i]), AnimationCurve.Linear(0, left[i], 1, right[i]));
                var animation = PoseSampling.SampleAnimation(fixture.Source, Candidate(clip));
                foreach (var time in new[] { .127f, .439f, .823f })
                    Assert.That(Quaternion.Angle(At(animation, "leftUpperArm", time), Native(fixture.Source, clip, time, HumanBodyBones.LeftUpperArm)), Is.LessThan(.25f));
            }
            finally { Object.DestroyImmediate(clip); }
        }

        [Test]
        public void MovingMuscleClipMatchesTheNativeHumanoidAtIntermediateTimes()
        {
            using var fixture = new AttachmentConnectionTests.Fixture(); var clip = new AnimationClip { name = "Muscle pose" };
            var muscle = Enumerable.Range(0, HumanTrait.MuscleCount).First(index => HumanTrait.BoneFromMuscle(index) == (int)HumanBodyBones.LeftUpperArm);
            try
            {
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), HumanTrait.MuscleName[muscle]), AnimationCurve.Linear(0, -.35f, 1, .7f));
                var animation = PoseSampling.SampleAnimation(fixture.Source, Candidate(clip));
                foreach (var time in new[] { .123f, .457f, .839f })
                    Assert.That(Quaternion.Angle(At(animation, "leftUpperArm", time), Native(fixture.Source, clip, time, HumanBodyBones.LeftUpperArm, true)), Is.LessThan(.5f));
            }
            finally { Object.DestroyImmediate(clip); }
        }

        [TestCase(HumanBodyBones.Neck)]
        [TestCase(HumanBodyBones.Head)]
        public void ExplicitHeadOrNeckMotionIsIncludedWhileEyeAndJawTracksStayOutsideTheContract(HumanBodyBones bone)
        {
            using var fixture = new AttachmentConnectionTests.Fixture(); var clip = Moving(fixture.Source, AnimationCurve.Linear(0, 0, 1, 30), bone);
            try
            {
                var animation = PoseSampling.SampleAnimation(fixture.Source, Candidate(clip));
                var name = bone == HumanBodyBones.Head ? "head" : "neck";
                Assert.That(animation.Bones.Any(value => value.Name == name), Is.True);
                Assert.That(animation.Bones.Any(value => value.Name == (name == "head" ? "neck" : "head") || value.Name.Contains("Eye") || value.Name == "jaw"), Is.False);
                Assert.That(Quaternion.Angle(At(animation, name, .413), Native(fixture.Source, clip, .413f, bone)), Is.LessThan(.25f));
            }
            finally { Object.DestroyImmediate(clip); }
        }

        [Test]
        public void ManualStartAndLoopArePreservedWithoutAnAdditionalStaticSnapshot()
        {
            using var fixture = new AttachmentConnectionTests.Fixture(); var clip = Moving(fixture.Source, AnimationCurve.Linear(0, 0, 1, 100));
            try
            {
                var settings = AnimationUtility.GetAnimationClipSettings(clip); settings.loopTime = true; AnimationUtility.SetAnimationClipSettings(clip, settings);
                var options = new PoseExportOptions(); options.Manual.Add(new ManualPose { Clip = clip, Time = .25f });
                using var session = new PoseExportSession(fixture.Source, options); session.CollectPrepared(fixture.Copy);
                Assert.That(session.Entries.Single().Error, Is.Null); Assert.That(session.Selected(), Is.Empty);
                var animation = session.SelectedAnimations().Single();
                Assert.That(session.SelectedCount, Is.EqualTo(1)); Assert.That(animation.SourceStartTime, Is.EqualTo(.25));
                Assert.That(animation.Duration, Is.EqualTo(.75)); Assert.That(animation.Loop, Is.True);
                Assert.That(Quaternion.Angle(At(animation, "leftUpperArm", .3), Native(fixture.Source, clip, .55f, HumanBodyBones.LeftUpperArm)), Is.LessThan(.25f));
                Assert.That(session.Entries.Single().Note, Is.Not.Empty);
                StringAssert.Contains(options.Manual[0].Time.ToString(), session.Entries.Single().Note);
                StringAssert.DoesNotContain("再生しません", session.Entries.Single().Note);
            }
            finally { Object.DestroyImmediate(clip); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MovingHumanoidClipKeepsLoopPoseSettingsAndNonMatchingAuthoredEndpoints(bool loopPose)
        {
            using var fixture = new AttachmentConnectionTests.Fixture(); var clip = new AnimationClip { name = "Looping authored muscle" };
            try
            {
                var muscle = Enumerable.Range(0, HumanTrait.MuscleCount).First(index => HumanTrait.BoneFromMuscle(index) == (int)HumanBodyBones.LeftUpperArm);
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), HumanTrait.MuscleName[muscle]), AnimationCurve.Linear(0, -.35f, 1, .7f));
                var settings = AnimationUtility.GetAnimationClipSettings(clip); settings.loopTime = true; settings.loopBlend = loopPose;
                AnimationUtility.SetAnimationClipSettings(clip, settings); var original = EditorJsonUtility.ToJson(clip);
                var animation = PoseSampling.SampleAnimation(fixture.Source, Candidate(clip));
                foreach (var time in new[] { .127f, .457f, .839f })
                    Assert.That(Quaternion.Angle(At(animation, "leftUpperArm", time), Native(fixture.Source, clip, time, HumanBodyBones.LeftUpperArm, true)), Is.LessThan(.5f));
                Assert.That(animation.Loop, Is.True); Assert.That(animation.Duration, Is.EqualTo(1));
                Assert.That(animation.Frames.Last().Time, Is.EqualTo(1));
                Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(original));
                Assert.That(AnimationUtility.GetAnimationClipSettings(clip).loopBlend, Is.EqualTo(loopPose));
                if (!loopPose) Assert.That(Quaternion.Angle(At(animation, "leftUpperArm", 0), At(animation, "leftUpperArm", 1)), Is.GreaterThan(20));
            }
            finally { Object.DestroyImmediate(clip); }
        }

        [TestCase(137f)]
        [TestCase(601f)]
        public void OversizedMovingClipIsDiagnosedAndNeverFallsBackToAStaticPose(float duration)
        {
            using var fixture = new AttachmentConnectionTests.Fixture(); var clip = Moving(fixture.Source, AnimationCurve.Linear(0, 0, duration, 100));
            try
            {
                var options = new PoseExportOptions(); options.Manual.Add(new ManualPose { Clip = clip });
                using var session = new PoseExportSession(fixture.Source, options); session.CollectPrepared(fixture.Copy);
                Assert.That(session.Entries.Single().Error, Is.Not.Null); Assert.That(session.Entries.Single().Data, Is.Null);
                Assert.That(session.Entries.Single().Animation, Is.Null); Assert.That(session.SelectedCount, Is.EqualTo(0));
            }
            finally { Object.DestroyImmediate(clip); }
        }

        [Test]
        public void RemainingFrameBudgetAndManualExclusionAreAppliedBeforeSampling()
        {
            using var fixture = new AttachmentConnectionTests.Fixture(); var clip = Moving(fixture.Source, AnimationCurve.Linear(0, 0, 1, 90));
            try
            {
                var error = Assert.Throws<InvalidOperationException>(() => PoseSampling.SampleAnimation(fixture.Source, Candidate(clip), 20));
                StringAssert.Contains("合計フレーム数", error.Message);
                var options = new PoseExportOptions(); options.Manual.Add(new ManualPose { Clip = clip });
                using (var selected = new PoseExportSession(fixture.Source, options)) options.Excluded.Add(selected.Entries.Single().Id);
                using (var excluded = new PoseExportSession(fixture.Source, options))
                {
                    excluded.CollectPrepared(fixture.Copy);
                    Assert.That(excluded.Entries.Single().Error, Is.Null); Assert.That(excluded.Entries.Single().Animation, Is.Null);
                    Assert.That(excluded.SelectedCount, Is.EqualTo(0)); Assert.That(excluded.SelectedAnimations(), Is.Empty);
                }
                options.Excluded.Clear();
                using var included = new PoseExportSession(fixture.Source, options); included.CollectPrepared(fixture.Copy);
                Assert.That(included.SelectedCount, Is.EqualTo(1)); Assert.That(included.SelectedAnimations().Single().Frames.Count, Is.GreaterThan(60));
            }
            finally { Object.DestroyImmediate(clip); }
        }

        [Test]
        public void StaticAndAnimatedRowsShareThe128ItemSelectionLimit()
        {
            using var fixture = new AttachmentConnectionTests.Fixture(); var clip = HumanoidPoseTests.Clip(fixture.Source);
            try
            {
                var options = new PoseExportOptions(); options.Manual.Add(new ManualPose { Clip = clip });
                using var session = new PoseExportSession(fixture.Source, options); session.CollectPrepared(fixture.Copy);
                for (var i = 0; i < 128; i++) session.Entries.Add(new PoseCandidate { Id = "animation" + i,
                    IsAnimation = true, Animation = new HumanoidAnimationData { Id = "animation" + i } });
                Assert.That(session.SelectedCount, Is.EqualTo(129));
                Assert.Throws<InvalidOperationException>(() => session.Selected()); Assert.Throws<InvalidOperationException>(() => session.SelectedAnimations());
                options.Excluded.Add(session.Entries.Last().Id);
                Assert.That(session.SelectedCount, Is.EqualTo(128)); Assert.DoesNotThrow(() => session.Selected());
            }
            finally { Object.DestroyImmediate(clip); }
        }

        [Test]
        public void ExistingPreviewButtonPathStartsMotionAndCleanupStopsItsEditorUpdates()
        {
            using var fixture = new AttachmentConnectionTests.Fixture(); var clip = Moving(fixture.Source, AnimationCurve.Linear(0, 0, 1, 90));
            var window = ScriptableObject.CreateInstance<PoseReviewWindow>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                var copy = Object.Instantiate(fixture.Copy);
                var type = typeof(PoseReviewWindow); type.GetField("copy", flags).SetValue(window, copy);
                var rest = (Dictionary<Transform, Quaternion>)type.GetField("rest", flags).GetValue(window);
                var animator = copy.GetComponent<Animator>();
                foreach (var name in HumanoidAnimationData.BoneNames)
                { var bone = animator.GetBoneTransform(PoseSampling.HumanBone(name)); if (bone != null) rest.Add(bone, bone.rotation); }
                type.GetField("hipsRest", flags).SetValue(window, animator.GetBoneTransform(HumanBodyBones.Hips).position);
                var row = Candidate(clip); row.Animation = PoseSampling.SampleAnimation(fixture.Source, row);
                var arm = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm); var before = arm.rotation;
                type.GetMethod("ApplyPreview", flags).Invoke(window, new object[] { row });
                Assert.That(type.GetField("playing", flags).GetValue(window), Is.SameAs(row));
                type.GetField("playStarted", flags).SetValue(window, EditorApplication.timeSinceStartup - .4);
                type.GetMethod("AdvanceRebuild", flags).Invoke(window, null);
                Assert.That(Quaternion.Angle(before, arm.rotation), Is.GreaterThan(20));
                type.GetMethod("Cleanup", flags).Invoke(window, null);
                Assert.That(type.GetField("playing", flags).GetValue(window), Is.Null); Assert.That(copy == null, Is.True);
                Assert.That(fixture.Source != null && fixture.Copy != null, Is.True);
            }
            finally { Object.DestroyImmediate(window); Object.DestroyImmediate(clip); }
        }

        [Test]
        public async Task ActualVrmExportAndReimportRetainsBoundAnimationFramesAndStaticCompatibility()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var sourceAvatar = HumanoidPoseTests.AddFingers(fixture.Source); var copyAvatar = HumanoidPoseTests.AddFingers(fixture.Copy);
            var clip = Moving(fixture.Source, AnimationCurve.Linear(0, 0, 1, 90));
            var head = Moving(fixture.Source, AnimationCurve.Linear(0, 0, 1, 30), HumanBodyBones.Head);
            var still = HumanoidPoseTests.Clip(fixture.Source); Vrm10Instance imported = null;
            void Curve(AnimationClip target, HumanBodyBones bone, string property, AnimationCurve values)
            {
                var path = AnimationUtility.CalculateTransformPath(fixture.Source.GetComponent<Animator>().GetBoneTransform(bone), fixture.Source.transform);
                AnimationUtility.SetEditorCurve(target, EditorCurveBinding.FloatCurve(path, typeof(Transform), property), values);
            }
            try
            {
                // This public fixture renders a triangle actually weighted to
                // the humanoid head, rather than the independent attachment
                // joint used by the broader attachment-test fixture.
                fixture.Mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 1, weight0 = 1 }, fixture.Mesh.vertexCount).ToArray();
                Curve(clip, HumanBodyBones.LeftIndexProximal, "localEulerAnglesRaw.z", AnimationCurve.Linear(0, 0, 1, 65));
                var hipY = fixture.Source.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Hips).localPosition.y;
                Curve(clip, HumanBodyBones.Hips, "m_LocalPosition.y", AnimationCurve.Linear(0, hipY, 1, hipY + .1f));
                Curve(head, HumanBodyBones.RightIndexProximal, "localEulerAnglesRaw.z", AnimationCurve.Linear(0, 0, 1, 50));
                var settings = AnimationUtility.GetAnimationClipSettings(head); settings.loopTime = true; AnimationUtility.SetAnimationClipSettings(head, settings);
                var options = new PoseExportOptions(); options.Manual.Add(new ManualPose { Clip = clip, Name = "Animated arm" });
                options.Manual.Add(new ManualPose { Clip = head, Name = "Animated head" });
                options.Manual.Add(new ManualPose { Clip = still, Name = "Static arm" });
                using var session = new PoseExportSession(fixture.Source, options); session.CollectPrepared(fixture.Copy);
                Assert.That(session.Entries.Select(row => row.Error), Is.All.Null);
                foreach (var animation in session.SelectedAnimations())
                    animation.Id = animation.Name == "Animated arm" ? "fixture.animated-arm" : "fixture.animated-head";
                session.Selected().Single().Id = "fixture.static-arm";
                fixture.PrepareMeshesForExport();
                var bytes = Vrm10AppearanceExporter.Export(new GltfExportSettings(), fixture.Copy, new BuiltInVrm10MaterialExporter(),
                    new MobileTextureSerializer(null), new VRM10ObjectMeta { Name = "Animated pose fixture", Authors = new List<string> { "Test" } }, afterExport: session.Bind);
                bytes = session.Inject(bytes); var json = GlbDocument.Read(bytes).Json; var extensions = (Dictionary<string, object>)json["extensions"];
                var animations = HumanoidAnimationData.Read(extensions[HumanoidAnimationData.Extension]); HumanoidAnimationData.ValidateReferences(animations, json);
                var armAnimation = animations.Single(value => value.Id == "fixture.animated-arm");
                var headAnimation = animations.Single(value => value.Id == "fixture.animated-head");
                Assert.That(armAnimation.Frames.Count, Is.GreaterThan(60)); Assert.That(armAnimation.Loop, Is.False); Assert.That(headAnimation.Loop, Is.True);
                Assert.That(headAnimation.Bones.Any(value => value.Name == "head"), Is.True); Assert.That(headAnimation.Bones.Any(value => value.Name == "neck"), Is.False);
                Assert.That(armAnimation.Bones.Any(value => value.Name == "head" || value.Name == "neck"), Is.False);
                Assert.That(Quaternion.Angle(At(armAnimation, "leftUpperArm", 0), At(armAnimation, "leftUpperArm", .5)), Is.GreaterThan(20));
                Assert.That(Quaternion.Angle(At(armAnimation, "leftIndexProximal", 0), At(armAnimation, "leftIndexProximal", .5)), Is.GreaterThan(20));
                Assert.That(armAnimation.Frames.Last().HipsOffset[1] - armAnimation.Frames.First().HipsOffset[1], Is.EqualTo(.1).Within(.001));
                foreach (var time in new[] { .173f, .419f, .831f })
                {
                    Assert.That(Quaternion.Angle(At(armAnimation, "leftUpperArm", time), Native(fixture.Source, clip, time, HumanBodyBones.LeftUpperArm)), Is.LessThan(.25f));
                    Assert.That(Quaternion.Angle(At(headAnimation, "head", time), Native(fixture.Source, head, time, HumanBodyBones.Head)), Is.LessThan(.25f));
                }
                Assert.That(HumanoidPoseData.Read(extensions[HumanoidPoseData.Extension]).Single().Name, Is.EqualTo("Static arm"));
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller()); Assert.That(imported, Is.Not.Null);
                var importedHead = imported.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Head);
                var headSkins = imported.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(skin =>
                {
                    var index = Array.IndexOf(skin.bones, importedHead);
                    return index >= 0 && skin.sharedMesh.boneWeights.Any(weight =>
                        weight.boneIndex0 == index && weight.weight0 > 0 || weight.boneIndex1 == index && weight.weight1 > 0 ||
                        weight.boneIndex2 == index && weight.weight2 > 0 || weight.boneIndex3 == index && weight.weight3 > 0);
                }).ToArray();
                Assert.That(headSkins.Length, Is.GreaterThan(0), "The imported fixture must contain vertices weighted to its real humanoid head.");
                Vector3[] WorldVertices(SkinnedMeshRenderer skin)
                {
                    var baked = new Mesh();
                    try { skin.BakeMesh(baked, false); return baked.vertices.Select(skin.transform.TransformPoint).ToArray(); }
                    finally { Object.DestroyImmediate(baked); }
                }
                var beforeSkin = headSkins.Select(WorldVertices).ToArray(); var headRest = importedHead.rotation;
                importedHead.rotation = PoseSampling.Reflect(At(headAnimation, "head", .5)) * headRest;
                for (var skin = 0; skin < headSkins.Length; skin++)
                    Assert.That(beforeSkin[skin].Zip(WorldVertices(headSkins[skin]), Vector3.Distance).Max(), Is.GreaterThan(.005f),
                        "The saved head animation must visibly deform the reimported fixture mesh.");
                importedHead.rotation = headRest;
                var fixturePath = Environment.GetEnvironmentVariable("VRVLOG_ANIMATION_FIXTURE");
                if (!string.IsNullOrEmpty(fixturePath)) File.WriteAllBytes(fixturePath, bytes);
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                Object.DestroyImmediate(clip); Object.DestroyImmediate(head); Object.DestroyImmediate(still);
                Object.DestroyImmediate(sourceAvatar); Object.DestroyImmediate(copyAvatar);
            }
        }
    }
}
