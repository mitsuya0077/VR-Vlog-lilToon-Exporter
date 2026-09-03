using UnityEditor;

namespace VRVlog.LilToonExporter
{
    internal static class LilToonExportMenuValidation
    {
        [MenuItem("Assets/VR Vlog/Validate lilToon VRM", true)]
        private static bool CanValidate() => Selection.activeObject != null && AssetDatabase.GetAssetPath(Selection.activeObject).EndsWith(".vrm", System.StringComparison.OrdinalIgnoreCase);

        [MenuItem("Assets/VR Vlog/Validate lilToon VRM")]
        private static void Validate()
        {
            var path = AssetDatabase.GetAssetPath(Selection.activeObject);
            LilToonGlbExtension.Validate(System.IO.File.ReadAllBytes(path));
            EditorUtility.DisplayDialog("Valid", "The GLB is structurally valid, contains the optional lilToon extension, and does not require it.", "OK");
        }
    }
}
