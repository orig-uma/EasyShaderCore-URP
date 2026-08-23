// =============================================================================
//  EasyPbrFaceSdfBaker.cs  (Editor only)
// -----------------------------------------------------------------------------
//  顔 SDF Shadow マップのベイク。4ch: R=右光 / G=左光 / B=上光 / A=下光。
//
//  T-346: 距離場ブレンド整形を追加。頂点スイープの生の出力は「頂点法線 →
//  重心座標補間」なので、影境界の等値線にメッシュのポリゴン割りと法線ノイズが
//  そのまま出る（＝線がガタつく）。手描き SDF ツールの本質工程である
//  「白黒マスク → 距離場変換 → ブレンド」を画像空間で内蔵し、等値線を
//  距離幾何で丸め直すことで、外部ツール無しでも滑らかな線を焼けるようにした。
// =============================================================================
using UnityEditor;
using UnityEngine;

namespace Origuma.EasyShaderCore.Editor
{
    public static class EasyPbrFaceSdfBaker
    {
        public struct Settings
        {
            public int   resolution;
            public int   angleSteps;
            public float ndotlThreshold;
            public bool  useCastShadow;
            public float castDistance;
            public bool  flipForward;
            public float xAxisTilt;   // 度。左右(R/G)スイープ光の仰角（+で上から差す光として焼く）
            public int   smooth;
            public int   blur;
            public int   dilate;
            public bool  dfBlend;     // 距離場ブレンド整形（等値線を画像空間で丸め直す）
            public float dfSpread;    // 線の丸め半径（texel）。大きいほど滑らか・細部が消える
            public bool  pack16;      // 右光 1ch を R×256+G の 16bit で焼く（ミラー U 規約の 1ch 経路用）
        }

        public static Settings Default => new Settings
        {
            resolution = 1024, angleSteps = 90, ndotlThreshold = 0.0f,
            useCastShadow = false, castDistance = 0.15f, flipForward = false,
            xAxisTilt = 0f, smooth = 1, blur = 1, dilate = 4,
            dfBlend = true, dfSpread = 4f, pack16 = false
        };

        // 距離場ブレンドの等値線の本数。texel の距離から連続値を再構成するので
        // 出力値は 1/64 に量子化されない（角度方向も滑らかなまま）。増やすほど
        // 再現度が上がるが処理時間は線形に伸びる（64 で 1024px 4ch ≈ 十数秒）。
        private const int DfLevels = 64;

