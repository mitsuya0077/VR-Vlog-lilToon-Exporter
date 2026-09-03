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

1. Open **VR Vlog > Export lilToon VRM 1.0**.
2. Select the avatar root, enter the required VRM name and author, and choose an
   output path.
3. Press **Export VR Vlog VRM**. The exporter creates a temporary cloned avatar,
   maps supported lilToon materials to a portable MToon10 fallback, exports VRM
   1.0 through the public UniVRM API, injects the optional lilToon extension,
   validates the complete GLB, and atomically commits one output file.

The source avatar, materials, textures, and importer settings are never
modified. The old existing-fallback workflow remains under **Advanced**.

No texture is duplicated by the extension: it references the already optimized
texture indices produced by UniVRM. Optimize or resize textures in the UniVRM
export step (1024 px recommended, 2048 px maximum for the mobile profile).

## Supported material subset

Main color, shadow, normal map, emission, rim light, matcap, and outline are
preserved. Fur, refraction, gem, tessellation, AudioLink, custom shaders,
ambiguous names, and extension textures absent from the fallback are rejected.
The failure is intentional: the exporter never emits a partially interpretable
extension.

## Install during development

Use VCC/ALCOM for a dependency-complete installation. Local package development
requires the matching UniVRM packages to already be present.

## Safety rules

- Never modify source materials, textures, or importer settings.
- Export only temporary copies.
- Always emit an MToon fallback.
- Reject unknown schema majors and unsupported shader families.
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
