// =============================================================================
//  DissolveController.cs
// -----------------------------------------------------------------------------
//  Dissolve（消失）の唯一の司令塔。キャラのルートに 1 つ付け、`amount` を書けば
//  配下マテリアルの `_DissolveAmount` と、同居する DissolveEdgeSampler の両方へ
//  一括反映される。`invert`（`_DissolveInvert`）で消える／現れるの向きも同時に
//  制御でき、キャラ入れ替わり演出（DissolveSwapController）はこの 1 点だけを
//  叩く。シーンに管理オブジェクトを並べず、Timeline / スクリプトからこの Controller
//  だけを動かす運用を想定する（マテリアルへ直接書くのは本 Controller のみ）。
//
//  設計（EasyPBR の DollLiveDirector.cs の作法をそのまま踏襲）:
//  - Play 中は「マテリアルインスタンス」経由で値を書く。MaterialPropertyBlock は
//    レンダラーを SRP Batcher から外すため使わない（別マテリアル同士は SRP
//    Batcher で問題なくバッチされる）。`.materials` で初回にインスタンス化し、
//    以後はそのインスタンスへ SetFloat する。OnDisable(Play) で元値へ復元。
//  - Edit モード（非 Play）のプレビューだけは非破壊な MaterialPropertyBlock を
//    使う（共有マテリアル資産を汚さない）。
//
//  対象マテリアルの収集ポリシー:
//  - 配下 Renderer の sharedMaterials から `_DissolveAmount` を持ち、**かつ
//    キーワード `_DISSOLVE_ON` が有効**なマテリアルだけを対象にする。
//    キーワードが無効なマテリアルには一切書き込まない（Dissolve が分岐 OFF の
//    ため変数を書いても描画に反映されず、無駄・誤爆になるだけだから）。
//    → 想定フロー: マテリアル側で Enable Dissolve を ON・Amount 0 にしておき、
//      本コンポーネントで Amount を動かす。
//
//  DissolveEdgeSampler 連携:
//  - 同 GameObject または配下から自動取得（手動指定・null 許容 = Sampler 無しでも
//    マテリアル制御だけで動作）。
//  - Play 中にマテリアルをインスタンス化した直後、Sampler が sharedMaterial 参照を
//    キャッシュし直すよう Sampler.Reinitialize() を呼ぶ。Sampler は毎フレーム
//    マテリアルから値を読むので、以後は amount 変更に自動追従する。
//  - 【制約】Edit 中の MaterialPropertyBlock プレビューは Sampler へ反映されない
//    （Sampler は sharedMaterial 読みの v1 制約。VFX_DISSOLVE.md 参照）。
// =============================================================================
using System.Collections.Generic;
using UnityEngine;

