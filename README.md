# EasyShaderCore for URP

EasyPBR（`com.origuma.easypbr-urp`）/ EasyToon（`com.origuma.easytoon-urp`）共通の基盤パッケージ。
**単体では使わない**。EasyPBR / EasyToon が依存パッケージとして参照する。

## 含まれるもの

| 領域 | 内容 |
| :--- | :--- |
| `Runtime/Shaders/Common/` | 純粋関数の HLSL ライブラリ（BRDF 群 / 色・数学・サンプリング / MatCap・Emission・Dissolve / URP 影サンプラ・環境反射） |
| `Editor/Baking/` | マップベイク（AO / Bent Normal / Shade Normal / Cavity / Curvature / SSS / Hair Flow / Face SDF）。`Origuma.EasyShaderCore.Editor` 名前空間・public |
| `Editor/ShaderGuiKit.cs` | マテリアル Inspector 用の汎用描画キット（折りたたみ・日英ラベル・⚡バリアント注記） |

## インストール順

1. **本パッケージ（EasyShaderCore）を先に**インストール
2. EasyPBR（>= 0.6.0）/ EasyToon をインストール

HLSL は `Packages/com.origuma.easyshader-core/Runtime/Shaders/Common/...` を絶対パスで include する。

## ライセンス

[MIT License](LICENSE.md)
