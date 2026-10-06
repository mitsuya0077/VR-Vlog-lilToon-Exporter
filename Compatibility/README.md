# Dependency compatibility maintenance

`dependencies.json` is the reviewed dependency policy. Run
`python3 Tools/generate-compatibility.py --write` after changing it, and run the
same command without `--write` to detect drift. Generated source is included in
the installable package; development manifests and test tools are excluded.

The independent Editor compatibility assembly references only Unity. It owns
the menu and environment diagnostics. The UniVRM backend and its tests compile
only when both VRM10 and UniGLTF match an explicitly listed version. The runtime
guard also rejects mixed versions. Missing/unsupported packages therefore do
not make this exporter's UniVRM references break compilation before diagnostics.
Compilation errors inside another package can still prevent Unity from loading
any new editor code; this cannot repair those packages.

The menu resolves the loaded backend's public `Open()` method when it is used;
there is no startup registration callback. Missing lookups are not cached.
Explicit **Reload** reimports this exporter's assembly definition and requests a
clean script compilation only when dependencies are supported and the backend
is absent. It never changes dependencies or avatar assets. The diagnostics copy
includes package versions, compilation state, loaded backend and version defines,
without absolute paths or avatar data. A missing backend alone is not evidence
of a compile error in another tool.

The VPM requirement stays at `0.131.x` to avoid forcing changes to an existing
creator project. Only the explicitly listed patches can export. A future patch
selected by VPM requires review before it is enabled. Diagnostics never install,
upgrade or downgrade packages. MA and NDMF remain optional; existing component,
API and ownership checks run during preparation. Reference versions are not a
claim that every avatar or every combination is verified.

## Upgrade procedure

1. Run `python3 Tools/check-upstream-dependencies.py --output upstream.json`
   or the manual **Check upstream dependency releases** Actions workflow. This only
   reports stable upstream releases; it neither changes dependencies nor starts
   a Unity build. No scheduled CI or paid build was introduced.
2. Review the official changes. Add a version and immutable source commit to the
   policy in a branch. For a different UniVRM API family, add a separately gated
   backend; keep third-party types out of the compatibility assembly. For a
   different lilToon property/keyword catalogue, review exporter and app readers,
   shader specialization and format version together. Do not merely relax the
   version/commit check.
3. In separate empty Unity projects, install the candidate's actual UniGLTF and
   VRM10 packages from the same source commit, lilToon 2.3.4, this exporter as a
   local development package and Unity Test Framework 1.4.6. Include
   `"testables": ["com.vrvlog.liltoon-vrm-exporter"]` in each manifest. Use
   `com.unity.collections` 2.1.4 for the existing mesh tests. Never test by changing
   only a supported package's version label and claim a real upstream result.
4. Run the focused test runner for every supported version:

   ```text
   python3 Tools/run-unity-compatibility.py --unity UNITY_EXECUTABLE --project TEST_PROJECT --expect-univrm 0.131.1 --expect-unity 2022.3.22f1 --output TEST_RESULTS
   ```

   Use 2022.3.22f1 for the existing creator environment. Also test the app's
   separate release baseline with `--expect-unity 2022.3.62f3` (the runner default).
   This does not require creators to upgrade their editor. The runner
   rejects missing/old results, skipped required tests and unexpected installed
   versions. It tests ordinary and full-lilToon export/reimport, meshes, morphs,
   material bindings, source preservation, renderer selection and skin weights.
5. Repeat with no UniVRM (`--expect-univrm missing`) and an unsupported-package
   fixture to verify the independent diagnostics. A synthetic unsupported
   fixture proves exclusion/diagnostics only, never future API compatibility.
   Run the existing MA/NDMF tests with the actual optional packages when those
   integrations change. Use the app's loading and full-lilToon behavior tests and
   compare transparent materials, expressions and representative avatars on a
   device before release. Synthetic mesh tests do not prove visual equivalence.
6. Record exact versions, editor, source commits, results and remaining limits.
   Review the PR and required checks before changing the supported matrix.

## Exporter behavior and integration regression profiles

Dependency compatibility alone does not prove the one-click export pipeline.
Before releasing changes to neutral sampling, expression endpoints or preparation,
run the generated-fixture behavior profile in a separate, explicitly pinned project:

