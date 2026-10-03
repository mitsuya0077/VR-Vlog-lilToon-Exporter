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
    // Uses the installed AAO/NDMF implementation and public OneClick path.
    // Assertions compare absolute source poses, not generated target names.
    public sealed class InstalledAaoNeutralEndpointExportTests
    {
        const string Opening = "UE/JawOpen";
        const string Closure = "UE/EyeClosed";

        static Type InstalledType(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(name)).FirstOrDefault(type => type != null);

        static Component Optimizer(GameObject target, string name, int version)
        {
            var type = InstalledType("Anatawa12.AvatarOptimizer." + name);
            var initialize = type.GetMethod("Initialize", new[] { typeof(int) });
            if (initialize == null) Assert.Ignore("Install AAO with the supported component configuration API.");
            var component = target.AddComponent(type); initialize.Invoke(component, new object[] { version }); return component;
        }

        static void Mask(SkinnedMeshRenderer skin, Texture2D texture)
        {
            var component = Optimizer(skin.gameObject, "RemoveMeshByMask", 1);
            var slotType = component.GetType().GetNestedType("MaterialSlot"); var slot = Activator.CreateInstance(slotType);
            slotType.GetProperty("Enabled").SetValue(slot, true); slotType.GetProperty("Mask").SetValue(slot, texture);
            slotType.GetProperty("Mode").SetValue(slot, Enum.Parse(component.GetType().GetNestedType("RemoveMode"), "RemoveBlack"));
            var slots = Array.CreateInstance(slotType, 1); slots.SetValue(slot, 0);
            component.GetType().GetProperty("Materials").SetValue(component, slots);
        }

        static void Fx(GameObject avatar, Type descriptorType, AnimatorController controller, SkinnedMeshRenderer[] skins)
        {
            var clip = new AnimationClip { name = "Native opening75 and open eyes" }; AssetDatabase.AddObjectToAsset(clip, controller);
            foreach (var skin in skins)
            {
                var path = AnimationUtility.CalculateTransformPath(skin.transform, avatar.transform);
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + Opening), AnimationCurve.Constant(0, 1, 75));
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + Closure), AnimationCurve.Constant(0, 1, 0));
            }
            var machine = controller.layers[0].stateMachine; var state = machine.AddState("Neutral");
            state.motion = clip; state.writeDefaultValues = false; machine.defaultState = state;
            using var data = new SerializedObject(avatar.AddComponent(descriptorType));
            data.FindProperty("customizeAnimationLayers").boolValue = true;
            var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = 1;
            var layer = layers.GetArrayElementAtIndex(0); var kind = layer.FindPropertyRelative("type");
            kind.enumValueIndex = Array.IndexOf(kind.enumNames, "FX");
            layer.FindPropertyRelative("isDefault").boolValue = false;
            layer.FindPropertyRelative("animatorController").objectReferenceValue = controller;
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        static Vector3[] SourceTriangle(SkinnedMeshRenderer skin, float opening, float closure)
        {
            var openingIndex = skin.sharedMesh.GetBlendShapeIndex(Opening); var closureIndex = skin.sharedMesh.GetBlendShapeIndex(Closure);
            var oldOpening = skin.GetBlendShapeWeight(openingIndex); var oldClosure = skin.GetBlendShapeWeight(closureIndex);
            var baked = new Mesh();
            try
            {
                skin.SetBlendShapeWeight(openingIndex, opening); skin.SetBlendShapeWeight(closureIndex, closure); skin.BakeMesh(baked);
                return baked.triangles.Skip(3).Select(index => skin.transform.TransformPoint(baked.vertices[index])).ToArray();
            }
            finally { skin.SetBlendShapeWeight(openingIndex, oldOpening); skin.SetBlendShapeWeight(closureIndex, oldClosure); Object.DestroyImmediate(baked); }
        }

        static Vector3[] OutputTriangles(Vrm10Instance imported)
        {
            var result = new List<Vector3>(); var baked = new Mesh();
            try
            {
                foreach (var skin in imported.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    skin.BakeMesh(baked); var vertices = baked.vertices;
                    result.AddRange(baked.triangles.Select(index => skin.transform.TransformPoint(vertices[index])));
                }
                return result.ToArray();
            }
            finally { Object.DestroyImmediate(baked); }
        }

        static void Geometry(Vector3[] actual, Vector3[] expected, string message)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length), message + ": AAO must delete exactly one triangle per source renderer.");
            var unmatched = expected.ToList();
            foreach (var point in actual)
            {
                var index = unmatched.FindIndex(value => Vector3.Distance(value, point) < .0001f);
                Assert.That(index, Is.GreaterThanOrEqualTo(0), message + ": unexpected world-space vertex " + point);
                unmatched.RemoveAt(index);
            }
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        public async Task MaskMergeAndTraceKeepFxNeutralAuthoredEndpointsTrackingAndDeferredBlink(bool explicitProfile, bool fullLilToon)
        {
            var descriptorType = InstalledType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            var mergeType = InstalledType("Anatawa12.AvatarOptimizer.MergeSkinnedMesh");
            if (descriptorType == null || mergeType == null || InstalledType("Anatawa12.AvatarOptimizer.RemoveMeshByMask") == null ||
                InstalledType("Anatawa12.AvatarOptimizer.TraceAndOptimize") == null || InstalledType("nadena.dev.ndmf.BuildContext") == null)
                Assert.Ignore("Install the real VRChat SDK, AAO and NDMF for this native integration test.");
            if (mergeType.GetProperty("MergeBlendShapes") == null) Assert.Ignore("This rename fixture requires the AAO 1.8+ configuration API.");
            Assert.That(ExportOptimizationMarker.AvatarOptimizerAdapterAvailable, Is.True);
            using var fixture = new AttachmentConnectionTests.Fixture();
            var folderName = "__AaoNeutralEndpoint_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folderName); var folder = "Assets/" + folderName;
            var settings = ScriptableObject.CreateInstance<VRM10Object>(); var happy = ScriptableObject.CreateInstance<VRM10Expression>();
            var profile = ScriptableObject.CreateInstance<VrmTrackingProfile>();
            var mask = new Texture2D(2, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point };
            Vrm10Instance imported = null;
            try
            {
                var retained = fixture.Mesh.vertices; var vertices = retained.Select(value => value + Vector3.left * .4f).Concat(retained).ToArray();
                fixture.Mesh.ClearBlendShapes(); fixture.Mesh.vertices = vertices; fixture.Mesh.triangles = new[] { 0, 1, 2, 3, 4, 5 };
                fixture.Mesh.normals = Enumerable.Repeat(Vector3.forward, 6).ToArray();
                fixture.Mesh.uv = Enumerable.Repeat(new Vector2(.25f, .5f), 3).Concat(Enumerable.Repeat(new Vector2(.75f, .5f), 3)).ToArray();
                fixture.Mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 6).ToArray();
                fixture.Mesh.AddBlendShapeFrame(Opening, 50, Enumerable.Repeat(Vector3.up * .01f, 6).ToArray(), null, null);
                fixture.Mesh.AddBlendShapeFrame(Opening, 100, Enumerable.Repeat(Vector3.up * .03f, 6).ToArray(), null, null);
                fixture.Mesh.AddBlendShapeFrame(Closure, 100, Enumerable.Repeat(Vector3.down * .03f, 6).ToArray(), null, null);
                var skins = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
                foreach (var skin in skins)
                { skin.SetBlendShapeWeight(0, 0); skin.SetBlendShapeWeight(1, 100); skin.sharedMaterial.shader = Shader.Find("lilToon"); }
                mask.SetPixels(new[] { Color.black, Color.white }); mask.Apply(); foreach (var skin in skins) Mask(skin, mask);
                var node = new GameObject("Merged face"); node.transform.SetParent(fixture.Source.transform, false); node.AddComponent<SkinnedMeshRenderer>();
                var merge = Optimizer(node, "MergeSkinnedMesh", 2); mergeType.GetProperty("MergeBlendShapes").SetValue(merge, false);
                mergeType.GetProperty("RemoveEmptyRendererObject").SetValue(merge, true);
                var sources = mergeType.GetProperty("SourceSkinnedMeshRenderers").GetValue(merge);
                var add = sources.GetType().GetMethod("Add", new[] { typeof(SkinnedMeshRenderer) });
                foreach (var skin in skins) add.Invoke(sources, new object[] { skin });
                fixture.Source.AddComponent(InstalledType("Anatawa12.AvatarOptimizer.TraceAndOptimize"));
                var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller"); Fx(fixture.Source, descriptorType, controller, skins);
                happy.name = "Authored absolute25";
                happy.MorphTargetBindings = skins.Select(skin => new MorphTargetBinding(AnimationUtility.CalculateTransformPath(skin.transform, fixture.Source.transform), 0, .25f)).ToArray();
                settings.Expression.Happy = happy; fixture.Source.AddComponent<Vrm10Instance>().Vrm = settings;
                if (explicitProfile)
                {
                    profile.expressions = VrmTrackingExpressions.Names.Select(name => new TrackingExpression { name = name,
                        morphs = new[] { new TrackingMorph { shape = Opening, weight = .25f } } }).ToArray();
                    fixture.Source.AddComponent<VrmTrackingMarker>().profile = profile;
                }
                var neutral = skins.SelectMany(skin => SourceTriangle(skin, 75, 0)).ToArray();
                var endpoint = skins.SelectMany(skin => SourceTriangle(skin, 25, 0)).ToArray();
                var tracking = skins.SelectMany(skin => SourceTriangle(skin, explicitProfile ? 25 : 100, 0)).ToArray();
                var blink = skins.SelectMany(skin => SourceTriangle(skin, 75, 100)).ToArray();
                var sourceVertices = fixture.Mesh.vertices; var sourceController = EditorJsonUtility.ToJson(controller);
                Assert.Throws<InvalidOperationException>(() => BlinkExportSession.Resolve(fixture.Source),
                    "The serialized fully closed eyes must need the deferred native FX-open neutral before blink can be resolved.");
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "AAO neutral endpoints", "Tests",
                    exporterVersion: fullLilToon ? "aao-neutral-endpoint-regression" : null, lilToonVersion: fullLilToon ? "2.3.4" : null);
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller()); imported.Runtime.Process();
                var outputs = imported.GetComponentsInChildren<SkinnedMeshRenderer>();
                Assert.That(outputs.Length, Is.EqualTo(1), "AAO must actually merge both source renderers.");
                Assert.That(imported.Vrm.Expression.Happy, Is.Not.Null); Assert.That(imported.Vrm.Expression.Blink, Is.Not.Null);
                Geometry(OutputTriangles(imported), neutral, "Native FX neutral75 after mask/merge");
                imported.Runtime.Expression.SetWeight(ExpressionKey.Happy, 1); imported.Runtime.Process();
                Geometry(OutputTriangles(imported), endpoint, "Authored0.25 must reach source25 after AAO property remapping");
                imported.Runtime.Expression.SetWeight(ExpressionKey.Happy, 0);
                var trackingKey = ExpressionKey.CreateCustom(explicitProfile ? "JawOpen" : "UE/JawOpen");
                Assert.That(imported.Vrm.Expression.CustomClips.Any(clip => clip.name == trackingKey.Name), Is.True);
                imported.Runtime.Expression.SetWeight(trackingKey, 1); imported.Runtime.Process();
                Geometry(OutputTriangles(imported), tracking, "Explicit or inferred absolute tracking endpoint after AAO remapping");
                imported.Runtime.Expression.SetWeight(trackingKey, 0); imported.Runtime.Expression.SetWeight(ExpressionKey.Blink, 1); imported.Runtime.Process();
                Geometry(OutputTriangles(imported), blink, "Deferred blink closes from FX-open neutral without reopening deleted triangles");
                Assert.That(skins.All(skin => skin != null && skin.sharedMesh == fixture.Mesh && skin.GetBlendShapeWeight(0) == 0 && skin.GetBlendShapeWeight(1) == 100), Is.True);
                Assert.That(fixture.Mesh.vertices, Is.EqualTo(sourceVertices)); Assert.That(fixture.Mesh.triangles.Length, Is.EqualTo(6));
                Assert.That(happy.MorphTargetBindings.All(binding => binding.Index == 0 && binding.Weight == .25f), Is.True);
                Assert.That(fixture.Source.GetComponent<Vrm10Instance>().Vrm, Is.SameAs(settings));
                Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(sourceController)); Assert.That(merge != null, Is.True);
                if (explicitProfile) Assert.That(profile.expressions.All(expression => expression.morphs.Single().shape == Opening && expression.morphs.Single().weight == .25f), Is.True);
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                Object.DestroyImmediate(happy); Object.DestroyImmediate(settings); Object.DestroyImmediate(profile); Object.DestroyImmediate(mask);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
