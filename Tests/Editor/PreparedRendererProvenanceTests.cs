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
    public sealed class PreparedRendererProvenanceTests
    {
        GameObject source, prepared;
        readonly List<Object> owned = new List<Object>();

        [SetUp] public void SetUp() => source = new GameObject("Avatar");
        [TearDown] public void TearDown()
        {
            Object.DestroyImmediate(prepared); Object.DestroyImmediate(source);
            foreach (var value in owned) if (value != null) Object.DestroyImmediate(value);
            owned.Clear();
        }

        Mesh Mesh(params string[] shapes)
        {
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            foreach (var shape in shapes)
                mesh.AddBlendShapeFrame(shape, 100, new[] { Vector3.up, Vector3.zero, Vector3.zero }, null, null);
            owned.Add(mesh); return mesh;
        }

        SkinnedMeshRenderer Skin(GameObject root, string path, Mesh mesh)
        {
            var face = new GameObject(path); face.transform.SetParent(root.transform, false);
            var skin = face.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = mesh; return skin;
        }

        (VRM10Object Vrm, VRM10Expression Clip) Author(params MorphTargetBinding[] values)
        {
            var vrm = ScriptableObject.CreateInstance<VRM10Object>(); owned.Add(vrm);
            var clip = ScriptableObject.CreateInstance<VRM10Expression>(); owned.Add(clip);
            clip.name = "UE/JawOpen"; clip.MorphTargetBindings = values;
            clip.IsBinary = true; clip.OverrideBlink = ExpressionOverrideType.block;
            clip.OverrideLookAt = ExpressionOverrideType.blend; clip.OverrideMouth = ExpressionOverrideType.block;
            clip.MaterialColorBindings = new[] { new MaterialColorBinding {
                MaterialName = "Face material", BindType = MaterialColorType.color, TargetValue = Color.red } };
            clip.MaterialUVBindings = new[] { new MaterialUVBinding {
                MaterialName = "Face material", Scaling = new Vector2(2, 3), Offset = new Vector2(.2f, .3f) } };
            vrm.Expression.CustomClips.Add(clip); source.AddComponent<Vrm10Instance>().Vrm = vrm;
            return (vrm, clip);
        }

        // Exercise the exact pinned optional NDMF registry API, without running
        // an Editor build or adding a hard NDMF assembly dependency to the tests.
        sealed class Context
        {
            public object ObjectRegistry { get; }
            internal Context(object registry) => ObjectRegistry = registry;
        }

        object Registry()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("nadena.dev.ndmf.ObjectRegistry"))
                .FirstOrDefault(value => value != null);
            if (type == null) Assert.Ignore("Optional NDMF ObjectRegistry is not installed in this project.");
            return Activator.CreateInstance(type, new object[] { prepared.transform, null });
        }

        static Type Contract(object registry) => registry.GetType().GetInterfaces()
            .Single(type => type.FullName == "nadena.dev.ndmf.IObjectRegistry");

        static void Register(object registry, SkinnedMeshRenderer old, SkinnedMeshRenderer current)
        {
            var contract = Contract(registry);
            var reference = contract.GetMethod("GetReference", new[] { typeof(Object), typeof(bool) })
                .Invoke(registry, new object[] { old, true });
            contract.GetMethod("RegisterReplacedObject", new[] { reference.GetType(), typeof(Object) })
                .Invoke(registry, new object[] { reference, current });
        }

        static NdmfExportPreparation Capture(GameObject prepared, object registry)
        {
            var preparation = new NdmfExportPreparation();
            typeof(NdmfExportPreparation).GetMethod("CaptureRendererReplacements", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(preparation, new object[] { prepared, new Context(registry) });
            return preparation;
        }

        [Test]
        public void RegisteredReplacementCarriesBlinkTrackingMenuAndAuthorIndices()
        {
            var originalMesh = Mesh("UE/EyeClosed", "Custom jaw", "Custom opening");
            Skin(source, "Face", originalMesh);
            var author = Author(new MorphTargetBinding("Face", 1, .25f), new MorphTargetBinding("Face", 1, 0),
                new MorphTargetBinding("Face", 999, .5f));
            using var sourceBlink = BlinkExportSession.CaptureForExport(source);
            prepared = Object.Instantiate(source);
            using var blink = sourceBlink.ForClone(source, prepared);
            var menu = new VrChatExpressionMenu.Source();
            var entry = new VrChatExpressionMenu.Entry { Name = "Custom" };
            entry.Values.Add(new VrChatExpressionMenu.MorphValue { Path = "Face", Shape = "Custom opening", Weight = 100 });
            menu.Entries.Add(entry);
            using var bindings = new PreparedExpressionBindings(prepared, menu);
            bindings.CaptureAuthoredExpressions();
            var guard = new UnifiedExpressionPreparation(prepared);
            var old = prepared.GetComponentInChildren<SkinnedMeshRenderer>();
            var current = Skin(prepared, "Final face", Mesh("Generated opening", "Custom opening", "UE/EyeClosed", "Custom jaw"));
            var registry = Registry(); Register(registry, old, current);
            Object.DestroyImmediate(old.gameObject);
            using var preparation = Capture(prepared, registry);
            Assert.That(preparation.PreparedRendererFor(old), Is.SameAs(current));
            bindings.RebindPrepared(preparation.PreparedRendererFor);
            bindings.RebindAuthoredExpressions(value => value);
            blink.RebindPrepared(preparation.PreparedRendererFor);
            guard.RebindPrepared(preparation.PreparedRendererFor);
            Assert.DoesNotThrow(() => blink.VerifyPreparedIdentity(prepared));
            Assert.DoesNotThrow(() => guard.VerifyIdentityAndDeformation());
            bindings.Capture(menu);
            Assert.That(entry.Error, Is.Null);
            Assert.That(bindings.AuthoringPath("Final face"), Is.EqualTo("Face"));
            Assert.That(bindings.Get("Face").Renderer, Is.SameAs(current));
            var clip = prepared.GetComponent<Vrm10Instance>().Vrm.Expression.CustomClips.Single();
            Assert.That(clip.MorphTargetBindings.Select(value => value.RelativePath), Is.EqualTo(new[] { "Final face", "Final face", "Face" }));
            Assert.That(clip.MorphTargetBindings.Select(value => value.Index), Is.EqualTo(new[] { 3, 3, int.MaxValue }));
            Assert.That(clip.MorphTargetBindings.Select(value => value.Weight), Is.EqualTo(new[] { .25f, 0, .5f }));
            Assert.That(clip.IsBinary, Is.True); Assert.That(clip.OverrideBlink, Is.EqualTo(author.Clip.OverrideBlink));
            Assert.That(clip.OverrideLookAt, Is.EqualTo(author.Clip.OverrideLookAt));
            Assert.That(clip.OverrideMouth, Is.EqualTo(author.Clip.OverrideMouth));
            Assert.That(clip.MaterialColorBindings, Is.EqualTo(author.Clip.MaterialColorBindings));
            Assert.That(clip.MaterialUVBindings, Is.EqualTo(author.Clip.MaterialUVBindings));
            Assert.That(author.Clip.MorphTargetBindings[0].RelativePath, Is.EqualTo("Face"));
            Assert.That(author.Clip.MorphTargetBindings[0].Index, Is.EqualTo(1));
            Assert.That(source.GetComponent<Vrm10Instance>().Vrm, Is.SameAs(author.Vrm));
            Assert.That(originalMesh.GetBlendShapeName(1), Is.EqualTo("Custom jaw"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnregisteredOrAmbiguousReplacementStopsWithoutChoosingByNames(bool ambiguous)
        {
            Skin(source, "Face", Mesh("UE/EyeClosed", "UE/JawOpen"));
            using var sourceBlink = BlinkExportSession.CaptureForExport(source);
            prepared = Object.Instantiate(source);
            using var blink = sourceBlink.ForClone(source, prepared);
            var guard = new UnifiedExpressionPreparation(prepared);
            var old = prepared.GetComponentInChildren<SkinnedMeshRenderer>();
            var first = Skin(prepared, "Face", old.sharedMesh);
            var second = Skin(prepared, "Face", old.sharedMesh);
            var registry = Registry();
            if (ambiguous) { Register(registry, old, first); Register(registry, old, second); }
            Object.DestroyImmediate(old.gameObject);
            using var preparation = Capture(prepared, registry);
            Assert.That(preparation.PreparedRendererFor(old), Is.Null);
            Assert.That(Assert.Throws<InvalidOperationException>(() => guard.RebindPrepared(preparation.PreparedRendererFor)).Message,
                Is.EqualTo(NdmfExportPreparation.UnknownRendererRelocation));
            Assert.That(Assert.Throws<InvalidOperationException>(() => blink.RebindPrepared(preparation.PreparedRendererFor)).Message,
                Is.EqualTo(NdmfExportPreparation.UnknownRendererRelocation));
        }

        [Test]
        public void RegisteredReplacementDoesNotChooseUnrelatedSameNameSibling()
        {
            Skin(source, "Face", Mesh("UE/JawOpen")); prepared = Object.Instantiate(source);
            var old = prepared.GetComponentInChildren<SkinnedMeshRenderer>();
            var sibling = Skin(prepared, "Face", old.sharedMesh);
            var replacement = Skin(prepared, "Face", old.sharedMesh);
            var registry = Registry(); Register(registry, old, replacement);
            Object.DestroyImmediate(old.gameObject);
            using var preparation = Capture(prepared, registry);
            Assert.That(preparation.PreparedRendererFor(old), Is.SameAs(replacement));
            Assert.That(preparation.PreparedRendererFor(sibling), Is.SameAs(sibling));
            Assert.That(Contract(registry).GetMethod("GetReference", new[] { typeof(Object), typeof(bool) })
                .Invoke(registry, new object[] { sibling, false }), Is.Null, "Inspection must not create provenance entries.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RegisteredReplacementTakesPriorityOverRetainedDisabledOriginal(bool ambiguous)
        {
            Skin(source, "Face", Mesh("UE/EyeClosed", "UE/JawOpen"));
            prepared = Object.Instantiate(source);
            var old = prepared.GetComponentInChildren<SkinnedMeshRenderer>();
            var guard = new UnifiedExpressionPreparation(prepared);
            var registry = Registry();
            var replacement = Skin(prepared, "Prepared face", old.sharedMesh);
            Register(registry, old, replacement);
            if (ambiguous) Register(registry, old, Skin(prepared, "Second replacement", old.sharedMesh));
            old.enabled = false;
            using var preparation = Capture(prepared, registry);
            Assert.That(old, Is.Not.Null, "NDMF may retain the old disabled component until later cleanup.");
            if (ambiguous)
            {
                Assert.That(preparation.PreparedRendererFor(old), Is.Null);
                Assert.That(Assert.Throws<InvalidOperationException>(() => guard.RebindPrepared(preparation.PreparedRendererFor)).Message,
                    Is.EqualTo(NdmfExportPreparation.UnknownRendererRelocation));
            }
            else
            {
                Assert.That(preparation.PreparedRendererFor(old), Is.SameAs(replacement));
                guard.RebindPrepared(preparation.PreparedRendererFor);
                Assert.DoesNotThrow(() => guard.VerifyIdentityAndDeformation());
            }
        }

        [Test]
        public void RegisteredReplacementRejectsAmbiguousSerializedPathDespiteUniqueProvenance()
        {
            Skin(source, "Face", Mesh("Custom jaw")); Author(new MorphTargetBinding("Face", 0, .25f));
            prepared = Object.Instantiate(source);
            using var bindings = new PreparedExpressionBindings(prepared, new VrChatExpressionMenu.Source());
            bindings.CaptureAuthoredExpressions();
            var old = prepared.GetComponentInChildren<SkinnedMeshRenderer>();
            Skin(prepared, "Final face", old.sharedMesh);
            var replacement = Skin(prepared, "Final face", old.sharedMesh);
            var registry = Registry(); Register(registry, old, replacement);
            Object.DestroyImmediate(old.gameObject);
            using var preparation = Capture(prepared, registry);
            bindings.RebindPrepared(preparation.PreparedRendererFor);
            Assert.That(preparation.PreparedRendererFor(old), Is.SameAs(replacement));
            Assert.That(bindings.AuthoringPath("Final face"), Is.Null);
            Assert.That(Assert.Throws<InvalidOperationException>(() => bindings.RebindAuthoredExpressions(value => value)).Message,
                Does.Contain("一意に確定"));
        }

        [Test]
        public void ExclusionsFollowIdentityWhenIncludedRendererReusesOmittedPath()
        {
            var mesh = Mesh("Opening"); Skin(source, "Omitted", mesh); Skin(source, "Included", mesh);
            prepared = Object.Instantiate(source);
            using var bindings = new PreparedExpressionBindings(prepared, new VrChatExpressionMenu.Source(), excludedPath: path => path == "Omitted");
            var omitted = prepared.transform.Find("Omitted"); var included = prepared.transform.Find("Included");
            omitted.name = "Moved omission";
            Assert.That(bindings.ExcludesPreparedPath("Moved omission"), Is.True);
            Assert.That(bindings.ExcludesPreparedPath("Omitted"), Is.True, "Dangling already-excluded curves remain ignored.");
            included.name = "Omitted";
            Assert.That(bindings.ExcludesPreparedPath("Omitted"), Is.False);
            Object.DestroyImmediate(included.gameObject);
            Skin(prepared, "Omitted", mesh);
            Assert.That(bindings.ExcludesPreparedPath("Omitted"), Is.False, "Generated renderers do not inherit source-path exclusion.");
        }

        [Test]
        public void NewRendererNeutralWithoutSelectedAuthorChannelsDoesNotRequireOldBaselineIdentity()
        {
            var original = Skin(source, "Face", Mesh("Original customization")); original.SetBlendShapeWeight(0, 100);
            prepared = Object.Instantiate(source);
            using var bindings = new PreparedExpressionBindings(prepared, new VrChatExpressionMenu.Source());
            bindings.CaptureAuthoredExpressions();
            var guard = new UnifiedExpressionPreparation(prepared);
            Assert.That(guard.SupportsUnified, Is.False);
            Object.DestroyImmediate(prepared.GetComponentInChildren<SkinnedMeshRenderer>().gameObject);
            var current = Skin(prepared, "Generated face", Mesh("UE/EyeClosed", "UE/JawOpen", "Generated opening"));
            bindings.RebindPrepared(value => value == null ? null : value);
            guard.RebindPrepared(value => value == null ? null : value);
            Assert.DoesNotThrow(() => guard.VerifyIdentityAndDeformation());
            bindings.RebindAuthoredExpressions(value => value);
            NeutralShapeSnapshot.Apply(prepared, new[] { new VrChatExpressionMenu.MorphValue {
                Path = "Generated face", Shape = "Generated opening", Weight = 100
            } });
            var neutral = NeutralShapeSnapshot.Capture(prepared);
            Assert.That(neutral.Get(current).Weights[2], Is.EqualTo(100));
            Assert.That(new UnifiedExpressionPreparation(prepared).SupportsUnified, Is.True);
            Assert.That(original.GetBlendShapeWeight(0), Is.EqualTo(100));
        }

        [Test]
        public void SameRendererReorderKeepsAbsoluteZeroAndDuplicateAuthorBinds()
        {
            var originalMesh = Mesh("Opening", "Custom jaw"); Skin(source, "Face", originalMesh);
            var author = Author(new MorphTargetBinding("Face", 1, 0), new MorphTargetBinding("Face", 1, .25f),
                new MorphTargetBinding("Face", -1, 1));
            prepared = Object.Instantiate(source);
            using var bindings = new PreparedExpressionBindings(prepared, new VrChatExpressionMenu.Source());
            bindings.CaptureAuthoredExpressions();
            prepared.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh = Mesh("Custom jaw", "Generated opening", "Opening");
            bindings.RebindPrepared(value => value); bindings.RebindAuthoredExpressions(value => value);
            var clip = prepared.GetComponent<Vrm10Instance>().Vrm.Expression.CustomClips.Single();
            Assert.That(clip.MorphTargetBindings.Select(value => value.Index), Is.EqualTo(new[] { 0, 0, -1 }));
            Assert.That(clip.MorphTargetBindings.Select(value => value.Weight), Is.EqualTo(new[] { 0, .25f, 1 }));
            Assert.That(author.Clip.MorphTargetBindings[0].Index, Is.EqualTo(1));
            Assert.That(originalMesh.GetBlendShapeName(1), Is.EqualTo("Custom jaw"));
        }

        [TestCase(0f)]
        [TestCase(.25f)]
        public void OriginallyInvalidIndexCannotBecomeUsableWhenNdmfAddsAChannel(float weight)
        {
            Skin(source, "Face", Mesh("Existing")); var author = Author(new MorphTargetBinding("Face", 1, weight));
            prepared = Object.Instantiate(source);
            using var bindings = new PreparedExpressionBindings(prepared, new VrChatExpressionMenu.Source());
            bindings.CaptureAuthoredExpressions();
            prepared.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh = Mesh("Existing", "Generated eyelid");
            bindings.RebindPrepared(value => value); bindings.RebindAuthoredExpressions(value => value);
            var clip = prepared.GetComponent<Vrm10Instance>().Vrm.Expression.CustomClips.Single();
            Assert.That(clip.MorphTargetBindings.Single().Index, Is.EqualTo(int.MaxValue));
            Assert.That(clip.MorphTargetBindings.Single().Weight, Is.EqualTo(weight));
            Assert.That(author.Clip.MorphTargetBindings.Single().Index, Is.EqualTo(1));
            Assert.That(author.Clip.MorphTargetBindings.Single().Weight, Is.EqualTo(weight));
            Assert.That(new UnifiedExpressionPreparation(prepared).SupportsUnified, Is.False);
        }

        [Test]
        public void PluginAlteredAuthorRouteIsRetainedThroughOwnedClipCorrelation()
        {
            Skin(source, "Face", Mesh("Custom jaw", "Plugin target")); var author = Author(new MorphTargetBinding("Face", 0, .25f));
            prepared = Object.Instantiate(source);
            using var bindings = new PreparedExpressionBindings(prepared, new VrChatExpressionMenu.Source());
            bindings.CaptureAuthoredExpressions();
            var settings = Object.Instantiate(author.Vrm); owned.Add(settings);
            var pluginClip = Object.Instantiate(author.Clip); owned.Add(pluginClip);
            pluginClip.MorphTargetBindings = new[] { new MorphTargetBinding("Face", 1, .75f) };
            settings.Expression.CustomClips = new List<VRM10Expression> { pluginClip };
            prepared.GetComponent<Vrm10Instance>().Vrm = settings;
            bindings.RebindPrepared(value => value);
            bindings.RebindAuthoredExpressions(value => ReferenceEquals(value, author.Clip) ? pluginClip : value);
            Assert.That(prepared.GetComponent<Vrm10Instance>().Vrm, Is.SameAs(settings));
            Assert.That(pluginClip.MorphTargetBindings.Single().Index, Is.EqualTo(1));
            Assert.That(pluginClip.MorphTargetBindings.Single().Weight, Is.EqualTo(.75f));
            Assert.That(author.Clip.MorphTargetBindings.Single().Index, Is.EqualTo(0));
            Assert.That(author.Clip.MorphTargetBindings.Single().Weight, Is.EqualTo(.25f));
        }

        [TestCase(0f)]
        [TestCase(.25f)]
        public void LostDeclaredKeyPreservesDisabledBindAndStopsUsableBind(float weight)
        {
            Skin(source, "Face", Mesh("Custom jaw")); var author = Author(new MorphTargetBinding("Face", 0, weight));
            prepared = Object.Instantiate(source);
            using var bindings = new PreparedExpressionBindings(prepared, new VrChatExpressionMenu.Source());
            bindings.CaptureAuthoredExpressions();
            prepared.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh = Mesh("Unrelated");
            bindings.RebindPrepared(value => value);
            if (weight == 0) Assert.DoesNotThrow(() => bindings.RebindAuthoredExpressions(value => value));
            else Assert.Throws<InvalidOperationException>(() => bindings.RebindAuthoredExpressions(value => value));
            Assert.That(author.Clip.MorphTargetBindings.Single().Index, Is.EqualTo(0));
            Assert.That(author.Clip.MorphTargetBindings.Single().Weight, Is.EqualTo(weight));
            Assert.That(source.GetComponent<Vrm10Instance>().Vrm, Is.SameAs(author.Vrm));
        }
    }
}
