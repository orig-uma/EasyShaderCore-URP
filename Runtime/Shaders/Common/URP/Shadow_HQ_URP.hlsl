// =============================================================================
//  Shadow_HQ_URP.hlsl
// -----------------------------------------------------------------------------
//  メインディレクショナルライト専用 高品質セルフシャドウサンプラ（URP結合）。
//   - ピクセル単位シャドウ座標 / 受け側ノーマルオフセット
//   - スクリーン空間 IGN 回転 Vogel PCF
//   - 任意 PCSS（コンタクトハードニング）
//
//  ※ このファイルは URP のシャドウグローバル（_MainLightShadowmapTexture 等）に
//    依存するため "URP結合" として隔離している。汎用部（IGN / VogelDisk）は
//    Common_Math / Common_Sampling に分離済み。
//
//  汎用化方針:
//   - 旧 _SHADOWQUALITY_PCSS キーワード → 引数 contactHardening(bool)
//   - 旧 _ReceiverNormalBias 直参照     → 引数 receiverNormalBias(float)
//   - _MAIN_LIGHT_SHADOWS の有無ガードはシャドウAPIの可用性判定なので維持。
//
//  前提: URP Core.hlsl / Shadows.hlsl, Common_Math.hlsl, Common_Sampling.hlsl。
// =============================================================================
#ifndef EASYPBR_SHADOW_HQ_URP_INCLUDED
#define EASYPBR_SHADOW_HQ_URP_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
#include "../Common_Math.hlsl"
#include "../Common_Sampling.hlsl"

// タップ数（UNITY_UNROLL のためコンパイル時定数）。8 が実用上の最適点。
#ifndef EASYPBR_SHADOW_TAPS
    #define EASYPBR_SHADOW_TAPS 8
#endif

// PCSS のブロッカー深度読み取り用 point sampler。
// 多くの URP バージョンで sampler_PointClamp は宣言済み。未宣言環境では下行を有効化。
// SAMPLER(sampler_PointClamp);

// ブロッカー探索: 平均遮蔽深度からペナンブラ幅を動的算出する。
bool EasyPBR_FindBlocker(float2 baseUV, float receiverZ, float2 texel, float2 phiSC,
                         float searchRadius, out float avgBlockerZ)
{
    float sumZ = 0.0;
    int   count = 0;
    UNITY_UNROLL
    for (int i = 0; i < EASYPBR_SHADOW_TAPS; i++)
    {
        float2 o = VogelDisk(i, EASYPBR_SHADOW_TAPS, phiSC) * texel * searchRadius;
        float  z = SAMPLE_TEXTURE2D_LOD(_MainLightShadowmapTexture, sampler_PointClamp,
                                        baseUV + o, 0).r;
    #if UNITY_REVERSED_Z
        if (z > receiverZ) { sumZ += z; count++; }
    #else
        if (z < receiverZ) { sumZ += z; count++; }
    #endif
    }
    avgBlockerZ = (count > 0) ? sumZ / count : receiverZ;
    return count > 0;
}

// -----------------------------------------------------------------------------
//  EasyPBR_SampleMainShadowHQ
//   返り値 0(影)..1(光)。ResolveCastShadow に shadowAttenuation として渡す。
//   normalWS / NdotL は grain を乗せていない clean normal 由来を渡すこと。
//   contactHardening = true で PCSS（接地点は鋭く・遠方はボケる）。
//   useTent = true で決定論的テント 5x5（ノイズなし・配信向け。PCSS とは併用しない）。
// -----------------------------------------------------------------------------
half EasyPBR_SampleMainShadowHQ(float3 positionWS, float3 normalWS, float NdotL,
                                float2 screenPix, float softness,
                                float receiverNormalBias, bool contactHardening,
                                bool useTent)
{
#if !defined(_MAIN_LIGHT_SHADOWS) && !defined(_MAIN_LIGHT_SHADOWS_CASCADE)
    return 1.0h; // シャドウキーワードが無ければ分岐ごと除去
#else
    // 受け側ノーマルオフセット: 傾斜面ほど強く押し出してアクネを除去。
    float  slope     = saturate(1.0 - NdotL);
    float3 offsetPos = positionWS + normalWS * (receiverNormalBias * (0.5 + slope)) * 0.01;

    float4 coord = TransformWorldToShadowCoord(offsetPos);
    float2 texel = _MainLightShadowmapSize.xy;
    // 毎ピクセル回転（スクリーン安定）。sincos は位相ごとに 1 回だけ ──
    // タップ側は回転版 VogelDisk で当てるので、UNROLL 展開後のループから
    // 実行時 sincos が消える（EasyToon Idol からの逆輸入。T-340）。
    float2 phiSC;
    sincos(IGN(screenPix) * TWO_PI, phiSC.x, phiSC.y);

    float radius = 1.0 + softness * 6.0;      // ペナンブラ幅（texel）※Vogel時のみ使用

    half atten;
    UNITY_BRANCH
    if (useTent)
    {
        // --- テント 5x5（Unity標準・決定論的・ノイズなし・9フェッチ） ---
        //  coord は受け側ノーマルオフセット済みなので、URP標準テントより高精度。
        real  tentWeights[9];
        real2 tentUV[9];
        SampleShadow_ComputeSamples_Tent_5x5(_MainLightShadowmapSize, coord.xy, tentWeights, tentUV);

        atten = 0.0h;
        UNITY_UNROLL
        for (int t = 0; t < 9; t++)
            atten += tentWeights[t] * SAMPLE_TEXTURE2D_SHADOW(
                _MainLightShadowmapTexture, sampler_LinearClampCompare,
                float3(tentUV[t].xy, coord.z));
    }
    else
    {
        // --- 回転 Vogel ディスク PCF（可変半径・PCSS対応・ノイズあり） ---
        UNITY_BRANCH
        if (contactHardening)
        {
            float avgBlockerZ;
            if (!EasyPBR_FindBlocker(coord.xy, coord.z, texel, phiSC, radius * 1.5, avgBlockerZ))
                return 1.0h; // 遮蔽物なし = 完全に光
            float penumbra = abs(coord.z - avgBlockerZ) / max(avgBlockerZ, 1e-4);
            radius = clamp(penumbra * radius * 8.0, 1.0, radius * 2.0);
        }

        atten = 0.0h;
        UNITY_UNROLL
        for (int i = 0; i < EASYPBR_SHADOW_TAPS; i++)
        {
            float2 o = VogelDisk(i, EASYPBR_SHADOW_TAPS, phiSC) * texel * radius;
            atten += SAMPLE_TEXTURE2D_SHADOW(_MainLightShadowmapTexture,
                                             sampler_LinearClampCompare,
                                             float3(coord.xy + o, coord.z));
        }
        atten /= EASYPBR_SHADOW_TAPS;
    }

    half fade = GetMainLightShadowFade(positionWS);
    return lerp(atten, 1.0h, fade);
#endif
}

#endif // EASYPBR_SHADOW_HQ_URP_INCLUDED
