// =============================================================================
//  BRDF_Translucency.hlsl
// -----------------------------------------------------------------------------
//  逆光時に透ける擬似サブサーフェス散乱（薄い皮膚・葉・布など）。
//  sssIntensity = 0 のとき pow/normalize 含め完全スキップ。
//  前提: URP Core.hlsl（UNITY_BRANCH）。
// =============================================================================
#ifndef EASYPBR_BRDF_TRANSLUCENCY_INCLUDED
#define EASYPBR_BRDF_TRANSLUCENCY_INCLUDED

half3 CalculateSSS(
    half3 detailNormalWS, float3 lightDirWS, half3 viewDirectionWS,
    half3 sssColor, float sssIntensity, float sssPower, float sssDistortion,
    float3 diffuseLightEnergy, float castShadow)
{
    half3 result = half3(0, 0, 0);
    UNITY_BRANCH
    if (sssIntensity > 0.0)
    {
        float3 backlightDir = normalize(lightDirWS + detailNormalWS * sssDistortion);
        float  backlightTerm = pow(saturate(dot(viewDirectionWS, -backlightDir)), sssPower);
        float  sssShadow = lerp(0.4, 1.0, castShadow);
        result = sssColor * backlightTerm * sssIntensity * diffuseLightEnergy * sssShadow;
    }
    return result;
}

#endif // EASYPBR_BRDF_TRANSLUCENCY_INCLUDED
