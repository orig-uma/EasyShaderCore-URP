// =============================================================================
//  DissolveEdgeSampler.cs
// -----------------------------------------------------------------------------
//  Dissolve（消失）の「エッジ帯（最前線）」に乗るスキン済み頂点を GPU で抽出し、
//  ワールド座標の点群として VFX Graph へ渡すコンポーネント。キャラのルートに付ける。
//
//  方式（パフォーマンス最優先・CPU readback ゼロ）:
//   1. 配下 Renderer から Dissolve プロパティを持つマテリアルを収集
//   2. SkinnedMeshRenderer.GetVertexBuffer() でスキン済み position/normal を、
//      sharedMesh.GetVertexBuffer(uvStream) で uv0 を ByteAddressBuffer として取得
//   3. ComputeShader（DissolveEdgeSample.compute）でエッジ帯頂点だけを
//      AppendStructuredBuffer へ書き出す（間引きストライド対応）
//   4. GraphicsBuffer.CopyCount で件数を 4byte バッファへ。VFX へバインド
//
//  既知の制約:
//   - スキニング反映は 1 フレーム遅延し得る（パーティクル用途では許容）
//   - マテリアルプロパティは sharedMaterial から読む（MaterialPropertyBlock 非対応・v1）
//   - 頂点フォーマットは position/normal/uv0 とも Float32 前提
//   - GetVertexBuffer() は呼ぶたびに新規参照を返すため、毎フレーム取得→Dispose する
// =============================================================================
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
#if ORIGUMA_VFXGRAPH
using UnityEngine.VFX;
#endif

namespace Origuma.EasyShaderCore
{
#if ORIGUMA_VFXGRAPH
    // VFX Graph の GraphicsBuffer 型として公開する 1 点の構造体。
    // compute の DissolveEdgePoint と厳密に同一レイアウト（32 bytes）。
    [VFXType(VFXTypeAttribute.Usage.GraphicsBuffer)]
    public struct DissolveEdgePoint
    {
        public Vector3 positionWS;
        public Vector3 normalWS;
        public float   edgeFactor;
        public float   _pad;
    }
#endif

    [AddComponentMenu("Origuma/EasyShaderCore/Dissolve Edge Sampler")]
    [DisallowMultipleComponent]
    public class DissolveEdgeSampler : MonoBehaviour
    {
        // 構造体サイズ（bytes）= float3 + float3 + float + pad。
        public const int PointStride = 32;

        // ------------------------------------------------------------------
        //  Inspector
        // ------------------------------------------------------------------
        [Tooltip("エッジ抽出 ComputeShader（DissolveEdgeSample.compute）。未指定なら Editor で自動アサインを試みる。")]
        [SerializeField] private ComputeShader edgeCompute;

        [Tooltip("頂点の間引きストライド。大きいほど点が疎になり高速。既定 4。")]
        [Min(1)]
        [SerializeField] private int sampleStride = 4;

        [Tooltip("抽出点の最大数（AppendBuffer 容量）。超過分は破棄される。")]
        [Min(64)]
        [SerializeField] private int maxPoints = 65536;

        [Tooltip("空欄なら配下の Renderer から Dissolve マテリアルを自動収集する。指定時はこのリストのみ対象。")]
        [SerializeField] private List<Renderer> manualRenderers = new List<Renderer>();

#if ORIGUMA_VFXGRAPH
        [Header("VFX Graph 連携")]
        [Tooltip("毎フレーム点群・件数・進行度・色を渡す先の VisualEffect。")]
        public List<VisualEffect> targets = new List<VisualEffect>();
#endif

        // VFX 側の Exposed プロパティ名（Documentation~/VFX_DISSOLVE.md と一致）。
        private const string PropPoints = "DissolveEdgePoints";
        private const string PropCount  = "DissolveEdgeCount";
        private const string PropAmount = "DissolveAmount";
        private const string PropColor  = "DissolveEdgeColor";

        // Dissolve プロパティ ID（Doll/Idol と同名）。
        private static readonly int IdDissolveAmount        = Shader.PropertyToID("_DissolveAmount");
        private static readonly int IdDissolveType          = Shader.PropertyToID("_DissolveType");
        private static readonly int IdDissolveInvert        = Shader.PropertyToID("_DissolveInvert");
        private static readonly int IdDissolveStartY        = Shader.PropertyToID("_DissolveStartY");
        private static readonly int IdDissolveEndY          = Shader.PropertyToID("_DissolveEndY");
        private static readonly int IdDissolveNoiseScale    = Shader.PropertyToID("_DissolveNoiseScale");
        private static readonly int IdDissolveNoiseStrength = Shader.PropertyToID("_DissolveNoiseStrength");
        private static readonly int IdDissolveEdgeWidth     = Shader.PropertyToID("_DissolveEdgeWidth");
        private static readonly int IdDissolveEdgeStep      = Shader.PropertyToID("_DissolveEdgeStep");
        private static readonly int IdDissolveEdgeColor     = Shader.PropertyToID("_DissolveEdgeColor");
        private static readonly int IdDissolveTex           = Shader.PropertyToID("_DissolveTex");

