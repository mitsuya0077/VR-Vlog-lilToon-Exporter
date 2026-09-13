using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class ExpressionImportRecoveryTests
    {
        private GameObject avatar, clone;
        private Mesh mesh;
        private AnimationClip clip;
        private readonly List<Mesh> owned = new List<Mesh>();
        private const string Missing = "extra_cheek5";

        [SetUp]
        public void SetUp()
        {
            avatar = new GameObject("Avatar");
            var body = new GameObject("Body", typeof(SkinnedMeshRenderer)); body.transform.SetParent(avatar.transform, false);
            mesh = BaseShapeFixture.Create();
            body.GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
            body.GetComponent<SkinnedMeshRenderer>().SetBlendShapeWeight(1, 50);
            clip = new AnimationClip { name = "Stale reference face" };
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(clone); Object.DestroyImmediate(avatar); Object.DestroyImmediate(mesh); Object.DestroyImmediate(clip);
            foreach (var item in owned) Object.DestroyImmediate(item);
            owned.Clear();
        }

        private void Curve(string shape, AnimationCurve curve) => AnimationUtility.SetEditorCurve(clip,
            EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape." + shape), curve);

        private VrChatExpressionMenu.Source Read(int count = 1)
        {
            var menu = new VrChatExpressionMenu.Source();
            for (var i = 0; i < count; i++)
            {
                var entry = new VrChatExpressionMenu.Entry { Id = "gesture/" + i, Name = "ジェスチャー / face " + i };
                VrChatGestureExpressions.ReadClip(avatar, clip, entry);
                menu.Entries.Add(entry);
            }
            return menu;
        }

        [TestCase(0f, false)]
        [TestCase(100f, false)]
        [TestCase(0f, true)]
        public void FourteenFacesSurviveStaleCurvesWithoutLosingResetsOrAnimation(float missingWeight, bool animated)
        {
            Curve("Face size", AnimationCurve.Constant(0, 1, 75));
            Curve("Blink", AnimationCurve.Constant(0, 1, 0));
            Curve(Missing, animated ? AnimationCurve.Linear(0, 0, 1, 100) : AnimationCurve.Constant(0, 1, missingWeight));
            var menu = Read(14);
            clone = Object.Instantiate(avatar);
            var bindings = new PreparedExpressionBindings(clone, menu);
            bindings.Capture(menu);
            AvatarBaseShape.Preserve(clone, clone, owned, null);
            var warnings = new List<string>();
            var baked = VrChatExpressionBaker.Bake(null, clone, menu, owned, warnings, bindings);
            Assert.That(baked.Count, Is.EqualTo(14));
            Assert.That(warnings.Count(w => w.Contains("Body/extra_cheek5")), Is.EqualTo(1), "Shared stale references are reported together.");
            foreach (var entry in menu.Entries)
            {
                Assert.That(entry.Error, Is.Null);
                Assert.That(entry.Values.Select(v => v.Shape), Is.EquivalentTo(new[] { "Face size", "Blink" }));
                Assert.That(entry.Values.Single(v => v.Shape == "Blink").Weight, Is.Zero);
                Assert.That(entry.Animation, Is.Empty);
                Assert.That(entry.Duration, Is.Zero);
            }
            var output = clone.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh;
            var delta = new Vector3[output.vertexCount];
            output.GetBlendShapeFrameVertices(output.GetBlendShapeIndex(baked[0].Targets.Single()), 0, delta, null, null);
            Assert.That(output.vertices[0] + delta[0], Is.EqualTo(new Vector3(4, 0, 0)));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(1), Is.EqualTo(50));
            Assert.That(mesh.blendShapeCount, Is.EqualTo(2));
            Assert.That(AnimationUtility.GetCurveBindings(clip).Length, Is.EqualTo(3));
        }

        [Test]
        public void ValidAnimationSurvivesAlongsideMissingAnimation()
        {
            Curve("Face size", AnimationCurve.Constant(0, 1, 75));
            Curve("Blink", AnimationCurve.Linear(0, 0, 1, 100));
            Curve(Missing, AnimationCurve.Linear(0, 0, 1, 100));
            var menu = Read(); clone = Object.Instantiate(avatar);
            var bindings = new PreparedExpressionBindings(clone, menu); bindings.Capture(menu);
            AvatarBaseShape.Preserve(clone, clone, owned, null);
            var baked = VrChatExpressionBaker.Bake(null, clone, menu, owned, null, bindings).Single();
            Assert.That(menu.Entries[0].Animation.Single().Shape, Is.EqualTo("Blink"));
            Assert.That(baked.Animation.Channels.Count, Is.EqualTo(1));
            Assert.That(baked.Animation.Channels[0].Curve.Evaluate(.5), Is.EqualTo(50).Within(.001));
        }

        [Test]
        public void AllMissingIsNotRegisteredAsAnEmptyFace()
        {
            Curve(Missing, AnimationCurve.Constant(0, 1, 100));
            var menu = Read(); clone = Object.Instantiate(avatar);
            var bindings = new PreparedExpressionBindings(clone, menu); bindings.Capture(menu);
            Assert.That(menu.Entries[0].Error, Does.Contain("有効な顔"));
            Assert.That(VrChatExpressionBaker.Bake(null, clone, menu, owned, null, bindings), Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MorphGeneratedByPreparationIsRetainedForClipsButNotGuessedForMenus(bool sampled)
        {
            Curve("Blink", AnimationCurve.Constant(0, 1, 0));
            Curve(Missing, AnimationCurve.Linear(0, 20, 1, 100));
            var menu = Read(); var entry = menu.Entries[0];
            if (sampled)
            {
                entry.Values.RemoveAll(v => v.Shape == Missing); entry.Animation.Clear();
                entry.Unevaluated.Add(new VrChatExpressionMenu.MorphValue { Path = "Body", Shape = Missing });
            }
            clone = Object.Instantiate(avatar);
            var bindings = new PreparedExpressionBindings(clone, menu);
            var body = clone.GetComponentInChildren<SkinnedMeshRenderer>();
            var prepared = Object.Instantiate(mesh); owned.Add(prepared);
            prepared.AddBlendShapeFrame(Missing, 100, new[] { Vector3.right, Vector3.zero, Vector3.zero }, new Vector3[3], new Vector3[3]);
            body.sharedMesh = prepared; body.name = "Renamed after preparation";
            bindings.Capture(menu);
            if (sampled) Assert.That(entry.Error, Does.Contain("メニュー選択値を確定できません"));
            else
            {
                Assert.That(entry.Error, Is.Null);
                Assert.That(entry.Values.Single(v => v.Shape == Missing).Weight, Is.EqualTo(20));
                Assert.That(entry.Animation.Single().Shape, Is.EqualTo(Missing));
                AvatarBaseShape.Preserve(clone, clone, owned, null);
                Assert.That(VrChatExpressionBaker.Bake(null, clone, menu, owned, null, bindings).Count, Is.EqualTo(1));
            }
        }

        [Test]
        public void LosingAnOriginallyPresentMorphRemainsAnError()
        {
            Curve("Blink", AnimationCurve.Constant(0, 1, 0));
            var menu = Read(); clone = Object.Instantiate(avatar);
            var bindings = new PreparedExpressionBindings(clone, menu);
            var prepared = Object.Instantiate(mesh); owned.Add(prepared); prepared.ClearBlendShapes();
            clone.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh = prepared;
            bindings.Capture(menu);
            Assert.That(menu.Entries[0].Error, Does.Contain("処理後に表情の対象が残っていない"));
        }

        [Test]
        public void MissingMorphDoesNotHideUnsupportedMaterialChangesOrAmbiguousPaths()
        {
            Curve(Missing, AnimationCurve.Constant(0, 1, 0));
            var materialBinding = EditorCurveBinding.PPtrCurve("Body", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]");
            AnimationUtility.SetObjectReferenceCurve(clip, materialBinding, new[] { new ObjectReferenceKeyframe { time = 0, value = null } });
            Assert.Throws<InvalidOperationException>(() => Read());
            AnimationUtility.SetObjectReferenceCurve(clip, materialBinding, null);
            new GameObject("Body").transform.SetParent(avatar.transform, false);
            Assert.Throws<InvalidOperationException>(() => Read());
        }

        [Test]
        public void FaceEmoRegisteredClipUsesTheSameRecoveryForPoseAndAnimation()
        {
            var folderName = "__FaceEmoRecovery_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folderName); var folder = "Assets/" + folderName;
            try
            {
                Curve("Blink", AnimationCurve.Linear(0, 0, 1, 100));
                Curve(Missing, AnimationCurve.Linear(0, 0, 1, 100));
                AssetDatabase.CreateAsset(clip, folder + "/Face.anim");
                var list = new { Modes = new[] { new { DisplayName = "Wink", Branches = new[] { new { BaseAnimation = new { GUID = AssetDatabase.AssetPathToGUID(folder + "/Face.anim") } } } } } };
                var menu = new VrChatExpressionMenu.Source(); var serial = 0;
                FaceEmoExpressions.ReadRegistered(avatar, list, "FaceEmo", menu, ref serial, new HashSet<object>(), 0);
                clone = Object.Instantiate(avatar);
                new PreparedExpressionBindings(clone, menu).Capture(menu);
                var entry = menu.Entries.Single();
                Assert.That(entry.Error, Is.Null);
                Assert.That(entry.Name, Does.StartWith("FaceEmo / Wink"));
                Assert.That(entry.Values.Single().Shape, Is.EqualTo("Blink"));
                Assert.That(entry.Animation.Single().Shape, Is.EqualTo("Blink"));
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }
    }
}
