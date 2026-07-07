// =============================================================================
//  EasyPbrHairFlowBaker.cs  (Editor only)
// -----------------------------------------------------------------------------
//  Hair Flow マップのベイク（接線平面内の毛流れ / レイ不要）。
//  既存の異方性は「UV接線 ＋ 単一グローバル角」で流れを決めるため、毛束ごとの補正・
//  ミラーUV・流れに沿っていないUVに弱い。ここを形状ベースの流れで上書きする。
//
//  毛流れは向きの無い「軸」(180°対称)なので、生の方向ベクトルで焼くと補間・ブラー・
//  ミラーで打ち消し合う。これを避けるため倍角(cos2θ, sin2θ)で焼く。各頂点で接線平面に
//  投影したエッジの構造テンソル [[a,b],[b,c]] を組むと、優勢方向がそのまま
//    2θ = atan2(2b, a-c),  cos2θ = (a-c)/D, sin2θ = 2b/D   (D = √((a-c)²+(2b)²))
//  として倍角で得られ、異方性 conf = D/(a+c) が信頼度になる。
//
//    R = cos2θ / G = sin2θ（接線基準の毛流れ角・倍角）
//    B = 信頼度 0..1（0=等方=方向不定 → ランタイムでUV接線へフォールバック）
//
//  接線平面内の角度で持つので、ランタイムの TBN で再解釈＝スキン変形に追従し、
//  UV接線が毛流れに対してずれていても「そのフレーム内での補正角」を復元できる。
//
//  モード: 既定は最長エッジ主軸（生 e·eᵀ が長さ²で自然に重み付く＝髪カードに強い）。
//          useCurvature=true で「法線変化が小さい方向」を優先（彫刻的な一枚メッシュ向け）。
// =============================================================================
using UnityEngine;

namespace Origuma.EasyShaderCore.Editor
{
    public static class EasyPbrHairFlowBaker
    {
        public struct Settings
        {
            public int   resolution;
            public bool  useCurvature;  // false=最長エッジ / true=最小法線変化(曲率)
            public int   dilate;
            public int   smooth;
            public int   blur;
        }

        public static Settings Default => new Settings
        {
            resolution = 1024, useCurvature = true, dilate = 4, smooth = 2, blur = 1
        };

        public static bool Bake(GameObject root, Material material, Settings s)
        {
            _cacheMesh = null;
            try
            {
                return EasyPbrBakeCore.RunBake(root, material, s.resolution, s.smooth, s.dilate, s.blur,
                    "HairFlow", "_HairFlowMap", "_HairFlowStrength", needsCollider: false,
                    (r, m) => Channel(m, s, 0),   // R = cos2θ
                    (r, m) => Channel(m, s, 1),   // G = sin2θ
                    (r, m) => Channel(m, s, 2),   // B = 信頼度
                    clearValue: 1.0f, clearValueG: 0.5f, clearValueB: 0.0f);
            }
            finally
            {
                _cacheMesh = null;
            }
        }

        // RunBake は R→G→B を同じ mesh で順に呼ぶので、初回に計算してキャッシュ。
        private static Mesh      _cacheMesh;
        private static Vector3[] _cacheFlow;   // x=cos2θ / y=sin2θ / z=信頼度

        private static float[] Channel(Mesh m, Settings s, int comp)
        {
            if (!ReferenceEquals(_cacheMesh, m))
            {
                _cacheFlow = ComputeFlow(m, s);
                _cacheMesh = m;
            }
            var n = _cacheFlow.Length;
            var outc = new float[n];
            for (var i = 0; i < n; i++)
            {
                var raw = comp == 0 ? _cacheFlow[i].x : (comp == 1 ? _cacheFlow[i].y : _cacheFlow[i].z);
                // cos2θ/sin2θ は 0.5 中心エンコード、信頼度は 0..1 そのまま。
                outc[i] = comp == 2 ? Mathf.Clamp01(raw) : 0.5f + 0.5f * raw;
            }
            return outc;
        }

