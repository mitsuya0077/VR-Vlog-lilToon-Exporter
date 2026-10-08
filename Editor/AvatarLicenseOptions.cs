using System;
using UniGLTF.Extensions.VRMC_vrm;
using UniVRM10;

namespace VRVlog.LilToonExporter
{
    [Serializable]
    internal sealed class AvatarLicenseOptions
    {
        public AvatarPermissionType AvatarPermission = AvatarPermissionType.onlyAuthor;
        public CommercialUsageType CommercialUsage = CommercialUsageType.personalNonProfit;
        public CreditNotationType CreditNotation = CreditNotationType.required;
        public ModificationType Modification = ModificationType.prohibited;
        public bool AllowExcessivelyViolentUsage;
        public bool AllowExcessivelySexualUsage;
        public bool AllowPoliticalOrReligiousUsage;
        public bool AllowAntisocialOrHateUsage;
        public bool AllowRedistribution;
        public string OtherLicenseUrl = "";
        public string CopyrightInformation = "";
        public string ThirdPartyLicenses = "";

        internal AvatarLicenseOptions Copy() => new AvatarLicenseOptions
        {
            AvatarPermission = AvatarPermission,
            CommercialUsage = CommercialUsage,
            CreditNotation = CreditNotation,
            Modification = Modification,
            AllowExcessivelyViolentUsage = AllowExcessivelyViolentUsage,
            AllowExcessivelySexualUsage = AllowExcessivelySexualUsage,
            AllowPoliticalOrReligiousUsage = AllowPoliticalOrReligiousUsage,
            AllowAntisocialOrHateUsage = AllowAntisocialOrHateUsage,
            AllowRedistribution = AllowRedistribution,
            OtherLicenseUrl = OtherLicenseUrl,
            CopyrightInformation = CopyrightInformation,
            ThirdPartyLicenses = ThirdPartyLicenses,
        };

        internal void ApplyTo(VRM10ObjectMeta meta)
        {
            if (meta == null) throw new ArgumentNullException(nameof(meta));
            meta.AvatarPermission = AvatarPermission;
            meta.CommercialUsage = CommercialUsage;
            meta.CreditNotation = CreditNotation;
            meta.Modification = Modification;
            meta.ViolentUsage = AllowExcessivelyViolentUsage;
            meta.SexualUsage = AllowExcessivelySexualUsage;
            meta.PoliticalOrReligiousUsage = AllowPoliticalOrReligiousUsage;
            meta.AntisocialOrHateUsage = AllowAntisocialOrHateUsage;
            meta.Redistribution = AllowRedistribution;
            meta.OtherLicenseUrl = OtherLicenseUrl;
            meta.CopyrightInformation = CopyrightInformation;
            meta.ThirdPartyLicenses = ThirdPartyLicenses;
        }
    }
}
