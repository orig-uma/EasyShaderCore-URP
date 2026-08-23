# Changelog

## [Unreleased]

## [0.3.1] - 2026-08-24

### Changed

- README の「含まれるもの」を実態に合わせた: `BlackOutController` / `FeatureSetup*`（Renderer Feature 追加ウィンドウの基底と未導入ガード）/ `MaterialReplacerWindow` を追記し、Face SDF ベイカーが 4ch（Doll）と 16bit 1ch（Idol）の両形式・距離場ブレンド・落ち影レイキャストに対応すること（仕様 `FACE_SDF_BAKING.md`）を明記。コードの変更は無い。

## [0.3.0] - 2026-08-23

### Changed

- **VogelDisk に位相回転オーバーロード `VogelDisk(int, int, float2 sincosPhi)` を追加し、`Shadow_HQ_URP.hlsl`（PCF 本体とブロッカー探索）をそちらへ移行**（EasyToon Idol の `ToonVogelDisk` からの逆輸入。T-340）。従来の float 位相版は θ に phi を含むため UNROLL 展開後もタップごとに実行時 `sincos` が残っていた。回転版は呼び出し側で `sincos(phi)` を 1 回だけ行い、ループ内の `sincos(i·黄金角)` は i が定数のためコンパイル時に畳まれる。加法定理そのままの変形で**数値は完全等価**（Idol 側で全 24 タップ × 5 位相・最大差 2.8e-15 の検証記録）。実測（fxc / D3D11・EasyPBR Doll ForwardLit フラグメント）: Vogel PCF **1,412 → 1,404 命令**、PCSS **1,474 → 1,463 命令**。float 位相版も互換のため残置（展開ループでは float2 版を使う旨をコメントに明記）。
- **`RgbToHsv` / `HsvToRgb` を `EasyPBR_RgbToHsv` / `EasyPBR_HsvToRgb` に改名し、`+e` の下駄形 → `max(x, e)` の下限形・half → float に変更**（EasyToon Idol 実装への引き上げ。T-340）。下駄は分母を常にずらして結果へ一様に混入する上、**half では 1e-10 がアンダーフローして実質 0**（half の最小正規値 ≈ 6.1e-5）となり、無彩色でゼロ除算になりうる。改名は必須だった ── float 化すると **URP 本体（Color.hlsl）の `float3 RgbToHsv` と完全一致で衝突**する（half3 だった頃はオーバーロードとして共存できていた）。外部の直接呼び出しは 3 パッケージとも 0 件（`ApplyColorCorrection` 経由のみ）で互換影響なし。実測では下限形の守りに **+2 命令**の対価（EasyToon Cel の色補正を含む組）── ゼロ除算防止と引き換えで許容。VogelDisk 側の削減（Cel キャラ影込みの組で **2,280 → 2,265 命令**）が同時に効くため総和では減少。
- **Dissolve のランタイム対象判定を「`_DISSOLVE_ON` を宣言しているシェーダーのみ要求」へ緩和**（`DissolveController.IsDissolveTarget` / `DissolveEdgeSampler.Dispatch`）。EasyToon の Idol はバリアント倍増を避けるため Dissolve を**意図的にキーワードレス**（`_DissolveAmount > 0` の一様分岐）で実装しており、宣言の無いシェーダーでは `IsKeywordEnabled` が常に false を返すため、旧判定では Idol マテリアルが**無言で対象 0 件**になっていた。`shader.keywordSpace.FindKeyword` の有効性で宣言の有無を見分け、宣言があるシェーダー（EasyPBR の Doll / EasyToon の Cel）は従来どおり「キーワードが有効なものだけ」を対象とする＝**挙動不変**。これで Idol でも `DissolveController` / `DissolveSwapController` / Timeline トラック / `DissolveEdgeSampler`（VFX 連携）がそのまま使える。

### Fixed

