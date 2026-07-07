// =============================================================================
//  FeatureSetup.cs
// -----------------------------------------------------------------------------
//  ScriptableRendererFeature のセットアップ支援ユーティリティ（EasyPBR / EasyToon
//  共通基盤）。IdolSetupWindow（旧 EasyToon）のロジックを汎用化して移管したもの。
//   - アクティブな URP Asset（GraphicsSettings 既定 + QualitySettings 全レベル）
//     からの Renderer Data 自動収集
//   - Feature の検索 / 追加（サブアセット化 + m_RendererFeatureMap 同期 + Undo）/ 削除
//   - Render Graph Compatibility Mode の判定
//   - ShaderGUI 用の「Feature 未追加ガード」描画ヘルパ
// =============================================================================
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Origuma.EasyShaderCore.Editor
{
    public static class FeatureSetup
    {
        // ------------------------------------------------------------------
        //  アクティブ URP Asset の収集（既定 + QualitySettings 全レベル）
        // ------------------------------------------------------------------
        public static List<ScriptableRendererData> CollectActiveRendererDatas()
        {
            var result = new List<ScriptableRendererData>();
            var assets = new List<UniversalRenderPipelineAsset>();

            if (GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset defaultAsset)
                assets.Add(defaultAsset);

            for (int i = 0; i < QualitySettings.count; i++)
                if (QualitySettings.GetRenderPipelineAssetAt(i) is UniversalRenderPipelineAsset qualityAsset
                    && !assets.Contains(qualityAsset))
                    assets.Add(qualityAsset);

            foreach (var asset in assets)
                foreach (var data in asset.rendererDataList)
                    if (data != null && !result.Contains(data))
                        result.Add(data);

            return result;
        }

        // アクティブなパイプラインのいずれかの Renderer Data に Feature が
        // 追加済みかを返す（ShaderGUI の未追加検知用）。
        public static bool IsFeatureAddedToActivePipeline<T>() where T : ScriptableRendererFeature
            => IsFeatureAddedToActivePipeline(typeof(T));

        public static bool IsFeatureAddedToActivePipeline(Type featureType)
        {
            foreach (var data in CollectActiveRendererDatas())
                if (FindFeature(data, featureType) != null)
                    return true;
            return false;
        }

        // Render Graph Compatibility Mode が有効か（RendererFeature が RG 専用の場合の警告用）。
        public static bool IsRenderGraphCompatibilityMode()
        {
            return GraphicsSettings.TryGetRenderPipelineSettings<RenderGraphSettings>(out var rgSettings)
                   && rgSettings.enableRenderCompatibilityMode;
        }

        // ------------------------------------------------------------------
        //  Feature の検索 / 追加 / 削除
        //  追加はサブアセット化 + m_RendererFeatureMap（localId）同期 + Undo 対応。
        // ------------------------------------------------------------------
        public static T FindFeature<T>(ScriptableRendererData data) where T : ScriptableRendererFeature
            => (T)FindFeature(data, typeof(T));

        public static ScriptableRendererFeature FindFeature(ScriptableRendererData data, Type featureType)
        {
            foreach (var f in data.rendererFeatures)
                if (f != null && featureType.IsInstanceOfType(f))
                    return f;
            return null;
        }

        public static void AddFeature<T>(ScriptableRendererData data, string featureName)
            where T : ScriptableRendererFeature
            => AddFeature(data, typeof(T), featureName);

        public static void AddFeature(ScriptableRendererData data, Type featureType, string featureName)
        {
            var feature = (ScriptableRendererFeature)ScriptableObject.CreateInstance(featureType);
            feature.name = featureName;

            Undo.RegisterCreatedObjectUndo(feature, $"Add {featureName} Feature");
            AssetDatabase.AddObjectToAsset(feature, data);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);

            var so = new SerializedObject(data);
            so.Update();
            var listProp = so.FindProperty("m_RendererFeatures");
            var mapProp  = so.FindProperty("m_RendererFeatureMap");

            int idx = listProp.arraySize;
            listProp.arraySize = idx + 1;
            listProp.GetArrayElementAtIndex(idx).objectReferenceValue = feature;
            mapProp.arraySize = idx + 1;
            mapProp.GetArrayElementAtIndex(idx).longValue = localId;

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[EasyShaderCore] {featureName} Feature を追加しました: {AssetDatabase.GetAssetPath(data)}");
        }

        public static void RemoveFeature<T>(ScriptableRendererData data, string featureName)
            where T : ScriptableRendererFeature
            => RemoveFeature(data, typeof(T), featureName);

        public static void RemoveFeature(ScriptableRendererData data, Type featureType, string featureName)
        {
            var so = new SerializedObject(data);
            so.Update();
            var listProp = so.FindProperty("m_RendererFeatures");
            var mapProp  = so.FindProperty("m_RendererFeatureMap");

            var toDestroy = new List<UnityEngine.Object>();
            for (int i = listProp.arraySize - 1; i >= 0; i--)
            {
                var obj = listProp.GetArrayElementAtIndex(i).objectReferenceValue;
                if (obj != null && featureType.IsInstanceOfType(obj))
                {
                    listProp.GetArrayElementAtIndex(i).objectReferenceValue = null;
                    listProp.DeleteArrayElementAtIndex(i);
                    if (i < mapProp.arraySize) mapProp.DeleteArrayElementAtIndex(i);
                    toDestroy.Add(obj);
                }
            }

            so.ApplyModifiedProperties();
            foreach (var feat in toDestroy)
                Undo.DestroyObjectImmediate(feat);

            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[EasyShaderCore] {featureName} Feature を削除しました: {AssetDatabase.GetAssetPath(data)}");
        }

        // ------------------------------------------------------------------
        //  ShaderGUI 用ガード: Feature 未追加ならヘルプボックス（警告）+
        //  Setup Window を開くボタンを描く。追加済みなら情報ヘルプボックスのみ。
        // ------------------------------------------------------------------
        public static void DrawFeatureGuard<T>(string addedMessage, string missingMessage,
                                               string buttonLabel, Action openWindow)
            where T : ScriptableRendererFeature
        {
            bool added = IsFeatureAddedToActivePipeline<T>();
            EditorGUILayout.HelpBox(added ? addedMessage : missingMessage,
                added ? MessageType.Info : MessageType.Warning);
            if (GUILayout.Button(buttonLabel))
                openWindow?.Invoke();
        }
    }
}
