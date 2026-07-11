// =============================================================================
//  UrpShadowSetupWindow.cs  (Editor only)
// -----------------------------------------------------------------------------
//  URP Asset の Shadows 設定を「プリセット」で一括適用する Editor ツール。
//  QualitySettings 全レベル + GraphicsSettings 既定から UniversalRenderPipelineAsset
//  を収集し、選択したアセットへ影距離・シャドウマップ解像度・カスケード・ソフト
//  シャドウ等をまとめて書き込む。EasyPBR / EasyToon のセルフシャドウ品質を狙った
//  プリセットを含む。
//  supportsSoftShadows 等の公開セッターが internal のため、書き込みは
//  SerializedObject 経由で m_* フィールドを直接操作する（Undo は自動記録）。
// =============================================================================
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Origuma.EasyShaderCore.Editor
{
    internal class UrpShadowSetupWindow : EditorWindow
    {
        // ------------------------------------------------------------------
        //  プリセット定義（テーブル駆動）
        //   値は UniversalRenderPipelineAsset のシリアライズフィールドに対応。
        //   将来のプリセット追加は Presets 配列へ 1 エントリ足すだけで済む。
        // ------------------------------------------------------------------
        private struct ShadowPreset
        {
            public string name;                                 // プリセット名
            public string note;                                 // 用途の説明（1 行）
            public bool mainLightShadows;                       // m_MainLightShadowsSupported
            public int mainShadowmapResolution;                 // m_MainLightShadowmapResolution (ShadowResolution)
            public float shadowDistance;                        // m_ShadowDistance
            public int shadowCascadeCount;                      // m_ShadowCascadeCount
            public float cascade2Split;                         // m_Cascade2Split
            public Vector2 cascade3Split;                       // m_Cascade3Split
            public Vector3 cascade4Split;                       // m_Cascade4Split
            public float cascadeBorder;                         // m_CascadeBorder
            public float shadowDepthBias;                       // m_ShadowDepthBias
            public float shadowNormalBias;                      // m_ShadowNormalBias
            public bool softShadows;                            // m_SoftShadowsSupported
            public SoftShadowQuality softShadowQuality;         // m_SoftShadowQuality
            public bool additionalLightShadows;                 // m_AdditionalLightShadowsSupported
            public int additionalShadowmapResolution;           // m_AdditionalLightsShadowmapResolution (ShadowResolution)
        }

        // カスケード分割は全プリセット共通の URP 既定値（キャラ重視のみ Cascade2Split /
        // CascadeBorder を変える）。定数化して各エントリを読みやすくする。
        private static readonly Vector2 kCascade3Split = new Vector2(0.1f, 0.3f);
        private static readonly Vector3 kCascade4Split = new Vector3(0.067f, 0.2f, 0.467f);

        private static readonly ShadowPreset[] Presets =
        {
            new ShadowPreset
            {
                name = "Unity 既定",
                note = "新規作成した URP Asset と同じ値へ戻す（変更のリセット用）。",
                mainLightShadows = true,
                mainShadowmapResolution = 2048,
                shadowDistance = 50f,
                shadowCascadeCount = 1,
                cascade2Split = 0.25f,
                cascade3Split = kCascade3Split,
                cascade4Split = kCascade4Split,
                cascadeBorder = 0.2f,
                shadowDepthBias = 1.0f,
                shadowNormalBias = 1.0f,
                softShadows = false,
                softShadowQuality = SoftShadowQuality.Medium,
                additionalLightShadows = false,
                additionalShadowmapResolution = 2048,
            },
            new ShadowPreset
            {
                name = "低（軽量）",
                note = "モバイル / 低スペック向け。解像度と影距離を絞って負荷を最小化する。",
                mainLightShadows = true,
                mainShadowmapResolution = 1024,
                shadowDistance = 20f,
                shadowCascadeCount = 1,
                cascade2Split = 0.25f,
                cascade3Split = kCascade3Split,
                cascade4Split = kCascade4Split,
                cascadeBorder = 0.2f,
                shadowDepthBias = 1.0f,
                shadowNormalBias = 1.0f,
                softShadows = false,
                softShadowQuality = SoftShadowQuality.Medium,
                additionalLightShadows = false,
                additionalShadowmapResolution = 2048,
            },
            new ShadowPreset
            {
                name = "中（標準）",
                note = "一般的な PC 向けのバランス設定。ソフトシャドウ有効・2 カスケード。",
                mainLightShadows = true,
                mainShadowmapResolution = 2048,
                shadowDistance = 35f,
                shadowCascadeCount = 2,
                cascade2Split = 0.25f,
                cascade3Split = kCascade3Split,
                cascade4Split = kCascade4Split,
                cascadeBorder = 0.2f,
                shadowDepthBias = 1.0f,
                shadowNormalBias = 1.0f,
                softShadows = true,
                softShadowQuality = SoftShadowQuality.Medium,
                additionalLightShadows = false,
                additionalShadowmapResolution = 2048,
            },
            new ShadowPreset
            {
                name = "高",
                note = "高品質 PC 向け。4 カスケード・高解像度・追加ライト影も有効。",
                mainLightShadows = true,
                mainShadowmapResolution = 4096,
                shadowDistance = 50f,
                shadowCascadeCount = 4,
                cascade2Split = 0.25f,
                cascade3Split = kCascade3Split,
                cascade4Split = kCascade4Split,
                cascadeBorder = 0.2f,
                shadowDepthBias = 1.0f,
                shadowNormalBias = 1.0f,
                softShadows = true,
                softShadowQuality = SoftShadowQuality.High,
                additionalLightShadows = true,
                additionalShadowmapResolution = 2048,
            },
            new ShadowPreset
            {
                name = "キャラ重視（3Dライブ）",
                note = "影距離を抑えてシャドウマップのテクセル密度をキャラクターに集中させる。" +
                       "EasyPBR / EasyToon のセルフシャドウ品質を優先しつつ、引きのカメラでも" +
                       "影が出る距離（40m）を確保する（40m 以遠の影は落ちない点に注意）。",
                mainLightShadows = true,
                mainShadowmapResolution = 4096,
                shadowDistance = 40f,
                shadowCascadeCount = 2,
                cascade2Split = 0.5f,
                cascade3Split = kCascade3Split,
                cascade4Split = kCascade4Split,
                cascadeBorder = 0.3f,
                shadowDepthBias = 1.0f,
                shadowNormalBias = 1.0f,
                softShadows = true,
                softShadowQuality = SoftShadowQuality.High,
                additionalLightShadows = true,
                additionalShadowmapResolution = 4096,
            },
        };

        // ------------------------------------------------------------------
        //  プレビュー / 適用で共有するフィールド記述子（プリセット定義とは別に、
        //  各 m_* フィールドの読み書きと表示整形をまとめる）。
        // ------------------------------------------------------------------
        private enum FieldKind { Bool, ResolutionInt, Int, Float, SoftQuality, Vec2, Vec3 }

        private struct FieldDesc
        {
            public string label;    // 表示ラベル
            public string prop;      // SerializedProperty 名（m_*）
            public FieldKind kind;

            public FieldDesc(string label, string prop, FieldKind kind)
            {
                this.label = label;
                this.prop = prop;
                this.kind = kind;
            }
        }

        private static readonly FieldDesc[] Fields =
        {
            new FieldDesc("メインライト影",   "m_MainLightShadowsSupported",           FieldKind.Bool),
            new FieldDesc("メイン影解像度",   "m_MainLightShadowmapResolution",        FieldKind.ResolutionInt),
            new FieldDesc("影距離",           "m_ShadowDistance",                      FieldKind.Float),
            new FieldDesc("カスケード数",     "m_ShadowCascadeCount",                  FieldKind.Int),
            new FieldDesc("Cascade2 Split",   "m_Cascade2Split",                       FieldKind.Float),
            new FieldDesc("Cascade3 Split",   "m_Cascade3Split",                       FieldKind.Vec2),
            new FieldDesc("Cascade4 Split",   "m_Cascade4Split",                       FieldKind.Vec3),
            new FieldDesc("カスケード境界",   "m_CascadeBorder",                       FieldKind.Float),
            new FieldDesc("深度バイアス",     "m_ShadowDepthBias",                     FieldKind.Float),
            new FieldDesc("法線バイアス",     "m_ShadowNormalBias",                    FieldKind.Float),
            new FieldDesc("ソフトシャドウ",   "m_SoftShadowsSupported",                FieldKind.Bool),
            new FieldDesc("ソフト影品質",     "m_SoftShadowQuality",                   FieldKind.SoftQuality),
            new FieldDesc("追加ライト影",     "m_AdditionalLightShadowsSupported",     FieldKind.Bool),
            new FieldDesc("追加影解像度",     "m_AdditionalLightsShadowmapResolution", FieldKind.ResolutionInt),
        };

        // ------------------------------------------------------------------
        //  対象アセット 1 件ぶんの情報
        // ------------------------------------------------------------------
        private class AssetEntry
        {
            public UniversalRenderPipelineAsset asset;
            public readonly List<string> usages = new List<string>();
            public bool isActiveLevel;   // アクティブ品質レベルのアセットか（初期選択の判定）
        }

        [SerializeField] private int _presetIndex;
        private Vector2 _scroll;

        // アセット参照でチェック状態を保持（再収集しても選択を失わない）。
        private readonly HashSet<UniversalRenderPipelineAsset> _selected = new HashSet<UniversalRenderPipelineAsset>();
        private readonly HashSet<UniversalRenderPipelineAsset> _known = new HashSet<UniversalRenderPipelineAsset>();

        [MenuItem("Window/Origuma/URP Shadow Setup")]
        public static void ShowWindow()
        {
            var window = GetWindow<UrpShadowSetupWindow>("URP Shadow Setup");
            window.minSize = new Vector2(480, 520);
        }

        // ------------------------------------------------------------------
        //  対象 URP Asset の収集（QualitySettings 全レベル + Graphics 既定）
        //   表示中に外部で変わり得るので毎描画で軽く再収集する。
        // ------------------------------------------------------------------
        private List<AssetEntry> CollectAssets()
        {
            var map = new Dictionary<UniversalRenderPipelineAsset, AssetEntry>();
            var order = new List<AssetEntry>();

            AssetEntry GetOrCreate(UniversalRenderPipelineAsset a)
            {
                if (!map.TryGetValue(a, out var e))
                {
                    e = new AssetEntry { asset = a };
                    map.Add(a, e);
                    order.Add(e);
                }
                return e;
            }

            int activeLevel = QualitySettings.GetQualityLevel();
            var names = QualitySettings.names;
            for (int i = 0; i < names.Length; i++)
            {
                if (!(QualitySettings.GetRenderPipelineAssetAt(i) is UniversalRenderPipelineAsset a))
                    continue;

                var e = GetOrCreate(a);
                if (i == activeLevel)
                {
                    e.usages.Add($"{names[i]} ★");
                    e.isActiveLevel = true;
                }
                else
                {
                    e.usages.Add(names[i]);
                }
            }

            if (GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset def)
                GetOrCreate(def).usages.Add("Graphics 既定");

            // 新規に見つかったアセットの初期選択（アクティブ品質レベルのみ ON）。
            foreach (var e in order)
            {
                if (_known.Contains(e.asset)) continue;
                _known.Add(e.asset);
                if (e.isActiveLevel) _selected.Add(e.asset);
            }

            return order;
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("URP Shadow Setup", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "URP Asset の Shadows 設定をプリセットで一括書き換えします。QualitySettings の各" +
                "品質レベルと Graphics 既定から URP Asset を収集し、選択したものへ適用します（Undo 可）。",
                MessageType.Info);

            var entries = CollectAssets();
            if (entries.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "URP Asset が見つかりませんでした。Project Settings > Quality もしくは Graphics に " +
                    "Universal Render Pipeline Asset を割り当ててください。",
                    MessageType.Warning);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawPresetSelector();
            EditorGUILayout.Space(8);
            DrawAssetList(entries);
            EditorGUILayout.Space(8);
            DrawPreview(entries);

            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(6);
            int selectedCount = 0;
            foreach (var e in entries)
                if (_selected.Contains(e.asset)) selectedCount++;

            using (new EditorGUI.DisabledScope(selectedCount == 0))
            {
                if (GUILayout.Button($"選択したアセットに適用（{selectedCount}）", GUILayout.Height(30)))
                    ApplyToSelected(entries);
            }
        }

        // ------------------------------------------------------------------
        //  UI: プリセット選択
        // ------------------------------------------------------------------
        private void DrawPresetSelector()
        {
            EditorGUILayout.LabelField("プリセット", EditorStyles.boldLabel);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                // ラジオ的挙動: 未選択のトグルを ON にしたものだけを選択に切り替える
                // （選択中を再クリックしても選択は外れない）。
                for (int i = 0; i < Presets.Length; i++)
                {
                    if (EditorGUILayout.ToggleLeft(Presets[i].name, _presetIndex == i) && _presetIndex != i)
                        _presetIndex = i;
                }
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField(Presets[_presetIndex].note, EditorStyles.wordWrappedMiniLabel);
            }
        }

        // ------------------------------------------------------------------
        //  UI: 対象アセット一覧（チェックボックス + ラベル + Ping ボタン）
        // ------------------------------------------------------------------
        private void DrawAssetList(List<AssetEntry> entries)
        {
            EditorGUILayout.LabelField("対象アセット", EditorStyles.boldLabel);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                foreach (var e in entries)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        bool on = _selected.Contains(e.asset);
                        bool newOn = EditorGUILayout.ToggleLeft(
                            $"{e.asset.name}  ({string.Join(", ", e.usages)})", on);
                        if (newOn != on)
                        {
                            if (newOn) _selected.Add(e.asset);
                            else _selected.Remove(e.asset);
                        }

                        if (GUILayout.Button("選択", GUILayout.Width(50)))
                            EditorGUIUtility.PingObject(e.asset);
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        //  UI: プレビュー表（現在値 → 適用後。変化する行を強調）
        // ------------------------------------------------------------------
        private void DrawPreview(List<AssetEntry> entries)
        {
            EditorGUILayout.LabelField("プレビュー", EditorStyles.boldLabel);

            // 現在値の参照元は「選択中アセットの 1 つ目」。
            AssetEntry first = null;
            foreach (var e in entries)
                if (_selected.Contains(e.asset)) { first = e; break; }

            var preset = Presets[_presetIndex];

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("項目", EditorStyles.miniBoldLabel, GUILayout.Width(140));
                    EditorGUILayout.LabelField("現在値", EditorStyles.miniBoldLabel, GUILayout.Width(120));
                    EditorGUILayout.LabelField("→", EditorStyles.miniBoldLabel, GUILayout.Width(16));
                    EditorGUILayout.LabelField("適用後", EditorStyles.miniBoldLabel, GUILayout.Width(120));
                }

                if (first == null)
                {
                    EditorGUILayout.LabelField("（アセットを選択すると現在値を表示します）",
                        EditorStyles.wordWrappedMiniLabel);
                    return;
                }

                var so = new SerializedObject(first.asset);
                var changedStyle = new GUIStyle(EditorStyles.label) { fontStyle = FontStyle.Bold };

                foreach (var f in Fields)
                {
                    string newVal = FormatPresetValue(f, preset);
                    var prop = so.FindProperty(f.prop);
                    string curVal = prop != null ? FormatCurrentValue(f, prop) : "-";
                    bool changed = curVal != newVal;

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(f.label, GUILayout.Width(140));
                        EditorGUILayout.LabelField(curVal, GUILayout.Width(120));
                        EditorGUILayout.LabelField("→", GUILayout.Width(16));
                        EditorGUILayout.LabelField(newVal, changed ? changedStyle : EditorStyles.label,
                            GUILayout.Width(120));
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        //  適用処理（SerializedObject 経由で m_* を直接書き込む）
        // ------------------------------------------------------------------
        private void ApplyToSelected(List<AssetEntry> entries)
        {
            var preset = Presets[_presetIndex];
            int applied = 0;

            foreach (var e in entries)
            {
                if (!_selected.Contains(e.asset)) continue;
                ApplyPreset(e.asset, preset);
                applied++;
            }

            Debug.Log($"[EasyShaderCore] URP Shadow プリセット「{preset.name}」を {applied} 個の URP Asset に適用しました。");
        }

        private void ApplyPreset(UniversalRenderPipelineAsset asset, ShadowPreset preset)
        {
            var so = new SerializedObject(asset);
            so.Update();

            var missing = new List<string>();

            foreach (var f in Fields)
            {
                var prop = so.FindProperty(f.prop);
                if (prop == null) { missing.Add(f.prop); continue; }
                WritePresetValue(f, prop, preset);
            }

            // m_AnyShadowsSupported は main || additional に保つ連動フィールド。
            // SerializedObject で直接書くため整合を自前で維持する。
            var anyProp = so.FindProperty("m_AnyShadowsSupported");
            if (anyProp != null)
                anyProp.boolValue = preset.mainLightShadows || preset.additionalLightShadows;
            else
                missing.Add("m_AnyShadowsSupported");

            // ApplyModifiedProperties が Undo を記録し、変更を Dirty にする。
            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssetIfDirty(asset);

            if (missing.Count > 0)
                Debug.LogWarning(
                    $"[EasyShaderCore] {asset.name}: 見つからなかったフィールドをスキップしました" +
                    $"（URP バージョン差の可能性）: {string.Join(", ", missing)}");
        }

        // ------------------------------------------------------------------
        //  フィールド値の読み書き / 整形
        // ------------------------------------------------------------------
        private static void WritePresetValue(FieldDesc f, SerializedProperty prop, ShadowPreset p)
        {
            switch (f.kind)
            {
                case FieldKind.Bool:
                    prop.boolValue = ReadPresetBool(f, p);
                    break;
                case FieldKind.ResolutionInt:
                    prop.intValue = ReadPresetInt(f, p);   // ShadowResolution enum の backing 値 = 解像度
                    break;
                case FieldKind.Int:
                    prop.intValue = ReadPresetInt(f, p);
                    break;
                case FieldKind.Float:
                    prop.floatValue = ReadPresetFloat(f, p);
                    break;
                case FieldKind.SoftQuality:
                    prop.intValue = (int)p.softShadowQuality;
                    break;
                case FieldKind.Vec2:
                    prop.vector2Value = p.cascade3Split;
                    break;
                case FieldKind.Vec3:
                    prop.vector3Value = p.cascade4Split;
                    break;
            }
        }

        private static string FormatPresetValue(FieldDesc f, ShadowPreset p)
        {
            switch (f.kind)
            {
                case FieldKind.Bool:          return ReadPresetBool(f, p) ? "ON" : "OFF";
                case FieldKind.ResolutionInt: return ReadPresetInt(f, p).ToString();
                case FieldKind.Int:           return ReadPresetInt(f, p).ToString();
                case FieldKind.Float:         return ReadPresetFloat(f, p).ToString("0.###");
                case FieldKind.SoftQuality:   return p.softShadowQuality.ToString();
                case FieldKind.Vec2:          return FormatVec2(p.cascade3Split);
                case FieldKind.Vec3:          return FormatVec3(p.cascade4Split);
            }
            return "-";
        }

        private static string FormatCurrentValue(FieldDesc f, SerializedProperty prop)
        {
            switch (f.kind)
            {
                case FieldKind.Bool:          return prop.boolValue ? "ON" : "OFF";
                case FieldKind.ResolutionInt: return prop.intValue.ToString();
                case FieldKind.Int:           return prop.intValue.ToString();
                case FieldKind.Float:         return prop.floatValue.ToString("0.###");
                case FieldKind.SoftQuality:   return ((SoftShadowQuality)prop.intValue).ToString();
                case FieldKind.Vec2:          return FormatVec2(prop.vector2Value);
                case FieldKind.Vec3:          return FormatVec3(prop.vector3Value);
            }
            return "-";
        }

        // プリセットの各値をフィールド名から引く（テーブルと構造体の橋渡し）。
        private static bool ReadPresetBool(FieldDesc f, ShadowPreset p)
        {
            switch (f.prop)
            {
                case "m_MainLightShadowsSupported":       return p.mainLightShadows;
                case "m_SoftShadowsSupported":            return p.softShadows;
                case "m_AdditionalLightShadowsSupported": return p.additionalLightShadows;
            }
            return false;
        }

        private static int ReadPresetInt(FieldDesc f, ShadowPreset p)
        {
            switch (f.prop)
            {
                case "m_MainLightShadowmapResolution":        return p.mainShadowmapResolution;
                case "m_ShadowCascadeCount":                  return p.shadowCascadeCount;
                case "m_AdditionalLightsShadowmapResolution": return p.additionalShadowmapResolution;
            }
            return 0;
        }

        private static float ReadPresetFloat(FieldDesc f, ShadowPreset p)
        {
            switch (f.prop)
            {
                case "m_ShadowDistance":   return p.shadowDistance;
                case "m_Cascade2Split":    return p.cascade2Split;
                case "m_CascadeBorder":    return p.cascadeBorder;
                case "m_ShadowDepthBias":  return p.shadowDepthBias;
                case "m_ShadowNormalBias": return p.shadowNormalBias;
            }
            return 0f;
        }

        private static string FormatVec2(Vector2 v)
        {
            var sb = new StringBuilder();
            sb.Append('(').Append(v.x.ToString("0.###")).Append(", ").Append(v.y.ToString("0.###")).Append(')');
            return sb.ToString();
        }

        private static string FormatVec3(Vector3 v)
        {
            var sb = new StringBuilder();
            sb.Append('(').Append(v.x.ToString("0.###")).Append(", ")
              .Append(v.y.ToString("0.###")).Append(", ")
              .Append(v.z.ToString("0.###")).Append(')');
            return sb.ToString();
        }
    }
}
