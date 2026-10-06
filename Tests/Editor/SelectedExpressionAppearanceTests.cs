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
using F = VRVlog.LilToon.LilToonFullContract;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class SelectedExpressionAppearanceTests
    {
        private string folder;
        private GameObject avatar, dummy;
        private SkinnedMeshRenderer skin;
        private Mesh mesh;
        private Material material;
        private AnimationClip clip;
        private readonly List<Object> owned = new List<Object>();
        private static readonly Color PreparedColor = new Color(.25f, .5f, .75f, 1f);

        [SetUp]
        public void SetUp()
        {
            var shader = Shader.Find("lilToon");
            if (shader == null) Assert.Ignore("Install the real lilToon package.");
            var name = "__SelectedAppearance_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            avatar = new GameObject("Avatar", typeof(Animator));
            var body = new GameObject("Body", typeof(SkinnedMeshRenderer)); body.transform.SetParent(avatar.transform, false);
            dummy = new GameObject("Dummy"); dummy.transform.SetParent(avatar.transform, false);
            skin = body.GetComponent<SkinnedMeshRenderer>(); mesh = BaseShapeFixture.Create(); skin.sharedMesh = mesh;
            material = new Material(shader); material.SetColor("_Color2nd", PreparedColor); material.SetFloat("_Cutoff", .5f);
            skin.sharedMaterial = material;
            clip = new AnimationClip { name = "Mixed face" };
            Curve("Body", typeof(SkinnedMeshRenderer), "blendShape.Face size", AnimationCurve.Linear(0, 20, 1, 80));
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(avatar); Object.DestroyImmediate(mesh); Object.DestroyImmediate(material); Object.DestroyImmediate(clip);
            foreach (var item in owned) if (item != null) Object.DestroyImmediate(item);
            owned.Clear(); if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        private EditorCurveBinding Curve(string path, Type type, string property, AnimationCurve curve)
        {
            var binding = EditorCurveBinding.FloatCurve(path, type, property); AnimationUtility.SetEditorCurve(clip, binding, curve); return binding;
        }

        private EditorCurveBinding Appearance(string kind, float? changed = null)
        {
            switch (kind)
            {
                case "activation": return Curve("Dummy", typeof(GameObject), "m_IsActive", AnimationCurve.Constant(0, 1, changed ?? 1));
                case "enabled": return Curve("Body", typeof(SkinnedMeshRenderer), "m_Enabled", AnimationCurve.Constant(0, 1, changed ?? 1));
                case "color": return Curve("Body", typeof(SkinnedMeshRenderer), "material._Color2nd.r", AnimationCurve.Constant(0, 1, changed ?? PreparedColor.r));
                case "scalar": return Curve("Body", typeof(SkinnedMeshRenderer), "material._Cutoff", AnimationCurve.Constant(0, 1, changed ?? .5f));
                case "absent activation": return Curve("Absent", typeof(GameObject), "m_IsActive", AnimationCurve.Constant(0, 1, 1));
                case "absent material": return Curve("Absent", typeof(SkinnedMeshRenderer), "material._Color2nd.r", AnimationCurve.Constant(0, 1, PreparedColor.r));
                case "absent renderer": return Curve("Dummy", typeof(SkinnedMeshRenderer), "m_Enabled", AnimationCurve.Constant(0, 1, 1));
                default: throw new ArgumentException(kind);
            }
        }

        [TestCase("activation")]
        [TestCase("enabled")]
        [TestCase("color")]
        [TestCase("scalar")]
        [TestCase("absent activation")]
        [TestCase("absent material")]
        [TestCase("absent renderer")]
        public void UnchangedPreparedAppearanceKeepsOnlyAuthoredMorphAnimation(string kind)
        {
            var binding = Appearance(kind);
            var before = new[] { avatar, (Object)skin, material, mesh, clip }.Select(EditorJsonUtility.ToJson).ToArray();
            var entry = new VrChatExpressionMenu.Entry(); VrChatGestureExpressions.ReadClip(avatar, clip, entry);
            Assert.That(SelectedExpressionAppearance.IsUnchanged(avatar, clip, binding), Is.True);
            Assert.That(entry.Values.Single().Shape, Is.EqualTo("Face size"));
            Assert.That(entry.Values.Single().Weight, Is.EqualTo(20));
            Assert.That(entry.Animation.Single().Shape, Is.EqualTo("Face size"));
            Assert.That(entry.Animation.Single().Curve.Evaluate(1), Is.EqualTo(80));
            Assert.That(entry.Duration, Is.EqualTo(1));
            Assert.That(new[] { avatar, (Object)skin, material, mesh, clip }.Select(EditorJsonUtility.ToJson), Is.EqualTo(before));
        }

        [TestCase("activation")]
        [TestCase("enabled")]
        [TestCase("color")]
        [TestCase("scalar")]
        public void ARealAppearanceChangeStillRejectsTheWholeSelectedClip(string kind)
        {
            var binding = Appearance(kind, 0);
            Assert.That(SelectedExpressionAppearance.IsUnchanged(avatar, clip, binding), Is.False);
            Assert.Throws<InvalidOperationException>(() => VrChatGestureExpressions.ReadClip(avatar, clip, new VrChatExpressionMenu.Entry()));
        }

        [TestCase("color")]
        [TestCase("activation")]
        [TestCase("enabled")]
        public void AChangingAppearanceCurveIsNotCertifiedFromItsFirstKey(string kind)
        {
            var binding = Appearance(kind);
            var first = AnimationUtility.GetEditorCurve(clip, binding).keys[0].value;
            AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Linear(0, first, 1, 0));
            Assert.That(SelectedExpressionAppearance.IsUnchanged(avatar, clip, binding), Is.False);
            Assert.Throws<InvalidOperationException>(() => VrChatGestureExpressions.ReadPose(avatar, clip));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PropertyBlockPreventsMaterialNoOpProofEvenWhenStoredMaterialMatches(bool perSlot)
        {
            var binding = Appearance("color"); var block = new MaterialPropertyBlock();
            block.SetColor("_Color2nd", Color.red);
            if (perSlot) skin.SetPropertyBlock(block, 0); else skin.SetPropertyBlock(block);
            Assert.That(skin.HasPropertyBlock(), Is.True);
            Assert.That(SelectedExpressionAppearance.IsUnchanged(avatar, clip, binding), Is.False);
            Assert.Throws<InvalidOperationException>(() => VrChatGestureExpressions.ReadPose(avatar, clip));
        }

        [TestCase("equal")]
        [TestCase("different")]
        [TestCase("missing")]
        public void EverySharedMaterialSlotMustHaveTheSameDeclaredValue(string mode)
        {
            var binding = Appearance("color"); var second = new Material(material); owned.Add(second);
            if (mode == "different") second.SetColor("_Color2nd", Color.black);
            skin.sharedMaterials = new[] { material, mode == "missing" ? null : second };
            Assert.That(SelectedExpressionAppearance.IsUnchanged(avatar, clip, binding), Is.EqualTo(mode == "equal"));
        }

        [TestCase("NaN tangent")]
        [TestCase("overshoot")]
        [TestCase("event")]
        [TestCase("object swap")]
        public void NoOpCandidateCannotConcealCorruptOrUnsupportedClipData(string kind)
        {
            var binding = Appearance("color");
            if (kind == "event") AnimationUtility.SetAnimationEvents(clip, new[] { new AnimationEvent { functionName = "UnexpectedCallback", time = .5f } });
            else if (kind == "object swap") AnimationUtility.SetObjectReferenceCurve(clip,
                EditorCurveBinding.PPtrCurve("Body", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = material } });
            else
            {
                var curve = AnimationCurve.Constant(0, 1, PreparedColor.r); var keys = curve.keys;
                keys[0].outTangent = kind == "NaN tangent" ? float.NaN : 1;
                curve.keys = keys; AnimationUtility.SetEditorCurve(clip, binding, curve);
                var stored = AnimationUtility.GetEditorCurve(clip, binding).keys;
                if (kind == "NaN tangent")
                    Assert.That(stored.Length == 2 && float.IsNaN(stored[0].outTangent), Is.True,
                        "The fixture must retain an active NaN tangent before testing the production guard.");
            }
            Assert.Throws<InvalidOperationException>(() => VrChatGestureExpressions.ReadClip(avatar, clip, new VrChatExpressionMenu.Entry()));
        }

        [Test]
        public void AmbiguousPathsRemainHardEvenWhenBothTargetsHaveMatchingValues()
        {
            var duplicate = new GameObject("Dummy"); duplicate.transform.SetParent(avatar.transform, false);
            var binding = Appearance("activation");
            Assert.Throws<InvalidOperationException>(() => SelectedExpressionAppearance.IsUnchanged(avatar, clip, binding));
            Assert.Throws<InvalidOperationException>(() => VrChatGestureExpressions.ReadPose(avatar, clip));
        }

        [TestCase("transform")]
        [TestCase("collider")]
        [TestCase("missing shader property")]
        [TestCase("vector")]
        [TestCase("texture")]
        public void ExistingUnknownPropertiesAreNotAuthorizedByCoincidentValues(string kind)
        {
            EditorCurveBinding binding;
            if (kind == "transform") binding = Curve("Dummy", typeof(Transform), "m_LocalScale.x", AnimationCurve.Constant(0, 1, 1));
            else if (kind == "collider")
            { dummy.AddComponent<BoxCollider>(); binding = Curve("Dummy", typeof(BoxCollider), "m_Enabled", AnimationCurve.Constant(0, 1, 1)); }
            else binding = Curve("Body", typeof(SkinnedMeshRenderer), "material." + (kind == "vector" ? "_MainTex_ScrollRotate.x" :
                kind == "texture" ? "_MainTex" : "_NotDeclared"), AnimationCurve.Constant(0, 1, 0));
            Assert.That(SelectedExpressionAppearance.IsUnchanged(avatar, clip, binding), Is.False);
            Assert.Throws<InvalidOperationException>(() => VrChatGestureExpressions.ReadPose(avatar, clip));
        }

        [Test]
        public void UnusedEndpointTangentsDoNotInvalidateAnOtherwiseConstantNoOp()
        {
            var binding = Appearance("color"); var curve = AnimationUtility.GetEditorCurve(clip, binding); var keys = curve.keys;
            keys[0].inTangent = float.NaN; keys[1].outTangent = float.NaN; curve.keys = keys;
            AnimationUtility.SetEditorCurve(clip, binding, curve);
            Assert.That(SelectedExpressionAppearance.IsUnchanged(avatar, clip, binding), Is.True);
        }

        [TestCase("color")]
        [TestCase("activation")]
        public void NoOpProofUsesThePreparedCopyRatherThanAnEarlierAuthoringValue(string kind)
        {
            var binding = Appearance(kind); Assert.That(SelectedExpressionAppearance.IsUnchanged(avatar, clip, binding), Is.True);
            var prepared = Object.Instantiate(avatar); owned.Add(prepared);
            if (kind == "activation") prepared.transform.Find("Dummy").gameObject.SetActive(false);
            else
            {
                var changed = new Material(material); owned.Add(changed); changed.SetColor("_Color2nd", Color.black);
                prepared.transform.Find("Body").GetComponent<SkinnedMeshRenderer>().sharedMaterial = changed;
            }
            Assert.That(SelectedExpressionAppearance.IsUnchanged(prepared, clip, binding), Is.False);
            Assert.Throws<InvalidOperationException>(() => VrChatGestureExpressions.ReadPose(prepared, clip));
            Assert.That(SelectedExpressionAppearance.IsUnchanged(avatar, clip, binding), Is.True);
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void MatchingShaderColorChannelsKeepTheirNativeAppliedValue(bool useStoredColor, bool hdr)
        {
            var property = hdr ? "_EmissionColor" : "_Color2nd";
            material.SetColor(property, PreparedColor);
            var shader = material.shader;
            var colorIndex = shader.FindPropertyIndex(property);
            var componentIndex = shader.FindPropertyIndex(property + ".r");
            string Property(int index) => index < 0 ? "absent" : shader.GetPropertyName(index) + " / " + shader.GetPropertyType(index);
            var native = Object.Instantiate(avatar); owned.Add(native);
            var nativeSkin = native.transform.Find("Body").GetComponent<SkinnedMeshRenderer>();
            var nativeMaterial = new Material(material); owned.Add(nativeMaterial); nativeSkin.sharedMaterial = nativeMaterial;
            var before = EditorJsonUtility.ToJson(material);
            var stored = material.GetColor(property);
            var authored = useStoredColor ? stored : PreparedColor;
            foreach (var channel in "rgba")
                Curve("Body", typeof(SkinnedMeshRenderer), "material." + property + "." + channel,
                    AnimationCurve.Constant(0, 1, authored["rgba".IndexOf(channel)]));
            var diagnostics = "ColorSpace=" + QualitySettings.activeColorSpace + "; shader=" + shader.name +
                "; full=" + Property(colorIndex) + "; flags=" + shader.GetPropertyFlags(colorIndex) +
                "; component=" + Property(componentIndex) + "; stored=" + stored.ToString("R") +
                "; raw=" + material.GetVector(property).ToString("R") + "; declared linear=" + PreparedColor.linear.ToString("R") +
                "; declared linear-gamma=" + PreparedColor.linear.gamma.ToString("R") + "; stored linear=" + stored.linear.ToString("R") +
                "; propertyBlock=" + skin.HasPropertyBlock() + "; curves=" + string.Join("; ", AnimationUtility.GetCurveBindings(clip).Select(binding =>
                    binding.propertyName + "=" + string.Join(",", AnimationUtility.GetEditorCurve(clip, binding).keys.Select(key =>
                        key.value.ToString("R") + "/" + key.inTangent.ToString("R") + "/" + key.outTangent.ToString("R")))));
            TestContext.WriteLine(diagnostics);
            clip.SampleAnimation(native, .5f);
            var global = new MaterialPropertyBlock(); nativeSkin.GetPropertyBlock(global);
            var slot = new MaterialPropertyBlock(); nativeSkin.GetPropertyBlock(slot, 0);
            TestContext.WriteLine("native material=" + nativeSkin.sharedMaterial.GetColor(property).ToString("R") +
                "; global empty=" + global.isEmpty + "; global=" + global.GetColor(property).ToString("R") +
                "; global raw=" + global.GetVector(property).ToString("R") +
                "; slot empty=" + slot.isEmpty + "; slot=" + slot.GetColor(property).ToString("R"));
            Assert.That(nativeSkin.sharedMaterial.GetColor(property), Is.EqualTo(stored), diagnostics);
            Assert.That(global.isEmpty, Is.False, "The native clip must actually apply its material channels.");
            Assert.That(slot.isEmpty, Is.True);
            // Native material and PropertyBlock color APIs can each perform a
            // float gamma round trip. The production proof itself uses exact
            // canonical equality; this oracle measures the resulting color.
            var applied = global.GetColor(property);
            for (var channel = 0; channel < 4; channel++)
                Assert.That(applied[channel], Is.EqualTo(stored[channel]).Within(.000001f), diagnostics);
            Assert.That(EditorJsonUtility.ToJson(material), Is.EqualTo(before));
            foreach (var binding in AnimationUtility.GetCurveBindings(clip).Where(binding => binding.propertyName.StartsWith("material.", StringComparison.Ordinal)))
                Assert.That(SelectedExpressionAppearance.IsUnchanged(avatar, clip, binding), Is.True, diagnostics);
        }

        [TestCase("HDR")]
        [TestCase("alpha")]
        [TestCase("scalar")]
        public void ColorCanonicalizationDoesNotRelaxOtherPropertyDomains(string domain)
        {
            string property; float current;
            if (domain == "HDR")
            {
                material.SetColor("_EmissionColor", PreparedColor); property = "_EmissionColor.r";
                var index = material.shader.FindPropertyIndex("_EmissionColor");
                Assert.That((material.shader.GetPropertyFlags(index) & UnityEngine.Rendering.ShaderPropertyFlags.HDR) != 0, Is.True);
                current = material.GetColor("_EmissionColor").r;
            }
            else if (domain == "alpha")
            {
                material.SetColor("_Color2nd", new Color(.25f, .5f, .75f, .25f)); property = "_Color2nd.a";
                current = material.GetColor("_Color2nd").a;
            }
            else
            {
                material.SetFloat("_Cutoff", .25f); property = "_Cutoff"; current = material.GetFloat("_Cutoff");
            }
            var different = Mathf.LinearToGammaSpace(Mathf.GammaToLinearSpace(current));
            Assert.That(different, Is.Not.EqualTo(current), "The fixture must contain a real float difference.");
            var binding = Curve("Body", typeof(SkinnedMeshRenderer), "material." + property, AnimationCurve.Constant(0, 1, different));
            Assert.That(SelectedExpressionAppearance.IsUnchanged(avatar, clip, binding), Is.False);
            Assert.Throws<InvalidOperationException>(() => VrChatGestureExpressions.ReadPose(avatar, clip));
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task MixedLilToonGesturePreservesPreparedAppearanceAndMorphEndpointsAfterExport(bool fullLilToon, bool controlledLayer)
        {
            var descriptorType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor"))
                .FirstOrDefault(type => type != null);
            if (descriptorType == null) Assert.Ignore("Install the real VRChat SDK.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            Vrm10Instance imported = null; GameObject reference = null;
            try
            {
                fixture.Mesh.ClearBlendShapes(); fixture.Mesh.AddBlendShapeFrame("Face", 100,
                    Enumerable.Repeat(Vector3.right * .02f, fixture.Mesh.vertexCount).ToArray(), null, null);
                foreach (var renderer in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    renderer.sharedMaterial.shader = Shader.Find("lilToon"); renderer.sharedMaterial.SetColor("_Color2nd", PreparedColor);
                    renderer.sharedMaterial.SetFloat("_UseMain2ndTex", 1); renderer.SetBlendShapeWeight(0, 17);
                }
                var preparedColor = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()[0].sharedMaterial.GetColor("_Color2nd");
                var fx = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Mixed.controller"); fx.AddParameter("GestureLeft", AnimatorControllerParameterType.Int);
                var faceLayer = controlledLayer ? 4 : 0;
                if (controlledLayer)
                {
                    var controlType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAnimatorLayerControl"))
                        .FirstOrDefault(type => type != null);
                    if (controlType == null) Assert.Ignore("Install the real VRChat SDK layer controls.");
                    for (var index = 1; index <= faceLayer; index++) fx.AddLayer("Authored slot " + index);
                    var layerDefinitions = fx.layers;
                    for (var index = 0; index < faceLayer; index++)
                    {
                        var empty = new AnimationClip { name = "Empty slot " + index }; AssetDatabase.AddObjectToAsset(empty, fx);
                        var state = layerDefinitions[index].stateMachine.AddState("Prepared slot " + index);
                        state.motion = empty; state.writeDefaultValues = false; layerDefinitions[index].stateMachine.defaultState = state;
                        if (index != 0) continue;
                        var control = state.AddStateMachineBehaviour(controlType);
                        using var data = new SerializedObject(control);
                        var playable = data.FindProperty("playable"); var fxIndex = Array.IndexOf(playable.enumNames, "FX");
                        Assert.That(fxIndex, Is.GreaterThanOrEqualTo(0)); playable.enumValueIndex = fxIndex;
                        data.FindProperty("layer").intValue = faceLayer; data.FindProperty("goalWeight").floatValue = 1;
                        data.FindProperty("blendDuration").floatValue = 0; data.ApplyModifiedPropertiesWithoutUndo();
                    }
                    layerDefinitions[faceLayer].defaultWeight = 0; fx.layers = layerDefinitions;
                    Assert.That(fx.layers[faceLayer].defaultWeight, Is.Zero, "The face slot must depend on the SDK command to become active.");
                }
                AnimatorState State(string name, float weight, bool appearance)
                {
                    var motion = new AnimationClip { name = name };
                    foreach (var path in new[] { "Front", "Back" })
                    {
                        AnimationUtility.SetEditorCurve(motion, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape.Face"), AnimationCurve.Constant(0, 1, weight));
                        if (!appearance) continue;
                        foreach (var channel in "rgba") AnimationUtility.SetEditorCurve(motion,
                            EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "material._Color2nd." + channel),
                            AnimationCurve.Constant(0, 1, PreparedColor["rgba".IndexOf(channel)]));
                        AnimationUtility.SetEditorCurve(motion, EditorCurveBinding.FloatCurve(path, typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1));
                    }
                    AssetDatabase.AddObjectToAsset(motion, fx); var state = fx.layers[faceLayer].stateMachine.AddState(name);
                    state.motion = motion; state.writeDefaultValues = false; return state;
                }
                var idle = State("Rest", 17, false); var selected = State("Mixed selected", 75, true); fx.layers[faceLayer].stateMachine.defaultState = idle;
                var transition = idle.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
                transition.AddCondition(AnimatorConditionMode.Equals, 1, "GestureLeft");
                var descriptor = fixture.Source.AddComponent(descriptorType);
                using (var data = new SerializedObject(descriptor))
                {
                    data.FindProperty("customizeAnimationLayers").boolValue = true;
                    var layers = data.FindProperty("baseAnimationLayers"); layers.arraySize = 1;
                    var layer = layers.GetArrayElementAtIndex(0); var type = layer.FindPropertyRelative("type");
                    type.enumValueIndex = Array.IndexOf(type.enumNames, "FX"); layer.FindPropertyRelative("isDefault").boolValue = false;
                    layer.FindPropertyRelative("animatorController").objectReferenceValue = fx; data.ApplyModifiedPropertiesWithoutUndo();
                }
                var fingerprint = ExportSourceFingerprint.Compute(fixture.Source);
                var assets = AssetDatabase.LoadAllAssetsAtPath(folder + "/Mixed.controller").Concat(new Object[] { fixture.Mesh })
                    .Concat(fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>().SelectMany(renderer => renderer.sharedMaterials)).Distinct().ToArray();
                var before = assets.Select(EditorJsonUtility.ToJson).ToArray(); reference = Object.Instantiate(fixture.Source);
                foreach (var behaviour in reference.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                var warnings = new List<string>();
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Mixed appearance face", "Tests", warnings,
                    exporterVersion: fullLilToon ? "mixed-appearance-regression" : null, lilToonVersion: fullLilToon ? "2.3.4" : null,
                    blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                Assert.That(VrmMenuExpressions.CountRegistered(bytes), Is.EqualTo(1), string.Join("\n", warnings));
                if (fullLilToon)
                {
                    var glb = GlbDocument.Read(bytes); F.Validate(glb.Json, bytes.Length, glb.Binary.Length);
                    foreach (var item in F.List(F.Root(glb.Json), "materials").Select(F.Object))
                    {
                        var color = F.List(item, "values").Select(F.Object).Single(value => F.Text(value, "name") == "_Color2nd");
                        Assert.That(F.Vector(F.Get(color, "value")), Is.EqualTo(new[] { preparedColor.r, preparedColor.g, preparedColor.b, preparedColor.a }));
                    }
                }
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                var expression = imported.Vrm.Expression.CustomClips.Single(item => item.name.StartsWith("VRChat / ", StringComparison.Ordinal));
                var importedMaterials = imported.GetComponentsInChildren<SkinnedMeshRenderer>().SelectMany(renderer => renderer.sharedMaterials).Distinct().ToArray();
                var colorBefore = importedMaterials.Select(item => item.color).ToArray();
                foreach (var input in new[] { 0f, 1f, 0f })
                {
                    imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(expression.name), input); imported.Runtime.Process();
                    foreach (var path in new[] { "Front", "Back" })
                    {
                        var expected = reference.transform.Find(path).GetComponent<SkinnedMeshRenderer>(); expected.SetBlendShapeWeight(0, input == 0 ? 17 : 75);
                        var actual = imported.GetComponentsInChildren<SkinnedMeshRenderer>().Single(renderer => renderer.name == path);
                        Assert.That(actual.enabled && actual.gameObject.activeInHierarchy, Is.True);
                        var a = Vertices(expected); var b = Vertices(actual); Assert.That(b.Length, Is.EqualTo(a.Length));
                        for (var i = 0; i < a.Length; i++) Assert.That(Vector3.Distance(a[i], b[i]), Is.LessThan(.0005f), path + " / " + input);
                    }
                    Assert.That(importedMaterials.Select(item => item.color), Is.EqualTo(colorBefore));
                }
                Assert.That(assets.Select(EditorJsonUtility.ToJson), Is.EqualTo(before));
                Assert.That(ExportSourceFingerprint.Compute(fixture.Source), Is.EqualTo(fingerprint));
                Assert.That(fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>().All(renderer => renderer.GetBlendShapeWeight(0) == 17), Is.True);
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                if (reference != null) Object.DestroyImmediate(reference);
            }
        }

        private static Vector3[] Vertices(SkinnedMeshRenderer renderer)
        {
            var result = new Mesh();
            try { renderer.BakeMesh(result, false); return result.vertices.Select(renderer.transform.TransformPoint).ToArray(); }
            finally { Object.DestroyImmediate(result); }
        }
    }
}
