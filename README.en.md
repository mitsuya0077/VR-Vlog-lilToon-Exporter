> **0.11.15-beta.2** includes expression and pose file selection, previews, and QR transfer to a compatible iPhone version of VR Vlog. Enable **Show pre-release packages** in ALCOM to select it. QR upload requires privacy consent; your choice is remembered in Unity Editor, and uploads start only when you press the button. This beta also fixes exports failing with `The scene is invalid.` when NDMF reports notifications. Stable **0.11.14** remains available.

# VR Vlog lilToon VRM Exporter

[日本語](README.md) · [English](README.en.md)

[![MIT License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
[![Validate](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter/actions/workflows/validate.yml/badge.svg?branch=main)](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter/actions/workflows/validate.yml)

Export your lilToon avatar to **VRM 1.0** for VR Vlog on iPhone. This Unity package saves a standard MToon fallback and a validated lilToon extension in the same file, using temporary copies to preserve the source avatar.

[Install](https://mitsuya0077.github.io/VR-Vlog-lilToon-Exporter/) · [Latest stable release](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter/releases/latest) · [Changelog](CHANGELOG.md) · [Contributing](CONTRIBUTING.md)

## Features

- Export lilToon material settings and textures for VR Vlog's dedicated renderer, with a standard MToon fallback for other VRM viewers.
- Preserve supported expressions, blinking, tracking morphs and appearance prepared by Modular Avatar / NDMF.
- Convert supported PhysBones to VRM SpringBones, with diagnostics for approximations and unsupported settings.
- Inspect export failures and try supported recovery options on a fresh copy of the avatar.

The exporter does not reproduce every shader effect, Animator behavior or VRChat gimmick. See the [detailed scope and limitations in Japanese](README.md#対応範囲と制限).

## Requirements

| Component | Supported version |
| --- | --- |
| Unity | 2022.3; use the version recommended by VCC / ALCOM for your VRChat project |
| lilToon | 2.3.4; install it with your avatar before exporting |
| UniVRM / UniGLTF | Matching 0.131.0, 0.131.1 or 0.131.2 packages |
| VR Vlog | 0.1.1 (build 345) or later for basic lilToon import; newer features require a compatible app version |

Install Modular Avatar and its dependencies beforehand if your avatar uses them. The [dependency policy](Compatibility/dependencies.json) records supported versions; the exporter reports missing or unsupported packages without automatically changing them.

## Installation

1. Open the [installation page](https://mitsuya0077.github.io/VR-Vlog-lilToon-Exporter/) and select **VCC / ALCOMに追加** (add to VCC / ALCOM).
2. Open your Unity project's package manager in VCC / ALCOM.
3. Install **VR Vlog lilToon VRM Exporter 0.11.14** from the stable versions. VPM resolves the UniVRM dependencies.

If the button does not open your package manager, add this repository URL manually:

```text
https://mitsuya0077.github.io/VR-Vlog-lilToon-Exporter/index.json
```

For manual installation, use **ZIPで導入** on the installation page. Download the exporter, UniGLTF and VRM ZIPs and extract each into a separate folder under the project's `Packages` directory, with its `package.json` directly inside that folder. Avoid installing the same UniVRM package twice.

Back up your Unity project before updating. Re-export from the original avatar to apply exporter changes to an existing VRM.

Stable **0.11.14** fixes exports stopping during neutral expression sampling when FX and another Playable Layer, such as Locomotion, use the same blend shape. Affected expression groups retain their current settings on the export copy, with the targets and reasons reported as warnings. Independent neutral expressions are still evaluated; the original avatar and controllers are unchanged.

The export details and failure windows offer **Copy full text** and **Save text**. Both include every diagnostic and the complete retained exception, including inner exceptions and stack traces. Text files use UTF-8. Full text includes avatar names and paths; the separate **Copy shareable diagnostics** action keeps its redacted report.

The stable package exports VRM files without QR transfer code or transfer guides. QR transfer is available in a separate prerelease and requires a compatible receiving app. Check the [Japanese release guidance](README.md#インストール) and [cloud transfer guide](Documentation~/CloudTransfer.md) for the supported app and platform before using it.

## Quick start

The steps below describe the UI adopted in 0.11.15-beta.2. Previously published 0.11.14 and 0.11.15-beta.1 ZIPs are unchanged. The UI follows the editor locale; the labels below match the Japanese guide.

1. Open your avatar in Unity. Enable the avatar, its parents and the outfit you want to export.
2. Select **VR Vlog → VRMを書き出す**.
3. Assign the avatar's root object from the Hierarchy and select **書き出しを準備**.
4. Select and add expression files, or drop multiple .anim files from Project. Click a name or **確認** to preview. Expressions are optional; model-only export is supported.
5. Add optional body poses, set the author and original license terms under **ライセンス設定**, then select **VRMを保存**.

See the [VRM export guide](Documentation~/ExperimentalExpressionCapture.md) for file support and capture ranges. In beta.2, select **QRコードでスマホに送る** in the result section, read the linked privacy policy, check consent, then press **アップロードしてQRを表示**. Scan the QR in a compatible iPhone version of VR Vlog using **モデルを変更 → PCから受け取る**. See the [cloud transfer guide](Documentation~/CloudTransfer.md) for requirements and transfer limits. Consent persists between sessions; opening the transfer window never uploads automatically. Android receiving remains disabled.

Textures retain their aspect ratio and are reduced to fit within 1024 × 1024. Smaller textures are not enlarged. The original avatar and textures remain unchanged.

To import on iPhone, save the `.vrm` in Files, enable **lilToon専用表示** in VR Vlog settings, then choose **モデルを変更 → 端末のVRMを選ぶ**. Reload the model after changing the rendering setting.

## Documentation

Most detailed user guides are currently in Japanese. The compatibility guide and schema files provide additional development references.

| Topic | Reference |
| --- | --- |
| Full user guide and troubleshooting | [Japanese README](README.md) |
| Avatar appearance and SpringBones | [Avatar preservation](Compatibility/AvatarPreservation.md), [PhysBone conversion](Compatibility/PhysBone.md) |
| Body and finger poses | [Humanoid poses](Documentation~/HumanoidPoses.md), [animations](Documentation~/HumanoidAnimations.md) |
| Dependency maintenance and Unity testing | [Compatibility guide](Compatibility/README.md) |
| VRM extension formats | [Schema directory](Schema/) |
| Source layout and local checks | [Contributing guide](CONTRIBUTING.md) |

## Help and contributions

Report vulnerabilities using the [private reporting instructions](SECURITY.md). Keep vulnerability reproduction details and exploit conditions out of public issues and pull requests.

Use **VR Vlog → VRMを書き出す** to inspect installed package versions. Export details explain unsupported items and possible next steps.

[Report a bug or propose an improvement](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter/issues/new/choose) with the relevant versions, reproduction steps and a short sanitized error excerpt. Please keep private avatar assets, credentials and full logs out of public reports. Documentation fixes and pull requests are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md).

## License

Exporter code is available under the [MIT License](LICENSE). Third-party notices cover [lilToon-derived shader code](ThirdPartyNotices/lilToon.md), [UniVRM / UniGLTF](ThirdPartyNotices/UniVRM.md), [Bouncy Castle / ZXing.Net for transfer](ThirdPartyNotices/LanTransfer.md), and the [explicit Avatar Optimizer correction patch](Documentation~/DependencyPatches/README.md) with its [license](Documentation~/DependencyPatches/AAO-LICENSE.txt). Transfer dependencies are excluded from stable ZIPs.

Avatar assets and published images retain their owners' terms. See the [image sources and credits](Website/assets/README.md).
