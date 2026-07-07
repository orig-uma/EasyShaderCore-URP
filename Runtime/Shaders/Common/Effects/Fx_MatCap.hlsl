// =============================================================================
//  Fx_MatCap.hlsl
// -----------------------------------------------------------------------------
//  MatCap（ビュー空間法線によるスフィアマップ）。
//  前提: URP Core.hlsl（GetWorldToViewMatrix）。
// =============================================================================
#ifndef EASYPBR_FX_MATCAP_INCLUDED
#define EASYPBR_FX_MATCAP_INCLUDED

float2 GetMatCapUV(half3 normalWS)
{
    float3 normalVS = mul((float3x3)GetWorldToViewMatrix(), normalWS);
    return normalVS.xy * 0.5 + 0.5;
}

// ライト連動 MatCap UV。
//  通常の MatCap はビュー固定で、ステージ照明が動いても映り込みが反応しない。
//  メインライトの画面内方向に合わせてサンプリングを回転させることで、
//  テクスチャに焼かれたハイライトが実際のライト方向へ追従する。
//  influence: 0 = 従来どおりビュー固定 / 1 = 完全にライト追従。
float2 GetMatCapUVLightAligned(half3 normalWS, float3 lightDirWS, float influence)
{
    float3x3 worldToView = (float3x3)GetWorldToViewMatrix();
    float3 normalVS = mul(worldToView, normalWS);

    UNITY_BRANCH
    if (influence > 0.0)
    {
        float2 lightVS = mul(worldToView, lightDirWS).xy;
        float  len = length(lightVS);
        if (len > 1e-4)
        {
            float2 dir = lightVS / len;                       // 光の画面内向き(cos,sin)
            // 基準（画面上方向 +Y）を dir へ向ける回転を normal.xy に適用。
            float2x2 rot = float2x2(dir.y, dir.x, -dir.x, dir.y);
            float2 rotated = mul(rot, normalVS.xy);
            normalVS.xy = lerp(normalVS.xy, rotated, influence);
        }
    }
    return normalVS.xy * 0.5 + 0.5;
}

// blendMode: 0 = Add, 1 = Multiply。
half3 ApplyMatCap(half3 finalColor, half3 matcapColor, float matcapIntensity, float blendMode)
{
    half3 addResult = finalColor + matcapColor * matcapIntensity;
    half3 mulResult = finalColor * lerp(half3(1.0, 1.0, 1.0), matcapColor, saturate(matcapIntensity));
    return (blendMode > 0.5) ? mulResult : addResult;
}

#endif // EASYPBR_FX_MATCAP_INCLUDED