        private const string KeywordDissolve = "_DISSOLVE_ON";

        // compute 側のプロパティ ID。
        private static readonly int CsPositionBuffer  = Shader.PropertyToID("_PositionBuffer");
        private static readonly int CsUVBuffer        = Shader.PropertyToID("_UVBuffer");
        private static readonly int CsEdgePoints      = Shader.PropertyToID("_EdgePoints");
        private static readonly int CsDissolveTex     = Shader.PropertyToID("_DissolveTex");
        private static readonly int CsVertexCount     = Shader.PropertyToID("_VertexCount");
        private static readonly int CsSampleStride    = Shader.PropertyToID("_SampleStride");
        private static readonly int CsPositionStride  = Shader.PropertyToID("_PositionStride");
        private static readonly int CsPositionOffset  = Shader.PropertyToID("_PositionOffset");
        private static readonly int CsNormalOffset    = Shader.PropertyToID("_NormalOffset");
        private static readonly int CsUVStride        = Shader.PropertyToID("_UVStride");
        private static readonly int CsUVOffset        = Shader.PropertyToID("_UVOffset");
        private static readonly int CsLocalToWorld    = Shader.PropertyToID("_LocalToWorld");

        // ------------------------------------------------------------------
        //  内部状態
        // ------------------------------------------------------------------
        // 収集した対象 Renderer のキャッシュ。
        private struct Entry
        {
            public Renderer renderer;
            public SkinnedMeshRenderer skinned;   // null なら静的 MeshRenderer
            public MeshFilter filter;             // 静的時のみ
            public Mesh mesh;                     // 頂点属性メタ取得用（skinned は sharedMesh）
            public Material material;             // Dissolve プロパティ保持マテリアル
            public int positionStride;
            public int positionOffset;
            public int normalOffset;
            public int uvStream;
            public int uvStride;
            public int uvOffset;
            public bool valid;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private GraphicsBuffer _edgeBuffer;   // AppendStructuredBuffer<DissolveEdgePoint>
        private GraphicsBuffer _countBuffer;  // uint 1 個
        private int _kernel = -1;
        private bool _initialized;

        // ------------------------------------------------------------------
        //  ライフサイクル
        // ------------------------------------------------------------------
        private void OnEnable()
        {
            Initialize();
        }

        private void OnDisable()
        {
            ReleaseBuffers();
            _initialized = false;
        }

        // スキニング反映後に走らせたいので LateUpdate で Dispatch。
        // （厳密には GPU スキニングは同フレーム描画時なので 1 フレーム遅延し得るが許容）
        private void LateUpdate()
        {
            if (!_initialized) return;
            Dispatch();
        }

        // ------------------------------------------------------------------
        //  初期化
        // ------------------------------------------------------------------
        // 対象 Renderer / マテリアル参照を収集し直す public ラッパ。
        // DissolveController が Play 中にマテリアルをインスタンス化した直後に呼ぶ想定
        // （インスタンス化で sharedMaterial の参照先が変わるため、キャッシュを取り直す）。
        public void Reinitialize()
        {
            ReleaseBuffers();
            Initialize();
        }

        private void Initialize()
        {
            _initialized = false;
            _entries.Clear();

            if (edgeCompute == null)
            {
                Debug.LogWarning($"[DissolveEdgeSampler] ComputeShader 未アサイン。'{name}' の抽出をスキップします。", this);
                return;
            }
            _kernel = edgeCompute.FindKernel("CSMain");

            CollectRenderers();
            if (_entries.Count == 0) return;

            AllocateBuffers();
            _initialized = true;
        }

