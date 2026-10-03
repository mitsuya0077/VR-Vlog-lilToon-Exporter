using System;
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
