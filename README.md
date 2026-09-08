# VR Vlog lilToon VRM Exporter

**UnityのアバターをVR Vlogで使う。**

lilToonのアバターを、VR Vlogで使える **VRM 1.0** に書き出すUnity Editor用パッケージです。
対応する色・影・輪郭線、調整した顔、VRChat・FaceEmoの表情をひとつのファイルに保存します。

[紹介ページ・インストール](https://mitsuya0077.github.io/VR-Vlog-lilToon-Exporter/) · [最新リリース](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter/releases/latest) · [詳しい仕様 / English](Docs/TechnicalDetails.md) · [不具合を報告](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter/issues)

## できること

- **lilToonの設定を引き継ぐ** — 色、影、発光、MatCap、輪郭線などの対応設定を保存します。追加データに未対応のVRMビューアー向けに、MToonの互換表示も含めます。
- **調整した顔を基本の形にする** — Unityで設定したBlendShape値を保ち、対応するまばたき・口のプリセットを補います。
- **登録済みの表情を自動で取り込む** — 対応するVRChatメニュー・ジェスチャーの表情を保存します。動くジェスチャー表情の再生には、対応版のVR Vlogが必要です。
- **元のアバターを変更しない** — 一時コピーから書き出し、元のメッシュ・マテリアル・画像・インポート設定を変更しません。
- **不要なペット・ギミックを除外する** — 書き出し設定で対象を指定すると、そのオブジェクトを除いたVRMを作ります。身体や残す衣装に必要な骨は保護します。
- **大きな画像を自動で縮小する** — 4Kなどの画像は、縦横比を保って長辺1024以下の出力用画像に変換します。色や透過の画像と、法線などの数値を持つ画像を区別して縮小します。
- **衣装と髪の追従を保存する** — Modular Avatarなどの対応する着せ替え設定がある場合、導入済みのNDMFで出力用コピーを処理します。元のアバターで手動ベイクする必要はありません。
- **ボーンを指定し直さずに書き出す** — アバター直下に置いた髪も、設定済みのMA Bone Proxy／Merge Armatureの接続を自動で反映します。頭への移動やエクスポーター内の手動プレビューは不要です。独立したペット・小物は現在の接続を保持します。

すべてのシェーダー効果や表情を再現するものではありません。[対応範囲と制限](#対応範囲と制限)を確認してください。

## インストール

**VCC（VRChat Creator Companion）またはALCOMからの導入を推奨します。**

1. [紹介ページ](https://mitsuya0077.github.io/VR-Vlog-lilToon-Exporter/)の「VCC / ALCOMに追加」を押します。PCで関連付けられたVCCまたはALCOMが開きます。開かない場合は、下のURLを使いたいアプリのリポジトリ追加画面に貼り付けてください。
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
| マテリアル | lilToon / lilToon Lite / lilToon Multi |
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

### iPhoneで読み込む

1. 書き出した `.vrm` ファイルをiPhoneの「ファイル」に保存します。
2. VR Vlogの設定を開き、**モデルを変更 → 端末のVRMを選ぶ**からファイルを選びます。
3. lilToonの追加データを使う場合は、設定の**実験的機能 → lilToon互換表示（モバイル）**をオンにします。
4. 見た目やまばたき、口、表情の動きを確認します。位置・サイズ・明るさなどは**モデルの見え方 → 内カメラで調整**から調整できます。

表示はアプリの描画方式や端末によって変わります。新しい書き出し設定を反映するには、元のUnityアバターから再度書き出してください。

### 書き出し後の確認

- **省略された効果・表情**：Unity Consoleの「VR Vlog 書き出し詳細」で理由を確認できます。
- **目などの白飛び**：「目などの白飛びを抑える」は初期状態でONです。ベース画像と発光画像に同じテクスチャが使われる場合に発光を省きます。意図した発光を残す場合はOFFにできます。
- **焼き込み後に目の色が変わる**：ワンクリック書き出しの「別画像の強い発光も抑える」は初期状態でONです。焼き込み前の目画像など、別のテクスチャを使うHDR発光も省きます。目以外の意図的なHDR発光にも適用されるため、必要な場合は解除してください。
- **メインカラー2nd/3rd**：静止したUV0のレイヤーと色調補正を出力時に自動で焼き込みます。UVが0〜1に収まるデカール、模様のコピー、MSDF文字にも対応します。元のマテリアルや画像は変更しません。左右別の表示、別のUV、透明度や時間・照明に依存する設定などは、対象・設定・次の操作を一覧で表示します。
- **自動変換できない装飾**：「表示されたレイヤーを省略して書き出す」を選ぶと、そのレイヤーの模様・文字・透明度変更を出力用コピーだけで省略します。同じマテリアルを使う箇所すべてに適用されます。表示されたレイヤー以外は勝手に省略せず、追加の問題があれば再度案内します。見た目を保ちたい場合は省略せず、対象マテリアルの選択や詳細のコピーができます。
- **既存VRMへの追加モード**：未ベイクのマテリアルがある場合は、画像も一緒に更新するワンクリック書き出しを使用してください。保存先は `.vrm` のみ指定できます。
- **古いVRMを使っている**：新しい変換内容を反映するには、Unityの元アバターから再度書き出してください。
- **全体が白飛びする・影の境界がおかしい**：0.8.0は材質ごとの明るさ上限・下限や照明への追従設定も保存します。対応するVR Vlogへ更新してから再出力してください。旧VRMも読み込めますが、以前のファイルに保存されていない独自の照明設定は再出力が必要です。
- **LightLimitChangerのエラーで止まる**：NDMFプラグインが必要とする一時アセットの保存先を自動で用意します。それでもエラーが出た場合は、表示されたプラグイン名・処理名と「詳細をコピー」の内容を不具合報告に添えてください。元のアバターを変更する必要はありません。
- **袖がTポーズのまま残る**：Modular AvatarのMerge Armatureなど、衣装の骨を身体へ追従させる設定を出力用コピーへ適用します。NDMFは1.8.3以降の1.xに対応します。元の追従設定が残っていないVRMだけから、骨の対応を推測して変更することはありません。
- **AutoAnchorObjectを使う小物の位置**：全頂点にボーンウェイトがないメッシュは、元のメッシュオブジェクトへの追従を明示的なウェイトへ変換します。Root Boneを表示位置の根拠には使いません。一部の頂点だけウェイトが欠けている場合は、対象メッシュと修正理由を表示して停止します。元のメッシュやRoot Boneは変更しません。
- **ペット・ギミックを含めたくない**：「書き出し設定」の「書き出さないオブジェクト」に、その最上位オブジェクトを追加します。元のHierarchyは変更しません。除外する動作だけを表情から省き、残す顔のBlendShapeを取り込みます。除外対象へのVRM拘束は現在の姿勢で固定されます。
- **顔の位置や大きさがずれる**：アプリの身体サイズ計算にも依存します。エクスポーターの更新だけでアプリの計算は変わりません。不要なオブジェクトを除外して再出力し、身体の骨格を基準にする修正を含むアプリでも確認してください。

## 対応範囲と制限

| 対象 | 動作・制限 |
| --- | --- |
| 見た目 | 対応する設定を保存し、MToonへ変換します。描画方式や端末による差が残ります |
| 特殊効果 | ファー、屈折、宝石、テッセレーション、AudioLinkなどは近似・省略します |
| 衣装 | 現在有効なオブジェクト・Rendererが対象です。非表示の衣装は含めません |
| メニュー表情 | 登録済みのButton・Toggleと対応する固定BlendShape表情が対象です |
| ジェスチャー表情 | FX Controllerに登録された対応クリップが対象です。動く表情は対応版のVR Vlogで再生します |
| FaceEmo | アバター内、または別のシーンオブジェクトから対象アバターを参照する保存済み設定を読み込みます。登録済みグループ・表情パターンのデフォルト顔と分岐クリップが対象で、トリガーの端点は個別の表情になります。未登録パターン、VRChatの条件判定・連続入力・追跡制御の再現は対象外です |
| 未対応の表情 | マテリアル差し替え・表示切り替え・ボーン変形を伴う項目、Puppet、ビルド時生成のメニューなどは対象外です。詳細は技術資料を参照してください |
| 他のVRMビューアー | MToonの互換表示を使用します。動く表情は静止した先頭の顔になります |

追加データはVRMを読むための必須拡張にしません。構造不正や安全上限の超過などはエラーにし、途中までのVRMを保存しません。アバター・衣装・テクスチャの利用条件を確認してから書き出してください。

## 詳しい情報・開発

- [変換の仕組み・対応条件・開発チェック（English / 日本語）](Docs/TechnicalDetails.md)
- [動く表情の保存仕様](Schema/ExpressionAnimations.md)
- [マテリアル拡張のJSON Schema](Schema/VRVLOG_materials_liltoon.schema.json)
- [変更履歴](CHANGELOG.md)
- [正式版の検証・ALCOM配布手順](Docs/ReleaseVerification.md)

リポジトリのルートで `python Tools/validate.py` と `pwsh -File Tools/run-behavior-tests.ps1` を実行できます。
Unity Editor内の検証は `Tests/Editor` にあり、依存パッケージを導入したUnityプロジェクトが必要です。

不具合は[Issues](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter/issues)へ、エクスポーター・Unity・UniVRMのバージョン、再現手順、エラー内容を添えて報告してください。有料アバターや個人情報を含むファイルは公開せず、必要な範囲の情報を共有してください。

## ライセンス

エクスポーターは [MIT License](LICENSE) です。配布しているUniVRMパッケージの出典とライセンスは [Third-party notices](ThirdPartyNotices/UniVRM.md) を参照してください。アバターなどの素材には、それぞれの利用条件が適用されます。

## English

A Unity Editor package that exports a VRM 1.0 file with a standard MToon fallback and optional VR Vlog lilToon data. Supported avatar shape customizations and VRChat expressions are preserved without modifying the source assets.

Add the repository URL above in VCC or ALCOM, then install **VR Vlog lilToon VRM Exporter**. Its tested UniVRM dependencies are installed automatically. See the [technical documentation](Docs/TechnicalDetails.md) for the full workflow, compatibility limits, and development checks.
