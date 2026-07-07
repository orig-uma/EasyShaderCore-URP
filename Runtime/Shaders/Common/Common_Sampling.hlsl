// =============================================================================
//  Common_Sampling.hlsl
// -----------------------------------------------------------------------------
//  シャドウ/AO/ブラー等で共有する純粋サンプリングパターン。
//  PCF実装そのものとは独立（座標を返すだけ）。
//  前提: なし。
// =============================================================================
#ifndef EASYPBR_COMMON_SAMPLING_INCLUDED
#define EASYPBR_COMMON_SAMPLING_INCLUDED

// Vogel ディスク: 低タップ数でも均一被覆（Poisson より縞が出にくい）。
// phi を毎ピクセル変えると回転ディスクになりバンディングが消える。
float2 VogelDisk(int i, int count, float phi)
{
    float r     = sqrt((i + 0.5) / (float)count);
    float theta = i * 2.39996323 + phi;   // golden angle
    float s, c; sincos(theta, s, c);
    return float2(c, s) * r;
}

#endif // EASYPBR_COMMON_SAMPLING_INCLUDED
