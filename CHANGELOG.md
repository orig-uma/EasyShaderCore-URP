# Changelog

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
