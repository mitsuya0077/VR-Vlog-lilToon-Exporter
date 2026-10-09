# 開発・検証・配布の運用 / Development pipeline

このパイプラインは、不具合報告・改善提案から、PR・検証・配布候補・承認・公開後の対応までを扱います。GitHubの実行成功と、Unity Editorや端末での受入、公開の承認はそれぞれ別の証拠です。

## 受付と対応判断

報告者は既存Issueを検索し、不具合または改善提案フォームを使います。脆弱性は[SECURITY](../SECURITY.md)の非公開窓口へ送ります。有料アバター・元プロジェクト・秘密情報・非公開リンクは公開Issue/PRへ貼らず、共有可能な最小素材と必要なエラーの抜粋を使います。

メンテナーは、再現情報と対応版を確認し、重複・製品の対応範囲・再現性・影響・優先度を判断します。追加情報が必要な場合は必要な項目を明示し、採用・見送りと理由を記録します。採用した作業は担当と受入条件を定め、まとまりが大きい場合はEpicから具体的な作業へ分けます。実装PRをIssueへ結び付け、検証結果・配布版まで追えるようにします。

## 参加とレビュー

[参加ガイド](../CONTRIBUTING.md)に従い、forkから作業ブランチを作ります。PRでは問題、変更後の動作、対象のIssue、実行した検証と残る制限、依存・形式・ライセンスへの影響を説明します。PRの自動検証は読み取り権限の標準Ubuntu runnerで実行し、公開用の権限や秘密情報を渡しません。初回の外部貢献者についてGitHubが実行承認を求める場合は、メンテナーが差分を確認してから実行を承認します。

メンテナーは最新headの必須 `schema` / `listing-validation`、自動Codex Review、未解決の指摘、変更に必要なUnity/実機の証跡を確認します。hostテストの成功だけで実際のUnity・画像・端末受入を通過したとは扱いません。検証済みheadから変更があれば、その変更に必要な検証とレビューを最新headでやり直します。

workflowの編集では、構文チェックに加え、攻撃者のPRが書込み権限や秘密情報を得られないか、入力をshellへ直接挿入していないか、必須チェックがskip・失敗無視で通らないか、配布候補のrun/コミット/ZIPを取り違えないかをレビューします。SHA固定だけで外部Actionの内容が安全と証明されるわけではありません。

## 実行経路と境界

| 経路 | 起動条件 | 公開権限 | 上限・保存 |
| --- | --- | --- | --- |
| Validate / `schema` | 全PR、mainへのpush | 読取りのみ | 30分。古い同一PRの検証は取消し |
| listing検証 | 全PRで変更判定、必要時だけbuilder | PR経路は読取りのみ | 判定/必須gate各2分、builder15分 |
| Build release candidate | mainへの手動起動、完全なexpected SHA | 読取りのみ。公開しない | 30分、ZIP最大20MiB・JSON各128KiB、artifact7日 |
| Release VPM Packages | mainへの手動起動、候補run ID・版・channel・SHA一致 | 配布jobに限定 | 30分、同時公開を直列化 |
| Pages / VPM更新 | mainへの手動起動、または公開releaseの既存経路 | 配布jobに限定 | 15分、Pages artifact1日、進行中の配置は取消さない |
| upstream調査 | 手動起動 | 読取りのみ。対応版は変更しない | 2分 |

既存の `github-pages` environment・mainルール・配布承認を維持します。workflowや環境を新しく有効化すること、リポジトリの公開設定を変更すること、OSSを告知することは、この手順を整えただけで自動的に許可されません。

## 共通のhost検証

Python 3.12とPowerShell 7を使います。workflow解析用のPyYAMLは版と配布ハッシュを固定しています。共有環境を変更したくない場合は専用venvへ導入してください。

```sh
python3 -m pip install --require-hashes --only-binary=:all: -r Tools/pipeline-requirements.txt
python3 Tools/run-actionlint.py
python3 Tools/run-validation.py --report work/validation.json
```

`run-actionlint.py` はLinux x64またはmacOS Apple Silicon向けの公式1.7.11を固定ハッシュで一時展開し、終了時に除去します。他のhostは同じ版のactionlintを別途用意し、そのhostの実行を記録してください。共有SDKやキャッシュは削除しません。

PR・main・候補・公開のhost検証は同じ `run-validation.py` で実行します。schema・公開内容・パッケージ・依存互換性・配布方針・listing・AAOパッチの既存Python検証、既存PowerShell検証、転送の4つのdefine構成を含み、最初の失敗で停止します。記録は開始時のコミット、作業コピーがcleanか、実行した相対コマンド、exit code、所要時間を含み、ログ全文や個人パスをartifactへ入れません。途中でHEADが変われば成功記録を出しません。未コミット変更のある参加者のhost検証は実行できますが、その記録を公開候補の証拠として使うことはできません。

