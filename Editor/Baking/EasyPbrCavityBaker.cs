// =============================================================================
//  EasyPbrCavityBaker.cs  (Editor only)
// -----------------------------------------------------------------------------
//  Cavity マップのベイク。隣接頂点の法線関係からくぼみを検出（レイ不要）。
// =============================================================================
using UnityEngine;

namespace Origuma.EasyShaderCore.Editor
{
    public static class EasyPbrCavityBaker
    {
        public struct Settings
        {
            public int   resolution;
            public float intensity;
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
                "Cavity", "_CavityMap", "_CavityStrength", needsCollider: false,
                (r, m) => ComputeVertexCavity(m, s));

        private static float[] ComputeVertexCavity(Mesh mesh, Settings s)
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
                AccumCavity(a, b, verts, norms, sum, cnt); AccumCavity(a, c, verts, norms, sum, cnt);
                AccumCavity(b, a, verts, norms, sum, cnt); AccumCavity(b, c, verts, norms, sum, cnt);
                AccumCavity(c, a, verts, norms, sum, cnt); AccumCavity(c, b, verts, norms, sum, cnt);
            }

            var result = new float[n];
            for (var i = 0; i < n; i++)
            {
                var mean = cnt[i] > 0 ? sum[i] / cnt[i] : 0f;
                result[i] = 1.0f - Mathf.Clamp01(mean * s.intensity);
            }
            return result;
        }

        private static void AccumCavity(int i, int j, Vector3[] verts, Vector3[] norms, float[] sum, int[] cnt)
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
