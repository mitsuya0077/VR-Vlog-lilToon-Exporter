# VR Vlog lilToon VRM Exporter

**お気に入りのアバターをVR Vlogで使う。**

lilToonのアバターを、VR Vlogで使う **VRM 1.0** に書き出すUnity Editor用パッケージです。対応するマテリアル設定、BlendShapeで調整した形状、登録済みの表情を保存します。

[インストールページ](https://mitsuya0077.github.io/VR-Vlog-lilToon-Exporter/) · [最新リリース](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter/releases/latest) · [Technical details / English](Docs/TechnicalDetails.md) · [変更履歴](CHANGELOG.md)

mainは次期0.9.0（schema 1.3）のソースです。対応アプリの配布確認までパッケージは未公開です。配布済みの最新版は[0.8.1](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter/releases/tag/v0.8.1)です。[インストールページ](https://mitsuya0077.github.io/VR-Vlog-lilToon-Exporter/)とVPM一覧では、配布済みのバージョンを案内しています。

以下はmainの仕様を含む説明です。0.9.0で追加したMAの現在のプレビュー・体形・材質・Mesh Cutterの固定、アルファマスクなどは、配布済み0.8.1には含まれません。版ごとの違いは[変更履歴](CHANGELOG.md)で確認してください。

## 必要な環境

| 項目 | 条件 |
| --- | --- |
| Unity | 2022.3。VRChatプロジェクトではVCC・ALCOMが案内する対応版を使用 |
| lilToon | **2.3.4必須**。元のアバターと一緒に事前に導入 |
| UniVRM | 0.131.x。VCC・ALCOMでは動作確認済みの0.131.0を依存パッケージとして導入 |
| 対応マテリアル | lilToon / lilToon Lite / lilToon Multiの対応設定 |
| 着せ替え | MAとその依存パッケージを事前に導入。0.9.0の現在のMAプレビュー反映はMA 1.18.7 / NDMF 1.14.8で検証。一般的な接続処理はNDMF 1.8.3以降の1.xが対象 |
| 出力 | VRM 1.0。標準MToonの互換表示と、VR Vlog用の追加データを保存 |

Unity 2022.3.22f1で検証しています。別のUnity系列・未検証のパッケージの組み合わせでの動作は保証していません。

## インストール

1. [インストールページ](https://mitsuya0077.github.io/VR-Vlog-lilToon-Exporter/)の「VCC / ALCOMに追加」を押します。
2. VCC・ALCOMで使いたいUnityプロジェクトのパッケージ管理画面を開きます。
3. **VR Vlog lilToon VRM Exporter** をインストールします。必要なUniVRMも依存関係として導入されます。

ボタンで開かない場合は、使いたいアプリのリポジトリ追加画面に次のURLを貼り付けてください。

```text
https://mitsuya0077.github.io/VR-Vlog-lilToon-Exporter/index.json
```

ZIPによる手動導入は[技術資料](Docs/TechnicalDetails.md#installation)を参照してください。更新前にはUnityプロジェクトをバックアップしてください。

## 使い方

1. Unityでアバターを開き、書き出す衣装・オブジェクト・Rendererを有効にします。アバターの最上位と親も有効にしてください。
2. メニューの **VR Vlog → lilToon VRM 1.0を書き出す** を開きます。
3. **① アバター（必須）** にHierarchyのアバター最上位を指定し、**② 作者名（必須）** にVRMへ記録する作者名を入力します。VRM名にはオブジェクト名を使います。
4. 必要に応じて「書き出し設定」で発光の抑制や、除外するペット・ギミックを指定します。
5. **③ 保存先を選んでVRMを書き出す** を押し、保存先を指定します。
6. できた `.vrm` をiPhoneへ送り、VR Vlogで読み込みます。見た目・まばたき・口・表情・髪と衣装の追従を確認してください。

エクスポーターは出力用コピー上でMAの現在の表示・体形・材質・Mesh Cutterを固定し、髪や衣装の接続処理後に基本形状・表情・材質を変換します。元のアバター・メッシュ・マテリアル・画像の設定を変更しません。MA Bone Proxy / Merge Armatureの設定済みの接続は自動で反映されます。接続設定のない髪を、配置や名前だけから頭へつなぐ機能ではありません。

## 対応範囲と制限

| 対象 | 動作・制限 |
| --- | --- |
| マテリアル | 色、影、発光、リム、MatCap、輪郭線などの対応設定を保存。描画方式や照明による差が残ります |
| メインカラー2nd/3rd | 対応する静止UV0レイヤー・色調補正・デカール・MSDF文字を焼き込み。自動変換できない設定は対象と理由を表示 |
| アルファ・輪郭・発光 | 0.9.0では静的アルファマスクを焼き込み、頂点R/Aによる輪郭幅と第1発光の対応設定を保存。専用表示にはschema 1.3対応アプリが必要 |
| 画像 | 縦横比を保って長辺1024以下へ縮小。元の画像は変更しません |
| 顔・体形 | MA処理後のBlendShapeの現在値を基本形状に反映し、表情との差分を保存。対応するまばたき・口のプリセットを補います |
| メニュー表情 | 登録済みButton・Toggleの対応する固定BlendShape表情を、一項目につき一つの表情として保存 |
| ジェスチャー表情 | FX Controllerに登録された対応クリップが対象。動くBlendShape表情の再生には対応版のVR Vlogが必要 |
| FaceEmo | 対象アバターに関連付けられた保存済み設定の、登録済みグループ・パターンが対象。VRChatの条件判定や連続操作全体は再現しません |
| 衣装・髪 | 有効なオブジェクト・Rendererと、対応する着せ替え設定が対象。非表示の衣装は含まれません |
| 揺れ物・ギミック | VRChatのPhysBoneや任意のギミック全般をVRMへ変換する機能はありません。既存の対応するVRM揺れ物は保持します |
| 未対応 | ファー・屈折・宝石・テッセレーション・AudioLinkなどは近似または省略。マテリアル差し替え・表示切り替え・ボーン変形を伴う表情、Puppet、ビルド時に初めて生成されるメニューは対象外 |
| 他のVRMビューアー | 標準MToonの互換表示を使用。追加の動く表情は静止した先頭の顔になります |

### iPhoneで読み込む

1. 書き出した `.vrm` ファイルをiPhoneの「ファイル」に保存します。
2. VR Vlogの設定に **実験的機能 → lilToon互換表示（モバイル）** がある版では、追加データを使う場合にオンにします。
3. **モデルを変更 → 端末のVRMを選ぶ** からファイルを選びます。設定を変更する前に読み込んでいた場合は、読み込み直してください。
4. 見た目・まばたき・口・表情を確認します。位置・サイズ・明るさなどは **モデルの見え方 → 内カメラで調整** から調整できます。

追加データの表示は、アプリの対応版と表示設定に依存します。オフの場合や追加データを適用できない場合は標準MToon表示になります。エクスポート成功だけで、アプリ内の見た目の一致を保証するものではありません。

0.9.0はマテリアルschema 1.3、配布済み0.8.xは1.2です。schema 1.3に未対応のアプリでは追加データが適用されず、標準MToon表示になります。[保存・描画の対応表](Docs/AppearanceFidelity.md)とアプリの更新内容を確認してください。以前のVRMに保存されなかった設定を反映するには、元のUnityアバターから再度書き出します。

## 困ったとき

- **省略された設定を確認したい**：0.9.0では近似・省略・縮小などがある場合に完了画面へ要約を表示します。「詳細を見る」またはUnity Consoleの「VR Vlog 書き出し詳細」を確認してください。
- **発光や目の色が違う**：「目などの白飛びを抑える」「別画像の強い発光も抑える」は0.9.0では初期状態でオフ、0.8.xではオンです。意図した発光も抑えられるため、必要な場合だけオンにして再出力してください。
- **レイヤーを変換できない**：画面の対象・設定・次の操作を確認してください。「表示されたレイヤーを省略して書き出す」は、その装飾を同じマテリアルの使用箇所すべてから省略します。見た目を残したい場合は省略せず、元の設定を確認してください。
- **髪や袖が追従しない**：元のプロジェクトでMAなどの接続設定とエラーを確認し、最新エクスポーターで再出力してください。既存VRMだけから元の接続設定を復元することはできません。
- **NDMFの処理で停止する**：表示されたプラグイン名・処理名と、該当するエラーを確認してください。

### 不具合報告とファイルの共有

[Issues](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter/issues)は公開されます。エクスポーター・Unity・UniVRM・lilToon・関連プラグインのバージョン、再現手順、期待した結果と実際の結果を記載してください。

「詳細をコピー」やConsoleの内容には、ユーザー名を含むファイルパス、アバター名、マテリアル名などが入ることがあります。貼り付ける前に確認し、個人情報・共有リンク・認証情報を伏せて、必要なエラー部分だけを共有してください。有料アバターのVRM、元プロジェクト、テクスチャ、ログ全文を公開で添付しないでください。

出力VRMには指定した作者名・モデル名のほか、モデルの形状・画像・表情名などが含まれます。素材の利用条件を確認してから書き出し・共有してください。

## 技術資料・開発

- [Unityの見た目の保存・対応表と検証](Docs/AppearanceFidelity.md)
- [変換仕様と開発チェック](Docs/TechnicalDetails.md)
- [照明データ仕様](Schema/Lighting.md)
- [動く表情のデータ仕様](Schema/ExpressionAnimations.md)
- [マテリアルのJSON Schema](Schema/VRVLOG_materials_liltoon.schema.json)
- [パッケージの検証・配布手順](Docs/ReleaseVerification.md)

## ライセンス

エクスポーターは [MIT License](LICENSE) です。0.9.0のアルファマスク処理にはlilToon 2.3.4由来の実装を含み、著作権表示とMITライセンス全文をパッケージに同梱します。出典とライセンスは [lilToon](ThirdPartyNotices/lilToon.md) · [UniVRM](ThirdPartyNotices/UniVRM.md) を参照してください。

掲載画像に含まれるアバターなどの第三者著作物は、本リポジトリのMITライセンスの対象外です。それぞれの権利者が定める利用条件が適用されます。画像の出典と利用条件へのリンクは[サイトの画像](Website/assets/README.md)を参照してください。

## English

A Unity Editor package exporting VRM 1.0 with a standard MToon fallback and optional VR Vlog data. Requires **lilToon 2.3.4** and **UniVRM 0.131.x**; tested with Unity 2022.3.22f1. VCC/ALCOM resolves the published UniVRM dependencies. Source avatars are processed through temporary copies. See [Technical details](Docs/TechnicalDetails.md) for installation, supported features and limitations.