Unityが必要な変更は[既存の互換性・回帰profile](../Compatibility/README.md#exporter-behavior-and-integration-regression-profiles)に従います。Editorと依存パッケージの実版、検証コミット、コマンド・結果・skip・未確認範囲をPRへ記録します。必要な端末・画像検証、固定コマンドと承認条件をhostテストに置き換えません。

## 候補の生成と公開の承認

1. 必須チェックと最新headのレビューを確認してmainへ統合します。公開する版と変更履歴・依存の設定を揃え、そのmainの完全SHAを選びます。
2. **Build release candidate**をmainから手動起動し、`expected_commit` に同じSHAを指定します。未コミット変更のあるcheckout、別SHA、不完全・失敗したhost検証、ZIP内の欠落した相対文書リンクは候補として拒否します。
3. 成功したrunの `release-candidate-<SHA>` artifactで、ZIP・`candidate.json`・`validation.json` を確認します。artifactは7日で期限切れになります。承認・復旧に必要な候補と証跡は、私有情報を混ぜずメンテナーの適切な保存先へ保管します。期限切れの候補を公開用の証拠として再利用せず、同じソースで候補を再生成します。
4. メンテナーは対象のUnity/実機受入・レビュー・ライセンス・配布文書・残る制限を確認します。所有者の必要な個別承認を、**コミット・version・stable/beta・ZIP SHA-256・候補run ID**に結び付けて得ます。候補生成成功は公開承認の代わりになりません。
5. 承認後だけ **Release VPM Packages**をmainから起動し、同じ情報を指定します。公開処理は、同じリポジトリの正式な候補workflowがmainで成功したこと、artifactが有効なこと、receiptとZIPの一致を確認し、host検証とローカル再生成ZIPのSHAも再確認します。fork PR・別workflow・失敗・取消し・途中のrunは拒否します。
6. 既存のタグ保護・stable/beta区別を保って公開します。betaはstableのlatestを動かしません。同じタグは上書きしません。Releases生成後、既存の許可されたVPM/Pages経路で全公開履歴を生成・検証します。
7. ReleasesとVPMのURL・版・ZIP hash・依存の整合、VCC/ALCOMへの導入・更新と必要な動作確認を行い、実際に確認した環境を記録して関連Issueへ反映します。

## 失敗・公開後の対応

失敗した段階のrunと対象SHAを残し、原因を切り分けます。コード修正後は新しいSHAの検証を行い、候補を再生成します。キャッシュ削除やcleanを定例処理にせず、不整合の原因と必要な範囲を確認してから行います。署名URLや鍵、私有素材を含むログは公開しません。

公開前の失敗ではタグやreleaseを作らず、公開後のVPM更新失敗では既に公開したreleaseを無断で作り直さず、同じ配布内容でlisting更新を再試行します。配布に問題が見つかった場合は利用者への影響と対応版を記録し、必要な承認を得て配布導線を止めるか以前の検証済み導線へ戻します。復旧でも公開済みタグ・ZIPを上書きせず、修正を新しいversionとして出します。

通常の報告は受付→再現→修正PR→検証→修正版の流れで追います。脆弱性は非公開で対応し、公開の時期・内容はセキュリティポリシーに従います。

## Actions費用のモデル

2026-10-10時点の[GitHub公式料金](https://docs.github.com/en/billing/concepts/product-billing/github-actions)では、公開リポジトリの標準runner実行は無料です。この構成は標準Ubuntuだけを使い、大型runner・新しい有料サービス・キャッシュの有料拡張を導入しません。PR検証を増やしてもrunner実行料金の増分は$0です。

新たに保存するのは成功した手動候補だけです。最大ZIP20MiBとJSON計256KiBを保守的に見込み、7日保存、30日月に均等に作成する定常状態を仮定すると、追加の平均保存量は `月の候補数 × 20.25MiB × 7/30` です。PR・mainのhost報告はrunner内だけに置き、artifactとして毎回保存しません。既存Pages artifactは1日保存です。新しいUnityキャッシュも作りません。

| 月のPR workflow実行数 / 候補数（仮定） | runner料金の増分 | 候補の平均追加保存量 | 無料枠が既に埋まっている場合の候補保存費用 |
| --- | --- | --- | --- |
| 100 / 2 | $0 | 約9.45MiB | 約$0.0023/月 |
| 500 / 8 | $0 | 約37.8MiB | 約$0.0092/月 |
| 2,000 / 30 | $0 | 約141.75MiB | 約$0.0346/月 |

上の保存費用は追加分がすべて課金対象だと仮定し、artifactの超過単価$0.25/GiB・月で計算した保守的な値です。アカウント全体で共有する無料枠と既存の請求額は未確認であり、確定請求額ではありません。無料枠内に収まれば保存費用の増分も$0です。実際のZIPが上限より小さい場合はさらに少なくなります。

```sh
python3 Tools/estimate-pipeline-cost.py --pr-runs 500 --candidate-runs 8
```

メンテナーは候補数・実サイズ・保存期間とアカウントの使用量を確認します。repositoryをprivateへ変える、大型runnerを導入する、保存期間を延ばす、キャッシュ上限を10GiBより増やす場合は、承認前に[runner料金](https://docs.github.com/en/billing/reference/actions-runner-pricing)と[保存料金](https://docs.github.com/en/billing/concepts/product-billing/github-actions#storage-pricing)で再見積もりします。手元端末の運用や必要なUnityライセンスはActionsの請求とは別です。
