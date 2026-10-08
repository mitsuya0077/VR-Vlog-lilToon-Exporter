using System;
using System.Text.RegularExpressions;

namespace VRVlog.LilToonExporter.LanTransfer
{
    internal static class CloudTransferAvailability
    {
#if UNITY_EDITOR && VRVLOG_CLOUD_TRANSFER_DEVELOPMENT
        internal static bool Enabled => true;
#elif UNITY_EDITOR
        internal static bool Enabled => IsPrereleaseVersion(UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(CloudTransferAvailability).Assembly)?.version);
#else
        internal static bool Enabled => false;
#endif
        internal static bool IsPrereleaseVersion(string version) => version != null &&
            Regex.IsMatch(version, @"\A\d+\.\d+\.\d+-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z");
        internal const string DisabledMessage = "クラウドQR転送はβ版で利用できます。この安定版では利用できません。VRMをファイルに書き出して、VR Vlogへ取り込んでください。";

        internal static void RequireEnabled()
        {
            if (!Enabled) throw new NotSupportedException(DisabledMessage);
        }
    }
}
