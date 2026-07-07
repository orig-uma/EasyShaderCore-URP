// =============================================================================
//  EasyPbrAoBaker.cs  (Editor only)
// -----------------------------------------------------------------------------
//  Ambient Occlusion マップのベイク。半球レイの遮蔽率を UV 空間へ焼く。
// =============================================================================
using UnityEngine;

namespace Origuma.EasyShaderCore.Editor
{
    public static class EasyPbrAoBaker
    {
        public struct Settings
        {
            public int   resolution;
            public int   rayCount;
            public float maxDistance;
            public float intensity;
            public int   dilate;
            public int   smooth;
            public float floor;
            public int   blur;
            public float enclosedCutoff;
        }

        public static Settings Default => new Settings
        {
            resolution = 1024, rayCount = 64, maxDistance = 0.25f, intensity = 1.0f,
            dilate = 4, smooth = 2, floor = 0.0f, blur = 1, enclosedCutoff = 0.95f
        };

        public static bool Bake(GameObject root, Material material, Settings s)
            => EasyPbrBakeCore.RunBake(root, material, s.resolution, s.smooth, s.dilate, s.blur,
                "AO", "_OcclusionMap", "_OcclusionStrength", needsCollider: true,
                (r, m) => ComputeVertexAO(r.transform, m, s));

        private static float[] ComputeVertexAO(Transform xf, Mesh mesh, Settings s)
        {
            Vector3[] verts = mesh.vertices;
            Vector3[] norms = mesh.normals;
            int n = verts.Length;
            var result = new float[n];
            int mask = 1 << EasyPbrBakeCore.BakeLayer;
            Vector3[] hemi = EasyPbrBakeCore.BuildHemisphere(s.rayCount);

            for (int i = 0; i < n; i++)
            {
                Vector3 nLocal   = norms.Length == n ? norms[i] : Vector3.up;
                Vector3 originWS = xf.TransformPoint(verts[i]);
                Vector3 normalWS = xf.TransformDirection(nLocal).normalized;
                Vector3 bias     = normalWS * 1e-3f;
                EasyPbrBakeCore.Basis(normalWS, out Vector3 t, out Vector3 b);

                int hits = 0;
                for (int r = 0; r < hemi.Length; r++)
                {
                    Vector3 h = hemi[r];
                    Vector3 dir = (t * h.x + b * h.y + normalWS * h.z).normalized;
                    if (Physics.Raycast(originWS + bias, dir, s.maxDistance, mask)) hits++;
                }
                float occ = (float)hits / Mathf.Max(1, hemi.Length);
                float bright = Mathf.Clamp01(1.0f - occ * s.intensity);
                float toWhite = Mathf.Clamp01(Mathf.InverseLerp(s.enclosedCutoff - 0.05f, s.enclosedCutoff, occ));
                bright = Mathf.Lerp(bright, 1.0f, toWhite);
                result[i] = Mathf.Lerp(s.floor, 1.0f, bright);
            }
            return result;
        }
    }
}
