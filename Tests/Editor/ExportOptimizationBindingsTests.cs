using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UniGLTF.Extensions.VRMC_vrm;
using UniVRM10;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class ExportOptimizationBindingsTests
    {
        readonly List<Object> owned = new List<Object>();
        GameObject source, clone;

        [SetUp]
        public void SetUp() => source = Own(new GameObject("source"));

        [TearDown]
        public void TearDown()
        {
            if (clone != null) Object.DestroyImmediate(clone);
            foreach (var asset in owned.AsEnumerable().Reverse()) if (asset != null) Object.DestroyImmediate(asset);
            owned.Clear();
        }

        T Own<T>(T value) where T : Object { owned.Add(value); return value; }

        SkinnedMeshRenderer Skin(GameObject root, string name, string shape, bool zero = false)
        {
            var child = new GameObject(name); child.transform.SetParent(root.transform, false);
            var skin = child.AddComponent<SkinnedMeshRenderer>();
            var mesh = Own(new Mesh { name = name + " mesh" });
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.AddBlendShapeFrame(shape, 100, Enumerable.Repeat(zero ? Vector3.zero : Vector3.forward * .1f, 3).ToArray(), null, null);
            skin.sharedMesh = mesh;
            return skin;
        }

        static object RegisteredAssetIdentity(object registry, Object asset)
        {
            var registryInterface = registry.GetType().GetInterface("nadena.dev.ndmf.IObjectRegistry");
            var getReference = registryInterface?.GetMethod("GetReference", new[] { typeof(Object), typeof(bool) });
            Assert.That(getReference, Is.Not.Null);
            return getReference.Invoke(registry, new object[] { asset, true }) ?? asset;
        }

        static float PinnedDuplicatePlayback(GameObject root, VRM10Expression clip, string path, float input)
        {
            // Execute the installed UniVRM merger on an independent native
            // renderer; it keeps the first declaration rather than summing them.
            var mergerType = typeof(Vrm10Instance).Assembly.GetType("UniVRM10.MorphTargetBindingMerger");
            Assert.That(mergerType, Is.Not.Null);
            var copy = Object.Instantiate(root);
            try
            {
                var key = new ExpressionKey(ExpressionPreset.happy);
                LogAssert.Expect(LogType.Warning, new Regex("^Duplicate MorphTargetBinding found:"));
                var merger = Activator.CreateInstance(mergerType, new object[] {
                    new Dictionary<ExpressionKey, VRM10Expression> { [key] = clip }, copy.transform });
                mergerType.GetMethod("AccumulateValue").Invoke(merger, new object[] { key, input });
                mergerType.GetMethod("Apply").Invoke(merger, null);
                return copy.transform.Find(path).GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(clip.MorphTargetBindings[0].Index);
            }
            finally { Object.DestroyImmediate(copy); }
        }

        [Test]
        public void DestroyedOriginalRenderersMapIndependentlyDespiteIdenticalNames()
        {
            Skin(source, "first", "__VRVlog_Menu_same");
            Skin(source, "second", "__VRVlog_Menu_same");
            clone = Object.Instantiate(source);
            var originals = clone.GetComponentsInChildren<SkinnedMeshRenderer>();
            using var bindings = ExportOptimizationBindings.Capture(clone);
            var marker = clone.GetComponent<ExportOptimizationMarker>();
            var target = Skin(clone, "merged", "first renamed");
            target.sharedMesh.AddBlendShapeFrame("second renamed", 100, Enumerable.Repeat(Vector3.up * .2f, 3).ToArray(), null, null);
            foreach (var route in marker.Morphs)
            {
                route.Shape = route.SourceRendererId == originals[0].GetInstanceID() ? "first renamed" : "second renamed";
                route.Renderer = target;
            }
            Object.DestroyImmediate(originals[0].gameObject); Object.DestroyImmediate(originals[1].gameObject);
            bindings.ValidateAndApply();
            Assert.That(bindings.MapMorph(originals[0], "__VRVlog_Menu_same").Shape, Is.EqualTo("first renamed"));
            Assert.That(bindings.MapMorph(originals[1], "__VRVlog_Menu_same").Shape, Is.EqualTo("second renamed"));
            Assert.That(source.GetComponentsInChildren<SkinnedMeshRenderer>().Length, Is.EqualTo(2));
            Assert.That(source.GetComponent<ExportOptimizationMarker>(), Is.Null);
        }

        [Test]
        public void LosingAnEffectiveEndpointCannotBeHiddenByANameOnAnotherMesh()
        {
            Skin(source, "face", "__VRVlog_Menu_face");
            Skin(source, "other", "__VRVlog_Menu_other");
            clone = Object.Instantiate(source);
            using var bindings = ExportOptimizationBindings.Capture(clone);
            var skin = clone.transform.Find("face").GetComponent<SkinnedMeshRenderer>();
            var empty = Own(Object.Instantiate(skin.sharedMesh));
            empty.ClearBlendShapes(); empty.AddBlendShapeFrame("__VRVlog_Menu_face", 100, new Vector3[3], null, null);
            skin.sharedMesh = empty;
            var error = Assert.Throws<InvalidOperationException>(() => bindings.ValidateAndApply());
            Assert.That(error.Message, Does.Contain("face"));
            Assert.That(source.transform.Find("face").GetComponent<SkinnedMeshRenderer>().sharedMesh.GetBlendShapeIndex("__VRVlog_Menu_face"), Is.EqualTo(0));
        }

        [Test]
        public void OptimizedAwayNoBlinkTargetIsRecreatedWithoutChangingTheSourceMesh()
        {
            var original = Skin(source, "face", "__VRVlog_BlinkNone_original", true);
            clone = Object.Instantiate(source);
            var skin = clone.GetComponentInChildren<SkinnedMeshRenderer>();
            using var bindings = ExportOptimizationBindings.Capture(clone);
            var route = clone.GetComponent<ExportOptimizationMarker>().Morphs.Single();
            route.Removed = true;
            bindings.ValidateAndApply();
            var mapped = bindings.MapMorph(skin, "__VRVlog_BlinkNone_original");
            Assert.That(mapped.Renderer.sharedMesh, Is.Not.SameAs(original.sharedMesh));
            Assert.That(mapped.Renderer.sharedMesh.GetBlendShapeIndex(mapped.Shape), Is.GreaterThanOrEqualTo(0));
            Assert.That(AvatarBaseShape.HasUsableMorphEndpoint(mapped.Renderer, mapped.Renderer.sharedMesh.GetBlendShapeIndex(mapped.Shape)), Is.False);
            Assert.That(original.sharedMesh.blendShapeCount, Is.EqualTo(1));
            Assert.That(original.sharedMesh.GetBlendShapeName(0), Is.EqualTo("__VRVlog_BlinkNone_original"));
        }

        [Test]
        public void AuthoredMorphAndMaterialBindingsFollowTheirMappedRoutesOnPrivateAssets()
        {
            Skin(source, "face", "original shape");
            var material = Own(new Material(Shader.Find("Unlit/Color")) { name = "original material" });
            source.GetComponentInChildren<SkinnedMeshRenderer>().sharedMaterial = material;
            var settings = Own(ScriptableObject.CreateInstance<VRM10Object>());
            var expression = Own(ScriptableObject.CreateInstance<VRM10Expression>());
            expression.name = "authored";
            expression.MorphTargetBindings = new[] { new MorphTargetBinding("face", 0, .4f) };
            expression.MaterialColorBindings = new[] { new MaterialColorBinding { MaterialName = material.name, TargetValue = Color.red } };
            settings.Expression.Happy = expression;
            source.AddComponent<Vrm10Instance>().Vrm = settings;
            clone = Object.Instantiate(source);
            using var bindings = ExportOptimizationBindings.Capture(clone);
            var target = Skin(clone, "mapped face", "mapped shape");
            var renamedMaterial = Own(new Material(material) { name = "mapped material" });
            target.sharedMaterials = new[] { material, renamedMaterial };
            var marker = clone.GetComponent<ExportOptimizationMarker>();
            marker.Morphs.Single().Renderer = target; marker.Morphs.Single().Shape = "mapped shape";
            marker.Materials.Single().Renderer = target; marker.Materials.Single().Slot = 1;
            Object.DestroyImmediate(clone.transform.Find("face").gameObject);
            bindings.ValidateAndApply();
            var copiedSettings = clone.GetComponent<Vrm10Instance>().Vrm;
            Assert.That(copiedSettings, Is.Not.SameAs(settings));
            var copied = copiedSettings.Expression.Happy;
            Assert.That(copied, Is.Not.SameAs(expression));
            Assert.That(copied.MorphTargetBindings.Single().RelativePath, Is.EqualTo("mapped face"));
            Assert.That(copied.MorphTargetBindings.Single().Weight, Is.EqualTo(.4f));
            Assert.That(copied.MaterialColorBindings.Single().MaterialName, Is.EqualTo("mapped material"));
            Assert.That(expression.MorphTargetBindings.Single().RelativePath, Is.EqualTo("face"));
            Assert.That(expression.MaterialColorBindings.Single().MaterialName, Is.EqualTo("original material"));
            Assert.That(source.GetComponent<Vrm10Instance>().Vrm, Is.SameAs(settings));
        }

        [TestCase(.25f)]
        [TestCase(.75f)]
        public void CoalescedAuthoredMorphRoutesRegisterOneEqualWeight(float weight)
        {
            Skin(source, "first", "original"); Skin(source, "second", "original");
            var settings = Own(ScriptableObject.CreateInstance<VRM10Object>());
            var expression = Own(ScriptableObject.CreateInstance<VRM10Expression>()); expression.name = "authored";
            expression.MorphTargetBindings = new[] { new MorphTargetBinding("first", 0, weight), new MorphTargetBinding("second", 0, weight) };
            settings.Expression.Happy = expression; source.AddComponent<Vrm10Instance>().Vrm = settings;
            clone = Object.Instantiate(source);
            using var bindings = ExportOptimizationBindings.Capture(clone);
            var target = Skin(clone, "merged", "coalesced");
            foreach (var route in clone.GetComponent<ExportOptimizationMarker>().Morphs) { route.Renderer = target; route.Shape = "coalesced"; }
            Object.DestroyImmediate(clone.transform.Find("first").gameObject); Object.DestroyImmediate(clone.transform.Find("second").gameObject);
            bindings.ValidateAndApply();
            var copied = clone.GetComponent<Vrm10Instance>().Vrm.Expression.Happy;
            Assert.That(copied.MorphTargetBindings.Length, Is.EqualTo(1));
            Assert.That(copied.MorphTargetBindings.Single().RelativePath, Is.EqualTo("merged"));
            Assert.That(copied.MorphTargetBindings.Single().Index, Is.EqualTo(0));
            Assert.That(copied.MorphTargetBindings.Single().Weight, Is.EqualTo(weight));
            Assert.That(expression.MorphTargetBindings.Select(binding => binding.Weight), Is.EqualTo(new[] { weight, weight }));
            Assert.That(expression.MorphTargetBindings.Select(binding => binding.RelativePath), Is.EqualTo(new[] { "first", "second" }));
            Assert.That(source.GetComponent<Vrm10Instance>().Vrm, Is.SameAs(settings));
        }

        [Test]
        public void CoalescedAuthoredConflictingWeightsStopBeforeMeshMaterialOrSettingsMutation()
        {
            var first = Skin(source, "first", "original"); var second = Skin(source, "second", "original");
            var noOp = Skin(source, "no op", "__VRVlog_BlinkNone_original", true);
            var material = Own(new Material(Shader.Find("Unlit/Color")) { name = "same material" });
            first.sharedMaterial = material; second.sharedMaterial = material;
            var settings = Own(ScriptableObject.CreateInstance<VRM10Object>());
            var expression = Own(ScriptableObject.CreateInstance<VRM10Expression>()); expression.name = "authored conflicting";
            expression.MorphTargetBindings = new[] { new MorphTargetBinding("first", 0, .25f), new MorphTargetBinding("second", 0, .75f) };
            expression.MaterialColorBindings = new[] { new MaterialColorBinding { MaterialName = material.name, TargetValue = Color.red } };
            settings.Expression.Happy = expression; source.AddComponent<Vrm10Instance>().Vrm = settings;
            clone = Object.Instantiate(source);
            using var bindings = ExportOptimizationBindings.Capture(clone);
            var target = Skin(clone, "merged", "coalesced");
            var otherMaterial = Own(new Material(material)); target.sharedMaterials = new[] { material, otherMaterial };
            var marker = clone.GetComponent<ExportOptimizationMarker>();
            foreach (var route in marker.Morphs)
            {
                if (route.SourceShape.StartsWith("__VRVlog_BlinkNone_", StringComparison.Ordinal)) { route.Removed = true; route.Renderer = null; }
                else { route.Renderer = target; route.Shape = "coalesced"; }
            }
            marker.Materials.Single().Renderer = target; marker.Materials.Single().Slot = 0;
            Object.DestroyImmediate(clone.transform.Find("first").gameObject); Object.DestroyImmediate(clone.transform.Find("second").gameObject);
            Object.DestroyImmediate(clone.transform.Find("no op").gameObject);
            var originalMesh = target.sharedMesh; var copies = 0;
            var error = Assert.Throws<InvalidOperationException>(() => bindings.ValidateAndApply((copy, original) => copies++));
            Assert.That(error.Message, Does.Contain("authored conflicting"));
            Assert.That(error.Message, Does.Contain("適用量が衝突"));
            Assert.That(copies, Is.Zero);
            Assert.That(target.sharedMesh, Is.SameAs(originalMesh), "No-op reconstruction must not run before conflict validation.");
            Assert.That(target.sharedMaterials, Is.EqualTo(new[] { material, otherMaterial }));
            Assert.That(clone.GetComponent<Vrm10Instance>().Vrm, Is.SameAs(settings));
            Assert.That(source.GetComponent<Vrm10Instance>().Vrm, Is.SameAs(settings));
            Assert.That(expression.MorphTargetBindings.Select(binding => binding.Weight), Is.EqualTo(new[] { .25f, .75f }));
            Assert.That(noOp.sharedMesh.GetBlendShapeName(0), Is.EqualTo("__VRVlog_BlinkNone_original"));
        }

        [TestCase(0f)]
        [TestCase(.2f)]
        public void DuplicateOriginalAuthoredBindingsKeepPinnedFirstWeight(float firstWeight)
        {
            Skin(source, "face", "original");
            var settings = Own(ScriptableObject.CreateInstance<VRM10Object>());
            var expression = Own(ScriptableObject.CreateInstance<VRM10Expression>()); expression.name = "authored duplicate";
            expression.MorphTargetBindings = new[] { new MorphTargetBinding("face", 0, firstWeight), new MorphTargetBinding("face", 0, .8f) };
            settings.Expression.Happy = expression; source.AddComponent<Vrm10Instance>().Vrm = settings;
            clone = Object.Instantiate(source);
            using var bindings = ExportOptimizationBindings.Capture(clone);
            Assert.That(clone.GetComponent<ExportOptimizationMarker>().Morphs.Length, Is.EqualTo(1), "Repeated declarations must share their captured original route.");
            bindings.ValidateAndApply();
            var copied = clone.GetComponent<Vrm10Instance>().Vrm.Expression.Happy;
            Assert.That(copied.MorphTargetBindings.Length, Is.EqualTo(2), "Original duplicate declarations must remain intact.");
            Assert.That(copied.MorphTargetBindings.Select(binding => binding.Weight), Is.EqualTo(new[] { firstWeight, .8f }));
            Assert.That(copied.MorphTargetBindings.All(binding => binding.RelativePath == "face" && binding.Index == 0), Is.True);
            Assert.That(PinnedDuplicatePlayback(source, expression, "face", .5f), Is.EqualTo(firstWeight * 50).Within(.001));
            Assert.That(PinnedDuplicatePlayback(clone, copied, "face", .5f), Is.EqualTo(firstWeight * 50).Within(.001));
            Assert.That(expression.MorphTargetBindings.Select(binding => binding.Weight), Is.EqualTo(new[] { firstWeight, .8f }));
            Assert.That(source.GetComponent<Vrm10Instance>().Vrm, Is.SameAs(settings));
        }

        [TestCase(0f, 0f, false)]
        [TestCase(.2f, .2f, false)]
        [TestCase(0f, .8f, true)]
        [TestCase(.2f, .8f, true)]
        public void CoalescedDistinctAuthoredRoutesCompareTheirFirstOriginalWeights(float firstWeight, float secondWeight, bool conflict)
        {
            Skin(source, "first", "original"); Skin(source, "second", "original");
            var settings = Own(ScriptableObject.CreateInstance<VRM10Object>());
            var expression = Own(ScriptableObject.CreateInstance<VRM10Expression>()); expression.name = "authored duplicated then coalesced";
            expression.MorphTargetBindings = new[] {
                new MorphTargetBinding("first", 0, firstWeight), new MorphTargetBinding("first", 0, .8f), new MorphTargetBinding("second", 0, secondWeight) };
            settings.Expression.Happy = expression; source.AddComponent<Vrm10Instance>().Vrm = settings;
            clone = Object.Instantiate(source);
            using var bindings = ExportOptimizationBindings.Capture(clone);
            var target = Skin(clone, "merged", "coalesced");
            var marker = clone.GetComponent<ExportOptimizationMarker>();
            Assert.That(marker.Morphs.Length, Is.EqualTo(2));
            foreach (var route in marker.Morphs) { route.Renderer = target; route.Shape = "coalesced"; }
            Object.DestroyImmediate(clone.transform.Find("first").gameObject); Object.DestroyImmediate(clone.transform.Find("second").gameObject);
            var before = target.sharedMesh; var copies = 0;
            if (conflict)
            {
                var error = Assert.Throws<InvalidOperationException>(() => bindings.ValidateAndApply((copy, original) => copies++));
                Assert.That(error.Message, Does.Contain("適用量が衝突"));
                Assert.That(copies, Is.Zero); Assert.That(target.sharedMesh, Is.SameAs(before));
                Assert.That(clone.GetComponent<Vrm10Instance>().Vrm, Is.SameAs(settings));
            }
            else
            {
                bindings.ValidateAndApply();
                var copied = clone.GetComponent<Vrm10Instance>().Vrm.Expression.Happy;
                Assert.That(copied.MorphTargetBindings.Length, Is.EqualTo(2), "Keep both winning-source declarations while omitting the distinct coalesced route.");
                Assert.That(copied.MorphTargetBindings.Select(binding => binding.Weight), Is.EqualTo(new[] { firstWeight, .8f }));
                Assert.That(copied.MorphTargetBindings.All(binding => binding.RelativePath == "merged" && binding.Index == 0), Is.True);
                Assert.That(PinnedDuplicatePlayback(source, expression, "first", .5f), Is.EqualTo(firstWeight * 50).Within(.001));
                Assert.That(PinnedDuplicatePlayback(clone, copied, "merged", .5f), Is.EqualTo(firstWeight * 50).Within(.001));
            }
            Assert.That(expression.MorphTargetBindings.Select(binding => binding.Weight), Is.EqualTo(new[] { firstWeight, .8f, secondWeight }));
            Assert.That(source.GetComponent<Vrm10Instance>().Vrm, Is.SameAs(settings));
        }

        [Test]
        public void ClonedPresetCannotOverwriteRemappedSnapshotAndNewPresetIsRetained()
        {
            Skin(source, "first", "original"); Skin(source, "second", "original");
            var settings = Own(ScriptableObject.CreateInstance<VRM10Object>());
            var expression = Own(ScriptableObject.CreateInstance<VRM10Expression>()); expression.name = "authored Happy";
            expression.MorphTargetBindings = new[] { new MorphTargetBinding("first", 0, .4f), new MorphTargetBinding("second", 0, .4f) };
            settings.Expression.Happy = expression; source.AddComponent<Vrm10Instance>().Vrm = settings;
            clone = Object.Instantiate(source);
            using var bindings = ExportOptimizationBindings.Capture(clone);
            var target = Skin(clone, "merged", "coalesced");
            foreach (var route in clone.GetComponent<ExportOptimizationMarker>().Morphs) { route.Renderer = target; route.Shape = "coalesced"; }
            Object.DestroyImmediate(clone.transform.Find("first").gameObject); Object.DestroyImmediate(clone.transform.Find("second").gameObject);
            var optimizedSettings = Own(Object.Instantiate(settings));
            var optimizedHappy = Own(Object.Instantiate(expression));
            Assert.That(optimizedHappy.name, Is.Not.EqualTo(expression.name), "Exercise Unity's cloned ScriptableObject name.");
            optimizedHappy.MorphTargetBindings = new[] { new MorphTargetBinding("merged", 0, .4f), new MorphTargetBinding("merged", 0, .4f) };
            var additional = Own(ScriptableObject.CreateInstance<VRM10Expression>()); additional.name = optimizedHappy.name;
            additional.MorphTargetBindings = new[] { new MorphTargetBinding("merged", 0, .7f) };
            optimizedSettings.Expression.Happy = optimizedHappy; optimizedSettings.Expression.Angry = additional;
            clone.GetComponent<Vrm10Instance>().Vrm = optimizedSettings;
            bindings.ValidateAndApply();
            var result = clone.GetComponent<Vrm10Instance>().Vrm.Expression;
            Assert.That(result.Happy.name, Is.EqualTo(expression.name));
            Assert.That(result.Happy.MorphTargetBindings.Length, Is.EqualTo(1), "A renamed copy in the same preset slot cannot replace the remapped authored snapshot.");
            Assert.That(result.Happy.MorphTargetBindings.Single().Weight, Is.EqualTo(.4f));
            Assert.That(result.Angry, Is.SameAs(additional), "A genuinely new preset slot remains available.");
            Assert.That(settings.Expression.Happy, Is.SameAs(expression)); Assert.That(settings.Expression.Angry, Is.Null);
            Assert.That(expression.MorphTargetBindings.Select(binding => binding.RelativePath), Is.EqualTo(new[] { "first", "second" }));
        }

        [Test]
        public void RegisteredCustomCloneCannotBecomeANewSlotAndDistinctSameNamedCustomIsRetained()
        {
            var registryType = InstalledType("nadena.dev.ndmf.ObjectRegistry");
            if (registryType == null) Assert.Ignore("Install supported NDMF to verify public expression replacement provenance.");
            var registry = Activator.CreateInstance(registryType, new object[] { source.transform, null });
            var registryInterface = registryType.GetInterface("nadena.dev.ndmf.IObjectRegistry");
            var register = registryInterface.GetMethod("RegisterReplacedObject", new[] { typeof(Object), typeof(Object) });
            Skin(source, "first", "original"); Skin(source, "second", "original");
            var settings = Own(ScriptableObject.CreateInstance<VRM10Object>());
            var expression = Own(ScriptableObject.CreateInstance<VRM10Expression>()); expression.name = "authored custom";
            expression.MorphTargetBindings = new[] { new MorphTargetBinding("first", 0, .4f), new MorphTargetBinding("second", 0, .4f) };
            settings.Expression.CustomClips.Add(expression); source.AddComponent<Vrm10Instance>().Vrm = settings;
            clone = Object.Instantiate(source);
            using var bindings = ExportOptimizationBindings.Capture(clone, objectRegistry: registry);
            var target = Skin(clone, "merged", "coalesced");
            foreach (var route in clone.GetComponent<ExportOptimizationMarker>().Morphs) { route.Renderer = target; route.Shape = "coalesced"; }
            Object.DestroyImmediate(clone.transform.Find("first").gameObject); Object.DestroyImmediate(clone.transform.Find("second").gameObject);
            var optimizedSettings = Own(Object.Instantiate(settings));
            var optimizedCustom = Own(Object.Instantiate(expression));
            optimizedCustom.MorphTargetBindings = new[] { new MorphTargetBinding("merged", 0, .4f), new MorphTargetBinding("merged", 0, .4f) };
            register.Invoke(registry, new object[] { expression, optimizedCustom });
            var additional = Own(ScriptableObject.CreateInstance<VRM10Expression>()); additional.name = optimizedCustom.name;
            additional.MorphTargetBindings = new[] { new MorphTargetBinding("merged", 0, .7f) };
            optimizedSettings.Expression.CustomClips.Clear(); optimizedSettings.Expression.CustomClips.Add(optimizedCustom);
            optimizedSettings.Expression.CustomClips.Add(additional); clone.GetComponent<Vrm10Instance>().Vrm = optimizedSettings;
            bindings.ValidateAndApply();
            var result = clone.GetComponent<Vrm10Instance>().Vrm.Expression.CustomClips;
            Assert.That(result.Count, Is.EqualTo(2));
            var copied = result.Single(clip => clip.name == expression.name);
            Assert.That(copied.MorphTargetBindings.Length, Is.EqualTo(1));
            Assert.That(copied.MorphTargetBindings.Single().Weight, Is.EqualTo(.4f));
            Assert.That(result.Single(clip => clip.name == additional.name), Is.SameAs(additional), "A distinct custom asset remains new even when its name matches the optimizer's cloned name.");
            Assert.That(settings.Expression.CustomClips, Is.EqualTo(new[] { expression }));
            Assert.That(expression.MorphTargetBindings.Select(binding => binding.RelativePath), Is.EqualTo(new[] { "first", "second" }));
        }

        [TestCase("color", false)]
        [TestCase("color", true)]
        [TestCase("emissionColor", false)]
        [TestCase("emissionColor", true)]
        [TestCase("uv", false)]
        [TestCase("uv", true)]
        public void MovingMaterialEvidenceFollowsMappedSlotsAndOwnedFallbackCopies(string kind, bool becomesInert)
        {
            var original = Skin(source, "face", "UE/JawOpen");
            var material = Own(new Material(Shader.Find("VRM10/MToon10")) { name = "original material" });
            material.SetColor("_Color", Color.red);
            material.SetColor("_EmissionColor", Color.black);
            material.mainTextureScale = new Vector2(2, 3); material.mainTextureOffset = new Vector2(.1f, .2f);
            original.sharedMaterial = material;
            var settings = Own(ScriptableObject.CreateInstance<VRM10Object>());
            var expression = Own(ScriptableObject.CreateInstance<VRM10Expression>());
            expression.name = "UE/MouthClosed";
            if (kind == "uv") expression.MaterialUVBindings = new[] { new MaterialUVBinding {
                MaterialName = material.name, Scaling = new Vector2(2, 3), Offset = new Vector2(.4f, .2f) } };
            else expression.MaterialColorBindings = new[] { new MaterialColorBinding {
                MaterialName = material.name, BindType = (MaterialColorType)Enum.Parse(typeof(MaterialColorType), kind),
                TargetValue = kind == "color" ? (Vector4)Color.white : (Vector4)Color.red } };
            settings.Expression.CustomClips.Add(expression); source.AddComponent<Vrm10Instance>().Vrm = settings;
            clone = Object.Instantiate(source);
            var guard = new UnifiedExpressionPreparation(clone);
            Assert.That(guard.SupportsUnified, Is.True);
            using var bindings = ExportOptimizationBindings.Capture(clone, preparation: guard);
            var target = Skin(clone, "merged face", "renamed jaw");
            var renamed = Own(new Material(material) { name = "mapped material" });
            target.sharedMaterials = new[] { material, renamed };
            var marker = clone.GetComponent<ExportOptimizationMarker>();
            marker.Morphs.Single().Renderer = target; marker.Morphs.Single().Shape = "renamed jaw";
            marker.Materials.Single().Renderer = target; marker.Materials.Single().Slot = 1;
            Object.DestroyImmediate(clone.transform.Find("face").gameObject);
            bindings.ValidateAndApply();
            Assert.DoesNotThrow(() => bindings.VerifyMaterialEvidence(), "Renaming and merging must preserve the material evidence.");
            var fallback = Own(new Material(renamed) { name = renamed.name });
            target.sharedMaterials = new[] { material, fallback };
            if (becomesInert)
            {
                if (kind == "uv") fallback.mainTextureOffset = expression.MaterialUVBindings.Single().Offset;
                else fallback.SetVector(UnifiedExpressionPreparation.ColorProperty(expression.MaterialColorBindings.Single().BindType),
                    expression.MaterialColorBindings.Single().TargetValue);
                var error = Assert.Throws<InvalidOperationException>(() => bindings.VerifyMaterialEvidence());
                Assert.That(error.Message, Is.EqualTo(UnifiedExpressionPreparation.LostTracking));
                Assert.That(AvatarBaseShape.HasUsableMorphEndpoint(target, 0), Is.True, "A surviving raw morph cannot conceal a lost material route.");
            }
            else Assert.DoesNotThrow(() => bindings.VerifyMaterialEvidence(), "An owned same-slot fallback copy must remain valid.");
            Assert.That(material.GetColor("_Color"), Is.EqualTo(Color.red));
            Assert.That(material.GetColor("_EmissionColor"), Is.EqualTo(Color.black));
            Assert.That(material.mainTextureOffset, Is.EqualTo(new Vector2(.1f, .2f)));
            Assert.That(expression.MaterialColorBindings.All(binding => binding.MaterialName == material.name), Is.True);
            Assert.That(expression.MaterialUVBindings.All(binding => binding.MaterialName == material.name), Is.True);
        }

        [Test]
        public void GeneratedTargetMappingRejectsAnAmbiguousGlobalOutputName()
        {
            Skin(source, "face", "__VRVlog_Menu_unique");
            Skin(source, "other", "unrelated");
            clone = Object.Instantiate(source);
            using var bindings = ExportOptimizationBindings.Capture(clone);
            var other = clone.transform.Find("other").GetComponent<SkinnedMeshRenderer>();
            var changed = Own(Object.Instantiate(other.sharedMesh)); other.sharedMesh = changed;
            changed.AddBlendShapeFrame("__VRVlog_Menu_unique", 100, Enumerable.Repeat(Vector3.up * .1f, 3).ToArray(), null, null);
            bindings.ValidateAndApply();
            Assert.Throws<InvalidOperationException>(() => bindings.MapGeneratedName("__VRVlog_Menu_unique"));
        }

        [Test]
        public void UnifiedSemanticsRetainBothIndependentSameNamedRoutesAfterOneMeshMerge()
        {
            Skin(source, "first", "UE/JawOpen"); Skin(source, "second", "UE/JawOpen");
            clone = Object.Instantiate(source);
            using var bindings = ExportOptimizationBindings.Capture(clone);
            var target = Skin(clone, "merged", "first renamed");
            target.sharedMesh.AddBlendShapeFrame("second renamed", 100, Enumerable.Repeat(Vector3.up * .2f, 3).ToArray(), null, null);
            var routes = clone.GetComponent<ExportOptimizationMarker>().Morphs;
            for (var index = 0; index < routes.Length; index++)
            {
                routes[index].Renderer = target;
                routes[index].Shape = index == 0 ? "first renamed" : "second renamed";
            }
            Object.DestroyImmediate(clone.transform.Find("first").gameObject); Object.DestroyImmediate(clone.transform.Find("second").gameObject);
            bindings.ValidateAndApply(); bindings.BindNodes(_ => 0);
            var document = GlbDocument.Read(bindings.ApplyUnified(Output("first renamed", "second renamed")));
            var vrm = (Dictionary<string, object>)((Dictionary<string, object>)document.Json["extensions"])["VRMC_vrm"];
            var custom = (Dictionary<string, object>)((Dictionary<string, object>)vrm["expressions"])["custom"];
            var expression = (Dictionary<string, object>)custom["UE/JawOpen"];
            var morphs = ((List<object>)expression["morphTargetBinds"]).Cast<Dictionary<string, object>>().ToArray();
            Assert.That(morphs.Length, Is.EqualTo(2));
            Assert.That(morphs.Select(binding => Convert.ToInt32(binding["index"])), Is.EquivalentTo(new[] { 0, 1 }));
            Assert.That(morphs.All(binding => Convert.ToInt32(binding["node"]) == 0), Is.True);
            var meshes = (List<object>)document.Json["meshes"];
            var names = (List<object>)((Dictionary<string, object>)((Dictionary<string, object>)meshes[0])["extras"])["targetNames"];
            Assert.That(names, Is.EqualTo(new[] { "first renamed", "second renamed" }));
            Assert.That(custom.Keys, Is.EquivalentTo(new[] { "UE/JawOpen" }));
        }

        [Test]
        public void ExplicitProfileUsesMappedNodeAndIndexWithoutDependingOnGlobalShapeNames()
        {
            Skin(source, "first", "original"); Skin(source, "second", "original");
            var profile = Own(ScriptableObject.CreateInstance<VrmTrackingProfile>());
            profile.expressions = VrmTrackingExpressions.Names.Select(name => new TrackingExpression { name = name,
                morphs = new[] { new TrackingMorph { shape = "original", weight = .3f } } }).ToArray();
            clone = Object.Instantiate(source);
            using var bindings = ExportOptimizationBindings.Capture(clone, profile);
            var target = Skin(clone, "merged", "first renamed");
            target.sharedMesh.AddBlendShapeFrame("second renamed", 100, Enumerable.Repeat(Vector3.up * .2f, 3).ToArray(), null, null);
            var routes = clone.GetComponent<ExportOptimizationMarker>().Morphs;
            for (var index = 0; index < routes.Length; index++) { routes[index].Renderer = target; routes[index].Shape = index == 0 ? "first renamed" : "second renamed"; }
            Object.DestroyImmediate(clone.transform.Find("first").gameObject); Object.DestroyImmediate(clone.transform.Find("second").gameObject);
            bindings.ValidateAndApply(); bindings.BindNodes(_ => 0);
            var document = GlbDocument.Read(bindings.ApplyTracking(Output("first renamed", "second renamed")));
            var vrm = (Dictionary<string, object>)((Dictionary<string, object>)document.Json["extensions"])["VRMC_vrm"];
            var custom = (Dictionary<string, object>)((Dictionary<string, object>)vrm["expressions"])["custom"];
            Assert.That(custom.Count, Is.EqualTo(52));
            foreach (var value in custom.Values.Cast<Dictionary<string, object>>())
            {
                var morphs = ((List<object>)value["morphTargetBinds"]).Cast<Dictionary<string, object>>().ToArray();
                Assert.That(morphs.Select(binding => Convert.ToInt32(binding["index"])), Is.EquivalentTo(new[] { 0, 1 }));
                Assert.That(morphs.All(binding => Math.Abs(Convert.ToDouble(binding["weight"]) - .3) < .000001), Is.True);
            }
            Assert.That(profile.expressions.All(expression => expression.morphs.Single().shape == "original" && expression.morphs.Single().weight == .3f), Is.True);
        }

        [TestCase(.25f, .25f)]
        [TestCase(.25f, .75f)]
        public void ExplicitProfileCoalescedRoutesRegisterOneEndpointWithTheFirstConfiguredWeight(float firstWeight, float laterWeight)
        {
            Skin(source, "first", "original"); Skin(source, "second", "original");
            var profile = Own(ScriptableObject.CreateInstance<VrmTrackingProfile>());
            profile.expressions = VrmTrackingExpressions.Names.Select(name => new TrackingExpression { name = name,
                morphs = new[] { new TrackingMorph { shape = "original", weight = firstWeight },
                    new TrackingMorph { shape = "original", weight = laterWeight } } }).ToArray();
            clone = Object.Instantiate(source);
            using var bindings = ExportOptimizationBindings.Capture(clone, profile);
            var target = Skin(clone, "merged", "coalesced");
            foreach (var route in clone.GetComponent<ExportOptimizationMarker>().Morphs)
            {
                route.Renderer = target; route.Shape = "coalesced";
            }
            Object.DestroyImmediate(clone.transform.Find("first").gameObject); Object.DestroyImmediate(clone.transform.Find("second").gameObject);
            bindings.ValidateAndApply(); bindings.BindNodes(_ => 0);
            AssertSingleTrackingEndpoint(bindings.ApplyTracking(Output("coalesced")), 0, firstWeight);
            Assert.That(profile.expressions.All(expression => expression.morphs[0].shape == "original" && expression.morphs[0].weight == firstWeight &&
                expression.morphs[1].shape == "original" && expression.morphs[1].weight == laterWeight), Is.True);
            Assert.That(source.GetComponentsInChildren<SkinnedMeshRenderer>().Length, Is.EqualTo(2));
        }

        static void AssertSingleTrackingEndpoint(byte[] bytes, int expectedIndex, float weight)
        {
            var document = GlbDocument.Read(bytes);
            var vrm = (Dictionary<string, object>)((Dictionary<string, object>)document.Json["extensions"])["VRMC_vrm"];
            var custom = (Dictionary<string, object>)((Dictionary<string, object>)vrm["expressions"])["custom"];
            Assert.That(custom.Count, Is.EqualTo(52));
            foreach (var expression in custom.Values.Cast<Dictionary<string, object>>())
            {
                var morphs = ((List<object>)expression["morphTargetBinds"]).Cast<Dictionary<string, object>>().ToArray();
                Assert.That(morphs.Length, Is.EqualTo(1), "Coalesced properties must not contribute the ARKit weight twice.");
                Assert.That(Convert.ToInt32(morphs[0]["node"]), Is.EqualTo(0));
                Assert.That(Convert.ToInt32(morphs[0]["index"]), Is.EqualTo(expectedIndex));
                Assert.That(Convert.ToDouble(morphs[0]["weight"]), Is.EqualTo((double)weight).Within(.000001));
                Assert.That(morphs.Sum(binding => Convert.ToDouble(binding["weight"])), Is.EqualTo((double)weight).Within(.000001));
            }
        }

        static byte[] Output(params string[] names) => GlbDocument.Create(new Dictionary<string, object> {
            ["asset"] = new Dictionary<string, object> { ["version"] = "2.0" },
            ["nodes"] = new List<object> { new Dictionary<string, object> { ["mesh"] = 0L } },
            ["meshes"] = new List<object> { new Dictionary<string, object> {
                ["extras"] = new Dictionary<string, object> { ["targetNames"] = names.Cast<object>().ToList() },
                ["primitives"] = new List<object> { new Dictionary<string, object> { ["targets"] = names.Select(_ => (object)new Dictionary<string, object>()).ToList() } }
            } },
            ["extensions"] = new Dictionary<string, object> { ["VRMC_vrm"] = new Dictionary<string, object>() }
        }, Array.Empty<byte>()).Write();

        static Type InstalledType(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(name)).FirstOrDefault(type => type != null);

        void RequireInstalledAvatarOptimizer(string component)
        {
            if (InstalledType("Anatawa12.AvatarOptimizer." + component) == null || InstalledType("nadena.dev.ndmf.BuildContext") == null)
                Assert.Ignore("Install supported Avatar Optimizer and NDMF to run the optimizer integration fixture.");
            Assert.That(ExportOptimizationMarker.AvatarOptimizerAdapterAvailable, Is.True, "The installed AAO adapter must compile and register.");
        }

        Component AddOptimizer(GameObject target, string name, int initializeVersion)
        {
            var type = InstalledType("Anatawa12.AvatarOptimizer." + name);
            var initialize = type.GetMethod("Initialize", new[] { typeof(int) });
            if (initialize == null) Assert.Ignore("This AAO version does not expose the fixture's component configuration API.");
            var component = target.AddComponent(type);
            initialize.Invoke(component, new object[] { initializeVersion });
            return component;
        }

        [Test]
        public void InstalledNdmfRootUsesItsPublicEditorOnlyInterface()
        {
            var rootType = InstalledType("nadena.dev.ndmf.runtime.components.NDMFAvatarRoot");
            if (rootType == null) Assert.Ignore("Install NDMF to verify its public editor-only component detection.");
            source.AddComponent(rootType);
            Assert.That(NdmfExportPreparation.NeedsProcessing(source), Is.True);
        }

        [Test]
        public void InstalledAvatarOptimizerMaskDeletionUpdatesGeometryAndRetainsAConsumedMorph()
        {
            RequireInstalledAvatarOptimizer("RemoveMeshByMask");
            var skin = Skin(source, "face", "__VRVlog_Menu_retained");
            var mesh = skin.sharedMesh;
            mesh.ClearBlendShapes();
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.right * 2, Vector3.right * 3, Vector3.right * 2 + Vector3.up };
            mesh.triangles = new[] { 0, 1, 2, 3, 4, 5 };
            mesh.uv = new[] { new Vector2(.25f, .5f), new Vector2(.25f, .5f), new Vector2(.25f, .5f), new Vector2(.75f, .5f), new Vector2(.75f, .5f), new Vector2(.75f, .5f) };
            mesh.AddBlendShapeFrame("__VRVlog_Menu_retained", 100, Enumerable.Repeat(Vector3.forward * .1f, 6).ToArray(), null, null);
            skin.sharedMaterial = Own(new Material(Shader.Find("Unlit/Color")));
            var mask = Own(new Texture2D(2, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point });
            mask.SetPixels(new[] { Color.black, Color.white }); mask.Apply();
            var component = AddOptimizer(skin.gameObject, "RemoveMeshByMask", 1);
            var slotType = component.GetType().GetNestedType("MaterialSlot");
            var slot = Activator.CreateInstance(slotType);
            slotType.GetProperty("Enabled").SetValue(slot, true);
            slotType.GetProperty("Mask").SetValue(slot, mask);
            slotType.GetProperty("Mode").SetValue(slot, Enum.Parse(component.GetType().GetNestedType("RemoveMode"), "RemoveBlack"));
            var slots = Array.CreateInstance(slotType, 1); slots.SetValue(slot, 0);
            component.GetType().GetProperty("Materials").SetValue(component, slots);
            clone = Object.Instantiate(source);
            Assert.That(NdmfExportPreparation.NeedsProcessing(clone), Is.True, "AAO authoring alone must start the NDMF build.");
            ExportOptimizationBindings bindings = null;
            try
            {
                using var preparation = NdmfExportPreparation.Prepare(source, clone, afterTransforming: _ => bindings = ExportOptimizationBindings.Capture(clone));
                bindings.ValidateAndApply();
                var prepared = clone.GetComponentInChildren<SkinnedMeshRenderer>();
                Assert.That(prepared.sharedMesh.vertexCount, Is.EqualTo(3));
                Assert.That(prepared.sharedMesh.triangles.Length, Is.EqualTo(3));
                Assert.That(prepared.sharedMesh.GetBlendShapeIndex(bindings.MapGeneratedName("__VRVlog_Menu_retained")), Is.GreaterThanOrEqualTo(0));
                Assert.That(skin.sharedMesh.vertexCount, Is.EqualTo(6));
                Assert.That(component != null, Is.True);
            }
            finally { bindings?.Dispose(); }
        }

        [Test]
        public void InstalledAvatarOptimizerPreservesRebasedNeutralAndStillFreezesUnconsumedZeroChannel()
        {
            RequireInstalledAvatarOptimizer("TraceAndOptimize");
            var skin = Skin(source, "face", "Neutral opening");
            var mesh = skin.sharedMesh;
            mesh.AddBlendShapeFrame("Unconsumed zero", 100, Enumerable.Repeat(Vector3.right * .1f, 3).ToArray(), null, null);
            mesh.AddBlendShapeFrame("__VRVlog_Menu_control", 100, Enumerable.Repeat(Vector3.up * .05f, 3).ToArray(), null, null);
            var bone = new GameObject("bone").transform; bone.SetParent(source.transform, false);
            mesh.bindposes = new[] { bone.worldToLocalMatrix * skin.transform.localToWorldMatrix };
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 3).ToArray();
            skin.bones = new[] { bone }; skin.rootBone = bone;
            skin.sharedMaterial = Own(new Material(Shader.Find("Unlit/Color")));
            skin.SetBlendShapeWeight(0, 75);
            source.AddComponent(InstalledType("Anatawa12.AvatarOptimizer.TraceAndOptimize"));
            var folderName = "__AaoRebasedNeutral_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folderName); var folder = "Assets/" + folderName;
            var meshes = new List<Mesh>();
            ExportOptimizationBindings bindings = null;
            Vector3[] NativeVertices(SkinnedMeshRenderer renderer)
            {
                var baked = new Mesh();
                try { renderer.BakeMesh(baked); return baked.vertices.Select(renderer.transform.TransformPoint).ToArray(); }
                finally { Object.DestroyImmediate(baked); }
            }
            try
            {
                var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
                var clip = new AnimationClip { name = "Absolute neutral75 and unused zero" };
                AssetDatabase.AddObjectToAsset(clip, controller);
                var neutralBinding = EditorCurveBinding.FloatCurve("face", typeof(SkinnedMeshRenderer), "blendShape.Neutral opening");
                var zeroBinding = EditorCurveBinding.FloatCurve("face", typeof(SkinnedMeshRenderer), "blendShape.Unconsumed zero");
                AnimationUtility.SetEditorCurve(clip, neutralBinding, AnimationCurve.Constant(0, 1, 75));
                AnimationUtility.SetEditorCurve(clip, zeroBinding, AnimationCurve.Constant(0, 1, 0));
                var machine = controller.layers[0].stateMachine;
                var state = machine.AddState("Neutral"); state.motion = clip; state.writeDefaultValues = false; machine.defaultState = state;
                source.AddComponent<Animator>().runtimeAnimatorController = controller;
                var sourceController = EditorJsonUtility.ToJson(controller);
                var expected = NativeVertices(skin);
                clone = Object.Instantiate(source);
                using var preparation = NdmfExportPreparation.Prepare(source, clone, afterTransforming: _ =>
                {
                    var neutral = NeutralShapeSnapshot.Capture(clone);
                    AvatarBaseShape.Preserve(clone, clone, meshes, null);
                    bindings = ExportOptimizationBindings.Capture(clone, neutral: neutral);
                });
                bindings.ValidateAndApply();
                var output = clone.GetComponentsInChildren<SkinnedMeshRenderer>().Single();
                var actual = NativeVertices(output);
                Assert.That(actual.Length, Is.EqualTo(expected.Length));
                var unmatched = expected.ToList();
                foreach (var vertex in actual)
                {
                    var index = unmatched.FindIndex(value => Vector3.Distance(vertex, value) < .00001f);
                    Assert.That(index, Is.GreaterThanOrEqualTo(0),
                        "AAO must preserve native source neutral75 rather than reapply its old FX constant.");
                    unmatched.RemoveAt(index);
                }
                Assert.That(output.sharedMesh.GetBlendShapeIndex("Unconsumed zero"), Is.EqualTo(-1),
                    "The unrelated constant-zero channel must still be frozen by the normal optimizer pass.");
                Assert.That(output.sharedMesh.GetBlendShapeIndex("Neutral opening"), Is.GreaterThanOrEqualTo(0));
                Assert.That(skin.sharedMesh, Is.SameAs(mesh));
                Assert.That(skin.GetBlendShapeWeight(0), Is.EqualTo(75));
                Assert.That(mesh.blendShapeCount, Is.EqualTo(3));
                Assert.That(AnimationUtility.GetEditorCurve(clip, neutralBinding).Evaluate(0), Is.EqualTo(75));
                Assert.That(AnimationUtility.GetEditorCurve(clip, zeroBinding).Evaluate(0), Is.Zero);
                Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(sourceController));
            }
            finally
            {
                bindings?.Dispose();
                foreach (var ownedMesh in meshes) if (ownedMesh != null) Object.DestroyImmediate(ownedMesh);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void InstalledAvatarOptimizerMergeMapsSameNamedRawTrackingOnDestroyedRenderers()
        {
            RequireInstalledAvatarOptimizer("MergeSkinnedMesh");
            if (InstalledType("Anatawa12.AvatarOptimizer.MergeSkinnedMesh").GetProperty("MergeBlendShapes") == null)
                Assert.Ignore("This rename-to-avoid-conflict fixture requires the AAO 1.8+ merge configuration API.");
            var first = Skin(source, "first", "UE/JawOpen");
            var second = Skin(source, "second", "UE/JawOpen");
            var material = Own(new Material(Shader.Find("Unlit/Color")));
            first.sharedMaterial = material; second.sharedMaterial = material;
            var node = new GameObject("merged"); node.transform.SetParent(source.transform, false); node.AddComponent<SkinnedMeshRenderer>();
            var merge = AddOptimizer(node, "MergeSkinnedMesh", 2);
            merge.GetType().GetProperty("MergeBlendShapes").SetValue(merge, false);
            var sources = merge.GetType().GetProperty("SourceSkinnedMeshRenderers").GetValue(merge);
            var add = sources.GetType().GetMethod("Add", new[] { typeof(SkinnedMeshRenderer) });
            Assert.That(add, Is.Not.Null, "The supported set API must expose Add.");
            add.Invoke(sources, new object[] { first }); add.Invoke(sources, new object[] { second });
            merge.GetType().GetProperty("RemoveEmptyRendererObject").SetValue(merge, true);
            source.AddComponent(InstalledType("Anatawa12.AvatarOptimizer.TraceAndOptimize"));
            clone = Object.Instantiate(source);
            Assert.That(NdmfExportPreparation.NeedsProcessing(clone), Is.True, "AAO authoring alone must start the NDMF build.");
            var oldFirst = clone.transform.Find("first").GetComponent<SkinnedMeshRenderer>();
            var oldSecond = clone.transform.Find("second").GetComponent<SkinnedMeshRenderer>();
            ExportOptimizationBindings bindings = null;
            try
            {
                using var preparation = NdmfExportPreparation.Prepare(source, clone, afterTransforming: _ => bindings = ExportOptimizationBindings.Capture(clone));
                bindings.ValidateAndApply();
                var firstRoute = bindings.MapMorph(oldFirst, "UE/JawOpen");
                var secondRoute = bindings.MapMorph(oldSecond, "UE/JawOpen");
                Assert.That(oldFirst == null && oldSecond == null, Is.True, "Merge must exercise mapping destroyed managed references.");
                Assert.That(firstRoute.Renderer, Is.SameAs(secondRoute.Renderer));
                Assert.That(firstRoute.Shape, Is.Not.EqualTo(secondRoute.Shape), "Independent source properties must retain separate output controls.");
                Assert.That(firstRoute.Renderer.sharedMesh.GetBlendShapeIndex(firstRoute.Shape), Is.GreaterThanOrEqualTo(0));
                Assert.That(firstRoute.Renderer.sharedMesh.GetBlendShapeIndex(secondRoute.Shape), Is.GreaterThanOrEqualTo(0));
                Assert.That(first != null && second != null, Is.True);
                Assert.That(first.sharedMesh.GetBlendShapeName(0), Is.EqualTo("UE/JawOpen"));
            }
            finally { bindings?.Dispose(); }
        }

        [TestCase(.25f)]
        [TestCase(.75f)]
        public void InstalledAvatarOptimizerCoalescedTrackingShapesDoNotMultiplyProfileWeights(float weight)
        {
            RequireInstalledAvatarOptimizer("MergeSkinnedMesh");
            if (InstalledType("Anatawa12.AvatarOptimizer.MergeSkinnedMesh").GetProperty("MergeBlendShapes") == null)
                Assert.Ignore("This coalescing fixture requires the AAO 1.8+ merge configuration API.");
            var first = Skin(source, "first", "profile shape"); var second = Skin(source, "second", "profile shape");
            var material = Own(new Material(Shader.Find("Unlit/Color")));
            first.sharedMaterial = material; second.sharedMaterial = material;
            var node = new GameObject("merged"); node.transform.SetParent(source.transform, false); node.AddComponent<SkinnedMeshRenderer>();
            var merge = AddOptimizer(node, "MergeSkinnedMesh", 2);
            merge.GetType().GetProperty("MergeBlendShapes").SetValue(merge, true);
            var sources = merge.GetType().GetProperty("SourceSkinnedMeshRenderers").GetValue(merge);
            var add = sources.GetType().GetMethod("Add", new[] { typeof(SkinnedMeshRenderer) });
            Assert.That(add, Is.Not.Null);
            add.Invoke(sources, new object[] { first }); add.Invoke(sources, new object[] { second });
            merge.GetType().GetProperty("RemoveEmptyRendererObject").SetValue(merge, true);
            source.AddComponent(InstalledType("Anatawa12.AvatarOptimizer.TraceAndOptimize"));
            var profile = Own(ScriptableObject.CreateInstance<VrmTrackingProfile>());
            profile.expressions = VrmTrackingExpressions.Names.Select(name => new TrackingExpression { name = name,
                morphs = new[] { new TrackingMorph { shape = "profile shape", weight = weight } } }).ToArray();
            clone = Object.Instantiate(source);
            Assert.That(NdmfExportPreparation.NeedsProcessing(clone), Is.True);
            var oldFirst = clone.transform.Find("first").GetComponent<SkinnedMeshRenderer>();
            var oldSecond = clone.transform.Find("second").GetComponent<SkinnedMeshRenderer>();
            ExportOptimizationBindings bindings = null;
            try
            {
                using var preparation = NdmfExportPreparation.Prepare(source, clone, afterTransforming: _ => bindings = ExportOptimizationBindings.Capture(clone, profile));
                bindings.ValidateAndApply();
                var firstRoute = bindings.MapMorph(oldFirst, "profile shape"); var secondRoute = bindings.MapMorph(oldSecond, "profile shape");
                Assert.That(oldFirst == null && oldSecond == null, Is.True);
                Assert.That(firstRoute.Renderer, Is.SameAs(secondRoute.Renderer));
                Assert.That(firstRoute.Shape, Is.EqualTo(secondRoute.Shape), "The installed optimizer must actually coalesce both source controls.");
                var mesh = firstRoute.Renderer.sharedMesh;
                bindings.BindNodes(renderer => renderer == firstRoute.Renderer ? 0 : -1);
                var bytes = bindings.ApplyTracking(Output(Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToArray()));
                AssertSingleTrackingEndpoint(bytes, mesh.GetBlendShapeIndex(firstRoute.Shape), weight);
                Assert.That(first != null && second != null, Is.True);
                Assert.That(first.sharedMesh.GetBlendShapeName(0), Is.EqualTo("profile shape"));
                Assert.That(second.sharedMesh.GetBlendShapeName(0), Is.EqualTo("profile shape"));
                Assert.That(first.sharedMesh.vertexCount, Is.EqualTo(3)); Assert.That(second.sharedMesh.vertexCount, Is.EqualTo(3));
                Assert.That(profile.expressions.All(expression => expression.morphs.Single().shape == "profile shape" && expression.morphs.Single().weight == weight), Is.True);
                Assert.That(merge != null, Is.True);
            }
            finally { bindings?.Dispose(); }
        }

        [TestCase(.25f, .25f, false, false)]
        [TestCase(.75f, .75f, false, false)]
        [TestCase(.25f, .75f, true, false)]
        [TestCase(.75f, .25f, true, false)]
        [TestCase(.25f, .25f, false, true)]
        [TestCase(.75f, .75f, false, true)]
        [TestCase(.25f, .75f, true, true)]
        [TestCase(.75f, .25f, true, true)]
        public void InstalledAvatarOptimizerCoalescedAuthoredShapesDriveOnceOrStopOnConflictingWeights(float firstWeight, float secondWeight, bool conflict, bool custom)
        {
            RequireInstalledAvatarOptimizer("MergeSkinnedMesh");
            if (InstalledType("Anatawa12.AvatarOptimizer.MergeSkinnedMesh").GetProperty("MergeBlendShapes") == null)
                Assert.Ignore("This coalescing fixture requires the AAO 1.8+ merge configuration API.");
            var first = Skin(source, "first", "authored shape"); var second = Skin(source, "second", "authored shape");
            var firstMesh = first.sharedMesh; var secondMesh = second.sharedMesh;
            var material = Own(new Material(Shader.Find("Unlit/Color")));
            first.sharedMaterial = material; second.sharedMaterial = material;
            var settings = Own(ScriptableObject.CreateInstance<VRM10Object>());
            var expression = Own(ScriptableObject.CreateInstance<VRM10Expression>()); expression.name = custom ? "authored custom(Clone)" : "authored Happy";
            expression.MorphTargetBindings = new[] { new MorphTargetBinding("first", 0, firstWeight), new MorphTargetBinding("second", 0, secondWeight) };
            settings.Expression.AddClip(custom ? ExpressionPreset.custom : ExpressionPreset.happy, expression); source.AddComponent<Vrm10Instance>().Vrm = settings;
            var node = new GameObject("merged"); node.transform.SetParent(source.transform, false); node.AddComponent<SkinnedMeshRenderer>();
            var merge = AddOptimizer(node, "MergeSkinnedMesh", 2);
            merge.GetType().GetProperty("MergeBlendShapes").SetValue(merge, true);
            var sources = merge.GetType().GetProperty("SourceSkinnedMeshRenderers").GetValue(merge);
            var add = sources.GetType().GetMethod("Add", new[] { typeof(SkinnedMeshRenderer) });
            Assert.That(add, Is.Not.Null);
            add.Invoke(sources, new object[] { first }); add.Invoke(sources, new object[] { second });
            merge.GetType().GetProperty("RemoveEmptyRendererObject").SetValue(merge, true);
            source.AddComponent(InstalledType("Anatawa12.AvatarOptimizer.TraceAndOptimize"));
            clone = Object.Instantiate(source);
            var oldFirst = clone.transform.Find("first").GetComponent<SkinnedMeshRenderer>();
            var oldSecond = clone.transform.Find("second").GetComponent<SkinnedMeshRenderer>();
            ExportOptimizationBindings bindings = null;
            object capturedIdentity = null;
            try
            {
                using var preparation = NdmfExportPreparation.Prepare(source, clone, afterTransforming: prepared =>
                {
                    var authored = custom ? clone.GetComponent<Vrm10Instance>().Vrm.Expression.CustomClips.Single() : clone.GetComponent<Vrm10Instance>().Vrm.Expression.Happy;
                    bindings = ExportOptimizationBindings.Capture(clone, objectRegistry: prepared.ObjectRegistry);
                    capturedIdentity = RegisteredAssetIdentity(prepared.ObjectRegistry, authored);
                });
                var routes = clone.GetComponent<ExportOptimizationMarker>().Morphs;
                Assert.That(oldFirst == null && oldSecond == null, Is.True, "Installed AAO must destroy both original renderer objects.");
                Assert.That(routes.Length, Is.EqualTo(2));
                Assert.That(routes[0].Renderer, Is.SameAs(routes[1].Renderer));
                Assert.That(routes[0].Shape, Is.EqualTo(routes[1].Shape), "The optimizer must really coalesce the authored output properties.");
                var target = routes[0].Renderer; var meshBeforeApply = target.sharedMesh; var materialsBeforeApply = target.sharedMaterials;
                var settingsBeforeApply = clone.GetComponent<Vrm10Instance>().Vrm; var copies = 0;
                var optimizedAuthored = custom ? settingsBeforeApply.Expression.CustomClips.Single() : settingsBeforeApply.Expression.Happy;
                Assert.That(optimizedAuthored.name, Is.Not.EqualTo(expression.name), "Installed AAO must really rename its copied ScriptableObject.");
                Assert.That(RegisteredAssetIdentity(preparation.ObjectRegistry, optimizedAuthored), Is.SameAs(capturedIdentity), "The asset registry identity must remain usable after Finish.");
                var generatedCustom = Own(ScriptableObject.CreateInstance<VRM10Expression>()); generatedCustom.name = "generated custom(Clone)";
                generatedCustom.MorphTargetBindings = new[] { new MorphTargetBinding(UnityEditor.AnimationUtility.CalculateTransformPath(target.transform, clone.transform), target.sharedMesh.GetBlendShapeIndex(routes[0].Shape), .1f) };
                settingsBeforeApply.Expression.CustomClips.Insert(0, generatedCustom);
                var generatedPreset = Own(ScriptableObject.CreateInstance<VRM10Expression>()); generatedPreset.name = expression.name;
                settingsBeforeApply.Expression.Angry = generatedPreset;
                if (conflict)
                {
                    var error = Assert.Throws<InvalidOperationException>(() => bindings.ValidateAndApply((copy, original) => copies++));
                    Assert.That(error.Message, Does.Contain(expression.name));
                    Assert.That(error.Message, Does.Contain("適用量が衝突"));
                    Assert.That(copies, Is.Zero);
                    Assert.That(target.sharedMesh, Is.SameAs(meshBeforeApply));
                    Assert.That(target.sharedMaterials, Is.EqualTo(materialsBeforeApply));
                    Assert.That(clone.GetComponent<Vrm10Instance>().Vrm, Is.SameAs(settingsBeforeApply));
                }
                else
                {
                    bindings.ValidateAndApply();
                    var result = clone.GetComponent<Vrm10Instance>().Vrm.Expression;
                    var copied = custom ? result.CustomClips.Single(clip => clip != generatedCustom) : result.Happy;
                    Assert.That(copied, Is.Not.SameAs(expression));
                    Assert.That(copied, Is.Not.SameAs(optimizedAuthored));
                    Assert.That(copied.name, Is.EqualTo(expression.name));
                    Assert.That(copied.MorphTargetBindings.Length, Is.EqualTo(1));
                    Assert.That(copied.MorphTargetBindings.Single().RelativePath, Is.EqualTo(UnityEditor.AnimationUtility.CalculateTransformPath(target.transform, clone.transform)));
                    Assert.That(copied.MorphTargetBindings.Single().Index, Is.EqualTo(target.sharedMesh.GetBlendShapeIndex(routes[0].Shape)));
                    Assert.That(copied.MorphTargetBindings.Single().Weight, Is.EqualTo(firstWeight));
                    Assert.That(result.CustomClips.Count, Is.EqualTo(custom ? 2 : 1), "A copied custom must not be appended again, and newly inserted customs must survive.");
                    Assert.That(result.CustomClips, Does.Contain(generatedCustom));
                    Assert.That(result.Angry, Is.SameAs(generatedPreset), "An uncaptured preset survives even when its clip name matches a captured slot.");
                }
                Assert.That(source.GetComponent<Vrm10Instance>().Vrm, Is.SameAs(settings));
                Assert.That(settings.Expression.CustomClips.Count, Is.EqualTo(custom ? 1 : 0));
                Assert.That(settings.Expression.Angry, Is.Null);
                Assert.That(expression.MorphTargetBindings.Select(binding => binding.RelativePath), Is.EqualTo(new[] { "first", "second" }));
                Assert.That(expression.MorphTargetBindings.Select(binding => binding.Weight), Is.EqualTo(new[] { firstWeight, secondWeight }));
                Assert.That(first.sharedMesh, Is.SameAs(firstMesh)); Assert.That(second.sharedMesh, Is.SameAs(secondMesh));
                Assert.That(firstMesh.GetBlendShapeName(0), Is.EqualTo("authored shape")); Assert.That(secondMesh.GetBlendShapeName(0), Is.EqualTo("authored shape"));
                Assert.That(merge != null, Is.True);
            }
            finally { bindings?.Dispose(); }
        }
    }
}
