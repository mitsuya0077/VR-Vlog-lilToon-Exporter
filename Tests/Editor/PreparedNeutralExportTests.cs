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
    public sealed class PreparedNeutralExportTests
    {
        static Type DescriptorType() => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor"))
            .FirstOrDefault(type => type != null);

        static void SetFx(Component descriptor, RuntimeAnimatorController controller)
        {
            using var data = new SerializedObject(descriptor);
            data.FindProperty("customizeAnimationLayers").boolValue = true;
            var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = 1;
            var layer = layers.GetArrayElementAtIndex(0);
            var type = layer.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
            layer.FindPropertyRelative("isDefault").boolValue = false;
            layer.FindPropertyRelative("animatorController").objectReferenceValue = controller;
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        static AnimatorController DefaultFx(string asset, string path, string shape, float weight)
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(asset);
            var clip = new AnimationClip { name = "Prepared neutral" };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + shape),
                AnimationCurve.Constant(0, 1, weight));
            AssetDatabase.AddObjectToAsset(clip, controller);
            var machine = controller.layers[0].stateMachine;
            var state = machine.AddState("Neutral"); state.writeDefaultValues = false; state.motion = clip;
            machine.defaultState = state;
            return controller;
        }

        [Test]
        public async Task IndependentLoopOnTheInfluencingSkinBoneKeepsPreparedGeometryAndAuthoredEndpoints()
        {
            var type = DescriptorType(); if (type == null) Assert.Ignore("Install the real VRChat SDK.");
            var shader = Shader.Find("lilToon"); if (shader == null) Assert.Ignore("Install lilToon for the real exporter entry point.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            var folderName = "__IndependentSkinBoneNeutral_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folderName); var folder = "Assets/" + folderName;
            Vrm10Instance imported = null;
            GameObject native = null, expected = null;
            var graph = PlayableGraph.Create("Independent skin bone native reference");
            try
            {
                var sourceSkins = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
                foreach (var skin in sourceSkins) skin.sharedMaterial.shader = shader;
                var sourceBone = sourceSkins[0].bones[0];
                Assert.That(sourceBone, Is.SameAs(fixture.Source.transform.Find("Independent hair/Head")));
                Assert.That(fixture.Mesh.boneWeights.All(weight => weight.boneIndex0 == 0 && weight.weight0 == 1), Is.True);
                sourceBone.localRotation = Quaternion.Euler(0, 0, 25);
                var sourcePosition = sourceBone.localPosition; var sourceRotation = sourceBone.localRotation; var sourceScale = sourceBone.localScale;
                var controller = DefaultFx(folder + "/FX.controller", "Front", "Hair detail", 50);
                var neutral = (AnimationClip)controller.layers[0].stateMachine.defaultState.motion;
                AnimationUtility.SetEditorCurve(neutral, EditorCurveBinding.FloatCurve("Back", typeof(SkinnedMeshRenderer), "blendShape.Hair detail"),
                    AnimationCurve.Constant(0, 1, 50));
                var loop = new AnimationClip { name = "Independent influencing bone loop" };
                AnimationUtility.SetEditorCurve(loop, EditorCurveBinding.FloatCurve("Independent hair/Head", typeof(Transform), "localEulerAnglesRaw.z"),
                    new AnimationCurve(new Keyframe(0, 0), new Keyframe(.5f, 60), new Keyframe(1, 0)));
                var clipSettings = AnimationUtility.GetAnimationClipSettings(loop); clipSettings.loopTime = true;
                AnimationUtility.SetAnimationClipSettings(loop, clipSettings); AssetDatabase.AddObjectToAsset(loop, controller);
                controller.AddLayer("Independent native additive motion");
                var layers = controller.layers; var motionLayer = layers[layers.Length - 1];
                motionLayer.defaultWeight = .5f; motionLayer.blendingMode = AnimatorLayerBlendingMode.Additive;
                var moving = motionLayer.stateMachine.AddState("Loop"); moving.writeDefaultValues = false; moving.motion = loop;
                motionLayer.stateMachine.defaultState = moving; controller.layers = layers;
                SetFx(fixture.Source.AddComponent(type), controller);
                var vrm = ScriptableObject.CreateInstance<VRM10Object>();
                var endpoint = ScriptableObject.CreateInstance<VRM10Expression>(); endpoint.name = "Authored source endpoint";
                endpoint.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 0, .8f), new MorphTargetBinding("Back", 0, .8f) };
                vrm.Expression.CustomClips.Add(endpoint);
                AssetDatabase.CreateAsset(vrm, folder + "/Vrm.asset"); AssetDatabase.CreateAsset(endpoint, folder + "/Endpoint.asset");
                fixture.Source.AddComponent<Vrm10Instance>().Vrm = vrm;
                var meshBefore = EditorJsonUtility.ToJson(fixture.Mesh); var controllerBefore = EditorJsonUtility.ToJson(controller);
                var vrmBefore = EditorJsonUtility.ToJson(vrm); var endpointBefore = EditorJsonUtility.ToJson(endpoint);

                // Play the authored controller without the export sampler's
                // reconstructed graph. A moving, fully influencing bone must
                // change pose while the independent native morph scalar holds.
                native = Object.Instantiate(fixture.Source);
                foreach (var behaviour in native.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                var animator = native.GetComponent<Animator>(); animator.enabled = true;
                animator.runtimeAnimatorController = null; animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                for (var layer = 0; layer < controller.layers.Length; layer++)
                    playable.SetLayerWeight(layer, layer == 0 ? 1 : controller.layers[layer].defaultWeight);
                AnimationPlayableOutput.Create(graph, "Native scalar reference", animator).SetSourcePlayable(playable);
                graph.Play(); graph.Evaluate(0); graph.Evaluate(.2f);
                var nativeSkin = native.transform.Find("Front").GetComponent<SkinnedMeshRenderer>();
                var nativeWeight = nativeSkin.GetBlendShapeWeight(0);
                var nativeRotation = nativeSkin.bones[0].localRotation;
                graph.Evaluate(.5f);
                Assert.That(nativeSkin.GetBlendShapeWeight(0), Is.EqualTo(nativeWeight).Within(.001f));
                Assert.That(Quaternion.Angle(nativeRotation, nativeSkin.bones[0].localRotation), Is.GreaterThan(.1f));
                Assert.That(nativeWeight, Is.GreaterThan(40).And.LessThan(100), "Native rest must differ from the serialized35 and remain within the source range.");
                var sampled = NeutralShapeSampler.Sample(fixture.Source);
                foreach (var path in new[] { "Front", "Back" })
                    Assert.That(sampled.Single(value => value.Path == path && value.Shape == "Hair detail").Weight,
                        Is.EqualTo(nativeWeight).Within(.001f));

                expected = Object.Instantiate(fixture.Source);
                foreach (var behaviour in expected.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                Vector3[] Vertices(SkinnedMeshRenderer skin)
                {
                    var baked = new Mesh();
                    try { skin.BakeMesh(baked, false); return baked.vertices.Select(skin.transform.TransformPoint).ToArray(); }
                    finally { Object.DestroyImmediate(baked); }
                }
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Independent influencing bone neutral", "Tests",
                    blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported, Is.Not.Null);
                foreach (var input in new[] { 0f, 1f, 0f })
                {
                    imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(endpoint.name), input); imported.Runtime.Process();
                    foreach (var path in new[] { "Front", "Back" })
                    {
                        var referenceSkin = expected.transform.Find(path).GetComponent<SkinnedMeshRenderer>();
                        referenceSkin.SetBlendShapeWeight(0, input == 0 ? 35 : 80);
                        var referenceVertices = Vertices(referenceSkin);
                        var output = imported.GetComponentsInChildren<SkinnedMeshRenderer>().Single(skin => skin.name == path);
                        var outputVertices = Vertices(output);
                        Assert.That(outputVertices.Length, Is.EqualTo(referenceVertices.Length));
                        for (var vertex = 0; vertex < referenceVertices.Length; vertex++)
                            Assert.That(Vector3.Distance(outputVertices[vertex], referenceVertices[vertex]), Is.LessThan(.0005f),
                                path + " / expression " + input + " / vertex " + vertex + ": native scalar with prepared bone pose");
                    }
                }
                Assert.That(sourceBone.localPosition, Is.EqualTo(sourcePosition)); Assert.That(sourceBone.localRotation, Is.EqualTo(sourceRotation));
                Assert.That(sourceBone.localScale, Is.EqualTo(sourceScale));
                Assert.That(sourceSkins.All(skin => skin.sharedMesh == fixture.Mesh && skin.GetBlendShapeWeight(0) == 35), Is.True);
                Assert.That(EditorJsonUtility.ToJson(fixture.Mesh), Is.EqualTo(meshBefore));
                Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(controllerBefore));
                Assert.That(EditorJsonUtility.ToJson(vrm), Is.EqualTo(vrmBefore)); Assert.That(EditorJsonUtility.ToJson(endpoint), Is.EqualTo(endpointBefore));
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                Object.DestroyImmediate(native); Object.DestroyImmediate(expected); AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public async Task OneClickManualBlinkPreservesCoupledPreparedAppearanceAndAbsoluteClosure()
        {
            var type = DescriptorType(); if (type == null) Assert.Ignore("Install the real VRChat SDK.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            var folderName = "__ManualBlinkAppearance_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folderName); var folder = "Assets/" + folderName;
            Vrm10Instance imported = null; GameObject expected = null;
            try
            {
                var cap = AppearanceCoupledFx(fixture, type, folder, out var controller);
                var front = fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>();
                var options = new BlinkExportOptions { Mode = BlinkExportMode.Manual };
                options.Both.Add(new BlinkShapeBinding { Renderer = front, Shape = "Cap mask", Weight = 100 });
                var beforeController = EditorJsonUtility.ToJson(controller);
                var beforeMesh = EditorJsonUtility.ToJson(fixture.Mesh); var warnings = new List<string>();
                expected = Object.Instantiate(fixture.Source);
                foreach (var behaviour in expected.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                var bytes = UniVrmOneClickExporter.Export(fixture.Source,
                    "Manual blink with retained wardrobe neutral", "Tests", warnings, blinkOptions: options);
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported.Vrm.Expression.Blink, Is.Not.Null);
                Assert.That(imported.GetComponentsInChildren<Renderer>(true).Any(renderer => renderer.name == "Cap"), Is.False,
                    "The prepared hidden wardrobe must not become visible with its associated FX morph.");
                Assert.That(warnings.Any(value => value.Contains("このベータ版ではFXの初期状態の復元を省略")), Is.True);
                foreach (var input in new[] { 0f, 1f, 0f })
                {
                    imported.Runtime.Expression.SetWeight(ExpressionKey.Blink, input); imported.Runtime.Process();
                    foreach (var path in new[] { "Front", "Back" })
                    {
                        var reference = expected.transform.Find(path).GetComponent<SkinnedMeshRenderer>();
                        reference.SetBlendShapeWeight(0, path == "Front" && input == 1 ? 100 : 25);
                        var output = imported.GetComponentsInChildren<SkinnedMeshRenderer>().Single(skin => skin.name == path);
                        AssertWorldVertices(reference, output, path + " / blink " + input);
                    }
                }
                Assert.That(cap.activeSelf, Is.False);
                Assert.That(front.GetBlendShapeWeight(0), Is.EqualTo(25));
                Assert.That(front.sharedMesh, Is.SameAs(fixture.Mesh));
                Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(beforeController));
                Assert.That(EditorJsonUtility.ToJson(fixture.Mesh), Is.EqualTo(beforeMesh));
                Assert.That(fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>().All(skin => skin.sharedMesh == fixture.Mesh && skin.GetBlendShapeWeight(0) == 25), Is.True);
                Assert.That(options.Both.Single().Renderer, Is.SameAs(front));
                Assert.That(options.Both.Single().Shape, Is.EqualTo("Cap mask")); Assert.That(options.Both.Single().Weight, Is.EqualTo(100));
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                if (expected != null) Object.DestroyImmediate(expected);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public async Task OneClickKeepsSourceTrackingEndpointsAndPreparedRestAfterMarkerRemoval()
        {
            var type = DescriptorType(); if (type == null) Assert.Ignore("Install the real VRChat SDK.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            var folderName = "__StrippedTrackingAppearance_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folderName); var folder = "Assets/" + folderName;
            var profile = ScriptableObject.CreateInstance<VrmTrackingProfile>();
            Vrm10Instance imported = null; GameObject expected = null;
            try
            {
                var cap = AppearanceCoupledFx(fixture, type, folder, out var controller);
                profile.expressions = VrmTrackingExpressions.Names.Select(name => new TrackingExpression
                    { name = name, morphs = new[] { new TrackingMorph { shape = "Cap mask", weight = 1 } } }).ToArray();
                var markerObject = new GameObject("Authoring tracking marker"); markerObject.transform.SetParent(fixture.Source.transform, false);
                markerObject.AddComponent<VrmTrackingMarker>().profile = profile;
                // Exercise the public pipeline's copy-only authoring removal.
                // This occurs before NDMF and guarantees the neutral planner
                // cannot discover the source obligation from a copied marker.
                var probe = Object.Instantiate(fixture.Source);
                try
                {
                    using var exclusions = new ExportObjectExclusions(fixture.Source, new[] { markerObject });
                    exclusions.Apply(probe, null);
                    Assert.That(probe.GetComponentInChildren<VrmTrackingMarker>(true), Is.Null);
                }
                finally { Object.DestroyImmediate(probe); }
                var beforeController = EditorJsonUtility.ToJson(controller);
                var beforeMesh = EditorJsonUtility.ToJson(fixture.Mesh); var beforeProfile = EditorJsonUtility.ToJson(profile);
                var front = fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>();
                expected = Object.Instantiate(fixture.Source);
                foreach (var behaviour in expected.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                var warnings = new List<string>();
                var bytes = UniVrmOneClickExporter.Export(fixture.Source,
                    "Stripped marker with retained wardrobe neutral", "Tests", warnings, excludedObjects: new[] { markerObject },
                    blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(VrmTrackingExpressions.Names.All(name => imported.Vrm.Expression.CustomClips.Any(clip => clip.name == name)), Is.True,
                    "Every explicit source tracking endpoint must survive removal of its authoring marker from the prepared copy.");
                Assert.That(imported.GetComponentsInChildren<Renderer>(true).Any(renderer => renderer.name == "Cap"), Is.False);
                Assert.That(warnings.Any(value => value.Contains("このベータ版ではFXの初期状態の復元を省略")), Is.True);
                foreach (var input in new[] { 0f, 1f, 0f })
                {
                    imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom("JawOpen"), input); imported.Runtime.Process();
                    foreach (var path in new[] { "Front", "Back" })
                    {
                        var reference = expected.transform.Find(path).GetComponent<SkinnedMeshRenderer>(); reference.SetBlendShapeWeight(0, input == 1 ? 100 : 25);
                        var output = imported.GetComponentsInChildren<SkinnedMeshRenderer>().Single(skin => skin.name == path);
                        AssertWorldVertices(reference, output, path + " / tracking " + input);
                    }
                }
                Assert.That(markerObject.GetComponent<VrmTrackingMarker>().profile, Is.SameAs(profile));
                Assert.That(markerObject.transform.parent, Is.SameAs(fixture.Source.transform));
                Assert.That(cap.activeSelf, Is.False);
                Assert.That(front.GetBlendShapeWeight(0), Is.EqualTo(25));
                Assert.That(front.sharedMesh, Is.SameAs(fixture.Mesh));
                Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(beforeController));
                Assert.That(EditorJsonUtility.ToJson(fixture.Mesh), Is.EqualTo(beforeMesh));
                Assert.That(EditorJsonUtility.ToJson(profile), Is.EqualTo(beforeProfile));
                Assert.That(fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>().All(skin => skin.sharedMesh == fixture.Mesh && skin.GetBlendShapeWeight(0) == 25), Is.True);
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                if (expected != null) Object.DestroyImmediate(expected);
                Object.DestroyImmediate(profile); AssetDatabase.DeleteAsset(folder);
            }
        }

        static void AssertWorldVertices(SkinnedMeshRenderer expected, SkinnedMeshRenderer actual, string context)
        {
            var reference = new Mesh(); var output = new Mesh();
            try
            {
                expected.BakeMesh(reference, false); actual.BakeMesh(output, false);
                Assert.That(output.vertexCount, Is.EqualTo(reference.vertexCount), context);
                for (var index = 0; index < reference.vertexCount; index++)
                    Assert.That(Vector3.Distance(expected.transform.TransformPoint(reference.vertices[index]), actual.transform.TransformPoint(output.vertices[index])),
                        Is.LessThan(.0005f), context + " / vertex " + index);
            }
            finally { Object.DestroyImmediate(reference); Object.DestroyImmediate(output); }
        }

        static GameObject AppearanceCoupledFx(AttachmentConnectionTests.Fixture fixture, Type descriptorType, string folder,
            out AnimatorController controller)
        {
            fixture.Mesh.ClearBlendShapes();
            fixture.Mesh.AddBlendShapeFrame("Cap mask", 100,
                Enumerable.Repeat(Vector3.up * .03f, fixture.Mesh.vertexCount).ToArray(), null, null);
            foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>())
            { skin.SetBlendShapeWeight(0, 25); skin.sharedMaterial.shader = Shader.Find("lilToon"); }
            var cap = new GameObject("Cap"); cap.transform.SetParent(fixture.Source.transform, false); cap.SetActive(false);
            cap.AddComponent<MeshFilter>().sharedMesh = fixture.Mesh;
            cap.AddComponent<MeshRenderer>().sharedMaterial = fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>().sharedMaterial;
            controller = DefaultFx(folder + "/FX.controller", "Front", "Cap mask", 100);
            var clip = (AnimationClip)controller.layers[0].stateMachine.defaultState.motion;
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Back", typeof(SkinnedMeshRenderer), "blendShape.Cap mask"),
                AnimationCurve.Constant(0, 1, 100));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Cap", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1));
            SetFx(fixture.Source.AddComponent(descriptorType), controller);
            return cap;
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ClosedPreparedEyesRequireAdjustmentWhenInitialFxIsSkipped(bool fullLilToon)
        {
            var type = DescriptorType(); if (type == null) Assert.Ignore("Install the real VRChat SDK.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            var folderName = "__PreparedNeutralExport_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folderName); var folder = "Assets/" + folderName;
            Vrm10Instance imported = null;
            try
            {
                fixture.Mesh.ClearBlendShapes();
                fixture.Mesh.AddBlendShapeFrame("UE/EyeClosed", 100,
                    Enumerable.Repeat(Vector3.down * .03f, fixture.Mesh.vertexCount).ToArray(), null, null);
                var sourceSkins = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
                foreach (var skin in sourceSkins) { skin.SetBlendShapeWeight(0, 100); skin.sharedMaterial.shader = Shader.Find("lilToon"); }
                var controller = DefaultFx(folder + "/FX.controller", "Front", "UE/EyeClosed", 0);
                var clip = (AnimationClip)controller.layers[0].stateMachine.defaultState.motion;
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Back", typeof(SkinnedMeshRenderer), "blendShape.UE/EyeClosed"),
                    AnimationCurve.Constant(0, 1, 0));
                SetFx(fixture.Source.AddComponent(type), controller);
                Assert.Throws<InvalidOperationException>(() => BlinkExportSession.Resolve(fixture.Source),
                    "The retained closed face has no remaining closure range; skipping FX must not invent an open-eye baseline.");
                Assert.Throws<InvalidOperationException>(() => UniVrmOneClickExporter.Export(fixture.Source,
                    "Closed prepared face", "Tests"));
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Prepared closed rest without blink", "Tests",
                    blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None },
                    exporterVersion: fullLilToon ? "prepared-neutral-regression" : null,
                    lilToonVersion: fullLilToon ? "2.3.4" : null);
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                imported.Runtime.Process();
                Assert.That(imported.Vrm.Expression.Blink, Is.Null);
                foreach (var skin in imported.GetComponentsInChildren<SkinnedMeshRenderer>())
                    Assert.That(skin.sharedMesh.vertices[0].y, Is.EqualTo(fixture.Mesh.vertices[0].y - .03f).Within(.00001f));
                Assert.That(sourceSkins.All(skin => skin.sharedMesh == fixture.Mesh && skin.GetBlendShapeWeight(0) == 100), Is.True);
                Assert.That(clip, Is.SameAs(controller.layers[0].stateMachine.defaultState.motion));
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); AssetDatabase.DeleteAsset(folder); }
        }

        [TestCase(false, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, false, false)]
        [TestCase(true, true, false)]
        [TestCase(false, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, false, true)]
        [TestCase(true, true, true)]
        public async Task NdmfGeneratedShapeAndReboundFxUsePreparedMeshAndRendererPathInRealVrm(bool moved, bool fullLilToon, bool replacedRenderer)
        {
            var type = DescriptorType(); if (type == null) Assert.Ignore("Install the real VRChat SDK.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            var folderName = "__NdmfPreparedNeutral_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folderName); var folder = "Assets/" + folderName;
            Vrm10Instance imported = null;
            NdmfExportPreparation preparation = null;
            var baked = new Mesh();
            try
            {
                fixture.Mesh.ClearBlendShapes();
                fixture.Mesh.AddBlendShapeFrame("Original opening", 100,
                    Enumerable.Repeat(Vector3.up * .01f, fixture.Mesh.vertexCount).ToArray(), null, null);
                foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>())
                { skin.SetBlendShapeWeight(0, 0); skin.sharedMaterial.shader = Shader.Find("lilToon"); }
                var originalFx = DefaultFx(folder + "/Original.controller", "Front", "Original opening", 0);
                SetFx(fixture.Source.AddComponent(type), originalFx);
                Object.DestroyImmediate(fixture.Copy); fixture.Copy = Object.Instantiate(fixture.Source);
                var generatedPath = moved ? "Generated/Prepared face" : "Prepared face";
                ResetFakeNdmf();
                NdmfPreparationTests.FakeProcessor.Action = root =>
                {
                    var skin = root.transform.Find("Front").GetComponent<SkinnedMeshRenderer>();
                    if (replacedRenderer)
                    {
                        var old = skin;
                        var replacement = new GameObject("Prepared face"); replacement.transform.SetParent(old.transform.parent, false);
                        replacement.transform.localPosition = old.transform.localPosition;
                        replacement.transform.localRotation = old.transform.localRotation;
                        replacement.transform.localScale = old.transform.localScale;
                        skin = replacement.AddComponent<SkinnedMeshRenderer>();
                        skin.sharedMesh = old.sharedMesh; skin.sharedMaterials = old.sharedMaterials;
                        skin.bones = old.bones; skin.rootBone = old.rootBone;
                        Object.DestroyImmediate(old.gameObject);
                    }
                    skin.name = "Prepared face";
                    if (moved)
                    {
                        var parent = new GameObject("Generated"); parent.transform.SetParent(root.transform, false);
                        skin.transform.SetParent(parent.transform, true);
                    }
                    var mesh = Object.Instantiate(skin.sharedMesh); mesh.name = "NDMF generated face";
                    mesh.ClearBlendShapes();
                    mesh.AddBlendShapeFrame("Prepared opening", 50, Enumerable.Repeat(Vector3.up * .01f, mesh.vertexCount).ToArray(), null, null);
                    mesh.AddBlendShapeFrame("Prepared opening", 100, Enumerable.Repeat(Vector3.up * .03f, mesh.vertexCount).ToArray(), null, null);
                    skin.sharedMesh = mesh; skin.SetBlendShapeWeight(0, 0);
                    SetFx(root.GetComponent(type), DefaultFx(folder + "/Generated.controller", generatedPath, "Prepared opening", 75));
                    var vrm = ScriptableObject.CreateInstance<VRM10Object>();
                    var expression = ScriptableObject.CreateInstance<VRM10Expression>(); expression.name = "UE/JawOpen";
                    expression.MorphTargetBindings = new[] { new MorphTargetBinding(generatedPath, 0, .25f) };
                    vrm.Expression.CustomClips.Add(expression); root.AddComponent<Vrm10Instance>().Vrm = vrm;
                };
                preparation = NdmfExportPreparation.ProcessClone(fixture.Source, fixture.Copy, FakeBridge());
                Assert.That(NdmfPreparationTests.FakeProcessor.Calls, Is.EqualTo(2));
                var preparedSkin = fixture.Copy.transform.Find(generatedPath).GetComponent<SkinnedMeshRenderer>();
                Assert.That(preparedSkin.sharedMesh.GetBlendShapeName(0), Is.EqualTo("Prepared opening"));
                Assert.That(NeutralShapeSampler.Sample(fixture.Copy).Single(value => value.Path == generatedPath && value.Shape == "Prepared opening").Weight,
                    Is.EqualTo(75));
                var bytes = UniVrmOneClickExporter.Export(fixture.Copy, "NDMF prepared neutral", "Tests",
                    exporterVersion: fullLilToon ? "prepared-neutral-regression" : null,
                    lilToonVersion: fullLilToon ? "2.3.4" : null,
                    blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                imported.Runtime.Process();
                var output = imported.GetComponentsInChildren<SkinnedMeshRenderer>().Single(skin => skin.name == "Prepared face");
                Assert.That(output.sharedMesh.vertices[0].y, Is.EqualTo(fixture.Mesh.vertices[0].y).Within(.00001f),
                    "The generated channel must retain prepared0 without restoring FX75 on the final renderer path.");
                output.BakeMesh(baked); var neutralY = baked.vertices[0].y;
                imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom("UE/JawOpen"), 1);
                imported.Runtime.Process(); output.BakeMesh(baked);
                Assert.That(baked.vertices[0].y, Is.EqualTo(neutralY + .005f).Within(.00001f),
                    "The authored0.25 endpoint must reach source25 from the retained prepared0 baseline.");
                Assert.That(preparedSkin.GetBlendShapeWeight(0), Is.Zero);
                Assert.That(fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>().sharedMesh, Is.SameAs(fixture.Mesh));
                Assert.That(fixture.Mesh.GetBlendShapeName(0), Is.EqualTo("Original opening"));
                Assert.That(fixture.Source.GetComponent(type), Is.Not.Null);
                Assert.That(fixture.Source.GetComponent<Vrm10Instance>(), Is.Null);
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                preparation?.Dispose(); NdmfPreparationTests.FakeProcessor.Action = null;
                Object.DestroyImmediate(baked); AssetDatabase.DeleteAsset(folder);
            }
        }

        static void ResetFakeNdmf()
        {
            NdmfPreparationTests.FakeContext.Success = true;
            NdmfPreparationTests.FakeContext.FailFinish = false; NdmfPreparationTests.FakeContext.FailSaverDispose = false;
            NdmfPreparationTests.FakeDirectoryScope.FailDispose = false;
            NdmfPreparationTests.FakeDirectoryScope.Current = "neutral-export-test";
            NdmfPreparationTests.FakeContext.ReportedErrors.Clear();
            NdmfPreparationTests.FakeRegistry.Selected = new NdmfPreparationTests.FakeProvider();
            NdmfPreparationTests.FakeProcessor.Calls = 0;
        }

        static NdmfExportPreparation.Bridge FakeBridge() => NdmfExportPreparation.Bridge.Resolve(name =>
        {
            switch (name)
            {
                case "nadena.dev.ndmf.AvatarProcessor": return typeof(NdmfPreparationTests.FakeProcessor);
                case "nadena.dev.ndmf.BuildContext": return typeof(NdmfPreparationTests.FakeContext);
                case "nadena.dev.ndmf.BuildPhase": return typeof(NdmfPreparationTests.FakePhase);
                case "nadena.dev.ndmf.platform.INDMFPlatformProvider": return typeof(NdmfPreparationTests.IFakeProvider);
                case "nadena.dev.ndmf.platform.PlatformRegistry": return typeof(NdmfPreparationTests.FakeRegistry);
                case "nadena.dev.ndmf.platform.GenericPlatform": return typeof(NdmfPreparationTests.FakeGeneric);
                case "nadena.dev.ndmf.OverrideTemporaryDirectoryScope": return typeof(NdmfPreparationTests.FakeDirectoryScope);
                default: return null;
            }
        }, "1.14.8");
    }
}
