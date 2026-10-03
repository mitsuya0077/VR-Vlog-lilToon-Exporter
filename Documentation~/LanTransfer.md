# iPhoneへVRMを直接送る

「スマホに送る」は、既存Exporterで生成したVRMを、UnityのPCから同じローカルネットワーク内のiPhoneへ直接渡す機能です。従来の「保存先を選んでVRMを書き出す」も引き続き利用できます。

> アバターはPCからスマホへ直接転送され、この転送でクラウドに送信・保存されません

VRM、認証トークン、鍵、QR内容を外部サーバーへ送信しません。QRもUnity内で生成します。転送するのは今回Exporterが出力したVRMだけです。転送側で再変換しないため、lilToon関連情報を含む元のバイト列を維持します。

## 用意するもの

- Windows PC上のUnity Editor。初版の「スマホに送る」はWindows専用で、Mac／Linuxではボタンが無効になり対応環境の説明を表示します。従来のファイル書き出しは引き続き利用できます。
- 既存Exporterの対応環境を満たすUnityプロジェクト。Unity／lilToon／UniVRMの対応版は[通常の導入ガイド](README.md)に従い、転送のためにUnity版や既存パッケージのpinを変更しないでください。
- 「スマホに送る」を含むExporterと、「PCから受け取る」を含むiPhone版VR Vlog。既存の配布版に両機能が含まれるか確認してください。
- 同じLANで機器同士が通信できるPCとiPhone。PCが有線Ethernet、iPhoneが同じルーターのWi-Fiでも利用できます。

## 操作

1. **VR Vlog → lilToon VRM 1.0を書き出す** を開き、アバター・作者名・書き出し設定を通常どおり指定します。
2. **スマホに送る** を押します。通常の保存ダイアログを開かず、既存Exporterの完成バイト列を転送用の一時VRMへ保存して準備画面を開きます。書き出し失敗時は通常の書き出し診断に従います。
3. 複数の接続があるPCでは、スマホと同じLANのIPv4を選び、受取期限を設定します。**待受を開始してQRを表示** を押し、画面に表示されたIP・TCPポート・期限を確認します。
4. iPhoneのVR Vlogで **モデルを変更 → PCから受け取る** を開き、アプリ内のカメラでQRを読み取ります。
5. 受取確認のファイル名・容量を確認して受信します。受信は一時ファイルへ逐次書き込み、容量とSHA-256の一致後に既存のインポート・正式保存へ進みます。
6. 完了を確認します。正式保存後の転送完了通知を受けたPCはトークンを無効化し、待受を停止します。スマホでのアバター読込みは既存のインポート処理が続けて行います。

受取期限は初期値10分で、Unity側の設定で1〜60分に変更できます。変更は次の待受開始時に適用します。受信上限は256MiB（268,435,456バイト）です。QRにはこの転送に必要な認証情報があるため、画像・内容を共有したり、ログ／不具合報告に貼ったりしないでください。

スマホには受信一時ファイルと正式保存用コピーのため、VRMサイズの2倍以上の空き容量を用意してください。受信前に容量を確認しますが、容量を取得できない場合は実際の書込みで判定します。確認後の容量変動などで保存が失敗する場合もあります。

PCとiPhoneの日時を正しく設定してください。時計がずれているとQRの受取期限や証明書の有効期間の検証で拒否される場合があります。日時を直してから、新しい転送とQRを作成します。

通信失敗後は、期限内なら同じQRの転送を先頭から再試行できます。期限切れ・PC取消し・完了済みの場合は新しい転送を作ります。スマホ側の取消し通知が届けばPC待受も停止します。通信断で通知が届かない場合は、PCで転送を停止するか期限切れを待ちます。取消し直後もPCが停止したとは限らないため、PCの表示を確認してください。

