# Technical details

[日本語の導入ガイド](../README.md)

This is a Unity Editor exporter for VRM 1.0. It emits standard
`VRMC_materials_mtoon` fallback materials and optional VR Vlog data.
The optional extensions are listed in `extensionsUsed`, never
`extensionsRequired`; ordinary VRM viewers can use the standard fallback.

## Installation

- Tested editor: Unity 2022.3.22f1. Use the editor supported by VCC/ALCOM for
  VRChat projects. Other editor series are not covered by this validation.
- **lilToon 2.3.4 is required.** Install it and the source avatar first. The
  exporter checks this exact version; it does not install or upgrade lilToon.
- UniVRM 0.131.x is required. VCC/ALCOM resolves the published
  `com.vrmc.gltf` and `com.vrmc.vrm` 0.131.0 packages automatically.
- For Modular Avatar authoring, install its dependencies in the source
  project. The exporter supports NDMF 1.x starting at 1.8.3.

Add this repository URL in VCC/ALCOM and install **VR Vlog lilToon VRM Exporter**:

```text
https://mitsuya0077.github.io/VR-Vlog-lilToon-Exporter/index.json
```

For manual installation, obtain the exporter ZIP and both matching UniVRM
ZIPs from the release. Extract each into its own package directory, then use
Unity Package Manager's **Add package from disk** on each `package.json`,
starting with UniGLTF, then VRM, then the exporter. Keep these directories
available; Unity references them. Do not mix duplicate Assets-based and
Packages-based UniVRM installations. Back up the project before updates.

## Export workflow

Enable the desired outfit, renderers, avatar root and its parents. Open
**VR Vlog > lilToon VRM 1.0を書き出す**, select the top-level avatar, enter the
author name and choose the destination. The model name comes from the avatar
object. Only `.vrm` destinations are accepted; an existing file requires
overwrite confirmation.

The exporter processes a temporary copy, preserves supported initial
BlendShape values and expressions, applies supported NDMF authoring, converts
materials, serializes with UniVRM, adds optional data and validates the file
before replacing the destination. Source objects, meshes, materials, textures
and importer settings are preserved.

MA Bone Proxy / Merge Armature connections are applied as authored. There is
no manual bone-selection step. Names or root-level placement alone do not
establish a head connection. Independent props retain their connections.
General VRChat PhysBone and arbitrary gimmick conversion are outside scope;
existing compatible VRM spring chains are retained.

Object exclusions apply to one-click export. Required body/outfit joints are
protected. Expressions and references to excluded objects are pruned; invalid
or ambiguous retained references stop export instead of producing a partial file.

The advanced **上級者向け：既存のVRM 1.0へlilToonデータを追加** mode needs a
UniVRM-compatible MToon fallback matching the active source outfit. It writes
to a different file and does not rebuild geometry or expressions. Unbaked
main-color layers require one-click export.

## Material conversion

| Data | Conversion |
| --- | --- |
| Main color, first shadow, normal map, emission, rim, MatCap, outline | Supported settings are saved with an MToon approximation |
| Backlight | Additional data retains supported settings and texture; portable MToon approximates it with rim lighting |
| Main 2nd/3rd layers and color adjustment | Supported static UV0 settings are baked into the output image, including supported decals, copied patterns and MSDF text |
| Outline width mask | Source red-channel mask is converted to MToon's green-channel width mask |
| Vertex-color outline width | Omitted with a warning in 0.8.x; not equivalent to an ordinary width texture |
| Fur, refraction, gem, tessellation, AudioLink and unsupported variants | Approximated or omitted, with warnings |

UV animation, unsupported UV sets, sidedness, alpha operations and other
unbakeable combinations are reported with the affected material and next
action. Omitting listed layers is an explicit retry action and applies to all
uses of that material on the export copy.

Both emission-suppression options default to on. The first omits emission
when the base and emission maps use the same Unity texture object. The second
also suppresses qualifying HDR emission with a separate image in one-click
export. Disable the applicable option to retain intentional glow.

