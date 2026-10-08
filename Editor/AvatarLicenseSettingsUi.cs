using System;
using UniGLTF.Extensions.VRMC_vrm;
using UnityEditor;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    internal static class AvatarLicenseSettingsUi
    {
        private static readonly string[] AvatarPermissionOptions = { "作者のみ", "個別に許可された人", "全員" };
        private static readonly string[] CommercialUsageOptions = { "個人・非営利のみ", "個人・営利も可", "法人利用も可" };
        private static readonly string[] CreditNotationOptions = { "必要", "不要" };
        private static readonly string[] ModificationOptions = { "許可しない", "改変のみ許可", "改変物の再配布も許可" };
        private static readonly string[] PermissionOptions = { "許可しない", "許可する" };

        internal static void Draw(ref bool showSettings, AvatarLicenseOptions options)
        {
            showSettings = EditorGUILayout.Foldout(showSettings, ExporterLocalization.T("ライセンス設定"), true);
            if (!showSettings) return;

            EditorGUILayout.Space(6f);
            EditorGUILayout.HelpBox(ExporterLocalization.T("元の利用規約に合わせて設定してください。"), MessageType.None);

            var previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 164f;
            try
            {
                EditorGUILayout.Space(4f);
                options.AvatarPermission = (AvatarPermissionType)DrawPopup("アバターを利用できる人", (int)options.AvatarPermission, AvatarPermissionOptions);
                options.CommercialUsage = (CommercialUsageType)DrawPopup("商用利用", (int)options.CommercialUsage, CommercialUsageOptions);
                options.CreditNotation = (CreditNotationType)DrawPopup("クレジット表記", (int)options.CreditNotation, CreditNotationOptions);

                EditorGUILayout.Space(6f);
                options.AllowExcessivelyViolentUsage = DrawPermission("過剰な暴力表現", options.AllowExcessivelyViolentUsage);
                options.AllowExcessivelySexualUsage = DrawPermission("過剰な性的表現", options.AllowExcessivelySexualUsage);
                options.AllowPoliticalOrReligiousUsage = DrawPermission("政治・宗教目的", options.AllowPoliticalOrReligiousUsage);
                options.AllowAntisocialOrHateUsage = DrawPermission("反社会的・憎悪表現", options.AllowAntisocialOrHateUsage);

                EditorGUILayout.Space(6f);
                options.AllowRedistribution = DrawPermission("再配布", options.AllowRedistribution);
                options.Modification = (ModificationType)DrawPopup("改変", (int)options.Modification, ModificationOptions);

                EditorGUILayout.Space(6f);
                options.OtherLicenseUrl = EditorGUILayout.TextField(ExporterLocalization.T("規約URL（VN3等）"), options.OtherLicenseUrl);
                options.CopyrightInformation = EditorGUILayout.TextField(ExporterLocalization.T("著作権表示"), options.CopyrightInformation);
                EditorGUILayout.LabelField(ExporterLocalization.T("第三者ライセンス"));
                options.ThirdPartyLicenses = EditorGUILayout.TextArea(options.ThirdPartyLicenses, GUILayout.MinHeight(48f));
            }
            finally
            {
                EditorGUIUtility.labelWidth = previousLabelWidth;
            }
        }

        private static int DrawPopup(string label, int value, string[] choices)
        {
            return EditorGUILayout.Popup(ExporterLocalization.T(label), value, Array.ConvertAll(choices, ExporterLocalization.T));
        }

        private static bool DrawPermission(string label, bool value)
        {
            return DrawPopup(label, value ? 1 : 0, PermissionOptions) == 1;
        }
    }
}