```text
python3 Tools/run-unity-compatibility.py --profile exporter-behavior --unity UNITY_EXECUTABLE --project INTEGRATION_TEST_PROJECT --expect-univrm 0.131.0 --expect-unity 2022.3.22f1 --output TEST_RESULTS
```

The default `compatibility` profile still requires the same 20 real-package cases
in a supported environment, or two diagnostic cases in a missing/unsupported one.
Both exporter profiles require a supported real UniVRM environment, the packages
listed in step 3, and the actual VRChat SDK, Modular Avatar and NDMF. Use the
reviewed authoring reference versions in `dependencies.json`; pin and record the
actual SDK version as well. These test prerequisites do not make MA/NDMF mandatory
dependencies for ordinary exporter users. The runner installs or changes nothing.

The behavior profile selects these complete classes in addition to compatibility:

- `NeutralShapeSamplerTests`, `ParameterDriverExpressionTests`, `MergedFxDefaultsTests`
- `DirectExpressionNativeLayersTests`, `MergedFixedNeutralSamplingTests`, `NeutralCurveConditionTests`
- `NeutralShapeEndpointTests`, `PreparedNeutralEligibilityTests`, `PreparedNeutralExportTests`
- `NeutralShapePipelineTests`, `NeutralShapeExportTests`, `UnifiedExpressionExportTests`
- `MaSceneReferencePreparationTests`, `NdmfPreparationTests`
- `FaceEmoPreparedFxIntegrationTests`
- `AdditionalPlayableCallbackTests`, `NeutralLayerControlTests`
- `TemporalNeutralShapeTests`

They cover fixed external inputs and dormant Action branches; whole prepared
appearance versus independent neutral morphs; native Additive/Override and Write
Defaults behavior; native source-frame clamping versus unlimited explicit endpoint
arithmetic; scalar sampling with independent Transform support on influencing
skin bones; absolute endpoints;
blink eligibility after preparation; renderer mapping; source-reference ownership;
phase ordering and cleanup; and real export/reimport. Native Animator evaluation
and source geometry provide independent expectations. Required cases also cover
optional missing/unexported roots, normal-external ownership pruning, direct or
reachable Copy-linked appearance controls (including transitive range conversion),
and source tracking obligations after preparation removes an authoring marker.
Direct gesture probes also require normal VRChat inputs and authored contact
defaults to match native sampling, while unresolved external inputs remain rejected.
Registration tests retain the selected state's effective clip and deterministic
callbacks, reject invalid or ambiguous callback provenance, and keep different
native outcomes from a shared clip. Identical evaluated registrations remain
deduplicated. The generated FaceEmo fixtures exercise exact or proved retargeted
clip identity and reject missing/changed state, motion or callback context.
An original registered clip identity cannot select a state when its effective
override supplies different motion data.
An existing NDMF object-registry origin may prove a uniquely matching augmented
prepared clip; the actual effective prepared motion and its callbacks then define
the entry. Missing/wrong/ambiguous origins and deferred clip or override changes
remain rejected.
Direct scalar support must keep prepared bone/wardrobe appearance, protect
captured Renderer activation, and defer unknown authored channels until prepared
renderer/mesh resolution.
An active base-state probe must retain its callbacks and dynamic upper graph;
an inactive slot must retain its authored clip without being force-enabled, and
reject callbacks that could change the retained graph.
An exactly identified moving gesture must retain its native lower, Additive and
Write Defaults support without an unrelated upper expression's reset. A proved
permanent upper override and a configured single-state BlendTree's native
contribution remain authoritative. Disconnected unsupported clips must not affect neutral sampling. Native Unity
and VRChat constraint settings keep their original WD/additive support without
committing the probe's pose. Unsupported appearance support retains the affected
prepared neutral weights with a warning; selected expression endpoints still
require independent validation. Coupled required morphs retain the whole prepared
appearance as their neutral instead of rejecting export. Bad curves, ambiguous
bindings, events and unknown callbacks remain errors and are checked before any
recoverable neutral refusal. A last, stationary, unmasked full Override layer
that explicitly writes every captured morph can prove those scalars independent
of lower state inputs and timing. The full native evaluation graph stays intact;
partial weights and masks do not authorize this exception.

