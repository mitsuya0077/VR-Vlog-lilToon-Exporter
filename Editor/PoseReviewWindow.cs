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
        int selected = -1;
        float yaw;
        string error;

        internal static void Show(GameObject avatar, PoseExportOptions options, GameObject[] excluded, ExportGimmickOptions gimmicks)
        {
            var window = CreateInstance<PoseReviewWindow>(); window.source = avatar; window.options = options;
            window.excluded = excluded; window.gimmicks = gimmicks;
            window.titleContent = new GUIContent("ポーズを確認・調整"); window.minSize = new Vector2(620, 600);
            window.Rebuild(); window.ShowUtility();
        }
        void Rebuild()
        {
            Cleanup(); error = null; selected = -1;
            try
            {
                using var manual = new ExportObjectExclusions(source, excluded);
                var findings = gimmicks.AutoExclude ? ExportGimmickDetection.Analyze(source, manual.Contains) : new List<ExportGimmickFinding>();
                using var omissions = new ExportObjectExclusions(source, excluded.Concat(ExportGimmickDetection.AutomaticRoots(findings, gimmicks)));
                session = new PoseExportSession(source, options, omissions.Contains);
                copy = Object.Instantiate(source); copy.hideFlags = HideFlags.HideAndDontSave;
                copy.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                copy.transform.localScale = Vector3.one;
                MaAppearanceSnapshot.Apply(source, copy, meshes, omissions);
                PoseExportSession.RemoveAplFromCopy(source, copy);
                preparation = NdmfExportPreparation.Prepare(source, copy);
                session.CollectPrepared(copy);
                foreach (var behaviour in copy.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                var animator = copy.GetComponent<Animator>();
                foreach (var name in VRVlog.Poses.HumanoidPoseData.BoneNames)
                { var bone = animator.GetBoneTransform(PoseSampling.HumanBone(name)); if (bone != null) rest[bone] = bone.rotation; }
                hipsRest = animator.GetBoneTransform(HumanBodyBones.Hips).position;
                preview = new PreviewRenderUtility(); preview.AddSingleGO(copy);
                preview.camera.fieldOfView = 30; preview.camera.nearClipPlane = .01f; preview.camera.farClipPlane = 100;
                preview.lights[0].intensity = 1; preview.lights[0].transform.rotation = Quaternion.Euler(30, 150, 0);
                preview.lights[1].intensity = .7f;
            }
            catch (Exception e) { error = e.Message; }
        }
        void OnGUI()
        {
            EditorGUILayout.HelpBox("対応した静止ポーズは自動で含まれます。首・顔・視線はアプリの追跡を使います。", MessageType.Info);
            if (GUILayout.Button("登録情報を再取得")) Rebuild();
            if (error != null) EditorGUILayout.HelpBox(error, MessageType.Warning);
            if (session != null)
                EditorGUILayout.HelpBox("同梱するポーズ: " + session.SelectedCount + " / 128" +
                    (session.SelectedCount > 128 ? " — 不要な項目を除外してください。" : ""), session.SelectedCount > 128 ? MessageType.Warning : MessageType.None);
            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(position.height * .42f));
            if (session != null)
                for (var i = 0; i < session.Entries.Count; i++)
                {
                    var row = session.Entries[i];
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        EditorGUILayout.BeginHorizontal();
                        using (new EditorGUI.DisabledScope(row.Error != null))
                        {
                            var enabled = !options.Excluded.Contains(row.Id);
                            var next = EditorGUILayout.Toggle(enabled && row.Error == null, GUILayout.Width(20));
                            if (next != enabled && row.Error == null) { if (next) options.Excluded.Remove(row.Id); else options.Excluded.Add(row.Id); }
                            var name = EditorGUILayout.TextField(row.Name);
                            if (name != row.Name) { options.Names[row.Id] = row.Name = name; }
                            if (GUILayout.Button("プレビュー", GUILayout.Width(90))) { selected = i; ApplyPreview(row); }
                        }
                        EditorGUILayout.EndHorizontal();
                        EditorGUILayout.LabelField(row.Source + " / " + row.Category, EditorStyles.miniLabel);
                        if (row.Error != null) EditorGUILayout.HelpBox(row.Error, MessageType.Warning);
                        else if (row.Note.Length != 0) EditorGUILayout.HelpBox(row.Note, MessageType.Info);
                    }
                }
            EditorGUILayout.EndScrollView();
            var drop = GUILayoutUtility.GetRect(100, 34, GUILayout.ExpandWidth(true));
            GUI.Box(drop, "複数の .anim をここへドロップして手動追加（既定0秒）");
            if (drop.Contains(Event.current.mousePosition) && (Event.current.type == EventType.DragUpdated || Event.current.type == EventType.DragPerform))
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (Event.current.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var clip in DragAndDrop.objectReferences.OfType<AnimationClip>()) options.Manual.Add(new ManualPose { Clip = clip });
                    Rebuild();
                }
                Event.current.Use();
            }
            for (var i = 0; i < options.Manual.Count; i++)
            {
                var row = options.Manual[i];
                EditorGUILayout.BeginHorizontal();
                row.Clip = (AnimationClip)EditorGUILayout.ObjectField(row.Clip, typeof(AnimationClip), false);
                row.Time = EditorGUILayout.FloatField("採用秒", row.Time);
                if (GUILayout.Button("削除", GUILayout.Width(45))) { options.Manual.RemoveAt(i--); }
                EditorGUILayout.EndHorizontal();
            }
            if (options.Manual.Count > 0 && GUILayout.Button("手動ポーズの変更をプレビューに反映")) Rebuild();
            if (preview == null) return;
            yaw = EditorGUILayout.Slider("向き", yaw, -180, 180);
            var rect = GUILayoutUtility.GetRect(100, 120, GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
            if (Event.current.type != EventType.Repaint) return;
            var center = hipsRest; var distance = Mathf.Max(1, hipsRest.y * 3.8f);
            preview.BeginPreview(rect, GUIStyle.none);
            preview.camera.transform.position = center + Quaternion.Euler(0, yaw, 0) * Vector3.forward * distance;
            preview.camera.transform.LookAt(center); preview.Render(); GUI.DrawTexture(rect, preview.EndPreview(), ScaleMode.ScaleToFit, false);
        }
        void ApplyPreview(PoseCandidate row)
        {
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
        void OnDisable() => Cleanup();
        void Cleanup()
        {
            preview?.Cleanup(); preview = null;
            if (copy != null) Object.DestroyImmediate(copy); copy = null;
            preparation?.Dispose(); preparation = null; session?.Dispose(); session = null;
            foreach (var mesh in meshes) if (mesh != null) Object.DestroyImmediate(mesh);
            meshes.Clear(); rest.Clear();
        }
    }
}
