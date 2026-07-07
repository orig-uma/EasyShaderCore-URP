// =============================================================================
//  Reflection_URP.hlsl  (URP-coupled)
// -----------------------------------------------------------------------------
//  リフレクションプローブ（unity_SpecCube0）からの環境スペキュラ反射。
//  単一プローブ・ボックス投影なしの軽量サンプル（キャラ用途では十分）。
//  瞳・小物・エナメル・肌のスペキュラに「実際のステージ環境の映り込み」を足す。
//
//  ※ Common（純粋）ではなく URP 結合層に置く。unity_SpecCube0 等の URP グローバルに
//    依存するため。Shadow_HQ_URP.hlsl と同じ扱いで、必要なパスから個別 include する。
//  前提: URP Core.hlsl / Lighting.hlsl
//        （unity_SpecCube0 系・PerceptualRoughnessToMipmapLevel・DecodeHDREnvironment）。
// =============================================================================
#ifndef EASYPBR_REFLECTION_URP_INCLUDED
#define EASYPBR_REFLECTION_URP_INCLUDED

// 反射ベクトル方向の環境色（HDR デコード済み）。perceptualRoughness 0..1 で mip を選ぶ。
half3 EasyPBR_SampleEnvironment(half3 reflectVectorWS, half perceptualRoughness)
{
    half mip = PerceptualRoughnessToMipmapLevel(perceptualRoughness);
    half4 encoded = SAMPLE_TEXTURECUBE_LOD(unity_SpecCube0, samplerunity_SpecCube0, reflectVectorWS, mip);
    return DecodeHDREnvironment(encoded, unity_SpecCube0_HDR);
}

// フレネル重み付きの環境反射寄与。finalColor へ加算する想定。
//  f0:        基準反射率（誘電体は 0.04 前後、瞳の濡れ感はやや高め）
//  strength:  全体強度（0 で無効）
//  occlusion: 反射の遮蔽（AO・スペキュラマスク等を掛けて渡す）
half3 EasyPBR_EnvironmentReflection(half3 normalWS, half3 viewDirectionWS,
                                    half perceptualRoughness, half f0,
                                    half strength, half occlusion)
{
    half3 reflectVector = reflect(-viewDirectionWS, normalWS);
    half3 envColor = EasyPBR_SampleEnvironment(reflectVector, perceptualRoughness);

    // 地平線オクルージョン: 反射ベクトルが面の裏側へ潜るぶんを減衰し、
    // グレージング角で「面の裏」を拾って薄光りするのを抑える。
    half horizon = saturate(1.0 + dot(reflectVector, normalWS));
    horizon *= horizon;

    half NdotV   = saturate(dot(normalWS, viewDirectionWS));
    half fresnel = f0 + (1.0 - f0) * pow(1.0 - NdotV, 5.0);

    return envColor * fresnel * strength * occlusion * horizon;
}

#endif // EASYPBR_REFLECTION_URP_INCLUDED
