# VR Vlog lilToon VRM Exporter

Unity Editor package for exporting a VRM 1.0 file with two material
representations:

- standard `VRMC_materials_mtoon` fallback;
- optional, versioned VR Vlog lilToon material data.

The extension is never placed in `extensionsRequired`. Viewers that do not
understand it must remain able to render the MToon fallback.

## Install with VCC or ALCOM

Add the VR Vlog repository and install only **VR Vlog lilToon VRM Exporter**:

`https://mitsuya0077.github.io/VR-Vlog-lilToon-Exporter/index.json`

The VPM dependency resolver installs the tested `com.vrmc.gltf` and
`com.vrmc.vrm` 0.131.x packages automatically. The user does not need to find
or select the UniVRM packages separately. The package manager may show them in
the confirmation screen before installation.

## Workflow

Only active objects with enabled renderers are exported, matching UniVRM's
material selection. Hidden wardrobe objects may remain in the avatar; they are
not converted or included in lilToon compatibility data. Enable the outfit you
want before exporting. The avatar root and its parents must be active. Export
never changes visibility, and missing materials on visible renderers still fail
instead of being silently omitted. This also applies to the existing-fallback
workflow, where the supplied VRM must match the currently enabled outfit.

1. Open **VR Vlog > lilToon VRM 1.0を書き出す**.
2. In **① アバター（必須）**, select the top-level avatar object from the
   Hierarchy. In **② 作者名（必須）**, enter the author name stored in the VRM.
   Those are the only required fields; the VRM name is taken from the selected
   object automatically.
3. Press **③ 保存先を選んでVRMを書き出す**, then choose the destination in the
   save dialog. The exporter creates a temporary cloned avatar,
   maps supported lilToon materials to a portable MToon10 fallback, exports VRM
   1.0 through the public UniVRM API, injects the optional lilToon extension,
   validates the complete GLB, and atomically commits one output file.

Missing standard blink and mouth presets are populated from a conservative list
of known morph names in the exported mesh (`eye_close[_left/_right]`, VRM/ARKit
blink names, VRoid names, and VRChat vowel visemes). Existing VRM presets and
custom expressions are preserved, including explicitly empty presets. Names are
matched exactly, ignoring case; ambiguous duplicate names are skipped with a
warning. The exporter reports generated presets and warns when it cannot
configure blinking. Verify expression movement on the target model.

Under **書き出し設定**, **目などの白飛びを抑える** defaults to ON. This mobile
appearance option omits emission only when the same Unity texture object is
assigned to both the base image and emission image. It affects both the ordinary
MToon fallback and lilToon compatibility data and reports each affected material.
It does not depend on material/texture names, does not suppress separate emission
maps, and can be disabled to retain intentional whole-image glow. It is an
explicit approximation, not a complete conversion of lilToon's emission blending.

The one-click workflow preserves the current renderer **BlendShapes** values as
the exported base face/body shape. Morphs then move from that customized base
toward their existing final-frame endpoint, so an already half-closed eye does
not receive its initial closing amount twice. Other shape customizations remain
in place while expressions animate. Shared meshes are copied per renderer, and
authored VRM expression bindings retain their names and indices. Starting in 0.6.0,
supported VRChat menu expressions are imported as composed custom VRM expressions,
as described below.

Multi-frame shapes are evaluated at the current initial weight; animation keeps
UniVRM's single final-frame target approximation. A nonzero default on a shape
with a zero-weight frame stops with an explicit error. The advanced existing-VRM
material-injection workflow does not change geometry; re-export from Unity to
recover a face whose initial values were absent in an older VRM.

The source avatar, meshes, materials, textures, and importer settings are never
modified. The old existing-fallback workflow remains under **上級者向け：既存のVRM
1.0へlilToonデータを追加**.

Most extension textures reference the already optimized texture indices produced
by UniVRM. A custom backlight color texture is instead copied into the extension
as a PNG so it cannot be confused with a same-named fallback texture. This may
increase the VRM file size. Resize it beforehand when needed (1024 px recommended,
2048 px maximum for the mobile profile).

## Supported material subset

Main color, shadow, backlight color/settings/texture, normal map, emission, rim
light, matcap, and outline are preserved. Backlight uses an MToon rim-light
approximation in fallback viewers. Optional variants such as FakeShadow and
unsupported effects such as fur, refraction, gem, tessellation, and AudioLink
are reduced to the closest standard lilToon/MToon representation. Any omitted
details are listed as warnings after a successful export. Ambiguous texture
names are resolved by embedding the exact source texture only for those cases.
Invalid VRM structure, ambiguous material matching, corrupt image data, and mobile safety-limit
violations still stop the export.

