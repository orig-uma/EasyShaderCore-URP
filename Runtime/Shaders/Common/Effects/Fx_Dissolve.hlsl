// =============================================================================
//  Fx_Dissolve.hlsl
// -----------------------------------------------------------------------------
//  ディゾルブ（消失）の「計算とクリップ」だけを担う汎用ロジック。
//  ノイズ/グラデーションのサンプリングは呼び出し側で行い、値を詰めて渡す
//  （三平面・UV・LocalY 等の取得方法やマテリアルプロパティに依存しない）。
//  前提: なし（clip / smoothstep は組み込み）。
//
//  使い方:
//    DissolveInput di;
//    di.noise = <サンプルしたノイズ 0..1>;
//    di.grad  = <高さ等のグラデーション 0..1>;  // NONE タイプでは未使用
//    di.amount = _DissolveAmount; ...
//    ResolveDissolve(di, albedo, emission);
// =============================================================================
#ifndef EASYPBR_FX_DISSOLVE_INCLUDED
#define EASYPBR_FX_DISSOLVE_INCLUDED

struct DissolveInput
{
    float  noise;          // サンプル済みノイズ 0..1
    float  grad;           // サンプル済みグラデーション 0..1（NONE では無視）
    float  amount;         // 0..1 進行度
    float  edgeWidth;      // 境界幅
    float  noiseStrength;  // ノイズ寄与
    half3  edgeColor;      // 発光する最前線色
    half3  edgeColor2;     // 焦げ/縁の置換色
    float  invert;         // 0..1（>0.5 で符号反転）
    bool   edgeStep;       // Toon調の階調エッジ
    bool   isNoneType;     // ノイズのみで切る（grad を使わない）
};

void ResolveDissolve(DissolveInput d, inout half3 albedo, out half3 dissolveEmission)
{
    dissolveEmission = half3(0, 0, 0);

    float dissolveVal = d.isNoneType
        ? d.noise
        : d.grad + (d.noise - 0.5) * d.noiseStrength;

    float dMin = d.isNoneType ? 0.0 : -0.5 * d.noiseStrength;
    float dMax = d.isNoneType ? 1.0 :  1.0 + 0.5 * d.noiseStrength;

    float adjustedAmount = lerp(dMin - d.edgeWidth - 0.01, dMax + d.edgeWidth + 0.01, d.amount);
    float clipVal = dissolveVal - adjustedAmount;

    // invert>0.5 で符号反転（分岐レス）。
    clipVal *= lerp(1.0, -1.0, saturate(d.invert));

    clip(clipVal);

    float dissolveEdgeMask = smoothstep(0.0, d.edgeWidth + 0.0001, clipVal);
    float edgeFactor = 1.0 - dissolveEdgeMask;

    if (d.edgeStep)
        edgeFactor = ceil(edgeFactor * 2.0) / 2.0 * step(0.01, edgeFactor);

    albedo = lerp(albedo, d.edgeColor2, edgeFactor);

    float emissionMask = d.edgeStep ? step(0.9, edgeFactor)
                                    : smoothstep(0.5, 1.0, edgeFactor);
    dissolveEmission = d.edgeColor * emissionMask;
}

#endif // EASYPBR_FX_DISSOLVE_INCLUDED
