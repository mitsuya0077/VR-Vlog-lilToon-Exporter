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
    public sealed class AdditionalPlayableMorphTests
    {
        string folder;
        GameObject avatar;
        Mesh mesh;
        SkinnedMeshRenderer skin;
        Component descriptor;
        AnimatorController fx, locomotion;
        AnimatorState rest;

        static EditorCurveBinding Binding(string shape, string path = "Body") =>
            EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + shape);

        static Type SdkType(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components." + name)).FirstOrDefault(type => type != null);

        [SetUp]
        public void SetUp()
        {
            if (SdkType("VRCAvatarDescriptor") == null) Assert.Ignore("Install the real VRChat SDK.");
            var name = "__AdditionalPlayableMorph_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            fx = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            locomotion = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Locomotion.controller");
            avatar = new GameObject("Prepared avatar", typeof(Animator));
            var body = new GameObject("Body", typeof(SkinnedMeshRenderer)); body.transform.SetParent(avatar.transform, false);
            skin = body.GetComponent<SkinnedMeshRenderer>();
            mesh = new Mesh { name = "Prepared neutral mouth" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }; mesh.triangles = new[] { 0, 1, 2 };
            foreach (var item in new[] { (Name: "Mouth", Delta: Vector3.up * .015f),
                (Name: "Companion", Delta: Vector3.right * .02f), (Name: "Independent", Delta: Vector3.forward * .01f) })
                mesh.AddBlendShapeFrame(item.Name, 100, Enumerable.Repeat(item.Delta, 3).ToArray(), null, null);
            skin.sharedMesh = mesh;
            skin.SetBlendShapeWeight(0, 17); skin.SetBlendShapeWeight(1, 23); skin.SetBlendShapeWeight(2, 31);
            descriptor = avatar.AddComponent(SdkType("VRCAvatarDescriptor"));
            rest = State(fx.layers[0].stateMachine, Clip(fx, "Authored FX mouth", ("Mouth", 75), ("Companion", 60)));
            State(Layer(fx, "Independent neutral"), Clip(fx, "Independent FX", ("Independent", 100)));
            State(locomotion.layers[0].stateMachine, Clip(locomotion, "Locomotion mouth", ("Mouth", 0)));
            SetControllers(descriptor, fx, ("Base", locomotion));
        }

        [TearDown]
        public void TearDown()
        {
            if (avatar != null) Object.DestroyImmediate(avatar);
            if (mesh != null) Object.DestroyImmediate(mesh);
            if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        static AnimatorStateMachine Layer(AnimatorController target, string name)
        {
            target.AddLayer(name); var layers = target.layers; layers[layers.Length - 1].defaultWeight = 1; target.layers = layers;
            return layers[layers.Length - 1].stateMachine;
        }

        static AnimatorState State(AnimatorStateMachine machine, Motion motion)
        {
            var state = machine.AddState("State " + machine.states.Length); state.motion = motion; state.writeDefaultValues = false;
            if (machine.states.Length == 1) machine.defaultState = state;
            return state;
        }

        static AnimationClip Clip(AnimatorController target, string name, params (string Shape, float Weight)[] values)
        {
            var clip = new AnimationClip { name = name };
            foreach (var value in values) AnimationUtility.SetEditorCurve(clip, Binding(value.Shape), AnimationCurve.Constant(0, 1, value.Weight));
            AssetDatabase.AddObjectToAsset(clip, target); return clip;
        }

        static void SetControllers(Component target, AnimatorController controller, params (string Kind, AnimatorController Controller)[] others)
        {
            using var data = new SerializedObject(target);
            data.FindProperty("customizeAnimationLayers").boolValue = true;
            var values = new[] { (Kind: "FX", Controller: controller) }.Concat(others).ToArray();
            var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = values.Length;
            for (var index = 0; index < values.Length; index++)
            {
                var layer = layers.GetArrayElementAtIndex(index); var type = layer.FindPropertyRelative("type");
                type.enumValueIndex = Array.IndexOf(type.enumNames, values[index].Kind);
                layer.FindPropertyRelative("isDefault").boolValue = false;
                layer.FindPropertyRelative("animatorController").objectReferenceValue = values[index].Controller;
            }
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        Dictionary<Object, string> Capture() => new[] { "FX.controller", "Locomotion.controller", "Later.controller" }
            .SelectMany(name => AssetDatabase.LoadAllAssetsAtPath(folder + "/" + name)).Where(value => value != null)
            .Concat(new Object[] { skin, mesh, descriptor }).ToDictionary(value => value, value => EditorJsonUtility.ToJson(value));

        void Unchanged(Dictionary<Object, string> before)
        {
            foreach (var pair in before) Assert.That(EditorJsonUtility.ToJson(pair.Key), Is.EqualTo(pair.Value), pair.Key.name);
            Assert.That(skin.sharedMesh, Is.SameAs(mesh));
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17)); Assert.That(skin.GetBlendShapeWeight(1), Is.EqualTo(23));
            Assert.That(skin.GetBlendShapeWeight(2), Is.EqualTo(31));
        }

        [TestCase("Base", false)]
        [TestCase("Base", true)]
        [TestCase("Gesture", false)]
        [TestCase("Gesture", true)]
        [TestCase("Action", false)]
        [TestCase("Action", true)]
        public void KnownAdditionalMorphKeepsCoupledPreparedRestAndSamplesIndependentNeutral(string playable, bool moving)
        {
            if (moving) AnimationUtility.SetEditorCurve((AnimationClip)locomotion.layers[0].stateMachine.defaultState.motion,
                Binding("Mouth"), AnimationCurve.Linear(0, 0, 10, 100));
            SetControllers(descriptor, fx, (playable, locomotion));
            var before = Capture(); var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings, requiredMorphs: new[] { Binding("Mouth") });
            Assert.That(values.Select(value => value.Shape), Is.EqualTo(new[] { "Independent" }));
            Assert.That(values.Single().Weight, Is.EqualTo(100).Within(.01));
            Assert.That(warnings.Any(value => value.Contains("Locomotion") && value.Contains("Body/blendShape.Mouth") &&
                value.Contains("Companion") && value.Contains("設定を保持")), Is.True, string.Join("\n", warnings));
            Unchanged(before);
            NeutralShapeSnapshot.Apply(avatar, values);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17)); Assert.That(skin.GetBlendShapeWeight(1), Is.EqualTo(23));
            Assert.That(skin.GetBlendShapeWeight(2), Is.EqualTo(100).Within(.01));
        }

        [Test]
        public void AdditionalMorphCannotBeSilentlyDroppedFromFixedExpressionEndpoints()
        {
            fx.AddParameter("Menu", AnimatorControllerParameterType.Int);
            var selected = State(fx.layers[0].stateMachine, Clip(fx, "Selected mouth", ("Mouth", 100), ("Companion", 80)));
            var transition = rest.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "Menu");
            var source = VrChatExpressionMenu.Read(avatar, new VrChatMenuImportPolicy { SkipAll = true });
            var before = Capture();
            var error = Assert.Throws<InvalidOperationException>(() => VrChatExpressionSampler.SampleFixed(avatar, fx, source.Defaults,
                new Dictionary<string, float> { ["Menu"] = 1 }, null, source));
            Assert.That(error.Message, Does.Contain("FX以外").And.Contain("Locomotion").And.Contain("blendShape.Mouth"));
            Assert.That(Assert.Throws<InvalidOperationException>(() => ExpressionDependencies.ValidateAdditionalProbeBehaviours(fx, source,
                FixedExpressionContext.Create(fx, source.Defaults, source), new HashSet<EditorCurveBinding> { Binding("Mouth") })).Message,
                Does.Contain("blendShape.Mouth"));
            Unchanged(before);
        }

        [Test]
        public void LaterOverlapDiscardsEarlierSamplesForTheCompleteParameterDependencyComponent()
        {
            fx.AddParameter("Axis", AnimatorControllerParameterType.Int);
            rest.motion = Clip(fx, "Earlier companion", ("Companion", 60));
            ParameterDriverExpressionTests.Driver(rest, ParameterDriverExpressionTests.Op("Set", "Axis", 1));
            var consumer = Layer(fx, "Coupled later mouth");
            var first = State(consumer, Clip(fx, "Normal mouth", ("Mouth", 75)));
            var next = State(consumer, Clip(fx, "Driven mouth", ("Mouth", 85)));
            var transition = first.AddTransition(next); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "Axis");
            var before = Capture(); var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values.Select(value => value.Shape), Is.EqualTo(new[] { "Independent" }),
                "A previous successful companion sample must not survive a later fallback in its dependency component.");
            Assert.That(warnings.Any(value => value.Contains("Mouth") && value.Contains("Companion") && value.Contains("FX以外")), Is.True);
            Unchanged(before);
            // Reverse the overlapping writer: the producer-side refusal must
            // co-retain its downstream consumer, too. The native execution
            // closure alone follows reader-to-producer dependencies.
            locomotion.layers[0].stateMachine.defaultState.motion = Clip(locomotion, "Overlapping parameter producer", ("Companion", 0));
            before = Capture(); warnings.Clear();
            values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values.Select(value => value.Shape), Is.EqualTo(new[] { "Independent" }),
                "A producer-side fallback must not keep its sampled downstream consumer as a partial prepared configuration.");
            Assert.That(warnings.Any(value => value.Contains("Mouth") && value.Contains("Companion") && value.Contains("FX以外")), Is.True);
            Unchanged(before);
        }

        [Test]
        public void AppearanceOwnedAdditionalMorphStillReportsThePreparedRestCapability()
        {
            var accessory = new GameObject("Accessory"); accessory.transform.SetParent(avatar.transform, false);
            AnimationUtility.SetEditorCurve((AnimationClip)rest.motion, EditorCurveBinding.FloatCurve("Accessory", typeof(GameObject), "m_IsActive"),
                AnimationCurve.Constant(0, 1, 0));
            var before = Capture(); var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values.Select(value => value.Shape), Is.EqualTo(new[] { "Independent" }));
            Assert.That(warnings.Any(value => value.Contains("FX以外") && value.Contains("Locomotion") && value.Contains("blendShape.Mouth")), Is.True);
            Assert.That(accessory.activeSelf, Is.True); Unchanged(before);
        }

        [Test]
        public void ImplicitDefaultClosureRetainsPreparedComponentAndPreservesProvenIndependentTop()
        {
            var support = State(Layer(fx, "Implicit default support"), Clip(fx, "WD companion", ("Companion", 40)));
            support.writeDefaultValues = true;
            State(Layer(fx, "Proven independent final override"), Clip(fx, "Independent final constant", ("Independent", 100)));
            var source = VrChatExpressionMenu.Read(avatar, new VrChatMenuImportPolicy { SkipAll = true });
            var context = FixedExpressionContext.Create(fx, source.Defaults, source);
            var error = Assert.Throws<NeutralShapeSamplingException>(() => ExpressionDependencies.AnalyzeNeutral(fx,
                new HashSet<EditorCurveBinding> { Binding("Mouth"), Binding("Companion") }, null, source, fixedContext: context,
                preserveCommittedMorphs: true));
            Assert.That(error.DependencyMorphs, Does.Contain(Binding("Independent")), "Keep the complete execution closure.");
            Assert.That(error.CoupledMorphs.Contains(Binding("Independent")), Is.False, "WD-only support is not an explicit parameter component.");
            var before = Capture(); var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values.Select(value => value.Shape), Is.EqualTo(new[] { "Independent" }));
            Assert.That(values.Single().Weight, Is.EqualTo(100).Within(.01));
            Assert.That(warnings.Any(value => value.Contains("Mouth") && value.Contains("FX以外")), Is.True);
            Assert.That(warnings.Any(value => value.Contains("Independent")), Is.False,
                "Diagnostics must describe the retained component without claiming the independently sampled output was preserved.");
            Unchanged(before);
        }

        [Test]
        public void AdditionalMorphCannotHideContradictoryEvaluatedFxLayerCommands()
        {
            var type = SdkType("VRCAnimatorLayerControl");
            if (type == null) Assert.Ignore("Install the real VRChat SDK.");
            foreach (var goal in new[] { 0f, 1f })
            {
                var control = rest.AddStateMachineBehaviour(type);
                using var data = new SerializedObject(control);
                var playable = data.FindProperty("playable"); playable.enumValueIndex = Array.IndexOf(playable.enumNames, "FX");
                data.FindProperty("layer").intValue = 1;
                data.FindProperty("goalWeight").floatValue = goal; data.FindProperty("blendDuration").floatValue = 0;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
            var source = VrChatExpressionMenu.Read(avatar, new VrChatMenuImportPolicy { SkipAll = true });
            var before = Capture();
            var error = Assert.Throws<InvalidOperationException>(() => ExpressionDependencies.AnalyzeNeutral(fx,
                new HashSet<EditorCurveBinding> { Binding("Mouth"), Binding("Independent") }, null, source,
                fixedContext: FixedExpressionContext.Create(fx, source.Defaults, source), preserveCommittedMorphs: true, evaluateLayerWeights: true));
            Assert.That(error.Message, Does.Contain("複数のFXレイヤー制御が競合"));
            Unchanged(before);
        }

        [TestCase("Unknown")]
        [TestCase("ParameterWriter")]
        [TestCase("AnimationEvent")]
        [TestCase("NaN")]
        [TestCase("ObjectTime")]
        [TestCase("MalformedDriver")]
        public void MorphCapabilityCannotHideHardFailuresInALaterController(string damage)
        {
            var later = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Later.controller");
            var clip = Clip(later, "Later authored data", ("Independent", 0)); var state = State(later.layers[0].stateMachine, clip);
            string diagnostic;
            if (damage == "Unknown") { state.AddStateMachineBehaviour<UnknownStateCallbackProbe>(); diagnostic = nameof(UnknownStateCallbackProbe); }
            else if (damage == "ParameterWriter")
            {
                fx.AddParameter("Axis", AnimatorControllerParameterType.Int); later.AddParameter("Axis", AnimatorControllerParameterType.Int);
                var next = State(fx.layers[0].stateMachine, Clip(fx, "Driven FX", ("Mouth", 100)));
                var transition = rest.AddTransition(next); transition.hasExitTime = false; transition.duration = 0;
                transition.AddCondition(AnimatorConditionMode.Equals, 1, "Axis");
                ParameterDriverExpressionTests.Driver(state, ParameterDriverExpressionTests.Op("Set", "Axis", 1)); diagnostic = "Axis";
            }
            else if (damage == "AnimationEvent")
            { AnimationUtility.SetAnimationEvents(clip, new[] { new AnimationEvent { functionName = "UnsupportedEvent", time = .5f } }); diagnostic = "AnimationEvent"; }
            else if (damage == "NaN")
            {
                var binding = EditorCurveBinding.FloatCurve("Body", typeof(Transform), "m_LocalPosition.x");
                // Unity drops nonfinite key values. An active malformed
                // tangent survives the actual native curve serialization.
                AnimationUtility.SetEditorCurve(clip, binding,
                    new AnimationCurve(new Keyframe(0, 0, 0, float.NaN), new Keyframe(1, 1, 1, 0)));
                var persisted = AnimationUtility.GetEditorCurve(clip, binding);
                Assert.That(persisted, Is.Not.Null); Assert.That(persisted.length, Is.EqualTo(2));
                Assert.That(float.IsNaN(persisted.keys[0].outTangent), Is.True, "The actual clip must contain malformed active interpolation data.");
                diagnostic = "m_LocalPosition.x";
            }
            else if (damage == "ObjectTime")
            {
                var reference = Object.Instantiate(mesh); AssetDatabase.CreateAsset(reference, folder + "/ReferencedMesh.asset");
                var binding = EditorCurveBinding.PPtrCurve("Body", typeof(SkinnedMeshRenderer), "m_Mesh");
                AnimationUtility.SetObjectReferenceCurve(clip, binding,
                    new[] { new ObjectReferenceKeyframe { time = 0, value = reference }, new ObjectReferenceKeyframe { time = 1, value = reference } });
                // The public setter drops invalid times. Modify a real,
                // persistent clip key after it has a valid native binding.
                using (var data = new SerializedObject(clip))
                {
                    var curves = data.FindProperty("m_PPtrCurves");
                    Assert.That(curves, Is.Not.Null); Assert.That(curves.arraySize, Is.EqualTo(1));
                    var keys = curves.GetArrayElementAtIndex(0).FindPropertyRelative("curve");
                    Assert.That(keys, Is.Not.Null); Assert.That(keys.arraySize, Is.EqualTo(2));
                    keys.GetArrayElementAtIndex(0).FindPropertyRelative("time").floatValue = float.NaN;
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
                var persisted = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                Assert.That(persisted.Length, Is.EqualTo(2));
                Assert.That(persisted.Any(key => float.IsNaN(key.time)), Is.True, "The actual clip must retain the invalid object reference key time.");
                diagnostic = "m_Mesh";
            }
            else
            {
                later.AddParameter("Needed", AnimatorControllerParameterType.Bool);
                var next = State(later.layers[0].stateMachine, Clip(later, "Later consumer", ("Independent", 0)));
                var transition = state.AddTransition(next); transition.hasExitTime = false; transition.duration = 0;
                transition.AddCondition(AnimatorConditionMode.If, 0, "Needed");
                ParameterDriverExpressionTests.Driver(state, ParameterDriverExpressionTests.Op("Add", "Needed", 1)); diagnostic = "Add";
            }
            SetControllers(descriptor, fx, ("Base", locomotion), ("Gesture", later));
            var before = Capture(); var warnings = new List<string>();
            var error = Assert.Throws<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar, warnings: warnings));
            Assert.That(error.Message, Does.Contain(diagnostic));
            Assert.That(warnings, Is.Empty, "A known morph capability must not conceal later unsafe data or effects.");
            Unchanged(before);
        }

        [Test]
        public async Task LocomotionMouthOverlapExportsPreparedRestAndAuthoredAbsoluteEndpoint()
        {
            var shader = Shader.Find("lilToon");
            if (shader == null) Assert.Ignore("Install lilToon for the full exporter entry point.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            fixture.Mesh.ClearBlendShapes();
            foreach (var item in new[] { (Name: "Mouth", Delta: Vector3.up * .015f),
                (Name: "Companion", Delta: Vector3.right * .02f), (Name: "Independent", Delta: Vector3.forward * .01f) })
                fixture.Mesh.AddBlendShapeFrame(item.Name, 100, Enumerable.Repeat(item.Delta, fixture.Mesh.vertexCount).ToArray(), null, null);
            foreach (var renderer in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                renderer.sharedMaterial.shader = shader;
                renderer.SetBlendShapeWeight(0, 17); renderer.SetBlendShapeWeight(1, 23); renderer.SetBlendShapeWeight(2, 31);
            }
            // Both fixture renderers bind to the same authored FX program.
            foreach (var runtime in new[] { fx, locomotion })
                foreach (var clip in runtime.animationClips)
                    foreach (var binding in AnimationUtility.GetCurveBindings(clip).ToArray())
                    {
                        var curve = AnimationUtility.GetEditorCurve(clip, binding);
                        AnimationUtility.SetEditorCurve(clip, binding, null);
                        foreach (var path in new[] { "Front", "Back" })
                            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, binding.type, binding.propertyName), curve);
                    }
            SetControllers(fixture.Source.AddComponent(SdkType("VRCAvatarDescriptor")), fx, ("Base", locomotion));
            var vrm = ScriptableObject.CreateInstance<VRM10Object>();
            var endpoint = ScriptableObject.CreateInstance<VRM10Expression>(); endpoint.name = "Authored mouth";
            endpoint.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 0, .8f), new MorphTargetBinding("Back", 0, .8f) };
            vrm.Expression.CustomClips.Add(endpoint);
            AssetDatabase.CreateAsset(vrm, folder + "/Vrm.asset"); AssetDatabase.CreateAsset(endpoint, folder + "/Endpoint.asset");
            fixture.Source.AddComponent<Vrm10Instance>().Vrm = vrm;
            var before = ExportSourceFingerprint.Compute(fixture.Source); var beforeAssets = Capture();
            var endpointBefore = EditorJsonUtility.ToJson(endpoint); var warnings = new List<string>();
            Vrm10Instance imported = null; GameObject reference = null;
            try
            {
                reference = Object.Instantiate(fixture.Source);
                foreach (var behaviour in reference.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Additional playable regression", "Tests", warnings,
                    blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported, Is.Not.Null); Assert.That(imported.Vrm.Expression.CustomClips.Any(value => value.name == endpoint.name), Is.True);
                Assert.That(warnings.Any(value => value.Contains("Locomotion") && value.Contains("blendShape.Mouth") && value.Contains("設定を保持")), Is.True,
                    string.Join("\n", warnings));
                var key = ExpressionKey.CreateCustom(endpoint.name);
                foreach (var weight in new[] { 0f, 1f, 0f })
                {
                    imported.Runtime.Expression.SetWeight(key, weight); imported.Runtime.Process();
                    foreach (var path in new[] { "Front", "Back" })
                    {
                        var expected = reference.transform.Find(path).GetComponent<SkinnedMeshRenderer>();
                        expected.SetBlendShapeWeight(0, weight == 0 ? 17 : 80);
                        expected.SetBlendShapeWeight(1, 23); expected.SetBlendShapeWeight(2, 100);
                        var actual = imported.GetComponentsInChildren<SkinnedMeshRenderer>().Single(value => value.name == path);
                        var expectedVertices = Vertices(expected); var actualVertices = Vertices(actual);
                        Assert.That(actualVertices.Length, Is.EqualTo(expectedVertices.Length));
                        for (var index = 0; index < expectedVertices.Length; index++)
                            Assert.That(Vector3.Distance(actualVertices[index], expectedVertices[index]), Is.LessThan(.0005f), path + " / " + weight);
                    }
                }
                Assert.That(ExportSourceFingerprint.Compute(fixture.Source), Is.EqualTo(before));
                Assert.That(EditorJsonUtility.ToJson(endpoint), Is.EqualTo(endpointBefore)); Unchanged(beforeAssets);
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                if (reference != null) Object.DestroyImmediate(reference);
            }
        }

        static Vector3[] Vertices(SkinnedMeshRenderer renderer)
        {
            var baked = new Mesh();
            try { renderer.BakeMesh(baked, false); return baked.vertices.Select(renderer.transform.TransformPoint).ToArray(); }
            finally { Object.DestroyImmediate(baked); }
        }
    }
}
