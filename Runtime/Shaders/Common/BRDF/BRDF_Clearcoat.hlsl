// =============================================================================
//  BRDF_Clearcoat.hlsl
// -----------------------------------------------------------------------------
//  クリアコート（薄い光沢層）＋イリデッセンス（薄膜の虹色）。
//  すべて加算専用で、下地の陰影・アルベドには一切干渉しない（黒ずみを出さない）。
//  視点依存のフレネルで斜めほど強まり、虹色の位相が視点角で動く＝カメラを振ると映える。
//
//  使い方: 下地ライティングを出し切った後の finalColor に += する。
//          コート法線は幾何法線(平滑)を渡すと艶がクリーンに走る（ディテール/グレイン非依存）。
//          マスクはテクスチャ（キャビティ/曲率を流用可）で「艶の置き場」を限定する。
//  前提: URP Core.hlsl（SafeNormalize）。
// =============================================================================
#ifndef EASYPBR_BRDF_CLEARCOAT_INCLUDED
#define EASYPBR_BRDF_CLEARCOAT_INCLUDED

// 滑らかな虹スペクトル（IQ風 cos パレット）。phase をずらして R/G/B を回す。
// phase は視点角などで動かすと、色がうっすら回転して薄膜らしくなる。
half3 IridescenceTint(float phase)
{
    const float TAU = 6.28318530718;
    return (half3)(0.5 + 0.5 * cos(TAU * (phase + float3(0.0, 0.33, 0.67))));
}

// 視点角からイリデッセンスのティントを作る（intensity 0 で白＝色なし）。
half3 ClearcoatIridescence(half NdotV, half intensity, half thickness, half shift)
{
    float phase = thickness * (1.0 - NdotV) + shift; // 斜めほど位相が進む＝視点で色が回る
    return lerp(half3(1, 1, 1), IridescenceTint(phase), intensity);
}

// クリアコートの直接ハイライト（メインライト等のライト依存項）。加算専用。
//   coatNormalWS : コート用法線（幾何法線=平滑 推奨）
//   coatStrength : 全体強度 / mask : 艶の置き場（0..1）
//   lightColor   : light.color * distanceAttenuation
//   castShadow   : 直接光なので影では消える（コートのハイライトは光が無いと出ない）
half3 CalculateClearcoat(
    half3 coatNormalWS, float3 lightDirWS, half3 viewDirWS,
    half coatSmoothness, half coatStrength, half mask,
    half3 lightColor, float castShadow,
    half iridescenceIntensity, half iridescenceThickness, half iridescenceShift)
{
    half3 result = half3(0, 0, 0);
    UNITY_BRANCH
    if (coatStrength * mask > 0.0)
    {
        half  NdotV = saturate(dot(coatNormalWS, viewDirWS));
        half3 h     = SafeNormalize(lightDirWS + viewDirWS);
        half  NdotH = saturate(dot(coatNormalWS, h));

        // 鋭い光沢ローブ。smoothness が高いほど引き締まる。
        half power = exp2(lerp(6.0, 12.0, coatSmoothness));
        half spec  = pow(NdotH, power);

        // 誘電体コートのフレネル（F0=0.04）。斜めで強まる＝視点依存の艶。
        half fresnel = 0.04 + 0.96 * pow(1.0 - NdotV, 5.0);

        // 薄膜の虹色（視点角で位相が動く）。
        half3 irid = ClearcoatIridescence(NdotV, iridescenceIntensity, iridescenceThickness, iridescenceShift);

        result = lightColor * spec * fresnel * irid * (coatStrength * mask * castShadow);
    }
    return result;
}

#endif // EASYPBR_BRDF_CLEARCOAT_INCLUDED
