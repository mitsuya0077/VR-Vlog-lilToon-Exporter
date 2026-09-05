#if EXPORTER_BEHAVIOR_TESTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VRVlog.LilToonExporter;

public static class ExporterBehaviorTests
{
    private static int _assertions;
    private static void Check(bool condition, string description)
    {
        _assertions++;
        if (!condition) throw new Exception(description);
    }
    private static Dictionary<string, object> Obj(params object[] pairs)
    {
        var result = new Dictionary<string, object>();
        for (int i = 0; i < pairs.Length; i += 2) result.Add((string)pairs[i], pairs[i + 1]);
        return result;
    }
    private static List<object> Arr(params object[] values) => new List<object>(values);
    private static Dictionary<string, object> Presets(Dictionary<string, object> root) =>
        (Dictionary<string, object>)((Dictionary<string, object>)((Dictionary<string, object>)((Dictionary<string, object>)root["extensions"])["VRMC_vrm"])["expressions"])["preset"];
    private static Dictionary<string, object> Fixture(params string[] names) => Obj(
        "asset", Obj("version", "2.0"),
        "extensions", Obj("VRMC_vrm", Obj("specVersion", "1.0")),
        "nodes", Arr(Obj("name", "unrelated"), Obj("mesh", 0L)),
        "meshes", Arr(Obj("extras", Obj("targetNames", names.Cast<object>().ToList()),
            "primitives", Arr(Obj("targets", names.Select(_ => (object)Obj()).ToList())))));
    private static byte[] Encode(Dictionary<string, object> root) => GlbDocument.Create(root, new byte[] { 7, 8, 9, 10 }).Write();

    public static string Run()
    {
        _assertions = 0;
        CheckMaterials();
        CheckHiddenMaterialInjection();
        var original = Encode(Fixture("unused", "eye_close", "eye_close_left", "eye_close_right", "mouth_a", "vrc.v.aa"));
        var output = VrmExpressionBindings.AddMissing(original);
        var result = GlbDocument.Read(output);
        var presets = Presets(result.Json);
        Check(presets.Count == 4, "Export bilateral blink and available mouth presets only.");
        Check(result.Binary.SequenceEqual(new byte[] { 7, 8, 9, 10 }), "Never rewrite mesh or image bytes.");
        foreach (var item in new[] { ("blink", 1L), ("blinkLeft", 2L), ("blinkRight", 3L), ("aa", 5L) })
        {
            var expression = (Dictionary<string, object>)presets[item.Item1];
            var binding = (Dictionary<string, object>)((List<object>)expression["morphTargetBinds"])[0];
            Check((long)binding["node"] == 1L && (long)binding["index"] == item.Item2 && Convert.ToDouble(binding["weight"]) == 1.0,
                "Bindings use final node and target indices, with VRChat viseme preference.");
        }
        Check(VrmExpressionBindings.AddMissing(output).SequenceEqual(output), "Reprocessing is idempotent.");
        var authored = Fixture("eye_close", "eye_close_left", "eye_close_right");
        var vrm = (Dictionary<string, object>)((Dictionary<string, object>)authored["extensions"])["VRMC_vrm"];
        var preset = Obj("blink", Obj("isBinary", true, "materialColorBinds", Arr(Obj("material", 0L))),
            "blinkLeft", Obj(), "blinkRight", Obj());
        vrm["expressions"] = Obj("preset", preset, "custom", Obj("custom-expression", Obj("isBinary", true)));
        var authoredBytes = Encode(authored);
        Check(VrmExpressionBindings.AddMissing(authoredBytes).SequenceEqual(authoredBytes), "Preserve authored, empty and custom expression settings.");
        var warnings = new List<string>();
        var unknown = Encode(Fixture("eye_close_extra", "previewBlink", "mouth_anger"));
        Check(VrmExpressionBindings.AddMissing(unknown, warnings).SequenceEqual(unknown), "Do not guess from substrings.");
        Check(warnings.Any(x => x.Contains("Blink")), "Report when blink could not be configured.");
        var duplicate = Encode(Fixture("eye_close_left", "EYE_CLOSE_LEFT"));
        Check(VrmExpressionBindings.AddMissing(duplicate, warnings).SequenceEqual(duplicate), "Reject ambiguous duplicate aliases.");
        var invalid = Fixture("eye_close");
        ((Dictionary<string, object>)((List<object>)invalid["nodes"])[1])["mesh"] = 999L;
        bool threw = false;
        try { VrmExpressionBindings.AddMissing(Encode(invalid)); } catch (InvalidOperationException) { threw = true; }
        Check(threw, "Reject invalid node/mesh indices.");
        var split = Fixture("eye_close_left");
        var mesh = (Dictionary<string, object>)((List<object>)split["meshes"])[0];
        ((List<object>)mesh["primitives"]).Add(Obj("targets", Arr()));
        threw = false;
        try { VrmExpressionBindings.AddMissing(Encode(split)); } catch (InvalidOperationException) { threw = true; }
        Check(threw, "Reject inconsistent target counts across split primitives.");
        return $"Exporter behavior checks passed ({_assertions} assertions).";
    }

