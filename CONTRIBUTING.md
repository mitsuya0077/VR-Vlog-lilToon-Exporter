# コントリビューション / Contributing

不具合報告・改善提案・文書修正・Pull Requestを受け付けています。Issue・PRは日本語または英語で記載できます。実装を変更する前に、対応範囲と依存パッケージの方針を確認してください。以下に開発環境と検証方法をまとめます。

Bug reports, improvement proposals, documentation fixes and pull requests are welcome. Issues and PRs may be written in Japanese or English.

## Reports and proposals

Search [existing issues](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter/issues) and [pull requests](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter/pulls) before opening a new report.

For bugs, include the exporter, Unity, lilToon and UniVRM versions, relevant optional plugin versions, reproduction steps, expected behavior and actual behavior. For proposals, describe the problem you want to solve and a concrete example. Discuss changes to dependency support or export formats in an issue before implementing them.

For vulnerabilities, use the [private reporting instructions](SECURITY.md) instead of public issues or pull requests.

Ordinary bug reports and proposals are public. Do not attach purchased or private avatars, complete Unity projects, credentials, private links or full logs. Use the exporter's shared diagnostics where available, and redact personal names and machine paths from error excerpts. Use a minimal fixture made from assets you can share.

## Development setup

1. Fork the repository, then clone with LF line endings and create a branch for your change. For a fork, replace the URL below with your fork URL:

   ```sh
   git -c core.autocrlf=false clone https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter.git
   cd VR-Vlog-lilToon-Exporter
   git config core.autocrlf false
   git switch -c your-change
   ```

   The clone option controls the initial checkout; the local setting keeps later checkouts from converting LF to CRLF. If an existing checkout already has CRLF conversion, use a separate fresh clone with these settings and preserve your local work.
2. For Python checks, use Python 3.12, as in [Validate CI](.github/workflows/validate.yml). These checks use the Python standard library.
3. For Unity work, use a separate Unity 2022.3 project with lilToon 2.3.4 and matching supported UniGLTF / VRM packages. Follow the exact versions and prerequisites in [Compatibility/README.md](Compatibility/README.md).
4. Add this checkout as a local package using Unity Package Manager's **Add package from disk**, selecting the root `package.json`. Avoid also installing a release copy of the exporter in that project.
5. For Unity tests, add `"testables": ["com.vrvlog.liltoon-vrm-exporter"]` to the test project's manifest and follow the compatibility guide's Test Framework, Collections and optional integration package requirements.

Contributor Unity projects, local outputs and private fixtures belong outside tracked source. `Editor/` contains export logic and diagnostics; `Runtime/` contains avatar components; `Schema/` defines the custom data formats; `Tests/` and `Tools/` contain validation and packaging tools. See the [repository map](README.md#開発に参加する) for the remaining directories.

## Local checks

Run these from the repository root:

```sh
python3 Tools/validate.py
python3 Tools/check-public-content.py
python3 Tools/test-package.py
python3 Tools/test-release-policy.py
python3 Tools/test-listing.py
python3 Tools/generate-compatibility.py
python3 Tools/test-compatibility-tooling.py
python3 Tools/test-aao-vertex-buffer-patch.py
git diff --check
```

`check-public-content.py` scans tracked files for common disclosure patterns; stage intended new files so they are included. It is not a complete security audit. `generate-compatibility.py` without `--write` checks generated policy drift and does not change source.

Host behavior checks also use PowerShell 7. The [Validate workflow](.github/workflows/validate.yml) is the complete command reference, including dependency, exporter, transfer, pose, bake and NDMF host checks. These host checks do not prove Unity integration or device behavior.

For implementation changes, run the relevant pinned Unity profiles from the [compatibility guide](Compatibility/README.md#exporter-behavior-and-integration-regression-profiles). Record the actual Editor and package versions, tested commit, results and any skips. Preserve the original avatar and its referenced assets. Do not use host adapters, relabeled dependencies or older test results as evidence of a successful Unity run.

For documentation changes, verify relative links and examples and check that the documented behavior matches source. If a change is intended to preserve implementation, confirm that product source, dependency policy, version metadata and package tooling are unchanged.

## Reproducing a package ZIP

ZIPの再現には、同じコミット・追跡ファイルの内容と改行が必要です。上記の手順でLFの作業コピーを作り、生成前に未保存・未コミットの変更がないことを確認してください。改行を自動変換した作業コピーは、同じコミットでも配布ZIPとSHA-256が異なります。

The package builder preserves the bytes of the tracked files in the working tree, including line endings. It fixes ZIP entry order, timestamps, permissions and storage format, but does not normalize those file bytes. Use the LF checkout above, a clean working tree and the exact release tag; then build from the repository root:

```sh
git checkout --detach v0.11.14
git status --short
```

Stop if `git status --short` shows local changes. Once the working tree is clean:

```sh
python3 Tools/build-package.py --output ../exporter-0.11.14.zip
python3 -c "import hashlib; from pathlib import Path; print(hashlib.sha256(Path('../exporter-0.11.14.zip').read_bytes()).hexdigest())"
```

Replace the tag, version and output filename together for another release. Compare the SHA-256 with that version's [VPM listing](https://mitsuya0077.github.io/VR-Vlog-lilToon-Exporter/index.json). The comparison is valid for the exact tagged source with unchanged checkout bytes; a different commit or edited documentation can change the ZIP. The release workflow's `expected_package_sha256` check uses this same byte equality.

## Pull requests

- Explain the concrete problem and resulting behavior, and link the related issue if one exists.
- Keep changes focused. Update affected guides and third-party notices when relevant.
- Describe what you actually verified, including commands, results and remaining limitations.
- Preserve dependency version gates, published tags and release channel policy. Do not combine unrelated version bumps or release operations with a documentation PR.

The current `main` ruleset requires a pull request and successful `schema` and `listing-validation` checks against an up-to-date branch. Address review findings before integration. Passing CI does not replace required Unity or visual checks for changes that need them.

## Licenses and attribution

Contributions to exporter code use the repository's [MIT License](LICENSE). Preserve existing copyright and license notices. Identify the source and license of third-party material you add; the existing [third-party notices](ThirdPartyNotices/) and [dependency patch notice](Documentation~/DependencyPatches/README.md) show the current attributions. Avatar assets and images retain their owners' terms.
