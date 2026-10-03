using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class FaceEmoPreparedFxIntegrationTests
    {
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void RegisteredBranchUsesOriginalPlayerSlotAndNativeCommonWeightOnce(bool fractionalCommon, bool writeDefaults)
        {
            using var fixture = new Fixture(fractionalCommon, writeDefaults);
            var original = fixture.RegisteredClip;
            var snapshot = FaceEmoExpressions.CaptureRegistered(fixture.Avatar, fixture.Registered,
                deferPermanentOverrides: true);
            var old = fixture.Skin;
            fixture.ReplaceRenderer();
            snapshot.RebindPrepared(renderer => ReferenceEquals(renderer, old) ? fixture.Skin : renderer);
            var source = new VrChatExpressionMenu.Source { Controller = fixture.Controller };
            var serial = 0;
            FaceEmoExpressions.ReadRegistered(fixture.Avatar, fixture.Registered, "FaceEmo", source,
                ref serial, new HashSet<object>(), 0, bindings: snapshot);
            var entry = source.Entries.Single();
            Assert.That(entry.Error, Is.Null);
            Assert.That(entry.Values.All(value => value.Path == "Prepared/Merged"), Is.True);
            Assert.That(entry.Values.Single(value => value.Shape == "Pupil").Weight, Is.Zero, "FX is deferred until the common face is validated.");
            using var bindings = new PreparedExpressionBindings(fixture.Avatar, source);
            var expected = fixture.NativeSelectedWeights();
            FaceEmoExpressions.ApplyPreparedDefaultFace(fixture.Avatar, source, bindings, registeredBindings: snapshot);
            Assert.That(entry.Error, Is.Null);
            foreach (var pair in expected)
                Assert.That(entry.Values.Single(value => value.Shape == pair.Key).Weight,
                    Is.EqualTo(pair.Value).Within(.02f), pair.Key);
            Assert.That(entry.Values.Single(value => value.Shape == "Pupil").Weight, Is.EqualTo(50).Within(.02f));
            if (!writeDefaults)
                Assert.That(entry.Values.Single(value => value.Shape == "Open").Weight,
                    Is.EqualTo(fractionalCommon ? 50 : 80).Within(.02f), "Raw common values must not bypass their original layer weight.");
            var result = entry.Values.Select(value => (value.Path, value.Shape, value.Weight)).ToArray();
            FaceEmoExpressions.ApplyPreparedDefaultFace(fixture.Avatar, source, bindings, registeredBindings: snapshot);
            Assert.That(entry.Values.Select(value => (value.Path, value.Shape, value.Weight)), Is.EqualTo(result),
                "A completed deferred evaluation cannot be applied twice.");
            Assert.That(AnimationUtility.GetCurveBindings(original).All(value => value.path == "Face"), Is.True);
            Assert.That(fixture.Skin.GetBlendShapeWeight(0), Is.EqualTo(20), "Native sampling must leave the prepared base untouched.");
        }

        [TestCase("duplicate")]
        [TestCase("missing")]
        [TestCase("write-defaults")]
        [TestCase("additive")]
        public void AmbiguousGeneratedPlayerCannotSilentlyUseStandaloneLayerOrder(string kind)
        {
            using var fixture = new Fixture(false, false);
            if (kind == "duplicate") fixture.Controller.AddLayer("[ USER EDIT ] FACE EMOTE PLAYER");
            if (kind == "missing") fixture.Controller.RemoveLayer(2);
            if (kind == "write-defaults")
            {
                var additional = fixture.Controller.layers[2].stateMachine.AddState("Incompatible state");
                additional.motion = fixture.PreparedBranch; additional.writeDefaultValues = true;
            }
            if (kind == "additive")
            {
                var layers = fixture.Controller.layers; layers[2].blendingMode = AnimatorLayerBlendingMode.Additive;
                fixture.Controller.layers = layers;
            }
            var snapshot = FaceEmoExpressions.CaptureRegistered(fixture.Avatar, fixture.Registered, deferPermanentOverrides: true);
            var source = new VrChatExpressionMenu.Source { Controller = fixture.Controller }; var serial = 0;
            FaceEmoExpressions.ReadRegistered(fixture.Avatar, fixture.Registered, "FaceEmo", source,
                ref serial, new HashSet<object>(), 0, bindings: snapshot);
            Assert.That(source.Entries.Single().Error, Does.Contain("FaceEmo"));
            Assert.That(source.Entries.Single().Error, Does.Contain(kind == "write-defaults" ? "Write Defaults" : "FX"));
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly GameObject Avatar;
            internal readonly AnimatorController Controller;
            internal readonly AnimationClip RegisteredClip, PreparedBranch;
            internal SkinnedMeshRenderer Skin;
            internal readonly object Registered;
            private readonly Mesh mesh;
            private readonly string folder;

            internal Fixture(bool fractionalCommon, bool writeDefaults)
            {
                var descriptorType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly =>
                    assembly.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor")).FirstOrDefault(type => type != null);
                if (descriptorType == null) Assert.Ignore("Install the real VRChat SDK.");
                var name = "__FaceEmoPreparedFx_" + Guid.NewGuid().ToString("N");
                AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
                Controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
                Avatar = new GameObject("Avatar", typeof(Animator));
                var child = new GameObject("Face", typeof(SkinnedMeshRenderer)); child.transform.SetParent(Avatar.transform, false);
                Skin = child.GetComponent<SkinnedMeshRenderer>();
                mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
                foreach (var shape in new[] { "Open", "Smile", "Pupil", "Customization" })
                    mesh.AddBlendShapeFrame(shape, 100, Enumerable.Repeat(Vector3.up * .1f, 3).ToArray(), null, null);
                Skin.sharedMesh = mesh; Weights();
                var descriptor = Avatar.AddComponent(descriptorType);
                using (var data = new SerializedObject(descriptor))
                {
                    data.FindProperty("customizeAnimationLayers").boolValue = true;
                    var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = 1;
                    var layer = layers.GetArrayElementAtIndex(0); var type = layer.FindPropertyRelative("type");
                    type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
                    layer.FindPropertyRelative("isDefault").boolValue = false;
                    layer.FindPropertyRelative("animatorController").objectReferenceValue = Controller;
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
                var baseClip = Clip("Base", ("Customization", 30));
                SetState(0, "Base", baseClip, false);
                Controller.AddLayer("[ USER EDIT ] DEFAULT FACE");
                SetState(1, "DEFAULT", Clip("Common", ("Open", 80), ("Smile", 40), ("Pupil", 0)), false,
                    fractionalCommon ? .5f : 1);
                Controller.AddLayer("[ USER EDIT ] FACE EMOTE PLAYER");
                SetState(2, "Alternative mode default", Clip("Mode", ("Open", 60), ("Smile", 90), ("Pupil", 80)), writeDefaults);
                PreparedBranch = Clip("Prepared registered branch", ("Smile", 75), ("Pupil", 0));
                var branch = Controller.layers[2].stateMachine.AddState("Registered branch");
                branch.motion = PreparedBranch; branch.writeDefaultValues = writeDefaults;
                Controller.AddLayer("Permanent pupil");
                SetState(3, "Pupil", Clip("Pupil", ("Pupil", 100)), false, .5f);
                RegisteredClip = new AnimationClip { name = "Registered branch" };
                Curve(RegisteredClip, "Face", "Smile", 75); Curve(RegisteredClip, "Face", "Pupil", 0);
                AssetDatabase.CreateAsset(RegisteredClip, folder + "/Registered.anim");
                Registered = new { Modes = new[] { new { ChangeDefaultFace = true, DisplayName = "Registered",
                    Animation = new { GUID = AssetDatabase.AssetPathToGUID(folder + "/Registered.anim") } } } };
                AssetDatabase.SaveAssets();
            }

            private void Weights()
            { Skin.SetBlendShapeWeight(0, 20); Skin.SetBlendShapeWeight(1, 10); Skin.SetBlendShapeWeight(2, 10); Skin.SetBlendShapeWeight(3, 30); }

            internal void ReplaceRenderer()
            {
                var old = Skin;
                var parent = new GameObject("Prepared"); parent.transform.SetParent(Avatar.transform, false);
                var child = new GameObject("Merged", typeof(SkinnedMeshRenderer)); child.transform.SetParent(parent.transform, false);
                Skin = child.GetComponent<SkinnedMeshRenderer>(); Skin.sharedMesh = mesh; Weights();
                Object.DestroyImmediate(old.gameObject);
                // A historical path reused by another renderer cannot become
                // the registered target merely because the string matches.
                var reused = new GameObject("Face", typeof(SkinnedMeshRenderer)); reused.transform.SetParent(Avatar.transform, false);
                reused.GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
            }

            private AnimationClip Clip(string name, params (string Shape, float Weight)[] values)
            {
                var clip = new AnimationClip { name = name }; AssetDatabase.AddObjectToAsset(clip, Controller);
                foreach (var value in values) Curve(clip, "Prepared/Merged", value.Shape, value.Weight);
                return clip;
            }
            private static void Curve(AnimationClip clip, string path, string shape, float weight) => AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + shape), AnimationCurve.Constant(0, 1, weight));
            private void SetState(int index, string name, AnimationClip clip, bool writeDefaults, float weight = 1)
            {
                var layers = Controller.layers; layers[index].defaultWeight = weight; layers[index].stateMachine.name = layers[index].name;
                Controller.layers = layers;
                var state = layers[index].stateMachine.AddState(name); state.motion = clip; state.writeDefaultValues = writeDefaults;
                layers[index].stateMachine.defaultState = state;
            }

            internal Dictionary<string, float> NativeSelectedWeights()
            {
                var copy = Object.Instantiate(Avatar);
                var animator = copy.GetComponent<Animator>(); animator.runtimeAnimatorController = null;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.enabled = true;
                var machine = Controller.layers[2].stateMachine;
                var previous = machine.defaultState;
                var graph = PlayableGraph.Create("FaceEmo direct native reference");
                try
                {
                    machine.defaultState = machine.states.Single(child => child.state.motion == PreparedBranch).state;
                    graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                    var playable = AnimatorControllerPlayable.Create(graph, Controller);
                    AnimationPlayableOutput.Create(graph, "Reference", animator).SetSourcePlayable(playable);
                    graph.Play(); graph.Evaluate(0);
                    for (var frame = 0; frame < 120; frame++) graph.Evaluate(.01f);
                    var skin = copy.transform.Find("Prepared/Merged").GetComponent<SkinnedMeshRenderer>();
                    return Enumerable.Range(0, mesh.blendShapeCount).ToDictionary(mesh.GetBlendShapeName, skin.GetBlendShapeWeight);
                }
                finally { graph.Destroy(); machine.defaultState = previous; Object.DestroyImmediate(copy); }
            }

            public void Dispose()
            { Object.DestroyImmediate(Avatar); Object.DestroyImmediate(mesh); if (folder != null) AssetDatabase.DeleteAsset(folder); }
        }
    }
}
