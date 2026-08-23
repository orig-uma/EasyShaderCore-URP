# Dissolve エッジ点群 → VFX Graph 連携

Dissolve（消失）の「エッジ帯（最前線）」に乗るスキン済み頂点を GPU で抽出し、
その**ワールド座標点群**を VFX Graph のパーティクル発生源として使うための手順です。

抽出は `DissolveEdgeSampler`（`Runtime/Scripts/Vfx/DissolveEdgeSampler.cs`）+
`DissolveEdgeSample.compute` が担当し、CPU readback を一切行いません（件数は
`GraphicsBuffer.CopyCount` で GPU 上の 4byte バッファへコピー）。

`.vfx` アセットはコードから生成できないため、VFX Graph 側は本書に従って手動で組みます。

---

## 1. コンポーネントの配置

推奨構成は、キャラのルートに **`Dissolve Controller`（司令塔）+ `Dissolve Edge Sampler` +
`Visual Effect`** の 3 点。Controller に `amount` を書けば、配下マテリアルの
`_DissolveAmount` と Sampler の両方へ一括反映されます。

1. キャラクターのルート GameObject に **`Dissolve Edge Sampler`** を追加
   （`Add Component > Origuma > EasyShaderCore > Dissolve Edge Sampler`）。
2. `Edge Compute` に `DissolveEdgeSample.compute` が自動アサインされていることを確認
   （未アサインなら `Packages/com.origuma.easyshader-core/Runtime/Shaders/Vfx/DissolveEdgeSample.compute` を手動指定）。
3. `Sample Stride`（既定 4）で点の密度を、`Max Points`（既定 65536）で上限を調整。
4. `Manual Renderers` を空にすると配下から Dissolve マテリアルを自動収集します。
   特定 Renderer だけを対象にしたい場合はここへ登録。
5. `Targets` に、点群を受け取る `Visual Effect` を登録。

`_DissolveAmount` が 0 以下・1 以上、または Renderer 非アクティブのときは抽出を
スキップします（点は 0 件）。

### Dissolve Controller（司令塔）の使い方

1. 同じルート GameObject に **`Dissolve Controller`** を追加
   （`Add Component > Origuma > EasyShaderCore > Dissolve Controller`）。
2. 対象マテリアルは、配下 Renderer のうち `_DissolveAmount` を持ち、**かつ
   キーワード `_DISSOLVE_ON`（Enable Dissolve）が有効**なものだけを自動収集します。
   マテリアル側で Enable Dissolve を ON・Amount 0 にしておくのが想定フロー。
   `Manual Renderers` で対象を限定することも可能。
3. `Edge Sampler` は空欄なら同 GameObject / 配下から自動取得します（Sampler が無くても
   マテリアル制御だけで動作）。
4. `amount`（0..1）を動かすだけで消失が進みます。スクリプトからは `SetAmount(float)`。
   Inspector 下部に現在の対象マテリアル数が表示されます。

- **Play 中**はマテリアルインスタンス経由で書き込み（SRP Batcher 維持・
  `MaterialPropertyBlock` 不使用）。インスタンス化の直後に Sampler を再初期化して
  参照を貼り直すため、点群も amount に自動追従します。
- **Edit 中（非 Play）**は `MaterialPropertyBlock` による非破壊プレビュー。
  共有マテリアル資産は汚しませんが、**この Edit プレビューは Sampler へは反映されません**
  （Sampler は `sharedMaterial` から値を読む v1 制約のため。実際の点群を確認するには
  Play してください）。

### Timeline トラックで駆動する

`Dissolve Controller` は専用 Timeline トラックから駆動できます（`com.unity.timeline`
導入時のみ有効。未導入ならトラック関連コードは無効化され、Controller だけで動作）。

1. Timeline に **`Origuma > Dissolve Track`** を追加。
2. トラックのバインディングに、対象キャラの **`Dissolve Controller`** をドラッグ。
   これで「シーンに管理オブジェクトを並べず、タイムラインが各キャラの Controller への
   参照を持つ」構成になります（キャラごとにトラックを 1 本）。
