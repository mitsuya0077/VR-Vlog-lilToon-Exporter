# PhysBoneからの揺れ物変換

VRChat SDKが導入済みの場合、元モデルのPhysBoneを読み、MA / NDMF処理後の
一時コピーにVRM 1.0 SpringBoneを作成します。元シーン・Prefab・アセットは
変更しません。SDKがない場合も通常のVRM出力は利用できます。
髪の名前から設定を推測しません。揺れ設定のない既存VRMには、元のUnityモデルからの
再出力が必要です。この実装は未リリースです。

## 保存する内容

- Root Transform、Ignore Transformsとその子孫、Ignore Other PhysBones、
  有効状態、子ボーン、Endpoint Positionを参照します。
- 分岐のIgnoreでは分岐元を固定し、Firstでは最初の子へ向け、
  Averageでは子の平均位置に仮想末端を作ります。各子の連鎖を別途保持します。
  同一ボーンを複数PhysBoneが駆動する場合は、重複を解消する案内を出して停止します。
- 既存のVRM SpringBoneと重なる連鎖は既存設定を優先し、理由を表示します。
- 各ボーンの階層深度 / 最大深度で設定カーブを評価します。
  各値とカーブの積を使い、長い分岐の末端を深度1とします。
- Sphere / Capsule / Planeと内側判定、位置・回転・半径を変換します。
  Capsuleの長さは半径を含む全高から両端の中心を求めます。
- Cone / Hinge / Polarの角度と回転を、UniVRMのCone / Hinge / Sphericalへ写します。
  UniVRM既存のVRMC_springBone_limit・VRMC_springBone_extended_colliderを使用します。
  独自拡張は追加しません。これらを扱わないビューアーでは制限・特殊衝突は再現されません。

## 近似と未対応

PhysBoneとSpringBoneは別のソルバーです。見た目が一致する保証はありません。
カーブ評価後のPullをp、Spring / Momentumをs、Gravityをg、
Gravity Falloffをfとすると、stiffness = 4p、dragForce = 0.6(1-s)、
gravityPower = |g|(1-f)です。PhysBone 1.1ではgravityPowerに4pを掛けます。
重力の符号は上下方向に反映します。半径は元値を使います。

Advancedの独立したStiffness、Immobile、伸縮、動的なIs Animated追従、
Grab / Pose、他アバターとの衝突、PhysBoneパラメーターの連動は保存しません。
Immobileはアプリの安定化設定に委ね、Is Animatedは出力時の姿勢を基準とします。
PolarのZ角度はUniVRMの対応範囲0〜90度に制限します。
角度制限もソルバーの差を含む近似です。変換結果の「詳細を見る」で各警告を確認できます。

変換件数・連鎖数・動く関節数（末端を除く）・コライダー数・省略数を表示します。
無効、除外、連鎖なし、既存VRM優先の各理由を残します。未知の種類、非有限値、
アバター外参照、重複駆動、長さゼロは保存前にエラーとします。
変換済みの連鎖・関節・コライダーが出力から欠落した場合も保存を止めます。

## 検証

Tests/Editor/PhysBoneSpringExportTests.csは、導入された実際のSDKを使って
分岐・末端・カーブ・コライダー・除外・既存設定・元モデル非変更を検証します。
標準出力とlilToon出力をUniVRMで再読込し、頭側の回転後に髪が遅れて動き、
静止後に収束することを測定します。MA Bone Proxy処理後の経路も含みます。
SDK / MA不在時は対応する統合テストのみスキップします。
実機の見た目と保存動画の確認はUnityテストと別に行う必要があります。

参照: [PhysBone](https://creators.vrchat.com/common-components/physbones/)、
[VRM SpringBone](https://github.com/vrm-c/vrm-specification/blob/master/specification/VRMC_springBone-1.0/README.md)