namespace Origuma.EasyShaderCore
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Origuma/EasyShaderCore/Dissolve Controller")]
    public class DissolveController : MonoBehaviour
    {
        // ------------------------------------------------------------------
        //  Inspector
        // ------------------------------------------------------------------
        [Tooltip("消失の進行度 0..1。Timeline の Animation Track から直接キーを打てるよう " +
                 "プロパティではなく public フィールドにしている（変更は毎フレーム前回値と比較して検知）。")]
        [Range(0f, 1f)]
        public float amount = 0f;

        [Tooltip("消失の向きを反転する（`_DissolveInvert`）。false=消えていく側 / true=現れる側。" +
                 "キャラ入れ替わり（DissolveSwapController）で B 側に true を渡すのに使う。")]
        public bool invert = false;

        [Tooltip("空欄なら配下の Renderer から対象マテリアルを自動収集する。指定時はこのリストのみ対象。")]
        [SerializeField] private List<Renderer> manualRenderers = new List<Renderer>();

        [Tooltip("連携する DissolveEdgeSampler。空欄なら同 GameObject / 配下から自動取得（無くても可）。")]
        [SerializeField] private DissolveEdgeSampler edgeSampler;

        // Dissolve プロパティ ID とキーワード（Doll/Idol と同名）。
        private static readonly int IdDissolveAmount = Shader.PropertyToID("_DissolveAmount");
        private static readonly int IdDissolveInvert = Shader.PropertyToID("_DissolveInvert");
        private const string KeywordDissolve = "_DISSOLVE_ON";

        // ------------------------------------------------------------------
        //  内部状態
        // ------------------------------------------------------------------
        // Play: 対象マテリアルインスタンスと、復元用の元値。
        private struct TargetMat
        {
            public Material mat;
            public float origAmount;
            public float origInvert;
        }

        private readonly List<TargetMat> _targets = new List<TargetMat>();
        private Renderer[] _renderers;
        private MaterialPropertyBlock _mpb;   // Edit モードプレビュー専用
        // Edit プレビューを当てた Renderer の記録。解除はこの集合に対してのみ行う
        // （無差別に SetPropertyBlock(null) を打つと、DollLiveDirector / IdolCharacter 等の
        //   Edit プレビュー MPB を消してしまうため）。
        private readonly HashSet<Renderer> _previewed = new HashSet<Renderer>();
        private bool _instancesReady;
        private float _lastApplied = float.NaN;      // 前回書き込んだ amount（変更検知用）
        private bool _lastAppliedInvert;             // 前回書き込んだ invert（変更検知用）
        private bool _hasLastApplied;                // まだ一度も書いていない（初回強制書き込み用）

        // Inspector 表示用（読み取り専用ラベル）。現在の対象マテリアル数。
        public int TargetMaterialCount => _targets.Count;

        // ------------------------------------------------------------------
        //  ライフサイクル
        // ------------------------------------------------------------------
        private void OnEnable()
        {
            _renderers = CollectRenderers();
            if (edgeSampler == null) edgeSampler = GetComponentInChildren<DissolveEdgeSampler>(true);
            _instancesReady = false;
            _hasLastApplied = false; // 次の LateUpdate で必ず一度書く（amount / invert 両方）
        }

        private void OnDisable()
        {
            if (Application.isPlaying)
            {
                RestoreAll();
            }
            else
            {
                // 自分がプレビューを当てた Renderer だけを解除する。
                foreach (var r in _previewed)
                    if (r != null) r.SetPropertyBlock(null);
                _previewed.Clear();
            }
        }

        // DollLiveDirector と同じく LateUpdate で反映（アニメ後の最終値を拾う）。
        private void LateUpdate()
        {
            if (_renderers == null || _renderers.Length == 0) return;

            if (Application.isPlaying)
            {
                if (!_instancesReady) CollectInstances();
                // 前回値と一致していれば書き込みをスキップ（amount・invert 両方を見る）。
                if (_hasLastApplied && _lastApplied == amount && _lastAppliedInvert == invert) return;
                ApplyToInstances();
                _lastApplied = amount;
                _lastAppliedInvert = invert;
                _hasLastApplied = true;
            }
            else
            {
                // Edit プレビューは軽いので毎フレーム更新（キーワード状態の変化にも追従）。
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

        // `_DissolveAmount` を持ち、かつ `_DISSOLVE_ON` が有効なマテリアルだけを対象とする。
        //
        // **キーワードを要求するのは、シェーダーがそのキーワードを宣言している場合だけ。**
        // EasyToon の Idol はバリアント倍増を避けるため Dissolve を意図的に
        // キーワードレスで実装している（`_DissolveAmount > 0` の一様分岐）。
        // 宣言の無いシェーダーで IsKeywordEnabled は常に false を返すため、
        // 旧判定では Idol マテリアルが**無言で対象 0 件**になっていた。
        private static bool IsDissolveTarget(Material m)
            => m != null
               && m.HasProperty(IdDissolveAmount)
               && (m.IsKeywordEnabled(KeywordDissolve) || !DeclaresDissolveKeyword(m));

        // シェーダーが `_DISSOLVE_ON` をローカルキーワードとして宣言しているか。
        // Doll / Cel（宣言あり）は従来どおり「有効なものだけ」、
        // キーワードレスのシェーダーは `_DissolveAmount` の存在だけで通す。
        private static bool DeclaresDissolveKeyword(Material m)
            => m.shader != null
               && m.shader.keywordSpace.FindKeyword(KeywordDissolve).isValid;

        // ------------------------------------------------------------------
        //  Play: マテリアルインスタンス経由（SRP Batcher 維持）
        // ------------------------------------------------------------------
        private void CollectInstances()
        {
            _targets.Clear();
            if (_renderers == null) { _instancesReady = true; return; }

            bool instancedAny = false;
            foreach (var r in _renderers)
            {
                if (r == null) continue;

                bool hasTarget = false;
                foreach (var m in r.sharedMaterials)
                    if (IsDissolveTarget(m)) { hasTarget = true; break; }
                if (!hasTarget) continue;

                // .materials アクセスでスロット全体がインスタンス化される（初回のみ）。
                var mats = r.materials;
                instancedAny = true;
                foreach (var m in mats)
                {
                    if (!IsDissolveTarget(m)) continue;
                    _targets.Add(new TargetMat
                    {
                        mat = m,
                        origAmount = m.GetFloat(IdDissolveAmount),
                        origInvert = m.HasProperty(IdDissolveInvert) ? m.GetFloat(IdDissolveInvert) : 0f,
                    });
                }
            }
            _instancesReady = true;

            // マテリアルをインスタンス化したら、Sampler が新しい sharedMaterial 参照を
            // キャッシュし直すよう再初期化させる（以後は自動追従）。
            if (instancedAny && edgeSampler != null)
                edgeSampler.Reinitialize();
        }

        private void ApplyToInstances()
        {
            float inv = invert ? 1f : 0f;
            foreach (var t in _targets)
            {
                if (t.mat == null) continue;
                t.mat.SetFloat(IdDissolveAmount, amount);
                t.mat.SetFloat(IdDissolveInvert, inv);
            }
        }

        private void RestoreAll()
        {
            foreach (var t in _targets)
            {
                if (t.mat == null) continue;
                t.mat.SetFloat(IdDissolveAmount, t.origAmount);
                t.mat.SetFloat(IdDissolveInvert, t.origInvert);
            }
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
                    if (IsDissolveTarget(m)) { hasTarget = true; count++; }
                if (!hasTarget)
                {
                    // 自分が過去にプレビューを当てた場合のみ解除（他コンポーネントの MPB を消さない）。
                    if (_previewed.Remove(r)) r.SetPropertyBlock(null);
                    continue;
                }

                // 注意: MPB は Renderer に 1 枚なので、同じ Renderer に他コンポーネント
                // （DollLiveDirector 等）も Edit プレビューを当てていると上書き合戦になる。
                // Edit 限定・表示のみの既知制約（Play 中はどちらも MPB 不使用で衝突しない）。
                _mpb.Clear();
                _mpb.SetFloat(IdDissolveAmount, amount);
                _mpb.SetFloat(IdDissolveInvert, invert ? 1f : 0f);
                r.SetPropertyBlock(_mpb);
                _previewed.Add(r);
            }

            // Edit 中は対象数を実測して表示に反映（Play 中は _targets が持つ）。
            if (!_instancesReady) _editTargetCount = count;
        }

        // Edit モード用の対象数（TargetMaterialCount のバッキング）。
        private int _editTargetCount;

        // ------------------------------------------------------------------
        //  スクリプト API（Timeline ミキサー / 外部スクリプトから）
        // ------------------------------------------------------------------
        // amount を設定する便宜メソッド。Timeline の DissolveMixerBehaviour から呼ばれる。
        public void SetAmount(float value)
        {
            amount = Mathf.Clamp01(value);
        }

        // amount と invert を一括設定する便宜メソッド。DissolveSwapController から
        // A/B 両キャラへ「進行度と向き」をまとめて渡すために使う。
        public void Set(float amount, bool invert)
        {
            this.amount = Mathf.Clamp01(amount);
            this.invert = invert;
        }

#if UNITY_EDITOR
        // 対象マテリアル数の読み取り専用ラベルを既定 Inspector の下に足すだけの最小エディタ。
        [UnityEditor.CustomEditor(typeof(DissolveController))]
        private class DissolveControllerEditor : UnityEditor.Editor
        {
            public override void OnInspectorGUI()
            {
                DrawDefaultInspector();

                var c = (DissolveController)target;
                int count = Application.isPlaying ? c.TargetMaterialCount : c._editTargetCount;
                UnityEditor.EditorGUILayout.Space();
                using (new UnityEditor.EditorGUI.DisabledScope(true))
                    UnityEditor.EditorGUILayout.IntField("対象マテリアル数", count);
            }
        }
#endif
    }
}
