// =============================================================================
//  ShaderGuiKit.cs
// -----------------------------------------------------------------------------
//  Unity マテリアル インスペクタ用の汎用描画キット（特定シェーダー非依存）。
//  セクション折りたたみ・日英ラベル・MaterialProperty キャッシュ・⚡注記・ツールバー
//  といった「描き方」の共通部品をまとめる。ShaderGUI から has-a で保持して使う。
//
//  状態（言語・各キャッシュ・折りたたみ）はこのインスタンスが所有する。
//  EditorPrefs の名前空間はコンストラクタの keyPrefix で分離する。
// =============================================================================
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Origuma.EasyShaderCore.Editor
{
    public class ShaderGuiKit
    {
        private readonly string _keyPrefix;
        private readonly string _langKey;
        private readonly string _customUIKey;

        private bool _jp;
        private bool _useCustomUI = true;
        private bool _prefsLoaded;

        public bool Jp => _jp;
        public bool UseCustomUI => _useCustomUI;

        private readonly Dictionary<string, bool> _foldCache = new();
        private readonly Dictionary<string, GUIContent> _labelCache = new();

        private MaterialProperty[] _cachedPropsRef;
        private readonly Dictionary<string, MaterialProperty> _propCache = new();

        private GUIStyle _miniWrapStyle;
        private GUIStyle MiniWrapStyle =>
            _miniWrapStyle ??= new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };

        private static readonly Color s_BarPro = new(0.22f, 0.22f, 0.24f);
        private static readonly Color s_BarPersonal = new(0.78f, 0.78f, 0.80f);
        private static readonly Color s_BarShadow = new(0f, 0f, 0f, 0.15f);
        private static readonly Color s_SubHeader = new(0.5f, 0.5f, 0.5f, 0.2f);

        private static readonly string[] s_UIModeEn = { "Custom", "Default" };
        private static readonly string[] s_UIModeJp = { "カスタム", "デフォルト" };
        private static readonly string[] s_LangOptions = { "English", "日本語" };

        // ⚡ = SRP Batcher バリアントを生むプロパティの注記。
        public const string VariantMark = " ⚡";
        public const string VariantTipEn =
            "\n\n[⚡ Shader variant] Differing values between materials split SRP Batcher batches. See Documentation~/SRP_BATCHER.md.";
        public const string VariantTipJp =
            "\n\n[⚡ シェーダーバリアント] マテリアル間で値が異なると SRP Batcher のバッチが分断されます。詳細は Documentation~/SRP_BATCHER.md。";

        public ShaderGuiKit(string keyPrefix)
        {
            _keyPrefix = keyPrefix;
            _langKey = keyPrefix + "lang.jp";
            _customUIKey = keyPrefix + "use.custom.ui";
        }

        // ================================================================
        //  初期化・キャッシュ
        // ================================================================
        public void LoadPrefs()
        {
            if (_prefsLoaded) return;
            _jp = EditorPrefs.GetBool(_langKey, Application.systemLanguage == SystemLanguage.Japanese);
            _useCustomUI = EditorPrefs.GetBool(_customUIKey, true);
            _prefsLoaded = true;
        }

        // properties 配列の参照が変わった時だけ Dictionary を再構築する。
        public void RebuildPropCache(MaterialProperty[] properties)
        {
            if (ReferenceEquals(properties, _cachedPropsRef)) return;
            _cachedPropsRef = properties;
            _propCache.Clear();
            foreach (var p in properties)
                _propCache[p.name] = p;
        }

        // ================================================================
        //  ツールバー（言語 / Custom-Default 切替 ＋ ⚡ 凡例 ＋ ドキュメントリンク）
        // ================================================================
        public void DrawToolbar(string title, string docLabel, string docUrl)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();

                EditorGUI.BeginChangeCheck();
                var uiMode = EditorGUILayout.Popup(_useCustomUI ? 0 : 1,
                    _jp ? s_UIModeJp : s_UIModeEn, GUILayout.Width(90));
                if (EditorGUI.EndChangeCheck())
                {
                    _useCustomUI = uiMode == 0;
                    EditorPrefs.SetBool(_customUIKey, _useCustomUI);
                }

                EditorGUI.BeginChangeCheck();
                var lang = EditorGUILayout.Popup(_jp ? 1 : 0, s_LangOptions, GUILayout.Width(90));
                if (EditorGUI.EndChangeCheck())
                {
                    _jp = lang == 1;
                    EditorPrefs.SetBool(_langKey, _jp);
                    _labelCache.Clear(); // 言語変更時にラベルキャッシュを破棄
                }
            }

            var legend = _jp
                ? "⚡ = シェーダーバリアントを生成（混在するとバッチが分断）"
                : "⚡ = generates a shader variant (mixing splits batches)";
            var legendContent = new GUIContent(legend);
            var legendH = MiniWrapStyle.CalcHeight(legendContent, EditorGUIUtility.currentViewWidth);
            EditorGUILayout.LabelField(legendContent, MiniWrapStyle, GUILayout.Height(legendH));
            if (!string.IsNullOrEmpty(docUrl))
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    DocLink(docLabel, docUrl);
                }
        }

        // ================================================================
        //  描画プリミティブ
        // ================================================================

        // Section ヘッダー（折りたたみ状態を _foldCache にキャッシュ）。
        public bool Section(string id, bool defaultOpen, string titleEn, string titleJp, string descEn, string descJp)
        {
            bool open;
            if (!_foldCache.TryGetValue(id, out open))
            {
                open = EditorPrefs.GetBool(_keyPrefix + "fold." + id, defaultOpen);
                _foldCache[id] = open;
            }

            var rect = EditorGUILayout.GetControlRect(false, 24f);
            var barColor = EditorGUIUtility.isProSkin ? s_BarPro : s_BarPersonal;
            EditorGUI.DrawRect(rect, barColor);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), s_BarShadow);
            EditorGUI.LabelField(new Rect(rect.x + 6f, rect.y + 3f, 14f, 18f), open ? "▼" : "▶");
            EditorGUI.LabelField(new Rect(rect.x + 22f, rect.y + 3f, rect.width - 26f, 18f), _jp ? titleJp : titleEn,
                EditorStyles.boldLabel);

            var e = Event.current;
            if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition))
            {
                open = !open;
                _foldCache[id] = open;
                EditorPrefs.SetBool(_keyPrefix + "fold." + id, open);
                e.Use();
            }

            if (open) EditorGUILayout.Space(4);
            return open;
        }

        // EditorStyles.foldoutHeader を使う簡易 foldout（開閉状態を id で永続化）。
        public bool Foldout(string id, bool defaultOpen, string label)
        {
            if (!_foldCache.TryGetValue(id, out var open))
            {
                open = EditorPrefs.GetBool(_keyPrefix + "fold." + id, defaultOpen);
                _foldCache[id] = open;
            }
            bool next = EditorGUILayout.Foldout(open, label, true, EditorStyles.foldoutHeader);
            if (next != open)
            {
                _foldCache[id] = next;
                EditorPrefs.SetBool(_keyPrefix + "fold." + id, next);
            }
            return next;
        }

        public void SubHeader(string en, string jp)
        {
            EditorGUILayout.Space(4);
            EditorGUI.DrawRect(EditorGUILayout.GetControlRect(false, 1f), s_SubHeader);
            EditorGUILayout.LabelField(_jp ? jp : en, EditorStyles.miniBoldLabel);
        }

        // ラベルは英語固定。説明は tooltip に日英で持たせ、言語に応じて切り替える。
        public GUIContent Label(string label, string tipEn, string tipJp)
        {
            var key = label + "␟" + tipEn + "␟" + tipJp;
            if (!_labelCache.TryGetValue(key, out var content))
            {
                content = new GUIContent(label, _jp ? tipJp : tipEn);
                _labelCache[key] = content;
            }

            return content;
        }

        // キャッシュから MaterialProperty を取得。
        public MaterialProperty Prop(string name)
        {
            return _propCache.TryGetValue(name, out var p) ? p : null;
        }

        public void P(MaterialEditor editor, MaterialProperty prop, string label, string tipEn, string tipJp)
        {
            if (prop == null) return;

            var content = Label(label, tipEn, tipJp);
            var h = editor.GetPropertyHeight(prop);
            var row = EditorGUILayout.GetControlRect(true, h);
            editor.ShaderProperty(row, prop, content);
        }

        public void P(MaterialEditor editor, string name, string label, string tipEn, string tipJp)
        {
            P(editor, Prop(name), label, tipEn, tipJp);
        }

        // ⚡ 付き（SRP Batcher バリアントを生むプロパティ）。
        public void Pv(MaterialEditor editor, MaterialProperty prop, string label, string tipEn, string tipJp)
        {
            P(editor, prop, label + VariantMark, tipEn + VariantTipEn, tipJp + VariantTipJp);
        }

        public void Pv(MaterialEditor editor, string name, string label, string tipEn, string tipJp)
        {
            Pv(editor, Prop(name), label, tipEn, tipJp);
        }

        public GUIContent VariantLabel(string label, string tipEn, string tipJp)
        {
            return Label(label + VariantMark, tipEn + VariantTipEn, tipJp + VariantTipJp);
        }

        // GitHub ドキュメントを開くリンクボタン。
        public void DocLink(string label, string url)
        {
            if (EditorGUILayout.LinkButton(label))
                Application.OpenURL(url);
        }
    }
}
