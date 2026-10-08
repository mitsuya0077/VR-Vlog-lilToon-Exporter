using System;

namespace VRVlog.LilToonExporter.LanTransfer
{
    internal static class CloudTransferAvailability
    {
#if UNITY_EDITOR
        internal static bool Enabled => true;
#else
        internal static bool Enabled => false;
#endif
        internal const string DisabledMessage = "クラウドQR転送は開発環境専用です。この配布版では利用できません。VRMをファイルに書き出して、VR Vlogへ取り込んでください。";

        internal static void RequireEnabled()
        {
            if (!Enabled) throw new NotSupportedException(DisabledMessage);
        }
    }
}
