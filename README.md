# VR Vlog lilToon VRM Exporter

Unity Editor package for exporting a VRM 1.0 file with two material
representations:

- standard `VRMC_materials_mtoon` fallback;
- optional, versioned VR Vlog lilToon material data.

The extension is never placed in `extensionsRequired`. Viewers that do not
understand it must remain able to render the MToon fallback.

## Install with VCC or ALCOM

Add the VR Vlog repository and install only **VR Vlog lilToon VRM Exporter**:

`https://mitsuya0077.github.io/VR-Vlog-lilToon-Exporter/index.json`

The VPM dependency resolver installs the tested `com.vrmc.gltf` and
`com.vrmc.vrm` 0.131.x packages automatically. The user does not need to find
or select the UniVRM packages separately. The package manager may show them in
the confirmation screen before installation.

## Workflow

1. Open **VR Vlog > lilToon VRM 1.0を書き出す**.
2. In **① アバター（必須）**, select the top-level avatar object from the
   Hierarchy. In **② 作者名（必須）**, enter the author name stored in the VRM.
   Those are the only required fields; the VRM name is taken from the selected
   object automatically.
3. Press **③ 保存先を選んでVRMを書き出す**, then choose the destination in the
   save dialog. The exporter creates a temporary cloned avatar,
   maps supported lilToon materials to a portable MToon10 fallback, exports VRM
   1.0 through the public UniVRM API, injects the optional lilToon extension,
   validates the complete GLB, and atomically commits one output file.

The source avatar, materials, textures, and importer settings are never
modified. The old existing-fallback workflow remains under **上級者向け：既存のVRM
1.0へlilToonデータを追加**.

Most extension textures reference the already optimized texture indices produced
by UniVRM. A custom backlight color texture is instead copied into the extension
as a PNG so it cannot be confused with a same-named fallback texture. This may
increase the VRM file size. Resize it beforehand when needed (1024 px recommended,
2048 px maximum for the mobile profile).

## Supported material subset

Main color, shadow, backlight color/settings/texture, normal map, emission, rim
light, matcap, and outline are preserved. Backlight uses an MToon rim-light
approximation in fallback viewers. Optional variants such as FakeShadow and
unsupported effects such as fur, refraction, gem, tessellation, and AudioLink
are reduced to the closest standard lilToon/MToon representation. Any omitted
details are listed as warnings after a successful export. Ambiguous texture
names are resolved by embedding the exact source texture only for those cases.
Invalid VRM structure, ambiguous material matching, corrupt image data, and mobile safety-limit
violations still stop the export.

The portable MToon fallback converts lilToon's outline width to metres at
one-hundredth scale. `_OutlineTex` is retained for lilToon restoration but is
not reused as MToon's unrelated green-channel outline-width mask.

## Install during development

Use VCC/ALCOM for a dependency-complete installation. Local package development
requires the matching UniVRM packages to already be present.

## Safety rules

- Never modify source materials, textures, or importer settings.
- Export only temporary copies.
- Always emit an MToon fallback.
- Reject unknown schema majors and invalid non-lilToon shader data.
- Bound material counts, texture counts, dimensions, and expanded memory.
- Do not claim pixel-identical output across render pipelines or devices.

## Compatibility

- Unity 2022.3 or later. VRChat creators should use VRChat's currently
  supported editor (2022.3.22f1 at the time of this release); later editor
  versions are source-compatible but are not a substitute for VRChat's
  required upload version.
- UniVRM 0.131.x (`com.vrmc.gltf` and `com.vrmc.vrm`)
- lilToon materials whose shader names identify lilToon, Lite, or Multi
- One-click export uses UniVRM's public `Vrm10Exporter.Export` API. New UniVRM
  minor series must be tested and released explicitly rather than accepted
  silently.
