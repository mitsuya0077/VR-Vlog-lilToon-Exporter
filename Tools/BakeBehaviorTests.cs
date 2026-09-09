#if EXPORTER_BAKE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VRVlog.LilToonExporter;

public static class ExporterBakeBehaviorTests
{
    private static int assertions;

    public static void Run()
    {
        assertions = 0;
        GpuForbidden.Calls = 0;
        var initializedTexture = Texture2D.whiteTexture;
        StaticLayerSettingsAreAccepted();
        EveryCauseAndSourceLocationIsReportedWithoutMutation();
        DisabledInactiveAndExcludedRenderersAreIgnored();
        ConsentUsesMaterialIdentityAndDoesNotMutatePriorOptions();
        ApprovedOmissionOnlyChangesExportCopies();
        UvProofUsesOnlyTheRenderedSubmesh();
        AtlasFrameZeroIsRequired();
        StaticLayerAlphaIsAcceptedAndInvalidModesAreRejected();
        DynamicAndSurfaceDependentSettingsRemainBlocked();
        GuardRejectsBeforeGpuAllocation();
        Equal(GpuForbidden.Calls, 0, "No GPU operation was attempted");
        Console.WriteLine("Bake policy checks passed (" + assertions + " assertions). Production preflight, consent and omission compiled and executed.");
        Console.WriteLine("Unity APIs use property/hierarchy test doubles. Shader rendering and Unity Editor compatibility are not tested; GPU operations throw.");
    }

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception("FAILED: " + message);
    }
    private static void Equal<T>(T actual, T expected, string message) => Check(EqualityComparer<T>.Default.Equals(actual, expected), message + " (expected " + expected + ", actual " + actual + ")");
    private static T Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T error) { Check(error.GetType() == typeof(T), message + " exact exception type"); return error; }
        throw new Exception("FAILED: " + message + " did not throw " + typeof(T).Name);
    }
    private static void Valid(GameObject avatar, string message, Func<Transform, bool> excluded = null, MaterialBakeOptions options = null)
    {
        LilToonMainTextureBaker.ValidateAvatar(avatar, excluded, options);
        Check(true, message);
    }
    private static Material LayerMaterial(string name = "Clothing", string layer = "2nd")
    {
        var material = new Material(Shader.Find("lilToon")) { name = name };
        material.SetTexture("_MainTex", Texture2D.whiteTexture);
        material.SetColor("_Color", Color.red);
        material.SetFloat("_UseMain" + layer + "Tex", 1);
        return material;
    }
    private static Mesh TileMesh() => new Mesh
    {
        uv = new[] { Vector2.zero, Vector2.one, new Vector2(0, 1) },
        indices = new[] { new[] { 0, 1, 2 } }
    };
    private static SkinnedMeshRenderer Skin(GameObject avatar, params Material[] materials)
    {
        var renderer = avatar.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = TileMesh();
        renderer.sharedMaterials = materials;
        return renderer;
    }
    private static GameObject Avatar(Material material)
    {
        var avatar = new GameObject("Avatar");
        Skin(avatar, material);
        return avatar;
    }
    private static MaterialBakeIssue OneIssue(GameObject avatar, string setting)
    {
        var error = Throws<MaterialBakeException>(() => LilToonMainTextureBaker.ValidateAvatar(avatar), setting);
        Equal(error.Issues.Count, 1, setting + " is the only issue");
        Check(error.SourceUnchanged, setting + " identifies unchanged source");
        Check(error.Issues[0].Setting.StartsWith(setting, StringComparison.Ordinal), "Specific setting: " + setting);
        return error.Issues[0];
    }

    private static void StaticLayerSettingsAreAccepted()
    {
        foreach (var layer in new[] { "2nd", "3rd" })
        {
            var material = LayerMaterial(layer: layer);
            var avatar = Avatar(material);
            Valid(avatar, layer + " official enabled-layer defaults");
            var prefix = "_Main" + layer + "Tex";
            foreach (var feature in new[] { "IsDecal", "ShouldCopy", "ShouldFlipCopy", "IsMSDF" })
            {
                material.SetFloat(prefix + feature, 1);
                Valid(avatar, layer + " accepts static " + feature + " with proven UVs");
            }
            material.SetFloat(prefix + "Angle", 1.234f);
            material.SetVector(prefix + "_ScrollRotate", new Vector4(0, 0, 2.345f, 0));
            material.SetTextureScale(prefix, new Vector2(2, 1));
            material.SetTextureOffset(prefix, new Vector2(-1, 0));
            material.SetVector(prefix + "DecalSubParam", new Vector4(2, 2, .05f, .9f));
            Valid(avatar, layer + " static angle, layer ST, subparameters and unused scroll.z remain supported");
            Equal(material.GetFloat(prefix + "IsMSDF"), 1f, "Preflight does not disable MSDF");
            Equal(material.GetFloat(prefix + "ShouldCopy"), 1f, "Preflight does not disable copy");
        }
    }

    private static void EveryCauseAndSourceLocationIsReportedWithoutMutation()
    {
        var avatar = new GameObject("Avatar");
        var body = avatar.Child("Body");
        var clothing = avatar.Child("Wardrobe").Child("Clothing");
        var first = LayerMaterial("Same display name");
        var second = LayerMaterial("Same display name");
        first.SetFloat("_Main2ndTex_UVMode", 1);
        first.SetFloat("_Main2ndTexIsLeftOnly", 1);
        first.SetFloat("_UseMain3rdTex", 1);
        first.SetFloat("_Main3rdEnableLighting", 0);
        second.SetFloat("_Main2ndTexIsRightOnly", 1);
        var bodyRenderer = Skin(body, first);
        var clothingRenderer = Skin(clothing, second);
        var firstBefore = first.Snapshot();
        var secondBefore = second.Snapshot();
        var objectCount = UnityEngine.Object.Created;
        var materialCount = Material.Count;
        var textureCount = Texture2D.Count;
        var gameObjectCount = GameObject.Count;

        var error = Throws<MaterialBakeException>(() => LilToonMainTextureBaker.ValidateAvatar(avatar), "Aggregate preflight");

        Check(error.SourceUnchanged, "Preflight confirms source unchanged");
        Equal(error.Issues.Count, 4, "All four independent causes, two layers and two materials");
        Check(error.Issues.Any(i => i.Material == first && i.Layer == "2nd" && i.RendererPath == "Body" && i.Setting == "UV Mode: UV1"), "UV issue context");
        Check(error.Issues.Any(i => i.Material == first && i.Layer == "2nd" && i.RendererPath == "Body" && i.Setting == "左側のみ"), "Same layer second issue context");
        Check(error.Issues.Any(i => i.Material == first && i.Layer == "3rd" && i.RendererPath == "Body" && i.Setting == "ライティングの適用: 0"), "Third layer issue context");
        Check(error.Issues.Any(i => i.Material == second && i.Layer == "2nd" && i.RendererPath == "Wardrobe/Clothing" && i.Setting == "右側のみ"), "Nested renderer context and distinct material identity");
        Check(error.Issues.All(i => !string.IsNullOrWhiteSpace(i.Reason) && !string.IsNullOrWhiteSpace(i.NextStep)), "Every issue explains effect and next action");
        Check(error.Message.Contains("Same display name") && error.Message.Contains("UV Mode: UV1"), "Exception contains actionable material and setting");
        Equal(first.Snapshot(), firstBefore, "First material unchanged");
        Equal(second.Snapshot(), secondBefore, "Second material unchanged");
        Check(bodyRenderer.sharedMaterials[0] == first && clothingRenderer.sharedMaterials[0] == second, "Renderer material references unchanged");
        Equal(UnityEngine.Object.Created, objectCount, "Preflight allocates no Unity objects");
        Equal(Material.Count, materialCount, "Preflight allocates no materials");
        Equal(Texture2D.Count, textureCount, "Preflight allocates no textures");
        Equal(GameObject.Count, gameObjectCount, "Preflight does not clone avatar");
    }

    private static void DisabledInactiveAndExcludedRenderersAreIgnored()
    {
        var bad = LayerMaterial();
        bad.SetFloat("_Main2ndTexIsRightOnly", 1);
        var avatar = new GameObject("Avatar");
        var body = avatar.Child("Body");
        var disabled = Skin(body, bad);
        disabled.enabled = false;
        var wardrobe = avatar.Child("Inactive wardrobe");
        wardrobe.activeSelf = false;
        Skin(wardrobe.Child("Nested clothing"), bad);
        var pet = avatar.Child("Pet");
        Skin(pet.Child("Pet body"), bad);
        var externalMaterial = new Material(Shader.Find("Standard"));
        externalMaterial.SetFloat("_UseMain2ndTex", 1);
        externalMaterial.SetFloat("_Main2ndTexIsRightOnly", 1);
        Skin(avatar, null, externalMaterial);
        Func<Transform, bool> excluded = t => t.IsChildOf(pet.transform);
        Valid(avatar, "Inactive, disabled, excluded, null and non-lilToon materials are ignored", excluded);
        var issue = OneIssue(avatar, "右側のみ");
        Equal(issue.RendererPath, "Pet/Pet body", "Only retained active pet is reported");
        disabled.enabled = true;
        var error = Throws<MaterialBakeException>(() => LilToonMainTextureBaker.ValidateAvatar(avatar, excluded), "Retained active body");
        Equal(error.Issues.Count, 1, "Disabled filter does not suppress active body");
        Equal(error.Issues[0].RendererPath, "Body", "Retained renderer path");
        avatar.activeSelf = false;
        Throws<InvalidOperationException>(() => LilToonMainTextureBaker.ValidateAvatar(avatar), "Inactive avatar rejected");
        Throws<ArgumentNullException>(() => LilToonMainTextureBaker.ValidateAvatar(null), "Null avatar rejected");
    }

    private static void ConsentUsesMaterialIdentityAndDoesNotMutatePriorOptions()
    {
        var first = LayerMaterial("Identical names");
        var second = LayerMaterial("Identical names");
        first.SetFloat("_Main2ndTexIsLeftOnly", 1);
        first.SetFloat("_UseMain3rdTex", 1);
        first.SetFloat("_Main3rdTexIsRightOnly", 1);
        second.SetFloat("_Main2ndTexIsLeftOnly", 1);
        var avatar = new GameObject("Avatar");
        Skin(avatar, first, second);
        var all = Throws<MaterialBakeException>(() => LilToonMainTextureBaker.ValidateAvatar(avatar), "Consent fixture");
        Equal(all.Issues.Count, 3, "Three separately consented material/layer pairs");
        Check(all.Issues.All(i => i.RendererPath == "Avatar"), "Root renderer has readable avatar path");
        var empty = new MaterialBakeOptions();
        var firstLayerOnly = empty.WithOmissions(all.Issues.Where(i => i.Material == first && i.Layer == "2nd"));
        Check(!empty.Omits(first, "2nd"), "Consent does not mutate prior options");
        Check(firstLayerOnly.Omits(first, "2nd"), "Selected pair consented");
        Check(!firstLayerOnly.Omits(first, "3rd") && !firstLayerOnly.Omits(second, "2nd"), "Same name and other layer unconsented");
        var remaining = Throws<MaterialBakeException>(() => LilToonMainTextureBaker.ValidateAvatar(avatar, null, firstLayerOnly), "Partial consent");
        Equal(remaining.Issues.Count, 2, "Only selected layer omitted from preflight");
        Check(remaining.Issues.Any(i => i.Material == first && i.Layer == "3rd") && remaining.Issues.Any(i => i.Material == second), "Unapproved identities retained");
        var complete = firstLayerOnly.WithOmissions(remaining.Issues);
        Valid(avatar, "Explicit merged consent clears listed pairs", options: complete);
        Check(!firstLayerOnly.Omits(first, "3rd"), "Merging keeps previous options immutable");
        Equal(first.GetFloat("_UseMain2ndTex"), 1f, "Consent leaves original layer enabled");
        Equal(first.GetFloat("_UseMain3rdTex"), 1f, "Consent leaves original third layer enabled");
        Throws<InvalidOperationException>(() => empty.WithOmissions(new[] { new MaterialBakeIssue(null, "", "2nd", "", "", "") }), "Missing source identity cannot be approved");
        Throws<InvalidOperationException>(() => empty.WithOmissions(new[] { new MaterialBakeIssue(first, "", "Main", "", "", "") }), "Unrelated material scope cannot be approved");
    }

    private static void ApprovedOmissionOnlyChangesExportCopies()
    {
        var first = LayerMaterial("Shared material");
        var second = LayerMaterial("Shared material");
        first.SetFloat("_Main2ndTexShouldFlipMirror", 1);
        first.SetFloat("_UseMain3rdTex", 1);
        first.SetFloat("_Main3rdTexIsLeftOnly", 1);
        second.SetFloat("_Main2ndTexIsRightOnly", 1);
        var sourceAvatar = new GameObject("Source avatar");
        var source = Skin(sourceAvatar, first, second);
        var firstBefore = first.Snapshot();
        var secondBefore = second.Snapshot();
        var issues = Throws<MaterialBakeException>(() => LilToonMainTextureBaker.ValidateAvatar(sourceAvatar), "Omission fixture").Issues;
        var options = new MaterialBakeOptions().WithOmissions(issues);
        var exportAvatar = new GameObject("Export clone fixture");
        var exported = Skin(exportAvatar, first, second, first);
        var duplicate = Skin(exportAvatar.Child("Also shared"), first);
        var materials = new List<Material>();
        var textures = new List<Texture2D>();
        var warnings = new List<string>();
        var materialCount = Material.Count;
        LilToonMainTextureBaker.Prepare(exportAvatar, materials, textures, warnings, false, false, options);
        Equal(materials.Count, 2, "One copy per source identity");
        Equal(Material.Count - materialCount, 2, "No extra baker material allocated when all layers omitted");
        var slots = exported.sharedMaterials;
        Check(slots[0] != first && slots[1] != second, "Only export references replaced");
        Check(slots[0] != slots[1], "Same-name sources remain distinct");
        Check(slots[0] == slots[2] && slots[0] == duplicate.sharedMaterials[0], "Copy reused across slots and renderers");
        Check(materials.Contains(slots[0]) && materials.Contains(slots[1]), "Copies owned by export cleanup collection");
        Equal(slots[0].GetFloat("_UseMain2ndTex"), 0f, "Approved second layer disabled on copy");
        Equal(slots[0].GetFloat("_UseMain3rdTex"), 0f, "Approved third layer disabled on copy");
        Equal(slots[1].GetFloat("_UseMain2ndTex"), 0f, "Second source's approved layer disabled");
        Check(slots[0].GetTexture("_MainTex") == first.GetTexture("_MainTex"), "Base texture identity retained");
        Equal(slots[0].GetColor("_Color"), first.GetColor("_Color"), "Base tint retained");
        Equal(slots[0].GetFloat("_Main2ndTexShouldFlipMirror"), 1f, "Omission disables layer, not authored layer properties");
        Equal(textures.Count, 0, "All-omitted path allocates no baked texture");
        Equal(warnings.Count, 3, "One clear omission notice per approved enabled pair");
        Check(warnings.All(w => w.Contains("出力用コピー") && w.Contains("省略")), "Notices identify export-copy omissions");
        Equal(first.Snapshot(), firstBefore, "First source material unchanged after prepare");
        Equal(second.Snapshot(), secondBefore, "Second source material unchanged after prepare");
        Check(source.sharedMaterials[0] == first && source.sharedMaterials[1] == second, "Original avatar references unchanged");
        Check(!LilToonMainTextureBaker.NeedsBake(slots[0]), "Approved copy requires no GPU work");
    }

    private static void UvProofUsesOnlyTheRenderedSubmesh()
    {
        var decal = LayerMaterial();
        decal.SetFloat("_Main2ndTexIsDecal", 1);
        var plain = LayerMaterial();
        var avatar = new GameObject("Avatar");
        var renderer = Skin(avatar, decal, plain);
        var mesh = renderer.sharedMesh;
        mesh.uv = new[] { Vector2.zero, Vector2.one, new Vector2(0, 1), new Vector2(2, -1) };
        mesh.indices = new[] { new[] { 0, 1, 2 }, new[] { 1, 2, 3 } };
        Valid(avatar, "Decal ignores vertices referenced only by another material's submesh");
        renderer.sharedMaterials = new[] { plain, decal };
        OneIssue(avatar, "模様を置くUVの範囲");
        renderer.sharedMaterials = new[] { decal };
        Valid(avatar, "Extra unrendered submesh does not invalidate retained material");
        renderer.sharedMaterials = new[] { plain, plain, decal };
        OneIssue(avatar, "模様を置くUVの範囲");
        mesh.indices[1] = new[] { 0, 1, 2 };
        Valid(avatar, "Extra material slot uses last submesh as Unity does");
        renderer.sharedMaterials = new[] { decal };
        mesh.indices[0] = new[] { 0, 1, 9 };
        OneIssue(avatar, "模様を置くUVの範囲");
        mesh.indices[0] = Array.Empty<int>();
        OneIssue(avatar, "模様を置くUVの範囲");
        mesh.indices[0] = new[] { 0, 1, 2 };
        mesh.unreadable = true;
        OneIssue(avatar, "模様を置くUVの範囲");
        mesh.unreadable = false;
        mesh.uv[1] = new Vector2(float.NaN, 1);
        OneIssue(avatar, "模様を置くUVの範囲");
        mesh.uv = Array.Empty<Vector2>();
        OneIssue(avatar, "模様を置くUVの範囲");
        renderer.sharedMesh = null;
        OneIssue(avatar, "模様を置くUVの範囲");
        renderer.sharedMesh = new Mesh();
        OneIssue(avatar, "模様を置くUVの範囲");
        foreach (var feature in new[] { "ShouldCopy", "ShouldFlipCopy" })
        {
            decal.SetFloat("_Main2ndTexIsDecal", 0);
            decal.SetFloat("_Main2ndTex" + feature, 1);
            OneIssue(avatar, "模様を置くUVの範囲");
            decal.SetFloat("_Main2ndTex" + feature, 0);
        }
        var staticAvatar = new GameObject("Static mesh avatar");
        staticAvatar.AddComponent<MeshRenderer>().sharedMaterials = new[] { decal };
        staticAvatar.AddComponent<MeshFilter>().sharedMesh = TileMesh();
        decal.SetFloat("_Main2ndTexIsDecal", 1);
        Valid(staticAvatar, "Static MeshRenderer obtains UVs from MeshFilter");
    }

    private static void AtlasFrameZeroIsRequired()
    {
        var material = LayerMaterial();
        var avatar = Avatar(material);
        foreach (var atlas in new[] { new Vector4(1, 1, 1, 30), new Vector4(2, 2, 1, 30), new Vector4(2, 2, 0, 0) })
        {
            material.SetVector("_Main2ndTexDecalAnimation", atlas);
            Valid(avatar, "Frame-zero atlas accepted: " + atlas);
        }
        foreach (var atlas in new[] {
            new Vector4(2, 2, 4, 30), new Vector4(2, 2, 1, 0), new Vector4(2, 2, 3, 0),
            new Vector4(1, 1, 0, 30), new Vector4(.5f, 1, 1, 30), new Vector4(1, 0, 1, 30),
            new Vector4(1, 1, -1, 0), new Vector4(float.NaN, 1, 1, 30),
            new Vector4(1, float.PositiveInfinity, 1, 30), new Vector4(1, 1, float.NaN, 30), new Vector4(1, 1, 1, float.NegativeInfinity) })
        {
            material.SetVector("_Main2ndTexDecalAnimation", atlas);
            OneIssue(avatar, "デカールのコマ設定:");
        }
    }

    private static void StaticLayerAlphaIsAcceptedAndInvalidModesAreRejected()
    {
        foreach (var layer in new[] { "2nd", "3rd" })
        {
            var material = LayerMaterial(layer: layer);
            material.SetFloat("_Main" + layer + "TexAlphaMode", 3);
            var avatar = Avatar(material);
            Valid(avatar, "Opaque ignores stored alpha mode: " + layer);
            Check(!LilToonMainTextureBaker.NeedsLayerAlphaBake(material), "Opaque uses original RGB baker");
            foreach (var tag in new[] { "Transparent", "TransparentCutout", "" })
            {
                material.SetOverrideTag("RenderType", tag);
                Valid(avatar, "Static alpha accepted for tag " + tag);
                Check(LilToonMainTextureBaker.NeedsLayerAlphaBake(material), "Alpha-aware baker selected");
            }
            material.SetOverrideTag("RenderType", "Opaque");
            material.SetFloat("_TransparentMode", 1);
            Valid(avatar, "Cutout mode supports alpha");
            material.SetFloat("_TransparentMode", 0);
            foreach (var variant in new[] { "Cutout", "Transparent", "Refraction", "Gem", "Fur" })
            {
                material.shader = Shader.Find("Hidden/lilToon" + variant);
                Valid(avatar, "Static alpha supported for " + variant);
            }
            material.shader = Shader.Find("lilToon");
            foreach (var keyword in new[] { "UNITY_UI_CLIP_RECT", "UNITY_UI_ALPHACLIP", "_ALPHATEST_ON", "_ALPHABLEND_ON", "_ALPHAPREMULTIPLY_ON" })
            {
                material.shaderKeywords = new[] { keyword };
                Valid(avatar, "Static alpha supported for " + keyword);
            }
            foreach (var mode in new[] { 0f, 1f, 2f, 3f, 4f })
            {
                material.SetFloat("_Main" + layer + "TexAlphaMode", mode);
                Valid(avatar, "Accept valid alpha mode " + mode);
            }
            foreach (var mode in new[] { -1f, .5f, 5f, float.NaN, float.PositiveInfinity })
            {
                material.SetFloat("_Main" + layer + "TexAlphaMode", mode);
                OneIssue(avatar, "透明度の合成:");
            }
            material.SetFloat("_Main" + layer + "TexAlphaMode", 2);
            material.SetTextureScale("_MainTex", new Vector2(.5f, .5f));
            material.SetTextureOffset("_MainTex", new Vector2(.25f, .25f));
            Valid(avatar, "Contained transformed main UVs supported");
            material.SetTextureScale("_MainTex", new Vector2(2, 2));
            OneIssue(avatar, "透過を焼き込むUVの範囲");
            material.SetTextureScale("_MainTex", Vector2.zero);
            OneIssue(avatar, "メイン画像の配置");
            material.SetTextureScale("_MainTex", Vector2.one);
            material.SetTextureOffset("_MainTex", Vector2.zero);
            material.SetVector("_MainTex_ScrollRotate", new Vector4(0, 0, .5f, 0));
            OneIssue(avatar, "メイン画像の回転・移動・裏面UV");
            material.SetVector("_MainTex_ScrollRotate", Vector4.zero);
            material.SetTextureScale("_Main" + layer + "Tex", new Vector2(float.PositiveInfinity, 1));
            OneIssue(avatar, "レイヤーの配置");
            material.SetTextureScale("_Main" + layer + "Tex", Vector2.one);
            var issue = new MaterialBakeIssue(material, "", layer, "省略", "", "");
            var options = new MaterialBakeOptions().WithOmissions(new[] { issue });
            Check(!LilToonMainTextureBaker.NeedsLayerAlphaBake(material, options), "Omitted alpha layer does not affect the selected baker");
            material.SetFloat("_Main" + layer + "TexAlphaMode", 0);
            Valid(avatar, "Transparent alpha guard ignores mode zero: " + layer);
        }
    }

    private static void DynamicAndSurfaceDependentSettingsRemainBlocked()
    {
        var material = LayerMaterial();
        var avatar = Avatar(material);
        foreach (var uv in new[] { 1f, 2f, 3f, 4f })
        {
            material.SetFloat("_Main2ndTex_UVMode", uv);
            OneIssue(avatar, uv == 4 ? "UV Mode: MatCap" : "UV Mode: UV" + uv);
        }
        material.SetFloat("_Main2ndTex_UVMode", 0);
        foreach (var pair in new[] { ("IsLeftOnly", "左側のみ"), ("IsRightOnly", "右側のみ"), ("ShouldFlipMirror", "ミラー側を反転") })
        {
            material.SetFloat("_Main2ndTex" + pair.Item1, 1);
            OneIssue(avatar, pair.Item2);
            material.SetFloat("_Main2ndTex" + pair.Item1, 0);
        }
        foreach (var scroll in new[] { new Vector4(1, 0, 0, 0), new Vector4(0, 1, 0, 0), new Vector4(0, 0, 0, 1) })
        {
            material.SetVector("_Main2ndTex_ScrollRotate", scroll);
            OneIssue(avatar, "模様のスクロール・回転速度:");
        }
        material.SetVector("_Main2ndTex_ScrollRotate", Vector4.zero);
        material.SetVector("_MainTex_ScrollRotate", new Vector4(0, 0, 1, 0));
        OneIssue(avatar, "メイン画像の移動・拡縮・回転");
        material.SetVector("_MainTex_ScrollRotate", Vector4.zero);
        material.SetTextureScale("_MainTex", new Vector2(2, 1));
        OneIssue(avatar, "メイン画像の移動・拡縮・回転");
        material.SetTextureScale("_MainTex", Vector2.one);
        material.SetTextureOffset("_MainTex", new Vector2(.1f, 0));
        OneIssue(avatar, "メイン画像の移動・拡縮・回転");
        material.SetTextureOffset("_MainTex", Vector2.zero);
        material.SetFloat("_Main2ndTex_Cull", 2);
        Valid(avatar, "Matching default Back cull is redundant and supported");
        material.SetFloat("_Main2ndTex_Cull", 1);
        OneIssue(avatar, "表示する面: 裏面のみ");
        material.SetFloat("_Cull", 1);
        Valid(avatar, "Matching Front cull is redundant and supported");
        material.SetFloat("_Main2ndTex_Cull", 2);
        OneIssue(avatar, "表示する面: 表面のみ");
        material.SetFloat("_Cull", 0);
        OneIssue(avatar, "表示する面: 表面のみ");
        material.SetFloat("_Main2ndTex_Cull", 0);
        material.SetFloat("_Main2ndEnableLighting", .5f);
        OneIssue(avatar, "ライティングの適用:");
        material.SetFloat("_Main2ndEnableLighting", 1);
        material.SetVector("_Main2ndDissolveParams", new Vector4(1, 0, .5f, .1f));
        OneIssue(avatar, "Dissolve");
        material.SetVector("_Main2ndDissolveParams", Vector4.zero);
        material.SetVector("_Main2ndDistanceFade", new Vector4(0, 0, 1, 0));
        OneIssue(avatar, "距離フェード");
        material.SetVector("_Main2ndDistanceFade", Vector4.zero);
        material.SetFloat("_AudioLink2Main2nd", 1);
        Valid(avatar, "Inactive AudioLink has no effect");
        material.SetFloat("_UseAudioLink", 1);
        OneIssue(avatar, "AudioLink");
    }

    private static void GuardRejectsBeforeGpuAllocation()
    {
        var material = LayerMaterial();
        material.SetFloat("_Main2ndTex_UVMode", 4);
        material.SetFloat("_Main2ndTexShouldFlipMirror", 1);
        var textures = new List<Texture2D>();
        var count = UnityEngine.Object.Created;
        var before = material.Snapshot();
        var error = Throws<MaterialBakeException>(() => LilToonMainTextureBaker.Bake(material, textures, new List<string>()), "Direct bake guard");
        Equal(error.Issues.Count, 2, "Direct guard reports all causes too");
        Check(!error.SourceUnchanged, "Internal bake guard does not claim preflight source guarantee");
        Equal(UnityEngine.Object.Created, count, "Guard creates no baker material or texture");
        Equal(textures.Count, 0, "No output texture before guard");
        Equal(material.Snapshot(), before, "Guard does not change supplied material");
        Throws<ArgumentNullException>(() => LilToonMainTextureBaker.Bake(material, null, null), "Null output ownership collection rejected");
    }
}
#endif