Output images are resized to a maximum dimension of 1024, preserving aspect
ratio. Color images use alpha-aware linear-light filtering; numeric maps retain
their channel meaning. The format validator accepts images up to 2048 for
compatibility with older files; this is not a selectable export quality preset.

0.8.x writes material schema 1.2. The full property contract is in
[Lighting.md](../Schema/Lighting.md) and the
[JSON Schema](../Schema/VRVLOG_materials_liltoon.schema.json).
Stored settings are not a promise of identical rendering. In VR Vlog versions
with a **lilToon互換表示** setting, enable it and reload the model to apply
supported additional data. Disabled or unsupported additional data uses the
standard fallback. Re-export the source avatar to recover settings absent
from an older file.

## Shape and expressions

Initial renderer BlendShape weights become the exported base geometry.
Expressions are rebased against it so an initial value is not added twice.
Shared meshes are copied per renderer. Multi-frame shapes are evaluated at
the initial value; ordinary UniVRM morph targets retain the final-frame
approximation. A nonzero default with a zero-weight frame stops with an error.

Missing standard blink and mouth expressions are populated only from known,
unambiguous morph names. Existing VRM expressions, including explicitly empty
presets, are preserved.

### Menu expressions

Registered Expressions Menu Button/Toggle items and submenus are evaluated
through the custom FX Controller, including parameter defaults and supported
BlendTrees. Each supported fixed BlendShape combination becomes one custom
VRM expression. Individual morph names are not exposed as separate expressions.

Puppet controls, time-varying menu expressions, material/object/bone changes,
hidden-renderer bindings, synchronized layers, and unsupported controller
behaviours such as Parameter Driver or Layer Control are not converted.
Tracking Control is handled through expression override flags. Collection
uses registered source data before NDMF; menus generated only during a build
are outside scope.

### Gesture and FaceEmo expressions

Gesture clips are collected from registered FX conditions for
`GestureLeft` / `GestureRight`. Supported BlendShape clips are applied to the
customized base face. AnimatorOverrideController substitutions are read and
duplicate clips are combined. Controller-layer composition, retained state,
Driver effects and gestures implemented only in the Gesture layer or generated
controllers are not reproduced. Gesture BlendTrees are not split into child clips.

FaceEmo uses saved settings associated with the target avatar, including
launchers elsewhere in the scene. Registered groups/patterns, default faces and
supported branch clips are read. Trigger endpoints become separate expressions;
unregistered patterns, continuous controls and VRChat tracking logic are not
simulated.

Supported animated gesture expressions retain their clip name, curves and
loop setting in an optional extension. A normal VRM expression contains the
first pose for viewers without playback support. Non-looping clips hold their
end pose; changing the selected expression ends the previous animation.
See [ExpressionAnimations.md](../Schema/ExpressionAnimations.md).

Unsupported entries are reported in **VR Vlog 書き出し詳細** in Unity Console.
The export does not silently truncate data exceeding its limits.

## Public reports and exported data

The exporter itself performs local conversion and has no avatar-upload or
telemetry implementation. Installed third-party editor plugins have their own
behaviour. Exported VRMs contain author/model names, geometry, images, material
and expression names, and version metadata. The author/name fields are taken
from the export window; metadata from an imported VRM is not implicitly copied.

Public issues should include versions, minimal reproduction steps and only the
relevant error excerpt. Review copied diagnostics for local paths, private names,
credentials and share links before posting. Do not attach private models,
project archives, paid textures or complete logs.

## Development checks

Run from a source checkout:

- `python Tools/validate.py`
- `python Tools/check-public-content.py`
- `python Tools/test-package.py`
- `pwsh -File Tools/run-behavior-tests.ps1`
- `pwsh -File Tools/run-bake-tests.ps1`
- `pwsh -File Tools/run-ndmf-preparation-tests.ps1`

Host tests execute production conversion code with synthetic inputs and API
doubles. They do not establish Unity rendering or end-device behaviour.
`Tests/Editor` requires a dependency-complete Unity project. Validate real
export/reimport and target-app appearance for the affected features.
See [ReleaseVerification.md](ReleaseVerification.md) for packaging and release.
