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
    // A generic permanent animation tool, not a copy of a private/vendor prefab.
    // Geometry after VRM reload is the contract: a correct sampled scalar alone
    // cannot prove that expressions never reveal the permanently hidden pupil.
    public sealed class MaPermanentAppearanceExportTests
    {
        static Type Installed(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(name, false)).FirstOrDefault(type => type != null);

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task MergedPermanentPupilHideSurvivesVrmReloadBlinkAndMenuGeometry(bool fullLilToon, bool writeDefaults)
        {
            var descriptorType = Installed("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            var mergeType = Installed("nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator");
            var menuType = Installed("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu");
            var parametersType = Installed("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters");
            if (descriptorType == null || mergeType == null || menuType == null || parametersType == null)
                Assert.Ignore("Install the real MA, NDMF and VRChat SDK packages.");
            var shader = Shader.Find("lilToon");
            if (shader == null) Assert.Ignore("Install lilToon for the real exporter entry point.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            var folderName = "__MaPermanentAppearance_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folderName); var folder = "Assets/" + folderName;
            Vrm10Instance imported = null;
            GameObject prepared = null;
            try
            {
                ConfigureGeometry(fixture.Mesh);
                var sourceSkins = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
                foreach (var skin in sourceSkins)
                {
                    skin.sharedMaterial.shader = shader;
                    for (var shape = 0; shape < fixture.Mesh.blendShapeCount; shape++) skin.SetBlendShapeWeight(shape, 0);
                }
                var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
                controller.AddParameter("Menu", AnimatorControllerParameterType.Int);
                var machine = controller.layers[0].stateMachine;
                var neutral = State(machine, "Neutral", Clip(controller, "Common neutral", 0, 0), writeDefaults);
                var selected = State(machine, "Smile", Clip(controller, "Selected face deliberately restores pupil", 100, 0), writeDefaults);
                machine.defaultState = neutral;
                var transition = neutral.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
                transition.AddCondition(AnimatorConditionMode.Equals, 1, "Menu");

                var permanent = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Permanent.controller");
                var hide = new AnimationClip { name = "Always hide pupil" };
                foreach (var path in new[] { "Front", "Back" })
                    Curve(hide, path, "Pupil hide", 100);
                AssetDatabase.AddObjectToAsset(hide, permanent);
                permanent.layers[0].stateMachine.defaultState = State(permanent.layers[0].stateMachine, "Always", hide, false);
                var tool = new GameObject("Permanent appearance tool"); tool.transform.SetParent(fixture.Source.transform, false);
                var merge = tool.AddComponent(mergeType);
                mergeType.GetField("animator").SetValue(merge, permanent);
                var pathMode = mergeType.GetField("pathMode"); pathMode.SetValue(merge, Enum.Parse(pathMode.FieldType, "Absolute"));
                mergeType.GetField("layerPriority").SetValue(merge, 100);
                mergeType.GetField("matchAvatarWriteDefaults").SetValue(merge, false);

                // This fixture exercises permanent appearance and both authored
                // WD policies, independently of MA's MMD-world sensor/relay.
                // MMD plus dynamic WD layers has a separate fixed-pose contract.
                var settingsType = Installed("nadena.dev.modular_avatar.core.ModularAvatarVRChatSettings");
                Assert.That(settingsType, Is.Not.Null);
                var settings = fixture.Source.AddComponent(settingsType);
                settingsType.GetProperty("MMDWorldSupport").SetValue(settings, false);

                var menu = ScriptableObject.CreateInstance(menuType); AssetDatabase.CreateAsset(menu, folder + "/Menu.asset");
                var parameters = ScriptableObject.CreateInstance(parametersType); AssetDatabase.CreateAsset(parameters, folder + "/Parameters.asset");
                ConfigureMenu(menu, parameters);
                var descriptor = fixture.Source.AddComponent(descriptorType);
                ConfigureDescriptor(descriptor, controller, menu, parameters);
                var sourceObjects = fixture.Source.GetComponentsInChildren<Component>(true).Where(value => value != null)
                    .Cast<Object>().Concat(new Object[] { fixture.Mesh, controller, permanent, menu, parameters, hide,
                        (AnimationClip)neutral.motion, (AnimationClip)selected.motion }).Distinct().ToArray();
                var before = sourceObjects.ToDictionary(value => value, value => EditorJsonUtility.ToJson(value));

                // MA must actually append the tool. Native playback of this
                // complete prepared controller supplies the expected geometry;
                // neither the export sampler nor a reconstructed overlay is used.
                Dictionary<string, Vector3[]> nativeNeutral = null, nativeSelected = null, nativeBlink = null;
                prepared = Object.Instantiate(fixture.Source); prepared.name = fixture.Source.name;
                using (NdmfExportPreparation.Prepare(fixture.Source, prepared, afterTransforming: lease =>
                {
                    var metadata = VrChatExpressionMenu.Read(prepared);
                    var fx = ExpressionDependencies.Controller(metadata.Controller);
                    Assert.That(fx.layers.Length, Is.GreaterThan(1), "The installed MA must merge the permanent controller.");
                    Assert.That(fx.layers.Any(layer => layer.name == "Modular Avatar: MMD Control"), Is.False,
                        "The authored MMD opt-out must keep this regression focused on permanent appearance.");
                    nativeNeutral = NativePose(prepared, fx, 0, 0);
                    nativeSelected = NativePose(prepared, fx, 1, 0);
                    nativeBlink = NativePose(prepared, fx, 0, 100);
                })) { }
                Object.DestroyImmediate(prepared); prepared = null;
                Assert.That(nativeNeutral, Is.Not.Null);
                foreach (var path in new[] { "Front", "Back" })
                {
                    Assert.That(TriangleArea(nativeNeutral[path], 0), Is.LessThan(1e-8f), "The independent native oracle must hide the pupil.");
                    Assert.That(TriangleArea(nativeNeutral[path], 3), Is.GreaterThan(1e-5f), "The surviving face must retain visible area.");
                    Assert.That(Vector3.Distance(nativeSelected[path][5], nativeNeutral[path][5]), Is.GreaterThan(.02f), "Menu movement must be exercised.");
                    Assert.That(Vector3.Distance(nativeBlink[path][5], nativeNeutral[path][5]), Is.GreaterThan(.02f), "Blink movement must be exercised.");
                }

                var options = new BlinkExportOptions { Mode = BlinkExportMode.Manual };
                foreach (var skin in sourceSkins) options.Both.Add(new BlinkShapeBinding { Renderer = skin, Shape = "Blink", Weight = 100 });
                var warnings = new List<string>();
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Merged permanent appearance", "Tests", warnings,
                    exporterVersion: fullLilToon ? "ma-permanent-appearance-regression" : null,
                    lilToonVersion: fullLilToon ? "2.3.4" : null, blinkOptions: options);
                Assert.That(VrmMenuExpressions.CountRegistered(bytes), Is.EqualTo(1), string.Join("\n", warnings));
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported, Is.Not.Null); Assert.That(imported.Vrm.Expression.Blink, Is.Not.Null);
                const string name = "VRChat / Smile";
                Assert.That(imported.Vrm.Expression.CustomClips.Count(clip => clip.name == name), Is.EqualTo(1));
                var menuExpression = imported.Vrm.Expression.CustomClips.Single(clip => clip.name == name);
                Assert.That(menuExpression.IsBinary, Is.True, "A VRChat toggle is exported as a binary expression.");
                Assert.That(imported.Vrm.Expression.Blink.IsBinary, Is.False, "Manual blink retains its continuous weight contract.");
                var menuKey = ExpressionKey.CreateCustom(name);
                var blinkKey = ExpressionKey.CreateFromPreset(ExpressionPreset.blink);
                foreach (var route in new[] { (Menu: 0f, Blink: 0f), (Menu: 0f, Blink: .5f), (Menu: 0f, Blink: 1f),
                    (Menu: 0f, Blink: 0f), (Menu: .49f, Blink: 0f), (Menu: .5f, Blink: 0f), (Menu: .51f, Blink: 0f),
                    (Menu: 1f, Blink: 0f), (Menu: 0f, Blink: 0f) })
                {
                    imported.Runtime.Expression.SetWeight(menuKey, route.Menu);
                    imported.Runtime.Expression.SetWeight(blinkKey, route.Blink);
                    imported.Runtime.Process();
                    foreach (var path in new[] { "Front", "Back" })
                    {
                        var output = imported.GetComponentsInChildren<SkinnedMeshRenderer>().Single(skin => skin.name == path);
                        var vertices = WorldVertices(output);
                        var endpoint = route.Menu > 0 ? nativeSelected[path] : nativeBlink[path];
                        // UniVRM's binary expression merger selects the endpoint
                        // above .5; precisely .5 remains neutral. The manual
                        // blink route must still interpolate continuously.
                        var coefficient = route.Menu > 0
                            ? (menuExpression.IsBinary ? (route.Menu > .5f ? 1f : 0f) : route.Menu)
                            : route.Blink;
                        Assert.That(vertices.Length, Is.EqualTo(nativeNeutral[path].Length));
                        for (var vertex = 0; vertex < vertices.Length; vertex++)
                            Assert.That(Vector3.Distance(vertices[vertex], Vector3.LerpUnclamped(nativeNeutral[path][vertex], endpoint[vertex], coefficient)),
                                Is.LessThan(.0005f), path + " / menu " + route.Menu + " / blink " + route.Blink + " / vertex " + vertex);
                        Assert.That(TriangleArea(vertices, 0), Is.LessThan(1e-8f), "Runtime expressions must never reveal the pupil.");
                        Assert.That(TriangleArea(vertices, 3), Is.GreaterThan(1e-5f), "The retained face must remain visible.");
                    }
                }
                foreach (var value in sourceObjects) Assert.That(EditorJsonUtility.ToJson(value), Is.EqualTo(before[value]), value.name);
                Assert.That(sourceSkins.All(skin => skin.sharedMesh == fixture.Mesh && Enumerable.Range(0, fixture.Mesh.blendShapeCount)
                    .All(shape => skin.GetBlendShapeWeight(shape) == 0)), Is.True);
                Assert.That(tool.GetComponent(mergeType), Is.SameAs(merge));
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                Object.DestroyImmediate(prepared); AssetDatabase.DeleteAsset(folder);
            }
        }

        static void ConfigureGeometry(Mesh mesh)
        {
            mesh.ClearBlendShapes(); mesh.triangles = Array.Empty<int>();
            var vertices = new[] { new Vector3(-.03f, 1.78f, .09f), new Vector3(.03f, 1.78f, .09f), new Vector3(0, 1.83f, .09f),
                new Vector3(-.1f, 1.7f, .08f), new Vector3(.1f, 1.7f, .08f), new Vector3(0, 1.9f, .08f) };
            mesh.vertices = vertices; mesh.triangles = new[] { 0, 1, 2, 3, 4, 5 };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.zero, Vector2.right, Vector2.up };
            mesh.normals = Enumerable.Repeat(Vector3.forward, 6).ToArray();
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 6).ToArray();
            var hide = new Vector3[6]; for (var vertex = 0; vertex < 3; vertex++) hide[vertex] = vertices[0] - vertices[vertex];
            mesh.AddBlendShapeFrame("Pupil hide", 100, hide, null, null);
            var blink = new Vector3[6]; blink[5] = Vector3.down * .04f; mesh.AddBlendShapeFrame("Blink", 100, blink, null, null);
            var smile = new Vector3[6]; smile[5] = Vector3.right * .04f; mesh.AddBlendShapeFrame("Smile", 100, smile, null, null);
            mesh.RecalculateBounds();
        }

        static void Curve(AnimationClip clip, string path, string shape, float value) => AnimationUtility.SetEditorCurve(clip,
            EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + shape), AnimationCurve.Constant(0, 1, value));

        static AnimationClip Clip(AnimatorController controller, string name, float smile, float pupil)
        {
            var clip = new AnimationClip { name = name };
            foreach (var path in new[] { "Front", "Back" })
            { Curve(clip, path, "Pupil hide", pupil); Curve(clip, path, "Blink", 0); Curve(clip, path, "Smile", smile); }
            AssetDatabase.AddObjectToAsset(clip, controller); return clip;
        }

        static AnimatorState State(AnimatorStateMachine machine, string name, AnimationClip clip, bool writeDefaults)
        { var state = machine.AddState(name); state.motion = clip; state.writeDefaultValues = writeDefaults; return state; }

        static void ConfigureMenu(ScriptableObject menu, ScriptableObject parameters)
        {
            using (var data = new SerializedObject(menu))
            {
                var list = data.FindProperty("controls"); list.arraySize = 1; var item = list.GetArrayElementAtIndex(0);
                item.FindPropertyRelative("name").stringValue = "Smile";
                var type = item.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "Toggle");
                item.FindPropertyRelative("parameter").FindPropertyRelative("name").stringValue = "Menu";
                item.FindPropertyRelative("value").floatValue = 1; data.ApplyModifiedPropertiesWithoutUndo();
            }
            using (var data = new SerializedObject(parameters))
            {
                var list = data.FindProperty("parameters"); list.arraySize = 1; var item = list.GetArrayElementAtIndex(0);
                item.FindPropertyRelative("name").stringValue = "Menu";
                var type = item.FindPropertyRelative("valueType"); type.enumValueIndex = Array.IndexOf(type.enumNames, "Int");
                item.FindPropertyRelative("defaultValue").floatValue = 0; data.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        static void ConfigureDescriptor(Component descriptor, AnimatorController controller, ScriptableObject menu, ScriptableObject parameters)
        {
            using var data = new SerializedObject(descriptor);
            data.FindProperty("customExpressions").boolValue = true;
            data.FindProperty("expressionsMenu").objectReferenceValue = menu;
            data.FindProperty("expressionParameters").objectReferenceValue = parameters;
            data.FindProperty("customizeAnimationLayers").boolValue = true;
            var list = data.FindProperty("baseAnimationLayers"); list.arraySize = 1; var layer = list.GetArrayElementAtIndex(0);
            var type = layer.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
            layer.FindPropertyRelative("isDefault").boolValue = false;
            layer.FindPropertyRelative("animatorController").objectReferenceValue = controller;
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        static Dictionary<string, Vector3[]> NativePose(GameObject source, AnimatorController controller, int selected, float blink)
        {
            var copy = Object.Instantiate(source); var graph = PlayableGraph.Create("MA permanent appearance independent native oracle");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            try
            {
                foreach (var behaviour in copy.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                var animator = copy.GetComponent<Animator>(); animator.enabled = true; animator.runtimeAnimatorController = null;
                animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                for (var layer = 0; layer < controller.layers.Length; layer++) playable.SetLayerWeight(layer, layer == 0 ? 1 : controller.layers[layer].defaultWeight);
                AnimationPlayableOutput.Create(graph, "Native appearance", animator).SetSourcePlayable(playable);
                playable.SetInteger("Menu", 0); graph.Play(); graph.Evaluate(0);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                playable.SetInteger("Menu", selected);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                return new[] { "Front", "Back" }.ToDictionary(path => path, path =>
                {
                    var skin = copy.transform.Find(path).GetComponent<SkinnedMeshRenderer>();
                    Assert.That(skin.GetBlendShapeWeight(skin.sharedMesh.GetBlendShapeIndex("Pupil hide")), Is.EqualTo(100).Within(.001f));
                    skin.SetBlendShapeWeight(skin.sharedMesh.GetBlendShapeIndex("Blink"), blink);
                    return WorldVertices(skin);
                });
            }
            finally { if (graph.IsValid()) graph.Destroy(); Object.DestroyImmediate(copy); }
        }

        static Vector3[] WorldVertices(SkinnedMeshRenderer skin)
        {
            var baked = new Mesh();
            try { skin.BakeMesh(baked, false); return baked.vertices.Select(skin.transform.TransformPoint).ToArray(); }
            finally { Object.DestroyImmediate(baked); }
        }

        static float TriangleArea(Vector3[] vertices, int start) =>
            Vector3.Cross(vertices[start + 1] - vertices[start], vertices[start + 2] - vertices[start]).magnitude * .5f;
    }
}
