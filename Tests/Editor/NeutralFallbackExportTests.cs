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
    public sealed class NeutralFallbackExportTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task UnresolvedBodyRestExportsWithIndependentOpenEyesAndAbsoluteEndpoints(bool externalInput)
        {
            var descriptorType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly =>
                assembly.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor")).FirstOrDefault(type => type != null);
            if (descriptorType == null) Assert.Ignore("Install the real VRChat SDK.");
            var shader = Shader.Find("lilToon");
            if (shader == null) Assert.Ignore("Install lilToon for the full exporter entry point.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            var folderName = "__NeutralFallbackExport_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folderName); var folder = "Assets/" + folderName;
            Vrm10Instance imported = null;
            GameObject reference = null;
            try
            {
                fixture.Mesh.ClearBlendShapes();
                foreach (var item in new[] {
                    (Name: "Body size", Delta: Vector3.right * .02f),
                    (Name: "Open", Delta: Vector3.up * .015f),
                    (Name: "Blink", Delta: Vector3.down * .03f) })
                    fixture.Mesh.AddBlendShapeFrame(item.Name, 100,
                        Enumerable.Repeat(item.Delta, fixture.Mesh.vertexCount).ToArray(), null, null);
                foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    skin.sharedMaterial.shader = shader;
                    skin.SetBlendShapeWeight(0, 17); skin.SetBlendShapeWeight(1, 0); skin.SetBlendShapeWeight(2, 0);
                }
                var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
                AnimationClip Clip(string name, string shape, float value)
                {
                    var clip = new AnimationClip { name = name };
                    foreach (var path in new[] { "Front", "Back" })
                        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + shape),
                            AnimationCurve.Constant(0, 10, value));
                    AssetDatabase.AddObjectToAsset(clip, controller); return clip;
                }
                AnimatorState State(AnimatorStateMachine machine, AnimationClip clip)
                {
                    var state = machine.AddState(clip.name); state.motion = clip; state.writeDefaultValues = false;
                    if (machine.states.Length == 1) machine.defaultState = state;
                    return state;
                }
                State(controller.layers[0].stateMachine, Clip("Independent open eyes", "Open", 100));
                controller.AddLayer("Unresolved body rest"); var layers = controller.layers;
                layers[1].defaultWeight = 1; controller.layers = layers;
                var machine = layers[1].stateMachine;
                var initial = Clip("Body rest with dynamic input", "Body size", 63);
                var idle = State(machine, initial);
                var alternate = State(machine, Clip("Other body phase", "Body size", 91));
                var parameter = externalInput ? "EyeHeightAsMeters" : "Body phase";
                controller.AddParameter(parameter, AnimatorControllerParameterType.Float);
                if (!externalInput)
                    AnimationUtility.SetEditorCurve(initial, EditorCurveBinding.FloatCurve("", typeof(Animator), parameter),
                        AnimationCurve.Linear(0, 0, 10, 1));
                var transition = idle.AddTransition(alternate); transition.hasExitTime = false; transition.duration = 0;
                transition.AddCondition(AnimatorConditionMode.Greater, .5f, parameter);
                var descriptor = fixture.Source.AddComponent(descriptorType);
                using (var data = new SerializedObject(descriptor))
                {
                    data.FindProperty("customizeAnimationLayers").boolValue = true;
                    var playableLayers = data.FindProperty("baseAnimationLayers"); playableLayers.arraySize = 1;
                    var layer = playableLayers.GetArrayElementAtIndex(0); var type = layer.FindPropertyRelative("type");
                    type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
                    layer.FindPropertyRelative("isDefault").boolValue = false;
                    layer.FindPropertyRelative("animatorController").objectReferenceValue = controller;
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
                var vrm = ScriptableObject.CreateInstance<VRM10Object>();
                var endpoint = ScriptableObject.CreateInstance<VRM10Expression>(); endpoint.name = "Body endpoint";
                endpoint.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 0, .8f), new MorphTargetBinding("Back", 0, .8f) };
                vrm.Expression.CustomClips.Add(endpoint);
                AssetDatabase.CreateAsset(vrm, folder + "/Vrm.asset"); AssetDatabase.CreateAsset(endpoint, folder + "/Endpoint.asset");
                fixture.Source.AddComponent<Vrm10Instance>().Vrm = vrm;
                var blink = new BlinkExportOptions { Mode = BlinkExportMode.Manual };
                foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>())
                    blink.Both.Add(new BlinkShapeBinding { Renderer = skin, Shape = "Blink", Weight = 100 });
                var meshBefore = EditorJsonUtility.ToJson(fixture.Mesh); var controllerBefore = EditorJsonUtility.ToJson(controller);
                var clipBefore = EditorJsonUtility.ToJson(initial); var endpointBefore = EditorJsonUtility.ToJson(endpoint);
                reference = Object.Instantiate(fixture.Source);
                foreach (var behaviour in reference.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                var warnings = new List<string>();
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Fallback neutral regression", "Tests", warnings, blinkOptions: blink);
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(warnings.Any(value => value.Contains("Body size") && value.Contains(parameter)), Is.True);
                Assert.That(imported.Vrm.Expression.CustomClips.Any(clip => clip.name == endpoint.name), Is.True);
                Assert.That(imported.Vrm.Expression.Blink, Is.Not.Null);
                foreach (var key in new[] { ExpressionKey.CreateCustom(endpoint.name), ExpressionKey.Blink })
                    foreach (var input in new[] { 0f, 1f, 0f })
                    {
                        imported.Runtime.Expression.SetWeight(key, input); imported.Runtime.Process();
                        foreach (var path in new[] { "Front", "Back" })
                        {
                            var expected = reference.transform.Find(path).GetComponent<SkinnedMeshRenderer>();
                            expected.SetBlendShapeWeight(0, key.Equals(ExpressionKey.Blink) || input == 0 ? 17 : 80);
                            expected.SetBlendShapeWeight(1, 100);
                            expected.SetBlendShapeWeight(2, key.Equals(ExpressionKey.Blink) ? input * 100 : 0);
                            var actual = imported.GetComponentsInChildren<SkinnedMeshRenderer>().Single(skin => skin.name == path);
                            var a = Vertices(expected); var b = Vertices(actual);
                            Assert.That(b.Length, Is.EqualTo(a.Length));
                            for (var i = 0; i < a.Length; i++)
                                Assert.That(Vector3.Distance(a[i], b[i]), Is.LessThan(.0005f), path + " / " + key + " / " + input);
                        }
                    }
                Assert.That(fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>().All(skin =>
                    skin.sharedMesh == fixture.Mesh && skin.GetBlendShapeWeight(0) == 17 &&
                    skin.GetBlendShapeWeight(1) == 0 && skin.GetBlendShapeWeight(2) == 0), Is.True);
                Assert.That(EditorJsonUtility.ToJson(fixture.Mesh), Is.EqualTo(meshBefore));
                Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(controllerBefore));
                Assert.That(EditorJsonUtility.ToJson(initial), Is.EqualTo(clipBefore));
                Assert.That(EditorJsonUtility.ToJson(endpoint), Is.EqualTo(endpointBefore));
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                if (reference != null) Object.DestroyImmediate(reference);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        static Vector3[] Vertices(SkinnedMeshRenderer renderer)
        {
            var mesh = new Mesh();
            try { renderer.BakeMesh(mesh, false); return mesh.vertices.Select(renderer.transform.TransformPoint).ToArray(); }
            finally { Object.DestroyImmediate(mesh); }
        }
    }
}
