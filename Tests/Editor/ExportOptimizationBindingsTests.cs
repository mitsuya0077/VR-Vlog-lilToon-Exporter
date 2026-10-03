using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UniGLTF.Extensions.VRMC_vrm;
using UniVRM10;
using UnityEngine;
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
    }
}
