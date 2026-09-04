# Changelog

## 0.3.9

- Convert lilToon outline width to MToon10 world units at one-hundredth scale.
- Stop treating lilToon's outline-color texture as MToon's green-channel outline-width mask.

## 0.3.8

- Resolve duplicate fallback texture names by embedding the exact source Unity
  texture instead of aborting the export.
- Reuse already embedded source textures and report the exceptional embedding
  in the successful export warnings, limiting file-size growth to ambiguous
  cases.

## 0.3.7

- Accept UniVRM 0.131 VRM 1.0 output when its empty `extensionsRequired` array
  is omitted from the serialized GLB.
- Continue validating `VRMC_vrm` in `extensionsUsed`, its root object,
  specification version, humanoid hierarchy, metadata, and MToon fallbacks.

## 0.3.6

- Recognize optional lilToon shader names whose leaf is prefixed with labels
  such as `[Optional]`, including `lilToonFakeShadow`.
- Match lilToon family names case-insensitively before applying the standard
  approximation and warning behavior.

## 0.3.5

- Export unsupported lilToon variants, including `lilToonFakeShadow`, using the
  closest standard lilToon/MToon representation instead of aborting.
- Skip unsupported feature details and unmatched optional textures while
  reporting every approximation in the successful export dialog.
- Keep structural corruption, invalid VRM data, and mobile safety limits as
  hard export errors.

## 0.3.4

- Added full `_BacklightColorTex` export with non-destructive PNG encoding.
- Deduplicate newly embedded source textures by Unity object identity.
- Preserve texture filtering and wrapping while enforcing the mobile texture
  count and 2048 px dimension limits.

## 0.3.3

- Added support for lilToon backlight color and numeric settings.
- Added a portable MToon rim-light approximation for backlight.
- Reject custom backlight color textures with a specific message instead of
  silently dropping them.

## 0.3.2

- Localized the exporter window and user-facing errors into Japanese.
- Reduced the normal workflow to the avatar and author fields, deriving the
  VRM name automatically and asking for the destination only during export.
- Added inline guidance and moved existing-VRM injection into an advanced
  section.
- Kept automatic avatar naming consistent for VRM metadata and filenames.

## 0.3.1

- Fixed package installation compilation on Unity versions that also expose
  `UnityEditor.PackageInfo` by explicitly using
  `UnityEditor.PackageManager.PackageInfo`.
- Added validation that prevents ambiguous `PackageInfo` references from being
  reintroduced.

## 0.3.0

- Added VCC/ALCOM VPM dependency declarations for tested UniVRM 0.131.x packages.
- Added one-click VRM 1.0 fallback generation and lilToon-extension injection.
- Added non-destructive lilToon-to-MToon10 fallback material mapping.
- Added GitHub Release packaging and GitHub Pages VPM listing automation.
- Kept existing-fallback injection as an advanced workflow.

## 0.2.0

- Added the complete editor export window and atomic output workflow.
- Added lilToon material/feature/property extraction.
- Added GLB 2.0 parsing, lossless BIN preservation, extension injection, and round-trip validation.
- Reused UniVRM fallback texture indices instead of duplicating textures.
- Added fail-closed material and texture name matching.

## Unreleased

- Reject empty, unknown, and unsupported shader families during extension validation.
- Keep root metadata, render-state, and feature-list validation aligned with the JSON Schema.
- Validate material property collection limits and every float, color, and texture record.
- Reject duplicate property names within float, color, and texture collections.

## 0.1.0-preview.1

- Add the initial application-owned extension schema.
- Add a conservative mobile material compatibility profile.
- Add schema and package validation.
- Reject duplicate material indices and unsupported material features.
