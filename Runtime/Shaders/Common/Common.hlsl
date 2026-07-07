// =============================================================================
//  Common.hlsl  (umbrella)
// -----------------------------------------------------------------------------
//  EasyPBR 汎用ライブラリの一括 include。依存順に並べてある。
//  これ 1 本を include すれば BRDF / Effects の純粋関数が全て使える。
//
//  ※ URP 結合の影サンプラ（Shadow_HQ_URP.hlsl）は URP の Shadows.hlsl を
//    引き込むため、ここには含めず必要なパスで個別 include すること。
//
//  前提: URP Core.hlsl を本ファイルより前に include しておくこと
//        （PI / TWO_PI / SafeNormalize / UNITY_BRANCH 等を使用）。
// =============================================================================
#ifndef EASYPBR_COMMON_UMBRELLA_INCLUDED
#define EASYPBR_COMMON_UMBRELLA_INCLUDED

// --- Layer 0: 純粋ユーティリティ -------------------------------------------
#include "Common_Math.hlsl"
#include "Common_Color.hlsl"
#include "Common_Sampling.hlsl"

// --- Layer 1: BRDF / ライティング素材 --------------------------------------
#include "BRDF/BRDF_GGX.hlsl"
#include "BRDF/BRDF_Specular.hlsl"
#include "BRDF/BRDF_Diffuse.hlsl"
#include "BRDF/BRDF_RimFuzz.hlsl"
#include "BRDF/BRDF_Translucency.hlsl"
#include "BRDF/BRDF_Anisotropic.hlsl"
#include "BRDF/BRDF_Glitter.hlsl"
#include "BRDF/BRDF_Detail.hlsl"
#include "BRDF/BRDF_Clearcoat.hlsl"

// --- Layer 2: エフェクト ----------------------------------------------------
#include "Effects/Fx_MatCap.hlsl"
#include "Effects/Fx_Emission.hlsl"
#include "Effects/Fx_Dissolve.hlsl"

#endif // EASYPBR_COMMON_UMBRELLA_INCLUDED
