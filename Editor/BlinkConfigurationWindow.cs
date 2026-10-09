using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // Optional detail window; the primary export form remains compact.
    internal sealed class BlinkConfigurationWindow : EditorWindow
    {
        GameObject source;
        internal BlinkExportOptions Options;
        Action<BlinkExportOptions> apply;
        string error;
        Vector2 scroll;

        internal static BlinkConfigurationWindow Show(GameObject source, BlinkExportOptions options, Action<BlinkExportOptions> apply)
        {
            var window = CreateInstance<BlinkConfigurationWindow>();
            window.source = source; window.Options = options.Copy(); window.apply = apply;
            window.titleContent = new GUIContent(ExporterLocalization.T("瞬き"));
            window.minSize = new Vector2(460, 400);
            window.ShowUtility();
            return window;
        }

        void OnGUI()
        {
            if (source == null) { Close(); return; }
            using (new EditorGUILayout.VerticalScope(new GUIStyle { padding = new RectOffset(16, 16, 12, 12) }))
            using (var scrolling = new EditorGUILayout.ScrollViewScope(scroll))
            {
                scroll = scrolling.scrollPosition;
                var mode = (BlinkExportMode)EditorGUILayout.Popup(ExporterLocalization.T("設定"), (int)Options.Mode,
                    new[] { ExporterLocalization.T("自動"), ExporterLocalization.T("手動"), ExporterLocalization.T("瞬きなし") });
                if (mode != Options.Mode) { Options.SelectMode(mode); error = null; }
                EditorGUILayout.Space(10);
                if (Options.Mode == BlinkExportMode.Manual)
                {
                    EditorGUILayout.LabelField(ExporterLocalization.T("メッシュと閉眼用の変形を選び、適用量を調整してください。"), EditorStyles.wordWrappedLabel);
                    DrawBindings(ExporterLocalization.T("両目"), Options.Both);
                    var individual = Options.Left.Count > 0 || Options.Right.Count > 0;
                    var next = EditorGUILayout.ToggleLeft(ExporterLocalization.T("左右を個別に設定"), individual);
                    if (next && !individual) { Options.Left.Add(new BlinkShapeBinding()); Options.Right.Add(new BlinkShapeBinding()); }
                    if (!next && individual) { Options.Left.Clear(); Options.Right.Clear(); }
                    if (next) { DrawBindings(ExporterLocalization.T("左目"), Options.Left); DrawBindings(ExporterLocalization.T("右目"), Options.Right); }
                }
                if (error != null) EditorGUILayout.HelpBox(ExporterLocalization.T(error), MessageType.Warning);
                EditorGUILayout.Space(12);
                if (GUILayout.Button(ExporterLocalization.T("開閉をプレビュー"), GUILayout.Height(30)))
                    try { BlinkPreviewWindow.Show(source, Options.Copy(), Array.Empty<GameObject>(), new ExportGimmickOptions()); error = null; }
                    catch (Exception exception) { error = exception.Message; }
                if (GUILayout.Button(ExporterLocalization.T("設定を適用"), GUILayout.Height(32)))
                    try { ApplySettings(); }
                    catch (Exception exception) { error = exception.Message; }
            }
        }

        void DrawBindings(string label, List<BlinkShapeBinding> bindings)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            var renderers = ExportRendererSelection.Enumerate(source).OfType<SkinnedMeshRenderer>()
                .Where(renderer => renderer.sharedMesh != null && renderer.sharedMesh.blendShapeCount > 0).ToArray();
            var choices = new[] { ExporterLocalization.T("メッシュを選択") }.Concat(renderers.Select(renderer =>
                AnimationUtility.CalculateTransformPath(renderer.transform, source.transform))).ToArray();
            for (var i = 0; i < bindings.Count; i++)
            {
                var binding = bindings[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    var selected = Array.IndexOf(renderers, binding.Renderer);
                    var next = EditorGUILayout.Popup(ExporterLocalization.T("メッシュ"), selected + 1, choices);
                    if (next != selected + 1) { binding.Renderer = next == 0 ? null : renderers[next - 1]; binding.Shape = ""; }
                    if (GUILayout.Button("×", GUILayout.Width(26))) { bindings.RemoveAt(i--); continue; }
                }
                var mesh = binding.Renderer != null ? binding.Renderer.sharedMesh : null;
                if (mesh == null) continue;
                var names = Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToArray();
                var nextShape = EditorGUILayout.Popup(ExporterLocalization.T("閉眼用の変形"), Array.IndexOf(names, binding.Shape) + 1,
                    new[] { ExporterLocalization.T("変形を選択") }.Concat(names).ToArray());
                binding.Shape = nextShape == 0 ? "" : names[nextShape - 1];
                binding.Weight = EditorGUILayout.Slider(ExporterLocalization.T("適用量 (%)"), binding.Weight, 0, 100);
            }
            if (GUILayout.Button(label + ExporterLocalization.T("の変形を追加"))) bindings.Add(new BlinkShapeBinding());
        }

        internal void ApplySettings()
        {
            using (BlinkExportSession.Resolve(source, Options)) { }
            apply?.Invoke(Options.Copy());
            Close();
        }

        void OnDisable() { apply = null; source = null; }
    }
}
