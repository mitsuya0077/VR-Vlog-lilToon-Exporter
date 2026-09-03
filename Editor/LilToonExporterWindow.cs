using System;
using System.IO;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    public sealed class LilToonExporterWindow : EditorWindow
    {
        private const string SupportedLilToonVersion = "2.3.4";
        private GameObject avatar;
        private string avatarName = "";
        private string author = "";
        private string outputPath = "";
        private string fallbackPath = "";
        private bool showAdvanced;

        [MenuItem("VR Vlog/Export lilToon VRM 1.0")]
        public static void Open() => GetWindow<LilToonExporterWindow>(true, "VR Vlog lilToon Exporter");

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Select the avatar and export one VRM. VR Vlog creates the VRM 1.0 MToon fallback and adds the supported lilToon data automatically.",
                MessageType.Info);
            avatar = (GameObject)EditorGUILayout.ObjectField("Avatar root", avatar, typeof(GameObject), true);
            avatarName = EditorGUILayout.TextField("Avatar name", avatarName);
            author = EditorGUILayout.TextField("Author", author);
            PathField("Output VRM", ref outputPath, true);
            EditorGUILayout.LabelField("lilToon", InstalledLilToonStatus());
            EditorGUILayout.LabelField("UniVRM", UniVrmOneClickExporter.SupportedUniVrmSeries + ".x (installed automatically by VCC/ALCOM)");

            using (new EditorGUI.DisabledScope(avatar == null || string.IsNullOrWhiteSpace(outputPath) || string.IsNullOrWhiteSpace(avatarName) || string.IsNullOrWhiteSpace(author)))
                if (GUILayout.Button("Export VR Vlog VRM")) ExportOneClick();

            showAdvanced = EditorGUILayout.Foldout(showAdvanced, "Advanced: inject into an existing VRM 1.0");
            if (!showAdvanced) return;
            EditorGUILayout.HelpBox("Use this only when you already have a compatible UniVRM VRM 1.0 MToon fallback.", MessageType.None);
            PathField("Fallback VRM 1.0", ref fallbackPath, false);
            using (new EditorGUI.DisabledScope(avatar == null || string.IsNullOrWhiteSpace(fallbackPath) || string.IsNullOrWhiteSpace(outputPath)))
                if (GUILayout.Button("Inject lilToon extension")) ExportExistingFallback();
        }

        private void ExportOneClick()
        {
            ExportAtomically(() =>
            {
                var fallback = UniVrmOneClickExporter.Export(avatar, avatarName, author);
                return LilToonGlbExtension.Inject(fallback, avatar, PackageVersion(), RequireSupportedLilToon());
            });
        }

        private void ExportExistingFallback()
        {
            ExportAtomically(() =>
            {
                if (!File.Exists(fallbackPath)) throw new FileNotFoundException("Fallback VRM does not exist.", fallbackPath);
                if (string.Equals(Path.GetFullPath(fallbackPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Output must differ from the fallback VRM so the source remains untouched.");
                return LilToonGlbExtension.Inject(File.ReadAllBytes(fallbackPath), avatar, PackageVersion(), RequireSupportedLilToon());
            });
        }

        private void ExportAtomically(Func<byte[]> create)
        {
            try
            {
                if (File.Exists(outputPath) && !EditorUtility.DisplayDialog("Replace output?", outputPath, "Replace", "Cancel")) return;
                var bytes = create();
                var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                if (string.IsNullOrEmpty(directory)) throw new InvalidOperationException("Output directory is invalid.");
                Directory.CreateDirectory(directory);
                var temporary = Path.Combine(directory, "." + Path.GetFileName(outputPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    File.WriteAllBytes(temporary, bytes);
                    LilToonGlbExtension.Validate(File.ReadAllBytes(temporary));
                    if (File.Exists(outputPath)) File.Replace(temporary, outputPath, null); else File.Move(temporary, outputPath);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                EditorUtility.RevealInFinder(outputPath);
                EditorUtility.DisplayDialog("Export complete", $"Wrote {bytes.Length:N0} bytes.\nThe file contains an MToon fallback and optional VR Vlog lilToon data.", "OK");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Export failed", exception.Message, "OK");
            }
        }

        private static void PathField(string label, ref string value, bool save)
        {
            EditorGUILayout.BeginHorizontal();
            value = EditorGUILayout.TextField(label, value);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                var chosen = save
                    ? EditorUtility.SaveFilePanel(label, "", "avatar-liltoon.vrm", "vrm")
                    : EditorUtility.OpenFilePanel(label, "", "vrm");
                if (!string.IsNullOrEmpty(chosen)) value = chosen;
            }
            EditorGUILayout.EndHorizontal();
        }

        private static string PackageVersion()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(LilToonExporterWindow).Assembly);
            return info != null && !string.IsNullOrWhiteSpace(info.version) ? info.version : "0.3.0";
        }

        private static string InstalledLilToonStatus()
        {
            var package = FindLilToonPackage();
            return package == null ? "not installed as UPM/VPM package" : package.version;
        }

        private static string RequireSupportedLilToon()
        {
            var package = FindLilToonPackage();
            if (package == null || !string.Equals(package.version, SupportedLilToonVersion, StringComparison.Ordinal))
                throw new InvalidOperationException($"lilToon {SupportedLilToonVersion} is required. Installed package version: {package?.version ?? "unknown"}.");
            return package.version;
        }

        private static PackageInfo FindLilToonPackage()
        {
            foreach (var package in PackageInfo.GetAllRegisteredPackages())
                if (string.Equals(package.name, "jp.lilxyzw.liltoon", StringComparison.Ordinal)) return package;
            return null;
        }
    }
}
