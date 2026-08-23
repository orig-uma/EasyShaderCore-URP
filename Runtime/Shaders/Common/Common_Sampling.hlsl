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
// **展開されるループでは下の float2 版を使うこと** ── この版は θ に phi を
// 含むため、タップごとに実行時 sincos が残る。
float2 VogelDisk(int i, int count, float phi)
{
    float r     = sqrt((i + 0.5) / (float)count);
    float theta = i * 2.39996323 + phi;   // golden angle
    float s, c; sincos(theta, s, c);
    return float2(c, s) * r;
}

// 位相を (sin φ, cos φ) で受ける回転版（EasyToon Idol からの逆輸入。T-340）。
// 呼び出し側で sincos(phi) を 1 回だけ行うと、ここの sincos(i·黄金角) は
// UNITY_UNROLL / [unroll] 展開時に i が定数になり**コンパイル時に畳まれる**
// ── 実行時 sincos がタップ数ぶん消える。
// 加法定理そのものなので上の版と完全等価（Idol 側で全 24 タップ × 5 位相を
// 数値検証済み・最大差 2.8e-15）。
float2 VogelDisk(int i, int count, float2 sc)
{
    float r     = sqrt((i + 0.5) / (float)count);
    float theta = i * 2.39996323;      // **phi を含めない。** ここが畳まれるのが要点

    float s, c;
    sincos(theta, s, c);

    // (c, s) を位相ぶん回す。cos(θ+φ) = cosθcosφ - sinθsinφ / sin(θ+φ) = sinθcosφ + cosθsinφ
    return float2(c * sc.y - s * sc.x,
                  c * sc.x + s * sc.y) * r;
}

#endif // EASYPBR_COMMON_SAMPLING_INCLUDED
