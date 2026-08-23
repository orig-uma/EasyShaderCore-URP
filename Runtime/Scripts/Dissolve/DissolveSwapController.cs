// =============================================================================
//  DissolveSwapController.cs
// -----------------------------------------------------------------------------
//  2 キャラの「入れ替わり」演出。片方（A）を消しながら、もう片方（B）を反転
//  Dissolve で出現させる。`amount` を 0→1 と動かすだけで A 消失／B 出現が同期する。
//
//  存在意義:
//  - 本コンポーネントは **マテリアルには一切触れない**。制御はすべて配下の
//    DissolveController 経由（`characterA.Set(...)` / `characterB.Set(...)`）で行う。
//    「マテリアルへ直接書くのは DissolveController だけ」という一本化方針を守るため、
//    Swap は Controller に値を渡すだけの薄い上位レイヤに徹する（Play/Edit の反映・
//    SRP Batcher 維持・Sampler 連携はすべて Controller 側の責務）。
//
//  実行順:
//  - `[DefaultExecutionOrder(-10)]` を付け、Controller の LateUpdate より **先に**
//    amount を書き込む。両者とも LateUpdate で動くが、Unity は実行順が小さい
//    コンポーネントを先に呼ぶため、Swap が値を確定 → Controller がその値を反映、
//    の順序を保証できる（付けないと Controller が 1 フレーム古い値を読むことがある）。
// =============================================================================
using System.Collections;
using UnityEngine;

namespace Origuma.EasyShaderCore
{
    [ExecuteAlways]
    [DefaultExecutionOrder(-10)] // Controller(LateUpdate) より先に amount を書く（理由は冒頭コメント）
    [DisallowMultipleComponent]
    [AddComponentMenu("Origuma/EasyShaderCore/Dissolve Swap Controller")]
    public class DissolveSwapController : MonoBehaviour
    {
        // ------------------------------------------------------------------
        //  Inspector
        // ------------------------------------------------------------------
        [Tooltip("消えていく側のキャラの DissolveController（invert=false で駆動）。")]
        public DissolveController characterA;

        [Tooltip("現れる側のキャラの DissolveController（invert=true で駆動）。" +
                 "B 側マテリアルも Enable Dissolve を ON にしておくこと。")]
        public DissolveController characterB;

        [Tooltip("入れ替わりの進行度 0..1。0=A 表示/B 非表示 → 1=A 消失/B 出現。" +
                 "Animation Track から直接キーを打てるよう public フィールドにしている。")]
        [Range(0f, 1f)]
        public float amount = 0f;

        [Tooltip("自動再生（PlayToB / PlayToA）にかかる秒数。")]
        [Range(0.1f, 10f)]
        public float duration = 2f;

        // 自動再生コルーチン管理。
        private Coroutine _routine;

        // ------------------------------------------------------------------
        //  反映（値を渡すだけ。マテリアルには触れない）
        // ------------------------------------------------------------------
        private void LateUpdate()
        {
            // A は通常向き（消えていく）、B は反転（現れる）。null は許容。
            if (characterA != null) characterA.Set(amount, false);
            if (characterB != null) characterB.Set(amount, true);
        }

        // ------------------------------------------------------------------
        //  自動再生（Play モード限定）
        // ------------------------------------------------------------------
        [ContextMenu("▶ A→B (0→1)")]
        public void PlayToB() => Play(1f);

        [ContextMenu("◀ B→A (1→0)")]
        public void PlayToA() => Play(0f);

        // target（0 or 任意値）へ duration 秒かけて Lerp する。
        public void Play(float target)
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[DissolveSwapController] 自動再生は Play モード中のみ動作します。", this);
                return;
            }

            if (_routine != null) StopCoroutine(_routine);
            _routine = StartCoroutine(PlayRoutine(Mathf.Clamp01(target)));
        }

        private IEnumerator PlayRoutine(float target)
        {
            float start = amount;
            float t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                amount = Mathf.Lerp(start, target, t / duration);
                yield return null;
            }
            amount = target; // 端数を切って確実に到達
            _routine = null;
        }

        private void OnDisable()
        {
            if (_routine != null)
            {
                StopCoroutine(_routine);
                _routine = null;
            }
        }
    }
}
