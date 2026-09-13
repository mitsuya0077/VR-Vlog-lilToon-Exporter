# Local verification, 2026-09-13

Creator/exporter editor: Windows Unity **2022.3.22f1**. The same matrix also
passed in a separate Windows **2022.3.62f3** installation, the app's existing
release baseline. Creators do not need to change their 22f1 editor for this work.
lilToon: actual 2.3.4 package. Test Framework: 1.4.6. Collections: 2.1.4.

| UniVRM / UniGLTF | Official source commit | 2022.3.22f1 | 2022.3.62f3 |
| --- | --- | --- | --- |
| 0.131.0 / 0.131.0 | `3b99078d26b362733ad9bf463f98c83b8a1b4c9f` | 19 passed | 19 passed |
| 0.131.1 / 0.131.1 | `66558744b7fa50790d7cec217c6dd94b58c7441e` | 19 passed | 19 passed |
| 0.131.2 / 0.131.2 | `a4711bbf8c4d10659d3e5568c2e3d7d595005e51` | 19 passed | 19 passed |
| Not installed | No VRM/UniGLTF/lilToon package | Diagnostics passed | Diagnostics passed |
| Synthetic unsupported 0.132.0 | 0.131.2 source with fixture package metadata changed | Backend excluded; diagnostics passed | Backend excluded; diagnostics passed |

Each editor completed all 59 required cases, with none skipped. The 62f3 projects
were newly created with empty Assets and no copied Library or lock. The runner
asserted editor identity and actual installed package versions inside Unity;
its unique XML/log/report outputs confirmed all required cases passed.

The last row is deliberately an exclusion fixture, **not** a real 0.132.0 test.
Actual package versions and editor identity were asserted inside Unity. Both
ordinary and full-lilToon export/reimport preserved a synthetic humanoid's two
skinned renderers, vertex counts, morphs, material bindings and source references.
Existing renderer-selection and skin-weight cases also passed. These tests do
not assess pixel parity, advanced lilToon effects, the app's custom renderer,
private avatars, all MA/NDMF combinations, or iPhone performance.

Host checks: dependency boundaries 50 assertions; exporter 108; lighting/schema
849; texture resize 57; skin roots 7 scenarios; bake policy 390; NDMF preparation
36 cases / 178 assertions. NDMF host tests use an API/asset-graph adapter; their
two installed-package Unity tests were skipped. Packaging tests passed except
the host's unsupported symlink-creation case (covered by Linux CI).

The manual upstream checker successfully read the official latest stable release
for all four packages. No update was applied, and no paid Unity build, release,
TestFlight upload or device test was performed.