The portable MToon fallback converts lilToon's outline width to metres at
one-hundredth scale. `_OutlineTex` is retained for lilToon restoration but is
not reused as MToon's unrelated green-channel outline-width mask.
When lilToon shadows are disabled, the base image is also bound as MToon's shade
image, so shaded regions retain their colors instead of turning white. If
shadows are enabled but no shade texture is assigned, the base image is reused.

## Development checks

- `python Tools/validate.py`: package, schema, and source integration checks.
- `pwsh -File Tools/run-behavior-tests.ps1`: compiles and executes production
  base-shape geometry, expression, material-reader, and injection code with synthetic inputs. The
  property-bag test doubles do not simulate Unity hierarchy or rendering; GPU
  operations throw if reached.
- Unity Editor tests under `Tests/Editor`: material fallback, emission opt-out,
  outline conversion, menu traversal, actual Animator graph evaluation (discrete
  states and BlendTrees), and composed expression baking. These require a
  dependency-complete Unity project; non-Unity checks cannot replace them.

## Install during development

Use VCC/ALCOM for a dependency-complete installation. Local package development
requires the matching UniVRM packages to already be present.

## Safety rules

- Never modify source materials, textures, or importer settings.
- Export only temporary copies.
- Always emit an MToon fallback.
- Reject unknown schema majors and invalid non-lilToon shader data.
- Bound material counts, texture counts, dimensions, and expanded memory.
- Do not claim pixel-identical output across render pipelines or devices.

## Compatibility

- Unity 2022.3 or later. VRChat creators should use VRChat's currently
  supported editor (2022.3.22f1 at the time of this release); later editor
  versions are source-compatible but are not a substitute for VRChat's
  required upload version.
- UniVRM 0.131.x (`com.vrmc.gltf` and `com.vrmc.vrm`)
- lilToon materials whose shader names identify lilToon, Lite, or Multi
- One-click export uses UniVRM's public `Vrm10Exporter.Export` API. New UniVRM
  minor series must be tested and released explicitly rather than accepted
  silently.

### 0.5.1: 光沢・口まわりの輪郭線

MatCapの色のアルファと合成の強さをMToonへ反映します。角度で光沢が出る効果は残りますが、
元の強さを無視して光を加算する不具合を修正しています。lilToonとMToonの合成方式の差は残ります。
輪郭線の太さマスクは赤チャンネルからMToonの緑チャンネルへ変換して保存します。
頂点カラーで輪郭の太さを調整している材質は、口や目への輪郭線の突き抜けを避けるため
輪郭を省略し、書き出し時に警告します。元のUnityマテリアルや画像は変更しません。

以前のVRMには太さ制御が保存されていないため、元のUnityアバターから再出力してください。
修正版アプリの互換表示では、既知の旧版（0.3.8、0.4.0、0.4.1、0.5.0）の輪郭を抑制します。
`vrc.v_aa`等のアンダースコア形式の口のBlendShapeもVRMの母音へ自動設定します。

### 0.6.0: VRChatメニューの表情

元のUnityアバターから書き出すと、表情を自動で読み込みます。
書き出すたびにメニューとアニメーションの登録を読み直すため、画面を開き直す必要は
ありません。表情を取り込むチェック、候補ごとの選択、確認・再読み込みボタンはありません。

Avatar Descriptorに設定されたExpressions MenuのButton・Toggleをサブメニューまで
たどり、メニューの名前、パラメーター値、サブメニューの条件とExpression Parametersの
初期値をカスタムFX Animatorに適用します。固定のAnimationClipとBlendTreeからなる
BlendShape表情を、メニューの一項目につき一つのVRMカスタム表情にします。
個々のBlendShape名を表情リストに並べる機能ではありません。SDKへのコンパイル依存は
追加せず、VRChat SDKがあるプロジェクトの登録データを読み取ります。

Unityの一時的なPreview Sceneで状態遷移を進め、2秒後に静止した表情を読み取ります。
過去の状態から残ったBlendShape値も含めて読み取り、有効な状態の時間による遷移と
曲線全体を検査します。数秒後にだけ変わる曲線も固定表情としては取り込みません。
複数Rendererの目・眉・口などを一つの表情にまとめ、調整済みの基本の顔からの差分を
保存します。アニメーションで指定されていないBlendShapeの調整はそのまま残ります。
初期値より小さい値や複数フレームの形状も、元のメッシュから評価して差分を計算します。
元のアバター・アセット・現在のBlendShape値は編集しません。

