# Changelog

## [Unreleased]

### Added

- **UrpShadowSetupWindow を追加**（`Editor/UrpShadowSetupWindow.cs`）。メニューは `Window > Origuma > URP Shadow Setup`（namespace は `Origuma.EasyShaderCore.Editor`）:
  - QualitySettings 全レベル + GraphicsSettings 既定から `UniversalRenderPipelineAsset` を収集し、使用品質レベルをラベル併記（★=アクティブ品質レベル）
  - Shadow 設定のプリセット 5 種（Unity 既定 / 低 / 中 / 高 / キャラ重視）をテーブル駆動で定義。選択アセットへ一括適用（現在値→適用後のプレビュー表・変更行の強調付き）
  - 書き込みは `SerializedObject` 経由で `m_*` フィールドを直接操作（`m_AnyShadowsSupported` の連動整合を維持、Undo は自動記録、URP バージョン差で欠けるフィールドはスキップして警告）

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
