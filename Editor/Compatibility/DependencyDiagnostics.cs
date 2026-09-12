using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace VRVlog.LilToonExporter.Compatibility
{
    public static class DependencyDiagnostics
    {
        // Registered by a supported, compiled backend after every domain reload.
        public static Action OpenExporter;

        public static Dictionary<string, string> Installed()
        {
            var versions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var package in PackageInfo.GetAllRegisteredPackages()) versions[package.name] = package.version;
            return versions;
        }

        public static string Version(Dictionary<string, string> packages, string name) =>
            packages.TryGetValue(name, out var version) ? version : null;

        public static string Error(Dictionary<string, string> packages) =>
            DependencyPolicy.UniVrmError(Version(packages, "com.vrmc.vrm"), Version(packages, "com.vrmc.gltf")) ??
            DependencyPolicy.LilToonError(Version(packages, "jp.lilxyzw.liltoon"));

        [MenuItem("VR Vlog/lilToon VRM 1.0を書き出す")]
        public static void Open()
        {
            if (OpenExporter != null && Error(Installed()) == null) OpenExporter();
            else OpenDiagnostics();
        }

        [MenuItem("VR Vlog/動作環境を確認")]
        public static void OpenDiagnostics() => EditorWindow.GetWindow<DependencyDiagnosticsWindow>(true, "VR Vlog 動作環境");
    }

    public sealed class DependencyDiagnosticsWindow : EditorWindow
    {
        Dictionary<string, string> packages;
        void OnEnable() => packages = DependencyDiagnostics.Installed();
        void OnGUI()
        {
            if (packages == null) OnEnable();
            EditorGUILayout.LabelField("Unity", Application.unityVersion);
            foreach (var name in new[] { "jp.lilxyzw.liltoon", "com.vrmc.vrm", "com.vrmc.gltf", "nadena.dev.modular-avatar", "nadena.dev.ndmf" })
                EditorGUILayout.LabelField(name, DependencyPolicy.Display(DependencyDiagnostics.Version(packages, name)));
            var error = DependencyDiagnostics.Error(packages);
            if (error == null && DependencyDiagnostics.OpenExporter == null)
                error = "書き出し処理を読み込めません。Unity Consoleのコンパイルエラーを確認してください。";
            EditorGUILayout.HelpBox(error == null ? "書き出しに必要なパッケージが揃っています。アバター固有の連携は書き出し時にも確認します。" : error + DependencyPolicy.Recovery,
                error == null ? MessageType.Info : MessageType.Warning);
            if (GUILayout.Button("再確認")) packages = DependencyDiagnostics.Installed();
            using (new EditorGUI.DisabledScope(error != null))
                if (GUILayout.Button("書き出し画面を開く")) DependencyDiagnostics.Open();
        }
    }
}
