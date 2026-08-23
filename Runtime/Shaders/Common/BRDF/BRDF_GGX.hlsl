// =============================================================================
//  BRDF_GGX.hlsl
// -----------------------------------------------------------------------------
//  微小面 GGX BRDF の純粋ヘルパーと 1ローブ分の Cook-Torrance。
//  Blinn-Phong ローブも併設し、モデル選択は呼び出し側の責務とする
//  （ライブラリ内にキーワード分岐を持たない）。
//  前提: URP Core.hlsl（PI を使用）。
// =============================================================================
#ifndef EASYPBR_BRDF_GGX_INCLUDED
#define EASYPBR_BRDF_GGX_INCLUDED

float EasyPBR_D_GGX(float NdotH, float alpha)
{
    float a2 = alpha * alpha;
    float d  = (NdotH * a2 - NdotH) * NdotH + 1.0; // NdotH^2 (a2-1) + 1

    // **下駄ではなく下限で挟む。しかも下限は十分小さくすること。**
    // 以前は `PI * d * d + 1e-7` だった。反射の芯（NdotH = 1）では d = a2 なので、
    // 滑らかな面ほど `PI * d * d` が小さくなり、**1e-7 のほうが支配的になる**:
    //
    //   Smoothness 0.80 → 山 199 が 197   （0.99 倍・実害なし）
    //   Smoothness 0.90 → 山 3,183 が 761 （**0.24 倍**）
    //   Smoothness 0.95 → 山 50,930 が 62 （**0.001 倍**）
    //
    // 真珠ビーズ・金具・エナメルのような**滑らかな材質でだけ**ハイライトが
    // 潰れる。粗い材質では起きないので、テストシーンの材質次第で気付けない。
    //
    // alpha の下限 0.002 のとき `PI * d * d` は 5.03e-11 なので、
    // 1e-12 なら余裕 50 倍で素通りし、alpha = 0 の退化だけを守れる。
    return a2 / max(PI * d * d, 1e-12);
}

// 高さ相関 Smith 可視性（1/(4 NdotL NdotV) を内包）。
float EasyPBR_V_SmithGGX(float NdotL, float NdotV, float alpha)
{
    float a2 = alpha * alpha;

    // **sqrt の中を負にしないこと。** NdotL / NdotV は呼び出し側で saturate 済みだが、
    // alpha が 1 を超えると (1 - a2) が負になり sqrt が NaN を返す。
    // alpha = perceptualRoughness² なので通常は 1 以下だが、守りは式の中に置く。
    float s = max(1.0 - a2, 0.0);

    float ggxV = NdotL * sqrt(NdotV * NdotV * s + a2);
    float ggxL = NdotV * sqrt(NdotL * NdotL * s + a2);
    return 0.5 / max(ggxV + ggxL, 1e-5);
}

float3 EasyPBR_F_Schlick(float VdotH, float3 f0)
{
    float f = pow(1.0 - VdotH, 5.0);
    return f0 + (1.0 - f0) * f;
}

float SmoothnessToAlpha(float smoothness)
{
    float roughness = 1.0 - saturate(smoothness);
    return max(roughness * roughness, 2e-3); // 完全鏡面のギラつき/NaN回避
}

// -----------------------------------------------------------------------------
//  Geometric Specular Antialiasing（Tokuyoshi & Kaplanyan）
//   画面内の法線分散から実効ラフネスを上げ、大型LED・激しいモーション時の
//   ハイライトのチラつき（ジャギ）を発生源で抑える。
//   variance は呼び出し側で ddx/ddy(normalWS) から1回だけ算出して渡すこと
//   （導関数は均一制御フローで評価する必要があるため）。
// -----------------------------------------------------------------------------

// 法線の画面内分散 → カーネルラフネス（alpha^2 加算量）。0 で無効。
float ComputeSpecularAAVariance(float3 normalWS, float strength, float threshold)
{
    float3 dndu = ddx(normalWS);
    float3 dndv = ddy(normalWS);
    float variance = strength * (dot(dndu, dndu) + dot(dndv, dndv));
    return min(2.0 * variance, threshold);
}

// smoothness を分散ぶんだけ下げて返す（lobe へ渡す前に1回適用）。
float ApplySpecularAA(float smoothness, float aaVariance)
{
    float roughness = 1.0 - saturate(smoothness);
    float alpha     = roughness * roughness;
    float alphaF    = sqrt(saturate(alpha * alpha + aaVariance)); // 分散を alpha^2 に加算
    return 1.0 - sqrt(alphaF);
}

// 1ローブ分の Cook-Torrance（D*V*F。NdotL は呼び出し側で乗算）。
half3 GGXLobe(float NdotH, float NdotL, float NdotV, float VdotH,
              float smoothness, half3 tint, float3 f0)
{
    float alpha = SmoothnessToAlpha(smoothness);
    float D = EasyPBR_D_GGX(NdotH, alpha);
    float V = EasyPBR_V_SmithGGX(NdotL, NdotV, alpha);
    half3 F = EasyPBR_F_Schlick(VdotH, f0);
    return D * V * F * tint;
}

// Blinn-Phong 1ローブ（最軽量・見た目互換）。NdotH のみで完結。
half3 BlinnPhongLobe(float NdotH, float smoothness, half3 tint, float intensity)
{
    float specPower = exp2(10.0 * smoothness + 1.0);
    return tint * pow(NdotH, specPower) * intensity;
}

#endif // EASYPBR_BRDF_GGX_INCLUDED
