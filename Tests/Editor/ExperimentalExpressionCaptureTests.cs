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
        public void OwnedCopyKeepsAValidIsolatedSceneAndPreservesSource()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var originalScene = fixture.Source.scene;
            var stamp = ExportSourceFingerprint.Compute(fixture.Source);
            GameObject copy = null;
            try
            {
                copy = NdmfExportPreparation.InstantiateOwnedCopy(fixture.Source);
                Assert.That(copy.scene.IsValid(), Is.True);
                Assert.That(copy.scene.isLoaded, Is.True);
                Assert.That(UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(copy.scene), Is.True);
                Assert.That(copy.scene, Is.Not.EqualTo(originalScene));
                Assert.That(copy.scene.GetRootGameObjects(), Does.Contain(copy));
                Assert.That(fixture.Source.scene, Is.EqualTo(originalScene));
                Assert.That(ExportSourceFingerprint.Compute(fixture.Source), Is.EqualTo(stamp));
            }
            finally { if (copy != null) Object.DestroyImmediate(copy); NdmfExportPreparation.ReleaseUnusedCopyScene(); }
        }

        [Test]
        public void InstalledNdmfReportRemainsSafeAfterItsOwnedCopyIsDestroyed()
        {
            var reportType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("nadena.dev.ndmf.ErrorReport"))
                .FirstOrDefault(type => type != null);
            if (reportType == null) Assert.Ignore("Requires the installed NDMF integration package.");
            var source = new GameObject("Owned diagnostic scene regression");
            GameObject copy = null;
            try
            {
                copy = NdmfExportPreparation.InstantiateOwnedCopy(source);
                var scene = copy.scene;
                var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
                var report = reportType.GetMethod("Create", flags).Invoke(null, new object[] { copy, true });
                typeof(NdmfExportPreparation).GetMethod("RetainCopySceneForReport", flags)
                    .Invoke(null, new object[] { copy, new { ErrorReport = report } });
                var resolve = reportType.GetMethod("TryResolveAvatar");
                Assert.That(resolve.Invoke(report, new object[] { null }), Is.EqualTo(true));
                Object.DestroyImmediate(copy); copy = null;
                NdmfExportPreparation.ReleaseUnusedCopyScene();
                Assert.That(scene.IsValid(), Is.True, "The console can still retain this report after export cleanup.");
                Assert.That(resolve.Invoke(report, new object[] { null }), Is.EqualTo(false));
                GC.KeepAlive(report);
            }
            finally { if (copy != null) Object.DestroyImmediate(copy); Object.DestroyImmediate(source); }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void AuxiliaryPreviewsDoNotInvokeAvatarEditorCallbacks(bool blinkPreview)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var material = fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>().sharedMaterial;
            fixture.Source.AddComponent<RecoveryCallbackProbe>().SharedMaterial = material;
            EditorWindow window = null;
            try
            {
                var stamp = ExportSourceFingerprint.Compute(fixture.Source); var color = material.GetColor("_Color");
                RecoveryCallbackProbe.ResetCounters(); RecoveryCallbackProbe.Armed = true;
                if (blinkPreview)
                {
                    var preview = ScriptableObject.CreateInstance<BlinkPreviewWindow>(); window = preview;
                    var options = new BlinkExportOptions { Mode = BlinkExportMode.Manual };
                    options.Both.Add(new BlinkShapeBinding { Renderer = fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>(), Shape = "Hair detail", Weight = 80 });
                    preview.Prepare(fixture.Source, options, Array.Empty<GameObject>(), new ExportGimmickOptions());
                }
                else
                {
                    var preview = ScriptableObject.CreateInstance<PoseReviewWindow>(); window = preview;
                    var type = typeof(PoseReviewWindow); var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    type.GetField("source", flags).SetValue(preview, fixture.Source);
                    type.GetField("options", flags).SetValue(preview, new PoseExportOptions());
                    type.GetField("excluded", flags).SetValue(preview, Array.Empty<GameObject>());
                    type.GetField("gimmicks", flags).SetValue(preview, new ExportGimmickOptions());
                    type.GetMethod("PreparePreview", flags).Invoke(preview, null);
                    Assert.That(type.GetField("error", flags).GetValue(preview), Is.Null);
                    Assert.That(type.GetField("copy", flags).GetValue(preview), Is.Not.Null);
                }
                Assert.That(RecoveryCallbackProbe.AwakeCalls, Is.Zero); Assert.That(RecoveryCallbackProbe.EnableCalls, Is.Zero);
                Assert.That(material.GetColor("_Color"), Is.EqualTo(color)); Assert.That(ExportSourceFingerprint.Compute(fixture.Source), Is.EqualTo(stamp));
            }
            finally { if (window != null) Object.DestroyImmediate(window); RecoveryCallbackProbe.ResetCounters(); }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void ExportRetainsTheAuthoredVrmConstraintEnabledState(bool recordFace, bool enabled)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var settings = ScriptableObject.CreateInstance<VRM10Object>();
            fixture.Source.AddComponent<Vrm10Instance>().Vrm = settings;
            foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = Shader.Find("lilToon");
            var original = fixture.Source.transform.Find("Independent hair").gameObject.AddComponent<Vrm10RotationConstraint>();
            original.Source = fixture.Source.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Head);
            original.enabled = enabled;
            var clip = new AnimationClip { name = "Constrained face" };
            try
            {
                var stamp = ExportSourceFingerprint.Compute(fixture.Source);
                using var session = new ExperimentalExpressionCaptureSession(fixture.Source, false);
                Assert.That(session.Copy.GetComponentInChildren<Vrm10RotationConstraint>().enabled, Is.EqualTo(enabled));
                if (recordFace)
                {
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail"), AnimationCurve.Constant(0, 1, 80));
                    session.ImportClips(new[] { new ExperimentalExpressionCaptureSession.ClipInput { Clip = clip, Name = clip.name, Time = clip.length } });
                }
                var bytes = session.Export("Constraint export", "Tests", blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                var nodes = (System.Collections.Generic.List<object>)GlbDocument.Read(bytes).Json["nodes"];
                var count = nodes.Cast<System.Collections.Generic.Dictionary<string, object>>().Count(node =>
                    node.TryGetValue("extensions", out var extensions) &&
                    ((System.Collections.Generic.Dictionary<string, object>)extensions).ContainsKey("VRMC_node_constraint"));
                Assert.That(count, Is.EqualTo(enabled ? 1 : 0));
                Assert.That(original.enabled, Is.EqualTo(enabled)); Assert.That(ExportSourceFingerprint.Compute(fixture.Source), Is.EqualTo(stamp));
            }
            finally { Object.DestroyImmediate(clip); Object.DestroyImmediate(settings); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PreparingAndExportingDoNotInvokeAvatarEditorCallbacks(bool recordFace)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = Shader.Find("lilToon");
            var material = fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>().sharedMaterial;
            var probe = fixture.Source.AddComponent<RecoveryCallbackProbe>(); probe.SharedMaterial = material;
            AppearancePreparationTests.AddRule(fixture.Source, "ModularAvatarShapeChanger", "Shapes", "ChangedShape",
                fixture.Source.transform.Find("Front").gameObject, ("ShapeName", "Hair detail"), ("ChangeType", 1), ("Value", 50f));
            var clip = new AnimationClip { name = "Callback-safe face" };
            try
            {
                var stamp = ExportSourceFingerprint.Compute(fixture.Source); var color = material.GetColor("_Color");
                RecoveryCallbackProbe.ResetCounters(); RecoveryCallbackProbe.Armed = true;
                using var session = new ExperimentalExpressionCaptureSession(fixture.Source, false);
                if (recordFace)
                {
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail"), AnimationCurve.Constant(0, 1, 80));
                    session.ImportClips(new[] { new ExperimentalExpressionCaptureSession.ClipInput { Clip = clip, Name = clip.name, Time = clip.length } });
                }
                var bytes = session.Export("Callback-safe export", "Tests", blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                Assert.DoesNotThrow(() => LilToonGlbExtension.Validate(bytes));
                Assert.That(RecoveryCallbackProbe.AwakeCalls, Is.Zero); Assert.That(RecoveryCallbackProbe.EnableCalls, Is.Zero);
                Assert.That(material.GetColor("_Color"), Is.EqualTo(color)); Assert.That(ExportSourceFingerprint.Compute(fixture.Source), Is.EqualTo(stamp));
            }
            finally { RecoveryCallbackProbe.ResetCounters(); Object.DestroyImmediate(clip); }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ReimportRefreshesExplicitRowsAndRestoresTheirPreviousSelection(bool selected)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var window = ScriptableObject.CreateInstance<ExperimentalExpressionCaptureWindow>(); var clip = new AnimationClip { name = "Explicit edited face" };
            var type = typeof(ExperimentalExpressionCaptureWindow); var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var binding = EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail");
            var unsupported = EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalPosition.x");
            try
            {
                AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, 1, 60));
                type.GetField("source", flags).SetValue(window, fixture.Source); type.GetMethod("Prepare", flags).Invoke(window, null);
                type.GetMethod("AddClip", flags).Invoke(window, new object[] { clip });
                var inputs = (System.Collections.Generic.List<ExperimentalExpressionCaptureSession.ClipInput>)type.GetField("clipInputs", flags).GetValue(window);
                var input = inputs.Single(); input.Selected = selected;
                AnimationUtility.SetEditorCurve(clip, unsupported, AnimationCurve.Constant(0, 1, 0));
                type.GetMethod("RefreshCandidateValidation", flags).Invoke(window, null);
                Assert.That(input.Error, Is.Not.Null); Assert.That(input.Selected, Is.False);
                AnimationUtility.SetEditorCurve(clip, unsupported, null); AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, 2, 90));
                type.GetMethod("RefreshCandidateValidation", flags).Invoke(window, null);
                Assert.That(input.Error, Is.Null); Assert.That(input.Selected, Is.EqualTo(selected)); Assert.That(input.Time, Is.EqualTo(2));
                if (selected)
                {
                    type.GetMethod("RecordSelectedClips", flags).Invoke(window, null);
                    var session = (ExperimentalExpressionCaptureSession)type.GetField("session", flags).GetValue(window);
                    Assert.That(session.Expressions.Single().Rows.Single(row => row.Path == "Front").Weights[0], Is.EqualTo(90));
                }
            }
            finally { Object.DestroyImmediate(window); Object.DestroyImmediate(clip); }
        }

        [Test]
        public void MeshRendererRecoveryMapsTheOriginalRendererToThePreparedExportCopy()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = Shader.Find("lilToon");
            var child = new GameObject("Aux plain mesh"); child.transform.SetParent(fixture.Source.transform, false);
            child.AddComponent<MeshFilter>().sharedMesh = fixture.Mesh;
            var original = child.AddComponent<MeshRenderer>(); original.sharedMaterial = fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>().sharedMaterial;
            var hidden = new Material(Shader.Find("VRVlogTests/UnsupportedHiddenFallback")); var clip = new AnimationClip { name = "Mesh recovery face" };
            try
            {
                var stamp = ExportSourceFingerprint.Compute(fixture.Source);
                using var session = new ExperimentalExpressionCaptureSession(fixture.Source, false);
                var prepared = session.Copy.transform.Find("Aux plain mesh").GetComponent<MeshRenderer>();
                // Simulate a later authoring material assignment on the owned copy.
                prepared.sharedMaterial = hidden;
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail"), AnimationCurve.Constant(0, 1, 80));
                session.ImportClips(new[] { new ExperimentalExpressionCaptureSession.ClipInput { Clip = clip, Name = clip.name, Time = clip.length } });
                var options = new ExportRecoveryOptions(); options.Actions.Add(new ExportRecoveryAction { Id = "mesh-recovery", Kind = ExportRecoveryActionKind.ExcludeHiddenRenderer, Renderer = original });
                var bytes = session.Export("Mesh recovery", "Tests", blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None }, recoveryOptions: options);
                Assert.DoesNotThrow(() => LilToonGlbExtension.Validate(bytes));
                var nodes = (System.Collections.Generic.List<object>)GlbDocument.Read(bytes).Json["nodes"];
                Assert.That(nodes.Cast<System.Collections.Generic.Dictionary<string, object>>().Any(node => node.TryGetValue("name", out var name) && (string)name == "Aux plain mesh" && node.ContainsKey("mesh")), Is.False);
                Assert.That(ExportSourceFingerprint.Compute(fixture.Source), Is.EqualTo(stamp)); Assert.That(original.enabled, Is.True);
            }
            finally { Object.DestroyImmediate(hidden); Object.DestroyImmediate(clip); }
        }

        [Test]
        public async Task RetrySnapshotReplacesTheSourceCatalogAfterLiveRowsWereRemoved()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = Shader.Find("lilToon");
            var authored = ScriptableObject.CreateInstance<VRM10Object>(); var original = ScriptableObject.CreateInstance<VRM10Expression>();
            var clip = new AnimationClip { name = "Captured face" }; Vrm10Instance imported = null;
            try
            {
                original.name = "Source face"; original.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 0, .4f) };
                authored.Expression.CustomClips.Add(original); fixture.Source.AddComponent<Vrm10Instance>().Vrm = authored;
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail"), AnimationCurve.Constant(0, 1, 80));
                using var session = new ExperimentalExpressionCaptureSession(fixture.Source, false);
                session.ImportClips(new[] { new ExperimentalExpressionCaptureSession.ClipInput { Clip = clip, Name = clip.name, Time = clip.length } });
                var snapshot = session.Expressions.Select(ExperimentalExpressionCaptureSession.ClonePose).ToArray(); session.Expressions.Clear();
                var bytes = session.Export("Retry snapshot", "Tests", blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None }, recordedExpressions: snapshot);
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported.Vrm.Expression.CustomClips.Select(item => item.name).ToArray(), Is.EqualTo(new[] { "VRChat / 記録 / Captured face" }));
                Assert.That(authored.Expression.CustomClips.Single(), Is.SameAs(original));
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); Object.DestroyImmediate(clip); Object.DestroyImmediate(authored); Object.DestroyImmediate(original); }
        }

        [UnityTest]
        public IEnumerator CorrectedRecommendedAssetBecomesSelectableWithoutLosingExplicitFiles()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var folder = "Assets/VRVlogCandidateRefresh_" + Guid.NewGuid().ToString("N"); AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            var clip = new AnimationClip { name = "Repair candidate" }; var retained = new AnimationClip { name = "Keep selected face" };
            var controller = new AnimatorController(); var machine = new AnimatorStateMachine();
            var window = ScriptableObject.CreateInstance<ExperimentalExpressionCaptureWindow>();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic; var type = typeof(ExperimentalExpressionCaptureWindow);
            var binding = EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail");
            var unsupported = EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalPosition.x");
            try
            {
                AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, 1, 75));
                AnimationUtility.SetEditorCurve(clip, unsupported, AnimationCurve.Constant(0, 1, 0)); AssetDatabase.CreateAsset(clip, folder + "/Face.anim");
                AnimationUtility.SetEditorCurve(retained, binding, AnimationCurve.Constant(0, 1, 50));
                var state = machine.AddState("Face"); state.motion = clip; machine.defaultState = state;
                controller.layers = new[] { new AnimatorControllerLayer { name = "Face", defaultWeight = 1, stateMachine = machine } };
                fixture.Source.GetComponent<Animator>().runtimeAnimatorController = controller;
                type.GetField("source", flags).SetValue(window, fixture.Source); type.GetMethod("Prepare", flags).Invoke(window, null);
                type.GetMethod("AddClip", flags).Invoke(window, new object[] { retained });
                window.position = new Rect(40, 40, 700, 1100); window.Show();
                for (var frame = 0; frame < 8; frame++) { window.Repaint(); yield return null; }
                var errors = (System.Collections.Generic.Dictionary<AnimationClip, string>)type.GetField("clipErrors", flags).GetValue(window);
                Assert.That(errors[clip], Is.Not.Null);
                AnimationUtility.SetEditorCurve(clip, unsupported, null); EditorUtility.SetDirty(clip); AssetDatabase.SaveAssets(); AssetDatabase.ImportAsset(folder + "/Face.anim", ImportAssetOptions.ForceUpdate);
                for (var frame = 0; frame < 16; frame++) { window.Repaint(); yield return null; }
                Assert.That(errors[clip], Is.Null, "Reimport refreshes validation and the disabled toggle.");
                var inputs = (System.Collections.Generic.List<ExperimentalExpressionCaptureSession.ClipInput>)type.GetField("clipInputs", flags).GetValue(window);
                Assert.That(inputs.Single().Clip, Is.SameAs(retained));
                var session = (ExperimentalExpressionCaptureSession)type.GetField("session", flags).GetValue(window);
                Assert.That(session.HasCurrentSource, Is.True);
                var rectangles = (System.Collections.Generic.Dictionary<AnimationClip, Rect>)type.GetField("candidateRects", flags).GetValue(window);
                Click(window, rectangles[clip]); for (var frame = 0; frame < 8; frame++) { window.Repaint(); yield return null; }
                session = (ExperimentalExpressionCaptureSession)type.GetField("session", flags).GetValue(window);
                Assert.That(session.Channels.Single(channel => channel.Path == "Front").Renderer.GetBlendShapeWeight(0), Is.EqualTo(75));
                type.GetMethod("AddClip", flags).Invoke(window, new object[] { clip });
                type.GetMethod("RecordSelectedClips", flags).Invoke(window, null);
                Assert.That(session.Expressions.Count, Is.EqualTo(2));
            }
            finally { Object.DestroyImmediate(window); AssetDatabase.DeleteAsset(folder); Object.DestroyImmediate(retained); Object.DestroyImmediate(controller); Object.DestroyImmediate(machine); }
        }

        [TestCase(false, "2nd", "stay")]
        [TestCase(false, "2nd", "close")]
        [TestCase(true, "2nd", "close")]
        [TestCase(false, "2nd", "comparison")]
        [TestCase(true, "2nd", "comparison")]
        [TestCase(false, "2nd", "change")]
        [TestCase(true, "2nd", "change")]
        [TestCase(true, "2nd", "stay")]
        [TestCase(false, "3rd", "stay")]
        [TestCase(true, "3rd", "stay")]
        public void PrimaryWindowOffersMaterialRecoveryAndSavesRetryWithoutChangingSource(bool recordFace, string layer, string ownerAction)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var material = new Material(Shader.Find("lilToon"));
            var texture = new RenderTexture(16, 16, 0);
            var clip = new AnimationClip { name = "Recovery face" };
            var window = ScriptableObject.CreateInstance<ExperimentalExpressionCaptureWindow>();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var type = typeof(ExperimentalExpressionCaptureWindow);
            var path = Path.Combine(Path.GetTempPath(), "vrvlog-primary-recovery-" + Guid.NewGuid().ToString("N") + ".vrm");
            var property = "_UseMain" + layer + "Tex"; var slot = "_Main" + layer + "Tex";
            try
            {
                material.SetFloat(property, 1); material.SetTexture(slot, texture);
                foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial = material;
                var stamp = ExportSourceFingerprint.Compute(fixture.Source);
                type.GetField("source", flags).SetValue(window, fixture.Source); type.GetField("author", flags).SetValue(window, "Tests");
                type.GetField("automaticBlink", flags).SetValue(window, false);
                type.GetMethod("Prepare", flags).Invoke(window, null);
                if (recordFace)
                {
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail"), AnimationCurve.Constant(0, 1, 65));
                    type.GetMethod("AddClip", flags).Invoke(window, new object[] { clip });
                }
                var recovery = window.SaveVrm(path);
                Assert.That(recovery, Is.Not.Null); Assert.That(File.Exists(path), Is.False);
                Assert.That(Resources.FindObjectsOfTypeAll<ExportFailureWindow>().Any(), Is.True, "Primary UI opens the existing recovery dialog.");
                var kind = layer == "2nd" ? ExportRecoveryActionKind.OmitSecondLayer : ExportRecoveryActionKind.OmitThirdLayer;
                var diagnostic = recovery.Report.Diagnostics.Single(item => item.Action?.Kind == kind);
                Assert.That(diagnostic.Action.Material, Is.SameAs(material));
                var target = (ExperimentalExpressionCaptureSession)type.GetField("session", flags).GetValue(window);
                if (ownerAction == "close" || ownerAction == "comparison") { Object.DestroyImmediate(window); window = null; }
                if (ownerAction == "change") type.GetMethod("ChangeAvatar", flags).Invoke(window, null);
                Assert.That(target.Copy, Is.Not.Null, "The recovery dialog retains the prepared copy after its owner closes.");
                var options = new ExportRecoveryOptions(); options.Actions.Add(diagnostic.Action);
                Assert.That(recovery.Attempt(options), Is.True, recovery.Failure?.ToString());
                if (ownerAction == "comparison")
                {
                    ExportRecoveryComparisonWindow.Show(recovery);
                    foreach (var failure in Resources.FindObjectsOfTypeAll<ExportFailureWindow>()) Object.DestroyImmediate(failure);
                    Assert.That(target.Copy, Is.Not.Null, "The comparison owns the captured input after the failure window closes.");
                    ExportFailureWindow.Show(recovery);
                    foreach (var comparison in Resources.FindObjectsOfTypeAll<ExportRecoveryComparisonWindow>()) Object.DestroyImmediate(comparison);
                    Assert.That(target.Copy, Is.Not.Null, "Returning to remedies transfers ownership back before closing the comparison.");
                    Assert.That(recovery.Attempt(options), Is.True, recovery.Failure?.ToString());
                }
                Assert.DoesNotThrow(() => recovery.SavePending());
                Assert.DoesNotThrow(() => LilToonGlbExtension.Validate(File.ReadAllBytes(path)));
                Assert.That(ExportSourceFingerprint.Compute(fixture.Source), Is.EqualTo(stamp));
                Assert.That(material.GetFloat(property), Is.EqualTo(1)); Assert.That(material.GetTexture(slot), Is.SameAs(texture));
                foreach (var failure in Resources.FindObjectsOfTypeAll<ExportFailureWindow>()) Object.DestroyImmediate(failure);
                if (ownerAction != "stay") Assert.That(target.Copy, Is.Null, "Closing recovery releases its private preview scene and capture assets.");
            }
            finally
            {
                foreach (var comparison in Resources.FindObjectsOfTypeAll<ExportRecoveryComparisonWindow>()) Object.DestroyImmediate(comparison);
                foreach (var failure in Resources.FindObjectsOfTypeAll<ExportFailureWindow>()) Object.DestroyImmediate(failure);
                if (window != null) Object.DestroyImmediate(window); Object.DestroyImmediate(clip); Object.DestroyImmediate(material); Object.DestroyImmediate(texture);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ChildAnimatorClipTargetsItsOwnMeshEvenWithAnIdenticalRootMesh(bool competingRoot)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var child = new GameObject("Accessory"); child.transform.SetParent(fixture.Source.transform, false);
            var face = fixture.Source.transform.Find("Front"); face.SetParent(child.transform, false);
            SkinnedMeshRenderer competing = null;
            if (competingRoot)
            {
                var duplicate = Object.Instantiate(face.gameObject, fixture.Source.transform); duplicate.name = "Front";
                competing = duplicate.GetComponent<SkinnedMeshRenderer>();
            }
            var clip = new AnimationClip { name = "Relative face" }; var controller = new AnimatorController(); var machine = new AnimatorStateMachine();
            try
            {
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail"), AnimationCurve.Constant(0, 1, 75));
                var state = machine.AddState("Face"); state.motion = clip; machine.defaultState = state;
                controller.layers = new[] { new AnimatorControllerLayer { name = "Face", defaultWeight = 1, stateMachine = machine } };
                child.AddComponent<Animator>().runtimeAnimatorController = controller;
                var stamp = ExportSourceFingerprint.Compute(fixture.Source);
                using var session = new ExperimentalExpressionCaptureSession(fixture.Source, false);
                Assert.That(session.RecommendClips().Any(item => item.Clip == clip), Is.True);
                Assert.That(session.ClipError(clip, clip.length), Is.Null);
                session.PreviewClip(clip, clip.length);
                Assert.That(session.Channels.Single(channel => channel.Path == "Accessory/Front").Renderer.GetBlendShapeWeight(0), Is.EqualTo(75));
                if (competingRoot) Assert.That(session.Channels.Single(channel => channel.Path == "Front").Renderer.GetBlendShapeWeight(0), Is.EqualTo(35));
                session.ImportClips(new[] { new ExperimentalExpressionCaptureSession.ClipInput { Clip = clip, Name = "Relative face", Time = clip.length } });
                Assert.That(session.Expressions.Single().Rows.Single(row => row.Path == "Accessory/Front").Weights[0], Is.EqualTo(75));
                Assert.That(AnimationUtility.GetCurveBindings(clip).Single().path, Is.EqualTo("Front"));
                Assert.That(ExportSourceFingerprint.Compute(fixture.Source), Is.EqualTo(stamp));
            }
            finally { Object.DestroyImmediate(clip); Object.DestroyImmediate(controller); Object.DestroyImmediate(machine); }
        }

        [TestCase(false, "APL", false)]
        [TestCase(true, "APL", false)]
        [TestCase(false, "menu", false)]
        [TestCase(true, "menu", false)]
        [TestCase(false, "APL", true)]
        [TestCase(true, "APL", true)]
        public async Task RegisteredAndManualBodyPosesSurviveWithOrWithoutRecordedFaces(bool recordFace, string registration, bool sameClip)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = Shader.Find("lilToon");
            var registeredClip = HumanoidPoseTests.Clip(fixture.Source, 30); registeredClip.name = "Registered clip";
            var manualClip = sameClip ? registeredClip : HumanoidPoseTests.Clip(fixture.Source, 60);
            var assets = new System.Collections.Generic.List<Object>();
            Vrm10Instance imported = null;
            try
            {
                if (registration == "APL")
                {
                    var type = Sdk(PoseExportSession.AplType); Assert.That(type, Is.Not.Null, "Installed APL is required.");
                    var child = new GameObject("Registered library"); child.transform.SetParent(fixture.Source.transform, false);
                    var component = child.AddComponent(type);
                    var data = Activator.CreateInstance(type.GetField("data").FieldType); SetPoseMember(component, "data", data);
                    var category = AddPoseListItem((IList)PoseMenuResolver.Member(data, "categories")); SetPoseMember(category, "name", "Body");
                    var entry = AddPoseListItem((IList)PoseMenuResolver.Member(category, "poses")); SetPoseMember(entry, "name", "Registered body"); SetPoseMember(entry, "animationClip", registeredClip);
                }
                else ConfigureRegisteredMenuPose(fixture.Source, registeredClip, assets);
                var sourceBefore = ExportSourceFingerprint.Compute(fixture.Source);
                using var session = new ExperimentalExpressionCaptureSession(fixture.Source, false);
                session.PoseOptions.Manual.Add(new ManualPose { Clip = manualClip, Name = "Manual body" });
                if (recordFace) { session.SetWeight(0, 0, 65); session.Capture("Selected face"); }
                var bytes = session.Export("Registered poses", "Tests", blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                var document = GlbDocument.Read(bytes);
                var extensions = (System.Collections.Generic.Dictionary<string, object>)document.Json["extensions"];
                var poses = VRVlog.Poses.HumanoidPoseData.Read(extensions[VRVlog.Poses.HumanoidPoseData.Extension]);
                Assert.That(poses.Count, Is.EqualTo(sameClip ? 1 : 2));
                Assert.That(poses.Any(pose => pose.Name == "Registered body"), Is.True);
                if (!sameClip) Assert.That(poses.Any(pose => pose.Name == "Manual body"), Is.True);
                Assert.That(poses.All(pose => pose.Bones.Any(bone => bone.Name == "leftUpperArm")), Is.True);
                VRVlog.Poses.HumanoidPoseData.ValidateReferences(poses, document.Json);
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported.GetComponent<Animator>().isHuman, Is.True);
                Assert.That(ExportSourceFingerprint.Compute(fixture.Source), Is.EqualTo(sourceBefore));
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                foreach (var asset in assets) Object.DestroyImmediate(asset);
                if (manualClip != registeredClip) Object.DestroyImmediate(manualClip); Object.DestroyImmediate(registeredClip);
            }
        }

        static void SetPoseMember(object target, string name, object value)
        {
            var field = target.GetType().GetField(name); field.SetValue(target, field.FieldType.IsEnum ? Enum.Parse(field.FieldType, value.ToString()) : value);
        }
        static object AddPoseListItem(IList list)
        {
            var value = Activator.CreateInstance(list.GetType().GetGenericArguments()[0]); list.Add(value); return value;
        }
        static void ConfigureRegisteredMenuPose(GameObject source, AnimationClip clip, System.Collections.Generic.List<Object> assets)
        {
            var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var descriptor = (Component)typeof(PoseRegistrationTests).GetMethod("Descriptor", flags).Invoke(null, new object[] { source });
            var controller = new AnimatorController(); assets.Add(controller);
            controller.AddParameter("Pose", AnimatorControllerParameterType.Int);
            var machine = new AnimatorStateMachine(); assets.Add(machine);
            var idle = machine.AddState("Idle"); idle.writeDefaultValues = false; machine.defaultState = idle;
            var state = machine.AddState("Selected"); state.writeDefaultValues = false; state.motion = clip;
            var tracking = state.AddStateMachineBehaviour(Sdk("VRC.SDK3.Avatars.Components.VRCAnimatorTrackingControl"));
            foreach (var part in new[] { "trackingLeftHand", "trackingRightHand", "trackingHip", "trackingLeftFoot", "trackingRightFoot", "trackingLeftFingers", "trackingRightFingers" }) SetPoseMember(tracking, part, "Animation");
            var transition = machine.AddAnyStateTransition(state); transition.canTransitionToSelf = false; transition.hasExitTime = false; transition.duration = 0; transition.AddCondition(AnimatorConditionMode.Equals, 1, "Pose");
            controller.layers = new[] { new AnimatorControllerLayer { name = "Body pose", defaultWeight = 1, stateMachine = machine } };
            foreach (var key in new[] { "baseAnimationLayers", "specialAnimationLayers" })
            {
                var field = descriptor.GetType().GetField(key); var layers = (Array)field.GetValue(descriptor);
                for (var i = 0; i < layers.Length; i++)
                {
                    var layer = layers.GetValue(i); var empty = new AnimatorController(); assets.Add(empty);
                    SetPoseMember(layer, "isDefault", false); SetPoseMember(layer, "animatorController", PoseMenuResolver.Member(layer, "type").ToString() == "Gesture" ? controller : empty); layers.SetValue(layer, i);
                }
                field.SetValue(descriptor, layers);
            }
            var menu = ScriptableObject.CreateInstance(Sdk("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu")); assets.Add(menu);
            var control = AddPoseListItem((IList)PoseMenuResolver.Member(menu, "controls")); SetPoseMember(control, "name", "Registered body"); SetPoseMember(control, "type", "Toggle"); SetPoseMember(control, "value", 1f);
            var parameter = Activator.CreateInstance(control.GetType().GetField("parameter").FieldType); SetPoseMember(parameter, "name", "Pose"); SetPoseMember(control, "parameter", parameter); SetPoseMember(descriptor, "expressionsMenu", menu);
            var parameters = ScriptableObject.CreateInstance(Sdk("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters")); assets.Add(parameters);
            var parametersField = parameters.GetType().GetField("parameters"); var element = parametersField.FieldType.GetElementType(); var definition = Activator.CreateInstance(element); SetPoseMember(definition, "name", "Pose"); SetPoseMember(definition, "valueType", "Int");
            var definitions = Array.CreateInstance(element, 1); definitions.SetValue(definition, 0); parametersField.SetValue(parameters, definitions); SetPoseMember(descriptor, "expressionParameters", parameters);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ReenablingBlinkAfterNoneRestoresAutomaticBlinkInSavedVrm(bool recordFace)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = Shader.Find("lilToon");
            var sourceSkin = fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>(); sourceSkin.SetBlendShapeWeight(0, 0);
            var descriptorType = Sdk("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            var descriptor = fixture.Source.AddComponent(descriptorType);
            descriptorType.GetField("enableEyeLook").SetValue(descriptor, true);
            var eyeField = descriptorType.GetField("customEyeLookSettings"); var eye = eyeField.GetValue(descriptor);
            var lidType = eye.GetType().GetField("eyelidType"); lidType.SetValue(eye, Enum.Parse(lidType.FieldType, "Blendshapes"));
            eye.GetType().GetField("eyelidsSkinnedMesh").SetValue(eye, sourceSkin);
            eye.GetType().GetField("eyelidsBlendshapes").SetValue(eye, new[] { 0, -1, -1 }); eyeField.SetValue(descriptor, eye);
            var sourceBefore = ExportSourceFingerprint.Compute(fixture.Source);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var type = typeof(ExperimentalExpressionCaptureWindow);
            var window = ScriptableObject.CreateInstance<ExperimentalExpressionCaptureWindow>();
            var clip = new AnimationClip { name = "Selected face" };
            var path = Path.Combine(Path.GetTempPath(), "vrvlog-reenabled-blink-" + Guid.NewGuid().ToString("N") + ".vrm");
            Vrm10Instance imported = null;
            try
            {
                type.GetField("source", flags).SetValue(window, fixture.Source); type.GetField("author", flags).SetValue(window, "Tests");
                type.GetMethod("Prepare", flags).Invoke(window, null);
                if (recordFace)
                {
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail"), AnimationCurve.Constant(0, 1, 65));
                    type.GetMethod("AddClip", flags).Invoke(window, new object[] { clip });
                }
                type.GetMethod("ShowBlinkConfiguration", flags).Invoke(window, null);
                var dialog = Resources.FindObjectsOfTypeAll<BlinkConfigurationWindow>().Single();
                dialog.Options.SelectMode(BlinkExportMode.None); dialog.ApplySettings();
                window.SaveVrm(path);
                imported = await Vrm10.LoadBytesAsync(File.ReadAllBytes(path), canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                // No-blink exports intentionally retain an inert standard clip.
                // Verify visible behavior rather than requiring absent metadata.
                var disabledSkin = VrChatExpressionSampler.FindRenderer(imported.gameObject, imported.Vrm.Expression.Blink.MorphTargetBindings[0].RelativePath);
                using (var disabledBaked = new BlinkTestMesh())
                {
                    imported.Runtime.Expression.SetWeight(ExpressionKey.Blink, 0); imported.Runtime.Process(); disabledSkin.BakeMesh(disabledBaked.Mesh); var rest = disabledBaked.Mesh.vertices;
                    imported.Runtime.Expression.SetWeight(ExpressionKey.Blink, 1); imported.Runtime.Process(); disabledSkin.BakeMesh(disabledBaked.Mesh);
                    Assert.That(disabledBaked.Mesh.vertices.Zip(rest, (a,b) => (a-b).sqrMagnitude).Max(), Is.LessThan(1e-8f));
                }
                Object.DestroyImmediate(imported.gameObject); imported = null;
                // The primary IMGUI checkbox writes this same bool directly.
                type.GetField("automaticBlink", flags).SetValue(window, true);
                type.GetMethod("ShowBlinkConfiguration", flags).Invoke(window, null);
                dialog = Resources.FindObjectsOfTypeAll<BlinkConfigurationWindow>().Single();
                Assert.That(dialog.Options.Mode, Is.EqualTo(BlinkExportMode.Auto)); dialog.Close();
                window.SaveVrm(path);
                imported = await Vrm10.LoadBytesAsync(File.ReadAllBytes(path), canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported.Vrm.Expression.Blink?.MorphTargetBindings, Is.Not.Empty);
                var outputSkin = VrChatExpressionSampler.FindRenderer(imported.gameObject, imported.Vrm.Expression.Blink.MorphTargetBindings[0].RelativePath);
                using var baked = new BlinkTestMesh();
                imported.Runtime.Expression.SetWeight(ExpressionKey.Blink, 0); imported.Runtime.Process(); outputSkin.BakeMesh(baked.Mesh); var before = baked.Mesh.vertices;
                imported.Runtime.Expression.SetWeight(ExpressionKey.Blink, 1); imported.Runtime.Process(); outputSkin.BakeMesh(baked.Mesh);
                Assert.That(baked.Mesh.vertices.Zip(before, (a,b) => (a-b).sqrMagnitude).Max(), Is.GreaterThan(1e-8f));
                imported.Runtime.Expression.SetWeight(ExpressionKey.Blink, 0); imported.Runtime.Process(); outputSkin.BakeMesh(baked.Mesh);
                Assert.That(baked.Mesh.vertices.Zip(before, (a,b) => (a-b).sqrMagnitude).Max(), Is.LessThan(1e-8f));
                Assert.That(ExportSourceFingerprint.Compute(fixture.Source), Is.EqualTo(sourceBefore));
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); Object.DestroyImmediate(window); Object.DestroyImmediate(clip); if (File.Exists(path)) File.Delete(path); }
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void ChangingAvatarDoesNotCarryLicenseSelectionsIntoTheNextSavedVrm(bool recordFace, bool changeFromUnpreparedWindow)
        {
            using var first = new AttachmentConnectionTests.Fixture();
            using var second = new AttachmentConnectionTests.Fixture();
            foreach (var skin in first.Source.GetComponentsInChildren<SkinnedMeshRenderer>().Concat(second.Source.GetComponentsInChildren<SkinnedMeshRenderer>()))
                skin.sharedMaterial.shader = Shader.Find("lilToon");
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var type = typeof(ExperimentalExpressionCaptureWindow);
            var window = ScriptableObject.CreateInstance<ExperimentalExpressionCaptureWindow>();
            var clip = new AnimationClip { name = "Selected face" };
            var path = Path.Combine(Path.GetTempPath(), "vrvlog-avatar-license-" + Guid.NewGuid().ToString("N") + ".vrm");
            try
            {
                type.GetField("source", flags).SetValue(window, first.Source);
                type.GetField("author", flags).SetValue(window, "Tests");
                type.GetField("automaticBlink", flags).SetValue(window, false);
                type.GetMethod("Prepare", flags).Invoke(window, null);
                var selected = new AvatarLicenseOptions {
                    AvatarPermission = UniGLTF.Extensions.VRMC_vrm.AvatarPermissionType.everyone,
                    CommercialUsage = UniGLTF.Extensions.VRMC_vrm.CommercialUsageType.corporation,
                    CreditNotation = UniGLTF.Extensions.VRMC_vrm.CreditNotationType.unnecessary,
                    Modification = UniGLTF.Extensions.VRMC_vrm.ModificationType.allowModificationRedistribution,
                    AllowExcessivelyViolentUsage = true, AllowExcessivelySexualUsage = true,
                    AllowPoliticalOrReligiousUsage = true, AllowAntisocialOrHateUsage = true, AllowRedistribution = true,
                    CopyrightInformation = "First avatar copyright", OtherLicenseUrl = "https://example.invalid/first-avatar", ThirdPartyLicenses = "First avatar licenses"
                };
                type.GetField("licenseOptions", flags).SetValue(window, selected);
                if (recordFace)
                {
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail"), AnimationCurve.Constant(0, 1, 65));
                    type.GetMethod("AddClip", flags).Invoke(window, new object[] { clip });
                }
                window.SaveVrm(path);
                var initial = SavedMeta(path);
                Assert.That(initial["avatarPermission"], Is.EqualTo("everyone"));
                Assert.That(initial["thirdPartyLicenses"], Is.EqualTo(selected.ThirdPartyLicenses));
                type.GetMethod(changeFromUnpreparedWindow ? "Cleanup" : "ChangeAvatar", flags).Invoke(window, null);
                type.GetMethod("SelectSource", flags).Invoke(window, new object[] { second.Source });
                type.GetMethod("Prepare", flags).Invoke(window, null);
                window.SaveVrm(path);
                var next = SavedMeta(path);
                Assert.That(next["avatarPermission"], Is.EqualTo("onlyAuthor"));
                Assert.That(next["commercialUsage"], Is.EqualTo("personalNonProfit"));
                Assert.That(next["creditNotation"], Is.EqualTo("required"));
                Assert.That(next["modification"], Is.EqualTo("prohibited"));
                foreach (var key in new[] { "allowExcessivelyViolentUsage", "allowExcessivelySexualUsage", "allowPoliticalOrReligiousUsage", "allowAntisocialOrHateUsage", "allowRedistribution" })
                    Assert.That(next[key], Is.EqualTo(false), key);
                foreach (var key in new[] { "copyrightInformation", "otherLicenseUrl", "thirdPartyLicenses" })
                    Assert.That(!next.TryGetValue(key, out var value) || value == null || string.IsNullOrEmpty(value.ToString()), Is.True, key);
            }
            finally { Object.DestroyImmediate(window); Object.DestroyImmediate(clip); if (File.Exists(path)) File.Delete(path); }
        }

        [TestCase(false, "", "avatar")]
        [TestCase(true, "", "avatar")]
        [TestCase(false, " \t ", "avatar")]
        [TestCase(true, " \t ", "avatar")]
        [TestCase(false, "  Named avatar  ", "Named avatar")]
        [TestCase(true, "  Named avatar  ", "Named avatar")]
        public void PrimaryWindowSavesBlankOrPaddedAvatarNames(bool recordFace, string rootName, string expectedName)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = Shader.Find("lilToon");
            fixture.Source.name = rootName;
            var sourceBefore = ExportSourceFingerprint.Compute(fixture.Source);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var type = typeof(ExperimentalExpressionCaptureWindow);
            var window = ScriptableObject.CreateInstance<ExperimentalExpressionCaptureWindow>();
            var clip = new AnimationClip { name = "Selected face" };
            var path = Path.Combine(Path.GetTempPath(), "vrvlog-avatar-name-" + Guid.NewGuid().ToString("N") + ".vrm");
            try
            {
                type.GetField("source", flags).SetValue(window, fixture.Source);
                type.GetField("author", flags).SetValue(window, "Tests");
                type.GetField("automaticBlink", flags).SetValue(window, false);
                type.GetMethod("Prepare", flags).Invoke(window, null);
                if (recordFace)
                {
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail"), AnimationCurve.Constant(0, 1, 65));
                    type.GetMethod("AddClip", flags).Invoke(window, new object[] { clip });
                }
                window.SaveVrm(path);
                Assert.That(SavedMeta(path)["name"], Is.EqualTo(expectedName));
                Assert.That(ExportSourceFingerprint.Compute(fixture.Source), Is.EqualTo(sourceBefore));
            }
            finally { Object.DestroyImmediate(window); Object.DestroyImmediate(clip); if (File.Exists(path)) File.Delete(path); }
        }

        static System.Collections.Generic.Dictionary<string, object> SavedMeta(string path)
        {
            var extensions = (System.Collections.Generic.Dictionary<string, object>)GlbDocument.Read(File.ReadAllBytes(path)).Json["extensions"];
            var vrm = (System.Collections.Generic.Dictionary<string, object>)extensions["VRMC_vrm"];
            return (System.Collections.Generic.Dictionary<string, object>)vrm["meta"];
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task AutomaticAuxiliaryExclusionIsConsistentWithOrWithoutRecordedFaces(bool recordFace, bool moveRendererWithMa)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = Shader.Find("lilToon");
            var originalSkin = fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>();
            var hiddenMaterial = new Material(Shader.Find("VRVlogTests/UnsupportedHiddenFallback"));
            var auxiliary = new GameObject("Auxiliary fallback"); auxiliary.transform.SetParent(fixture.Source.transform, false);
            var hiddenSkin = auxiliary.AddComponent<SkinnedMeshRenderer>();
            hiddenSkin.sharedMesh = originalSkin.sharedMesh; hiddenSkin.sharedMaterial = hiddenMaterial;
            hiddenSkin.bones = originalSkin.bones; hiddenSkin.rootBone = originalSkin.rootBone;
            if (moveRendererWithMa) MoveFrontWithInstalledMa(fixture.Source);
            var sourceBefore = ExportSourceFingerprint.Compute(fixture.Source);
            Vrm10Instance imported = null;
            try
            {
                using var session = new ExperimentalExpressionCaptureSession(fixture.Source, false);
                Assert.That(session.Copy.transform.Find("Auxiliary fallback")?.GetComponent<Renderer>(), Is.Null);
                Assert.That(session.Copy.GetComponentsInChildren<Renderer>(true).Any(renderer =>
                    renderer.sharedMaterials.Any(material => material != null && material.shader == hiddenMaterial.shader)), Is.False);
                Assert.That(session.Channels.Any(channel => channel.Renderer.name == "Auxiliary fallback"), Is.False);
                if (recordFace) { session.SetWeight(0, 0, 65); session.Capture("Selected face"); }
                imported = await Vrm10.LoadBytesAsync(session.Export("Auxiliary exclusion", "Tests", blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None }),
                    canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported.GetComponentsInChildren<Renderer>(true).Any(renderer => renderer.name == "Auxiliary fallback"), Is.False);
                Assert.That(imported.GetComponentsInChildren<Renderer>(true), Is.Not.Empty);
                if (recordFace) Assert.That(imported.Vrm.Expression.CustomClips.Any(clip => clip.name == "VRChat / 記録 / Selected face"), Is.True);
                Assert.That(hiddenSkin != null && hiddenSkin.sharedMaterial == hiddenMaterial, Is.True);
                Assert.That(ExportSourceFingerprint.Compute(fixture.Source), Is.EqualTo(sourceBefore));
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); Object.DestroyImmediate(hiddenMaterial); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RecordedFacesRetainAuthoredMouthAndGazePresetsAfterReload(bool moveRendererWithMa)
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
                if (moveRendererWithMa) MoveFrontWithInstalledMa(fixture.Source);
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
                var skin = VrChatExpressionSampler.FindRenderer(imported.gameObject, reloaded[ExpressionPreset.aa].MorphTargetBindings[0].RelativePath);
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

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task ManualBlinkConfigurationFromPrimaryWindowSurvivesModelAndRecordedFaceSave(bool recordFace, bool moveRendererWithMa)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial.shader = Shader.Find("lilToon");
            var sourceSkin = fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>(); sourceSkin.SetBlendShapeWeight(0, 0);
            if (moveRendererWithMa) MoveFrontWithInstalledMa(fixture.Source);
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
                var skin = VrChatExpressionSampler.FindRenderer(imported.gameObject, imported.Vrm.Expression.Blink.MorphTargetBindings[0].RelativePath);
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

        [TestCase("descriptor", false, false)]
        [TestCase("descriptor", true, false)]
        [TestCase("vrm", false, false)]
        [TestCase("vrm", true, false)]
        [TestCase("descriptor", false, true)]
        [TestCase("descriptor", true, true)]
        [TestCase("vrm", false, true)]
        [TestCase("vrm", true, true)]
        public async Task ExplicitBlinkMetadataSurvivesModelAndRecordedFaceExport(string configuration, bool recordFace, bool moveRendererWithMa)
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
                if (moveRendererWithMa) MoveFrontWithInstalledMa(fixture.Source);
                var sourceBefore = ExportSourceFingerprint.Compute(fixture.Source);
                using var session = new ExperimentalExpressionCaptureSession(fixture.Source, replayInstalledDefaults: false);
                if (recordFace) { session.SetWeight(0, 0, 65); session.Capture("Selected face"); }
                var bytes = session.Export("Explicit blink", "Tests");
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported.Vrm.Expression.Blink, Is.Not.Null);
                Assert.That(imported.Vrm.Expression.Blink.MorphTargetBindings, Is.Not.Empty);
                var outputSkin = VrChatExpressionSampler.FindRenderer(imported.gameObject, imported.Vrm.Expression.Blink.MorphTargetBindings[0].RelativePath);
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

        static void MoveFrontWithInstalledMa(GameObject source)
        {
            var proxyType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("nadena.dev.modular_avatar.core.ModularAvatarBoneProxy")).FirstOrDefault(type => type != null);
            var markerType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("nadena.dev.ndmf.runtime.components.NDMFAvatarRoot")).FirstOrDefault(type => type != null);
            Assert.That(proxyType, Is.Not.Null); Assert.That(markerType, Is.Not.Null);
            if (source.GetComponent(markerType) == null) source.AddComponent(markerType);
            var proxy = source.transform.Find("Front").gameObject.AddComponent(proxyType);
            proxyType.GetProperty("target").SetValue(proxy, source.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Head));
            var mode = proxyType.GetField("attachmentMode"); mode.SetValue(proxy, Enum.Parse(mode.FieldType, "AsChildKeepWorldPose"));
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