        // 対象 Renderer を収集し、頂点属性メタをキャッシュ。
        private void CollectRenderers()
        {
            var source = new List<Renderer>();
            if (manualRenderers != null && manualRenderers.Count > 0)
            {
                foreach (var r in manualRenderers)
                    if (r != null) source.Add(r);
            }
            else
            {
                GetComponentsInChildren(true, source);
            }

            foreach (var r in source)
            {
                var mat = FindDissolveMaterial(r);
                if (mat == null) continue;

                var entry = new Entry { renderer = r, material = mat };

                if (r is SkinnedMeshRenderer smr)
                {
                    if (smr.sharedMesh == null) continue;
                    entry.skinned = smr;
                    entry.mesh = smr.sharedMesh;
                    // スキン済み頂点を Raw で GPU から読めるようにする。
                    smr.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
                }
                else if (r is MeshRenderer)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    entry.filter = mf;
                    entry.mesh = mf.sharedMesh;
                }
                else
                {
                    continue; // その他 Renderer は非対応
                }

                // UV0 はスキンで変わらないので sharedMesh 側を Raw 化。
                entry.mesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;

                if (!entry.mesh.HasVertexAttribute(VertexAttribute.Position) ||
                    !entry.mesh.HasVertexAttribute(VertexAttribute.Normal) ||
                    !entry.mesh.HasVertexAttribute(VertexAttribute.TexCoord0))
                    continue;

                int posStream = entry.mesh.GetVertexAttributeStream(VertexAttribute.Position);
                entry.positionStride = entry.mesh.GetVertexBufferStride(posStream);
                entry.positionOffset = entry.mesh.GetVertexAttributeOffset(VertexAttribute.Position);
                entry.normalOffset   = entry.mesh.GetVertexAttributeOffset(VertexAttribute.Normal);

                entry.uvStream = entry.mesh.GetVertexAttributeStream(VertexAttribute.TexCoord0);
                entry.uvStride = entry.mesh.GetVertexBufferStride(entry.uvStream);
                entry.uvOffset = entry.mesh.GetVertexAttributeOffset(VertexAttribute.TexCoord0);

                entry.valid = true;
                _entries.Add(entry);
            }
        }

        // Renderer の共有マテリアルから Dissolve プロパティを持つ最初のものを返す。
        private static Material FindDissolveMaterial(Renderer r)
        {
            var mats = r.sharedMaterials;
            if (mats == null) return null;
            foreach (var m in mats)
                if (m != null && m.HasProperty(IdDissolveAmount))
                    return m;
            return null;
        }

        private void AllocateBuffers()
        {
            ReleaseBuffers();
            _edgeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Append, maxPoints, PointStride);
            _countBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopyDestination, 1, sizeof(uint));
        }

        private void ReleaseBuffers()
        {
            _edgeBuffer?.Dispose();
            _edgeBuffer = null;
            _countBuffer?.Dispose();
            _countBuffer = null;
        }

        // ------------------------------------------------------------------
        //  Dispatch
        // ------------------------------------------------------------------
        private void Dispatch()
        {
            // 毎フレーム AppendBuffer のカウンタをリセット。
            _edgeBuffer.SetCounterValue(0);

            int stride = Mathf.Max(1, sampleStride);
            Material amountSourceMat = null; // VFX へ渡す代表マテリアル（最初の有効エントリ）

            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (!e.valid || e.renderer == null || !e.renderer.enabled || !e.renderer.gameObject.activeInHierarchy)
                    continue;
                // キーワードを要求するのは宣言しているシェーダーだけ（Controller と同じ緩和。
                // キーワードレスの Idol では IsKeywordEnabled が常に false になるため）。
                if (e.material == null ||
                    (!e.material.IsKeywordEnabled(KeywordDissolve) &&
                     e.material.shader != null &&
                     e.material.shader.keywordSpace.FindKeyword(KeywordDissolve).isValid))
                    continue;

                float amount = e.material.HasProperty(IdDissolveAmount) ? e.material.GetFloat(IdDissolveAmount) : 0f;
                // 完全表示(<=0) / 完全消失(>=1) は帯が無いので Dispatch 自体をスキップ（ゼロ点）。
                if (amount <= 0f || amount >= 1f) continue;

                // GetVertexBuffer() は毎回新規参照を返すので、使い終わったら必ず Dispose。
                GraphicsBuffer posBuffer = null;
                GraphicsBuffer uvBuffer = null;
                try
                {
                    if (e.skinned != null)
                        posBuffer = e.skinned.GetVertexBuffer();
                    else
                        posBuffer = e.mesh.GetVertexBuffer(
                            e.mesh.GetVertexAttributeStream(VertexAttribute.Position));

                    if (posBuffer == null) continue;

                    uvBuffer = e.mesh.GetVertexBuffer(e.uvStream);
                    if (uvBuffer == null) continue;

                    int vertexCount = e.mesh.vertexCount;

                    edgeCompute.SetBuffer(_kernel, CsPositionBuffer, posBuffer);
                    edgeCompute.SetBuffer(_kernel, CsUVBuffer, uvBuffer);
                    edgeCompute.SetBuffer(_kernel, CsEdgePoints, _edgeBuffer);

                    // ノイズ未設定時は white にフォールバック（未バインドの compute テクスチャは
                    // 結果不定になるため。マテリアル側の _DissolveTex 既定 "white" とも一致）。
                    var noiseTex = e.material.HasProperty(IdDissolveTex) ? e.material.GetTexture(IdDissolveTex) : null;
                    edgeCompute.SetTexture(_kernel, CsDissolveTex, noiseTex != null ? noiseTex : Texture2D.whiteTexture);

                    edgeCompute.SetInt(CsVertexCount, vertexCount);
                    edgeCompute.SetInt(CsSampleStride, stride);
                    edgeCompute.SetInt(CsPositionStride, e.positionStride);
                    edgeCompute.SetInt(CsPositionOffset, e.positionOffset);
                    edgeCompute.SetInt(CsNormalOffset, e.normalOffset);
                    edgeCompute.SetInt(CsUVStride, e.uvStride);
                    edgeCompute.SetInt(CsUVOffset, e.uvOffset);
                    edgeCompute.SetMatrix(CsLocalToWorld, e.renderer.localToWorldMatrix);

                    SetDissolveParams(e.material);

                    // 間引き後の走査数からグループ数を決定（64 スレッド/グループ）。
                    int sampleCount = (vertexCount + stride - 1) / stride;
                    int groups = Mathf.Max(1, (sampleCount + 63) / 64);
                    edgeCompute.Dispatch(_kernel, groups, 1, 1);

                    if (amountSourceMat == null) amountSourceMat = e.material;
                }
                finally
                {
                    posBuffer?.Dispose();
                    uvBuffer?.Dispose();
                }
            }

            // 全 Renderer 分を積んだ後に一度だけ件数をコピー。
            GraphicsBuffer.CopyCount(_edgeBuffer, _countBuffer, 0);

