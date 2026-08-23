// =============================================================================
//  DissolveTimeline.cs
// -----------------------------------------------------------------------------
//  DissolveController を Timeline から駆動する専用トラック一式（3 クラス）。
//  「シーンに管理オブジェクトを並べず、タイムライン側が各キャラの
//  DissolveController への参照（トラックバインディング）を持つ」構成を実現する。
//
//  - DissolveTrack          : キャラの DissolveController にバインドするトラック
//  - DissolveClip           : クリップ正規化時間 0..1 → amount のカーブを持つ
//  - DissolveMixerBehaviour : 全クリップの重み付き合成値を Controller へ書き込む
//
//  依存: com.unity.timeline への hard 依存は禁止。asmdef の versionDefines に
//  ORIGUMA_TIMELINE を追加し、Timeline パッケージ不在ではコード全体が #if で
//  消える（Controller とマテリアル制御だけでも動く）。
// =============================================================================
#if ORIGUMA_TIMELINE
using System;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Origuma.EasyShaderCore
{
    // -------------------------------------------------------------------------
    //  トラック: DissolveController にバインドし、DissolveClip を並べる
    // -------------------------------------------------------------------------
    [TrackColor(0.35f, 0.55f, 0.95f)]
    [TrackClipType(typeof(DissolveClip))]
    [TrackBindingType(typeof(DissolveController))]
    public class DissolveTrack : TrackAsset
    {
        // トラックのランタイム実体（クリップのミキサー）を生成する。
        public override Playable CreateTrackMixer(PlayableGraph graph, GameObject go, int inputCount)
        {
            return ScriptPlayable<DissolveMixerBehaviour>.Create(graph, inputCount);
        }
    }

    // -------------------------------------------------------------------------
    //  クリップのランタイム値: 正規化時間 0..1 → amount のカーブ
    // -------------------------------------------------------------------------
    [Serializable]
    public class DissolveClipBehaviour : PlayableBehaviour
    {
        [Tooltip("クリップ正規化時間 0..1 を amount(0..1) へ写すカーブ。")]
        public AnimationCurve curve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
    }

    // -------------------------------------------------------------------------
    //  クリップ（シリアライズ資産）
    // -------------------------------------------------------------------------
    [Serializable]
    public class DissolveClip : PlayableAsset, ITimelineClipAsset
    {
        [NotKeyable] // Timeline のアニメ対象化を防ぐ（カーブはこのフィールドで持つ）。
        public DissolveClipBehaviour template = new DissolveClipBehaviour();

        // Blending でクリップ間ブレンド、Extrapolation で「消え終わり」を Hold できる。
        public ClipCaps clipCaps => ClipCaps.Blending | ClipCaps.Extrapolation;

        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            // template を使うとシリアライズ値（カーブ）が複製される。
            return ScriptPlayable<DissolveClipBehaviour>.Create(graph, template);
        }
    }

    // -------------------------------------------------------------------------
    //  ミキサー: 全クリップの重み付き合成を DissolveController へ書き込む
    // -------------------------------------------------------------------------
    public class DissolveMixerBehaviour : PlayableBehaviour
    {
        // 毎フレーム（入力評価後）に呼ばれる。
        public override void ProcessFrame(Playable playable, FrameData info, object playerData)
        {
            var controller = playerData as DissolveController;
            if (controller == null) return;

            int inputCount = playable.GetInputCount();
            float blended = 0f;      // 各クリップ値の重み付き和
            float totalWeight = 0f;  // 書き込み可否判定用の重み合計

            for (int i = 0; i < inputCount; i++)
            {
                float w = playable.GetInputWeight(i);
                if (w <= 0f) continue;

                var input = (ScriptPlayable<DissolveClipBehaviour>)playable.GetInput(i);
                var behaviour = input.GetBehaviour();
                if (behaviour == null || behaviour.curve == null) continue;

                // クリップ内の正規化時間 0..1（Extrapolation 時は 0..1 外へ出ないよう Clamp）。
                double duration = input.GetDuration();
                double t = duration > 0.0 ? input.GetTime() / duration : 0.0;
                float value = behaviour.curve.Evaluate(Mathf.Clamp01((float)t));

                // 重み付き和（ブレンド区間は 2 クリップの重みが和 1 になり自然に補間される）。
                blended += value * w;
                totalWeight += w;
            }

            // totalWeight がほぼ 0 のフレームは書き込まない。クリップ外・Timeline 停止時に
            // シーン側（Controller.amount）の値を勝手に 0 へ戻さないため（Extrapolation Hold は
            // この「書き込まない」ことで直前の amount がマテリアルに保持され成立する）。
            if (totalWeight <= 0.0001f) return;

            controller.SetAmount(blended);
        }
    }
}
#endif
