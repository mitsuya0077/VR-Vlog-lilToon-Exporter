# 同梱Humanoid静止ポーズ / VRVLOG_humanoid_poses 1

Exporter と VR Vlog の共通仕様。任意のGLB root extensionとして保存し、extensionsUsedに宣言する。
extensionsRequiredには入れない。拡張子は.vrm。生の.animやAnimatorは配布しない。

## データと座標

`schemaVersion: 1`, `space: "vrm1AvatarRestDelta"`, `poses: [...]`。
各poseは`id, name, category, source, sampleTime, sampledMotion, hipsOffset, bones`。
boneは`bone`（VRM 1.0のHumanoid名）、`node`（VRMC_vrm.humanoidと同じnode）、
`rotation`（xyzw単位Quaternion）。首・頭・目・顎は含めない。

回転はアバターroot空間のレスト姿勢からの**ワールド方向差分**:
`D = inverse(root.rotation) * sampled.rotation * inverse(rest.rotation) * root.rotation`。
親から子の順で `worldRotation = root.rotation * D * restAvatarRotation` と適用する。
これは親相対のlocalRotationではない。control rigのレスト軸が変わっても同じ方向差分を使用できる。
hipsOffsetはレスト腰からのroot空間の位置差分、単位m。rootの配置位置・回転・サイズは保存しない。
VRM1の右手系。Unityとの変換はX反転、位置(-x,y,z)、回転(x,-y,-z,w)。
UniVRMが省略するアバターrootのTRSを除いたメートル座標で採取し、読み込み後のサイズを一度だけ適用する。

上限は128ポーズ、各50骨、JSON UTF-8 2MiB、文字列256 UTF-16文字、採用時刻0〜600秒、
腰差分の各軸±10m。非有限値・重複ID/骨/node・非単位回転・不一致骨参照は拒否。
読込時は再シリアライズを避ける保守的な容量計算も行うため、文字列などの内容によっては2MiB未満でも拒否する。
読込に失敗した拡張は全体を切り離し、通常VRM読込は継続する。

## 収集・対応範囲

APLは生成前の対象アバター内の登録を読み取り、コピー側のAPLタグだけを除去する。
これによりAPL生成メニューからの二重取り込みとAPLのキャッシュ生成を避ける。
名前・カテゴリー・元クリップは登録から直接取得する。開始/終了クリップは未対応として表示する。
通常メニューとMA生成メニューは操作条件から静止した到達状態を解決する。
履歴・外部入力・未対応の合成/behaviour/時間遷移は理由付きで除外する。
手動クリップの既定時刻は0秒。動くクリップは明示的な時刻の静止画として扱う。
同じクリップ名や表示名では統合しない。クリップ識別子・時刻・マスク・操作条件が一致する場合だけ統合する。
一時生成クリップは実カーブの指紋を使い、再生成によるGUID変更で確認画面の除外・改名が失われないようにする。

初版のメニュー解析は、選択したButton/Toggleの値だけで全ての先行状態から同じ静止状態へ到達できる
ゼロ時間の遷移を対象とする。サブメニュー、階層化されたAny State、AnimatorOverrideController、
Base/Gesture/Action/FXの順序、AvatarMask、一定のレイヤー重みを確認する。
Action等を有効にする即時のVRCPlayableLayerControlも解析する。
Additive、BlendTree、複数Humanoidクリップの筋肉カーブ合成、HumanoidとTransformの混合、
Parameter Driver、時間付き遷移、開始/終了、ミラー/IKは未対応理由を表示する。
MAが既定の移動用Animatorまで展開した場合、Upright等への依存も検出して対象外にする。
その構成のメニューを全て自動変換できるわけではない。APL登録の直接収集、または明示的な手動時刻採用を使用する。
Transformカーブは独立コピー上でUnityのカーブを評価し回転を合成する。Humanoid筋肉カーブは
独立コピーのPlayableで評価する。どちらも共有クリップのイベントや非ポーズカーブを実行しない。

通常はアバターを選んでそのまま書き出す。対応した静止ポーズは既定で含める。
「ポーズを確認・調整」で結果と未対応理由を確認し、除外・名称変更・複数.animの追加・時刻指定・プレビューが可能。

## 適用

アプリは通常撮影とARで同じカタログと選択処理を使用する。内蔵ポーズは維持する。
control rigへ体・手足・指を適用し、首・頭・表情・視線の追跡を後から適用する。
UniVRMの制約・表情・SpringBoneはその後に処理する。解除・モデル変更・モード変更で基準姿勢へ戻す。
ARは立位の基準身長でサイズを維持する。座ると見かけの高さは下がる。
腰差分の適用後に足・つま先の骨の最低点を基準高さへ合わせ、ARアンカーを動かさない。
靴底のメッシュ形状による接地誤差は残り、椅子との接触や任意の床面へのIKは行わない。

## 検証境界

Unity EditModeの対象: 登録収集、SDKなし手動、条件解決、マスク/重み/override、非変更、出力/読込/適用。
iPhone: 指の形・座り/しゃがみ・ARサイズ/接地・追跡/視線/表情・髪・動画/2秒ログを別途実機確認する。
ホスト検査・Unity合格をiPhone実機合格とは扱わない。実行記録は変更報告に記載する。

両リポジトリの `Tools/run-pose-contract-tests.ps1` は同一仕様の振る舞いを確認する。
エクスポーターの `HumanoidPoseTests` に `VRVLOG_POSE_FIXTURE` 出力先を与えると、腕・指・座りを含む
合成VRMと、拡張なし/未対応版の派生ファイルを生成する。アプリの `HumanoidPoseImportTests` は同変数、
またはコミットされた合成fixtureで実際のLoader・control rigへの適用と復元を検証する。
APLの登録テストは導入済みの実Runtime型を使用する。APL生成プラグイン自体の完全な動作確認ではない。
