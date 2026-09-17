# VR Vlog lilToon VRM Exporter

**お気に入りのアバターをVR Vlogで使う。**

lilToonのアバターを、iPhoneのVR Vlogで使う **VRM 1.0** に書き出すUnity用パッケージです。

[インストールページ](https://mitsuya0077.github.io/VR-Vlog-lilToon-Exporter/) · [最新リリース](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter/releases/latest) · [変更履歴](CHANGELOG.md)

## 必要な環境

| 項目 | 条件 |
| --- | --- |
| Unity | 2022.3。VRChatプロジェクトではVCC・ALCOMが案内する対応版 |
| lilToon | **2.3.4**。アバターと一緒に事前に導入 |
| UniVRM | **0.131.0／0.131.1／0.131.2**。VCC・ALCOMでは0.131.xを自動導入し、対応版か確認 |
| VR Vlog | **0.1.1（ビルド345）以降** |

Modular Avatarを使うアバターは、Modular Avatarとその依存パッケージも事前に導入してください。

## インストール

1. [インストールページ](https://mitsuya0077.github.io/VR-Vlog-lilToon-Exporter/)で **VCC / ALCOMに追加** を押します。
2. VCC・ALCOMで使いたいUnityプロジェクトのパッケージ管理画面を開きます。
3. **VR Vlog lilToon VRM Exporter** をインストールします。

ボタンで開かない場合は、リポジトリ追加画面に次のURLを貼り付けてください。

```text
https://mitsuya0077.github.io/VR-Vlog-lilToon-Exporter/index.json
```

更新するときはUnityプロジェクトをバックアップし、パッケージ一覧を更新して最新版を適用します。変更を反映するには、元のアバターからVRMを書き出し直してください。

### ZIPで手動導入する

1. [インストールページ](https://mitsuya0077.github.io/VR-Vlog-lilToon-Exporter/)の **ZIPで導入** から、エクスポーター・UniGLTF・VRMの3つのZIPをダウンロードします。
2. プロジェクトの `Packages` 内の別々のフォルダーへ展開し、それぞれの直下に `package.json` がある状態にします。
3. Unityで読み込みが終わるのを待ちます。同じUniVRMパッケージを重複して導入しないでください。

## 使い方

1. Unityでアバターを開き、書き出す衣装を表示します。アバターと親オブジェクトも有効にしてください。
2. **VR Vlog → lilToon VRM 1.0を書き出す** を開きます。
3. **アバター** にHierarchyのアバター最上位を指定し、**作者名** を入力します。
4. **保存先を選んでVRMを書き出す** を押します。

瞬きは自動設定されます。必要な場合は **確認・調整** でプレビュー・手動設定ができます。不要なペットやギミックは **書き出し設定** で除外できます。元のアバターやマテリアルは変更しません。

対応するAPL登録・VRChat/MAメニューのHumanoid静止ポーズも自動で同梱します。**ポーズを確認・調整** では取得元・名前・カテゴリー・未対応理由を確認し、プレビュー、除外、名称変更、複数.animの手動追加と採用時刻の指定ができます。動くモーションの再生は行いません。[対応範囲・データ仕様](Documentation~/HumanoidPoses.md)

VRChatメニューの自動収集には、有効な終端状態で体・手足・指のTracking ControlがすべてAnimationに指定されている必要があります。Tracking・NoChange・指定なしは、別レイヤーや以前の状態の設定も含めて外部追跡/履歴に依存するため理由付きで除外します。首・顔・目の追跡設定はポーズ対象外です。APL直接登録と手動追加は独立して利用できます。
メニュー条件はExpression Parametersと実際のAnimatorパラメーター宣言の両方と照合します。未定義・重複・型/値の不整合・型に合わない条件・Triggerへの依存は未対応理由として表示します。
Write Defaultsが無効な状態は、以前の状態の有効な体カーブを選択先がすべて上書きする場合に対応します。部位マスクとクリップ差し替えを反映し、未上書きの値が残る履歴依存は除外します。

初版では、標準のBase・Gesture等が有効で移動・ジェスチャーからの姿勢寄与を確定できないメニューは、MAの有無にかかわらず理由付きで対象外にします。APL登録の直接収集と、手動追加したクリップの時刻指定は独立して利用できます。

### iPhoneで読み込む

1. 書き出した `.vrm` をiPhoneの「ファイル」に保存します。
2. VR Vlogの設定で **lilToon専用表示** をオンにします。
3. **モデルを変更 → 端末のVRMを選ぶ** からファイルを選びます。表示設定を変更した場合は読み込み直してください。

## 対応範囲と制限

- lilToonの見た目、対応する顔・体形の調整、VRChat・FaceEmoの表情、Modular Avatarで設定した髪・衣装を引き継ぎます。
- 導入済みVRChat SDKのPhysBoneを、髪・衣装のVRM SpringBoneへ変換します。力・減衰・重力は近似です。書き出し結果に件数と未対応項目を表示します。[揺れ物変換の詳細](Compatibility/PhysBone.md)
- すべてのシェーダー効果・表情・VRChatの動作を再現するものではありません。外部連携や独自改造シェーダー、任意のギミック全般の変換は対象外です。
- 髪や衣装には、元のUnityプロジェクトで接続設定が必要です。非表示の衣装は出力されません。
- 他のVRMビューアーでは標準MToonによる互換表示になり、見た目や動く表情が異なる場合があります。
- モデルによっては端末のメモリ不足で読み込めない場合があります。

## 困ったとき

**VR Vlog → 動作環境を確認** で、必要なパッケージと現在のバージョンを確認できます。
未対応版の場合は書き出しを停止し、対処方法を表示します。パッケージを自動で変更することはありません。
今後の更新への対応手順は [互換性の管理](Compatibility/README.md) を参照してください。

書き出し結果の **詳細を見る** で対象と対処方法を確認してください。解決しない場合は、バージョン・再現手順・該当エラーを添えて [Issues](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter/issues/new/choose) へ報告できます。

VR Vlogアプリ内の問い合わせからもご連絡いただけます。

## ライセンス

[MIT License](LICENSE) · [lilToon](ThirdPartyNotices/lilToon.md) · [UniVRM](ThirdPartyNotices/UniVRM.md)

アバターなどの素材・掲載画像には各権利者の利用条件が適用されます。[画像の出典・クレジット](Website/assets/README.md)
