// =============================================================================
//  EasyPbrBentNormalBaker.cs  (Editor only)
// -----------------------------------------------------------------------------
//  Bent Normal マップのベイク（接線空間 / RGBA）。
//  AO と同じ半球レイを飛ばし、「遮蔽されなかった方向の平均」＝開いている方向を求め、
//  各頂点の TBN でタンジェント空間へ射影して RGB に焼く。A には開き具合(可視率)を焼く。
//  法線マップと同じ符号化なので、シェーダーのデコードも法線マップと同一。
//
//    RGB = 接線空間ベント法線（SH/アンビエントの評価方向）
//    A   = 開き具合 0..1（0=閉じ / 1=全開）。方向スペキュラ遮蔽の重みに使う。
//
//  用途: SH/アンビエントを幾何法線の代わりにこの方向で評価 → くぼみの陰が方向まで
//        正しくなりのっぺり感が消える。鏡面遮蔽にも使える。AO(強度マスク)と併用。
//
//  接線空間なのでスキン変形に追従する（ランタイムの TBN で再解釈されるため）。
//  タンジェント必須（無いメッシュはフラット(0,0,1)へフォールバック＝無効）。
// =============================================================================
using UnityEngine;

namespace Origuma.EasyShaderCore.Editor
{
    public static class EasyPbrBentNormalBaker
    {
        public struct Settings
        {
            public int   resolution;
            public int   rayCount;
            public float maxDistance;
            public float strength;   // 0=幾何法線そのまま / 1=フルに開いた方向へ
            public int   dilate;
            public int   smooth;
            public int   blur;
        }

        public static Settings Default => new Settings
        {
            resolution = 1024, rayCount = 64, maxDistance = 0.25f, strength = 1.0f,
            dilate = 4, smooth = 2, blur = 1
        };

        public static bool Bake(GameObject root, Material material, Settings s)
            => EasyPbrBakeCore.RunBake(root, material, s.resolution, s.smooth, s.dilate, s.blur,
                "BentNormal", "_BentNormalMap", "_BentNormalStrength", needsCollider: true,
                (r, m) => Channel(r, m, s, 0),   // R = tangent.x
                (r, m) => Channel(r, m, s, 1),   // G = tangent.y
                (r, m) => Channel(r, m, s, 2),   // B = tangent.z
                (r, m) => Channel(r, m, s, 3));  // A = 開き具合(可視率) … 方向スペキュラ遮蔽用

        // RunBake は R→G→B→A を同じ mesh で順に呼ぶので、最初の呼び出しでレイを飛ばして
        // 結果をキャッシュし、残りのチャンネルはキャッシュから取り出す（レイは頂点1回だけ）。
        private static Mesh      _cacheMesh;
        private static Vector4[] _cacheBent;   // xyz=接線空間bent / w=可視率(0=閉/1=全開)

        private static float[] Channel(Renderer r, Mesh m, Settings s, int comp)
        {
            if (!ReferenceEquals(_cacheMesh, m))
            {
                _cacheBent = ComputeBentNormals(r.transform, m, s);
                _cacheMesh = m;
            }
            var n = _cacheBent.Length;
            var outc = new float[n];
            for (var i = 0; i < n; i++)
            {
                var raw = comp == 0 ? _cacheBent[i].x
                          : comp == 1 ? _cacheBent[i].y
                          : comp == 2 ? _cacheBent[i].z
                          :             _cacheBent[i].w;
                // xyz は法線マップと同じ 0.5 中心エンコード。w(可視率)は 0..1 をそのまま。
                outc[i] = comp == 3 ? Mathf.Clamp01(raw) : 0.5f + 0.5f * raw;
            }
            return outc;
        }

        private static Vector4[] ComputeBentNormals(Transform xf, Mesh mesh, Settings s)
        {
            var verts = mesh.vertices;
            var norms = mesh.normals;
            var tans  = mesh.tangents;
            var n = verts.Length;
            var result = new Vector4[n];
            var mask = 1 << EasyPbrBakeCore.BakeLayer;
            var hemi = EasyPbrBakeCore.BuildHemisphere(s.rayCount);

            var hasTan = tans != null && tans.Length == n;
            if (!hasTan)
                Debug.LogWarning($"[EasyPBR Baker] '{mesh.name}' にタンジェントが無いため Bent Normal はフラット(無効)で焼かれます。" +
                                 "インポート設定で Calculate Tangents を有効化するか、UV を用意してください。");

            for (var i = 0; i < n; i++)
            {
                var nLocal   = norms.Length == n ? norms[i] : Vector3.up;
                var originWS = xf.TransformPoint(verts[i]);
                var normalWS = xf.TransformDirection(nLocal).normalized;
                var bias     = normalWS * 1e-3f;
                EasyPbrBakeCore.Basis(normalWS, out var t, out var b);

                // 遮蔽されなかったレイ方向だけ平均 → 開いている方向（コサイン重みは半球分布由来）。
                var openSum = Vector3.zero;
                var openCnt = 0;
                for (var r = 0; r < hemi.Length; r++)
                {
                    var h   = hemi[r];
                    var dir = (t * h.x + b * h.y + normalWS * h.z).normalized;
                    if (!Physics.Raycast(originWS + bias, dir, s.maxDistance, mask))
                    {
                        openSum += dir;
                        openCnt++;
                    }
                }

                // 完全に埋まっている / 開きが相殺 → 法線そのものへフォールバック。
                var bentWS = (openCnt > 0 && openSum.sqrMagnitude > 1e-12f)
                    ? openSum.normalized : normalWS;

                // 効き具合を幾何法線との間で補間（0=効果なし）。方向(xyz)のみに作用。
                bentWS = Vector3.Slerp(normalWS, bentWS, s.strength).normalized;

                // 開き具合(可視率): 遮蔽されなかったレイの割合。0=完全に閉じ / 1=全開。
                // 方向(xyz)とは独立した幾何量なので strength の影響は受けない。
                var openness = (float)openCnt / Mathf.Max(1, hemi.Length);

                if (hasTan)
                {
                    // ワールド → タンジェント空間（ランタイムの TBN で再解釈＝スキン追従）。
                    var tanWS = xf.TransformDirection(new Vector3(tans[i].x, tans[i].y, tans[i].z)).normalized;
                    var biWS  = Vector3.Cross(normalWS, tanWS) * tans[i].w; // handedness 込み従法線
                    var ts = new Vector3(
                        Vector3.Dot(bentWS, tanWS),
                        Vector3.Dot(bentWS, biWS),
                        Vector3.Dot(bentWS, normalWS));
                    if (ts.sqrMagnitude > 1e-12f) ts = ts.normalized; else ts = new Vector3(0, 0, 1);
                    result[i] = new Vector4(ts.x, ts.y, ts.z, openness);
                }
                else
                {
                    result[i] = new Vector4(0, 0, 1, openness); // 方向はフラット(無効)、開き具合だけ有効
                }
            }
            return result;
        }
    }
}