In `0.11.11-beta.9`, documented SDK Animator/Playable layer-weight commands are
classified separately from unknown callbacks. An unsupported neutral effect
retains the affected prepared appearance with a warning; it does not authorize a
selected endpoint. A full final scalar override can remain independent only of
commands targeting lower FX layers, never the final layer or the whole FX playable.
SDK callbacks are not executed or emulated, and a goal weight of one is not a
no-op proof. Other playable data and reachable parameter writers are checked before
neutral fallback, including parameters read across controller boundaries. Original
controllers and avatar settings remain unchanged. See [avatar preservation](AvatarPreservation.md)
for the body-playable metadata boundary and the difference from running VRChat.
Copy ownership does not expand through Set/Add/Random or generic reset morphs.
The transient-input matrix requires an unsaved declared signal with compatible
types/defaults, no menu input, a raw producer in another playable, no raw FX
producer, and no producer reachable under that playable's normal external inputs.
Saved/exposed, unwritten, FX-written, external, unknown, explicitly selected or
remotely synchronized inputs, and inputs with reachable random writers, must
retain their appearance alternatives.
The actual SDK serialized menu inventory covers shared/cyclic pages and null
optional inputs; missing required submenus or unknown data cannot prove absence.
The influencing-bone loop regression compares native scalar sampling with real
export/reimport geometry at the retained prepared pose and explicit authored
endpoints. Probe Transform animation must remain on the native graph, but its
pose must not be copied into the prepared avatar. Bone or vertex overlap alone
does not prove that a Transform curve changes a BlendShape weight.
The unclamped source-range case preserves the explicit arithmetic contract; it
does not claim that Editor BakeMesh is a native oracle for an unclamped player.
Run the native clamped-range and negative-rest geometry cases through the
GPU-backed batch behavior or integration profile above. An interactive EditMode
runner can keep the native skinning policy stale after PlayerSettings readback
changes, even while Editor updates and Camera.Render complete. The shared test
helper queues four updates and verifies the declared true policy on a fresh
100-frame BakeMesh sentinel before the full geometry oracle proceeds. A failed
precondition must not be skipped or replaced with relaxed geometry expectations.
The required one-click
additive geometry regression must pass even if it failed on an earlier development
commit. A known failure is not a release exemption.
Missing ordinary blink can use only a surviving moving Unified Expression route.
Disabled, missing, inert and fully closed routes remain rejected. A negative source
rest normalized by the native clamp policy is covered separately by real reimport
and native geometry at coefficients 0, 0.5 and 1; the source weight stays intact.

Temporal rest is a separate contract from a selectable animated expression.
Inspect complete curves on active effective clips after the normal-context,
dependency and callback checks. Preserve a genuinely varying morph channel's
prepared authored weight, rather than baking a sampled intermediate phase.
Reconstruct independent stationary channels through the same native graph,
including Write Defaults and Additive support. Keep the complete graph and binding
safety checks even when no stationary scalar remains to capture. This policy
does not make a varying curve constant, authorize animated Animator parameters or
unknown callbacks, or add automatic idle playback to the app. Existing selectable
programs retain their authored curves, duration and loop setting.

The synthetic regression matrix must include a delayed loop whose movement begins
beyond the sample window, weighted tangent variation, a nonzero prepared rest and
stationary siblings, partial Override/Additive composition, and source preservation.
History captured before temporal classification must not restore an excluded
temporal channel later. Prepared rest and explicit blink/tracking endpoints must
also survive real export/reimport and optimization. A varying lower writer has a
bounded dominance proof only when a later included Override layer has exact native
weight one, no mask or transition, exactly one current effective clip at weight
one, no next clip and an explicit constant curve for that same binding. Only that
lower layer/clip/binding is exempt from the curve check; timed state, parameter and
callback checks still run. Fractional, near-one, Additive, masked or moving upper
writers do not establish this proof, and finite sample equality alone cannot
provide it. Conservative
prepared-rest preservation must be reported, and cannot excuse a missing required
route, an altered authored endpoint or an inert required blink. A zero residual
that independently matches an explicitly authored endpoint is not itself a failure.

Additional playable inspection distinguishes structurally valid known SDK effects
from unknown or malformed callbacks. A known layer-control command without a
parameter write may be excluded only when typed fixed-input reachability proves
its branch dormant. Every surviving unsupported command remains a diagnostic.
Raw writers and cross-controller parameter type conflicts still prevent invariant
pruning. Test default/entry/timed paths, selected inputs, authored writers, malformed
command data and unknown callbacks as well as the ordinary dormant AFK branch.
Neither temporal rest nor additional playable handling may use an avatar or clip
name as a compatibility exception.