    private static void CheckMaterials()
    {
        var image = new UnityEngine.Texture { name = "shared-image" };
        var material = new UnityEngine.Material { name = "arbitrary-material" };
        material.Properties["_MainTex"] = image;
        material.Properties["_EmissionMap"] = image;
        material.Properties["_UseEmission"] = 1f;
        material.Properties["_EmissionBlend"] = 1f;
        material.Properties["_EmissionColor"] = new UnityEngine.Color(1.4f, 1.4f, 1.4f);
        var warnings = new List<string>();
        var requested = new List<string>();
        var record = LilToonMaterialReader.Read(material, 0, (_, semantic) => { requested.Add(semantic); return 0; }, warnings);
        Check(!record.features.Contains("emission"), "Default policy suppresses whole-base-image emission.");
        Check(!requested.Contains("emission") && record.textures.Count == 1, "Suppressed emission does not resolve or embed a texture.");
        Check(record.colors.Single(c => c.name == "_EmissionColor").r == 0 && record.floats.Single(f => f.name == "_EmissionBlend").value == 0,
            "Extension scalars cannot re-enable suppressed emission.");
        Check(material.GetColor("_EmissionColor").r == 1.4f && material.GetFloat("_EmissionBlend") == 1f, "Source material is unchanged.");
        Check(warnings.Count == 1, "Disclose the appearance approximation.");
        record = LilToonMaterialReader.Read(material, 0, (_, __) => 0, suppressSharedTextureEmission: false);
        Check(record.features.Contains("emission") && record.colors.Single(c => c.name == "_EmissionColor").r == 1.4f,
            "Opt-out preserves intentional emission.");
        material.Properties["_EmissionMap"] = new UnityEngine.Texture { name = "shared-image" };
        record = LilToonMaterialReader.Read(material, 0, (_, __) => 0);
        Check(record.features.Contains("emission"), "Distinct textures are not matched by name.");
        material.Properties["_EmissionMap"] = null;
        record = LilToonMaterialReader.Read(material, 0, (_, __) => 0);
        Check(record.features.Contains("emission"), "Scalar-only emission remains supported.");
        material.Properties["_UseShadow"] = 1f;
        material.Properties["_ShadowColorTex"] = UnityEngine.Texture2D.whiteTexture;
        record = LilToonMaterialReader.Read(material, 0, (_, __) => 0);
        Check(record.features.Contains("shadow") && !record.textures.Any(t => t.semantic == "shadow"),
            "White shade placeholder must not override the fallback base image.");
        Check(material.GetTexture("_ShadowColorTex") == UnityEngine.Texture2D.whiteTexture, "Leave the source shade placeholder unchanged.");
        material.Properties["_ShadowColorTex"] = new UnityEngine.Texture { name = "authored-shade" };
        record = LilToonMaterialReader.Read(material, 0, (_, __) => 0);
        Check(record.textures.Any(t => t.semantic == "shadow"), "Preserve a separately authored shade image.");
    }

