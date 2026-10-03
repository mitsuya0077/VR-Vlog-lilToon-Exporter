using System;
using System.Collections.Generic;
using System.IO;
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
    public sealed class ExportRecoveryTests
    {
        [Test]
        public void CreatingADisplayCopyCannotRunAuthoringCallbacksAgainstSharedSourceMaterials()
        {
            using var f = new RecoveryFixture();
            RecoveryCallbackProbe.ResetCounters();
            try
            {
                var originalProbe = f.Source.AddComponent<RecoveryCallbackProbe>();
                Assert.That(originalProbe, Is.Not.Null, "The callback probe must be registered as a runtime-compatible test component.");
                originalProbe.SharedMaterial = f.Material;
                f.Material.SetColor("_Color", Color.blue);
                RecoveryCallbackProbe.Armed = true;
                using var preview = ExportRecoveryPreview.Create(f.Source);
                Assert.That(RecoveryCallbackProbe.AwakeCalls, Is.Zero, "Disabling a script after Instantiate is too late to prevent Awake.");
                Assert.That(RecoveryCallbackProbe.EnableCalls, Is.Zero);
                Assert.That(f.Material.GetColor("_Color"), Is.EqualTo(Color.blue));
                Assert.That(preview.Copy.GetComponent<RecoveryCallbackProbe>(), Is.Null, "The display copy cannot execute arbitrary authoring scripts later.");
                Assert.That(originalProbe.enabled, Is.True);
                Assert.That(originalProbe.SharedMaterial, Is.SameAs(f.Material));
                Assert.That(f.Source.activeSelf, Is.True);
            }
            finally { RecoveryCallbackProbe.ResetCounters(); }
        }

        [Test]
        public void UnknownFailureDoesNotInventARecipeOrSelectByObjectName()
        {
            using var f = new RecoveryFixture();
            f.Renderer.gameObject.name = "AudioLink pet clothes unknown gimmick";
            f.Material.shader = Shader.Find("Hidden/VRVlogTests/ShadowSphere");
            var report = ExportRecoveryReport.FromException(f.Source, new InvalidOperationException("Unknown tool failed"));
            Assert.That(report.Diagnostics, Is.Not.Empty);
            Assert.That(report.Diagnostics.All(d => d.Action == null), Is.True);
            Assert.That(f.Renderer.enabled, Is.True);
        }

        [Test]
        public void OutsideSceneReferencesAreDiagnosedWithoutAnAutomaticExclusion()
        {
            using var f = new RecoveryFixture();
            var external = new GameObject("External scene target");
            try
            {
                var constraint = f.Renderer.gameObject.AddComponent<UnityEngine.Animations.ParentConstraint>();
                constraint.AddSource(new UnityEngine.Animations.ConstraintSource { sourceTransform = external.transform, weight = 1 });
                var report = ExportRecoveryReport.FromException(f.Source, new InvalidOperationException("Reference failure"));
                var diagnostic = report.Diagnostics.First(d => d.Code == "outside-reference");
                Assert.That(diagnostic.Source, Is.SameAs(constraint));
                Assert.That(diagnostic.Action, Is.Null);
                Assert.That(diagnostic.Remedy, Is.Not.Empty);
                Assert.That(constraint.GetSource(0).sourceTransform, Is.SameAs(external.transform));
            }
            finally { Object.DestroyImmediate(external); }
        }

        [Test]
        public void PhysBoneFailureIncludesManualGuidanceAndDoesNotSuggestDeletingTheRig()
        {
            using var f = new RecoveryFixture();
            var report = ExportRecoveryReport.FromException(f.Source, new InvalidOperationException("Invalid spring reference"), "揺れ物変換");
            var diagnostic = report.Diagnostics.Single();
            Assert.That(diagnostic.Code, Is.EqualTo("physbone"));
            Assert.That(diagnostic.Action, Is.Null);
            Assert.That(diagnostic.Remedy, Does.Contain("PhysBone"));
            Assert.That(diagnostic.Stage, Is.EqualTo("揺れ物変換"));
            Assert.That(f.Renderer.enabled, Is.True);
        }

        [TestCase("2nd")]
        [TestCase("3rd")]
        public void OnlyAnIdentifiedOptionalLayerGetsALayerRecipe(string layer)
        {
            using var f = new RecoveryFixture();
            var issue = new MaterialBakeIssue(f.Material, "display", layer, "UV", "Unsupported UV", "Adjust UV");
            var report = ExportRecoveryReport.FromException(f.Source, new MaterialBakeException(new[] { issue }, true));
            var action = report.Diagnostics.Single(d => d.Action != null).Action;
            Assert.That(action.Kind, Is.EqualTo(layer == "2nd" ? ExportRecoveryActionKind.OmitSecondLayer : ExportRecoveryActionKind.OmitThirdLayer));
            Assert.That(action.Material, Is.SameAs(f.Material));
            Assert.That(report.Diagnostics.All(d => !string.IsNullOrEmpty(d.Stage) && !string.IsNullOrEmpty(d.Reason) && !string.IsNullOrEmpty(d.Remedy)), Is.True);
            var property = layer == "2nd" ? "_UseMain2ndTex" : "_UseMain3rdTex";
            f.Material.SetFloat(property, 1);
            using var preview = ExportRecoveryPreview.Create(f.Source, Options(action));
            Assert.That(preview.Copy.GetComponentInChildren<Renderer>().sharedMaterial.GetFloat(property), Is.Zero);
            Assert.That(f.Material.GetFloat(property), Is.EqualTo(1));
        }

        [Test]
        public void AudioLinkEnabledOnAnUnrelatedMaterialDoesNotBecomeARecipe()
        {
            using var f = new RecoveryFixture();
            f.Material.SetFloat("_UseAudioLink", 1);
            var report = ExportRecoveryReport.FromException(f.Source, new InvalidOperationException("AudioLink appears in this unrelated error"));
            Assert.That(report.Diagnostics.All(d => d.Action == null), Is.True);
        }

        [TestCase("2nd")]
        [TestCase("3rd")]
        public void OmittedLayerRemovesOnlyItsCopyTextureIncludingDynamicTextures(string layer)
        {
            using var f = new RecoveryFixture();
            var dynamicTexture = new RenderTexture(16, 16, 0);
            var main = new Texture2D(2, 2);
            try
            {
                var flag = layer == "2nd" ? "_UseMain2ndTex" : "_UseMain3rdTex";
                var slot = layer == "2nd" ? "_Main2ndTex" : "_Main3rdTex";
                f.Material.SetFloat(flag, 1); f.Material.SetTexture(slot, dynamicTexture); f.Material.SetTexture("_MainTex", main);
                var issue = new MaterialBakeIssue(f.Material, "display", layer, "dynamic texture", "Cannot save dynamic layer", "Omit this layer");
                var action = ExportRecoveryReport.FromException(f.Source, new MaterialBakeException(new[] { issue }, true)).Diagnostics.Single(d => d.Action != null).Action;
                using var preview = ExportRecoveryPreview.Create(f.Source, Options(action));
                var copy = preview.Copy.GetComponentInChildren<Renderer>().sharedMaterial;
                Assert.That(copy.GetFloat(flag), Is.Zero);
                Assert.That(copy.GetTexture(slot), Is.Null);
                Assert.That(copy.GetTexture("_MainTex"), Is.SameAs(main));
                Assert.That(f.Material.GetFloat(flag), Is.EqualTo(1));
                Assert.That(f.Material.GetTexture(slot), Is.SameAs(dynamicTexture));
                Assert.That(f.Material.GetTexture("_MainTex"), Is.SameAs(main));
                Assert.That(dynamicTexture.width, Is.EqualTo(16));
            }
            finally { Object.DestroyImmediate(dynamicTexture); Object.DestroyImmediate(main); }
        }

        [Test]
        public void MainLayerFailureAndForeignMaterialNeverBecomeOmissionRecipes()
        {
            using var f = new RecoveryFixture();
            var foreign = new Material(f.Material);
            try
            {
                foreach (var issue in new[] {
                    new MaterialBakeIssue(f.Material, "display", "1st", "UV", "Unsupported", "Adjust"),
                    new MaterialBakeIssue(foreign, "outside", "2nd", "UV", "Unsupported", "Adjust") })
                {
                    var report = ExportRecoveryReport.FromException(f.Source, new MaterialBakeException(new[] { issue }, true));
                    Assert.That(report.Diagnostics.All(d => d.Action == null), Is.True);
                }
            }
            finally { Object.DestroyImmediate(foreign); }
        }

        [Test]
        public void SameNamedMaterialsRemainDistinctAndSharedUseOfSelectedMaterialChangesTogether()
        {
            using var f = new RecoveryFixture();
            var other = new Material(f.Material) { name = f.Material.name };
            var texture = new Texture2D(2, 2) { name = "source image" };
            try
            {
                f.Material.SetTexture("_MainTex", texture);
                f.Material.SetFloat("_UseAudioLink", 1);
                other.SetFloat("_UseAudioLink", 1);
                f.AddRenderer("display", other);
                f.AddRenderer("display", f.Material);
                var originals = f.Source.GetComponentsInChildren<Renderer>().Select(r => r.sharedMaterial).ToArray();
                var before = EditorJsonUtility.ToJson(f.Material);
                var issue = ExportRecoveryReport.FromException(f.Source, AudioLinkFailure(f.Material));
                var otherIssue = ExportRecoveryReport.FromException(f.Source, AudioLinkFailure(other));
                Assert.That(otherIssue.Diagnostics.Single(d => d.Action != null).Action.Id, Is.Not.EqualTo(issue.Diagnostics.Single(d => d.Action != null).Action.Id));
                var action = issue.Diagnostics.First(d => d.Action?.Material == f.Material).Action;
                var options = Options(action);
                using (var preview = ExportRecoveryPreview.Create(f.Source, options))
                {
                    var copied = preview.Copy.GetComponentsInChildren<Renderer>().Select(r => r.sharedMaterial).ToArray();
                    Assert.That(copied[0], Is.Not.SameAs(f.Material));
                    Assert.That(copied[0], Is.SameAs(copied[2]), "Shared source identity must remain shared inside the copy.");
                    Assert.That(copied[0].GetFloat("_UseAudioLink"), Is.Zero);
                    Assert.That(copied[1].GetFloat("_UseAudioLink"), Is.EqualTo(1));
                    Assert.That(copied[0].GetTexture("_MainTex"), Is.SameAs(texture));
                }
                Assert.That(EditorJsonUtility.ToJson(f.Material), Is.EqualTo(before));
                Assert.That(f.Source.GetComponentsInChildren<Renderer>().Select(r => r.sharedMaterial), Is.EqualTo(originals));
                Assert.That(other.GetFloat("_UseAudioLink"), Is.EqualTo(1));
                Assert.That(texture.width, Is.EqualTo(2));
            }
            finally { Object.DestroyImmediate(other); Object.DestroyImmediate(texture); }
        }

        [Test]
        public void RemovingConsentCreatesAFreshCopyWithOriginalSettings()
        {
            using var f = new RecoveryFixture();
            f.Material.SetFloat("_UseAudioLink", 1);
            var action = ExportRecoveryReport.FromException(f.Source, AudioLinkFailure(f.Material)).Diagnostics.First(d => d.Action != null).Action;
            using (var adjusted = ExportRecoveryPreview.Create(f.Source, Options(action)))
                Assert.That(adjusted.Copy.GetComponentInChildren<Renderer>().sharedMaterial.GetFloat("_UseAudioLink"), Is.Zero);
            using (var original = ExportRecoveryPreview.Create(f.Source, new ExportRecoveryOptions()))
                Assert.That(original.Copy.GetComponentInChildren<Renderer>().sharedMaterial.GetFloat("_UseAudioLink"), Is.EqualTo(1));
            Assert.That(f.Material.GetFloat("_UseAudioLink"), Is.EqualTo(1));
        }

        [Test]
        public void ExcludingAHiddenRendererKeepsItsHierarchyAndClothingBones()
        {
            using var f = new RecoveryFixture();
            var hidden = new Material(Shader.Find("VRVlogTests/UnsupportedHiddenFallback"));
            try
            {
                var auxiliary = f.AddRenderer("auxiliary", hidden);
                var child = new GameObject("clothing bone"); child.transform.SetParent(auxiliary.transform);
                var report = ExportRecoveryReport.FromException(f.Source, new InvalidOperationException("Unsupported shader"));
                var action = report.Diagnostics.Single(d => d.Action?.Renderer == auxiliary).Action;
                using var preview = ExportRecoveryPreview.Create(f.Source, Options(action));
                Assert.That(preview.Copy.transform.Find("auxiliary").GetComponent<Renderer>().enabled, Is.False);
                Assert.That(preview.Copy.transform.Find("auxiliary/clothing bone"), Is.Not.Null);
                Assert.That(preview.Copy.transform.Find("display").GetComponent<Renderer>().enabled, Is.True);
                Assert.That(auxiliary.enabled, Is.True);
                Assert.That(child.transform.parent, Is.SameAs(auxiliary.transform));
            }
            finally { Object.DestroyImmediate(hidden); }
        }

        [Test]
        public void RendererRecipeIsRecheckedWhenItsShaderBecomesOrdinaryClothing()
        {
            using var f = new RecoveryFixture();
            var hidden = new Material(Shader.Find("VRVlogTests/UnsupportedHiddenFallback"));
            try
            {
                var auxiliary = f.AddRenderer("auxiliary", hidden);
                var action = ExportRecoveryReport.FromException(f.Source, new InvalidOperationException("Unsupported shader")).Diagnostics.Single(d => d.Action?.Renderer == auxiliary).Action;
                auxiliary.sharedMaterial = f.Material;
                Assert.Throws<InvalidOperationException>(() => ExportRecoveryPreview.Create(f.Source, Options(action)));
                Assert.That(auxiliary.enabled, Is.True);
            }
            finally { Object.DestroyImmediate(hidden); }
        }

        [Test]
        public void SameNamedHiddenRenderersUseSiblingIdentityForOnlyTheSelectedExclusion()
        {
            using var f = new RecoveryFixture();
            var hidden = new Material(Shader.Find("VRVlogTests/UnsupportedHiddenFallback"));
            try
            {
                var first = f.AddRenderer("same", hidden); var second = f.AddRenderer("same", hidden);
                var actions = ExportRecoveryReport.FromException(f.Source, new InvalidOperationException("Unsupported shader")).Diagnostics.Where(d => d.Action != null).Select(d => d.Action).ToArray();
                Assert.That(actions.Select(a => a.Id).Distinct().Count(), Is.EqualTo(2));
                using var preview = ExportRecoveryPreview.Create(f.Source, Options(actions.Single(a => a.Renderer == second)));
                var renderers = preview.Copy.GetComponentsInChildren<Renderer>();
                Assert.That(renderers[1].enabled, Is.True);
                Assert.That(renderers[2].enabled, Is.False);
                Assert.That(first.enabled && second.enabled, Is.True);
            }
            finally { Object.DestroyImmediate(hidden); }
        }

        [Test]
        public void PreviewExclusionsDoNotShiftTheIdentityOfAKeptSameNamedSibling()
        {
            using var f = new RecoveryFixture();
            var hidden = new Material(Shader.Find("VRVlogTests/UnsupportedHiddenFallback"));
            try
            {
                var omitted = f.AddRenderer("same", hidden);
                var kept = f.AddRenderer("same", hidden);
                using var preview = ExportRecoveryPreview.Create(f.Source, excludedObjects: new[] { omitted.gameObject },
                    gimmickOptions: new ExportGimmickOptions { AutoExclude = true, IncludedObjects = new[] { kept.gameObject } });
                var survivors = preview.Copy.GetComponentsInChildren<Renderer>(true);
                Assert.That(survivors.Length, Is.EqualTo(2), "The body and the explicitly kept auxiliary renderer must remain.");
                Assert.That(survivors.Single(r => r.name == "same").enabled, Is.True);
                Assert.That(survivors.Single(r => r.name == "same").sharedMaterial.shader, Is.SameAs(hidden.shader));
                Assert.That(kept.enabled && omitted.enabled, Is.True);
                Assert.That(f.Source.GetComponentsInChildren<Renderer>().Length, Is.EqualTo(3));
                Assert.That(kept.sharedMaterial, Is.SameAs(hidden));
                Assert.That(omitted.sharedMaterial, Is.SameAs(hidden));
            }
            finally { Object.DestroyImmediate(hidden); }
        }

        [Test]
        public void ForgedOrForeignActionCannotMutateAnExportCopy()
        {
            using var f = new RecoveryFixture();
            var foreign = new Material(f.Material);
            try
            {
                Assert.Throws<InvalidOperationException>(() => ExportRecoveryPreview.Create(f.Source, Options(new ExportRecoveryAction { Kind = ExportRecoveryActionKind.DisableAudioLink, Material = foreign })));
                Assert.Throws<InvalidOperationException>(() => ExportRecoveryPreview.Create(f.Source, Options(new ExportRecoveryAction { Kind = (ExportRecoveryActionKind)999, Material = f.Material })));
                Assert.That(f.Renderer.sharedMaterial, Is.SameAs(f.Material));
            }
            finally { Object.DestroyImmediate(foreign); }
        }

        [TestCase("material")]
        [TestCase("hierarchy")]
        [TestCase("component")]
        [TestCase("animation")]
        public void SourceStampRejectsChangesToSourceAndRelatedAssets(string change)
        {
            using var f = new RecoveryFixture();
            var clip = new AnimationClip();
            var controller = new AnimatorController();
            var machine = new AnimatorStateMachine();
            try
            {
                var state = machine.AddState("Idle"); state.motion = clip;
                controller.AddLayer(new AnimatorControllerLayer { name = "base", stateMachine = machine, defaultWeight = 1 });
                f.Source.AddComponent<Animator>().runtimeAnimatorController = controller;
                var stamp = ExportRecoverySourceStamp.Capture(f.Source);
                Assert.That(stamp.Matches(f.Source), Is.True);
                if (change == "material") f.Material.SetColor("_Color", Color.red);
                if (change == "hierarchy") { var added = new GameObject("added clothing"); added.transform.SetParent(f.Source.transform); }
                if (change == "component") f.Renderer.enabled = false;
                if (change == "animation") clip.SetCurve("display", typeof(Transform), "m_LocalPosition.x", AnimationCurve.Linear(0, 0, 1, .3f));
                Assert.That(stamp.Matches(f.Source), Is.False, change);
            }
            finally { Object.DestroyImmediate(controller); Object.DestroyImmediate(machine); Object.DestroyImmediate(clip); }
        }

        [TestCase("position")]
        [TestCase("rotation")]
        [TestCase("scale")]
        [TestCase("active")]
        public void ParentOnlyChangesInvalidateTheCapturedSourceAppearance(string change)
        {
            using var f = new RecoveryFixture();
            var parent = new GameObject("Source parent");
            try
            {
                f.Source.transform.SetParent(parent.transform, false);
                var sourceLocal = f.Source.transform.localToWorldMatrix;
                var stamp = ExportRecoverySourceStamp.Capture(f.Source);
                Assert.That(stamp.Matches(f.Source), Is.True);
                if (change == "position") parent.transform.position = new Vector3(1, .5f, -.3f);
                if (change == "rotation") parent.transform.rotation = Quaternion.Euler(0, 25, 0);
                if (change == "scale") parent.transform.localScale = Vector3.one * 1.1f;
                if (change == "active") parent.SetActive(false);
                Assert.That(stamp.Matches(f.Source), Is.False, change);
                if (change != "active") Assert.That(f.Source.transform.localToWorldMatrix, Is.Not.EqualTo(sourceLocal));
                Assert.That(f.Source.transform.localPosition, Is.EqualTo(Vector3.zero));
                Assert.That(f.Renderer.sharedMaterial, Is.SameAs(f.Material));
            }
            finally { f.Source.transform.SetParent(null, true); Object.DestroyImmediate(parent); }
        }

        [Test]
        public void DynamicTextureFrameUpdatesKeepTheDiagnosticUsableButDescriptorChangesInvalidateIt()
        {
            using var f = new RecoveryFixture();
            var dynamicTexture = new RenderTexture(16, 16, 0);
            try
            {
                f.Material.SetTexture("_Main2ndTex", dynamicTexture);
                var stamp = ExportRecoverySourceStamp.Capture(f.Source);
                dynamicTexture.IncrementUpdateCount();
                Assert.That(stamp.Matches(f.Source), Is.True, "A live image update must not make its omission recipe impossible to use.");
                dynamicTexture.width = 32;
                Assert.That(stamp.Matches(f.Source), Is.False, "Changes to the texture descriptor require reinspection.");
            }
            finally { Object.DestroyImmediate(dynamicTexture); }
        }

        [Test]
        public void StaticTextureImageUpdatesInvalidateAPreviousAppearanceComparison()
        {
            using var f = new RecoveryFixture();
            var texture = new Texture2D(2, 2);
            try
            {
                f.Material.SetTexture("_MainTex", texture);
                var stamp = ExportRecoverySourceStamp.Capture(f.Source);
                texture.IncrementUpdateCount();
                Assert.That(stamp.Matches(f.Source), Is.False, "A saved-image change must invalidate the old comparison.");
            }
            finally { Object.DestroyImmediate(texture); }
        }

        [TestCase("環境確認", "environment")]
        [TestCase("原本検査", "source-validation")]
        [TestCase("表情メニュー読込", "expression-menu")]
        [TestCase("コピー作成", "copy")]
        [TestCase("ビルド処理", "preparation")]
        [TestCase("基準形評価", "neutral")]
        [TestCase("状態確定", "appearance")]
        [TestCase("材質保存", "material-snapshot")]
        [TestCase("揺れ物変換", "physbone")]
        [TestCase("VRM変換", "vrm")]
        [TestCase("出力検査", "output-validation")]
        [TestCase("完了", "complete")]
        [TestCase("PRIVATE_STAGE_983", "other")]
        public void SharedDiagnosticClassifiesExportStagesWithoutExposingStageOrErrorDetails(string stage, string expected)
        {
            var privatePath = Path.Combine(Path.GetTempPath(), "private-stage-input-983", "avatar.fbx");
            var report = ExportRecoveryReport.FromException(null, new InvalidOperationException(privatePath), stage);
            var text = report.BuildSupportText().Replace("\r\n", "\n");
            Assert.That(text, Does.Contain("\nStage: " + expected + "\n"));
            Assert.That(text, Does.Not.Contain(stage));
            Assert.That(text, Does.Not.Contain(privatePath));
            Assert.That(text, Does.Not.Contain("private-stage-input-983"));
        }

        [Test]
        public void SharedDiagnosticDoesNotContainPrivateErrorPathsOrAvatarNames()
        {
            using var f = new RecoveryFixture();
            f.Source.name = "PRIVATE_AVATAR_467";
            f.Material.name = "PRIVATE_MATERIAL_842";
            var privatePath = Path.Combine(Path.GetTempPath(), "private-input-671", "avatar.fbx");
            var report = ExportRecoveryReport.FromException(f.Source, new IOException(privatePath + " " + f.Source.name + " " + f.Material.name));
            var text = report.BuildSupportText();
            Assert.That(text, Does.Not.Contain(privatePath));
            Assert.That(text, Does.Not.Contain(f.Source.name));
            Assert.That(text, Does.Not.Contain(f.Material.name));
            Assert.That(text, Does.Not.Contain("private-input-671"));
        }

        [Test]
        public async Task AudioLinkFailureCanRetryAndReloadAsStandardMToonWithoutChangingSource()
        {
            using var f = new AttachmentConnectionTests.Fixture();
            var material = new Material(Shader.Find("lilToon")) { name = "AudioLink fixture" };
            Vrm10Instance loaded = null;
            try
            {
                material.SetFloat("_UseAudioLink", 1);
                var skins = f.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
                foreach (var skin in skins) skin.sharedMaterial = material;
                var report = new ExportRecoveryReport();
                byte[] Export(ExportRecoveryOptions options) => UniVrmOneClickExporter.Export(f.Source, "Recovery test", "Tests",
                    exporterVersion: "0.11.5", lilToonVersion: "2.3.4", gimmickOptions: new ExportGimmickOptions { AutoExclude = false },
                    blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None }, recoveryOptions: options, recoveryReport: report);
                Assert.Throws<NotSupportedException>(() => Export(null));
                var action = report.Diagnostics.Single(d => d.Action?.Kind == ExportRecoveryActionKind.DisableAudioLink).Action;
                var bytes = Export(Options(action));
                loaded = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Where(m => m != null).All(m => m.shader.name == "VRM10/MToon10"), Is.True);
                Assert.That(material.GetFloat("_UseAudioLink"), Is.EqualTo(1));
                Assert.That(skins.All(s => s.sharedMaterial == material && s.sharedMesh == f.Mesh && s.GetBlendShapeWeight(0) == 35), Is.True);
            }
            finally { if (loaded != null) Object.DestroyImmediate(loaded.gameObject); Object.DestroyImmediate(material); }
        }

        [TestCase("2nd")]
        [TestCase("3rd")]
        public void DynamicOptionalLayerActualFailureHasARecipeAndFullExportRetrySucceeds(string layer)
        {
            using var f = new AttachmentConnectionTests.Fixture();
            var material = new Material(Shader.Find("lilToon"));
            var dynamicTexture = new RenderTexture(16, 16, 0);
            try
            {
                var flag = layer == "2nd" ? "_UseMain2ndTex" : "_UseMain3rdTex";
                var slot = layer == "2nd" ? "_Main2ndTex" : "_Main3rdTex";
                var kind = layer == "2nd" ? ExportRecoveryActionKind.OmitSecondLayer : ExportRecoveryActionKind.OmitThirdLayer;
                material.SetFloat(flag, 1); material.SetTexture(slot, dynamicTexture);
                foreach (var skin in f.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial = material;
                var report = new ExportRecoveryReport();
                byte[] Export(ExportRecoveryOptions options) => UniVrmOneClickExporter.Export(f.Source, "Layer recovery", "Tests",
                    exporterVersion: "0.11.5", lilToonVersion: "2.3.4", blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None },
                    recoveryOptions: options, recoveryReport: report);
                Assert.Catch<Exception>(() => Export(null));
                var diagnostic = report.Diagnostics.Single(d => d.Action?.Kind == kind);
                Assert.That(diagnostic.Action.Material, Is.SameAs(material));
                Assert.That(diagnostic.LostEffect, Is.Not.Empty);
                var bytes = Export(Options(diagnostic.Action));
                Assert.DoesNotThrow(() => LilToonGlbExtension.Validate(bytes));
                Assert.That(report.Succeeded, Is.True);
                Assert.That(material.GetFloat(flag), Is.EqualTo(1));
                Assert.That(material.GetTexture(slot), Is.SameAs(dynamicTexture));
                Assert.That(f.Source.GetComponentsInChildren<SkinnedMeshRenderer>().All(s => s.sharedMaterial == material && s.sharedMesh == f.Mesh), Is.True);
            }
            finally { Object.DestroyImmediate(dynamicTexture); Object.DestroyImmediate(material); }
        }

        static NotSupportedException AudioLinkFailure(Material material) => ExportRecoveryFailure.At(new NotSupportedException("AudioLinkは外部連携のため今回の再現対象外です。"), "audio-link", material);
        static ExportRecoveryOptions Options(ExportRecoveryAction action) { var result = new ExportRecoveryOptions(); result.Actions.Add(action); return result; }

        internal sealed class RecoveryFixture : IDisposable
        {
            internal readonly GameObject Source = new GameObject("Recovery source");
            internal readonly Material Material = new Material(Shader.Find("lilToon")) { name = "Same name" };
            internal readonly Mesh Mesh = new Mesh { name = "Shared fixture mesh" };
            internal readonly MeshRenderer Renderer;
            internal RecoveryFixture()
            {
                Mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
                Mesh.triangles = new[] { 0, 1, 2 };
                Mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
                Mesh.RecalculateNormals();
                Renderer = AddRenderer("display", Material);
            }
            internal MeshRenderer AddRenderer(string name, Material material)
            {
                var go = new GameObject(name); go.transform.SetParent(Source.transform);
                go.AddComponent<MeshFilter>().sharedMesh = Mesh;
                var renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material; return renderer;
            }
            public void Dispose() { Object.DestroyImmediate(Source); Object.DestroyImmediate(Material); Object.DestroyImmediate(Mesh); }
        }
    }
}
