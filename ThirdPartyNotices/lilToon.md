# lilToon

- Upstream: https://github.com/lilxyzw/lilToon
- Referenced version: [2.3.4](https://github.com/lilxyzw/lilToon/tree/2.3.4)
- Copyright (c) 2020-2024 lilxyzw
- License: MIT — [full license text](lilToon-LICENSE.txt)

The exporter requires a separately installed lilToon package. It does not
redistribute the complete lilToon package.

Starting with exporter 0.9.0, `Editor/AlphaMaskBaker.shader` adapts the
lilToon 2.3.4 alpha-mask equations for export-time texture baking. The
exporter package includes the upstream copyright and MIT permission notice
in `ThirdPartyNotices/lilToon-LICENSE.txt`.

`Editor/LayerAlphaBaker.shader` adapts the main-color pass of
[`ltspass_baker.shader`](https://github.com/lilxyzw/lilToon/blob/2.3.4/Assets/lilToon/Shader/ltspass_baker.shader)
and the layer-alpha/RGB ordering in `lilGetMain2nd` / `lilGetMain3rd` from
[`lil_common_frag.hlsl`](https://github.com/lilxyzw/lilToon/blob/2.3.4/Assets/lilToon/Shader/Includes/lil_common_frag.hlsl).
It uses the separately installed 2.3.4 package's tone, decal, MSDF and blending
helpers. This adaptation is also covered by the bundled upstream MIT notice.

The license text is copied from the [upstream 2.3.4 license](https://github.com/lilxyzw/lilToon/blob/2.3.4/LICENSE).
