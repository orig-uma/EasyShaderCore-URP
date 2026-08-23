// =============================================================================
//  BlackOutController.cs
// -----------------------------------------------------------------------------
//  暗転（`_BlackOut`）の司令塔。キャラのルートに 1 つ付け、`amount` を書けば
//  配下マテリアルの `_BlackOut` へ一括反映される。Timeline の Animation Track
//  から `amount` に直接キーを打つ運用を想定（専用トラックは持たない）。
//
//  **なぜ要るか。** 暗転はマテリアル 1 枚ずつ動かす類の値ではない ── キャラ
//  まるごと同時に沈めるものなので、Inspector のスライダーを 20 枚も手で動かす
//  形では演出に使えない。EasyPBR には `DollLiveDirector` があるが EasyToon の
//  Idol には相当物が無く、暗転を実装（T-361）しても駆動する手段が無かった。
//
//  **Doll / Idol 両方で動く。** `_BlackOut` は両シェーダーで同名・同じ意味
//  （最終色を黒へ lerp）なので、シェーダーを問わずプロパティの有無だけで拾う。
//  Dissolve を Core の `DissolveController` へ一本化したのと同じ考え方。
//
//  **DollLiveDirector との併用に注意。** あちらも `_BlackOut` を上書きできる。
//  同じキャラに両方付けて両方が有効だと書き込み合戦になるので、どちらか一方に
//  寄せること（新規は本 Controller を推奨）。
//
//  設計（`DissolveController` / `DollLiveDirector` の作法をそのまま踏襲）:
//  - Play 中は「マテリアルインスタンス」経由で書く。MaterialPropertyBlock は
//    レンダラーを SRP Batcher から外すため使わない。`.materials` で初回に
//    インスタンス化し、以後はそのインスタンスへ SetFloat。OnDisable(Play) で
//    元値へ復元する。
//  - Edit モード（非 Play）のプレビューだけ非破壊の MaterialPropertyBlock を
//    使い、共有マテリアル資産を汚さない。解除は自分が当てた Renderer だけ。
// =============================================================================
using System.Collections.Generic;
using UnityEngine;