        // 顔SDFは4チャンネルで焼く: R=右 / G=左 / B=上 / A=下。ランタイムはミラー不要＝
        // 左右非対称の顔（傷跡・マーク等）にも対応。
        public static bool Bake(GameObject root, Material material, Settings s)
        {
            if (s.pack16)
            {
                // 1ch 16bit: 右光スイープのみ。左は「U をミラーして読む」規約
                //（lilToon 系の 1ch 経路と同じ＝左右対称の顔が前提）で作られるため
                // 焼くのは片側だけでよい。R×256+G のデコードは RG に線形なので、
                // バイリニア補間・ブラーを通しても値が壊れない。
                // ダイレート・ブラーは RunBake に任せず float 域で済ませる
                //（パッキング後の 8bit チャンネル別処理は 16bit 精度を壊す）。
                // 遮蔽はマテリアルのサブメッシュ限定（T-355）: 統合メッシュの
                // 睫毛・眉（別マテリアル）にレイが当たると目の周りに恒久影が
                // 焼き込まれる。鼻・唇の落ち影は同マテリアルなので残る。
                return EasyPbrBakeCore.RunBake(root, material, s.resolution, s.smooth, 0, 0,
                    "FaceSDF", "_FaceSDFMap", "_UseFaceSDF", needsCollider: true,
                    (r, m) => SdfSweepAxis(r, m, s, Vector3.right, true),
                    occluderSubmeshesOnly: true,
                    postProcess: (px, cov, res) => PostProcess16(px, cov, res, s));
            }

            if (s.dfBlend)
            {
                return EasyPbrBakeCore.RunBake(root, material, s.resolution, s.smooth, 0, 0,
                    "FaceSDF", "_FaceSDFMap", "_UseFaceSDF", needsCollider: true,
                    (r, m) => SdfSweepAxis(r, m, s, Vector3.right, true),
                    (r, m) => SdfSweepAxis(r, m, s, Vector3.left,  true),
                    (r, m) => SdfSweepAxis(r, m, s, Vector3.up,    false),
                    (r, m) => SdfSweepAxis(r, m, s, Vector3.down,  false),
                    occluderSubmeshesOnly: true,
                    postProcess: (px, cov, res) => PostProcess4(px, cov, res, s));
            }

            // 従来経路（頂点スイープそのまま）。比較・退避用に残す。
            return EasyPbrBakeCore.RunBake(root, material, s.resolution, s.smooth, s.dilate, s.blur,
                "FaceSDF", "_FaceSDFMap", "_UseFaceSDF", needsCollider: true,
                (r, m) => SdfSweepAxis(r, m, s, Vector3.right, true),  // R: 右（X Axis Tilt 適用）
                (r, m) => SdfSweepAxis(r, m, s, Vector3.left,  true),  // G: 左（X Axis Tilt 適用）
                (r, m) => SdfSweepAxis(r, m, s, Vector3.up,    false), // B: 上
                (r, m) => SdfSweepAxis(r, m, s, Vector3.down,  false), // A: 下
                occluderSubmeshesOnly: true);
        }

        // ------------------------------------------------------------------
        //  距離場ブレンド（画像空間の後処理）
        // ------------------------------------------------------------------

        private static Color32[] PostProcess4(Color32[] px, bool[] covered, int res, Settings s)
        {
            var outPx = new Color32[px.Length];
            for (var ch = 0; ch < 4; ch++)
            {
                var f = ExtractChannel(px, ch);
                DistanceFieldBlend(f, covered, res, s.dfSpread, ch, 4);
                BoxBlur(f, res, s.blur);
                for (var i = 0; i < f.Length; i++)
                {
                    var b = (byte)(Mathf.Clamp01(f[i]) * 255f + 0.5f);
                    switch (ch)
                    {
                        case 0: outPx[i].r = b; break;
                        case 1: outPx[i].g = b; break;
                        case 2: outPx[i].b = b; break;
                        default: outPx[i].a = b; break;
                    }
                }
            }
            return outPx;
        }

        private static Color32[] PostProcess16(Color32[] px, bool[] covered, int res, Settings s)
        {
            var f = ExtractChannel(px, 0);
            DistanceFieldBlend(f, covered, res, s.dfSpread, 0, 1);
            BoxBlur(f, res, s.blur);

            var outPx = new Color32[px.Length];
            for (var i = 0; i < f.Length; i++)
            {
                // **1ch 経路の規約は内部規約の反転**（lilToon 系: 白 = 最後まで
                // 照らされる側。ランタイムは lit ⇔ sdf > 1−(F·L·0.5+0.5)）。
                // 内部の頂点スイープは「白 = すぐ陰る側」（4ch 経路と同じ向き）で
                // 持っているので、ここで 1−f に反転して格納する。反転を忘れると
                // すぐ陰るはずの顎下〜首が「永遠に照らされる」と誤読され、
                // 顎から首にかけて影が入らなくなる（実際に出た不具合）。
                //
                // R が上位・G が下位（値 = (R×256+G)/65535）。8bit 単チャンネルだと
                // 閾値が約 0.7 度刻みの階段になり、ライトを回すと影の線がカクつく。
                var u = (int)(Mathf.Clamp01(1f - f[i]) * 65535f + 0.5f);
                outPx[i] = new Color32((byte)(u >> 8), (byte)(u & 0xFF), 0, 255);
            }
            return outPx;
        }

