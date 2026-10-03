using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class FaceEmoDefaultFaceTests
    {
        private string folder;
        private GameObject avatar;
        private AnimatorController controller;
        private Component descriptor;
        private AnimationClip common;
        private readonly List<Mesh> meshes = new List<Mesh>();

        [SetUp]
        public void SetUp()
        {
            var descriptorType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor")).FirstOrDefault(type => type != null);
            if (descriptorType == null) Assert.Ignore("Install the real VRChat SDK.");
            var name = "__FaceEmoDefaultFace_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            avatar = new GameObject("Avatar", typeof(Animator));
            var merged = new GameObject("Merged"); merged.transform.SetParent(avatar.transform, false);
            Renderer(merged, "Face", "Open", "Smile"); Renderer(merged, "Accessory", "Cheek");
            descriptor = avatar.AddComponent(descriptorType); SetFx(controller);
            common = Clip("Common underlay");
            Curve(common, "Merged/Face", "Open", AnimationCurve.Constant(0, 1, 100));
            Curve(common, "Merged/Face", "Smile", AnimationCurve.Constant(0, 1, 40));
            Curve(common, "Merged/Accessory", "Cheek", AnimationCurve.Constant(0, 1, 10));
            var layers = controller.layers; layers[0].name = "[ USER EDIT ] DEFAULT FACE"; controller.layers = layers;
            var state = layers[0].stateMachine.AddState("DEFAULT"); state.motion = common; state.writeDefaultValues = false;
            layers[0].stateMachine.defaultState = state;
            controller.AddLayer("[ USER EDIT ] FACE EMOTE PLAYER");
            var mode = Clip("Mode default is an alternative");
            Curve(mode, "Merged/Face", "Open", AnimationCurve.Constant(0, 1, 50));
            Curve(mode, "Merged/Face", "Smile", AnimationCurve.Constant(0, 1, 80));
            var modeState = controller.layers[1].stateMachine.AddState("Mode default"); modeState.motion = mode;
            modeState.writeDefaultValues = false; controller.layers[1].stateMachine.defaultState = modeState;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(avatar);
            foreach (var mesh in meshes) Object.DestroyImmediate(mesh); meshes.Clear();
            if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        private void Renderer(GameObject parent, string name, params string[] shapes)
        {
            var child = new GameObject(name, typeof(SkinnedMeshRenderer)); child.transform.SetParent(parent.transform, false);
            var mesh = new Mesh(); meshes.Add(mesh);
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }; mesh.triangles = new[] { 0, 1, 2 };
            foreach (var shape in shapes) mesh.AddBlendShapeFrame(shape, 100, Enumerable.Repeat(Vector3.up, 3).ToArray(), null, null);
            child.GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
        }

        private void SetFx(RuntimeAnimatorController runtime)
        {
            using (var data = new SerializedObject(descriptor))
            {
                data.FindProperty("customizeAnimationLayers").boolValue = true;
                var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = 1;
                var layer = layers.GetArrayElementAtIndex(0);
                var type = layer.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
                layer.FindPropertyRelative("isDefault").boolValue = false;
                layer.FindPropertyRelative("animatorController").objectReferenceValue = runtime;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private AnimationClip Clip(string name)
        {
            var clip = new AnimationClip { name = name }; AssetDatabase.AddObjectToAsset(clip, controller); return clip;
        }

        private static void Curve(AnimationClip clip, string path, string shape, AnimationCurve curve)
            => AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + shape), curve);

        private static string OriginalPath(string preparedPath) => preparedPath == "Merged/Face" ? "Body" :
            preparedPath == "Merged/Accessory" ? "Accessory" : null;

        private static VrChatExpressionMenu.Source Menu(float smile)
        {
            var source = new VrChatExpressionMenu.Source();
            var entry = new VrChatExpressionMenu.Entry { Id = "faceemo/0", Name = "Registered branch" };
            entry.Values.Add(new VrChatExpressionMenu.MorphValue { Path = "Body", Shape = "Smile", Weight = smile });
            source.Entries.Add(entry); return source;
        }

        [TestCase(0)]
        [TestCase(100)]
        public void BranchUsesCommonUnderlayAndExplicitZeroWithoutInheritingModeDefault(float smile)
        {
            var menu = Menu(smile);
            FaceEmoExpressions.ApplyPreparedDefaultFace(avatar, menu, OriginalPath);
            var entry = menu.Entries.Single(); Assert.That(entry.Error, Is.Null);
            Assert.That(entry.Values.Single(value => value.Path == "Body" && value.Shape == "Open").Weight, Is.EqualTo(100));
            Assert.That(entry.Values.Single(value => value.Shape == "Smile").Weight, Is.EqualTo(smile));
            Assert.That(entry.Values.Single(value => value.Path == "Accessory" && value.Shape == "Cheek").Weight, Is.EqualTo(10));
            Assert.That(entry.Values.Select(value => value.Path), Does.Not.Contain("Merged/Face"));
        }

        [Test]
        public void ExplicitOpeningZeroOverridesCommonOpeningHundred()
        {
            var menu = Menu(100); menu.Entries[0].Values.Add(new VrChatExpressionMenu.MorphValue { Path = "Body", Shape = "Open", Weight = 0 });
            FaceEmoExpressions.ApplyPreparedDefaultFace(avatar, menu, OriginalPath);
            Assert.That(menu.Entries[0].Values.Single(value => value.Shape == "Open").Weight, Is.Zero);
        }

        [Test]
        public void UnresolvedOriginalRendererAndAnimatedCommonFaceAreActionableErrors()
        {
            var menu = Menu(100);
            FaceEmoExpressions.ApplyPreparedDefaultFace(avatar, menu, path => null);
            Assert.That(menu.Entries[0].Error, Does.Contain("元Renderer"));
            menu = Menu(100); Curve(common, "Merged/Face", "Open", AnimationCurve.Linear(0, 0, 10, 100));
            FaceEmoExpressions.ApplyPreparedDefaultFace(avatar, menu, OriginalPath);
            Assert.That(menu.Entries[0].Error, Does.Contain("時間で変化"));
        }

        [Test]
        public void PreparedOverrideAndExclusionApplyToTheCommonUnderlay()
        {
            var replacement = Clip("Prepared common override");
            Curve(replacement, "Merged/Face", "Open", AnimationCurve.Constant(0, 1, 60));
            Curve(replacement, "Merged/Accessory", "Cheek", AnimationCurve.Constant(0, 1, 30));
            var runtime = new AnimatorOverrideController(controller);
            try
            {
                runtime.ApplyOverrides(new[] { new KeyValuePair<AnimationClip, AnimationClip>(common, replacement) }); SetFx(runtime);
                var menu = Menu(100);
                FaceEmoExpressions.ApplyPreparedDefaultFace(avatar, menu, OriginalPath, path => path == "Merged/Accessory");
                Assert.That(menu.Entries[0].Error, Is.Null);
                Assert.That(menu.Entries[0].Values.Single(value => value.Shape == "Open").Weight, Is.EqualTo(60));
                Assert.That(menu.Entries[0].Values.Any(value => value.Shape == "Cheek"), Is.False);
            }
            finally { Object.DestroyImmediate(runtime); }
        }
    }
}
