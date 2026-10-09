using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UniGLTF;
using UniVRM10;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class ExperimentalExpressionCaptureTests
    {
        [Test]
        public async Task RecordedFacesRetainAuthoredMouthAndGazePresetsAfterReload()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = Shader.Find("lilToon");
            fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>().SetBlendShapeWeight(0, 0);
            var presets = new[] { ExpressionPreset.aa, ExpressionPreset.ih, ExpressionPreset.ou, ExpressionPreset.ee, ExpressionPreset.oh,
                ExpressionPreset.lookUp, ExpressionPreset.lookDown, ExpressionPreset.lookLeft, ExpressionPreset.lookRight };
            var authored = ScriptableObject.CreateInstance<VRM10Object>();
            var clips = presets.Select(preset => {
                var clip = ScriptableObject.CreateInstance<VRM10Expression>();
                clip.name = "Authored " + preset; clip.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 0, .8f) };
                authored.Expression.AddClip(preset, clip); return clip;
            }).ToArray();
            Vrm10Instance imported = null;
            try
            {
                fixture.Source.AddComponent<Vrm10Instance>().Vrm = authored;
                var sourceBefore = ExportSourceFingerprint.Compute(fixture.Source);
                using var session = new ExperimentalExpressionCaptureSession(fixture.Source, false);
                session.SetWeight(0, 0, 65); session.Capture("Selected face");
                imported = await Vrm10.LoadBytesAsync(session.Export("Functional presets", "Tests", blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None }),
                    canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                var reloaded = imported.Vrm.Expression.Clips.ToDictionary(pair => pair.Preset, pair => pair.Clip);
                foreach (var preset in presets)
                {
                    Assert.That(reloaded.ContainsKey(preset), Is.True, preset.ToString());
                    Assert.That(reloaded[preset].MorphTargetBindings, Is.Not.Empty);
                }
                var skin = imported.transform.Find("Front").GetComponent<SkinnedMeshRenderer>();
                using var baked = new BlinkTestMesh();
                imported.Runtime.Expression.SetWeight(ExpressionKey.Aa, 0); imported.Runtime.Process(); skin.BakeMesh(baked.Mesh); var before = baked.Mesh.vertices;
                imported.Runtime.Expression.SetWeight(ExpressionKey.Aa, 1); imported.Runtime.Process(); skin.BakeMesh(baked.Mesh);
                Assert.That(baked.Mesh.vertices.Zip(before, (a,b) => (a-b).sqrMagnitude).Max(), Is.GreaterThan(1e-8f));
                imported.Runtime.Expression.SetWeight(ExpressionKey.Aa, 0); imported.Runtime.Process(); skin.BakeMesh(baked.Mesh);
                Assert.That(baked.Mesh.vertices.Zip(before, (a,b) => (a-b).sqrMagnitude).Max(), Is.LessThan(1e-8f));
                Assert.That(ExportSourceFingerprint.Compute(fixture.Source), Is.EqualTo(sourceBefore));
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); foreach (var clip in clips) Object.DestroyImmediate(clip); Object.DestroyImmediate(authored); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ManualBlinkConfigurationFromPrimaryWindowSurvivesModelAndRecordedFaceSave(bool recordFace)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = Shader.Find("lilToon");
            var sourceSkin = fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>(); sourceSkin.SetBlendShapeWeight(0, 0);
            var sourceBefore = ExportSourceFingerprint.Compute(fixture.Source);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var type = typeof(ExperimentalExpressionCaptureWindow);
            var window = ScriptableObject.CreateInstance<ExperimentalExpressionCaptureWindow>();
            var path = Path.Combine(Path.GetTempPath(), "vrvlog-manual-blink-" + Guid.NewGuid().ToString("N") + ".vrm");
            var clip = new AnimationClip { name = "Selected face" }; Vrm10Instance imported = null;
            try
            {
                type.GetField("source", flags).SetValue(window, fixture.Source);
                type.GetField("author", flags).SetValue(window, "Tests");
                type.GetMethod("Prepare", flags).Invoke(window, null);
                if (recordFace)
                {
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail"), AnimationCurve.Constant(0, 1, 65));
                    type.GetMethod("AddClip", flags).Invoke(window, new object[] { clip });
                }
                type.GetMethod("ShowBlinkConfiguration", flags).Invoke(window, null);
                var dialog = Resources.FindObjectsOfTypeAll<BlinkConfigurationWindow>().Single();
                dialog.Options.SelectMode(BlinkExportMode.Manual);
                dialog.Options.Both[0].Renderer = sourceSkin; dialog.Options.Both[0].Shape = sourceSkin.sharedMesh.GetBlendShapeName(0); dialog.Options.Both[0].Weight = 80;
                dialog.ApplySettings();
                window.SaveVrm(path);
                imported = await Vrm10.LoadBytesAsync(File.ReadAllBytes(path), canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported.Vrm.Expression.Blink?.MorphTargetBindings, Is.Not.Empty);
                var skin = imported.transform.Find("Front").GetComponent<SkinnedMeshRenderer>();
                using var baked = new BlinkTestMesh();
                imported.Runtime.Expression.SetWeight(ExpressionKey.Blink, 0); imported.Runtime.Process(); skin.BakeMesh(baked.Mesh); var before = baked.Mesh.vertices;
                imported.Runtime.Expression.SetWeight(ExpressionKey.Blink, 1); imported.Runtime.Process(); skin.BakeMesh(baked.Mesh);
                Assert.That(baked.Mesh.vertices.Zip(before, (a,b) => (a-b).sqrMagnitude).Max(), Is.GreaterThan(1e-8f));
                imported.Runtime.Expression.SetWeight(ExpressionKey.Blink, 0); imported.Runtime.Process(); skin.BakeMesh(baked.Mesh);
                Assert.That(baked.Mesh.vertices.Zip(before, (a,b) => (a-b).sqrMagnitude).Max(), Is.LessThan(1e-8f));
                Assert.That(ExportSourceFingerprint.Compute(fixture.Source), Is.EqualTo(sourceBefore));
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); Object.DestroyImmediate(window); Object.DestroyImmediate(clip); if (File.Exists(path)) File.Delete(path); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ReadWriteDisabledFaceMeshesCanBePreparedExportedAndReimported(bool recordFace)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var meshes = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>().Select(skin => skin.sharedMesh).Distinct().ToArray();
            foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = Shader.Find("lilToon");
            foreach (var mesh in meshes) mesh.UploadMeshData(true);
            Assert.That(meshes.All(mesh => !mesh.isReadable), Is.True);
            var sourceBefore = ExportSourceFingerprint.Compute(fixture.Source);
            Vrm10Instance imported = null;
            try
            {
                using var session = new ExperimentalExpressionCaptureSession(fixture.Source, replayInstalledDefaults: false);
                if (recordFace) { session.SetWeight(0, 0, 65); session.Capture("Read only face"); }
                var bytes = session.Export("Read only meshes", "Tests", blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported, Is.Not.Null);
                if (recordFace) Assert.That(imported.Vrm.Expression.CustomClips.Any(face => face.name == "VRChat / 記録 / Read only face"), Is.True);
                Assert.That(meshes.All(mesh => !mesh.isReadable), Is.True);
                Assert.That(ExportSourceFingerprint.Compute(fixture.Source), Is.EqualTo(sourceBefore));
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); }
        }

        [TestCase(.25f)]
        [TestCase(2f)]
        public void EditedClipEndpointIsRefreshedForBothPreviewAndSaving(float newLength)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var type = typeof(ExperimentalExpressionCaptureWindow);
            var window = ScriptableObject.CreateInstance<ExperimentalExpressionCaptureWindow>();
            var clip = new AnimationClip { name = "Edited face" };
            var binding = EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail");
            try
            {
                AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Linear(0, 10, 1, 70));
                type.GetField("source", flags).SetValue(window, fixture.Source);
                type.GetMethod("Prepare", flags).Invoke(window, null);
                type.GetMethod("AddClip", flags).Invoke(window, new object[] { clip });
                var inputs = (System.Collections.Generic.List<ExperimentalExpressionCaptureSession.ClipInput>)type.GetField("clipInputs", flags).GetValue(window);
                AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Linear(0, 10, newLength, 95));
                type.GetMethod("PreviewClipInput", flags).Invoke(window, new object[] { inputs.Single() });
                var session = (ExperimentalExpressionCaptureSession)type.GetField("session", flags).GetValue(window);
                Assert.That(session.Channels.First(channel => channel.Path == "Front").Renderer.GetBlendShapeWeight(0), Is.EqualTo(95));
                inputs.Single().Time = 1; // Saving must refresh independently of preview.
                type.GetMethod("RecordSelectedClips", flags).Invoke(window, null);
                Assert.That(session.Expressions.Single().Rows.First(row => row.Path == "Front").Weights[0], Is.EqualTo(95));
                Assert.That(inputs.Single().Time, Is.EqualTo(newLength));
            }
            finally { Object.DestroyImmediate(window); Object.DestroyImmediate(clip); }
        }

        [TestCase("descriptor", false)]
        [TestCase("descriptor", true)]
        [TestCase("vrm", false)]
        [TestCase("vrm", true)]
        public async Task ExplicitBlinkMetadataSurvivesModelAndRecordedFaceExport(string configuration, bool recordFace)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = Shader.Find("lilToon");
            var authored = ScriptableObject.CreateInstance<VRM10Object>();
            var blink = ScriptableObject.CreateInstance<VRM10Expression>();
            Vrm10Instance imported = null;
            try
            {
                var sourceSkin = fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>();
                sourceSkin.SetBlendShapeWeight(0, 0);
                if (configuration == "vrm")
                {
                    blink.name = "Authored blink";
                    blink.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 0, 1f) };
                    authored.Expression.Blink = blink; fixture.Source.AddComponent<Vrm10Instance>().Vrm = authored;
                }
                else
                {
                    var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor", false)).FirstOrDefault(value => value != null);
                    Assert.That(type, Is.Not.Null, "Run with the pinned actual VRChat SDK.");
                    var descriptor = fixture.Source.AddComponent(type);
                    type.GetField("enableEyeLook").SetValue(descriptor, true);
                    var field = type.GetField("customEyeLookSettings"); var settings = field.GetValue(descriptor);
                    var lidType = settings.GetType().GetField("eyelidType"); lidType.SetValue(settings, Enum.Parse(lidType.FieldType, "Blendshapes"));
                    settings.GetType().GetField("eyelidsSkinnedMesh").SetValue(settings, sourceSkin);
                    settings.GetType().GetField("eyelidsBlendshapes").SetValue(settings, new[] { 0, -1, -1 });
                    field.SetValue(descriptor, settings);
                }
                var sourceBefore = ExportSourceFingerprint.Compute(fixture.Source);
                using var session = new ExperimentalExpressionCaptureSession(fixture.Source, replayInstalledDefaults: false);
                if (recordFace) { session.SetWeight(0, 0, 65); session.Capture("Selected face"); }
                var bytes = session.Export("Explicit blink", "Tests");
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported.Vrm.Expression.Blink, Is.Not.Null);
                Assert.That(imported.Vrm.Expression.Blink.MorphTargetBindings, Is.Not.Empty);
                var outputSkin = imported.transform.Find("Front").GetComponent<SkinnedMeshRenderer>();
                var before = new Vector3[outputSkin.sharedMesh.vertexCount];
                using (var baked = new BlinkTestMesh())
                {
                    imported.Runtime.Expression.SetWeight(ExpressionKey.Blink, 0); imported.Runtime.Process(); outputSkin.BakeMesh(baked.Mesh); before = baked.Mesh.vertices;
                    imported.Runtime.Expression.SetWeight(ExpressionKey.Blink, 1); imported.Runtime.Process(); outputSkin.BakeMesh(baked.Mesh);
                    Assert.That(baked.Mesh.vertices.Zip(before, (a,b) => (a-b).sqrMagnitude).Max(), Is.GreaterThan(1e-8f), "Configured eyelids must still deform after VRM reload.");
                    imported.Runtime.Expression.SetWeight(ExpressionKey.Blink, 0); imported.Runtime.Process(); outputSkin.BakeMesh(baked.Mesh);
                    Assert.That(baked.Mesh.vertices.Zip(before, (a,b) => (a-b).sqrMagnitude).Max(), Is.LessThan(1e-8f));
                }
                Assert.That(ExportSourceFingerprint.Compute(fixture.Source), Is.EqualTo(sourceBefore));
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); Object.DestroyImmediate(blink); Object.DestroyImmediate(authored); }
        }

        sealed class BlinkTestMesh : IDisposable
        {
            internal readonly Mesh Mesh = new Mesh();
            public void Dispose() => Object.DestroyImmediate(Mesh);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ModelOnlyExportReimportsWithoutRequiringFacialFiles(bool withAuthoredExpression)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                skin.sharedMaterial.shader = Shader.Find("lilToon");
                skin.sharedMaterial.SetFloat("_UseAudioLink", 1);
            }
            var authored = ScriptableObject.CreateInstance<VRM10Object>();
            var face = ScriptableObject.CreateInstance<VRM10Expression>();
            Vrm10Instance imported = null;
            try
            {
                if (withAuthoredExpression)
                {
                    face.name = "Authored face";
                    face.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 0, .8f) };
                    authored.Expression.CustomClips.Add(face);
                    fixture.Source.AddComponent<Vrm10Instance>().Vrm = authored;
                }
                else fixture.Mesh.ClearBlendShapes();
                using var session = new ExperimentalExpressionCaptureSession(fixture.Source, replayInstalledDefaults: false);
                Assert.That(session.Expressions, Is.Empty);
                var before = ExportSourceFingerprint.Compute(fixture.Source);
                var bytes = session.Export("Model only", "Tests", blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported, Is.Not.Null);
                Assert.That(imported.Vrm.Meta.Authors, Does.Contain("Tests"));
                Assert.That(imported.Vrm.Expression.CustomClips.Any(clip => clip.name == "Authored face"), Is.EqualTo(withAuthoredExpression));
                Assert.That(imported.GetComponentsInChildren<Renderer>(true).SelectMany(renderer => renderer.sharedMaterials)
                    .Where(material => material != null && material.HasProperty("_UseAudioLink"))
                    .All(material => material.GetFloat("_UseAudioLink") == 0), Is.True);
                Assert.That(ExportSourceFingerprint.Compute(fixture.Source), Is.EqualTo(before));
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); Object.DestroyImmediate(face); Object.DestroyImmediate(authored); }
        }

        [Test]
        public void BatchClipValidationKeepsPerFileErrorsAndRejectsChangedSource()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var face = new AnimationClip(); var wrong = new AnimationClip();
            try
            {
                AnimationUtility.SetEditorCurve(face, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail"), AnimationCurve.Constant(0, 1, 75));
                AnimationUtility.SetEditorCurve(wrong, EditorCurveBinding.FloatCurve("Front", typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Constant(0, 1, 1));
                using var session = new ExperimentalExpressionCaptureSession(fixture.Source, replayInstalledDefaults: false);
                var weights = session.Channels.Select(channel => channel.Renderer.GetBlendShapeWeight(0)).ToArray();
                var errors = session.ClipErrors(new[] { face, wrong, face });
                Assert.That(errors.Count, Is.EqualTo(2)); Assert.That(errors[face], Is.Null); Assert.That(errors[wrong], Is.Not.Null);
                Assert.That(session.Channels.Select(channel => channel.Renderer.GetBlendShapeWeight(0)).ToArray(), Is.EqualTo(weights));
                var vertices = fixture.Mesh.vertices; vertices[0].x += .1f; fixture.Mesh.vertices = vertices;
                Assert.Throws<InvalidOperationException>(() => session.ClipErrors(new[] { face, wrong }));
            }
            finally { Object.DestroyImmediate(face); Object.DestroyImmediate(wrong); }
        }

        [TestCase("ja", "VRM書き出し", "VRMを保存")]
        [TestCase("en", "VRM Export", "Save VRM")]
        [TestCase("ko", "VRM 내보내기", "VRM 저장")]
        [TestCase("zh-Hans", "VRM 导出", "保存 VRM")]
        [TestCase("zh-Hant", "VRM 匯出", "儲存 VRM")]
        public void PrimaryWindowUsesInstalledLocaleTables(string locale, string title, string save)
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var localeField = typeof(ExporterLocalization).GetField("_locale", flags);
            var messagesField = typeof(ExporterLocalization).GetField("_messages", flags);
            var previousLocale = localeField.GetValue(null); var previousMessages = messagesField.GetValue(null);
            ExperimentalExpressionCaptureWindow window = null;
            try
            {
                localeField.SetValue(null, locale); messagesField.SetValue(null, null);
                ExperimentalExpressionCaptureWindow.Open(); window = Resources.FindObjectsOfTypeAll<ExperimentalExpressionCaptureWindow>().Single();
                Assert.That(window.titleContent.text, Is.EqualTo(title));
                Assert.That(ExporterLocalization.T("VRMを保存"), Is.EqualTo(save));
                Assert.That(ExporterLocalization.T("候補ファイル"), Is.Not.Empty);
                if (locale != "ja") Assert.That(ExporterLocalization.T("候補ファイル"), Is.Not.EqualTo("候補ファイル"));
            }
            finally { window?.Close(); localeField.SetValue(null, previousLocale); messagesField.SetValue(null, previousMessages); }
        }

        [Test]
        public void AudioLinkSuppressionCoversSharedInactiveAndNewMaterialsWithoutEditingTheSource()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var materials = new System.Collections.Generic.List<Material>();
            var first = new Material(Shader.Find("lilToon"));
            var generated = new Material(first);
            try
            {
                first.SetFloat("_UseAudioLink", 1); first.SetFloat("_AudioLink2Vertex", 1);
                generated.shader = Shader.Find("_lil/lilToonMulti"); Assert.That(generated.shader, Is.Not.Null);
                generated.SetFloat("_UseAudioLink", 1); generated.SetFloat("_AudioLink2Emission", 1);
                generated.EnableKeyword("_MAPPING_6_FRAMES_LAYOUT"); generated.EnableKeyword("_SUNDISK_HIGH_QUALITY");
                foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial = first;
                foreach (var skin in fixture.Skins) skin.sharedMaterials = new[] { first, first, null };
                fixture.Skins[1].gameObject.SetActive(false);
                UniVrmOneClickExporter.DisableAudioLinkOnCopy(fixture.Copy, materials);
                Assert.That(materials.Count, Is.EqualTo(1));
                foreach (var skin in fixture.Skins)
                {
                    Assert.That(skin.sharedMaterials[0], Is.SameAs(materials[0]));
                    Assert.That(skin.sharedMaterials[1], Is.SameAs(materials[0]));
                    Assert.That(skin.sharedMaterials[2], Is.Null);
                    Assert.That(skin.sharedMaterial.GetFloat("_UseAudioLink"), Is.Zero);
                    Assert.That(skin.sharedMaterial.GetFloat("_AudioLink2Vertex"), Is.Zero);
                }
                // Simulate an authoring pass assigning a fresh material, and
                // another renderer restoring its source material reference.
                fixture.Skins[0].sharedMaterial = generated;
                fixture.Skins[1].sharedMaterial = first;
                UniVrmOneClickExporter.DisableAudioLinkOnCopy(fixture.Copy, materials);
                Assert.That(materials.Count, Is.EqualTo(3));
                Assert.That(fixture.Skins.All(skin => skin.sharedMaterial.GetFloat("_UseAudioLink") == 0), Is.True);
                Assert.That(fixture.Skins[0].sharedMaterial.GetFloat("_AudioLink2Emission"), Is.Zero);
                Assert.That(fixture.Skins[0].sharedMaterial.IsKeywordEnabled("_MAPPING_6_FRAMES_LAYOUT"), Is.False);
                Assert.That(fixture.Skins[0].sharedMaterial.IsKeywordEnabled("_SUNDISK_HIGH_QUALITY"), Is.False);
                UniVrmOneClickExporter.DisableAudioLinkOnCopy(fixture.Copy, materials);
                Assert.That(materials.Count, Is.EqualTo(3), "Repeated passes must not create extra material copies.");
                Assert.That(first.GetFloat("_UseAudioLink"), Is.EqualTo(1));
                Assert.That(first.GetFloat("_AudioLink2Vertex"), Is.EqualTo(1));
                Assert.That(generated.GetFloat("_UseAudioLink"), Is.EqualTo(1));
                Assert.That(generated.IsKeywordEnabled("_MAPPING_6_FRAMES_LAYOUT"), Is.True);
                // A stale Multi keyword must also be disabled when all toggles
                // already say off; the original still retains its keyword.
                generated.SetFloat("_UseAudioLink", 0); generated.SetFloat("_AudioLink2Emission", 0);
                fixture.Skins[0].sharedMaterial = generated;
                UniVrmOneClickExporter.DisableAudioLinkOnCopy(fixture.Copy, materials);
                Assert.That(materials.Count, Is.EqualTo(4));
                Assert.That(fixture.Skins[0].sharedMaterial.IsKeywordEnabled("_MAPPING_6_FRAMES_LAYOUT"), Is.False);
                Assert.That(generated.IsKeywordEnabled("_MAPPING_6_FRAMES_LAYOUT"), Is.True);
                Assert.That(fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>().All(skin => skin.sharedMaterial == first), Is.True);
            }
            finally
            {
                foreach (var material in materials) Object.DestroyImmediate(material);
                Object.DestroyImmediate(first); Object.DestroyImmediate(generated);
            }
        }

        [TestCase("lilToon")]
        [TestCase("_lil/lilToonMulti")]
        public async Task ExplicitExportWithAudioLinkEnabledReimportsWithoutChangingThePreviewOrWarning(string shaderName)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var skins = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
            var extra = new Material(Shader.Find(shaderName));
            Vrm10Instance imported = null;
            try
            {
                skins[0].sharedMaterial.shader = Shader.Find(shaderName);
                skins[0].sharedMaterial.SetFloat("_UseAudioLink", 1);
                skins[0].sharedMaterial.SetFloat("_AudioLink2Vertex", 1);
                extra.SetFloat("_UseAudioLink", 1); extra.SetFloat("_AudioLink2Emission", 1);
                if (shaderName == "_lil/lilToonMulti")
                    foreach (var material in new[] { skins[0].sharedMaterial, extra })
                    {
                        material.EnableKeyword("_MAPPING_6_FRAMES_LAYOUT"); material.EnableKeyword("_SUNDISK_HIGH_QUALITY");
                    }
                skins[1].sharedMaterial = extra;
                using var session = new ExperimentalExpressionCaptureSession(fixture.Source, replayInstalledDefaults: false);
                session.SetWeight(0, 0, 90); session.SetWeight(1, 0, 0); session.Capture("Smile");
                var warnings = new System.Collections.Generic.List<string>();
                var bytes = session.Export("Test", "Tests", warnings, new BlinkExportOptions { Mode = BlinkExportMode.None });
                Assert.That(warnings.Any(warning => warning.IndexOf("AudioLink", StringComparison.OrdinalIgnoreCase) >= 0), Is.False);
                Assert.That(session.Channels.All(channel => channel.Renderer.sharedMaterial.GetFloat("_UseAudioLink") == 1), Is.True);
                Assert.That(skins.All(skin => skin.sharedMaterial.GetFloat("_UseAudioLink") == 1), Is.True);
                Assert.That(session.Channels[0].Renderer.GetBlendShapeWeight(0), Is.EqualTo(90));
                using (var data = new GlbBinaryParser(bytes, "audiolink-copy.vrm").Parse())
                {
                    Assert.That(data.Json.Contains("_UseAudioLink"), Is.False, "External audio controls must not be emitted into the lilToon payload.");
                    Assert.That(data.Json.Contains("_MAPPING_6_FRAMES_LAYOUT"), Is.False);
                    Assert.That(data.Json.Contains("_SUNDISK_HIGH_QUALITY"), Is.False);
                }
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported, Is.Not.Null);
                var expression = imported.Vrm.Expression.CustomClips.Single(clip => clip.name == "VRChat / 記録 / Smile");
                Assert.That(expression.MorphTargetBindings.Length, Is.EqualTo(2));
                imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(expression.name), 1);
                imported.Runtime.Process();
                Assert.That(imported.GetComponentsInChildren<SkinnedMeshRenderer>().Any(skin =>
                    Enumerable.Range(0, skin.sharedMesh.blendShapeCount).Any(index => skin.GetBlendShapeWeight(index) > 0)), Is.True);
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                Object.DestroyImmediate(extra);
            }
        }

        [Test]
        public void RecordingAndSettingsRoundTripKeepIndependentMeshesAndSourceUntouched()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var before = fixture.Mesh.vertices;
            string json;
            using (var session = new ExperimentalExpressionCaptureSession(fixture.Source))
            {
                Assert.That(session.Channels.Count, Is.EqualTo(2));
                session.SetWeight(0, 0, 80); session.SetWeight(1, 0, 0);
                var pose = session.Capture("笑顔");
                session.RestoreBaseline();
                Assert.That(session.Channels.Select(channel => channel.Renderer.GetBlendShapeWeight(0)), Is.EqualTo(new[] { 35f, 35f }));
                session.PreviewRecorded(pose);
                Assert.That(session.Channels.Select(channel => channel.Renderer.GetBlendShapeWeight(0)), Is.EqualTo(new[] { 80f, 0f }));
                json = session.SaveSettings();
            }
            // A fresh session has different Unity object identities. Settings resolve exact mesh content instead.
            using (var session = new ExperimentalExpressionCaptureSession(fixture.Source))
            {
                session.LoadSettings(json);
                Assert.That(session.Expressions.Single().Name, Is.EqualTo("笑顔"));
                session.PreviewRecorded(session.Expressions.Single());
                Assert.That(session.Channels.Select(channel => channel.Renderer.GetBlendShapeWeight(0)), Is.EqualTo(new[] { 80f, 0f }));
            }
            Assert.That(fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>().Select(skin => skin.GetBlendShapeWeight(0)), Is.EqualTo(new[] { 35f, 35f }));
            Assert.That(fixture.Mesh.vertices, Is.EqualTo(before));
            Assert.That(fixture.Mesh.blendShapeCount, Is.EqualTo(1));
        }

        [TestCase("duplicate")]
        [TestCase("missing-row")]
        [TestCase("wrong-shape")]
        [TestCase("wrong-mesh")]
        [TestCase("nonfinite")]
        [TestCase("wrong-version")]
        public void InvalidSettingsLeaveBaselinePreviewAndRecordedFacesUnchanged(string kind)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            using var session = new ExperimentalExpressionCaptureSession(fixture.Source);
            session.SetWeight(0, 0, 75); session.Capture("元の記録");
            var settings = JsonUtility.FromJson<ExperimentalExpressionCaptureSession.Settings>(session.SaveSettings());
            switch (kind)
            {
                case "duplicate": settings.Expressions.Add(settings.Expressions[0]); break;
                case "missing-row": settings.Expressions[0].Rows.RemoveAt(0); break;
                case "wrong-shape": settings.Expressions[0].Rows[0].Shapes[0] = "different"; break;
                case "wrong-mesh": settings.MeshHashes[0] = "different"; break;
                case "nonfinite": settings.Expressions[0].Rows[0].Weights[0] = float.NaN; break;
                case "wrong-version": settings.Version = 999; break;
            }
            Assert.Throws<InvalidOperationException>(() => session.LoadSettings(JsonUtility.ToJson(settings)));
            Assert.That(session.Expressions.Single().Name, Is.EqualTo("元の記録"));
            Assert.That(session.Baseline.Rows[0].Weights[0], Is.EqualTo(35));
            Assert.That(session.Channels[0].Renderer.GetBlendShapeWeight(0), Is.EqualTo(75));
        }

        [Test]
        public void SourceMeshEditInvalidatesTheSessionAndOldSettingsWithoutMutatingPreview()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            string json;
            using (var session = new ExperimentalExpressionCaptureSession(fixture.Source))
            {
                session.SetWeight(0, 0, 70); session.Capture("face"); json = session.SaveSettings();
                var vertices = fixture.Mesh.vertices; vertices[0].x += .1f; fixture.Mesh.vertices = vertices;
                Assert.Throws<InvalidOperationException>(() => session.Export("Test", "Tests"));
                Assert.Throws<InvalidOperationException>(() => session.Capture("stale"));
                Assert.That(session.Expressions.Count, Is.EqualTo(1));
                Assert.That(session.SaveSettings(), Is.EqualTo(json), "Source edits must not prevent saving recovery settings.");
            }
            using (var replacement = new ExperimentalExpressionCaptureSession(fixture.Source))
                Assert.Throws<InvalidOperationException>(() => replacement.LoadSettings(json));
        }

        [Test]
        public void NeutralAndDuplicateFacesAreRejectedWithoutLosingExistingRecords()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            using var session = new ExperimentalExpressionCaptureSession(fixture.Source);
            Assert.Throws<InvalidOperationException>(() => session.Capture("neutral"));
            Assert.Throws<InvalidOperationException>(() => session.SetWeight(0, 0, float.PositiveInfinity));
            session.SetWeight(0, 0, 60); session.Capture("face");
            Assert.Throws<InvalidOperationException>(() => session.Capture("face"));
            Assert.That(session.Expressions.Count, Is.EqualTo(1));
        }

        [Test]
        public async Task CapturedFacesActuallyReimportAndReleaseToAnEditedBaseline()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var shader = Shader.Find("lilToon"); Assert.That(shader, Is.Not.Null);
            foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = shader;
            using var session = new ExperimentalExpressionCaptureSession(fixture.Source);
            var original = fixture.Mesh.vertices;
            session.SetWeight(0, 0, 55); session.SetWeight(1, 0, 20); session.CaptureBaseline();
            session.SetWeight(0, 0, 90); session.SetWeight(1, 0, 0); session.Capture("笑顔");
            var bytes = session.Export("Captured faces", "Tests", blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None },
                licenseOptions: new AvatarLicenseOptions { OtherLicenseUrl = "https://example.invalid/fixture-license", ThirdPartyLicenses = "Synthetic test fixture" });
            Assert.That(((System.Collections.Generic.Dictionary<string, object>)GlbDocument.Read(bytes).Json["extensions"])
                .ContainsKey(LilToonMobileProfile.ExtensionName), Is.True, "The captured VRM retains the dedicated lilToon payload.");
            File.WriteAllBytes(Path.Combine(Application.dataPath, "../CapturedExpressionExample.vrm"), bytes);
            Vrm10Instance imported = null;
            try
            {
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported.Vrm.Meta.OtherLicenseUrl, Is.EqualTo("https://example.invalid/fixture-license"));
                Assert.That(imported.Vrm.Meta.ThirdPartyLicenses, Is.EqualTo("Synthetic test fixture"));
                Assert.That(imported.Vrm.Meta.Redistribution, Is.False);
                var expression = imported.Vrm.Expression.CustomClips.Single(clip => clip.name == "VRChat / 記録 / 笑顔");
                Assert.That(expression.MorphTargetBindings.Length, Is.EqualTo(2));
                Assert.That(expression.IsBinary, Is.True, "Recorded faces use the existing fixed-expression selection contract.");
                Assert.That(expression.OverrideBlink.ToString(), Is.EqualTo("block"));
                var key = ExpressionKey.CreateCustom(expression.name);
                foreach (var strength in new[] { 1f, .5f, .49f, .51f, 0f, 1f, 0f })
                {
                    imported.Runtime.Expression.SetWeight(key, strength); imported.Runtime.Process();
                    for (var i = 0; i < session.Channels.Count; i++)
                    {
                        var skin = imported.transform.Find(session.Channels[i].Path).GetComponent<SkinnedMeshRenderer>();
                        var selected = strength > .5f ? 1f : 0f;
                        var expected = i == 0 ? Mathf.Lerp(55, 90, selected) : Mathf.Lerp(20, 0, selected);
                        var delta = new Vector3[skin.sharedMesh.vertexCount]; var value = skin.sharedMesh.vertices[0];
                        for (var shape = 0; shape < skin.sharedMesh.blendShapeCount; shape++)
                        {
                            skin.sharedMesh.GetBlendShapeFrameVertices(shape, skin.sharedMesh.GetBlendShapeFrameCount(shape) - 1, delta, null, null);
                            value += delta[0] * skin.GetBlendShapeWeight(shape) / 100;
                        }
                        Assert.That(Vector3.Distance(value, original[0] + new Vector3(.02f, .01f, 0) * expected / 100), Is.LessThan(.0001));
                    }
                }
                Assert.That(fixture.Mesh.vertices, Is.EqualTo(original));
                Assert.That(fixture.Mesh.blendShapeCount, Is.EqualTo(1));
                Assert.That(session.Channels[0].Renderer.GetBlendShapeWeight(0), Is.EqualTo(90), "Export does not change the current preview.");
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); }
        }

        [TestCase("model-only")]
        [TestCase("none")]
        [TestCase("synced")]
        [TestCase("neutral")]
        [TestCase("display")]
        [TestCase("delayed")]
        [TestCase("completed")]
        [TestCase("audio")]
        [TestCase("audio-reactive")]
        [TestCase("unknown")]
        public async Task ExistingMenuCandidateCanBeDiscoveredPreviewedAndCapturedWithoutChangingItsController(string callback)
        {
            var descriptorType = Sdk("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            Assert.That(descriptorType, Is.Not.Null, "This experiment is tested with the real VRChat SDK.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            var folderName = "__ExperimentalCapture_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folderName); var folder = "Assets/" + folderName;
            try
            {
                var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
                controller.AddParameter("Face", AnimatorControllerParameterType.Int);
                var machine = controller.layers[0].stateMachine;
                var neutral = machine.AddState("Neutral"); neutral.writeDefaultValues = false;
                var smile = machine.AddState("Smile"); smile.writeDefaultValues = false;
                if (callback.StartsWith("audio", StringComparison.Ordinal)) smile.AddStateMachineBehaviour(Sdk("VRC.SDK3.Avatars.Components.VRCAnimatorPlayAudio"));
                if (callback == "unknown") Assert.That(smile.AddStateMachineBehaviour<UnknownStateCallbackProbe>(), Is.Not.Null);
                var clip = new AnimationClip();
                var expectedWeight = callback == "neutral" ? 35f : callback == "synced" ? 85f : 75f;
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail"), AnimationCurve.Constant(0, 1, callback == "neutral" ? 35 : 75));
                if (callback == "display") AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "m_Enabled"), AnimationCurve.Constant(0, 1, 0));
                if (callback == "delayed") AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail"),
                    new AnimationCurve(new Keyframe(0, 75), new Keyframe(10, 75), new Keyframe(11, 95)));
                if (callback == "completed") AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail"),
                    new AnimationCurve(new Keyframe(0, 35), new Keyframe(1, 75)));
                AssetDatabase.AddObjectToAsset(clip, controller); smile.motion = clip; machine.defaultState = neutral;
                var transition = neutral.AddTransition(smile); transition.hasExitTime = false; transition.duration = 0;
                transition.AddCondition(AnimatorConditionMode.Equals, 1, "Face");
                if (callback == "synced")
                {
                    controller.AddLayer("Synced"); var synced = controller.layers;
                    synced[1].syncedLayerIndex = 0; synced[1].defaultWeight = .5f; controller.layers = synced;
                    var overrideClip = new AnimationClip();
                    AnimationUtility.SetEditorCurve(overrideClip, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail"), AnimationCurve.Constant(0, 1, 95));
                    AssetDatabase.AddObjectToAsset(overrideClip, controller);
                    controller.SetStateEffectiveMotion(smile, overrideClip, 1);
                }
                var menu = ScriptableObject.CreateInstance(Sdk("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu"));
                AssetDatabase.CreateAsset(menu, folder + "/Menu.asset");
                using (var data = new SerializedObject(menu))
                {
                    var controls = data.FindProperty("controls"); controls.arraySize = 1;
                    var item = controls.GetArrayElementAtIndex(0); item.FindPropertyRelative("name").stringValue = "Smile";
                    var type = item.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "Toggle");
                    item.FindPropertyRelative("parameter").FindPropertyRelative("name").stringValue = "Face";
                    item.FindPropertyRelative("value").floatValue = 1; data.ApplyModifiedPropertiesWithoutUndo();
                }
                var descriptor = fixture.Source.AddComponent(descriptorType);
                using (var data = new SerializedObject(descriptor))
                {
                    data.FindProperty("customExpressions").boolValue = true; data.FindProperty("expressionsMenu").objectReferenceValue = menu;
                    data.FindProperty("customizeAnimationLayers").boolValue = true;
                    var list = data.FindProperty("baseAnimationLayers"); list.arraySize = 1;
                    var layer = list.GetArrayElementAtIndex(0); var type = layer.FindPropertyRelative("type");
                    type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
                    layer.FindPropertyRelative("isDefault").boolValue = false;
                    layer.FindPropertyRelative("animatorController").objectReferenceValue = controller;
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
                if (callback == "audio-reactive")
                {
                    var material = fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>().sharedMaterial;
                    material.shader = Shader.Find("lilToon"); material.SetFloat("_UseAudioLink", 1);
                    Assert.Throws<InvalidOperationException>(() => new ExperimentalExpressionCaptureSession(fixture.Source));
                    Assert.That(smile.behaviours.Length, Is.EqualTo(1), "Refusing an audio-reactive face cannot remove the source callback.");
                    return;
                }
                if (callback == "model-only")
                    foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = Shader.Find("lilToon");
                using var session = new ExperimentalExpressionCaptureSession(fixture.Source);
                if (callback == "model-only")
                {
                    var original = ExportSourceFingerprint.Compute(fixture.Source);
                    var bytes = session.Export("Existing menu", "Tests", blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                    var model = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                    try
                    {
                        var face = model.Vrm.Expression.CustomClips.Single(item => item.name.EndsWith("Smile", StringComparison.Ordinal));
                        var skin = model.transform.Find("Front").GetComponent<SkinnedMeshRenderer>();
                        var key = ExpressionKey.CreateCustom(face.name);
                        using var baked = new BlinkTestMesh();
                        model.Runtime.Expression.SetWeight(key, 0); model.Runtime.Process(); skin.BakeMesh(baked.Mesh); var before = baked.Mesh.vertices;
                        model.Runtime.Expression.SetWeight(key, 1); model.Runtime.Process(); skin.BakeMesh(baked.Mesh);
                        Assert.That(baked.Mesh.vertices.Zip(before, (a,b) => (a-b).sqrMagnitude).Max(), Is.GreaterThan(1e-8f));
                        model.Runtime.Expression.SetWeight(key, 0); model.Runtime.Process(); skin.BakeMesh(baked.Mesh);
                        Assert.That(baked.Mesh.vertices.Zip(before, (a,b) => (a-b).sqrMagnitude).Max(), Is.LessThan(1e-8f));
                        Assert.That(ExportSourceFingerprint.Compute(fixture.Source), Is.EqualTo(original));
                    }
                    finally { Object.DestroyImmediate(model.gameObject); }
                    return;
                }
                session.DiscoverCandidates();
                var candidate = session.Candidates.Entries.First(entry => entry.Name == "Smile");
                Assert.That(smile.behaviours.Length, Is.EqualTo(callback == "audio" || callback == "unknown" ? 1 : 0), "Source callbacks remain on their original asset.");
                if (callback == "unknown" || callback == "display" || callback == "delayed")
                {
                    Assert.That(candidate.Error, Is.Not.Null, "Unknown callbacks must not be silently omitted.");
                    Assert.Throws<InvalidOperationException>(() => session.PreviewCandidate(candidate));
                    return;
                }
                Assert.That(candidate.Error, Is.Null);
                session.RestoreBaseline();
                Assert.Throws<InvalidOperationException>(() => session.ImportCandidates(new[] { candidate, new VrChatExpressionMenu.Entry { Name = "foreign" } }));
                Assert.That(session.Expressions.Count, Is.Zero, "Failed imports never publish partially captured faces.");
                Assert.That(session.Channels.First(channel => channel.Path == "Front").Renderer.GetBlendShapeWeight(0), Is.EqualTo(35));
                var imported = session.ImportCandidates(new[] { candidate });
                Assert.That(imported.Imported, Is.EqualTo(new[] { "Smile" }));
                Assert.That(session.ImportCandidates(new[] { candidate }).Imported, Is.Empty, "Retrying import cannot duplicate a face.");
                Assert.That(session.Expressions.Single().Name, Is.EqualTo("Smile"));
                Assert.That(session.Channels.First(channel => channel.Path == "Front").Renderer.GetBlendShapeWeight(0), Is.EqualTo(35));
                session.PreviewCandidate(candidate);
                if (callback == "neutral") Assert.Throws<InvalidOperationException>(() => session.Capture("manual neutral"));
                else session.Capture("from menu");
                Assert.That(session.Channels.First(channel => channel.Path == "Front").Renderer.GetBlendShapeWeight(0), Is.EqualTo(expectedWeight));
                Assert.That(controller.layers[0].stateMachine.states.Length, Is.EqualTo(2));
                Assert.That(fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(35));
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }

        [TestCase("constant")]
        [TestCase("moving")]
        [TestCase("neutral")]
        public void ExplicitClipsMatchNativeValuesWithoutEvaluatingUnrelatedControllers(string mode)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var clip = new AnimationClip { name = "Authored face" };
            var controller = new AnimatorController();
            var machine = new AnimatorStateMachine(); var state = machine.AddState("Unknown unrelated idle");
            state.AddStateMachineBehaviour<UnknownStateCallbackProbe>();
            controller.layers = new[] { new AnimatorControllerLayer { name = "Unrelated tail", stateMachine = machine } };
            fixture.Source.GetComponent<Animator>().runtimeAnimatorController = controller;
            var tail = new GameObject("Tail"); tail.transform.SetParent(fixture.Source.transform);
            tail.transform.localRotation = Quaternion.Euler(0, 0, 359.9999f);
            var weight = mode == "neutral" ? 35 : 75;
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail"),
                mode == "moving" ? AnimationCurve.Linear(0, 35, 1, 75) : AnimationCurve.Constant(0, 1, weight));
            GameObject native = null;
            try
            {
                using var session = new ExperimentalExpressionCaptureSession(fixture.Source, replayInstalledDefaults: false);
                Assert.That(session.RecommendClips(), Is.Empty, "A name does not make an unreferenced clip a recommendation.");
                session.SetWeight(1, 0, 99);
                session.PreviewClip(clip, .5f);
                native = Object.Instantiate(fixture.Source); native.GetComponent<Animator>().runtimeAnimatorController = null;
                clip.SampleAnimation(native, .5f);
                Assert.That(session.Channels.First(channel => channel.Path == "Front").Renderer.GetBlendShapeWeight(0),
                    Is.EqualTo(native.transform.Find("Front").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(0)).Within(.001));
                Assert.That(session.Channels.First(channel => channel.Path == "Back").Renderer.GetBlendShapeWeight(0), Is.EqualTo(35), "Unspecified curves always start from the baseline.");
                var inputs = new[] { new ExperimentalExpressionCaptureSession.ClipInput { Clip = clip, Name = "Explicit", Time = .5f } };
                Assert.That(session.ImportClips(inputs).Imported, Is.EqualTo(new[] { "Explicit" }));
                Assert.That(session.ImportClips(inputs).Imported, Is.Empty);
                session.LoadSettings(session.SaveSettings()); session.PreviewRecorded(session.Expressions.Single());
                Assert.That(fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(35));
                Assert.That(state.behaviours.Length, Is.EqualTo(1));
            }
            finally { Object.DestroyImmediate(native); Object.DestroyImmediate(clip); Object.DestroyImmediate(controller); Object.DestroyImmediate(machine); }
        }

        [TestCase("bone")]
        [TestCase("material")]
        [TestCase("display")]
        [TestCase("reference")]
        [TestCase("missing")]
        [TestCase("event")]
        [TestCase("wrong-shape")]
        [TestCase("bad-time")]
        public void ExplicitClipErrorsNeverPartiallyRecordOrAlterThePreview(string mode)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            using var session = new ExperimentalExpressionCaptureSession(fixture.Source, false);
            var valid = new AnimationClip(); var invalid = new AnimationClip();
            var binding = EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail");
            AnimationUtility.SetEditorCurve(valid, binding, AnimationCurve.Constant(0, 1, 80));
            AnimationUtility.SetEditorCurve(invalid, binding, AnimationCurve.Constant(0, 1, 65));
            if (mode == "bone") AnimationUtility.SetEditorCurve(invalid, EditorCurveBinding.FloatCurve("Head", typeof(Transform), "localEulerAnglesRaw.x"), AnimationCurve.Constant(0, 1, 15));
            if (mode == "material") AnimationUtility.SetEditorCurve(invalid, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "material._Color.r"), AnimationCurve.Constant(0, 1, .3f));
            if (mode == "display") AnimationUtility.SetEditorCurve(invalid, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "m_Enabled"), AnimationCurve.Constant(0, 1, 0));
            if (mode == "reference") AnimationUtility.SetObjectReferenceCurve(invalid, EditorCurveBinding.PPtrCurve("Front", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]"), new[] { new ObjectReferenceKeyframe { time = 0, value = fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>().sharedMaterial } });
            if (mode == "missing" || mode == "wrong-shape") AnimationUtility.SetEditorCurve(invalid,
                EditorCurveBinding.FloatCurve(mode == "missing" ? "Missing" : "Front", typeof(SkinnedMeshRenderer), mode == "wrong-shape" ? "blendShape.Missing" : "blendShape.Hair detail"), AnimationCurve.Constant(0, 1, 65));
            if (mode == "event") AnimationUtility.SetAnimationEvents(invalid, new[] { new AnimationEvent { functionName = "MustNeverRun", time = .1f } });
            try
            {
                session.SetWeight(0, 0, 50);
                var time = mode == "bad-time" ? float.NaN : 0;
                Assert.That(session.ClipError(invalid, time), Is.Not.Null);
                Assert.Throws<InvalidOperationException>(() => session.PreviewClip(invalid, time));
                Assert.Throws<InvalidOperationException>(() => session.ImportClips(new[] {
                    new ExperimentalExpressionCaptureSession.ClipInput { Clip = valid, Name = "Valid" },
                    new ExperimentalExpressionCaptureSession.ClipInput { Clip = invalid, Name = "Invalid", Time = time } }));
                Assert.That(session.Expressions, Is.Empty);
                Assert.That(session.Channels[0].Renderer.GetBlendShapeWeight(0), Is.EqualTo(50));
            }
            finally { Object.DestroyImmediate(valid); Object.DestroyImmediate(invalid); }
        }

        [Test]
        public void RecommendationsFollowActualMergeAnimatorReferencesInsteadOfScanningByName()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var folderName = "__ExplicitRecommendations_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folderName); var folder = "Assets/" + folderName;
            try
            {
                var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Faces.controller");
                var clip = new AnimationClip { name = "An arbitrary user name" };
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail"), AnimationCurve.Constant(0, 1, 70));
                AssetDatabase.AddObjectToAsset(clip, controller); controller.layers[0].stateMachine.AddState("Face").motion = clip;
                var mergeType = Sdk("nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator");
                Assert.That(mergeType, Is.Not.Null, "This experiment uses the real Modular Avatar integration.");
                var merge = fixture.Source.AddComponent(mergeType);
                using (var data = new SerializedObject(merge))
                {
                    data.FindProperty("animator").objectReferenceValue = controller;
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
                using var session = new ExperimentalExpressionCaptureSession(fixture.Source, false);
                Assert.That(session.RecommendClips().Any(item => item.Clip == clip), Is.True);
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }

        [UnityTest]
        public IEnumerator CandidateSelectionBulkAddAndNamePreviewUseTheAutomaticFinalFrame()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var type = typeof(ExperimentalExpressionCaptureWindow);
            var window = ScriptableObject.CreateInstance<ExperimentalExpressionCaptureWindow>();
            var clips = Enumerable.Range(0, 3).Select(index => new AnimationClip { name = "Candidate " + index }).ToArray();
            for (var index = 0; index < clips.Length; index++) AnimationUtility.SetEditorCurve(clips[index],
                EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail"), AnimationCurve.Linear(0, 10, 1, 70 + index));
            try
            {
                window.position = new Rect(40, 40, 700, 1100); window.Show();
                type.GetField("source", flags).SetValue(window, fixture.Source);
                type.GetMethod("Prepare", flags).Invoke(window, null);
                type.GetField("recommendations", flags).SetValue(window, clips.Select(clip => new ExperimentalExpressionCaptureSession.ClipRecommendation { Clip = clip, Source = "Fixture" }).ToList());
                var errors = (System.Collections.Generic.Dictionary<AnimationClip, string>)type.GetField("clipErrors", flags).GetValue(window);
                foreach (var clip in clips) errors[clip] = null;
                for (var frame = 0; frame < 8; frame++) { window.Repaint(); yield return null; }
                var names = (System.Collections.Generic.Dictionary<AnimationClip, Rect>)type.GetField("candidateRects", flags).GetValue(window);
                Click(window, names[clips[0]]);
                for (var frame = 0; frame < 8; frame++) { window.Repaint(); yield return null; }
                var session = (ExperimentalExpressionCaptureSession)type.GetField("session", flags).GetValue(window);
                Assert.That(session.Channels.First(channel => channel.Path == "Front").Renderer.GetBlendShapeWeight(0), Is.EqualTo(70));
                var toggles = (System.Collections.Generic.Dictionary<AnimationClip, Rect>)type.GetField("candidateToggleRects", flags).GetValue(window);
                Click(window, toggles[clips[0]]); Click(window, toggles[clips[2]]);
                for (var frame = 0; frame < 8; frame++) { window.Repaint(); yield return null; }
                Click(window, (Rect)type.GetField("addSelectedRect", flags).GetValue(window));
                for (var frame = 0; frame < 8; frame++) { window.Repaint(); yield return null; }
                var inputs = (System.Collections.Generic.List<ExperimentalExpressionCaptureSession.ClipInput>)type.GetField("clipInputs", flags).GetValue(window);
                Assert.That(inputs.Select(input => input.Clip), Is.EqualTo(new[] { clips[0], clips[2] }));
                Assert.That(inputs.All(input => input.Selected && input.Time == 1), Is.True);
                type.GetMethod("RecordSelectedClips", flags).Invoke(window, null);
                Assert.That(session.Expressions.Count, Is.EqualTo(2));
                Assert.That(session.Expressions.Select(pose => pose.Rows.First(row => row.Path == "Front").Weights[0]), Is.EqualTo(new[] { 70f, 72f }));
                Assert.That(fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(35));
            }
            finally { window.Close(); foreach (var clip in clips) Object.DestroyImmediate(clip); }
        }

        static void Click(EditorWindow window, Rect rect)
        {
            Assert.That(rect.width, Is.GreaterThan(0));
            window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = rect.center });
            window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = rect.center });
        }

        [UnityTest] public IEnumerator OneExplicitClipCanBeDroppedAndPreviewed() => VerifyExplicitDrop(1);
        [UnityTest] public IEnumerator FourExplicitClipsCanBeDroppedAndRecorded() => VerifyExplicitDrop(4);

        static IEnumerator VerifyExplicitDrop(int count)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var type = typeof(ExperimentalExpressionCaptureWindow);
            var window = ScriptableObject.CreateInstance<ExperimentalExpressionCaptureWindow>();
            var clips = Enumerable.Range(0, count).Select(index => new AnimationClip { name = "Dropped face " + index }).ToArray();
            for (var index = 0; index < clips.Length; index++) AnimationUtility.SetEditorCurve(clips[index],
                EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail"), AnimationCurve.Constant(0, 1, 70 + index));
            try
            {
                window.position = new Rect(40, 40, 700, 1100); window.Show();
                type.GetField("source", flags).SetValue(window, fixture.Source);
                type.GetMethod("Prepare", flags).Invoke(window, null);
                for (var frame = 0; frame < 8; frame++) { window.Repaint(); yield return null; }
                var drop = (Rect)type.GetField("expressionDropRect", flags).GetValue(window);
                Assert.That(drop.height, Is.EqualTo(42));
                DragAndDrop.PrepareStartDrag(); DragAndDrop.objectReferences = clips.Cast<Object>().ToArray();
                window.SendEvent(new Event { type = EventType.DragUpdated, mousePosition = drop.center });
                window.SendEvent(new Event { type = EventType.DragPerform, mousePosition = drop.center });
                for (var frame = 0; frame < 8; frame++) { window.Repaint(); yield return null; }
                var inputs = (System.Collections.Generic.List<ExperimentalExpressionCaptureSession.ClipInput>)type.GetField("clipInputs", flags).GetValue(window);
                Assert.That(inputs.Count, Is.EqualTo(count), "Actual IMGUI drag events must add the selected files.");
                Assert.That(inputs.All(input => input.Error == null && input.Selected), Is.True);
                var session = (ExperimentalExpressionCaptureSession)type.GetField("session", flags).GetValue(window);
                Assert.That(session.Channels.First(channel => channel.Path == "Front").Renderer.GetBlendShapeWeight(0), Is.EqualTo(70 + count - 1), "Adding a valid file previews it.");
                Assert.That(session.ImportClips(inputs).Imported.Count, Is.EqualTo(count));
                Assert.That(fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(35));
            }
            finally
            {
                DragAndDrop.objectReferences = Array.Empty<Object>(); window.Close();
                foreach (var clip in clips) Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void AtomicSaveReplacesTheIntendedFileAndCleansItsOwnTemporaryFile()
        {
            var directory = Path.Combine(Path.GetTempPath(), "vrvlog-capture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "face.vrm"); File.WriteAllBytes(path, new byte[] { 1, 2 });
                ExperimentalExpressionCaptureWindow.AtomicWrite(path, new byte[] { 3, 4, 5 });
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 3, 4, 5 }));
                Assert.That(Directory.GetFiles(directory), Is.EqualTo(new[] { path }));
            }
            finally { Directory.Delete(directory, true); }
        }

        [Test]
        public void FileRecordsUpdateNamesAndTimeAndDeselectWithoutDeletingManualFaces()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            using var session = new ExperimentalExpressionCaptureSession(fixture.Source, false);
            var clip = new AnimationClip();
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail"), AnimationCurve.Linear(0, 35, 1, 80));
            try
            {
                session.SetWeight(0, 0, 60); session.Capture("Manual");
                var manual = session.Expressions.Single();
                var input = new ExperimentalExpressionCaptureSession.ClipInput { Clip = clip, Name = "Old", Time = 0 };
                var old = session.ReplaceClipRecords(new[] { input }, null);
                input.Name = "Updated"; input.Time = 1;
                var updated = session.ReplaceClipRecords(new[] { input }, old);
                Assert.That(session.Expressions.Select(pose => pose.Name), Is.EqualTo(new[] { "Manual", "Updated" }));
                session.PreviewRecorded(updated.Single());
                Assert.That(session.Channels.First(channel => channel.Path == "Front").Renderer.GetBlendShapeWeight(0), Is.EqualTo(80));
                session.ReplaceClipRecords(Array.Empty<ExperimentalExpressionCaptureSession.ClipInput>(), updated);
                Assert.That(session.Expressions, Is.EqualTo(new[] { manual }));
            }
            finally { Object.DestroyImmediate(clip); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InvalidOrDuplicateUpdatesPreserveEveryPreviouslyRecordedFace(bool duplicate)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            using var session = new ExperimentalExpressionCaptureSession(fixture.Source, false);
            var clip = new AnimationClip();
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail"), AnimationCurve.Constant(0, 1, 75));
            try
            {
                session.SetWeight(0, 0, 60); session.Capture("Manual");
                var input = new ExperimentalExpressionCaptureSession.ClipInput { Clip = clip, Name = "File" };
                var previous = session.ReplaceClipRecords(new[] { input }, null);
                var all = session.Expressions.ToArray();
                if (duplicate) input.Name = "Manual"; else input.Time = float.NaN;
                Assert.Throws<InvalidOperationException>(() => session.ReplaceClipRecords(new[] { input }, previous));
                Assert.That(session.Expressions, Is.EqualTo(all));
            }
            finally { Object.DestroyImmediate(clip); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LastSavedOutputCannotTransferAReplacedOrMissingFile(bool missing)
        {
            var path = Path.Combine(Path.GetTempPath(), "vrvlog-capture-output-" + Guid.NewGuid().ToString("N") + ".vrm");
            var bytes = new byte[] { 1, 2, 3 };
            try
            {
                File.WriteAllBytes(path, bytes);
                var saved = new SavedExpressionVrm(path, bytes, 2, 1);
                Assert.That(saved.VerifiedPath(), Is.EqualTo(path));
                if (missing) File.Delete(path); else File.WriteAllBytes(path, new byte[] { 3, 2, 1 });
                Assert.Throws<InvalidOperationException>(() => saved.VerifiedPath());
                Assert.That(File.Exists(path), Is.EqualTo(!missing));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        static Type Sdk(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(name)).FirstOrDefault(type => type != null);
    }

}
