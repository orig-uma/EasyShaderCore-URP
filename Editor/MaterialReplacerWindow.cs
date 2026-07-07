// =============================================================================
//  MaterialReplacerWindow.cs  (Editor only)
// -----------------------------------------------------------------------------
//  マテリアル一括置換ユーティリティ。対象オブジェクト配下の全 Renderer の
//  マテリアルを、指定フォルダ内の「同名マテリアル」へ差し替える。
//  元モデルのマテリアル一式を EasyPBR / EasyToon 版（同名で用意）へ移行する導線を想定。
//  Undo 対応・sharedMaterials 経由（アセットは変更しない）。
// =============================================================================
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Origuma.EasyShaderCore.Editor
{
    internal class MaterialReplacerWindow : EditorWindow
    {
        private GameObject _targetObject;
        private DefaultAsset _targetFolder;

        [MenuItem("Window/EasyShader/Material Replacer")]
        public static void ShowWindow()
        {
            GetWindow<MaterialReplacerWindow>("Material Replacer");
        }

        private void OnGUI()
        {
            GUILayout.Space(10);
            GUILayout.Label("置換設定", EditorStyles.boldLabel);

            if (_targetObject == null && Selection.activeGameObject != null)
                _targetObject = Selection.activeGameObject;

            _targetObject = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("対象オブジェクト", "配下の全 Renderer が対象（Hierarchy の選択から自動補完）"),
                _targetObject, typeof(GameObject), true);

            _targetFolder = (DefaultAsset)EditorGUILayout.ObjectField(
                new GUIContent("マテリアルフォルダ", "差し替え先のマテリアルが入ったフォルダ。現在のマテリアルと同名のものへ置換される"),
                _targetFolder, typeof(DefaultAsset), false);

            GUILayout.Space(10);
            EditorGUILayout.HelpBox(
                "現在アサインされているマテリアルと同じ名前のマテリアルがフォルダ内にあれば置換します（Undo 可）。" +
                "元マテリアルと同名で EasyPBR / EasyToon 版を用意しておくと、モデル一式をワンクリックで移行できます。",
                MessageType.Info);
            GUILayout.Space(10);

            using (new EditorGUI.DisabledScope(_targetObject == null || _targetFolder == null))
            {
                if (GUILayout.Button("マテリアルを置換", GUILayout.Height(30)))
                    ReplaceMaterials();
            }
        }

        private void ReplaceMaterials()
        {
            var folderPath = AssetDatabase.GetAssetPath(_targetFolder);
            if (!AssetDatabase.IsValidFolder(folderPath))
            {
                EditorUtility.DisplayDialog("エラー", "有効なフォルダを指定してください。", "OK");
                return;
            }

            // フォルダ内の全マテリアルを名前をキーにした辞書へ（同名重複は先勝ち）。
            var guids = AssetDatabase.FindAssets("t:Material", new[] { folderPath });
            var newMaterialsDict = new Dictionary<string, Material>();
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat != null && !newMaterialsDict.ContainsKey(mat.name))
                    newMaterialsDict.Add(mat.name, mat);
            }

            if (newMaterialsDict.Count == 0)
            {
                EditorUtility.DisplayDialog("確認", "指定したフォルダ内にマテリアルが見つかりませんでした。", "OK");
                return;
            }

            var renderers = _targetObject.GetComponentsInChildren<Renderer>(true);
            var replacedCount = 0;

            foreach (var renderer in renderers)
            {
                // アセットを変更しないよう sharedMaterials のスロット参照だけ差し替える。
                var sharedMaterials = renderer.sharedMaterials;
                var changed = false;

                for (var i = 0; i < sharedMaterials.Length; i++)
                {
                    var currentMat = sharedMaterials[i];
                    if (currentMat == null || !newMaterialsDict.TryGetValue(currentMat.name, out var newMat)) continue;
                    if (ReferenceEquals(currentMat, newMat)) continue; // 既に置換済み

                    Undo.RecordObject(renderer, "Replace Material");
                    sharedMaterials[i] = newMat;
                    changed = true;
                    replacedCount++;
                }

                if (changed)
                {
                    renderer.sharedMaterials = sharedMaterials;
                    EditorUtility.SetDirty(renderer);
                }
            }

            EditorUtility.DisplayDialog("完了", $"{replacedCount} 個のマテリアルスロットを置換しました。", "OK");
        }
    }
}
