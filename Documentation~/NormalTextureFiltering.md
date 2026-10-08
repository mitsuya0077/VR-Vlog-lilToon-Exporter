# 通常の法線画像と iPhone の読み込み互換性

Exporter 0.11.11 (`f5aed3fc`) は通常の 8-bit 法線画像も、デコード後に
`rgbaFloat`（成分ごとに 32-bit float）へ保存していた。未設定の法線スロットで
使う Unity の既定法線画像も対象になる。元の Bilinear / Trilinear を保持する
ため、Float32 画像のフィルタリングに対応しない GPU ではアプリの検査が拒否する。

iPhone 13 Pro は A15 Bionic を使用する。Apple の Metal 表では A15 は
Apple8 に属し、RGBA32Float の通常の対応には Filter が含まれない。
Apple7 / Apple8 の一部の iPadOS / macOS GPU にある追加対応とは区別する。
RGBA16Float は Filter に対応する。
出典: [iPhone 13 Pro の仕様](https://support.apple.com/en-us/111871)、
[Metal Feature Set Tables](https://developer.apple.com/metal/Metal-Feature-Set-Tables.pdf)
（2026-05-21、2・14・15 ページ）。

通常の 8-bit / 圧縮法線画像と既定法線画像を `rgbaHalf`（成分ごとに
16-bit float）へ保存する。各 mip、linear 色空間、filter、wrap、aniso と
mipBias、元の Unity アセットと importer 設定を保持する。32-bit float、
16-bit normalized、11-bit EAC など、高精度の法線画像は既存の Float 出力を
保持する。元が Half の法線画像は Half のまま保存する。

これは Half の丸めを伴い、ビット単位の可逆変換ではない。
合成画像の実 GPU コピーでは、Float 出力との差は成分ごとに最大
0.00048447。表面に対して浅い方向の法線を含むストレス画素では、アプリと同じ
RG からの方向再構成による差は最大 1.15639 度だった。任意の強度設定や
すべての実アバターで同じ誤差を保証するものではない。

既存の VRM には元画像の精度情報がないため、アプリで Float を一律に Half へ
変換しない。0.11.11 で書き出した該当 VRM は修正版で再書き出しする。
Half は既存の拡張形式であり、TestFlight 445 の loader が受け付ける。
高精度の Float 画像を意図的に使う場合の GPU 条件は引き続き必要になる。

## 検証の範囲

- Unity 2022.3.62f3 / Metal の実 Editor で、通常・圧縮・未設定の法線画像、
  全 mip と sampler、高精度の形式選択、元アセットの不変を検証した。
- 実 PNG を TextureImporter で法線画像として読み込み、実際の VRM を書き出した。
  通常法線と既定法線の出力が Float から Half へ変わり、元ファイル、meta、
  importer 設定と sampler は一致した。
- その合成 VRM を Unity 6000.3.25f1 / Metal の実アプリ loader で読み込み、
  Full lilToon の素材と Half 法線画像、5 段の mip と sampler、描画成功を確認。
  この Mac は Float32 の補間にも対応するので、旧 VRM の拒否の再現には使わない。
- schema / package / publication policy / listing / compatibility / AAO patch の
  ホスト検証は成功した。

テスターの私有 VRM と端末側の例外ログは未取得。書き出しの注意ログと端末条件に
合う不具合だが、そのアバターの原因確定や iPhone での受入成功を示す結果ではない。
正式リリースと TestFlight 配布はこの修正の検証に含まれない。