        /// <summary>
        /// 閾値マップ f を「等値線ごとの符号付き距離場の重ね合わせ」で作り直す。
        /// 各等値線 θ_k について 2 値マスク (f ≥ θ_k) の内外それぞれへの chamfer
        /// 距離を取り、符号付き距離のランプを 0..1 に緩和したものを平均する。
        /// 等値線の形が texel 距離の幾何で決まるため、頂点補間由来のガタつきが
        /// 丸まる。被覆外の texel にも距離伝播で自然な外挿値が入る＝ダイレート不要。
        ///
        /// **ランプ幅は固定にしない。** 固定幅 spread だと、等値線同士が spread より
        /// 離れている平坦部で全ランプが 0/1 に飽和し、出力が 1/DfLevels 刻みに
        /// 量子化される（＝段々畑。ライトを回すと影の線が等値線ごとに引っかかり、
        /// 16bit 出力も無意味になる）。そこで幅を「隣の等値線までの局所間隔」まで
        /// 広げる: 間隔いっぱいのランプは隣同士がちょうど連結して区分線形の連続
        /// 再構成になり、平坦部は元の値が保存される。spread はその下限
        /// （＝線の形を丸める半径）としてだけ効く。
        /// </summary>
        private static void DistanceFieldBlend(float[] f, bool[] covered, int res,
                                               float spread, int chIndex, int chCount)
        {
            spread = Mathf.Max(0.5f, spread);
            var n = f.Length;
            var acc    = new float[n];
            var dIn    = new float[n];
            var dOut   = new float[n];
            var sdPrev = new float[n];   // 等値線 k-1 の符号付き距離
            var sdCur  = new float[n];   // 等値線 k
            var sdNext = new float[n];   // 等値線 k+1
            const float Inf = 1e9f;

            // 等値線 k の符号付き距離（内側 +・外側 −）を dst へ。全 texel が内側／
            // 外側だけの等値線は片側の種が無く距離が Inf になるので、±res に丸めて
            // おく（ランプ幅 ≤ res のため 0.5 + res/(2·res) = 1 で正しく飽和する）。
            void ComputeSd(int k, float[] dst)
            {
                var theta = (k + 0.5f) / DfLevels;
                for (var i = 0; i < n; i++)
                {
                    // 被覆外は種にしない（UV アイランドの外から等値線が引っ張られない）
                    var inside = covered[i] && f[i] >= theta;
                    dOut[i] = inside ? 0f : Inf;
                    dIn[i]  = (covered[i] && !inside) ? 0f : Inf;
                }
                Chamfer(dOut, res);
                Chamfer(dIn, res);
                for (var i = 0; i < n; i++)
                    dst[i] = Mathf.Clamp(dIn[i] - dOut[i], -res, res);
            }

            ComputeSd(0, sdCur);
            if (DfLevels > 1) ComputeSd(1, sdNext);

            for (var k = 0; k < DfLevels; k++)
            {
                EditorUtility.DisplayProgressBar("EasyPBR Baker",
                    $"距離場ブレンド中... ({chIndex + 1}/{chCount})",
                    0.8f + 0.05f * (chIndex + (k + 1f) / DfLevels) / chCount);

                // マスクは入れ子（θ が上がると内側が縮む）なので sd は k について
                // 単調減少。局所の等値線間隔 ≈ (sd_{k-1} − sd_{k+1}) / 2。端の
                // 等値線は片側しか無いので自身で代用（半分の間隔になるが、θ≈0/1 の
                // 端は絵にほぼ出ない）。
                var prevBuf = k == 0             ? sdCur : sdPrev;
                var nextBuf = k == DfLevels - 1  ? sdCur : sdNext;
                for (var i = 0; i < n; i++)
                {
                    var w = Mathf.Max(spread, 0.5f * (prevBuf[i] - nextBuf[i]));
                    acc[i] += Mathf.Clamp01(0.5f + sdCur[i] / (2f * w));
                }

                if (k < DfLevels - 1)
                {
                    var tmp = sdPrev; sdPrev = sdCur; sdCur = sdNext; sdNext = tmp;
                    if (k + 2 < DfLevels) ComputeSd(k + 2, sdNext);
                }
            }

            var norm = 1f / DfLevels;
            for (var i = 0; i < n; i++) f[i] = acc[i] * norm;
        }

