#!/usr/bin/env python3
import json
from pathlib import Path

root = Path(__file__).resolve().parents[1]
package = json.loads((root / "package.json").read_text(encoding="utf-8"))
schema = json.loads(
    (root / "Schema/VRVLOG_materials_liltoon.schema.json").read_text(encoding="utf-8")
)
profile = (root / "Editor/LilToonMobileProfile.cs").read_text(encoding="utf-8")
validator = (root / "Editor/LilToonExtensionValidator.cs").read_text(encoding="utf-8")
glb = (root / "Editor/GlbDocument.cs").read_text(encoding="utf-8")
injector = (root / "Editor/LilToonGlbExtension.cs").read_text(encoding="utf-8")
reader = (root / "Editor/LilToonMaterialReader.cs").read_text(encoding="utf-8")
window = (root / "Editor/LilToonExporterWindow.cs").read_text(encoding="utf-8")
one_click = (root / "Editor/UniVrmOneClickExporter.cs").read_text(encoding="utf-8")
listing = json.loads((root / "source.json").read_text(encoding="utf-8"))

assert package["name"] == "com.vrvlog.liltoon-vrm-exporter"
assert package["unity"] == "2022.3"
assert package["version"] == "0.3.0"
assert package["vpmDependencies"] == {
    "com.vrmc.gltf": "0.131.x",
    "com.vrmc.vrm": "0.131.x",
}
assert listing["url"].startswith("https://") and listing["url"].endswith("/index.json")
assert listing["url"] == "https://mitsuya0077.github.io/VR-Vlog-lilToon-Exporter/index.json"
assert listing["author"]["url"] == "https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter"
assert listing["infoLink"]["url"] == "https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter"
assert listing["githubRepos"] == ["mitsuya0077/VR-Vlog-lilToon-Exporter"]
assert schema["properties"]["schemaMajor"]["const"] == 1
assert schema["properties"]["materials"]["maxItems"] == 64
assert "VRVLOG_materials_liltoon" in profile
assert "new HashSet<int>()" in validator
assert "!materialIndices.Add(material.materialIndex)" in validator
assert "Duplicate materialIndex" in validator
assert "IsSupportedShaderFamily(material.shaderFamily)" in validator
assert "SupportedShaderFamilies" in profile
assert "extension.schemaMinor < 0" in validator
assert "IsValidVersion(extension.exporterVersion)" in validator
assert "IsSupportedRenderMode(material.renderMode)" in validator
assert "IsSupportedCullMode(material.cullMode)" in validator
assert "material.renderQueue > 5000" in validator
assert "MaximumFeaturesPerMaterial" in validator
assert "new HashSet<string>(StringComparer.Ordinal)" in validator
assert "!features.Add(feature)" in validator
assert "ValidateProperties(material, out error)" in validator
assert "MaximumFloatProperties" in validator
assert "MaximumColorProperties" in validator
assert "MaximumTextureProperties" in validator
assert "item.textureIndex >= LilToonMobileProfile.MaximumTextures" in validator
assert "float.IsNaN(value)" in validator
for property_names in ("floatNames", "colorNames", "textureNames"):
    assert f"!{property_names}.Add(item.name)" in validator
for supported in ("lilToon", "lilToonLite", "lilToonMulti"):
    assert f'"{supported}"' in profile
for rejected in ("fur", "refraction", "gem", "tessellation", "audioLink", "custom"):
    assert f'"{rejected}"' in profile

# Delivery contracts: GLB 2.0 parsing/writing, fallback preservation, optional
# extension injection, real material extraction, atomic output, and Unity tests.
assert "Only GLB 2.0 is supported" in glb
assert "GLB contains multiple JSON chunks" in glb
assert "VRMC_materials_mtoon" in injector
assert "extensionsRequired" in injector
assert "Custom extension must not be required" in injector
assert "already contains the lilToon extension" in injector
assert "must be an array" in injector and "must be an object" in injector
assert "Validate(output, extension.materials.Count)" in injector
assert "GetComponentsInChildren<Renderer>(true)" in injector
assert "_MainTex" in reader and "_UseShadow" in reader and "_UseOutline" in reader
assert "Texture '{texture.name}' is not present" in reader
assert "Unsupported lilToon shader variant" in reader
assert 'EndsWith("Outline"' in reader
assert 'material.shader.name.EndsWith("Outline"' in reader
assert "Unsupported lilToon transparent mode" in reader
assert "EnabledOrTexture" in reader
assert "UnsupportedFeatureToggles" in reader
assert "Unsupported enabled lilToon feature" in reader
for unsupported_toggle in ("_UseEmission2nd", "_UseBump2ndMap", "_UseMatCap2nd", "_AlphaMaskMode"):
    assert f'"{unsupported_toggle}"' in reader
