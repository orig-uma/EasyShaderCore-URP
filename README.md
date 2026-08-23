# EasyShaderCore for URP

EasyPBR（`com.origuma.easypbr-urp`）/ EasyToon（`com.origuma.easytoon-urp`）共通の基盤パッケージです。
**単体では使いません**。EasyPBR / EasyToon が依存パッケージとして参照する共通基盤で、
純粋関数の HLSL ライブラリとマップベイク資産・共通の Editor UI を提供します。

## 含まれるもの

| 領域 | 内容 |
| :--- | :--- |
| `Runtime/Shaders/Common/` | 純粋関数の HLSL ライブラリ（BRDF 群 / 色・数学・サンプリング / MatCap・Emission・Dissolve / URP 影サンプラ・環境反射） |
| `Editor/Baking/` | マップベイク（AO / Bent Normal / Shade Normal / Cavity / Curvature / SSS / Hair Flow / Face SDF）。`Origuma.EasyShaderCore.Editor` 名前空間・public。Face SDF は 4ch（Doll）と 16bit 1ch（Idol）の両形式・距離場ブレンド・落ち影レイキャスト対応（仕様は `Documentation~/FACE_SDF_BAKING.md`） |
| `Editor/ShaderGuiKit.cs` | マテリアル Inspector 用の汎用描画キット（折りたたみ・日英ラベル・⚡バリアント注記） |
| `Editor/FeatureSetup*.cs` | Renderer Feature の追加 / 削除ウィンドウの基底（`FeatureSetupWindowBase`）と Inspector 内の未導入ガード（`FeatureSetup.DrawFeatureGuard`）。利用側の `Idol Setup` 等はこれの派生 |
| `Editor/MaterialReplacerWindow.cs` | シーン / プレハブ内のマテリアル一括置換（Window > Origuma > Material Replacer） |
| `Editor/UrpShadowSetupWindow.cs` | URP Asset の Shadow 設定プリセット適用ツール（Window > Origuma > URP Shadow Setup） |
| `Runtime/Scripts/BlackOutController.cs` | 暗転（`_BlackOut`）のキャラ単位制御。Play = マテリアルインスタンス（SRP Batcher 維持）/ Edit = 非破壊 MPB。Timeline の Animation Track からフィールド直キー可。EasyPBR Doll / EasyToon Idol 共用 |
| `Runtime/Scripts/Dissolve/` | Dissolve の統括制御（`DissolveController` 司令塔 = マテリアル `_DissolveAmount` / `_DissolveInvert` + Sampler 一括制御）、2 キャラの入れ替わり演出（`DissolveSwapController` = A 消失／B 出現を Controller 経由で同期）、Timeline トラック（`DissolveTrack` / `DissolveClip` / ミキサー、`com.unity.timeline` 任意依存）。使い方は `Documentation~/VFX_DISSOLVE.md` |
| `Runtime/Scripts/Vfx/` | Dissolve エッジ（消失最前線）点群の GPU 抽出と VFX Graph 連携（`DissolveEdgeSampler` + `DissolveEdgeSample.compute`）。組み方は `Documentation~/VFX_DISSOLVE.md` |

## インストール

通常は**手動インストール不要**です。EasyPBR / EasyToon をインストールすると、
各パッケージの Installer が本パッケージを**自動で導入**します（git が必要。詳細は各パッケージの README）。

手動で入れる場合は `Window > Package Manager > + > Add package from git URL...` に以下を入力する。

```
https://github.com/orig-uma/EasyShaderCore.git
```

特定バージョンを指定する場合:

```
https://github.com/orig-uma/EasyShaderCore.git#v0.3.1
```

HLSL は `Packages/com.origuma.easyshader-core/Runtime/Shaders/Common/...` を絶対パスで include する。

## 動作環境

* Unity 6 (6000.3) 以降
* Universal RP 17.3 以降

## 設計メモ

- 利用側パッケージ（EasyPBR / EasyToon）は package.json に本パッケージを**依存宣言しない**。UPM は git 依存をレジストリ解決できず、宣言すると利用側パッケージの git URL インストール自体が拒否されるため。
- 代わりに各利用側の Installer がタグ固定 URL（例: `https://github.com/orig-uma/EasyShaderCore.git#v0.3.1`）で自動導入する。リリース時に本パッケージへタグ `v0.x.x` を打ち、利用側のピン留め URL を更新する運用。

## ライセンス

[MIT License](LICENSE.md)

## 作者

Origuma — [https://github.com/orig-uma](https://github.com/orig-uma)