- **頂点値の平滑化（`SmoothVertexScalar`）を位置溶接したグラフ上で行うようにし、UV 継ぎ目に段差が焼き込まれていたのを修正**（T-372 / T-373）。Unity は UV 継ぎ目で頂点を複製するため、素の三角形隣接で平均すると継ぎ目の左のコピーは左側の隣接とだけ、右のコピーは右側の隣接とだけ平均され、**同じ点だった 2 つの値が勾配に比例して割れる**。顔 SDF では UV 中央（ミラーの継ぎ目）に額で 0.05・鼻で 0.16 の段差が入り（周囲の勾配の 30〜50 倍）、光が 80〜90 度のとき額から顎までの硬い割線として出ていた。**原因は入力（法線は連続だった）ではなく既定 1 回の平滑化**。位置溶接を `EasyPbrBakeCore.WeldByPosition` / `WeldedNormals` として共有ヘルパ化し（ShadeNormal と共用。T-107）、平滑化はグループ単位で行う。Face SDF の掃引法線も溶接済みの法線を使う（硬エッジで分割されたメッシュへの保険）。AO / Cavity / Thickness など `smooth` を使う全ベイカーの継ぎ目の筋も同根で直る。**継ぎ目のあるメッシュは要再ベイク（見た目は継ぎ目の段差が消える方向にのみ変わる）。**
- **Face SDF の Cast Shadow が、統合メッシュ内の別マテリアル（睫毛・眉など）を遮蔽物として拾い、目の周りに恒久影を焼き込んでいたのを修正**（T-355）。遮蔽コライダーはメッシュ全体から作られていたため、顔の頂点からのレイが浮いている睫毛・眉に当たり「正面からでも影」と判定されていた。`RunBake` に `occluderSubmeshesOnly`（遮蔽物を編集中マテリアルのサブメッシュに限定）を追加し、Face SDF の全経路で有効化。鼻・唇など同マテリアル内の落ち影は従来どおり焼ける。AO / Thickness 等の他 Baker は従来どおり全体を遮蔽物にする（挙動不変）。**Cast Shadow ON で焼いた Face SDF は要再ベイク。**

### Added