assert "TextureFeatureEnabled" in reader
assert "ValidateEncodedTexture" in injector
assert "Encoded fallback texture is" in injector
assert "PNG dimensions must be positive" in injector
assert "JPEG SOF segment is invalid" in injector
assert "JPEG dimensions must be positive" in injector
assert "complete baseline/progressive JPEG" in injector
assert "JPEG SOS segment is invalid" in injector
assert "MIME type does not match" in injector
assert "PNG chunk CRC is invalid" in injector
assert "PNG IDAT/IEND structure is invalid" in injector
assert "Ambiguous texture name" in injector
assert "FromDom(root)" in injector
assert "LilToonExtensionValidator.TryValidate(extension" in injector
assert "Extension is missing from extensionsUsed" in injector
assert "outside the GLB materials array" in injector
assert "outside the GLB textures array" in injector
assert "RequireMToonFallback(glb.Json, material.materialIndex)" in injector
assert "VRMC_materials_mtoon 1.0 fallback" in injector
assert "RequireVrm10Root(glb.Json);" in injector
assert 'required==null||!Contains(required,"VRMC_vrm")' in injector
assert 'humanoid.TryGetValue("humanBones",out var rawBones)' in injector
assert "RequiredHumanBones" in injector
assert "has no valid node" in injector
assert 'meta.TryGetValue("name",out var rawName)' in injector
assert 'meta.TryGetValue("authors",out var rawAuthors)' in injector
assert 'meta.TryGetValue("licenseUrl",out var rawLicenseUrl)' in injector
assert "does not declare VRMC_materials_mtoon in extensionsUsed" in injector
assert "ValidateEncodedTexture(glb, texture.textureIndex, textureSources)" in injector
assert "Unexpected extension property" in injector
assert 'Guid.NewGuid().ToString("N")' in window
assert "File.Replace(temporary, outputPath, null)" in window
assert "finally { if (File.Exists(temporary)) File.Delete(temporary); }" in window
assert "UniVrmOneClickExporter.Export" in window
assert 'SupportedLilToonVersion = "2.3.4"' in window
assert 'package.name, "jp.lilxyzw.liltoon"' in window
assert "RequireSupportedLilToon()" in window
assert "Vrm10Exporter.Export" in one_click
assert "UnityEngine.Object.Instantiate(source)" in one_click
assert "ReplaceLilToonMaterials(clone" in one_click
assert "DestroyImmediate(clone)" in one_click
assert "MToon10Meta.UnityShaderName" in one_click
assert "CreateMToonFallback(source, created)" in one_click
assert "created.Add(material)" in one_click
assert 'Float(source, "_Cull", 2f) == 2f' in one_click
assert "context.Validate()" in one_click
assert "PackageInfo.FindForAssembly" in one_click
assert 'version.StartsWith(SupportedUniVrmSeries + "."' in one_click
assert (root / ".github/workflows/build-listing.yml").is_file()
assert (root / ".github/workflows/release-vpm.yml").is_file()
listing_workflow = (root / ".github/workflows/build-listing.yml").read_text(encoding="utf-8")
release_workflow = (root / ".github/workflows/release-vpm.yml").read_text(encoding="utf-8")
assert "cb31c3b5d17d1070d7741c61de2ca1b219224039" in listing_workflow
assert "dotnet-version: 8.0.x" in listing_workflow
assert "--disable-build-servers" in listing_workflow
assert "--maxcpucount:1" in listing_workflow
assert "${{ env.pathToCi }}/.nuke/temp" not in listing_workflow
assert "pull_request:" in listing_workflow
assert "check-listing-builder:" in listing_workflow
assert "if: github.event_name == 'pull_request'" in listing_workflow
assert "if: github.event_name != 'pull_request'" in listing_workflow
assert listing_workflow.index("if: github.event_name != 'pull_request'") < listing_workflow.index("environment:")
assert "Build listing tool" in listing_workflow
assert "Generate VPM listing" in listing_workflow
assert "3b99078d26b362733ad9bf463f98c83b8a1b4c9f" in release_workflow
assert '--target "${GITHUB_SHA}"' in release_workflow
assert 'os.environ["UNIVRM_VERSION"] == "0.131.0"' in release_workflow
assert 'tag v${VERSION} already exists' in release_workflow
assert 'git ls-remote --exit-code --tags origin' in release_workflow
assert "gh workflow run build-listing.yml --ref main" in release_workflow
assert '"com.vrmc.vrmshaders" not in gltf.get("dependencies", {})' in release_workflow
assert (root / "ThirdPartyNotices/UniVRM.md").is_file()
assert (root / "LICENSE").is_file()
assert "instance.Vrm.Meta.CopyTo" not in one_click
assert "Never copy contact information" in one_click
assert "NormalTextureScale = normalEnabled ?" in one_click
assert "EmissiveFactorLinear = emissionEnabled ?" in one_click
assert "MatcapColorFactorSrgb = matcapEnabled ?" in one_click
assert "ParametricRimColorFactorSrgb = rimEnabled ?" in one_click
assert "ShadeColorFactorSrgb = shadowEnabled ?" in one_click
assert "ShadeColorTexture = shadowEnabled ?" in one_click
for gated_texture in ("NormalTexture", "EmissiveTexture", "MatcapTexture", "RimMultiplyTexture", "OutlineWidthMultiplyTexture"):
    assert f"{gated_texture} =" in one_click and "? Texture(source," in one_click
assert 'private string avatarName = "";' in window
assert 'private string author = "";' in window
assert "ValidateAllEncodedTextures(glb, textureSources);" in injector
assert "materialCount > LilToonMobileProfile.MaximumMaterials" in injector
assert (root / "Tests/Editor/GlbDocumentTests.cs").is_file()
assert (root / "Tests/Editor/VRVlog.LilToonExporter.Editor.Tests.asmdef").is_file()

print("Exporter implementation, package, and schema checks passed.")
