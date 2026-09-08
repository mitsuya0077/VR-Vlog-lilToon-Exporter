# Changelog

## 0.7.2

- Fix CS1503 compilation errors in the shipped Editor tests by testing integer set membership through `HashSet.Contains` and boolean NUnit constraints. This removes the incompatible `Does.Not.Contain(int)` calls that blocked Unity after installing 0.7.1.

## 0.7.1

- Bake static UV0 main-color layers and tone correction with the installed lilToon baker, then share the prepared image between MToon and the optional lilToon extension. Reject unsupported layer configurations instead of silently losing the layer.
- Add an opt-out setting for suppressing HDR texture emission that remains on a different image after baking. Apply the policy only to temporary export materials.
- Read saved FaceEmo registered patterns and their authored expression clips directly, including separate scene launchers linked to the selected avatar, interleaved group order and trigger endpoints. VRChat condition evaluation and tracking-control settings are not reproduced.
- Allow explicit pet/gimmick exclusions on the temporary export copy. Protect humanoid and retained mesh bones; omit excluded animation channels before face validation; prune dependent VRM spring, constraint, first-person and expression references without changing shared assets.
- Downsample large baked layers proportionally to the mobile 2048-pixel limit, accounting for blend and adjustment masks.
- Add Editor tests for layer color/encoding, source preservation, FaceEmo registration and exclusions. See the release verification checklist for Unity GPU and device validation, which remain separate from host tests. Application-side framing requires the companion app fix.

## 0.7.0

- Keep changing BlendShape AnimationClips as named animated expressions instead of dropping them as non-constant. Export their first pose as an ordinary VRM custom expression and preserve all curve keys, weighted/stepped tangents, curve wrapping and clip looping in the optional `VRVLOG_expression_animations` extension for VR Vlog playback.
- Preserve source multi-frame morph geometry with reusable animation basis targets, including negative and partial weights. These internal targets are not registered as selectable expressions. Bound expanded geometry and validate all references before writing the VRM.
- Always refresh and import expressions on export. Remove the expression enable/gesture toggles, selection checklist, preview and reload button. Keep the completion dialog short; detailed conversion diagnostics remain in the Console.

## 0.6.1

- Include authored fixed facial AnimationClips targeted by GestureLeft/GestureRight transitions in the registered FX controller. Preserve each clip's complete BlendShape combination and name, including explicit zero and partial weights; do not catalogue raw mesh shapes. Resolve Animator overrides and deduplicate shared clips.
- Follow sub-state-machine entry/default destinations without importing unrelated descendant blink/reset states. Resolve synced-layer motion substitutions before controller-level clip replacements.
- Read gesture clips independently of running FX timers and SDK state behaviours, so unrelated automatic blink or Parameter Driver no longer prevents this import route. These entries apply the individual authored clip to the customized base face; they do not emulate cross-layer mixing, retained state values, or driver effects. BlendTrees are reported rather than split into misleading individual faces.
- Show the selected expression count before export and count actually registered VRChat expressions in the output. Gesture discovery also works without an enabled custom Expressions Menu.
- Keep export completion to two short lines. Move omission details to one Console entry and collapse unsupported candidates in the exporter window.
- Add constant-curve/output-count regression checks and Unity Editor cases for gesture discovery, automatic-blink isolation, overrides, duplicate clips, and rejection of animated or partial faces. Unity/device verification remains separate from command-line tests.

## 0.6.0

- Import supported VRChat Expressions Menu Buttons/Toggles as named, composed VRM expressions, including submenu gates and FX BlendTrees. Individual raw BlendShapes are not listed as expressions.
- Add a menu checklist with per-item conversion results. Re-read live menu/controller assets on each export and report unsupported puppets, non-morph animation and VRChat state drivers without silently exporting incomplete faces.
- Bake composed expressions relative to the customized rest face, including partial and decreasing weights and multiple renderers, without editing source meshes or scene values. Preserve existing VRM clips and bind only verified final export targets.
- Add executable menu traversal, geometry and VRM binding regressions, plus Unity Editor tests for actual Animator/BlendTree evaluation. Unity and device checks remain separate from command-line validation.

## 0.5.1

- Preserve MatCap color alpha and blend strength in the portable MToon light contribution, reducing excessive view-dependent highlights.
- Encode lilToon outline-width masks into MToon green-channel masks without changing source textures. Keep outline color textures separate.
- Omit outlines controlled by vertex colors, with an export warning: MToon cannot reproduce that width control, and uniform outlines can protrude through closed lips or eyelids.
- Recognize underscore VRChat vowel names such as `vrc.v_aa`, retaining existing authored expression presets.
- Add compiled conversion/viseme regressions and Unity mask-conversion tests.

## 0.5.0

- Preserve authored SkinnedMeshRenderer BlendShape values in one-click VRM base
  geometry. Work on per-renderer temporary mesh copies, leaving scene assets,
  skinning, topology and expression target names/indices unchanged.
- Rebase morph deltas so a default partially closed eye reaches the existing
  full-blink endpoint without double-applying the authored offset. Expressions
  blend from the customized rest face; other customized shapes remain present.
- Evaluate initial values across multiple frames, including extrapolation.
  VRM retains UniVRM's single final-frame target per shape. Nonzero defaults
  using zero-weight frames are rejected explicitly instead of silently lost.
- Add executable geometry regressions and Unity per-renderer isolation tests.

## 0.4.1

- Match UniVRM's active-object and enabled-renderer selection when converting
  materials and injecting compatibility data. Hidden wardrobe materials no
  longer cause `material 'outfit' is not present in fallback VRM` errors.
- Reject inactive avatar roots before cloning; preserve clothing visibility and
  keep missing materials on visible renderers as explicit errors.
- Add executable injection regressions and Unity hierarchy tests for hidden
  objects, inactive parents, disabled renderers, and re-enabled outfits.

## 0.4.0

- Generate missing standard blink and vowel expression bindings from known
  exported morph names, preserving authored VRM presets and custom expressions.
- Preserve the base image on the MToon shade side when lilToon shadows are off
  or no shade texture is assigned, preventing white shaded regions.
- Add a default-on, opt-out mobile appearance option that suppresses emission
  using the same texture object as the base image, with per-material warnings.
  Apply the choice consistently to fallback materials and compatibility data.
- Include the 0.3.9 outline unit/mask correction in one-click exports so their
  fallback stays bounded without relying on an application-side legacy repair.
- Add executable expression/material-reader regression checks to CI and Unity
  material fallback tests. Target-device appearance remains a separate gate.

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
