using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UniGLTF;
using UniVRM10;
using UnityEditor;
using UnityEngine;
using VRVlog.Expressions;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    // Explicit identity-map fixtures exercise the post-preparation contract.
    // They do not claim an installed NDMF mesh merger produced these mappings.
    public sealed class PreparedMenuMergedRendererTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task EqualStaticEndpointRoundTripsOnceAndReturnsToNeutral(bool registeredFaceEmo)
        {
            using var f = new Fixture();
            var menu = registeredFaceEmo ? f.RegisteredMenu(75, 75) : Menu(75, 75);
            var entry = menu.Entries.Single();
            Assert.That(entry.Error, Is.Null);
            var values = entry.Values.ToArray();
            f.Prepare(menu);
            var neutral = Vertices(f.Merged);
            f.Merged.SetBlendShapeWeight(0, 75); var selected = Vertices(f.Merged); f.Merged.SetBlendShapeWeight(0, 25);
            Assert.That(Vector3.Distance(neutral[0], selected[0]), Is.GreaterThan(.01f));
            AvatarBaseShape.Preserve(f.Avatar.Copy, f.Avatar.Copy, f.Owned, null);
            var expressions = f.Bake(menu);
            Assert.That(expressions.Single().Targets, Has.Count.EqualTo(1), "One final channel must contribute once.");
            Assert.That(entry.Values, Is.EqualTo(values));
            Assert.That(entry.Values.Select(value => value.Path), Is.EquivalentTo(new[] { "Front", "Back" }));
            Assert.That(entry.Values.Select(value => value.Weight), Is.EqualTo(new[] { 75f, 75f }));
            var bytes = VrmMenuExpressions.Add(Vrm10Exporter.Export(new GltfExportSettings(), f.Avatar.Copy,
                textureSerializer: new MobileTextureSerializer(null),
                vrmMeta: new VRM10ObjectMeta { Name = "Merged menu fixture", Version = "1", Authors = new List<string> { "Tests" } }), expressions);
            var imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
            try
            {
                var clip = imported.Vrm.Expression.CustomClips.Single(value => value.name == "VRChat / " + entry.Name);
                Assert.That(clip.MorphTargetBindings, Has.Length.EqualTo(1));
                var binding = clip.MorphTargetBindings.Single();
                var skin = imported.transform.Find(binding.RelativePath).GetComponent<SkinnedMeshRenderer>();
                foreach (var weight in new[] { 0f, 1f, 0f })
                {
                    imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(clip.name), weight); imported.Runtime.Process();
                    AssertVertices(weight == 0 ? neutral : selected, Vertices(skin));
                }
            }
            finally { Object.DestroyImmediate(imported.gameObject); }
            Assert.That(f.Avatar.Source.GetComponentsInChildren<SkinnedMeshRenderer>().All(skin => skin.sharedMesh == f.Avatar.Mesh), Is.True);
            Assert.That(f.Avatar.Mesh.blendShapeCount, Is.EqualTo(3));
        }

        [Test]
        public void DifferentShapesShareOneCompositeAndRetainUnreferencedRest()
        {
            using var f = new Fixture();
            var menu = Menu(75, 80, "Smile"); f.Prepare(menu);
            var neutral = Vertices(f.Merged);
            f.Merged.SetBlendShapeWeight(0, 75); f.Merged.SetBlendShapeWeight(1, 80);
            var selected = Vertices(f.Merged);
            f.Merged.SetBlendShapeWeight(0, 25); f.Merged.SetBlendShapeWeight(1, 10);
            AvatarBaseShape.Preserve(f.Avatar.Copy, f.Avatar.Copy, f.Owned, null);
            var expression = f.Bake(menu).Single();
            Assert.That(expression.Targets, Has.Count.EqualTo(1));
            Apply(f.Merged, expression.Targets, 100); AssertVertices(selected, Vertices(f.Merged));
            Apply(f.Merged, expression.Targets, 0); AssertVertices(neutral, Vertices(f.Merged));
            Assert.That(selected[0].z, Is.EqualTo(neutral[0].z).Within(.00002f), "Unreferenced customization must remain neutral.");
        }

        [Test]
        public void IndependentRenderersStillHaveIndependentTargets()
        {
            using var f = new Fixture();
            var menu = Menu(75, 80); f.Prepare(menu, merge: false);
            AvatarBaseShape.Preserve(f.Avatar.Copy, f.Avatar.Copy, f.Owned, null);
            var expression = f.Bake(menu).Single();
            Assert.That(expression.Targets, Has.Count.EqualTo(2));
            Assert.That(expression.Targets.Distinct().Count(), Is.EqualTo(2));
            foreach (var path in new[] { "Front", "Back" })
            {
                var skin = f.Avatar.Copy.transform.Find(path).GetComponent<SkinnedMeshRenderer>();
                var target = expression.Targets.Single(name => skin.sharedMesh.GetBlendShapeIndex(name) >= 0);
                var delta = new Vector3[skin.sharedMesh.vertexCount];
                skin.sharedMesh.GetBlendShapeFrameVertices(skin.sharedMesh.GetBlendShapeIndex(target), 0, delta, null, null);
                Assert.That(delta[0].y, Is.EqualTo(.03f * (path == "Front" ? .5f : .55f)).Within(.00002f));
            }
        }

        [Test]
        public void MatchingAnimatedAliasesHaveOneProgramAndPreserveSource()
        {
            using var f = new Fixture();
            var menu = AnimatedMenu(); var entry = menu.Entries.Single();
            var curves = entry.Animation.Select(value => value.Curve).ToArray();
            var animations = entry.Animation.ToArray(); var values = entry.Values.ToArray();
            f.Prepare(menu); AvatarBaseShape.Preserve(f.Avatar.Copy, f.Avatar.Copy, f.Owned, null);
            var expression = f.Bake(menu).Single();
            Assert.That(expression.Targets, Has.Count.EqualTo(1));
            Assert.That(expression.Animation.Channels, Has.Count.EqualTo(1));
            Assert.That(expression.Animation.Channels.Single().Curve.Evaluate(.5), Is.EqualTo(70).Within(.00001));
            Assert.That(entry.Values, Is.EqualTo(values)); Assert.That(entry.Animation, Is.EqualTo(animations));
            Assert.That(entry.Animation.Select(value => value.Curve), Is.EqualTo(curves));
            Assert.That(entry.Animation.Select(value => value.Path), Is.EqualTo(new[] { "Front", "Back" }));
            Assert.That(curves.Select(curve => curve.Keys[1].Value), Is.EqualTo(new[] { 100d, 100d }));
        }

        [TestCase("value")]
        [TestCase("tangent")]
        [TestCase("weight")]
        [TestCase("wrap")]
        [TestCase("static")]
        public void TemporalCollisionRejectsBeforeAnyEntryBakes(string difference)
        {
            using var f = new Fixture();
            var menu = AnimatedMenu(); var entry = menu.Entries.Single();
            var second = entry.Animation[1].Curve;
            if (difference == "value") second.Keys[1].Value = 90;
            if (difference == "tangent") second.Keys[1].InTangent = 0;
            if (difference == "weight") second.Keys[1].InWeight = .2;
            if (difference == "wrap") second.PostWrap = "loop";
            if (difference == "static") entry.Animation.RemoveAt(0);
            var earlier = new VrChatExpressionMenu.Entry { Name = "Earlier valid entry" };
            earlier.Values.Add(new VrChatExpressionMenu.MorphValue { Path = "Front", Shape = "Smile", Weight = 50 });
            menu.Entries.Insert(0, earlier);
            var values = entry.Values.ToArray(); var animations = entry.Animation.ToArray();
            f.Prepare(menu); var before = f.Merged.sharedMesh;
            var error = Assert.Throws<InvalidOperationException>(() => f.Bake(menu));
            Assert.That(error.Message, Does.Contain("Prepared/Merged").And.Contain("Open"));
            Assert.That(f.Owned, Is.Empty, "A later conflict must not bake the earlier entry.");
            Assert.That(f.Merged.sharedMesh, Is.SameAs(before)); Assert.That(before.blendShapeCount, Is.EqualTo(3));
            Assert.That(entry.Values, Is.EqualTo(values)); Assert.That(entry.Animation, Is.EqualTo(animations));
            Assert.That(entry.Values.Select(value => value.Path), Is.EqualTo(new[] { "Front", "Back" }));
        }

        [Test]
        public void DifferentStaticWeightsStopWithFinalRendererAndShape()
        {
            using var f = new Fixture(); var menu = Menu(75, 50); f.Prepare(menu);
            var error = Assert.Throws<InvalidOperationException>(() => f.Bake(menu));
            Assert.That(error.Message, Does.Contain("Prepared/Merged").And.Contain("Open"));
            Assert.That(f.Owned, Is.Empty); Assert.That(f.Merged.sharedMesh, Is.SameAs(f.Avatar.Mesh));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InconsistentCapturedSnapshotsStopBeforeBaking(bool differentMesh)
        {
            using var f = new Fixture(); var menu = Menu(75, 75); f.Prepare(menu);
            if (differentMesh)
            {
                var snapshot = Object.Instantiate(f.Avatar.Mesh); f.Resources.Add(snapshot);
                f.Bindings.Get("Back").Mesh = snapshot;
            }
            else f.Bindings.Get("Back").Weights[2] = 0;
            Assert.Throws<InvalidOperationException>(() => f.Bake(menu));
            Assert.That(f.Owned, Is.Empty); Assert.That(f.Merged.sharedMesh, Is.SameAs(f.Avatar.Mesh));
        }

        [Test]
        public void DifferentAnimatedShapesKeepIndependentPrograms()
        {
            using var f = new Fixture(); var menu = AnimatedMenu();
            menu.Entries[0].Values[1].Shape = "Smile"; menu.Entries[0].Animation[1].Shape = "Smile";
            f.Prepare(menu); AvatarBaseShape.Preserve(f.Avatar.Copy, f.Avatar.Copy, f.Owned, null);
            var expression = f.Bake(menu).Single();
            Assert.That(expression.Targets, Has.Count.EqualTo(1));
            Assert.That(expression.Animation.Channels, Has.Count.EqualTo(2));
            Assert.That(expression.Animation.Channels.SelectMany(channel => channel.Points).Where(point => point.Target != null)
                .Select(point => point.Target).Distinct().Count(), Is.EqualTo(2));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void StaticAndConstantAnimatedAliasesAreEquivalent(bool staticFirst)
        {
            using var f = new Fixture(); var menu = Menu(40, 40); var entry = menu.Entries.Single();
            var curve = Curve(); curve.Keys[1].Value = 40; curve.Keys.ForEach(key => { key.InTangent = 0; key.OutTangent = 0; });
            entry.Animation.Add(new VrChatExpressionMenu.AnimatedMorph { Path = staticFirst ? "Back" : "Front", Shape = "Open", Curve = curve });
            entry.Duration = 1; f.Prepare(menu);
            f.Merged.SetBlendShapeWeight(0, 40); var selected = Vertices(f.Merged); f.Merged.SetBlendShapeWeight(0, 25);
            AvatarBaseShape.Preserve(f.Avatar.Copy, f.Avatar.Copy, f.Owned, null);
            var expression = f.Bake(menu).Single();
            Assert.That(expression.Targets, Has.Count.EqualTo(1));
            Apply(f.Merged, expression.Targets, 100); AssertVertices(selected, Vertices(f.Merged));
            Assert.That(expression.Animation == null || expression.Animation.Channels.Single().Curve.Evaluate(500) == 40, Is.True);
        }

        private static VrChatExpressionMenu.Source Menu(float first, float second, string secondShape = "Open")
        {
            var menu = new VrChatExpressionMenu.Source(); var entry = new VrChatExpressionMenu.Entry { Id = "menu/1", Name = "Merged endpoint" };
            entry.Values.Add(new VrChatExpressionMenu.MorphValue { Path = "Front", Shape = "Open", Weight = first });
            entry.Values.Add(new VrChatExpressionMenu.MorphValue { Path = "Back", Shape = secondShape, Weight = second });
            menu.Entries.Add(entry); return menu;
        }
        private static ExpressionAnimationData.Curve Curve()
        {
            var curve = new ExpressionAnimationData.Curve();
            curve.Keys.Add(new ExpressionAnimationData.Key { Time = 0, Value = 40, InTangent = 60, OutTangent = 60 });
            curve.Keys.Add(new ExpressionAnimationData.Key { Time = 1, Value = 100, InTangent = 60, OutTangent = 60 }); return curve;
        }
        private static VrChatExpressionMenu.Source AnimatedMenu()
        {
            var menu = Menu(40, 40); menu.Entries[0].Duration = 1;
            foreach (var path in new[] { "Front", "Back" })
                menu.Entries[0].Animation.Add(new VrChatExpressionMenu.AnimatedMorph { Path = path, Shape = "Open", Curve = Curve() });
            return menu;
        }
        private static Vector3[] Vertices(SkinnedMeshRenderer skin)
        {
            var baked = new Mesh(); try { skin.BakeMesh(baked); return baked.vertices; } finally { Object.DestroyImmediate(baked); }
        }
        private static void Apply(SkinnedMeshRenderer skin, IEnumerable<string> targets, float weight)
        { foreach (var target in targets) skin.SetBlendShapeWeight(skin.sharedMesh.GetBlendShapeIndex(target), weight); }
        private static void AssertVertices(Vector3[] expected, Vector3[] actual)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            for (var i = 0; i < expected.Length; i++) Assert.That(Vector3.Distance(actual[i], expected[i]), Is.LessThan(.00003f), "Vertex " + i);
        }

        public sealed class RegisteredList { public object[] Modes, Groups = Array.Empty<object>(); }
        public sealed class RegisteredMode { public string DisplayName; public bool ChangeDefaultFace; public object[] Branches; }
        public sealed class RegisteredBranch { public RegisteredAnimation BaseAnimation; }
        public sealed class RegisteredAnimation { public string GUID; }

        private sealed class Fixture : IDisposable
        {
            internal readonly AttachmentConnectionTests.Fixture Avatar = new AttachmentConnectionTests.Fixture();
            internal readonly List<Mesh> Owned = new List<Mesh>();
            internal readonly List<Object> Resources = new List<Object>();
            internal PreparedExpressionBindings Bindings;
            internal SkinnedMeshRenderer Merged;
            private string directory;
            internal Fixture()
            {
                Avatar.Mesh.ClearBlendShapes();
                foreach (var delta in new[] { ("Open", Vector3.up * .03f), ("Smile", Vector3.right * .04f), ("Customization", Vector3.forward * .02f) })
                    Avatar.Mesh.AddBlendShapeFrame(delta.Item1, 100, Enumerable.Repeat(delta.Item2, Avatar.Mesh.vertexCount).ToArray(), null, null);
                Avatar.Mesh.RecalculateTangents();
                foreach (var skin in Avatar.Source.GetComponentsInChildren<SkinnedMeshRenderer>().Concat(Avatar.Skins))
                { skin.SetBlendShapeWeight(0, 25); skin.SetBlendShapeWeight(1, 10); skin.SetBlendShapeWeight(2, 40); }
            }
            internal VrChatExpressionMenu.Source RegisteredMenu(float first, float second)
            {
                var name = "__MergedMenu_" + Guid.NewGuid().ToString("N"); AssetDatabase.CreateFolder("Assets", name); directory = "Assets/" + name;
                var clip = new AnimationClip { name = "Registered endpoint" }; AssetDatabase.CreateAsset(clip, directory + "/Endpoint.anim");
                foreach (var pair in new[] { ("Front", first), ("Back", second) })
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(pair.Item1, typeof(SkinnedMeshRenderer), "blendShape.Open"),
                        AnimationCurve.Constant(0, 1, pair.Item2));
                AssetDatabase.SaveAssets();
                var list = new RegisteredList { Modes = new object[] { new RegisteredMode { DisplayName = "Pattern", Branches = new object[] {
                    new RegisteredBranch { BaseAnimation = new RegisteredAnimation { GUID = AssetDatabase.AssetPathToGUID(directory + "/Endpoint.anim") } }
                } } } };
                var menu = new VrChatExpressionMenu.Source(); var serial = 0;
                FaceEmoExpressions.ReadRegistered(Avatar.Copy, list, "FaceEmo", menu, ref serial, new HashSet<object>(), 0); return menu;
            }
            internal void Prepare(VrChatExpressionMenu.Source menu, bool merge = true)
            {
                Bindings = new PreparedExpressionBindings(Avatar.Copy, menu, Avatar.Source);
                if (merge)
                {
                    var parent = new GameObject("Prepared"); parent.transform.SetParent(Avatar.Copy.transform, false);
                    var target = new GameObject("Merged"); target.transform.SetParent(parent.transform, false);
                    Merged = target.AddComponent<SkinnedMeshRenderer>();
                    Merged.sharedMesh = Avatar.Mesh; Merged.sharedMaterials = Avatar.Skins[0].sharedMaterials;
                    Merged.bones = Avatar.Skins[0].bones; Merged.rootBone = Avatar.Skins[0].rootBone;
                    Merged.SetBlendShapeWeight(0, 25); Merged.SetBlendShapeWeight(1, 10); Merged.SetBlendShapeWeight(2, 40);
                    Bindings.RebindPrepared(renderer => Avatar.Skins.Contains(renderer) ? Merged : renderer);
                    foreach (var skin in Avatar.Skins) Object.DestroyImmediate(skin.gameObject);
                }
                Bindings.Capture(menu); Assert.That(menu.Entries.All(entry => entry.Error == null), Is.True);
            }
            internal List<VrmMenuExpressions.Expression> Bake(VrChatExpressionMenu.Source menu)
                => VrChatExpressionBaker.Bake(null, Avatar.Copy, menu, Owned, null, Bindings);
            public void Dispose()
            {
                Bindings?.Dispose(); Avatar.Dispose();
                foreach (var mesh in Owned) Object.DestroyImmediate(mesh);
                foreach (var resource in Resources) Object.DestroyImmediate(resource);
                if (directory != null) AssetDatabase.DeleteAsset(directory);
            }
        }
    }
}
