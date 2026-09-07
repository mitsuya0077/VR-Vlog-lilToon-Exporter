# VR Vlog lilToon VRM Exporter

**アバターを、いつもの表情で。**

lilToonのアバターを、VR Vlogで使える **VRM 1.0** に書き出すUnity Editor用パッケージです。
対応する見た目の設定、調整した顔、VRChatの表情をひとつのファイルにまとめます。

[紹介ページ・インストール](https://mitsuya0077.github.io/VR-Vlog-lilToon-Exporter/) · [最新リリース](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter/releases/latest) · [詳しい仕様 / English](Docs/TechnicalDetails.md) · [不具合を報告](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter/issues)

## できること

- **lilToonの設定を引き継ぐ** — 色、影、発光、MatCap、輪郭線などの対応設定を保存します。追加データに未対応のVRMビューアー向けに、MToonの互換表示も含めます。
- **調整した顔を基本の形にする** — Unityで設定したBlendShape値を保ち、対応するまばたき・口のプリセットを補います。
- **登録済みの表情を自動で取り込む** — 対応するVRChatメニュー・ジェスチャーの表情を保存します。動くジェスチャー表情の再生には、対応版のVR Vlogが必要です。
- **元のアバターを変更しない** — 一時コピーから書き出し、元のメッシュ・材質・画像・インポート設定を変更しません。

すべてのシェーダー効果や表情を再現するものではありません。[対応範囲と制限](#対応範囲と制限)を確認してください。

## インストール

**VCC（VRChat Creator Companion）またはALCOMからの導入を推奨します。**

1. [紹介ページ](https://mitsuya0077.github.io/VR-Vlog-lilToon-Exporter/)の「VCC / ALCOMに追加する」を押します。PCで関連付けられたVCCまたはALCOMが開きます。開かない場合は、下のURLを使いたいアプリのリポジトリ追加画面に貼り付けてください。
2. 使いたいUnityプロジェクトのパッケージ管理画面を開きます。
3. **VR Vlog lilToon VRM Exporter** を選んでインストールします。

```text
https://mitsuya0077.github.io/VR-Vlog-lilToon-Exporter/index.json
```

必要な **UniVRM 0.131.x**（`com.vrmc.gltf` / `com.vrmc.vrm`）は依存関係として自動導入されます。
確認画面に表示されることがありますが、別々に探して選ぶ必要はありません。

| 項目 | 対応環境 |
| --- | --- |
| Unity | 2022.3以降。VRChatプロジェクトではVCCが案内する対応版を使用 |
| 材質 | lilToon / lilToon Lite / lilToon Multi |
| UniVRM | 0.131.x（VCC・ALCOMで自動導入） |
| 出力形式 | VRM 1.0（MToon互換表示 + VR Vlog用の追加データ） |

ZIPで手動導入する場合は、[最新リリース](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter/releases/latest)のエクスポーターと対応するUniVRMパッケージが必要です。Unityプロジェクトには、元のアバターとlilToonをあらかじめ導入してください。

## 使い方

1. **使いたい衣装を表示する。** Unityでアバターを開き、書き出す衣装とRendererを有効にします。アバターの最上位と親も有効にしてください。非表示の衣装は含まれません。
2. **書き出し画面を開く。** Unityのメニューで **VR Vlog → lilToon VRM 1.0を書き出す** を選びます。
3. **アバターと作者名を指定する。** 「① アバター（必須）」にHierarchyのアバター最上位を指定し、「② 作者名（必須）」にVRMへ記録する作者名を入力します。VRM名にはオブジェクト名を使います。
4. **保存する。** 「③ 保存先を選んでVRMを書き出す」を押し、保存先を指定します。
5. **VR Vlogで確認する。** できた `.vrm` ファイルをiPhoneへ送り、アプリで読み込みます。見た目、まばたき、口、表情を確認してください。

表情の取り込みは書き出すたびに自動で行われます。個々のBlendShapeではなく、対応するメニュー項目やジェスチャーのアニメーションをひとつの表情として保存します。

### 書き出し後の確認

- **省略された効果・表情**：Unity Consoleの「VR Vlog 書き出し詳細」で理由を確認できます。
- **目などの白飛び**：「目などの白飛びを抑える」は初期状態でONです。ベース画像と発光画像に同じテクスチャが使われる場合に発光を省きます。意図した発光を残す場合はOFFにできます。
- **古いVRMを使っている**：新しい変換内容を反映するには、Unityの元アバターから再度書き出してください。

## 対応範囲と制限

| 対象 | 動作・制限 |
| --- | --- |
| 見た目 | 対応する設定を保存し、MToonへ変換します。描画方式や端末による差が残ります |
| 特殊効果 | ファー、屈折、宝石、テッセレーション、AudioLinkなどは近似・省略します |
| 衣装 | 現在有効なオブジェクト・Rendererが対象です。非表示の衣装は含めません |
| メニュー表情 | 登録済みのButton・Toggleと対応する固定BlendShape表情が対象です |
| ジェスチャー表情 | FX Controllerに登録された対応クリップが対象です。動く表情は対応版のVR Vlogで再生します |
| 未対応の表情 | 材質差し替え・表示切り替え・ボーン変形を伴う項目、Puppet、ビルド時生成のメニューなどは対象外です。詳細は技術資料を参照してください |
| 他のVRMビューアー | MToonの互換表示を使用します。動く表情は静止した先頭の顔になります |

追加データはVRMを読むための必須拡張にしません。構造不正や安全上限の超過などはエラーにし、途中までのVRMを保存しません。アバター・衣装・テクスチャの利用条件を確認してから書き出してください。

## 詳しい情報・開発

- [変換の仕組み・対応条件・開発チェック（English / 日本語）](Docs/TechnicalDetails.md)
- [動く表情の保存仕様](Schema/ExpressionAnimations.md)
- [マテリアル拡張のJSON Schema](Schema/VRVLOG_materials_liltoon.schema.json)
- [変更履歴](CHANGELOG.md)

リポジトリのルートで `python Tools/validate.py` と `pwsh -File Tools/run-behavior-tests.ps1` を実行できます。
Unity Editor内の検証は `Tests/Editor` にあり、依存パッケージを導入したUnityプロジェクトが必要です。

不具合は[Issues](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter/issues)へ、エクスポーター・Unity・UniVRMのバージョン、再現手順、エラー内容を添えて報告してください。有料アバターや個人情報を含むファイルは公開せず、必要な範囲の情報を共有してください。

## ライセンス

エクスポーターは [MIT License](LICENSE) です。配布しているUniVRMパッケージの出典とライセンスは [Third-party notices](ThirdPartyNotices/UniVRM.md) を参照してください。アバターなどの素材には、それぞれの利用条件が適用されます。

## English

A Unity Editor package that exports a VRM 1.0 file with a standard MToon fallback and optional VR Vlog lilToon data. Supported avatar shape customizations and VRChat expressions are preserved without modifying the source assets.

Add the repository URL above in VCC or ALCOM, then install **VR Vlog lilToon VRM Exporter**. Its tested UniVRM dependencies are installed automatically. See the [technical documentation](Docs/TechnicalDetails.md) for the full workflow, compatibility limits, and development checks.
