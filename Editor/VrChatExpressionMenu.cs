using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // The SDK remains optional. Read its public serialized data without taking
    // an assembly dependency or changing the user's descriptor/menu assets.
    internal static class VrChatExpressionMenu
    {
        internal sealed class MorphValue
        {
            internal string Path = "", Shape = "";
            internal float Weight = 0f;
        }

        internal sealed class Entry
        {
            internal string Id, Name, Error;
            internal readonly Dictionary<string, float> Parameters = new Dictionary<string, float>(StringComparer.Ordinal);
            internal readonly List<MorphValue> Values = new List<MorphValue>();
        }

        internal sealed class Source
        {
            internal RuntimeAnimatorController Controller;
            internal readonly Dictionary<string, float> Defaults = new Dictionary<string, float>(StringComparer.Ordinal);
            internal readonly List<Entry> Entries = new List<Entry>();
            internal readonly List<string> Messages = new List<string>();
            internal int VisitedMenus;
        }

        internal static Source Read(GameObject avatar)
        {
            var result = new Source();
            result.Defaults["IsLocal"] = 1f;
            result.Defaults["TrackingType"] = 6f;
            var descriptor = avatar.GetComponents<Component>().FirstOrDefault(c => c != null &&
                c.GetType().FullName == "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            if (descriptor == null) { result.Messages.Add("VRChat Avatar Descriptorがありません。既存のVRM表情はそのまま保存します。"); return result; }
            if (!(Member(descriptor, "customExpressions") is bool enabled) || !enabled)
            { result.Messages.Add("VRChatのCustom Expressionsが有効になっていません。"); return result; }
            var menu = Member(descriptor, "expressionsMenu");
            Walk(menu, "", "", new Dictionary<string, float>(), new HashSet<object>(), result, 0);
            foreach (var parameter in Items(Member(Member(descriptor, "expressionParameters"), "parameters")))
            {
                var name = Member(parameter, "name") as string;
                if (!string.IsNullOrEmpty(name)) result.Defaults[name] = Number(Member(parameter, "defaultValue"));
            }
            if (Member(descriptor, "customizeAnimationLayers") is bool custom && custom)
                foreach (var layer in Items(Member(descriptor, "baseAnimationLayers")))
                    if (Member(layer, "type")?.ToString() == "FX" && !(Member(layer, "isDefault") is bool isDefault && isDefault))
                        result.Controller = Member(layer, "animatorController") as RuntimeAnimatorController;
            if (result.Controller == null) result.Messages.Add("カスタムFX Animatorがありません。メニューの表情を解決できません。");
            return result;
        }

        internal static void Walk(object menu, string prefix, string idPrefix, Dictionary<string, float> parents,
            HashSet<object> stack, Source result, int depth)
        {
            if (menu == null || menu is UnityEngine.Object obj && obj == null) return;
            if (++result.VisitedMenus > 512) throw new InvalidOperationException("サブメニューの参照が多すぎます（512件まで）。");
            if (depth > 16 || !stack.Add(menu)) { result.Messages.Add(prefix + ": 循環または深すぎるサブメニューを省略しました。"); return; }
            try
            {
                var index = 0;
                foreach (var control in Items(Member(menu, "controls")))
                {
                    if (result.Entries.Count >= 256) throw new InvalidOperationException("表情メニューが256項目を超えています。メニューを分割してください。");
                    var id = idPrefix + "/" + index++;
                    var label = Member(control, "name") as string;
                    if (string.IsNullOrWhiteSpace(label)) label = "名称未設定";
                    var name = string.IsNullOrEmpty(prefix) ? label : prefix + " / " + label;
                    var values = new Dictionary<string, float>(parents, StringComparer.Ordinal);
                    var parameter = Member(Member(control, "parameter"), "name") as string;
                    if (!string.IsNullOrEmpty(parameter)) values[parameter] = Number(Member(control, "value"));
                    var type = Member(control, "type")?.ToString();
                    if (type == "SubMenu") { Walk(Member(control, "subMenu"), name, id, values, stack, result, depth + 1); continue; }
                    var entry = new Entry { Id = id + "\n" + name + "\n" + JsonDom.Serialize(values.ToDictionary(p => p.Key, p => (object)p.Value)), Name = name };
                    foreach (var pair in values) entry.Parameters.Add(pair.Key, pair.Value);
                    if (type != "Button" && type != "Toggle") entry.Error = "連続調整（Puppet）は一つの固定表情に変換できません。";
                    else if (string.IsNullOrEmpty(parameter)) entry.Error = "操作対象のパラメーターがありません。";
                    result.Entries.Add(entry);
                }
            }
            finally { stack.Remove(menu); }
        }

        internal static object Member(object value, string name)
        {
            if (value == null) return null;
            var type = value.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            return type.GetField(name, flags)?.GetValue(value) ?? type.GetProperty(name, flags)?.GetValue(value);
        }

        private static IEnumerable<object> Items(object value) => value is IEnumerable list ? list.Cast<object>() : Enumerable.Empty<object>();
        private static float Number(object value)
        {
            var number = value == null ? 0f : Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture);
            if (float.IsNaN(number) || float.IsInfinity(number)) throw new InvalidOperationException("表情メニューに不正な数値があります。");
            return number;
        }
    }
}
