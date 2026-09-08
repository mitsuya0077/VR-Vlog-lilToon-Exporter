# Lighting profile 1.2

`VRVLOG_materials_liltoon` schema 1.2 retains the original material settings for a companion renderer. Portable MToon output is an approximation; it does not define the extension's source values. The extension remains optional in a standard VRM 1.0 file.

Every material requires `_AsUnlit`, `_LightMinLimit`, `_LightMaxLimit`, `_MonochromeLighting`, `_lilDirectionalLightStrength` and `_VertexLightStrength` in `floats`. In addition:

| Feature | Required float properties |
| --- | --- |
| `shadow` | `_ShadowNormalStrength`, `_ShadowReceive`, `_ShadowMainStrength`, `_ShadowEnvStrength` |
| `rimLight` | `_RimEnableLighting`, `_RimMainStrength`, `_RimShadowMask`, `_RimNormalStrength`, `_RimBlendMode` |
| `matCap` | `_MatCapEnableLighting`, `_MatCapMainStrength`, `_MatCapShadowMask`, `_MatCapNormalStrength`, `_MatCapBlendMode` |

`vectors` is a required array of exactly one `{ "name": "_LightDirectionOverride", "x": 0.001, "y": 0.002, "z": 0.001, "w": 0 }` entry. XYZ must be finite and within ±10000; W is 0 for world space or 1 for object space. A zero XYZ direction is legal and must be normalized safely by a renderer. `vectors` is absent in schemas 1.0/1.1.

The new float values are finite and within 0–1, except `_LightMaxLimit` (0–10) and the two blend modes (integers 0–3: normal, add, screen, multiply). The lower light limit cannot exceed the upper light limit. Exporting invalid values repairs only the output record and reports the adjusted property; it never rewrites the source material.

Old schemas remain readable. Missing lighting values use the corresponding lilToon 2.3.4 defaults: unlit 0, light minimum 0.05, maximum 1, monochrome 0, directional 1, vertex 0; shadow normal 1 and receive/main/environment 0; rim lighting 1, main 0, shadow mask 0.5 (Lite: 0), normal 1, blend mode 1; MatCap lighting 1, main/shadow mask 0, normal 1, blend mode 1. Values actually stored by an old exporter take precedence. Restoring a missing custom setting requires exporting the source avatar again.

The lighting limits apply to lighting, not the final emissive color. `_RimBlur` controls the rim transition and is independent of `_RimEnableLighting`. Emissive RGB is converted from authored sRGB to Linear once; the emission blend is applied once. The directional-strength property is retained even where the rendering pipeline does not use it (lilToon 2.3.4 Built-in uses OpenLit; the directional-strength control is consumed by HDRP).

Release the compatible application before publishing an exporter that writes schema 1.2.
