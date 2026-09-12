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
   python3 Tools/run-unity-compatibility.py --unity UNITY_EXECUTABLE --project TEST_PROJECT --expect-univrm 0.131.1 --output TEST_RESULTS
   ```

   The default expected editor is the app release editor, 2022.3.62f3. An explicit
   `--expect-unity` can record another editor for development checks. The runner
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

The file format remains schema 2.0 for full lilToon (legacy 1.x remains readable).
The app's embedded 2.3.4 catalogue and shaders are unchanged. More versions mean
more validation work; exact gates deliberately trade immediate adoption of an
unknown update for predictable exports and actionable errors.
