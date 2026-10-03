using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UniGLTF;
using UniVRM10;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRVlog.Expressions;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    // Explicit renderer identity maps exercise the post-preparation contract;
    // they do not claim an installed NDMF merger generated these mappings.
    public sealed class FaceEmoMergedUnderlayTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task PartialRegisteredBranchKeepsCommonFaceAndExplicitAliasPrecedence(bool openingZero)
        {
            using var f = new Fixture();
            var menu = f.RegisteredBranch(openingZero); var entry = menu.Entries.Single();
            var sourceValues = entry.Values.ToArray(); var sourceAnimation = entry.Animation.ToArray();
            f.Prepare(menu);
            Assert.That(f.Bindings.AuthoringPath("Prepared/Merged"), Is.Null);
            Assert.That(f.Bindings.AuthoringPaths("Prepared/Merged"), Is.EqualTo(new[] { "Back", "Front" }));
            var sampled = NeutralShapeSampler.Sample(f.Avatar.Copy);
            Assert.That(sampled.Single(value => value.Shape == "Open").Weight, Is.EqualTo(50).Within(.001f));
            Assert.That(sampled.Single(value => value.Shape == "Mouth").Weight, Is.EqualTo(80).Within(.001f));
            f.ApplyCommon(menu);
            Assert.That(entry.Error, Is.Null);
            Assert.That(entry.Values.Count(value => value.Shape == "Mouth"), Is.EqualTo(1));
            Assert.That(entry.Values.Single(value => value.Shape == "Mouth").Weight, Is.EqualTo(20));
            Assert.That(entry.Values.Where(value => value.Shape == "Open").Select(value => value.Path),
                Is.EquivalentTo(openingZero ? new[] { "Front" } : new[] { "Front", "Back" }));
            Assert.That(entry.Values.Where(value => value.Shape == "Open").Select(value => value.Weight),
                Is.All.EqualTo(openingZero ? 0 : 100));
            Assert.That(sourceValues.All(value => entry.Values.Contains(value)), Is.True);
            Assert.That(entry.Animation, Is.EqualTo(sourceAnimation));
            f.Bindings.Capture(menu);
            var neutral = Vertices(f.Merged);
            f.Pose(openingZero ? 0 : 100, 20); var selected = Vertices(f.Merged); f.Pose(50, 80);
            Assert.That(Vector3.Distance(neutral[0], selected[0]), Is.GreaterThan(.01f));
            Assert.That(selected[0].z, Is.EqualTo(neutral[0].z).Within(.00002f), "Untargeted customization stays neutral.");
            AvatarBaseShape.Preserve(f.Avatar.Copy, f.Avatar.Copy, f.Owned, null);
            var expression = f.Bake(menu).Single(); Assert.That(expression.Targets, Has.Count.EqualTo(1));
            var bytes = VrmMenuExpressions.Add(Vrm10AppearanceExporter.Export(new GltfExportSettings(), f.Avatar.Copy,
                new BuiltInVrm10MaterialExporter(), new MobileTextureSerializer(null),
                new VRM10ObjectMeta { Name = "Merged common face fixture", Version = "1", Authors = new List<string> { "Tests" } }),
                new[] { expression });
            var imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller(),
                controlRigGenerationOption: ControlRigGenerationOption.None);
            try
            {
                var clip = imported.Vrm.Expression.CustomClips.Single(value => value.name == "VRChat / " + entry.Name);
                Assert.That(clip.MorphTargetBindings, Has.Length.EqualTo(1));
                var skin = imported.transform.Find(clip.MorphTargetBindings.Single().RelativePath).GetComponent<SkinnedMeshRenderer>();
                foreach (var weight in new[] { 0f, 1f, 0f })
                {
                    imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(clip.name), weight); imported.Runtime.Process();
                    AssertVertices(weight == 0 ? neutral : selected, Vertices(skin));
                }
            }
            finally { imported.DisposeRuntime(); Object.DestroyImmediate(imported.gameObject); }
            Assert.That(f.Avatar.Source.GetComponentsInChildren<SkinnedMeshRenderer>().All(skin =>
                skin.sharedMesh == f.Avatar.Mesh && skin.GetBlendShapeWeight(0) == 25), Is.True);
            Assert.That(f.Avatar.Mesh.blendShapeCount, Is.EqualTo(3));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DuplicateCommonSamplesCoalesceOrRejectBeforePublication(bool conflicting)
        {
            using var f = new Fixture(); var menu = Branch(); f.Prepare(menu);
            var entry = menu.Entries.Single(); var originals = entry.Values.ToArray();
            var input = new[] { Value("Prepared/Merged", "Open", 100), Value("Prepared/Merged", "Open", conflicting ? 60 : 100) };
            // This guards normalization inputs; a Unity clip cannot itself store
            // two different curves with the exact same binding.
            if (conflicting)
            {
                var error = Assert.Throws<InvalidOperationException>(() =>
                    FaceEmoExpressions.ComposeCommonValues(f.Avatar.Copy, menu, input, f.Bindings));
                Assert.That(error.Message, Does.Contain("Prepared/Merged / Open"));
                Assert.That(entry.Values, Is.EqualTo(originals)); Assert.That(f.Owned, Is.Empty);
            }
            else
            {
                FaceEmoExpressions.ComposeCommonValues(f.Avatar.Copy, menu, input, f.Bindings);
                Assert.That(entry.Values.Count(value => value.Shape == "Open"), Is.EqualTo(2));
                Assert.That(entry.Values.Where(value => value.Shape == "Open").Select(value => value.Path), Is.EquivalentTo(new[] { "Front", "Back" }));
                f.Bindings.Capture(menu); AvatarBaseShape.Preserve(f.Avatar.Copy, f.Avatar.Copy, f.Owned, null);
                Assert.That(f.Bake(menu).Single().Targets, Has.Count.EqualTo(1));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ConflictingSelectedAliasesStopBeforeAnyUnderlayIsPublished(bool temporal)
        {
            using var f = new Fixture(); var menu = Branch();
            var invalid = new VrChatExpressionMenu.Entry { Id = "faceemo/1", Name = "Conflicting branch", Duration = 1 };
            invalid.Values.Add(Value("Front", "Open", 25)); invalid.Values.Add(Value("Back", "Open", temporal ? 25 : 50));
            if (temporal) invalid.Animation.Add(new VrChatExpressionMenu.AnimatedMorph { Path = "Front", Shape = "Open", Curve = Curve() });
            menu.Entries.Add(invalid); f.Prepare(menu);
            var values = menu.Entries.Select(entry => entry.Values.ToArray()).ToArray();
            var animations = invalid.Animation.ToArray(); var mesh = f.Merged.sharedMesh;
            f.ApplyCommon(menu);
            foreach (var entry in menu.Entries) Assert.That(entry.Error, Does.Contain("Prepared/Merged / Open"));
            for (var index = 0; index < menu.Entries.Count; index++) Assert.That(menu.Entries[index].Values, Is.EqualTo(values[index]));
            Assert.That(invalid.Animation, Is.EqualTo(animations)); Assert.That(f.Owned, Is.Empty);
            Assert.That(f.Merged.sharedMesh, Is.SameAs(mesh));
            if (temporal) Assert.That(invalid.Animation.Single().Curve.Evaluate(.5), Is.EqualTo(50).Within(.00001));
        }

        [Test]
        public void AnimatedExplicitAliasSuppressesEveryCommonAliasAndKeepsItsProgram()
        {
            using var f = new Fixture(); var menu = Branch(); var entry = menu.Entries.Single(); entry.Duration = 1;
            entry.Values.Add(Value("Front", "Open", 25));
            var curve = Curve(); var animated = new VrChatExpressionMenu.AnimatedMorph { Path = "Front", Shape = "Open", Curve = curve };
            entry.Animation.Add(animated); f.Prepare(menu); f.ApplyCommon(menu);
            Assert.That(entry.Error, Is.Null);
            Assert.That(entry.Values.Count(value => value.Shape == "Open"), Is.EqualTo(1));
            Assert.That(entry.Values.Single(value => value.Shape == "Open").Path, Is.EqualTo("Front"));
            Assert.That(entry.Animation.Single(), Is.SameAs(animated)); Assert.That(animated.Curve, Is.SameAs(curve));
            f.Bindings.Capture(menu); AvatarBaseShape.Preserve(f.Avatar.Copy, f.Avatar.Copy, f.Owned, null);
            var expression = f.Bake(menu).Single();
            Assert.That(expression.Targets, Has.Count.EqualTo(1)); Assert.That(expression.Animation.Channels, Has.Count.EqualTo(1));
            Assert.That(expression.Animation.Channels.Single().Curve, Is.SameAs(curve));
            Assert.That(curve.Evaluate(.5), Is.EqualTo(50).Within(.00001));
        }

        [Test]
        public void AmbiguousPreparedRendererDoesNotPublishCommonDefaults()
        {
            using var f = new Fixture(); var menu = Branch(); f.Prepare(menu);
            var values = menu.Entries.Single().Values.ToArray();
            var duplicate = new GameObject(f.Merged.name);
            duplicate.transform.SetParent(f.Merged.transform.parent, false);
            duplicate.AddComponent<SkinnedMeshRenderer>().sharedMesh = f.Avatar.Mesh;
            Assert.That(f.Bindings.AuthoringPaths("Prepared/Merged"), Is.Empty);
            f.ApplyCommon(menu);
            Assert.That(menu.Entries.Single().Error, Is.Not.Null);
            Assert.That(menu.Entries.Single().Values, Is.EqualTo(values)); Assert.That(f.Owned, Is.Empty);
        }

        [Test]
        public void CommonRendererAbsentFromBranchKeepsEveryOriginAndIndependentEndpoint()
        {
            using var f = new Fixture();
            var accessory = f.Accessory(); var menu = Branch("Accessory"); f.Prepare(menu); f.ApplyCommon(menu);
            var entry = menu.Entries.Single(); Assert.That(entry.Error, Is.Null);
            Assert.That(entry.Values.Where(value => value.Shape == "Open").Select(value => value.Path), Is.EquivalentTo(new[] { "Front", "Back" }));
            Assert.That(entry.Values.Where(value => value.Shape == "Mouth" && value.Path != "Accessory").Select(value => value.Path),
                Is.EquivalentTo(new[] { "Front", "Back" }));
            f.Bindings.Capture(menu);
            f.Pose(100, 40); var selected = Vertices(f.Merged); f.Pose(50, 80);
            accessory.SetBlendShapeWeight(1, 20); var independent = Vertices(accessory); accessory.SetBlendShapeWeight(1, 10);
            AvatarBaseShape.Preserve(f.Avatar.Copy, f.Avatar.Copy, f.Owned, null);
            var expression = f.Bake(menu).Single(); Assert.That(expression.Targets, Has.Count.EqualTo(2));
            foreach (var skin in new[] { f.Merged, accessory })
                foreach (var target in expression.Targets.Where(name => skin.sharedMesh.GetBlendShapeIndex(name) >= 0))
                    skin.SetBlendShapeWeight(skin.sharedMesh.GetBlendShapeIndex(target), 100);
            AssertVertices(selected, Vertices(f.Merged)); AssertVertices(independent, Vertices(accessory));
        }

        private static VrChatExpressionMenu.MorphValue Value(string path, string shape, float weight)
            => new VrChatExpressionMenu.MorphValue { Path = path, Shape = shape, Weight = weight };
        private static VrChatExpressionMenu.Source Branch(string path = "Front")
        {
            var source = new VrChatExpressionMenu.Source(); var entry = new VrChatExpressionMenu.Entry { Id = "faceemo/0", Name = "Partial branch" };
            entry.Values.Add(Value(path, "Mouth", 20)); source.Entries.Add(entry); return source;
        }
        private static ExpressionAnimationData.Curve Curve()
        {
            var curve = new ExpressionAnimationData.Curve();
            curve.Keys.Add(new ExpressionAnimationData.Key { Time = 0, Value = 25, InTangent = 50, OutTangent = 50 });
            curve.Keys.Add(new ExpressionAnimationData.Key { Time = 1, Value = 75, InTangent = 50, OutTangent = 50 }); return curve;
        }
        private static Vector3[] Vertices(SkinnedMeshRenderer skin)
        { var baked = new Mesh(); try { skin.BakeMesh(baked); return baked.vertices; } finally { Object.DestroyImmediate(baked); } }
        private static void AssertVertices(Vector3[] expected, Vector3[] actual)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            for (var index = 0; index < expected.Length; index++)
                Assert.That(Vector3.Distance(actual[index], expected[index]), Is.LessThan(.00003f), "Vertex " + index);
        }
        public sealed class RegisteredList { public object[] Modes, Groups = Array.Empty<object>(); }
        public sealed class RegisteredMode { public string DisplayName; public bool ChangeDefaultFace; public object[] Branches; }
        public sealed class RegisteredBranch { public RegisteredAnimation BaseAnimation; }
        public sealed class RegisteredAnimation { public string GUID; }

        private sealed class Fixture : IDisposable
        {
            internal readonly AttachmentConnectionTests.Fixture Avatar = new AttachmentConnectionTests.Fixture();
            internal readonly List<Mesh> Owned = new List<Mesh>();
            internal PreparedExpressionBindings Bindings;
            internal SkinnedMeshRenderer Merged;
            private readonly string directory;
            private readonly AnimatorController controller;
            internal Fixture()
            {
                var descriptorType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly =>
                    assembly.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor")).FirstOrDefault(type => type != null);
                if (descriptorType == null) { Avatar.Dispose(); Assert.Ignore("Install the real VRChat SDK."); }
                var name = "__FaceEmoMergedUnderlay_" + Guid.NewGuid().ToString("N");
                AssetDatabase.CreateFolder("Assets", name); directory = "Assets/" + name;
                controller = AnimatorController.CreateAnimatorControllerAtPath(directory + "/FX.controller");
                var descriptor = Avatar.Copy.AddComponent(descriptorType);
                using (var data = new SerializedObject(descriptor))
                {
                    data.FindProperty("customizeAnimationLayers").boolValue = true;
                    var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = 1;
                    var layer = layers.GetArrayElementAtIndex(0); var type = layer.FindPropertyRelative("type");
                    type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
                    layer.FindPropertyRelative("isDefault").boolValue = false;
                    layer.FindPropertyRelative("animatorController").objectReferenceValue = controller;
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
                Avatar.Mesh.ClearBlendShapes();
                foreach (var delta in new[] { ("Open", Vector3.up * .03f), ("Mouth", Vector3.right * .04f), ("Customization", Vector3.forward * .02f) })
                    Avatar.Mesh.AddBlendShapeFrame(delta.Item1, 100, Enumerable.Repeat(delta.Item2, Avatar.Mesh.vertexCount).ToArray(), null, null);
                foreach (var skin in Avatar.Source.GetComponentsInChildren<SkinnedMeshRenderer>().Concat(Avatar.Skins))
                { skin.SetBlendShapeWeight(0, 25); skin.SetBlendShapeWeight(1, 10); skin.SetBlendShapeWeight(2, 40); }
            }
            internal VrChatExpressionMenu.Source RegisteredBranch(bool openingZero)
            {
                var clip = new AnimationClip { name = "Partial registered branch" }; AssetDatabase.CreateAsset(clip, directory + "/Branch.anim");
                SetCurve(clip, "Front", "Mouth", 20); if (openingZero) SetCurve(clip, "Front", "Open", 0);
                AssetDatabase.SaveAssets();
                var list = new RegisteredList { Modes = new object[] { new RegisteredMode { DisplayName = "Pattern", Branches = new object[] {
                    new RegisteredBranch { BaseAnimation = new RegisteredAnimation { GUID = AssetDatabase.AssetPathToGUID(directory + "/Branch.anim") } }
                } } } };
                var menu = new VrChatExpressionMenu.Source(); var serial = 0;
                FaceEmoExpressions.ReadRegistered(Avatar.Copy, list, "FaceEmo", menu, ref serial, new HashSet<object>(), 0);
                Assert.That(menu.Entries, Has.Count.EqualTo(1)); Assert.That(menu.Entries.Single().Error, Is.Null); return menu;
            }
            internal SkinnedMeshRenderer Accessory()
            {
                var child = new GameObject("Accessory"); child.transform.SetParent(Avatar.Copy.transform, false);
                var skin = child.AddComponent<SkinnedMeshRenderer>(); CopyRenderer(Avatar.Skins[0], skin);
                skin.SetBlendShapeWeight(0, 25); skin.SetBlendShapeWeight(1, 10); skin.SetBlendShapeWeight(2, 40); return skin;
            }
            internal void Prepare(VrChatExpressionMenu.Source menu)
            {
                Bindings = new PreparedExpressionBindings(Avatar.Copy, menu, Avatar.Source);
                var parent = new GameObject("Prepared"); parent.transform.SetParent(Avatar.Copy.transform, false);
                var target = new GameObject("Merged"); target.transform.SetParent(parent.transform, false);
                Merged = target.AddComponent<SkinnedMeshRenderer>(); CopyRenderer(Avatar.Skins[0], Merged); Pose(50, 80);
                Bindings.RebindPrepared(renderer => Avatar.Skins.Contains(renderer) ? Merged : renderer);
                foreach (var skin in Avatar.Skins) Object.DestroyImmediate(skin.gameObject);
                // Official FxGenerator puts the common DEFAULT FACE in its
                // own layer; mode/branch states are alternatives on PLAYER.
                var common = Clip("Common DEFAULT FACE"); SetCurve(common, "Prepared/Merged", "Open", 100); SetCurve(common, "Prepared/Merged", "Mouth", 40);
                var layers = controller.layers; layers[0].name = "[ USER EDIT ] DEFAULT FACE"; layers[0].defaultWeight = 1; controller.layers = layers;
                // Native state hashes use the root machine name. Match the
                // source generator's layer/machine names after renaming base.
                layers[0].stateMachine.name = layers[0].name;
                var state = layers[0].stateMachine.AddState("DEFAULT"); state.motion = common; state.writeDefaultValues = false; layers[0].stateMachine.defaultState = state;
                controller.AddLayer("[ USER EDIT ] FACE EMOTE PLAYER"); layers = controller.layers; layers[1].defaultWeight = 1; controller.layers = layers;
                var mode = Clip("Alternative mode default"); SetCurve(mode, "Prepared/Merged", "Open", 50); SetCurve(mode, "Prepared/Merged", "Mouth", 80);
                var modeState = layers[1].stateMachine.AddState("Mode default"); modeState.motion = mode; modeState.writeDefaultValues = false;
                layers[1].stateMachine.defaultState = modeState;
                var branch = Clip("Alternative partial branch");
                foreach (var value in menu.Entries.First().Values)
                    SetCurve(branch, value.Path == "Front" || value.Path == "Back" ? "Prepared/Merged" : value.Path, value.Shape, value.Weight);
                var branchState = layers[1].stateMachine.AddState("Branch"); branchState.motion = branch; branchState.writeDefaultValues = false;
                Assert.That(controller.layers.All(layer => layer.name == layer.stateMachine.name), Is.True);
            }
            internal void Pose(float open, float mouth)
            { Merged.SetBlendShapeWeight(0, open); Merged.SetBlendShapeWeight(1, mouth); Merged.SetBlendShapeWeight(2, 40); }
            internal void ApplyCommon(VrChatExpressionMenu.Source menu)
                => FaceEmoExpressions.ApplyPreparedDefaultFace(Avatar.Copy, menu, Bindings);
            internal List<VrmMenuExpressions.Expression> Bake(VrChatExpressionMenu.Source menu)
                => VrChatExpressionBaker.Bake(null, Avatar.Copy, menu, Owned, null, Bindings);
            private AnimationClip Clip(string name)
            { var clip = new AnimationClip { name = name }; AssetDatabase.AddObjectToAsset(clip, controller); return clip; }
            private static void SetCurve(AnimationClip clip, string path, string shape, float weight)
                => AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + shape), AnimationCurve.Constant(0, 1, weight));
            private static void CopyRenderer(SkinnedMeshRenderer source, SkinnedMeshRenderer target)
            { target.sharedMesh = source.sharedMesh; target.sharedMaterials = source.sharedMaterials; target.bones = source.bones; target.rootBone = source.rootBone; }
            public void Dispose()
            {
                Bindings?.Dispose(); Avatar.Dispose();
                foreach (var mesh in Owned) Object.DestroyImmediate(mesh);
                if (directory != null) AssetDatabase.DeleteAsset(directory);
            }
        }
    }
}
