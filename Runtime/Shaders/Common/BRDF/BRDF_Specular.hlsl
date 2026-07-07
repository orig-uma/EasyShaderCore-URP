// =============================================================================
//  BRDF_Specular.hlsl
// -----------------------------------------------------------------------------
//  2ローブ スペキュラ。GGX 版と Blinn-Phong 版を別関数として提供し、
//  どちらを使うかは呼び出し側（キーワード分岐）が決める。
//  out specularMaskVal は呼び出し側でエネルギー保存(Diffuse減算)に使う。
//  前提: URP Core.hlsl（SafeNormalize）, BRDF_GGX.hlsl。
// =============================================================================
#ifndef EASYPBR_BRDF_SPECULAR_INCLUDED
#define EASYPBR_BRDF_SPECULAR_INCLUDED

#include "BRDF_GGX.hlsl"

// 共通: スペキュラの可視マスク（ndotl の立ち上がり * specMask * castShadow）。
float DualLobeSpecMask(float ndotlSpecular, float specMask, float castShadow)
{
    return saturate(ndotlSpecular * 10.0) * specMask * castShadow;
}

// --- 物理ベース GGX: Fresnel・可視性込みで自然な裾と縁の輝き ---
//  aaVariance: Geometric Specular AA のカーネル量（呼び出し側で1回算出。0で無効）。
half3 DualLobeSpecularGGX(
    half3 detailNormalWS, float3 lightDirWS, half3 viewDirectionWS, float ndotlSpecular,
    float3 priSpecEnergy, half4 specColor1, float smoothness1, float intensity1,
    float3 secSpecEnergy, half4 specColor2, float smoothness2, float intensity2,
    float specMask, float castShadow, float specF0, float aaVariance,
    out float specularMaskVal)
{
    float3 halfVector = SafeNormalize(lightDirWS + viewDirectionWS);
    float  NdotH = saturate(dot(detailNormalWS, halfVector));
    specularMaskVal = DualLobeSpecMask(ndotlSpecular, specMask, castShadow);

    float NdotL = saturate(ndotlSpecular);
    float NdotV = saturate(dot(detailNormalWS, viewDirectionWS));
    float VdotH = saturate(dot(viewDirectionWS, halfVector));
    float3 f0   = specF0.xxx;

    smoothness1 = ApplySpecularAA(smoothness1, aaVariance);
    smoothness2 = ApplySpecularAA(smoothness2, aaVariance);

    half3 spec1 = GGXLobe(NdotH, NdotL, NdotV, VdotH, smoothness1, specColor1.rgb, f0)
                  * intensity1 * priSpecEnergy;
    half3 spec2 = GGXLobe(NdotH, NdotL, NdotV, VdotH, smoothness2, specColor2.rgb, f0)
                  * intensity2 * secSpecEnergy;

    return (spec1 + spec2) * NdotL * (specMask * castShadow);
}

// --- 従来 Blinn-Phong（既定・最軽量・見た目互換） ---
//  aaVariance: Geometric Specular AA のカーネル量（呼び出し側で1回算出。0で無効）。
half3 DualLobeSpecularBlinn(
    half3 detailNormalWS, float3 lightDirWS, half3 viewDirectionWS, float ndotlSpecular,
    float3 priSpecEnergy, half4 specColor1, float smoothness1, float intensity1,
    float3 secSpecEnergy, half4 specColor2, float smoothness2, float intensity2,
    float specMask, float castShadow, float aaVariance,
    out float specularMaskVal)
{
    float3 halfVector = SafeNormalize(lightDirWS + viewDirectionWS);
    float  NdotH = saturate(dot(detailNormalWS, halfVector));
    specularMaskVal = DualLobeSpecMask(ndotlSpecular, specMask, castShadow);

    smoothness1 = ApplySpecularAA(smoothness1, aaVariance);
    smoothness2 = ApplySpecularAA(smoothness2, aaVariance);

    half3 spec1 = BlinnPhongLobe(NdotH, smoothness1, specColor1.rgb, intensity1) * priSpecEnergy;
    half3 spec2 = BlinnPhongLobe(NdotH, smoothness2, specColor2.rgb, intensity2) * secSpecEnergy;

    return (spec1 + spec2) * specularMaskVal;
}

#endif // EASYPBR_BRDF_SPECULAR_INCLUDED
