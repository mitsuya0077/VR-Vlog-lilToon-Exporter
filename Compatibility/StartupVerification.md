# Exporter startup verification, 0.10.4

Creator editor: Windows Unity 2022.3.22f1. The 0.10.3 report showed supported
lilToon 2.3.4 and UniVRM/UniGLTF 0.131.0, MA 1.19.0-alpha.0 and NDMF 1.14.8,
but no registered exporter entry point. The cropped Console image showed update
warnings, not a compiler error. The reason registration was missing in that
particular project has not been reproduced.

## Before and after

- Actual 0.10.3 published ZIPs, all three dependencies embedded as in VCC/ALCOM:
  19 real Unity tests passed. Startup without exporter test assemblies also opened the menu.
- The same packages with actual VRChat SDK Base/Avatars 3.10.5, NDMF 1.14.8 and
  the official MA 1.19.0-alpha.0 release ZIP: 19 tests passed on clean startup.
  The reporter's VRChat SDK version was not supplied; 3.10.5 is our fixture.
- Injecting the specific missing-registration state into 0.10.3 made
  `MenuResolvesBackendWithoutInitializationRegistration` fail: the exporter
  window did not open. This is a deterministic regression, not a reproduction
  of the original import/reload sequence.
- With 0.10.4's direct lookup and no initialization callback, all 20 tests passed
  in that VRChat/MA/NDMF project, including the menu and two VRM round trips.
- UniVRM/UniGLTF 0.131.1 and 0.131.2: 20 tests passed per version.
- Missing dependencies and the synthetic unsupported 0.132.0 fixture: 2 tests
  passed per environment. The menu still opens independent diagnostics.

64 focused Unity cases passed across these five environments, none skipped.
The unsupported fixture tests exclusion, not compatibility with a future release.
The dependency policy, version gates and VRM data format are unchanged.

The production startup probe is also run without exporter test assemblies or
`testables`. UniGLTF's transitive Test Framework dependency is preserved. It checks the real menu, resolves the backend again after resetting
its cache, exercises the already-loaded reload path and generates a Ready report.
The manual recompilation recovery path uses Unity's own import/compilation APIs;
it cannot repair a compiler error in another package or prove recovery from every
project-specific cache state.

Host checks passed: dependency policy 50 assertions; exporter 108;
lighting/schema 849; resize 57; skin roots 7 cases; bake 390; NDMF adapter
36 cases/178 assertions. The latter's two installed-package checks remain skipped;
the VRChat run above validates startup and synthetic export, not every MA feature.
No app build, TestFlight upload or private-avatar/device rendering test is part
of this hotfix.
