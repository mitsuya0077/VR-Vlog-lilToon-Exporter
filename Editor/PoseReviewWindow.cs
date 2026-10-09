using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter
{
    internal sealed class PoseReviewWindow : EditorWindow
    {
        GameObject source, copy;
        GameObject[] excluded;
        ExportGimmickOptions gimmicks;
        PoseExportOptions options;
        PoseExportSession session;
        NdmfExportPreparation preparation;
        PreviewRenderUtility preview;
        readonly List<Mesh> meshes = new List<Mesh>();
        readonly Dictionary<Transform, Quaternion> rest = new Dictionary<Transform, Quaternion>();
        Vector3 hipsRest;
        Vector2 scroll;
        IEnumerator<PoseCandidate> pendingSamples;
        bool rebuildRequested;
        int processed;
        Rect dropRect, previewRect;
        internal bool IsBusy => rebuildRequested || pendingSamples != null;
        float yaw;
        string error;
        PoseCandidate playing;
        bool manualOnly;
        double playStarted;

        internal static PoseReviewWindow Show(GameObject avatar, PoseExportOptions options, GameObject[] excluded, ExportGimmickOptions gimmicks, bool manualOnly = false)
        {
            var window = CreateInstance<PoseReviewWindow>(); window.source = avatar; window.options = options;
            window.excluded = excluded; window.gimmicks = gimmicks;
            window.manualOnly = manualOnly;
            window.titleContent = new GUIContent(ExporterLocalization.T("ポーズを確認・調整")); window.minSize = new Vector2(620, 600);
            window.RequestRebuild(); window.ShowUtility();
            return window;
        }
        void OnEnable() => EditorApplication.update += AdvanceRebuild;
        void RequestRebuild() { rebuildRequested = true; Repaint(); }

        // Never rebuild inside a drag/button IMGUI event. Sample at most one
        // candidate per editor update so a batch leaves time for input/repaint.
        void AdvanceRebuild()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (!IsBusy)
            {
                if (playing?.Animation != null && copy != null)
                {
                    var animation = playing.Animation;
                    var elapsed = EditorApplication.timeSinceStartup - playStarted;
                    ApplyAnimation(animation, animation.Loop ? elapsed % animation.Duration : Math.Min(elapsed, animation.Duration));
                    if (!animation.Loop && elapsed >= animation.Duration) playing = null;
                    Repaint();
                }
                return;
            }
            try
            {
                if (rebuildRequested)
                {
                    rebuildRequested = false;
                    PreparePreview();
                }
                else if (pendingSamples.MoveNext()) processed++;
                else { pendingSamples.Dispose(); pendingSamples = null; }
            }
            catch (Exception e) { Cleanup(); error = e.Message; }
            Repaint();
        }

        void PreparePreview()
        {
            Cleanup(); error = null; processed = 0;
            try
            {
                using var manual = new ExportObjectExclusions(source, excluded);
                var findings = gimmicks.AutoExclude ? ExportGimmickDetection.Analyze(source, manual.Contains) : new List<ExportGimmickFinding>();
                using var omissions = new ExportObjectExclusions(source, excluded.Concat(ExportGimmickDetection.AutomaticRoots(findings, gimmicks)));
                session = new PoseExportSession(source, options, omissions.Contains,
                    manualOnly ? new VrChatMenuImportPolicy { SkipAll = true } : null);
                if (manualOnly) session.Entries.RemoveAll(entry => entry.Source != "手動");
                copy = Object.Instantiate(source); copy.hideFlags = HideFlags.HideAndDontSave;
                copy.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                copy.transform.localScale = Vector3.one;
                MaAppearanceSnapshot.Apply(source, copy, meshes, omissions);
                PoseExportSession.RemoveAplFromCopy(source, copy);
                preparation = NdmfExportPreparation.Prepare(source, copy);
                foreach (var behaviour in copy.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                // This preview has no Animator/player-loop update. Recalculate
                // skinning when rendering manually applied bone transforms.
                foreach (var renderer in copy.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    renderer.updateWhenOffscreen = true;
                    renderer.forceMatrixRecalculationPerRender = true;
                }
                var animator = copy.GetComponent<Animator>();
                foreach (var name in VRVlog.Poses.HumanoidAnimationData.BoneNames)
                { var bone = animator.GetBoneTransform(PoseSampling.HumanBone(name)); if (bone != null) rest[bone] = bone.rotation; }
                hipsRest = animator.GetBoneTransform(HumanBodyBones.Hips).position;
                preview = new PreviewRenderUtility(); preview.AddSingleGO(copy);
                preview.camera.fieldOfView = 30; preview.camera.nearClipPlane = .01f; preview.camera.farClipPlane = 100;
                preview.lights[0].intensity = 1; preview.lights[0].transform.rotation = Quaternion.Euler(30, 150, 0);
                preview.lights[1].intensity = .7f;
                pendingSamples = session.CollectPreparedIncrementally(copy).GetEnumerator();
            }
            catch (Exception e) { Cleanup(); error = e.Message; }
        }
        void OnGUI()
        {
            if (options == null) return;
            if (manualOnly) { DrawSimple(); return; }
            EditorGUILayout.HelpBox(ExporterLocalization.T("静止ポーズと手動追加した動くポーズを含めます。動くポーズは開始秒から再生し、頭・首はクリップに指定がある場合だけ含めます。"), MessageType.Info);
            using (new EditorGUI.DisabledScope(IsBusy))
                if (GUILayout.Button(ExporterLocalization.T("登録情報を再取得"))) RequestRebuild();
            if (error != null) EditorGUILayout.HelpBox(ExporterLocalization.T(error), MessageType.Warning);
            if (IsBusy)
                EditorGUILayout.HelpBox(ExporterLocalization.T("ポーズを確認中… ") + processed + " / " + (session?.Entries.Count ?? 0), MessageType.Info);
            else if (session != null)
                EditorGUILayout.HelpBox(ExporterLocalization.T("同梱するポーズ: ") + session.SelectedCount + " / 128" +
                    (session.SelectedCount > 128 ? ExporterLocalization.T(" — 不要な項目を除外してください。") : ""), session.SelectedCount > 128 ? MessageType.Warning : MessageType.None);

            var drop = GUILayoutUtility.GetRect(100, 34, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint) dropRect = drop;
            GUI.Box(drop, ExporterLocalization.T("複数の .anim をここへドロップして手動追加（既定0秒）"));
            var current = Event.current;
            if (drop.Contains(current.mousePosition) && (current.type == EventType.DragUpdated || current.type == EventType.DragPerform))
            {
                var clips = DragAndDrop.objectReferences.OfType<AnimationClip>().Where(c => c != null).Distinct().ToArray();
                var accept = !IsBusy && clips.Length > 0;
                DragAndDrop.visualMode = accept ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                if (accept && current.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var clip in clips)
                        if (!options.Manual.Any(m => m.Clip == clip && m.Time == 0)) options.Manual.Add(new ManualPose { Clip = clip });
                    RequestRebuild();
                    current.Use();
                    // The next Layout must see the new rows before drawing them.
                    return;
                }
                current.Use();
            }

            // Both lists share a bounded viewport. Twenty manual rows must not
            // push the apply button or preview out of the window.
            using (new EditorGUI.DisabledScope(IsBusy))
            {
                scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(position.height * .42f));
                if (session != null)
                    foreach (var row in session.Entries)
                    {
                        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                        {
                            EditorGUILayout.BeginHorizontal();
                            using (new EditorGUI.DisabledScope(row.Error != null))
                            {
                                var enabled = !options.Excluded.Contains(row.Id);
                                var next = EditorGUILayout.Toggle(enabled && row.Error == null, GUILayout.Width(20));
                                if (next != enabled && row.Error == null)
                                {
                                    if (next) options.Excluded.Remove(row.Id); else options.Excluded.Add(row.Id);
                                    if (row.IsAnimation) RequestRebuild();
                                }
                            }
                            // Metadata errors must remain repairable.
                            var name = EditorGUILayout.TextField(row.Name);
                            if (name != row.Name) options.Names[row.Id] = row.Name = name;
                            using (new EditorGUI.DisabledScope(row.Error != null || row.Data == null && row.Animation == null))
                                if (GUILayout.Button(ExporterLocalization.T("プレビュー"), GUILayout.Width(90))) ApplyPreview(row);
                            EditorGUILayout.EndHorizontal();
                            EditorGUILayout.LabelField(row.Source + " / " + row.Category, EditorStyles.miniLabel);
                    if (row.Error != null) EditorGUILayout.HelpBox(ExporterLocalization.T(row.Error), MessageType.Warning);
                    else if (row.Note.Length != 0) EditorGUILayout.HelpBox(ExporterLocalization.T(row.Note), MessageType.Info);
                        }
                    }
                if (options.Manual.Count > 0) EditorGUILayout.LabelField(ExporterLocalization.T("手動追加したポーズ"), EditorStyles.boldLabel);
                var remove = -1;
                for (var i = 0; i < options.Manual.Count; i++)
                {
                    var row = options.Manual[i];
                    EditorGUILayout.BeginHorizontal();
                    row.Clip = (AnimationClip)EditorGUILayout.ObjectField(row.Clip, typeof(AnimationClip), false);
                    var animated = session?.Entries.Any(entry => entry.IsAnimation && entry.Layers.Count == 1 && entry.Layers[0].Clip == row.Clip) == true;
                    EditorGUILayout.LabelField(ExporterLocalization.T(animated ? "開始秒" : "採用秒"), GUILayout.Width(45));
                    row.Time = EditorGUILayout.FloatField(row.Time, GUILayout.Width(65));
                    if (GUILayout.Button(ExporterLocalization.T("削除"), GUILayout.Width(45))) remove = i;
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndScrollView();
                if (options.Manual.Count > 0 && GUILayout.Button(ExporterLocalization.T("手動ポーズの変更をプレビューに反映"))) RequestRebuild();
                if (remove >= 0)
                {
                    options.Manual.RemoveAt(remove);
                    RequestRebuild();
                    return;
                }
            }
            if (preview == null) return;
            using (new EditorGUI.DisabledScope(IsBusy)) yaw = EditorGUILayout.Slider(ExporterLocalization.T("向き"), yaw, -180, 180);
            var rect = GUILayoutUtility.GetRect(100, 120, GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
            if (Event.current.type != EventType.Repaint) return;
            previewRect = rect;
            if (rect.width <= 0 || rect.height <= 0) return;
            var center = hipsRest; var distance = Mathf.Max(1, hipsRest.y * 3.8f);
            preview.BeginPreview(rect, GUIStyle.none);
            preview.camera.transform.position = center + Quaternion.Euler(0, yaw, 0) * Vector3.forward * distance;
            preview.camera.transform.LookAt(center); preview.Render(); GUI.DrawTexture(rect, preview.EndPreview(), ScaleMode.ScaleToFit, false);
        }
        void DrawSimple()
        {
            using (new EditorGUILayout.VerticalScope(new GUIStyle { padding = new RectOffset(16, 16, 12, 12) }))
            {
                EditorGUILayout.LabelField(ExporterLocalization.T("ポーズを確認"), EditorStyles.boldLabel);
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField(ExporterLocalization.T("動くポーズは先頭から最後まで再生します。"), EditorStyles.wordWrappedLabel);
                if (error != null) EditorGUILayout.HelpBox(error, MessageType.Warning);
                if (IsBusy) EditorGUILayout.LabelField(ExporterLocalization.T("確認中…"), EditorStyles.miniLabel);
                scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(150));
                if (session != null)
                    foreach (var row in session.Entries)
                    {
                        using (new EditorGUILayout.HorizontalScope(GUILayout.Height(28)))
                        {
                            EditorGUILayout.LabelField(row.Name);
                            using (new EditorGUI.DisabledScope(IsBusy || row.Error != null || row.Data == null && row.Animation == null))
                                if (GUILayout.Button(ExporterLocalization.T("確認"), GUILayout.Width(60))) ApplyPreview(row);
                        }
                        if (row.Error != null) EditorGUILayout.HelpBox(row.Error, MessageType.Warning);
                    }
                EditorGUILayout.EndScrollView();
                if (preview == null) return;
                EditorGUILayout.Space(12);
                yaw = EditorGUILayout.Slider(ExporterLocalization.T("向き"), yaw, -180, 180);
                var rect = GUILayoutUtility.GetRect(100, 120, GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
                if (Event.current.type != EventType.Repaint || rect.width <= 0 || rect.height <= 0) return;
                preview.BeginPreview(rect, GUIStyle.none);
                preview.camera.transform.position = hipsRest + Quaternion.Euler(0, yaw, 0) * Vector3.forward * Mathf.Max(1, hipsRest.y * 3.8f);
                preview.camera.transform.LookAt(hipsRest); preview.Render();
                GUI.DrawTexture(rect, preview.EndPreview(), ScaleMode.ScaleToFit, false);
            }
        }

        void ApplyPreview(PoseCandidate row)
        {
            playing = null;
            if (row.Animation != null)
            {
                playing = row; playStarted = EditorApplication.timeSinceStartup;
                ApplyAnimation(row.Animation, 0); return;
            }
            if (row.Data == null) return;
            var animator = copy.GetComponent<Animator>();
            foreach (var pair in rest) pair.Key.rotation = pair.Value;
            animator.GetBoneTransform(HumanBodyBones.Hips).position = hipsRest;
            foreach (var bone in row.Data.Bones)
            {
                var target = animator.GetBoneTransform(PoseSampling.HumanBone(bone.Name)); var q = bone.Rotation;
                target.rotation = new Quaternion((float)q[0], -(float)q[1], -(float)q[2], (float)q[3]) * rest[target];
            }
            var p = row.Data.HipsOffset;
            animator.GetBoneTransform(HumanBodyBones.Hips).position += new Vector3(-(float)p[0], (float)p[1], (float)p[2]);
        }
        void ApplyAnimation(VRVlog.Poses.HumanoidAnimationData animation, double time)
        {
            var animator = copy.GetComponent<Animator>();
            foreach (var pair in rest) pair.Key.rotation = pair.Value;
            var hips = animator.GetBoneTransform(HumanBodyBones.Hips); hips.position = hipsRest;
            animation.Locate(time, out var left, out var right, out var blend);
            for (var i = 0; i < animation.Bones.Count; i++)
            {
                var target = animator.GetBoneTransform(PoseSampling.HumanBone(animation.Bones[i].Name));
                var a = left.Rotations[i]; var b = right.Rotations[i];
                var rotation = Quaternion.Slerp(new Quaternion((float)a[0], -(float)a[1], -(float)a[2], (float)a[3]),
                    new Quaternion((float)b[0], -(float)b[1], -(float)b[2], (float)b[3]), (float)blend);
                target.rotation = rotation * rest[target];
            }
            var p = Vector3.Lerp(new Vector3(-(float)left.HipsOffset[0], (float)left.HipsOffset[1], (float)left.HipsOffset[2]),
                new Vector3(-(float)right.HipsOffset[0], (float)right.HipsOffset[1], (float)right.HipsOffset[2]), (float)blend);
            hips.position += p;
        }
        void OnDisable()
        {
            EditorApplication.update -= AdvanceRebuild;
            rebuildRequested = false;
            Cleanup();
        }
        void Cleanup()
        {
            playing = null;
            pendingSamples?.Dispose(); pendingSamples = null;
            preview?.Cleanup(); preview = null;
            if (copy != null) Object.DestroyImmediate(copy); copy = null;
            preparation?.Dispose(); preparation = null; session?.Dispose(); session = null;
            foreach (var mesh in meshes) if (mesh != null) Object.DestroyImmediate(mesh);
            meshes.Clear(); rest.Clear();
        }
    }
}