#if ORIGUMA_VFXGRAPH
            PushToVfx(amountSourceMat);
#endif
        }

        // compute の cbuffer へ Dissolve プロパティを写す。
        private void SetDissolveParams(Material m)
        {
            edgeCompute.SetFloat(IdDissolveAmount,        GetFloat(m, IdDissolveAmount, 0f));
            edgeCompute.SetFloat(IdDissolveType,          GetFloat(m, IdDissolveType, 0f));
            edgeCompute.SetFloat(IdDissolveInvert,        GetFloat(m, IdDissolveInvert, 0f));
            edgeCompute.SetFloat(IdDissolveStartY,        GetFloat(m, IdDissolveStartY, 0f));
            edgeCompute.SetFloat(IdDissolveEndY,          GetFloat(m, IdDissolveEndY, 1f));
            edgeCompute.SetFloat(IdDissolveNoiseScale,    GetFloat(m, IdDissolveNoiseScale, 1f));
            edgeCompute.SetFloat(IdDissolveNoiseStrength, GetFloat(m, IdDissolveNoiseStrength, 0f));
            edgeCompute.SetFloat(IdDissolveEdgeWidth,     GetFloat(m, IdDissolveEdgeWidth, 0.05f));
            edgeCompute.SetFloat(IdDissolveEdgeStep,      GetFloat(m, IdDissolveEdgeStep, 0f));
        }

        private static float GetFloat(Material m, int id, float fallback)
            => m.HasProperty(id) ? m.GetFloat(id) : fallback;

#if ORIGUMA_VFXGRAPH
        // 点群・件数・進行度・色を各 VisualEffect へ渡す（存在するプロパティのみ）。
        private void PushToVfx(Material amountSourceMat)
        {
            if (targets == null) return;

            float amount = amountSourceMat != null ? GetFloat(amountSourceMat, IdDissolveAmount, 0f) : 0f;
            Color edgeColor = amountSourceMat != null && amountSourceMat.HasProperty(IdDissolveEdgeColor)
                ? amountSourceMat.GetColor(IdDissolveEdgeColor)
                : Color.white;

            foreach (var vfx in targets)
            {
                if (vfx == null) continue;
                if (vfx.HasGraphicsBuffer(PropPoints)) vfx.SetGraphicsBuffer(PropPoints, _edgeBuffer);
                if (vfx.HasGraphicsBuffer(PropCount))  vfx.SetGraphicsBuffer(PropCount, _countBuffer);
                if (vfx.HasFloat(PropAmount))          vfx.SetFloat(PropAmount, amount);
                if (vfx.HasVector4(PropColor))         vfx.SetVector4(PropColor, edgeColor);
            }
        }
#endif

#if UNITY_EDITOR
        // コンポーネント追加時、パッケージ内の compute を自動アサイン。
        private void Reset()
        {
            if (edgeCompute != null) return;
            const string path =
                "Packages/com.origuma.easyshader-core/Runtime/Shaders/Vfx/DissolveEdgeSample.compute";
            edgeCompute = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
        }
#endif
    }
}
