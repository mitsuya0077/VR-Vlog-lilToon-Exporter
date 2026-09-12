using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UniGLTF;
using UniVRM10;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class BlinkExportTests
    {
        GameObject root, copy;
        readonly List<Object> owned = new List<Object>();
        readonly List<Mesh> generated = new List<Mesh>();

        [SetUp] public void SetUp() => root = new GameObject("avatar");
        [TearDown] public void TearDown()
        {
            if (copy != null) Object.DestroyImmediate(copy);
            Object.DestroyImmediate(root);
            foreach (var value in generated) if (value != null) Object.DestroyImmediate(value);
            foreach (var value in owned) if (value != null) Object.DestroyImmediate(value);
            generated.Clear(); owned.Clear();
        }

        SkinnedMeshRenderer Skin(params string[] names)
        {
            var go = new GameObject("face"); go.transform.SetParent(root.transform, false);
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            foreach (var name in names)
                mesh.AddBlendShapeFrame(name, 100, new[] { Vector3.up * .2f, Vector3.zero, Vector3.zero }, new Vector3[3], new Vector3[3]);
            owned.Add(mesh);
            var skin = go.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = mesh;
            return skin;
        }

        [Test] public void EyeSpacingNeverWinsOverAnEyelidShape()
        {
            var skin = Skin("eye_close", "vrc.Blink", "eye_blink_1", "eye_blink_1_L", "eye_blink_1_R");
            var resolved = BlinkExportSession.Resolve(root);
            Assert.That(resolved.Slots[0].Single().Shape, Is.EqualTo("vrc.Blink"));
            Assert.That(resolved.Slots[1].Single().Shape, Is.EqualTo("eye_blink_1_L"));
            Assert.That(resolved.Slots[2].Single().Shape, Is.EqualTo("eye_blink_1_R"));
            Assert.That(resolved.Slots[0].Single().Renderer, Is.SameAs(skin));
        }

        [TestCase("eye_close")][TestCase("previewBlink")][TestCase("eye_close_left")]
        public void AmbiguousOrPartialNamesNeedAnExplicitChoice(string name)
        {
            Skin(name);
            Assert.Throws<InvalidOperationException>(() => BlinkExportSession.Resolve(root));
            Assert.That(BlinkExportSession.Resolve(root, new BlinkExportOptions { Mode = BlinkExportMode.None }).Disabled, Is.True);
        }

        [Test] public void LeftAndRightMustBelongToOneNameFamily()
        {
            Skin("Blink_L", "eye_blink_1_R");
            Assert.Throws<InvalidOperationException>(() => BlinkExportSession.Resolve(root));
        }

        [Test] public void DuplicateSemanticNamesAreNotGuessed()
        {
            Skin("Blink", "BLINK");
            Assert.Throws<InvalidOperationException>(() => BlinkExportSession.Resolve(root));
        }

        [Test] public void AllBlinkRenderersMustSupportTheIndividualPair()
        {
            Skin("Blink", "Blink_L", "Blink_R");
            Skin("Blink");
            var result = BlinkExportSession.Resolve(root);
            Assert.That(result.Slots[0].Count, Is.EqualTo(2));
            Assert.That(result.Slots[1], Is.Empty);
            Assert.That(result.Slots[2], Is.Empty);
        }

        [Test] public void ManualChoiceOverridesInferenceAndRejectsForeignOrRemovedShapes()
        {
            var skin = Skin("Blink", "custom_eyelids");
            var options = new BlinkExportOptions { Mode = BlinkExportMode.Manual };
            options.Both.Add(new BlinkShapeBinding { Renderer = skin, Shape = "custom_eyelids", Weight = 65 });
            Assert.That(BlinkExportSession.Resolve(root, options).Slots[0].Single().Weight, Is.EqualTo(65));
            Assert.Throws<InvalidOperationException>(() => BlinkExportSession.Resolve(root, options, _ => true));
            options.Both[0].Shape = "deleted";
            Assert.Throws<InvalidOperationException>(() => BlinkExportSession.Resolve(root, options));
            options.Both[0].Shape = "Blink";
            skin.transform.SetParent(null);
            try { Assert.Throws<InvalidOperationException>(() => BlinkExportSession.Resolve(root, options)); }
            finally { Object.DestroyImmediate(skin.gameObject); }
        }

        [Test] public void PairOnlyEyelashesContributeToTheBilateralFallback()
        {
            Skin("Blink");
            Skin("Blink_L", "Blink_R");
            var result = BlinkExportSession.Resolve(root);
            Assert.That(result.Slots[0].Select(b => b.Shape), Is.EquivalentTo(new[] { "Blink", "Blink_L", "Blink_R" }));
            Assert.That(result.Slots[1], Is.Empty);
            Assert.That(result.Slots[2], Is.Empty);
        }

        [Test] public void EmptyAuthoredVrmClipIsPreservedUnlessExplicitlyOverridden()
        {
            var skin = Skin("Blink", "custom");
            var vrm = ScriptableObject.CreateInstance<VRM10Object>(); owned.Add(vrm);
            var clip = ScriptableObject.CreateInstance<VRM10Expression>(); owned.Add(clip);
            vrm.Expression.Blink = clip;
            root.AddComponent<Vrm10Instance>().Vrm = vrm;
            var resolved = BlinkExportSession.Resolve(root);
            Assert.That(resolved.PreserveAuthored, Is.True);
            Assert.That(resolved.Slots.All(s => s.Count == 0), Is.True);
            var manual = new BlinkExportOptions { Mode = BlinkExportMode.Manual };
            manual.Both.Add(new BlinkShapeBinding { Renderer = skin, Shape = "custom" });
            Assert.That(BlinkExportSession.Resolve(root, manual).PreserveAuthored, Is.False);
        }

        [Test] public void SourceRendererIdentitySurvivesRenameAndSiblingReorder()
        {
            var first = Skin("Blink"); var second = Skin("Blink");
            var source = BlinkExportSession.Resolve(root);
            copy = Object.Instantiate(root);
            var mapped = source.ForClone(root, copy);
            var originalTarget = mapped.Slots[0][0].Renderer;
            originalTarget.name = "renamed";
            originalTarget.transform.SetAsLastSibling();
            mapped.Validate(copy);
            Assert.That(mapped.Slots[0][0].Renderer, Is.SameAs(originalTarget));
            Assert.That(mapped.Slots[0][0].Renderer, Is.Not.SameAs(first));
            Object.DestroyImmediate(originalTarget.gameObject);
            Assert.Throws<InvalidOperationException>(() => mapped.Validate(copy));
        }

        [Test] public void PartialClosureIsBakedRelativeToTheAuthoredRestWithoutTouchingSource()
        {
            var skin = Skin("custom"); skin.SetBlendShapeWeight(0, 20);
            var options = new BlinkExportOptions { Mode = BlinkExportMode.Manual };
            options.Both.Add(new BlinkShapeBinding { Renderer = skin, Shape = "custom", Weight = 50 });
            var resolved = BlinkExportSession.Resolve(root, options);
            copy = Object.Instantiate(root);
            var session = resolved.ForClone(root, copy); session.Bake(copy, generated);
            AvatarBaseShape.Preserve(copy, copy, generated, new List<string>());
            var binding = session.Slots[0].Single(); var mesh = binding.Renderer.sharedMesh;
            var delta = new Vector3[3];
            mesh.GetBlendShapeFrameVertices(mesh.GetBlendShapeIndex(binding.Shape), 0, delta, null, null);
            Assert.That(mesh.vertices[0].y, Is.EqualTo(.04f).Within(.00001));
            Assert.That(delta[0].y, Is.EqualTo(.06f).Within(.00001));
            Assert.That(skin.sharedMesh.blendShapeCount, Is.EqualTo(1));
            Assert.That(skin.sharedMesh.vertices[0], Is.EqualTo(Vector3.zero));
            Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(20));
        }

        [Test] public void NoneProducesAnInertBindingWithoutDestroyingAuthoredMorphs()
        {
            var skin = Skin("Blink");
            var resolved = BlinkExportSession.Resolve(root, new BlinkExportOptions { Mode = BlinkExportMode.None });
            copy = Object.Instantiate(root);
            var session = resolved.ForClone(root, copy); session.Bake(copy, generated);
            Assert.That(session.Slots.All(s => s.Count == 1), Is.True);
            var row = session.Slots[0].Single(); var mesh = row.Renderer.sharedMesh;
            var delta = new Vector3[3]; mesh.GetBlendShapeFrameVertices(mesh.GetBlendShapeIndex(row.Shape), 0, delta, null, null);
            Assert.That(delta.All(v => v == Vector3.zero), Is.True);
            Assert.That(mesh.GetBlendShapeIndex("Blink"), Is.EqualTo(0));
            Assert.That(skin.sharedMesh.blendShapeCount, Is.EqualTo(1));
        }

        [Test] public void DescriptorBlinkUsesOnlyTheClosedSlotAndValidatesMissingIndices()
        {
            var descriptorType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor", false)).FirstOrDefault(t => t != null);
            if (descriptorType == null) Assert.Ignore("VRChat SDK is required for the descriptor integration test.");
            var skin = Skin("custom_closed", "custom_up", "custom_down", "Blink");
            var descriptor = root.AddComponent(descriptorType);
            descriptorType.GetField("enableEyeLook").SetValue(descriptor, true);
            var settingsField = descriptorType.GetField("customEyeLookSettings"); var settings = settingsField.GetValue(descriptor);
            var settingsType = settings.GetType(); var lidType = settingsType.GetField("eyelidType");
            lidType.SetValue(settings, Enum.Parse(lidType.FieldType, "Blendshapes"));
            settingsType.GetField("eyelidsSkinnedMesh").SetValue(settings, skin);
            settingsType.GetField("eyelidsBlendshapes").SetValue(settings, new[] { 0, 1, 2 });
            settingsField.SetValue(descriptor, settings);
            Assert.That(BlinkExportSession.Resolve(root).Slots[0].Single().Shape, Is.EqualTo("custom_closed"));
            settingsType.GetField("eyelidsBlendshapes").SetValue(settings, new[] { -1, 1, 2 }); settingsField.SetValue(descriptor, settings);
            Assert.That(BlinkExportSession.Resolve(root).Slots[0].Single().Shape, Is.EqualTo("Blink"));
        }

        [TestCase(false)][TestCase(true)]
        public void ActualUniVrmExportBindsGeneratedTargetsToTheirFinalNodes(bool modularAvatar)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            if (modularAvatar)
            {
                var proxyType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("nadena.dev.modular_avatar.core.ModularAvatarBoneProxy")).FirstOrDefault(t => t != null);
                var markerType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("nadena.dev.ndmf.runtime.components.NDMFAvatarRoot")).FirstOrDefault(t => t != null);
                if (proxyType == null || markerType == null) Assert.Ignore("Requires Modular Avatar and NDMF.");
                fixture.Source.AddComponent(markerType);
                var hair = fixture.Source.transform.Find("Independent hair");
                fixture.Source.transform.Find("Front").SetParent(hair, true);
                var proxy = hair.gameObject.AddComponent(proxyType);
                proxyType.GetProperty("target").SetValue(proxy, fixture.Source.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Head));
            }
            var material = new Material(Shader.Find("lilToon")); owned.Add(material);
            foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial = material;
            var zeros = new Vector3[fixture.Mesh.vertexCount];
            fixture.Mesh.AddBlendShapeFrame("Blink", 100, zeros, zeros, zeros);
            var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Blink integration", "Tests");
            var glb = GlbDocument.Read(bytes);
            var vrm = (Dictionary<string, object>)((Dictionary<string, object>)glb.Json["extensions"])["VRMC_vrm"];
            var preset = (Dictionary<string, object>)((Dictionary<string, object>)vrm["expressions"])["preset"];
            var binds = (List<object>)((Dictionary<string, object>)preset["blink"])["morphTargetBinds"];
            Assert.That(binds.Count, Is.EqualTo(fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>().Length));
            foreach (Dictionary<string, object> binding in binds)
            {
                var node = (Dictionary<string, object>)((List<object>)glb.Json["nodes"])[Convert.ToInt32(binding["node"])];
                var mesh = (Dictionary<string, object>)((List<object>)glb.Json["meshes"])[Convert.ToInt32(node["mesh"])];
                var names = (List<object>)((Dictionary<string, object>)mesh["extras"])["targetNames"];
                Assert.That((string)names[Convert.ToInt32(binding["index"])], Does.StartWith("__VRVlog_Blink_"));
            }
            Assert.That(fixture.Mesh.blendShapeCount, Is.EqualTo(2));
        }

        [TestCase(false)][TestCase(true)]
        public void ActualExportPreservesAuthoredClipsIncludingExplicitEmpty(bool empty)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var material = new Material(Shader.Find("lilToon")); owned.Add(material);
            foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial = material;
            var vrm = ScriptableObject.CreateInstance<VRM10Object>(); owned.Add(vrm);
            var clip = ScriptableObject.CreateInstance<VRM10Expression>(); owned.Add(clip);
            clip.IsBinary = true;
            clip.MorphTargetBindings = empty ? Array.Empty<MorphTargetBinding>() : new[] { new MorphTargetBinding("Front", 0, .6f) };
            vrm.Expression.Blink = clip; fixture.Source.AddComponent<Vrm10Instance>().Vrm = vrm;
            var glb = GlbDocument.Read(UniVrmOneClickExporter.Export(fixture.Source, "Authored blink", "Tests"));
            var extension = (Dictionary<string, object>)((Dictionary<string, object>)glb.Json["extensions"])["VRMC_vrm"];
            var presets = (Dictionary<string, object>)((Dictionary<string, object>)extension["expressions"])["preset"];
            var blink = (Dictionary<string, object>)presets["blink"];
            Assert.That(blink["isBinary"], Is.EqualTo(true));
            var count = blink.TryGetValue("morphTargetBinds", out var raw) ? ((List<object>)raw).Count : 0;
            Assert.That(count, Is.EqualTo(empty ? 0 : 1));
            Assert.That(presets.ContainsKey("blinkLeft"), Is.False);
            Assert.That(vrm.Expression.Blink, Is.SameAs(clip));
            Assert.That(clip.MorphTargetBindings.Length, Is.EqualTo(empty ? 0 : 1));
            if (!empty) Assert.That(clip.MorphTargetBindings[0].Weight, Is.EqualTo(.6f));
        }

        [Test] public void PreviewOwnsItsCopyAndCleansItUpWithoutChangingTheSource()
        {
            var skin = Skin("Blink");
            var material = new Material(Shader.Find("Standard")); owned.Add(material); skin.sharedMaterial = material;
            skin.SetBlendShapeWeight(0, 15);
            var original = skin.sharedMesh;
            var window = ScriptableObject.CreateInstance<BlinkPreviewWindow>();
            try
            {
                window.Prepare(root, new BlinkExportOptions(), Array.Empty<GameObject>(), new ExportGimmickOptions { AutoExclude = false });
                var field = typeof(BlinkPreviewWindow).GetField("copy", BindingFlags.Instance | BindingFlags.NonPublic);
                var previewCopy = (GameObject)field.GetValue(window);
                Assert.That(previewCopy, Is.Not.Null);
                Assert.That(previewCopy, Is.Not.SameAs(root));
                window.Cleanup();
                Assert.That(previewCopy == null, Is.True);
                Assert.That(skin.sharedMesh, Is.SameAs(original));
                Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(15));
            }
            finally { Object.DestroyImmediate(window); }
        }
    }
}
