using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter
{
    internal sealed class AttachmentPreviewWindow : EditorWindow
    {
        private ExportAttachmentSession session;
        private PreviewRenderUtility preview;
        private GameObject previewCopy;
        private readonly Dictionary<Transform, Transform> copied = new Dictionary<Transform, Transform>();
        private readonly List<Action> restore = new List<Action>();
        private int[] choices;
        private string[] targetLabels;
        private Vector2 scroll;
        private float yaw, pitch, viewYaw;
        private string error;
        private Action accept;
        private bool disposed;

        internal static bool Review(ExportAttachmentSession session, ICollection<string> warnings)
        {
            var decisions = new List<(Transform root, Transform target)>();
            var accepted = false;
            var window = CreateInstance<AttachmentPreviewWindow>();
            try
            {
                window.session = session;
                window.titleContent = new GUIContent("VR Vlog パーツの追従");
                window.minSize = new Vector2(580f, 650f);
                window.position = new Rect(120, 80, 720, 820);
                window.choices = new int[session.Parts.Count];
                window.targetLabels = new[] { "現在の接続を維持" }.Concat(session.Targets.Select(t => session.Path(t))).ToArray();
                window.accept = () =>
                {
                    for (var i = 0; i < window.choices.Length; i++)
                        if (window.choices[i] > 0) decisions.Add((session.Parts[i].Root, session.Targets[window.choices[i] - 1]));
                    accepted = true;
                };
                window.InitializePreview();
                // Keep the prepared export copy and NDMF lease alive on this
                // synchronous call stack. Closing/canceling writes no output.
                WhileCopyInactive(session.Copy, window.ShowModal);
            }
            finally
            {
                window.DisposePreview();
                if (window != null) Object.DestroyImmediate(window);
            }
            if (!accepted) return false;
            foreach (var decision in decisions) session.Attach(decision.root, decision.target, warnings);
            return true;
        }

        internal static void WhileCopyInactive(GameObject copy, Action modal)
        {
            var active = copy.activeSelf;
            copy.SetActive(false);
            try { modal(); }
            finally { if (copy != null) copy.SetActive(active); }
        }

        private void InitializePreview()
        {
            preview = new PreviewRenderUtility();
            preview.cameraFieldOfView = 30f;
            preview.lights[0].intensity = 1.2f;
            preview.lights[0].transform.rotation = Quaternion.Euler(35f, 135f, 0f);
            preview.lights[1].intensity = 0.7f;
            preview.ambientColor = new Color(0.65f, 0.65f, 0.65f, 1f);
            // Build render components explicitly instead of Instantiate: an
            // ExecuteAlways script must never run against shared preview assets.
            previewCopy = CopyTransforms(session.Copy.transform, null).gameObject;
            preview.AddSingleGO(previewCopy);
            CopyRenderers();
            foreach (var pair in copied)
            {
                var value = pair.Value;
                var parent = value.parent;
                var position = value.localPosition;
                var rotation = value.localRotation;
                var scale = value.localScale;
                var sibling = value.GetSiblingIndex();
                restore.Add(() =>
                {
                    value.SetParent(parent, false);
                    value.localPosition = position;
                    value.localRotation = rotation;
                    value.localScale = scale;
                    value.SetSiblingIndex(sibling);
                });
            }
            UpdatePreview();
        }

        private Transform CopyTransforms(Transform original, Transform parent)
        {
            var copy = new GameObject(original.name).transform;
            if (previewCopy == null) previewCopy = copy.gameObject;
            copy.gameObject.hideFlags = HideFlags.HideAndDontSave;
            copy.SetParent(parent, false);
            copy.localPosition = original.localPosition;
            copy.localRotation = original.localRotation;
            copy.localScale = original.localScale;
            copy.gameObject.SetActive(original.gameObject.activeSelf);
            copied.Add(original, copy);
            for (var i = 0; i < original.childCount; i++) CopyTransforms(original.GetChild(i), copy);
            return copy;
        }

        private void CopyRenderers()
        {
            foreach (var renderer in ExportRendererSelection.Enumerate(session.Copy))
            {
                var target = copied[renderer.transform].gameObject;
                Renderer result;
                if (renderer is SkinnedMeshRenderer skin)
                {
                    var copy = target.AddComponent<SkinnedMeshRenderer>();
                    copy.sharedMesh = skin.sharedMesh;
                    copy.bones = skin.bones.Select(b => b != null && copied.TryGetValue(b, out var mapped) ? mapped : null).ToArray();
                    copy.rootBone = skin.rootBone != null && copied.TryGetValue(skin.rootBone, out var root) ? root : null;
                    copy.localBounds = skin.localBounds;
                    copy.quality = skin.quality;
                    copy.updateWhenOffscreen = true;
                    if (skin.sharedMesh != null)
                        for (var i = 0; i < skin.sharedMesh.blendShapeCount; i++) copy.SetBlendShapeWeight(i, skin.GetBlendShapeWeight(i));
                    result = copy;
                }
                else if (renderer is MeshRenderer)
                {
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (filter == null) continue;
                    target.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                    result = target.AddComponent<MeshRenderer>();
                }
                else continue;
                result.sharedMaterials = renderer.sharedMaterials;
                result.shadowCastingMode = renderer.shadowCastingMode;
                result.receiveShadows = renderer.receiveShadows;
                result.sortingLayerID = renderer.sortingLayerID;
                result.sortingOrder = renderer.sortingOrder;
            }
        }

        private void UpdatePreview()
        {
            error = null;
            try
            {
                foreach (var action in restore) action();
                for (var i = 0; i < choices.Length; i++)
                    if (choices[i] > 0)
                    {
                        var root = session.Parts[i].Root;
                        var target = session.Targets[choices[i] - 1];
                        session.ValidateConnection(root, target);
                        ExportAttachmentSession.ReparentPreservingPose(copied[root], copied[target]);
                    }
                if (session.Targets.Count > 0)
                {
                    var head = copied[session.Targets[0]];
                    head.localRotation = head.localRotation * Quaternion.Euler(pitch, yaw, 0f);
                }
            }
            catch (Exception exception) { error = exception.Message; }
            Repaint();
        }

        private void OnGUI()
        {
            if (session == null || preview == null) return;
            EditorGUILayout.HelpBox("本体と独立した骨を使うパーツを確認します。髪などを追従させる場合だけ接続先を指定してください。ペットなどは現在の接続を維持できます。", MessageType.Info);
            var rect = GUILayoutUtility.GetRect(200f, 300f, GUILayout.ExpandWidth(true));
            DrawPreview(rect);
            EditorGUI.BeginChangeCheck();
            yaw = EditorGUILayout.Slider("頭を左右に動かす", yaw, -60f, 60f);
            pitch = EditorGUILayout.Slider("頭を上下に動かす", pitch, -35f, 35f);
            viewYaw = EditorGUILayout.Slider("見る方向", viewYaw, -180f, 180f);
            var changed = EditorGUI.EndChangeCheck();
            EditorGUILayout.HelpBox("プレビューはコピーの頭の回転と接続を確認するためのものです。揺れ物・表情・拘束の動作は再生しません。元のアバターは変更しません。", MessageType.None);
            using (var view = new EditorGUILayout.ScrollViewScope(scroll))
            {
                scroll = view.scrollPosition;
                for (var i = 0; i < session.Parts.Count; i++)
                {
                    var part = session.Parts[i];
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        EditorGUILayout.LabelField("接続する範囲: " + session.Path(part.Root), EditorStyles.wordWrappedLabel);
                        EditorGUILayout.LabelField("同じ骨・階層を使う箇所（すべてに影響）: " +
                            string.Join(", ", part.Renderers.Select(r => session.Path(r.transform))), EditorStyles.wordWrappedLabel);
                        var selected = EditorGUILayout.Popup("追従先", choices[i], targetLabels);
                        if (selected != choices[i]) { choices[i] = selected; changed = true; }
                    }
                }
                if (session.Parts.Count == 0) EditorGUILayout.HelpBox("本体の骨と独立した接続範囲はありません。", MessageType.Info);
            }
            if (changed) UpdatePreview();
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("キャンセル", GUILayout.Height(30))) { Close(); return; }
                using (new EditorGUI.DisabledScope(!string.IsNullOrEmpty(error)))
                    if (GUILayout.Button("この接続で書き出す", GUILayout.Height(30)))
                    {
                        accept();
                        Close();
                    }
            }
        }

        private void DrawPreview(Rect rect)
        {
            if (Event.current.type != EventType.Repaint) return;
            var center = session.Targets.Count > 0 ? copied[session.Targets[0]].position : previewCopy.transform.position;
            var span = session.Targets.Count > 1
                ? Vector3.Distance(copied[session.Targets[0]].position, copied[session.Targets[session.Targets.Count - 1]].position) * 2.4f
                : Mathf.Abs(previewCopy.transform.lossyScale.y);
            span = Mathf.Max(0.05f, span);
            center -= previewCopy.transform.up * span * 0.13f;
            var direction = Quaternion.AngleAxis(viewYaw, previewCopy.transform.up) * previewCopy.transform.forward;
            preview.camera.transform.position = center + direction * span * 2.5f;
            preview.camera.transform.LookAt(center, previewCopy.transform.up);
            preview.camera.nearClipPlane = span * 0.01f;
            preview.camera.farClipPlane = span * 20f;
            try
            {
                preview.BeginPreview(rect, GUIStyle.none);
                Texture image;
                try { preview.Render(true); }
                finally { image = preview.EndPreview(); }
                GUI.DrawTexture(rect, image, ScaleMode.StretchToFill, false);
            }
            catch (Exception exception) { error = "プレビューを描画できません: " + exception.Message; }
        }

        private void OnDisable() => DisposePreview();
        private void DisposePreview()
        {
            if (disposed) return;
            disposed = true;
            preview?.Cleanup();
            preview = null;
            if (previewCopy != null) Object.DestroyImmediate(previewCopy);
            previewCopy = null;
        }
    }
}
