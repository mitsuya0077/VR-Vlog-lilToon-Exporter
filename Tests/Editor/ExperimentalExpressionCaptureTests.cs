using System;
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
    public sealed class ExperimentalExpressionCaptureTests
    {
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

        [Test]
        public void ExistingMenuCandidateCanBeDiscoveredPreviewedAndCapturedWithoutChangingItsController()
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
                var clip = new AnimationClip();
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail"), AnimationCurve.Constant(0, 1, 75));
                AssetDatabase.AddObjectToAsset(clip, controller); smile.motion = clip; machine.defaultState = neutral;
                var transition = neutral.AddTransition(smile); transition.hasExitTime = false; transition.duration = 0;
                transition.AddCondition(AnimatorConditionMode.Equals, 1, "Face");
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
                using var session = new ExperimentalExpressionCaptureSession(fixture.Source);
                session.DiscoverCandidates();
                var candidate = session.Candidates.Entries.First(entry => entry.Name == "Smile");
                Assert.That(candidate.Error, Is.Null);
                session.PreviewCandidate(candidate); session.Capture("from menu");
                Assert.That(session.Channels.First(channel => channel.Path == "Front").Renderer.GetBlendShapeWeight(0), Is.EqualTo(75));
                Assert.That(controller.layers[0].stateMachine.states.Length, Is.EqualTo(2));
                Assert.That(fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(35));
            }
            finally { AssetDatabase.DeleteAsset(folder); }
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

        static Type Sdk(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(name)).FirstOrDefault(type => type != null);
    }
}
