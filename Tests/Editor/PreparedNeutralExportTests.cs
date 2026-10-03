using System;
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

        [TestCase(false)]
        [TestCase(true)]
        public async Task AutomaticBlinkUsesFxOpenNeutralWhenSerializedUnifiedClosureIsFullyClosed(bool fullLilToon)
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
                    "The static face has no remaining closure range; export must evaluate its FX defaults first.");
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "FX neutral automatic blink", "Tests",
                    exporterVersion: fullLilToon ? "prepared-neutral-regression" : null,
                    lilToonVersion: fullLilToon ? "2.3.4" : null);
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                imported.Runtime.Process();
                Assert.That(imported.Vrm.Expression.Blink, Is.Not.Null);
                foreach (var skin in imported.GetComponentsInChildren<SkinnedMeshRenderer>())
                    Assert.That(skin.sharedMesh.vertices[0].y, Is.EqualTo(fixture.Mesh.vertices[0].y).Within(.00001f));
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
                Assert.That(NdmfPreparationTests.FakeProcessor.Calls, Is.EqualTo(1));
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
                Assert.That(output.sharedMesh.vertices[0].y, Is.EqualTo(fixture.Mesh.vertices[0].y + .02f).Within(.00001f),
                    "The generated multi-frame channel must supply neutral75 on the final renderer path.");
                output.BakeMesh(baked); var neutralY = baked.vertices[0].y;
                imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom("UE/JawOpen"), 1);
                imported.Runtime.Process(); output.BakeMesh(baked);
                Assert.That(baked.vertices[0].y, Is.EqualTo(neutralY - .015f).Within(.00001f),
                    "The authored0.25 endpoint must reach source25 rather than scale the remaining75-to100 interval.");
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
