// =============================================================================
//  Common_Math.hlsl
// -----------------------------------------------------------------------------
//  外部依存ゼロの純粋数学ユーティリティ。
//  ハッシュ / スクリーン空間ノイズ / remap / 輝度クランプ。
//  前提: なし（HLSL 組み込み関数のみ）。
// =============================================================================
#ifndef EASYPBR_COMMON_MATH_INCLUDED
#define EASYPBR_COMMON_MATH_INCLUDED

// 2D -> 1D ハッシュ（旧 Hash2DTo1D）。グリッタ等のセル乱数に使用。
float Hash21(float2 p)
{
    p = frac(p * float2(443.897, 441.423));
    p += dot(p, p.yx + 19.19);
    return frac((p.x + p.y) * p.x);
}

// Interleaved Gradient Noise（テクスチャ不要・ALUのみ・面の上で泳がない）。
// 旧 Doll_IGN。ディザ / PCF回転 phi 等に使用。
float IGN(float2 pix)
{
    const float3 m = float3(0.06711056, 0.00583715, 52.9829189);
    return frac(m.z * frac(dot(pix, m.xy)));
}

// 線形 remap（ゼロ割回避つき）。
// ※ URP コア(Common.hlsl)に同名 Remap があるため EasyPBR_ プレフィックスで回避。
float EasyPBR_Remap(float v, float inMin, float inMax, float outMin, float outMax)
{
    float t = saturate((v - inMin) / max(inMax - inMin, 1e-6));
    return lerp(outMin, outMax, t);
}

// a..b を 0..1 に（ゼロ割回避つき）。
float InvLerpSafe(float a, float b, float v)
{
    return saturate((v - a) / max(b - a, 1e-6));
}

// Rec.601 輝度。
float Luminance601(float3 c)
{
    return dot(c, float3(0.299, 0.587, 0.114));
}

// 輝度が limit を超えないよう色をスケール（旧 ApplyLightEnergyLimit）。
// Diffuse / Primary Spec / Secondary Spec それぞれ個別の limit で呼ぶ。
float3 ApplyLuminanceClamp(float3 rawLight, float limit)
{
    float lum     = max(0.001, Luminance601(rawLight));
    float safeLum = min(lum, limit);
    return rawLight * (safeLum / lum);
}

#endif // EASYPBR_COMMON_MATH_INCLUDED
