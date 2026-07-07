// =============================================================================
//  EasyPbrFaceSdfBaker.cs  (Editor only)
// -----------------------------------------------------------------------------
//  顔 SDF Shadow マップのベイク。2ch: R=右光 / G=左光。
// =============================================================================
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
            public int   smooth;
            public int   blur;
            public int   dilate;
        }

        public static Settings Default => new Settings
        {
            resolution = 1024, angleSteps = 90, ndotlThreshold = 0.0f,
            useCastShadow = false, castDistance = 0.15f, flipForward = false,
            smooth = 1, blur = 1, dilate = 4
        };

        // 顔SDFは4チャンネルで焼く: R=右 / G=左 / B=上 / A=下。ランタイムはミラー不要＝
        // 左右非対称の顔（傷跡・マーク等）にも対応。
        public static bool Bake(GameObject root, Material material, Settings s)
        {
            return EasyPbrBakeCore.RunBake(root, material, s.resolution, s.smooth, s.dilate, s.blur,
                "FaceSDF", "_FaceSDFMap", "_UseFaceSDF", needsCollider: true,
                (r, m) => SdfSweepAxis(r, m, s, Vector3.right), // R: 右
                (r, m) => SdfSweepAxis(r, m, s, Vector3.left),  // G: 左
                (r, m) => SdfSweepAxis(r, m, s, Vector3.up),    // B: 上
                (r, m) => SdfSweepAxis(r, m, s, Vector3.down)); // A: 下
        }


        private static float[] SdfSweepAxis(Renderer r, Mesh m, Settings s, Vector3 localAxis)
        {
            // 前方ベクトルの取得（フリップ設定を考慮）
            var fwd = r.transform.forward * (s.flipForward ? -1f : 1f);
            
            // 指定されたローカル軸（右・左・上・下）をワールド空間の軸に変換
            var sweepAxis = r.transform.TransformDirection(localAxis).normalized; 
            
            return ComputeVertexSdf(r.transform, m, fwd, sweepAxis, s);
        }

        private static float[] ComputeVertexSdf(Transform xf, Mesh mesh, Vector3 fwd, Vector3 sweepAxis, Settings s)
        {
            var verts = mesh.vertices;
            var norms = mesh.normals;
            var n = verts.Length;
            var result = new float[n];
            var mask = 1 << EasyPbrBakeCore.BakeLayer;
            var steps = Mathf.Max(2, s.angleSteps);
            var fallbackUp = xf.up;

            for (var i = 0; i < n; i++)
            {
                // 頂点のワールド座標と法線の計算
                var N = (norms.Length == n) ? xf.TransformDirection(norms[i]).normalized : fallbackUp;
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