        /// <summary>2 パス chamfer 距離変換（3-4 近似・斜め √2）。種 = 値 0 の texel。</summary>
        private static void Chamfer(float[] d, int res)
        {
            const float A = 1f, B = 1.41421356f;

            for (var y = 0; y < res; y++)
            for (var x = 0; x < res; x++)
            {
                var i = y * res + x;
                var v = d[i];
                if (x > 0) v = Mathf.Min(v, d[i - 1] + A);
                if (y > 0)
                {
                    v = Mathf.Min(v, d[i - res] + A);
                    if (x > 0)       v = Mathf.Min(v, d[i - res - 1] + B);
                    if (x < res - 1) v = Mathf.Min(v, d[i - res + 1] + B);
                }
                d[i] = v;
            }

            for (var y = res - 1; y >= 0; y--)
            for (var x = res - 1; x >= 0; x--)
            {
                var i = y * res + x;
                var v = d[i];
                if (x < res - 1) v = Mathf.Min(v, d[i + 1] + A);
                if (y < res - 1)
                {
                    v = Mathf.Min(v, d[i + res] + A);
                    if (x < res - 1) v = Mathf.Min(v, d[i + res + 1] + B);
                    if (x > 0)       v = Mathf.Min(v, d[i + res - 1] + B);
                }
                d[i] = v;
            }
        }

        private static float[] ExtractChannel(Color32[] px, int ch)
        {
            var f = new float[px.Length];
            for (var i = 0; i < px.Length; i++)
            {
                var c = px[i];
                var b = ch == 0 ? c.r : ch == 1 ? c.g : ch == 2 ? c.b : c.a;
                f[i] = b / 255f;
            }
            return f;
        }

        /// <summary>
        /// 分離ボックスブラー（float 域）。距離場ブレンド後は全 texel に有効値が
        /// あるので被覆判定は不要。16bit パッキング前に掛けることで、8bit の
        /// チャンネル別ブラーが起こす精度崩れ（上位バイトの丸めが実効 8bit 未満に
        /// なる）を避ける。
        /// </summary>
        private static void BoxBlur(float[] f, int res, int radius)
        {
            if (radius <= 0) return;
            var tmp = new float[f.Length];

            for (var y = 0; y < res; y++)
            for (var x = 0; x < res; x++)
            {
                var sum = 0f; var num = 0;
                for (var dx = -radius; dx <= radius; dx++)
                {
                    var nx = x + dx;
                    if (nx < 0 || nx >= res) continue;
                    sum += f[y * res + nx]; num++;
                }
                tmp[y * res + x] = sum / num;
            }

            for (var x = 0; x < res; x++)
            for (var y = 0; y < res; y++)
            {
                var sum = 0f; var num = 0;
                for (var dy = -radius; dy <= radius; dy++)
                {
                    var ny = y + dy;
                    if (ny < 0 || ny >= res) continue;
                    sum += tmp[ny * res + x]; num++;
                }
                f[y * res + x] = sum / num;
            }
        }

        // ------------------------------------------------------------------
        //  頂点スイープ（閾値マップの素材）
        // ------------------------------------------------------------------

