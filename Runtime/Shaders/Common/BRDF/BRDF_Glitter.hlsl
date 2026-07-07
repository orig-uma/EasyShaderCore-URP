// =============================================================================
//  BRDF_Glitter.hlsl
// -----------------------------------------------------------------------------
//  グリッタ / スパンコール（ラメ・ホログラム素材）。
//  Prepare(ライト非依存・最近傍セル探索) / Apply(ライト依存・フラッシュ) の2段。
//  前提: URP Core.hlsl（SafeNormalize / UNITY_UNROLL / UNITY_BRANCH）,
//        Common_Math.hlsl（Hash21）, Common_Color.hlsl（HueToRGB）。
// =============================================================================
#ifndef EASYPBR_BRDF_GLITTER_INCLUDED
#define EASYPBR_BRDF_GLITTER_INCLUDED

#include "../Common_Math.hlsl"
#include "../Common_Color.hlsl"

// PrepareGlitter が計算したライト非依存データ（全ライトで共有）。
struct GlitterGeom
{
    float  dotMask;          // スパンコール円盤のマスク（距離ベース）
    float  outerMask;        // 円盤外縁マスク
    float  innerGlow;        // 中心ほど明るい内部グロー
    half3  glitterNormal;    // ランダムチルト後の法線
    float  NdotV;            // glitterNormal · viewDir
    float  baseHue;          // iridescence 基準色相（セル固有）
    float  perSequinOffset;  // スパンコールごとの色相個体差
};

// ライト非依存の幾何・ランダム計算。frag でライトループ前に1回だけ呼ぶ。
// false の場合は ApplyGlitterLight をスキップ可。
bool PrepareGlitter(
    half3 baseNormalWS, half3 viewDirectionWS,
    float2 uv, float scale, float dotSize,
    float tiltStrength, float glitterMask,
    float intensity, float sparsity,
    out GlitterGeom geom)
{
    geom = (GlitterGeom)0;
    if (glitterMask <= 0.0 || intensity <= 0.0) return false;

    float2 gridUV   = uv * scale;
    float2 id       = floor(gridUV);
    float2 localUV  = frac(gridUV);
    float  invScale = rcp(scale);

    float  minDistSq = 999.0;
    float2 bestId    = id;
    float  bestRand1 = 0.0, bestRand2 = 0.0;

    UNITY_UNROLL
    for (int y = -1; y <= 1; y++)
    {
        UNITY_UNROLL
        for (int x = -1; x <= 1; x++)
        {
            float2 neighborId = id + float2(x, y);

            float r4 = Hash21(neighborId + float2(98.76, 54.32));
            float r1 = Hash21(neighborId);
            float r2 = Hash21(neighborId + float2(45.67, 89.12));

            float2 diff   = float2(x, y) + float2(r1, r2) - localUV;
            float  distSq = dot(diff, diff);
            distSq = (r4 >= sparsity) ? distSq : 999.0;

            if (distSq < minDistSq)
            {
                minDistSq = distSq;
                bestId    = neighborId;
                bestRand1 = r1;
                bestRand2 = r2;
            }
        }
    }

    float bestRand3 = Hash21(bestId + float2(12.34, 56.78));
    float bestRand4 = Hash21(bestId + float2(33.21, 77.65));

    float absoluteDist  = sqrt(minDistSq) * invScale;
    float actualDotSize = dotSize * lerp(0.7, 1.0, bestRand3);

    geom.outerMask = 1.0 - smoothstep(actualDotSize * 0.85, actualDotSize, absoluteDist);
    geom.innerGlow = 1.0 - smoothstep(0.0, actualDotSize * 0.5, absoluteDist);
    geom.dotMask   = geom.outerMask;

    if (geom.dotMask <= 0.0) return false;

    float3 randomTilt   = float3(bestRand1 - 0.5, bestRand2 - 0.5, bestRand3 - 0.5) * tiltStrength;
    geom.glitterNormal  = normalize(baseNormalWS + randomTilt);
    geom.NdotV          = saturate(dot(geom.glitterNormal, viewDirectionWS));

    geom.baseHue         = bestRand4;
    geom.perSequinOffset = bestRand4 * 0.8;

    return true;
}

// PrepareGlitter の幾何データにライトエネルギーを乗算して最終輝度を返す。
// ハーフベクトルと iridescence 色相はライト依存なのでここで計算。
half3 ApplyGlitterLight(
    GlitterGeom geom,
    float3 lightDirWS, half3 viewDirectionWS,
    half3 color, float intensity,
    float iridescenceAmount, float iridescenceShift,
    float baseReflection, float3 diffuseLightEnergy)
{
    float3 halfVector = SafeNormalize(lightDirWS + viewDirectionWS);
    float  NdotH      = saturate(dot(geom.glitterNormal, halfVector));

    float flashSharp  = pow(NdotH, 500.0) * step(0.94, NdotH);
    float flashSoft   = pow(NdotH, 80.0)  * step(0.70, NdotH);
    float flash       = flashSharp + flashSoft * 0.15;

    float2 halfFlat    = halfVector.xz;
    float  halfAzimuth = dot(halfFlat, float2(0.8, 0.6));
    float  iridHue     = frac(geom.baseHue + halfAzimuth * iridescenceShift + geom.perSequinOffset);
    half3  iridColor   = HueToRGB(iridHue);
    half3  finalColor  = lerp(color, color * iridColor * 2.0, iridescenceAmount);

    half3 baseReflColor = finalColor * baseReflection * (1.0 - geom.NdotV * 0.5);
    half3 flashContrib  = finalColor * flash * intensity;
    half3 baseContrib   = baseReflColor * geom.outerMask * (1.0 - geom.innerGlow * 0.5);

    return (flashContrib + baseContrib) * geom.dotMask * diffuseLightEnergy;
}

#endif // EASYPBR_BRDF_GLITTER_INCLUDED
