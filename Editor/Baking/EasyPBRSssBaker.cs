// =============================================================================
//  EasyPbrSssBaker.cs  (Editor only)
// -----------------------------------------------------------------------------
//  SSS マップのベイク（厚み＋透過方向を 1 枚に統合 / RGBA）。旧 Thickness(_SSSMask) を置換。
//  内向き半球レイで厚みを測り、同時に「最も薄く抜ける軸（透過方向）」を求めて接線空間に焼く。
//
//    RGB = 接線空間の透過方向（厚みが最小に抜ける外向き軸・法線マップと同じ符号化）
//    A   = 厚みスカラ 0..1（薄いほど 1。旧 _SSSMask.r 相当）
//
//  DICE系の透過（法線を歪ませて光を裏へ回す）の「歪ませる軸」を、幾何法線から
//  この透過方向へ差し替えると、耳の縁・指・小鼻・布の折りなど、薄さの抜ける向きが
//  法線と一致しない箇所で透過グローが正しい向きに出る。フラットで真後ろが最薄なら
//  透過軸＝法線へ縮退するので、現状から悪化しない。
//
//  透過方向は接線空間なのでスキン変形に追従する。タンジェントが無いメッシュは
//  方向を (0,0,1)=幾何法線へフォールバック（＝従来挙動）、厚みAはレイなのでそのままOK。
// =============================================================================
using UnityEngine;

namespace Origuma.EasyShaderCore.Editor
{
    public static class EasyPbrSssBaker
    {
        public struct Settings
        {
            public int   resolution;
            public int   rayCount;
            public float maxDistance;
            public float intensity;   // 薄い部分の強調（厚みAに作用）
            public int   dilate;
            public int   smooth;
            public int   blur;
        }

        public static Settings Default => new Settings
        {
            resolution = 1024, rayCount = 48, maxDistance = 0.3f, intensity = 1.0f,
            dilate = 4, smooth = 2, blur = 1
        };

        public static bool Bake(GameObject root, Material material, Settings s)
        {
            _cacheMesh = null;
            _cacheRenderer = null;
            try
            {
                return EasyPbrBakeCore.RunBake(root, material, s.resolution, s.smooth, s.dilate, s.blur,
                    "SSS", "_SSSMap", "_SSSIntensity", needsCollider: true,
                    (r, m) => Channel(r, m, s, 0),
                    (r, m) => Channel(r, m, s, 1),
                    (r, m) => Channel(r, m, s, 2),
                    (r, m) => Channel(r, m, s, 3),
                    clearValue: 0.5f, clearValueG: 0.5f, clearValueB: 1.0f, clearValueA: 0.0f);
            }
            finally
            {
                _cacheMesh = null;
                _cacheRenderer = null;
            }
        }

        private static Mesh       _cacheMesh;
        private static Renderer   _cacheRenderer;
        private static Vector4[]  _cacheSss;

        private static float[] Channel(Renderer r, Mesh m, Settings s, int comp)
        {
            if (!ReferenceEquals(_cacheMesh, m) || !ReferenceEquals(_cacheRenderer, r))
            {
                _cacheSss = ComputeSss(r.transform, m, s);
                _cacheMesh = m;
                _cacheRenderer = r;
            }
            var n = _cacheSss.Length;
            var outc = new float[n];
            for (var i = 0; i < n; i++)
            {
                var raw = comp == 0 ? _cacheSss[i].x
                          : comp == 1 ? _cacheSss[i].y
                          : comp == 2 ? _cacheSss[i].z
                          :             _cacheSss[i].w;
                // xyz は法線マップと同じ 0.5 中心エンコード、厚みは 0..1 そのまま。
                outc[i] = comp == 3 ? Mathf.Clamp01(raw) : 0.5f + 0.5f * raw;
            }
            return outc;
        }

        private static Vector4[] ComputeSss(Transform xf, Mesh mesh, Settings s)
        {
            var verts = mesh.vertices;
            var norms = mesh.normals;
            var tans  = mesh.tangents;
            var n = verts.Length;
            var result = new Vector4[n];
            var mask = 1 << EasyPbrBakeCore.BakeLayer;
            var hemi = EasyPbrBakeCore.BuildHemisphere(s.rayCount);

            var hasTan = tans != null && tans.Length == n && norms.Length == n;
            if (!hasTan)
                Debug.LogWarning($"[EasyPBR Baker] '{mesh.name}' にタンジェント/法線が無いため SSS 透過方向は (0,0,1)=幾何法線へ。" +
                                 "厚み(A)は有効です。インポート設定で Calculate Tangents を有効化すると方向も焼けます。");

            for (var i = 0; i < n; i++)
            {
                var nLocal   = norms.Length == n ? norms[i] : Vector3.up;
                var normalWS = xf.TransformDirection(nLocal).normalized;
                var inward   = -normalWS;
                var originIn = xf.TransformPoint(verts[i]) - normalWS * 1e-3f;
                EasyPbrBakeCore.Basis(inward, out var t, out var b);

                var acc = 0f;
                var dirSum = Vector3.zero;   // 近ヒット(薄い)方向ほど重い → 透過軸(内向き)
                for (var r = 0; r < hemi.Length; r++)
                {
                    var h   = hemi[r];
                    var dir = (t * h.x + b * h.y + inward * h.z).normalized;
                    var d = Physics.Raycast(originIn, dir, out var hit, s.maxDistance, mask)
                        ? hit.distance : s.maxDistance;
                    acc += d;
                    dirSum += dir * Mathf.Clamp01(1f - d / Mathf.Max(1e-4f, s.maxDistance)); // 近いほど重い
                }

                var avg  = acc / Mathf.Max(1, hemi.Length);
                var thin = Mathf.Clamp01((1.0f - avg / Mathf.Max(1e-4f, s.maxDistance)) * s.intensity);

                // 透過軸: 最薄方向(内向き)。十分な近ヒットが無ければ真後ろ(-法線)へフォールバック。
                var transIn  = dirSum.sqrMagnitude > 1e-12f ? dirSum.normalized : inward;
                var transOut = -transIn;   // 外向き(法線的)に持つ＝DICE項の法線置換にそのまま使える

                if (hasTan)
                {
                    var tanWS = xf.TransformDirection(new Vector3(tans[i].x, tans[i].y, tans[i].z)).normalized;
                    var biWS  = Vector3.Cross(normalWS, tanWS) * tans[i].w;
                    var ts = new Vector3(
                        Vector3.Dot(transOut, tanWS),
                        Vector3.Dot(transOut, biWS),
                        Vector3.Dot(transOut, normalWS));
                    if (ts.sqrMagnitude > 1e-12f) ts = ts.normalized; else ts = new Vector3(0, 0, 1);
                    result[i] = new Vector4(ts.x, ts.y, ts.z, thin);
                }
                else
                {
                    result[i] = new Vector4(0, 0, 1, thin); // 方向は幾何法線、厚みのみ有効
                }
            }
            return result;
        }
    }
}
