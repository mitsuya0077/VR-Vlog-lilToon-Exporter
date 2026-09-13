using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace VRVlog.LilToonExporter.Compatibility
{
    public static class DependencyDiagnostics
    {
        const string BackendAssembly = "VRVlog.LilToonExporter.Editor";
        const string BackendType = "VRVlog.LilToonExporter.LilToonExporterWindow";
        const string BackendDefinition = "Packages/com.vrvlog.liltoon-vrm-exporter/Editor/VRVlog.LilToonExporter.Editor.asmdef";
        static Action openExporter;
        static string backendError;

        // Resolve at the point of use. Opening the menu must not depend on an
        // InitializeOnLoadMethod callback having registered a delegate earlier.
        // Do not load DLLs from disk: only use the backend Unity actually loaded.
        public static Action OpenExporter
        {
            get
            {
                if (openExporter != null) return openExporter;
                backendError = null;
                try
                {
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (assembly.GetName().Name != BackendAssembly) continue;
                        var type = assembly.GetType(BackendType, false);
                        var method = type?.GetMethod("Open", BindingFlags.Public | BindingFlags.Static,
                            null, Type.EmptyTypes, null);
                        if (method == null || method.ReturnType != typeof(void))
                        {
                            backendError = "書き出し画面の入口が見つかりません。";
                            break;
                        }
                        openExporter = (Action)Delegate.CreateDelegate(typeof(Action), method);
                        break;
                    }
                }
                catch (Exception exception) when (exception is TypeLoadException || exception is FileNotFoundException ||
                    exception is FileLoadException || exception is BadImageFormatException || exception is ArgumentException)
                {
                    backendError = exception.GetType().Name;
                }
                // A miss is not cached; a later assembly load can recover immediately.
                return openExporter;
            }
        }

        public static void RefreshBackend()
        {
            openExporter = null;
            backendError = null;
        }

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

        public static bool IsBusy => EditorApplication.isCompiling || EditorApplication.isUpdating;

        public static string StartupError(Dictionary<string, string> packages)
        {
            if (IsBusy) return "Unityがパッケージを読み込み中です。完了すると自動で再確認します。";
            var dependencyError = Error(packages);
            if (dependencyError != null) return dependencyError + DependencyPolicy.Recovery;
            if (OpenExporter != null) return null;
            if (EditorUtility.scriptCompilationFailed)
                return "Unityでコンパイルエラーが発生しています。Consoleの赤いエラーを確認してください。黄色い更新通知だけでは書き出しを止めません。";
            return "書き出し処理の準備が完了していません。「再読み込み」を押してください。" +
                (backendError == null ? "" : "\n" + backendError);
        }

        public static void ReloadBackend()
        {
            if (IsBusy) return;
            RefreshBackend();
            if (Error(Installed()) != null || OpenExporter != null) return;
            // An explicit retry repairs stale import/compilation state without
            // editing package versions, project settings, or avatar assets.
            AssetDatabase.ImportAsset(BackendDefinition, ImportAssetOptions.ForceUpdate);
            CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache);
        }

        public static string CreateReport()
        {
            var packages = Installed();
            var text = new StringBuilder();
            text.AppendLine("Unity: " + Application.unityVersion);
            foreach (var name in new[] { "com.vrvlog.liltoon-vrm-exporter", "jp.lilxyzw.liltoon", "com.vrmc.vrm", "com.vrmc.gltf",
                "com.vrchat.base", "com.vrchat.avatars", "nadena.dev.modular-avatar", "nadena.dev.ndmf" })
                text.AppendLine(name + ": " + DependencyPolicy.Display(Version(packages, name)));
            text.AppendLine("Status: " + (StartupError(packages) ?? "Ready"));
            text.AppendLine("Compiling: " + EditorApplication.isCompiling + "; Updating: " + EditorApplication.isUpdating +
                "; Compilation failed: " + EditorUtility.scriptCompilationFailed);
            text.AppendLine("Backend entry point: " + (OpenExporter != null));
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetName().Name == BackendAssembly) text.AppendLine("Loaded: " + assembly.FullName);
            foreach (var assembly in CompilationPipeline.GetAssemblies(AssembliesType.Editor))
                if (assembly.name == BackendAssembly)
                {
                    text.AppendLine("Compilation includes: " + assembly.name);
                    foreach (var define in assembly.defines)
                        if (define.StartsWith("VRVLOG_", StringComparison.Ordinal)) text.AppendLine("Define: " + define);
                }
            return text.ToString();
        }

        [MenuItem("VR Vlog/lilToon VRM 1.0を書き出す")]
        public static void Open()
        {
            if (StartupError(Installed()) == null) OpenExporter();
            else OpenDiagnostics();
        }

        [MenuItem("VR Vlog/動作環境を確認")]
        public static void OpenDiagnostics() => EditorWindow.GetWindow<DependencyDiagnosticsWindow>(true, "VR Vlog 動作環境");
    }

    public sealed class DependencyDiagnosticsWindow : EditorWindow
    {
        Dictionary<string, string> packages;
        void OnEnable() => packages = DependencyDiagnostics.Installed();
        void OnInspectorUpdate()
        {
            packages = DependencyDiagnostics.Installed();
            Repaint();
        }
        void OnGUI()
        {
            if (packages == null) OnEnable();
            EditorGUILayout.LabelField("Unity", Application.unityVersion);
            foreach (var name in new[] { "com.vrvlog.liltoon-vrm-exporter", "jp.lilxyzw.liltoon", "com.vrmc.vrm", "com.vrmc.gltf", "nadena.dev.modular-avatar", "nadena.dev.ndmf" })
                EditorGUILayout.LabelField(name, DependencyPolicy.Display(DependencyDiagnostics.Version(packages, name)));
            var error = DependencyDiagnostics.StartupError(packages);
            EditorGUILayout.HelpBox(error == null ? "書き出しに必要なパッケージが揃っています。アバター固有の連携は書き出し時にも確認します。" : error,
                error == null || DependencyDiagnostics.IsBusy ? MessageType.Info : MessageType.Warning);
            using (new EditorGUI.DisabledScope(DependencyDiagnostics.IsBusy))
                if (GUILayout.Button("再読み込み"))
                {
                    DependencyDiagnostics.ReloadBackend();
                    packages = DependencyDiagnostics.Installed();
                }
            using (new EditorGUI.DisabledScope(error != null))
                if (GUILayout.Button("書き出し画面を開く")) DependencyDiagnostics.Open();
            if (GUILayout.Button("診断情報をコピー")) EditorGUIUtility.systemCopyBuffer = DependencyDiagnostics.CreateReport();
        }
    }
}
