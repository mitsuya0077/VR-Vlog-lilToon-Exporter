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
using VRVlog.Expressions;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class TemporalNeutralShapeTests
    {
        private string folder;
        private GameObject avatar;
        private Mesh mesh;
        private SkinnedMeshRenderer skin;
        private AnimatorController controller;

        private static Type DescriptorType() => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor")).FirstOrDefault(type => type != null);

        private static EditorCurveBinding Binding(string shape, string path = "Body") =>
            EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + shape);

        private static void SetFx(Component descriptor, RuntimeAnimatorController runtime)
        {
            using var data = new SerializedObject(descriptor);
            data.FindProperty("customizeAnimationLayers").boolValue = true;
            var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = 1;
            var layer = layers.GetArrayElementAtIndex(0);
            var type = layer.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
            layer.FindPropertyRelative("isDefault").boolValue = false;
            layer.FindPropertyRelative("animatorController").objectReferenceValue = runtime;
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        [SetUp]
        public void SetUp()
        {
            var descriptor = DescriptorType(); if (descriptor == null) Assert.Ignore("Install the real VRChat SDK.");
            var name = "__TemporalNeutral_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            avatar = new GameObject("Temporal neutral reference", typeof(Animator));
            var body = new GameObject("Body", typeof(SkinnedMeshRenderer)); body.transform.SetParent(avatar.transform, false);
            skin = body.GetComponent<SkinnedMeshRenderer>();
            mesh = new Mesh { name = "Prepared authored appearance" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }; mesh.triangles = new[] { 0, 1, 2 };
            foreach (var shape in new[] { "Temporal", "Constant", "Implicit" })
                mesh.AddBlendShapeFrame(shape, 100, Enumerable.Repeat(Vector3.up * .01f, 3).ToArray(), null, null);
            skin.sharedMesh = mesh; skin.SetBlendShapeWeight(0, 17); skin.SetBlendShapeWeight(1, 35); skin.SetBlendShapeWeight(2, 27);
            SetFx(avatar.AddComponent(descriptor), controller);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(avatar); Object.DestroyImmediate(mesh);
            if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        private AnimationClip Clip(string name, params (string Shape, AnimationCurve Curve)[] curves)
        {
            var clip = new AnimationClip { name = name };
            foreach (var item in curves) AnimationUtility.SetEditorCurve(clip, Binding(item.Shape), item.Curve);
            AssetDatabase.AddObjectToAsset(clip, controller); return clip;
        }

        private AnimatorState State(int layer, AnimationClip clip, bool writeDefaults = false)
        {
            var machine = controller.layers[layer].stateMachine;
            var state = machine.AddState("State " + machine.states.Length); state.motion = clip; state.writeDefaultValues = writeDefaults;
            if (machine.states.Length == 1) machine.defaultState = state;
            return state;
        }

        private int Layer(string name, float weight = 1, AnimatorLayerBlendingMode mode = AnimatorLayerBlendingMode.Override)
        {
            controller.AddLayer(name); var layers = controller.layers; var index = layers.Length - 1;
            layers[index].defaultWeight = weight; layers[index].blendingMode = mode; controller.layers = layers; return index;
        }

        private static AnimationCurve Varying(string kind)
        {
            if (kind == "Delayed") return new AnimationCurve(new Keyframe(0, 0), new Keyframe(10, 0), new Keyframe(10.5f, 80), new Keyframe(12, 0));
            if (kind == "Nonloop") return AnimationCurve.Linear(0, 0, 1, 80);
            return new AnimationCurve(
                new Keyframe(0, 25, 0, 70, .3f, .7f) { weightedMode = WeightedMode.Both },
                new Keyframe(2, 25, -70, 0, .7f, .3f) { weightedMode = WeightedMode.Both });
        }

        [TestCase("Delayed", true)]
        [TestCase("Nonloop", false)]
        [TestCase("Weighted", true)]
        public void TemporalRestKeepsPreparedWeightAndReconstructsTheIndependentConstant(string kind, bool loop)
        {
            var clip = Clip("Authored idle", ("Temporal", Varying(kind)), ("Constant", AnimationCurve.Constant(0, 12, 63)));
            var settings = AnimationUtility.GetAnimationClipSettings(clip); settings.loopTime = loop;
            AnimationUtility.SetAnimationClipSettings(clip, settings); State(0, clip);
            var meshBefore = EditorJsonUtility.ToJson(mesh); var clipBefore = EditorJsonUtility.ToJson(clip);
            var controllerBefore = EditorJsonUtility.ToJson(controller); var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings, requiredMorphs: new[] { Binding("Temporal") });
            Assert.That(values.Select(value => value.Shape), Is.EquivalentTo(new[] { "Constant" }));
            Assert.That(values.Single().Weight, Is.EqualTo(NativeWeight("Constant")).Within(.001));
            Assert.That(values.Single().Weight, Is.EqualTo(63).Within(.001));
            NeutralShapeSnapshot.Apply(avatar, values);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17)); Assert.That(skin.GetBlendShapeWeight(1), Is.EqualTo(63).Within(.001));
            Assert.That(skin.GetBlendShapeWeight(2), Is.EqualTo(27));
            Assert.That(warnings.Any(value => value.Contains("時間で変わる") && value.Contains("Authored idle") && value.Contains("blendShape.Temporal")), Is.True);
            Assert.That(EditorJsonUtility.ToJson(mesh), Is.EqualTo(meshBefore)); Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(clipBefore));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(controllerBefore));
        }

        [Test]
        public void MixedIdleKeepsAllSeventeenTemporalAndSamplesAllOneHundredFifteenStaticChannels()
        {
            mesh.ClearBlendShapes();
            var curves = new List<(string Shape, AnimationCurve Curve)>();
            for (var index = 0; index < 132; index++)
            {
                var name = "Channel " + index;
                mesh.AddBlendShapeFrame(name, 100, Enumerable.Repeat(Vector3.up * .01f, 3).ToArray(), null, null);
                skin.SetBlendShapeWeight(index, 17);
                curves.Add((name, index < 17 ? Varying("Delayed") : AnimationCurve.Constant(0, 12, 63)));
            }
            var clip = Clip("Complete authored idle", curves.ToArray()); State(0, clip);
            var before = EditorJsonUtility.ToJson(clip); var warnings = new List<string>();
            var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values.Count, Is.EqualTo(115));
            Assert.That(values.Select(value => value.Shape), Is.EquivalentTo(Enumerable.Range(17, 115).Select(index => "Channel " + index)));
            Assert.That(values.All(value => Math.Abs(value.Weight - 63) < .001f), Is.True);
            NeutralShapeSnapshot.Apply(avatar, values);
            for (var index = 0; index < 132; index++) Assert.That(skin.GetBlendShapeWeight(index), Is.EqualTo(index < 17 ? 17 : 63).Within(.001));
            Assert.That(warnings.Any(value => value.Contains("時間で変わる") && value.Contains("ほか9件")), Is.True);
            Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(before));
        }

        [Test]
        public void InactiveAlternateTemporalClipDoesNotChangeTheActiveConstantCapture()
        {
            State(0, Clip("Active flat rest", ("Temporal", AnimationCurve.Constant(0, 1, 51))));
            State(0, Clip("Unreachable animated alternative", ("Temporal", Varying("Delayed"))));
            var warnings = new List<string>(); var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values.Single(value => value.Shape == "Temporal").Weight, Is.EqualTo(51).Within(.001));
            Assert.That(warnings.Any(value => value.Contains("時間で変わる")), Is.False);
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void TemporalCaptureKeepsIndependentNativeOverrideAdditiveAndWriteDefaults(bool additive, bool writeDefaults)
        {
            State(0, Clip("Lower native rest", ("Temporal", Varying("Delayed")), ("Constant", AnimationCurve.Constant(0, 12, 20)),
                ("Implicit", AnimationCurve.Constant(0, 12, 27))));
            var upper = Layer("Independent native contribution", .5f, additive ? AnimatorLayerBlendingMode.Additive : AnimatorLayerBlendingMode.Override);
            var contribution = Clip("Upper static contribution", ("Constant", AnimationCurve.Constant(0, 12, 60)));
            if (additive)
            {
                var reference = Clip("Explicit additive zero reference", ("Constant", AnimationCurve.Constant(0, 12, 0)));
                AnimationUtility.SetAdditiveReferencePose(contribution, reference, 0);
            }
            State(upper, contribution, writeDefaults);
            var expected = NativeWeight("Constant"); var before = EditorJsonUtility.ToJson(controller);
            var values = NeutralShapeSampler.Sample(avatar);
            Assert.That(values.Any(value => value.Shape == "Temporal"), Is.False);
            Assert.That(values.Single(value => value.Shape == "Constant").Weight, Is.EqualTo(expected).Within(.001));
            Assert.That(expected, Is.GreaterThan(20.01f), "The independent upper contribution must be exercised by the native reference.");
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17)); Assert.That(skin.GetBlendShapeWeight(1), Is.EqualTo(35));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before));
        }

        [TestCase("Full")]
        [TestCase("Fractional")]
        [TestCase("NearFull")]
        [TestCase("Additive")]
        [TestCase("Masked")]
        [TestCase("VariableUpper")]
        public void OnlyAFullUnmaskedExplicitConstantOverrideDominatesTheLowerTemporalCurve(string mode)
        {
            State(0, Clip("Lower changing rest", ("Temporal", Varying("Delayed")), ("Constant", AnimationCurve.Constant(0, 12, 63))));
            var upper = Layer("Upper writer", mode == "Fractional" ? .5f : mode == "NearFull" ? .999995f : 1,
                mode == "Additive" ? AnimatorLayerBlendingMode.Additive : AnimatorLayerBlendingMode.Override);
            var clip = Clip("Upper rest", ("Temporal", mode == "VariableUpper" ? Varying("Nonloop") : AnimationCurve.Constant(0, 12, 0)));
            State(upper, clip);
            if (mode == "Masked")
            {
                var mask = new AvatarMask(); AssetDatabase.CreateAsset(mask, folder + "/Mask.mask");
                var layers = controller.layers; layers[upper].avatarMask = mask; controller.layers = layers;
            }
            // An Additive layer can also contribute implicit native bindings.
            // Prove the lower authored constant independently, then compare the
            // full native graph instead of assuming absent upper curves are inert.
            var expectedConstant = NativeWeight("Constant");
            Assert.That(NativeWeight("Constant", upper), Is.EqualTo(63).Within(.001));
            var warnings = new List<string>(); var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
            Assert.That(values.Any(value => value.Shape == "Temporal"), Is.EqualTo(mode == "Full"));
            if (mode == "Full") Assert.That(values.Single(value => value.Shape == "Temporal").Weight, Is.EqualTo(NativeWeight("Temporal")).Within(.001));
            Assert.That(warnings.Any(value => value.Contains("時間で変わる")), Is.EqualTo(mode != "Full"));
            Assert.That(values.Single(value => value.Shape == "Constant").Weight, Is.EqualTo(expectedConstant).Within(.001));
            if (mode != "Additive") Assert.That(expectedConstant, Is.EqualTo(63).Within(.001));
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17));
            Assert.That(skin.GetBlendShapeWeight(1), Is.EqualTo(35));
            Assert.That(skin.GetBlendShapeWeight(2), Is.EqualTo(27));
        }

        [TestCase("TimedState")]
        [TestCase("TimedUpper")]
        [TestCase("AnimatorCurve")]
        [TestCase("RandomDriver")]
        [TestCase("InvalidRandomDriver")]
        public void UnresolvedTemporalGraphKeepsPreparedRestButStillRejectsUnsupportedDrivers(string kind)
        {
            var clip = Clip("Temporal-only idle", ("Temporal", Varying("Delayed"))); var state = State(0, clip);
            if (kind == "TimedState" || kind == "TimedUpper")
            {
                var layer = 0;
                if (kind == "TimedUpper")
                {
                    layer = Layer("Initially dominating upper rest");
                    state = State(layer, Clip("Upper flat rest", ("Temporal", AnimationCurve.Constant(0, 12, 0))));
                }
                var alternate = State(layer, Clip("Later state", ("Temporal", AnimationCurve.Constant(0, 1, 80))));
                var transition = state.AddTransition(alternate); transition.hasExitTime = true; transition.exitTime = 5; transition.duration = 0;
            }
            else
            {
                controller.AddParameter("Changing input", AnimatorControllerParameterType.Float);
                var alternate = State(0, Clip("Controlled state", ("Temporal", AnimationCurve.Constant(0, 1, 80))));
                var transition = state.AddTransition(alternate); transition.hasExitTime = false; transition.duration = 0;
                transition.AddCondition(AnimatorConditionMode.Greater, 1, "Changing input");
                if (kind == "AnimatorCurve") AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "Changing input"), Varying("Delayed"));
                else
                {
                    var driver = ParameterDriverExpressionTests.Driver(state, ParameterDriverExpressionTests.Op("Random", "Changing input"));
                    using var data = new SerializedObject(driver);
                    var operation = data.FindProperty("parameters").GetArrayElementAtIndex(0);
                    operation.FindPropertyRelative("valueMin").floatValue = kind == "InvalidRandomDriver" ? 3 : 0;
                    operation.FindPropertyRelative("valueMax").floatValue = 2;
                    operation.FindPropertyRelative("chance").floatValue = .5f;
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            var before = ExportSourceFingerprint.Compute(avatar);
            if (kind == "InvalidRandomDriver")
            {
                var warnings = new List<string>();
                var error = Assert.Catch<InvalidOperationException>(() => NeutralShapeSampler.Sample(avatar, warnings: warnings));
                Assert.That(error.Message, Does.Contain("Random"));
                Assert.That(VrChatParameterDriver.IsRandomCapability(error), Is.False);
                Assert.That(warnings, Is.Empty, "Malformed Random data must fail before a prepared-rest fallback can mask it.");
            }
            else
            {
                var warnings = new List<string>();
                var values = NeutralShapeSampler.Sample(avatar, warnings: warnings);
                Assert.That(values, Is.Empty, "An unresolved dynamic graph cannot provide a captured neutral phase.");
                NeutralShapeSnapshot.Apply(avatar, values);
                Assert.That(warnings.Any(value => value.Contains(kind == "RandomDriver" ? "Random" : "Temporal")), Is.True);
            }
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(17));
            Assert.That(skin.GetBlendShapeWeight(1), Is.EqualTo(35)); Assert.That(skin.GetBlendShapeWeight(2), Is.EqualTo(27));
            Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before));
        }

        [Test]
        public void DirectFixedExpressionSamplingStillRejectsItsActualTemporalCurve()
        {
            State(0, Clip("Temporal direct route", ("Temporal", Varying("Delayed"))));
            var metadata = VrChatExpressionMenu.Read(avatar, new VrChatMenuImportPolicy { SkipAll = true });
            var context = FixedExpressionContext.Create(controller, metadata.Defaults, metadata);
            var dependencies = ExpressionDependencies.AnalyzeNeutral(controller, new HashSet<EditorCurveBinding> { Binding("Temporal") }, null, metadata,
                fixedContext: context, preserveCommittedMorphs: true);
            Assert.Catch<NeutralShapeSamplingException>(() => VrChatExpressionSampler.SampleNeutral(avatar, controller, dependencies, metadata, null, context));
        }

        private float NativeWeight(string shape, int disabledLayer = -1)
        {
            var reference = Object.Instantiate(avatar); var graph = PlayableGraph.Create("Independent native temporal reference");
            try
            {
                foreach (var behaviour in reference.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                var animator = reference.GetComponent<Animator>(); animator.enabled = true; animator.runtimeAnimatorController = null;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.applyRootMotion = false;
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual); var playable = AnimatorControllerPlayable.Create(graph, controller);
                for (var index = 0; index < controller.layers.Length; index++)
                    playable.SetLayerWeight(index, index == disabledLayer ? 0 : index == 0 ? 1 : controller.layers[index].defaultWeight);
                AnimationPlayableOutput.Create(graph, "Native output", animator).SetSourcePlayable(playable);
                graph.Play(); graph.Evaluate(0); for (var index = 0; index < 120; index++) graph.Evaluate(1f / 60);
                return reference.transform.Find("Body").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(mesh.GetBlendShapeIndex(shape));
            }
            finally { graph.Destroy(); Object.DestroyImmediate(reference); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task TemporalRequiredEndpointExportsPreparedRestAndTheExactAuthoredProgram(bool dominatedBlink)
        {
            var shader = Shader.Find("lilToon"); if (shader == null) Assert.Ignore("Install lilToon for the actual exporter.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            fixture.Mesh.ClearBlendShapes();
            foreach (var item in new[] { (Name: "Blink", Delta: Vector3.down * .03f), (Name: "Static detail", Delta: Vector3.up * .02f) })
                fixture.Mesh.AddBlendShapeFrame(item.Name, 100, Enumerable.Repeat(item.Delta, fixture.Mesh.vertexCount).ToArray(), null, null);
            foreach (var sourceSkin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>())
            { sourceSkin.sharedMaterial.shader = shader; sourceSkin.SetBlendShapeWeight(0, dominatedBlink ? 100 : 17); sourceSkin.SetBlendShapeWeight(1, 35); }
            var fx = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Humanoid.controller");
            fx.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
            var idleClip = new AnimationClip { name = "Authored moving idle" };
            foreach (var path in new[] { "Front", "Back" })
            {
                AnimationUtility.SetEditorCurve(idleClip, Binding("Blink", path), Varying("Delayed"));
                AnimationUtility.SetEditorCurve(idleClip, Binding("Static detail", path), AnimationCurve.Constant(0, 12, 50));
            }
            var settings = AnimationUtility.GetAnimationClipSettings(idleClip); settings.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(idleClip, settings); AssetDatabase.AddObjectToAsset(idleClip, fx);
            var machine = fx.layers[0].stateMachine;
            var idle = machine.AddState("Idle authored expression"); idle.writeDefaultValues = false; idle.motion = idleClip; machine.defaultState = idle;
            var pose = new AnimationClip { name = "Selected static face" };
            foreach (var path in new[] { "Front", "Back" }) AnimationUtility.SetEditorCurve(pose, Binding("Blink", path), AnimationCurve.Constant(0, 1, 80));
            AssetDatabase.AddObjectToAsset(pose, fx); var selected = machine.AddState("Selected face"); selected.writeDefaultValues = false; selected.motion = pose;
            var intoSelected = idle.AddTransition(selected); intoSelected.hasExitTime = false; intoSelected.duration = 0;
            intoSelected.AddCondition(AnimatorConditionMode.Equals, 2, "GestureRight");
            var intoIdle = selected.AddTransition(idle); intoIdle.hasExitTime = false; intoIdle.duration = 0;
            intoIdle.AddCondition(AnimatorConditionMode.Equals, 0, "GestureRight");
            if (dominatedBlink)
            {
                fx.AddLayer("Permanent explicit open eyes"); var layers = fx.layers; layers[1].defaultWeight = 1; fx.layers = layers;
                var open = new AnimationClip { name = "Full Override open rest" };
                foreach (var path in new[] { "Front", "Back" }) AnimationUtility.SetEditorCurve(open, Binding("Blink", path), AnimationCurve.Constant(0, 12, 0));
                AssetDatabase.AddObjectToAsset(open, fx); var upper = layers[1].stateMachine;
                var state = upper.AddState("Open"); state.motion = open; state.writeDefaultValues = false; upper.defaultState = state;
            }
            SetFx(fixture.Source.AddComponent(DescriptorType()), fx);
            var vrm = ScriptableObject.CreateInstance<VRM10Object>(); var endpoint = ScriptableObject.CreateInstance<VRM10Expression>(); endpoint.name = "Explicit temporal endpoint";
            endpoint.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 0, .8f), new MorphTargetBinding("Back", 0, .8f) };
            vrm.Expression.CustomClips.Add(endpoint); AssetDatabase.CreateAsset(vrm, folder + "/Vrm.asset"); AssetDatabase.CreateAsset(endpoint, folder + "/Endpoint.asset");
            fixture.Source.AddComponent<Vrm10Instance>().Vrm = vrm;
            var blink = new BlinkExportOptions { Mode = BlinkExportMode.Manual };
            foreach (var sourceSkin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>())
                blink.Both.Add(new BlinkShapeBinding { Renderer = sourceSkin, Shape = "Blink", Weight = 100 });
            var meshBefore = EditorJsonUtility.ToJson(fixture.Mesh); var controllerBefore = EditorJsonUtility.ToJson(fx);
            var idleBefore = EditorJsonUtility.ToJson(idleClip); var endpointBefore = EditorJsonUtility.ToJson(endpoint);
            var expected = Object.Instantiate(fixture.Source); Vrm10Instance imported = null;
            try
            {
                foreach (var behaviour in expected.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                var warnings = new List<string>();
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Temporal prepared neutral", "Tests", warnings, blinkOptions: blink);
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported.Vrm.Expression.CustomClips.Any(clip => clip.name == endpoint.name), Is.True);
                Assert.That(imported.Vrm.Expression.Blink, Is.Not.Null);
                foreach (var key in new[] { ExpressionKey.CreateCustom(endpoint.name), ExpressionKey.Blink })
                    foreach (var input in new[] { 0f, 1f, 0f })
                    {
                        imported.Runtime.Expression.SetWeight(key, input); imported.Runtime.Process();
                        foreach (var path in new[] { "Front", "Back" })
                        {
                            var reference = expected.transform.Find(path).GetComponent<SkinnedMeshRenderer>();
                            reference.SetBlendShapeWeight(0, input == 0 ? dominatedBlink ? 0 : 17 : key.Equals(ExpressionKey.Blink) ? 100 : 80);
                            reference.SetBlendShapeWeight(1, 50);
                            var output = imported.GetComponentsInChildren<SkinnedMeshRenderer>().Single(renderer => renderer.name == path);
                            var a = Vertices(reference); var b = Vertices(output); Assert.That(b.Length, Is.EqualTo(a.Length));
                            for (var vertex = 0; vertex < a.Length; vertex++) Assert.That(Vector3.Distance(a[vertex], b[vertex]), Is.LessThan(.0005f),
                                path + " / " + key + " / " + input + " / " + vertex);
                        }
                    }
                if (!dominatedBlink)
                {
                    var document = GlbDocument.Read(bytes);
                    var programs = ExpressionAnimationData.Read(((Dictionary<string, object>)document.Json["extensions"])[ExpressionAnimationData.Extension]);
                    var program = programs.Single(animation => animation.Expression.Contains(idleClip.name));
                    Assert.That(program.Loop, Is.True); Assert.That(program.Duration, Is.EqualTo(idleClip.length).Within(.001));
                    Assert.That(program.Channels.Count, Is.EqualTo(2));
                    foreach (var channel in program.Channels)
                    {
                        Assert.That(channel.Curve.Keys.Count, Is.EqualTo(4));
                        foreach (var time in new[] { 0d, 10d, 10.5d, 12d })
                            Assert.That(channel.Curve.Evaluate(time), Is.EqualTo(Varying("Delayed").Evaluate((float)time)).Within(.001));
                    }
                    Assert.That(warnings.Any(warning => warning.Contains("時間で変わる")), Is.True);
                }
                Assert.That(fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>().All(renderer => renderer.sharedMesh == fixture.Mesh &&
                    renderer.GetBlendShapeWeight(0) == (dominatedBlink ? 100 : 17) && renderer.GetBlendShapeWeight(1) == 35), Is.True);
                Assert.That(EditorJsonUtility.ToJson(fixture.Mesh), Is.EqualTo(meshBefore)); Assert.That(EditorJsonUtility.ToJson(fx), Is.EqualTo(controllerBefore));
                Assert.That(EditorJsonUtility.ToJson(idleClip), Is.EqualTo(idleBefore)); Assert.That(EditorJsonUtility.ToJson(endpoint), Is.EqualTo(endpointBefore));
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); Object.DestroyImmediate(expected); }
        }

        private static Vector3[] Vertices(SkinnedMeshRenderer renderer)
        {
            var baked = new Mesh();
            try { renderer.BakeMesh(baked, false); return baked.vertices.Select(renderer.transform.TransformPoint).ToArray(); }
            finally { Object.DestroyImmediate(baked); }
        }
    }
}
