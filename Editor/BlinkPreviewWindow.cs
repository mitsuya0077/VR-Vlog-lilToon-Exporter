using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter
{
    internal sealed class BlinkPreviewWindow : EditorWindow
    {
        PreviewRenderUtility preview;
        GameObject copy;
        BlinkExportSession blink;
        NdmfExportPreparation preparation;
        readonly List<Mesh> meshes = new List<Mesh>();
        readonly Dictionary<SkinnedMeshRenderer, float[]> rest = new Dictionary<SkinnedMeshRenderer, float[]>();
        Vector3 center;
        float distance = 1;
        float closure;
        int side;

        internal static void Show(GameObject source, BlinkExportOptions options,
            IEnumerable<GameObject> excluded, ExportGimmickOptions gimmickOptions)
        {
            var window = CreateInstance<BlinkPreviewWindow>();
            window.titleContent = new GUIContent("瞬きを確認");
            window.minSize = new Vector2(360, 460);
            try
            {
                window.Prepare(source, options, excluded, gimmickOptions);
                window.ShowUtility();
            }
            catch
            {
                window.Cleanup();
                Object.DestroyImmediate(window);
                throw;
            }
        }

        internal void Prepare(GameObject source, BlinkExportOptions options,
            IEnumerable<GameObject> excluded, ExportGimmickOptions gimmickOptions)
        {
            using var manual = new ExportObjectExclusions(source, excluded ?? Array.Empty<GameObject>());
            var findings = gimmickOptions?.AutoExclude != false
                ? ExportGimmickDetection.Analyze(source, manual.Contains) : new List<ExportGimmickFinding>();
            var automatic = ExportGimmickDetection.AutomaticRoots(findings, gimmickOptions ?? new ExportGimmickOptions());
            using var exclusions = new ExportObjectExclusions(source, (excluded ?? Array.Empty<GameObject>()).Concat(automatic));
            var resolved = BlinkExportSession.Resolve(source, options, exclusions.Contains);
            copy = Object.Instantiate(source);
            blink = resolved.ForClone(source, copy);
            copy.hideFlags = HideFlags.HideAndDontSave;
            var warnings = new List<string>();
            MaAppearanceSnapshot.Apply(source, copy, meshes, exclusions, warnings);
            preparation = NdmfExportPreparation.Prepare(source, copy, warnings);
            blink.Validate(copy);
            foreach (var behaviour in copy.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
            foreach (var renderer in copy.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (renderer.sharedMesh != null)
                    rest.Add(renderer, Enumerable.Range(0, renderer.sharedMesh.blendShapeCount).Select(renderer.GetBlendShapeWeight).ToArray());
            preview = new PreviewRenderUtility();
            preview.AddSingleGO(copy);
            var renderers = ExportRendererSelection.Enumerate(copy).ToArray();
            if (renderers.Length == 0) throw new InvalidOperationException("プレビューするメッシュがありません。");
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
            var animator = copy.GetComponent<Animator>();
            var head = animator != null && animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman
                ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
            center = head != null ? head.position + Vector3.up * bounds.size.y * .065f : bounds.center;
            distance = head != null ? Mathf.Max(.35f, bounds.size.y * .65f) : Mathf.Max(.5f, bounds.size.magnitude * 1.2f);
            preview.camera.fieldOfView = 30;
            preview.camera.nearClipPlane = .01f;
            preview.camera.farClipPlane = 100;
            preview.camera.clearFlags = CameraClearFlags.SolidColor;
            preview.camera.backgroundColor = new Color(.18f, .18f, .2f);
            preview.lights[0].intensity = 1;
            preview.lights[0].transform.rotation = Quaternion.Euler(30, 150, 0);
            preview.lights[1].intensity = .5f;
            preview.ambientColor = Color.gray;
        }

        void OnGUI()
        {
            if (preview == null || copy == null) return;
            EditorGUILayout.LabelField(blink.Description, EditorStyles.boldLabel);
            EditorGUILayout.LabelField("開いた状態と閉じた状態を比較できます。", EditorStyles.wordWrappedLabel);
            if (blink.Slots[1].Count > 0 && blink.Slots[2].Count > 0)
                side = GUILayout.Toolbar(side, new[] { "両目", "左目", "右目" });
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("開く")) closure = 0;
            if (GUILayout.Button("閉じる")) closure = 1;
            EditorGUILayout.EndHorizontal();
            closure = EditorGUILayout.Slider("閉じる量", closure, 0, 1);
            ApplyPose();
            var rect = GUILayoutUtility.GetRect(100, 100, GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                preview.BeginPreview(rect, GUIStyle.none);
                preview.camera.transform.position = center + copy.transform.forward * distance;
                preview.camera.transform.LookAt(center, Vector3.up);
                preview.Render();
                GUI.DrawTexture(rect, preview.EndPreview(), ScaleMode.ScaleToFit, false);
            }
            if (GUILayout.Button("閉じる", GUILayout.Height(26))) Close();
        }

        void ApplyPose()
        {
            foreach (var entry in rest)
                if (entry.Key != null)
                    for (var i = 0; i < entry.Value.Length; i++) entry.Key.SetBlendShapeWeight(i, entry.Value[i]);
            if (blink.Disabled) return;
            var slots = side > 0 ? new[] { side } : blink.Slots[1].Count > 0 && blink.Slots[2].Count > 0 ? new[] { 1, 2 } : new[] { 0 };
            foreach (var slot in slots)
                foreach (var binding in blink.Slots[slot])
                {
                    var index = binding.Renderer.sharedMesh.GetBlendShapeIndex(binding.Shape);
                    binding.Renderer.SetBlendShapeWeight(index, Mathf.Lerp(rest[binding.Renderer][index], binding.Weight, closure));
                }
        }

        void OnDisable() => Cleanup();
        internal void Cleanup()
        {
            preview?.Cleanup(); preview = null;
            if (copy != null) Object.DestroyImmediate(copy);
            copy = null;
            preparation?.Dispose(); preparation = null;
            foreach (var mesh in meshes) if (mesh != null) Object.DestroyImmediate(mesh);
            meshes.Clear(); rest.Clear();
        }
    }
}
