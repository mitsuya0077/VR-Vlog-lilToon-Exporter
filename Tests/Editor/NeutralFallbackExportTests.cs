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
using UnityEngine.Animations;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class NeutralFallbackExportTests
    {
        [TestCase(false, 0)]
        [TestCase(true, 0)]
        [TestCase(false, 1)]
        [TestCase(true, 1)]
        [TestCase(false, 2)]
        [TestCase(false, 3)]
        public async Task UnresolvedBodyRestExportsWithIndependentOpenEyesAndAbsoluteEndpoints(bool externalInput, int worldFixMode)
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
                if (!externalInput && worldFixMode != 3)
                    AnimationUtility.SetEditorCurve(initial, EditorCurveBinding.FloatCurve("", typeof(Animator), parameter),
                        AnimationCurve.Linear(0, 0, 10, 1));
                if (worldFixMode != 3)
                {
                    var transition = idle.AddTransition(alternate); transition.hasExitTime = false; transition.duration = 0;
                    transition.AddCondition(AnimatorConditionMode.Greater, .5f, parameter);
                }
                Component constraint = null; AnimationClip worldFix = null; string worldFixBefore = null;
                BoxCollider collider = null;
                if (worldFixMode == 3)
                {
                    collider = fixture.Source.transform.Find("Front").gameObject.AddComponent<BoxCollider>();
                    collider.size = new Vector3(2, 3, 4);
                    AnimationUtility.SetEditorCurve(initial, EditorCurveBinding.FloatCurve("Front", typeof(BoxCollider), "m_Size.x"),
                        AnimationCurve.Constant(0, 1, 6));
                }
                if (worldFixMode == 1 || worldFixMode == 2)
                {
                    var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly =>
                        assembly.GetType("VRC.SDK3.Dynamics.Constraint.Components.VRCParentConstraint")).FirstOrDefault(value => value != null);
                    if (type == null) Assert.Ignore("Install the real VRChat SDK with VRCParentConstraint.");
                    // Keep this on a rendered part so optional-gimmick pruning
                    // cannot make the world-fix regression vacuously pass.
                    constraint = fixture.Source.transform.Find("Front").gameObject.AddComponent(type);
                    if (constraint is Behaviour behaviour) behaviour.enabled = false;
                    worldFix = worldFixMode == 2 ? initial : new AnimationClip { name = "U_WorldFix_off" };
                    AnimationUtility.SetEditorCurve(worldFix, EditorCurveBinding.FloatCurve("Front", type, "FreezeToWorld"),
                        AnimationCurve.Constant(0, 1, 1));
                    if (worldFixMode == 1)
                    {
                        AssetDatabase.AddObjectToAsset(worldFix, controller);
                        controller.AddLayer("World fixed accessory"); layers = controller.layers;
                        layers[layers.Length - 1].defaultWeight = 1; controller.layers = layers;
                        var support = State(layers[layers.Length - 1].stateMachine, worldFix); support.writeDefaultValues = true;
                    }
                    controller.AddLayer("Independent face above world fix"); layers = controller.layers;
                    layers[layers.Length - 1].defaultWeight = 1; controller.layers = layers;
                    State(layers[layers.Length - 1].stateMachine, Clip("Independent face above world fix", "Open", 100));
                    worldFixBefore = EditorJsonUtility.ToJson(worldFix);
                }
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
                if (worldFixMode == 1)
                {
                    var native = NativeOpenWeights(fixture.Source, controller);
                    Assert.That(native.Length, Is.EqualTo(2));
                    foreach (var weight in native) Assert.That(weight, Is.EqualTo(100).Within(.01),
                        "The original Unity FX graph must preserve the upper independent Open=100 layer despite unresolved body rest and WD support.");
                }
                reference = Object.Instantiate(fixture.Source);
                foreach (var behaviour in reference.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                var warnings = new List<string>();
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Fallback neutral regression", "Tests", warnings, blinkOptions: blink);
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                var reason = worldFixMode == 2 ? "FreezeToWorld" : worldFixMode == 3 ? "m_Size.x" : parameter;
                Assert.That(warnings.Any(value => value.Contains("Body size") && value.Contains(reason) &&
                    (worldFixMode != 3 || value.Contains(initial.name))), Is.True);
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
                                Assert.That(Vector3.Distance(a[i], b[i]), Is.LessThan(.0005f), path + " / " + key + " / " + input +
                                    "\nExport warnings:\n" + string.Join("\n", warnings));
                        }
                    }
                Assert.That(fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>().All(skin =>
                    skin.sharedMesh == fixture.Mesh && skin.GetBlendShapeWeight(0) == 17 &&
                    skin.GetBlendShapeWeight(1) == 0 && skin.GetBlendShapeWeight(2) == 0), Is.True);
                Assert.That(EditorJsonUtility.ToJson(fixture.Mesh), Is.EqualTo(meshBefore));
                Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(controllerBefore));
                Assert.That(EditorJsonUtility.ToJson(initial), Is.EqualTo(clipBefore));
                Assert.That(EditorJsonUtility.ToJson(endpoint), Is.EqualTo(endpointBefore));
                if (worldFixMode == 1 || worldFixMode == 2)
                {
                    Assert.That(EditorJsonUtility.ToJson(worldFix), Is.EqualTo(worldFixBefore));
                    using var data = new SerializedObject(constraint);
                    Assert.That(data.FindProperty("FreezeToWorld").boolValue, Is.False);
                    Assert.That(((Behaviour)constraint).enabled, Is.False);
                }
                if (worldFixMode == 3) Assert.That(collider.size, Is.EqualTo(new Vector3(2, 3, 4)));
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

        static float[] NativeOpenWeights(GameObject source, AnimatorController controller)
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            GameObject copy = null; var graph = default(PlayableGraph);
            try
            {
                copy = Object.Instantiate(source); copy.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(copy, scene);
                foreach (var component in copy.GetComponentsInChildren<Behaviour>(true)) component.enabled = false;
                var animator = copy.GetComponent<Animator>(); animator.runtimeAnimatorController = null;
                animator.enabled = true; animator.fireEvents = false; animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                graph = PlayableGraph.Create("Unmodified world fix FX oracle"); graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                AnimationPlayableOutput.Create(graph, "Original FX", animator).SetSourcePlayable(playable);
                graph.Play(); graph.Evaluate(0);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                return copy.GetComponentsInChildren<SkinnedMeshRenderer>().Select(skin =>
                    skin.GetBlendShapeWeight(skin.sharedMesh.GetBlendShapeIndex("Open"))).ToArray();
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                if (copy != null) Object.DestroyImmediate(copy);
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
            }
        }
    }
}
