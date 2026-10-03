using System.Reflection;
using NUnit.Framework;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class OptimizationLocalizationTests
    {
        [TestCase("en")]
        [TestCase("ko")]
        [TestCase("zh-Hans")]
        [TestCase("zh-Hant")]
        public void OptimizationAndPermanentFxErrorsTranslateWithoutChangingAvatarLabels(string locale)
        {
            var localeField = typeof(ExporterLocalization).GetField("_locale", BindingFlags.NonPublic | BindingFlags.Static);
            var messagesField = typeof(ExporterLocalization).GetField("_messages", BindingFlags.NonPublic | BindingFlags.Static);
            var previousLocale = localeField.GetValue(null);
            var previousMessages = messagesField.GetValue(null);
            try
            {
                localeField.SetValue(null, locale);
                messagesField.SetValue(null, null);
                const string label = "顔/Body/EyeShrink(Clone)";
                foreach (var prefix in new[]
                {
                    "最適化で書き出し用の表情が失われました: ",
                    "マテリアルの移動先を解決できません: ",
                    "変形しない表情の出力先メッシュがありません: ",
                    "常時適用するFXのBlendShapeが見つかりません: ",
                    "追跡表情の対象が最終VRMにありません: "
                })
                {
                    var translatedPrefix = ExporterLocalization.T(prefix);
                    Assert.That(translatedPrefix, Is.Not.EqualTo(prefix), locale + ": missing error translation");
                    var translated = ExporterLocalization.T(prefix + label);
                    Assert.That(translated, Is.EqualTo(translatedPrefix + label));
                    Assert.That(translated, Does.EndWith(label), "Authored mesh and shape labels must remain identifiable.");
                }
                const string fxFailure = "常時適用するFXの変形を確定できません。統合後のBlendShape設定を確認してください。";
                Assert.That(ExporterLocalization.T(fxFailure), Is.Not.EqualTo(fxFailure));
            }
            finally
            {
                localeField.SetValue(null, previousLocale);
                messagesField.SetValue(null, previousMessages);
            }
        }
    }
}
