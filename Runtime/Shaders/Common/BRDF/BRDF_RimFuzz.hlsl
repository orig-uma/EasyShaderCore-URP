// =============================================================================
//  BRDF_RimFuzz.hlsl
// -----------------------------------------------------------------------------
//  輪郭まわりの光沢: フレネル項 / リムライト / ピーチファズ（産毛）。
//  fresnel は frag 側でライト非依存に 1 回算出し各ライトへ渡す想定。
//  前提: URP Core.hlsl（UNITY_BRANCH）。
// =============================================================================
#ifndef EASYPBR_BRDF_RIMFUZZ_INCLUDED
#define EASYPBR_BRDF_RIMFUZZ_INCLUDED

// Rim / Peach Fuzz 用フレネル項 (1-NdotV)^power（ライト非依存）。
// rimThickness(0..1): 0 = 極細(指数12)、1 = 極太(指数0.5)。
void GetFresnelTerms(float ndotv, float rimIntensity, float rimThickness,
                     float fuzzIntensity, float fuzzPower,
                     out float rimFresnel, out float fuzzFresnel)
{
    rimFresnel = 0.0;
    UNITY_BRANCH
    if (rimIntensity > 0.0)
    {
        float actualPower = lerp(12.0, 0.5, rimThickness);
        rimFresnel = pow(1.0 - ndotv, actualPower);
    }

    fuzzFresnel = 0.0;
    UNITY_BRANCH
    if (fuzzIntensity > 0.0)
    {
        fuzzFresnel = pow(saturate(1.0 - ndotv), fuzzPower);
    }
}

half3 CalculateRimLight(half3 rimColor, float rimFresnel, float rimIntensity,
                        float3 diffuseLightEnergy, float ndotlSpecular, float castShadow)
{
    float rimLightMask = saturate(ndotlSpecular * 5.0) * castShadow;
    return rimColor * rimFresnel * rimIntensity * diffuseLightEnergy * rimLightMask;
}

half3 CalculatePeachFuzz(half3 fuzzColor, float fuzzFresnel, float fuzzIntensity,
                         float3 diffuseLightEnergy, float ndotlSpecular, float castShadow)
{
    float fuzzMask = fuzzFresnel * saturate(ndotlSpecular) * castShadow;
    return fuzzColor * fuzzMask * fuzzIntensity * diffuseLightEnergy;
}

#endif // EASYPBR_BRDF_RIMFUZZ_INCLUDED
