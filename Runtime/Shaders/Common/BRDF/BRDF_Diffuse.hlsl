// =============================================================================
//  BRDF_Diffuse.hlsl
// -----------------------------------------------------------------------------
//  ディフューズ整形の純粋関数群。
//   - Half Lambert
//   - トゥーン量子化ランプ（キーワード非依存。toon/smooth は呼び出し側で選択）
//   - 影色ブレンド
//   - 落ち影(shadow map)のディザ＆ソフトランプ整形
//  顔マスク等のキャラ固有ポリシーはここには持たない。
//  前提: なし（fwidth は組み込み）。
// =============================================================================
#ifndef EASYPBR_BRDF_DIFFUSE_INCLUDED
#define EASYPBR_BRDF_DIFFUSE_INCLUDED

// Half Lambert (Valve流): NdotL を 0..1 に再マップして陰側を持ち上げる。
float HalfLambert(float ndotl, float wrap)
{
    return saturate((ndotl + wrap) / (1.0 + wrap));
}

// トゥーン量子化: fwidth で 1px のアンチエイリアス幅を確保。
float ToonRamp(float halfLambert, float toonStep, float toonFeather)
{
    float softness = max(fwidth(halfLambert), toonFeather);
    return smoothstep(toonStep - softness, toonStep + softness, halfLambert);
}

// toon/smooth をフラグで選ぶ便宜版（キーワードは呼び出し側で bool に解決）。
float ShadeRamp(float halfLambert, bool useToon, float toonStep, float toonFeather)
{
    return useToon ? ToonRamp(halfLambert, toonStep, toonFeather) : halfLambert;
}

// ベースカラーに影色(Tint)を乗算し、finalShade で明暗をブレンド。
half3 ShadedAlbedo(half3 baseColor, half3 shadowColorTint, float finalShade)
{
    half3 shadedBaseColor = baseColor * shadowColorTint;
    return lerp(shadedBaseColor, baseColor, finalShade);
}

// -----------------------------------------------------------------------------
//  ApplyTerminatorScatter
//   明暗境界（ターミネータ）を scatterColor 方向へ滲ませる pre-integrated
//   skin scattering の近似。finalShade（0=影, 1=光）が 0.5 を跨ぐ遷移域で
//   バンドが立ち、トゥーンランプ・落ち影ペナンブラ・SDF 顔影のどの境界にも
//   同じ式で乗る。width: 0 = 細い、1 = 広い（バンド形状の指数を制御）。
// -----------------------------------------------------------------------------
half3 ApplyTerminatorScatter(half3 diffuseColor, half3 albedo, half3 scatterColor,
                             float finalShade, float width, float amount)
{
    float band = saturate(4.0 * finalShade * (1.0 - finalShade));
    band = pow(band, lerp(4.0, 0.5, width));
    return lerp(diffuseColor, albedo * scatterColor, band * amount);
}

// -----------------------------------------------------------------------------
//  ResolveCastShadow
//   落ち影(shadow map)専用のソフトランプ。0(影)..1(光)。
//   penumbraReady = true（PCF/PCSS で連続ペナンブラ生成済み）の場合は
//   ディザ＆再量子化をスキップする。キャラ固有の顔マスク適用は呼び出し側で行う。
// -----------------------------------------------------------------------------
float ResolveCastShadow(float shadowAttenuation, float receiveShadowMask, float receiveShadowStrength,
                        float ditherValue, float shadowDither, float shadowMapSoftness,
                        bool penumbraReady)
{
    float rawShadow = lerp(1.0, shadowAttenuation, receiveShadowMask * receiveShadowStrength);

    if (penumbraReady)
        return rawShadow;

    float ditheredShadow = rawShadow + (ditherValue - 0.5) * shadowDither * 0.1;
    return smoothstep(0.5 - shadowMapSoftness * 0.5, 0.5 + shadowMapSoftness * 0.5, ditheredShadow);
}

#endif // EASYPBR_BRDF_DIFFUSE_INCLUDED
