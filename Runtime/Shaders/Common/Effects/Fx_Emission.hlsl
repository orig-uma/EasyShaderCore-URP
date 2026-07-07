// =============================================================================
//  Fx_Emission.hlsl
// -----------------------------------------------------------------------------
//  自己発光（map * color * intensity）。
//  前提: なし。
// =============================================================================
#ifndef EASYPBR_FX_EMISSION_INCLUDED
#define EASYPBR_FX_EMISSION_INCLUDED

half3 CalculateEmission(half3 emissionMapColor, half3 emissionColor, float emissionIntensity)
{
    return emissionMapColor * emissionColor * emissionIntensity;
}

#endif // EASYPBR_FX_EMISSION_INCLUDED
