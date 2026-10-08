using System.Collections.Generic;
using NUnit.Framework;
using UniGLTF.Extensions.VRMC_vrm;
using UniVRM10;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class AvatarLicenseTests
    {
        private const string PublicLicenseUrl = "https://vrm.dev/licenses/1.0/";
        private const string AdditionalLicenseUrl = "https://example.com/avatar/vn3-license";
        private const string Copyright = "Copyright 2026 アバター作者";
        private const string ThirdPartyLicenses = "衣装: https://example.com/clothes/license\n髪: https://example.com/hair/license";

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void UnchangedDefaultsPreserveTheOriginalExporterLicense(bool fullLilToon, bool explicitOptions)
        {
            var meta = ExportMeta(explicitOptions ? new AvatarLicenseOptions() : null, fullLilToon);
            AssertIdentity(meta);
            Assert.That(meta["avatarPermission"], Is.EqualTo("onlyAuthor"));
            Assert.That(meta["commercialUsage"], Is.EqualTo("personalNonProfit"));
            Assert.That(meta["creditNotation"], Is.EqualTo("required"));
            Assert.That(meta["modification"], Is.EqualTo("prohibited"));
            foreach (var field in new[] { "allowExcessivelyViolentUsage", "allowExcessivelySexualUsage",
                "allowPoliticalOrReligiousUsage", "allowAntisocialOrHateUsage", "allowRedistribution" })
                Assert.That(meta[field], Is.False, field);
            AssertOptionalTextEmpty(meta, "otherLicenseUrl");
            AssertOptionalTextEmpty(meta, "copyrightInformation");
            AssertOptionalTextEmpty(meta, "thirdPartyLicenses");
            AssertOptionalTextEmpty(meta, "contactInformation");
        }

        [TestCase(0, false)]
        [TestCase(0, true)]
        [TestCase(1, false)]
        [TestCase(1, true)]
        [TestCase(2, false)]
        [TestCase(2, true)]
        public void EveryLicenseSelectionReachesTheActualExportedVrm(int selection, bool fullLilToon)
        {
            var options = SelectedLicense(selection);
            var before = JsonUtility.ToJson(options);
            var meta = ExportMeta(options, fullLilToon);
            AssertIdentity(meta);
            Assert.That(meta["avatarPermission"], Is.EqualTo(new[] { "onlyAuthor", "onlySeparatelyLicensedPerson", "everyone" }[selection]));
            Assert.That(meta["commercialUsage"], Is.EqualTo(new[] { "personalNonProfit", "personalProfit", "corporation" }[selection]));
            Assert.That(meta["creditNotation"], Is.EqualTo(selection == 1 ? "unnecessary" : "required"));
            Assert.That(meta["modification"], Is.EqualTo(new[] { "prohibited", "allowModification", "allowModificationRedistribution" }[selection]));
            // Each permission has a distinct pattern across the three cases;
            // a transposed mapping cannot pass merely because all flags agree.
            Assert.That(meta["allowExcessivelyViolentUsage"], Is.EqualTo(selection == 0));
            Assert.That(meta["allowExcessivelySexualUsage"], Is.EqualTo(selection == 1));
            Assert.That(meta["allowPoliticalOrReligiousUsage"], Is.EqualTo(selection != 2));
            Assert.That(meta["allowAntisocialOrHateUsage"], Is.EqualTo(selection == 2));
            Assert.That(meta["allowRedistribution"], Is.EqualTo(selection != 1));
            Assert.That(meta["otherLicenseUrl"], Is.EqualTo(AdditionalLicenseUrl));
            Assert.That(meta["copyrightInformation"], Is.EqualTo(Copyright));
            Assert.That(meta["thirdPartyLicenses"], Is.EqualTo(ThirdPartyLicenses));
            Assert.That(JsonUtility.ToJson(options), Is.EqualTo(before), "Export must not reset or normalize the selections retained by the window.");
        }

        [Test]
        public void LicenseSnapshotIsIndependentOfLaterChangesToWindowSelections()
        {
            var options = SelectedLicense(2);
            var snapshot = options.Copy();
            var before = JsonUtility.ToJson(options);
            Assert.That(snapshot, Is.Not.SameAs(options));
            Assert.That(JsonUtility.ToJson(snapshot), Is.EqualTo(before), "Every license and attribution field must be included in the export snapshot.");
            options.AvatarPermission = AvatarPermissionType.onlyAuthor;
            options.CommercialUsage = CommercialUsageType.personalNonProfit;
            options.CreditNotation = CreditNotationType.unnecessary;
            options.Modification = ModificationType.prohibited;
            options.AllowExcessivelyViolentUsage = !options.AllowExcessivelyViolentUsage;
            options.AllowExcessivelySexualUsage = !options.AllowExcessivelySexualUsage;
            options.AllowPoliticalOrReligiousUsage = !options.AllowPoliticalOrReligiousUsage;
            options.AllowAntisocialOrHateUsage = !options.AllowAntisocialOrHateUsage;
            options.AllowRedistribution = !options.AllowRedistribution;
            options.OtherLicenseUrl = "https://example.com/other-avatar/license";
            options.CopyrightInformation = "Another author";
            options.ThirdPartyLicenses = "Another material";
            Assert.That(JsonUtility.ToJson(snapshot), Is.EqualTo(before), "Pending exports must retain the conditions chosen for their avatar.");
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("  ")]
        public void LicenseAttributionDoesNotReplaceTheRequiredAuthor(string author)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var error = Assert.Throws<System.InvalidOperationException>(() =>
                UniVrmOneClickExporter.Export(fixture.Source, "License regression", author, licenseOptions: SelectedLicense(2)));
            Assert.That(error.Message, Does.Contain("作者名"));
        }

        [Test]
        public void ApplyingLicenseSelectionsPreservesModelIdentityAndExplicitMetadata()
        {
            var authors = new List<string> { "原作者", "共同作者" };
            var references = new List<string> { "https://example.com/source" };
            var meta = new VRM10ObjectMeta
            {
                Name = "Model identity", Version = "9", Authors = authors,
                ContactInformation = "Contact explicitly supplied by caller", References = references
            };
            SelectedLicense(2).ApplyTo(meta);
            Assert.That(meta.Name, Is.EqualTo("Model identity"));
            Assert.That(meta.Version, Is.EqualTo("9"));
            Assert.That(meta.Authors, Is.SameAs(authors));
            Assert.That(meta.Authors, Is.EqualTo(new[] { "原作者", "共同作者" }));
            Assert.That(meta.ContactInformation, Is.EqualTo("Contact explicitly supplied by caller"));
            Assert.That(meta.References, Is.SameAs(references));
        }

        private static AvatarLicenseOptions SelectedLicense(int selection)
        {
            return new AvatarLicenseOptions
            {
                AvatarPermission = new[] { AvatarPermissionType.onlyAuthor, AvatarPermissionType.onlySeparatelyLicensedPerson, AvatarPermissionType.everyone }[selection],
                CommercialUsage = new[] { CommercialUsageType.personalNonProfit, CommercialUsageType.personalProfit, CommercialUsageType.corporation }[selection],
                CreditNotation = selection == 1 ? CreditNotationType.unnecessary : CreditNotationType.required,
                Modification = new[] { ModificationType.prohibited, ModificationType.allowModification, ModificationType.allowModificationRedistribution }[selection],
                AllowExcessivelyViolentUsage = selection == 0,
                AllowExcessivelySexualUsage = selection == 1,
                AllowPoliticalOrReligiousUsage = selection != 2,
                AllowAntisocialOrHateUsage = selection == 2,
                AllowRedistribution = selection != 1,
                OtherLicenseUrl = AdditionalLicenseUrl,
                CopyrightInformation = Copyright,
                ThirdPartyLicenses = ThirdPartyLicenses
            };
        }

        private static Dictionary<string, object> ExportMeta(AvatarLicenseOptions options, bool fullLilToon)
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var shader = Shader.Find("lilToon");
            Assert.That(shader, Is.Not.Null, "The real lilToon package is required for the exporter entry point.");
            var material = new Material(shader);
            try
            {
                foreach (var skin in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>()) skin.sharedMaterial = material;
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "  License regression  ", "  アバター作者  ",
                    exporterVersion: fullLilToon ? "license-test" : null, lilToonVersion: fullLilToon ? "2.3.4" : null,
                    blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None }, licenseOptions: options);
                var glb = GlbDocument.Read(bytes);
                var extensions = (Dictionary<string, object>)glb.Json["extensions"];
                var vrm = (Dictionary<string, object>)extensions["VRMC_vrm"];
                return (Dictionary<string, object>)vrm["meta"];
            }
            finally { Object.DestroyImmediate(material); }
        }

        private static void AssertIdentity(Dictionary<string, object> meta)
        {
            Assert.That(meta["name"], Is.EqualTo("License regression"));
            Assert.That(meta["version"], Is.EqualTo("1.0"));
            Assert.That((List<object>)meta["authors"], Is.EqualTo(new[] { "アバター作者" }));
            Assert.That(meta["licenseUrl"], Is.EqualTo(PublicLicenseUrl), "The additional VN3 URL must not replace the VRM 1.0 license URL.");
        }

        private static void AssertOptionalTextEmpty(Dictionary<string, object> meta, string field)
        {
            if (meta.TryGetValue(field, out var value)) Assert.That(value, Is.Null.Or.EqualTo(""), field);
        }
    }
}