For AAO/NDMF optimization changes, run the integration profile with a pinned actual
AAO installation too:

```text
python3 Tools/run-unity-compatibility.py --profile exporter-integration --unity UNITY_EXECUTABLE --project INTEGRATION_TEST_PROJECT --expect-univrm 0.131.0 --expect-unity 2022.3.22f1 --output TEST_RESULTS
```

It additionally requires `InstalledNdmfNeutralExportTests`,
`InstalledAaoNeutralEndpointExportTests`, `NdmfBlinkPreparationTests` and
`PhysBoneSpringExportTests`. These
exercise the installed processors, moved/replaced renderers, mesh deletion and
merge, endpoint remapping, and the difference between callback-free preview and
export optimization, including spring motion and collisions after real export.
The optional AAO spring fixture checks real package registration before reflection;
an absent package is a skip, while an installed but unloaded component is a failure.
Record the exact AAO version and any explicitly applied
dependency patch; use the fixture's supported configuration API. Missing or
unsupported integration packages cause required skips and therefore a failed run.

The runner runs each selected class and checks named critical regressions and their
parameter counts. A missing class/case, duplicate identity, failed/skipped case,
unexpected class or unsuccessful root result cannot satisfy the gate. Extra tests
added to a selected class must also pass. XML/log names are fresh per invocation;
the JSON report records the profile, counts, expected environment and, when
available, the runner checkout's package version, Git commit and dirty state.
`runnerSource` identifies the checkout that supplies the runner; it does not prove
that a different exporter copy installed in the test project has the same source.
Point the project's local development package at that checkout, or verify the
installed candidate against it, and record the production ZIP hash separately.
A dirty checkout's commit alone cannot identify its uncommitted fixes.

These fixtures create their own meshes, controllers and avatars; they require no
private or purchased avatar. Obtain actual dependency packages from their official
sources and preserve their pins and licenses. Do not replace SDKs/processors with
stubs or relabel unsupported packages to obtain a passing integration report.
`Tools/test-compatibility-tooling.py` uses artificial NUnit XML to test the result
gate itself; its success is not evidence that Unity behavior passed.

Use an available, appropriately licensed Editor for these runs. No cloud Unity CI
or license provisioning is introduced by these profiles. Host CI and the release
workflow run the same AAO patch, LAN and pose checks, but do not launch Unity.
Keep actual Unity XML and exact tested versions/commits in release review evidence;
do not substitute a skipped Unity job with a successful source-only check. Repeat
the relevant profile on the separate 2022.3.62f3 baseline and each supported UniVRM
version when the change affects that matrix. Existing real-avatar/app/device visual
checks still apply; generated fixtures do not prove every avatar's appearance.

Development packages must be distinguishable from an already published stable
version. Assign the next development version before distributing a changed main
checkout, and prepare the final stable version/changelog only after review and
required checks. Preserve published tags; the release workflow refuses to replace
an existing tag. Retest the final version's actual package, and never use an older
version label or XML to claim that a new candidate was verified. Private avatar
assets, local paths, logs and screenshots remain outside public packages.

For startup changes, also validate the release ZIP as embedded packages, as used
by VCC/ALCOM, with the actual VRChat/MA/NDMF packages from a report. The focused
runner includes a menu test that removes the old startup registration to verify
the menu cannot become permanently unavailable. Test the production menu once
without exporter test assemblies or `testables`, so test-only references cannot hide
an assembly-loading problem. For the production probe, copy
`Tests/Startup/ExporterStartupProbe.cs` into the isolated project's `Assets/Editor`
and run Unity with `-batchmode -quit -projectPath TEST_PROJECT -executeMethod
ExporterStartupProbe.Run -logFile STARTUP_LOG`. The probe rejects exporter test assemblies and `testables`, and checks the real
menu, refresh and diagnostic report. UniGLTF itself depends on Test Framework;
that upstream dependency is preserved.
A successful clean startup does not reproduce every
user project's import/reload history; record this limit explicitly.

The file format remains schema 2.0 for full lilToon (legacy 1.x remains readable).
The app's embedded 2.3.4 catalogue and shaders are unchanged. More versions mean
more validation work; exact gates deliberately trade immediate adoption of an
unknown update for predictable exports and actionable errors.
