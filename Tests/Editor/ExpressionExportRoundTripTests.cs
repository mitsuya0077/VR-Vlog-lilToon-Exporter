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
    public sealed class ExpressionExportRoundTripTests
    {
        private static Type SdkType(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);

        [TestCase(false)]
        [TestCase(true)]
        public async Task DriverMenuAndAnimatedGestureSurviveRealExportAndReimport(bool fullLilToon)
        {
            var descriptorType = SdkType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            if (descriptorType == null) Assert.Ignore("Install the real VRChat SDK.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            var folderName = "__ExpressionRoundTrip_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folderName); var folder = "Assets/" + folderName;
            Vrm10Instance imported = null;
            try
            {
                foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = Shader.Find("lilToon");
                var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
                controller.AddParameter("Menu", AnimatorControllerParameterType.Int);
                controller.AddParameter("Face", AnimatorControllerParameterType.Int);
                controller.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
                AnimatorState State(AnimatorStateMachine machine, string name, AnimationClip clip = null)
                { var s = machine.AddState(name); s.writeDefaultValues = false; s.motion = clip; return s; }
                void Transition(AnimatorState from, AnimatorState to, string parameter, int value)
                { var t = from.AddTransition(to); t.hasExitTime = false; t.duration = 0; t.AddCondition(AnimatorConditionMode.Equals, value, parameter); }
                AnimationClip Clip(string name, bool moving, float value)
                {
                    var clip = new AnimationClip { name = name };
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail"),
                        moving ? AnimationCurve.Linear(0, 35, 1, 100) : AnimationCurve.Constant(0, 1, value));
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.extra_cheek5"),
                        moving ? AnimationCurve.Linear(0, 0, 1, 100) : AnimationCurve.Constant(0, 1, 0));
                    AssetDatabase.AddObjectToAsset(clip, controller); return clip;
                }
                var control = controller.layers[0].stateMachine;
                var idle = State(control, "Idle"); control.defaultState = idle;
                var gate = State(control, "Lock expression"); Transition(idle, gate, "Menu", 1);
                ParameterDriverExpressionTests.Driver(gate, ParameterDriverExpressionTests.Op("Set", "Face", 1));
                controller.AddLayer("Expressions"); var layers = controller.layers; layers[1].defaultWeight = 1; controller.layers = layers;
                var machine = layers[1].stateMachine;
                var neutral = State(machine, "Neutral", Clip("Neutral", false, 35)); machine.defaultState = neutral;
                var smile = State(machine, "Smile", Clip("Smile", false, 75)); Transition(neutral, smile, "Face", 1);
                var animation = State(machine, "Animated gesture", Clip("Animated gesture", true, 0)); Transition(neutral, animation, "GestureRight", 2);

                var menu = ScriptableObject.CreateInstance(SdkType("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu"));
                AssetDatabase.CreateAsset(menu, folder + "/Menu.asset");
                using (var data = new SerializedObject(menu))
                {
                    var controls = data.FindProperty("controls"); controls.arraySize = 1;
                    var item = controls.GetArrayElementAtIndex(0); item.FindPropertyRelative("name").stringValue = "Face / Smile";
                    var type = item.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "Toggle");
                    item.FindPropertyRelative("parameter").FindPropertyRelative("name").stringValue = "Menu";
                    item.FindPropertyRelative("value").floatValue = 1; data.ApplyModifiedPropertiesWithoutUndo();
                }
                var parameters = ScriptableObject.CreateInstance(SdkType("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters"));
                AssetDatabase.CreateAsset(parameters, folder + "/Parameters.asset");
                using (var data = new SerializedObject(parameters))
                {
                    var list = data.FindProperty("parameters"); list.arraySize = 1;
                    var item = list.GetArrayElementAtIndex(0); item.FindPropertyRelative("name").stringValue = "Menu";
                    var type = item.FindPropertyRelative("valueType"); type.enumValueIndex = Array.IndexOf(type.enumNames, "Int");
                    item.FindPropertyRelative("defaultValue").floatValue = 0; data.ApplyModifiedPropertiesWithoutUndo();
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
                var warnings = new List<string>();
                var before = fixture.Mesh.vertices;
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Expression regression", "Tests", warnings,
                    exporterVersion: fullLilToon ? "expression-regression" : null, lilToonVersion: fullLilToon ? "2.3.4" : null,
                    blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                Assert.That(VrmMenuExpressions.CountRegistered(bytes), Is.EqualTo(2), string.Join("\n", warnings));
                var glb = GlbDocument.Read(bytes);
                var animations = ExpressionAnimationData.Read(((Dictionary<string, object>)glb.Json["extensions"])[ExpressionAnimationData.Extension]);
                Assert.That(animations.Single().Channels.Count, Is.EqualTo(1));
                Assert.That(animations.Single().Channels[0].Curve.Evaluate(.5), Is.EqualTo(67.5).Within(.001));
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                const string expressionName = "VRChat / Face / Smile";
                var expression = imported.Vrm.Expression.CustomClips.Single(c => c.name == expressionName);
                Assert.That(expression.OverrideBlink.ToString(), Is.EqualTo("block"));
                Assert.That(expression.OverrideMouth.ToString(), Is.EqualTo("block"));
                Assert.That(expression.OverrideLookAt.ToString(), Is.EqualTo("block"));
                imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(expressionName), 1);
                imported.Runtime.Process();
                var binding = expression.MorphTargetBindings.Single();
                var skinOut = imported.transform.Find(binding.RelativePath).GetComponent<SkinnedMeshRenderer>();
                Assert.That(skinOut.GetBlendShapeWeight(binding.Index), Is.EqualTo(100).Within(.01));
                var delta = new Vector3[skinOut.sharedMesh.vertexCount];
                skinOut.sharedMesh.GetBlendShapeFrameVertices(binding.Index, 0, delta, null, null);
                Assert.That(Vector3.Distance(skinOut.sharedMesh.vertices[0] + delta[0], before[0] + new Vector3(.02f, .01f, 0) * .75f), Is.LessThan(.0001));
                Assert.That(fixture.Mesh.vertices, Is.EqualTo(before));
                Assert.That(fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>().All(s => s.sharedMesh == fixture.Mesh && s.GetBlendShapeWeight(0) == 35), Is.True);
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
