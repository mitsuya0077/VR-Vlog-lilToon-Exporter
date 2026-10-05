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
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class LegacyBlinkMenuRoundTripTests
    {
        static Type SdkType(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(name)).FirstOrDefault(type => type != null);

        [TestCase(false)]
        [TestCase(true)]
        public async Task SplitLegacyBlinkAndDistinctNamedMenusKeepTheirAppearanceAfterReimport(bool fullLilToon)
        {
            var descriptorType = SdkType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            if (descriptorType == null) Assert.Ignore("Install the real VRChat SDK.");
            if (Shader.Find("lilToon") == null) Assert.Ignore("Install lilToon.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            var folderName = "__LegacyBlinkMenuRoundTrip_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folderName); var folder = "Assets/" + folderName;
            var owned = new List<Mesh>();
            Vrm10Instance imported = null;
            GameObject reference = null;
            try
            {
                foreach (var path in new[] { "Front", "Back" })
                {
                    var renderer = fixture.Source.transform.Find(path).GetComponent<SkinnedMeshRenderer>();
                    var mesh = Object.Instantiate(fixture.Mesh); owned.Add(mesh); mesh.ClearBlendShapes();
                    mesh.AddBlendShapeFrame("Face", 100, Enumerable.Repeat(Vector3.right * .02f, mesh.vertexCount).ToArray(), null, null);
                    mesh.AddBlendShapeFrame(path == "Front" ? "Blink_L" : "Blink_R", 100,
                        Enumerable.Repeat(Vector3.down * .03f, mesh.vertexCount).ToArray(), null, null);
                    renderer.sharedMesh = mesh; renderer.sharedMaterial.shader = Shader.Find("lilToon");
                    renderer.SetBlendShapeWeight(0, 17); renderer.SetBlendShapeWeight(1, 0);
                }
                var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
                controller.AddParameter("FaceMenu", AnimatorControllerParameterType.Int);
                AnimatorState State(string name, float value)
                {
                    var clip = new AnimationClip { name = name };
                    foreach (var path in new[] { "Front", "Back" })
                        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape.Face"), AnimationCurve.Constant(0, 1, value));
                    AssetDatabase.AddObjectToAsset(clip, controller);
                    var state = controller.layers[0].stateMachine.AddState(name); state.motion = clip; state.writeDefaultValues = false; return state;
                }
                var idle = State("Rest", 17); var selected = State("Selected", 75); controller.layers[0].stateMachine.defaultState = idle;
                var transition = idle.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
                transition.AddCondition(AnimatorConditionMode.Equals, 1, "FaceMenu");
                transition = selected.AddTransition(idle); transition.hasExitTime = false; transition.duration = 0;
                transition.AddCondition(AnimatorConditionMode.Equals, 0, "FaceMenu");
                var menu = ScriptableObject.CreateInstance(SdkType("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu"));
                AssetDatabase.CreateAsset(menu, folder + "/Menu.asset");
                using (var data = new SerializedObject(menu))
                {
                    var controls = data.FindProperty("controls"); controls.arraySize = 2;
                    for (var index = 0; index < 2; index++)
                    {
                        var control = controls.GetArrayElementAtIndex(index);
                        control.FindPropertyRelative("name").stringValue = index == 0 ? "Happy" : "Photo face";
                        var type = control.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "Toggle");
                        control.FindPropertyRelative("parameter").FindPropertyRelative("name").stringValue = "FaceMenu";
                        control.FindPropertyRelative("value").floatValue = 1;
                    }
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
                var parameters = ScriptableObject.CreateInstance(SdkType("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters"));
                AssetDatabase.CreateAsset(parameters, folder + "/Parameters.asset");
                using (var data = new SerializedObject(parameters))
                {
                    var list = data.FindProperty("parameters"); list.arraySize = 1; var parameter = list.GetArrayElementAtIndex(0);
                    parameter.FindPropertyRelative("name").stringValue = "FaceMenu";
                    var type = parameter.FindPropertyRelative("valueType"); type.enumValueIndex = Array.IndexOf(type.enumNames, "Int");
                    parameter.FindPropertyRelative("defaultValue").floatValue = 0; data.ApplyModifiedPropertiesWithoutUndo();
                }
                var descriptor = fixture.Source.AddComponent(descriptorType);
                using (var data = new SerializedObject(descriptor))
                {
                    data.FindProperty("customExpressions").boolValue = true;
                    data.FindProperty("expressionsMenu").objectReferenceValue = menu;
                    data.FindProperty("expressionParameters").objectReferenceValue = parameters;
                    data.FindProperty("customizeAnimationLayers").boolValue = true;
                    var list = data.FindProperty("baseAnimationLayers"); list.arraySize = 1;
                    var layer = list.GetArrayElementAtIndex(0); var type = layer.FindPropertyRelative("type");
                    type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
                    layer.FindPropertyRelative("isDefault").boolValue = false;
                    layer.FindPropertyRelative("animatorController").objectReferenceValue = controller;
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
                var originals = owned.Select(EditorJsonUtility.ToJson).ToArray();
                reference = Object.Instantiate(fixture.Source);
                foreach (var behaviour in reference.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                var warnings = new List<string>();
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Legacy split blink", "Tests", warnings,
                    exporterVersion: fullLilToon ? "split-blink-regression" : null, lilToonVersion: fullLilToon ? "2.3.4" : null);
                Assert.That(VrmMenuExpressions.CountRegistered(bytes), Is.EqualTo(2), string.Join("\n", warnings));
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                var names = new[] { "VRChat / Happy", "VRChat / Photo face" };
                var clips = names.Select(name => imported.Vrm.Expression.CustomClips.Single(clip => clip.name == name)).ToArray();
                Assert.That(clips[0].MorphTargetBindings.Select(binding => (binding.RelativePath, binding.Index, binding.Weight)),
                    Is.Not.EqualTo(clips[1].MorphTargetBindings.Select(binding => (binding.RelativePath, binding.Index, binding.Weight))));
                Assert.That(imported.Vrm.Expression.Blink, Is.Not.Null);
                Assert.That(imported.Vrm.Expression.BlinkLeft, Is.Not.Null);
                Assert.That(imported.Vrm.Expression.BlinkRight, Is.Not.Null);
                foreach (var key in new[] { ExpressionKey.Blink, ExpressionKey.BlinkLeft, ExpressionKey.BlinkRight,
                    ExpressionKey.CreateCustom(names[0]), ExpressionKey.CreateCustom(names[1]) })
                    foreach (var input in new[] { 0f, 1f, 0f })
                    {
                        imported.Runtime.Expression.SetWeight(key, input); imported.Runtime.Process();
                        foreach (var path in new[] { "Front", "Back" })
                        {
                            var expected = reference.transform.Find(path).GetComponent<SkinnedMeshRenderer>();
                            var blink = key.Equals(ExpressionKey.Blink) || key.Equals(ExpressionKey.BlinkLeft) || key.Equals(ExpressionKey.BlinkRight);
                            var closesThisEye = key.Equals(ExpressionKey.Blink) ||
                                key.Equals(ExpressionKey.BlinkLeft) && path == "Front" || key.Equals(ExpressionKey.BlinkRight) && path == "Back";
                            expected.SetBlendShapeWeight(0, blink || input == 0 ? 17 : 75);
                            expected.SetBlendShapeWeight(1, closesThisEye ? input * 100 : 0);
                            AssertVertices(Vertices(expected), Vertices(imported.GetComponentsInChildren<SkinnedMeshRenderer>().Single(skin => skin.name == path)), path + " / " + key + " / " + input);
                        }
                    }
                // Named menus keep independent morph identities and weights;
                // equal individual poses must still support additive selection.
                var rest = imported.GetComponentsInChildren<SkinnedMeshRenderer>().ToDictionary(skin => skin.name, Vertices);
                imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(names[0]), 1); imported.Runtime.Process();
                var single = imported.GetComponentsInChildren<SkinnedMeshRenderer>().ToDictionary(skin => skin.name, Vertices);
                imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(names[1]), 1); imported.Runtime.Process();
                foreach (var skin in imported.GetComponentsInChildren<SkinnedMeshRenderer>())
                    AssertVertices(rest[skin.name].Select((vertex, index) => vertex + 2 * (single[skin.name][index] - vertex)).ToArray(), Vertices(skin), "combined names / " + skin.name);
                Assert.That(owned.Select(EditorJsonUtility.ToJson), Is.EqualTo(originals));
                Assert.That(fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>().All(skin => skin.GetBlendShapeWeight(0) == 17 && skin.GetBlendShapeWeight(1) == 0), Is.True);
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                if (reference != null) Object.DestroyImmediate(reference);
                foreach (var mesh in owned) Object.DestroyImmediate(mesh);
                AssetDatabase.DeleteAsset(folder);
            }
        }
        static Vector3[] Vertices(SkinnedMeshRenderer renderer)
        {
            var mesh = new Mesh();
            try { renderer.BakeMesh(mesh, false); return mesh.vertices.Select(renderer.transform.TransformPoint).ToArray(); }
            finally { Object.DestroyImmediate(mesh); }
        }
        static void AssertVertices(Vector3[] expected, Vector3[] actual, string context)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            for (var index = 0; index < actual.Length; index++) Assert.That(Vector3.Distance(expected[index], actual[index]), Is.LessThan(.0005f), context);
        }
    }
}