PCが認証済みの完了・取消し通知を受理した後は、その結果を期限切れや転送画面の取消しで上書きしません。画面を閉じる場合は、待受と接続を直ちに終了し、一時VRMを削除します。そのため完了通知への応答がスマホへ届かず、スマホに完了通知未確認の案内が出る場合があります。アバターの再受信は不要です。

検証済み受信一時VRMができた後の正式保存・アバター読込み失敗では、スマホの「保存済みVRMを読み込み直す」でそのファイルからimportを再実行します。正式保存時にも容量不足・保存先の失敗・バックアップ除外失敗を区別します。容量不足なら空きを確保してから再試行してください。正式保存を確認するまではPCへ完了通知を送りません。受信画面を閉じるまでは新しいQRや再ダウンロードは不要です。

## iPhoneの権限

QR読取りにはカメラ、直接ダウンロードにはローカルネットワークの許可が必要です。拒否した場合は、iPhoneの **設定 → プライバシーとセキュリティ → カメラ／ローカルネットワーク** でVR Vlogを許可し、受信をやり直します。[Appleの権限説明](https://support.apple.com/guide/iphone/iph168c4bbd5/ios)、[ローカルネットワークの説明](https://support.apple.com/102229)

QRはOSの外部カメラアプリではなくVR Vlog内で読み取ります。受信VRMと一時ファイルはOSバックアップから除外します。通常のファイル選択による読み込みも残ります。

## Windows Firewallの限定許可

信頼できるLANの接続がPrivateプロファイルになっているか確認してください。公共・共用ネットワークを無条件にPrivateへ変更しないでください。組織管理のPCでは管理者のポリシーに従います。Firewall全体の無効化は行いません。

今回使用中のUnity.exe、選択したLAN IPv4、表示中のTCPポート、Privateプロファイル、LocalSubnetからの接続だけを許可します。管理者としてPowerShellを開き、Unityの表示値を入力してください。QRやトークンは入力しません。

```powershell
Get-NetConnectionProfile
$taskUnityProgram = Read-Host '今回使用中の Unity.exe の絶対パス'
$taskLanIPv4 = Read-Host 'Unity の転送画面に表示された LAN IPv4'
$taskTransferPort = [int](Read-Host 'Unity の転送画面に表示された TCP ポート')
$taskFirewallRuleName = "VRVlogLanTransfer-$taskTransferPort"
New-NetFirewallRule -Name $taskFirewallRuleName `
  -DisplayName 'VR Vlog 今回の LAN 転送' `
  -Direction Inbound -Action Allow -Protocol TCP `
  -Program $taskUnityProgram -Profile Private `
  -LocalAddress $taskLanIPv4 -LocalPort $taskTransferPort `
  -RemoteAddress LocalSubnet -EdgeTraversalPolicy Block
```

終了後は今回のルールだけを削除します。新しい転送でポートが変わった場合は、古いルールを削除して現在の表示値で作り直します。他のルールは変更しません。
同じPowerShellで以下を実行します。閉じてしまった場合は、ルール名 `VRVlogLanTransfer-<その転送のポート>` を指定して削除してください。

```powershell
Remove-NetFirewallRule -Name $taskFirewallRuleName
```

詳細は[MicrosoftのNew-NetFirewallRule](https://learn.microsoft.com/powershell/module/netsecurity/new-netfirewallrule)を参照してください。ルーターのポート転送やインターネット公開は不要です。

## 接続できないとき

| 状況 | 確認すること |
| --- | --- |
| PCが見つからない／応答しない | PC待受・IP・ポート・同じLAN・Privateの限定Firewallルールを確認する。PCのスリープを解除する。 |
| Wi-Fi名が同じなのに接続できない | ゲストWi-Fi、端末間隔離、別VLAN、VPNの経路を確認する。PC有線とスマホWi-Fiの機器間通信もルーターで許可される必要がある。 |
| スマホで権限を拒否した | iOSのカメラ／ローカルネットワーク設定を変更し、受信をやり直す。 |
| 期限切れ／転送取消し／完了済み | PCとiPhoneの日時を確認し、Unityで新しい転送とQRを作成する。 |
| 証明書・認証・QRの検証失敗 | 接続を停止し、PCとiPhoneの日時を確認してから、正しいPC画面で新しいQRを作成して読む。証明書検証を無効化しない。 |
| 通信断 | 接続を戻し、期限内に先頭から再試行する。 |
| スマホ保存完了、PCへの完了通知は未確認 | 再受信は不要。Unityの転送画面を閉じる。閉じない場合も受取期限で待受が停止する。 |
| 容量／SHA-256不一致 | 受信VRMを採用せず、新しい転送を作り直す。 |
| 256MiB超過／スマホの空き容量不足 | 元の書き出し設定・画像容量を確認し、新規受信にはVRMサイズの2倍以上の空きを確保する。正式保存時の容量不足なら検証済み一時VRMから再試行する。 |
| 保存先の失敗／バックアップ除外の失敗 | スマホの案内に従い、保存先を確認するかアプリを再起動する。正式保存が確認できるまで完了扱いにしない。 |
| PCの応答が時間内に届かない | Unityの待受、同じLAN、通信許可を確認し、受取期限内に再試行する。 |
| 転送後のVRM読込み失敗 | 「保存済みVRMを読み込み直す」で検証済み受信コピーを再利用する。通常のファイル読み込みでも再現する場合は既存Exporter／アプリの読込み診断を調べる。転送中にVRMは再変換しない。 |

TLS接続ではQRに含まれる証明書フィンガープリントを確認します。転送ごとに推測困難な認証トークンを発行し、完了通知・取消し・期限切れで無効化します。任意のローカルファイルを指定してダウンロードする機能はありません。

## 検証記録

以下はPCとiPhoneを組み合わせた実機の受入れ試験で、全項目が未実施です。後段のホスト検証と分け、実施環境、対象commit、操作、pass/fail/skippedと理由を記録します。秘密を含むQR、トークン、秘密鍵、私有VRM、ログ・スクリーンショットは共有・commitしません。

| ケース | 期待結果 | 実機状態 |
| --- | --- | --- |
| 正常転送／PC有線＋スマホWi-Fi | 元出力・受信一時ファイル・正式保存の容量とSHA-256が一致し、既存importで表示される。 | 未実施 |
| lilToon拡張付き実出力 | 拡張を含むbytesが一致し、通常のファイル読み込みと同じ表示になる。 | 未実施 |
| 権限拒否／Firewall拒否／別LAN | 原因の案内が出て、未検証ファイルは正式採用されない。 | 未実施 |
| 通信断／取消し／再試行 | 先頭から再試行でき、通知到達時は待受停止。通知不能時はPC停止／期限切れで終了する。 | 未実施 |
| 保存後の完了通知に応答がない | スマホ保存成功とPC停止未確認を区別し、Unity画面を閉じる案内を出す。再受信は不要。 | 未実施 |
| 期限切れ：待機中／受信中 | トークン無効化・待受停止。古いQRが使えない。 | 未実施 |
| 不正トークン／不正QR／別証明書／任意パス | 拒否され、今回のVRM以外を取得できない。 | 未実施 |
| 改ざん／切詰め／過大サイズ | size/hash検証で拒否され、正式保存されない。 | 未実施 |
| 同一VRM／同名異内容／256MiB境界 | bytes保全、既存保存の再利用／非上書き、上限判定が成立する。 | 未実施 |
| 正式保存後のVRM読込み失敗 | PC転送は終了し、スマホ側の検証済みコピーからimportを再試行できる。 | 未実施 |
| 受信時・正式保存時の容量不足／保存失敗／除外失敗／応答時間切れ | 原因を区別して表示し、未完了ファイルは正式採用しない。検証済みコピーがあれば再ダウンロードせずに保存・読込みを再試行する。 | 未実施 |
| Unityの再読込み／転送画面を閉じる／終了 | 待受が終了し、期限を過ぎてもトークンが復活しない。 | 未実施 |
| iPhoneの撮影復帰／背景移行／バックアップ除外 | カメラ競合なく復帰し、未完了ファイルを採用せず、tempと正式VRMがバックアップ除外。 | 未実施 |

ホストC#試験、Unity Editorのコンパイル／Console／テスト／画面、iOS nativeコンパイル、IL2CPP/Xcodeビルド、物理iPhoneの結果を分けて記録します。Editorや静的テストだけでは、実際のTLS・権限・カメラ所有権・Windows Firewall・有線LAN・OSバックアップ除外を検証したことになりません。配布済みアプリでの確認も別に必要です。

### ホスト・CI検証（2026-10-03 05:16 UTCごろ）

対象はExporter `ba6aab9`とApp `db6756d8`（mainへのrebase後）です。以下はこの時点の記録です。後続のEditor検証や追加CIは、対応するPRの最新記述と検証成果物を参照してください。

| 検証 | 結果と証拠の範囲 |
| --- | --- |
| `pwsh -File Tools/run-lan-transfer-tests.ps1` | WindowsとLinux CIでそれぞれ19/19合格。合成ファイルを実際のTLS loopback接続で送り、認証、別証明書、不正要求、通信断・再試行、取消し・期限切れ、ファイル一致などを検証。別端末、Windows Firewall、iPhoneとの接続は対象外。 |
| Exporter [schema run 37097031136](https://github.com/mitsuya0077/VR-Vlog-lilToon-Exporter/actions/runs/37097031136) | 対象headのCI成功。Linuxでの実TLS loopback 19/19を含む。 |
| Windows package検査 | パッケージ検査合格。Windowsのsymlinkケースは権限不足のためskipped。実機転送の合格とは別の証拠。 |
| Unityの参照DLLを使用したMonoソースコンパイル | ホストコンパイル成功。Unity Editorでの全体コンパイル、Console、実行確認の代わりにはしない。 |
| App `Tools/run-ios-preflight-behavior-tests.ps1` | Windowsで44個のproductionチェック／120 assertion合格。ソース契約の検査であり、iOS SDKコンパイルや権限プロンプトの実機確認ではない。 |
| App `bash Tools/run-development-checks.sh` | rebase後の対象ソースで全体検査が終了コード0。Hub／Adapter／Recordingの既存回帰1090／18／36チェック、正式保存のホスト回帰73チェックが合格。 |
| App `Tools/run-lan-vrm-privacy-tests.ps1` | productionのmerge／検証をXML Plist/PBX host adapterで実行し、20 assertion合格。既存宣言の保持・再実行・欠落拒否・DiskSpace／E174.1の対応を検査。本物のUnity Xcode Plist DLLとは別の証拠。 |
| App native helper／iOS SDK | macOSでQR parser、書込み／hash、容量判定／エラー分類、proxy設定、証明書trustのproduction helper動作試験が合格。Xcode 16.4 SDKのiOS 17 simulator／arm64 syntax検査も合格。iPhoneの受信画面・権限、IL2CPP／リンク・archiveとは別。 |
| App `Tools/test-lan-vrm-localization.py` | native文言38個／Unity文言7個／四言語の翻訳契約が合格。実機画面・言語選択は未確認。 |
| App [Security preflight run 37098886489](https://github.com/mitsuya0077/FaceMaskVTuber/actions/runs/37098886489) | 対象headに紐づくLinux／Appleの全job成功。development checks、Apple helper、iOS SDK syntax検査を含む。 |

この記録時点では新AppへUnity MCPが接続済みですが、最新ソースのEditorコンパイル／Console・Unityテスト・画面は結果待ちです。IL2CPP/Xcode archive、本物のUnity Xcode Plist DLLによるmerge実行、iPhoneでの一連の転送は未実施です。PC有線＋iPhone Wi-Fiの転送、権限拒否、バックアップ除外などの実機合格はまだ記録していません。
