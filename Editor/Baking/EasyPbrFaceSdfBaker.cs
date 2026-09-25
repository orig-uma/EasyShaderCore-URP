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
            // 0.3.6: pack16 と併用。上光スイープ（正面 → 真上 → 背面）も 16bit で焼き、
            // B×256+A に詰める（RG = 横 / BA = 縦の 2ch 16bit）。横スイープは水平面内なので
            // 光の仰角を知らない ── 頭が俯く・トップライトでは鼻下・唇・顎裏が「正面光」と
            // 読まれて明るいまま残る。縦スイープはその仰角方向の遷移角を持つ。
            // 下光（仰角 < 0）は焼かない: ランタイムは仰角 0 として読む（横だけで決まる）。
            public bool  pack16Vertical;

            // ---- プロキシ法線（0.3.2）----
            // 顔メッシュの法線の代わりに、頭に合わせた楕円体やプロキシメッシュの法線で影の遷移角を
            // 求める。ローポリの顔は法線がポリゴンごとに折れて SDF の等値線がガタつくが、楕円体なら
            // 完全に滑らかな線になる。UV は顔メッシュのものをそのまま使うので UV 合わせは要らない
            //（頂点の位置からプロキシの法線を引くだけ）。
            public int       proxyMode;      // 0 = メッシュの法線 / 1 = 楕円体 / 2 = プロキシメッシュ
            public Vector3   proxyCenterWS;  // 楕円体の中心（ワールド）。プロキシメッシュのレイの原点にも使う
            public Vector3   proxyRadii;     // 楕円体の半径（ワールド、m）
            public Mesh      proxyMesh;      // proxyMode 2 のメッシュ
            public Matrix4x4 proxyMatrix;    // proxyMesh のローカル → ワールド
            public float     proxyBlend;     // 0 = メッシュの法線 … 1 = プロキシの法線（鼻の影を少し残すなら 0.7 前後）
            // 中心・半径を、焼くメッシュの頂点（バインドポーズをワールドへ写したもの）から自動で
            // 決める。Renderer.bounds はスキン後の姿勢の箱で、Baker が使う頂点とはずれる（実測で
            // 6 cm。半径 9 cm の頭では法線が大きく傾く）ので、既定は自動に任せる。
            public bool      proxyAutoCenter;
            public bool      proxyAutoRadii;
            // ---- 楕円体の形のバリエーション（0.3.3。「ただの楕円だと顔っぽくない」への対応）----
            // 楕円体を顔のローカル軸（右・上・前）で変形する。どれも 0 で従来の楕円体。
            //   proxyTaper   : 卵型。上（+）ほど幅・奥行きを広げ、顎側を細くする。0.3 で顎が 0.7 倍
            //   proxyFlatten : 前半分を平らにする（超楕円の指数 2 → 8）。1 でほぼ円盤（正面が平面、縁が丸い）。
            //                  後ろ半分は丸いまま（z = 0 の面で法線は連続）
            //   proxyDetail  : 部分的にメッシュの法線を使う量。メッシュの法線がプロキシの法線から
            //                  proxyDetailAngle 度以上ずれる場所（鼻・眉・唇）だけ実際の法線に戻す。
            //                  頬・額など滑らかな場所はプロキシのまま
            public float     proxyTaper;
            public float     proxyFlatten;
            public float     proxyDetail;
            public float     proxyDetailAngle; // 度。既定 35
        }

        public static Settings Default => new Settings
        {
            resolution = 1024, angleSteps = 90, ndotlThreshold = 0.0f,
            useCastShadow = false, castDistance = 0.15f, flipForward = false,
            xAxisTilt = 0f, smooth = 1, blur = 1, dilate = 4,
            dfBlend = true, dfSpread = 4f, pack16 = false, pack16Vertical = false,
            proxyMode = 0, proxyCenterWS = Vector3.zero, proxyRadii = new Vector3(0.09f, 0.11f, 0.09f),
            proxyMesh = null, proxyMatrix = Matrix4x4.identity, proxyBlend = 1f,
            proxyAutoCenter = true, proxyAutoRadii = true,
            proxyTaper = 0f, proxyFlatten = 0f, proxyDetail = 0f, proxyDetailAngle = 35f
        };

        // プロキシメッシュ（ワールド空間に展開したもの）。Bake の間だけ持つ。
        private static Vector3[] s_proxyV, s_proxyN;
        private static int[]     s_proxyT;

        private static void PrepareProxy(Settings s)
        {
            s_proxyV = null; s_proxyN = null; s_proxyT = null;
            if (s.proxyMode != 2 || s.proxyMesh == null) return;
            var v = s.proxyMesh.vertices; var nrm = s.proxyMesh.normals; var t = s.proxyMesh.triangles;
            if (v == null || v.Length == 0 || t == null || t.Length < 3) return;
            var wv = new Vector3[v.Length]; var wn = new Vector3[v.Length];
            var nm = s.proxyMatrix.inverse.transpose;
            bool hasN = nrm != null && nrm.Length == v.Length;
            for (var i = 0; i < v.Length; i++)
            {
                wv[i] = s.proxyMatrix.MultiplyPoint3x4(v[i]);
                wn[i] = hasN ? nm.MultiplyVector(nrm[i]).normalized : Vector3.up;
            }
            s_proxyV = wv; s_proxyN = wn; s_proxyT = t;
        }

        /// <summary>
        /// 頂点位置 posWS に対応するプロキシの法線。楕円体は解析、メッシュは中心からのレイの
        /// **一番遠い**交点（凸なら外側の面）の補間法線。見つからなければメッシュの法線に戻す。
        /// </summary>
        // 顔のローカル軸（右・上・前。ワールド）。形のバリエーション（卵型・前を平ら）はこの軸で変形する
        private static Vector3 s_proxyRight = Vector3.right, s_proxyUp = Vector3.up, s_proxyFwd = Vector3.forward;

        private static Vector3 ProxyNormal(Vector3 meshN, Vector3 posWS, Settings s)
        {
            if (s.proxyMode == 0 || s.proxyBlend <= 0f) return meshN;
            Vector3 pn = meshN;
            if (s.proxyMode == 1)
            {
                if (!EllipsoidNormal(posWS, s, out pn)) return meshN;
            }
            else if (s.proxyMode == 2)
            {
                if (s_proxyV == null || !ProxyMeshNormal(posWS, s.proxyCenterWS, out pn)) return meshN;
            }
            // 部分的にメッシュの法線へ戻す: プロキシから大きくずれる場所（鼻・眉・唇）だけ。
            // 角度差でなだらかに（開始角から +15 度で完全にメッシュ）。頬・額はプロキシのまま
            if (s.proxyDetail > 0f)
            {
                var ang = Vector3.Angle(meshN, pn);
                var w = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(s.proxyDetailAngle, s.proxyDetailAngle + 15f, ang));
                pn = Vector3.Slerp(pn, meshN, Mathf.Clamp01(s.proxyDetail) * w);
            }
            return Vector3.Lerp(meshN, pn, s.proxyBlend).normalized;
        }

        /// <summary>
        /// 楕円体（＋卵型・前を平らのバリエーション）の法線。陰関数 F(p) = 0 の勾配を中心差分で取る。
        /// 楕円体だけなら解析解 (d/r²) と一致し、超楕円（|z/rz|^p）や卵型（y で幅を変える）でも
        /// 同じ式で扱えるので、形を増やしても法線の取り方は一つで済む。
        /// </summary>
        private static bool EllipsoidNormal(Vector3 posWS, Settings s, out Vector3 n)
        {
            var d = posWS - s.proxyCenterWS;
            var l = new Vector3(Vector3.Dot(d, s_proxyRight), Vector3.Dot(d, s_proxyUp), Vector3.Dot(d, s_proxyFwd));
            var r = Vector3.Max(s.proxyRadii, Vector3.one * 1e-4f);
            var h = Mathf.Min(r.x, Mathf.Min(r.y, r.z)) * 1e-3f;
            var g = new Vector3(
                (ProxyField(l + Vector3.right   * h, r, s) - ProxyField(l - Vector3.right   * h, r, s)),
                (ProxyField(l + Vector3.up      * h, r, s) - ProxyField(l - Vector3.up      * h, r, s)),
                (ProxyField(l + Vector3.forward * h, r, s) - ProxyField(l - Vector3.forward * h, r, s)));
            if (g.sqrMagnitude < 1e-20f) { n = Vector3.zero; return false; }
            g.Normalize();
            n = (s_proxyRight * g.x + s_proxyUp * g.y + s_proxyFwd * g.z).normalized;
            return true;
        }

        // 陰関数。楕円体 = |x/rx|² + |y/ry|² + |z/rz|² − 1。
        //   卵型: 幅・奥行きの半径を (1 + taper · y/ry) 倍（上で広く、顎で細く）
        //   前を平ら: 前半分（z > 0）だけ z の指数を 2 → 2 + 6·flatten（1 でほぼ円盤）
        private static float ProxyField(Vector3 l, Vector3 r, Settings s)
        {
            var t = Mathf.Max(0.2f, 1f + s.proxyTaper * Mathf.Clamp(l.y / r.y, -1f, 1f));
            var x = Mathf.Abs(l.x / (r.x * t));
            var y = Mathf.Abs(l.y / r.y);
            var z = Mathf.Abs(l.z / (r.z * t));
            var pz = l.z > 0f ? 2f + 6f * Mathf.Clamp01(s.proxyFlatten) : 2f;
            return x * x + y * y + Mathf.Pow(z, pz) - 1f;
        }

        /// <summary>
        /// 軸に沿った楕円体 A x² + B y² + C z² + D x + E y + F z = 1 を最小二乗で当て、残差上位 15% を
        /// 捨てて当て直す。失敗（退化）したら箱に落ちる。
        /// </summary>
        public static bool FitEllipsoid(Vector3[] pts, out Vector3 center, out Vector3 radii)
        {
            center = Vector3.zero; radii = Vector3.one * 0.1f;
            var n = pts.Length;
            if (n < 12) return BoxFit(pts, out center, out radii);
            // 数値安定のため平均を引いて解く
            var mean = Vector3.zero;
            for (var i = 0; i < n; i++) mean += pts[i];
            mean /= n;
            var use = new bool[n];
            for (var i = 0; i < n; i++) use[i] = true;
            double[] coef = null;
            for (var pass = 0; pass < 2; pass++)
            {
                coef = SolveEllipsoid(pts, mean, use);
                if (coef == null) return BoxFit(pts, out center, out radii);
                if (pass == 0)
                {
                    // 残差の大きい 15% を外す
                    var res = new float[n];
                    var idx = new int[n];
                    for (var i = 0; i < n; i++)
                    {
                        var p = pts[i] - mean;
                        var v = coef[0] * p.x * p.x + coef[1] * p.y * p.y + coef[2] * p.z * p.z
                              + coef[3] * p.x + coef[4] * p.y + coef[5] * p.z - 1.0;
                        res[i] = (float)System.Math.Abs(v); idx[i] = i;
                    }
                    System.Array.Sort(res, idx);
                    var keep = Mathf.Max(12, (int)(n * 0.85f));
                    for (var k = keep; k < n; k++) use[idx[k]] = false;
                }
            }
            double A = coef[0], Bq = coef[1], C = coef[2], D = coef[3], E = coef[4], F = coef[5];
            if (A <= 0 || Bq <= 0 || C <= 0) return BoxFit(pts, out center, out radii);
            var cx = -D / (2 * A); var cy = -E / (2 * Bq); var cz = -F / (2 * C);
            var k1 = 1.0 + A * cx * cx + Bq * cy * cy + C * cz * cz;   // 右辺を移項した定数
            if (k1 <= 0) return BoxFit(pts, out center, out radii);
            center = mean + new Vector3((float)cx, (float)cy, (float)cz);
            radii  = new Vector3((float)System.Math.Sqrt(k1 / A), (float)System.Math.Sqrt(k1 / Bq), (float)System.Math.Sqrt(k1 / C));
            if (float.IsNaN(radii.x) || float.IsNaN(radii.y) || float.IsNaN(radii.z)) return BoxFit(pts, out center, out radii);
            return true;
        }

        private static bool BoxFit(Vector3[] pts, out Vector3 center, out Vector3 radii)
        {
            var mn = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var mx = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var p in pts) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
            center = (mn + mx) * 0.5f;
            radii  = Vector3.Max((mx - mn) * 0.5f, Vector3.one * 1e-3f);
            return pts.Length > 0;
        }

        // 正規方程式（6×6）をガウス消去で解く
        private static double[] SolveEllipsoid(Vector3[] pts, Vector3 mean, bool[] use)
        {
            var M = new double[6, 6]; var b = new double[6]; var row = new double[6];
            for (var i = 0; i < pts.Length; i++)
            {
                if (!use[i]) continue;
                var p = pts[i] - mean;
                row[0] = p.x * p.x; row[1] = p.y * p.y; row[2] = p.z * p.z; row[3] = p.x; row[4] = p.y; row[5] = p.z;
                for (var r = 0; r < 6; r++) { b[r] += row[r]; for (var c = 0; c < 6; c++) M[r, c] += row[r] * row[c]; }
            }
            for (var col = 0; col < 6; col++)
            {
                var piv = col; var best = System.Math.Abs(M[col, col]);
                for (var r = col + 1; r < 6; r++) if (System.Math.Abs(M[r, col]) > best) { best = System.Math.Abs(M[r, col]); piv = r; }
                if (best < 1e-18) return null;
                if (piv != col)
                {
                    for (var c = 0; c < 6; c++) { var t = M[col, c]; M[col, c] = M[piv, c]; M[piv, c] = t; }
                    var tb = b[col]; b[col] = b[piv]; b[piv] = tb;
                }
                for (var r = 0; r < 6; r++)
                {
                    if (r == col) continue;
                    var f = M[r, col] / M[col, col];
                    if (f == 0) continue;
                    for (var c = col; c < 6; c++) M[r, c] -= f * M[col, c];
                    b[r] -= f * b[col];
                }
            }
            var x = new double[6];
            for (var r = 0; r < 6; r++) x[r] = b[r] / M[r, r];
            return x;
        }

        private static bool ProxyMeshNormal(Vector3 posWS, Vector3 center, out Vector3 normal)
        {
            normal = Vector3.up;
            var dir = posWS - center;
            var len = dir.magnitude;
            if (len < 1e-6f) return false;
            dir /= len;
            float bestT = -1f; Vector3 bestN = Vector3.up;
            var V = s_proxyV; var N = s_proxyN; var T = s_proxyT;
            for (var i = 0; i < T.Length; i += 3)
            {
                int i0 = T[i], i1 = T[i + 1], i2 = T[i + 2];
                var v0 = V[i0]; var e1 = V[i1] - v0; var e2 = V[i2] - v0;
                var p = Vector3.Cross(dir, e2);
                var det = Vector3.Dot(e1, p);
                if (Mathf.Abs(det) < 1e-9f) continue;
                var inv = 1f / det;
                var tv = center - v0;
                var u = Vector3.Dot(tv, p) * inv; if (u < 0f || u > 1f) continue;
                var q = Vector3.Cross(tv, e1);
                var w = Vector3.Dot(dir, q) * inv; if (w < 0f || u + w > 1f) continue;
                var t = Vector3.Dot(e2, q) * inv;
                if (t <= 0f || t < bestT) continue;
                bestT = t;
                bestN = (N[i0] * (1f - u - w) + N[i1] * u + N[i2] * w);
            }
            if (bestT < 0f) return false;
            if (bestN.sqrMagnitude < 1e-12f) return false;
            normal = bestN.normalized;
            return true;
        }

        // 距離場ブレンドの等値線の本数。texel の距離から連続値を再構成するので
        // 出力値は 1/64 に量子化されない（角度方向も滑らかなまま）。増やすほど
        // 再現度が上がるが処理時間は線形に伸びる（64 で 1024px 4ch ≈ 十数秒）。
        private const int DfLevels = 64;

        // 顔SDFは4チャンネルで焼く: R=右 / G=左 / B=上 / A=下。ランタイムはミラー不要＝
        // 左右非対称の顔（傷跡・マーク等）にも対応。
        public static bool Bake(GameObject root, Material material, Settings s)
        {
            PrepareProxy(s);
            if (s.pack16 && s.pack16Vertical)
            {
                // 2ch 16bit: RG = 右光スイープ（1ch 経路と同じ）、BA = 上光スイープ。
                // 縦は左右対称の前提が要らない（fwd-up 面内の 1 本で足りる）ので
                // ミラーも無い。X Axis Tilt は横だけに掛かる（縦は仰角そのものを掃くため）。
                return EasyPbrBakeCore.RunBake(root, material, s.resolution, s.smooth, 0, 0,
                    "FaceSDF", "_FaceSDFMap", "_UseFaceSDF", needsCollider: true,
                    (r, m) => SdfSweepAxis(r, m, s, Vector3.right, true),
                    (r, m) => SdfSweepAxis(r, m, s, Vector3.up,    false),
                    occluderSubmeshesOnly: true,
                    postProcess: (px, cov, res) => PostProcess16x2(px, cov, res, s));
            }

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
        /// RG = 横（右光）/ BA = 縦（上光）の 2ch 16bit。どちらも 1ch 経路と同じ反転規約
        /// （白 = 最後まで照らされる側）で格納する。ランタイムは横を R×256+G、縦を
        /// B×256+A で読み、lit ⇔ sdf > 閾値 を軸ごとに評価して min で合成する。
        /// 反転規約が横と違うと、縦だけ顎裏が「永遠に照らされる」と誤読される（T-354 と同型）。
        /// </summary>
        private static Color32[] PostProcess16x2(Color32[] px, bool[] covered, int res, Settings s)
        {
            var fh = ExtractChannel(px, 0);
            var fv = ExtractChannel(px, 1);
            DistanceFieldBlend(fh, covered, res, s.dfSpread, 0, 2);
            DistanceFieldBlend(fv, covered, res, s.dfSpread, 1, 2);
            BoxBlur(fh, res, s.blur);
            BoxBlur(fv, res, s.blur);

            var outPx = new Color32[px.Length];
            for (var i = 0; i < fh.Length; i++)
            {
                var uh = (int)(Mathf.Clamp01(1f - fh[i]) * 65535f + 0.5f);
                var uv = (int)(Mathf.Clamp01(1f - fv[i]) * 65535f + 0.5f);
                outPx[i] = new Color32((byte)(uh >> 8), (byte)(uh & 0xFF),
                                       (byte)(uv >> 8), (byte)(uv & 0xFF));
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

            // プロキシの中心・半径を、このメッシュの頂点（Baker が実際に使う位置）から合わせる（0.3.2）。
            // 箱（min/max）ではなく最小二乗の楕円体。顔のメッシュには首・口の中・耳が混ざるので、
            // 箱だと中心が下がり縦に伸びる。残差の大きい頂点を捨てて 2 回目を当てると、首などの
            // 外れ値に引きずられない。
            var sp = s;
            // 形のバリエーション用の顔の軸（前 = fwd、上 = メッシュの up を fwd と直交化）
            s_proxyFwd   = fwd.normalized;
            s_proxyUp    = Vector3.ProjectOnPlane(xf.up, s_proxyFwd).normalized;
            if (s_proxyUp.sqrMagnitude < 0.5f) s_proxyUp = Vector3.ProjectOnPlane(Vector3.up, s_proxyFwd).normalized;
            s_proxyRight = Vector3.Cross(s_proxyUp, s_proxyFwd).normalized;
            if (s.proxyMode != 0 && (s.proxyAutoCenter || s.proxyAutoRadii) && n > 0)
            {
                var pts = new Vector3[n];
                for (var i = 0; i < n; i++) pts[i] = xf.TransformPoint(verts[i]);
                if (FitEllipsoid(pts, out var c, out var r))
                {
                    if (s.proxyAutoCenter) sp.proxyCenterWS = c;
                    if (s.proxyAutoRadii)  sp.proxyRadii    = r;
                }
            }

            for (var i = 0; i < n; i++)
            {
                // 頂点のワールド座標と法線の計算
                var originWS = xf.TransformPoint(verts[i]);
                var N = xf.TransformDirection(welded[i]).normalized;
                // プロキシ法線（0.3.2）: 楕円体 / プロキシメッシュの法線に置き換える（混ぜる）
                N = ProxyNormal(N, originWS, sp);

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