    private static void CheckHiddenMaterialInjection()
    {
        var root = new UnityEngine.GameObject();
        var body = new UnityEngine.Material { name = "body" };
        var outfit = new UnityEngine.Material { name = "outfit" };
        // A hidden material whose missing main texture would itself fail if read.
        outfit.Properties["_MainTex"] = new UnityEngine.Texture { name = "not-exported" };
        var hidden = new UnityEngine.Renderer { sharedMaterials = new[] { outfit } };
        hidden.gameObject.activeInHierarchy = false;
        root.Renderers.Add(hidden);
        root.Renderers.Add(new UnityEngine.Renderer { sharedMaterials = new[] { body } });
        var source = VRVlog.LilToonExporter.Tests.MaterialBindingFixture.Build("body");
        Func<byte[], int> injectCount = bytes => VRVlog.LilToonExporter.Tests.MaterialBindingFixture.InjectedMaterials(
            LilToonGlbExtension.Inject(bytes, root, "0.4.1", "2.3.4")).Count;
        Check(injectCount(source) == 1, "Inactive outfit absent from fallback must not abort material injection.");
        Check(!hidden.gameObject.activeInHierarchy && ReferenceEquals(hidden.sharedMaterials[0], outfit), "Hidden clothing and source materials remain unchanged.");
        hidden.gameObject.activeInHierarchy = true;
        hidden.enabled = false;
        Check(injectCount(source) == 1, "Disabled renderer must not require or decode its missing material.");
        hidden.enabled = true;
        bool threw = false;
        try { injectCount(source); } catch (InvalidOperationException e) { threw = e.Message.Contains("outfit"); }
        Check(threw, "A visible outfit missing from fallback must still fail explicitly.");
        outfit.Properties.Clear();
        Check(injectCount(VRVlog.LilToonExporter.Tests.MaterialBindingFixture.Build("body", "outfit")) == 2,
            "A visible outfit is retained when its fallback material exists.");
        hidden.sharedMaterials = new[] { body };
        Check(injectCount(source) == 1, "Shared visible material is injected only once.");
        root.activeInHierarchy = false;
        threw = false;
        try { injectCount(source); } catch (InvalidOperationException) { threw = true; }
        Check(threw, "An inactive avatar root is rejected rather than changing visibility.");
    }

    // The private fixture is supplied locally; it is never checked in or copied
    // into a package. Exercise the same production helper used by one-click export.
    public static string VerifyLocalVrm(string path)
    {
        var source = File.ReadAllBytes(path);
        var before = GlbDocument.Read(source);
        var after = GlbDocument.Read(VrmExpressionBindings.AddMissing(source));
        Check(before.Binary.SequenceEqual(after.Binary), "Fixture geometry/textures changed.");
        var presets = Presets(after.Json);
        Check(presets.ContainsKey("blinkLeft") && presets.ContainsKey("blinkRight"), "Fixture bilateral blink missing.");
        foreach (var name in new[] { "blinkLeft", "blinkRight" })
        {
            var expression = (Dictionary<string, object>)presets[name];
            foreach (Dictionary<string, object> binding in (List<object>)expression["morphTargetBinds"])
            {
                var node = (Dictionary<string, object>)((List<object>)after.Json["nodes"])[(int)(long)binding["node"]];
                var mesh = (Dictionary<string, object>)((List<object>)after.Json["meshes"])[(int)(long)node["mesh"]];
                var names = (List<object>)((Dictionary<string, object>)mesh["extras"])["targetNames"];
                Check((string)names[(int)(long)binding["index"]] == (name == "blinkLeft" ? "eye_close_left" : "eye_close_right"), "Fixture binding resolves to the wrong shape.");
            }
        }
        return $"Local VRM: preserved binary, generated {presets.Count} presets, verified bilateral blink bindings.";
    }
}
#endif
