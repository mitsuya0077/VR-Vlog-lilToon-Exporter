# VR Vlog lilToon VRM Exporter

lilToonのアバターを、iPhoneのVR Vlogで使うVRM 1.0に書き出すUnity用パッケージです。

## 必要な環境

Unity 2022.3、lilToon **2.3.4**、UniVRM **0.131.0／0.131.1／0.131.2**、VR Vlog **0.1.1（ビルド345）以降**が必要です。VCC・ALCOMではUniVRM 0.131.xを自動で導入し、対応版か確認します。Modular Avatarを使う場合は、その依存パッケージも事前に導入してください。

## 使い方

1. Unityでアバターと書き出す衣装を表示します。
2. **VR Vlog → lilToon VRM 1.0を書き出す** を開き、アバターと作者名を指定して保存します。
3. VRMをiPhoneに送り、VR Vlogで **lilToon専用表示** をオンにします。
4. **モデルを変更 → 端末のVRMを選ぶ** から読み込みます。

登録済みポーズは自動で収集され、**ポーズを確認・調整** から確認できます。瞬きのプレビュー・手動設定や、不要なペット・ギミックの除外は **書き出し設定** を開いて調整します。パッケージのバージョンは画面下の **動作環境** を開くと確認できます。

0.11.0以降では、APL登録やVRChat/Modular Avatarメニューから静止姿勢を確定できるポーズを自動で同梱します。**ポーズを確認・調整** で除外・名前変更・プレビューや、未登録.animの手動追加と採用時刻の指定ができます。利用には静止ポーズ対応版のVR Vlogが必要です。元のUnityアバターから再出力し、アプリの **ポーズ** から選択してください。モーション再生、複雑な合成、外部入力や履歴に依存する姿勢は対象外です。[対応範囲の詳細](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter/blob/v0.11.0/Documentation~/HumanoidPoses.md)を確認してください。

0.10.6以降では、導入済みVRChat SDKのPhysBoneから髪・衣装の揺れ設定を引き継ぎます。力・減衰・重力は近似で、件数や未対応項目は書き出し結果の **詳細を見る** で確認できます。SDKは必須ではありません。揺れ設定がない既存VRMは、元のUnityモデルから書き出し直してください。

表情メニューは、各項目を既定状態から選んだ結果を同じ名前で登録します。Parameter DriverのSet・Add・Copyに対応しますが、操作履歴・Random・外部入力に依存する結果は移植しません。元モデルと前処理後のモデルの両方にないBlendShape参照は、その参照だけ省略して名前を報告します。

パッケージが不足・未対応の場合は **VR Vlog → 動作環境を確認** で現在の版と対処方法を確認できます。パッケージを自動で変更することはありません。

対応版が入っているのに書き出し画面が開かない場合は、同画面の **再読み込み** を押し、Unityのコンパイル完了を待ってください。直らない場合は **診断情報をコピー** の内容を不具合報告に添えてください。

元のアバターは変更しません。更新内容を反映するには元アバターから再書き出ししてください。すべてのシェーダー効果やVRChatの動作を再現するものではありません。

[導入ガイド・対応範囲](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter#readme) · [変更履歴](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter/blob/main/CHANGELOG.md) · [不具合報告](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter/issues/new/choose)

## ライセンス

[MIT License](../LICENSE) · [lilToon](../ThirdPartyNotices/lilToon.md) · [UniVRM](../ThirdPartyNotices/UniVRM.md)

アバター・衣装・テクスチャには各権利者の利用条件が適用されます。