- **`BlackOutController` を新設**（`Runtime/Scripts/BlackOutController.cs`、Add Component > Origuma/EasyShaderCore/Black Out Controller。T-364）。キャラのルートに 1 つ付けて `amount` を書けば、配下マテリアルの `_BlackOut` へ一括反映される。**`_BlackOut` は EasyPBR の Doll と EasyToon の Idol で同名・同義**（最終色を黒へ lerp）なので、シェーダーを問わずプロパティの有無だけで対象を拾う ── Dissolve を `DissolveController` へ一本化したのと同じ考え方。書き込みは既存の作法どおり **Play = マテリアルインスタンス（SRP Batcher 維持）/ Edit = 非破壊 MPB プレビュー**、OnDisable(Play) で元値復元、Edit の解除は自分が当てた Renderer のみ。`amount` は public フィールドなので **Timeline の Animation Track から直接キーを打てる**（専用トラックは持たない）。暗転は動かない時間の方が長いので前回値スキップを入れてある。**注意**: `DollLiveDirector` も `_BlackOut` を上書きできるため、同じキャラで両方を有効にすると書き込み合戦になる（新規は本 Controller を推奨）。
- **顔 SDF ベイク手法の完全仕様書を追加**（`Documentation~/FACE_SDF_BAKING.md`。T-357）。距離場ブレンド + 16bit パッキングの規約（角度写像・格納の反転・RG 線形デコード）・全段のアルゴリズム（頂点スイープ / 等値線ごとの chamfer 符号付き距離 / **適応ランプ幅**による連続再構成）・実際に出荷した 3 つの不具合を規範（MUST / MUST NOT）として明文化・再現実装のための検証チェックリスト付き。この文書単体で再実装できることを目標にした。先行手法（マスク→距離場→ブレンド。alwei 方式）との関係と本手法の寄与 4 点（マスクスタック不要の定式化・適応幅・遮蔽のサブメッシュ限定・16bit 線形パイプライン）も正直に記載。lilToon 等の外部シェーダーでの利用手順を含む。
- **Face SDF ベイクに距離場ブレンド整形と 16bit 1ch 出力を追加**（`Editor/Baking/EasyPbrFaceSdfBaker.cs`、`Settings.dfBlend` / `dfSpread` / `pack16`。T-346）: 頂点スイープの生の出力は「頂点法線 → 重心座標補間」なので、影境界の等値線にポリゴン割りと法線ノイズがそのまま出て線がガタつく。手描き SDF ツールの本質工程（**白黒マスク → 距離場変換 → ブレンド**）を画像空間で内蔵し、64 本の等値線それぞれについて 2 値マスクの内外 chamfer 距離（3-4 近似・2 パス）から符号付き距離を取り、0..1 に緩和したランプを平均する。**ランプ幅は固定でなく「隣の等値線までの局所間隔」（下限 = `dfSpread`）** ── 固定幅だと等値線の間の平坦部で全ランプが飽和し、出力が 1/64 刻みに量子化される（ライトを回すと影の線が等値線ごとに引っかかり、16bit 出力も無意味になる）。間隔いっぱいのランプは隣同士がちょうど連結する区分線形の連続再構成になり、平坦部は元の値が保存される。`dfSpread` は線の形を丸める半径の下限としてだけ効く。等値線の形が texel 距離の幾何で決まるため頂点補間由来のガタつきが丸まり、**外部ツール無しで滑らかな線**が焼ける（`dfBlend` 既定 ON。従来出力は OFF で焼ける）。距離伝播が被覆外 texel にも自然な外挿値を入れるためダイレートは不要になり、ブラーは float 域の分離ボックスで掛ける。`pack16` は右光スイープだけを **R×256+G の 16bit** で焼く 1 ch モード（ミラー U 規約の 1ch 経路用・左右対称の顔向け）。格納値は **lilToon 規約（白 = 最後まで照らされる側）＝内部規約の反転**（ランタイム 1ch 経路は lit ⇔ sdf > 1−(F·L·0.5+0.5) で読むため。初版は反転が抜けており、すぐ陰る顎下〜首が「永遠に照らされる」と誤読され影が入らなかった ── 要再ベイク）。デコードが RG に線形なのでバイリニア補間・ブラーを通しても値が壊れず、8bit 単チャンネルの約 0.7 度刻みの閾値階段が消える。保存は既存の `SaveAndAssign` がそのまま非圧縮・sRGB OFF を保証。`EasyPbrBakeCore.RunBake` にはラスタライズ直後の画像加工フック `postProcess` を追加（他 Baker は不使用・挙動不変）。
- **Face SDF ベイクに X Axis Tilt（左右スイープ光の仰角）を追加**（`Editor/Baking/EasyPbrFaceSdfBaker.cs`、`Settings.xAxisTilt`・度）: R/G（左右）チャンネルのスイープ面を顔 Up 方向へ `xAxisTilt` 度だけ倒し、「やや上から差す光」を前提に境界角度を焼く。水平スイープのままだと顎下〜首の境界が実際のライト（通常は上方から）とずれ、モデルによっては首まわりの影が不自然になるため。左右どちらの軸も同じ「上」へ倒すので左右対称は保たれ、傾けた軸は Forward と直交のまま＝ライトベクトルは単位長、格納値（`cosθ*0.5+0.5` = frontness）の意味も不変で**ランタイム/シェーダー側の変更は不要**。B/A（上下）チャンネルは対象外。既定 0 = 従来と同一出力。
- **DissolveController に invert（`_DissolveInvert`）制御を追加**（`Runtime/Scripts/Dissolve/DissolveController.cs`）: `amount` に加えて `invert`（bool）で消える／現れるの向きも制御。Play=マテリアルインスタンス（元値を `origInvert` に控えて OnDisable で復元）/ Edit=MPB プレビューの双方で `_DissolveInvert`(0/1) を反映。変更検知は amount・invert の両方を見る。一括設定 API `Set(float amount, bool invert)` を追加（Swap から使用）。
- **DissolveSwapController を新設**（`Runtime/Scripts/Dissolve/DissolveSwapController.cs`）: 2 キャラの入れ替わり演出（A 消失=invert:false／B 出現=invert:true）。**マテリアルには一切触れず**、A/B の `DissolveController.Set()` に `amount` を渡すだけ（一本化方針の徹底）。`amount`(0..1) は public フィールドで Animation Track 直キー可。`duration` 秒の自動再生 `PlayToB()` / `PlayToA()` / `Play(target)`（Play モード限定）。実行順は `[DefaultExecutionOrder(-10)]` で Controller の LateUpdate より先に `amount` を確定。
- **旧実装（`Assets/Dissolve`）からの移行ガイドを追加**（`Documentation~/VFX_DISSOLVE.md`「5. 旧実装からの移行」）: 旧 C#（`DissolveSwapController` / `DissolveParticleController`）・旧 compute の新実装への置き換え表と、旧 VFX（`DissolveEffect.vfx` / `MeshDissolve.vfx`）の Exposed プロパティ繋ぎ替え（`EmitBuffer`→`DissolveEdgePoints` / `EmitCount`→`DissolveEdgeCount`、Sample Buffer を `DissolveEdgePoint` 型へ）。
- **Dissolve の統括制御コンポーネントと Timeline トラックを追加**（`Runtime/Scripts/Dissolve/DissolveController.cs` / `Runtime/Scripts/Dissolve/DissolveTimeline.cs`）:
  - `DissolveController`（司令塔）: `amount`(0..1) を書けば、配下 Renderer の対象マテリアル `_DissolveAmount` と同居する `DissolveEdgeSampler` の双方へ一括反映。対象は `_DissolveAmount` を持ち**かつキーワード `_DISSOLVE_ON` が有効**なマテリアルのみ自動収集（無効なマテリアルには触れない）
  - マテリアル書き込みは EasyPBR の `DollLiveDirector` 方式を踏襲: Play 中はマテリアルインスタンス経由（SRP Batcher 維持・`MaterialPropertyBlock` 不使用）、Edit 中は `MaterialPropertyBlock` で非破壊プレビュー、OnDisable(Play) で元値復元
  - Play 中にマテリアルをインスタンス化した直後、Sampler の参照を貼り直すため `DissolveEdgeSampler.Reinitialize()`（既存 `Initialize()` の public ラッパ）を追加・呼び出し。以後 Sampler は amount に自動追従（Edit 中の MPB プレビューは Sampler へ非反映＝v1 制約）
  - Timeline トラック `DissolveTrack` / `DissolveClip`（正規化時間 0..1 → amount のカーブ・ClipCaps = Blending | Extrapolation）/ `DissolveMixerBehaviour`（全クリップの重み付き合成を `SetAmount`。totalWeight≈0 のフレームは書き込まずシーン値を尊重）。`com.unity.timeline` は versionDefine `ORIGUMA_TIMELINE` による**任意依存**（不在時はトラックコードが `#if` で消え、Controller 単体で動作）
  - asmdef（`Origuma.EasyShaderCore.Runtime.asmdef`）に references `Unity.Timeline` と versionDefine `ORIGUMA_TIMELINE` を追加

