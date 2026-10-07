# 同梱Humanoidアニメーション / VRVLOG_humanoid_animations 1

ExporterとVR Vlogアプリで共有する、手動導入した動くHumanoidポーズの仕様。
GLB rootの任意拡張 `VRVLOG_humanoid_animations` に保存し、`extensionsUsed` に宣言する。
`extensionsRequired` には入れない。元の `.anim`、Animator Controller、イベント、スクリプトは配布しない。

既存の静止ポーズ用 `VRVLOG_humanoid_poses` v1の形式・骨一覧・意味は変更しない。
動く手動クリップはアニメーション拡張にだけ収録し、同じクリップの0秒静止fallbackを静止拡張に入れない。
旧アプリは新拡張を無視し、既存の静止ポーズと通常のVRMを従来どおり読み込む。
新アプリは二つの拡張を独立に検証し、アニメーション拡張が破損・未対応でも静止ポーズと通常VRMの読込を続ける。

## データ

Rootは `schemaVersion: 1`, `space: "vrm1AvatarRestDelta"`, `animations: [...]` の三項目。
各animationは次の項目をすべて持つ。未知の項目、未知のschema versionは受け付けない。

| 項目 | 意味 |
| --- | --- |
| `id`, `name`, `category`, `source` | 安定ID、表示名、分類、出所。分類だけ空文字可。 |
| `sourceStartTime` | 元クリップでの採取開始時刻（秒）。再生時計には加算しない。 |
| `duration` | 出力したローカル時間列の長さ（秒）。0より大きく600以下。 |
| `loop` | 元クリップに由来する既定の繰り返し設定。アプリのonce/loop選択は再生側が管理する。 |
| `bones` | `{ "bone": VRM骨名, "node": 最終VRMのnode番号 }` の対応表。 |
| `frames` | `{ "time": 秒, "hipsOffset": [x,y,z], "rotations": [[x,y,z,w], ...] }` の時間列。 |

`rotations[i]` は常に `bones[i]` に対応する。全frameの回転列の長さを骨表の長さに一致させる。
骨名・node・animation IDは重複禁止。nodeは `VRMC_vrm.humanoid.humanBones` の同じ骨の参照と完全一致させる。
名前やTransformの階層からnodeを推定しない。

骨は静止仕様の50骨に `neck` と `head` を加えた52骨から選ぶ。
首・頭は明示的な元カーブがある場合だけ表に収録する。眼・顎・表情カーブは収録しない。
頭を含むかどうかをアプリが骨表から判断し、顔・頭の追跡との所有権は再生側で決める。
静止仕様の `sampledMotion` は動く元クリップから一点を採ったという由来情報であり、この時間列の代用ではない。

## 座標と補間

回転とhips位置は静止仕様と同じ `vrm1AvatarRestDelta` 空間を使う。
回転は親相対localRotationではなく、アバターroot空間のレスト姿勢からのワールド回転差分。
`D = inverse(root.rotation) * sampled.rotation * inverse(rest.rotation) * root.rotation`。
親から子へ `worldRotation = root.rotation * D * restAvatarRotation` を適用する。
hipsOffsetはレストからのroot空間位置差分で単位m。アバターrootのシーン配置・回転・スケールは収録しない。
VRM1の右手系への変換は位置 `(-x,y,z)`、Quaternion `(x,-y,-z,w)`。

手動クリップは元アバターの独立コピーで評価し、元の骨・マスクの対応と回転軸を保持する。
書き出し前処理で骨名や親階層が変わっても、この差分を最終VRMのHumanoid骨へ対応付ける。
明示的に動かす骨が最終コピーから失われた場合は、その項目を理由付きで除外する。

通常の隣接frameではhipsOffsetを線形補間し、Quaternionは両端を正規化した最短経路slerpで補間する。
Quaternionの内積が負なら片方の符号を反転する。`q` と `-q` は同じ姿勢を表す。
アプリは時間列全体に同じレスト姿勢・サイズ基準を使い、各frameの差分を積み重ねない。

frameの時刻は0からdurationまでの非減少順。最初は正確に0、最後は正確にdurationとする。
STEP不連続では隣接する二つのframeだけ同時刻を許す。左が直前の値、右がその時刻以後の値。
同時刻を三つ以上続けること、0秒の重複、時間の逆行は禁止。
その時刻に正確に一致したら右側を採用し、直前の区間では左側へ補間する。
durationに二つある場合も終端は右側を採用する。
採取時刻はUnityの元カーブのfloat時刻に合わせ、STEP直前の評価が丸めによって右側へ飛び越えないようにする。

共有クラスの `Locate(time, out from, out to, out amount)` はローカル時刻を0〜durationにclampする。
binary searchでframe参照と補間率を返し、通常経路では配列・List・iteratorを割り当てない。
once/loopの時計、速度、選択時の補間、停止・解除時の復元はアプリの責務で、Locate自体はloop wrapを行わない。
onceの終端は最終frameに固定し、loopでは呼出側が時間を折り返す。

## 上限と検証

アニメーション128件、各2〜8192frame、全件合計32768frame、各1〜52骨、拡張JSON UTF-8最大16MiB。
文字列は256 UTF-16文字以下で制御文字禁止、ID・名前・出所は空白だけの値も禁止。
sourceStartTimeは0〜600秒。位置は各軸±10m。hipsを骨表に含めない場合、全frameのhipsOffsetは0。
数値は有限値のみ。Quaternionは四要素、各成分の絶対値1.001以下、二乗長と1の差が0.002以下。
nodeは0〜65535の整数とし、存在する最終VRMのHumanoid nodeとの一致を別途検証する。

読込・書込とも、scalar/containerと文字列escapeを含む保守的な容量予算を使う。
実際のJSONが16MiB未満でも予算に収まらない構成は拒否する。書込はframeごとに予算を計上し、過大なDOMを先に構築しない。
上限超過・不正な時間列・骨参照を静止化、切り捨て、部分採用で回復しない。
schemaは形と基本の範囲を示し、時刻の順序、STEP、列長、unit回転、重複、node対応、合計上限は共有C# readerが検証する。

## 検証

`Tools/run-pose-contract-tests.ps1` はUnityなしで静止v1と新アニメーション契約を一緒に確認する。
roundtrip、STEPの直前・exact・直後、終端、各上限、有限値、骨列、metadata、exact node参照を検証する。
静止v1の既存テストは維持する。

Exporter側は腕・指・Humanoid筋肉・hips/root・首頭・mask/weight・STEPを元のUnity評価と中間時刻で比較する。
元クリップや元アバターの不変性、イベント未実行、export/reimport後の骨対応も確認する。
アプリ側はLoaderからcontrol rigへ適用し、時間経過、once/loop、選択解除、モデル変更、追跡継続、古い拡張との共存を確認する。
Unity・ホストテストの成功をiPhone実機の成功とは扱わない。

## 短いキー間隔とUnityの不連続評価

0.11.11-beta.14では、短いキー間隔を固定の最小時間だけで拒否せず、Unityが区別できる元クリップのfloat時刻で細分化します。有限・重みなしのHermiteカーブでも、Unityがキー境界を不連続に評価する場合があります。元カーブの直前・直後の評価を微分の上限と照合し、通常の連続補間やfloatの丸めでは説明できない境界の跳ねを確認した場合だけ、既存のSTEP形式で両側の値を保存します。急な連続曲線を通すための誤差緩和ではありません。

全対象骨と腰について元のUnity評価との誤差を検査します。精度を満たせないクリップは静止姿勢へ置き換えず、問題の骨・元時刻・区間・誤差・許容値を詳細に表示します。
