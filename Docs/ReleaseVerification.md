# パッケージの検証と配布

## 公開前の確認

対象コミットと依存バージョンを記録し、PRのレビューとCIを完了します。コードの変更後は、影響する検証をやり直してください。

- `python Tools/validate.py`：パッケージ・スキーマ・ソースの整合性。
- `python Tools/check-public-content.py`：追跡対象ファイルへの秘密情報・ローカル入力の混入。
- `python Tools/test-package.py`：配布ZIPの許可リストと混入防止。
- `pwsh -File Tools/run-behavior-tests.ps1`：形状・表情・マテリアル・画像・GLBの処理。
- `pwsh -File Tools/run-bake-tests.ps1`：マテリアルの事前判定とレイヤー省略。
- `pwsh -File Tools/run-ndmf-preparation-tests.ps1`：NDMF準備の契約とアセット所有権。
- 依存パッケージを導入したUnityで `Tests/Editor` を実行。

ホストのテストダブルはUnityの描画やプラグイン全体を再現しません。UnityではGamma / Linearの画像処理、MAの衣装・髪、独立した小物、元アセットの保持、出力VRMの再読み込みを確認します。GPUベイクを検証する場合は `-nographics` を使わないでください。

VR Vlogでは見た目・表情・髪と衣装の追従・保存動画を確認します。新しい追加データを出力する版は、対応アプリが利用可能になってから配布してください。実施済みの確認と未確認の端末・構成を区別して記録します。

## 配布内容

`Tools/build-package.py` はGitの追跡対象から、次のファイルだけをZIPに含めます。

- `Editor/` のC#・アセンブリ定義とUnityメタファイル
- `package.json`、`LICENSE`、`CHANGELOG.md`
- パッケージ用の `Documentation~/README.md`
- `ThirdPartyNotices/` のライセンス表示

開発用ツール・テスト・CI設定・Webサイト・開発資料はGitHubから参照できます。作業ディレクトリやローカルに追加したファイルは配布しません。公開前にコンテンツ検査を通します。

## リリース手順

1. レビューとCIを通したPRをmainへマージします。
2. `package.json` と変更履歴を確認します。
3. GitHub Actionsの **Release VPM Packages** をmainから実行し、パッケージのバージョンと固定のUniVRMバージョンを指定します。
4. 生成したタグが対象コミットを指し、ZIPの `package.json` と収録ファイルが一致することを確認します。
5. [VPM一覧](https://mitsuya0077.github.io/VR-Vlog-lilToon-Exporter/index.json)のバージョン・ダウンロードURL・SHA-256を添付ZIPと照合します。
6. VCC・ALCOMの新規導入と更新を確認します。

既存リリースのZIPやタグを置き換えず、パッケージ内容の変更は新しいバージョンで配布してください。公開リリースの説明は変更履歴の該当版から生成します。私有アバター・プロジェクト・ログ・認証情報を添付しないでください。