        private static float[] SdfSweepAxis(Renderer r, Mesh m, Settings s, Vector3 localAxis, bool applyTilt)
        {
            // 前方ベクトルの取得（フリップ設定を考慮）
            var fwd = r.transform.forward * (s.flipForward ? -1f : 1f);

            // 指定されたローカル軸（右・左・上・下）をワールド空間の軸に変換
            var sweepAxis = r.transform.TransformDirection(localAxis).normalized;

            // 左右(R/G)のスイープ面を顔 Up 方向へ傾ける。水平光の想定だと顎下〜首の境界が
            // 実際のライト（やや上から差す）とずれ、首まわりの影が不自然になるため。
            // 左右どちらの軸も同じ「上」へ倒すので左右対称は保たれ、Up は fwd と直交する
            // ＝ sweepAxis は fwd と直交のまま（ライトベクトルは単位ベクトルのまま）。
            if (applyTilt && Mathf.Abs(s.xAxisTilt) > 1e-4f)
            {
                var t = s.xAxisTilt * Mathf.Deg2Rad;
                sweepAxis = (sweepAxis * Mathf.Cos(t) + r.transform.up * Mathf.Sin(t)).normalized;
            }

            return ComputeVertexSdf(r.transform, m, fwd, sweepAxis, s);
        }

        private static float[] ComputeVertexSdf(Transform xf, Mesh mesh, Vector3 fwd, Vector3 sweepAxis, Settings s)
        {
            var verts = mesh.vertices;
            var n = verts.Length;
            var result = new float[n];
            var mask = 1 << EasyPbrBakeCore.BakeLayer;
            var steps = Mathf.Max(2, s.angleSteps);
            var fallbackUp = xf.up;

            // **法線は位置で溶接してから使う（T-372）。** ミラーで作られた顔は
            // 中央線の頂点が左右で分割されていることが多く、そのままだと中央で
            // 法線が不連続＝遷移角がジャンプし、SDF の UV 中央に**縦一直線の段差**
            // が焼き込まれる（実測: 4 テクセルで 0.05〜0.16 のジャンプ＝周囲の
            // 勾配の 30〜50 倍）。光が 80〜90 度のとき、その段が額から顎までの
            // 硬い割線として絵に出る。UV 継ぎ目・硬エッジの分割も同じ理由で溶かす。
            var welded = EasyPbrBakeCore.WeldedNormals(verts, mesh.normals, Vector3.up);

            for (var i = 0; i < n; i++)
            {
                // 頂点のワールド座標と法線の計算
                var N = xf.TransformDirection(welded[i]).normalized;
                var originWS = xf.TransformPoint(verts[i]);

                // セルフシャドウのノイズを防ぐための微小なオフセット
                var bias = N * 1e-3f;

                var sdf = 0f;
                var prevLit = true;

                for (var st = 0; st < steps; st++)
                {
                    // 0 ～ PI (180度) までスイープ
                    var th = Mathf.PI * st / (steps - 1);

                    // fwd(正面) から始まり、sweepAxis(指定軸) を経由して、-fwd(背面) へ向かうライトベクトル
                    var L = (fwd * Mathf.Cos(th) + sweepAxis * Mathf.Sin(th)).normalized;

                    // ライト方向が法線の表側にあるか判定
                    var lit = Vector3.Dot(N, L) > s.ndotlThreshold;

                    // ジオメトリによるキャストシャドウ判定
                    if (lit && s.useCastShadow)
                    {
                        if (Physics.Raycast(originWS + bias, L, s.castDistance, mask))
                        {
                            lit = false;
                        }
                    }

                    // 正面(0度)の時点で既に影になっている場合は、常に影(1.0)とする
                    if (st == 0 && !lit)
                    {
                        sdf = 1f;
                        break;
                    }

                    // 光が当たっている状態から影に切り替わった瞬間を捉える
                    if (prevLit && !lit)
                    {
                        // -1 ～ 1 の Cos カーブを 0 ～ 1 にマッピングして SDF 値とする
                        sdf = Mathf.Cos(th) * 0.5f + 0.5f;
                        break;
                    }

                    prevLit = lit;
                }

                result[i] = sdf;
            }

            return result;
        }
    }
}
