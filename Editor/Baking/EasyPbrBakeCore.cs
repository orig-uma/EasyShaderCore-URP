// =============================================================================
//  EasyPbrBakeCore.cs  (Editor only)
// -----------------------------------------------------------------------------
//  ベイク共通パイプライン: Renderer 収集 → Collider → 頂点計算 → ラスタライズ →
//  ダイレート → ブラー → 保存(Linear) → マテリアルへアサイン。
//  各マップ Baker から RunBake を呼ぶ。最大4チャンネル(RGBA)に対応。
// =============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Origuma.EasyShaderCore.Editor
{
    public static class EasyPbrBakeCore
    {
        internal const int BakeLayer = 31; // レイキャスト隔離用（通常未使用の最上位）

        internal static bool RunBake(GameObject root, Material material, int res, int smooth, int dilate, int blur,
                                     string suffix, string slot, string strengthProp, bool needsCollider,
                                     Func<Renderer, Mesh, float[]> computeR,
                                     Func<Renderer, Mesh, float[]> computeG = null,
                                     Func<Renderer, Mesh, float[]> computeB = null,
                                     Func<Renderer, Mesh, float[]> computeA = null,
                                     float clearValue = 1.0f,
                                     float clearValueG = -1f,
                                     float clearValueB = -1f,
                                     float clearValueA = -1f)
        {
            if (root == null || material == null)
            {
                EditorUtility.DisplayDialog("EasyPBR Baker", "Root（GameObject）と Material が必要です。", "OK");
                return false;
            }

            var renderers = GatherRenderers(root, material);
            if (renderers.Count == 0)
            {
                Debug.LogWarning($"[EasyPBR Baker] '{root.name}' 配下に '{material.name}' を使う Renderer が無いためスキップ。");
                return false;
            }

            var prevBackface = Physics.queriesHitBackfaces;
            var temps = new List<GameObject>();
            var usable = new List<Renderer>();
            var meshes = new List<Mesh>();
            var tempFlags = new List<bool>();
            try
            {
                EditorUtility.DisplayProgressBar("EasyPBR Baker", "メッシュを準備中...", 0.05f);
                foreach (var r in renderers)
                {
                    var m = ResolveMesh(r, out var tmp);
                    if (m == null || m.vertexCount == 0) continue;
                    usable.Add(r); meshes.Add(m); tempFlags.Add(tmp);
                }
                if (usable.Count == 0)
                {
                    EditorUtility.DisplayDialog("EasyPBR Baker",
                        "焼けるメッシュがありません（Read/Write Enabled が無効の可能性）。", "OK");
                    return false;
                }

                if (needsCollider)
                {
                    Physics.queriesHitBackfaces = true;
                    for (var i = 0; i < usable.Count; i++)
                    {
                        var r = usable[i];
                        var go = new GameObject("~EasyPbrBakeCollider") { hideFlags = HideFlags.HideAndDontSave };
                        go.layer = BakeLayer;
                        go.transform.SetPositionAndRotation(r.transform.position, r.transform.rotation);
                        go.transform.localScale = r.transform.lossyScale;
                        var col = go.AddComponent<MeshCollider>();
                        col.sharedMesh = meshes[i];
                        temps.Add(go);
                    }
                    Physics.SyncTransforms();
                }

                var px = new Color32[res * res];
                var clearR = ToClearByte(clearValue);
                var clearG = ToClearByte(clearValueG < 0f ? clearValue : clearValueG);
                var clearB = ToClearByte(clearValueB < 0f ? clearValue : clearValueB);
                var clearA = ToClearByte(clearValueA < 0f ? 1f : clearValueA);
                var clearPx = new Color32(clearR, clearG, clearB, clearA);
                for (var i = 0; i < px.Length; i++) px[i] = clearPx;
                var covered = new bool[res * res];

                for (var i = 0; i < usable.Count; i++)
                {
                    EditorUtility.DisplayProgressBar("EasyPBR Baker",
                        $"計算中... ({i + 1}/{usable.Count})", 0.1f + 0.7f * i / usable.Count);
                    
                    // チャンネルごとの計算とスムージング
                    var vr = computeR(usable[i], meshes[i]);
                    SmoothVertexScalar(meshes[i], vr, smooth);
                    
                    float[] vg = null, vb = null, va = null;
                    if (computeG != null) { vg = computeG(usable[i], meshes[i]); SmoothVertexScalar(meshes[i], vg, smooth); }
                    if (computeB != null) { vb = computeB(usable[i], meshes[i]); SmoothVertexScalar(meshes[i], vb, smooth); }
                    if (computeA != null) { va = computeA(usable[i], meshes[i]); SmoothVertexScalar(meshes[i], va, smooth); }

                    var subs = ResolveSubmeshes(usable[i], material, meshes[i]);
                    RasterizeInto(px, covered, meshes[i], vr, vg, vb, va, res, subs);
                }

                EditorUtility.DisplayProgressBar("EasyPBR Baker", "仕上げ中...", 0.85f);
                var tex = new Texture2D(res, res, TextureFormat.RGBA32, false, true);
                tex.SetPixels32(px);
                tex.Apply(false, false);
                _coverage = covered;
                Dilate(tex, dilate);
                BlurTexture(tex, blur);

                var meshName = usable.Count == 1 ? ResolveSourceMeshName(usable[0]) : root.name;
                var ok = SaveAndAssign(tex, material, meshName, suffix, slot, strengthProp);
                UnityEngine.Object.DestroyImmediate(tex);
                return ok;
            }
            finally
            {
                Physics.queriesHitBackfaces = prevBackface;
                foreach (var go in temps) if (go != null) UnityEngine.Object.DestroyImmediate(go);
                for (var i = 0; i < meshes.Count; i++)
                    if (tempFlags[i] && meshes[i] != null) UnityEngine.Object.DestroyImmediate(meshes[i]);
                EditorUtility.ClearProgressBar();
            }
        }

        private static List<Renderer> GatherRenderers(GameObject root, Material material)
        {
            var list = new List<Renderer>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                if (Array.IndexOf(r.sharedMaterials, material) >= 0) list.Add(r);
            }
            return list;
        }

        private static Mesh ResolveMesh(Renderer renderer, out bool isTemp)
        {
            isTemp = false;
            if (renderer is SkinnedMeshRenderer smr)
            {
                var baked = new Mesh { name = "~bakedPose" };
                smr.BakeMesh(baked, false);
                isTemp = true;
                return baked;
            }
            var mf = renderer.GetComponent<MeshFilter>();
            var shared = mf != null ? mf.sharedMesh : null;
            if (shared != null && !shared.isReadable)
            {
                Debug.LogWarning($"[EasyPBR Baker] '{shared.name}' は Read/Write 無効のためスキップ。インポート設定で有効化してください。");
                return null;
            }
            return shared;
        }

        private static int[] ResolveSubmeshes(Renderer renderer, Material material, Mesh mesh)
        {
            var mats = renderer.sharedMaterials;
            var list = new List<int>();
            for (var i = 0; i < mesh.subMeshCount; i++)
                if (i < mats.Length && mats[i] == material) list.Add(i);
            if (list.Count == 0)
                for (var i = 0; i < mesh.subMeshCount; i++) list.Add(i);
            return list.ToArray();
        }

        private static string ResolveSourceMeshName(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer smr && smr.sharedMesh != null) return smr.sharedMesh.name;
            var mf = renderer.GetComponent<MeshFilter>();
            return mf != null && mf.sharedMesh != null ? mf.sharedMesh.name : renderer.name;
        }

        internal static Vector3[] BuildHemisphere(int count)
        {
            count = Mathf.Max(1, count);
            var dirs = new Vector3[count];
            var ga = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (var i = 0; i < count; i++)
            {
                var z = Mathf.Sqrt((i + 0.5f) / count);
                var r = Mathf.Sqrt(1f - z * z);
                var phi = i * ga;
                dirs[i] = new Vector3(Mathf.Cos(phi) * r, Mathf.Sin(phi) * r, z);
            }
            return dirs;
        }

        internal static void Basis(Vector3 n, out Vector3 t, out Vector3 b)
        {
            var up = Mathf.Abs(n.y) < 0.99f ? Vector3.up : Vector3.right;
            t = Vector3.Normalize(Vector3.Cross(up, n));
            b = Vector3.Cross(n, t);
        }

        private static bool[] _coverage;

        private static void RasterizeInto(Color32[] px, bool[] covered, Mesh mesh,
                                          float[] vR, float[] vG, float[] vB, float[] vA, 
                                          int res, int[] submeshes)
        {
            var uv = mesh.uv;
            if (uv == null || uv.Length != mesh.vertexCount) return;
            
            var hasG = vG != null;
            var hasB = vB != null;
            var hasA = vA != null;

            foreach (var sub in submeshes)
            {
                var tris = mesh.GetTriangles(sub);
                for (var ti = 0; ti < tris.Length; ti += 3)
                {
                    int i0 = tris[ti], i1 = tris[ti + 1], i2 = tris[ti + 2];
                    Vector2 p0 = uv[i0] * res, p1 = uv[i1] * res, p2 = uv[i2] * res;
                    
                    RasterTriangle(px, covered, res, p0, p1, p2,
                        vR[i0], vR[i1], vR[i2],
                        hasG ? vG[i0] : 0f, hasG ? vG[i1] : 0f, hasG ? vG[i2] : 0f,
                        hasB ? vB[i0] : 0f, hasB ? vB[i1] : 0f, hasB ? vB[i2] : 0f,
                        hasA ? vA[i0] : 0f, hasA ? vA[i1] : 0f, hasA ? vA[i2] : 0f,
                        hasG, hasB, hasA);
                }
            }
        }

        private static void RasterTriangle(Color32[] px, bool[] covered, int res,
            Vector2 pA, Vector2 pB, Vector2 pC,
            float ra, float rb, float rc, 
            float ga, float gb, float gc, 
            float ba, float bb, float bc, 
            float aa, float ab, float ac, 
            bool hasG, bool hasB, bool hasA)
        {
            var minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(pA.x, Mathf.Min(pB.x, pC.x))), 0, res - 1);
            var maxX = Mathf.Clamp(Mathf.CeilToInt (Mathf.Max(pA.x, Mathf.Max(pB.x, pC.x))), 0, res - 1);
            var minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(pA.y, Mathf.Min(pB.y, pC.y))), 0, res - 1);
            var maxY = Mathf.Clamp(Mathf.CeilToInt (Mathf.Max(pA.y, Mathf.Max(pB.y, pC.y))), 0, res - 1);

            var denom = (pB.y - pC.y) * (pA.x - pC.x) + (pC.x - pB.x) * (pA.y - pC.y);
            if (Mathf.Abs(denom) < 1e-9f) return;
            var invDen = 1f / denom;

            for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
            {
                float fx = x + 0.5f, fy = y + 0.5f;
                var w0 = ((pB.y - pC.y) * (fx - pC.x) + (pC.x - pB.x) * (fy - pC.y)) * invDen;
                var w1 = ((pC.y - pA.y) * (fx - pC.x) + (pA.x - pC.x) * (fy - pC.y)) * invDen;
                var w2 = 1f - w0 - w1;
                if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f) continue;

                // Rチャンネル計算
                var rByte = (byte)(Mathf.Clamp01(w0 * ra + w1 * rb + w2 * rc) * 255f + 0.5f);
                
                // 1chのみの場合はグレースケール(R=G=B)、2chの場合は G に入り B は 0 になるようにフォールバック
                var gByte = hasG ? (byte)(Mathf.Clamp01(w0 * ga + w1 * gb + w2 * gc) * 255f + 0.5f) : rByte;
                var bByte = hasB ? (byte)(Mathf.Clamp01(w0 * ba + w1 * bb + w2 * bc) * 255f + 0.5f) : (hasG ? (byte)0 : rByte);
                var aByte = hasA ? (byte)(Mathf.Clamp01(w0 * aa + w1 * ab + w2 * ac) * 255f + 0.5f) : (byte)255;

                var idx = y * res + x;
                px[idx] = new Color32(rByte, gByte, bByte, aByte);
                covered[idx] = true;
            }
        }

        private static void Dilate(Texture2D tex, int iterations)
        {
            if (_coverage == null || iterations <= 0) return;
            var res = tex.width;
            var px = tex.GetPixels32();
            var covered = (bool[])_coverage.Clone();

            for (var it = 0; it < iterations; it++)
            {
                var next = (bool[])covered.Clone();
                for (var y = 0; y < res; y++)
                for (var x = 0; x < res; x++)
                {
                    var idx = y * res + x;
                    if (covered[idx]) continue;
                    for (var dy = -1; dy <= 1 && !next[idx]; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= res || ny >= res) continue;
                        var nIdx = ny * res + nx;
                        if (covered[nIdx]) { px[idx] = px[nIdx]; next[idx] = true; break; }
                    }
                }
                covered = next;
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            _coverage = covered;
        }

        private static void SmoothVertexScalar(Mesh mesh, float[] v, int iterations)
        {
            if (iterations <= 0) return;
            var n = v.Length;
            var tris = mesh.triangles;

            for (var it = 0; it < iterations; it++)
            {
                var sum = new float[n];
                var count = new int[n];
                for (var i = 0; i < n; i++) { sum[i] = v[i]; count[i] = 1; }

                for (var t = 0; t < tris.Length; t += 3)
                {
                    int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                    sum[a] += v[b] + v[c]; count[a] += 2;
                    sum[b] += v[a] + v[c]; count[b] += 2;
                    sum[c] += v[a] + v[b]; count[c] += 2;
                }
                for (var i = 0; i < n; i++) v[i] = sum[i] / count[i];
            }
        }

        private static void BlurTexture(Texture2D tex, int radius)
        {
            if (radius <= 0 || _coverage == null) { _coverage = null; return; }
            var res = tex.width;
            var src = tex.GetPixels32();
            var dst = (Color32[])src.Clone();
            var cov = _coverage;

            for (var y = 0; y < res; y++)
            for (var x = 0; x < res; x++)
            {
                var idx = y * res + x;
                if (!cov[idx]) continue;
                int accR = 0, accG = 0, accB = 0, accA = 0, num = 0;
                for (var dy = -radius; dy <= radius; dy++)
                for (var dx = -radius; dx <= radius; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= res || ny >= res) continue;
                    var nIdx = ny * res + nx;
                    if (!cov[nIdx]) continue;
                    accR += src[nIdx].r; accG += src[nIdx].g; accB += src[nIdx].b; accA += src[nIdx].a; num++;
                }
                if (num > 0)
                    dst[idx] = new Color32((byte)(accR / num), (byte)(accG / num), (byte)(accB / num), (byte)(accA / num));
            }
            tex.SetPixels32(dst);
            tex.Apply(false, false);
            _coverage = null;
        }

        private static byte ToClearByte(float v)
            => (byte)(Mathf.Clamp01(v) * 255f + 0.5f);

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        private static bool SaveAndAssign(Texture2D tex, Material material, string meshName,
                                          string suffix, string slot, string strengthProp)
        {
            var matPath = AssetDatabase.GetAssetPath(material);
            var dir = string.IsNullOrEmpty(matPath) ? "Assets" : Path.GetDirectoryName(matPath);
            var bakedDir = Path.Combine(dir, "Baked").Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(bakedDir))
                AssetDatabase.CreateFolder(dir, "Baked");

            // 同名ファイルは上書き（連番で増やさない）。GUID が維持されるため、
            // アサイン済みの参照はそのまま新しい内容に更新される。
            // 以前の結果に戻したい場合は焼き直すか、バージョン管理で戻す。
            var baseName = Sanitize($"{meshName}_{material.name}_{suffix}");
            var path = $"{bakedDir}/{baseName}.png";

            File.WriteAllBytes(path, tex.EncodeToPNG());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                importer.sRGBTexture = false;
                importer.textureType = TextureImporterType.Default;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }

            var imported = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (imported == null) return false;

            Undo.RecordObject(material, "Assign Baked Map");
            if (slot != null && material.HasProperty(slot)) material.SetTexture(slot, imported);
            if (strengthProp != null && material.HasProperty(strengthProp) && material.GetFloat(strengthProp) <= 0f)
                material.SetFloat(strengthProp, 1f);
            EditorUtility.SetDirty(material);

            var assignNote = (slot != null && material.HasProperty(slot)) ? $"（{slot} に自動アサイン）" : "（保存のみ）";
            Debug.Log($"[EasyPBR Baker] {suffix} baked → {path} {assignNote}", imported);
            return true;
        }
    }
}
