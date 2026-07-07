// =============================================================================
//  FeatureSetupWindowBase.cs
// -----------------------------------------------------------------------------
//  RendererFeature セットアップ用 EditorWindow の汎用基底（EasyPBR / EasyToon 共通）。
//  サブクラスは「ヘッダ・説明・Feature エントリ配列（Type / 表示名 / 説明）」を
//  宣言するだけでよく、描画（アクティブ URP Asset からの Renderer Data 自動収集
//  リスト・状態表示・追加/削除/有効切替・手動 ObjectField・Render Graph
//  Compatibility Mode 警告）はこの基底が担う。ロジックは FeatureSetup に委譲。
// =============================================================================
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Origuma.EasyShaderCore.Editor
{
    public abstract class FeatureSetupWindowBase : EditorWindow
    {
        // Feature 1 種ぶんの宣言（Type は ScriptableRendererFeature 派生であること）。
        public struct FeatureEntry
        {
            public Type featureType;   // 追加する RendererFeature の型
            public string label;       // 表示名（サブアセット名・ログにも使用）
            public string note;        // ツールチップ説明

            public FeatureEntry(Type featureType, string label, string note)
            {
                this.featureType = featureType;
                this.label = label;
                this.note = note;
            }
        }

        // --- サブクラスが宣言するもの -----------------------------------------
        protected abstract string HeaderLabel { get; }       // 例: "Idol RendererFeature セットアップ"
        protected abstract string Description { get; }       // 説明ヘルプボックスの本文
        protected abstract FeatureEntry[] Entries { get; }   // 管理する Feature 一覧

        private ScriptableRendererData _manualRendererData;
        private Vector2 _scroll;

        // ------------------------------------------------------------------
        //  描画（旧 IdolSetupWindow の UI をそのまま基底化）
        // ------------------------------------------------------------------
        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(HeaderLabel, EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(Description, MessageType.Info);

            // Render Graph Compatibility Mode の警告（Feature は Render Graph 専用）。
            if (FeatureSetup.IsRenderGraphCompatibilityMode())
            {
                EditorGUILayout.HelpBox(
                    "Render Graph Compatibility Mode が有効です。RendererFeature は " +
                    "Render Graph 専用のため動作しません。Project Settings > Graphics > " +
                    "Render Graph で Compatibility Mode を無効化してください。",
                    MessageType.Warning);
            }

            EditorGUILayout.Space(4);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            // --- アクティブな URP Asset から自動収集 ---
            var datas = FeatureSetup.CollectActiveRendererDatas();
            if (datas.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "アクティブな URP Asset から Renderer Data を検出できませんでした。" +
                    "下の手動割り当てを使用してください。",
                    MessageType.Warning);
            }
            else
            {
                EditorGUILayout.LabelField("アクティブな Renderer Data", EditorStyles.boldLabel);
                foreach (var data in datas)
                    DrawRendererDataEntry(data);
            }

            // --- 手動割り当て（自動収集で拾えない場合の保険） ---
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("手動割り当て", EditorStyles.boldLabel);
            _manualRendererData = (ScriptableRendererData)EditorGUILayout.ObjectField(
                "Universal Renderer Data", _manualRendererData, typeof(ScriptableRendererData), false);
            if (_manualRendererData != null && !datas.Contains(_manualRendererData))
                DrawRendererDataEntry(_manualRendererData);

            EditorGUILayout.EndScrollView();
        }

        // Renderer Data 1 件ぶんの UI（全エントリの状態と追加/削除/有効切替）。
        private void DrawRendererDataEntry(ScriptableRendererData data)
        {
            if (data == null) return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.ObjectField(GUIContent.none, data, typeof(ScriptableRendererData), false);

                foreach (var entry in Entries)
                    DrawFeatureRow(data, entry);
            }
        }

        private void DrawFeatureRow(ScriptableRendererData data, FeatureEntry entry)
        {
            var existing = FeatureSetup.FindFeature(data, entry.featureType);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(new GUIContent(entry.label, entry.note), GUILayout.Width(150));
                if (existing == null)
                {
                    EditorGUILayout.LabelField("未追加", GUILayout.Width(110));
                    if (GUILayout.Button("追加", GUILayout.Width(70)))
                        FeatureSetup.AddFeature(data, entry.featureType, entry.label);
                }
                else
                {
                    EditorGUILayout.LabelField(existing.isActive ? "追加済み（有効）" : "追加済み（無効）",
                        GUILayout.Width(110));
                    EditorGUI.BeginChangeCheck();
                    var active = EditorGUILayout.ToggleLeft("有効", existing.isActive, GUILayout.Width(50));
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(existing, $"Toggle {entry.label} Feature");
                        existing.SetActive(active);
                        EditorUtility.SetDirty(data);
                        AssetDatabase.SaveAssets();
                    }
                    if (GUILayout.Button("削除", GUILayout.Width(70)))
                        FeatureSetup.RemoveFeature(data, entry.featureType, entry.label);
                }
            }
        }
    }
}
