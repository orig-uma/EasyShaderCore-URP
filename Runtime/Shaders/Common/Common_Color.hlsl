// =============================================================================
//  Common_Color.hlsl
// -----------------------------------------------------------------------------
//  純粋な色変換ユーティリティ。RGB<->HSV / Hue->RGB / HSV補正。
//  前提: なし。
// =============================================================================
#ifndef EASYPBR_COMMON_COLOR_INCLUDED
#define EASYPBR_COMMON_COLOR_INCLUDED

half3 RgbToHsv(half3 c)
{
    half4 K = half4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    half4 p = lerp(half4(c.bg, K.wz), half4(c.gb, K.xy), step(c.b, c.g));
    half4 q = lerp(half4(p.xyw, c.r), half4(c.r, p.yzx), step(p.x, c.r));
    float d = q.x - min(q.w, q.y);
    float e = 1.0e-10;
    return half3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
}

half3 HsvToRgb(half3 c)
{
    half4 K = half4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
    half3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
    return c.z * lerp(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);
}

// iridescence（虹色）用の Hue -> RGB。
half3 HueToRGB(float hue)
{
    return saturate(abs(frac(hue + half3(0.0, 2.0/3.0, 1.0/3.0)) * 6.0 - 3.0) - 1.0);
}

// 色相回転 / 彩度 / 明度のまとめてHSV補正。
half3 ApplyColorCorrection(half3 color, half hueShift, half saturation, half valueMulti)
{
    half3 hsv = RgbToHsv(color);
    hsv.x = frac(hsv.x + hueShift);
    hsv.y = saturate(hsv.y * saturation);
    hsv.z = hsv.z * valueMulti;
    return HsvToRgb(hsv);
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
