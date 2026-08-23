// =============================================================================
//  BRDF_Anisotropic.hlsl
// -----------------------------------------------------------------------------
//  異方性ハイライト（髪の天使の輪・毛束ノイズ付き2バンド）。
//  Precompute(ライト非依存) / Calculate(ライト依存) の2段構成。
//  前提: URP Core.hlsl（SafeNormalize / UNITY_BRANCH）。
// =============================================================================
#ifndef EASYPBR_BRDF_ANISOTROPIC_INCLUDED
#define EASYPBR_BRDF_ANISOTROPIC_INCLUDED

struct AnisoPrecomp
{
    float3 tangentDir;   // 主ハイライト用（鋭い・天使の輪）
    float3 tangentDir2;  // 副ハイライト用（広い・毛色付き）
    float  strandNoise;  // 毛束ノイズ（副バンドのきらめきマスクに再利用）
};

AnisoPrecomp PrecomputeAnisoTangent(
    float3 tangentWS, float3 bitangentWS, half3 normalWS, float2 uv,
    float angle, float strandDir, float strandScale, float strandStrength,
    float offset, float offset2,
    float flowC2, float flowS2, float flowConf, float flowStrength)
{
    // 焼いた毛流れ(倍角)を信頼度×強度で識別(1,0)へブレンド。strength 0 で完全に従来挙動。
    float2 fv = lerp(float2(1.0, 0.0), float2(flowC2, flowS2), saturate(flowConf * flowStrength));

    // **向きが原点に潰れたときの `atan2(0,0)` は未定義。**
    // 焼いた毛流れの「ここは向きが決まらない」は倍角表現で (0,0) になり、
    // 旋毛の中心や毛流れの交差点に必ず現れる。信頼度×強度が 1 に飽和すると
    // lerp が完全にそちらへ寄って長さ 0 になる。
    // 返り値は環境依存（0 のことも NaN のこともある）で、NaN なら
    // sincos → 接線フレーム → **ハイライトに黒い穴が開く**。
    // 潰れていたら接線そのもの（theta = 0）へ戻す。
    float theta = (dot(fv, fv) > 1e-12) ? (0.5 * atan2(fv.y, fv.x)) : 0.0;

    float rad = theta + radians(angle + 90.0);
    float s, c; sincos(rad, s, c);
    float3 tBase = normalize(tangentWS * c + bitangentWS * s);

    float dirRad = radians(strandDir);
    float2 dirVec = float2(cos(dirRad), sin(dirRad));
    float strandCoord = dot(uv, dirVec);

    float strandNoise = sin(strandCoord * strandScale)
                      + sin(strandCoord * strandScale * 2.34) * 0.5
                      + sin(strandCoord * strandScale * 3.71) * 0.25;

    float noiseShift = (strandNoise * 0.5) * strandStrength;

    AnisoPrecomp result;
    result.tangentDir  = normalize(tBase + normalWS * (offset  + noiseShift));
    result.tangentDir2 = normalize(tBase + normalWS * (offset2 + noiseShift));
    result.strandNoise = strandNoise;
    return result;
}

half3 CalculateAnisotropicSpecular(
    AnisoPrecomp anisoPrecomp,
    half3 detailNormalWS, float3 lightDirWS, half3 viewDirectionWS,
    half4 anisoColor, float thickness,
    half4 anisoColor2, float thickness2,
    float3 diffuseLightEnergy, float castShadow)
{
    half3 result = half3(0, 0, 0);
    UNITY_BRANCH
    if (anisoColor.a > 0.0)
    {
        float3 h = SafeNormalize(lightDirWS + viewDirectionWS);
        float  mask = saturate(dot(detailNormalWS, lightDirWS) * 5.0) * castShadow;

        // --- 主バンド ---
        float dotTH1 = dot(anisoPrecomp.tangentDir, h);
        float sinTH1 = sqrt(1.0 - saturate(dotTH1 * dotTH1));
        float power1 = exp2(lerp(10.0, 1.0, thickness));
        result = anisoColor.rgb * pow(saturate(sinTH1), power1);

        // --- 副バンド（広い・毛色付き・ノイズきらめき・縁で強まる） ---
        UNITY_BRANCH
        if (anisoColor2.a > 0.0)
        {
            float VdotH   = saturate(dot(viewDirectionWS, h));
            float fresnel = lerp(0.5, 1.0, pow(1.0 - VdotH, 4.0));
            float dotTH2  = dot(anisoPrecomp.tangentDir2, h);
            float sinTH2  = sqrt(1.0 - saturate(dotTH2 * dotTH2));
            float power2  = exp2(lerp(10.0, 1.0, thickness2));
            float sparkle = saturate(anisoPrecomp.strandNoise * 0.5 + 0.5);
            result += anisoColor2.rgb * pow(saturate(sinTH2), power2) * sparkle * fresnel;
        }

        result = result * mask * diffuseLightEnergy;
    }
    return result;
}

#endif // EASYPBR_BRDF_ANISOTROPIC_INCLUDED
