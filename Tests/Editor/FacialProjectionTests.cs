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
    // A merged renderer is deliberately named Body. Opaque shape names and a
    // disconnected head-skinned accessory prevent name/renderer/bone shortcuts.
    public sealed class FacialProjectionTests
    {
        private AttachmentConnectionTests.Fixture rig;
        private GameObject avatar, shoes;
        private SkinnedMeshRenderer skin;
        private Mesh mesh;
        private Material material;
        private AnimatorController controller;
        private string folder;
        private static readonly string[] Shapes = { "seed", "channel-1", "channel-2", "channel-3", "channel-4" };

        [SetUp]
        public void SetUp()
        {
            var descriptorType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor"))
                .FirstOrDefault(t => t != null);
            if (descriptorType == null || Shader.Find("lilToon") == null) Assert.Ignore("Install the real VRChat SDK and lilToon.");
            rig = new AttachmentConnectionTests.Fixture(); avatar = rig.Source;
            foreach (var renderer in avatar.GetComponentsInChildren<SkinnedMeshRenderer>()) Object.DestroyImmediate(renderer.gameObject);
            skin = new GameObject("Body", typeof(SkinnedMeshRenderer)).GetComponent<SkinnedMeshRenderer>();
            skin.transform.SetParent(avatar.transform, false);
            var animator = avatar.GetComponent<Animator>();
            skin.bones = new[] { animator.GetBoneTransform(HumanBodyBones.Head), animator.GetBoneTransform(HumanBodyBones.Hips) };
            skin.rootBone = skin.bones[1];
            mesh = new Mesh { name = "Face, torso and separate head accessory" };
            mesh.vertices = new[] { new Vector3(-.1f, 1.7f, .1f), new Vector3(.1f, 1.7f, .1f), new Vector3(0, 1.9f, .1f),
                new Vector3(-.2f, 1, 0), new Vector3(.2f, 1, 0), new Vector3(-.1f, 2, 0), new Vector3(.1f, 2, 0), new Vector3(0, 2.2f, 0) };
            mesh.triangles = new[] { 0, 1, 2, 2, 3, 4, 5, 6, 7 };
            mesh.normals = Enumerable.Repeat(Vector3.forward, 8).ToArray(); mesh.uv = new Vector2[8];
            mesh.boneWeights = Enumerable.Range(0, 8).Select(i => new BoneWeight { boneIndex0 = i == 3 || i == 4 ? 1 : 0, weight0 = 1 }).ToArray();
            mesh.bindposes = skin.bones.Select(b => b.worldToLocalMatrix).ToArray();
            void Shape(string name, params int[] vertices)
            {
                var delta = new Vector3[8]; foreach (var vertex in vertices) delta[vertex] = Vector3.right * .03f;
                mesh.AddBlendShapeFrame(name, 100, delta, null, null);
            }
            Shape(Shapes[0], 0); Shape(Shapes[1], 2); Shape(Shapes[2], 3); Shape(Shapes[3], 5); Shape(Shapes[4], 1, 4);
            skin.sharedMesh = mesh;
            material = new Material(Shader.Find("lilToon")); material.SetColor("_Color2nd", new Color(.25f, .5f, .75f, 1));
            skin.sharedMaterial = material;
            for (var i = 1; i < Shapes.Length; i++) skin.SetBlendShapeWeight(i, i * 7);
            shoes = new GameObject("Shoes_Sneakers"); shoes.transform.SetParent(avatar.transform, false); shoes.SetActive(false);
            var descriptor = avatar.AddComponent(descriptorType);
            descriptorType.GetField("VisemeSkinnedMesh").SetValue(descriptor, skin);
            descriptorType.GetField("VisemeBlendShapes").SetValue(descriptor, new[] { Shapes[0] });
            var name = "__FacialProjection_" + Guid.NewGuid().ToString("N"); AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            controller.AddParameter("FacialSet", AnimatorControllerParameterType.Int);
        }

        [TearDown]
        public void TearDown()
        {
            rig?.Dispose(); Object.DestroyImmediate(mesh); Object.DestroyImmediate(material);
            if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        private AnimationClip Clip(string name, float weight, bool appearance, bool moving = false)
        {
            var clip = new AnimationClip { name = name }; AssetDatabase.AddObjectToAsset(clip, controller);
            foreach (var shape in Shapes.Skip(1)) AnimationUtility.SetEditorCurve(clip, Binding(shape), moving
                ? AnimationCurve.Linear(0, weight, 1, weight + 10) : AnimationCurve.Constant(0, 1, weight));
            if (appearance) AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "material._Color2nd.r"), AnimationCurve.Constant(0, 1, .9f));
            return clip;
        }

        private static EditorCurveBinding Binding(string shape) => EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape." + shape);
        private static AnimatorState State(AnimatorStateMachine machine, string name, AnimationClip clip, bool wd = false)
        {
            var state = machine.AddState(name); state.motion = clip; state.writeDefaultValues = wd; return state;
        }
        private static void Select(AnimatorState from, AnimatorState to)
        {
            var transition = from.AddTransition(to); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "FacialSet");
        }
        private VrChatExpressionMenu.Source Metadata() => new VrChatExpressionMenu.Source { Controller = controller };

        // Independent native witness: the rejected effects must really execute;
        // asserting only the exporter would permit an empty/constant false pass.
        private float[] Native(bool selected, out bool activated)
        {
            var clone = Object.Instantiate(avatar); var graph = PlayableGraph.Create("Face projection oracle");
            try
            {
                foreach (var behaviour in clone.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                var animator = clone.GetComponent<Animator>(); animator.enabled = true; animator.runtimeAnimatorController = null;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.fireEvents = false;
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                AnimationPlayableOutput.Create(graph, "Native", animator).SetSourcePlayable(playable);
                graph.Play(); graph.Evaluate(0);
                for (var i = 0; i < 120; i++) graph.Evaluate(1f / 60);
                if (selected) { playable.SetInteger("FacialSet", 1); for (var i = 0; i < 120; i++) graph.Evaluate(1f / 60); }
                var renderer = clone.transform.Find("Body").GetComponent<SkinnedMeshRenderer>();
                activated = clone.transform.Find("Shoes_Sneakers").gameObject.activeSelf;
                return Enumerable.Range(0, Shapes.Length).Select(renderer.GetBlendShapeWeight).ToArray();
            }
            finally { graph.Destroy(); Object.DestroyImmediate(clone); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SharedMaterialInputKeepsOnlyProvenFacialAnimation(bool appearanceInFaceClip)
        {
            var machine = controller.layers[0].stateMachine;
            var idle = State(machine, "Rest", Clip("Rest", 10, false)); machine.defaultState = idle;
            var motion = Clip("Selected", 70, appearanceInFaceClip, true); var selected = State(machine, "Selected", motion); Select(idle, selected);
            controller.AddLayer("Eye material"); var layers = controller.layers; layers[1].defaultWeight = 1; controller.layers = layers;
            var color = Clip("Eye__default", 0, true);
            foreach (var binding in AnimationUtility.GetCurveBindings(color).Where(b => b.propertyName.StartsWith("blendShape."))) AnimationUtility.SetEditorCurve(color, binding, null);
            var visual = State(layers[1].stateMachine, "Eye default", color); layers[1].stateMachine.defaultState = visual;
            var alternate = State(layers[1].stateMachine, "Eye selected", color); Select(visual, alternate);
            var before = ExportSourceFingerprint.Compute(avatar); var materialBefore = EditorJsonUtility.ToJson(material);
            var entry = new VrChatExpressionMenu.Entry();
            VrChatGestureExpressions.ReadClip(avatar, motion, entry);
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 0, false, metadata: Metadata(), sourceState: selected);
            Assert.That(entry.Values.Select(v => v.Shape), Is.EqualTo(new[] { Shapes[1] }));
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(70).Within(.01));
            Assert.That(entry.Animation.Select(v => v.Shape), Is.EqualTo(new[] { Shapes[1] }));
            Assert.That(entry.Animation.Single().Curve.Evaluate(.5), Is.EqualTo(75).Within(.01));
            Assert.That(entry.Messages, Does.Contain(ExporterLocalization.T(FacialProjectionScope.Notice)),
                "Face-only conversion must disclose the prepared material boundary.");
            Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before)); Assert.That(EditorJsonUtility.ToJson(material), Is.EqualTo(materialBefore));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MenuNativeSupportCanActivateShoesWithoutExportingTheirMorphs(bool writeDefaults)
        {
            var machine = controller.layers[0].stateMachine;
            var idle = State(machine, "Rest", Clip("Rest", 10, true), writeDefaults); machine.defaultState = idle;
            var selected = State(machine, "Selected", Clip("Selected", 75, true), writeDefaults); Select(idle, selected);
            controller.AddLayer("Reactive Component Defaults"); var layers = controller.layers; layers[1].defaultWeight = 1; controller.layers = layers;
            var support = new AnimationClip { name = "Reactive Component Defaults" }; AssetDatabase.AddObjectToAsset(support, controller);
            AnimationUtility.SetEditorCurve(support, EditorCurveBinding.FloatCurve("Shoes_Sneakers", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1));
            layers[1].stateMachine.defaultState = State(layers[1].stateMachine, "Defaults", support, true);
            var native = Native(true, out var activated); Assert.That(activated, Is.True);
            Assert.That(native.Skip(1), Is.All.EqualTo(75).Within(.01));
            var before = ExportSourceFingerprint.Compute(avatar);
            var values = VrChatExpressionSampler.SampleFixed(avatar, controller, new Dictionary<string, float>(),
                new Dictionary<string, float> { ["FacialSet"] = 1 }, metadata: Metadata());
            Assert.That(values.Select(v => v.Shape), Is.EqualTo(new[] { Shapes[1] }));
            Assert.That(values.Single().Weight, Is.EqualTo(native[1]).Within(.01));
            Assert.That(shoes.activeSelf, Is.False); Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(before));
        }

        [Test]
        public void FaceRendererDisappearanceRemainsUnsupported()
        {
            var motion = Clip("Hidden face", 75, true);
            AnimationUtility.SetEditorCurve(motion, EditorCurveBinding.FloatCurve("Body", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 0));
            Assert.Throws<InvalidOperationException>(() => VrChatGestureExpressions.ReadClip(avatar, motion, new VrChatExpressionMenu.Entry()));
        }

        [Test]
        public void ACanonicalBlinkNameOnAMergedHeadAccessoryIsNotFacialEvidence()
        {
            var delta = new Vector3[8]; delta[5] = Vector3.up * .04f;
            mesh.AddBlendShapeFrame("Blink", 100, delta, null, null);
            var motion = Clip("Face and accessory", 75, true);
            AnimationUtility.SetEditorCurve(motion, Binding("Blink"), AnimationCurve.Constant(0, 1, 90));
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, motion, entry);
            Assert.That(entry.Values.Select(v => v.Shape), Is.EqualTo(new[] { Shapes[1] }));
            Assert.That(entry.Messages.Any(m => m.Contains("blendShape.Blink")), Is.True);
        }

        [TestCase("normal")]
        [TestCase("tangent")]
        [TestCase("tiny position")]
        public void LaterFramesAndNonPositionBodyDeformationsCannotEnterTheFaceProjection(string kind)
        {
            var positions = new Vector3[8]; var normals = new Vector3[8]; var tangents = new Vector3[8];
            positions[2] = Vector3.right * .06f;
            if (kind == "normal") normals[3] = Vector3.right * .03f;
            else if (kind == "tangent") tangents[3] = Vector3.right * .03f;
            // Unity discards 1e-7 deltas when creating its sparse native shape
            // buffer. Use a small retained displacement and verify it below;
            // an absent native deformation cannot witness an exclusion rule.
            else positions[3] = Vector3.right * .00002f;
            var original = Enumerable.Range(0, Shapes.Length).Select(index =>
            {
                var delta = new Vector3[8]; mesh.GetBlendShapeFrameVertices(index, 0, delta, null, null); return delta;
            }).ToArray();
            mesh.ClearBlendShapes();
            for (var index = 0; index < Shapes.Length; index++)
            {
                mesh.AddBlendShapeFrame(Shapes[index], 100, original[index], null, null);
                if (index == 1) mesh.AddBlendShapeFrame(Shapes[index], 200, positions, normals, tangents);
            }
            var storedPositions = new Vector3[8]; var storedNormals = new Vector3[8]; var storedTangents = new Vector3[8];
            mesh.GetBlendShapeFrameVertices(1, 1, storedPositions, storedNormals, storedTangents);
            var storedBodyDelta = kind == "normal" ? storedNormals[3] : kind == "tangent" ? storedTangents[3] : storedPositions[3];
            Assert.That(storedBodyDelta.Equals(Vector3.zero), Is.False,
                "The native mesh must actually retain the body deformation: " + kind + " / " + storedBodyDelta.ToString("R"));
            var motion = Clip("Face and body", 75, true);
            AnimationUtility.SetEditorCurve(motion, Binding(Shapes[0]), AnimationCurve.Constant(0, 1, 70));
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, motion, entry);
            Assert.That(entry.Values.Select(v => v.Shape), Is.EqualTo(new[] { Shapes[0] }));
            Assert.That(entry.Messages.Any(m => m.Contains("blendShape." + Shapes[1])), Is.True);
        }

        [Test]
        public void AnIndependentWardrobeLayerDoesNotNarrowAnAlreadyValidPureMorphClip()
        {
            var motion = Clip("Existing pure morph", 70, false, true);
            var machine = controller.layers[0].stateMachine;
            var selected = State(machine, "Face", motion); machine.defaultState = selected;
            controller.AddLayer("Independent wardrobe"); var layers = controller.layers; layers[1].defaultWeight = 1; controller.layers = layers;
            var wardrobe = new AnimationClip { name = "Unrelated shoes" }; AssetDatabase.AddObjectToAsset(wardrobe, controller);
            AnimationUtility.SetEditorCurve(wardrobe, EditorCurveBinding.FloatCurve("Shoes_Sneakers", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1));
            layers[1].stateMachine.defaultState = State(layers[1].stateMachine, "Wardrobe", wardrobe);
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, motion, entry);
            VrChatExpressionSampler.ApplyPermanentOverrides(avatar, controller, entry, 0, false, metadata: Metadata(), sourceState: selected);
            Assert.That(entry.Values.Select(v => v.Shape), Is.EquivalentTo(Shapes.Skip(1)));
            Assert.That(entry.Animation.Select(v => v.Shape), Is.EquivalentTo(Shapes.Skip(1)));
            Assert.That(entry.Messages, Does.Not.Contain(ExporterLocalization.T(FacialProjectionScope.Notice)));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MissingDescriptorRendererRetainsStrictFallbackWithoutDereferencingUnityFakeNull(bool destroyed)
        {
            var descriptor = avatar.GetComponents<Component>().Single(c => c != null && c.GetType().FullName == "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            var field = descriptor.GetType().GetField("VisemeSkinnedMesh");
            var clone = Object.Instantiate(avatar);
            try
            {
                var cloneDescriptor = clone.GetComponent(descriptor.GetType());
                if (destroyed)
                {
                    var dummy = new GameObject("Removed descriptor renderer").AddComponent<SkinnedMeshRenderer>();
                    field.SetValue(cloneDescriptor, dummy); Object.DestroyImmediate(dummy.gameObject);
                }
                else
                {
                    using var serialized = new SerializedObject(cloneDescriptor);
                    serialized.FindProperty("VisemeSkinnedMesh").objectReferenceValue = null;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                var missing = field.GetValue(cloneDescriptor) as SkinnedMeshRenderer;
                Assert.That(missing == null, Is.True);
                Assert.That(ReferenceEquals(missing, null), Is.False, "Exercise a Unity fake-null reference, not an ordinary CLR null.");
                Assert.That(FacialProjectionScope.Create(clone), Is.Null);
                var motion = Clip("No facial identity proof", 75, true);
                Assert.Throws<InvalidOperationException>(() => VrChatGestureExpressions.ReadClip(clone, motion, new VrChatExpressionMenu.Entry()));
            }
            finally { Object.DestroyImmediate(clone); }
        }

        private void ConfigureDescriptorFx()
        {
            var descriptor = avatar.GetComponents<Component>().Single(c => c != null && c.GetType().FullName == "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            using var data = new SerializedObject(descriptor);
            data.FindProperty("customizeAnimationLayers").boolValue = true;
            var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = 1;
            var layer = layers.GetArrayElementAtIndex(0); var type = layer.FindPropertyRelative("type");
            type.enumValueIndex = Array.IndexOf(type.enumNames, "FX"); layer.FindPropertyRelative("isDefault").boolValue = false;
            layer.FindPropertyRelative("animatorController").objectReferenceValue = controller; data.ApplyModifiedPropertiesWithoutUndo();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ANewAnalysisRechecksChangedGeometryAfterBothSuccessfulAndFailedFaceProofs(bool initiallyValid)
        {
            controller.AddParameter("GestureLeft", AnimatorControllerParameterType.Int);
            var machine = controller.layers[0].stateMachine;
            var idle = State(machine, "Rest", Clip("Rest", 10, true)); machine.defaultState = idle;
            var selected = State(machine, "Gesture", Clip("Gesture", 75, true));
            var transition = idle.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "GestureLeft"); ConfigureDescriptorFx();
            void SetSeed(bool valid)
            {
                mesh.ClearBlendShapes();
                var vertices = new[] { new[] { valid ? 0 : 3 }, new[] { 2 }, new[] { 3 }, new[] { 5 }, new[] { 1, 4 } };
                for (var i = 0; i < Shapes.Length; i++)
                {
                    var delta = new Vector3[8]; foreach (var index in vertices[i]) delta[index] = Vector3.right * .03f;
                    mesh.AddBlendShapeFrame(Shapes[i], 100, delta, null, null);
                }
            }
            foreach (var valid in new[] { initiallyValid, !initiallyValid })
            {
                SetSeed(valid);
                var result = VrChatExpressionSampler.Analyze(avatar);
                Assert.That(result.FacialProjectionSession, Is.Null, "Geometry proof must not escape the Analyze snapshot.");
                var entries = result.Entries.Where(entry => entry.Name.EndsWith(" / Gesture", StringComparison.Ordinal)).ToArray();
                Assert.That(entries.Length, Is.GreaterThan(0));
                Assert.That(entries.Any(entry => entry.Error == null && entry.Values.Any(value => value.Shape == Shapes[1])), Is.EqualTo(valid));
            }
        }

        [Test]
        public async Task ExportedProjectedFaceReimportsWithPreparedOutfitAndReturnsExactlyToRest()
        {
            controller.AddParameter("GestureLeft", AnimatorControllerParameterType.Int);
            var machine = controller.layers[0].stateMachine;
            var idle = State(machine, "Rest", Clip("Rest", 10, true)); machine.defaultState = idle;
            var selected = State(machine, "Gesture", Clip("Gesture", 75, true));
            var transition = idle.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "GestureLeft");
            ConfigureDescriptorFx();
            var fingerprint = ExportSourceFingerprint.Compute(avatar); var reference = Object.Instantiate(avatar); Vrm10Instance imported = null;
            try
            {
                foreach (var behaviour in reference.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                var warnings = new List<string>();
                var bytes = UniVrmOneClickExporter.Export(avatar, "Projected face", "Tests", warnings,
                    exporterVersion: "face-projection-regression", lilToonVersion: "2.3.4", blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                Assert.That(VrmMenuExpressions.CountRegistered(bytes), Is.GreaterThan(0), string.Join("\n", warnings));
                Assert.That(warnings.Any(message => message.Contains(ExporterLocalization.T(FacialProjectionScope.Notice))), Is.True, string.Join("\n", warnings));
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                var expression = imported.Vrm.Expression.CustomClips.Single(item => item.name.StartsWith("VRChat / ", StringComparison.Ordinal) && item.name.EndsWith(" / Gesture", StringComparison.Ordinal));
                var actual = imported.GetComponentsInChildren<SkinnedMeshRenderer>().Single(renderer => renderer.name == "Body");
                var expected = reference.transform.Find("Body").GetComponent<SkinnedMeshRenderer>();
                var materials = actual.sharedMaterials.Select(EditorJsonUtility.ToJson).ToArray();
                Vector3[] Vertices(SkinnedMeshRenderer renderer)
                {
                    var baked = new Mesh();
                    try { renderer.BakeMesh(baked, false); return baked.vertices.Select(renderer.transform.TransformPoint).ToArray(); }
                    finally { Object.DestroyImmediate(baked); }
                }
                foreach (var input in new[] { 0f, 1f, 0f })
                {
                    imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(expression.name), input); imported.Runtime.Process();
                    expected.SetBlendShapeWeight(1, input == 0 ? 7 : 75);
                    var a = Vertices(expected); var b = Vertices(actual); Assert.That(b.Length, Is.EqualTo(a.Length));
                    for (var i = 0; i < a.Length; i++) Assert.That(Vector3.Distance(a[i], b[i]), Is.LessThan(.0005f), "vertex " + i + " / input " + input);
                    Assert.That(actual.sharedMaterials.Select(EditorJsonUtility.ToJson), Is.EqualTo(materials));
                }
                Assert.That(ExportSourceFingerprint.Compute(avatar), Is.EqualTo(fingerprint));
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); Object.DestroyImmediate(reference); }
        }
    }
}