namespace Origuma.EasyShaderCore
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Origuma/EasyShaderCore/Black Out Controller")]
    public class BlackOutController : MonoBehaviour
    {
        // ------------------------------------------------------------------
        //  Inspector
        // ------------------------------------------------------------------
        [Tooltip("暗転の深さ 0..1（1 で真っ黒）。Timeline の Animation Track から直接キーを " +
                 "打てるようプロパティではなく public フィールドにしている。")]
        [Range(0f, 1f)]
        public float amount = 0f;

        [Tooltip("空欄なら配下の Renderer から対象マテリアルを自動収集する。指定時はこのリストのみ対象。")]
        [SerializeField] private List<Renderer> manualRenderers = new List<Renderer>();

        // Doll / Idol とも同名・同じ意味のプロパティ。
        private static readonly int IdBlackOut = Shader.PropertyToID("_BlackOut");

        // ------------------------------------------------------------------
        //  内部状態
        // ------------------------------------------------------------------
        private struct TargetMat
        {
            public Material mat;
            public float    origAmount;
        }

        private readonly List<TargetMat> _targets = new List<TargetMat>();
        private Renderer[] _renderers;
        private MaterialPropertyBlock _mpb;   // Edit モードプレビュー専用

        // Edit プレビューを当てた Renderer の記録。解除はこの集合に対してのみ行う
        // （無差別に SetPropertyBlock(null) を打つと、DissolveController や
        //   FaceDirectionBinder が当てたプレビューまで消してしまう）。
        private readonly HashSet<Renderer> _previewed = new HashSet<Renderer>();

        private bool  _instancesReady;
        private float _lastApplied;      // 前回書き込んだ値（変更検知用）
        private bool  _hasLastApplied;   // まだ一度も書いていない（初回強制書き込み用）
        private int   _editTargetCount;  // Edit モードの対象数（表示用）

        /// <summary>現在の対象マテリアル数（Inspector の読み取り専用ラベル用）。</summary>
        public int TargetMaterialCount => _targets.Count;

        // ------------------------------------------------------------------
        //  ライフサイクル
        // ------------------------------------------------------------------
        private void OnEnable()
        {
            _renderers = CollectRenderers();
            _instancesReady = false;
            _hasLastApplied = false;   // 次の LateUpdate で必ず一度書く
        }

        private void OnDisable()
        {
            if (Application.isPlaying)
            {
                RestoreAll();
            }
            else
            {
                foreach (var r in _previewed)
                    if (r != null) r.SetPropertyBlock(null);
                _previewed.Clear();
            }
        }

        // アニメーションが amount を動かした後の最終値を拾うため LateUpdate。
        private void LateUpdate()
        {
            if (_renderers == null || _renderers.Length == 0) return;

            if (Application.isPlaying)
            {
                if (!_instancesReady) CollectInstances();
                // 暗転は「動かない時間」の方が長いので前回値スキップが実際に効く
                //（毎フレーム書くと同じ値で CBUFFER を送り直させるだけになる）。
                if (_hasLastApplied && _lastApplied == amount) return;
                ApplyToInstances();
                _lastApplied = amount;
                _hasLastApplied = true;
            }
            else
            {
                ApplyEditPreview();
            }
        }

        // ------------------------------------------------------------------
        //  収集
        // ------------------------------------------------------------------
        private Renderer[] CollectRenderers()
        {
            if (manualRenderers != null && manualRenderers.Count > 0)
            {
                var list = new List<Renderer>();
                foreach (var r in manualRenderers)
                    if (r != null) list.Add(r);
                return list.ToArray();
            }
            return GetComponentsInChildren<Renderer>(true);
        }

        // **キーワードは要求しない。** `_BlackOut` は Doll / Idol とも一様分岐
        // （というより単なる lerp）で、キーワードを持たない。プロパティの有無だけで
        // 判定すれば、暗転を持つシェーダーはすべて対象になる。
        private static bool IsBlackOutTarget(Material m)
            => m != null && m.HasProperty(IdBlackOut);

        // ------------------------------------------------------------------
        //  Play: マテリアルインスタンス経由（SRP Batcher 維持）
        // ------------------------------------------------------------------
        private void CollectInstances()
        {
            _targets.Clear();
            if (_renderers == null) { _instancesReady = true; return; }

            foreach (var r in _renderers)
            {
                if (r == null) continue;

                bool hasTarget = false;
                foreach (var m in r.sharedMaterials)
                    if (IsBlackOutTarget(m)) { hasTarget = true; break; }
                if (!hasTarget) continue;

                // .materials アクセスでスロット全体がインスタンス化される（初回のみ）。
                // 別マテリアル同士でも同一バリアントなら SRP Batcher でまとまるので、
                // 対象外スロットが巻き添えでインスタンス化されても害は無い。
                foreach (var m in r.materials)
                {
                    if (!IsBlackOutTarget(m)) continue;
                    _targets.Add(new TargetMat
                    {
                        mat        = m,
                        origAmount = m.GetFloat(IdBlackOut),
                    });
                }
            }
            _instancesReady = true;
        }

        private void ApplyToInstances()
        {
            foreach (var t in _targets)
            {
                if (t.mat == null) continue;
                t.mat.SetFloat(IdBlackOut, amount);
            }
        }

        private void RestoreAll()
        {
            foreach (var t in _targets)
            {
                if (t.mat == null) continue;
                t.mat.SetFloat(IdBlackOut, t.origAmount);
            }
            _targets.Clear();
            _instancesReady = false;   // 再有効化で収集し直す
        }

        // ------------------------------------------------------------------
        //  Edit: MaterialPropertyBlock プレビュー（非破壊・資産を汚さない）
        // ------------------------------------------------------------------
        private void ApplyEditPreview()
        {
            _mpb ??= new MaterialPropertyBlock();
            int count = 0;

            foreach (var r in _renderers)
            {
                if (r == null) continue;

                bool hasTarget = false;
                foreach (var m in r.sharedMaterials)
                    if (IsBlackOutTarget(m)) { hasTarget = true; count++; }
                if (!hasTarget)
                {
                    if (_previewed.Remove(r)) r.SetPropertyBlock(null);
                    continue;
                }

                // 他コンポーネントが入れた値を消さないよう、既存のブロックを読んでから
                // 上書きする（MPB は Renderer に 1 枚しか無い）。
                r.GetPropertyBlock(_mpb);
                _mpb.SetFloat(IdBlackOut, amount);
                r.SetPropertyBlock(_mpb);
                _previewed.Add(r);
            }

            if (!_instancesReady) _editTargetCount = count;
        }

        // ------------------------------------------------------------------
        //  スクリプト API
        // ------------------------------------------------------------------
        /// <summary>暗転の深さを設定する（0..1 にクランプ）。</summary>
        public void SetAmount(float value)
        {
            amount = Mathf.Clamp01(value);
        }

#if UNITY_EDITOR
        // 対象マテリアル数の読み取り専用ラベルを既定 Inspector の下に足すだけの最小エディタ。
        [UnityEditor.CustomEditor(typeof(BlackOutController))]
        private class BlackOutControllerEditor : UnityEditor.Editor
        {
            public override void OnInspectorGUI()
            {
                DrawDefaultInspector();

                var c = (BlackOutController)target;
                int count = Application.isPlaying ? c.TargetMaterialCount : c._editTargetCount;
                UnityEditor.EditorGUILayout.Space();
                using (new UnityEditor.EditorGUI.DisabledScope(true))
                    UnityEditor.EditorGUILayout.IntField("対象マテリアル数", count);
            }
        }
#endif
    }
}
