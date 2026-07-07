// =============================================================================
//  BRDF_Detail.hlsl
// -----------------------------------------------------------------------------
//  法線ディテール。ブルーノイズ等で法線を僅かに揺らし質感を足す。
//  noiseVec はテクスチャサンプル結果を *2-1 した値( -1..1 )を渡す。
//  intensity = 0 のとき normalize をスキップして入力をそのまま返す。
//  前提: URP Core.hlsl（UNITY_BRANCH）。
// =============================================================================
#ifndef EASYPBR_BRDF_DETAIL_INCLUDED
#define EASYPBR_BRDF_DETAIL_INCLUDED

half3 GetGrainNormal(half3 cleanNormalWS, half3 noiseVec, float grainIntensity)
{
    UNITY_BRANCH
    if (grainIntensity <= 0.0) return cleanNormalWS;
    return normalize(cleanNormalWS + noiseVec * grainIntensity * 0.15);
}

#endif // EASYPBR_BRDF_DETAIL_INCLUDED