3. トラック上に **`Dissolve Clip`** を置き、クリップの `Curve`（正規化時間 0..1 → amount）
   で進行を作ります。既定は Linear（0→1）。
4. クリップ間はブレンド可能、末尾は Extrapolation（Hold）で「消え終わり」を保持できます。
   クリップが無い区間・Timeline 停止時は、Controller は書き込まれず**シーン側の
   `amount` を尊重**します（勝手に 0 へ戻しません）。

### キャラ入れ替わり（Swap）

2 キャラの「入れ替わり」演出は **`Dissolve Swap Controller`**
（`Runtime/Scripts/Dissolve/DissolveSwapController.cs`）で組みます。片方（A）を
消しながら、もう片方（B）を反転 Dissolve で出現させます。

**Swap 自体はマテリアルに一切触れません**。制御はすべて各キャラの `DissolveController`
経由（`Set(amount, invert)` を呼ぶだけ）で、「マテリアルへ直接書くのは
DissolveController だけ」という一本化方針を保ちます。

1. A・B それぞれのキャラのルートに **`Dissolve Controller`** を用意する（上記の
   司令塔の手順どおり。**B 側マテリアルも Enable Dissolve を ON** にしておくこと）。
2. 空の GameObject（またはいずれかのルート）に **`Dissolve Swap Controller`** を追加
   （`Add Component > Origuma > EasyShaderCore > Dissolve Swap Controller`）。
3. `Character A`（消えていく側）と `Character B`（現れる側）に、各キャラの
   `DissolveController` を割り当てる。
4. `amount`（0..1）を動かすと、A は通常向き（`invert=false`）で消え、B は反転
   （`invert=true`）で現れます。0=A 表示/B 非表示 → 1=A 消失/B 出現。
   `amount` は public フィールドなので **Animation Track から直接キー打ち**できます。
5. 手軽な確認には、コンポーネントの右クリックメニュー **`▶ A→B (0→1)`** /
   **`◀ B→A (1→0)`**（`duration` 秒かけて自動 Lerp。Play モード限定）や、
   スクリプトから `PlayToB()` / `PlayToA()` / `Play(target)`。

> **実行順**: Swap には `[DefaultExecutionOrder(-10)]` が付いており、Controller の
> `LateUpdate` より先に `amount` を書き込みます（Swap が値を確定 → Controller が反映、
> の順序を保証。付けないと Controller が 1 フレーム古い値を読むことがあります）。

---

## 2. VFX Graph 側の Exposed プロパティ

VFX Graph の Blackboard に以下の **Exposed** プロパティを、**この名前で**作成します。

| 型 | 名前 | 用途 |
| :--- | :--- | :--- |
| `GraphicsBuffer` | `DissolveEdgePoints` | エッジ点群（`DissolveEdgePoint` 構造体の配列） |
| `GraphicsBuffer` | `DissolveEdgeCount`  | 有効点数（`uint` 1 個） |
| `float`          | `DissolveAmount`     | 進行度 0..1（代表マテリアル） |
| `Vector4`        | `DissolveEdgeColor`  | 最前線の HDR 発光色 |

> `GraphicsBuffer` 型プロパティを作るには、Blackboard の型に本パッケージの
> `DissolveEdgePoint`（`[VFXType(Usage.GraphicsBuffer)]`）が現れます。

### `DissolveEdgePoint` のレイアウト（32 bytes / stride 一致必須）

```
float3 positionWS;   // ワールド座標
float3 normalWS;     // ワールド法線
float  edgeFactor;   // 0..1（最前線=1）
float  _pad;         // 整列パディング
```

---

## 3. グラフの組み方（定番パターン）

### Initialize / Spawn（1 パーティクル = 1 エッジ点）

1. **件数の取得**: `Sample Buffer` オペレータで `DissolveEdgeCount` の index 0 を
   読み、`uint count` を得る。