- **UrpShadowSetupWindow を追加**（`Editor/UrpShadowSetupWindow.cs`）。メニューは `Window > Origuma > URP Shadow Setup`（namespace は `Origuma.EasyShaderCore.Editor`）:
  - QualitySettings 全レベル + GraphicsSettings 既定から `UniversalRenderPipelineAsset` を収集し、使用品質レベルをラベル併記（★=アクティブ品質レベル）
  - Shadow 設定のプリセット 5 種（Unity 既定 / 低 / 中 / 高 / キャラ重視）をテーブル駆動で定義。選択アセットへ一括適用（現在値→適用後のプレビュー表・変更行の強調付き）
  - 書き込みは `SerializedObject` 経由で `m_*` フィールドを直接操作（`m_AnyShadowsSupported` の連動整合を維持、Undo は自動記録、URP バージョン差で欠けるフィールドはスキップして警告）
- **Dissolve エッジ点群の GPU 抽出 + VFX Graph 連携を追加**（`Runtime/Scripts/Vfx/DissolveEdgeSampler.cs` / `Runtime/Shaders/Vfx/DissolveEdgeSample.compute` / `Runtime/Scripts/Origuma.EasyShaderCore.Runtime.asmdef`）:
  - スキン済み頂点バッファ（`SkinnedMeshRenderer.GetVertexBuffer()` / `Mesh.GetVertexBuffer`）を GPU で直接読み、`Fx_Dissolve.hlsl` と同一のフィールド式でエッジ帯（`0 <= clipVal <= edgeWidth`）の頂点だけを `AppendStructuredBuffer` へ抽出。CPU readback なし（件数は `GraphicsBuffer.CopyCount`）
  - `DissolveEdgePoint`（`positionWS` / `normalWS` / `edgeFactor`、32 bytes）を `[VFXType(Usage.GraphicsBuffer)]` として公開し、`VisualEffect` へ毎フレーム `DissolveEdgePoints` / `DissolveEdgeCount` / `DissolveAmount` / `DissolveEdgeColor` をバインド（プロパティ存在時のみ）
  - `com.unity.visualeffectgraph` は versionDefine `ORIGUMA_VFXGRAPH` による**任意依存**。パッケージ不在でもバッファ抽出部はコンパイル・動作する
  - 間引きストライド対応・64 スレッド/グループ・非ディゾルブ時は Dispatch 完全スキップ。VFX 側の組み方は `Documentation~/VFX_DISSOLVE.md`

## [0.2.0]

- **MaterialReplacerWindow を EasyPBR から移管**（`Editor/MaterialReplacerWindow.cs`）。メニューはシェーダー非依存の `Window > Origuma > Material Replacer` へ変更（namespace は `Origuma.EasyShaderCore.Editor`）
- **FeatureSetup 基盤を追加**（`Editor/FeatureSetup.cs` / `Editor/FeatureSetupWindowBase.cs`）:
  - アクティブな URP Asset（GraphicsSettings 既定 + QualitySettings 全レベル）からの Renderer Data 自動収集
  - RendererFeature の検索 / 追加（サブアセット化 + `m_RendererFeatureMap` 同期 + Undo）/ 削除 / 有効切替
  - Render Graph Compatibility Mode 判定、ShaderGUI 用の未追加ガード描画（`DrawFeatureGuard<T>`）
  - セットアップウィンドウ汎用基底 `FeatureSetupWindowBase`（サブクラスはタイトルと Feature エントリ宣言のみ）

## [0.1.0]

- 初回リリース。EasyPBR 0.5.0 から以下を移管:
  - `Runtime/Shaders/Common/**`（BRDF / Effects / URP / 色・数学・サンプリングの純粋関数 HLSL）
  - `Editor/Baking/**`（EasyPbr*Baker 群 + EasyPbrBakeCore。`internal` → `public`、名前空間を `Origuma.EasyShaderCore.Editor` へ変更。クラス名は互換のため変更なし）
  - `Editor/ShaderGuiKit.cs`（名前空間変更のみ）
- .meta（GUID）は移管元のまま維持（テクスチャ・マテリアル参照は壊れない）
