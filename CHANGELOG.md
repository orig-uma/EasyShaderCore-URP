# Changelog

## [0.1.0]

- 初回リリース。EasyPBR 0.5.0 から以下を移管:
  - `Runtime/Shaders/Common/**`（BRDF / Effects / URP / 色・数学・サンプリングの純粋関数 HLSL）
  - `Editor/Baking/**`（EasyPbr*Baker 群 + EasyPbrBakeCore。`internal` → `public`、名前空間を `Origuma.EasyShaderCore.Editor` へ変更。クラス名は互換のため変更なし）
  - `Editor/ShaderGuiKit.cs`（名前空間変更のみ）
- .meta（GUID）は移管元のまま維持（テクスチャ・マテリアル参照は壊れない）
