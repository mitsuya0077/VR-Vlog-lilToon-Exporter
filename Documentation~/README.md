# VR Vlog lilToon VRM Exporter

lilToonのアバターをVRM 1.0へ書き出すUnity Editor用パッケージです。

- Unity 2022.3、lilToon **2.3.4**、UniVRM **0.131.x**が必要です。
- VCC・ALCOMでは必要なUniVRMを依存パッケージとして導入します。
- Unityの **VR Vlog → lilToon VRM 1.0を書き出す** から、アバターと作者名を指定して保存してください。
- 対応する見た目・表情・接続設定を出力用コピーから変換します。すべてのシェーダー効果やVRChatの動作を再現するものではありません。
- 0.10.0はschema 2.0の専用データを生成します。表示には対応版のVR Vlogが必要です。対応アプリは先行してTestFlightへ配布するため、アプリの更新内容を確認してください。全機能のiPhone実機比較はまだ完了していません。
- 専用データには焼き込み前の材質と原寸画像・ミップを保存します。標準MToon用の画像には従来の縮小を適用します。
- 対応アプリで「lilToon専用表示」をオンにしてからモデルを読み込みます。専用表示で資源不足やデータ不正があれば理由付きで読み込みを停止します。以前のVRMへ新しい情報を反映するには元アバターから再書き出ししてください。
- 「補助ギミックを自動除外」は初期状態でオンです。検出一覧の「この対象は含める」で個別に保持できます。不明な対象や通常材質との混在は自動除外しません。
- 自動除外はワンクリック書き出しに適用します。元アバターは変更せず、以前のVRMには再出力が必要です。
- VR Vlogの対応版と表示設定を確認し、読み込んだモデルの見た目・表情・髪と衣装の追従を確認してください。

[導入ガイド・対応範囲](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter#readme) · [変更履歴](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter/blob/main/CHANGELOG.md)

不具合報告は公開されます。コピーしたエラーから個人情報・ローカルパス・共有リンク・認証情報を除き、必要な箇所だけを共有してください。私有アバター・有料素材・元プロジェクト・ログ全文は公開で添付しないでください。

エクスポーターはMIT Licenseです。アルファマスク処理に利用するlilToon 2.3.4の著作権表示とMITライセンス全文は [lilToonのライセンス](../ThirdPartyNotices/lilToon-LICENSE.txt) に同梱しています。[lilToonの出典](../ThirdPartyNotices/lilToon.md) · [UniVRMの出典とライセンス](../ThirdPartyNotices/UniVRM.md) も参照してください。アバター・衣装・テクスチャにはそれぞれの利用条件が適用されます。
