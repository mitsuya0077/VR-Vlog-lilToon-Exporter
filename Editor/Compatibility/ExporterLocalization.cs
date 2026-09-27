using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    public static class ExporterLocalization
    {
        [Serializable]
        sealed class Entry
        {
            public string source;
            public string translated;
        }

        [Serializable]
        sealed class Table
        {
            public Entry[] entries;
        }

        static string _locale;
        static Dictionary<string, string> _messages;

        internal static string Locale => _locale ?? (_locale = ResolveLocale(CultureInfo.CurrentUICulture.Name));

        internal static string ResolveLocale(string languageTag)
        {
            if (string.IsNullOrEmpty(languageTag)) return "ja";
            if (languageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                return languageTag.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ||
                       languageTag.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) ||
                       languageTag.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase) ||
                       languageTag.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase)
                    ? "zh-Hant" : "zh-Hans";
            }
            if (languageTag.StartsWith("ko", StringComparison.OrdinalIgnoreCase)) return "ko";
            if (languageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en";
            return "ja";
        }

        public static string T(string source)
        {
            if (string.IsNullOrEmpty(source) || Locale == "ja") return source;
            if (_messages == null) LoadMessages();
            return _messages.TryGetValue(source, out var translated) ? translated : source;
        }

        static void LoadMessages()
        {
            _messages = new Dictionary<string, string>(StringComparer.Ordinal);
            var guids = AssetDatabase.FindAssets("ExporterLocale_" + Locale + " t:TextAsset");
            TextAsset asset = null;
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith("/Editor/Locales/ExporterLocale_" + Locale + ".json", StringComparison.Ordinal))
                    continue;
                asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                if (asset != null) break;
            }
            if (asset == null)
            {
                Debug.LogError("VR Vlog exporter locale data missing: " + Locale);
                return;
            }
            var table = JsonUtility.FromJson<Table>(asset.text);
            if (table?.entries == null) return;
            foreach (var entry in table.entries)
                if (entry != null && !string.IsNullOrEmpty(entry.source) &&
                    !string.IsNullOrEmpty(entry.translated))
                    _messages[entry.source] = entry.translated;
        }
    }
}