2. **空なら生成しない**: `count == 0` のとき `Alive = false`（または Spawn 数 0）。
3. **ランダムに 1 点選ぶ**: `Random Number`(0..1) × `count` を `floor` して index。
   `Sample Buffer(DissolveEdgePoints, index)` で 1 点を取得。
4. 取得した `positionWS` を **Set Position**、`normalWS` を **Set Velocity**
   （法線方向へ飛ばす）や向きに、`edgeFactor` を寿命・サイズ・輝度の重みに使う。
5. 色は `DissolveEdgeColor`（HDR）を **Set Color**。`DissolveAmount` は
   スポーン量カーブやフェードに使える。

> `Sample Buffer` はストライドを `DissolveEdgePoint` に合わせること。位置は
> **ワールド空間**なので、システムの Space を **World** にする。

### Update / Output

- 通常のパーティクル（重力・ドラッグ・サイズ over life など）で装飾。
- 出力は Quad/Mesh いずれも可。`edgeFactor` を Alpha やサイズに掛けると
  最前線ほど強く光る表現になる。

---

## 4. 既知の制約

- **1 フレーム遅延**: 抽出は `LateUpdate` で Dispatch。GPU スキニングの反映が
  1 フレーム遅れる場合があるが、パーティクル用途では許容。
- **頂点ベースの密度**: 点はメッシュ頂点位置に限られ、`Sample Stride` で間引く。
  面上の連続点ではない（高密度が要る場合は Stride を下げる）。
- **マテリアルプロパティは `sharedMaterial` から取得**（v1）。
  `MaterialPropertyBlock` での上書きは未反映。
- **頂点フォーマットは Float32**（position/normal/uv0）を前提。
- VFX Graph パッケージが無い環境ではバッファ抽出のみ動作し、VFX 連携は無効化されます。

---

## 5. 旧実装（`Assets/Dissolve`）からの移行

旧 `Assets/Dissolve` の C# / compute / VFX は、本パッケージの新実装で**完全に
置き換え**られます。新実装は「マテリアルへ直接書くのは `DissolveController` だけ・
上位（Swap / Timeline）は Controller にだけ話す」方針で、CPU readback を行いません。

### C# の対応

| 旧（`Assets/Dissolve/Scripts`） | 新（本パッケージ） |
| :--- | :--- |
| `DissolveSwapController`（各マテリアルへ直接 `SetFloat` / `EnableKeyword`） | `DissolveSwapController`（マテリアル非接触。`DissolveController` 経由のみ） |
| `DissolveParticleController`（CPU 側でエッジ点を算出し VFX へ供給） | `DissolveEdgeSampler`（GPU 抽出・CPU readback なし） |

旧スクリプトは新実装に機能内包されるため**削除可**です（削除はユーザー判断。
`Assets/` 配下のファイルは本作業では変更しません）。

### VFX アセット（`DissolveEffect.vfx` / `MeshDissolve.vfx`）の繋ぎ替え

旧 VFX を新しいエッジ点群バッファに繋ぎ替える手順:

| 旧 Exposed プロパティ | 新 Exposed プロパティ |
| :--- | :--- |
| `EmitBuffer`（`GraphicsBuffer`） | `DissolveEdgePoints`（`GraphicsBuffer`、要素型 `DissolveEdgePoint`） |
| `EmitCount`（`uint` バッファ） | `DissolveEdgeCount`（`uint` 1 個の `GraphicsBuffer`） |

1. Blackboard のプロパティ名を上表のとおりリネーム（または新規作成して差し替え）。
2. **`Sample Buffer` ノードの型を `DissolveEdgePoint` に差し替え**る。フィールドは
   `positionWS`（ワールド座標）/ `normalWS`（ワールド法線）/ `edgeFactor`（0..1・最前線=1）。
   位置はワールド空間なのでシステムの Space を **World** にする（詳細は本書「3. グラフの組み方」）。
3. 供給元を `DissolveEdgeSampler` の `Targets` に登録する（Sampler が毎フレーム
   `DissolveEdgePoints` / `DissolveEdgeCount` / `DissolveAmount` / `DissolveEdgeColor`
   をバインドする。プロパティが存在するものだけ）。
