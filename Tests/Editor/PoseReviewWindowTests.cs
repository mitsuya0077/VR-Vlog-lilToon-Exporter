using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class PoseReviewWindowTests
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        static T Field<T>(PoseReviewWindow window, string name) =>
            (T)typeof(PoseReviewWindow).GetField(name, Fields).GetValue(window);

        static PoseReviewWindow Open(GameObject source, PoseExportOptions options)
        {
            PoseReviewWindow.Show(source, options, Array.Empty<GameObject>(), new ExportGimmickOptions());
            var window = Resources.FindObjectsOfTypeAll<PoseReviewWindow>().Single();
            window.position = new Rect(80, 80, 620, 600);
            return window;
        }

        static IEnumerator Ready(PoseReviewWindow window)
        {
            var deadline = EditorApplication.timeSinceStartup + 60;
            while (window.IsBusy)
            {
                Assert.That(EditorApplication.timeSinceStartup, Is.LessThan(deadline), "Pose review did not finish.");
                yield return null;
            }
            Assert.That(Field<string>(window, "error"), Is.Null);
            window.SendEvent(new Event { type = EventType.Layout });
            window.SendEvent(new Event { type = EventType.Repaint });
        }

        static void Drop(PoseReviewWindow window, params Object[] clips)
        {
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.objectReferences = clips;
            var point = Field<Rect>(window, "dropRect").center;
            window.SendEvent(new Event { type = EventType.DragUpdated, mousePosition = point });
            window.SendEvent(new Event { type = EventType.DragPerform, mousePosition = point });
        }

        static Color32[] RenderPixels(PoseReviewWindow window)
        {
            var preview = Field<PreviewRenderUtility>(window, "preview");
            var target = RenderTexture.GetTemporary(256, 256, 24);
            var previousTarget = preview.camera.targetTexture;
            var previousActive = RenderTexture.active;
            var texture = new Texture2D(256, 256, TextureFormat.RGBA32, false);
            try
            {
                preview.camera.targetTexture = target;
                preview.camera.Render();
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
                texture.Apply();
                return texture.GetPixels32();
            }
            finally
            {
                preview.camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(texture);
            }
        }

        [UnityTest] public IEnumerator OneAnimDropRemainsUsable() => VerifyDrop(1);
        [UnityTest] public IEnumerator TwentyAnimDropRemainsUsable() => VerifyDrop(20);

        static IEnumerator VerifyDrop(int count)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            // Make the synthetic visible geometry follow the animated arm, so
            // a changed bone with a stale skinned-mesh render cannot pass.
            var sourceArm = fixture.Source.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.LeftUpperArm);
            foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                skin.bones = new[] { sourceArm };
                skin.rootBone = sourceArm;
                fixture.Mesh.bindposes = new[] { sourceArm.worldToLocalMatrix * skin.transform.localToWorldMatrix };
            }
            var clips = Enumerable.Range(0, count).Select(i => HumanoidPoseTests.Clip(fixture.Source, 20 + i)).ToArray();
            var options = new PoseExportOptions();
            var window = Open(fixture.Source, options);
            var sourceRotation = sourceArm.rotation;
            try
            {
                yield return Ready(window);
                var previous = Field<PoseExportSession>(window, "session");
                Drop(window, clips.Cast<Object>().ToArray());
                Assert.That(options.Manual.Count, Is.EqualTo(count));
                Assert.That(window.IsBusy, Is.True);
                Assert.That(Field<PoseExportSession>(window, "session"), Is.SameAs(previous),
                    "DragPerform must return before rebuilding or sampling the avatar.");
                yield return Ready(window);
                var session = Field<PoseExportSession>(window, "session");
                Assert.That(session.SelectedCount, Is.EqualTo(count));
                var preview = Field<Rect>(window, "previewRect");
                Assert.That(preview.height, Is.GreaterThanOrEqualTo(100));
                Assert.That(preview.yMax, Is.LessThanOrEqualTo(window.position.height + 1), "Preview must stay inside the minimum-size window.");
                Assert.That(Field<Rect>(window, "dropRect").yMax, Is.LessThan(preview.yMin));

                // Exercise the actual preview button through IMGUI, then check
                // the prepared copy moved without touching the source avatar.
                var copy = Field<GameObject>(window, "copy");
                var arm = copy.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.LeftUpperArm);
                var before = arm.rotation;
                var beforePixels = RenderPixels(window);
                var button = new Vector2(window.position.width - 60, Field<Rect>(window, "dropRect").yMax + 14);
                window.SendEvent(new Event { type = EventType.MouseDown, mousePosition = button, button = 0 });
                window.SendEvent(new Event { type = EventType.MouseUp, mousePosition = button, button = 0 });
                Assert.That(Quaternion.Angle(before, arm.rotation), Is.GreaterThan(5), "Preview button must apply the selected pose.");
                Assert.That(Quaternion.Angle(sourceRotation, sourceArm.rotation), Is.LessThan(.001));
                var afterPixels = RenderPixels(window);
                Assert.That(beforePixels.Where((pixel, index) => !pixel.Equals(afterPixels[index])).Count(), Is.GreaterThan(20),
                    "The rendered skinned mesh must change, even while the preview Animator is disabled.");
                Assert.That(copy.GetComponent<Animator>().enabled, Is.False);

                Drop(window, clips[0]);
                Assert.That(options.Manual.Count, Is.EqualTo(count), "Repeated zero-second drops must not multiply manual rows.");
                yield return Ready(window);
                options.Manual[0].Time = .5f;
                Drop(window, clips[0]);
                yield return Ready(window);
                Assert.That(options.Manual.Count, Is.EqualTo(count + 1), "Different sample times remain separate poses.");
                Assert.That(Field<PoseExportSession>(window, "session").SelectedCount, Is.EqualTo(count + 1));
            }
            finally
            {
                window.Close();
                DragAndDrop.objectReferences = Array.Empty<Object>();
                foreach (var clip in clips) Object.DestroyImmediate(clip);
            }
        }

        [UnityTest]
        public IEnumerator UnsupportedDropIsRejectedAndClosingCancelsPendingWork()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var options = new PoseExportOptions();
            var clip = HumanoidPoseTests.Clip(fixture.Source);
            var window = Open(fixture.Source, options);
            try
            {
                yield return Ready(window);
                Drop(window, fixture.Source);
                Assert.That(options.Manual, Is.Empty);
                Assert.That(window.IsBusy, Is.False);
                Assert.That(DragAndDrop.visualMode, Is.EqualTo(DragAndDropVisualMode.Rejected));
                Drop(window, clip);
                window.Close();
                yield return null;
                Assert.That(Resources.FindObjectsOfTypeAll<PoseReviewWindow>(), Is.Empty);
            }
            finally
            {
                if (window != null) window.Close();
                DragAndDrop.objectReferences = Array.Empty<Object>();
                Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void IncrementalCollectionSamplesOneCandidatePerStepAndMatchesExport()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var valid = HumanoidPoseTests.Clip(fixture.Source);
            var invalid = new AnimationClip();
            try
            {
                var options = new PoseExportOptions();
                options.Manual.Add(new ManualPose { Clip = valid });
                options.Manual.Add(new ManualPose { Clip = valid });
                options.Manual.Add(new ManualPose { Clip = invalid });
                options.Manual.Add(new ManualPose { Clip = valid, Time = .5f });
                using var incremental = new PoseExportSession(fixture.Source, options);
                using var steps = incremental.CollectPreparedIncrementally(fixture.Copy).GetEnumerator();
                Assert.That(incremental.SelectedCount, Is.Zero);
                Assert.That(steps.MoveNext(), Is.True);
                Assert.That(incremental.SelectedCount, Is.EqualTo(1));
                Assert.That(steps.MoveNext(), Is.True);
                Assert.That(steps.Current.Error, Is.Not.Null, "Invalid clips are reported and do not stop the batch.");
                Assert.That(incremental.SelectedCount, Is.EqualTo(1));
                Assert.That(steps.MoveNext(), Is.True);
                Assert.That(incremental.SelectedCount, Is.EqualTo(2));
                Assert.That(steps.MoveNext(), Is.False);
                using var synchronous = new PoseExportSession(fixture.Source, options);
                synchronous.CollectPrepared(fixture.Copy);
                Assert.That(incremental.Entries.Select(e => e.Id), Is.EqualTo(synchronous.Entries.Select(e => e.Id)));
                Assert.That(JsonDom.Serialize(VRVlog.Poses.HumanoidPoseData.Write(incremental.Selected())),
                    Is.EqualTo(JsonDom.Serialize(VRVlog.Poses.HumanoidPoseData.Write(synchronous.Selected()))));
            }
            finally { Object.DestroyImmediate(valid); Object.DestroyImmediate(invalid); }
        }
    }
}
