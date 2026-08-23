// =============================================================================
//  Common_Color.hlsl
// -----------------------------------------------------------------------------
//  純粋な色変換ユーティリティ。RGB<->HSV / Hue->RGB / HSV補正。
//  前提: なし。
// =============================================================================
#ifndef EASYPBR_COMMON_COLOR_INCLUDED
#define EASYPBR_COMMON_COLOR_INCLUDED

// EasyToon Idol の実装へ引き上げた（T-340 逆輸入）。変更点は 2 つ:
//  1. `+ e` の下駄 → `max(x, e)` の**下限**。下駄は分母を常にずらすので
//     結果へ一様に混入する。色は 0 以上で分母は負にならないため、下限で
//     退化（真っ黒・無彩色）だけを守るのが正しい形。
//  2. half → float。half では 1e-10 が**アンダーフローして実質 0** になり、
//     下駄そのものが消えて無彩色でゼロ除算になりうる（half の最小正規値は
//     約 6.1e-5）。呼び出し側の half3 とは暗黙変換で互換。
// 名前に EasyPBR_ を付けるのは、URP 本体（Color.hlsl）の float3 RgbToHsv と
// 完全一致で衝突するため（half3 だった頃はオーバーロードで共存できていた）。
float3 EasyPBR_RgbToHsv(float3 c)
{
    float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
    float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
    float  d = q.x - min(q.w, q.y);
    return float3(abs(q.z + (q.w - q.y) / max(6.0 * d, 1e-10)),
                  d / max(q.x, 1e-10), q.x);
}

float3 EasyPBR_HsvToRgb(float3 c)
{
    float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
    float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
    return c.z * lerp(K.xxx, saturate(p - K.xxx), c.y);
}

// iridescence（虹色）用の Hue -> RGB。
half3 HueToRGB(float hue)
{
    return saturate(abs(frac(hue + half3(0.0, 2.0/3.0, 1.0/3.0)) * 6.0 - 3.0) - 1.0);
}

// 色相回転 / 彩度 / 明度のまとめてHSV補正。
half3 ApplyColorCorrection(half3 color, half hueShift, half saturation, half valueMulti)
{
    float3 hsv = EasyPBR_RgbToHsv(color);
    hsv.x = frac(hsv.x + hueShift);
    hsv.y = saturate(hsv.y * saturation);
    hsv.z = hsv.z * valueMulti;
    return (half3)EasyPBR_HsvToRgb(hsv);
}

// -----------------------------------------------------------------------------
//  ConditionLightColor
//   ライト色の整形（キャラの可読性をライト環境から守る防御層）。
//    influence:     0 = 同輝度の白色光として扱う（キャラの色設計を保持）
//    satLimit:      ライト彩度の上限（1 = 制限なし。色相は保持したまま減衰）
//    minBrightness: 輝度の下限（0 = なし。暗所でも最低限の明るさを保証）
//   すべて既定値（1 / 1 / 0）のとき素通し。
// -----------------------------------------------------------------------------
half3 ConditionLightColor(half3 lightColor, half influence, half satLimit, half minBrightness)
{
    const half3 kLumWeights = half3(0.2126, 0.7152, 0.0722);

    half lum = dot(lightColor, kLumWeights);
    half3 color = lerp(half3(lum, lum, lum), lightColor, influence);

    // 彩度上限: HSV の S（(max-min)/max）が satLimit に収まるまでグレー側へ。
    half maxC = max(color.r, max(color.g, color.b));
    half minC = min(color.r, min(color.g, color.b));
    half sat = (maxC > 1e-4) ? (maxC - minC) / maxC : 0.0;
    if (sat > satLimit)
    {
        half gray = dot(color, kLumWeights);
        color = lerp(color, half3(gray, gray, gray), 1.0 - satLimit / sat);
    }

    // 輝度下限: 色相比を保ったままスケールアップ（完全黒は白色光で持ち上げ）。
    half lumOut = dot(color, kLumWeights);
    if (lumOut < minBrightness)
    {
        color = (lumOut > 1e-4)
            ? color * (minBrightness / lumOut)
            : half3(minBrightness, minBrightness, minBrightness);
    }
    return color;
}

#endif // EASYPBR_COMMON_COLOR_INCLUDED