        private static Vector3[] ComputeFlow(Mesh mesh, Settings s)
        {
            var verts = mesh.vertices;
            var norms = mesh.normals;
            var tans  = mesh.tangents;
            var n = verts.Length;
            var result = new Vector3[n];

            var hasTan = tans != null && tans.Length == n && norms.Length == n;
            if (!hasTan)
            {
                Debug.LogWarning($"[EasyPBR Baker] '{mesh.name}' にタンジェント/法線が無いため Hair Flow は無効(識別)で焼かれます。" +
                                 "インポート設定で Calculate Tangents を有効化してください。");
                for (var i = 0; i < n; i++) result[i] = new Vector3(1f, 0f, 0f); // cos2θ=1,sin2θ=0,conf=0
                return result;
            }

            // 各頂点の接線平面基底（ランタイムの TBN と同じ取り方：B = N × T * w）。
            var T = new Vector3[n];
            var B = new Vector3[n];
            for (var i = 0; i < n; i++)
            {
                var t = new Vector3(tans[i].x, tans[i].y, tans[i].z).normalized;
                T[i] = t;
                B[i] = Vector3.Cross(norms[i].normalized, t) * tans[i].w;
            }

            // 接線平面に投影したエッジの構造テンソルを 1 リングで累積。
            var aa = new float[n];
            var bb = new float[n];
            var cc = new float[n];
            var tris = mesh.triangles;
            for (var k = 0; k < tris.Length; k += 3)
            {
                int a = tris[k], b = tris[k + 1], c = tris[k + 2];
                Accum(a, b, verts, norms, T, B, s.useCurvature, aa, bb, cc);
                Accum(a, c, verts, norms, T, B, s.useCurvature, aa, bb, cc);
                Accum(b, a, verts, norms, T, B, s.useCurvature, aa, bb, cc);
                Accum(b, c, verts, norms, T, B, s.useCurvature, aa, bb, cc);
                Accum(c, a, verts, norms, T, B, s.useCurvature, aa, bb, cc);
                Accum(c, b, verts, norms, T, B, s.useCurvature, aa, bb, cc);
            }

            for (var i = 0; i < n; i++)
            {
                var diff = aa[i] - cc[i];
                var off  = 2f * bb[i];
                var D     = Mathf.Sqrt(diff * diff + off * off);
                var trace = aa[i] + cc[i];

                var cos2 = D > 1e-12f ? diff / D : 1f;   // 等方なら識別(1,0)
                var sin2 = D > 1e-12f ? off  / D : 0f;
                var conf = trace > 1e-12f ? Mathf.Clamp01(D / trace) : 0f; // (λ1-λ2)/(λ1+λ2)

                result[i] = new Vector3(cos2, sin2, conf);
            }
            return result;
        }

        // 有向エッジ i→j を頂点 i の接線平面へ投影し、構造テンソルへ加算。
        private static void Accum(int i, int j, Vector3[] verts, Vector3[] norms,
                                  Vector3[] T, Vector3[] B, bool curvature,
                                  float[] aa, float[] bb, float[] cc)
        {
            var e = verts[j] - verts[i];
            var eT = Vector3.Dot(e, T[i]);
            var eB = Vector3.Dot(e, B[i]);
            var l2 = eT * eT + eB * eB;
            if (l2 < 1e-12f) return;

            float w;
            if (curvature)
            {
                // 法線変化が小さい方向ほど重い＝毛流れに沿う。方向は正規化して扱う。
                var invLen = 1f / Mathf.Sqrt(l2);
                eT *= invLen; eB *= invLen;
                w = 1f / (1e-3f + (norms[j] - norms[i]).magnitude);
            }
            else
            {
                // 最長エッジ主軸: 生の e·eᵀ は長さ²で自然に重み付く（正規化しない）。
                w = 1f;
            }

            aa[i] += w * eT * eT;
            cc[i] += w * eB * eB;
            bb[i] += w * eT * eB;
        }
    }
}
