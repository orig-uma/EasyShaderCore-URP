// =============================================================================
//  EasyPbrShadeNormalBaker.cs  (Editor only)
// -----------------------------------------------------------------------------
//  Shade Normal マップのベイク（接線空間 / RGB）。
//  頂点法線を「位置で溶接した隣接グラフ」上でラプラシアン平滑化し、シワ・
//  ファセット・細かい起伏を除いた滑らかな法線を接線空間に焼く。
//  シェーダーは拡散の陰ランプだけをこの法線で駆動し（スペキュラ・リム等は
//  ディテール法線のまま）、グラデーションを一本の綺麗な曲線として通す。
//
//  位置溶接: UV 継ぎ目・硬エッジで分割された頂点を同一点として扱わないと、
//  平滑化後も継ぎ目で陰が割れるため、量子化した位置でグループ化して平均する。
//  レイ不要・高速。タンジェント必須（無いメッシュはフラット(0,0,1)＝無効）。
// =============================================================================
using System.Collections.Generic;
using UnityEngine;

namespace Origuma.EasyShaderCore.Editor
{
    public static class EasyPbrShadeNormalBaker
    {
        public struct Settings
        {
            public int resolution;
            public int smoothIterations; // 法線のラプラシアン平滑化の回数（高いほど滑らか）
            public int dilate;
            public int blur;
        }

        public static Settings Default => new Settings
        {
            resolution = 1024, smoothIterations = 16, dilate = 4, blur = 1
        };

        public static bool Bake(GameObject root, Material material, Settings s)
            => EasyPbrBakeCore.RunBake(root, material, s.resolution, smooth: 0, s.dilate, s.blur,
                "ShadeNormal", "_ShadeNormalMap", "_ShadeNormalStrength", needsCollider: false,
                (r, m) => Channel(r, m, s, 0),
                (r, m) => Channel(r, m, s, 1),
                (r, m) => Channel(r, m, s, 2),
                computeA: null,
                clearValue: 0.5f, clearValueG: 0.5f, clearValueB: 1.0f);

        // RunBake は R→G→B を同じ mesh で順に呼ぶため、初回で平滑化してキャッシュする。
        private static Mesh      _cacheMesh;
        private static Vector3[] _cacheTS;

        private static float[] Channel(Renderer r, Mesh m, Settings s, int comp)
        {
            if (!ReferenceEquals(_cacheMesh, m))
            {
                _cacheTS = ComputeSmoothedTangentNormals(r.transform, m, s);
                _cacheMesh = m;
            }
            var n = _cacheTS.Length;
            var outc = new float[n];
            for (var i = 0; i < n; i++)
            {
                var raw = comp == 0 ? _cacheTS[i].x : comp == 1 ? _cacheTS[i].y : _cacheTS[i].z;
                outc[i] = 0.5f + 0.5f * raw; // 法線マップと同じ 0.5 中心エンコード
            }
            return outc;
        }

        private static Vector3[] ComputeSmoothedTangentNormals(Transform xf, Mesh mesh, Settings s)
        {
            var verts = mesh.vertices;
            var norms = mesh.normals;
            var tans  = mesh.tangents;
            var     tris  = mesh.triangles;
            var n = verts.Length;

            var hasTan = tans != null && tans.Length == n;
            if (!hasTan)
                Debug.LogWarning($"[EasyPBR Baker] '{mesh.name}' にタンジェントが無いため Shade Normal はフラット(無効)で焼かれます。" +
                                 "インポート設定で Calculate Tangents を有効化するか、UV を用意してください。");

            // --- 1) 位置溶接: 量子化した位置で頂点をグループ化 -----------------
            var groupOf = new int[n];
            var keyToGroup = new Dictionary<Vector3Int, int>(n);
            var groupCount = 0;
            for (var i = 0; i < n; i++)
            {
                var key = new Vector3Int(
                    Mathf.RoundToInt(verts[i].x * 1e5f),
                    Mathf.RoundToInt(verts[i].y * 1e5f),
                    Mathf.RoundToInt(verts[i].z * 1e5f));
                if (!keyToGroup.TryGetValue(key, out var g))
                {
                    g = groupCount++;
                    keyToGroup.Add(key, g);
                }
                groupOf[i] = g;
            }

            // グループ初期法線 = 所属頂点法線の平均（溶接するだけで硬エッジが消える）。
            var groupN = new Vector3[groupCount];
            for (var i = 0; i < n; i++)
                if (norms.Length == n) groupN[groupOf[i]] += norms[i];
            for (var g = 0; g < groupCount; g++)
                groupN[g] = groupN[g].sqrMagnitude > 1e-12f ? groupN[g].normalized : Vector3.up;

            // --- 2) グループ隣接（三角形エッジ由来）------------------------------
            var adjacency = new HashSet<int>[groupCount];
            for (var g = 0; g < groupCount; g++) adjacency[g] = new HashSet<int>();
            for (var t = 0; t < tris.Length; t += 3)
            {
                int a = groupOf[tris[t]], b = groupOf[tris[t + 1]], c = groupOf[tris[t + 2]];
                if (a != b) { adjacency[a].Add(b); adjacency[b].Add(a); }
                if (b != c) { adjacency[b].Add(c); adjacency[c].Add(b); }
                if (c != a) { adjacency[c].Add(a); adjacency[a].Add(c); }
            }

            // --- 3) ラプラシアン平滑化（自分＋隣接平均を反復）--------------------
            var current = groupN;
            var next = new Vector3[groupCount];
            for (var it = 0; it < s.smoothIterations; it++)
            {
                for (var g = 0; g < groupCount; g++)
                {
                    var sum = current[g];
                    foreach (var nb in adjacency[g]) sum += current[nb];
                    next[g] = sum.sqrMagnitude > 1e-12f ? sum.normalized : current[g];
                }
                (current, next) = (next, current);
            }

            // --- 4) 各頂点の TBN で接線空間へ射影（Bent Normal と同じ符号化）-----
            var result = new Vector3[n];
            for (var i = 0; i < n; i++)
            {
                if (!hasTan) { result[i] = new Vector3(0, 0, 1); continue; }

                var nLocal = norms.Length == n ? norms[i] : Vector3.up;
                var normalWS   = xf.TransformDirection(nLocal).normalized;
                var smoothedWS = xf.TransformDirection(current[groupOf[i]]).normalized;
                var tanWS = xf.TransformDirection(new Vector3(tans[i].x, tans[i].y, tans[i].z)).normalized;
                var biWS  = Vector3.Cross(normalWS, tanWS) * tans[i].w;

                var ts = new Vector3(
                    Vector3.Dot(smoothedWS, tanWS),
                    Vector3.Dot(smoothedWS, biWS),
                    Vector3.Dot(smoothedWS, normalWS));
                result[i] = ts.sqrMagnitude > 1e-12f ? ts.normalized : new Vector3(0, 0, 1);
            }
            return result;
        }
    }
}