VR Vlogでは「表情」から `VRChat / 表情 / 笑顔` などを選べます。選択中は表情の形を
守るためVRMの瞬き・口・視線の自動変形をブロックし、「デフォルト」で通常の追従に
戻ります。既存のVRM表情を上書きせず、同名項目には番号を付けます。

**変換できない項目は理由を表示します。** 連続操作のPuppet、時間で変わり続ける表情、
非表示のRendererを使う項目、マテリアル差し替え・表示切り替え・ボーン変形を含む項目は
固定BlendShape表情として取り込みません。Parameter Driver・Layer Controlなど、FXに
VRChat固有の状態処理がある場合も、実際と異なる顔を出さないため自動変換しません。
Tracking Controlのみの状態処理は許容し、出力表情の追従ブロックで扱います。
同期Animatorレイヤーは未対応です。Modular Avatar等がビルド時に生成するメニューや
コントローラーを、この書き出し機能で生成することはありません。Descriptorへ登録済みの
データを対象にします。Gestureレイヤーだけに実装された表情もFX取り込みの対象外です。

メニューは256項目、追加する表情の頂点差分は128 MiBまでです。上限を超える場合は
エラーにし、途中までのVRMは保存しません。既存VRMには元のVRChatメニューやアニメーションの対応が
入っていないため、VRMだけから正確な表情の組み合わせを復元することはできません。
Unityの元プロジェクトで再出力し、メニューと同じ顔になることを確認してください。

### 0.6.1: ジェスチャーの表情と短い完了表示

ジェスチャーの表情も毎回自動で取り込みます。FX Controllerで
`GestureLeft` / `GestureRight` の条件から遷移する状態に登録された、固定の顔の
AnimationClipを取り込みます。アニメーション内の目・眉・口などのBlendShape値を
組み合わせたまま、`VRChat / ジェスチャー / アニメーション名` として保存します。
同じアニメーションの重複登録はまとめ、AnimatorOverrideControllerの差し替えも読みます。
メニューのCustom Expressionsが無効でも、このジェスチャー取り込みは動作します。

この経路は**登録アニメーション単体を調整済みの基本の顔に適用した固定表情**です。
無関係な自動瞬きやParameter DriverがFX全体にあっても、アニメーション自体の値を
読み取れます。一方、別レイヤーとの合成・以前の状態から残る値・Driverによる変化は
再現しません。ジェスチャーのBlendTreeを個々の子アニメーションへ分解することも
ありません。0.7.0では時間変化するBlendShapeの曲線も動きとして保存します。
材質・表示・ボーンを同時に変更するクリップは、この経路では引き続き省略します。
元の顔が複数レイヤーやDriverの合成で作られる場合は、その固定顔を一つにまとめた
表情アニメーションが必要です。ビルド時生成のControllerは引き続き対象外です。

完了ダイアログは保存結果とVRChat表情件数だけを表示し、
長い省略一覧は表示しません。必要な診断情報はUnity Consoleの「VR Vlog 書き出し詳細」
にまとまります。VRMに選択用の表情がない場合、アプリに表情ボタンは表示されません。

### 0.7.0: 動く表情と自動取り込み

`kipfel_facial_cry` など、時間でBlendShape値が変わるジェスチャーの表情も取り込みます。
アニメーションの名前を一項目として保存し、対応版のVR Vlogで選ぶと動きを再生します。
元のキーフレーム、重み付き接線、段階的な切り替え、曲線の折り返しとクリップのループ設定を
保存します。ループしないクリップは終端の顔で止まり、「デフォルト」や別の表情を選ぶと
前の動きを解除します。動きの途中のフレームを個別の表情としてリストに追加しません。

**動きの再生には対応版のアプリが必要です。** 通常のVRM表情として先頭の顔も保存するため、
拡張に未対応のビューアーでは静止した先頭の顔を選べます。0.6.1以前に除外された動きは
VRMに入っていないため、Unityの元アバターから再出力してください。

取り込み設定と表情の一覧・再読み込みボタンはありません。書き出すたびに登録データを
自動取得します。省略した衣装や未対応の処理の長い一覧は、完了ウィンドウへ表示しません。
詳細な保存仕様と検証境界は [ExpressionAnimations.md](Schema/ExpressionAnimations.md) にあります。
