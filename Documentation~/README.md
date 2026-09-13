# VR Vlog lilToon VRM Exporter

lilToonのアバターを、iPhoneのVR Vlogで使うVRM 1.0に書き出すUnity用パッケージです。

## 必要な環境

Unity 2022.3、lilToon **2.3.4**、UniVRM **0.131.0／0.131.1／0.131.2**、VR Vlog **0.1.1（ビルド345）以降**が必要です。VCC・ALCOMではUniVRM 0.131.xを自動で導入し、対応版か確認します。Modular Avatarを使う場合は、その依存パッケージも事前に導入してください。

## 使い方

1. Unityでアバターと書き出す衣装を表示します。
2. **VR Vlog → lilToon VRM 1.0を書き出す** を開き、アバターと作者名を指定して保存します。
3. VRMをiPhoneに送り、VR Vlogで **lilToon専用表示** をオンにします。
4. **モデルを変更 → 端末のVRMを選ぶ** から読み込みます。

瞬きは自動設定され、必要に応じて **確認・調整** で変更できます。不要なペットやギミックは **書き出し設定** で除外できます。

パッケージが不足・未対応の場合は **VR Vlog → 動作環境を確認** で現在の版と対処方法を確認できます。パッケージを自動で変更することはありません。

対応版が入っているのに書き出し画面が開かない場合は、同画面の **再読み込み** を押し、Unityのコンパイル完了を待ってください。直らない場合は **診断情報をコピー** の内容を不具合報告に添えてください。

元のアバターは変更しません。更新内容を反映するには元アバターから再書き出ししてください。すべてのシェーダー効果やVRChatの動作を再現するものではありません。

[導入ガイド・対応範囲](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter#readme) · [変更履歴](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter/blob/main/CHANGELOG.md) · [不具合報告](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter/issues/new/choose)

## ライセンス

[MIT License](../LICENSE) · [lilToon](../ThirdPartyNotices/lilToon.md) · [UniVRM](../ThirdPartyNotices/UniVRM.md)

アバター・衣装・テクスチャには各権利者の利用条件が適用されます。
