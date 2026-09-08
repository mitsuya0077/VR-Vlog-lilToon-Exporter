#if EXPORTER_BEHAVIOR_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VRVlog.LilToonExporter;
using VRVlog.LilToonExporter.Tests;

public static class ExporterLightingBehaviorTests
{
    private static int assertions;
    private static void Check(bool value, string label)
    { assertions++; if (!value) throw new Exception(label); }
    private static void Near(double actual, double expected, string label)
    { Check(Math.Abs(actual - expected) < 0.00002, label); }
    private static double Saturate(double value) => Math.Min(1, Math.Max(0, value));
    private static void Reject(Action action, string label)
    { var rejected = false; try { action(); } catch (InvalidOperationException) { rejected = true; } Check(rejected, label); }

    public static string Run()
    {
        assertions = 0;
        // Independent evaluation of the two public shading equations, across
        // borders and normal directions. Catches an inverted boundary sign.
        foreach (float border in new[] { .1f, .2f, .5f, .8f, .95f })
        foreach (float blur in new[] { .01f, .1f, .4f, .8f })
        for (int i = 0; i <= 40; i++)
        {
            double dotNL = -1 + i * .05;
            double lower = Saturate(border - blur * .5), upper = Saturate(border + blur * .5);
            var lil = Saturate(((dotNL + 1) * .5 - lower) / (upper - lower));
            var shift = MobileMaterialMath.ShadowShift(border, blur);
            var toony = MobileMaterialMath.ShadowToony(border, blur);
            var mtoon = Saturate((dotNL + shift + 1 - toony) / (2 - 2 * toony));
            Near(mtoon, lil, "Portable shadow boundary must retain the source lit/shaded direction.");
        }
        foreach (float border in new[] { .1f, .5f, .9f })
        foreach (float power in new[] { .5f, 1f, 3f })
        {
            var halfAngle = Math.Pow(border, 1 / power);
            Near(Math.Pow(halfAngle, MobileMaterialMath.RimPower(border, power)), .5,
                "Fallback rim matches the authored midpoint without a positive front-face lift.");
        }
        var main = new Color(.2f, .4f, .8f);
        var shade = new Color(.1f, .2f, .4f);
        Near(MobileMaterialMath.ShadeColor(main, shade, 0).b, main.b, "Disabled shadow retains the main tint.");
        Near(MobileMaterialMath.ShadeColor(main, shade, .5f).linear.g,
            (main.linear.g + shade.linear.g) * .5, "Shadow strength blends in linear light.");

        var source = new Material { name = "lighting-fixture" };
        source.Properties["_UseShadow"] = 1f;
        source.Properties["_UseRim"] = 1f;
        source.Properties["_UseMatCap"] = 1f;
        source.Properties["_LightMinLimit"] = .25f;
        source.Properties["_LightMaxLimit"] = .8f;
        source.Properties["_RimEnableLighting"] = .3f;
        source.Properties["_LightDirectionOverride"] = new Vector4(-.3f, .7f, .2f, 1f);
        var record = LilToonMaterialReader.Read(source, 0, (_, __) => 0);
        var root = new LilToonExtensionRoot { exporterVersion = "0.8.0", sourceLilToonVersion = "2.3.4" };
        root.materials.Add(record);
        Check(LilToonExtensionValidator.TryValidate(root, out var error), error);
        Check(record.floats.Count == 20, "Enabled source effects retain all 20 lighting properties.");
        Near(record.floats.Single(f => f.name == "_LightMaxLimit").value, .8, "Authored limit survives reading.");
        Near(record.vectors.Single().x, -.3, "Direction is numeric, not gamma-converted color.");
        Near(record.vectors.Single().w, 1, "Local direction flag survives reading.");

        var avatar = new GameObject();
        avatar.Renderers.Add(new Renderer { sharedMaterials = new[] { source } });
        var input = MaterialBindingFixture.Build(source.name);
        var bytes = LilToonGlbExtension.Inject(input, avatar, "0.8.0", "2.3.4");
        LilToonGlbExtension.Validate(bytes, 1);
        var document = GlbDocument.Read(bytes);
        var extension = (Dictionary<string, object>)((Dictionary<string, object>)document.Json["extensions"])[LilToonMobileProfile.ExtensionName];
        Check((long)extension["schemaMinor"] == 2L, "New output declares schema 1.2.");
        var material = (Dictionary<string, object>)((List<object>)extension["materials"])[0];
        var vector = (Dictionary<string, object>)((List<object>)material["vectors"])[0];
        Near(Convert.ToDouble(vector["y"]), .7, "GLB round trip preserves numeric light direction.");
        var savedVectors = material["vectors"];
        material.Remove("vectors");
        Reject(() => LilToonGlbExtension.Validate(document.Write()), "1.2 requires direction metadata.");
        material["vectors"] = savedVectors;
        var floats = (List<object>)material["floats"];
        var min = floats.Cast<Dictionary<string, object>>().Single(f => (string)f["name"] == "_LightMinLimit");
        floats.Remove(min);
        Reject(() => LilToonGlbExtension.Validate(document.Write()), "1.2 cannot silently discard required illumination limits.");
        floats.Add(min);
        vector["w"] = .5;
        Reject(() => LilToonGlbExtension.Validate(document.Write()), "A malformed coordinate-space flag is rejected.");
        vector["w"] = 1.0;
        extension["schemaMinor"] = 1L;
        Reject(() => LilToonGlbExtension.Validate(document.Write()), "Legacy schema does not accept new vector keys.");
        material.Remove("vectors");
        floats.Clear();
        LilToonGlbExtension.Validate(document.Write());
        extension["schemaMinor"] = 0L;
        LilToonGlbExtension.Validate(document.Write());
        Check(true, "Old 1.0/1.1 records remain accepted without invented lighting metadata.");

        source.Properties["_LightMinLimit"] = .9f;
        source.Properties["_LightMaxLimit"] = .2f;
        source.Properties["_RimEnableLighting"] = float.NaN;
        var warnings = new List<string>();
        var safe = LilToonMaterialReader.Read(source, 0, (_, __) => 0, warnings);
        root.materials[0] = safe;
        Check(LilToonExtensionValidator.TryValidate(root, out error), error);
        Check(warnings.Count == 2, "Only actually invalid authoring values produce repair messages.");
        Near(safe.floats.Single(f => f.name == "_LightMinLimit").value, .2, "Inverted limits are repaired on output.");
        Near(source.GetFloat("_LightMinLimit"), .9, "The original material is never changed.");
        Check(float.IsNaN(source.GetFloat("_RimEnableLighting")), "Source invalid values remain untouched.");
        source.shader.name = "lilToonLite";
        var lite = LilToonMaterialReader.Read(source, 0, (_, __) => 0);
        Near(lite.floats.Single(f => f.name == "_RimShadowMask").value, 0, "Lite keeps its distinct rim shadow default.");
        return $"Lighting schema and conversion checks passed ({assertions} assertions); GPU rendering requires Unity.";
    }
}
#endif
