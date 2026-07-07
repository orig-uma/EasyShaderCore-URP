// =============================================================================
//  EasyPbrCurvatureBaker.cs  (Editor only)
// -----------------------------------------------------------------------------
//  Curvature マップのベイク。Cavity と同じ「隣接頂点と法線の関係」から、
//  くぼみ(凹)だけでなく稜線(凸)も符号付きで取る（レイ不要）。
//
//    0.5 = 平坦 / >0.5 = 凸(稜線・エッジ) / <0.5 = 凹(くぼみ・しわ)
//
//  Cavity が凹だけを 0..1 で焼くのに対し、曲率は 1 枚で稜線マスクと
//  くぼみマスクの両方を取り出せる。エッジハイライト、金属角の擦れ、
//  リム変調、アウトライン幅変調などに使える。Cavity と併用も可。
// =============================================================================
using UnityEngine;

namespace Origuma.EasyShaderCore.Editor
{
    public static class EasyPbrCurvatureBaker
    {
        public struct Settings
        {
            public int   resolution;
            public float intensity;   // 凹凸コントラストの強さ
            public int   dilate;
            public int   smooth;
            public int   blur;
        }

        public static Settings Default => new Settings
        {
            resolution = 1024, intensity = 4.0f, dilate = 4, smooth = 1, blur = 1
        };

        public static bool Bake(GameObject root, Material material, Settings s)
            => EasyPbrBakeCore.RunBake(root, material, s.resolution, s.smooth, s.dilate, s.blur,
                "Curvature", "_CurvatureMap", "_CurvatureStrength", needsCollider: false,
                (r, m) => ComputeVertexCurvature(m, s), clearValue: 0.5f);

        // 各頂点で「隣接頂点が法線側にどれだけ寄っているか」の平均を取る。
        //  mean > 0 → 隣接が法線側 = 凹(くぼみ) / mean < 0 → 凸(稜線)。
        //  これを符号反転して [-1,1] に正規化し、0.5 中心で 0..1 へエンコード。
        private static float[] ComputeVertexCurvature(Mesh mesh, Settings s)
        {
            var verts = mesh.vertices;
            var norms = mesh.normals;
            var n = verts.Length;
            var sum = new float[n];
            var cnt = new int[n];
            var tris = mesh.triangles;

            for (var t = 0; t < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                Accum(a, b, verts, norms, sum, cnt); Accum(a, c, verts, norms, sum, cnt);
                Accum(b, a, verts, norms, sum, cnt); Accum(b, c, verts, norms, sum, cnt);
                Accum(c, a, verts, norms, sum, cnt); Accum(c, b, verts, norms, sum, cnt);
            }

            var result = new float[n];
            for (var i = 0; i < n; i++)
            {
                var mean = cnt[i] > 0 ? sum[i] / cnt[i] : 0f;   // >0 凹 / <0 凸
                var k    = Mathf.Clamp(-mean * s.intensity, -1f, 1f); // 凸を正に
                result[i]  = 0.5f + 0.5f * k;                     // 0.5平坦 / >0.5凸 / <0.5凹
            }
            return result;
        }

        private static void Accum(int i, int j, Vector3[] verts, Vector3[] norms, float[] sum, int[] cnt)
        {
            if (norms.Length != verts.Length) return;
            var d = verts[j] - verts[i];
            var len = d.magnitude;
            if (len < 1e-7f) return;
            sum[i] += Vector3.Dot(d / len, norms[i]);
            cnt[i]++;
        }
    }
}
