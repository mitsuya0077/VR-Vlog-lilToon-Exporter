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
    public sealed class NeutralShapeExportTests
    {
        [TestCase(false, false, false)]
        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, true, false)]
        [TestCase(false, false, true)]
        [TestCase(true, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, true, true)]
        public async Task RuntimeNeutralAndGeneratedProxyExportPreserveStationaryPoseAndAuthoredExpression(
            bool fullLilToon, bool timeVarying, bool generatedProxy)
        {
            var descriptorType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly =>
                assembly.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor")).FirstOrDefault(type => type != null);
            if (descriptorType == null) Assert.Ignore("Install the real VRChat SDK.");
            var shader = Shader.Find("lilToon");
            if (shader == null) Assert.Ignore("Install the real lilToon shader.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            var folderName = "__NeutralCompatibilityExport_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folderName); var folder = "Assets/" + folderName;
            var vrm = ScriptableObject.CreateInstance<VRM10Object>();
            var authored = ScriptableObject.CreateInstance<VRM10Expression>();
            Vrm10Instance imported = null;
            try
            {
                fixture.Mesh.ClearBlendShapes();
                fixture.Mesh.AddBlendShapeFrame("Open", 100,
                    Enumerable.Repeat(Vector3.up * .03f, fixture.Mesh.vertexCount).ToArray(), null, null);
                fixture.Mesh.AddBlendShapeFrame("Reactive face", 100,
                    Enumerable.Repeat(Vector3.right * .02f, fixture.Mesh.vertexCount).ToArray(), null, null);
                var skins = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
                foreach (var skin in skins)
                {
                    skin.SetBlendShapeWeight(0, 0); skin.SetBlendShapeWeight(1, 35);
                    skin.sharedMaterial.shader = shader;
                }
                var vertices = fixture.Mesh.vertices;
                authored.name = "Authored reactive";
                authored.MorphTargetBindings = skins.Select(skin => new MorphTargetBinding(skin.name, 1, .8f)).ToArray();
                vrm.Expression.CustomClips.Add(authored);
                fixture.Source.AddComponent<Vrm10Instance>().Vrm = vrm;
                var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
                AnimationClip Clip(string name, string shape, AnimationCurve curve)
                {
                    var clip = new AnimationClip { name = name };
                    foreach (var skin in skins)
                        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(skin.name,
                            typeof(SkinnedMeshRenderer), "blendShape." + shape), curve);
                    AssetDatabase.AddObjectToAsset(clip, controller); return clip;
                }
                AnimatorState State(AnimatorStateMachine machine, string name, AnimationClip clip)
                {
                    var state = machine.AddState(name); state.motion = clip; state.writeDefaultValues = false;
                    if (machine.states.Length == 1) machine.defaultState = state;
                    return state;
                }
                State(controller.layers[0].stateMachine, "Independent opening", Clip("Opening", "Open", AnimationCurve.Constant(0, 1, 100)));
                controller.AddLayer("Runtime reactive face"); var layers = controller.layers;
                layers[1].defaultWeight = 1; controller.layers = layers;
                var runtime = layers[1].stateMachine;
                var initial = State(runtime, "Runtime neutral", Clip("Runtime neutral", "Reactive face", timeVarying ?
                    AnimationCurve.Linear(0, 0, 10, 100) : AnimationCurve.Constant(0, 1, 0)));
                if (generatedProxy)
                {
                    controller.AddParameter(NeutralShapeSamplerTests.GestureWeightProxy, AnimatorControllerParameterType.Float);
                    var tree = new BlendTree { name = "Face controlled by generated proxy", blendType = BlendTreeType.Simple1D,
                        blendParameter = NeutralShapeSamplerTests.GestureWeightProxy, useAutomaticThresholds = false };
                    tree.AddChild(Clip("Proxy face zero", "Reactive face", AnimationCurve.Constant(0, 1, 0)), 0);
                    tree.AddChild(Clip("Proxy face full", "Reactive face", AnimationCurve.Constant(0, 1, 100)), 1);
                    AssetDatabase.AddObjectToAsset(tree, controller); initial.motion = tree;
                    var proxy = new AnimationClip { name = "Generated gesture weight proxy" };
                    AnimationUtility.SetEditorCurve(proxy, EditorCurveBinding.FloatCurve("", typeof(Animator),
                        NeutralShapeSamplerTests.GestureWeightProxy), NeutralShapeSamplerTests.GeneratedProxyCurve("firstIn", linear: timeVarying));
                    AssetDatabase.AddObjectToAsset(proxy, controller);
                    NeutralShapeSamplerTests.AssertNativeProxyMetadata(proxy, "firstIn", linear: timeVarying);
                    controller.AddLayer("Generated proxy writer"); layers = controller.layers;
                    layers[2].defaultWeight = 1; controller.layers = layers;
                    State(layers[2].stateMachine, "Generated proxy", proxy);
                }
                if (!timeVarying && !generatedProxy)
                {
                    controller.AddParameter("Voice", AnimatorControllerParameterType.Float);
                    var selected = State(runtime, "Voice chooses face", Clip("Reactive endpoint", "Reactive face", AnimationCurve.Constant(0, 1, 100)));
                    var transition = initial.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
                    transition.AddCondition(AnimatorConditionMode.Greater, .5f, "Voice");
                }
                var descriptor = fixture.Source.AddComponent(descriptorType);
                using (var data = new SerializedObject(descriptor))
                {
                    data.FindProperty("customizeAnimationLayers").boolValue = true;
                    var descriptorLayers = data.FindProperty("baseAnimationLayers"); descriptorLayers.arraySize = 1;
                    var layer = descriptorLayers.GetArrayElementAtIndex(0);
                    var type = layer.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
                    layer.FindPropertyRelative("isDefault").boolValue = false;
                    layer.FindPropertyRelative("animatorController").objectReferenceValue = controller;
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
                var controllerJson = EditorJsonUtility.ToJson(controller);
                var warnings = new List<string>();
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Neutral compatibility regression", "Tests", warnings,
                    exporterVersion: fullLilToon ? "neutral-compatibility-regression" : null,
                    lilToonVersion: fullLilToon ? "2.3.4" : null,
                    blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                if (generatedProxy && !timeVarying)
                    Assert.That(warnings.Any(warning => warning.Contains("FXの初期表情を固定できない")), Is.False, string.Join("\n", warnings));
                else Assert.That(warnings.Any(warning => warning.Contains("Reactive face") &&
                    warning.Contains(timeVarying ? "時間で変わる" : "Voice")), Is.True, string.Join("\n", warnings));
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                imported.Runtime.Process();
                var importedSkins = imported.GetComponentsInChildren<SkinnedMeshRenderer>();
                Assert.That(importedSkins, Has.Length.EqualTo(skins.Length));
                var expectedReactiveWeight = generatedProxy && !timeVarying ? 50 : 35;
                foreach (var skin in importedSkins)
                    Assert.That(Vector3.Distance(skin.sharedMesh.vertices[0], vertices[0] + new Vector3(.02f * expectedReactiveWeight / 100, .03f, 0)),
                        Is.LessThan(.00001), "The independent opening and either proved proxy pose or retained authored runtime weight must survive export.");
                var expression = imported.Vrm.Expression.CustomClips.Single(clip => clip.name == authored.name);
                Assert.That(expression.MorphTargetBindings, Has.Length.EqualTo(skins.Length));
                imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(authored.name), 1);
                imported.Runtime.Process();
                foreach (var skin in importedSkins)
                {
                    var point = skin.sharedMesh.vertices[0];
                    var delta = new Vector3[skin.sharedMesh.vertexCount];
                    for (var index = 0; index < skin.sharedMesh.blendShapeCount; index++)
                    {
                        skin.sharedMesh.GetBlendShapeFrameVertices(index, 0, delta, null, null);
                        point += delta[0] * skin.GetBlendShapeWeight(index) / 100;
                    }
                    Assert.That(Vector3.Distance(point, vertices[0] + new Vector3(.02f * .8f, .03f, 0)), Is.LessThan(.00001),
                        "Neutral fallback must preserve the authored expression endpoint and the independent opening underlay.");
                }
                Assert.That(skins.All(skin => skin.sharedMesh == fixture.Mesh && skin.GetBlendShapeWeight(0) == 0 && skin.GetBlendShapeWeight(1) == 35), Is.True);
                Assert.That(fixture.Mesh.vertices, Is.EqualTo(vertices));
                Assert.That(fixture.Source.GetComponent<Vrm10Instance>().Vrm, Is.SameAs(vrm));
                Assert.That(authored.MorphTargetBindings.All(binding => binding.Index == 1 && binding.Weight == .8f), Is.True);
                Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(controllerJson));
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                Object.DestroyImmediate(authored); Object.DestroyImmediate(vrm);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        [TestCase(false, false, false)]
        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, true, false)]
        [TestCase(false, false, true)]
        [TestCase(true, false, true)]
        public async Task FxDefaultOpeningShapeSurvivesExportWithoutAnExpressionMenu(bool fullLilToon, bool independentBlink, bool resetClosingShape)
        {
            var descriptorType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor"))
                .FirstOrDefault(type => type != null);
            if (descriptorType == null) Assert.Ignore("Install the real VRChat SDK.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            var folderName = "__NeutralShapeExport_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folderName);
            var folder = "Assets/" + folderName;
            Vrm10Instance imported = null;
            try
            {
                // Opening can raise an Open morph or reset an initially applied
                // closing morph. The default FX pose supplies both changes.
                var shape = resetClosingShape ? "Blink" : "Open";
                var neutralWeight = resetClosingShape ? 0 : 100;
                fixture.Mesh.ClearBlendShapes();
                fixture.Mesh.AddBlendShapeFrame(shape, 100,
                    Enumerable.Repeat(Vector3.up * .03f, fixture.Mesh.vertexCount).ToArray(), null, null);
                if (independentBlink)
                    fixture.Mesh.AddBlendShapeFrame("Blink", 100,
                        Enumerable.Repeat(Vector3.down * .04f, fixture.Mesh.vertexCount).ToArray(), null, null);
                var sourceSkins = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
                foreach (var skin in sourceSkins)
                {
                    skin.SetBlendShapeWeight(0, resetClosingShape ? 100 : 0);
                    skin.sharedMaterial.shader = Shader.Find("lilToon");
                }
                var vertices = fixture.Mesh.vertices;
                var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
                var clip = new AnimationClip { name = "Open at startup" };
                foreach (var path in new[] { "Front", "Back" })
                    AnimationUtility.SetEditorCurve(clip,
                        EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + shape),
                        AnimationCurve.Constant(0, 1, neutralWeight));
                AssetDatabase.AddObjectToAsset(clip, controller);
                var machine = controller.layers[0].stateMachine;
                var state = machine.AddState("Neutral open eyes");
                state.writeDefaultValues = false;
                state.motion = clip;
                machine.defaultState = state;
                if (independentBlink)
                {
                    controller.AddLayer("Independent automatic blink");
                    var layers = controller.layers; layers[1].defaultWeight = 1; controller.layers = layers;
                    var blinkClip = new AnimationClip { name = "Automatic blink" };
                    foreach (var path in new[] { "Front", "Back" })
                        AnimationUtility.SetEditorCurve(blinkClip,
                            EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape.Blink"),
                            AnimationCurve.Linear(0, 0, 10, 100));
                    AssetDatabase.AddObjectToAsset(blinkClip, controller);
                    var blinkMachine = layers[1].stateMachine;
                    var blinking = blinkMachine.AddState("Blinking"); blinking.motion = blinkClip;
                    blinking.writeDefaultValues = false; blinkMachine.defaultState = blinking;
                }
                var descriptor = fixture.Source.AddComponent(descriptorType);
                using (var data = new SerializedObject(descriptor))
                {
                    data.FindProperty("customizeAnimationLayers").boolValue = true;
                    var layers = data.FindProperty("baseAnimationLayers");
                    layers.arraySize = 1;
                    var layer = layers.GetArrayElementAtIndex(0);
                    var type = layer.FindPropertyRelative("type");
                    type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
                    layer.FindPropertyRelative("isDefault").boolValue = false;
                    layer.FindPropertyRelative("animatorController").objectReferenceValue = controller;
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Neutral opening regression", "Tests",
                    exporterVersion: fullLilToon ? "neutral-shape-regression" : null,
                    lilToonVersion: fullLilToon ? "2.3.4" : null,
                    blinkOptions: independentBlink || resetClosingShape ? null : new BlinkExportOptions { Mode = BlinkExportMode.None });
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                imported.Runtime.Process();
                foreach (var skin in imported.GetComponentsInChildren<SkinnedMeshRenderer>())
                    Assert.That(skin.sharedMesh.vertices[0].y, Is.EqualTo(vertices[0].y + .03f * neutralWeight / 100).Within(.00001),
                        "Default display must preserve the FX opening pose even with no expression menu.");
                Assert.That(sourceSkins.All(skin => skin.sharedMesh == fixture.Mesh && skin.GetBlendShapeWeight(0) == (resetClosingShape ? 100 : 0)), Is.True);
                Assert.That(fixture.Mesh.vertices, Is.EqualTo(vertices));
                Assert.That(state.motion, Is.SameAs(clip));
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
