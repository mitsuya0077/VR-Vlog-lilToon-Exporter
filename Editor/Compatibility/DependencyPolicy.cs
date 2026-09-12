using System;

namespace VRVlog.LilToonExporter.Compatibility
{
    // No UniVRM/MA/lilToon types: diagnostics survive an unavailable backend.
    public static partial class DependencyPolicy
    {
        public static string UniVrmError(string vrm, string gltf)
        {
            if (!SupportsUniVrm(vrm) || !SupportsUniVrm(gltf))
                return "UniVRM / UniGLTF の対応版: " + UniVrmVersions +
                    "。現在: " + Display(vrm) + " / " + Display(gltf) + "。";
            if (!string.Equals(vrm, gltf, StringComparison.Ordinal))
                return "UniVRM と UniGLTF のバージョンが異なります（" + vrm + " / " + gltf + "）。同じ版を選んでください。";
            return null;
        }

        public static string LilToonError(string version) => version == LilToonVersion ? null :
            "lilToon " + LilToonVersion + " が必要です。現在: " + Display(version) + "。";

        public static string Display(string version) => string.IsNullOrEmpty(version) ? "未インストール" : version;

        public static void RequireUniVrm(string vrm, string gltf)
        {
            var error = UniVrmError(vrm, gltf);
            if (error != null) throw new InvalidOperationException(error + Recovery);
        }

        public const string Recovery = " VCC／ALCOMで対応版を確認してください。別のツールと競合する場合は、更新前のプロジェクトのコピーで書き出してください。";
    }
}
